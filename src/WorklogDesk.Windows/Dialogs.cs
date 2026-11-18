using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal sealed class TaskDialog : Form
{
    private readonly TextBox client = new() { MaxLength = 120 };
    private readonly TextBox issue = new() { MaxLength = 40 };
    private readonly TextBox description = new() { MaxLength = 1000 };
    private readonly TextBox link = new() { MaxLength = 2048 };
    private readonly CheckBox startTimer = new() { Text = "Start manual timer", AutoSize = true };
    private readonly DateTimePicker start = ClockPicker();
    private readonly DateTimePicker end = ClockPicker();
    private readonly ComboBox disposition = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Allocation? interval;
    private readonly TimeZoneInfo? zone;
    private readonly bool requireDescription;

    public TaskLabel Task { get; private set; }
    public bool Clear { get; private set; }
    public bool StartTimer => startTimer.Checked;
    public DateTimeOffset Start { get; private set; }
    public DateTimeOffset End { get; private set; }
    public Disposition Status { get; private set; }

    public TaskDialog(string caption, TaskLabel initial, bool requireDescription = false, bool allowClear = false,
        bool allowStart = false, Allocation? interval = null, TimeZoneInfo? zone = null,
        IReadOnlyList<TaskLabel>? recent = null)
    {
        this.requireDescription = requireDescription;
        this.interval = interval;
        this.zone = zone;
        Task = initial;
        Text = caption;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(545, (interval == null ? 260 : 365) + (recent is { Count: > 0 } ? 40 : 0));
        var layout = Ui.DialogLayout();
        Controls.Add(layout);
        client.Text = initial.Client;
        issue.Text = initial.Issue;
        description.Text = initial.Description;
        link.Text = initial.Link;
        if (recent is { Count: > 0 })
        {
            // Today's tasks, the pin and mapped standing issues, so repeat work needs no retyping.
            var choices = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Recent task" };
            choices.Items.Add("Type a new task below");
            foreach (var item in recent)
                choices.Items.Add(new RecentTask(item));
            choices.SelectedIndex = 0;
            choices.SelectedIndexChanged += (_, _) =>
            {
                if (choices.SelectedItem is not RecentTask { Task: var chosen })
                    return;
                client.Text = chosen.Client;
                issue.Text = chosen.Issue;
                if (chosen.Description.Length > 0 || description.Text.Length == 0)
                    description.Text = chosen.Description;
            };
            Ui.Field(layout, "Recent task", choices);
            ActiveControl = choices;
        }
        Ui.Field(layout, "Client", client);
        Ui.Field(layout, "Jira issue", issue);
        Ui.Field(layout, "Description", description);
        Ui.Field(layout, "Evidence link (optional)", link);
        if (interval != null && zone != null)
        {
            start.Value = TimeZoneInfo.ConvertTime(interval.Start, zone).DateTime;
            var localEnd = TimeZoneInfo.ConvertTime(interval.End, zone).DateTime;
            end.Value = localEnd.Date == start.Value.Date ? localEnd : start.Value.Date.AddDays(1);
            disposition.Items.AddRange(Enum.GetNames<Disposition>());
            disposition.SelectedIndex = (int)interval.Status;
            Ui.Field(layout, "Start", start);
            Ui.Field(layout, "End (00:00 = midnight)", end);
            Ui.Field(layout, "Classification", disposition);
        }
        if (allowStart)
            Ui.Field(layout, "", startTimer);
        var actions = Ui.Actions(layout);
        var cancel = Ui.Button("Cancel", (_, _) => Close(), 86);
        cancel.DialogResult = DialogResult.Cancel;
        CancelButton = cancel;
        actions.Controls.Add(cancel);
        if (allowClear)
            actions.Controls.Add(Ui.Button("Unpin", (_, _) => { Clear = true; DialogResult = DialogResult.OK; }, 86));
        var save = Ui.Button(allowStart ? "Save request" : "Save", (_, _) => Save(), 112);
        AcceptButton = save;
        actions.Controls.Add(save);
        if (recent is not { Count: > 0 })
            ActiveControl = requireDescription ? description : client;
    }

    private sealed record RecentTask(TaskLabel Task)
    {
        public override string ToString() => string.Join("  ·  ",
            new[] { Task.Issue, Task.Client, Task.Description }.Where(part => part.Length > 0));
    }

    private void Save()
    {
        var result = new TaskLabel(client.Text.Trim(), issue.Text.Trim().ToUpperInvariant(),
            description.Text.Trim(), link.Text.Trim());
        if (result.Issue.Length > 0 && !TaskLabel.ValidIssue(result.Issue))
        {
            Error("Enter a Jira issue key such as BC-123, or leave it blank.");
            return;
        }
        if (!TaskLabel.ValidLink(result.Link))
        {
            Error("Use a valid HTTPS link, or leave the evidence link blank.");
            return;
        }
        if (requireDescription && result.Description.Length == 0)
        {
            Error("Enter a short description of the work.");
            return;
        }
        if (interval != null && zone != null)
        {
            var localStart = interval.Day.ToDateTime(TimeOnly.FromDateTime(start.Value));
            var localEnd = interval.Day.ToDateTime(TimeOnly.FromDateTime(end.Value));
            if (localEnd.TimeOfDay == TimeSpan.Zero)
                localEnd = localEnd.AddDays(1);
            var begin = GetInstant(localStart, zone, interval.Start);
            var finish = GetInstant(localEnd, zone, interval.End);
            if (!begin.HasValue || !finish.HasValue)
                return;
            if (finish <= begin)
            {
                Error("End must follow Start on the selected day.");
                return;
            }
            Start = begin.Value;
            End = finish.Value;
            Status = (Disposition)disposition.SelectedIndex;
            if (Status == Disposition.Unassigned && TaskLabel.ValidIssue(result.Issue))
                Status = Disposition.Billable;
            if (Status == Disposition.Billable && !TaskLabel.ValidIssue(result.Issue))
            {
                Error("Billable time needs an issue key.");
                return;
            }
        }
        Task = result;
        DialogResult = DialogResult.OK;
    }

    private DateTimeOffset? GetInstant(DateTime local, TimeZoneInfo timeZone, DateTimeOffset original)
    {
        var previous = TimeZoneInfo.ConvertTime(original, timeZone);
        if (previous.DateTime == local)
            return original;
        if (timeZone.IsInvalidTime(local))
        {
            Error("That time does not exist on this day because the clock moved forward.");
            return null;
        }
        var offset = timeZone.GetUtcOffset(local);
        if (timeZone.IsAmbiguousTime(local))
        {
            var choices = timeZone.GetAmbiguousTimeOffsets(local).OrderDescending().ToArray();
            var result = MessageBox.Show(this, $"{local:HH:mm:ss} occurs twice on this day.\n\nYes: first occurrence ({choices[0]}).\nNo: second occurrence ({choices[1]}).",
                "Choose occurrence", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (result == DialogResult.Cancel)
                return null;
            offset = choices[result == DialogResult.Yes ? 0 : 1];
        }
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private void Error(string text) => MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);

    private static DateTimePicker ClockPicker() => new()
    {
        Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm:ss", ShowUpDown = true
    };
}

internal sealed class SettingsDialog : Form
{
    private readonly CheckBox browser = new() { Text = "Browser process timing (no URLs)", AutoSize = true };
    private readonly CheckBox developer = new() { Text = "VS Code and terminal / AI", AutoSize = true };
    private readonly CheckBox remote = new() { Text = "Remote Desktop", AutoSize = true };
    private readonly CheckBox communications = new() { Text = "Outlook and Teams foreground", AutoSize = true };
    private readonly NumericUpDown idle = new() { Minimum = 1, Maximum = 60 };
    private readonly NumericUpDown target = new() { Minimum = 0, Maximum = 1440, Increment = 15 };
    private readonly DataGridView mappings = Ui.Grid();

    public Preferences Result { get; private set; }

    public SettingsDialog(Preferences preferences)
    {
        Result = preferences;
        Text = "Worklog Desk settings";
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(620, 475);
        Size = new Size(700, 510);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        var layout = Ui.DialogLayout();
        Controls.Add(layout);
        browser.Checked = preferences.Browser;
        developer.Checked = preferences.Developer;
        remote.Checked = preferences.Remote;
        communications.Checked = preferences.Communications;
        idle.Value = Math.Clamp(preferences.IdleMinutes, 1, 60);
        target.Value = Math.Clamp(preferences.WorkdayMinutes, 0, 1440);
        Ui.Field(layout, "Sources", browser);
        Ui.Field(layout, "", developer);
        Ui.Field(layout, "", remote);
        Ui.Field(layout, "", communications);
        Ui.Field(layout, "Idle cutoff (minutes)", idle);
        Ui.Field(layout, "Daily target (minutes)", target);
        Ui.Field(layout, "Workday timezone", new Label { Text = preferences.TimeZoneId, AutoSize = true });
        var note = new Label
        {
            AutoSize = true,
            Text = "Aliases match an exact workspace, Remote Desktop label or Edge profile title segment.\n" +
                "Add a Jira issue only for a standing ticket. Its time still needs your approval in the daily review."
        };
        layout.Controls.Add(note, 0, layout.RowCount++);
        layout.SetColumnSpan(note, 2);
        mappings.ReadOnly = false;
        mappings.AllowUserToAddRows = true;
        mappings.AllowUserToDeleteRows = true;
        mappings.EditMode = DataGridViewEditMode.EditOnEnter;
        mappings.Height = 155;
        mappings.AccessibleName = "Source aliases and clients";
        mappings.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "Source", Name = "Source", DataSource = new[] { "Remote Desktop", "VS Code", "Browser" },
            FillWeight = 120, FlatStyle = FlatStyle.Flat
        });
        mappings.Columns.Add("Alias", "Exact alias");
        mappings.Columns.Add("Client", "Client");
        mappings.Columns.Add("Issue", "Jira issue (optional)");
        foreach (var mapping in preferences.Mappings ?? [])
            mappings.Rows.Add(mapping.App, mapping.Alias, mapping.Task.Client, mapping.Task.Issue);
        layout.Controls.Add(mappings, 0, layout.RowCount++);
        layout.SetColumnSpan(mappings, 2);
        mappings.Dock = DockStyle.Fill;
        var buttons = Ui.Actions(layout);
        var cancel = Ui.Button("Cancel", (_, _) => Close(), 90);
        cancel.DialogResult = DialogResult.Cancel;
        CancelButton = cancel;
        buttons.Controls.Add(cancel);
        var save = Ui.Button("Save settings", (_, _) => Save(), 120);
        AcceptButton = save;
        buttons.Controls.Add(save);
    }

    private void Save()
    {
        mappings.EndEdit();
        var approved = new List<SourceMapping>();
        foreach (DataGridViewRow row in mappings.Rows)
        {
            if (row.IsNewRow)
                continue;
            var app = Convert.ToString(row.Cells[0].Value)?.Trim() ?? "";
            var alias = Convert.ToString(row.Cells[1].Value)?.Trim() ?? "";
            var client = Convert.ToString(row.Cells[2].Value)?.Trim() ?? "";
            var issue = Convert.ToString(row.Cells[3].Value)?.Trim().ToUpperInvariant() ?? "";
            if (app.Length + alias.Length + client.Length + issue.Length == 0)
                continue;
            if (issue.Length > 0 && !TaskLabel.ValidIssue(issue))
            {
                MessageBox.Show(this, $"\"{issue}\" is not a Jira issue key. Use a key such as BC-123, or leave it empty.", Text);
                return;
            }
            if (app.Length == 0 || alias.Length is 0 or > 120 || client.Length is 0 or > 120 ||
                approved.Any(entry => entry.App == app && entry.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, "Each alias needs a source, a unique exact alias and a client.", Text);
                return;
            }
            approved.Add(new SourceMapping(app, alias, new TaskLabel(client, issue)));
        }
        Result = Result with { Browser = browser.Checked, Developer = developer.Checked, Remote = remote.Checked,
            Communications = communications.Checked, IdleMinutes = (int)idle.Value, WorkdayMinutes = (int)target.Value,
            Mappings = approved.ToArray() };
        DialogResult = DialogResult.OK;
    }
}

