namespace WorklogDesk.Core;

public enum SubmissionProvenance { Manual, VerifiedTempo }

public sealed record ManualEntry(string Id, string Issue, RoundingMode Mode, int Seconds, string[] AllocationIds,
    DateTimeOffset? Start = null, WorkInterval[]? Intervals = null, string Description = "",
    SubmissionProvenance? Provenance = null, DateTimeOffset? RecordedAtUtc = null);

public sealed record ManualCoverage(ManualEntry? Entry, bool NeedsReview, IReadOnlyList<ManualEntry>? Consolidated = null)
{
    public int? Difference(Worklog worklog) => Consolidated is { Count: > 0 }
        ? worklog.ProposedSeconds - Consolidated.Sum(item => item.Seconds)
        : Entry == null ? null : worklog.ProposedSeconds - Entry.Seconds;
    public bool DescriptionChanged(Worklog worklog) => Entry != null &&
        !StringComparer.Ordinal.Equals(Entry.Description, worklog.Description);
    public bool StartChanged(Worklog worklog) => Entry != null && Entry.Start != worklog.Start;
    public IEnumerable<ManualEntry> RelatedEntries => Consolidated ?? (Entry == null ? [] : [Entry]);
}

public static class ManualLedger
{
    public static bool CanExport(Worklog line, RoundingMode mode, IReadOnlyList<Worklog> current,
        IReadOnlyList<ManualEntry> entered, IReadOnlySet<string> approved) =>
        line.AllocationIds.All(approved.Contains) &&
        Check(line, mode, current, entered) is { Entry: null, NeedsReview: false, Consolidated: null };

    public static IReadOnlyList<ManualEntry> Orphaned(RoundingMode mode, IReadOnlyList<Worklog> current,
        IReadOnlyList<ManualEntry> entered)
    {
        var matched = current.SelectMany(line => Check(line, mode, current, entered).RelatedEntries)
            .Select(item => item.Id).ToHashSet();
        return entered.Where(entry => !matched.Contains(entry.Id)).ToArray();
    }

    public static ManualCoverage Check(Worklog line, RoundingMode mode,
        IReadOnlyList<Worklog> current, IReadOnlyList<ManualEntry> entered)
    {
        var related = entered.Where(entry => entry.Issue == line.Issue || SharesTime(entry, line) ||
            IsUnplacedLegacy(entry, current)).ToArray();
        if (related.Any(entry => entry.Mode != mode || entry.Issue != line.Issue))
            return new ManualCoverage(null, true);
        var attached = related.Where(entry => mode == RoundingMode.IssueDay || SharesTime(entry, line)).ToArray();
        if (attached.Length > 0)
        {
            if (attached.Length == 1 && (mode == RoundingMode.IssueDay ||
                current.Count(candidate => candidate.Issue == line.Issue && SharesTime(attached[0], candidate)) == 1))
                return new ManualCoverage(attached[0], false);
            if (attached.Length > 1 && mode == RoundingMode.Session && attached.All(entry =>
                Identifies(entry, line) && current.Count(candidate => candidate.Issue == line.Issue && SharesTime(entry, candidate)) == 1))
                return new ManualCoverage(null, false, attached);
            return new ManualCoverage(null, true);
        }
        var unidentified = related.Any(entry =>
            !current.Any(candidate => candidate.Issue == entry.Issue && Identifies(entry, candidate)));
        return new ManualCoverage(null, unidentified);
    }

    /// <summary>
    /// A mark saved without a time range that no current line identifies. Its time could now sit under any
    /// issue, so it holds the whole day until the user places it with <see cref="KeepSeparate"/>.
    /// </summary>
    public static bool IsUnplacedLegacy(ManualEntry entry, IReadOnlyList<Worklog> current) =>
        entry.Intervals == null && !current.Any(candidate => candidate.Issue == entry.Issue && Identifies(entry, candidate));

    /// <summary>
    /// The user confirmed the mark is a separate Tempo entry for its own issue. An empty range keeps the
    /// same-issue check while releasing lines for other issues.
    /// </summary>
    public static ManualEntry KeepSeparate(ManualEntry entry) => entry with { Intervals = [] };

    public static IReadOnlyList<ManualEntry> OverlappingMarks(Worklog line, IReadOnlyList<ManualEntry> entered) =>
        entered.Where(entry => entry.Issue != line.Issue && SharesTime(entry, line)).ToArray();

    public static IReadOnlyDictionary<string, ManualEntry>? ReconcileIssueDayBatch(RoundingMode mode,
        IReadOnlyList<Worklog> selected, IReadOnlyList<ManualEntry> entered)
    {
        if (mode != RoundingMode.IssueDay || selected.Count < 2)
            return null;
        var chosen = new Dictionary<string, ManualEntry>(StringComparer.Ordinal);
        foreach (var line in selected)
        {
            var matches = entered.Where(entry => entry.Issue == line.Issue).ToArray();
            if (matches.Length != 1 || matches[0].Mode != mode || matches[0].Intervals is not { Length: > 0 } ||
                !chosen.TryAdd(line.Issue, matches[0]))
                return null;
        }
        var otherEntries = entered.Where(entry => !chosen.ContainsKey(entry.Issue));
        if (otherEntries.Any(entry => selected.Any(line => SharesTime(entry, line))))
            return null;
        return chosen;
    }

    private static bool SharesTime(ManualEntry entry, Worklog line) =>
        entry.AllocationIds.Intersect(line.AllocationIds).Any() ||
        entry.Intervals?.Any(interval => line.Intervals.Any(interval.Overlaps)) == true;

    private static bool Identifies(ManualEntry entry, Worklog line) =>
        entry.AllocationIds.All(line.AllocationIds.Contains) ||
        entry.Intervals is { Length: > 0 } && entry.Intervals.All(interval => Covers(interval, line.Intervals));

    private static bool Covers(WorkInterval interval, IReadOnlyList<WorkInterval> spans)
    {
        var cursor = interval.Start;
        foreach (var candidate in spans)
        {
            if (candidate.End <= cursor)
                continue;
            if (candidate.Start > cursor)
                return false;
            cursor = candidate.End;
            if (cursor >= interval.End)
                return true;
        }
        return false;
    }
}
