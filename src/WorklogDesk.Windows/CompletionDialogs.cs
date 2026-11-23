namespace WorklogDesk.Windows;

internal sealed class CompletionReminderDialog : Form
{
    private readonly CheckBox enabled = new() { Text = "Remind me to finish my worklog", AutoSize = true };
    private readonly CheckedListBox days = new() { Height = 143, CheckOnClick = true,
        BackColor = Palette.Row, ForeColor = Palette.Text };
    private readonly DateTimePicker time = TimePicker();
    private static readonly DayOfWeek[] Week = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
    public CompletionReminder Result { get; private set; }

    public CompletionReminderDialog(CompletionReminder initial, TimeZoneInfo zone)
    {
        Result = initial;
        NameForm(this, "Worklog reminder settings", new Size(555, 365));
        var table = Ui.DialogLayout();
        Controls.Add(table);
        enabled.Checked = initial.Enabled;
        foreach (var day in Week)
            days.Items.Add(System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day), initial.Days.Contains(day));
        time.Value = DateTime.Today.Add(initial.Time.ToTimeSpan());
        var plan = new Label { AutoSize = true, MaximumSize = new Size(342, 0) };
        void UpdatePlan() => plan.Text = $"At {time.Value:HH:mm} on the selected workdays, I’ll review and submit my worklog.";
        time.ValueChanged += (_, _) => UpdatePlan();
        UpdatePlan();
        Ui.Field(table, "Reminder", enabled);
        Ui.Field(table, "Workdays", days);
        Ui.Field(table, "Time", time);
        Ui.Field(table, "Workday time zone", new Label { Text = zone.Id, AutoSize = true });
        Ui.Field(table, "My plan", plan);
        var actions = Ui.Actions(table);
        var save = Ui.Button("Save", (_, _) =>
        {
            var selected = days.CheckedIndices.Cast<int>().Select(index => Week[index]).ToArray();
            if (enabled.Checked && selected.Length == 0)
            {
                MessageBox.Show(this, "Choose at least one workday.", Text);
                return;
            }
            Result = new CompletionReminder(enabled.Checked, selected, time.Value.TruncateToMinute());
            DialogResult = DialogResult.OK;
        });
        Ui.Primary(save);
        var cancel = Ui.Button("Cancel", (_, _) => DialogResult = DialogResult.Cancel);
        actions.Controls.AddRange([save, cancel]);
        AcceptButton = save;
        CancelButton = cancel;
    }

    internal static DateTimePicker TimePicker() => new()
    {
        Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true
    };

    internal static void NameForm(Form form, string title, Size size)
    {
        form.Text = title;
        form.ClientSize = size;
        form.StartPosition = FormStartPosition.CenterParent;
        form.ShowInTaskbar = false;
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        form.BackColor = Palette.Surface;
        form.ForeColor = Palette.Text;
        Palette.TitleBar(form);
    }
}

