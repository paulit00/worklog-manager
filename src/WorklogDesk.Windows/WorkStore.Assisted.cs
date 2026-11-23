using System.IO;
using Microsoft.Data.Sqlite;
using WorklogDesk.Core;

namespace WorklogDesk.Windows;

/// <summary>Everything the assisted-edit guard reads for one day, captured once so the check itself is pure.</summary>
internal sealed record GuardData(IReadOnlyList<Allocation> Current, IReadOnlyList<ManualEntry> Entries, ExportReceipt[] Exports,
    Dictionary<string, TempoAttempt> Attempts, string[] Opaque, bool LegacyPending);

/// <summary>
/// Rows of one accepted segment as they were when the AI request was built. Inside the window, up to the capture
/// horizon (the latest row end at request time), the stored rows must still match exactly.
/// </summary>
internal sealed record SegmentGuard(DateTimeOffset Start, DateTimeOffset End, DateTimeOffset Horizon, Allocation[] Snapshot);

internal static class AssistedGuard
{
    public const string SelectAgain = "Select recorded activity again.";
    public const string Scope = "Assisted review can only assign or undo an assignment for unapproved recorded activity.";
    public const string Changed = "This activity changed. Refresh suggestions before making this edit.";
    public const string Protected = "This edit could affect approved, copied, or entered time. Use the existing manual review workflow.";
    public const string SegmentChanged = "This part of the day changed. Reconstruct again.";

    /// <summary>
    /// The assisted-edit rule, without database access: only status and task may change (Unassigned to Billable
    /// with a Jira key, or the exact reverse), on unapproved rows, and never in a way that changes a worklog line
    /// that was approved, copied, exported, attempted or entered, in either rounding mode. Returns null when allowed.
    /// </summary>
    public static string? Evaluate(GuardData data, IReadOnlyCollection<Allocation> expected, IReadOnlyCollection<Allocation> updated)
    {
        if (expected.Count == 0 || expected.Count != updated.Count)
            return SelectAgain;
        var day = expected.First().Day;
        var original = expected.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var changes = updated.ToDictionary(row => row.Id, StringComparer.Ordinal);
        if (original.Count != expected.Count || changes.Count != updated.Count)
            return SelectAgain;
        // Only the status and task may change: Unassigned -> Billable with a Jira key, or the exact reverse for undo.
        if (changes.Values.Any(row => row.Day != day || !original.TryGetValue(row.Id, out var prior) ||
            row with { Status = prior.Status, Task = prior.Task } != prior || row.Approved || prior.Approved ||
            !(AssistedEvidence.CanAssign(prior) && row.Status == Disposition.Billable && TaskLabel.ValidIssue(row.Task.Issue) ||
              AssistedEvidence.CanAssign(row) && prior.Status == Disposition.Billable)))
            return Scope;
        var current = data.Current;
        var actual = current.Where(row => original.ContainsKey(row.Id)).ToDictionary(row => row.Id, StringComparer.Ordinal);
        if (expected.Any(row => row.Day != day || !actual.TryGetValue(row.Id, out var saved) || saved != row))
            return Changed;

        var revised = current.Select(row => changes.GetValueOrDefault(row.Id, row)).ToArray();
        var affected = new List<Worklog>();
        var knownKeys = new HashSet<string>(StringComparer.Ordinal);
        var affectedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mode in Enum.GetValues<RoundingMode>())
        {
            var before = Timeline.Preview(current, mode).ToDictionary(line => line.ApprovalKey(mode));
            var after = Timeline.Preview(revised, mode).ToDictionary(line => line.ApprovalKey(mode));
            knownKeys.UnionWith(before.Keys);
            foreach (var (key, line) in before.Concat(after))
            {
                if (before.ContainsKey(key) && after.ContainsKey(key))
                    continue;
                affected.Add(line);
                affectedKeys.Add(key);
            }
        }
        var affectedIds = affected.SelectMany(line => line.AllocationIds).ToHashSet(StringComparer.Ordinal);
        if (current.Any(row => row.Approved && affectedIds.Contains(row.Id)) ||
            data.Opaque.Any(key => affectedKeys.Contains(key) || !knownKeys.Contains(key)) || data.LegacyPending ||
            data.Entries.Any(entry => entry.Intervals == null || Affects(entry.Issue, entry.Intervals) || entry.AllocationIds.Any(affectedIds.Contains)) ||
            data.Exports.Any(export => Affects(export.Issue, export.Intervals)) ||
            data.Attempts.Any(item => affectedKeys.Contains(item.Key) ||
                (item.Value.Line is { } line ? Affects(line.Issue, line.Intervals) : !knownKeys.Contains(item.Key))))
            return Protected;
        return null;

