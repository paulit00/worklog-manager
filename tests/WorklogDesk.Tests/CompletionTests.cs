using System.Windows.Forms;
using WorklogDesk.Core;
using WorklogDesk.Windows;

internal static class CompletionTests
{
    private static readonly DateOnly Day = new(2026, 1, 5);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;
    private const RoundingMode Mode = RoundingMode.IssueDay;
    private static readonly CompletionReminder Reminder = CompletionReminder.Default with { Enabled = true };

    public static void ReminderLifecycle() => WithFolder(folder =>
    {
        var clock = new TestClock(At(Day, 16, 44));
        using (var store = new WorkStore(folder))
        {
            var flow = Flow(store, clock);
            store.ReplaceRows([], [Row(Day)]);
            Expect(CompletionCue.None, flow.Tick(Reminder, () => true));
            clock.Utc = At(Day, 16, 45);
            Expect(CompletionCue.None, flow.Tick(Reminder, () => false));
            clock.Utc = At(Day, 17, 10);
            Expect(CompletionCue.First, flow.Tick(Reminder, () => true));
            flow.CueRequested(Day, CompletionCue.First);
            Expect(false, flow.Activate(Day, CompletionCue.First, sessionReady: false));
            Expect(true, flow.Snooze());
            Expect(false, flow.Snooze());
        }
        using var reopened = new WorkStore(folder);
        var restarted = Flow(reopened, clock);
        Expect(CompletionCue.None, restarted.Tick(Reminder, () => true));
        clock.Utc = At(Day, 17, 40);
        Expect(CompletionCue.None, restarted.Tick(Reminder, () => false));
        clock.Utc = At(Day, 17, 41);
        Expect(CompletionCue.Snoozed, restarted.Tick(Reminder, () => true));
        Expect(CompletionCue.None, restarted.Tick(Reminder, () => true));
        reopened.ReplaceRows([reopened.Rows(Day).Single().Id], []);
        reopened.ReplaceRows([], [Row(Day)]);
        Expect(CompletionCue.None, restarted.Tick(Reminder, () => true));
        clock.Utc = At(Day, 18, 45);
        Expect(CompletionCue.None, restarted.Tick(Reminder, () => true));
        Expect(false, restarted.Activate(Day, CompletionCue.Snoozed, sessionReady: true));
        clock.Utc = At(Day, 17, 00);
        Expect(CompletionCue.None, restarted.Tick(Reminder, () => true));
        Expect(false, restarted.Day(Day).Cue.CanSnooze(clock.Utc));

        var tomorrow = Day.AddDays(1);
        clock.Utc = At(tomorrow, 17, 30);
        reopened.ReplaceRows([], [Row(tomorrow)]);
        restarted.SkipToday(true);
        Expect(CompletionCue.None, restarted.Tick(Reminder, () => true));
        var afterSkipRestart = Flow(reopened, clock);
        Expect(true, afterSkipRestart.Day(tomorrow).Cue.Skipped);
        clock.Utc = At(tomorrow, 17, 45);
        afterSkipRestart.SkipToday(false);
        Expect(CompletionCue.None, afterSkipRestart.Tick(Reminder, () => true));
        clock.Utc = At(tomorrow, 17, 10);
        Expect(CompletionCue.None, afterSkipRestart.Tick(Reminder with { Time = new TimeOnly(17, 0) }, () => true));
        Expect(false, afterSkipRestart.Activate(Day, CompletionCue.First, sessionReady: true));
    });