internal sealed class SplitDialog : Form
{
    private readonly DateTimePicker time = new()
    {
        Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm:ss", ShowUpDown = true, AccessibleName = "Split at"
    };
    private readonly Allocation row;
    private readonly TimeZoneInfo zone;

    public DateTimeOffset At { get; private set; }

    public SplitDialog(Allocation row, TimeZoneInfo zone)
    {
        this.row = row;
        this.zone = zone;
        Text = "Split activity";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        ClientSize = new Size(380, 150);
        var layout = Ui.DialogLayout();
        Controls.Add(layout);
        var start = TimeZoneInfo.ConvertTime(row.Start, zone);
        var end = TimeZoneInfo.ConvertTime(row.End, zone);
        time.Value = TimeZoneInfo.ConvertTime(row.Start.AddTicks((row.End - row.Start).Ticks / 2), zone).DateTime;
        Ui.Field(layout, $"Split between {start:HH:mm:ss} and {end:HH:mm:ss} at", time);
        var actions = Ui.Actions(layout);
        var cancel = Ui.Button("Cancel", (_, _) => Close(), 86);
        cancel.DialogResult = DialogResult.Cancel;
        CancelButton = cancel;
        actions.Controls.Add(cancel);
        var split = Ui.Button("Split", (_, _) => Accept(), 86);
        AcceptButton = split;
        actions.Controls.Add(split);
        ActiveControl = time;
    }

