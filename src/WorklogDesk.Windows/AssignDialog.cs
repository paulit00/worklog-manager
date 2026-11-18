using WorklogDesk.Core;

namespace WorklogDesk.Windows;

/// <summary>An issue the user can pick: a local suggestion, one of their Jira issues, or a search result.</summary>
internal sealed record IssueChoice(string Key, string Summary, string Where, string Client = "", string Description = "");

/// <summary>
/// Keeps the user's open Jira issues for a few minutes so the assign window opens with them already listed.
/// </summary>
internal sealed class JiraIssueCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private DateTime loadedAt = DateTime.MinValue;

    public IReadOnlyList<JiraIssue> Issues { get; private set; } = [];
    public bool Fresh => DateTime.UtcNow - loadedAt < Lifetime;

    public void Store(IReadOnlyList<JiraIssue> issues)
    {
        Issues = issues;
        loadedAt = DateTime.UtcNow;
    }

    public void Clear() => loadedAt = DateTime.MinValue;
}

/// <summary>
/// One place to give selected time a destination: pick or search a Jira issue (yours or a colleague's),
/// type a key, or mark the time internal or excluded.
/// </summary>
internal sealed class AssignDialog : Form
{
    private readonly TextBox search = new()
    {
        Dock = DockStyle.Fill, AccessibleName = "Search Jira or type a key", PlaceholderText = "Search Jira or type a key, for example BC-123"
    };
    private readonly ListView list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable, AccessibleName = "Issues"
    };
    private readonly TextBox client = new() { Dock = DockStyle.Fill, MaxLength = 120, AccessibleName = "Client" };
    private readonly TextBox description = new() { Dock = DockStyle.Fill, MaxLength = 1000, AccessibleName = "Description" };
    private readonly Label status = new() { AutoSize = true, ForeColor = Palette.Muted, Margin = new Padding(3, 6, 3, 6) };
    private readonly System.Windows.Forms.Timer debounce = new() { Interval = 350 };
    private readonly IReadOnlyList<IssueChoice> local;
    private readonly JiraIssueCache cache;
    private readonly Func<JiraCatalog?> openCatalog;
    private readonly bool demo;
    private readonly TaskLabel initial;
    private JiraCatalog? catalog;
    private bool catalogFailed;
    private CancellationTokenSource? pending;
    private IReadOnlyList<IssueChoice> found = [];
    private string autoDescription = "";
    private string autoClient = "";

    public TaskLabel Result { get; private set; }
    public Disposition Choice { get; private set; } = Disposition.Billable;

    public AssignDialog(string caption, TaskLabel initial, IReadOnlyList<IssueChoice> local, JiraIssueCache cache,
        Func<JiraCatalog?> openCatalog, bool demo, bool allowClassify = true)
    {
        this.initial = initial;
        this.local = local;
        this.cache = cache;
        this.openCatalog = openCatalog;
        this.demo = demo;
        Result = initial;
        Text = caption;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(760, 560);
        MinimumSize = new Size(620, 440);
        ShowInTaskbar = false;
        MinimizeBox = false;
        KeyPreview = true;
        BackColor = Palette.Surface;
        ForeColor = Palette.Text;
        Palette.TitleBar(this);
        list.BackColor = Palette.Row;
        list.ForeColor = Palette.Text;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        layout.Controls.Add(search, 0, 0);
        layout.SetColumnSpan(search, 2);
        layout.Controls.Add(status, 0, 1);
        layout.SetColumnSpan(status, 2);
        list.Columns.Add("Issue", 95);
        list.Columns.Add("Summary", 420);
        list.Columns.Add("Where", 170);
        list.GridLines = false;
        layout.Controls.Add(list, 0, 2);
        layout.SetColumnSpan(list, 2);
        AddField(layout, "Client", client, 3);
        AddField(layout, "Description", description, 4);
        client.Text = initial.Client;
        description.Text = initial.Description;

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = Ui.Button("Cancel", (_, _) => Close(), 86);
        cancel.DialogResult = DialogResult.Cancel;
        CancelButton = cancel;
        var assign = Ui.Button("Assign", (_, _) => Accept(Disposition.Billable), 96);
        Ui.Primary(assign);
        actions.Controls.Add(cancel);
        actions.Controls.Add(assign);
        if (allowClassify)
        {
            actions.Controls.Add(Ui.Button("Exclude", (_, _) => Accept(Disposition.Excluded), 86));
            actions.Controls.Add(Ui.Button("Internal", (_, _) => Accept(Disposition.Internal), 86));
        }
        layout.Controls.Add(actions, 0, 5);
        layout.SetColumnSpan(actions, 2);

        search.Text = TaskLabel.ValidIssue(initial.Issue) ? initial.Issue : "";
        search.TextChanged += (_, _) =>
        {
            found = [];
            Refill();
            debounce.Stop();
            debounce.Start();
        };
        search.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Down && list.Items.Count > 0)
            {
                e.SuppressKeyPress = true;
                list.Focus();
                Select(list.SelectedItems.Count > 0 ? list.SelectedIndices[0] : 0);
            }
            else if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                if (list.SelectedItems.Count == 0 && list.Items.Count > 0)
                    Select(0);
                Accept(Disposition.Billable);
            }
        };
        list.SelectedIndexChanged += (_, _) => Fill();
        list.DoubleClick += (_, _) => Accept(Disposition.Billable);
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Accept(Disposition.Billable);
            }
        };
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            await SearchJira();
        };
        Shown += async (_, _) =>
        {
            search.Focus();
            search.SelectAll();
            Refill();
            if (list.Items.Count > 0 && list.Items[0].Tag is IssueChoice { Where: "Suggested" })
                Select(0);
            await LoadMine();
            if (search.Text.Length >= 2)
                await SearchJira();
        };
    }

    private static void AddField(TableLayoutPanel layout, string label, Control field, int row)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 3) }, 0, row);
        layout.Controls.Add(field, 1, row);
    }

    private JiraCatalog? Catalog()
    {
        if (catalog != null || catalogFailed || demo)
            return catalog;
        try { catalog = openCatalog(); }
        catch (InvalidOperationException error)
        {
            catalogFailed = true;
            status.Text = error.Message.StartsWith("Connect Jira", StringComparison.Ordinal)
                ? "Jira isn't connected. Pick a recent issue or type a key, or use Connect Jira to search."
                : error.Message;
        }
        return catalog;
    }

    private async Task LoadMine()
    {
        if (demo)
        {
            cache.Store(JiraIssueDialog.DemoIssues);
            Refill();
            status.Text = "Demo Jira issues. Type to filter, or type any key.";
            return;
        }
        if (cache.Fresh)
        {
            Refill();
            status.Text = $"{cache.Issues.Count} of your Jira issues. Type to search all projects.";
            return;
        }
        if (Catalog() is not { } jira)
            return;
        status.Text = "Loading your Jira issues…";
        try
        {
            var token = Restart();
            var mine = await jira.SearchAsync(new JiraQuery("", Mine: true, WaitingOnly: false), token);
            var waiting = await jira.SearchAsync(new JiraQuery("", Mine: false, WaitingOnly: true), token);
            if (IsDisposed || token.IsCancellationRequested)
                return;
            cache.Store(mine.Issues.Concat(waiting.Issues).DistinctBy(issue => issue.Key).ToArray());
            Refill();
            status.Text = $"{cache.Issues.Count} of your open and Waiting for Customer issues. Type to search all projects.";
        }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException or
            TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
        {
            if (!IsDisposed)
                status.Text = "Couldn't load your Jira issues. " + error.Message;
        }
    }

    private async Task SearchJira()
    {
        var text = search.Text.Trim();
        if (text.Length < 2 || IsDisposed)
            return;
        if (demo)
        {
            found = JiraIssueDialog.DemoIssues.Where(issue => Matches(issue.Key, issue.Summary, text))
                .Select(issue => new IssueChoice(issue.Key, issue.Summary, issue.Status)).ToArray();
            Refill();
            return;
        }
        if (Catalog() is not { } jira)
            return;
        status.Text = "Searching Jira…";
        try
        {
            var token = Restart();
            var results = await jira.SearchAsync(new JiraQuery(text, Mine: false, WaitingOnly: false), token);
            if (IsDisposed || token.IsCancellationRequested || search.Text.Trim() != text)
                return;
            found = results.Issues.Select(issue => new IssueChoice(issue.Key, issue.Summary, issue.Status)).ToArray();
            Refill();
            status.Text = found.Count == 0 ? "No Jira issue matched. You can still type a key." : $"{found.Count} found in Jira.";
        }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException or
            TaskCanceledException or System.Text.Json.JsonException or InvalidDataException or ArgumentException)
        {
            if (!IsDisposed && error is not TaskCanceledException)
                status.Text = error.Message;
        }
    }

    private CancellationToken Restart()
    {
        pending?.Cancel();
        pending?.Dispose();
        pending = new CancellationTokenSource();
        return pending.Token;
    }

    private static bool Matches(string key, string summary, string text) => text.Length == 0 ||
        key.Contains(text, StringComparison.OrdinalIgnoreCase) || summary.Contains(text, StringComparison.OrdinalIgnoreCase);

    private void Refill()
    {
        var text = search.Text.Trim();
        var chosen = list.SelectedItems.Count > 0 ? ((IssueChoice)list.SelectedItems[0].Tag!).Key : null;
        list.BeginUpdate();
        list.Items.Clear();
        var shown = new HashSet<string>(StringComparer.Ordinal);
        var typed = text.ToUpperInvariant();
        if (TaskLabel.ValidIssue(typed) && !local.Concat(cache.Issues.Select(FromJira)).Concat(found).Any(item => item.Key == typed))
            Add(new IssueChoice(typed, "Use this key as typed", "Typed"));
        foreach (var item in local.Where(item => Matches(item.Key, item.Summary + " " + item.Client, text)))
            Add(item);
        foreach (var issue in cache.Issues.Where(issue => Matches(issue.Key, issue.Summary, text)))
            Add(FromJira(issue) with { Where = "Mine · " + issue.Status });
        foreach (var item in found)
            Add(item with { Where = "Jira · " + item.Where });
        list.EndUpdate();
        var index = chosen == null ? -1 : list.Items.Cast<ListViewItem>().ToList().FindIndex(item => ((IssueChoice)item.Tag!).Key == chosen);
        if (index >= 0)
            Select(index);

        void Add(IssueChoice item)
        {
            // An issue appears once, in its most relevant place: suggested, then yours, then search results.
            if (!shown.Add(item.Key))
                return;
            list.Items.Add(new ListViewItem([item.Key, item.Summary, item.Where]) { Tag = item });
        }
    }

    private static IssueChoice FromJira(JiraIssue issue) => new(issue.Key, issue.Summary, issue.Status);

    private void Select(int index)
    {
        if (index < 0 || index >= list.Items.Count)
            return;
        list.Items[index].Selected = true;
        list.Items[index].Focused = true;
        list.EnsureVisible(index);
    }

    // Choosing an issue fills client and description, but never overwrites what the user typed.
    private void Fill()
    {
        if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not IssueChoice item)
            return;
        if (item.Client.Length > 0 && (client.Text.Length == 0 || client.Text == autoClient))
            client.Text = autoClient = item.Client;
        var text = item.Description.Length > 0 ? item.Description : item.Where == "Typed" ? "" : item.Summary;
        if (text.Length > 0 && (description.Text.Length == 0 || description.Text == autoDescription))
            description.Text = autoDescription = text;
    }

    private void Accept(Disposition choice)
    {
        var picked = list.SelectedItems.Count > 0 ? (IssueChoice)list.SelectedItems[0].Tag! : null;
        var typed = search.Text.Trim().ToUpperInvariant();
        var key = picked?.Key ?? (TaskLabel.ValidIssue(typed) ? typed : "");
        if (choice == Disposition.Billable && key.Length == 0 && client.Text.Trim().Length == 0)
        {
            status.Text = "Pick an issue, type a key such as BC-123, or use Internal or Exclude.";
            search.Focus();
            return;
        }
        Result = initial with { Client = client.Text.Trim(), Issue = key, Description = description.Text.Trim() };
        Choice = choice == Disposition.Billable && !TaskLabel.ValidIssue(key) ? Disposition.Unassigned : choice;
        DialogResult = DialogResult.OK;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        debounce.Dispose();
        pending?.Cancel();
        pending?.Dispose();
        catalog?.Dispose();
        base.OnFormClosed(e);
    }
}
