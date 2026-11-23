using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal enum EpisodeState { Unassigned, Uncertain, Assigned, Internal, Idle, Excluded, UnassignedOther, Other }

internal enum TitleDecision { Sent, Disabled, State, FamilyOff, Mail, OlderBrowser, Unticked }

/// <summary>Which window titles may leave the PC for one request.</summary>
internal sealed record TitlePolicy(bool Enabled, IReadOnlySet<string> FamiliesOff, bool MailSubjects, bool OlderBrowser,
    DateTimeOffset? Cutoff, string? MailName, string? MailAddress, IReadOnlySet<string> Unticked)
{
    public static TitlePolicy Off { get; } = new(false, new HashSet<string>(), false, false, null, null, null, new HashSet<string>());
}

/// <summary>A run of rows read as one stretch of activity. Row ids never leave the PC.</summary>
internal sealed record Episode(int Number, EpisodeState State, string Issue, bool Locked, Allocation[] Rows)
{
    public DateTimeOffset Start => Rows[0].Start;
    public DateTimeOffset End => Rows.Max(row => row.End);
    public long Milliseconds => Rows.Sum(row => row.Milliseconds);
    public IEnumerable<Allocation> Assignable => State == EpisodeState.Unassigned ? Rows.Where(AssistedEvidence.CanAssign) : [];
}

internal static class DayEpisodes
{
    public const int Cap = 400;
    private static readonly TimeSpan MergeGap = TimeSpan.FromMinutes(2);

    /// <summary>The state of one row, in the plan's order of precedence.</summary>
    public static EpisodeState State(Allocation row) => row switch
    {
        { Kind: SpanKind.Incidental } or { Status: Disposition.Excluded } => EpisodeState.Excluded,
        { Status: Disposition.Billable } when TaskLabel.ValidIssue(row.Task.Issue) => EpisodeState.Assigned,
        { Status: Disposition.Internal } => EpisodeState.Internal,
        { Kind: SpanKind.Idle } => EpisodeState.Idle,
        { Status: Disposition.Billable } => EpisodeState.UnassignedOther,
        { Kind: SpanKind.Uncertain } => EpisodeState.Uncertain,
        { Status: Disposition.Unassigned, Approved: false, Kind: SpanKind.Foreground or SpanKind.Manual } => EpisodeState.Unassigned,
        _ => EpisodeState.Other
    };

    /// <summary>Titles of these states may be sent; excluded, idle, internal and other rows never send titles.</summary>
    public static bool TitleState(EpisodeState state) =>
        state is EpisodeState.Unassigned or EpisodeState.Uncertain or EpisodeState.UnassignedOther or EpisodeState.Assigned;

    /// <summary>The family used for privacy rules: a browser title naming Microsoft Teams counts as Teams.</summary>
    public static string PrivacyFamily(Allocation row, string title)
    {
        var family = AssistedEvidence.AppFamily(row.App);
        return family == "Browser" && AssistedEvidence.TeamsLike(title) ? "Teams" : family;
    }

    public static TitleDecision Decide(Allocation row, string title, TitlePolicy policy)
    {
        if (!policy.Enabled)
            return TitleDecision.Disabled;
        if (!TitleState(State(row)))
            return TitleDecision.State;
        var family = PrivacyFamily(row, title);
        if (family == "Outlook" && !policy.MailSubjects)
            return TitleDecision.Mail;
        if (policy.FamiliesOff.Contains(family))
            return TitleDecision.FamilyOff;
        if (AssistedEvidence.AppFamily(row.App) == "Browser")
        {
            // Older browser rows have no Outlook-web or Teams-web label. Until the Microsoft account names the mail
            // owner, or while Teams titles are off, they stay behind an explicit opt-in.
            var older = policy.Cutoff == null || row.Start < policy.Cutoff;
            if (older && !policy.OlderBrowser && (policy.MailName == null || policy.FamiliesOff.Contains("Teams")))
                return TitleDecision.OlderBrowser;
            if (!policy.MailSubjects && AssistedEvidence.MailLike(title, policy.MailName, policy.MailAddress))
                return TitleDecision.Mail;
        }
        // Single-title unticks apply last, so they hold for mail included by opt-in too.
        return policy.Unticked.Contains(title) ? TitleDecision.Unticked : TitleDecision.Sent;
    }

    /// <summary>True when the row's context may be missing titles: three are kept, or the text is near the 320-character cap.</summary>
    public static bool MoreTitles(Allocation row) =>
        AssistedEvidence.Titles(row.Context).Length >= 3 || row.Context.Length > 2 * WindowContext.MaxLength - WindowContext.MaxLength - 3;

