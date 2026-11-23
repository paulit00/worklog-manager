using System.Diagnostics;
using System.Net.Http;
using Microsoft.Identity.Client;
using WorklogDesk.Core;

namespace WorklogDesk.Windows;

/// <summary>What the reconstruction window needs from the review window that opened it.</summary>
internal interface IReconstructHost
{
    RoundingMode Mode { get; }
    /// <summary>Applies one confirmed batch through the guarded store path. Returns an error message or null.</summary>
    string? ApplyAssisted(DateOnly day, Allocation[] expected, Allocation[] changes, SegmentGuard[] guards,
        Func<IReadOnlyList<Allocation>, string?> verify);
    /// <summary>Selects the rows in the Activity tab and opens Assign, pre-filled with the issue when given.</summary>
    void OpenInActivity(DateOnly day, IReadOnlyCollection<string> rowIds, string? issue);
}

/// <summary>
/// "Reconstruct my day": sends the whole day's activity, as ordered episodes with context, to Claude or OpenAI in one
/// previewed request, and reviews the proposed tasks one segment at a time. Nothing is written without confirmation,
/// and every write goes through the guarded store path.
/// </summary>
internal sealed class ReconstructDayDialog : Form
{
    private enum Scope { Day, Morning, Afternoon }
    private enum Outcome { Open, Accepted, Skipped }

    private sealed class Proposal(Segment segment)
    {
        public Segment Segment { get; set; } = segment;
        public string Issue { get; set; } = segment.Issue;
        public string Action { get; set; } = segment.Action;
        public string Confidence { get; set; } = segment.Confidence;
        public bool IssueChanged { get; set; }
        public HashSet<int> Unticked { get; } = segment.Exclude.ToHashSet();
        public string? Blocked { get; set; }
        public Outcome Outcome { get; set; }
        public HashSet<string> AcceptedIds { get; } = new(StringComparer.Ordinal);
    }

    private static IOutlookAuth? outlook;
    private static string outlookKey = "";

    private readonly TrayApplication app;
    private readonly WorkStore store;
    private readonly IReconstructHost host;
    private readonly DateOnly day;
    private readonly CancellationTokenSource closing = new();
    private CancellationTokenSource? request;
    private readonly ComboBox provider = new() { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "AI provider" };
    private readonly TextBox model = new() { Width = 130, MaxLength = 80, AccessibleName = "AI model" };
    private readonly ComboBox scope = new() { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Part of the day" };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(1_100, 0), Padding = new Padding(0, 0, 0, 6) };
    private readonly Label elapsed = new() { AutoSize = true, Padding = new Padding(6, 8, 6, 0) };
    private readonly Button cancel;
    private readonly DataGridView grid = Ui.Grid();
    private readonly CheckedListBox episodesList = new() { Dock = DockStyle.Fill, IntegralHeight = false, CheckOnClick = true, AccessibleName = "Episodes in the selected proposal" };
    private readonly TextBox evidence = new() { ReadOnly = true, Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, AccessibleName = "Evidence for the selected proposal" };
    private readonly TextBox jiraTerms = new() { Width = 180, MaxLength = 120, PlaceholderText = "Jira key or words", AccessibleName = "Jira search terms" };
    private readonly System.Windows.Forms.Timer clock = new() { Interval = 1_000 };
    private readonly List<Control> actions = [];
    private readonly Stopwatch watch = new();

    private List<Allocation> snapshot = [];
    private DateTimeOffset horizon;
    private Episode[] episodes = [];
    private GuardData? guard;
    private List<(DateOnly Day, List<Allocation> Rows, List<ManualEntry> Entries)> history = [];
    private HashSet<string> seenTitles = new(StringComparer.Ordinal);
    private JiraCandidate[] candidates = [];
    private readonly List<JiraCandidate> added = [];
    private Meeting[] meetings = [];
    private Meeting[] requestMeetings = [];
    private long checkedRevision = -1;
    private SentEvidence[] sent = [];
    private readonly List<Proposal> proposals = [];
    private bool busy;
    private bool dayBlocked;
    private bool updatingList;

    public ReconstructDayDialog(TrayApplication app, WorkStore store, IReconstructHost host, DateOnly day)
    {
        this.app = app;
        this.store = store;
        this.host = host;
        this.day = day;
        CompletionReminderDialog.NameForm(this, $"Reconstruct my day · {day:yyyy-MM-dd}", new Size(1_180, 700));
        ShowInTaskbar = true;
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;
        cancel = Ui.Button("Cancel request", (_, _) => request?.Cancel(), 120);
        cancel.Visible = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        layout.Controls.Add(status, 0, 0);

        provider.Items.AddRange([AiService.Claude, AiService.OpenAI]);
        provider.SelectedItem = AiService.For(app.Settings.AiProvider);
        model.Text = app.Settings.AiModel.Length > 0 && SafeModel(Service, app.Settings.AiModel) ? app.Settings.AiModel : Service.DefaultModel;
        provider.SelectedIndexChanged += (_, _) => model.Text = Service.DefaultModel;
        scope.Items.AddRange(["Whole day", "Morning", "Afternoon"]);
        scope.SelectedIndex = 0;
        var reconstruct = Button("Reconstruct day…", (_, _) => Run(Reconstruct), 140);
        Ui.Primary(reconstruct);
        layout.Controls.Add(FinishWorklogWindow.Bar(provider, model, Button("API key…", (_, _) => EditKey(), 85),
            Button("Connect Outlook…", (_, _) => Run(ConnectOutlook), 135), scope, reconstruct, elapsed, cancel), 0, 1);

        grid.AccessibleName = "Proposed tasks";
        grid.MultiSelect = true;
        foreach (var (name, header, weight) in new[] { ("time", "Time", 70), ("minutes", "Recorded", 95), ("proposal", "Proposal", 150),
                     ("effect", "Tempo effect", 110), ("confidence", "Confidence", 60), ("reason", "Reason", 230), ("state", "Status", 120) })
            grid.Columns[grid.Columns.Add(name, header)].FillWeight = weight;
        grid.SelectionChanged += (_, _) => ShowSelected();
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 700, FixedPanel = FixedPanel.None };
        split.Panel1.Controls.Add(grid);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.Controls.Add(episodesList, 0, 0);
        right.Controls.Add(evidence, 0, 1);
        right.Controls.Add(FinishWorklogWindow.Bar(Button("Include short switches", (_, _) => IncludeShort(), 160),
            Button("Open in Activity", (_, _) => OpenInActivity(), 130)), 0, 2);
        split.Panel2.Controls.Add(right);
        layout.Controls.Add(split, 0, 2);
        episodesList.ItemCheck += (_, e) =>
        {
            // Items added while filling the list raise ItemCheck too; only user clicks change a proposal.
            if (updatingList)
                return;
            var index = e.Index;
            var included = e.NewValue == CheckState.Checked;
            BeginInvoke(() => EpisodeChecked(index, included));
        };

        var accept = Button("Accept selected…", (_, _) => AcceptSelected(), 135);
        Ui.Primary(accept);
        layout.Controls.Add(FinishWorklogWindow.Bar(accept, Button("Change issue…", (_, _) => ChangeIssue(), 115),
            Button("Create Jira issue…", (_, _) => CreateIssue(), 135), Button("Split at…", (_, _) => SplitSelected(), 85),
            Button("Skip", (_, _) => Skip(), 70), jiraTerms, Button("Add candidate", (_, _) => Run(SearchJira), 110),
            Ui.Button("Close", (_, _) => Close(), 80)), 0, 3);

        clock.Tick += (_, _) => elapsed.Text = busy && watch.IsRunning ? $"{watch.Elapsed:m\\:ss}" : "";
        status.Text = "Reconstruct day sends the day's episodes, with the titles you choose, to the selected AI provider after a preview. " +
            "Nothing is assigned until you accept a proposal.";
    }

