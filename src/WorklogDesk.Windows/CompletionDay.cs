using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal enum TempoAttemptState { Pending, ReadFailed, Refused, RemoteConflict }
internal sealed record TempoAttempt(TempoAttemptState State, Worklog? Line = null,
    RoundingMode Mode = RoundingMode.IssueDay, string Reason = "", bool RequiresManualHandoff = false);

internal enum CompletionLineState { Draft, Ready, Unknown, Refused, Correction, Verified, Attested }

internal sealed record CompletionCounts(int Activity, int Drafts, int Ready, int Unknown, int Refused,
    int Corrections, int Verified, int Attested, bool HasActivity, bool InProgress)
{
    public bool Actionable => Activity + Drafts + Ready + Unknown + Refused + Corrections > 0;
    public bool Closed => !Actionable && !InProgress && (HasActivity || Verified + Attested > 0);
    public bool ToWorklogs => Ready + Unknown + Refused + Corrections + Drafts > 0;
}

internal sealed class CompletionDay
{
    public IReadOnlyList<Worklog> Lines { get; }
    public Dictionary<string, string> ApprovalKeys { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Approved { get; }
    public Dictionary<Worklog, ManualCoverage> Coverage { get; }
    public Dictionary<Worklog, CompletionLineState> States { get; } = [];
    public IReadOnlyList<ManualEntry> Unlinked { get; }
    public IReadOnlyDictionary<string, TempoAttempt> Attempts { get; }
    public CompletionCounts Counts { get; }

    public CompletionDay(IReadOnlyList<Allocation> rows, IReadOnlyList<ManualEntry> entries,
        RoundingMode mode, IReadOnlyDictionary<string, TempoAttempt> attempts, bool inProgress = false,
        IReadOnlyList<Worklog>? lines = null)
    {
        Lines = lines ?? Timeline.Preview(rows, mode);
        Attempts = attempts;
        foreach (var line in Lines)
        {
            var key = line.ApprovalKey(mode);
            foreach (var id in line.AllocationIds)
                ApprovalKeys.Add(id, key);
        }
        Approved = rows.Where(row => ApprovalKeys.TryGetValue(row.Id, out var key) && row.IsApprovedFor(mode, key))
            .Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        Coverage = Lines.ToDictionary(line => line, line => ManualLedger.Check(line, mode, Lines, entries));
        var linked = Coverage.Values.SelectMany(value => value.RelatedEntries).Select(item => item.Id).ToHashSet();
        Unlinked = entries.Where(entry => !linked.Contains(entry.Id)).ToArray();
        var separate = Unlinked.Where(entry => entry.Intervals is { Length: 0 }).ToArray();
        var pending = attempts.Where(item => item.Value.State == TempoAttemptState.Pending).ToArray();
        foreach (var line in Lines)
        {
            var coverage = Coverage[line];
            var attempt = attempts.GetValueOrDefault(line.ApprovalKey(mode));
            States[line] = coverage switch
            {
                { NeedsReview: true } or { Consolidated: not null } => CompletionLineState.Correction,
                { Entry: not null } when coverage.Difference(line) != 0 || coverage.StartChanged(line) ||
                    coverage.DescriptionChanged(line) => CompletionLineState.Correction,
                { Entry: { Provenance: SubmissionProvenance.VerifiedTempo } } => CompletionLineState.Verified,
                { Entry: not null } => CompletionLineState.Attested,
                _ when attempt?.State is TempoAttemptState.Pending or TempoAttemptState.ReadFailed => CompletionLineState.Unknown,
                _ when HasEarlierPending(line, mode) => CompletionLineState.Unknown,
                _ when attempt?.State == TempoAttemptState.RemoteConflict => CompletionLineState.Correction,
                _ when attempt?.State == TempoAttemptState.Refused => CompletionLineState.Refused,
                _ when line.AllocationIds.All(Approved.Contains) => CompletionLineState.Ready,
                _ => CompletionLineState.Draft
            };
        }
        var pendingWithoutLine = pending.Count(item => !Lines.Any(line => line.ApprovalKey(mode) == item.Key ||
            States[line] == CompletionLineState.Unknown && MayOverlap(item.Value, line)));

        var activity = rows.Count(row => NeedsActivityReview(row, Approved) && row.Kind != SpanKind.Idle &&
            !ApprovalKeys.ContainsKey(row.Id));
        var hasActivity = rows.Any(row => row.Status is Disposition.Billable or Disposition.Internal ||
            row.Status == Disposition.Unassigned && row.Kind is not (SpanKind.Idle or SpanKind.Incidental));
        Counts = new CompletionCounts(activity, Count(CompletionLineState.Draft), Count(CompletionLineState.Ready),
            Count(CompletionLineState.Unknown) + pendingWithoutLine, Count(CompletionLineState.Refused),
            Count(CompletionLineState.Correction) + Unlinked.Count - separate.Length,
            Count(CompletionLineState.Verified) + separate.Count(entry => entry.Provenance == SubmissionProvenance.VerifiedTempo),
            Count(CompletionLineState.Attested) + separate.Count(entry => entry.Provenance != SubmissionProvenance.VerifiedTempo),
            hasActivity, inProgress);
    }

    public static bool NeedsActivityReview(Allocation row, IReadOnlySet<string> approved) =>
        !approved.Contains(row.Id) && row.Status is not (Disposition.Internal or Disposition.Excluded);

    public bool HasEarlierPending(Worklog line, RoundingMode mode) => Attempts.Any(item =>
        item.Value.State == TempoAttemptState.Pending && item.Key != line.ApprovalKey(mode) &&
        MayOverlap(item.Value, line));

    private static bool MayOverlap(TempoAttempt attempt, Worklog line) => attempt.Line == null ||
        attempt.Line.Issue == line.Issue || attempt.Line.Intervals.Any(interval => line.Intervals.Any(interval.Overlaps));

    private int Count(CompletionLineState state) => States.Values.Count(value => value == state);
}