internal sealed class CompletionPilotWindow : Form
{
    private readonly CompletionCoordinator completion;
    private readonly CompletionReminder reminder;
    private readonly DateTimePicker day = new() { Format = DateTimePickerFormat.Short, AccessibleName = "Check-in date" };
    private readonly DateTimePicker cutoff = CompletionReminderDialog.TimePicker();
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(690, 0) };
    private readonly Label checkIn = new() { AutoSize = true };
    private readonly TextBox report = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
        BackColor = Palette.Row, ForeColor = Palette.Text, AccessibleName = "Local pilot results" };
    private readonly Button start, worked, off, nothing, clear;
    private readonly Label cutoffLabel = new() { AutoSize = true, Text = "Fixed cutoff", Margin = new Padding(3, 9, 3, 0) };

    public CompletionPilotWindow(CompletionCoordinator completion, CompletionReminder reminder)
    {
        this.completion = completion;
        this.reminder = reminder;
        CompletionReminderDialog.NameForm(this, "Worklog pilot and private check-in", new Size(750, 510));
        MinimumSize = new Size(650, 430);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14), RowCount = 6 };
        for (var i = 0; i < layout.RowCount; i++)
            layout.RowStyles.Add(new RowStyle(i == 5 ? SizeType.Percent : SizeType.AutoSize, i == 5 ? 100 : 0));
        Controls.Add(layout);
        cutoff.AccessibleName = "Fixed evaluation cutoff";
        cutoff.Value = DateTime.Today.AddHours(18);
        start = Ui.Button("Start 5-day baseline", (_, _) =>
        {
            completion.StartPilot(cutoff.Value.TruncateToMinute());
            Render();
        }, 154);
        Ui.Primary(start);
        layout.Controls.Add(status, 0, 0);
        layout.Controls.Add(FinishWorklogWindow.Bar(cutoffLabel, cutoff, start), 0, 1);
        day.MaxDate = completion.Today.ToDateTime(TimeOnly.MinValue);
        day.Value = day.MaxDate;
        day.ValueChanged += (_, _) => Render();
        layout.Controls.Add(FinishWorklogWindow.Bar(new Label { Text = "Check-in date", AutoSize = true,
            Margin = new Padding(3, 5, 3, 0) }, day), 0, 2);
        layout.Controls.Add(checkIn, 0, 3);
        worked = Ui.Button("I worked", (_, _) => CheckIn(DayCheckIn.Worked));
        off = Ui.Button("Day off", (_, _) => CheckIn(DayCheckIn.Leave));
        nothing = Ui.Button("Nothing to log", (_, _) => CheckIn(DayCheckIn.NoWork), 115);
        clear = Ui.Button("Clear check-in", (_, _) => CheckIn(DayCheckIn.Unknown), 115);
        layout.Controls.Add(FinishWorklogWindow.Bar(worked, off, nothing, clear), 0, 4);
        layout.Controls.Add(report, 0, 5);
        Render();
    }

    private DateOnly Selected => DateOnly.FromDateTime(day.Value);

    private void CheckIn(DayCheckIn answer)
    {
        completion.CheckIn(Selected, answer);
        Render();
    }

    private void Render()
    {
        var pilot = completion.Pilot;
        var setting = pilot?.Cutoff ?? new TimeOnly(18, 0);
        cutoff.Enabled = start.Enabled = pilot == null;
        if (pilot != null)
            cutoff.Value = DateTime.Today.Add(setting.ToTimeSpan());
        status.Text = pilot == null
            ? "Track five actual workdays without reminders, then ten with reminders. Check in even if capture missed your work. Results stay on this PC."
            : $"{(pilot.Ends <= completion.Today ? "Pilot complete" : completion.Phase(completion.Today) == PilotPhase.Reminder ? "Reminder period" : "Baseline")} · " +
                $"{setting:HH:mm} cutoff · {pilot.ZoneId}." + (!reminder.Enabled ? " Reminders are off in reminder settings." : "");
        var record = completion.Day(Selected);
        var counts = completion.Counts(Selected);
        checkIn.Text = record.CheckIn switch
        {
            DayCheckIn.Worked => "Worked",
            DayCheckIn.Leave => "Day off",
            DayCheckIn.NoWork => "Nothing to log",
            _ => "No check-in recorded"
        };
        if (record.CheckIn == DayCheckIn.Worked && !counts.HasActivity && counts.Verified + counts.Attested == 0)
            checkIn.Text += " · No activity recorded. Use Add time in Activity.";
        off.Enabled = nothing.Enabled = CompletionCoordinator.AllowsNoWork(counts);
        clear.Enabled = record.CheckIn != DayCheckIn.Unknown;
        report.Text = completion.Report(reminder);
    }
}

internal static class CompletionTime
{
    public static TimeOnly TruncateToMinute(this DateTime time) => new(time.Hour, time.Minute);
}
