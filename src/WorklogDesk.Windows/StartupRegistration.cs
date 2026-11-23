using Microsoft.Win32;

namespace WorklogDesk.Windows;

/// <summary>Starts Worklog Desk at sign-in through the current user's Run key. No admin rights needed.</summary>
internal static class StartupRegistration
{
    private const string Name = "WorklogDesk";
    internal static string RunKey { get; set; } = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) is string command && command.Length > 0;
    }

    /// <summary>The registered command, so moving the app can be detected and re-registered.</summary>
    public static string? Command()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) as string;
    }

    public static void Enable(string executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(Name, $"\"{Path.GetFullPath(executable)}\"", RegistryValueKind.String);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(Name, throwOnMissingValue: false);
    }
}
