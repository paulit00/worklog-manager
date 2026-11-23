using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorklogDesk.Core;

public enum SpanKind { Foreground, Manual, Idle, Uncertain, Incidental }
public enum Disposition { Unassigned, Billable, Internal, Excluded }
public enum RoundingMode { IssueDay, Session }

public sealed record TaskLabel(string Client = "", string Issue = "", string Description = "", string Link = "")
{
    public static bool ValidIssue(string value) =>
        value.Length <= 40 && Regex.IsMatch(value, @"\A[A-Z][A-Z0-9_]*-[1-9][0-9]*\z", RegexOptions.CultureInvariant);

    public static bool ValidLink(string value) => value.Length == 0 ||
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo);
}

/// <param name="Context">Normalized window titles seen in the span; review context only, never part of attribution.</param>
public sealed record CaptureSource(string App, TaskLabel? Task = null, SpanKind Kind = SpanKind.Foreground, string Context = "")
{
    public bool SameAttribution(CaptureSource? other) => other != null && this with { Context = "" } == other with { Context = "" };
}

public sealed record WorkSpan(Guid Id, DateTimeOffset Start, DateTimeOffset End, CaptureSource Source);

public sealed record Allocation(
    string Id, DateOnly Day, DateTimeOffset Start, DateTimeOffset End, string App,
    SpanKind Kind, Disposition Status, TaskLabel Task, bool Approved = false, RoundingMode? ApprovedMode = null,
    string? ApprovedKey = null, string Context = "")
{
    public long Milliseconds => (End - Start).Ticks / TimeSpan.TicksPerMillisecond;
    public bool IsApprovedIn(RoundingMode mode) => Approved && ApprovedMode == mode;
    public bool IsApprovedFor(RoundingMode mode, string key) =>
        IsApprovedIn(mode) && StringComparer.Ordinal.Equals(ApprovedKey, key);
}

public sealed record WorkInterval(DateTimeOffset Start, DateTimeOffset End)
{
    public bool Overlaps(WorkInterval other) => Start < other.End && other.Start < End;
}

public sealed record Worklog(string Issue, string Client, DateTimeOffset Start, long ActualMilliseconds,
    int ProposedSeconds, string Description, IReadOnlyList<string> AllocationIds, IReadOnlyList<WorkInterval> Intervals)
{
    public string ApprovalKey(RoundingMode mode) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            mode, Issue, Client, Start = Start.UtcTicks, ProposedSeconds, Description, AllocationIds,
            Intervals = Intervals.Select(interval => new { Start = interval.Start.UtcTicks, End = interval.End.UtcTicks })
        })));
}

