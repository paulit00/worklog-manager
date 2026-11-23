using Microsoft.Data.Sqlite;
using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal sealed partial class WorkStore
{
    private void CheckAssistedEdit(IReadOnlyCollection<Allocation> expected, IReadOnlyCollection<Allocation> updated,
        SqliteTransaction transaction)
    {
        if (expected.Count == 0 || expected.Count != updated.Count)
            throw new InvalidOperationException("Select recorded activity again.");
        var day = expected.First().Day;
        var original = expected.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var changes = updated.ToDictionary(row => row.Id, StringComparer.Ordinal);
        // Only the status and issue may change: Unassigned -> Billable with a Jira key, or the exact reverse for undo.
        if (changes.Values.Any(row => row.Day != day || !original.TryGetValue(row.Id, out var prior) ||
            row with { Status = prior.Status, Task = prior.Task } != prior || row.Approved || prior.Approved ||
            !(AssistedEvidence.CanAssign(prior) && row.Status == Disposition.Billable && TaskLabel.ValidIssue(row.Task.Issue) ||
              AssistedEvidence.CanAssign(row) && prior.Status == Disposition.Billable)))
            throw new InvalidOperationException("Assisted review can only assign or undo an assignment for unapproved recorded activity.");
        var current = Rows(day, transaction);
        var actual = current.Where(row => original.ContainsKey(row.Id)).ToDictionary(row => row.Id, StringComparer.Ordinal);
        if (expected.Any(row => row.Day != day || !actual.TryGetValue(row.Id, out var saved) || saved != row))
            throw new InvalidOperationException("This activity changed. Refresh suggestions before making this edit.");

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
        var keyDay = FormatDay(day);
        var entries = ManualEntries(day, transaction);
        var exports = Read<ExportReceipt[]>("export-" + keyDay, transaction) ?? [];
        var attempts = Read<Dictionary<string, TempoAttempt>>("tempo-attempts-" + keyDay, transaction) ?? [];
        var opaque = (Read<string[]>("handoff-keys-" + keyDay, transaction) ?? [])
            .Concat(Read<string[]>("manual-handoff-" + keyDay, transaction) ?? [])
            .Concat(Read<string[]>("correction-" + keyDay, transaction) ?? []);
        // Modern attempts supersede entries in the old pending list. A missing modern record indicates a resolved attempt.
        var legacyPending = Read<Dictionary<string, TempoAttempt>>("tempo-attempts-" + keyDay, transaction) == null &&
            (Read<string[]>("tempo-pending-" + keyDay, transaction) ?? []).Length > 0;
        if (current.Any(row => row.Approved && affectedIds.Contains(row.Id)) ||
            opaque.Any(key => affectedKeys.Contains(key) || !knownKeys.Contains(key)) || legacyPending ||
            entries.Any(entry => entry.Intervals == null || Affects(entry.Issue, entry.Intervals) || entry.AllocationIds.Any(affectedIds.Contains)) ||
            exports.Any(export => Affects(export.Issue, export.Intervals)) ||
            attempts.Any(item => affectedKeys.Contains(item.Key) ||
                (item.Value.Line is { } line ? Affects(line.Issue, line.Intervals) : !knownKeys.Contains(item.Key))))
            throw new InvalidOperationException("This edit could affect approved, copied, or entered time. Use the existing manual review workflow.");

        bool Affects(string issue, IReadOnlyList<WorkInterval> intervals) => affected.Any(line =>
            line.Issue == issue || line.Intervals.Any(candidate => intervals.Any(candidate.Overlaps)));
    }
}
