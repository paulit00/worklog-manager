using System.Diagnostics;
using System.Text.Json;
using Microsoft.Identity.Client;
using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal sealed class AssistedReviewDialog : Form
{
    private readonly TrayApplication app;
    private readonly DateOnly day;
    private readonly EvidenceGroup[] groups;
    private readonly Func<(EvidenceGroup Group, TaskLabel Task)[], string?> apply;
    private readonly CancellationTokenSource closing = new();
    private readonly Dictionary<string, AssistedProposal> proposals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Uri> teams = new(StringComparer.Ordinal);
    private readonly DataGridView queue = Ui.Grid();
    private readonly ListBox candidates = new() { Dock = DockStyle.Fill, IntegralHeight = false, DisplayMember = nameof(IssueCandidate.Display), AccessibleName = "Jira candidates" };
    private readonly TextBox jiraTerms = new() { Width = 224, MaxLength = 120, PlaceholderText = "Jira key or keywords", AccessibleName = "Jira search terms" };
    private readonly ComboBox provider = new() { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "AI provider" };
    private readonly TextBox model = new() { Width = 125, MaxLength = 80, AccessibleName = "AI model" };
    private readonly CheckBox windowText = new() { Text = "Include window text", AutoSize = true };
    private readonly CheckBox subjects = new() { Text = "Include Outlook subjects", AutoSize = true };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(1_040, 0) };
    private readonly TextBox evidence = new() { ReadOnly = true, Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, AccessibleName = "Selected proposal evidence" };
    private readonly List<Control> actions = [];
    private JiraIssue[] searched = [];
    private SentEvidence[] sent = [];
    private bool busy;

    private sealed record IssueCandidate(JiraIssue Issue)
    {
        public string Display => $"{Issue.Key} · {Issue.Summary}".TrimEnd(' ', '·');
    }

    public AssistedReviewDialog(TrayApplication app, DateOnly day, IReadOnlyList<Allocation> rows,
        Func<(EvidenceGroup Group, TaskLabel Task)[], string?> apply)
    {
        this.app = app;
        this.day = day;
        this.apply = apply;
        groups = AssistedEvidence.Group(rows);
        CompletionReminderDialog.NameForm(this, $"Suggestions · {day:yyyy-MM-dd}", new Size(1_120, 670));
        MinimumSize = new Size(840, 560);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 69));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        AddFull(status, 0);
        AddFull(FinishWorklogWindow.Bar(jiraTerms, Button("Find Jira candidates", (_, _) => Run(SearchJira), 132),
            provider, Button("API key…", (_, _) => EditKey(), 85), model, Button("Preview AI request…", (_, _) => Run(AskAi), 139),
            windowText, subjects, Button("Outlook…", (_, _) => Run(ReadOutlook), 86)), 1);
        queue.AccessibleName = "Unresolved activity suggestions";
        queue.Columns.Add("time", "Recorded time");
        queue.Columns.Add("source", "Source");
        queue.Columns.Add("proposal", "Proposed action");
        queue.Columns[2].FillWeight = 200;
        queue.SelectionChanged += (_, _) => ShowEvidence();
        layout.Controls.Add(queue, 0, 2);
        layout.Controls.Add(candidates, 1, 2);
        AddFull(evidence, 3);
        AddFull(FinishWorklogWindow.Bar(Button("Use Jira candidate", (_, _) => ChooseCandidate(), 128),
            Button("Apply selected assignments", (_, _) => ApplySelected(), 167), Button("Open Jira draft…", (_, _) => Draft(), 122),
            Button("Attach Teams link…", (_, _) => AttachTeams(), 135), Button("Open Teams link", (_, _) => OpenTeams(), 119),
            Ui.Button("Close", (_, _) => Close(), 80)), 4);
        provider.Items.AddRange([AiService.Claude, AiService.OpenAI]);
        provider.SelectedIndexChanged += (_, _) => model.Text = Service.DefaultModel;
        // Start with the provider that already has a key; Claude when neither or both do.
        provider.SelectedItem = !HasKey(AiService.Claude) && HasKey(AiService.OpenAI) ? AiService.OpenAI : AiService.Claude;
        RefreshCandidates();
        foreach (var group in groups)
        {
            if (group.Nearby is { } nearby && group.Assignable)
                proposals[group.Id] = new(group.Id, "assign", nearby.Issue, "", "Nearby recorded activity on this Jira issue.");
            var index = queue.Rows.Add($"{Local(group.Start):HH:mm}–{Local(group.End):HH:mm} · {group.Seconds}s", group.Source, "");
            queue.Rows[index].Tag = group;
        }
        RefreshQueue();
        status.Text = groups.Length == 0 ? "No unresolved recorded activity on this date." :
            "Select activity to review its evidence. Jira search sends the terms you type. AI requests require preview and confirmation.";

        void AddFull(Control control, int row)
        {
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 2);
        }
    }

    private AiService Service => provider.SelectedItem as AiService ?? AiService.Claude;

    private static bool HasKey(AiService service)
    {
        try { return !string.IsNullOrWhiteSpace(CredentialVault.Read(service.CredentialTarget)); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private DateTimeOffset Local(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, app.Zone);
    private EvidenceGroup[] Selection() => queue.SelectedRows.Cast<DataGridViewRow>().OrderBy(row => row.Index)
        .Select(row => row.Tag).OfType<EvidenceGroup>().ToArray();

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
        try { await operation(closing.Token); }
        catch (OperationCanceledException) when (IsDisposed || closing.IsCancellationRequested) { }
        catch (OperationCanceledException) { status.Text = "The request timed out. Existing suggestions are still available."; }
        catch (MsalException) { status.Text = "Outlook sign-in failed. Check your tenant's app registration and consent."; }
        catch (HttpRequestException) { status.Text = "The service could not be reached. Existing suggestions are still available."; }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or
            System.IO.InvalidDataException or JsonException or KeyNotFoundException)
        {
            if (!IsDisposed)
                status.Text = error is InvalidOperationException or ArgumentException ? error.Message : "The service returned an unreadable result.";
        }
        finally
        {
            busy = false;
            if (!IsDisposed)
                foreach (var control in actions)
                    control.Enabled = true;
        }
    }

    private async Task SearchJira(CancellationToken cancel)
    {
        var terms = jiraTerms.Text.Trim();
        if (terms.Length == 0)
            throw new ArgumentException("Enter a Jira issue key or keywords.");
        status.Text = "Searching configured Jira projects…";
        using var catalog = app.OpenCatalog();
        var result = catalog == null ? DemoSearch(terms) : await catalog.SearchAssistedAsync(terms, null, cancel);
        if (IsDisposed || cancel.IsCancellationRequested)
            return;
        searched = result.Issues.ToArray();
        RefreshCandidates();
        status.Text = $"Jira returned {searched.Length} candidate(s)." +
            (result.Next != null || searched.Length == 20 ? " More results may exist. Refine your search." : "");
    }

    private JiraResults DemoSearch(string terms) => new(groups.Select(group => group.Nearby).OfType<TaskLabel>()
        .Where(task => task.Issue.Contains(terms, StringComparison.OrdinalIgnoreCase) || task.Description.Contains(terms, StringComparison.OrdinalIgnoreCase))
        .Select(task => new JiraIssue(task.Issue, task.Description, "Demo")).DistinctBy(issue => issue.Key).Take(20).ToArray(), null);

    private JiraIssue[] VisibleCandidates(EvidenceGroup[] selected)
    {
        var keys = selected.Select(group => group.Nearby?.Issue).OfType<string>().Distinct(StringComparer.Ordinal);
        var issueByKey = searched.ToDictionary(issue => issue.Key, StringComparer.Ordinal);
        return keys.Select(key => issueByKey.GetValueOrDefault(key) ?? new JiraIssue(key, "", "Local activity"))
            .Concat(searched).DistinctBy(issue => issue.Key).Take(20).ToArray();
    }

    private void RefreshCandidates()
    {
        candidates.Items.Clear();
        candidates.Items.AddRange(VisibleCandidates(groups).Select(issue => new IssueCandidate(issue)).ToArray());
    }

    private void ChooseCandidate()
    {
        if (candidates.SelectedItem is not IssueCandidate choice || Selection() is not { Length: > 0 } selected || selected.Any(group => !group.Assignable))
        {
            status.Text = "Select a Jira candidate and assignable activity. Resolve uncertain timing in Activity first.";
            return;
        }
        foreach (var group in selected)
            proposals[group.Id] = new(group.Id, "assign", choice.Issue.Key, "", "Jira candidate you selected.");
        RefreshQueue();
    }

    private async Task AskAi(CancellationToken cancel)
    {
        if (app.IsDemo)
        {
            status.Text = "Demo uses synthetic evidence. API requests run only in a personal review session.";
            return;
        }
        var selected = Selection();
        var keys = VisibleCandidates(selected);
        var service = Service;
        var body = AssistedInference.BuildRequest(service, model.Text.Trim(), selected, keys, windowText.Checked, subjects.Checked, sent);
        using var preview = new Form();
        CompletionReminderDialog.NameForm(preview, $"Preview request to {service.Name}", new Size(750, 520));
        var payload = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            Text = body, Dock = DockStyle.Fill, AccessibleName = "Exact request body" };
        var controls = FinishWorklogWindow.Bar(Ui.Button($"Send to {service.Name}", (_, _) => preview.DialogResult = DialogResult.OK, 126),
            Ui.Button("Cancel", (_, _) => preview.DialogResult = DialogResult.Cancel, 85));
        controls.Dock = DockStyle.Bottom;
        preview.Controls.Add(payload);
        preview.Controls.Add(controls);
        preview.Controls.Add(new Label { Text = "These fields leave this PC. Provider data controls apply.", Dock = DockStyle.Top, Height = 25 });
        // Opt-ins apply to one request; the next request starts without window text or subjects.
        windowText.Checked = subjects.Checked = false;
        if (preview.ShowDialog(this) != DialogResult.OK)
            return;
        var key = CredentialVault.Read(service.CredentialTarget);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"Set your {service.Name} API key using API key…");
        status.Text = $"Requesting suggestions from {service.Name}…";
        using var client = AssistedInference.SecureClient(120);
        var result = await new AssistedInference(client).SuggestAsync(service, body, key, selected, keys, cancel);
        if (IsDisposed || cancel.IsCancellationRequested)
            return;
        foreach (var proposal in result)
            proposals[proposal.EvidenceId] = proposal;
        RefreshQueue();
        status.Text = $"Received {result.Length} proposal(s). Review the evidence before applying an assignment.";
    }

    private void ApplySelected()
    {
        var chosen = Selection();
        if (chosen.Length == 0 || chosen.Any(group => !group.Assignable || proposals.GetValueOrDefault(group.Id)?.Action != "assign"))
        {
            status.Text = "Select activity with an assignment proposal first.";
            return;
        }
        var assignments = chosen.Select(group =>
        {
            var proposal = proposals[group.Id];
            var candidate = searched.FirstOrDefault(issue => issue.Key == proposal.Issue);
            var label = group.Nearby is { } nearby && nearby.Issue == proposal.Issue ? nearby : new TaskLabel("", proposal.Issue, candidate?.Summary ?? "");
            return (Group: group, Task: label);
        }).ToArray();
        var list = string.Join("\n", assignments.GroupBy(item => item.Task.Issue).Select(items =>
            $"{items.Key} · {items.Sum(item => item.Group.Seconds)} recorded seconds"));
        if (MessageBox.Show(this, list + "\n\nApply these assignments?", "Confirm assignments", MessageBoxButtons.YesNo,
            MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        if (apply(assignments) is { } failure)
        {
            status.Text = failure;
            return;
        }
        foreach (var row in queue.SelectedRows.Cast<DataGridViewRow>().OrderByDescending(row => row.Index).ToArray())
            queue.Rows.Remove(row);
        status.Text = "Assignments saved. Approve worklogs separately. Ctrl+Z in Review can undo this batch.";
    }

    private void Draft()
    {
        if (Selection() is not [{ Assignable: true } group])
        {
            status.Text = "Select one assignable activity group before drafting a Jira task.";
            return;
        }
        var draft = proposals.GetValueOrDefault(group.Id);
        var title = draft?.Action == "draft" ? draft.Title : "";
        if (app.CreateJiraIssue(this, title, draft?.Action == "draft" ? draft.Description : "") is { } issue)
        {
            searched = searched.Prepend(issue).DistinctBy(item => item.Key).Take(20).ToArray();
            RefreshCandidates();
            status.Text = $"{issue.Key} created. Select it as a Jira candidate if you want to assign recorded time.";
        }
    }

    private void AttachTeams()
    {
        if (Selection() is not [var group] || group.Source != "Teams")
        {
            status.Text = "Select one Teams activity group first.";
            return;
        }
        using var field = new TextBox { MaxLength = 2_048, Text = teams.GetValueOrDefault(group.Id)?.AbsoluteUri ?? "" };
        if (!Prompt("Attach Teams source", "Paste a Teams message link. It stays in this Suggestions window.", ("Message link", field)))
            return;
        if (AssistedEvidence.TryTeamsLink(field.Text.Trim(), out var uri))
        {
            teams[group.Id] = uri!;
            ShowEvidence();
            status.Text = "Teams source attached for this session.";
        }
        else
            status.Text = "Paste a valid HTTPS Teams message link.";
    }

    private void OpenTeams()
    {
        if (Selection() is [var group] && teams.TryGetValue(group.Id, out var uri))
        {
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { status.Text = "Windows could not open the Teams link."; }
        }
        else
            status.Text = "Select a group with an attached Teams link.";
    }

    private async Task ReadOutlook(CancellationToken cancel)
    {
        if (app.IsDemo)
        {
            status.Text = "Outlook sign-in is available in a personal review session.";
            return;
        }
        using var tenant = new TextBox { MaxLength = 36 };
        using var client = new TextBox { MaxLength = 36 };
        if (!Prompt("Outlook Sent Items", "Use a tenant-approved Entra public client. This reads the selected day's Sent Items subjects and timestamps.",
            ("Tenant ID", tenant), ("Application ID", client)))
            return;
        if (!Guid.TryParse(tenant.Text, out var tenantId) || !Guid.TryParse(client.Text, out var appId))
            throw new ArgumentException("Enter the tenant and public-client application GUIDs.");
        status.Text = "Signing in to Microsoft Graph…";
        var result = await OutlookEvidence.SignInAsync(tenantId, appId, Handle, day, app.Zone, cancel);
        if (IsDisposed || cancel.IsCancellationRequested)
            return;
        sent = result.Items;
        status.Text = $"Loaded {sent.Length} Sent Items for this session." + (result.Incomplete ? " Outlook results are incomplete." : "");
        ShowEvidence();
    }

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

    private bool Prompt(string title, string explanation, params (string Label, TextBox Field)[] fields)
    {
        using var dialog = new Form();
        CompletionReminderDialog.NameForm(dialog, title, new Size(575, 230));
        var layout = Ui.DialogLayout();
        dialog.Controls.Add(layout);
        Ui.Field(layout, "", new Label { Text = explanation, AutoSize = true, MaximumSize = new Size(370, 0) });
        foreach (var (label, field) in fields)
            Ui.Field(layout, label, field);
        var actions = Ui.Actions(layout);
        var ok = Ui.Button("Continue", (_, _) => dialog.DialogResult = DialogResult.OK, 92);
        var cancel = Ui.Button("Cancel", (_, _) => dialog.DialogResult = DialogResult.Cancel, 90);
        actions.Controls.AddRange([ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        var accepted = dialog.ShowDialog(this) == DialogResult.OK;
        foreach (var (_, field) in fields)
            layout.Controls.Remove(field);
        return accepted;
    }

    private void RefreshQueue()
    {
        foreach (DataGridViewRow row in queue.Rows)
            if (row.Tag is EvidenceGroup group)
                row.Cells[2].Value = !group.Assignable ? "Check timing in Activity" : proposals.GetValueOrDefault(group.Id) switch
                {
                    { Action: "assign" } value => $"Assign to {value.Issue}",
                    { Action: "draft" } value => $"Draft · {value.Title}",
                    _ => "Needs a decision"
                };
        ShowEvidence();
    }

    private void ShowEvidence()
    {
        var group = Selection().FirstOrDefault();
        if (group == null)
        {
            evidence.Text = "Select an activity group to inspect its evidence.";
            return;
        }
        var details = new List<string> { $"{group.Rows.Length} recorded row(s) · {group.Seconds} seconds · {group.Source}" };
        if (proposals.TryGetValue(group.Id, out var proposal))
            details.Add(proposal.Reason);
        details.AddRange(group.Rows.Select(row => row.Context).Where(text => text.Length > 0).Distinct().Take(3));
        if (teams.ContainsKey(group.Id))
            details.Add("Teams source attached · use Open Teams link.");
        if (group.Source == "Outlook")
            details.AddRange(sent.Where(item => item.SentAt >= group.Start.AddMinutes(-15) && item.SentAt <= group.End.AddMinutes(15))
                .Take(5).Select(item => $"Sent {Local(item.SentAt):HH:mm} · {item.Subject}"));
        evidence.Lines = details.ToArray();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        closing.Cancel();
        closing.Dispose();
        base.OnFormClosed(e);
    }
}
