using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal sealed class WindowsObserver : NativeWindow, IDisposable
{
    private const int ForegroundEvent = 3;
    private const int WmHotkey = 0x0312;
    private const int WmSessionChange = 0x02B1;
    private const int WmPowerBroadcast = 0x0218;
    private const int WmForegroundSample = 0x8001;
    private const uint ShortcutModifiers = 0x4007; // Alt+Ctrl+Shift, without auto-repeat.
    private readonly WinEventCallback callback;
    private readonly nint hook;
    private readonly nint powerRegistration;
    private readonly bool notifications;
    private bool locked;
    private bool suspended;
    private bool ending;
    private nint observedWindow;
    private long? observedInput;
    private bool disposed;

    public event Action<CaptureObservation>? Observed;
    public event Action<int>? Shortcut;
    public event Action? SessionInterrupted;
    public event Action? SessionBoundaryObserved;
    public event Action? SessionResumed;
    public event Action? CompletionClockChanged;
    public event Action? PinConflict;
    public Func<Preferences>? Preferences { get; init; }
    public Func<TaskLabel?, TaskLabel?>? Pin { get; init; }
    public Func<TaskLabel?>? Manual { get; init; }
    public bool SessionNotificationsAvailable => notifications;
    public bool HotkeysAvailable { get; }

    public bool CanRequestCompletionCue => notifications && !locked && !suspended && !ending && IsCurrentSessionInteractive();
    public bool IsSuspended => suspended || ending;

    public WindowsObserver(bool registerShortcuts)
    {
        CreateHandle(new CreateParams { Caption = "Worklog Desk capture" });
        notifications = WTSRegisterSessionNotification(Handle, 0);
        powerRegistration = RegisterSuspendResumeNotification(Handle, 0); // DEVICE_NOTIFY_WINDOW_HANDLE
        callback = OnForeground;
        hook = SetWinEventHook(ForegroundEvent, ForegroundEvent, 0, callback, 0, 0, 0);
        var review = registerShortcuts && RegisterHotKey(Handle, 1, ShortcutModifiers, (uint)Keys.L);
        var pin = registerShortcuts && RegisterHotKey(Handle, 2, ShortcutModifiers, (uint)Keys.P);
        var capture = registerShortcuts && RegisterHotKey(Handle, 3, ShortcutModifiers, (uint)Keys.N);
        HotkeysAvailable = review && pin && capture;
    }

    public void Sample(bool periodic = false)
    {
        var tick = Environment.TickCount64;
        var foreground = GetForegroundWindow();
        var source = GetSource(foreground);
        var disagrees = periodic && observedWindow != 0 && observedWindow != foreground &&
            Preferences?.Invoke().Recording == true && !locked && !suspended && source?.Kind != SpanKind.Manual;
        observedWindow = foreground;
        var lastInput = ReadLastInput(tick);
        if (lastInput.HasValue)
            observedInput = lastInput.Value;
        if (lastInput == null && source?.Kind == SpanKind.Foreground)
            source = source with { Kind = SpanKind.Uncertain };
        Observed?.Invoke(new CaptureObservation(tick, DateTimeOffset.UtcNow, lastInput ?? tick, source, disagrees));
    }

    public static long LastInput(long tick) => ReadLastInput(tick) ?? tick;

    private static long? ReadLastInput(long tick)
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? tick - unchecked((uint)tick - info.Tick) : null;
    }

    protected override void WndProc(ref Message message)
    {
        switch (message.Msg)
        {
            case 0x0011: // WM_QUERYENDSESSION
                ending = true;
                SessionInterrupted?.Invoke();
                Sample();
                SessionBoundaryObserved?.Invoke();
                message.Result = 1;
                return;
            case 0x0016: // WM_ENDSESSION
                if (message.WParam == 0)
                {
                    ending = false;
                    Sample();
                    if (!locked && !suspended)
                        SessionResumed?.Invoke();
                    CompletionClockChanged?.Invoke();
                }
                break;
            case WmHotkey:
                Shortcut?.Invoke((int)message.WParam);
                break;
            case WmForegroundSample:
                ReceiveForeground(message.WParam, unchecked((uint)(long)message.LParam));
                break;
            case WmSessionChange:
                if ((int)message.WParam is 2 or 4 or 6 or 7)
                {
                    locked = true;
                    SessionInterrupted?.Invoke();
                    Sample();
                    SessionBoundaryObserved?.Invoke();
                }
                if ((int)message.WParam is 1 or 3 or 8)
                {
                    locked = false;
                    Sample();
                    if (!suspended && !ending)
                        SessionResumed?.Invoke();
                }
                CompletionClockChanged?.Invoke();
                break;
            case WmPowerBroadcast:
                if ((int)message.WParam is 4 or 6 or 7 or 18)
                {
                    suspended = (int)message.WParam == 4;
                    if (suspended)
                        SessionInterrupted?.Invoke();
                    Sample();
                    if (suspended)
                        SessionBoundaryObserved?.Invoke();
                    else if (!locked && !ending)
                        SessionResumed?.Invoke();
                    CompletionClockChanged?.Invoke();
                    message.Result = 1;
                    return;
                }
                break;
            case 0x001E: // WM_TIMECHANGE
                CompletionClockChanged?.Invoke();
                break;
        }
        base.WndProc(ref message);
    }

    private static bool IsCurrentSessionInteractive()
    {
        const int connectState = 8;
        if (!WTSQuerySessionInformation(0, -1, connectState, out var state, out var bytes))
            return false;
        try
        {
            if (bytes < sizeof(int) || Marshal.ReadInt32(state) != 0) // WTSActive
                return false;
        }
        finally { WTSFreeMemory(state); }

        const uint readDesktopObjects = 1;
        const int objectName = 2;
        var desktop = OpenInputDesktop(0, false, readDesktopObjects);
        if (desktop == 0)
            return false;
        try
        {
            var name = new StringBuilder(64);
            return GetUserObjectInformation(desktop, objectName, name, name.Capacity * sizeof(char), out _) &&
                string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (hook != 0)
            UnhookWinEvent(hook);
        for (var id = 1; id <= 3; id++)
            UnregisterHotKey(Handle, id);
        if (notifications)
            WTSUnRegisterSessionNotification(Handle);
        if (powerRegistration != 0)
            UnregisterSuspendResumeNotification(powerRegistration);
        DestroyHandle();
    }

    private void OnForeground(nint registration, uint eventType, nint window, int objectId,
        int childId, uint threadId, uint eventTime)
    {
        if (!disposed)
            PostMessage(Handle, WmForegroundSample, window, unchecked((nint)(long)eventTime));
    }

    private void ReceiveForeground(nint window, uint eventTime)
    {
        var now = Environment.TickCount64;
        var age = unchecked((uint)now - eventTime);
        if (age > 60_000)
        {
            Sample(periodic: true);
            return;
        }
        var tick = now - age;
        var actualInput = ReadLastInput(now);
        var validInput = actualInput.HasValue && actualInput.Value <= tick ? actualInput : observedInput;
        if (validInput.HasValue && validInput.Value <= tick)
            observedInput = validInput;
        else
            validInput = null;
        observedWindow = window;
        var source = GetSource(window);
        if (source?.Kind == SpanKind.Foreground && !validInput.HasValue)
            source = source with { Kind = SpanKind.Uncertain };
        if (source == null && !locked && !suspended && !ending && window != 0 && !IsWindow(window) &&
            Preferences?.Invoke().Recording == true)
            source = new CaptureSource("Unobserved focus", Kind: SpanKind.Uncertain);
        Observed?.Invoke(new CaptureObservation(tick, DateTimeOffset.UtcNow.AddMilliseconds(-age),
            validInput ?? tick, source));
    }

    private CaptureSource? GetSource(nint window)
    {
        if (locked || suspended || ending)
            return null;
        if (Manual?.Invoke() is { } manual)
            return new CaptureSource("Manual timer", manual, SpanKind.Manual);
        var preferences = Preferences?.Invoke();
        if (window == 0 || preferences?.Recording != true)
            return null;
        GetWindowThreadProcessId(window, out var processId);
        if (processId == (uint)Environment.ProcessId || processId == 0)
            return null;
        var app = AppName(processId);
        var family = app switch
        {
            "chrome" or "msedge" or "firefox" or "brave" => preferences.Browser ? "Browser" : "",
            "code" or "code - insiders" => preferences.Developer ? "VS Code" : "",
            "windowsterminal" or "powershell" or "pwsh" or "cmd" or "conhost" or "claude" or "codex"
                => preferences.Developer ? "Terminal / AI" : "",
            "mstsc" or "msrdc" => preferences.Remote ? "Remote Desktop" : "",
            "outlook" or "olk" => preferences.Communications ? "Outlook" : "",
            "ms-teams" or "teams" => preferences.Communications ? "Teams" : "",
            _ => ""
        };
        if (family.Length == 0)
            return null;

        // The raw title stays in memory. Only its normalized form is kept, encrypted, as review context.
        var raw = preferences.WindowTitles || preferences.Mappings is { Length: > 0 } ? ReadTitle(window) : "";
        var context = preferences.WindowTitles ? WindowContext.Normalize(raw) : "";
        var mapped = Match(raw, family, app, preferences.Mappings);
        // Remote Desktop titles are host names such as BC-01, so they never count as issue keys.
        if (preferences.WindowTitles && family != "Remote Desktop" &&
            WindowContext.DetectIssue(raw, Projects(preferences.Jira)) is { } key)
        {
            var summary = WindowContext.JiraSummary(context, key);
            mapped = (mapped ?? new TaskLabel()) with
            {
                Issue = key,
                Description = summary.Length > 0 ? summary : mapped?.Issue == key ? mapped.Description : ""
            };
        }
        if (family is "Outlook" or "Teams")
            return new CaptureSource(family, mapped, Context: context);
        // Mail and chat in a browser tab are labelled so their titles follow the mail and Teams privacy rules.
        // Mappings and pinning above still saw the plain "Browser" family; like desktop Outlook and Teams, these are never pinned.
        if (family == "Browser" && WindowContext.WebApp(raw) is { } web)
            return new CaptureSource("Browser · " + web, mapped, Context: context);
        var pinned = Pin?.Invoke(mapped);
        if (mapped != null && pinned != null && Conflicts(mapped, pinned))
        {
            PinConflict?.Invoke();
            return new CaptureSource(family + " · pin conflict", Kind: SpanKind.Uncertain, Context: context);
        }
        return new CaptureSource(family, pinned ?? mapped, Context: context);
    }

    private string? projectText;
    private HashSet<string>? projectSet;

    private HashSet<string>? Projects(JiraSettings? jira)
    {
        var text = jira?.Projects ?? "";
        if (!ReferenceEquals(text, projectText))
        {
            projectText = text;
            projectSet = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(key => key.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        }
        return projectSet;
    }

    private static string ReadTitle(nint window)
    {
        var title = new StringBuilder(512);
        return GetWindowText(window, title, title.Capacity) > 0 ? title.ToString() : "";
    }

    private string AppName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.ProcessName.ToLowerInvariant();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

    public void MeasureNativeProbe()
    {
        _ = GetForegroundWindow();
        GetWindowThreadProcessId(Handle, out var processId);
        _ = AppName(processId);
        var title = new StringBuilder(512);
        _ = GetWindowText(Handle, title, title.Capacity);
        _ = ReadLastInput(Environment.TickCount64);
    }

    private static TaskLabel? Match(string title, string family, string app, SourceMapping[]? mappings)
    {
        if (mappings == null || title.Length == 0 || (family is not ("VS Code" or "Remote Desktop") && app != "msedge"))
            return null;
        var candidates = mappings.Where(mapping => mapping.App == family).ToArray();
        if (candidates.Length == 0)
            return null;
        var parts = title.Split([" - ", " — "], StringSplitOptions.TrimEntries);
        var matches = candidates.Where(candidate => parts.Contains(candidate.Alias, StringComparer.OrdinalIgnoreCase))
            .Select(candidate => candidate.Task).Distinct().Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool Conflicts(TaskLabel map, TaskLabel pin) =>
        (map.Issue.Length > 0 && pin.Issue.Length > 0 && map.Issue != pin.Issue) ||
        (map.Client.Length > 0 && pin.Client.Length > 0 &&
            !StringComparer.OrdinalIgnoreCase.Equals(map.Client, pin.Client));

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Tick; }

    private delegate void WinEventCallback(nint hook, uint eventType, nint window, int objectId,
        int childId, uint eventThread, uint eventTime);

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo input);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder title, int max);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max,
        nint module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(nint window, uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(nint window);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(nint server, int session, int infoClass, out nint buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint buffer);
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern nint RegisterSuspendResumeNotification(nint recipient, uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterSuspendResumeNotification(nint registration);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(nint desktop, int index, StringBuilder buffer, int length, out int needed);
}
