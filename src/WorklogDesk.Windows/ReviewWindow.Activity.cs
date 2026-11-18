using System.Diagnostics;
using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal sealed partial class ReviewWindow
{
    private void EditSelected()
    {
        if (tabs.SelectedIndex == 2)
        {
            EditRequest();
            return;
        }
        var selection = Selected();
        if (selection.Length > 1)
        {
            AssignSelected();
            return;
        }
        var row = selection.Length == 1 ? selection[0] : null;
        if (row == null)
        {
            Notify("Select one activity interval. Turn off Group adjoining to edit part of a group.");
            return;
        }
        using var dialog = new TaskDialog("Edit activity", row.Task, interval: row, zone: app.Zone);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        var edited = row with { Start = dialog.Start, End = dialog.End, Status = dialog.Status,
            Task = dialog.Task, Approved = false };
        var remainders = Timeline.Remainders(row, edited).ToArray();
        Put(edited, remainders);
        if (remainders.Length > 0)
            Notify($"Kept {Duration(remainders.Sum(item => item.Milliseconds))} trimmed from the row as unassigned time.");
    }

    private void AssignSelected()
    {
        var selected = AssignableSelection(out var skipped);
        if (selected.Length == 0)
        {
            Notify("Select activity rows first.");
            return;
        }
        using var dialog = new TaskDialog("Assign selected activity", selected[0].Task, recent: RecentTasks());
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        var disposition = TaskLabel.ValidIssue(dialog.Task.Issue) ? Disposition.Billable : Disposition.Unassigned;
        var updated = selected.Select(row => row with { Status = disposition, Task = dialog.Task, Approved = false }).ToArray();
        Save([], updated);
        ReportSkipped(skipped);
    }

    internal IReadOnlyList<TaskLabel> RecentTasks()
    {
        var pinned = app.CurrentPin is { } pin ? new[] { pin } : [];
        var today = rows.Where(row => TaskLabel.ValidIssue(row.Task.Issue)).OrderByDescending(row => row.End)
            .Select(row => row.Task with { Link = "" });
        var mapped = (app.Settings.Mappings ?? []).Select(mapping => mapping.Task).Where(task => TaskLabel.ValidIssue(task.Issue));
        return pinned.Concat(today).Concat(mapped)
            .DistinctBy(task => (task.Issue, task.Client.ToUpperInvariant(), task.Description)).Take(12).ToArray();
    }

    private Allocation[] AssignableSelection(out int skipped)
    {
        var selected = Selected();
        var eligible = selected.Length == 1 ? selected :
            selected.Where(row => row.Kind is not (SpanKind.Uncertain or SpanKind.Idle) &&
                row.Status != Disposition.Excluded).ToArray();
        skipped = selected.Length - eligible.Length;
        if (eligible.Length == 0 && skipped > 0)
            Notify("Review idle, excluded or uncertain rows individually.");
        return eligible;
    }

    private void ReportSkipped(int skipped)
    {
        if (skipped > 0)
            Notify($"Left {skipped} idle, excluded or uncertain row(s) unchanged.");
    }

    private void ShowClassifier()
    {
        if (Selected().Length == 0)
        {
            Notify("Select activity rows first.");
            return;
        }
        using var dialog = new Form { Text = "Classify selected activity", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(332, 66), FormBorderStyle = FormBorderStyle.FixedDialog, ShowInTaskbar = false,
            MinimizeBox = false, MaximizeBox = false };
        var buttons = Flow();
        var disposition = Disposition.Unassigned;
        void Choose(Disposition value) { disposition = value; dialog.DialogResult = DialogResult.OK; }
        var cancel = Ui.Button("Cancel", (_, _) => dialog.Close(), 90);
        cancel.DialogResult = DialogResult.Cancel;
        dialog.CancelButton = cancel;
        buttons.Controls.AddRange([Ui.Button("Internal", (_, _) => Choose(Disposition.Internal), 93),
            Ui.Button("Exclude", (_, _) => Choose(Disposition.Excluded), 91),
            cancel]);
        dialog.Controls.Add(buttons);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            ClassifySelected(disposition);
    }

    private void ClassifySelected(Disposition disposition)
    {
        var selected = Selected();
        if (selected.Length == 0)
            return;
        if ((selected.Any(row => row.Approved) || disposition == Disposition.Internal && selected.Any(row => row.Kind == SpanKind.Uncertain))
            && MessageBox.Show(this, "This will change approved or unresolved time. Continue?",
                "Confirm classification", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        Save([], selected.Select(row => row with { Status = disposition, Approved = false }).ToArray());
    }

    private void FindIssue()
    {
        var selected = AssignableSelection(out var skipped);
        var draft = tabs.SelectedIndex == 2 ? requests.CurrentRow?.Tag as RequestDraft : null;
        if (draft == null && (tabs.SelectedIndex != 0 || selected.Length == 0))
        {
            Notify("Select activity or a saved request before finding an issue.");
            return;
        }
        try
        {
            using var catalog = app.OpenCatalog();
            var initial = draft?.Task.Issue ?? selected.FirstOrDefault()?.Task.Issue ?? "";
            using var dialog = new JiraIssueDialog(catalog, initial);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedIssue is not { } issue)
                return;
            if (draft != null)
            {
                store.SaveRequest(draft with { Task = draft.Task with { Issue = issue.Key } });
                Reload();
                Notify($"Linked request to {issue.Key}.");
            }
            else
            {
                Save([], selected.Select(row => row with
                {
                    Status = Disposition.Billable, Approved = false,
                    Task = row.Task with
                    {
                        Issue = issue.Key,
                        Description = row.Task.Description.Length == 0 ? issue.Summary : row.Task.Description
                    }
                }).ToArray());
                ReportSkipped(skipped);
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            Notify(error.Message);
        }
    }

    private void OpenJiraForm()
    {
        if (requests.CurrentRow?.Tag is not RequestDraft draft || app.Settings.Jira is not { } config || app.IsDemo)
        {
            Notify("Select a request and connect Jira Cloud to open its create form.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(new Uri(new Uri(config.Site), "secure/CreateIssue!default.jspa").AbsoluteUri)
            {
                UseShellExecute = true
            });
            Notify($"Create the ticket in Jira, then link its key to the saved request.");
            if (MessageBox.Show(this, "Copy this request summary to your clipboard?", "Jira draft",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                Clipboard.SetText(draft.Task.Description);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        {
            Notify("Could not open Jira or access the clipboard.");
        }
    }

    private void ApproveSelected() => ApproveRows(Selected());

    private void ApproveWorklogs()
    {
        if (tabs.SelectedIndex != 1)
            return;
        var ids = worklogs.SelectedRows.Cast<DataGridViewRow>().Select(row => WorklogAt(row.Index)).OfType<Worklog>()
            .SelectMany(line => line.AllocationIds).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            Notify("Select one or more proposed worklogs.");
            return;
        }
        ApproveRows(rows.Where(row => ids.Contains(row.Id)).ToArray());
    }

    private void ApproveRows(Allocation[] selected)
    {
        if (selected.Length == 0 || selected.Any(row => row.Status != Disposition.Billable || !approvalKeys.ContainsKey(row.Id)))
        {
            Notify("Select activity already assigned to a Jira issue.");
            return;
        }
        var selectionIds = selected.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var undescribed = preview.Where(log => log.AllocationIds.Any(selectionIds.Contains) &&
            string.IsNullOrWhiteSpace(log.Description)).Select(log => log.Issue).Distinct().ToArray();
        if (undescribed.Length > 0)
        {
            Notify($"Add a description to at least one row of {string.Join(", ", undescribed)} before approving.");
            return;
        }
        var shortGroups = preview.Where(log => log.ActualMilliseconds < 120_000 &&
            log.AllocationIds.Any(selectionIds.Contains)).Select(log => log.Issue).Distinct().ToArray();
        if (shortGroups.Length > 0 && MessageBox.Show(this,
            $"These worklogs contain less than two minutes each and may round to 15 minutes: {string.Join(", ", shortGroups)}.\n\nApprove the selection?",
            "Confirm short work", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            Notify("Approval cancelled.");
            return;
        }
        Save([], selected.Select(row => row with { Approved = true, ApprovedMode = Mode, ApprovedKey = approvalKeys[row.Id] }).ToArray());
    }

    private void AddTime()
    {
        var begin = Timeline.DayStart(Day, app.Zone).AddHours(9);
        var row = new Allocation(Guid.NewGuid().ToString("N"), Day, begin, begin.AddMinutes(15),
            "Manual entry", SpanKind.Manual, Disposition.Unassigned, new());
        using var dialog = new TaskDialog("Add time", row.Task, interval: row, zone: app.Zone);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            Put(row with { Start = dialog.Start, End = dialog.End, Status = dialog.Status, Task = dialog.Task });
    }

    private void Put(Allocation row, IReadOnlyList<Allocation>? remainders = null)
    {
        var changes = new List<Allocation> { row };
        changes.AddRange(remainders ?? []);
        var overlaps = rows.Where(other => other.Id != row.Id && Timeline.Intersects(row, other)).ToArray();
        if (overlaps.Length > 0 && MessageBox.Show(this,
            $"Replace the overlapping part of {overlaps.Length} existing row(s)? Remaining time will stay in the day.",
            "Review overlap", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        foreach (var existing in overlaps)
        {
            if (existing.Start < row.Start)
                changes.Add(existing with { Id = Guid.NewGuid().ToString("N"), End = row.Start, Approved = false });
            if (existing.End > row.End)
                changes.Add(existing with { Id = Guid.NewGuid().ToString("N"), Start = row.End, Approved = false });
        }
        Save(overlaps.Select(overlap => overlap.Id).ToArray(), changes);
    }

    private void SplitSelected()
    {
        var selection = Selected();
        var row = selection.Length == 1 ? selection[0] : null;
        if (row == null || row.Milliseconds < 2_000)
        {
            Notify("Select an interval at least two seconds long.");
            return;
        }
        using var dialog = new SplitDialog(row, app.Zone);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        var midpoint = dialog.At;
        Save([row.Id], [
            row with { Id = Guid.NewGuid().ToString("N"), End = midpoint, Approved = false },
            row with { Id = Guid.NewGuid().ToString("N"), Start = midpoint, Approved = false }
        ]);
    }

    private void MergeSelected()
    {
        var selected = Selected();
        if (selected.Length < 2 || selected.Zip(selected.Skip(1)).Any(pair =>
            pair.First.End != pair.Second.Start || pair.First.Task != pair.Second.Task ||
            pair.First.Status != pair.Second.Status || pair.First.Kind == SpanKind.Uncertain || pair.Second.Kind == SpanKind.Uncertain))
        {
            Notify("Select adjoining rows with the same task and classification.");
            return;
        }
        var first = selected[0];
        var merged = first with { Id = Guid.NewGuid().ToString("N"), End = selected[^1].End,
            App = selected.Select(row => row.App).Distinct().Count() > 1 ? "Multiple apps" : first.App, Approved = false };
        Save(selected.Select(row => row.Id).ToArray(), [merged]);
    }

    private void EditRequest()
    {
        if (requests.CurrentRow?.Tag is not RequestDraft draft)
        {
            Notify("Select a request draft first.");
            return;
        }
        using var dialog = new TaskDialog("Edit request or link Jira issue", draft.Task, requireDescription: true);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            store.SaveRequest(draft with { Task = dialog.Task });
            Reload();
        }
    }

}
