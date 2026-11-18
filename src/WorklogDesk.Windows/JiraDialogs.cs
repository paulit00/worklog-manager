namespace WorklogDesk.Windows;

internal sealed class JiraConfigDialog : Form
{
    private readonly TextBox site = new() { MaxLength = 200, PlaceholderText = "https://company.atlassian.net" };
    private readonly TextBox email = new() { MaxLength = 254 };
    private readonly TextBox token = new() { MaxLength = 1024, UseSystemPasswordChar = true };
    private readonly TextBox cloud = new() { MaxLength = 36, PlaceholderText = "Only for a scoped API token" };
    private readonly TextBox projects = new() { MaxLength = 500, PlaceholderText = "Example: BC, OPS" };
    private readonly TextBox waiting = new() { Text = "Waiting for Customer", MaxLength = 120 };
    private readonly Label notice = new() { AutoSize = true };
    private readonly CancellationTokenSource cancellation = new();
    private readonly Button connect;
    private readonly JiraSettings? original;

    public JiraSettings? Result { get; private set; }

    public JiraConfigDialog(JiraSettings? settings)
    {
        original = settings;
        Text = "Connect Jira Cloud";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = false;
        ClientSize = new Size(590, 365);
        var layout = Ui.DialogLayout();
        Controls.Add(layout);
        site.Text = settings?.Site ?? "";
        email.Text = settings?.Email ?? "";
        cloud.Text = settings?.CloudId ?? "";
        projects.Text = settings?.Projects ?? "";
        waiting.Text = settings?.WaitingStatus ?? "Waiting for Customer";
        token.PlaceholderText = settings == null ? "Paste an Atlassian API token" : "Leave blank to keep the saved token";
        Ui.Field(layout, "Jira Cloud site", site);
        Ui.Field(layout, "Atlassian email", email);
        Ui.Field(layout, "API token", token);
        Ui.Field(layout, "Cloud ID", cloud);
        Ui.Field(layout, "Project keys", projects);
        Ui.Field(layout, "Waiting status", waiting);
        notice.Text = "Read-only issue access. The token stays in Windows Credential Manager.";
        notice.MaximumSize = new Size(555, 0);
        layout.Controls.Add(notice, 0, layout.RowCount++);
        layout.SetColumnSpan(notice, 2);
        var actions = Ui.Actions(layout);
        var cancel = Ui.Button("Cancel", (_, _) => Close(), 85);
        cancel.DialogResult = DialogResult.Cancel;
        CancelButton = cancel;
        actions.Controls.Add(cancel);
        if (settings != null)
            actions.Controls.Add(Ui.Button("Disconnect", (_, _) => Disconnect(), 107));
        connect = Ui.Button("Test and save", async (_, _) => await Connect(), 113);
        AcceptButton = connect;
        actions.Controls.Add(connect);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        cancellation.Cancel();
        cancellation.Dispose();
        base.OnFormClosed(e);
    }

    private async Task Connect()
    {
        connect.Enabled = false;
        notice.Text = "Checking Jira account…";
        try
        {
            var settings = JiraSettings.Create(site.Text, email.Text, cloud.Text, projects.Text, waiting.Text);
            var provided = token.Text.Trim();
            var secret = provided.Length == 0 ? CredentialVault.Read(settings.CredentialTarget) : provided;
            if (string.IsNullOrEmpty(secret))
                throw new ArgumentException("Enter an API token for this Jira site and account.");
            using var catalog = new JiraCatalog(settings, secret);
            var cancel = cancellation.Token;
            _ = await catalog.AccountAsync(cancel);
            if (IsDisposed || cancel.IsCancellationRequested)
                return;
            if (provided.Length > 0)
                CredentialVault.Write(settings, provided);
            token.Clear();
            Result = settings;
            DialogResult = DialogResult.OK;
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
                notice.Text = "Jira did not respond in time.";
        }
        catch (Exception error) when (error is ArgumentException or HttpRequestException or InvalidOperationException)
        {
            if (!IsDisposed)
                notice.Text = error is HttpRequestException ? "Could not reach Jira Cloud. Check the connection." : error.Message;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or KeyNotFoundException or InvalidDataException)
        {
            if (!IsDisposed)
                notice.Text = "Jira returned an unexpected account response.";
        }
        finally
        {
            if (!IsDisposed)
                connect.Enabled = true;
        }
    }

    private void Disconnect()
    {
        if (original == null)
            return;
        Result = null;
        DialogResult = DialogResult.OK;
    }
}

