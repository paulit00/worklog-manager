using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal sealed partial class TrayApplication
{
    private CompletionCoordinator? completion;
    private System.Windows.Forms.Timer? completionTimer;
    private FinishWorklogWindow? completionWindow;
    private CompletionNotification? notificationCenter;
    private CompletionToast? activeToast;
    private bool notificationRegistrationStarted;
    private bool notificationRegistering;
    private bool completionStopping;
    private bool useSyntheticNotifications;
    private bool suppressSyntheticBanner;
    private string notificationFolder = "";
    private DateOnly? requestedReviewDay;
    private long lastSummaryRefresh;
    private bool notificationAccepted;

    internal DateTimeOffset CompletionNow => completion?.Now ?? DateTimeOffset.UtcNow;
    internal bool CompletionNotificationAccepted => notificationAccepted;
    internal string? CompletionNotificationError { get; private set; }
    internal CompletionToast? CompletionToastForDiagnostics => activeToast;
    internal bool CompletionCueActivated => completion?.Day(Today).Events?.Any(item => item.Kind == CompletionEventKind.CueActivated) == true;
    internal bool CompletionSessionInteractive => observer.CanRequestCompletionCue;
    internal event Action<bool, bool>? CompletionSessionObserved;

    private void StartCompletion(TimeProvider clock, bool diagnostics, string folder, bool syntheticNotifications, bool suppressSyntheticBanner)
    {
        if (diagnostics)
            return;
        notificationFolder = folder;
        useSyntheticNotifications = syntheticNotifications;
        this.suppressSyntheticBanner = syntheticNotifications && suppressSyntheticBanner;
        completion = new CompletionCoordinator(store, clock, Zone, () => Settings.Rounding,
            () => RunningManual || capture.CurrentKind is SpanKind.Foreground or SpanKind.Uncertain);
        store.CompletionChanging += ObserveCompletionCutoff;
        observer.CompletionClockChanged += CompletionClockChanged;
        completionTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        completionTimer.Tick += (_, _) => RefreshCompletion(forceSummary: false);
        completionTimer.Start();
        dispatcher.BeginInvoke((Action)(() => RefreshCompletion()));
    }

    private void StopCompletion()
    {
        completionStopping = true;
        completionTimer?.Dispose();
        store.CompletionChanging -= ObserveCompletionCutoff;
        observer.CompletionClockChanged -= CompletionClockChanged;
        if (DetachCompletionToast() is { } removal)
        {
            try { removal.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException error) { System.Diagnostics.Debug.WriteLine(error.Message); }
        }
        notificationCenter?.Unregister(removeRegistration: useSyntheticNotifications);
        notificationCenter = null;
        completionWindow?.Dispose();
    }

    private void ObserveCompletionCutoff()
    {
        if (!observer.IsSuspended)
            completion?.ObserveCutoff();
    }

    private void CompletionClockChanged()
    {
        CompletionSessionObserved?.Invoke(observer.IsSuspended, observer.CanRequestCompletionCue);
        if (observer.IsSuspended)
            completion?.InvalidateCutoffContinuity();
        RefreshCompletion();
    }

    internal void ShowCompletion()
    {
        if (completion == null)
            return;
        Flush(true);
        if (completionWindow is null || completionWindow.IsDisposed)
            completionWindow = new FinishWorklogWindow(this, completion, store);
        completionWindow.RefreshSummary();
        if (!completionWindow.Visible)
            completionWindow.Show();
        if (completionWindow.WindowState == FormWindowState.Minimized)
            completionWindow.WindowState = FormWindowState.Normal;
        completionWindow.Activate();
    }

    internal void EditCompletionReminder()
    {
        using var settings = new CompletionReminderDialog(Settings.CompletionReminder ?? CompletionReminder.Default, Zone);
        if (settings.ShowDialog(completionWindow) == DialogResult.OK)
        {
            SaveSettings(Settings with { CompletionReminder = settings.Result });
            RefreshCompletion();
        }
    }

    internal void SnoozeCompletion()
    {
        if (completion?.Snooze() == true)
        {
            CloseCompletionNotification();
            RefreshCompletion();
        }
    }

    internal void SkipCompletionToday()
    {
        if (completion == null)
            return;
        completion.SkipToday(!completion.Day(Today).Cue.Skipped);
        CloseCompletionNotification();
        RefreshCompletion();
    }

    internal void OpenCompletionReview(DateOnly day)
    {
        if (review is { IsDisposed: false, CanNavigateCompletion: false })
        {
            requestedReviewDay = day;
            review.ShowCompletionDeferred(day);
            return;
        }
        requestedReviewDay = null;
        ShowReview(completionDate: day);
        if (completion?.Day(day).CheckIn == DayCheckIn.Worked && CompletionCoordinator.AllowsNoWork(completion.Counts(day)))
            review?.AddMissingCompletionTime();
    }

    internal void ResumeCompletionNavigation()
    {
        if (requestedReviewDay.HasValue && !dispatcher.IsDisposed)
            dispatcher.BeginInvoke((Action)OpenDeferredReview);
    }

    private void OpenDeferredReview()
    {
        if (requestedReviewDay is { } date && (review == null || review.CanNavigateCompletion))
            OpenCompletionReview(date);
    }

    internal void CompletionSubmission(DateOnly day, SubmissionProvenance kind, int count = 1) =>
        completion?.Submission(day, kind, count);

    internal void CompletionDuplicate(DateOnly day, IEnumerable<long> remoteIds) => completion?.Duplicate(day, remoteIds);

    internal void CompletionReviewChanged(DateOnly day, CompletionCounts counts)
    {
        var inProgress = day == Today && (RunningManual || capture.CurrentKind is SpanKind.Foreground or SpanKind.Uncertain);
        completion?.ObserveReview(day, counts with { InProgress = inProgress });
        if (activeToast?.Day == day && !counts.Actionable && !inProgress)
            CloseCompletionNotification();
    }

    internal void RefreshCompletion(bool forceSummary = true)
    {
        if (completion == null || completionStopping)
            return;
        var settings = Settings.CompletionReminder ?? CompletionReminder.Default;
        MaintainNotificationRegistration(settings.Enabled);
        if (activeToast is { } toast && (!settings.Enabled || !settings.Days.Contains(toast.Day.DayOfWeek) || toast.Day != Today ||
            !completion.Day(toast.Day).Cue.CanActivate(completion.Now, toast.Cue)))
            CloseCompletionNotification();
        var due = CompletionCue.None;
        if (!observer.IsSuspended)
        {
            due = completion.Tick(settings, () => tray.Visible && observer.CanRequestCompletionCue && NotificationAvailable());
            if (due != CompletionCue.None)
                RequestCompletionNotification(Today, due);
        }
        var tickCount = Environment.TickCount64;
        if (completionWindow is { IsDisposed: false, Visible: true } &&
            (forceSummary || due != CompletionCue.None || tickCount - lastSummaryRefresh >= 25_000))
        {
            completionWindow.RefreshSummary();
            lastSummaryRefresh = tickCount;
        }
        OpenDeferredReview();
    }

    private void RequestCompletionNotification(DateOnly day, CompletionCue cue)
    {
        CloseCompletionNotification();
        if (completion?.Day(day).Cue.Window is not { } window || notificationCenter == null)
            return;
        var toast = CompletionToast.Create(day, cue, cue == CompletionCue.First ? window.FirstExpiry : window.Expiry);
        activeToast = toast;
        try
        {
            notificationAccepted = notificationCenter.Request(toast, suppressSyntheticBanner);
            completion.CueRequested(day, cue);
        }
        catch (Exception error)
        {
            CompletionNotificationError = error.ToString();
            CloseCompletionNotification();
        }
    }

    private async void MaintainNotificationRegistration(bool enabled)
    {
        if (demo && !useSyntheticNotifications)
            return;
        if (!enabled)
        {
            CloseCompletionNotification();
            notificationCenter?.Unregister(removeRegistration: true);
            notificationCenter = null;
            if (!notificationRegistering)
                notificationRegistrationStarted = false;
            return;
        }
        if (notificationRegistrationStarted)
            return;
        notificationRegistrationStarted = true;
        notificationRegistering = true;
        try
        {
            var displayName = useSyntheticNotifications ? "Worklog Desk (Synthetic test)" : "Worklog Desk";
            var registered = await CompletionNotification.RegisterAsync(notificationFolder, displayName, ReceiveToastActivation);
            if (completionStopping || Settings.CompletionReminder?.Enabled != true)
            {
                registered?.Unregister(removeRegistration: true);
                notificationRegistrationStarted = false;
                return;
            }
            notificationCenter = registered;
            if (registered == null)
                CompletionNotificationError = "Windows App Notifications are unsupported in this process.";
            RefreshCompletion();
        }
        catch (Exception error)
        {
            CompletionNotificationError = error.ToString();
        }
        finally { notificationRegistering = false; }
    }

    private bool NotificationAvailable()
    {
        try { return notificationCenter?.Available == true; }
        catch (Exception error)
        {
            CompletionNotificationError = error.ToString();
            return false;
        }
    }

    private void ReceiveToastActivation(string tag, string token)
    {
        if (completionStopping || dispatcher.IsDisposed)
            return;
        try { dispatcher.BeginInvoke((Action)(() => OpenFromToast(tag, token))); }
        catch (InvalidOperationException) { }
    }

    internal void OpenFromToast(string tag, string token)
    {
        if (activeToast is not { } toast || !toast.Matches(tag, token) || completionStopping || completion == null ||
            Settings.CompletionReminder is not { Enabled: true } settings || !settings.Days.Contains(toast.Day.DayOfWeek))
            return;
        if (!completion.Activate(toast.Day, toast.Cue, observer.CanRequestCompletionCue))
            return;
        CloseCompletionNotification();
        OpenCompletionReview(toast.Day);
    }

    internal async Task<bool> CompletionToastRetainedAsync()
    {
        return activeToast is { } toast && notificationCenter != null && await notificationCenter.IsInCenterAsync(toast.Tag);
    }

    private void CloseCompletionNotification()
    {
        _ = DetachCompletionToast();
    }

    private Task? DetachCompletionToast()
    {
        var toast = activeToast;
        activeToast = null;
        return toast != null && notificationCenter != null ? RemoveToast(notificationCenter, toast.Tag) : null;
    }

    private static async Task RemoveToast(CompletionNotification center, string tag)
    {
        try { await center.RemoveAsync(tag).ConfigureAwait(false); }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine(error.Message); }
    }
}
