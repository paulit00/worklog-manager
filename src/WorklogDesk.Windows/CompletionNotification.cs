using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace WorklogDesk.Windows;

internal sealed record CompletionToast(DateOnly Day, CompletionCue Cue, DateTimeOffset Expires, string Tag, string Token)
{
    public static CompletionToast Create(DateOnly day, CompletionCue cue, DateTimeOffset expires) =>
        new(day, cue, expires, Guid.NewGuid().ToString("N")[..16], Guid.NewGuid().ToString("N"));

    public bool Matches(string tag, string token) => Tag == tag && Token == token;
}

// Registration is intentionally lazy so unavailable Windows notifications cannot prevent tray startup or capture.
internal sealed class CompletionNotification(AppNotificationManager manager, Action<string, string> activate)
{
    public static async Task<CompletionNotification?> RegisterAsync(string folder, string displayName, Action<string, string> activate)
    {
        Marshal.ThrowExceptionForHR(WindowsAppRuntime_EnsureIsLoaded());
        if (!AppNotificationManager.IsSupported())
            return null;
        var manager = AppNotificationManager.Default;
        var notifications = new CompletionNotification(manager, activate);
        manager.NotificationInvoked += notifications.OnActivation;
        try
        {
            Directory.CreateDirectory(folder);
            var iconPath = Path.Combine(folder, "notification-icon.png");
            if (!File.Exists(iconPath))
            {
                using var icon = SystemIcons.Application.ToBitmap();
                icon.Save(iconPath, System.Drawing.Imaging.ImageFormat.Png);
            }
            manager.Register(displayName, new Uri(iconPath));
            await manager.RemoveAllAsync();
            return notifications;
        }
        catch
        {
            notifications.Unregister(removeRegistration: false);
            throw;
        }
    }

    public bool Available => manager.Setting == AppNotificationSetting.Enabled;

    public bool Request(CompletionToast cue, bool suppressBanner)
    {
        var review = new AppNotificationButton("Review worklog").AddArgument("tag", cue.Tag).AddArgument("cue", cue.Token);
        var toast = new AppNotificationBuilder().AddArgument("tag", cue.Tag).AddArgument("cue", cue.Token)
            .AddText("Worklog Desk has items to review.")
            .AddButton(review).BuildNotification();
        toast.Tag = cue.Tag;
        toast.Expiration = cue.Expires;
        toast.ExpiresOnReboot = true;
        toast.SuppressDisplay = suppressBanner;
        manager.Show(toast);
        return toast.Id != 0;
    }

    public async Task<bool> IsInCenterAsync(string tag) => (await manager.GetAllAsync()).Any(item => item.Tag == tag);

    public Task RemoveAsync(string tag) => manager.RemoveByTagAsync(tag).AsTask();

    public void Unregister(bool removeRegistration)
    {
        manager.NotificationInvoked -= OnActivation;
        try
        {
            if (removeRegistration)
                manager.UnregisterAll();
            else
                manager.Unregister();
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(error.Message);
        }
    }

    private void OnActivation(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue("tag", out var tag) && args.Arguments.TryGetValue("cue", out var token))
            activate(tag, token);
    }

    // Windows App SDK supplies this entry point for app-local WinRT class registration.
    [DllImport("Microsoft.WindowsAppRuntime.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WindowsAppRuntime_EnsureIsLoaded();
}