    /// <summary>Builds the ordered episodes of a day. Every row appears in exactly one episode.</summary>
    public static Episode[] Build(IReadOnlyList<Allocation> rows, DateTimeOffset dayStart)
    {
        var ordered = rows.OrderBy(row => row.Start).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
        var runs = new List<(EpisodeState State, List<Allocation> Rows)>();
        var index = 0;
        while (index < ordered.Length)
        {
            var row = ordered[index];
            var state = State(row);
            if (state != EpisodeState.Unassigned)
            {
                AddSingle(row, state);
                index++;
                continue;
            }
            var run = new List<Allocation> { row };
            var last = row;
            var pending = new List<Allocation>();
            var next = index + 1;
            while (next < ordered.Length)
            {
                var candidate = ordered[next];
                if (Bridge(candidate))
                {
                    pending.Add(candidate);
                    next++;
                    continue;
                }
                if (State(candidate) == EpisodeState.Unassigned && Joins(last, pending, candidate))
                {
                    run.AddRange(pending);
                    run.Add(candidate);
                    pending.Clear();
                    last = candidate;
                    next++;
                    continue;
                }
                break;
            }
            runs.Add((EpisodeState.Unassigned, run));
            index = next - pending.Count;
        }
        if (runs.Count > Cap)
            runs = Windows(runs, dayStart);
        if (runs.Count > Cap)
            throw new InvalidOperationException("This day is too large for one request. Reconstruct the morning or the afternoon.");
        return runs.Select((run, position) =>
        {
            var first = run.Rows[0];
            return new Episode(position + 1, run.State, run.State == EpisodeState.Assigned ? first.Task.Issue : "",
                run.Rows.Any(item => item.Approved), run.Rows.ToArray());
        }).ToArray();

        void AddSingle(Allocation row, EpisodeState state)
        {
            // Neighbouring anchors of one issue, idle stretches and excluded stretches read as one entry.
            if (runs.Count > 0 && runs[^1].State == state && state != EpisodeState.Unassigned &&
                row.Start - runs[^1].Rows.Max(item => item.End) <= MergeGap &&
                (state != EpisodeState.Assigned || runs[^1].Rows[0].Task.Issue == row.Task.Issue && runs[^1].Rows[0].Approved == row.Approved))
                runs[^1].Rows.Add(row);
            else
                runs.Add((state, [row]));
        }
    }

    /// <summary>
    /// Display bridges inside an unassigned run: brief excluded switches and unassigned slivers under 10 s.
    /// The slivers can't change Tempo on their own because <see cref="Timeline"/> bridges them too.
    /// </summary>
    private static bool Bridge(Allocation row) => State(row) switch
    {
        EpisodeState.Excluded => row.Kind == SpanKind.Incidental || row.Kind == SpanKind.Foreground && row.Milliseconds <= 120_000,
        EpisodeState.Unassigned => row.Milliseconds < 10_000,
        _ => false
    };

    private static bool Joins(Allocation last, List<Allocation> bridges, Allocation candidate)
    {
        var family = AssistedEvidence.AppFamily(last.App);
        if (family != AssistedEvidence.AppFamily(candidate.App))
            return false;
        var previous = last;
        foreach (var row in bridges.Append(candidate))
        {
            if (row.Start - previous.End > MergeGap)
                return false;
            previous = row;
        }
        var left = Tokens(last);
        var right = Tokens(candidate);
        return left.Count == 0 && right.Count == 0 || left.Overlaps(right);
    }

    public static HashSet<string> Tokens(Allocation row)
    {
        var family = AssistedEvidence.AppFamily(row.App);
        return AssistedEvidence.Titles(row.Context).Select(title => AssistedEvidence.Token(family, title))
            .Where(token => token.Length > 0).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Fallback for very busy days: unassigned runs that start in the same 5-minute window become one entry.</summary>
    private static List<(EpisodeState State, List<Allocation> Rows)> Windows(List<(EpisodeState State, List<Allocation> Rows)> runs, DateTimeOffset dayStart)
    {
        var merged = new List<(EpisodeState State, List<Allocation> Rows)>();
        long? window = null;
        foreach (var run in runs)
        {
            var slot = (long)((run.Rows[0].Start - dayStart).TotalMinutes / 5);
            if (run.State == EpisodeState.Unassigned && merged.Count > 0 && merged[^1].State == EpisodeState.Unassigned && window == slot)
                merged[^1].Rows.AddRange(run.Rows);
            else
                merged.Add((run.State, new List<Allocation>(run.Rows)));
            window = run.State == EpisodeState.Unassigned ? slot : null;
        }
        return merged;
    }
}