public static class Timeline
{
    public static DateOnly DayOf(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    public static DateTimeOffset DayStart(DateOnly day, TimeZoneInfo zone) => AtLocalTime(day, TimeOnly.MinValue, zone);

    public static DateTimeOffset AtLocalTime(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    public static IEnumerable<Allocation> Allocate(WorkSpan span, TimeZoneInfo zone)
    {
        for (var start = span.Start; start < span.End;)
        {
            var day = DayOf(start, zone);
            var boundary = DayStart(day.AddDays(1), zone);
            var end = span.End < boundary ? span.End : boundary;
            var task = span.Source.Task ?? new TaskLabel();
            // A client-only alias is a clue, not a task, so a brief switch into a mapped window is still incidental.
            var kind = span.Source.Kind == SpanKind.Foreground && !TaskLabel.ValidIssue(task.Issue) &&
                (span.End - span.Start).TotalSeconds < 10 ? SpanKind.Incidental : span.Source.Kind;
            var status = kind switch
            {
                SpanKind.Incidental => Disposition.Excluded,
                SpanKind.Idle or SpanKind.Uncertain => Disposition.Unassigned,
                _ when TaskLabel.ValidIssue(task.Issue) => Disposition.Billable,
                _ => Disposition.Unassigned
            };
            yield return new Allocation($"{span.Id:N}-{start.UtcTicks}", day, start, end, span.Source.App,
                kind, status, task, Context: span.Source.Context);
            start = end;
        }
    }

    public static int RoundSeconds(long durationMilliseconds)
    {
        if (durationMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        return checked((int)((durationMilliseconds + 899_999L) / 900_000L * 900));
    }

    public static IReadOnlyList<Worklog> Preview(IEnumerable<Allocation> allocations, RoundingMode mode)
    {
        var rows = allocations.OrderBy(row => row.Start).ThenBy(row => row.Id, StringComparer.Ordinal).ToList();
        var entries = rows.Where(row => row.Status == Disposition.Billable && TaskLabel.ValidIssue(row.Task.Issue));
        if (mode == RoundingMode.IssueDay)
            return entries.GroupBy(row => (row.Day, row.Task.Issue), row => row)
                .Select(CreateWorklog).ToArray();

        var sessions = new List<Worklog>();
        var session = new List<Allocation>();
        foreach (var row in rows)
        {
            if (IsBridge(row))
                continue;
            if (row.Status == Disposition.Billable && TaskLabel.ValidIssue(row.Task.Issue))
            {
                if (session.Count > 0 && (session[^1].Day != row.Day || session[^1].Task.Issue != row.Task.Issue ||
                    session[^1].Task.Client != row.Task.Client || row.Start - session[^1].End > TimeSpan.FromMinutes(2)))
                    Finish();
                session.Add(row);
            }
            else
            {
                Finish();
            }
        }
        Finish();
        return sessions;

        void Finish()
        {
            if (session.Count == 0)
                return;
            sessions.Add(CreateWorklog(session));
            session.Clear();
        }
    }

    // A bridge row contributes no time and does not end a session by itself. The two-minute gap rule in
    // Preview still applies, so a long excluded or idle stretch ends the session. Internal work and
    // unresolved activity over ten seconds may belong to a competing task, so they still end the session.
    private static bool IsBridge(Allocation row) => row.Kind switch
    {
        SpanKind.Incidental => row.Status == Disposition.Excluded,
        SpanKind.Idle => row.Status == Disposition.Unassigned,
        SpanKind.Foreground => row.Status == Disposition.Unassigned && row.Milliseconds < 10_000 ||
            row.Status == Disposition.Excluded && row.Milliseconds <= 120_000,
        _ => false
    };

    /// <summary>
    /// The parts of an original row that an edit no longer covers. They return as unassigned time with no task,
    /// so shortening or moving a row never removes captured time from the day.
    /// </summary>
    public static IEnumerable<Allocation> Remainders(Allocation original, Allocation edited)
    {
        if (edited.Start > original.Start)
            yield return Remainder(original.Start, edited.Start < original.End ? edited.Start : original.End);
        if (edited.End < original.End)
            yield return Remainder(edited.End > original.Start ? edited.End : original.Start, original.End);

        Allocation Remainder(DateTimeOffset start, DateTimeOffset end) => original with
        {
            Id = Guid.NewGuid().ToString("N"), Start = start, End = end, Status = Disposition.Unassigned,
            Task = new TaskLabel(), Approved = false, ApprovedMode = null, ApprovedKey = null
        };
    }

    /// <summary>
    /// Suggests an issue for unassigned active rows from the nearest detected issue around them: the closest
    /// assigned row before, else after, within the window. A row whose mapped client differs from that issue's
    /// client gets no suggestion. Suggestions change nothing until the user accepts them.
    /// </summary>
    public static IReadOnlyDictionary<string, TaskLabel> Suggest(IReadOnlyList<Allocation> rows, TimeSpan? window = null)
    {
        var reach = window ?? TimeSpan.FromMinutes(30);
        var anchors = rows.Where(row => row.Status == Disposition.Billable && TaskLabel.ValidIssue(row.Task.Issue))
            .OrderBy(row => row.Start).ToArray();
        var suggestions = new Dictionary<string, TaskLabel>(StringComparer.Ordinal);
        if (anchors.Length == 0)
            return suggestions;
        foreach (var row in rows)
        {
            if (row.Status != Disposition.Unassigned || row.Kind is not (SpanKind.Foreground or SpanKind.Manual))
                continue;
            var before = anchors.LastOrDefault(anchor => anchor.End <= row.Start && row.Start - anchor.End <= reach);
            var after = anchors.FirstOrDefault(anchor => anchor.Start >= row.End && anchor.Start - row.End <= reach);
            var anchor = before ?? after;
            if (anchor == null)
                continue;
            if (row.Task.Client.Length > 0 && anchor.Task.Client.Length > 0 &&
                !StringComparer.OrdinalIgnoreCase.Equals(row.Task.Client, anchor.Task.Client))
                continue;
            suggestions[row.Id] = anchor.Task with { Link = "" };
        }
        return suggestions;
    }

    public static bool Intersects(Allocation left, Allocation right) =>
        left.Start < right.End && right.Start < left.End;

    private static Worklog CreateWorklog(IEnumerable<Allocation> group)
    {
        var rows = group.ToArray();
        var first = rows[0];
        var actual = rows.Sum(row => row.Milliseconds);
        var clients = rows.Select(row => row.Task.Client).Where(client => client.Length > 0).Distinct().ToArray();
        var description = string.Join(" / ", rows.Select(row => row.Task.Description)
            .Where(value => value.Length > 0).Distinct());
        return new Worklog(first.Task.Issue, string.Join(" / ", clients), first.Start, actual,
            RoundSeconds(actual), description, rows.Select(row => row.Id).ToArray(),
            rows.Select(row => new WorkInterval(row.Start, row.End)).ToArray());
    }
}

public static class WorklogCsv
{
    public static string Export(DateOnly day, IEnumerable<Worklog> worklogs, TimeZoneInfo zone, bool includeDescriptions)
    {
        var csv = new StringBuilder("Date,Start,Client,Issue,Actual seconds,Proposed seconds,To log");
        if (includeDescriptions)
            csv.Append(",Description");
        csv.Append("\r\n");
        foreach (var worklog in worklogs)
        {
            string[] cells = [day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                TimeZoneInfo.ConvertTime(worklog.Start, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                worklog.Client, worklog.Issue, (worklog.ActualMilliseconds / 1000d).ToString("0.###", CultureInfo.InvariantCulture),
                worklog.ProposedSeconds.ToString(CultureInfo.InvariantCulture), TempoDuration(worklog.ProposedSeconds)];
            csv.AppendJoin(',', cells.Select(Escape));
            if (includeDescriptions)
                csv.Append(',').Append(Escape(worklog.Description));
            csv.Append("\r\n");
        }
        return csv.ToString();
    }

    /// <summary>Formats a duration the way it is typed into Tempo, for example 1h 45m.</summary>
    public static string TempoDuration(int seconds)
    {
        var minutes = seconds / 60;
        return minutes >= 60 ? $"{minutes / 60}h" + (minutes % 60 == 0 ? "" : $" {minutes % 60}m") : $"{minutes}m";
    }

    private static string Escape(string cell)
    {
        var ordinary = cell.TrimStart();
        if ((ordinary.Length > 0 && "=+-@".Contains(ordinary[0])) ||
            (cell.Length > 0 && "\r\n\t".Contains(cell[0])))
            cell = "'" + cell;
        return $"\"{cell.Replace("\"", "\"\"")}\"";
    }
}