        bool Affects(string issue, IReadOnlyList<WorkInterval> intervals) => affected.Any(line =>
            line.Issue == issue || line.Intervals.Any(candidate => intervals.Any(candidate.Overlaps)));
    }

    /// <summary>
    /// True when the whole date refuses assisted edits whatever is changed, for example because of a stale handoff or
    /// correction key or a legacy pending Tempo list.
    /// </summary>
    public static bool DayBlocked(GuardData data)
    {
        var known = Enum.GetValues<RoundingMode>()
            .SelectMany(mode => Timeline.Preview(data.Current, mode).Select(line => line.ApprovalKey(mode)))
            .ToHashSet(StringComparer.Ordinal);
        return data.LegacyPending || data.Opaque.Any(key => !known.Contains(key)) ||
            data.Entries.Any(entry => entry.Intervals == null) ||
            data.Attempts.Any(item => item.Value.Line == null && !known.Contains(item.Key));
    }

    /// <summary>Checks each guard window against the current rows. Returns null when nothing in them changed.</summary>
    public static string? CheckSegments(IReadOnlyList<Allocation> current, IEnumerable<SegmentGuard> guards)
    {
        foreach (var guard in guards)
        {
            var end = guard.End < guard.Horizon ? guard.End : guard.Horizon;
            if (end <= guard.Start)
                continue;
            var now = current.Where(row => row.Start < end && row.End > guard.Start).OrderBy(row => row.Id, StringComparer.Ordinal).ToArray();
            var then = guard.Snapshot.Where(row => row.Start < end && row.End > guard.Start).OrderBy(row => row.Id, StringComparer.Ordinal).ToArray();
            if (now.Length != then.Length || now.Zip(then).Any(pair => pair.First != pair.Second))
                return SegmentChanged;
        }
        return null;
    }
}

internal sealed partial class WorkStore
{
    private readonly string? folder;

    private WorkStore(SqliteConnection readOnly)
    {
        connection = readOnly;
    }

    /// <summary>
    /// A second, read-only connection to the same database for background reads. The main connection is used by
    /// capture on the UI timer. Returns null for an in-memory store, where callers read on the main connection.
    /// </summary>
    public WorkStore? OpenReader() => folder == null ? null : OpenReadOnly(folder);

    public static WorkStore OpenReadOnly(string folder)
    {
        var path = Path.Combine(folder, "worklog.db");
        if (!File.Exists(path))
            throw new InvalidOperationException("No Worklog Desk database was found.");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2
        }.ToString());
        connection.Open();
        return new WorkStore(connection);
    }

    public long DiskVersion()
    {
        using var command = Command("PRAGMA user_version");
        return (long)command.ExecuteScalar()!;
    }

    public GuardData ReadGuardData(DateOnly day) => ReadGuardData(day, null);

    private GuardData ReadGuardData(DateOnly day, SqliteTransaction? transaction)
    {
        var keyDay = FormatDay(day);
        var attempts = Read<Dictionary<string, TempoAttempt>>("tempo-attempts-" + keyDay, transaction);
        var opaque = (Read<string[]>("handoff-keys-" + keyDay, transaction) ?? [])
            .Concat(Read<string[]>("manual-handoff-" + keyDay, transaction) ?? [])
            .Concat(Read<string[]>("correction-" + keyDay, transaction) ?? []).ToArray();
        // Modern attempts supersede entries in the old pending list. A missing modern record indicates a resolved attempt.
        var legacyPending = attempts == null && (Read<string[]>("tempo-pending-" + keyDay, transaction) ?? []).Length > 0;
        return new GuardData(Rows(day, transaction), ManualEntries(day, transaction),
            Read<ExportReceipt[]>("export-" + keyDay, transaction) ?? [], attempts ?? [], opaque, legacyPending);
    }

    /// <summary>Rows of each day in the range, oldest first. For history: run it on a reader, off the UI thread.</summary>
    public List<(DateOnly Day, List<Allocation> Rows, List<ManualEntry> Entries)> History(DateOnly from, DateOnly to)
    {
        var days = new List<(DateOnly, List<Allocation>, List<ManualEntry>)>();
        for (var day = from; day <= to; day = day.AddDays(1))
            days.Add((day, Rows(day), ManualEntries(day)));
        return days;
    }

    private void CheckAssistedEdit(IReadOnlyCollection<Allocation> expected, IReadOnlyCollection<Allocation> updated,
        SqliteTransaction transaction)
    {
        if (expected.Count == 0)
            throw new InvalidOperationException(AssistedGuard.SelectAgain);
        if (AssistedGuard.Evaluate(ReadGuardData(expected.First().Day, transaction), expected, updated) is { } refusal)
            throw new InvalidOperationException(refusal);
    }
}