    private void Accept()
    {
        var local = TimeZoneInfo.ConvertTime(row.Start, zone).Date + time.Value.TimeOfDay;
        if (local < TimeZoneInfo.ConvertTime(row.Start, zone).DateTime)
            local = local.AddDays(1);
        // In a repeated DST hour, pick the offset that places the split inside the row.
        var offsets = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local) : [zone.GetUtcOffset(local)];
        foreach (var offset in offsets)
        {
            var candidate = new DateTimeOffset(local, offset);
            if (candidate > row.Start && candidate < row.End)
            {
                At = candidate.ToUniversalTime();
                DialogResult = DialogResult.OK;
                return;
            }
        }
        MessageBox.Show(this, "Choose a time strictly inside the interval.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

internal static class Ui
{
    public static Button Button(string text, EventHandler handler, int width = 100)
    {
        var button = new Button { Text = text, Width = width, Height = 32, Margin = new Padding(3, 3, 4, 3) };
        button.Click += handler;
        return button;
    }

    public static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, MultiSelect = true, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.FixedSingle, RowTemplate = { Height = 27 },
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        AlternatingRowsDefaultCellStyle = { BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(246, 249, 252) }
    };

    public static TableLayoutPanel DialogLayout()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    public static void Field(TableLayoutPanel table, string label, Control editor)
    {
        var row = table.RowCount++;
        var caption = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left };
        editor.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        if (label.Length > 0)
            editor.AccessibleName ??= label;
        table.Controls.Add(caption, 0, row);
        table.Controls.Add(editor, 1, row);
    }

    public static FlowLayoutPanel Actions(TableLayoutPanel table)
    {
        var actions = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false
        };
        table.Controls.Add(actions, 0, table.RowCount++);
        table.SetColumnSpan(actions, 2);
        return actions;
    }
}
