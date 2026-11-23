using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using WorklogDesk.Core;
using WorklogDesk.Windows;

// Runs the actual tray app and Windows Notification Center in an isolated interactive Windows test.
internal static class CompletionWindowsSmoke
{
    public static int Run(string report, bool injectMessages, bool operatorPass, bool expectUnavailable)
    {
        var folder = Directory.CreateTempSubdirectory("WorklogDesk-WindowsSmoke-");
        var result = 1;
        var thread = new Thread(() =>
        {
            try { result = RunOnSta(folder.FullName, report, injectMessages, operatorPass, expectUnavailable); }
            catch (Exception error) { File.AppendAllText(report, $"{error}\r\n"); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        folder.Delete(true);
        return result;
    }

    private static int RunOnSta(string folder, string report, bool injectMessages, bool operatorPass, bool expectUnavailable)
    {
        void Log(string item) => File.AppendAllText(report, $"{DateTimeOffset.Now:HH:mm:ss} {item}\r\n");
        var today = Timeline.DayOf(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        var schedule = Timeline.AtLocalTime(today, new TimeOnly(17, 0), TimeZoneInfo.Local);
        var clock = new SmokeClock { Utc = schedule.AddMinutes(-5) };
        using var app = new TrayApplication(folder, demo: true, "WorklogDesk-CompletionSmoke-" + Guid.NewGuid().ToString("N"),
            showOnStart: false, clock: clock, syntheticNotifications: true, suppressSyntheticBanner: injectMessages);
        app.Apply(app.Settings with { CompletionReminder = new CompletionReminder(true, [today.DayOfWeek], new TimeOnly(17, 0)) });
        app.OpenCompletionReview(today.AddDays(-1));
        var review = Application.OpenForms.OfType<ReviewWindow>().Single();
        review.Text = "Worklog Desk · Synthetic completion test";
        review.TempoOverride = () => throw new InvalidOperationException("Reminder activation opened a Tempo gateway.");
        app.ShowCompletion();
        Log($"Synthetic review initially on {review.SelectedDay:yyyy-MM-dd}. Waiting for cue for {today:yyyy-MM-dd}.");
        var observer = ThreadWindow("Worklog Desk capture");
        if (observer == 0)
            throw new InvalidOperationException("Cannot find the synthetic session observer.");
        var elapsed = Stopwatch.StartNew();
        var stage = 0;
        var accepted = false;
        var completed = false;
        var sawSuspend = false;
        var sawResume = false;
        double? acceptedAt = null;
        var checkedRetention = false;
        var retained = false;
        var injectedActivation = false;
        string? notificationError = null;
        void ObserveSession(bool suspended, bool interactive)
        {
            if (stage == 0 && !interactive)
            {
                stage = 1;
                clock.Utc = schedule;
                Log("Observed a non-interactive Windows session. Cue is now scheduled.");
            }
            if (suspended && !sawSuspend)
            {
                sawSuspend = true;
                Log("Observed native Windows suspend.");
            }
            if (!suspended && sawSuspend && !sawResume)
            {
                sawResume = true;
                Log("Observed native Windows resume. Interactive=" + interactive);
            }
            if (interactive && stage == 1)
            {
                stage = 2;
                Log("Observed return to interactive session.");
            }
        }

        if (operatorPass)
        {
            app.CompletionSessionObserved += ObserveSession;
            Log("READY for operator lock, sleep, wake and unlock.");
        }
        using var timer = new System.Windows.Forms.Timer { Interval = 200 };
        timer.Tick += async (_, _) =>
        {
            var seconds = elapsed.Elapsed.TotalSeconds;
            if (app.CompletionNotificationError is { } failure && notificationError != failure)
            {
                notificationError = failure;
                Log("Notification error: " + failure);
                if (expectUnavailable)
                {
                    completed = Application.OpenForms.OfType<FinishWorklogWindow>().Any(window => window.Visible) &&
                        review.SelectedDay == today.AddDays(-1) && !app.CompletionNotificationAccepted;
                    Log(completed ? "Private tray summary and dated review remained available without notifications."
                        : "FAIL: notification failure interfered with the private summary or review.");
                    timer.Stop();
                    app.ShutDownDiagnostics();
                    return;
                }
            }
            if (seconds > (expectUnavailable ? 30 : operatorPass ? 240 : injectMessages ? 45 : 150))
            {
                Log("Timed out waiting for activation.");
                app.ShutDownDiagnostics();
                return;
            }
            if (operatorPass && stage < 2)
                ObserveSession(sawSuspend && !sawResume, app.CompletionSessionInteractive);
            if (!operatorPass && stage == 0 && seconds >= (injectMessages ? 5 : 15))
            {
                stage++;
                if (injectMessages)
                    _ = SendMessage(observer, 0x02B1, 7, 0); // Lock.
                clock.Utc = schedule;
                if (injectMessages)
                    _ = SendMessage(observer, 0x0218, 4, 0); // Suspend.
                else
                    app.RefreshCompletion();
            }
            if (injectMessages && stage == 1 && seconds >= 9)
            {
                stage++;
                _ = SendMessage(observer, 0x0218, 7, 0); // Resume while locked.
                if (app.CompletionNotificationAccepted)
                {
                    Log("FAIL: resume while locked requested a cue.");
                    app.ShutDownDiagnostics();
                    return;
                }
                Log("No native cue while locked or after resume while locked.");
            }
            if (injectMessages && stage == 2 && seconds >= 12)
            {
                stage++;
                _ = SendMessage(observer, 0x02B1, 8, 0); // Unlock.
            }
            if (!accepted && app.CompletionNotificationAccepted)
            {
                accepted = true;
                acceptedAt = seconds;
                Log("Windows accepted the app notification.");
                if (operatorPass && stage < 2)
                {
                    Log("FAIL: native cue accepted before the session became interactive.");
                    app.ShutDownDiagnostics();
                    return;
                }
            }
            if (acceptedAt is { } at && seconds - at >= 16 && !checkedRetention)
            {
                checkedRetention = true;
                retained = await app.CompletionToastRetainedAsync();
                Log(retained ? "Toast still present in Windows Notification Center after 16 seconds." : "Toast absent from Notification Center.");
                if (injectMessages && app.CompletionToastForDiagnostics is { } cue)
                {
                    app.OpenFromToast(cue.Tag, "invalid-token");
                    if (app.CompletionCueActivated)
                    {
                        Log("FAIL: invalid activation token opened review.");
                        app.ShutDownDiagnostics();
                        return;
                    }
                    app.OpenFromToast(cue.Tag, cue.Token);
                    injectedActivation = true;
                }
            }
            if (accepted && app.CompletionCueActivated && review.SelectedDay == today)
            {
                Log(injectedActivation ? "Injected activation routed the existing review to the intended date."
                    : "Windows activation routed the existing review to the intended date.");
                completed = retained && (!operatorPass || sawSuspend && sawResume);
                if (operatorPass && !completed)
                    Log("Windows activation passed. A native suspend/resume pair was not observed.");
                timer.Stop();
                app.ShutDownDiagnostics();
            }
        };
        timer.Start();
        Application.Run(app);
        return completed ? 0 : 1;
    }

    private static nint ThreadWindow(string title)
    {
        nint selected = 0;
        _ = EnumThreadWindows(GetCurrentThreadId(), (window, _) =>
        {
            var name = new StringBuilder(128);
            _ = GetWindowText(window, name, name.Capacity);
            if (name.ToString() != title)
                return true;
            selected = window;
            return false;
        }, 0);
        return selected;
    }

    private sealed class SmokeClock : TimeProvider
    {
        public DateTimeOffset Utc { get; set; }
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private delegate bool WindowCallback(nint window, nint data);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, WindowCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
