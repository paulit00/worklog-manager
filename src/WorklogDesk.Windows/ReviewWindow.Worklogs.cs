using System.Text;
using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal sealed partial class ReviewWindow
{
    /// <summary>Test seam for Yes/No confirmations in the worklog flow; null shows the normal dialog.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<string, bool>? ConfirmOverride { get; set; }

    /// <summary>Test seam for clipboard writes; null writes to the Windows clipboard.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Action<string>? ClipboardOverride { get; set; }

    private void SetClipboard(string text)
    {
        if (ClipboardOverride != null)
            ClipboardOverride(text);
        else
            Clipboard.SetText(text);
    }

    // Single fields of a line that was already handed off, for pasting into individual Tempo form fields.
    // Only handed-off lines qualify, so the duplicate guard still sees every first copy.
    internal void CopyField(string field)
    {
        if (tabs.SelectedIndex != 1 || WorklogAt(worklogs.CurrentRow?.Index ?? -1) is not Worklog line)
            return;
        if (!WasHandedOff(line) && !correctionCopies.Contains(line.ApprovalKey(Mode)))
        {
            Notify("Copy the whole line first with Ctrl+C. Single fields are available after that.");
            return;
        }
        var value = field switch
        {
            "issue" => line.Issue,
            "duration" => WorklogCsv.TempoDuration(line.ProposedSeconds),
            "start" => LocalStart(line.Start),
            _ => line.Description
        };
        try
        {
            SetClipboard(value);
            Notify($"Copied {field}: {(value.Length > 60 ? value[..60] + "…" : value)}");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Notify("The clipboard is busy. Try again.");
        }
    }

    private void Export()
    {
        var day = Day;
        var mode = Mode;
        var approved = ApprovedWorklogs();
        if (approved.Count == 0)
        {
            Notify("No new, fully approved worklogs. Review drafts and previously entered lines in Worklogs.");
            return;
        }
        var skipped = preview.Count - approved.Count;
        if (!ConfirmHandoff(approved, skipped, "Export"))
            return;
        using var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"worklog-{Day:yyyy-MM-dd}.csv",
            Title = $"Export {rounding.Text} preview" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        var description = MessageBox.Show(this, "Include worklog descriptions in the exported file?",
            "Export options", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (description == DialogResult.Cancel)
            return;
        if (!VerifyUnchanged(day, mode, approved, newHandoff: true))
            return;
        try
        {
            WriteCsv(dialog.FileName, WorklogCsv.Export(day, approved, app.Zone, description == DialogResult.Yes), day, approved);
            Reload();
            Notify($"Exported {approved.Count} approved worklog(s). After entering them in Tempo, select them and choose Mark entered.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Notify(exception.Message);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            Notify("Export stopped because the local handoff record could not be saved.");
        }
    }

    internal void WriteCsv(string path, string csv, DateOnly day, IReadOnlyList<Worklog> lines)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, csv, new UTF8Encoding(true));
            var prior = store.Exports(day);
            var priorKeys = store.HandoffKeys(day);
            store.RecordExport(day, lines);
            store.RecordHandoffKeys(day, lines.Select(line => line.ApprovalKey(Mode)));
            try { File.Move(temporary, path, overwrite: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(temporary))
                {
                    store.RestoreExportReceipts(day, prior);
                    store.RestoreHandoffKeys(day, priorKeys);
                }
                throw;
            }
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal bool VerifyUnchanged(DateOnly day, RoundingMode mode, IReadOnlyList<Worklog> expected, bool newHandoff)
    {
        if (day == Day && mode == Mode)
        {
            Reload();
            var eligible = newHandoff ? ApprovedWorklogs() :
                preview.Where(line => line.AllocationIds.All(approvedIds.Contains)).ToArray();
            var current = eligible.Select(line => line.ApprovalKey(mode)).ToHashSet(StringComparer.Ordinal);
            if (expected.All(line => current.Contains(line.ApprovalKey(mode))))
                return true;
        }
        Notify("Work changed while the dialog was open. Review the current lines before continuing.");
        return false;
    }

    private bool Ask(string text, string title, MessageBoxIcon icon) =>
        ConfirmOverride?.Invoke(text) ?? MessageBox.Show(this, text, title, MessageBoxButtons.YesNo, icon) == DialogResult.Yes;

    private bool ConfirmHandoff(IReadOnlyList<Worklog> lines, int skipped, string action)
    {
        var history = store.Exports(Day);
        var repeated = lines.Count(line => history.Any(item =>
            item.Intervals.Any(interval => line.Intervals.Any(interval.Overlaps))));
        var open = OpenTimeWarning();
        if (skipped == 0 && repeated == 0 && open == null)
            return true;
        var summary = $"{action} {lines.Count} worklog(s) using {rounding.Text.ToLowerInvariant()}?";
        if (skipped > 0)
            summary += $"\n{skipped} draft, entered or reconciliation line(s) will be left out.";
        if (repeated > 0)
            summary += $"\n{repeated} line(s) overlap an earlier copy or export attempt. Check Tempo before re-entering them.";
        if (open != null)
            summary += "\n" + open;
        return Ask(summary, $"Review {action.ToLowerInvariant()}",
            repeated > 0 || open != null ? MessageBoxIcon.Warning : MessageBoxIcon.Question);
    }

    // The day is not settled while active time is unassigned or uncertain, or the manual timer runs.
    // Assigning that time later can change issue/day totals, so the handoff warns instead of staying silent.
    private string? OpenTimeWarning()
    {
        var unresolved = rows.Where(row => row.Status == Disposition.Unassigned && row.Kind != SpanKind.Idle)
            .Sum(row => row.Milliseconds);
        var timer = Day == app.Today && app.RunningManual;
        if (unresolved < 60_000 && !timer)
            return null;
        var parts = new List<string>();
        if (unresolved >= 60_000)
            parts.Add($"{Duration(unresolved)} of activity is still unassigned or uncertain");
        if (timer)
            parts.Add("the manual timer is still running");
        return $"Note: {string.Join(" and ", parts)}. Assigning it later can change these totals.";
    }

    private void CopyWorklog()
    {
        var day = Day;
        var mode = Mode;
        if (tabs.SelectedIndex == 1 && worklogs.SelectedRows.Count > 1)
        {
            CopyWorklogs(day, mode);
            return;
        }
        if (tabs.SelectedIndex != 1 || WorklogAt(worklogs.CurrentRow?.Index ?? -1) is not Worklog line ||
            !line.AllocationIds.All(approvedIds.Contains))
        {
            tabs.SelectedIndex = 1;
            Notify("Select an approved line in Worklogs.");
            return;
        }
        var coverage = coverages[line];
        if (coverage.NeedsReview)
        {
            Notify(ReconciliationHint(line));
            return;
        }
        var adjustment = coverage.Difference(line);
        if (adjustment == 0 && coverage.Consolidated == null &&
            !coverage.DescriptionChanged(line) && !coverage.StartChanged(line))
        {
            Notify("This worklog is already recorded as entered in Tempo.");
            return;
        }
        try
        {
            if (coverage.Consolidated is { } prior)
            {
                var history = string.Join("\r\n", prior.Select(item =>
                    $"{(item.Start is { } start ? LocalStart(start) : "Start unknown")} · {item.Seconds / 60}m"));
                if (CopyCorrection(day, mode, line, $"{line.Issue} · {Day:yyyy-MM-dd}\r\nConsolidate these existing Tempo lines:\r\n{history}\r\n\r\n" +
                    $"One target entry: {LocalStart(line.Start)} · {line.ProposedSeconds / 60}m\r\nDescription: {line.Description}"))
                    Notify("Copied the consolidation. Verify the existing Tempo lines were replaced, then press Ctrl+M.");
            }
            else if (adjustment is { } seconds)
            {
                var change = seconds == 0 ? "Duration unchanged." :
                    $"Adjustment: {(seconds > 0 ? "+" : "−")}{Math.Abs(seconds) / 60}m";
                var start = coverage.StartChanged(line)
                    ? $"Revised start: {TimeZoneInfo.ConvertTime(line.Start, app.Zone):HH:mm:ss}\r\n" : "";
                if (CopyCorrection(day, mode, line, $"{line.Issue} · {Day:yyyy-MM-dd}\r\n" +
                    $"Current in Tempo: {coverage.Entry!.Seconds / 60}m\r\nTarget: {line.ProposedSeconds / 60}m\r\n" +
                    $"{change}\r\n{start}Description: {line.Description}"))
                    Notify("Copied the adjustment. Apply it in Tempo, then press Ctrl+M to record it.");
            }
            else
            {
                if (!ConfirmHandoff([line], 0, "Copy"))
                    return;
                if (!VerifyUnchanged(day, mode, [line], newHandoff: true))
                    return;
                HandOff(day, [line]);
                Notify("Copied issue, date, start, duration and description.");
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            Notify("Copy stopped because the local handoff record could not be saved.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Notify("The clipboard is busy. Try again.");
        }
    }

    // A correction is keyed by the exact line it targets. A repeat copy of the same correction warns, because
    // applying an adjustment twice in Tempo double-counts it. A changed line gets a new key and a fresh copy.
    private bool CopyCorrection(DateOnly day, RoundingMode mode, Worklog line, string text)
    {
        var key = line.ApprovalKey(mode);
        var prior = store.CorrectionCopies(day);
        if (prior.Contains(key, StringComparer.Ordinal) && !Ask(
            "This correction was already copied. Check Tempo first: applying it twice double-counts the change.\n\nCopy it again?",
            "Repeat correction", MessageBoxIcon.Warning))
            return false;
        if (!VerifyUnchanged(day, mode, [line], newHandoff: false))
            return false;
        store.RecordCorrectionCopy(day, key);
        try
        {
            SetClipboard(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            store.RestoreCorrectionCopies(day, prior);
            throw;
        }
        finally
        {
            Reload();
        }
        return true;
    }

    private void CopyWorklogs(DateOnly day, RoundingMode mode)
    {
        var selected = worklogs.SelectedRows.Cast<DataGridViewRow>().OrderBy(row => row.Index)
            .Select(row => WorklogAt(row.Index)).OfType<Worklog>().ToArray();
        var ready = ApprovedWorklogs().ToHashSet();
        if (selected.Length == 0 || !selected.All(ready.Contains))
        {
            Notify("Multi-line copy takes new, approved lines only. Copy adjustments one at a time.");
            return;
        }
        if (!ConfirmHandoff(selected, 0, "Copy") || !VerifyUnchanged(day, mode, selected, newHandoff: true))
            return;
        try
        {
            HandOff(day, selected);
            Notify($"Copied {selected.Length} worklog(s), one per line.");
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            Notify("Copy stopped because the local handoff record could not be saved.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Notify("The clipboard is busy. Try again.");
        }
    }

    // Records the handoff before writing the clipboard, so a lost record can never hide a real copy.
    // Restores the previous record if the clipboard rejects the text, so no false repeat warning remains.
    private void HandOff(DateOnly day, IReadOnlyList<Worklog> lines)
    {
        var prior = store.Exports(day);
        var priorKeys = store.HandoffKeys(day);
        store.RecordExport(day, lines);
        store.RecordHandoffKeys(day, lines.Select(line => line.ApprovalKey(Mode)));
        try
        {
            SetClipboard(string.Join("\r\n", lines.Select(line =>
                $"{line.Issue}\t{day:yyyy-MM-dd}\t{LocalStart(line.Start)}\t{WorklogCsv.TempoDuration(line.ProposedSeconds)}\t{line.Description}")));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            store.RestoreExportReceipts(day, prior);
            store.RestoreHandoffKeys(day, priorKeys);
            throw;
        }
        finally
        {
            Reload();
        }
    }

    internal IReadOnlyList<Worklog> ApprovedWorklogs()
    {
        return preview.Where(line => line.AllocationIds.All(approvedIds.Contains) &&
            coverages[line] is { Entry: null, NeedsReview: false, Consolidated: null }).ToArray();
    }

    private string WorklogState(Worklog line)
    {
        var state = BaseWorklogState(line);
        return state.StartsWith("Adjustment", StringComparison.Ordinal) || state.StartsWith("Merge", StringComparison.Ordinal) ||
            state.StartsWith("Update", StringComparison.Ordinal)
            ? correctionCopies.Contains(line.ApprovalKey(Mode)) ? state + " · copied, mark entered" : state
            : state;
    }

    private string BaseWorklogState(Worklog line)
    {
        var coverage = coverages[line];
        var needsApproval = !line.AllocationIds.All(approvedIds.Contains);
        if (coverage.NeedsReview)
            return "Reconcile prior entry";
        if (coverage.Consolidated is { } entries)
        {
            var netChange = coverage.Difference(line) ?? 0;
            var delta = netChange == 0 ? "same total" : $"{(netChange < 0 ? "−" : "+")}{Math.Abs(netChange) / 60}m";
            return WithApproval($"Merge {entries.Count} entries · {delta}");
        }
        if (coverage.Difference(line) is { } seconds)
        {
            if (seconds != 0)
                return WithApproval($"Adjustment {(seconds > 0 ? "+" : "−")}{Math.Abs(seconds) / 60}m");
            if (coverage.StartChanged(line))
                return WithApproval(coverage.DescriptionChanged(line) ? "Update start and description" : "Update start");
            return coverage.DescriptionChanged(line) ? WithApproval("Update description") : $"Entered {coverage.Entry!.Seconds / 60}m";
        }
        if (line.AllocationIds.All(approvedIds.Contains))
            return !WasHandedOff(line) ? "Approved" : handoffKeys.Contains(line.ApprovalKey(Mode))
                ? "Copied or exported · mark entered" : "Changed since copy · copy again";
        return line.AllocationIds.Any(staleApprovalIds.Contains)
            ? "Reapprove changed line" : "Draft";

        string WithApproval(string state) => needsApproval ? "Approve first · " + state : state;
    }

    private bool WasHandedOff(Worklog line) => handoffs.Any(item =>
        item.Issue == line.Issue && item.Intervals.Any(interval => line.Intervals.Any(interval.Overlaps)));

    private void MarkEntered()
    {
        var day = Day;
        var mode = Mode;
        if (tabs.SelectedIndex == 1 && WorklogAt(worklogs.CurrentRow?.Index ?? -1) is ManualEntry old)
        {
            ManageEntries(old.Id);
            return;
        }
        if (tabs.SelectedIndex != 1)
            return;
        var selected = worklogs.SelectedRows.Cast<DataGridViewRow>().Select(row => WorklogAt(row.Index)).OfType<Worklog>().ToArray();
        if (selected.Length == 0 || selected.Any(line => !line.AllocationIds.All(approvedIds.Contains)))
        {
            Notify("Select one or more fully approved worklogs.");
            return;
        }
        var changed = selected.FirstOrDefault(line => coverages[line].Entry == null && coverages[line].Consolidated == null &&
            WasHandedOff(line) && !handoffKeys.Contains(line.ApprovalKey(Mode)));
        if (changed != null)
        {
            Notify($"{changed.Issue} changed after it was copied or exported (now {WorklogCsv.TempoDuration(changed.ProposedSeconds)}). " +
                "Copy it again and update Tempo before marking it entered.");
            return;
        }
        var records = new List<ManualEntry>();
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        var selectedCoverage = selected.Select(line => coverages[line]).ToArray();
        var batch = selectedCoverage.Any(item => item.NeedsReview)
            ? ManualLedger.ReconcileIssueDayBatch(Mode, selected, entered) : null;
        for (var index = 0; index < selected.Length; index++)
        {
            var line = selected[index];
            var coverage = selectedCoverage[index];
            if (coverage.NeedsReview && batch == null)
            {
                Notify(ReconciliationHint(line));
                return;
            }
            if (coverage.Consolidated is { } prior)
                replaced.UnionWith(prior.Select(item => item.Id));
            var knownId = batch != null ? batch[line.Issue].Id : coverage.Entry?.Id;
            records.Add(new ManualEntry(knownId ?? Guid.NewGuid().ToString("N"), line.Issue, Mode,
                line.ProposedSeconds, line.AllocationIds.ToArray(), line.Start, line.Intervals.ToArray(), line.Description));
        }
        var summary = string.Join("\n", records.Take(6).Select(item =>
            $"{item.Issue} · {LocalStart(item.Start!.Value)} · {item.Seconds / 60}m"));
        if (records.Count > 6)
            summary += $"\nand {records.Count - 6} more selected worklog(s).";
        var instruction = batch != null
            ? "Verify that all selected existing entries have been corrected in Tempo before updating their local records."
            : replaced.Count > 0
            ? "Verify in Tempo that the old entries were replaced by the consolidated entries."
            : "Have you verified these starts, durations and descriptions in Tempo?";
        if (!Ask($"{instruction}\n\n{summary}", "Record manual entry", MessageBoxIcon.Question))
            return;
        if (!VerifyUnchanged(day, mode, selected, newHandoff: false))
            return;
        try
        {
            store.SaveManualEntries(day, records, replaced);
            Reload();
            Notify("Recorded locally. No change was made in Tempo.");
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            Notify("The local marks could not be saved. Review the existing marks before trying again.");
        }
    }

    private string ReconciliationHint(Worklog line)
    {
        var priorIssues = ManualLedger.OverlappingMarks(line, entered).Select(entry => entry.Issue).Distinct().ToArray();
        if (priorIssues.Length > 0)
        {
            var blockedSources = preview.Any(candidate => priorIssues.Contains(candidate.Issue) &&
                ManualLedger.Check(candidate, Mode, preview, entered).NeedsReview);
            return Mode == RoundingMode.IssueDay && blockedSources
                ? $"After correcting the linked entries in Tempo, select {line.Issue} and {string.Join(", ", priorIssues)} together and mark them entered."
                : $"Correct {string.Join(", ", priorIssues)} in Tempo and mark the corrected source before copying this line.";
        }
        if (entered.Any(entry => entry.Issue == line.Issue && entry.Mode != Mode))
            return "Review the existing entries in their original rounding mode.";
        if (Mode == RoundingMode.Session && entered.Any(entry => entry.Issue == line.Issue))
            return "Review the old Tempo session. If it was split, verify removal of the old line before recording the replacement lines.";
        if (entered.FirstOrDefault(entry => ManualLedger.IsUnplacedLegacy(entry, preview)) is { } legacy)
            return $"An older {legacy.Issue} mark has no time range. Open Manage local Tempo entries and choose " +
                "Keep as separate Tempo entry, or record its removal.";
        return "Review the earlier entry in Manage local Tempo entries.";
    }

    private void ManageEntries(string? selectedId = null)
    {
        if (entered.Count == 0)
        {
            Notify("No manual Tempo entries are recorded for this date.");
            return;
        }
        using var dialog = new Form { Text = "Local Tempo entries", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(640, 290), MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false };
        var records = new ListBox { Dock = DockStyle.Fill, AccessibleName = "Local entries", IntegralHeight = false,
            HorizontalScrollbar = true };
        foreach (var item in entered)
        {
            var start = item.Start is { } recordedStart ? TimeZoneInfo.ConvertTime(recordedStart, app.Zone).ToString("HH:mm:ss") : "Start unknown";
            records.Items.Add($"{item.Issue}   ·   {start}   ·   {item.Seconds / 60}m   ·   " +
                $"{(item.Mode == RoundingMode.IssueDay ? "Issue/day" : "Session")}   ·   {item.Description}");
        }
        if (selectedId != null)
            records.SelectedIndex = Array.FindIndex(entered.ToArray(), item => item.Id == selectedId);
        var reset = Ui.Button("Record removal from Tempo", (_, _) =>
        {
            if (records.SelectedIndex < 0)
                return;
            var old = entered[records.SelectedIndex];
            if (MessageBox.Show(dialog,
                $"Verify in Tempo that this entry does not exist before clearing the local mark.\n\n" +
                $"{records.SelectedItem}\n\nIf the entry still exists, use the adjustment and mark it again. " +
                "Clearing a local mark can make its time exportable again. Confirm verified absence?",
                "Confirm removal", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            try
            {
                store.RemoveManualEntry(old.Id);
                dialog.DialogResult = DialogResult.OK;
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                MessageBox.Show(dialog, "The local mark could not be updated. The existing record was kept.",
                    "Local storage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }, 130);
        reset.Dock = DockStyle.Bottom;
        var keep = Ui.Button("Keep as separate Tempo entry", (_, _) =>
        {
            if (records.SelectedIndex < 0)
                return;
            var old = entered[records.SelectedIndex];
            if (MessageBox.Show(dialog,
                $"This mark has no time range and matches no current line, so every line for the day is on hold.\n\n" +
                $"{records.SelectedItem}\n\nConfirm that it is a separate Tempo entry for {old.Issue} that does not " +
                "include time now shown under another issue. Lines for other issues will be released.",
                "Keep separate entry", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            try
            {
                store.SaveManualEntry(Day, ManualLedger.KeepSeparate(old));
                dialog.DialogResult = DialogResult.OK;
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                MessageBox.Show(dialog, "The local mark could not be updated. The existing record was kept.",
                    "Local storage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }, 190);
        keep.Dock = DockStyle.Bottom;
        void UpdateKeep() => keep.Enabled = records.SelectedIndex >= 0 &&
            ManualLedger.IsUnplacedLegacy(entered[records.SelectedIndex], preview);
        records.SelectedIndexChanged += (_, _) => UpdateKeep();
        UpdateKeep();
        dialog.Controls.Add(records);
        dialog.Controls.Add(keep);
        dialog.Controls.Add(reset);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            Reload();
    }

}
