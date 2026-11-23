namespace WorklogDesk.Windows;

/// <summary>
/// Creates a Jira issue in a searched project, assigned to the user, and hands back its key.
/// </summary>
internal sealed class CreateIssueDialog : Form
{
    private static readonly JiraProject[] DemoProjects = [new("NW", "Northwind Business Central"), new("CED", "Cedar Foods")];

    private readonly TextBox projectSearch = new()
    {
        Dock = DockStyle.Fill, PlaceholderText = "Search projects by name or key", AccessibleName = "Project search"
    };
    private readonly ListBox projects = new() { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = "Projects" };
    private readonly ComboBox type = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, AccessibleName = "Issue type" };
    private readonly TextBox summary = new() { Dock = DockStyle.Fill, MaxLength = 250, AccessibleName = "Summary" };
    private readonly TextBox description = new()
    {
        Dock = DockStyle.Fill, Multiline = true, Height = 70, ScrollBars = ScrollBars.Vertical, AccessibleName = "Description"
    };
    private readonly CheckBox assignMe = new() { Text = "Assign to me", Checked = true, AutoSize = true };
    private readonly Label status = new() { AutoSize = true, ForeColor = Palette.Muted, MaximumSize = new Size(620, 0) };
    private readonly System.Windows.Forms.Timer debounce = new() { Interval = 350 };
    private readonly Button create;
    private readonly JiraCatalog? catalog;
    private readonly string[] preferred;
    private readonly bool assistedDraft;
    private (string Project, string Title, string Result)? duplicateCheck;
    private readonly CancellationTokenSource closing = new();
    private CancellationTokenSource? pending;
    private string? account;
    private string? typesFor;

    public JiraIssue? NewIssue { get; private set; }
    public string ProjectKey { get; private set; } = "";

    /// <param name="catalog">Null runs a demo that creates nothing.</param>
    /// <param name="preferred">Last used and configured project keys, listed first.</param>
    public CreateIssueDialog(JiraCatalog? catalog, string initialSummary, string[] preferred, string? assistedDescription = null)
    {
        this.catalog = catalog;
        this.preferred = preferred;
        assistedDraft = assistedDescription != null;
        Text = catalog == null ? "New Jira task · demo" : "New Jira task";
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(680, 560);
        MinimumSize = new Size(560, 480);
        ShowInTaskbar = catalog != null && Owner == null;
        MinimizeBox = false;
        BackColor = Palette.Surface;
        ForeColor = Palette.Text;
        Palette.TitleBar(this);
        projects.BackColor = description.BackColor = Palette.Row;
        projects.ForeColor = Palette.Text;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 8 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 8; row++)
            layout.RowStyles.Add(row == 1 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        Field(layout, "Project", projectSearch, 0);
        layout.Controls.Add(projects, 1, 1);
        Field(layout, "Type", type, 2);
        Field(layout, "Summary", summary, 3);
        Field(layout, "Description", description, 4);
        layout.Controls.Add(assignMe, 1, 5);
        layout.Controls.Add(status, 1, 6);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = Ui.Button("Cancel", (_, _) => Close(), 86);
        cancel.DialogResult = DialogResult.Cancel;
        CancelButton = cancel;
        create = Ui.Button("Create", async (_, _) => await Create(), 96);
        Ui.Primary(create);
        actions.Controls.AddRange([cancel, create]);
        if (assistedDraft)
        {
            actions.Controls.Add(Ui.Button("Check duplicates", async (_, _) => await CheckDuplicates(), 132));
            summary.TextChanged += (_, _) => duplicateCheck = null;
            projects.SelectedIndexChanged += (_, _) => duplicateCheck = null;
            description.Text = assistedDescription;
        }
        layout.Controls.Add(actions, 0, 7);
        layout.SetColumnSpan(actions, 2);

        summary.Text = initialSummary;
        projectSearch.TextChanged += (_, _) =>
        {
            debounce.Stop();
            debounce.Start();
        };
        projectSearch.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Down && projects.Items.Count > 0)
            {
                e.SuppressKeyPress = true;
                projects.Focus();
                projects.SelectedIndex = Math.Max(projects.SelectedIndex, 0);
            }
        };
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            await LoadProjects(projectSearch.Text);
        };
        projects.SelectedIndexChanged += async (_, _) => await LoadTypes();
        Shown += async (_, _) =>
        {
            projectSearch.Focus();
            await LoadProjects("");
        };
    }

    private static void Field(TableLayoutPanel layout, string label, Control field, int row)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 7, 10, 3) }, 0, row);
        layout.Controls.Add(field, 1, row);
    }

    private CancellationToken Restart()
    {
        pending?.Cancel();
        pending?.Dispose();
        pending = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        return pending.Token;
    }

    private async Task LoadProjects(string text)
    {
        var token = Restart();
        try
        {
            IReadOnlyList<JiraProject> found;
            if (catalog == null)
                found = DemoProjects.Where(item => item.Key.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    item.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
            else
            {
                status.Text = "Loading projects…";
                found = await catalog.ProjectsAsync(text, token);
            }
            if (IsDisposed || token.IsCancellationRequested)
                return;
            var ordered = found.OrderBy(item => Array.IndexOf(preferred, item.Key) is var index && index >= 0 ? index : int.MaxValue)
                .ThenBy(item => item.Key, StringComparer.Ordinal).ToArray();
            var chosen = (projects.SelectedItem as ProjectItem)?.Project.Key;
            projects.BeginUpdate();
            projects.Items.Clear();
            foreach (var project in ordered)
                projects.Items.Add(new ProjectItem(project));
            projects.EndUpdate();
            var select = Array.FindIndex(ordered, item => item.Key == (chosen ?? preferred.FirstOrDefault()));
            if (select < 0 && ordered.Length == 1)
                select = 0;
            if (select >= 0)
                projects.SelectedIndex = select;
            status.Text = ordered.Length == 0 ? "No project matched." : "";
        }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException or ArgumentException or
            System.Text.Json.JsonException or KeyNotFoundException)
        {
            if (!IsDisposed)
                status.Text = error.Message;
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadTypes()
    {
        if (projects.SelectedItem is not ProjectItem { Project: var project } || project.Key == typesFor)
            return;
        typesFor = project.Key;
        type.Items.Clear();
        try
        {
            IReadOnlyList<JiraIssueType> types = catalog == null
                ? [new JiraIssueType("1", "Task"), new JiraIssueType("2", "Bug")]
                : await catalog.IssueTypesAsync(project.Key, closing.Token);
            if (IsDisposed || typesFor != project.Key)
                return;
            foreach (var item in types)
                type.Items.Add(new TypeItem(item));
            var task = types.ToList().FindIndex(item => item.Name.Equals("Task", StringComparison.OrdinalIgnoreCase));
            type.SelectedIndex = types.Count == 0 ? -1 : Math.Max(task, 0);
            status.Text = types.Count == 0 ? "You can't create issues in this project." : "";
        }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException or
            System.Text.Json.JsonException or KeyNotFoundException)
        {
            if (!IsDisposed)
                status.Text = error.Message;
        }
        catch (OperationCanceledException) { }
    }

    private async Task Create()
    {
        if (projects.SelectedItem is not ProjectItem { Project: var project })
        {
            status.Text = "Pick a project.";
            return;
        }
        if (type.SelectedItem is not TypeItem { Type: var issueType })
        {
            status.Text = "Pick an issue type.";
            return;
        }
        if (summary.Text.Trim().Length == 0)
        {
            status.Text = "Enter a summary.";
            summary.Focus();
            return;
        }
        var finalTitle = summary.Text.Trim();
        var finalDescription = description.Text;
        var assign = assignMe.Checked;
        if (assistedDraft)
        {
            var result = duplicateCheck is { } check && check.Project == project.Key && check.Title == finalTitle
                ? check.Result : "Duplicate check unavailable for this project and title.";
            if (MessageBox.Show(this, result + "\n\nCreate this Jira issue?", "Confirm Jira task", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
        }
        create.Enabled = false;
        status.Text = "Creating the issue in Jira…";
        try
        {
            if (catalog == null)
            {
                NewIssue = new JiraIssue(project.Key + "-900", finalTitle, "New");
            }
            else
            {
                account ??= await catalog.AccountAsync(closing.Token);
                var (issue, warning) = await catalog.CreateIssueAsync(new NewJiraIssue(project.Key, issueType.Id,
                    finalTitle, finalDescription, assign ? account : null), closing.Token, recoverByTitle: !assistedDraft);
                NewIssue = issue;
                if (warning != null)
                    MessageBox.Show(this, warning, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            ProjectKey = project.Key;
            DialogResult = DialogResult.OK;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or HttpRequestException or
            System.Text.Json.JsonException or KeyNotFoundException or InvalidDataException)
        {
            if (!IsDisposed)
                status.Text = error.Message;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!IsDisposed)
                create.Enabled = true;
        }
    }

    private async Task CheckDuplicates()
    {
        if (catalog == null || projects.SelectedItem is not ProjectItem { Project: var project } || summary.Text.Trim().Length == 0)
        {
            status.Text = "Choose a project and enter a title to check duplicates in Jira.";
            return;
        }
        var title = summary.Text.Trim();
        duplicateCheck = null;
        status.Text = "Checking Jira, including completed issues…";
        try
        {
            var results = await catalog.SearchAssistedAsync(title, project.Key, closing.Token);
            if (IsDisposed || projects.SelectedItem is not ProjectItem picked || picked.Project.Key != project.Key || summary.Text.Trim() != title)
                return;
            var closest = results.Issues.Take(4).Select(issue => $"{issue.Key} · {issue.Summary}").ToArray();
            var text = closest.Length == 0 ? "No matches in this search." : string.Join("\n", closest);
            if (results.Next != null || results.Issues.Count == 20 || results.Issues.Count > closest.Length)
                text += "\nAdditional matches may exist.";
            duplicateCheck = (project.Key, title, text);
            status.Text = text;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or HttpRequestException or
            System.Text.Json.JsonException or KeyNotFoundException)
        {
            if (!IsDisposed)
                status.Text = "Duplicate check unavailable. " + error.Message;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        closing.Cancel();
        debounce.Dispose();
        pending?.Dispose();
        closing.Dispose();
        base.OnFormClosed(e);
    }

    private sealed record ProjectItem(JiraProject Project)
    {
        public override string ToString() => $"{Project.Key}  ·  {Project.Name}";
    }

    private sealed record TypeItem(JiraIssueType Type)
    {
        public override string ToString() => Type.Name;
    }
}
