using System.Globalization;
using System.IO;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Identity.Client;
using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal sealed record SentEvidenceResult(SentEvidence[] Items, bool Incomplete);
internal sealed record MeetingResult(Meeting[] Items, bool Incomplete);

/// <summary>Microsoft Graph access for one scope at a time. Behind an interface so tests can fake sign-in.</summary>
internal interface IOutlookAuth
{
    /// <summary>A token for one Graph scope: silent first, interactive only when needed.</summary>
    Task<string> TokenAsync(string scope, nint owner, CancellationToken cancel);
    /// <summary>The signed-in account's display name and email, once known (feeds the mail-title rule).</summary>
    string? DisplayName { get; }
    string? Address { get; }
    Task DisconnectAsync();
}

/// <summary>
/// One MSAL public client per app session with a DPAPI-protected token cache file, so a restart signs in silently.
/// Each scope is requested only when its source is first used, so one missing consent leaves the other working.
/// </summary>
internal sealed class MsalOutlookAuth : IOutlookAuth
{
    public const string CalendarScope = "https://graph.microsoft.com/Calendars.ReadBasic";
    public const string MailScope = "https://graph.microsoft.com/Mail.ReadBasic";
    private readonly IPublicClientApplication client;
    private readonly string cacheFile;

    public MsalOutlookAuth(Guid tenant, Guid application, string folder)
    {
        cacheFile = Path.Combine(folder, "outlook-token.bin");
        client = PublicClientApplicationBuilder.Create(application.ToString("D"))
            .WithAuthority($"https://login.microsoftonline.com/{tenant:D}")
            .WithRedirectUri("http://localhost").Build();
        client.UserTokenCache.SetBeforeAccess(args =>
        {
            try
            {
                if (File.Exists(cacheFile))
                    args.TokenCache.DeserializeMsalV3(ProtectedData.Unprotect(File.ReadAllBytes(cacheFile), null, DataProtectionScope.CurrentUser));
            }
            catch (Exception error) when (error is CryptographicException or IOException or MsalException) { }
        });
        client.UserTokenCache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged)
                return;
            try { File.WriteAllBytes(cacheFile, ProtectedData.Protect(args.TokenCache.SerializeMsalV3(), null, DataProtectionScope.CurrentUser)); }
            catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException) { }
        });
    }

    public string? DisplayName { get; private set; }
    public string? Address { get; private set; }

    public async Task<string> TokenAsync(string scope, nint owner, CancellationToken cancel)
    {
        AuthenticationResult result;
        var account = (await client.GetAccountsAsync()).FirstOrDefault();
        try
        {
            if (account == null)
                throw new MsalUiRequiredException("no_account", "No cached account.");
            result = await client.AcquireTokenSilent([scope], account).ExecuteAsync(cancel);
        }
        catch (MsalUiRequiredException)
        {
            result = await client.AcquireTokenInteractive([scope]).WithParentActivityOrWindow(owner)
                .WithUseEmbeddedWebView(false).ExecuteAsync(cancel);
        }
        Address = result.Account?.Username;
        DisplayName = result.ClaimsPrincipal?.FindFirst("name")?.Value ?? DisplayName;
        return result.AccessToken;
    }

    public async Task DisconnectAsync()
    {
        foreach (var account in await client.GetAccountsAsync())
            await client.RemoveAsync(account);
        try { File.Delete(cacheFile); }
        catch (IOException) { }
        DisplayName = Address = null;
    }
}

internal static class OutlookEvidence
{
    private const string Host = "graph.microsoft.com";
    private const string Folder = "/v1.0/me/mailFolders/sentitems/messages";
    private const string CalendarPath = "/v1.0/me/calendarView";

    internal static async Task<SentEvidenceResult> ReadAsync(HttpClient client, string token, DateOnly day,
        TimeZoneInfo zone, CancellationToken cancel)
    {
        var start = Timeline.DayStart(day, zone);
        var finish = Timeline.DayStart(day.AddDays(1), zone);
        var filter = $"sentDateTime ge {start.UtcDateTime:O} and sentDateTime lt {finish.UtcDateTime:O}";
        string? next = "https://" + Host + Folder + "?$select=subject,sentDateTime&$orderby=sentDateTime%20desc&$top=20&$filter=" +
            Uri.EscapeDataString(filter);
        var found = new List<SentEvidence>();
        for (var page = 0; page < 3 && next != null; page++)
        {
            if (!Uri.TryCreate(next, UriKind.Absolute, out var uri) || !Allowed(uri, Folder, "/v1.0/me/mailFolders('sentitems')/messages") || next.Length > 8_192)
                return new(found.ToArray(), true);
            using var body = await Page(client, token, uri, page, cancel);
            if (body == null)
                return new(found.ToArray(), true);
            foreach (var item in body.RootElement.GetProperty("value").EnumerateArray())
            {
                if (found.Count >= 60)
                    return new(found.ToArray(), true);
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("sentDateTime", out var stamp) ||
                    stamp.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(stamp.GetString(),
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) || time < start || time >= finish)
                    continue;
                var subject = item.TryGetProperty("subject", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                found.Add(new(time, AssistedEvidence.Text(subject ?? "", 160)));
            }
            next = NextLink(body);
        }
        return new(found.ToArray(), next != null);
    }

