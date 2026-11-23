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
            store.ConfirmHandoffKeys(day, lines.Select(line => line.ApprovalKey(Mode)));
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

    private bool Ask(string text, string title, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1) =>
        ConfirmOverride?.Invoke(text) ?? MessageBox.Show(this, text, title, MessageBoxButtons.YesNo, icon, defaultButton) == DialogResult.Yes;

    private bool ConfirmHandoff(IReadOnlyList<Worklog> lines, int skipped, string action)
    {
        var history = store.Exports(Day);
        var repeated = lines.Count(line => history.Any(item =>
            item.Intervals.Any(interval => line.Intervals.Any(interval.Overlaps))) ||
            completionDay.HasEarlierPending(line, Mode) || completionDay.Attempts.GetValueOrDefault(line.ApprovalKey(Mode))?.State
                == TempoAttemptState.Pending);
        var open = OpenTimeWarning();
        if (skipped == 0 && repeated == 0 && open == null)
            return true;
        var summary = $"{action} {lines.Count} worklog(s) using {rounding.Text.ToLowerInvariant()}?";
        if (skipped > 0)
            summary += $"\n{skipped} draft, entered or reconciliation line(s) will be left out.";
        if (repeated > 0)
            summary += $"\n{repeated} line(s) overlap an earlier handoff or unconfirmed send. Check Tempo before re-entering them.";
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

    /// <summary>Test seam for the Tempo connection.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<ITempoGateway>? TempoOverride { get; set; }

    /// <summary>
    /// Sends approved new lines to Tempo. Reads the day from Tempo first: an identical worklog is recorded, not resent;
    /// unrecorded time on the same issue asks first. A send that may have gone through is held until checked.
    /// </summary>
    internal Task SendToTempo() => RunTempoOperation(SendApprovedToTempo);

    internal Task CheckTempoStatus() => RunTempoOperation(ReconcileTempo);

    internal bool TempoBusy { get; private set; }

    private async Task RunTempoOperation(Func<Task> operation)
    {
        if (TempoBusy)
            return;
        TempoBusy = true;
        var commands = sendTempo.Parent;
        var connections = tempo.Parent;
        if (commands != null)
            commands.Enabled = false;
        if (connections != null)
            connections.Enabled = false;
        rounding.Enabled = false;
        activity.Enabled = worklogs.Enabled = false;
        try { await operation(); }
        finally
        {
            TempoBusy = false;
            if (commands != null)
                commands.Enabled = true;
            if (connections != null)
                connections.Enabled = true;
            rounding.Enabled = true;
            activity.Enabled = worklogs.Enabled = true;
            app.ResumeCompletionNavigation();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (TempoBusy && e.CloseReason == CloseReason.UserClosing)
            e.Cancel = true;
        base.OnFormClosing(e);
    }

    private async Task SendApprovedToTempo()
    {
        var day = Day;
        var mode = Mode;
        if (!app.Settings.Tempo && TempoOverride == null)
        {
            app.ConnectTempo();
            if (!app.Settings.Tempo)
                return;
        }
        var ready = ApprovedWorklogs();
        var picked = worklogs.SelectedRows.Cast<DataGridViewRow>().Select(row => WorklogAt(row.Index)).OfType<Worklog>()
            .Where(ready.Contains).ToArray();
        var lines = picked.Length > 0 ? picked : ready.ToArray();
        if (lines.Length == 0)
        {
            Notify("Nothing new to send. Approve lines first; entered lines and adjustments aren't sent.");
            return;
        }
        var list = string.Join("\n", lines.Take(8).Select(line =>
            $"{line.Issue} · {LocalStart(line.Start)} · {WorklogCsv.TempoDuration(line.ProposedSeconds)} · {Shorten(line.Description)}"));
        if (lines.Length > 8)
            list += $"\nand {lines.Length - 8} more";
        var total = WorklogCsv.TempoDuration(lines.Sum(line => line.ProposedSeconds));
        var open = OpenTimeWarning();
        if (!Ask($"Send {lines.Length} worklog(s), {total}, to Tempo for {day:yyyy-MM-dd}?\n\n{list}" +
            (open == null ? "" : "\n\n" + open), "Send to Tempo", MessageBoxIcon.Question))
            return;
        if (!VerifyUnchanged(day, mode, lines, newHandoff: true))
            return;

        Notify("Sending to Tempo…");
        int sent = 0, matched = 0, held = 0;
        var problems = new List<string>();
        var uncheckedLines = lines.ToHashSet();
        try
        {
            using var gateway = TempoOverride?.Invoke() ?? app.OpenTempo();
            var cancel = CancellationToken.None;
            var account = await gateway.AccountAsync(cancel);
            var existing = (await gateway.DayAsync(account, day, cancel)).ToList();
            var issueIds = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                var key = line.ApprovalKey(mode);
                uncheckedLines.Remove(line);
                long issueId;
                try
                {
                    if (!issueIds.TryGetValue(line.Issue, out issueId))
                        issueIds[line.Issue] = issueId = await gateway.IssueIdAsync(line.Issue, cancel);
                }
                catch (Exception error) when (IsTempoReadError(error))
                {
                    var message = ReadErrorMessage(error);
                    MarkTempoReadFailure(day, mode, [line], message);
                    problems.Add($"{line.Issue}: Tempo status unknown. {message}");
                    continue;
                }
                if (!VerifyUnchanged(day, mode, [line], newHandoff: true))
                    continue;
                var prior = completionDay.Attempts.GetValueOrDefault(key);
                if (completionDay.HasEarlierPending(line, mode))
                {
                    held++;
                    problems.Add($"{line.Issue}: check an earlier unconfirmed worklog before sending.");
                    continue;
                }
                var draft = Draft(day, line, account, issueId);
                var exact = existing.Where(draft.SameAs).ToArray();
                var known = store.ManualEntries(day).Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
                if (exact is [{ } same] && !known.Contains(EntryId(same.Id)))
                {
                    Record(day, mode, line, same.Id, key);
                    matched++;
                    continue;
                }
                if (exact.Length > 0)
                {
                    if (exact.Length > 1)
                        app.CompletionDuplicate(day, exact.Select(item => item.Id));
                    HoldRemoteConflict(day, key, line, mode, prior, "Tempo has multiple matches or a match already used by another line.");
                    held++;
                    problems.Add($"{line.Issue}: matching Tempo time needs reconciliation.");
                    continue;
                }
                if (prior?.State == TempoAttemptState.ReadFailed)
                    store.SetTempoAttempt(day, key, prior.RequiresManualHandoff ? prior with { State = TempoAttemptState.Refused } : null);
                var unrecorded = existing.Where(worklog => worklog.IssueId == issueId && !known.Contains(EntryId(worklog.Id))).ToArray();
                if (unrecorded.Length > 0 && !Ask(
                    $"Tempo already has {WorklogCsv.TempoDuration(unrecorded.Sum(item => item.Seconds))} on {line.Issue} for this day " +
                    "that this app didn't send or record. Sending adds another worklog.\n\nSend this line anyway?",
                    "Existing Tempo time", MessageBoxIcon.Warning))
                {
                    problems.Add($"{line.Issue}: skipped, Tempo already has time on it");
                    continue;
                }
                var earlierPending = prior?.State == TempoAttemptState.Pending;
                if (earlierPending && !Ask(
                    $"An earlier send of {line.Issue} wasn't confirmed, and Tempo shows no matching worklog now. Send it again?",
                    "Unconfirmed send", MessageBoxIcon.Warning))
                {
                    held++;
                    continue;
                }
                if (!VerifyUnchanged(day, mode, [line], newHandoff: true))
                    continue;
                store.SetTempoAttempt(day, key, new TempoAttempt(TempoAttemptState.Pending, line, mode,
                    RequiresManualHandoff: prior?.RequiresManualHandoff == true));
                try
                {
                    var created = await gateway.CreateAsync(draft, cancel);
                    if (created.Id <= 0 || !draft.SameAs(created) || known.Contains(EntryId(created.Id)))
                        throw new TempoUncertainException("Tempo replied with a receipt that needs reconciliation.");
                    Record(day, mode, line, created.Id, key);
                    existing.Add(created);
                    sent++;
                }
                catch (TempoUncertainException error)
                {
                    held++;
                    problems.Add($"{line.Issue}: {error.Message} Send to Tempo again checks Tempo before resending.");
                }
                catch (InvalidOperationException error)
                {
                    store.SetTempoAttempt(day, key, new TempoAttempt(earlierPending ? TempoAttemptState.Pending : TempoAttemptState.Refused,
                        line, mode, error.Message, RequiresManualHandoff: true));
                    problems.Add($"{line.Issue}: {error.Message}");
                }
            }
        }
        catch (Exception error) when (IsTempoReadError(error))
        {
            var message = ReadErrorMessage(error);
            MarkTempoReadFailure(day, mode, uncheckedLines, message);
            problems.Add(message);
        }
        Reload();
        var summary = new List<string>();
        if (sent > 0) summary.Add($"Sent {sent} worklog(s) to Tempo");
        if (matched > 0) summary.Add($"{matched} already in Tempo, recorded");
        if (held > 0) summary.Add($"{held} unconfirmed, held");
        Notify(string.Join(". ", summary.Concat(problems.Take(2))) + (problems.Count > 2 ? $" (+{problems.Count - 2} more)" : "") +
            (summary.Count + problems.Count == 0 ? "Nothing was sent." : "."));
        if (problems.Count > 0)
        {
            if (ConfirmOverride != null)
                ConfirmOverride(string.Join("\n", problems));
            else
                MessageBox.Show(this, string.Join("\n", problems), "Send to Tempo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task ReconcileTempo()
    {
        var day = Day;
        var mode = Mode;
        Reload();
        var selected = ApprovedWorklogs();
        var candidates = selected.Concat(preview.Where(line => coverages[line].Entry is { Provenance: null } entry &&
            entry.Id.StartsWith("tempo-", StringComparison.Ordinal) &&
            completionDay.States[line] == CompletionLineState.Attested)).Distinct().ToArray();
        var active = candidates.Select(line => line.ApprovalKey(mode)).ToHashSet(StringComparer.Ordinal);
        var priorEntries = candidates.ToDictionary(line => line.ApprovalKey(mode), line => coverages[line].Entry);
        var currentAttempts = store.TempoAttempts(day);
        var items = candidates.Select(line => (Key: line.ApprovalKey(mode), Line: (Worklog?)line, Mode: mode, Detached: false))
            .Concat(currentAttempts.Where(item => item.Value.State == TempoAttemptState.Pending && !active.Contains(item.Key))
                .Select(item => (item.Key, item.Value.Line, item.Value.Mode, Detached: true))).ToArray();
        if (items.Length == 0)
        {
            Notify("No approved or unconfirmed worklogs to check.");
            return;
        }
        var checkedKeys = new HashSet<string>(StringComparer.Ordinal);
        var notes = new List<string>();
        var matches = 0;
        try
        {
            using var gateway = TempoOverride?.Invoke() ?? app.OpenTempo();
            var account = await gateway.AccountAsync(CancellationToken.None);
            var remote = await gateway.DayAsync(account, day, CancellationToken.None);
            var issueIds = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var (key, line, lineMode, detached) in items)
            {
                var prior = currentAttempts.GetValueOrDefault(key);
                if (line == null)
                {
                    var recordedIds = store.ManualEntries(day).Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
                    if (remote.All(entry => recordedIds.Contains(EntryId(entry.Id))) && Ask(
                        "The older attempt has no saved line details. Every worklog returned by Tempo for this date is already linked to a local entry. " +
                        "Clear the attempt only after verifying there is nothing else to reconcile for this date in Tempo.",
                        "Clear older attempt?", MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2))
                        store.SetTempoAttempt(day, key, null);
                    else
                        notes.Add("An older unconfirmed attempt has no line details. Use the original rounding mode and check the worklog in Tempo.");
                    continue;
                }
                if (!issueIds.TryGetValue(line.Issue, out var issueId))
                    issueIds[line.Issue] = issueId = await gateway.IssueIdAsync(line.Issue, CancellationToken.None);
                checkedKeys.Add(key);
                var draft = Draft(day, line, account, issueId);
                var exact = remote.Where(draft.SameAs).ToArray();
                var priorEntry = priorEntries.GetValueOrDefault(key);
                var known = store.ManualEntries(day).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                if (exact is [{ } same] && (priorEntry == null ? !known.Contains(EntryId(same.Id)) : priorEntry.Id == EntryId(same.Id)))
                {
                    if (detached || (priorEntry == null ? VerifyUnchanged(day, lineMode, [line], newHandoff: true)
                        : LegacyReceiptStillApplies(key, priorEntry.Id)))
                    {
                        Record(day, lineMode, line, same.Id, key);
                        matches++;
                    }
                    continue;
                }
                if (exact.Length > 1)
                    app.CompletionDuplicate(day, exact.Select(item => item.Id));
                var unmatched = remote.Count(entry => entry.IssueId == issueId && !known.Contains(EntryId(entry.Id)));
                if (exact.Length > 0 || unmatched > 0)
                {
                    notes.Add($"{line.Issue}: Tempo has time that needs manual reconciliation.");
                    if (priorEntry == null)
                        HoldRemoteConflict(day, key, line, lineMode, prior, notes[^1]);
                    continue;
                }
                if (detached && Ask($"Tempo returned no match for the earlier {line.Issue} attempt. " +
                    "Check the date, issue and start in Tempo. Clear this earlier attempt only after confirming it did not create a worklog.",
                    "Clear earlier attempt?", MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2))
                    store.SetTempoAttempt(day, key, null);
                else if (prior?.State is TempoAttemptState.ReadFailed or TempoAttemptState.RemoteConflict)
                    store.SetTempoAttempt(day, key, prior.RequiresManualHandoff ? prior with { State = TempoAttemptState.Refused } : null);
                else if (prior?.State == TempoAttemptState.Pending)
                    notes.Add($"{line.Issue}: no matching receipt found. The earlier send is still unconfirmed.");
            }
        }
        catch (Exception error) when (IsTempoReadError(error))
        {
            var message = ReadErrorMessage(error);
            MarkTempoReadFailure(day, mode, selected.Where(line => !checkedKeys.Contains(line.ApprovalKey(mode))), message);
            notes.Add("Tempo status unknown. " + message);
        }
        Reload();
        Notify($"{matches} matching Tempo receipt(s) recorded. " + string.Join(" ", notes.Distinct().Take(2)));
        if (notes.Count > 2 && ConfirmOverride == null)
            MessageBox.Show(this, string.Join("\n", notes.Distinct()), "Check Tempo status");
    }

    private bool LegacyReceiptStillApplies(string key, string id)
    {
        Reload();
        return preview.Any(line => line.ApprovalKey(Mode) == key && coverages[line].Entry?.Id == id &&
            completionDay.States[line] == CompletionLineState.Attested);
    }

    private void MarkTempoReadFailure(DateOnly day, RoundingMode mode, IEnumerable<Worklog> lines, string message)
    {
        var attempts = store.TempoAttempts(day);
        foreach (var line in lines)
        {
            var key = line.ApprovalKey(mode);
            var current = attempts.GetValueOrDefault(key);
            if (current?.State != TempoAttemptState.Pending)
                store.SetTempoAttempt(day, key, new TempoAttempt(TempoAttemptState.ReadFailed, line, mode, message,
                    current?.RequiresManualHandoff == true));
        }
    }

    private void HoldRemoteConflict(DateOnly day, string key, Worklog line, RoundingMode mode, TempoAttempt? prior, string reason)
    {
        if (prior?.State != TempoAttemptState.Pending)
            store.SetTempoAttempt(day, key, new TempoAttempt(TempoAttemptState.RemoteConflict, line, mode, reason,
                prior?.RequiresManualHandoff == true));
    }

    private TempoDraft Draft(DateOnly day, Worklog line, string account, long issueId) =>
        new(issueId, account, line.ProposedSeconds, day, LocalStart(line.Start), line.Description);

    private static bool IsTempoReadError(Exception error) => error is InvalidOperationException or ArgumentException or
        HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or FormatException;

    private static string ReadErrorMessage(Exception error) => error is TaskCanceledException ?
        "Jira or Tempo did not respond in time." : error.Message;

    private static string EntryId(long tempoId) => "tempo-" + tempoId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Shorten(string text) => text.Length <= 48 ? text : text[..47] + "…";

    private void Record(DateOnly day, RoundingMode mode, Worklog line, long tempoId, string key)
    {
        store.SaveManualEntries(day, [new ManualEntry(EntryId(tempoId), line.Issue, mode, line.ProposedSeconds,
            line.AllocationIds.ToArray(), line.Start, line.Intervals.ToArray(), line.Description,
            SubmissionProvenance.VerifiedTempo, app.CompletionNow)], clearAttempts: [key]);
        app.CompletionSubmission(day, SubmissionProvenance.VerifiedTempo);
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
            store.ConfirmHandoffKeys(day, lines.Select(line => line.ApprovalKey(Mode)));
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

    internal string WorklogState(Worklog line)
    {
        var state = BaseWorklogState(line);
        var coverage = coverages[line];
        return completionDay.States[line] == CompletionLineState.Correction && !coverage.NeedsReview &&
            (coverage.Entry != null || coverage.Consolidated != null) && line.AllocationIds.All(approvedIds.Contains) &&
            correctionCopies.Contains(line.ApprovalKey(Mode)) ? state + " · copied, mark entered" : state;
    }

    private string BaseWorklogState(Worklog line)
    {
        var coverage = coverages[line];
        var needsApproval = !line.AllocationIds.All(approvedIds.Contains);
        var key = line.ApprovalKey(Mode);
        var attempt = completionDay.Attempts.GetValueOrDefault(key);
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
            if (coverage.DescriptionChanged(line))
                return WithApproval("Update description");
            return completionDay.States[line] == CompletionLineState.Verified
                ? $"Verified in Tempo · {coverage.Entry!.Seconds / 60}m" : $"Manually attested · {coverage.Entry!.Seconds / 60}m";
        }
        if (completionDay.HasEarlierPending(line, Mode))
            return "Earlier send unconfirmed · check Tempo status";
        if (attempt?.State == TempoAttemptState.Pending)
            return "Send unconfirmed · Tempo status unknown";
        if (attempt?.State == TempoAttemptState.ReadFailed)
            return "Tempo status unknown · " + attempt.Reason;
        if (attempt?.State == TempoAttemptState.Refused)
            return "Tempo refused · " + attempt.Reason;
        if (attempt?.State == TempoAttemptState.RemoteConflict)
            return "Reconcile Tempo time · " + attempt.Reason;
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
        if (TempoBusy)
            return;
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
        var confirmedHandoffs = store.ConfirmedHandoffKeys(day).ToHashSet(StringComparer.Ordinal);
        if (selected.Any(line => completionDay.HasEarlierPending(line, mode)))
        {
            Notify("Check the earlier unconfirmed worklog before marking this time entered.");
            return;
        }
        if (selected.Any(line => completionDay.Attempts.GetValueOrDefault(line.ApprovalKey(mode))?.RequiresManualHandoff == true &&
            !confirmedHandoffs.Contains(line.ApprovalKey(mode))))
        {
            Notify("Tempo refused this line. Use Check Tempo status, or copy and enter it before marking it entered.");
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
            if (completionDay.States[line] is CompletionLineState.Verified or CompletionLineState.Attested)
                continue;
            records.Add(new ManualEntry(knownId ?? Guid.NewGuid().ToString("N"), line.Issue, Mode,
                line.ProposedSeconds, line.AllocationIds.ToArray(), line.Start, line.Intervals.ToArray(), line.Description,
                SubmissionProvenance.Manual, app.CompletionNow));
        }
        if (records.Count == 0)
        {
            Notify("The selected worklogs are already recorded as entered.");
            return;
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
            store.SaveManualEntries(day, records, replaced, selected.Select(line => line.ApprovalKey(mode)).ToArray());
            app.CompletionSubmission(day, SubmissionProvenance.Manual, records.Count);
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
                store.SaveManualEntry(Day, ManualLedger.KeepSeparate(old) with { Provenance = SubmissionProvenance.Manual,
                    RecordedAtUtc = app.CompletionNow });
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
