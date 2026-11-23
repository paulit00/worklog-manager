using System.IO;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorklogDesk.Windows;

internal sealed record SentEvidence(DateTimeOffset SentAt, string Subject);

internal enum AiProvider { Claude, OpenAI }

internal sealed record AiService(AiProvider Provider, string Name, Uri Endpoint, string CredentialTarget, string DefaultModel)
{
    public static readonly AiService Claude = new(AiProvider.Claude, "Claude", new("https://api.anthropic.com/v1/messages"),
        "WorklogDesk/AI/Anthropic", "claude-opus-5-5");
    public static readonly AiService OpenAI = new(AiProvider.OpenAI, "OpenAI", new("https://api.openai.com/v1/responses"),
        "WorklogDesk/AI/OpenAI", "gpt-5-mini");
    public static AiService For(AiProvider provider) => provider == AiProvider.Claude ? Claude : OpenAI;
    public static AiService For(string name) => name == OpenAI.Name ? OpenAI : Claude;
    public override string ToString() => Name;
}

/// <summary>The structured answer plus what the provider reported about the call, for the local diagnostics log.</summary>
internal sealed record AiResult(string Text, string Stop, long InputTokens, long OutputTokens, long ThinkingTokens);

/// <summary>Raised when the response stopped at the token limit, so the caller can offer a half-day request.</summary>
internal sealed class AiTooLargeException(string message) : InvalidOperationException(message);

/// <summary>
/// Sends one structured-output request to Claude or OpenAI. The body is built by the caller and previewed to the
/// user byte for byte before this sends it.
/// </summary>
internal sealed class AssistedInference(HttpClient client)
{
    internal const string AnthropicVersion = "2026-10-21";
    internal const string FallbackBeta = "server-side-fallback-2026-07-01";
    internal const string TooLarge = "This day is too large for one request. Reconstruct the morning or the afternoon.";
    // Server-side refusal fallbacks ("default" routing) are accepted only on these models.
    private static readonly HashSet<string> FallbackModels = new(StringComparer.Ordinal)
        { "claude-fable-5-2", "claude-fable-5-1", "claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5" };

    /// <summary>Checks a model name for the provider. Throws ArgumentException with a readable message.</summary>
    public static void CheckModel(AiService service, string model)
    {
        if (!Regex.IsMatch(model, @"\A[a-zA-Z0-9_.:-]{1,80}\z"))
            throw new ArgumentException("Enter a supported model name.");
        if (service.Provider == AiProvider.Claude != model.StartsWith("claude-", StringComparison.Ordinal))
            throw new ArgumentException($"Enter a {service.Name} model name, such as {service.DefaultModel}.");
    }