internal sealed class JiraIssueDialog : Form
{
    internal static readonly JiraIssue[] DemoIssues =
    [
        new("NW-243", "Review Business Central deployment", "In Progress"),
        new("CED-118", "Reply to customer with restore details", "Waiting for Customer"),
        new("CED-132", "Refresh sandbox environment", "Open")
    ];
    private readonly JiraCatalog? catalog;
    private readonly TextBox term = new() { MaxLength = 120, Width = 245, PlaceholderText = "Issue key or title words" };
    private readonly CheckBox mine = new() { Text = "Assigned to me", AutoSize = true, Margin = new Padding(6, 9, 2, 0) };
    private readonly CheckBox waiting = new() { Text = "Waiting only", AutoSize = true, Margin = new Padding(6, 9, 2, 0) };
    private readonly Label notice = new() { AutoSize = true };
    private readonly DataGridView issues = Ui.Grid();
    private readonly Button search;
    private readonly Button next;
    private readonly CancellationTokenSource cancellation = new();
    private JiraQuery? previous;
    private string? continuation;

    public JiraIssue? SelectedIssue { get; private set; }

    public JiraIssueDialog(JiraCatalog? catalog, string initial)
    {
        this.catalog = catalog;
        Text = catalog == null ? "Find Jira issue · demo" : "Find Jira issue";
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(730, 460);
        MinimumSize = new Size(670, 400);
        ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        var filters = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        term.Text = initial;
        term.AccessibleName = "Jira search";
        term.Margin = new Padding(2, 8, 5, 3);
        search = Ui.Button("Search", async (_, _) => await Search(false), 83);
        filters.Controls.AddRange([term, mine, waiting, search]);
        layout.Controls.Add(filters, 0, 0);
        notice.Text = "Enter a Jira key or search the configured projects.";
        layout.Controls.Add(notice, 0, 1);
        issues.AccessibleName = "Matching Jira issues";
        issues.MultiSelect = false;
        issues.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Key", FillWeight = 65 });
        issues.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Summary", FillWeight = 180 });
        issues.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", FillWeight = 95 });
        issues.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) SelectIssue(); };
        issues.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SelectIssue();
            }
        };
        layout.Controls.Add(issues, 0, 2);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = Ui.Button("Cancel", (_, _) => Close(), 85);
        cancel.DialogResult = DialogResult.Cancel;
        CancelButton = cancel;
        actions.Controls.Add(cancel);
        actions.Controls.Add(Ui.Button("Use issue", (_, _) => SelectIssue(), 100));
        next = Ui.Button("Next page", async (_, _) => await Search(true), 100);
        next.Enabled = false;
        actions.Controls.Add(next);
        layout.Controls.Add(actions, 0, 3);
        AcceptButton = search;
        if (WorklogDesk.Core.TaskLabel.ValidIssue(initial))
            Shown += async (_, _) => await Search(false);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        cancellation.Cancel();
        cancellation.Dispose();
        base.OnFormClosed(e);
    }

    private async Task Search(bool page)
    {
        if (!search.Enabled)
            return;
        search.Enabled = false;
        next.Enabled = false;
        if (!page)
        {
            issues.Rows.Clear();
            continuation = null;
        }
        notice.Text = "Searching…";
        var query = page && previous != null
            ? previous with { Next = continuation }
            : new JiraQuery(term.Text, mine.Checked, waiting.Checked);
        var cancel = cancellation.Token;
        try
        {
            var results = catalog == null ? new JiraResults(DemoIssues.Where(item =>
                (!query.WaitingOnly || item.Status == "Waiting for Customer") &&
                (item.Key.Contains(query.Text, StringComparison.OrdinalIgnoreCase) ||
                    item.Summary.Contains(query.Text, StringComparison.OrdinalIgnoreCase))).ToArray(), null)
                : await catalog.SearchAsync(query, cancel);
            if (IsDisposed || cancel.IsCancellationRequested)
                return;
            previous = query;
            continuation = results.Next;
            issues.Rows.Clear();
            foreach (var issue in results.Issues)
                issues.Rows[issues.Rows.Add(issue.Key, issue.Summary, issue.Status)].Tag = issue;
            notice.Text = results.Issues.Count == 0 ? "No accessible issues matched." : $"{results.Issues.Count} matching issue(s).";
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
                notice.Text = "Jira did not respond in time.";
        }
        catch (Exception error) when (error is ArgumentException or HttpRequestException or InvalidOperationException)
        {
            if (!IsDisposed)
                notice.Text = error is HttpRequestException ? "Could not reach Jira Cloud." : error.Message;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or KeyNotFoundException or InvalidDataException)
        {
            if (!IsDisposed)
                notice.Text = "Jira returned an unexpected issue response.";
        }
        finally
        {
            if (!IsDisposed)
            {
                search.Enabled = true;
                next.Enabled = continuation != null;
            }
        }
    }

    private void SelectIssue()
    {
        if (issues.CurrentRow?.Tag is not JiraIssue issue)
        {
            notice.Text = "Search and select an issue first.";
            return;
        }
        SelectedIssue = issue;
        DialogResult = DialogResult.OK;
    }
}
