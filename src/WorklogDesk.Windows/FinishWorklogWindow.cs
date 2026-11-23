namespace WorklogDesk.Windows;

internal sealed class FinishWorklogWindow : Form
{
    private readonly TrayApplication app;
    private readonly CompletionCoordinator completion;
    private readonly WorkStore store;
    private readonly Label date = new() { AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(530, 0) };
    private readonly Label details = new() { AutoSize = true, MaximumSize = new Size(530, 0) };
    private readonly Label schedule = new() { AutoSize = true, ForeColor = Palette.Muted, MaximumSize = new Size(530, 0) };
    private readonly ListBox earlier = new() { Dock = DockStyle.Fill, AccessibleName = "Earlier unfinished workdays",
        BackColor = Palette.Row, ForeColor = Palette.Text, IntegralHeight = false, HorizontalScrollbar = true };
    private readonly Button review, snooze, skip, nothing, openEarlier;
    private long listRevision = -1;
    private long checkInRevision = -1;
    private DateOnly listDay;
    private WorklogDesk.Core.RoundingMode listMode;

    public FinishWorklogWindow(TrayApplication app, CompletionCoordinator completion, WorkStore store)
    {
        this.app = app;
        this.completion = completion;
        this.store = store;
        Text = "Finish today’s worklog";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(580, 420);
        ClientSize = new Size(640, 440);
        BackColor = Palette.Surface;
        ForeColor = Palette.Text;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        Palette.TitleBar(this);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(18), RowCount = 8 };
        for (var i = 0; i < layout.RowCount; i++)
            layout.RowStyles.Add(new RowStyle(i == 6 ? SizeType.Percent : SizeType.AutoSize, i == 6 ? 100 : 0));
        Controls.Add(layout);
        layout.Controls.Add(date, 0, 0);
        layout.Controls.Add(status, 0, 1);
        layout.Controls.Add(details, 0, 2);
        layout.Controls.Add(schedule, 0, 3);
        review = Ui.Button("Open review", (_, _) => app.OpenCompletionReview(completion.Today), 165);
        Ui.Primary(review);
        snooze = Ui.Button("Snooze 30 minutes", (_, _) => app.SnoozeCompletion(), 142);
        skip = Ui.Button("Skip today", (_, _) => app.SkipCompletionToday(), 109);
        nothing = Ui.Button("Nothing to log", (_, _) =>
        {
            completion.CheckIn(completion.Today, DayCheckIn.NoWork);
            app.RefreshCompletion();
        }, 114);
        layout.Controls.Add(Bar(review, snooze, skip, nothing), 0, 4);
        layout.Controls.Add(new Label { Text = "Earlier unfinished workdays", AutoSize = true,
            Margin = new Padding(3, 16, 3, 4) }, 0, 5);
        layout.Controls.Add(earlier, 0, 6);
        openEarlier = Ui.Button("Open selected date", (_, _) => OpenSelected(), 140);
        earlier.SelectedIndexChanged += (_, _) => openEarlier.Enabled = earlier.SelectedItem is EarlierDay;
        earlier.DoubleClick += (_, _) => OpenSelected();
        layout.Controls.Add(Bar(openEarlier, Ui.Button("Reminder settings…", (_, _) => app.EditCompletionReminder(), 148),
            Ui.Button("Pilot and check-in…", (_, _) =>
            {
                using var dialog = new CompletionPilotWindow(completion, app.Settings.CompletionReminder ?? CompletionReminder.Default);
                dialog.ShowDialog(this);
                app.RefreshCompletion();
            }, 145)), 0, 7);
        AcceptButton = review;
    }

    public void RefreshSummary()
    {
        var today = completion.Today;
        var day = completion.Day(today);
        var counts = completion.Counts(today);
        date.Text = today.ToString("dddd, d MMMM yyyy");
        status.Text = day.CheckIn == DayCheckIn.Leave && CompletionCoordinator.AllowsNoWork(counts) ? "Day off recorded." :
            day.CheckIn == DayCheckIn.NoWork && CompletionCoordinator.AllowsNoWork(counts) ? "Nothing to log recorded." :
            counts.Closed ? "No remaining items in Worklog Desk." :
            !counts.HasActivity && !counts.InProgress && !counts.Actionable ? "No activity recorded." :
            counts.InProgress ? "Activity is still being recorded." : "Worklog review is open.";
        details.Text = Describe(counts);
        var settings = app.Settings.CompletionReminder ?? CompletionReminder.Default;
        schedule.Text = day.Cue.Skipped ? "Reminder skipped today." :
            day.Cue.SnoozeDue is { } due && !day.Cue.SnoozedReserved && !day.Cue.Expired
                ? $"Snoozed until {TimeZoneInfo.ConvertTime(due, app.Zone):HH:mm}." :
            completion.Phase(today) == PilotPhase.Baseline ? "Pilot baseline · reminders paused." :
            settings.Enabled ? $"Reminder at {settings.Time:HH:mm} on selected workdays · {app.Zone.Id}" : "Reminders are off.";
        review.Text = counts.ToWorklogs ? "Review worklogs" : counts.Activity > 0 ? "Review activity" :
            day.CheckIn == DayCheckIn.Worked && CompletionCoordinator.AllowsNoWork(counts) ? "Add time" : "Open review";
        snooze.Enabled = settings.Enabled && day.Cue.CanSnooze(completion.Now) && counts.Actionable;
        skip.Text = day.Cue.Skipped ? "Undo skip" : "Skip today";
        nothing.Enabled = day.CheckIn is DayCheckIn.Unknown && CompletionCoordinator.AllowsNoWork(counts);
        if (listRevision == store.CompletionRevision && checkInRevision == completion.CheckInRevision &&
            listDay == today && listMode == app.Settings.Rounding)
            return;
        var selected = (earlier.SelectedItem as EarlierDay)?.Date;
        earlier.BeginUpdate();
        earlier.Items.Clear();
        var worked = completion.Dates.Where(item => item.Key < today && item.Value.CheckIn == DayCheckIn.Worked).Select(item => item.Key);
        foreach (var previous in store.CompletionDatesBefore(today).Union(worked).OrderDescending())
        {
            var state = completion.Counts(previous);
            if (state.Actionable)
                earlier.Items.Add(new EarlierDay(previous, Describe(state)));
            else if (!state.Closed && completion.Day(previous).CheckIn == DayCheckIn.Worked)
                earlier.Items.Add(new EarlierDay(previous, "No activity recorded · add time"));
        }
        if (earlier.Items.Count == 0)
            earlier.Items.Add("No earlier items to review.");
        for (var index = 0; index < earlier.Items.Count; index++)
            if (earlier.Items[index] is EarlierDay item && item.Date == selected)
                earlier.SelectedIndex = index;
        earlier.EndUpdate();
        openEarlier.Enabled = earlier.SelectedItem is EarlierDay;
        listRevision = store.CompletionRevision;
        checkInRevision = completion.CheckInRevision;
        listDay = today;
        listMode = app.Settings.Rounding;
    }

    private void OpenSelected()
    {
        if (earlier.SelectedItem is EarlierDay day)
            app.OpenCompletionReview(day.Date);
    }

    internal static string Describe(CompletionCounts counts)
    {
        var parts = new List<string>();
        Add(counts.Verified, "verified in Tempo");
        Add(counts.Attested, "manually attested");
        Add(counts.Ready, "ready");
        Add(counts.Drafts, "to approve");
        Add(counts.Activity, "activity rows to review");
        Add(counts.Corrections, "corrections to review");
        Add(counts.Refused, "refused by Tempo");
        if (counts.Unknown > 0)
            parts.Add($"Tempo status unknown for {counts.Unknown}");
        return string.Join("  ·  ", parts);

        void Add(int count, string label)
        {
            if (count > 0)
                parts.Add($"{count} {label}");
        }
    }

    internal static FlowLayoutPanel Bar(params Control[] controls)
    {
        var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        bar.Controls.AddRange(controls);
        return bar;
    }

    private sealed record EarlierDay(DateOnly Date, string Status)
    {
        public override string ToString() => $"{Date:yyyy-MM-dd}  ·  {Status}";
    }
}