    private AiService Service => provider.SelectedItem as AiService ?? AiService.Claude;
    private TimeZoneInfo Zone => app.Zone;
    private string Local(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, Zone).ToString("HH:mm");

    private static bool SafeModel(AiService service, string name)
    {
        try { AssistedInference.CheckModel(service, name); return true; }
        catch (ArgumentException) { return false; }
    }

    private Button Button(string text, EventHandler click, int width)
    {
        var button = Ui.Button(text, click, width);
        actions.Add(button);
        return button;
    }

    private async void Run(Func<CancellationToken, Task> operation)
    {
        if (busy)
            return;
        busy = true;
        foreach (var control in actions)
            control.Enabled = false;
        request = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        cancel.Visible = true;
        watch.Restart();
        clock.Start();
        try { await operation(request.Token); }
        catch (OperationCanceledException) when (IsDisposed || closing.IsCancellationRequested) { }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { status.Text = "Cancelled. Existing proposals are still available."; }
        catch (OperationCanceledException) { status.Text = "The request timed out. Large days can take too long: choose Morning or Afternoon and reconstruct again."; }
        catch (AiTooLargeException error) { status.Text = error.Message + " Choose Morning or Afternoon and reconstruct again."; }
        catch (MsalException) { status.Text = "Outlook sign-in failed. Check the tenant's app registration and consent."; }
        catch (HttpRequestException) { status.Text = "The service could not be reached. Existing proposals are still available."; }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or
            System.IO.InvalidDataException or System.Text.Json.JsonException or KeyNotFoundException or System.IO.IOException)
        {
            if (!IsDisposed)
                status.Text = error is InvalidOperationException or ArgumentException ? error.Message : "The service returned an unreadable result.";
        }
        catch (System.Data.Common.DbException)
        {
            if (!IsDisposed)
                status.Text = "Local storage is busy. Try again in a moment.";
        }
        catch (Exception error)
        {
            // Never let an unexpected failure escape this async handler and close the window.
            if (!IsDisposed)
                status.Text = "Something went wrong: " + error.GetType().Name + ". Existing proposals are still available.";
        }
        finally
        {
            busy = false;
            watch.Stop();
            clock.Stop();
            request.Dispose();
            request = null;
            if (!IsDisposed)
            {
                cancel.Visible = false;
                elapsed.Text = "";
                foreach (var control in actions)
                    control.Enabled = true;
                RefreshGrid();
            }
        }
    }

    // ----- Loading -------------------------------------------------------------------------------------------------

    /// <summary>Reads the day, its guard data and 30 days of history on a separate read-only connection, off the UI thread.</summary>
    private async Task Load(CancellationToken cancel)
    {
        status.Text = "Reading the day and the last 30 days of history…";
        var reader = store.OpenReader();
        (List<Allocation> Rows, GuardData Guard, List<(DateOnly, List<Allocation>, List<ManualEntry>)> History) loaded;
        if (reader == null)
            loaded = (store.Rows(day), store.ReadGuardData(day), store.History(day.AddDays(-30), day.AddDays(-1)));
        else
            loaded = await Task.Run(() =>
            {
                using (reader)
                    return (reader.Rows(day), reader.ReadGuardData(day), reader.History(day.AddDays(-30), day.AddDays(-1)));
            }, cancel);
        snapshot = loaded.Rows;
        guard = loaded.Guard;
        history = loaded.History;
        horizon = snapshot.Count == 0 ? DateTimeOffset.MinValue : snapshot.Max(row => row.End);
        seenTitles = history.SelectMany(item => item.Rows).SelectMany(row => AssistedEvidence.Titles(row.Context)).ToHashSet(StringComparer.Ordinal);
        dayBlocked = AssistedGuard.DayBlocked(guard);
        var noon = Timeline.AtLocalTime(day, new TimeOnly(12, 0), Zone);
        var part = (Scope)scope.SelectedIndex;
        var rows = snapshot.Where(row => part == Scope.Day || (part == Scope.Morning ? row.Start < noon : row.Start >= noon)).ToList();
        episodes = DayEpisodes.Build(rows, Timeline.DayStart(day, Zone));
    }

    private TitlePolicy Policy(bool mailSubjects = false, bool olderBrowser = false, IReadOnlySet<string>? unticked = null) =>
        new(true, (app.Settings.AssistedTitlesOff ?? []).ToHashSet(StringComparer.Ordinal), mailSubjects, olderBrowser,
            app.Settings.MailLabelCutoff, outlook?.DisplayName, outlook?.Address, unticked ?? new HashSet<string>());

    // ----- Reconstruct ---------------------------------------------------------------------------------------------

    private async Task Reconstruct(CancellationToken cancel)
    {
        if (!app.Settings.AssistedNoticeShown)
        {
            if (MessageBox.Show(this, "Reconstruct day sends window titles, including Teams chat names (people's names), Jira " +
                    "candidate summaries and meeting subjects to the AI provider you choose. Mail subjects stay on this PC unless you " +
                    "include them for a request. You can turn title families off in the preview.\n\nContinue?", "Before the first request",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                return;
            app.SaveAssisted(settings => settings with { AssistedNoticeShown = true });
        }
        AssistedInference.CheckModel(Service, model.Text.Trim());
        // Reloading renumbers episodes. Earlier proposals are kept aside and restored unless a new plan replaces them.
        var earlier = (snapshot, horizon, episodes, guard, history, seenTitles, requestMeetings, proposals: proposals.ToList(), candidates);
        proposals.Clear();
        RefreshGrid();
        var replaced = false;
        try
        {
            replaced = await ReconstructCore(cancel);
        }
        finally
        {
            if (!replaced && !IsDisposed)
            {
                (snapshot, horizon, episodes, guard, history, seenTitles, requestMeetings, _, candidates) = earlier;
                proposals.AddRange(earlier.proposals);
                RefreshGrid();
            }
        }
    }

    /// <summary>Returns true when a new plan replaced the proposals; false leaves the earlier ones to be restored.</summary>
    private async Task<bool> ReconstructCore(CancellationToken cancel)
    {
        await Load(cancel);
        if (!episodes.Any(episode => episode.State == EpisodeState.Unassigned))
        {
            status.Text = "No unassigned recorded activity in this part of the day.";
            return false;
        }
        using var preview = new RequestPreview(this);
        if (preview.ShowDialog(this) != DialogResult.OK || preview.Body == null)
        {
            status.Text = "Request cancelled. Nothing was sent; earlier proposals are back.";
            return false;
        }
        var service = Service;
        var modelName = model.Text.Trim();
        app.SaveAssisted(settings => settings with { AiProvider = service.Name, AiModel = modelName });
        string answer;
        if (app.IsDemo)
            answer = DayReconstruction.DemoAnswer(episodes);
        else
        {
            var key = CredentialVault.Read(service.CredentialTarget);
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException($"Set your {service.Name} API key using API key…");
            status.Text = $"Waiting for {service.Name}. Large days can take a few minutes.";
            using var client = AssistedInference.SecureClient(300);
            AiResult? result = null;
            var started = Stopwatch.StartNew();
            try
            {
                result = await new AssistedInference(client).CompleteAsync(service, preview.Body, key, cancel);
                answer = result.Text;
            }
            finally
            {
                AssistedInference.LogDiagnostics(app.DataFolder, service, modelName, result, started.Elapsed, result == null ? "failed" : null);
            }
        }
        if (IsDisposed)
            return false;
        var segments = DayReconstruction.Parse(answer, episodes, preview.Candidates, meetings);
        candidates = preview.Candidates;
        requestMeetings = meetings;
        proposals.Clear();
        proposals.AddRange(segments.Select(segment => new Proposal(segment)));
        RefreshChecks();
        var invalid = segments.Count(segment => segment.Invalid != null);
        status.Text = $"{segments.Length} proposal(s)" + (invalid > 0 ? $", {invalid} invalid" : "") +
            ". Review the evidence, untick episodes that belong elsewhere, then accept." +
            (dayBlocked ? " Assisted assignment is unavailable for this date until its Tempo corrections are resolved in Worklogs." : "");
        if (grid.Rows.Count > 0)
            grid.Rows[0].Selected = true;
        return true;
    }

    /// <summary>Test seam: loads the stored day and a model answer without the preview or a network call.</summary>
    internal void LoadForTest(string answer, JiraCandidate[] found)
    {
        snapshot = store.Rows(day);
        horizon = snapshot.Count == 0 ? DateTimeOffset.MinValue : snapshot.Max(row => row.End);
        episodes = DayEpisodes.Build(snapshot, Timeline.DayStart(day, Zone));
        candidates = found;
        proposals.Clear();
        proposals.AddRange(DayReconstruction.Parse(answer, episodes, candidates, meetings).Select(segment => new Proposal(segment)));
        RefreshChecks();
    }

    /// <summary>How many times the open proposals were re-checked (tests watch for refresh loops).</summary>
    internal int Refreshes { get; private set; }
    internal CheckedListBox EpisodeList => episodesList;
    internal DataGridView ProposalGrid => grid;
    internal IReadOnlyCollection<int> Unticked(int proposal) => proposals[proposal].Unticked;

    /// <summary>Re-reads the stored rows and re-checks every open proposal: blocked state and Tempo effect.</summary>
    private void RefreshChecks()
    {
        Refreshes++;
        if (episodes.Length == 0 || proposals.Count == 0)
            return;
        guard = store.ReadGuardData(day);
        checkedRevision = store.CompletionRevision;
        dayBlocked = AssistedGuard.DayBlocked(guard);
        // Ctrl+Z in Review undoes an accepted batch; such proposals open again.
        var stored = guard.Current.ToDictionary(row => row.Id, StringComparer.Ordinal);
        foreach (var proposal in proposals.Where(item => item.Outcome == Outcome.Accepted && item.AcceptedIds.Any(id =>
                     !stored.TryGetValue(id, out var row) || row.Status != Disposition.Billable || row.Task.Issue != item.Issue)))
        {
            proposal.Outcome = Outcome.Open;
            proposal.AcceptedIds.Clear();
        }
        foreach (var proposal in proposals.Where(item => item.Outcome == Outcome.Open && item.Action == "assign" && item.Segment.Invalid == null))
        {
            var changes = Changes(proposal, withDescription: true);
            var expected = Expected(changes);
            proposal.Blocked = changes.Length == 0 ? "Nothing assignable is ticked."
                : AssistedGuard.CheckSegments(guard.Current, [Guard(proposal)]) ?? AssistedGuard.Evaluate(guard, expected, changes) switch
                {
                    null => null,
                    AssistedGuard.Protected => $"{proposal.Issue} already has approved, copied or sent time today, and adding to it would change " +
                        "that worklog (checked in both rounding modes). Use Open in Activity, which handles the correction flow.",
                    var other => other
                };
        }
        RefreshGrid();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // The user may have approved, sent or edited time in the review window meanwhile.
        if (!busy && proposals.Count > 0 && store.CompletionRevision != checkedRevision)
            RefreshChecks();
    }

    // ----- Grid and details ----------------------------------------------------------------------------------------

    private Episode[] Range(Proposal proposal) => episodes[(proposal.Segment.First - 1)..proposal.Segment.Last];

    private TaskLabel? AnchorTask(string issue)
    {
        var task = DayReconstruction.Anchor(guard?.Current ?? snapshot, issue);
        if (task != null)
            return task;
        var candidate = candidates.Concat(added).FirstOrDefault(item => item.Key == issue);
        return candidate == null ? null : new TaskLabel("", issue, candidate.Summary);
    }

    private Allocation[] Changes(Proposal proposal, bool withDescription = false)
    {
        if (proposal.Action != "assign" || !TaskLabel.ValidIssue(proposal.Issue))
            return [];
        var anchor = AnchorTask(proposal.Issue);
        var changes = DayReconstruction.Changes(proposal.Segment, episodes, proposal.Unticked, proposal.Issue,
            DayReconstruction.Anchor(guard?.Current ?? snapshot, proposal.Issue));
        // For the effect preview only; the confirmation sets the real description.
        return withDescription ? changes.Select(row => row.Task.Description.Length > 0 ? row
            : row with { Task = row.Task with { Description = anchor?.Description ?? "" } }).ToArray() : changes;
    }

    private Allocation[] Expected(IEnumerable<Allocation> changes)
    {
        var byId = snapshot.ToDictionary(row => row.Id, StringComparer.Ordinal);
        // A row deleted since the request drops out here; the store then refuses the batch as changed.
        return changes.Where(row => byId.ContainsKey(row.Id)).Select(row => byId[row.Id]).ToArray();
    }

    private SegmentGuard Guard(Proposal proposal)
    {
        var range = Range(proposal);
        return new SegmentGuard(range[0].Start, range.Max(episode => episode.End), horizon, snapshot.ToArray());
    }

    private void RefreshGrid()
    {
        if (IsDisposed)
            return;
        var selected = grid.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Index).ToHashSet();
        grid.Rows.Clear();
        foreach (var proposal in proposals)
        {
            var range = Range(proposal);
            var assignable = range.Where(episode => episode.State == EpisodeState.Unassigned && !proposal.Unticked.Contains(episode.Number))
                .Sum(episode => episode.Assignable.Sum(row => row.Milliseconds));
            var review = range.Where(episode => episode.State is EpisodeState.Uncertain or EpisodeState.UnassignedOther).Sum(episode => episode.Milliseconds);
            var label = proposal.Action switch
            {
                "assign" => $"Assign to {proposal.Issue}",
                "draft" => $"Draft · {proposal.Segment.Title}",
                _ => "Needs your decision"
            };
            var index = grid.Rows.Add($"{Local(range[0].Start)}–{Local(range.Max(item => item.End))}",
                $"{Minutes(assignable)} assignable" + (review > 0 ? $" · {Minutes(review)} needs review" : ""),
                label, EffectText(proposal), proposal.Confidence, proposal.Segment.Reason, StatusText(proposal));
            grid.Rows[index].Tag = proposal;
        }
        foreach (var episode in episodes.Where(item => item.State == EpisodeState.Unassigned &&
                     !proposals.Any(proposal => proposal.Segment.First <= item.Number && item.Number <= proposal.Segment.Last)))
            grid.Rows.Add($"{Local(episode.Start)}–{Local(episode.End)}", Minutes(episode.Milliseconds), "Not covered", "", "",
                "No proposal includes this activity. Assign it in Activity.", "");
        foreach (var index in selected.Where(index => index < grid.Rows.Count))
            grid.Rows[index].Selected = true;
        ShowSelected();
    }

    private string EffectText(Proposal proposal)
    {
        if (proposal.Action != "assign" || guard == null || proposal.Outcome != Outcome.Open)
            return "";
        var changes = Changes(proposal, withDescription: true);
        if (changes.Length == 0)
            return "";
        var effect = DayReconstruction.Effect(guard.Current, changes, host.Mode);
        var lines = effect.Issues.FirstOrDefault(item => item.Issue == proposal.Issue);
        var sessions = host.Mode == RoundingMode.Session && lines != null && lines.LinesAfter - lines.LinesBefore > 0
            ? $" · +{lines.LinesAfter - lines.LinesBefore} sessions" : "";
        return $"+{Minutes(effect.RecordedAddedMs)} → +{effect.RoundedAddedSeconds / 60} min{sessions}";
    }

    private static string StatusText(Proposal proposal) => proposal.Outcome switch
    {
        Outcome.Accepted => "Accepted",
        Outcome.Skipped => "Skipped",
        _ when proposal.Segment.Invalid != null => "Invalid: " + proposal.Segment.Invalid,
        _ when proposal.Blocked != null => "Blocked",
        _ when proposal.Confidence == "low" && !proposal.IssueChanged && proposal.Action == "assign" => "Low confidence: change the issue to accept",
        _ => ""
    };

    private static string Minutes(long milliseconds) => $"{Math.Round(milliseconds / 60_000.0):0} min";

    private Proposal[] SelectedProposals() => grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(row => row.Index)
        .Select(row => row.Tag).OfType<Proposal>().ToArray();

    private void ShowSelected()
    {
        updatingList = true;
        try
        {
            episodesList.Items.Clear();
            if (SelectedProposals() is not [var proposal])
            {
                evidence.Text = grid.SelectedRows.Count > 1 ? "Several proposals selected. Accept applies them as one batch." : "Select a proposal to see its evidence.";
                return;
            }
            foreach (var episode in Range(proposal))
            {
                var titles = episode.Rows.SelectMany(row => AssistedEvidence.Titles(row.Context)
                    .Where(title => DayEpisodes.Decide(row, title, Policy()) == TitleDecision.Sent)).Distinct().Take(2);
                var text = $"{Local(episode.Start)} {Minutes(episode.Milliseconds),6} · {Describe(episode)}" +
                    (titles.Any() ? " · " + string.Join(" · ", titles) : "");
                var index = episodesList.Items.Add(text, episode.State == EpisodeState.Unassigned && !proposal.Unticked.Contains(episode.Number));
                _ = index;
            }
            var details = new List<string>();
            if (proposal.Blocked != null)
                details.Add("Blocked: " + proposal.Blocked);
            if (proposal.Segment.Invalid != null)
                details.Add("Invalid: " + proposal.Segment.Invalid);
            details.Add("Reason: " + proposal.Segment.Reason);
            var range = Range(proposal);
            var start = range[0].Start;
            var end = range.Max(episode => episode.End);
            foreach (var meeting in meetings.Where(item => item.Start < end && start < item.End))
            {
                var idle = snapshot.Where(row => row.Kind == SpanKind.Idle && row.Status == Disposition.Unassigned &&
                    row.Start < meeting.End && meeting.Start < row.End).Sum(row => row.Milliseconds);
                details.Add($"Meeting {Local(meeting.Start)}–{Local(meeting.End)} · {meeting.Subject}" + (meeting.Unconfirmed ? " (not answered)" : "") +
                    (idle > 0 ? $" · {Minutes(idle)} without input during it; assign that in Activity" : ""));
                var recorded = snapshot.Where(row => row.Start < meeting.End && meeting.Start < row.End)
                    .Sum(row => (Math.Min(row.End.UtcTicks, meeting.End.UtcTicks) - Math.Max(row.Start.UtcTicks, meeting.Start.UtcTicks)) / TimeSpan.TicksPerMillisecond);
                var missing = (long)(meeting.End - meeting.Start).TotalMilliseconds - recorded;
                if (missing >= 60_000)
                    details.Add($"   {Minutes(missing)} of this meeting were not recorded.");
            }
            foreach (var episode in range.Where(item => item.State == EpisodeState.Uncertain))
                details.Add($"Split by an unresolved {episode.Milliseconds / 1000} s switch at {Local(episode.Start)}. Resolve it in Activity.");
            var neighbours = episodes.Where(item => item.State == EpisodeState.Assigned && item.Issue != proposal.Issue &&
                item.Number >= proposal.Segment.First - 1 && item.Number <= proposal.Segment.Last + 1 && item.Milliseconds < 120_000);
            foreach (var anchor in neighbours)
                details.Add($"Brief {anchor.Issue} anchor ({anchor.Milliseconds / 1000} s, often a Jira tab) at {Local(anchor.Start)}. Use Open in Activity to reassign it.");
            evidence.Lines = details.ToArray();
        }
        finally { updatingList = false; }
    }

    private static string Describe(Episode episode) => episode.State switch
    {
        EpisodeState.Unassigned => string.Join("/", episode.Rows.Select(row => AssistedEvidence.AppFamily(row.App)).Distinct()),
        EpisodeState.Assigned => $"assigned {episode.Issue}" + (episode.Locked ? " (approved)" : ""),
        EpisodeState.Internal => "internal",
        EpisodeState.Idle => "no input",
        EpisodeState.Excluded => "excluded",
        EpisodeState.Uncertain => "unresolved switch",
        EpisodeState.UnassignedOther => "needs review",
        _ => "other"
    };

    private void EpisodeChecked(int index, bool included)
    {
        if (updatingList || SelectedProposals() is not [var proposal] || index < 0)
            return;
        var episode = Range(proposal)[index];
        if (episode.State != EpisodeState.Unassigned)
        {
            ShowSelected();
            return;
        }
        if (included)
            proposal.Unticked.Remove(episode.Number);
        else
            proposal.Unticked.Add(episode.Number);
        RefreshChecks();
    }

    private void IncludeShort()
    {
        if (SelectedProposals() is not [var proposal])
            return;
        foreach (var episode in Range(proposal).Where(item => item.Milliseconds < 120_000))
            proposal.Unticked.Remove(episode.Number);
        RefreshChecks();
    }

    private void OpenInActivity()
    {
        if (SelectedProposals() is not [var proposal])
            return;
        // Only the ticked, assignable rows: never excluded bridges, idle, internal, anchors or unticked chats.
        var ids = Range(proposal).Where(episode => episode.State == EpisodeState.Unassigned && !proposal.Unticked.Contains(episode.Number))
            .SelectMany(episode => episode.Assignable).Select(row => row.Id).ToArray();
        if (ids.Length == 0)
        {
            status.Text = "No ticked unassigned activity in this proposal.";
            return;
        }
        host.OpenInActivity(day, ids, proposal.Action == "assign" ? proposal.Issue : null);
    }

    private void Skip()
    {
        var selected = SelectedProposals();
        foreach (var proposal in selected)
            proposal.Outcome = Outcome.Skipped;
        var next = grid.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Index).DefaultIfEmpty(-1).Max() + 1;
        RefreshGrid();
        grid.ClearSelection();
        if (next > 0 && next < grid.Rows.Count)
            grid.Rows[next].Selected = true;
    }

    // ----- Editing proposals ---------------------------------------------------------------------------------------

    private void ChangeIssue()
    {
        if (SelectedProposals() is not [var proposal] || proposal.Outcome != Outcome.Open)
        {
            status.Text = "Select one open proposal.";
            return;
        }
        var keys = candidates.Concat(added).Select(candidate => $"{candidate.Key} · {candidate.Summary}").ToArray();
        using var picker = new ComboBox { Width = 380, DropDownStyle = ComboBoxStyle.DropDown, Text = proposal.Issue };
        picker.Items.AddRange(keys);
        if (!Prompt("Change issue", "Choose a Jira candidate or type an issue key.", ("Issue", picker)))
            return;
        var key = picker.Text.Split(' ', 2)[0].Trim().ToUpperInvariant();
        if (!TaskLabel.ValidIssue(key))
        {
            status.Text = "Enter a Jira issue key such as BC-123.";
            return;
        }
        var projects = app.Settings.Jira is { } jira ? JiraSettings.ProjectKeys(jira.Projects) : [];
        if (projects.Length > 0 && !projects.Contains(key[..key.LastIndexOf('-')], StringComparer.Ordinal))
        {
            status.Text = "That issue is outside your configured Jira projects.";
            return;
        }
        if (candidates.Concat(added).All(candidate => candidate.Key != key))
            added.Add(new JiraCandidate(key, "", "Typed"));
        proposal.Issue = key;
        proposal.Action = "assign";
        proposal.IssueChanged = true;
        // The user's issue replaces the AI's proposal for this range; anchors must still match it.
        proposal.Segment = proposal.Segment with { Invalid = Revalidate(proposal) };
        RefreshChecks();
    }

    /// <summary>Re-runs the anchor and internal-time rules for a user-chosen issue.</summary>
    private string? Revalidate(Proposal proposal)
    {
        var range = Range(proposal);
        if (range.Any(episode => episode.State == EpisodeState.Assigned && episode.Issue != proposal.Issue))
            return "Spans activity already assigned to another issue.";
        if (range.Any(episode => episode.State == EpisodeState.Internal))
            return "Spans internal time.";
        return proposal.Segment.Invalid is { } old && !old.Contains("Jira candidates", StringComparison.Ordinal) &&
            !old.Contains("draft", StringComparison.OrdinalIgnoreCase) && !old.Contains("review item", StringComparison.Ordinal) ? old : null;
    }

    private void CreateIssue()
    {
        if (SelectedProposals() is not [var proposal] || proposal.Outcome != Outcome.Open)
        {
            status.Text = "Select one open proposal.";
            return;
        }
        // The AI's draft text reaches Jira only through the editable, confirmed create dialog.
        var draft = proposal.Segment.Action == "draft";
        if (app.CreateJiraIssue(this, draft ? proposal.Segment.Title : "", draft ? proposal.Segment.Description : "") is not { } issue)
            return;
        added.Add(new JiraCandidate(issue.Key, issue.Summary, issue.Status));
        proposal.Issue = issue.Key;
        proposal.Action = "assign";
        proposal.Confidence = "medium";
        proposal.IssueChanged = true;
        proposal.Segment = proposal.Segment with { Invalid = Revalidate(proposal) };
        RefreshChecks();
        status.Text = $"{issue.Key} created. The proposal now assigns to it; review and accept.";
    }

    private void SplitSelected()
    {
        if (SelectedProposals() is not [var proposal] || proposal.Outcome != Outcome.Open || proposal.Segment.First == proposal.Segment.Last)
        {
            status.Text = "Select one open proposal that covers more than one episode.";
            return;
        }
        var range = Range(proposal);
        using var picker = new ComboBox { Width = 380, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var episode in range.Skip(1))
            picker.Items.Add($"{episode.Number}: starts {Local(episode.Start)} · {Describe(episode)}");
        picker.SelectedIndex = 0;
        if (!Prompt("Split proposal", "The second part starts at this episode. Both parts keep the issue at medium confidence.", ("Split at", picker)))
            return;
        var at = range[picker.SelectedIndex + 1].Number;
        var first = proposal.Segment with { Last = at - 1, Exclude = proposal.Segment.Exclude.Where(number => number < at).ToArray() };
        var second = proposal.Segment with { First = at, Exclude = proposal.Segment.Exclude.Where(number => number >= at).ToArray() };
        var position = proposals.IndexOf(proposal);
        proposals.RemoveAt(position);
        foreach (var (part, offset) in new[] { (first, 0), (second, 1) })
        {
            var piece = new Proposal(part) { Issue = proposal.Issue, Action = proposal.Action, Confidence = proposal.Confidence, IssueChanged = proposal.IssueChanged };
            piece.Unticked.Clear();
            piece.Unticked.UnionWith(proposal.Unticked.Where(number => number >= part.First && number <= part.Last));
            piece.Segment = part with
            {
                Invalid = DayReconstruction.Check(part with { Issue = piece.Issue, Action = piece.Action, Exclude = piece.Unticked.ToArray() },
                    episodes, candidates.Concat(added).DistinctBy(item => item.Key).ToDictionary(item => item.Key, StringComparer.Ordinal), requestMeetings)
            };
            proposals.Insert(position + offset, piece);
        }
        MarkOverlaps();
        RefreshChecks();
    }

    /// <summary>After an edit, proposals that overlap another open one are invalid again.</summary>
    private void MarkOverlaps()
    {
        var open = proposals.Where(item => item.Outcome == Outcome.Open).ToArray();
        foreach (var proposal in open)
            if (open.Any(other => other != proposal && other.Segment.First <= proposal.Segment.Last && proposal.Segment.First <= other.Segment.Last))
                proposal.Segment = proposal.Segment with { Invalid = "Overlaps another proposal." };
    }

    private bool HasUnassigned(Proposal proposal) => Range(proposal).Any(episode => episode.State == EpisodeState.Unassigned && !proposal.Unticked.Contains(episode.Number));

    private async Task SearchJira(CancellationToken cancel)
    {
        var terms = jiraTerms.Text.Trim();
        if (terms.Length == 0)
            throw new ArgumentException("Enter a Jira issue key or words.");
        using var catalog = app.OpenCatalog() ?? throw new InvalidOperationException("Connect Jira first.");
        var result = await catalog.SearchAssistedAsync(terms, null, cancel);
        foreach (var issue in result.Issues.Where(issue => candidates.Concat(added).All(item => item.Key != issue.Key)))
            added.Add(new JiraCandidate(issue.Key, issue.Summary, issue.Status));
        status.Text = $"Added {result.Issues.Count} candidate(s). Use Change issue… to apply one.";
    }

    // ----- Accept --------------------------------------------------------------------------------------------------

    private void AcceptSelected()
    {
        var selected = SelectedProposals().Where(proposal => proposal.Outcome == Outcome.Open).ToArray();
        if (selected.Length == 0)
        {
            status.Text = "Select one or more open proposals.";
            return;
        }
        if (dayBlocked)
        {
            status.Text = "Assisted assignment is unavailable for this date until its Tempo corrections are resolved in Worklogs.";
            return;
        }
        if (selected.FirstOrDefault(proposal => proposal.Action != "assign" || proposal.Segment.Invalid != null) is { } wrong)
        {
            status.Text = wrong.Segment.Invalid != null ? "An invalid proposal can't be accepted: " + wrong.Segment.Invalid
                : "Only assignments can be accepted. Use Change issue… or Create Jira issue… first.";
            return;
        }
        if (selected.FirstOrDefault(proposal => proposal.Confidence == "low" && !proposal.IssueChanged) is not null)
        {
            status.Text = "A low-confidence proposal needs you to choose the issue first (Change issue…).";
            return;
        }
        RefreshChecks();
        if (selected.FirstOrDefault(proposal => proposal.Blocked != null) is { } blocked)
        {
            status.Text = "Blocked: " + blocked.Blocked;
            return;
        }
        var data = guard!;
        var batch = selected.Select(proposal => (Proposal: proposal, Changes: Changes(proposal))).ToArray();
        var changes = batch.SelectMany(item => item.Changes).DistinctBy(row => row.Id).ToArray();
        var expected = Expected(changes);
        var issues = batch.Select(item => item.Proposal.Issue).Distinct(StringComparer.Ordinal).ToArray();
        // Descriptions: approval needs one per worklog. Prefill from today's anchor, else the Jira summary; never AI text.
        var fields = issues.ToDictionary(issue => issue, issue => new TextBox
        {
            Width = 420, MaxLength = 200, Text = AnchorTask(issue)?.Description ?? "", AccessibleName = $"Description for {issue}"
        }, StringComparer.Ordinal);
        Allocation[] Final() => changes.Select(row => row.Task.Description.Length > 0 ? row
            : row with { Task = row.Task with { Description = fields[row.Task.Issue].Text.Trim() } }).ToArray();
        var effect = DayReconstruction.Effect(data.Current, Final(), host.Mode);
        var limit = Math.Max(0, app.Settings.AssistedOverheadPercent);
        var flags = new List<string>();
        foreach (var issue in effect.SmallNewLines)
            flags.Add($"{issue} gets a new worklog with under 5 recorded minutes, rounded up to 15.");
        if (effect.OverheadPercent > limit)
            flags.Add($"Rounding adds {effect.OverheadPercent}% on top of the recorded time (limit {limit}%).");
        foreach (var item in effect.Issues.Where(item => item.RecordedAddedMs <= 0 && item.RoundedAddedSeconds > 0))
            flags.Add($"{item.Issue} gains {item.RoundedAddedSeconds / 60} min in Tempo without new recorded time, because a session was split.");
        foreach (var (proposal, rows) in batch)
            if (DayReconstruction.ClientConflict(Expected(rows), proposal.Issue, DayReconstruction.Anchor(data.Current, proposal.Issue)))
                flags.Add($"Some activity for {proposal.Issue} is mapped to a different client than its existing time.");
        foreach (var issue in issues)
        {
            var parts = batch.Count(item => item.Proposal.Issue == issue);
            var foreign = batch.Where(item => item.Proposal.Issue == issue).SelectMany(item => Range(item.Proposal))
                .Any(episode => episode.State == EpisodeState.Assigned && episode.Issue != issue);
            if (parts > 1 && foreign)
                flags.Add($"{issue} is split into separate worklogs by activity on another issue.");
        }
        using var confirm = new Form();
        CompletionReminderDialog.NameForm(confirm, "Accept proposals", new Size(620, 260 + issues.Length * 34 + flags.Count * 22));
        var layout = Ui.DialogLayout();
        confirm.Controls.Add(layout);
        var lines = effect.Issues.Select(item => $"{item.Issue}: +{Minutes(item.RecordedAddedMs)} recorded → +{item.RoundedAddedSeconds / 60} min in Tempo" +
            (host.Mode == RoundingMode.Session ? $" · {item.LinesAfter} worklog(s)" : ""));
        Ui.Field(layout, "", new Label
        {
            AutoSize = true, MaximumSize = new Size(560, 0),
            Text = $"Assign {expected.Length} row(s) ({Minutes(expected.Sum(row => row.Milliseconds))} recorded) as one undoable step.\n" +
                string.Join("\n", lines) + $"\nTotal: +{Minutes(effect.RecordedAddedMs)} → +{effect.RoundedAddedSeconds / 60} min ({effect.OverheadPercent}% rounding)."
        });
        foreach (var (issue, field) in fields)
            Ui.Field(layout, issue, field);
        CheckBox? anyway = null;
        if (flags.Count > 0)
        {
            Ui.Field(layout, "", new Label { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Palette.Muted, Text = string.Join("\n", flags) });
            anyway = new CheckBox { Text = "Accept anyway", AutoSize = true };
            Ui.Field(layout, "", anyway);
        }
        var buttons = Ui.Actions(layout);
        var ok = Ui.Button("Accept", (_, _) => confirm.DialogResult = DialogResult.OK, 92);
        var no = Ui.Button("Cancel", (_, _) => confirm.DialogResult = DialogResult.Cancel, 90);
        buttons.Controls.AddRange([ok, no]);
        confirm.CancelButton = no;
        if (confirm.ShowDialog(this) != DialogResult.OK)
            return;
        if (anyway is { Checked: false })
        {
            status.Text = "Tick Accept anyway to accept flagged proposals.";
            return;
        }
        var final = Final();
        if (final.Any(row => row.Task.Description.Length == 0))
        {
            status.Text = "Add a description for each issue so the worklogs can be approved.";
            return;
        }
        var confirmed = DayReconstruction.Effect(data.Current, final, host.Mode).Fingerprint;
        var mode = host.Mode;
        string? Verify(IReadOnlyList<Allocation> current) => DayReconstruction.Effect(current, final, mode).Fingerprint == confirmed ? null
            : "Tempo totals changed while you were reviewing. Check the new totals and accept again.";
        var error = host.ApplyAssisted(day, expected, final, batch.Select(item => Guard(item.Proposal)).ToArray(), Verify);
        if (error != null)
        {
            status.Text = error;
            RefreshChecks();
            return;
        }
        foreach (var (proposal, rows) in batch)
        {
            proposal.Outcome = Outcome.Accepted;
            proposal.AcceptedIds.UnionWith(rows.Select(row => row.Id));
        }
        // Later proposals keep guarding against the request-time rows; segments never share rows, and undo restores them exactly.
        RefreshChecks();
        status.Text = $"Accepted {selected.Length} proposal(s). Approve the worklogs in Review when ready. Ctrl+Z in Review undoes this batch.";
    }

    // ----- Keys and Outlook ----------------------------------------------------------------------------------------

    private void EditKey()
    {
        if (app.IsDemo)
            return;
        using var key = new TextBox { UseSystemPasswordChar = true, MaxLength = 512 };
        var service = Service;
        if (!Prompt($"{service.Name} API key", "The key is stored in Windows Credential Manager. Leave blank to remove it.", ("API key", key)))
            return;
        try
        {
            if (key.Text.Trim().Length == 0)
                CredentialVault.Delete(service.CredentialTarget);
            else
                CredentialVault.Write(service.CredentialTarget, service.Name, key.Text.Trim());
            status.Text = $"{service.Name} key setting updated.";
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            status.Text = $"Windows Credential Manager could not update the {service.Name} key.";
        }
    }

    private async Task ConnectOutlook(CancellationToken cancel)
    {
        if (app.IsDemo)
        {
            status.Text = "Outlook is available in a personal review session.";
            return;
        }
        using var tenant = new TextBox { MaxLength = 36, Text = app.Settings.OutlookTenant };
        using var client = new TextBox { MaxLength = 36, Text = app.Settings.OutlookClient };
        if (!Prompt("Connect Outlook", "Use a tenant-approved Entra public client. Meetings use Calendars.ReadBasic; Sent Items use Mail.ReadBasic.",
                ("Tenant ID", tenant), ("Application ID", client)))
            return;
        if (!Guid.TryParse(tenant.Text, out var tenantId) || !Guid.TryParse(client.Text, out var appId))
            throw new ArgumentException("Enter the tenant and public-client application GUIDs.");
        app.SaveAssisted(settings => settings with { OutlookTenant = tenantId.ToString("D"), OutlookClient = appId.ToString("D") });
        var key = $"{tenantId:D}/{appId:D}";
        if (outlook == null || outlookKey != key)
        {
            outlook = new MsalOutlookAuth(tenantId, appId, app.DataFolder);
            outlookKey = key;
        }
        status.Text = "Signing in to Microsoft Graph for meetings…";
        using var http = AssistedInference.SecureClient(20);
        var token = await outlook.TokenAsync(MsalOutlookAuth.CalendarScope, Handle, cancel);
        var result = await OutlookEvidence.CalendarAsync(http, token, day, Zone, cancel);
        meetings = result.Items;
        var message = $"Loaded {meetings.Length} meeting(s)" + (result.Incomplete ? " (incomplete)" : "") + ".";
        if (MessageBox.Show(this, "Also read today's Sent Items subjects? They stay on this PC unless you include mail subjects in a request.",
                "Sent Items", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
        {
            try
            {
                var mailToken = await outlook.TokenAsync(MsalOutlookAuth.MailScope, Handle, cancel);
                var items = await OutlookEvidence.ReadAsync(http, mailToken, day, Zone, cancel);
                sent = items.Items;
                message += $" Loaded {sent.Length} Sent Items.";
            }
            catch (Exception error) when (error is MsalException or InvalidOperationException or HttpRequestException)
            {
                message += " Sent Items are unavailable (Mail.ReadBasic consent missing, or the network failed); meetings still work.";
            }
        }
        status.Text = message;
        if (proposals.Count > 0)
            ShowSelected();
    }

    private bool Prompt(string title, string explanation, params (string Label, Control Field)[] fields)
    {
        using var dialog = new Form();
        CompletionReminderDialog.NameForm(dialog, title, new Size(600, 150 + fields.Length * 34));
        var layout = Ui.DialogLayout();
        dialog.Controls.Add(layout);
        Ui.Field(layout, "", new Label { Text = explanation, AutoSize = true, MaximumSize = new Size(420, 0) });
        foreach (var (label, field) in fields)
            Ui.Field(layout, label, field);
        var bar = Ui.Actions(layout);
        var ok = Ui.Button("Continue", (_, _) => dialog.DialogResult = DialogResult.OK, 92);
        var no = Ui.Button("Cancel", (_, _) => dialog.DialogResult = DialogResult.Cancel, 90);
        bar.Controls.AddRange([ok, no]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = no;
        var accepted = dialog.ShowDialog(this) == DialogResult.OK;
        foreach (var (_, field) in fields)
            layout.Controls.Remove(field);
        return accepted;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        closing.Cancel();
        closing.Dispose();
        clock.Dispose();
        base.OnFormClosed(e);
    }

    // ----- Request preview -----------------------------------------------------------------------------------------

    /// <summary>
    /// The summarized preview: what will be sent, per family and per title, with held-back counts. The exact request
    /// is built after the choices and the Jira lookup, and shown on demand; it is what Send transmits.
    /// </summary>
    private sealed class RequestPreview : Form
    {
        private readonly ReconstructDayDialog owner;
        private readonly TreeView tree = new() { Dock = DockStyle.Fill, CheckBoxes = true, AccessibleName = "Titles to send" };
        private readonly Label header = new() { AutoSize = true, MaximumSize = new Size(820, 0), Padding = new Padding(0, 0, 0, 6) };
        private readonly CheckBox mail = new() { Text = "Include mail subjects (this request only)", AutoSize = true };
        private readonly CheckBox older = new() { Text = "Include older browser titles (this request only)", AutoSize = true };
        private readonly Button send;
        private readonly Button prepare;
        private readonly HashSet<string> unticked = new(StringComparer.Ordinal);
        private readonly HashSet<string> familiesOff;
        private bool filling;
        private int generation;
        private string? warning;

        public string? Body { get; private set; }
        public JiraCandidate[] Candidates { get; private set; } = [];

        public RequestPreview(ReconstructDayDialog owner)
        {
            this.owner = owner;
            familiesOff = (owner.app.Settings.AssistedTitlesOff ?? []).ToHashSet(StringComparer.Ordinal);
            CompletionReminderDialog.NameForm(this, $"Preview request to {owner.Service.Name}", new Size(860, 600));
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);
            layout.Controls.Add(header, 0, 0);
            layout.Controls.Add(FinishWorklogWindow.Bar(mail, older, Ui.Button("Hide new titles", (_, _) => HideNew(), 125)), 0, 1);
            layout.Controls.Add(tree, 0, 2);
            prepare = Ui.Button("Prepare request", async (_, _) => await Prepare(), 125);
            send = Ui.Button($"Send to {owner.Service.Name}", (_, _) => DialogResult = DialogResult.OK, 130);
            Ui.Primary(send);
            send.Enabled = false;
            layout.Controls.Add(FinishWorklogWindow.Bar(prepare, Ui.Button("Show exact request", (_, _) => ShowExact(), 135), send,
                Ui.Button("Cancel", (_, _) => DialogResult = DialogResult.Cancel, 85)), 0, 3);
            mail.CheckedChanged += (_, _) => Changed();
            older.CheckedChanged += (_, _) => Changed();
            tree.BeforeCheck += (_, e) => e.Cancel = !filling && e.Node?.Tag is not (string or TitleUse);
            tree.AfterCheck += (_, e) => { if (!filling) Toggle(e.Node!); };
            Fill();
        }

        private TitlePolicy Policy => owner.Policy(mail.Checked, older.Checked, unticked) with { FamiliesOff = familiesOff };

        private void Fill()
        {
            filling = true;
            tree.BeginUpdate();
            try
            {
                tree.Nodes.Clear();
                var uses = DayReconstruction.Titles(owner.episodes, Policy, owner.seenTitles);
                foreach (var family in uses.GroupBy(use => use.Family).OrderByDescending(group => group.Key == "Teams")
                             .ThenByDescending(group => group.Sum(use => use.Milliseconds)))
                {
                    var sentCount = family.Count(use => Sent(use));
                    var node = new TreeNode($"{family.Key} · {sentCount} of {family.Count()} titles sent" +
                        (family.Key == "Teams" ? " (chat names are people's names)" : "") +
                        (family.Key == "Outlook" ? " (mail: only with Include mail subjects)" : "")) { Tag = family.Key, Checked = !familiesOff.Contains(family.Key) };
                    foreach (var use in family)
                        node.Nodes.Add(new TreeNode($"{Math.Round(use.Milliseconds / 60_000.0),3} min  {use.Title}{(use.New ? "  [new]" : "")}{Why(use)}")
                            { Tag = use, Checked = Sent(use) });
                    tree.Nodes.Add(node);
                }
                var older = uses.Count(use => use.Decision == TitleDecision.OlderBrowser);
                var mailLike = uses.Count(use => use.Decision == TitleDecision.Mail);
                var teams = uses.Where(use => use.Family == "Teams").Count(Sent);
                var sent = uses.Count(Sent);
                header.Text = $"Teams chats {teams} (people's names) · {sent} titles sent · Browser titles not sent: {older} " +
                    "(recorded before mail detection; connect Outlook or include them for this request) · possible mail titles not sent: " +
                    $"{mailLike} · {owner.meetings.Length} meetings · " +
                    (Body == null ? "Prepare the request to fetch Jira candidates and history hints." : $"{Candidates.Length} Jira candidates · ready to send.");
                if (Body != null)
                {
                    var jira = new TreeNode($"Jira candidates · {Candidates.Length} (key, summary, project, parent)");
                    foreach (var candidate in Candidates)
                        jira.Nodes.Add($"{candidate.Key} · {candidate.Summary}{(candidate.Locked ? " · may be blocked" : "")}");
                    tree.Nodes.Add(jira);
                }
            }
            finally
            {
                tree.EndUpdate();
                filling = false;
            }
        }

        private bool Sent(TitleUse use) => use.Decision == TitleDecision.Sent && !unticked.Contains(use.Title) && !familiesOff.Contains(use.Family);

        private static string Why(TitleUse use) => use.Decision switch
        {
            TitleDecision.Mail => "  (mail, not sent)",
            TitleDecision.OlderBrowser => "  (older browser title, not sent)",
            TitleDecision.FamilyOff => "  (family off)",
            _ => ""
        };

        private void Toggle(TreeNode node)
        {
            switch (node.Tag)
            {
                case string family:
                    if (node.Checked)
                        familiesOff.Remove(family);
                    else
                        familiesOff.Add(family);
                    owner.app.SaveAssisted(settings => settings with { AssistedTitlesOff = familiesOff.Order().ToArray() });
                    break;
                case TitleUse use:
                    if (node.Checked)
                        unticked.Remove(use.Title);
                    else
                        unticked.Add(use.Title);
                    break;
            }
            Changed();
        }

        private void HideNew()
        {
            foreach (var use in DayReconstruction.Titles(owner.episodes, Policy, owner.seenTitles).Where(use => use.New))
                unticked.Add(use.Title);
            Changed();
        }

        private void Changed()
        {
            generation++;
            Body = null;
            send.Enabled = false;
            BeginInvoke(Fill);
        }

        /// <summary>Fetches Jira candidates from titles that will be sent, computes hints, then builds the exact body.</summary>
        private async Task Prepare()
        {
            prepare.Enabled = false;
            var started = generation;
            foreach (var input in new Control[] { tree, mail, older })
                input.Enabled = false;
            try
            {
                var policy = Policy;
                var anchors = owner.episodes.Where(episode => episode.State == EpisodeState.Assigned).Select(episode => episode.Issue);
                var projects = owner.app.Settings.Jira is { } jira ? JiraSettings.ProjectKeys(jira.Projects).ToHashSet(StringComparer.Ordinal) : [];
                var keys = DayReconstruction.KeysInSentTitles(owner.episodes, policy, projects).Concat(anchors).Distinct(StringComparer.Ordinal).ToArray();
                JiraCandidate[] found = [];
                warning = null;
                if (owner.app.IsDemo)
                    found = keys.Select(key => new JiraCandidate(key, owner.AnchorTask(key)?.Description ?? "", "Demo")).ToArray();
                else
                {
                    using var catalog = owner.app.OpenCatalog();
                    if (catalog == null)
                        warning = "Jira isn't connected, so only today's assigned issues are candidates.";
                    else if (projects.Count == 0)
                        warning = "Add your Jira project keys in Settings to get automatic candidates.";
                    else
                    {
                        var result = await catalog.CandidatesAsync(keys, owner.closing.Token);
                        found = result.Candidates;
                        warning = result.Warning;
                    }
                }
                // Today's anchors are always candidates, so work that continues an issue can be proposed. Their
                // stored description may come from a window title, so it isn't sent as a summary.
                found = found.Concat(anchors.Where(key => found.All(item => item.Key != key)).Distinct()
                        .Select(key => new JiraCandidate(key, "", "Assigned today")))
                    .Concat(owner.added).DistinctBy(item => item.Key).Take(70).ToArray();
                var data = owner.guard!;
                var probe = data.Current.FirstOrDefault(AssistedEvidence.CanAssign);
                found = await Task.Run(() => found.Select(candidate => candidate with
                {
                    Client = DayReconstruction.Anchor(data.Current, candidate.Key)?.Client ?? "",
                    Locked = probe != null && AssistedGuard.Evaluate(data, [probe],
                        [probe with { Status = Disposition.Billable, Task = new TaskLabel("", candidate.Key, "") }]) != null
                }).ToArray());
                // Choices changed while Jira answered: this result was built from the old policy and is discarded.
                if (started != generation)
                    return;
                var hints = DayReconstruction.Hints(owner.history, DayReconstruction.SentTokens(owner.episodes, policy), policy);
                var facts = DayReconstruction.Facts(owner.day, owner.Zone, owner.episodes, policy, owner.meetings, owner.sent, found, hints);
                Body = DayReconstruction.BuildRequest(owner.Service, owner.model.Text.Trim(), facts);
                Candidates = found;
                Fill();
                header.Text += $" · {hints.Length} history hints" + (hints.Length == 0 ? " (hints build up as you approve work)" : "") +
                    (warning == null ? "" : " · " + warning);
                send.Enabled = true;
            }
            catch (Exception error)
            {
                // Prepare runs from a button's async handler; nothing may escape it.
                header.Text = "Could not prepare the request: " + (error is InvalidOperationException or ArgumentException
                    ? error.Message : error is System.Data.Common.DbException ? "local storage is busy." : "the reply couldn't be read.");
            }
            finally
            {
                prepare.Enabled = true;
                foreach (var input in new Control[] { tree, mail, older })
                    input.Enabled = true;
            }
        }

        private void ShowExact()
        {
            if (Body == null)
            {
                header.Text = "Prepare the request first; the exact body is built from your choices.";
                return;
            }
            using var view = new Form();
            CompletionReminderDialog.NameForm(view, "Exact request", new Size(820, 560));
            view.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                Text = Body, Dock = DockStyle.Fill, AccessibleName = "Exact request body" });
            view.ShowDialog(this);
        }
    }
}
