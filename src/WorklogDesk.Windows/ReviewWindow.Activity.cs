using System.Diagnostics;
using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal sealed partial class ReviewWindow
{
    private void EditSelected()
    {
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

    /// <summary>Test seam replacing the assign window; returns the chosen task and disposition, or null to cancel.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<TaskLabel, (TaskLabel, Disposition)?>? AssignOverride { get; set; }

    internal void AssignSelected(string? issue = null)
    {
        var selected = Selected();
        if (selected.Length == 0)
        {
            Notify(tabs.SelectedIndex == 0 ? "Select one or more activity rows first." : "Assign works on the Activity view.");
            return;
        }
        var clients = selected.Select(row => row.Task.Client).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var issues = selected.Select(row => row.Task.Issue).Where(value => value.Length > 0).Distinct().ToArray();
        var descriptions = selected.Select(row => row.Task.Description).Where(value => value.Length > 0).Distinct().ToArray();
        var initial = new TaskLabel(clients.Length == 1 ? clients[0] : "", issue ?? (issues.Length == 1 ? issues[0] : ""),
            descriptions.Length == 1 ? descriptions[0] : "");
        var caption = $"Assign {selected.Length} row(s) · {Duration(selected.Sum(row => row.Milliseconds))}";
        TaskLabel task;
        Disposition choice;
        if (AssignOverride != null)
        {
            if (AssignOverride(initial) is not { } answer)
                return;
            (task, choice) = answer;
        }
        else
        {
            using var dialog = new AssignDialog(caption, initial, IssueChoices(selected), app.JiraIssues, app.OpenCatalog, app.IsDemo,
            createIssue: app.CreateJiraIssue);
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            choice = dialog.Choice;
            task = dialog.Result;
        }
        if (choice is Disposition.Internal or Disposition.Excluded)
        {
            ClassifySelected(choice, selected);
            return;
        }
        // An explicit assignment settles uncertain timing for those rows; idle rows become reviewed passive work.
        Save([], selected.Select(row => row with
        {
            Status = choice, Approved = false,
            Kind = row.Kind == SpanKind.Uncertain ? SpanKind.Foreground : row.Kind,
            Task = task with { Client = task.Client.Length > 0 ? task.Client : row.Task.Client, Link = row.Task.Link }
        }).ToArray(), "assignment");
        Notify(choice == Disposition.Billable
            ? $"Assigned {selected.Length} row(s) to {task.Issue}. Ctrl+Z undoes it."
            : $"Set the client on {selected.Length} row(s). Pick an issue to make them billable.");
    }

    /// <summary>Suggestions for the selection first, then today's issues, the pin and mapped standing issues.</summary>
    private IReadOnlyList<IssueChoice> IssueChoices(IReadOnlyList<Allocation> selected)
    {
        var suggested = selected.Where(row => suggestions.ContainsKey(row.Id)).Select(row => suggestions[row.Id])
            .Select(task => new IssueChoice(task.Issue, task.Description, "Suggested", task.Client, task.Description));
        var recent = RecentTasks().Select(task => new IssueChoice(task.Issue, task.Description,
            task.Client.Length > 0 ? $"Recent · {task.Client}" : "Recent", task.Client, task.Description));
        return suggested.Concat(recent).Where(item => TaskLabel.ValidIssue(item.Key))
            .DistinctBy(item => (item.Key, item.Client.ToUpperInvariant())).ToArray();
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

    /// <summary>
    /// Assigns suggested rows to the issue detected next to them. With a selection that contains suggestions,
    /// only those rows change; otherwise every suggestion for the day is accepted. Approval stays a separate step.
    /// </summary>
    internal void AcceptSuggestions()
    {
        var selectedIds = Selected().Select(row => row.Id).Where(suggestions.ContainsKey).ToHashSet(StringComparer.Ordinal);
        var targets = rows.Where(row => suggestions.ContainsKey(row.Id) &&
            (selectedIds.Count == 0 || selectedIds.Contains(row.Id))).ToArray();
        if (targets.Length == 0)
        {
            Notify("No suggestions. Rows get one when a detected Jira issue is open within 30 minutes of them.");
            return;
        }
        var minutes = Duration(targets.Sum(row => row.Milliseconds));
        Save([], targets.Select(row => row with
        {
            Status = Disposition.Billable,
            Task = suggestions[row.Id] with { Client = row.Task.Client.Length > 0 ? row.Task.Client : suggestions[row.Id].Client },
            Approved = false
        }).ToArray(), "accepting suggestions");
        Notify($"Assigned {targets.Length} row(s), {minutes}, to their suggested issues. Ctrl+Z undoes it.");
    }

    private ReconstructDayDialog? reconstruct;

    /// <summary>Opens "Reconstruct my day" for the shown date. It stays open beside the review; date navigation is locked meanwhile.</summary>
    private void OpenAssistedReview()
    {
        if (TempoBusy)
            return;
        if (reconstruct is { IsDisposed: false })
        {
            reconstruct.Activate();
            return;
        }
        reconstruct = new ReconstructDayDialog(app, store, this, Day);
        reconstruct.FormClosed += (_, _) =>
        {
            reconstruct = null;
            LockNavigation(false);
        };
        LockNavigation(true);
        reconstruct.Show(this);
    }

    RoundingMode IReconstructHost.Mode => Mode;

    string? IReconstructHost.ApplyAssisted(DateOnly date, Allocation[] expected, Allocation[] changes, SegmentGuard[] guards,
        Func<IReadOnlyList<Allocation>, string?> verify)
    {
        if (date != Day || TempoBusy)
            return "The reviewed date or Tempo operation changed. Close and reopen Reconstruct my day.";
        try
        {
            store.ReplaceRows([], changes, expected, guards, verify);
            undo.Push(new UndoStep(date, [], expected, "suggested assignments", changes));
            undoButton.Enabled = true;
            Reload();
            return null;
        }
        catch (InvalidOperationException error) { return error.Message; }
        catch (Microsoft.Data.Sqlite.SqliteException) { return "Local storage could not save the assignment. Try again."; }
    }

    void IReconstructHost.OpenInActivity(DateOnly date, IReadOnlyCollection<string> rowIds, string? issue)
    {
        if (date != Day)
            return;
        Activate();
        tabs.SelectedIndex = 0;
        var ids = rowIds.ToHashSet(StringComparer.Ordinal);
        if (groupActivity.Checked && visibleActivity.Any(group => group.Any(row => ids.Contains(row.Id)) && group.Any(row => !ids.Contains(row.Id))))
            groupActivity.Checked = false;
        activity.ClearSelection();
        DataGridViewRow? first = null;
        for (var index = 0; index < visibleActivity.Length && index < activity.Rows.Count; index++)
            if (visibleActivity[index].Any(row => ids.Contains(row.Id)))
            {
                activity.Rows[index].Selected = true;
                first ??= activity.Rows[index];
            }
        if (first == null || Selected().Length != ids.Count)
        {
            activity.ClearSelection();
            Notify("Those rows aren't visible. Clear the Activity filter and try again.");
            return;
        }
        activity.FirstDisplayedScrollingRowIndex = first.Index;
        AssignSelected(issue);
    }

    private void ClassifySelected(Disposition disposition, Allocation[]? rowsToChange = null)
    {
        var selected = rowsToChange ?? Selected();
        if (selected.Length == 0)
            return;
        if ((selected.Any(row => row.Approved) || disposition == Disposition.Internal && selected.Any(row => row.Kind == SpanKind.Uncertain))
            && MessageBox.Show(this, "This will change approved or unresolved time. Continue?",
                "Confirm classification", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        Save([], selected.Select(row => row with { Status = disposition, Approved = false }).ToArray(),
            disposition == Disposition.Internal ? "marking internal" : "excluding");
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
        Save([], selected.Select(row => row with { Approved = true, ApprovedMode = Mode, ApprovedKey = approvalKeys[row.Id] }).ToArray(),
            "approval");
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


}