    /// <summary>
    /// The day's meetings. Cancelled, all-day, free and declined events are skipped; unanswered ones are kept as
    /// unconfirmed. Private and confidential events carry no subject.
    /// </summary>
    internal static async Task<MeetingResult> CalendarAsync(HttpClient client, string token, DateOnly day,
        TimeZoneInfo zone, CancellationToken cancel)
    {
        var start = Timeline.DayStart(day, zone);
        var finish = Timeline.DayStart(day.AddDays(1), zone);
        string? next = "https://" + Host + CalendarPath + "?startDateTime=" + Uri.EscapeDataString(start.UtcDateTime.ToString("O")) +
            "&endDateTime=" + Uri.EscapeDataString(finish.UtcDateTime.ToString("O")) +
            "&$top=50&$select=subject,start,end,showAs,isCancelled,isAllDay,responseStatus,sensitivity,isOrganizer";
        var found = new List<Meeting>();
        var seen = 0;
        for (var page = 0; page < 3 && next != null; page++)
        {
            if (!Uri.TryCreate(next, UriKind.Absolute, out var uri) || !Allowed(uri, CalendarPath) || next.Length > 8_192)
                return new(found.ToArray(), true);
            using var body = await Page(client, token, uri, page, cancel, calendar: true);
            if (body == null)
                return new(found.ToArray(), true);
            foreach (var item in body.RootElement.GetProperty("value").EnumerateArray())
            {
                if (++seen > 50)
                    return new(found.ToArray(), true);
                if (item.ValueKind != JsonValueKind.Object || Flag(item, "isCancelled") || Flag(item, "isAllDay") ||
                    String(item, "showAs") == "free" || !Time(item, "start", out var begin) || !Time(item, "end", out var end) || end <= begin)
                    continue;
                var response = item.TryGetProperty("responseStatus", out var status) && status.ValueKind == JsonValueKind.Object
                    ? String(status, "response") : null;
                if (response == "declined")
                    continue;
                var sensitivity = String(item, "sensitivity");
                var subject = sensitivity is "private" or "confidential" ? "Private meeting" : String(item, "subject") ?? "";
                found.Add(new Meeting(begin, end, AssistedEvidence.Redact(subject, 120),
                    !Flag(item, "isOrganizer") && response is null or "none" or "notResponded"));
            }
            next = NextLink(body);
        }
        return new(found.OrderBy(meeting => meeting.Start).ToArray(), next != null);

        static bool Flag(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        static string? String(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        static bool Time(JsonElement item, string name, out DateTimeOffset time)
        {
            time = default;
            return item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object &&
                String(value, "dateTime") is { } text &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time);
        }
    }

    /// <summary>Reads one page. A first-page failure throws a readable message; a later one returns null (partial).</summary>
    private static async Task<JsonDocument?> Page(HttpClient client, string token, Uri uri, int page, CancellationToken cancel, bool calendar = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (calendar)
            request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
        using var response = await client.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode)
        {
            if (page > 0)
                return null;
            throw new InvalidOperationException($"Outlook returned HTTP {(int)response.StatusCode}. Check sign-in or tenant consent.");
        }
        JsonDocument? parsed = null;
        try { parsed = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancel)); }
        catch (JsonException) { }
        if (parsed != null && parsed.RootElement.ValueKind == JsonValueKind.Object &&
            parsed.RootElement.TryGetProperty("value", out var list) && list.ValueKind == JsonValueKind.Array)
            return parsed;
        parsed?.Dispose();
        if (page > 0)
            return null;
        throw new InvalidOperationException("Outlook returned an unreadable result.");
    }

    private static string? NextLink(JsonDocument body) =>
        body.RootElement.TryGetProperty("@odata.nextLink", out var link) && link.ValueKind == JsonValueKind.String ? link.GetString() : null;

    private static bool Allowed(Uri uri, params string[] paths) => uri.Scheme == "https" && uri.Host == Host &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        paths.Any(path => string.Equals(uri.AbsolutePath, path, StringComparison.OrdinalIgnoreCase));
}
