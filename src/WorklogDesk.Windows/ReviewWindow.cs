using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal sealed partial class ReviewWindow : Form
{
    private readonly TrayApplication app;
    private readonly WorkStore store;
    private readonly bool openWithoutActivation;
    private readonly DateTimePicker date = new()
    {
        Width = 210, Format = DateTimePickerFormat.Custom, CustomFormat = "dddd, dd MMM yyyy",
        AccessibleName = "Workday date"
    };
    private readonly Label status = new() { AutoSize = true };
    private readonly Label actual = SummaryLabel();
    private readonly Label attention = SummaryLabel();
    private readonly Label proposed = SummaryLabel();
    private readonly Label message = new() { AutoSize = true, ForeColor = Color.FromArgb(62, 75, 93) };
    private readonly Label roundingComparison = new() { AutoSize = true, ForeColor = Color.FromArgb(62, 75, 93) };
    private readonly DataGridView activity = Ui.Grid();
    private readonly DataGridView worklogs = Ui.Grid();
    private readonly DataGridView requests = Ui.Grid();
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly ComboBox rounding = new() { Width = 155, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox needsReview = new() { Text = "Needs review only", AutoSize = true };
    private readonly CheckBox groupActivity = new() { Text = "Group adjoining", Checked = true, AutoSize = true };
    private readonly Button tracking;
    private readonly Button timer;
    private readonly Button jira;
    private readonly Button pinButton;
    private List<Allocation> rows = [];
    private Allocation[][] visibleActivity = [];
    private sealed record WorklogGridRow(object Item, string[] Cells);
    private WorklogGridRow[] visibleWorklogs = [];
    private IReadOnlyList<Worklog> preview = [];
    private IReadOnlyList<ManualEntry> entered = [];
    private HashSet<string> approvedIds = [];
    private HashSet<string> staleApprovalIds = [];
    private Dictionary<string, string> approvalKeys = [];
    private Dictionary<Worklog, ManualCoverage> coverages = [];
    private ExportReceipt[] handoffs = [];
    private HashSet<string> correctionCopies = [];
    private HashSet<string> handoffKeys = [];
    private RoundingMode Mode => rounding.SelectedIndex == 1 ? RoundingMode.Session : RoundingMode.IssueDay;
    private DateOnly Day => DateOnly.FromDateTime(date.Value);

    protected override bool ShowWithoutActivation => openWithoutActivation;

    public ReviewWindow(TrayApplication app, WorkStore store, bool openWithoutActivation = false)
    {
        this.app = app;
        this.store = store;
        this.openWithoutActivation = openWithoutActivation;
        Text = "Worklog Desk";
        Size = new Size(1180, 750);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        BackColor = Color.FromArgb(246, 248, 251);

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(23), ColumnCount = 1, RowCount = 6 };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < 6; index++)
            shell.RowStyles.Add(new RowStyle(index == 4 ? SizeType.Percent : SizeType.AutoSize, index == 4 ? 100 : 0));
        Controls.Add(shell);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 2, RowCount = 2, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new Label
        {
            Text = "Worklog Desk", AutoSize = true, Font = new Font("Segoe UI", 23, FontStyle.Bold),
            ForeColor = Color.FromArgb(19, 37, 61)
        };
        status.Anchor = AnchorStyles.Left;
        status.Margin = new Padding(4, 0, 0, 15);
        header.Controls.Add(heading, 0, 0);
        header.Controls.Add(status, 0, 1);
        var topActions = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Top | AnchorStyles.Right, WrapContents = false
        };
        tracking = Ui.Button("Start foreground", (_, _) => app.ToggleCapture(), 128);
        tracking.Enabled = !app.IsDemo;
        timer = Ui.Button("Start timer", (_, _) => app.ToggleManual(), 95);
        jira = Ui.Button("Connect Jira", (_, _) => app.ConnectJira(), 107);
        jira.Enabled = !app.IsDemo;
        pinButton = Ui.Button("Pin task", (_, _) => app.ResumeOrEditPin(), 104);
        topActions.Controls.AddRange([tracking, timer, pinButton,
            jira, Ui.Button("Settings", (_, _) => app.EditSettings(), 80)]);
        UpdateConnections();
        header.Controls.Add(topActions, 1, 0);
        shell.Controls.Add(header, 0, 0);

        var navigation = Flow();
        var previous = Ui.Button("‹", (_, _) => ChangeDay(Day.AddDays(-1)), 36);
        previous.AccessibleName = "Previous day";
        var next = Ui.Button("›", (_, _) => ChangeDay(Day.AddDays(1)), 36);
        next.AccessibleName = "Next day";
        date.Value = app.Today.ToDateTime(TimeOnly.MinValue);
        date.Margin = new Padding(4, 8, 4, 4);
        date.ValueChanged += (_, _) => Reload();
        navigation.Controls.AddRange([previous, date, next,
            Ui.Button("Today", (_, _) => ChangeDay(app.Today), 69),
            Ui.Button("Oldest unassigned", (_, _) => GoToUnreviewed(), 142)]);
        shell.Controls.Add(navigation, 0, 1);

        var totals = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, Margin = new Padding(2, 8, 2, 8) };
        for (var index = 0; index < 3; index++)
            totals.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        totals.Controls.Add(actual, 0, 0);
        totals.Controls.Add(attention, 1, 0);
        totals.Controls.Add(proposed, 2, 0);
        shell.Controls.Add(totals, 0, 2);

        var commands = Flow();
        var edit = Ui.Button("Edit row", (_, _) => EditSelected(), 100);
        var assign = Ui.Button("Assign selected", (_, _) => AssignSelected(), 125);
        var find = Ui.Button("Find Jira", (_, _) => FindIssue(), 83);
        var approve = Ui.Button("Approve", (_, _) => ApproveSelected(), 89);
        var add = Ui.Button("Add time", (_, _) => AddTime(), 90);
        var split = Ui.Button("Split row", (_, _) => SplitSelected(), 84);
        var mark = Ui.Button("Mark entered in Tempo", (_, _) => MarkEntered(), 172);
        var approveLine = Ui.Button("Approve line", (_, _) => ApproveWorklogs(), 104);
        approveLine.Visible = false;
        var classify = Ui.Button("Classify…", (_, _) => ShowClassifier(), 92);
        mark.Visible = false;
        commands.Controls.AddRange([edit, assign, find, classify, approve, add, split, approveLine, mark,
            Ui.Button("Capture request", (_, _) => app.QuickRequest(), 125)]);
        needsReview.Margin = new Padding(8, 9, 2, 0);
        needsReview.CheckedChanged += (_, _) => LoadActivity();
        commands.Controls.Add(needsReview);
        groupActivity.Margin = new Padding(8, 9, 2, 0);
        groupActivity.CheckedChanged += (_, _) => LoadActivity();
        commands.Controls.Add(groupActivity);
        tabs.SelectedIndexChanged += (_, _) =>
        {
            var onActivity = tabs.SelectedIndex == 0;
            foreach (var control in new Control[] { assign, classify, approve, add, split, needsReview, groupActivity })
                control.Visible = onActivity;
            edit.Visible = find.Visible = tabs.SelectedIndex != 1;
            edit.Text = onActivity ? "Edit row" : "Edit request";
            mark.Visible = approveLine.Visible = tabs.SelectedIndex == 1;
        };
        shell.Controls.Add(commands, 0, 3);

        activity.AccessibleName = "Daily activity";
        AddColumns(activity, ("Time", 120), ("Actual", 66), ("Source", 105), ("Client", 95),
            ("Jira issue", 85), ("Description", 155), ("State", 100));
        activity.VirtualMode = true;
        foreach (DataGridViewColumn activityColumn in activity.Columns)
            activityColumn.SortMode = DataGridViewColumnSortMode.NotSortable;
        activity.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.RowIndex < visibleActivity.Length)
                e.Value = ActivityCell(visibleActivity[e.RowIndex], e.ColumnIndex);
        };
        activity.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditSelected(); };
        activity.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.F2)
            {
                e.SuppressKeyPress = true;
                EditSelected();
            }
        };
        activity.CellFormatting += (_, e) =>
        {
            if (!SystemInformation.HighContrast && e.ColumnIndex == 6 &&
                e.Value is "Needs assignment" or "Check timing" or "Review idle gap")
            {
                e.CellStyle!.BackColor = Color.FromArgb(255, 243, 215);
                e.CellStyle.ForeColor = Color.FromArgb(85, 49, 11);
            }
        };
        var activityMenu = new ContextMenuStrip();
        activityMenu.Items.Add("Edit activity", null, (_, _) => EditSelected());
        activityMenu.Items.Add("Merge adjacent rows", null, (_, _) => MergeSelected());
        activityMenu.Items.Add("Mark selected internal", null, (_, _) => ClassifySelected(Disposition.Internal));
        activityMenu.Items.Add("Exclude selected", null, (_, _) => ClassifySelected(Disposition.Excluded));
        activity.ContextMenuStrip = activityMenu;
        AddTab("Activity", activity);

        worklogs.AccessibleName = "Proposed Tempo worklogs";
        AddColumns(worklogs, ("Issue", 80), ("Client", 108), ("Actual", 70), ("To log", 70),
            ("Description", 190), ("State", 100));
        worklogs.Columns.Insert(2, new DataGridViewTextBoxColumn { HeaderText = "Start", FillWeight = 70, MinimumWidth = 65 });
        worklogs.VirtualMode = true;
        foreach (DataGridViewColumn worklogColumn in worklogs.Columns)
            worklogColumn.SortMode = DataGridViewColumnSortMode.NotSortable;
        worklogs.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.RowIndex < visibleWorklogs.Length)
                e.Value = visibleWorklogs[e.RowIndex].Cells[e.ColumnIndex];
        };
        // Every clipboard copy must go through CopyWorklog so the handoff is recorded and the duplicate warning works.
        worklogs.ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable;
        worklogs.KeyDown += (_, e) =>
        {
            Action? command = e.KeyData switch
            {
                Keys.Enter => ApproveWorklogs,
                Keys.Control | Keys.C => CopyWorklog,
                Keys.Control | Keys.M => MarkEntered,
                Keys.Control | Keys.D1 => () => CopyField("issue"),
                Keys.Control | Keys.D2 => () => CopyField("duration"),
                Keys.Control | Keys.D3 => () => CopyField("description"),
                Keys.Control | Keys.D4 => () => CopyField("start"),
                _ => null
            };
            if (command == null)
                return;
            e.Handled = e.SuppressKeyPress = true;
            command();
        };
        var worklogMenu = new ContextMenuStrip();
        worklogMenu.Items.Add(new ToolStripMenuItem("Approve selected lines", null, (_, _) => ApproveWorklogs())
            { ShortcutKeyDisplayString = "Enter" });
        worklogMenu.Items.Add(new ToolStripMenuItem("Copy for Tempo", null, (_, _) => CopyWorklog())
            { ShortcutKeyDisplayString = "Ctrl+C" });
        worklogMenu.Items.Add(new ToolStripMenuItem("Mark entered in Tempo", null, (_, _) => MarkEntered())
            { ShortcutKeyDisplayString = "Ctrl+M" });
        var fields = new ToolStripMenuItem("Copy one field");
        fields.DropDownItems.Add(new ToolStripMenuItem("Issue key", null, (_, _) => CopyField("issue")) { ShortcutKeyDisplayString = "Ctrl+1" });
        fields.DropDownItems.Add(new ToolStripMenuItem("Duration", null, (_, _) => CopyField("duration")) { ShortcutKeyDisplayString = "Ctrl+2" });
        fields.DropDownItems.Add(new ToolStripMenuItem("Description", null, (_, _) => CopyField("description")) { ShortcutKeyDisplayString = "Ctrl+3" });
        fields.DropDownItems.Add(new ToolStripMenuItem("Start time", null, (_, _) => CopyField("start")) { ShortcutKeyDisplayString = "Ctrl+4" });
        worklogMenu.Items.Add(fields);
        worklogMenu.Items.Add(new ToolStripSeparator());
        worklogMenu.Items.Add("Manage local Tempo entries", null, (_, _) => ManageEntries());
        worklogs.ContextMenuStrip = worklogMenu;
        AddTab("Worklogs", worklogs);

        requests.AccessibleName = "Saved request drafts";
        AddColumns(requests, ("Description", 200), ("Client", 110), ("Jira issue", 85), ("Evidence", 100));
        requests.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditRequest(); };
        requests.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                EditRequest();
            }
        };
        var requestMenu = new ContextMenuStrip();
        requestMenu.Items.Add("Find or link existing issue", null, (_, _) => FindIssue());
        requestMenu.Items.Add("Open Jira create form", null, (_, _) => OpenJiraForm());
        requests.ContextMenuStrip = requestMenu;
        AddTab("Requests", requests);
        shell.Controls.Add(tabs, 0, 4);

        var bottom = Flow();
        rounding.AccessibleName = "Rounding preview";
        rounding.Items.AddRange(["Once per issue/day", "Each work session"]);
        rounding.SelectedIndex = (int)app.Settings.Rounding;
        rounding.Margin = new Padding(4, 8, 6, 2);
        rounding.SelectedIndexChanged += (_, _) =>
        {
            app.SaveRounding(Mode);
            LoadWorklogs();
            LoadActivity();
        };
        bottom.Controls.Add(new Label { Text = "Rounding preview", AutoSize = true, Margin = new Padding(2, 11, 6, 0) });
        bottom.Controls.Add(rounding);
        bottom.Controls.Add(Ui.Button("Export approved CSV", (_, _) => Export(), 151));
        var copy = Ui.Button("Copy worklog", (_, _) => CopyWorklog(), 109);
        copy.Visible = false;
        tabs.SelectedIndexChanged += (_, _) => copy.Visible = tabs.SelectedIndex == 1;
        bottom.Controls.Add(copy);
        roundingComparison.Margin = new Padding(8, 11, 0, 0);
        bottom.Controls.Add(roundingComparison);
        message.Margin = new Padding(8, 11, 0, 0);
        bottom.Controls.Add(message);
        shell.Controls.Add(bottom, 0, 5);

    }

    public void SetStatus(string state, TaskLabel? pin, bool hotkeys, bool interrupted = false, bool pinPaused = false)
    {
        var pinText = pin == null ? "" : $"   ·   Pinned {pin.Issue} {pin.Client}".TrimEnd();
        status.Text = $"{state}{pinText}" + (pinPaused ? "   ·   Pin paused. Use Pin task to confirm." : "") +
            (hotkeys ? "" : "   ·   Some global shortcuts unavailable");
        tracking.Text = app.Settings.Recording ? "Pause foreground" : "Start foreground";
        timer.Text = app.RunningManual ? "Stop timer" : interrupted ? "Resume timer" : "Start timer";
        pinButton.Text = pinPaused ? "Confirm pin" : "Pin task";
    }

    public void UpdateConnections() => jira.Text = app.Settings.Jira == null ? "Connect Jira" : "Jira connected";

    public void Reload()
    {
        rows = store.Rows(Day);
        entered = store.ManualEntries(Day);
        LoadWorklogs();
        LoadActivity();
        var selected = requests.CurrentRow?.Tag as RequestDraft;
        requests.Rows.Clear();
        foreach (var request in store.Requests(Day))
        {
            var index = requests.Rows.Add(request.Task.Description, request.Task.Client, request.Task.Issue,
                request.Task.Link.Length == 0 ? "—" : "Link saved");
            requests.Rows[index].Tag = request;
            if (selected?.Id == request.Id)
                requests.CurrentCell = requests.Rows[index].Cells[0];
        }
    }

    public bool IntersectsDay(WorkSpan span) => span.Start < Timeline.DayStart(Day.AddDays(1), app.Zone) &&
        span.End > Timeline.DayStart(Day, app.Zone);

    internal void OpenWorklogPreview(RoundingMode mode)
    {
        rounding.SelectedIndex = (int)mode;
        tabs.SelectedIndex = 1;
    }

    public void SelectLastRemote()
    {
        tabs.SelectedIndex = 0;
        needsReview.Checked = false;
        var remoteIndex = Array.FindLastIndex(visibleActivity, group => group.Any(allocation =>
            allocation.App.StartsWith("Remote Desktop", StringComparison.Ordinal)));
        if (remoteIndex < 0)
        {
            Notify("No Remote Desktop span on this date.");
            return;
        }
        activity.ClearSelection();
        var remote = activity.Rows[remoteIndex];
        activity.CurrentCell = remote.Cells[0];
        remote.Selected = true;
        activity.Focus();
    }

    private void LoadActivity()
    {
        var selected = Selected().Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var priorIndex = activity.CurrentRow?.Index ?? -1;
        var cursor = priorIndex >= 0 && priorIndex < visibleActivity.Length ? visibleActivity[priorIndex][0].Id : null;
        var column = activity.CurrentCell?.ColumnIndex ?? 0;
        var scroll = activity.FirstDisplayedScrollingRowIndex;
        var visible = rows.Where(row => !needsReview.Checked ||
            (!approvedIds.Contains(row.Id) && row.Status is not (Disposition.Internal or Disposition.Excluded)));
        visibleActivity = ActivityGroups(visible).ToArray();
        activity.RowCount = visibleActivity.Length;
        activity.ClearSelection();
        if (cursor != null)
        {
            var index = Array.FindIndex(visibleActivity, group => group.Any(item => item.Id == cursor));
            if (index >= 0)
                activity.CurrentCell = activity.Rows[index].Cells[column];
        }
        if (selected.Count > 0)
            for (var index = 0; index < visibleActivity.Length; index++)
                if (visibleActivity[index].Any(row => selected.Contains(row.Id)))
                    activity.Rows[index].Selected = true;
        if (scroll >= 0 && scroll < activity.RowCount)
            activity.FirstDisplayedScrollingRowIndex = scroll;
        activity.Invalidate();
    }

    private string ActivityCell(Allocation[] group, int column)
    {
        var row = group[0];
        return column switch
        {
            0 => Range(row with { End = group[^1].End }),
            1 => Duration(group.Sum(item => item.Milliseconds)),
            2 => group.Length == 1 ? row.App : $"{row.App} ({group.Length})",
            3 => row.Task.Client,
            4 => row.Task.Issue,
            5 => row.Task.Description,
            6 => State(row),
            _ => ""
        };
    }

    private IEnumerable<Allocation[]> ActivityGroups(IEnumerable<Allocation> source)
    {
        var group = new List<Allocation>();
        foreach (var row in source)
        {
            if (group.Count > 0)
            {
                var last = group[^1];
                if (!groupActivity.Checked || last.End != row.Start || last.App != row.App ||
                    last.Task != row.Task || last.Status != row.Status || last.Kind != row.Kind ||
                    approvedIds.Contains(last.Id) != approvedIds.Contains(row.Id))
                {
                    yield return group.ToArray();
                    group.Clear();
                }
            }
            group.Add(row);
        }
        if (group.Count > 0)
            yield return group.ToArray();
    }

    private void LoadWorklogs()
    {
        var daily = Timeline.Preview(rows, RoundingMode.IssueDay);
        var bySession = Timeline.Preview(rows, RoundingMode.Session);
        preview = Mode == RoundingMode.IssueDay ? daily : bySession;
        var issueDay = daily.Sum(row => row.ProposedSeconds);
        var sessions = bySession.Sum(row => row.ProposedSeconds);
        actual.Text = $"Assigned   {Duration(rows.Where(row => row.Status is Disposition.Billable or Disposition.Internal).Sum(row => row.Milliseconds))}" +
            $" / {Duration(app.Settings.WorkdayMinutes * 60_000L)}";
        var idle = rows.Where(row => row.Status == Disposition.Unassigned && row.Kind == SpanKind.Idle).Sum(row => row.Milliseconds);
        var activeUnassigned = rows.Where(row => row.Status == Disposition.Unassigned && row.Kind != SpanKind.Idle).Sum(row => row.Milliseconds);
        attention.Text = $"Unassigned   {Duration(activeUnassigned)}" + (idle == 0 ? "" : $"   ·   Idle {Duration(idle)}");
        proposed.Text = $"Proposed   {Duration(preview.Sum(row => row.ProposedSeconds) * 1000L)}";
        proposed.AccessibleDescription = $"Issue/day {Duration(issueDay * 1000L)}. Sessions {Duration(sessions * 1000L)}.";
        approvalKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in preview)
        {
            var key = line.ApprovalKey(Mode);
            foreach (var id in line.AllocationIds)
                approvalKeys.Add(id, key);
        }
        approvedIds = rows.Where(row => approvalKeys.TryGetValue(row.Id, out var key) && row.IsApprovedFor(Mode, key))
            .Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        staleApprovalIds = rows.Where(row => row.Approved && !approvedIds.Contains(row.Id))
            .Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        coverages = preview.ToDictionary(line => line, line => ManualLedger.Check(line, Mode, preview, entered));
        handoffs = store.Exports(Day);
        correctionCopies = store.CorrectionCopies(Day).ToHashSet(StringComparer.Ordinal);
        handoffKeys = store.HandoffKeys(Day).ToHashSet(StringComparer.Ordinal);
        // Keep the whole batch selected across reloads so approve, copy and mark entered can run in sequence.
        var current = (WorklogAt(worklogs.CurrentRow?.Index ?? -1) as Worklog)?.AllocationIds.FirstOrDefault();
        var keep = worklogs.SelectedRows.Cast<DataGridViewRow>().Select(row => WorklogAt(row.Index)).OfType<Worklog>()
            .SelectMany(line => line.AllocationIds).ToHashSet(StringComparer.Ordinal);
        var currentIndex = -1;
        var reselect = new List<int>();
        var display = new List<WorklogGridRow>(preview.Count + entered.Count);
        foreach (var worklog in preview)
        {
            if (current != null && worklog.AllocationIds.Contains(current))
                currentIndex = display.Count;
            if (worklog.AllocationIds.Any(keep.Contains))
                reselect.Add(display.Count);
            display.Add(new WorklogGridRow(worklog, [worklog.Issue, worklog.Client, LocalStart(worklog.Start), Duration(worklog.ActualMilliseconds),
                Duration(worklog.ProposedSeconds * 1000L), worklog.Description,
                WorklogState(worklog)]));
        }
        var linkedMarks = coverages.Values.SelectMany(coverage => coverage.RelatedEntries).Select(entry => entry.Id).ToHashSet();
        foreach (var entry in entered.Where(item => !linkedMarks.Contains(item.Id)))
        {
            var status = entry.Mode == Mode && !preview.Any(line => line.Issue == entry.Issue)
                ? $"No current work · review −{entry.Seconds / 60}m"
                : $"Reconcile entered {entry.Seconds / 60}m";
            display.Add(new WorklogGridRow(entry, [entry.Issue, "", entry.Start is { } start ? LocalStart(start) : "—",
                "—", "—", "Previously entered in Tempo", status]));
        }
        visibleWorklogs = display.ToArray();
        worklogs.RowCount = visibleWorklogs.Length;
        worklogs.ClearSelection();
        if (currentIndex < 0 && reselect.Count > 0)
            currentIndex = reselect[0];
        if (currentIndex >= 0)
            worklogs.CurrentCell = worklogs.Rows[currentIndex].Cells[0];
        foreach (var index in reselect.Append(currentIndex).Where(index => index >= 0))
            worklogs.Rows[index].Selected = true;
        worklogs.Invalidate();
        roundingComparison.Text = preview.Count == 0
            ? "Assign activity to preview a worklog."
            : $"Issue/day {Duration(issueDay * 1000L)}  ·  Sessions {Duration(sessions * 1000L)}. Use your team’s rule.";
    }

    internal object? WorklogAt(int index) => index >= 0 && index < visibleWorklogs.Length ? visibleWorklogs[index].Item : null;

    private void ChangeDay(DateOnly day)
    {
        if (day >= DateOnly.FromDateTime(date.MinDate) && day <= DateOnly.FromDateTime(date.MaxDate))
            date.Value = day.ToDateTime(TimeOnly.MinValue);
    }

    private void GoToUnreviewed()
    {
        var days = store.UnreviewedDays();
        if (days.Count > 0)
            ChangeDay(days.Min());
        else
            Notify("No unassigned days.");
    }

    private Allocation[] Selected() => tabs.SelectedIndex != 0 ? [] :
        activity.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Index)
            .Where(index => index >= 0 && index < visibleActivity.Length)
            .SelectMany(index => visibleActivity[index]).OrderBy(row => row.Start).ToArray();

    private void Save(IReadOnlyCollection<string> removed, IReadOnlyCollection<Allocation> updated)
    {
        try
        {
            store.ReplaceRows(removed, updated);
            Reload();
        }
        catch (InvalidOperationException error)
        {
            Notify(error.Message);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            Notify("Local storage could not save this edit. Try again.");
        }
    }

    private void Notify(string text) => message.Text = text;

    private string Range(Allocation row)
    {
        var start = TimeZoneInfo.ConvertTime(row.Start, app.Zone);
        var end = TimeZoneInfo.ConvertTime(row.End, app.Zone);
        return $"{start:HH:mm:ss}–{end:HH:mm:ss}";
    }

    private string LocalStart(DateTimeOffset start) => TimeZoneInfo.ConvertTime(start, app.Zone).ToString("HH:mm:ss");

    private string State(Allocation row) => approvedIds.Contains(row.Id) ? "Approved" : row.Status switch
    {
        Disposition.Billable when row.IsApprovedIn(Mode) => "Reapprove changed line",
        Disposition.Billable when row.Approved => "Approve in this mode",
        Disposition.Billable => "Assigned",
        Disposition.Internal => "Internal",
        Disposition.Excluded => "Excluded",
        _ when row.Kind == SpanKind.Uncertain => "Check timing",
        _ when row.Kind == SpanKind.Idle => "Review idle gap",
        _ => "Needs assignment"
    };

    internal static string Duration(long milliseconds)
    {
        if (milliseconds < 60_000)
            return $"{milliseconds / 1000}s";
        var hours = milliseconds / 3_600_000;
        var remainder = milliseconds / 60_000 % 60;
        return hours == 0 ? $"{remainder}m" : $"{hours}h {remainder:00}m";
    }

    private void AddTab(string name, DataGridView table)
    {
        var tab = new TabPage(name) { Padding = new Padding(5) };
        tab.Controls.Add(table);
        tabs.TabPages.Add(tab);
    }

    private static void AddColumns(DataGridView view, params (string Text, int Width)[] columns)
    {
        foreach (var (text, width) in columns)
            view.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = text, FillWeight = width, MinimumWidth = 50 });
    }

    private static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };

    private static Label SummaryLabel() => new()
    {
        Text = "—", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(10),
        BackColor = SystemColors.Window, ForeColor = Color.FromArgb(23, 44, 71),
        Font = new Font("Segoe UI", 12, FontStyle.Bold), Margin = new Padding(2)
    };
}