    public static void Calendar()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var spring = new DateOnly(2026, 3, 8);
        var springWindow = CueWindow.For(spring, new TimeOnly(2, 30), eastern);
        Expect(At(spring, 7, 0), springWindow.Start);
        Expect(At(spring, 8, 0), springWindow.FirstExpiry);
        var autumn = new DateOnly(2026, 11, 1);
        var autumnWindow = CueWindow.For(autumn, new TimeOnly(1, 30), eastern);
        Expect(At(autumn, 5, 30), autumnWindow.Start);
        Expect(At(autumn, 6, 30), autumnWindow.FirstExpiry);
        var state = new CueDay(autumnWindow);
        Expect(CompletionCue.First, state.Due(At(autumn, 5, 30), true));
        state = state with { FirstReserved = true };
        Expect(CompletionCue.None, state.Due(At(autumn, 6, 30), true));
        var midnight = CueWindow.For(Day, new TimeOnly(23, 50), Zone);
        Expect(At(Day.AddDays(1), 0, 0), midnight.FirstExpiry);
        Expect(midnight.FirstExpiry, midnight.Expiry);
        Expect(false, new CueDay(midnight, FirstReserved: true).CanSnooze(midnight.Start));
    }

    public static void Summary() => WithFolder(folder =>
    {
        using var store = new WorkStore(folder);
        var empty = store.Completion(Day, Mode).Counts;
        Expect(false, empty.Closed);
        Expect(false, empty.Actionable);
        var billable = Row(Day, "WD-1");
        var idle = Row(Day, start: 600) with { Kind = SpanKind.Idle };
        var incidental = Row(Day, start: 1200) with { Kind = SpanKind.Incidental, Status = Disposition.Excluded };
        var uncertain = Row(Day, start: 1800) with { Kind = SpanKind.Uncertain, End = At(Day, 9, 30).AddSeconds(8) };
        store.ReplaceRows([], [billable, idle, incidental, uncertain]);
        var model = store.Completion(Day, Mode);
        Expect(1, model.Counts.Activity);
        Expect(1, model.Counts.Drafts);
        Expect(true, CompletionDay.NeedsActivityReview(idle, model.Approved));
        var line = model.Lines.Single();
        var key = line.ApprovalKey(Mode);
        billable = billable with { Approved = true, ApprovedMode = Mode, ApprovedKey = key };
        store.ReplaceRows([], [billable]);
        store.RecordExport(Day, [line]);
        store.RecordHandoffKeys(Day, [key]);
        Expect(1, store.Completion(Day, Mode).Counts.Ready);
        Expect(0, store.Completion(Day, Mode).Counts.Verified);
        store.SetTempoAttempt(Day, key, new(TempoAttemptState.ReadFailed, line));
        Expect(1, store.Completion(Day, Mode).Counts.Unknown);

        var entry = new ManualEntry("tempo-22", line.Issue, Mode, line.ProposedSeconds, line.AllocationIds.ToArray(),
            line.Start, line.Intervals.ToArray(), line.Description);
        store.SaveManualEntries(Day, [entry], clearAttempts: [key]);
        Expect(1, store.Completion(Day, Mode).Counts.Attested);
        Expect(0, store.Completion(Day, Mode).Counts.Verified);
        store.SaveManualEntry(Day, entry with { Provenance = SubmissionProvenance.VerifiedTempo });
        Expect(false, store.Completion(Day, Mode).Counts.Closed);
        store.ReplaceRows([], [uncertain with { Status = Disposition.Excluded }]);
        Expect(true, store.Completion(Day, Mode).Counts.Closed);
        Expect(false, store.Completion(Day, Mode, inProgress: true).Counts.Closed);
        Expect(true, store.Completion(Day, RoundingMode.Session).Counts.Corrections >= 1);

        store.ReplaceRows([], [billable with { Task = billable.Task with { Description = "Changed" } }]);
        Expect(false, store.Completion(Day, Mode).Counts.Closed);
        Expect(1, store.Completion(Day, Mode).Counts.Corrections);
        store.RemoveManualEntry(entry.Id);
        store.SetTempoAttempt(Day, key, new(TempoAttemptState.Pending, line));
        model = store.Completion(Day, Mode);
        Expect(1, model.Counts.Unknown);
        Expect(true, model.HasEarlierPending(model.Lines.Single(), Mode));
        Expect(true, store.CompletionDatesBefore(Day.AddDays(1)).Contains(Day));

        var manualDate = Day.AddDays(1);
        store.ReplaceRows([], [Row(manualDate, "WD-2") with { Kind = SpanKind.Manual }]);
        var manualLine = store.Completion(manualDate, Mode).Lines.Single();
        store.SaveManualEntry(manualDate, new ManualEntry("manual-1", manualLine.Issue, Mode, manualLine.ProposedSeconds,
            manualLine.AllocationIds.ToArray(), manualLine.Start, manualLine.Intervals.ToArray(), manualLine.Description,
            SubmissionProvenance.Manual));
        Expect(true, store.Completion(manualDate, Mode).Counts.Closed);
        Expect(1, store.Completion(manualDate, Mode).Counts.Attested);
    });

    public static void Pilot() => WithFolder(folder =>
    {
        var clock = new TestClock(At(Day, 12, 0));
        using var store = new WorkStore(folder);
        var flow = Flow(store, clock);
        store.CompletionChanging += flow.ObserveCutoff;
        flow.StartPilot(new TimeOnly(18, 0));
        for (var i = 0; i < 5; i++)
        {
            var day = Day.AddDays(i);
            clock.Utc = At(day, 16, 46);
            Expect(CompletionCue.None, flow.Tick(Reminder, () => true));
            flow.CheckIn(day, DayCheckIn.Worked);
            clock.Utc = At(day, 17, 59).AddSeconds(58);
            flow.ObserveCutoff();
            if (i == 1)
                flow.InvalidateCutoffContinuity();
            clock.Utc = At(day, 18, 0).AddSeconds(1);
            if (i == 4)
                store.ReplaceRows([], [Row(day)]);
            flow.ObserveCutoff();
            var outcome = flow.Day(day).Pilot!;
            Expect(i == 1, outcome.MissedCutoff);
            if (i != 1)
                Expect(false, outcome.Observation!.Counts.HasActivity);
            Expect<PilotPhase?>(PilotPhase.Baseline, flow.Phase(day));
        }
        var transition = Day.AddDays(5);
        Expect<DateOnly?>(transition, flow.Pilot!.ReminderStarts);
        Expect<PilotPhase?>(PilotPhase.Reminder, flow.Phase(transition));
        clock.Utc = At(transition, 16, 50);
        flow.CheckIn(transition, DayCheckIn.Leave);
        Expect(DayCheckIn.Leave, flow.Day(transition).CheckIn);
        Expect(true, flow.Report(Reminder).Contains("Closed by cutoff 0/5 · missing cutoff 1", StringComparison.Ordinal));
    });

    public static void Navigation() => WithFolder(folder => OnSta(() =>
    {
        using var store = new WorkStore(folder, inMemory: true);
        using var app = new TrayApplication(folder, demo: true, "WorklogDesk-Completion-Test", showOnStart: false, diagnostics: true);
        try
        {
            var previous = app.Today.AddDays(-1);
            store.ReplaceRows([], [Row(previous)]);
            store.ReplaceRows([], [Row(app.Today, "WD-1")]);
            using var review = new ReviewWindow(app, store, openWithoutActivation: true)
            {
                ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(SystemInformation.VirtualScreen.Right + 32, 0)
            };
            review.TempoOverride = () => throw new Exception("Opening a date must never open a network gateway.");
            review.Show();
            review.Reload();
            review.OpenCompletionDate(previous);
            Expect(previous, review.SelectedDay);
            Expect(0, Find<TabControl>(review).SelectedIndex);
            Expect(true, Find<CheckBox>(review, "Needs review only").Checked);
            review.OpenCompletionDate(app.Today);
            Expect(app.Today, review.SelectedDay);
            Expect(1, Find<TabControl>(review).SelectedIndex);
            Expect(0, Find<DataGridView>(review, "Proposed Tempo worklogs").SelectedRows.Count);
        }
        finally { app.ShutDownDiagnostics(); }
    }));

    public static void PartialSend() => WithFolder(folder => OnSta(() =>
    {
        using var store = new WorkStore(folder, inMemory: true);
        using var app = new TrayApplication(folder, demo: true, "WorklogDesk-PartialSend-Test", showOnStart: false, diagnostics: true);
        try
        {
            var date = app.Today;
            var rows = new[] { Row(date, "WD-1"), Row(date, "WD-2", start: 600), Row(date, start: 1200) };
            var lines = Timeline.Preview(rows, Mode);
            store.ReplaceRows([], rows.Select(row => row.Task.Issue.Length == 0 ? row : row with
            {
                Approved = true, ApprovedMode = Mode,
                ApprovedKey = lines.Single(line => line.AllocationIds.Contains(row.Id)).ApprovalKey(Mode)
            }).ToArray());
            using var review = new ReviewWindow(app, store, openWithoutActivation: true)
            {
                ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(SystemInformation.VirtualScreen.Right + 32, 0)
            };
            var tempo = new FakeTempo { RefuseIssueId = new FakeTempo().IssueId("WD-1") };
            review.TempoOverride = () => tempo;
            review.ConfirmOverride = _ => true;
            review.Show();
            review.Reload();
            review.OpenCompletionDate(date);
            review.SendToTempo().GetAwaiter().GetResult();
            var counts = store.Completion(date, Mode).Counts;
            Expect(1, tempo.Creates);
            Expect(1, counts.Verified);
            Expect(1, counts.Refused);
            Expect(1, counts.Activity);
            Expect(false, counts.Closed);
        }
        finally { app.ShutDownDiagnostics(); }
    }));

    private static T Find<T>(Control parent, string? label = null) where T : Control => Children(parent).OfType<T>()
        .Single(control => label == null || control.Text == label || control.AccessibleName == label);

    private static IEnumerable<Control> Children(Control parent) => parent.Controls.Cast<Control>()
        .SelectMany(child => Children(child).Prepend(child));

    private static CompletionCoordinator Flow(WorkStore store, TestClock clock) => new(store, clock, Zone, () => Mode, () => false);
    private static DateTimeOffset At(DateOnly day, int hour, int minute) => Timeline.AtLocalTime(day, new(hour, minute), Zone);

    private static Allocation Row(DateOnly day, string issue = "", int start = 0)
    {
        var begin = At(day, 9, 0).AddSeconds(start);
        return new(Guid.NewGuid().ToString("N"), day, begin, begin.AddMinutes(5), "Synthetic", SpanKind.Foreground,
            issue.Length > 0 ? Disposition.Billable : Disposition.Unassigned, new TaskLabel("Sample", issue, "Work performed"));
    }

    private static void WithFolder(Action<string> check)
    {
        var folder = Directory.CreateTempSubdirectory("WorklogDesk-CompletionTests-");
        try { check(folder.FullName); }
        finally { folder.Delete(true); }
    }

    private static void OnSta(Action check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { check(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20)))
            throw new TimeoutException("Native review did not return.");
        if (failure != null)
            throw new InvalidOperationException(failure.Message, failure);
    }

    private static void Expect<T>(T expected, T actual, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; observed {actual} (line {line}).");
    }

    private sealed class TestClock(DateTimeOffset initial) : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = initial;
        public override DateTimeOffset GetUtcNow() => Utc;
    }
}
