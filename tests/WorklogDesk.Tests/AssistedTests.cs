using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using WorklogDesk.Core;
using WorklogDesk.Windows;

internal static class AssistedTests
{
    private static readonly DateOnly Day = new(2026, 1, 1);
    private static DateTimeOffset Origin => Timeline.DayStart(Day, TimeZoneInfo.Utc);
    private static readonly HashSet<string> None = new(StringComparer.Ordinal);

    // ----- Store guard -------------------------------------------------------------------------------------------

    public static void StaleAssignment() => WithFolder(folder =>
    {
        using var store = new WorkStore(folder, inMemory: true);
        var first = Row(0, 600, "VS Code");
        var second = Row(600, 1200, "Teams");
        store.ReplaceRows([], [first, second]);

        var snapshot = store.Rows(Day).Single(row => row.Id == first.Id);
        var assigned = snapshot with { Status = Disposition.Billable, Task = new TaskLabel("Client", "BC-7") };
        store.ReplaceRows([], [assigned], [snapshot]);
        var saved = store.Rows(Day).Single(row => row.Id == first.Id);
        Equal("BC-7", saved.Task.Issue);
        store.ReplaceRows([], [saved with { Status = Disposition.Unassigned, Task = new TaskLabel("", "") }], [saved]);
        Equal(Disposition.Unassigned, store.Rows(Day).Single(row => row.Id == first.Id).Status);

        var stale = store.Rows(Day).Single(row => row.Id == second.Id);
        store.ReplaceRows([], [stale with { Status = Disposition.Internal, Task = new TaskLabel("Internal", "") }]);
        Throws<InvalidOperationException>(() => store.ReplaceRows([],
            [stale with { Status = Disposition.Billable, Task = new TaskLabel("Client", "BC-8") }], [stale]), "changed");

        var current = store.Rows(Day).Single(row => row.Id == first.Id);
        var assign = current with { Status = Disposition.Billable, Task = new TaskLabel("Client", "BC-7") };
        foreach (var change in new[]
                 {
                     assign with { End = current.End.AddMinutes(-1) }, assign with { Kind = SpanKind.Manual }, assign with { App = "Teams" },
                     assign with { Context = "rewritten" }, current with { Status = Disposition.Internal, Task = new TaskLabel("Internal", "") },
                     current with { Status = Disposition.Excluded }, current with { Status = Disposition.Billable, Task = new TaskLabel("Client", "") },
                     assign with { Approved = true }
                 })
            Throws<InvalidOperationException>(() => store.ReplaceRows([], [change], [current]), "only assign");
        Throws<InvalidOperationException>(() => store.ReplaceRows([second.Id], [assign], [current]), "only assign");
        Equal(2, store.Rows(Day).Count);
    });

    public static void ProtectedTime() => WithFolder(folder =>
    {
        using var store = new WorkStore(folder, inMemory: true);
        var entered = Row(0, 900, "VS Code") with { Status = Disposition.Billable, Task = new TaskLabel("Client", "BC-5", "Work") };
        var open = Row(900, 1500, "VS Code");
        store.ReplaceRows([], [entered, open]);
        store.RecordExport(Day, Timeline.Preview(store.Rows(Day), RoundingMode.IssueDay));
        var snapshot = store.Rows(Day).Single(row => row.Id == open.Id);
        var data = store.ReadGuardData(Day);
        // The pure check and the store agree.
        Equal(AssistedGuard.Protected, AssistedGuard.Evaluate(data, [snapshot],
            [snapshot with { Status = Disposition.Billable, Task = new TaskLabel("Client", "BC-5") }]));
        Throws<InvalidOperationException>(() => store.ReplaceRows([],
            [snapshot with { Status = Disposition.Billable, Task = new TaskLabel("Client", "BC-5") }], [snapshot]), "copied, or entered");
        Equal<string?>(null, AssistedGuard.Evaluate(data, [snapshot],
            [snapshot with { Status = Disposition.Billable, Task = new TaskLabel("Client", "BC-9") }]));
        True(!AssistedGuard.DayBlocked(data));
        store.RecordHandoffKeys(Day, ["stale-key"]);
        True(AssistedGuard.DayBlocked(store.ReadGuardData(Day)));
    });

    public static void SegmentGuards() => WithFolder(folder =>
    {
        using var store = new WorkStore(folder, inMemory: true);
        var a = Row(0, 600, "VS Code", "repo - file.cs");
        var b = Row(600, 1200, "VS Code", "repo - other.cs");
        var later = Row(3000, 3600, "Teams", "Chat - Jane");
        store.ReplaceRows([], [a, b, later]);
        var snapshot = store.Rows(Day).ToArray();
        var horizon = snapshot.Max(row => row.End);
        var guard = new SegmentGuard(a.Start, b.End, horizon, snapshot);
        var changes = new[] { a, b }.Select(row => row with { Status = Disposition.Billable, Task = new TaskLabel("", "BC-1", "Work") }).ToArray();
        var expected = snapshot.Where(row => row.Id == a.Id || row.Id == b.Id).ToArray();

        // A span that was open at request time closes later and starts exactly at the horizon: still accepted.
        var tail = Row(3600, 3900, "Teams", "Chat - Jane");
        store.ReplaceRows([], [tail]);
        Equal<string?>(null, AssistedGuard.CheckSegments(store.Rows(Day), [guard]));
        // A new row inside the window (not one being assigned): refused by the segment guard.
        var inserted = Row(400, 450, "Teams", "Chat - Bob") with { Status = Disposition.Internal, Task = new TaskLabel("Internal") };
        store.ReplaceRows([], [inserted]);
        Throws<InvalidOperationException>(() => store.ReplaceRows([], changes, expected, [guard]), "part of the day changed");
        store.ReplaceRows([inserted.Id], []);
        // Totals changed between confirmation and apply: refused by the verify callback, nothing written.
        Throws<InvalidOperationException>(() => store.ReplaceRows([], changes, expected, [guard], _ => "Tempo totals changed"), "Tempo totals");
        Equal(Disposition.Unassigned, store.Rows(Day).Single(row => row.Id == a.Id).Status);
        store.ReplaceRows([], changes, expected, [guard], current =>
            DayReconstruction.Effect(current, changes, RoundingMode.IssueDay).Fingerprint ==
            DayReconstruction.Effect(snapshot, changes, RoundingMode.IssueDay).Fingerprint ? null : "Tempo totals changed");
        Equal("BC-1", store.Rows(Day).Single(row => row.Id == b.Id).Task.Issue);
        // Undo through the same guard restores the rows.
        store.ReplaceRows([], expected, store.Rows(Day).Where(row => row.Id == a.Id || row.Id == b.Id).ToArray());
        True(store.Rows(Day).Where(row => row.Id == a.Id || row.Id == b.Id).All(row => row.Status == Disposition.Unassigned));
    });

    // ----- Titles and redaction ----------------------------------------------------------------------------------

    public static void Redaction()
    {
        foreach (var kept in new[] { "BC-1234 - 2026-11-23", "26.0.12345.0", "Build 20261123.4", "feature/BC-1234-fix-posting-routine-errors", "10:42" })
            Equal(kept, AssistedEvidence.Redact(kept, 200));
        foreach (var (input, gone) in new[]
                 {
                     ("call +370 612 34567 now", "34567"), ("mail jane@x.com", "jane@x.com"), ("see https://intra.example/doc", "intra.example"),
                     ("key sk-abcdef1234", "sk-abcdef"), ("token aB3dE5gH7jK9mN1pQ3sT5vX7zA9cE1gI3", "aB3dE5")
                 })
            True(!AssistedEvidence.Redact(input, 200).Contains(gone));
    }

