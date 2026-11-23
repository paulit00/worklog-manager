using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Identity.Client;
using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal sealed record SentEvidenceResult(SentEvidence[] Items, bool Incomplete);

internal static class OutlookEvidence
{
    private const string Host = "graph.microsoft.com";
    private const string Folder = "/v1.0/me/mailFolders/sentitems/messages";

    public static async Task<SentEvidenceResult> SignInAsync(Guid tenant, Guid application, nint owner, DateOnly day,
        TimeZoneInfo zone, CancellationToken cancel)
    {
        var auth = PublicClientApplicationBuilder.Create(application.ToString("D"))
            .WithAuthority($"https://login.microsoftonline.com/{tenant:D}")
            .WithRedirectUri("http://localhost").Build();
        var token = await auth.AcquireTokenInteractive(["https://graph.microsoft.com/Mail.ReadBasic"])
            .WithParentActivityOrWindow(owner).WithUseEmbeddedWebView(false).ExecuteAsync(cancel);
        using var client = AssistedInference.SecureClient(15);
        return await ReadAsync(client, token.AccessToken, day, zone, cancel);
    }

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
            if (!Uri.TryCreate(next, UriKind.Absolute, out var uri) || !IsSentFolder(uri) || next.Length > 8_192)
                return new(found.ToArray(), true);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, cancel);
            if (!response.IsSuccessStatusCode)
            {
                if (page > 0)
                    return new(found.ToArray(), true);
                throw new InvalidOperationException($"Outlook returned HTTP {(int)response.StatusCode}. Check sign-in or tenant consent.");
            }
            JsonDocument? parsed = null;
            try { parsed = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancel)); }
            catch (JsonException) { }
            using var body = parsed;
            if (body == null || body.RootElement.ValueKind != JsonValueKind.Object ||
                !body.RootElement.TryGetProperty("value", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                if (page > 0)
                    return new(found.ToArray(), true);
                throw new InvalidOperationException("Outlook returned an unreadable result.");
            }
            foreach (var item in list.EnumerateArray())
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
            next = body.RootElement.TryGetProperty("@odata.nextLink", out var link) && link.ValueKind == JsonValueKind.String
                ? link.GetString() : null;
        }
        return new(found.ToArray(), next != null);
    }

    private static bool IsSentFolder(Uri uri) => uri.Scheme == "https" && uri.Host == Host &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        (string.Equals(uri.AbsolutePath, Folder, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.AbsolutePath, "/v1.0/me/mailFolders('sentitems')/messages", StringComparison.OrdinalIgnoreCase));
}
