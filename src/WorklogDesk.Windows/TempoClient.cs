using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace WorklogDesk.Windows;

/// <summary>A worklog as Tempo stores it. StartTime is local "HH:mm:ss".</summary>
internal sealed record TempoWorklog(long Id, long IssueId, int Seconds, DateOnly Date, string StartTime, string Description);

/// <summary>What the app asks Tempo to create.</summary>
internal sealed record TempoDraft(long IssueId, string AccountId, int Seconds, DateOnly Date, string StartTime, string Description)
{
    public bool SameAs(TempoWorklog existing) => existing.IssueId == IssueId && existing.Seconds == Seconds &&
        existing.Date == Date && existing.StartTime == StartTime &&
        StringComparer.Ordinal.Equals(existing.Description.Trim(), Description.Trim());
}

/// <summary>
/// The request may or may not have created a worklog (timeout, lost connection, server error, unreadable reply).
/// The line must be checked against Tempo before it is ever sent again.
/// </summary>
internal sealed class TempoUncertainException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Everything the review needs to post a worklog: identity from Jira, issue IDs from Jira, worklogs in Tempo.</summary>
internal interface ITempoGateway : IDisposable
{
    Task<string> AccountAsync(CancellationToken cancel);
    Task<long> IssueIdAsync(string key, CancellationToken cancel);
    Task<IReadOnlyList<TempoWorklog>> DayAsync(string accountId, DateOnly date, CancellationToken cancel);
    Task<TempoWorklog> CreateAsync(TempoDraft draft, CancellationToken cancel);
}

internal sealed class TempoGateway(JiraCatalog jira, TempoClient tempo) : ITempoGateway
{
    public Task<string> AccountAsync(CancellationToken cancel) => jira.AccountAsync(cancel);
    public Task<long> IssueIdAsync(string key, CancellationToken cancel) => jira.IssueIdAsync(key, cancel);
    public Task<IReadOnlyList<TempoWorklog>> DayAsync(string accountId, DateOnly date, CancellationToken cancel) =>
        tempo.DayAsync(accountId, date, cancel);
    public Task<TempoWorklog> CreateAsync(TempoDraft draft, CancellationToken cancel) => tempo.CreateAsync(draft, cancel);

    public void Dispose()
    {
        jira.Dispose();
        tempo.Dispose();
    }
}

/// <summary>Tempo Cloud REST API v4. Reads a day's worklogs and creates new ones; never updates or deletes.</summary>
internal sealed class TempoClient : IDisposable
{
    public static readonly Uri Origin = new("https://api.tempo.io/4/");
    private readonly HttpClient client;

    public TempoClient(string token, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl) || token.Length > 2048)
            throw new ArgumentException("A valid Tempo API token is required.");
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = Origin, Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 4 * 1024 * 1024
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IReadOnlyList<TempoWorklog>> DayAsync(string accountId, DateOnly date, CancellationToken cancel)
    {
        var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = $"worklogs/user/{Uri.EscapeDataString(accountId)}?from={day}&to={day}&limit=1000";
        using var response = await Send(() => client.GetAsync(path, cancel), write: false).ConfigureAwait(false);
        await Fail(response, write: false, cancel).ConfigureAwait(false);
        using var json = await Json(response, write: false, cancel).ConfigureAwait(false);
        return json.RootElement.GetProperty("results").EnumerateArray().Select(Parse).ToArray();
    }

    public async Task<TempoWorklog> CreateAsync(TempoDraft draft, CancellationToken cancel)
    {
        var body = new
        {
            issueId = draft.IssueId,
            timeSpentSeconds = draft.Seconds,
            startDate = draft.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            startTime = draft.StartTime,
            description = draft.Description,
            authorAccountId = draft.AccountId
        };
        using var response = await Send(() => client.PostAsJsonAsync("worklogs", body, cancel), write: true).ConfigureAwait(false);
        await Fail(response, write: true, cancel).ConfigureAwait(false);
        using var json = await Json(response, write: true, cancel).ConfigureAwait(false);
        try { return Parse(json.RootElement); }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new TempoUncertainException("Tempo accepted the request but its reply could not be read.", error);
        }
    }

    public void Dispose() => client.Dispose();

    // A failed write before any response is uncertain: the server may have created the worklog.
    private static async Task<HttpResponseMessage> Send(Func<Task<HttpResponseMessage>> request, bool write)
    {
        try { return await request().ConfigureAwait(false); }
        catch (Exception error) when (write && error is HttpRequestException or TaskCanceledException)
        {
            throw new TempoUncertainException("The connection to Tempo dropped before it confirmed the worklog.", error);
        }
        catch (HttpRequestException error)
        {
            throw new InvalidOperationException("Could not reach Tempo. Check the connection.", error);
        }
        catch (TaskCanceledException error)
        {
            throw new InvalidOperationException("Tempo did not respond in time.", error);
        }
    }

    private static async Task<JsonDocument> Json(HttpResponseMessage response, bool write, CancellationToken cancel)
    {
        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
            return JsonDocument.Parse(bytes);
        }
        catch (Exception error) when (error is JsonException or HttpRequestException or TaskCanceledException)
        {
            if (write)
                throw new TempoUncertainException("Tempo's reply was cut off or unreadable.", error);
            throw new InvalidOperationException("Tempo returned an unreadable reply.", error);
        }
    }

    private static async Task Fail(HttpResponseMessage response, bool write, CancellationToken cancel)
    {
        if (response.IsSuccessStatusCode)
            return;
        var status = (int)response.StatusCode;
        if (write && (status >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout))
            throw new TempoUncertainException($"Tempo returned HTTP {status} while saving.");
        var detail = await Detail(response, cancel).ConfigureAwait(false);
        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Tempo rejected the token. Check it hasn't expired, then reconnect Tempo.",
            HttpStatusCode.Forbidden => "Tempo refused this worklog" + Suffix(detail, "the token or account lacks permission, or the period is locked"),
            HttpStatusCode.TooManyRequests => "Tempo is limiting requests. Nothing was saved; try again in a minute.",
            _ when status >= 300 && status < 400 => "Tempo redirected the request; nothing was sent.",
            _ => $"Tempo didn't accept the worklog (HTTP {status})" + Suffix(detail, "")
        });

        static string Suffix(string detail, string fallback) =>
            detail.Length > 0 ? ": " + detail : fallback.Length > 0 ? ": " + fallback + "." : ".";
    }

    private static async Task<string> Detail(HttpResponseMessage response, CancellationToken cancel)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                return string.Join("; ", errors.EnumerateArray()
                    .Select(item => item.TryGetProperty("message", out var message) ? message.GetString() : null)
                    .Where(message => !string.IsNullOrWhiteSpace(message)).Take(3));
        }
        catch (Exception error) when (error is JsonException or HttpRequestException or TaskCanceledException) { }
        return "";
    }

    private static TempoWorklog Parse(JsonElement item)
    {
        var description = item.TryGetProperty("description", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() ?? "" : "";
        return new TempoWorklog(item.GetProperty("tempoWorklogId").GetInt64(),
            item.GetProperty("issue").GetProperty("id").GetInt64(), item.GetProperty("timeSpentSeconds").GetInt32(),
            DateOnly.ParseExact(item.GetProperty("startDate").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            item.TryGetProperty("startTime", out var start) && start.ValueKind == JsonValueKind.String ? start.GetString() ?? "" : "",
            description);
    }
}