    public static void TitleRules()
    {
        True(WindowContext.Normalize("Report and 3 more pages - Work - Microsoft Edge") == "Report - Work");
        Equal("Outlook web", WindowContext.WebApp("Subject - Jane Doe - Outlook and 3 more pages - Work - Microsoft Edge"));
        Equal("Outlook web", WindowContext.WebApp("Mail - Jane Doe - Outlook - Google Chrome"));
        Equal("Teams web", WindowContext.WebApp("Chat | Jane | Microsoft Teams - Microsoft Edge"));
        Equal<string?>(null, WindowContext.WebApp("[BC-12] Restore failed - Jira - Microsoft Edge"));
        Equal("Outlook", AssistedEvidence.AppFamily("Browser · Outlook web"));
        Equal("Teams", AssistedEvidence.AppFamily("Browser · Teams web"));

        var cutoff = Origin.AddHours(12);
        var policy = new TitlePolicy(true, None, false, false, cutoff, "Jane Doe", "jane@x.com", None);
        TitleDecision Decide(Allocation row, string title, TitlePolicy? custom = null) => DayEpisodes.Decide(row, title, custom ?? policy);
        var early = Row(0, 60, "Browser");
        var late = Row(13 * 3600, 13 * 3600 + 60, "Browser");
        Equal(TitleDecision.Mail, Decide(late, "Mail - Jane Doe"));
        Equal(TitleDecision.Mail, Decide(late, "Re: contract renewal - Jane Doe"));
        Equal(TitleDecision.Mail, Decide(late, "Subject - Name - Outlook - Work"));
        Equal(TitleDecision.Mail, Decide(late, "Subject - Jane Doe - Outlook and 3 more pages"));
        Equal(TitleDecision.Mail, Decide(late, "Invoice from bob@corp.com"));
        Equal(TitleDecision.Sent, Decide(late, "[BC-12] Restore failed"));
        Equal(TitleDecision.Sent, Decide(early, "[BC-12] Restore failed"));
        // No Microsoft account yet: older browser titles stay behind the opt-in.
        Equal(TitleDecision.OlderBrowser, Decide(early, "[BC-12] Restore failed", policy with { MailName = null, MailAddress = null }));
        Equal(TitleDecision.Sent, Decide(early, "[BC-12] Restore failed", policy with { MailName = null, MailAddress = null, OlderBrowser = true }));
        // Teams off: older browser titles are held back too, because older Teams-web titles can't be told apart.
        var teamsOff = policy with { FamiliesOff = new HashSet<string> { "Teams" } };
        Equal(TitleDecision.OlderBrowser, Decide(early, "[BC-12] Restore failed", teamsOff));
        Equal(TitleDecision.FamilyOff, Decide(Row(0, 60, "Teams"), "Chat - Jane", teamsOff));
        Equal(TitleDecision.FamilyOff, Decide(late, "Chat - Microsoft Teams", teamsOff));
        Equal(TitleDecision.Mail, Decide(Row(0, 60, "Outlook"), "Re: hello"));
        Equal(TitleDecision.Mail, Decide(Row(13 * 3600, 13 * 3600 + 60, "Browser · Outlook web"), "Anything"));
        Equal(TitleDecision.Sent, Decide(Row(0, 60, "Outlook"), "Re: hello", policy with { MailSubjects = true }));
        // With mail included, a single untick and a switched-off Outlook family still hold.
        Equal(TitleDecision.Unticked, Decide(Row(0, 60, "Outlook"), "Re: hello", policy with { MailSubjects = true, Unticked = new HashSet<string> { "Re: hello" } }));
        Equal(TitleDecision.FamilyOff, Decide(Row(0, 60, "Outlook"), "Re: hello", policy with { MailSubjects = true, FamiliesOff = new HashSet<string> { "Outlook" } }));
        Equal(TitleDecision.Unticked, Decide(late, "Mail - Jane Doe", policy with { MailSubjects = true, Unticked = new HashSet<string> { "Mail - Jane Doe" } }));
        // Excluded, idle and internal titles never leave the PC.
        Equal(TitleDecision.State, Decide(Row(0, 60, "VS Code") with { Status = Disposition.Excluded }, "x"));
        Equal(TitleDecision.State, Decide(Row(0, 60, "VS Code") with { Kind = SpanKind.Idle }, "x"));
        Equal(TitleDecision.State, Decide(Row(0, 60, "VS Code") with { Status = Disposition.Internal }, "x"));
        Equal(TitleDecision.Unticked, Decide(Row(0, 60, "VS Code"), "x", policy with { Unticked = new HashSet<string> { "x" } }));
        Equal(TitleDecision.Disabled, Decide(Row(0, 60, "VS Code"), "x", TitlePolicy.Off));
    }

    // ----- Episodes ----------------------------------------------------------------------------------------------

    public static void Episodes()
    {
        var idle = Row(300, 600, "VS Code") with { Kind = SpanKind.Idle };
        var billableIdle = Row(600, 700, "Teams") with { Kind = SpanKind.Idle, Status = Disposition.Billable, Task = new TaskLabel("", "BC-3") };
        Equal(EpisodeState.Idle, DayEpisodes.State(idle));
        Equal(EpisodeState.Assigned, DayEpisodes.State(billableIdle));
        Equal(EpisodeState.Internal, DayEpisodes.State(idle with { Status = Disposition.Internal }));
        Equal(EpisodeState.Excluded, DayEpisodes.State(Row(0, 5, "Teams") with { Kind = SpanKind.Incidental, Status = Disposition.Excluded }));
        Equal(EpisodeState.Uncertain, DayEpisodes.State(Row(0, 5, "VS Code") with { Kind = SpanKind.Uncertain }));
        Equal(EpisodeState.UnassignedOther, DayEpisodes.State(Row(0, 5, "VS Code") with { Status = Disposition.Billable }));
        Equal(EpisodeState.Other, DayEpisodes.State(Row(0, 5, "VS Code") with { Approved = true }));

        // VS Code in one repo, a 5 s excluded Teams switch, VS Code again: one episode. Another repo: a new one.
        var rows = new[]
        {
            Row(0, 120, "VS Code", "a.cs - erp"),
            Row(120, 125, "Teams", "Chat - Jane") with { Kind = SpanKind.Incidental, Status = Disposition.Excluded },
            Row(125, 300, "VS Code", "b.cs - erp"),
            Row(300, 306, "Teams", "Chat - Bob"),
            Row(306, 500, "VS Code", "c.cs - erp"),
            Row(500, 700, "VS Code", "x.cs - portal"),
            Row(700, 800, "Browser", "[BC-1] Fix - Jira") with { Status = Disposition.Billable, Task = new TaskLabel("", "BC-1") },
            Row(800, 900, "Browser", "[BC-1] Fix - Jira") with { Status = Disposition.Billable, Task = new TaskLabel("", "BC-1") }
        };
        var episodes = DayEpisodes.Build(rows, Origin);
        Equal(3, episodes.Length);
        Equal(5, episodes[0].Rows.Length);
        Equal(EpisodeState.Assigned, episodes[2].State);
        Equal(rows.Sum(row => row.Milliseconds), episodes.Sum(episode => episode.Milliseconds));
        Equal(rows.Length, episodes.SelectMany(episode => episode.Rows).Select(row => row.Id).Distinct().Count());
        var original = episodes[..2];
        var regrouped = episodes[0] with { Rows = original.SelectMany(episode => episode.Rows).ToArray() };
        True(DayReconstruction.RowsFingerprint(original, rows) != DayReconstruction.RowsFingerprint([regrouped], rows));

        // A realistic day fits without the fallback; a very busy one uses 5-minute windows and still conserves time.
        var day = RealisticDay();
        var built = DayEpisodes.Build(day, Origin);
        True(built.Length < DayEpisodes.Cap && built.Length < day.Count);
        Equal(day.Sum(row => row.Milliseconds), built.Sum(episode => episode.Milliseconds));
        var busy = Enumerable.Range(0, 600).Select(index => Row(index * 40, index * 40 + 30, index % 2 == 0 ? "VS Code" : "Browser",
            $"page {index}")).ToArray();
        var windows = DayEpisodes.Build(busy, Origin);
        True(windows.Length <= DayEpisodes.Cap);
        Equal(busy.Sum(row => row.Milliseconds), windows.Sum(episode => episode.Milliseconds));
    }

