using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WorklogDesk.Windows;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--mcp")
        {
            Environment.ExitCode = WorklogMcp.Run(args[1..]);
            return;
        }
        // Only the running tray session can accept a notification token. A COM-launched stale toast must never
        // run normal startup or send the unconditional open-review message to a different instance.
        if (IsColdNotificationActivation(args))
            return;
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, _) =>
            MessageBox.Show("Worklog Desk could not complete that action. Your saved entries are still local.",
                "Worklog Desk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        if (args.Length == 3 && args[0] == "--ui-benchmark")
        {
            Benchmark.CaptureView(args[1], args[2]);
            return;
        }
        var demo = args.Contains("--demo", StringComparer.Ordinal);
        if (args.Contains("--benchmark", StringComparer.Ordinal))
        {
            Benchmark.Run(args);
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var instance = "WorklogDesk." + identity.User!.Value +
            (demo ? ".Demo" : args.Contains("--idle-benchmark", StringComparer.Ordinal) ? ".Benchmark" : "");
        var rules = new MutexSecurity();
        rules.AddAccessRule(new MutexAccessRule(identity.User!, MutexRights.FullControl, AccessControlType.Allow));
        using var mutex = MutexAcl.Create(false, @"Global\" + instance, out _, rules);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.Zero); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                ShowExisting(instance);
                return;
            }

            var folder = demo
                ? Path.Combine(Path.GetTempPath(), "WorklogDesk-demo-" + Guid.NewGuid().ToString("N"))
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorklogDesk");
            if (args.Length == 2 && args[0] == "--idle-benchmark")
            {
                folder = Path.GetFullPath(args[1]);
                using (var store = new WorkStore(folder))
                    store.SavePreferences(new Preferences(Recording: true, Browser: false, Developer: false,
                        Remote: false, Communications: false));
                using var idle = new TrayApplication(folder, false, instance, showOnStart: false, diagnostics: true,
                    syntheticIdle: true);
                idle.WarmReviewForIdleMeasurement();
                Application.Run(idle);
                return;
            }
            using var app = new TrayApplication(folder, demo, instance);
            Application.Run(app);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Worklog Desk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (acquired)
                mutex.ReleaseMutex();
        }
    }

    internal static bool IsColdNotificationActivation(IEnumerable<string> args) =>
        args.Any(arg => arg.StartsWith("----AppNotificationActivated:", StringComparison.Ordinal));

    private static void ShowExisting(string instance)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", instance, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(500);
            pipe.WriteByte(1);
            pipe.Flush();
        }
        catch (IOException) { }
        catch (TimeoutException)
        {
            MessageBox.Show("Worklog Desk already has a running session.", "Worklog Desk");
        }
    }
}
