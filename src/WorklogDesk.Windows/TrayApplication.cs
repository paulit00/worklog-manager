using System.IO.Pipes;
using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal sealed class TrayApplication : ApplicationContext
{
    private readonly WorkStore store;
    private readonly WindowsObserver observer;
    private readonly CaptureEngine capture = new();
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem captureMenu;
    private readonly System.Windows.Forms.Timer tick;
    private readonly Control dispatcher = new();
    private readonly CancellationTokenSource listenerCancellation = new();
    private readonly bool demo;
    private readonly bool syntheticIdle;
    private ReviewWindow? review;
    private TaskLabel? pin;
    private TaskLabel? manual;
    private TaskLabel? interruptedManual;
    private long lastSave;
    private long? pinSuspendedAt;
    private DateOnly retentionDay;
    private bool reviewDirty;
    private bool pinPauseReported;
    private bool pinConflict;
    private DateTimeOffset? interruptedAt;
    private long lastAutomaticRefresh;

    public Preferences Settings { get; private set; }
    public TimeZoneInfo Zone { get; }
    public DateOnly Today => Timeline.DayOf(DateTimeOffset.UtcNow, Zone);
    public bool RunningManual => manual != null;
    public bool IsDemo => demo;

    public TrayApplication(string folder, bool demo, string instance, bool showOnStart = true, bool diagnostics = false,
        bool syntheticIdle = false)
    {
        this.demo = demo;
        this.syntheticIdle = syntheticIdle;
        store = new WorkStore(folder, inMemory: demo);
        Settings = store.Preferences();
        Zone = TimeZoneInfo.FindSystemTimeZoneById(Settings.TimeZoneId);
        store.SavePreferences(Settings);
        interruptedManual = store.Recover(Zone);
        store.ExpireSource(DateTimeOffset.UtcNow);
        retentionDay = Today;
        if (demo)
            DemoData.Populate(store, Today, Zone);

        observer = new WindowsObserver(!demo && !diagnostics)
            { Preferences = () => Settings, Pin = AvailablePin, Manual = () => manual };
        if (Settings.Recording && !observer.SessionNotificationsAvailable)
        {
            Settings = Settings with { Recording = false };
            store.SavePreferences(Settings);
        }
        if (!syntheticIdle)
            observer.Observed += capture.Observe;
        observer.SessionInterrupted += InterruptSession;
        observer.SessionBoundaryObserved += () => Flush(true);
        observer.SessionResumed += ResumeSession;
        observer.PinConflict += () =>
        {
            pinConflict = true;
            pinSuspendedAt ??= Environment.TickCount64;
        };
        observer.Shortcut += key =>
        {
            if (key == 1) ShowReview();
            if (key == 2) EditPin();
            if (key == 3) QuickRequest();
        };
        capture.IdleThreshold = TimeSpan.FromMinutes(Settings.IdleMinutes);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open review   Ctrl+Shift+Alt+L", null, (_, _) => ShowReview());
        menu.Items.Add("Pin task   Ctrl+Shift+Alt+P", null, (_, _) => EditPin());
        menu.Items.Add("Capture request   Ctrl+Shift+Alt+N", null, (_, _) => QuickRequest());
        menu.Items.Add("Assign last RDP span", null, (_, _) => ShowReview(selectRemote: true));
        menu.Items.Add("Start / stop manual timer", null, (_, _) => ToggleManual());
        captureMenu = new ToolStripMenuItem("", null, (_, _) => ToggleCapture());
        menu.Items.Add(captureMenu);
        menu.Items.Add("Settings", null, (_, _) => EditSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit Worklog Desk", null, (_, _) => ExitThread());
        tray = new NotifyIcon { Icon = SystemIcons.Application, Visible = !diagnostics, ContextMenuStrip = menu };
        tray.MouseDoubleClick += (_, _) => ShowReview();

        tick = new System.Windows.Forms.Timer { Interval = 5_000 };
        tick.Tick += (_, _) => Tick();
        _ = dispatcher.Handle;
        if (!diagnostics)
            _ = Listen(instance, listenerCancellation.Token);
        Sample();
        UpdateStatus();
        tick.Start();

        if (showOnStart && (demo || !Settings.Recording))
            dispatcher.BeginInvoke((Action)(() => ShowReview()));
    }

    internal void RenderDemo(string output)
    {
        using var window = new ReviewWindow(this, store, openWithoutActivation: true);
        window.SetStatus("Demo · synthetic activity", null, true);
        window.Reload();
        OpenOffscreen(window);
        using var bitmap = new Bitmap(window.Width, window.Height);
        window.DrawToBitmap(bitmap, new Rectangle(0, 0, window.Width, window.Height));
        bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        window.Hide();
    }

    internal double[] MeasureReview(RoundingMode? worklogMode = null)
    {
        using var window = new ReviewWindow(this, store, openWithoutActivation: true);
        if (worklogMode.HasValue)
            window.OpenWorklogPreview(worklogMode.Value);
        OpenOffscreen(window);
        using var image = new Bitmap(window.Width, window.Height);
        var samples = new double[30];
        for (var run = 0; run < samples.Length; run++)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            window.Reload();
            window.DrawToBitmap(image, new Rectangle(0, 0, window.Width, window.Height));
            samples[run] = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        return samples;
    }

    internal void WarmReviewForIdleMeasurement()
    {
        DemoData.Populate(store, Today, Zone);
        _ = MeasureReview();
    }

    private static void OpenOffscreen(Form window)
    {
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(SystemInformation.VirtualScreen.Right + 32, 0);
        window.ShowInTaskbar = false;
        window.Show();
        Application.DoEvents();
    }

    internal void ShutDownDiagnostics() => ExitThread();

    public void Apply(Preferences settings)
    {
        if (demo)
            settings = settings with { Recording = false };
        if (settings.Recording && !observer.SessionNotificationsAvailable)
        {
            MessageBox.Show("Windows session notifications are unavailable. Automatic tracking stays paused.",
                "Worklog Desk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            settings = settings with { Recording = false };
        }
        SaveSettings(settings);
        capture.IdleThreshold = TimeSpan.FromMinutes(Settings.IdleMinutes);
        observer.Sample();
        Flush(true);
        UpdateStatus();
    }

    public void ToggleCapture() => Apply(Settings with { Recording = !Settings.Recording });

    public TaskLabel? CurrentPin => pinSuspendedAt.HasValue ? null : pin;

    public void EditPin()
    {
        using var dialog = new TaskDialog("Pin task", pin ?? new(), requireDescription: false, allowClear: true);
        if (dialog.ShowDialog(review) != DialogResult.OK)
            return;
        pin = dialog.Clear ? null : dialog.Task;
        pinSuspendedAt = null;
        pinConflict = false;
        observer.Sample();
        UpdateStatus();
    }

    public void ResumeOrEditPin()
    {
        if (pin == null || !pinSuspendedAt.HasValue)
        {
            EditPin();
            return;
        }
        if (MessageBox.Show(review, $"Resume pin {pin.Issue} {pin.Client}?".TrimEnd(),
            "Confirm task", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        pinSuspendedAt = null;
        pinConflict = false;
        observer.Sample();
        UpdateStatus();
    }

    public void QuickRequest()
    {
        using var dialog = new TaskDialog("Capture request", new TaskLabel(pin?.Client ?? ""),
            requireDescription: true, allowStart: true);
        if (dialog.ShowDialog(review) != DialogResult.OK)
            return;
        store.SaveRequest(new RequestDraft(Guid.NewGuid().ToString("N"), Today, dialog.Task));
        if (dialog.StartTimer)
            StartManual(dialog.Task);
        review?.Reload();
    }

    public void ToggleManual()
    {
        if (manual != null)
        {
            manual = null;
            observer.Sample();
            Flush(true);
            UpdateStatus();
            review?.Reload();
            return;
        }
        using var dialog = new TaskDialog(interruptedManual == null ? "Start manual timer" : "Resume interrupted timer",
            interruptedManual ?? pin ?? new(), requireDescription: true);
        if (dialog.ShowDialog(review) == DialogResult.OK)
            StartManual(dialog.Task);
    }

    public void EditSettings()
    {
        using var dialog = new SettingsDialog(Settings);
        if (dialog.ShowDialog(review) == DialogResult.OK)
            Apply(dialog.Result);
    }

    public void ConnectJira()
    {
        using var dialog = new JiraConfigDialog(Settings.Jira);
        if (dialog.ShowDialog(review) != DialogResult.OK)
            return;
        var original = Settings.Jira;
        SaveSettings(Settings with { Jira = dialog.Result });
        review?.UpdateConnections();
        if (original != null && original.CredentialTarget != dialog.Result?.CredentialTarget)
            CredentialVault.Delete(original.CredentialTarget);
    }

    public void SaveRounding(RoundingMode mode) => SaveSettings(Settings with { Rounding = mode });

    private void SaveSettings(Preferences settings)
    {
        store.SavePreferences(settings);
        Settings = settings;
    }

    public JiraCatalog? OpenCatalog()
    {
        if (demo)
            return null;
        if (Settings.Jira is not { } config)
            throw new InvalidOperationException("Connect Jira Cloud first, or type the issue key in Edit row.");
        var token = CredentialVault.Read(config.CredentialTarget)
            ?? throw new InvalidOperationException("The Jira token is missing. Reconnect Jira Cloud.");
        return new JiraCatalog(config, token);
    }

    protected override void ExitThreadCore()
    {
        tick.Stop();
        listenerCancellation.Cancel();
        observer.Observed -= capture.Observe;
        var now = Environment.TickCount64;
        capture.Observe(new CaptureObservation(now, DateTimeOffset.UtcNow, WindowsObserver.LastInput(now), null));
        Flush(true);
        tray.Visible = false;
        review?.Dispose();
        observer.Dispose();
        tray.ContextMenuStrip?.Dispose();
        tray.Dispose();
        tick.Dispose();
        dispatcher.Dispose();
        store.Dispose();
        listenerCancellation.Dispose();
        base.ExitThreadCore();
    }

    private void StartManual(TaskLabel task)
    {
        if (!observer.SessionNotificationsAvailable)
        {
            MessageBox.Show("Windows session notifications are unavailable. Use Add time in the review instead.",
                "Worklog Desk");
            return;
        }
        manual = task;
        interruptedManual = null;
        observer.Sample();
        Flush(true);
        UpdateStatus();
    }

    private void InterruptSession()
    {
        if (manual != null)
        {
            interruptedManual = manual;
            interruptedAt ??= DateTimeOffset.UtcNow;
        }
        manual = null;
        if (pin != null)
        {
            pinSuspendedAt ??= Environment.TickCount64;
            pinConflict = true;
        }
    }

    private void ResumeSession()
    {
        if (interruptedAt is not { } start)
            return;
        interruptedAt = null;
        var end = DateTimeOffset.UtcNow;
        if (end > start)
        {
            var gap = new WorkSpan(Guid.NewGuid(), start, end,
                new CaptureSource("Timer interrupted by Windows", interruptedManual, SpanKind.Idle));
            store.SaveCapture([gap], capture.Checkpoint(), Zone);
            reviewDirty |= review is { } window && window.IntersectsDay(gap);
        }
        if (tray.Visible)
            tray.ShowBalloonTip(5500, "Manual timer interrupted",
                "Resume the timer if work continues. Review the excluded gap if you worked while away.", ToolTipIcon.Info);
        UpdateStatus();
    }

    private TaskLabel? AvailablePin(TaskLabel? mapping = null)
    {
        if (pin == null)
            return null;
        var now = Environment.TickCount64;
        var idle = now - WindowsObserver.LastInput(now);
        if (idle >= Settings.IdleMinutes * 60_000L)
            pinSuspendedAt ??= now;
        if (pin != null && pinSuspendedAt.HasValue && !pinConflict && idle < Settings.IdleMinutes * 60_000L &&
            mapping != null && pin.Client.Length > 0 && mapping.Client.Equals(pin.Client, StringComparison.OrdinalIgnoreCase) &&
            mapping.Issue == pin.Issue)
            pinSuspendedAt = null;
        return pinSuspendedAt.HasValue ? null : pin;
    }

    private void Tick()
    {
        _ = AvailablePin();
        Sample();
        reviewDirty |= Flush(false);
        if (reviewDirty && review is { Visible: true, ContainsFocus: false, WindowState: not FormWindowState.Minimized } &&
            Environment.TickCount64 - lastAutomaticRefresh >= 30_000)
        {
            review.Reload();
            reviewDirty = false;
            lastAutomaticRefresh = Environment.TickCount64;
        }
        if (retentionDay != Today)
        {
            store.ExpireSource(DateTimeOffset.UtcNow);
            store.CheckpointWal();
            retentionDay = Today;
        }
        UpdateStatus();
    }

    private void Sample()
    {
        if (!syntheticIdle)
        {
            observer.Sample(periodic: true);
            return;
        }
        var now = Environment.TickCount64;
        observer.MeasureNativeProbe();
        capture.Observe(new CaptureObservation(now, DateTimeOffset.UtcNow, now - 180_000,
            new CaptureSource("Benchmark foreground")));
    }

    private bool Flush(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && capture.Ready.Count == 0 && (capture.CurrentKind == null || now - lastSave < 30_000))
            return false;
        try
        {
            var changed = review is { } window && capture.Ready.Any(window.IntersectsDay);
            store.SaveCapture(capture.Ready, capture.Checkpoint(), Zone);
            capture.ConfirmSaved();
            lastSave = now;
            return changed;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            tray.ShowBalloonTip(2500, "Worklog Desk", "Local storage is busy. Unsaved time will be retried.", ToolTipIcon.Warning);
            return false;
        }
    }

    private void UpdateStatus()
    {
        var state = demo ? "Demo mode" : RunningManual ? "Manual timer running" :
            Settings.Recording ? capture.CurrentKind == SpanKind.Idle ? "Idle" : "Tracking enabled" : "Paused";
        var pinned = pin == null ? "" : pinSuspendedAt.HasValue ? " · Confirm pin" : $" · {pin.Issue} {pin.Client}".TrimEnd();
        var status = $"Worklog Desk · {state}{pinned}";
        var tooltip = status[..Math.Min(status.Length, 63)];
        if (tray.Text != tooltip)
            tray.Text = tooltip;
        var icon = RunningManual ? SystemIcons.Application : Settings.Recording ? SystemIcons.Information : SystemIcons.Warning;
        if (tray.Icon != icon)
            tray.Icon = icon;
        captureMenu.Text = Settings.Recording ? "Pause foreground tracking" : "Start foreground tracking";
        captureMenu.Enabled = !demo;
        var pinPaused = pin != null && pinSuspendedAt.HasValue;
        var isBack = Environment.TickCount64 - WindowsObserver.LastInput(Environment.TickCount64) <
            Settings.IdleMinutes * 60_000L;
        if (pinPaused && isBack && !pinPauseReported && tray.Visible)
            tray.ShowBalloonTip(3500, "Task pin paused",
                "Confirm the pin before assigning new activity to it.", ToolTipIcon.Info);
        pinPauseReported = pinPaused && (isBack || pinPauseReported);
        review?.SetStatus(state, pinPaused ? null : pin, observer.HotkeysAvailable, interruptedManual != null, pinPaused);
    }

    private void ShowReview(bool selectRemote = false)
    {
        Flush(true);
        if (review is null || review.IsDisposed)
        {
            review = new ReviewWindow(this, store);
            review.FormClosed += (_, _) => review = null;
        }
        if (!review.Visible)
            review.Show();
        if (review.WindowState == FormWindowState.Minimized)
            review.WindowState = FormWindowState.Normal;
        review.Activate();
        observer.Sample();
        Flush(true);
        review.Reload();
        reviewDirty = false;
        if (selectRemote)
            review.SelectLastRemote();
        UpdateStatus();
    }

    private async Task Listen(string instance, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(instance, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                var message = new byte[1];
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    if (await pipe.ReadAsync(message, timeout.Token).ConfigureAwait(false) == 1 && message[0] == 1)
                        dispatcher.BeginInvoke((Action)(() => ShowReview()));
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }
}