    public static void Facts()
    {
        var rows = new[]
        {
            Row(0, 600, "VS Code", "a.cs - erp"),
            Row(600, 900, "Browser", "Personal banking") with { Status = Disposition.Excluded },
            Row(900, 1200, "Teams", "Chat - Jane Doe"),
            Row(1200, 1500, "Outlook", "Re: contract renewal"),
            Row(1500, 2400, "VS Code", "lunch") with { Kind = SpanKind.Idle },
            Row(2400, 2500, "VS Code", "notes") with { Status = Disposition.Internal, Task = new TaskLabel("Internal") }
        };
        var episodes = DayEpisodes.Build(rows, Origin);
        var policy = new TitlePolicy(true, None, false, false, Origin, "Jane Doe", null, None);
        var facts = DayReconstruction.Facts(Day, TimeZoneInfo.Utc, episodes, policy,
            [new Meeting(Origin.AddMinutes(10), Origin.AddMinutes(20), "Standup", true)], [new(Origin, "Quarterly invoice")],
            [new JiraCandidate("BC-1", "Fix posting", "In Progress", "ERP", "Year-end", "Contoso", true)], []);
        True(facts.Contains("a.cs - erp") && facts.Contains("Chat - Jane Doe") && facts.Contains("Standup"));
        foreach (var hidden in new[] { "Personal banking", "contract renewal", "lunch", "notes", "Quarterly invoice" })
            True(!facts.Contains(hidden));
        using (var document = JsonDocument.Parse(facts))
        {
            var root = document.RootElement;
            Equal(episodes.Length, root.GetProperty("episodes").GetArrayLength());
            True(root.GetProperty("jira_candidates")[0].GetProperty("locked").GetBoolean());
        }
        // Preview and request agree: a title sent from any row is shown as sent.
        var split = DayEpisodes.Build([Row(0, 60, "Browser", "[BC-1] Fix"), Row(7200, 7260, "Browser", "[BC-1] Fix")], Origin);
        var noAccount = new TitlePolicy(true, None, false, false, Origin.AddHours(1), null, null, None);
        Equal(TitleDecision.Sent, DayReconstruction.Titles(split, noAccount, None).Single().Decision);
        True(DayReconstruction.Facts(Day, TimeZoneInfo.Utc, split, noAccount, [], [], [], []).Contains("[BC-1] Fix"));
        var teamsOff = DayReconstruction.Facts(Day, TimeZoneInfo.Utc, episodes, policy with { FamiliesOff = new HashSet<string> { "Teams" } }, [], [], [], []);
        True(!teamsOff.Contains("Jane Doe"));
        var withMail = DayReconstruction.Facts(Day, TimeZoneInfo.Utc, episodes, policy with { MailSubjects = true }, [], [new(Origin, "Quarterly invoice")], [], []);
        True(withMail.Contains("contract renewal") && withMail.Contains("Quarterly invoice"));
        var body = DayReconstruction.BuildRequest(AiService.Claude, "claude-opus-5-5", facts);
        using (var request = JsonDocument.Parse(body))
        {
            Equal(DayReconstruction.MaxTokens, request.RootElement.GetProperty("max_tokens").GetInt32());
            Equal("default", request.RootElement.GetProperty("fallbacks").GetString());
            Equal(facts, request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        }
        var openai = DayReconstruction.BuildRequest(AiService.OpenAI, "gpt-5-mini", facts);
        True(openai.Contains("\"store\": false") && openai.Contains("worklog_reconstruction"));
        Throws<ArgumentException>(() => DayReconstruction.BuildRequest(AiService.Claude, "gpt-5-mini", facts), "Claude model");
    }

    // ----- Validation --------------------------------------------------------------------------------------------

    public static void Validation()
    {
        // Episodes 1..12 alternate unassigned VS Code work with anchors and gaps.
        var rows = new List<Allocation>();
        for (var index = 0; index < 8; index++)
            rows.Add(Row(index * 300, index * 300 + 290, "VS Code", $"f{index}.cs - repo{index}"));
        rows.Add(Row(2400, 2460, "Browser", "[BC-2] Other") with { Status = Disposition.Billable, Task = new TaskLabel("", "BC-2") });
        rows.Add(Row(2460, 2760, "VS Code", "g.cs - repoX"));
        rows.Add(Row(2760, 4000, "VS Code", "idle") with { Kind = SpanKind.Idle });
        rows.Add(Row(4000, 4300, "VS Code", "h.cs - repoY"));
        var episodes = DayEpisodes.Build(rows, Origin);
        Equal(12, episodes.Length);
        JiraCandidate[] candidates = [new("BC-1", "Fix", "Open"), new("BC-2", "Other", "Open"), new("BC-9", "Locked", "Done", Locked: true)];
        string Segment(int first, int last, string action = "assign", string issue = "BC-1", string title = "", string description = "",
            int meeting = -1, int[]? exclude = null, string reason = "VS Code repo evidence") =>
            JsonSerializer.Serialize(new
            {
                first_episode = first, last_episode = last, action, issue, title, description, meeting,
                exclude_episodes = exclude ?? [], confidence = "high", reason
            });
        Segment[] Parse(params string[] items) => DayReconstruction.Parse($"{{\"segments\":[{string.Join(',', items)}]}}", episodes, candidates, []);

        // Whole-response rejections.
        foreach (var bad in new[]
                 {
                     "not json", "{}", """{"segments":{}}""", $"{{\"segments\":[{Segment(0, 1)}]}}", $"{{\"segments\":[{Segment(1, 13)}]}}",
                     $"{{\"segments\":[{Segment(3, 2)}]}}", $"{{\"segments\":[{Segment(1, 1, meeting: 0)}]}}",
                     $"{{\"segments\":[{Segment(1, 1, exclude: [99])}]}}", """{"segments":[{"first_episode":"1"}]}""",
                     "{\"segments\":[" + string.Join(',', Enumerable.Repeat(Segment(1, 1), 81)) + "]}"
                 })
            Throws<InvalidOperationException>(() => DayReconstruction.Parse(bad, episodes, candidates, []), "invalid plan");

        // Numeric order: 9 < 10 (string order would say otherwise).
        var ok = Parse(Segment(1, 8), Segment(10, 10));
        True(ok.All(segment => segment.Invalid == null));
        // Per-segment rules.
        Equal("Overlaps another proposal.", Parse(Segment(1, 3), Segment(3, 4))[0].Invalid);
        True(Parse(Segment(1, 10))[0].Invalid!.Contains("another issue"));
        True(Parse(Segment(10, 12))[0].Invalid!.Contains("long idle"));
        True(Parse(Segment(1, 1, issue: "BC-404"))[0].Invalid!.Contains("Jira candidates"));
        Equal<string?>(null, Parse(Segment(1, 1, issue: "BC-9"))[0].Invalid);
        True(Parse(Segment(9, 9, action: "review", issue: ""))[0].Invalid!.Contains("no unassigned"));
        True(Parse(Segment(1, 2, exclude: [1, 2]))[0].Invalid!.Contains("no unassigned"));
        True(Parse(Segment(1, 1, action: "draft", issue: ""))[0].Invalid!.Contains("draft"));
        Equal<string?>(null, Parse(Segment(1, 1, action: "draft", issue: "", title: "Investigate restore"))[0].Invalid);
        True(Parse(Segment(1, 1, action: "review", issue: "BC-1"))[0].Invalid!.Contains("review"));
        True(Parse(Segment(1, 1, reason: ""))[0].Invalid!.Contains("reason"));
    }

    // ----- Applying ----------------------------------------------------------------------------------------------

    public static void Applying() => WithFolder(folder =>
    {
        using var store = new WorkStore(folder, inMemory: true);
        var anchor = Row(0, 600, "Browser", "[BC-1] Fix") with { Status = Disposition.Billable, Task = new TaskLabel("Contoso", "BC-1", "Fix posting") };
        var work = Row(600, 1200, "VS Code", "a.cs - erp");
        var stale = Row(1200, 1800, "VS Code", "b.cs - erp") with { Task = new TaskLabel("Fabrikam", "BC-2", "Other issue text") };
        var chat = Row(1800, 1830, "Teams", "Chat - Bob");
        var more = Row(1830, 2400, "VS Code", "c.cs - erp");
        store.ReplaceRows([], [anchor, work, stale, chat, more]);
        var rows = store.Rows(Day);
        var episodes = DayEpisodes.Build(rows, Origin);
        var segment = new Segment(0, 2, episodes.Length, "assign", "BC-1", "", "", -1, [], "high", "x");
        var chatEpisode = episodes.Single(episode => episode.Rows.Any(row => row.Id == chat.Id)).Number;
        var changes = DayReconstruction.Changes(segment, episodes, new HashSet<int> { chatEpisode }, "BC-1", DayReconstruction.Anchor(rows, "BC-1"));
        True(changes.All(row => row.Id != chat.Id && row.Status == Disposition.Billable && row.Task.Issue == "BC-1"));
        // The stale row's BC-2 description and client never reach BC-1; the anchor's client is used.
        var fixedStale = changes.Single(row => row.Id == stale.Id);
        Equal("", fixedStale.Task.Description);
        Equal("Contoso", fixedStale.Task.Client);
        True(DayReconstruction.ClientConflict(rows.Where(row => row.Id == stale.Id), "BC-1", DayReconstruction.Anchor(rows, "BC-1")));

        // Effects come from Preview: IssueDay merges, Session splits around the unticked chat.
        var issueDay = DayReconstruction.Effect(rows, changes, RoundingMode.IssueDay);
        var session = DayReconstruction.Effect(rows, changes, RoundingMode.Session);
        True(issueDay.RecordedAddedMs == changes.Sum(row => row.Milliseconds));
        True(session.Issues.Single(item => item.Issue == "BC-1").LinesAfter > issueDay.Issues.Single(item => item.Issue == "BC-1").LinesAfter);
        True(session.RoundedAddedSeconds >= issueDay.RoundedAddedSeconds);
        True(DayReconstruction.Effect(rows, [Row(5000, 5060, "VS Code") with { Id = work.Id }], RoundingMode.IssueDay) is not null);
        // A new short line is flagged.
        var tiny = rows.Single(row => row.Id == chat.Id) with { Status = Disposition.Billable, Task = new TaskLabel("", "BC-7", "x") };
        Equal("BC-7", DayReconstruction.Effect(rows, [tiny], RoundingMode.Session).SmallNewLines.Single());

        // Approval needs a description: the accept fills it, then the line can be approved.
        var final = changes.Select(row => row.Task.Description.Length > 0 ? row : row with { Task = row.Task with { Description = "Fix posting" } }).ToArray();
        store.ReplaceRows([], final, rows.Where(row => final.Any(change => change.Id == row.Id)).ToArray());
        True(Timeline.Preview(store.Rows(Day), RoundingMode.IssueDay).Where(line => line.Issue == "BC-1").All(line => line.Description.Length > 0));
    });

    public static void History()
    {
        var today = new[] { Row(0, 600, "Remote Desktop", "BC-PROD-01"), Row(600, 900, "Teams", "Chat - Jane") };
        var episodes = DayEpisodes.Build(today, Origin);
        var policy = new TitlePolicy(true, None, false, false, Origin.AddDays(-60), "Me", null, None);
        var tokens = DayReconstruction.SentTokens(episodes, policy);
        Allocation Past(int days, string app, string title, string issue, bool approved) =>
            Row(0, 1800, app, title) with { Day = Day.AddDays(-days), Status = Disposition.Billable, Approved = approved, Task = new TaskLabel("", issue, "x") };
        var history = new List<(DateOnly, List<Allocation>, List<ManualEntry>)>
        {
            (Day.AddDays(-2), [Past(2, "Remote Desktop", "BC-PROD-01", "BC-5", true), Past(2, "Remote Desktop", "OTHER-HOST", "BC-6", true)], []),
            (Day.AddDays(-3), [Past(3, "Teams", "Chat - Jane", "BC-8", false)], []),
            (Day.AddDays(-4), [Past(4, "Teams", "Chat - Jane", "BC-9", false) with { Id = "entered" }], [new ManualEntry("m", "BC-9", RoundingMode.IssueDay, 900, ["entered"])]),
            // An unapproved AI-assigned row on a day whose Tempo record covers other rows of the issue doesn't count.
            (Day.AddDays(-5), [Past(5, "Teams", "Chat - Jane", "BC-10", false) with { Id = "ai" }], [new ManualEntry("m2", "BC-10", RoundingMode.IssueDay, 900, ["other"])])
        };
        var hints = DayReconstruction.Hints(history, tokens, policy);
        True(hints.Any(hint => hint.Issue == "BC-5" && hint.Minutes == 30));
        True(hints.All(hint => hint.Issue != "BC-6" && hint.Issue != "BC-8"));
        True(hints.Any(hint => hint.Issue == "BC-9"));
        True(hints.All(hint => hint.Issue != "BC-10"));
        var teamsOff = policy with { FamiliesOff = new HashSet<string> { "Teams" } };
        True(DayReconstruction.Hints(history, DayReconstruction.SentTokens(episodes, teamsOff), teamsOff).All(hint => hint.Source != "Teams"));
    }

    // ----- External services -------------------------------------------------------------------------------------

    public static void JiraCandidates()
    {
        var settings = JiraSettings.Create("https://company.atlassian.net", "demo@example.com", "", "BC, OPS", "Waiting");
        var requests = new List<(HttpMethod Method, string Uri, string Body)>();
        var refuseWatcher = true;
        using var handler = new StubHandler(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
            requests.Add((request.Method, Uri.UnescapeDataString(request.RequestUri!.ToString()), body));
            if (request.Method == HttpMethod.Get && request.RequestUri!.ToString().Contains("watcher") && refuseWatcher)
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            if (request.Method == HttpMethod.Get)
                return Json("""{"issues":[{"key":"BC-1","fields":{"summary":"Fix","status":{"name":"Open"},"project":{"name":"Business Central"},"parent":{"fields":{"summary":"Year-end"}}}},{"key":"XX-1","fields":{"summary":"Outside","status":{"name":"Open"}}}]}""");
            return Json("""{"issues":[{"key":"OPS-7","fields":{"summary":"Deploy","status":{"name":"Done"}}}],"issueErrors":[{"id":"BC-404"}]}""");
        });
        using var catalog = new JiraCatalog(settings, "fake-token", handler);
        var found = catalog.CandidatesAsync(["OPS-7", "BC-404", "ZZ-1", "not a key"], CancellationToken.None).GetAwaiter().GetResult().Candidates;
        Equal("BC-1,OPS-7", string.Join(",", found.Select(item => item.Key)));
        Equal("Business Central", found[0].Project);
        Equal("Year-end", found[0].Parent);
        Equal(3, requests.Count);
        True(requests[0].Uri.Contains("watcher") && !requests[1].Uri.Contains("watcher"));
        True(requests.Take(2).All(item => item.Uri.Contains("project in (\"BC\",\"OPS\")")));
        True(requests[2].Method == HttpMethod.Post && requests[2].Body.Contains("OPS-7") && requests[2].Body.Contains("BC-404") &&
             !requests[2].Body.Contains("ZZ-1") && !requests[2].Body.Contains("not a key"));
        // No configured projects: no request, no candidates.
        using var none = new JiraCatalog(JiraSettings.Create("https://company.atlassian.net", "demo@example.com", "", "", "Waiting"), "fake-token", handler);
        requests.Clear();
        Equal(0, none.CandidatesAsync(["BC-1"], CancellationToken.None).GetAwaiter().GetResult().Candidates.Length);
        Equal(0, requests.Count);
        // Jira failing degrades with a warning instead of blocking the request.
        using var failing = new StubHandler(request => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : throw new HttpRequestException("offline"));
        using var broken = new JiraCatalog(settings, "fake-token", failing);
        var degraded = broken.CandidatesAsync(["OPS-7"], CancellationToken.None).GetAwaiter().GetResult();
        Equal(0, degraded.Candidates.Length);
        True(degraded.Warning != null && degraded.Warning.Contains("couldn't be read"));
    }