    /// <summary>Wraps instructions, the facts document and a strict JSON schema into the provider's request body.</summary>
    public static string Wrap(AiService service, string model, string instructions, string facts, string schemaName,
        JsonElement schema, int maxTokens)
    {
        CheckModel(service, model);
        var indented = new JsonSerializerOptions { WriteIndented = true };
        if (service.Provider == AiProvider.OpenAI)
            return JsonSerializer.Serialize(new
            {
                model, store = false, max_output_tokens = maxTokens,
                instructions,
                input = facts,
                text = new { format = new { type = "json_schema", name = schemaName, strict = true, schema } }
            }, indented);
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["system"] = instructions,
            ["messages"] = new[] { new { role = "user", content = facts } },
            ["output_config"] = model.StartsWith("claude-haiku-4", StringComparison.Ordinal)
                ? new { format = new { type = "json_schema", schema } }
                : (object)new { effort = "medium", format = new { type = "json_schema", schema } }
        };
        if (FallbackModels.Contains(model))
            body["fallbacks"] = "default";
        return JsonSerializer.Serialize(body, indented);
    }

    public async Task<AiResult> CompleteAsync(AiService service, string body, string key, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, service.Endpoint);
        if (service.Provider == AiProvider.Claude)
        {
            request.Headers.Add("x-api-key", key);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            using (var sent = JsonDocument.Parse(body))
                if (sent.RootElement.TryGetProperty("fallbacks", out _))
                    request.Headers.Add("anthropic-beta", FallbackBeta);
        }
        else
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancel);
        var raw = await response.Content.ReadAsByteArrayAsync(cancel);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"{service.Name} returned HTTP {(int)response.StatusCode}{ErrorDetail(raw)}. Check the model and API key.");
        try
        {
            using var document = JsonDocument.Parse(raw);
            return service.Provider == AiProvider.Claude ? ClaudeResult(document.RootElement) : OpenAIResult(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException ||
            error is InvalidOperationException and not AiTooLargeException && error.Data["readable"] == null)
        {
            throw new InvalidOperationException("The AI service returned an unreadable result. Your local suggestions are still available.");
        }
    }

    private static AiResult OpenAIResult(JsonElement output)
    {
        var status = output.GetProperty("status").GetString();
        var usage = output.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
        long Count(string name) => usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        var thinking = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("output_tokens_details", out var details) &&
            details.ValueKind == JsonValueKind.Object && details.TryGetProperty("reasoning_tokens", out var reasoning) &&
            reasoning.ValueKind == JsonValueKind.Number ? reasoning.GetInt64() : 0;
        if (status != "completed")
        {
            var reason = output.TryGetProperty("incomplete_details", out var incomplete) && incomplete.ValueKind == JsonValueKind.Object &&
                incomplete.TryGetProperty("reason", out var why) ? why.GetString() : null;
            throw reason == "max_output_tokens" ? new AiTooLargeException(TooLarge)
                : Readable("The AI response was incomplete. Your local suggestions are still available.");
        }
        var content = output.GetProperty("output").EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "message")
            .SelectMany(item => item.GetProperty("content").EnumerateArray()).ToArray();
        if (content.Length != 1 || content[0].GetProperty("type").GetString() != "output_text")
            throw Readable("The AI did not return a suggestions list.");
        return new(content[0].GetProperty("text").GetString() ?? "", status, Count("input_tokens"), Count("output_tokens"), thinking);
    }

    private static AiResult ClaudeResult(JsonElement output)
    {
        var stop = output.GetProperty("stop_reason").GetString() ?? "";
        switch (stop)
        {
            case "end_turn":
                break;
            case "refusal":
                throw Readable("Claude declined this request. Your local suggestions are still available.");
            case "max_tokens":
                throw new AiTooLargeException(TooLarge);
            default:
                throw Readable("The AI response was incomplete. Your local suggestions are still available.");
        }
        // Thinking and fallback blocks may accompany the answer; the structured answer is the single text block.
        var text = output.GetProperty("content").EnumerateArray()
            .Where(block => block.GetProperty("type").GetString() == "text").ToArray();
        if (text.Length != 1)
            throw Readable("The AI did not return a suggestions list.");
        var usage = output.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
        long Count(string name) => usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        return new(text[0].GetProperty("text").GetString() ?? "", stop, Count("input_tokens"), Count("output_tokens"), 0);
    }

    private static InvalidOperationException Readable(string message)
    {
        var error = new InvalidOperationException(message);
        error.Data["readable"] = true;
        return error;
    }

    private static string ErrorDetail(byte[] raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                ? ": " + AssistedEvidence.Redact(message.GetString() ?? "", 200) : "";
        }
        catch (JsonException) { return ""; }
    }

    /// <summary>Appends one line per request to a local log: provider, model, stop reason, token counts and time. No content.</summary>
    public static void LogDiagnostics(string folder, AiService service, string model, AiResult? result, TimeSpan elapsed, string? failure)
    {
        try
        {
            var line = string.Join('\t', DateTimeOffset.Now.ToString("O"), service.Name, model, result?.Stop ?? failure ?? "",
                result?.InputTokens ?? 0, result?.OutputTokens ?? 0, result?.ThinkingTokens ?? 0, (long)elapsed.TotalMilliseconds);
            File.AppendAllText(Path.Combine(folder, "ai-diagnostics.log"), line + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static HttpClient SecureClient(int timeoutSeconds, bool proxy = true) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseProxy = proxy, UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(timeoutSeconds), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
}