    public static void Calendar()
    {
        string Event(string subject, int startMinute, int endMinute, string response = "accepted", string showAs = "busy",
            bool cancelled = false, bool allDay = false, string sensitivity = "normal") => JsonSerializer.Serialize(new
            {
                subject, showAs, isCancelled = cancelled, isAllDay = allDay, sensitivity, isOrganizer = false,
                responseStatus = new { response },
                start = new { dateTime = Origin.AddMinutes(startMinute).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"), timeZone = "UTC" },
                end = new { dateTime = Origin.AddMinutes(endMinute).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"), timeZone = "UTC" }
            });
        const string Next = "https://graph.microsoft.com/v1.0/me/calendarView?$skiptoken=1";
        var calls = new List<HttpRequestMessage>();
        var result = Read(request =>
        {
            calls.Add(request);
            return calls.Count == 1
                ? Json($"{{\"value\":[{Event("Standup", 540, 555)},{Event("Declined", 600, 630, "declined")},{Event("Holiday", 0, 1440, allDay: true)}],\"@odata.nextLink\":\"{Next}\"}}")
                : Json($"{{\"value\":[{Event("Board", 660, 690, sensitivity: "private")},{Event("Invite", 700, 730, "notResponded")},{Event("Free", 740, 750, showAs: "free")},{Event("Gone", 760, 770, cancelled: true)}]}}");
        });
        Equal("Standup,Private meeting,Invite", string.Join(",", result.Items.Select(item => item.Subject)));
        True(result.Items.Single(item => item.Subject == "Invite").Unconfirmed && !result.Items[0].Unconfirmed);
        Equal(Origin.AddMinutes(540), result.Items[0].Start);
        True(calls[0].RequestUri!.Query.Contains("%24top=50") || calls[0].RequestUri!.Query.Contains("$top=50"));
        True(calls[0].Headers.GetValues("Prefer").Single().Contains("UTC"));
        // Paging stays on Graph's calendarView.
        result = Read(_ => Json($"{{\"value\":[{Event("A", 1, 2)}],\"@odata.nextLink\":\"https://evil.example/v1.0/me/calendarView\"}}"));
        True(result.Incomplete && result.Items.Length == 1);
        Throws<InvalidOperationException>(() => Read(_ => Json("<html>")), "unreadable");
        Throws<InvalidOperationException>(() => Read(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)), "HTTP 403");

        MeetingResult Read(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            using var handler = new StubHandler(respond);
            using var client = new HttpClient(handler);
            return OutlookEvidence.CalendarAsync(client, "token", Day, TimeZoneInfo.Utc, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    public static void OutlookPaging()
    {
        string Item(int minute, string subject) => JsonSerializer.Serialize(new { subject, sentDateTime = Origin.AddMinutes(minute).UtcDateTime.ToString("O") });
        string Page(string? next, params string[] items) =>
            $"{{\"value\":[{string.Join(',', items)}]{(next == null ? "" : $",\"@odata.nextLink\":{JsonSerializer.Serialize(next)}")}}}";
        const string Next = "https://graph.microsoft.com/v1.0/me/mailFolders/sentitems/messages?$skiptoken=abc";
        var calls = 0;
        var result = Read(_ => ++calls == 1 ? Json(Page(Next, Item(60, "First"), Item(-30, "Yesterday"))) : Json(Page(null, Item(120, "Second"))));
        Equal("First,Second", string.Join(",", result.Items.Select(item => item.Subject)));
        result = Read(_ => Json(Page("https://attacker.example/v1.0/me/mailFolders/sentitems/messages", Item(60, "First"))));
        True(result.Incomplete && result.Items.Length == 1);
        calls = 0;
        result = Read(_ => ++calls == 1 ? Json(Page(Next, Item(60, "First"))) : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        True(result.Incomplete && result.Items.Single().Subject == "First");
        Throws<InvalidOperationException>(() => Read(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), "HTTP 401");
        foreach (var bad in new[] { "<html>", "[]", "{}", """{"value":{}}""" })
            Throws<InvalidOperationException>(() => Read(_ => Json(bad)), "unreadable");

        SentEvidenceResult Read(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            using var handler = new StubHandler(respond);
            using var client = new HttpClient(handler);
            return OutlookEvidence.ReadAsync(client, "token", Day, TimeZoneInfo.Utc, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    public static void AiTransport()
    {
        var body = DayReconstruction.BuildRequest(AiService.Claude, "claude-opus-5-5", "{}");
        string Claude(string stop, params object[] content) => JsonSerializer.Serialize(new
        {
            stop_reason = stop, content, usage = new { input_tokens = 1200, output_tokens = 300 }
        });
        HttpRequestMessage? seen = null;
        var result = Send(AiService.Claude, body, HttpStatusCode.OK, Claude("end_turn", new { type = "thinking", thinking = "" },
            new { type = "fallback" }, new { type = "text", text = "{\"segments\":[]}" }), request => seen = request);
        Equal("{\"segments\":[]}", result.Text);
        Equal(1200L, result.InputTokens);
        Equal("sk-ant-test", seen!.Headers.GetValues("x-api-key").Single());
        Equal(AssistedInference.FallbackBeta, seen.Headers.GetValues("anthropic-beta").Single());
        Throws<AiTooLargeException>(() => Send(AiService.Claude, body, HttpStatusCode.OK, Claude("max_tokens")), "too large");
        Throws<InvalidOperationException>(() => Send(AiService.Claude, body, HttpStatusCode.OK, Claude("refusal")), "Claude declined");
        Throws<InvalidOperationException>(() => Send(AiService.Claude, body, HttpStatusCode.OK, "<html>"), "unreadable");
        Throws<InvalidOperationException>(() => Send(AiService.Claude, body, HttpStatusCode.Unauthorized,
            """{"error":{"message":"invalid x-api-key; see https://x.example"}}"""), "HTTP 401: invalid x-api-key; see [redacted]");
        var openai = DayReconstruction.BuildRequest(AiService.OpenAI, "gpt-5-mini", "{}");
        Throws<AiTooLargeException>(() => Send(AiService.OpenAI, openai, HttpStatusCode.OK,
            """{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"}}"""), "too large");
        var done = Send(AiService.OpenAI, openai, HttpStatusCode.OK,
            """{"status":"completed","usage":{"input_tokens":5,"output_tokens":7,"output_tokens_details":{"reasoning_tokens":3}},"output":[{"type":"message","content":[{"type":"output_text","text":"{}"}]}]}""");
        Equal(3L, done.ThinkingTokens);

        AiResult Send(AiService service, string request, HttpStatusCode code, string response, Action<HttpRequestMessage>? inspect = null)
        {
            using var handler = new StubHandler(message =>
            {
                inspect?.Invoke(message);
                return new HttpResponseMessage(code) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
            });
            using var client = new HttpClient(handler);
            return new AssistedInference(client).CompleteAsync(service, request, service == AiService.Claude ? "sk-ant-test" : "sk-test",
                CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    // ----- MCP ---------------------------------------------------------------------------------------------------

    public static void McpOverStdio() => WithFolder(folder =>
    {
        using (var store = new WorkStore(folder))
        {
            store.SavePreferences(store.Preferences() with { TimeZoneId = "UTC", MailLabelCutoff = Origin.AddDays(-1) });
            store.ReplaceRows([], [
                Row(0, 600, "VS Code", "BC-7 private title"),
                Row(600, 900, "Outlook · Mail", "Re: private subject"),
                Row(900, 1000, "Browser · Outlook web", "Web mail subject"),
                Row(1000, 1100, "Browser", "Mail - Someone"),
                Row(1100, 1200, "VS Code", "personal note") with { Status = Disposition.Excluded },
                Row(1200, 1500, "VS Code", "assigned title") with { Status = Disposition.Billable, Task = new TaskLabel("Client", "BC-7") }
            ]);
            store.CheckpointWal();
        }
        var exe = Path.Combine(AppContext.BaseDirectory, "WorklogDesk.Windows.exe");
        True(File.Exists(exe));

        var plain = Session(exe, folder, includeContext: false);
        using (var tools = JsonDocument.Parse(plain.Tools))
            Equal("worklog_activity,worklog_episodes", string.Join(",", tools.RootElement.GetProperty("result").GetProperty("tools")
                .EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).Order()));
        True(plain.Tools.Contains("\"readOnlyHint\":true"));
        foreach (var text in new[] { "private title", "private subject", "Web mail", "Mail - Someone", "personal note", "assigned title" })
            True(!plain.Activity.Contains(text) && !plain.Episodes.Contains(text));
        True(plain.Episodes.Contains("\"issue\":\"BC-7\""));

        var context = Session(exe, folder, includeContext: true);
        True(context.Activity.Contains("private title") && context.Episodes.Contains("assigned title"));
        foreach (var text in new[] { "private subject", "Web mail", "Mail - Someone", "personal note" })
            True(!context.Activity.Contains(text) && !context.Episodes.Contains(text));

        using var refused = Process.Start(new ProcessStartInfo(exe, ["--mcp", "not-a-date"])
        {
            RedirectStandardError = true, RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false
        })!;
        True(refused.WaitForExit(15_000));
        Equal(2, refused.ExitCode);
    });

    private sealed record McpResult(string Tools, string Activity, string Episodes);

    private static McpResult Session(string exe, string folder, bool includeContext)
    {
        var arguments = new List<string> { "--mcp", Day.ToString("yyyy-MM-dd"), "--data", folder };
        if (includeContext)
            arguments.Add("--include-context");
        using var process = Process.Start(new ProcessStartInfo(exe, arguments)
        {
            RedirectStandardError = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false)
        })!;
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            Send(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new
            {
                protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" }
            } });
            Receive(1);
            Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
            Send(new { jsonrpc = "2.0", id = 2, method = "tools/list" });
            var tools = Receive(2);
            Send(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "worklog_activity", arguments = new { } } });
            var activity = Text(Receive(3));
            Send(new { jsonrpc = "2.0", id = 4, method = "tools/call", @params = new { name = "worklog_episodes", arguments = new { } } });
            var episodes = Text(Receive(4));
            return new(tools, activity, episodes);
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(10_000))
                process.Kill(true);
            _ = errors.Wait(5_000);
        }

        static string Text(string call)
        {
            using var document = JsonDocument.Parse(call);
            var result = document.RootElement.GetProperty("result");
            True(!result.TryGetProperty("isError", out var isError) || !isError.GetBoolean());
            return result.GetProperty("content")[0].GetProperty("text").GetString()!;
        }

        void Send(object message)
        {
            process.StandardInput.WriteLine(JsonSerializer.Serialize(message));
            process.StandardInput.Flush();
        }

        string Receive(int id)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var read = process.StandardOutput.ReadLineAsync();
                if (!read.Wait(TimeSpan.FromSeconds(30)) || read.Result == null)
                    break;
                using var message = JsonDocument.Parse(read.Result);
                if (message.RootElement.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number && value.GetInt32() == id)
                    return read.Result;
            }
            throw new Exception($"No MCP response for request {id}. stderr: {(errors.IsCompleted ? errors.Result : "")}");
        }
    }

    // ----- Dialog ------------------------------------------------------------------------------------------------

    public static void DialogRefresh() => OnSta(() => WithFolder(folder =>
    {
        using var app = new TrayApplication(folder, true, "reconstruct-test", showOnStart: false, diagnostics: true);
        try
        {
            using var store = new WorkStore(folder, inMemory: true);
            store.ReplaceRows([], [Row(0, 600, "VS Code", "a.cs - erp"), Row(600, 605, "Teams", "Chat - Bob"),
                Row(605, 1200, "VS Code", "b.cs - erp"), Row(1200, 1800, "VS Code", "c.cs - portal")]);
            var host = new FakeHost();
            using var dialog = new ReconstructDayDialog(app, store, host, Day)
            {
                ShowInTaskbar = false, StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                Location = new System.Drawing.Point(System.Windows.Forms.SystemInformation.VirtualScreen.Right + 32, 0)
            };
            dialog.Show();
            var answer = JsonSerializer.Serialize(new { segments = new[] { new
            {
                first_episode = 1, last_episode = 2, action = "assign", issue = "BC-1", title = "", description = "", meeting = -1,
                exclude_episodes = Array.Empty<int>(), confidence = "high", reason = "VS Code erp work"
            } } });
            dialog.LoadForTest(answer, [new JiraCandidate("BC-1", "Fix posting", "Open")]);
            dialog.ProposalGrid.ClearSelection();
            dialog.ProposalGrid.Rows[0].Selected = true;
            var before = dialog.Refreshes;
            Pump();
            // Filling the episode list must not feed back into another refresh.
            True(dialog.Refreshes - before <= 1);
            Equal(2, dialog.EpisodeList.Items.Count);
            True(dialog.EpisodeList.GetItemChecked(0) && dialog.EpisodeList.GetItemChecked(1));
            dialog.EpisodeList.SetItemChecked(1, false);
            Pump();
            True(dialog.Unticked(0).Contains(2));
            True(dialog.Refreshes - before <= 4);
            // Open in Activity hands over only the ticked, assignable rows: not the unticked episode.
            Descendants(dialog).OfType<System.Windows.Forms.Button>().Single(button => button.Text == "Open in Activity").PerformClick();
            var opened = host.Opened ?? throw new Exception("Open in Activity did nothing.");
            var portal = store.Rows(Day).Single(row => row.Context == "c.cs - portal").Id;
            True(opened.Ids.Count == 3 && !opened.Ids.Contains(portal) && opened.Issue == "BC-1");
            dialog.Close();
        }
        finally { app.ShutDownDiagnostics(); }

        static void Pump()
        {
            for (var index = 0; index < 30; index++)
            {
                System.Windows.Forms.Application.DoEvents();
                Thread.Sleep(5);
            }
        }
    }));

    public static void DialogRestore() => WithReconstructionDialog((app, store, _) =>
    {
        var portal = Row(1200, 1800, "VS Code", "c.cs - portal");
        store.ReplaceRows([], [Row(0, 600, "VS Code", "a.cs - erp"), Row(600, 605, "Teams", "Chat - Bob"),
            Row(605, 1200, "VS Code", "b.cs - erp"), portal]);
        using (var first = OpenReconstruction(app, store))
        {
            LoadProposal(first, 1, 2);
            first.EpisodeList.SetItemChecked(0, false);
            PumpEvents();
            first.EpisodeList.SetItemChecked(1, false);
            PumpEvents();
            first.Close();
        }
        True(store.Reconstruction(Day, 0) != null);
        True(store.Reconstruction(Day.AddDays(1), 0) == null && store.Reconstruction(Day, 1) == null);
        store.ReplaceRows([], [Row(2400, 3000, "Word", "notes.docx")]);
        using (var second = OpenReconstruction(app, store))
        {
            Equal(1, second.ProposalCount);
            True(second.StatusLine.StartsWith("Restored the reconstruction", StringComparison.Ordinal));
            Equal(2, second.Unticked(0).Count);
            second.EpisodeList.SetItemChecked(0, true);
            PumpEvents();
            var portalEpisode = second.Episodes.Single(episode => episode.Rows.Any(row => row.Id == portal.Id)).Number;
            True(second.Unticked(0).SequenceEqual([portalEpisode]));
            True(second.ProposalGrid.Rows.Cast<System.Windows.Forms.DataGridViewRow>()
                .Any(row => (string?)row.Cells["proposal"].Value == "Not covered"));
            True(second.Invalid(0) == null);
            second.EpisodeList.SetItemChecked(1, true);
            PumpEvents();
            Equal(0, second.Unticked(0).Count);
            second.Close();
        }

        store.ReplaceRows([], [portal with { Task = new TaskLabel("New client", Description: "Edited allocation") }]);
        using (var third = OpenReconstruction(app, store))
        {
            Equal(1, third.ProposalCount);
            True(third.Invalid(0)?.StartsWith("The activity changed", StringComparison.Ordinal) == true);
            third.Close();
        }

        var captured = Row(1800, 2100, "VS Code", "c.cs - portal");
        store.ReplaceRows([], [portal, captured]);
        var host = new FakeHost();
        using var fourth = OpenReconstruction(app, store, host);
        True(fourth.Episodes.Any(episode => episode.Rows.Any(row => row.Id == portal.Id) && episode.Rows.Any(row => row.Id == captured.Id)));
        True(fourth.Invalid(0)?.StartsWith("The activity changed", StringComparison.Ordinal) == true);
        Descendants(fourth).OfType<System.Windows.Forms.Button>().Single(button => button.Text == "Open in Activity").PerformClick();
        True(host.Opened == null);
        fourth.Close();
    });

    public static void DialogAcceptedRestore() => WithReconstructionDialog((app, store, _) =>
    {
        var task = new TaskLabel("", "BC-1", "Fix posting");
        var anchor = Row(0, 3600, "VS Code") with { Status = Disposition.Billable, Task = task };
        var portal = Row(3600, 4500, "VS Code", "portal.cs");
        store.ReplaceRows([], [anchor, portal]);
        string proposalTime;
        using (var first = OpenReconstruction(app, store))
        {
            LoadProposal(first, 2, 2);
            proposalTime = (string)first.ProposalGrid.Rows[0].Cells["time"].Value!;
            first.Close();
        }

        var saved = store.Reconstruction(Day, 0)!;
        store.ReplaceRows([], [portal with { Status = Disposition.Billable, Task = task }]);
        store.SaveReconstruction(Day, 0, saved with
        {
            Proposals = [saved.Proposals.Single() with { Outcome = 1, AcceptedIds = [portal.Id] }]
        });
        using (var second = OpenReconstruction(app, store))
        {
            Equal(1, second.Episodes.Length);
            Equal(1, second.ProposalCount);
            Equal("Accepted", (string?)second.ProposalGrid.Rows[0].Cells["state"].Value);
            Equal(proposalTime, (string?)second.ProposalGrid.Rows[0].Cells["time"].Value);
            second.Close();
        }

        store.ReplaceRows([], [portal]);
        using var undone = OpenReconstruction(app, store);
        Equal(1, undone.ProposalCount);
        Equal(null, undone.Invalid(0));
        Equal("", (string?)undone.ProposalGrid.Rows[0].Cells["state"].Value);
        Equal(proposalTime, (string?)undone.ProposalGrid.Rows[0].Cells["time"].Value);
        undone.Close();
    });

    public static void ActivityHandoff() => WithReconstructionDialog((app, store, _) =>
    {
        var start = Timeline.DayStart(app.Today, app.Zone).AddHours(9);
        var first = Row(0, 600, "VS Code", "portal.cs") with { Day = app.Today, Start = start, End = start.AddMinutes(10) };
        var adjoining = Row(600, 900, "VS Code", "erp.cs") with { Day = app.Today, Start = first.End, End = first.End.AddMinutes(5) };
        store.ReplaceRows([], [first, adjoining]);
        using var window = new ReviewWindow(app, store, openWithoutActivation: true)
        {
            ShowInTaskbar = false, StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            Location = new System.Drawing.Point(System.Windows.Forms.SystemInformation.VirtualScreen.Right + 32, 0)
        };
        window.Show();
        PumpEvents();
        window.Reload();
        Equal(2, window.ActivityAt(0).Length);
        window.AssignOverride = initial => (initial with { Description = "Fix posting" }, Disposition.Billable);
        ((IReconstructHost)window).OpenInActivity(app.Today, [first.Id], "BC-1");
        var rows = store.Rows(app.Today);
        Equal("BC-1", rows.Single(row => row.Id == first.Id).Task.Issue);
        Equal(Disposition.Unassigned, rows.Single(row => row.Id == adjoining.Id).Status);
        window.Close();
    });

    public static void DialogScopeSaveRetry() => WithReconstructionDialog((app, store, disk) =>
    {
        store.ReplaceRows([], [Row(0, 600, "VS Code", "portal.cs")]);
        using var dialog = OpenReconstruction(app, store);
        LoadProposal(dialog, 1, 1);
        var picker = Descendants(dialog).OfType<System.Windows.Forms.ComboBox>().Single(combo => combo.AccessibleName == "Part of the day");
        using var locker = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(disk, "worklog.db"));
        locker.Open();
        using (locker.BeginTransaction())
        {
            dialog.EpisodeList.SetItemChecked(0, false);
            PumpEvents();
            True(Descendants(dialog).OfType<System.Windows.Forms.Label>().Any(label => label.Visible && label.Text.Contains("couldn’t be saved", StringComparison.Ordinal)));
            picker.SelectedIndex = 1;
            Equal(0, picker.SelectedIndex);
            Equal(1, dialog.ProposalCount);
        }
        picker.SelectedIndex = 1;
        PumpEvents();
        Equal(1, picker.SelectedIndex);
        Equal(0, dialog.ProposalCount);
        Equal(1, store.Reconstruction(Day, 0)!.Proposals.Single().UntickedOffsets!.Length);
        dialog.Close();

        // A reconstruction generated for an older date can coexist with its other scopes across a store reopen.
        var whole = store.Reconstruction(Day, 0)!;
        store.SaveReconstruction(Day, 1, whole with { Scope = 1 });
        using var reopened = new WorkStore(disk);
        True(reopened.Reconstruction(Day, 0) != null && reopened.Reconstruction(Day, 1) != null);
        var generated = DateOnly.FromDateTime(whole.Created.LocalDateTime);
        var cutoff = new DateTimeOffset(generated.AddDays(30).ToDateTime(new TimeOnly(12, 0), DateTimeKind.Local));
        reopened.ExpireSourcesAndReconstructions(cutoff.ToUniversalTime());
        True(reopened.Reconstruction(Day, 0) != null && reopened.Reconstruction(Day, 1) != null);
        reopened.ExpireSourcesAndReconstructions(cutoff.AddDays(1).ToUniversalTime());
        True(reopened.Reconstruction(Day, 0) == null && reopened.Reconstruction(Day, 1) == null);
    }, onDisk: true);

    private static void WithReconstructionDialog(Action<TrayApplication, WorkStore, string> test, bool onDisk = false) => OnSta(() => WithFolder(folder =>
    {
        using var app = new TrayApplication(folder, true, "reconstruct-restore", showOnStart: false, diagnostics: true);
        try
        {
            var disk = Path.Combine(folder, "reconstruction-test");
            using var store = new WorkStore(disk, inMemory: !onDisk);
            test(app, store, disk);
        }
        finally { app.ShutDownDiagnostics(); }
    }));

    private static ReconstructDayDialog OpenReconstruction(TrayApplication app, WorkStore store, FakeHost? host = null)
    {
        var dialog = new ReconstructDayDialog(app, store, host ?? new FakeHost(), Day)
        {
            ShowInTaskbar = false, StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            Location = new System.Drawing.Point(System.Windows.Forms.SystemInformation.VirtualScreen.Right + 32, 0)
        };
        dialog.Show();
        PumpEvents();
        return dialog;
    }

    private static void LoadProposal(ReconstructDayDialog dialog, int first, int last)
    {
        var answer = JsonSerializer.Serialize(new { segments = new[] { new
        {
            first_episode = first, last_episode = last, action = "assign", issue = "BC-1", title = "", description = "", meeting = -1,
            exclude_episodes = Array.Empty<int>(), confidence = "high", reason = "VS Code work"
        } } });
        dialog.LoadForTest(answer, [new JiraCandidate("BC-1", "Fix posting", "Open")]);
        dialog.ProposalGrid.ClearSelection();
        dialog.ProposalGrid.Rows[0].Selected = true;
        PumpEvents();
    }

    private static void PumpEvents()
    {
        for (var index = 0; index < 30; index++)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private sealed class FakeHost : IReconstructHost
    {
        public RoundingMode Mode => RoundingMode.Session;
        public string? ApplyAssisted(DateOnly day, Allocation[] expected, Allocation[] changes, SegmentGuard[] guards,
            Func<IReadOnlyList<Allocation>, string?> verify) => "not in this test";
        public (IReadOnlyCollection<string> Ids, string? Issue)? Opened { get; private set; }
        public void OpenInActivity(DateOnly day, IReadOnlyCollection<string> rowIds, string? issue) => Opened = (rowIds, issue);
    }

    private static IEnumerable<System.Windows.Forms.Control> Descendants(System.Windows.Forms.Control control) =>
        control.Controls.Cast<System.Windows.Forms.Control>().SelectMany(child => Descendants(child).Prepend(child));

    private static void OnSta(Action check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { check(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("The reconstruction window test did not finish.");
        if (failure != null)
            throw new InvalidOperationException(failure.Message, failure);
    }

    // ----- Helpers -----------------------------------------------------------------------------------------------

    /// <summary>About 220 rows: VS Code work with brief Teams switches, chats, RDP hosts, Jira and DevOps pages, a personal stretch and lunch.</summary>
    private static List<Allocation> RealisticDay()
    {
        var rows = new List<Allocation>();
        var second = 8 * 3600;
        var random = new Random(7);
        void Add(int seconds, string app, string title, Func<Allocation, Allocation>? shape = null)
        {
            var row = Row(second, second + seconds, app, title);
            rows.Add(shape?.Invoke(row) ?? row);
            second += seconds;
        }
        for (var block = 0; block < 44; block++)
        {
            var repo = block % 3 == 0 ? "erp" : "portal";
            Add(20 + random.Next(40), "VS Code", $"file{block}.cs - {repo}");
            Add(5, "Teams", "Chat - Jane", row => row with { Kind = SpanKind.Incidental, Status = Disposition.Excluded });
            Add(15 + random.Next(30), "VS Code", $"other{block}.cs - {repo}");
            Add(20 + random.Next(40), block % 4 == 0 ? "Remote Desktop" : "Browser", block % 4 == 0 ? "BC-PROD-0" + block % 2 : $"[BC-{100 + block % 5}] Issue - Jira");
            Add(10 + random.Next(30), "Teams", block % 2 == 0 ? "Chat - Jane" : "Chat - Ops channel");
        }
        Add(1800, "Browser", "Personal banking", row => row with { Status = Disposition.Excluded });
        Add(3000, "VS Code", "lunch", row => row with { Kind = SpanKind.Idle });
        return rows;
    }

    private static Allocation Row(int start, int end, string app, string context = "") =>
        new(Guid.NewGuid().ToString("N"), Day, Origin.AddSeconds(start), Origin.AddSeconds(end), app,
            SpanKind.Foreground, Disposition.Unassigned, new TaskLabel("", ""), Context: context);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static void WithFolder(Action<string> check)
    {
        var folder = Directory.CreateTempSubdirectory("WorklogDesk-AssistedTests-");
        try { check(folder.FullName); }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            folder.Delete(true);
        }
    }

    private static void Throws<T>(Action action, string fragment) where T : Exception
    {
        try { action(); }
        catch (T error) when (error.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase)) { return; }
        catch (Exception error) { throw new Exception($"Expected {typeof(T).Name} containing \"{fragment}\"; got {error.GetType().Name}: {error.Message}"); }
        throw new Exception($"Expected {typeof(T).Name} containing \"{fragment}\".");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}; got {actual}.");
    }

    private static void True(bool value, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!value)
            throw new Exception($"Expected condition to hold (AssistedTests line {line}).");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(respond(request));
    }
}
