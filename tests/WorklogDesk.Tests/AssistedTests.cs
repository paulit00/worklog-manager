using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using WorklogDesk.Core;
using WorklogDesk.Windows;

internal static class AssistedTests
{
    private static readonly DateOnly Day = new(2026, 1, 1);
    private static DateTimeOffset Origin => Timeline.DayStart(Day, TimeZoneInfo.Utc);

    public static void StaleAssignment() => WithFolder(folder =>
    {
        using var store = new WorkStore(folder, inMemory: true);
        var first = Row(0, 600, "VS Code");
        var second = Row(600, 1200, "Teams");
        store.ReplaceRows([], [first, second]);

        // A fresh snapshot can be assigned and then undone through the guarded path.
        var snapshot = store.Rows(Day).Single(row => row.Id == first.Id);
        var assigned = snapshot with { Status = Disposition.Billable, Task = new TaskLabel("Client", "BC-7") };
        store.ReplaceRows([], [assigned], [snapshot]);
        var saved = store.Rows(Day).Single(row => row.Id == first.Id);
        Equal("BC-7", saved.Task.Issue);
        store.ReplaceRows([], [saved with { Status = Disposition.Unassigned, Task = new TaskLabel("", "") }], [saved]);
        Equal(Disposition.Unassigned, store.Rows(Day).Single(row => row.Id == first.Id).Status);

        // Another edit lands after suggestions were made; the stale suggestion must be refused.
        var stale = store.Rows(Day).Single(row => row.Id == second.Id);
        store.ReplaceRows([], [stale with { Status = Disposition.Internal, Task = new TaskLabel("Internal", "") }]);
        Throws<InvalidOperationException>(() => store.ReplaceRows([],
            [stale with { Status = Disposition.Billable, Task = new TaskLabel("Client", "BC-8") }], [stale]), "changed");
        Equal(Disposition.Internal, store.Rows(Day).Single(row => row.Id == second.Id).Status);

        // Only status and issue may change, only to a billable Jira key, and nothing may be removed.
        var current = store.Rows(Day).Single(row => row.Id == first.Id);
        var assign = current with { Status = Disposition.Billable, Task = new TaskLabel("Client", "BC-7") };
        foreach (var change in new[]
                 {
                     assign with { End = current.End.AddMinutes(-1) },
                     assign with { Kind = SpanKind.Manual },
                     assign with { App = "Teams" },
                     assign with { Context = "rewritten" },
                     current with { Status = Disposition.Internal, Task = new TaskLabel("Internal", "") },
                     current with { Status = Disposition.Excluded },
                     current with { Status = Disposition.Billable, Task = new TaskLabel("Client", "") },
                     assign with { Approved = true }
                 })
            Throws<InvalidOperationException>(() => store.ReplaceRows([], [change], [current]), "only assign");
        Throws<InvalidOperationException>(() => store.ReplaceRows([second.Id], [assign], [current]), "only assign");
        Equal(2, store.Rows(Day).Count);
        Equal(Disposition.Unassigned, store.Rows(Day).Single(row => row.Id == first.Id).Status);
    });

    public static void ProtectedTime() => WithFolder(folder =>
    {
        using var store = new WorkStore(folder, inMemory: true);
        var entered = Row(0, 900, "VS Code") with { Status = Disposition.Billable, Task = new TaskLabel("Client", "BC-5") };
        var open = Row(900, 1500, "VS Code");
        store.ReplaceRows([], [entered, open]);
        store.RecordExport(Day, Timeline.Preview(store.Rows(Day), RoundingMode.IssueDay));
        var snapshot = store.Rows(Day).Single(row => row.Id == open.Id);
        Throws<InvalidOperationException>(() => store.ReplaceRows([],
            [snapshot with { Status = Disposition.Billable, Task = new TaskLabel("Client", "BC-5") }], [snapshot]), "copied, or entered");
        Equal(Disposition.Unassigned, store.Rows(Day).Single(row => row.Id == open.Id).Status);

        // Unrelated issue with no overlap is still allowed.
        store.ReplaceRows([], [snapshot with { Status = Disposition.Billable, Task = new TaskLabel("Client", "BC-9") }], [snapshot]);
        Equal("BC-9", store.Rows(Day).Single(row => row.Id == open.Id).Task.Issue);
    });

    public static void MalformedResponses()
    {
        var groups = AssistedEvidence.Group([Row(0, 600, "VS Code"), Row(600, 1200, "Teams")]);
        Equal(2, groups.Length);
        JiraIssue[] candidates = [new("BC-7", "Fix posting", "In Progress")];
        string Proposal(string id, string action, string issue = "", string title = "", string reason = "VS Code evidence",
            string description = "") => JsonSerializer.Serialize(new
            {
                evidence_id = id, action, issue, title, reason, description
            });
        string Wrap(params string[] items) => $"{{\"proposals\":[{string.Join(',', items)}]}}";

        var valid = AssistedInference.Validate(Wrap(Proposal(groups[0].Id, "assign", "BC-7"),
            Proposal(groups[1].Id, "draft", title: "Answer request", description: "See https://evil.example/x for details")),
            groups, candidates);
        Equal(2, valid.Length);
        True(!valid[1].Description.Contains("evil.example"));

        string[] rejected =
        [
            Wrap(Proposal("activity-99", "review")),
            Wrap(Proposal(groups[0].Id, "review"), Proposal(groups[0].Id, "review")),
            Wrap(Proposal(groups[0].Id, "assign", "BC-404")),
            Wrap(Proposal(groups[0].Id, "assign")),
            Wrap(Proposal(groups[0].Id, "review", "BC-7")),
            Wrap(Proposal(groups[0].Id, "draft")),
            Wrap(Proposal(groups[0].Id, "delete")),
            Wrap(Proposal(groups[0].Id, "review", reason: "")),
            Wrap(Proposal(groups[0].Id, "review", title: "unexpected")),
            """{"proposals":{"evidence_id":"activity-1"}}""",
            """{"proposals":[{"evidence_id":"activity-1"}]}""",
            """{"proposals":[{"evidence_id":1,"action":"review","issue":"","title":"","reason":"x","description":""}]}""",
            """{"other":[]}""",
            "not json"
        ];
        foreach (var json in rejected)
            Throws<InvalidOperationException>(() => AssistedInference.Validate(json, groups, candidates), "invalid suggestion");

        // Timing-review groups accept only "review".
        var approvedNeighbour = Row(1200, 1500, "VS Code") with { Kind = SpanKind.Uncertain };
        var uncertain = new EvidenceGroup("activity-9", [approvedNeighbour], null);
        ThrowsHandled(() => AssistedInference.Validate(Wrap(Proposal("activity-9", "assign", "BC-7")), [uncertain], candidates), "uncertain");

        // End-to-end response envelope handling.
        var body = AssistedInference.BuildRequest("gpt-test", groups, candidates, false, false, []);
        string Envelope(string status, string text, int parts = 1) => JsonSerializer.Serialize(new
        {
            status,
            output = new object[]
            {
                new { type = "reasoning", summary = Array.Empty<object>() },
                new { type = "message", content = Enumerable.Range(0, parts).Select(_ => new { type = "output_text", text }).ToArray() }
            }
        });
        Equal(1, Suggest(HttpStatusCode.OK, Envelope("completed", Wrap(Proposal(groups[0].Id, "review")))).Length);
        string[] envelopes =
        [
            Envelope("incomplete", Wrap(Proposal(groups[0].Id, "review"))),
            Envelope("completed", Wrap(Proposal(groups[0].Id, "review")), parts: 2),
            Envelope("completed", Wrap(Proposal(groups[0].Id, "review")), parts: 0),
            """{"status":"completed"}""",
            """{"status":"completed","output":[{"type":"message","content":[{"type":"refusal","refusal":"no"}]}]}""",
            "<html>gateway</html>"
        ];
        foreach (var envelope in envelopes)
        {
            try { Suggest(HttpStatusCode.OK, envelope); }
            catch (InvalidOperationException error) when (!error.Message.Contains("element", StringComparison.OrdinalIgnoreCase) &&
                error.Message.Contains("AI", StringComparison.Ordinal)) { continue; }
            throw new Exception($"Expected a readable rejection for {envelope}.");
        }
        Throws<InvalidOperationException>(() => Suggest(HttpStatusCode.Unauthorized, "{}"), "HTTP 401. Check");
        Throws<InvalidOperationException>(() => Suggest(HttpStatusCode.BadRequest,
            """{"error":{"message":"Invalid schema for response_format; see https://platform.example/x"}}"""),
            "HTTP 400: Invalid schema for response_format; see [redacted]");

        AssistedProposal[] Suggest(HttpStatusCode code, string response)
        {
            using var handler = new StubHandler(request =>
            {
                Equal("api.openai.com", request.RequestUri!.Host);
                Equal("Bearer", request.Headers.Authorization!.Scheme);
                return new HttpResponseMessage(code) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
            });
            using var client = new HttpClient(handler);
            return new AssistedInference(client).SuggestAsync(body, "sk-test", groups, candidates, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
    }

    public static void ClaudeProvider()
    {
        var groups = AssistedEvidence.Group([Row(0, 600, "VS Code") with { Context = "private title" }]);
        JiraIssue[] candidates = [new("BC-7", "Fix posting", "In Progress")];
        var body = AssistedInference.BuildRequest(AiService.Claude, "claude-opus-5-5", groups, candidates, false, false, []);
        using (var request = JsonDocument.Parse(body))
        {
            var root = request.RootElement;
            Equal("claude-opus-5-5", root.GetProperty("model").GetString());
            Equal("default", root.GetProperty("fallbacks").GetString());
            Equal("json_schema", root.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
            Equal("medium", root.GetProperty("output_config").GetProperty("effort").GetString());
            True(root.GetProperty("system").GetString()!.Contains("untrusted data"));
            True(!root.TryGetProperty("thinking", out _) && !root.TryGetProperty("temperature", out _) && !body.Contains("private title"));
        }
        // No fallback parameter (or beta header) for models that don't accept it; no effort on Haiku 4.5.
        var haiku = AssistedInference.BuildRequest(AiService.Claude, "claude-haiku-4-5", groups, candidates, false, false, []);
        True(!haiku.Contains("\"fallbacks\"") && !haiku.Contains("\"effort\""));
        Throws<ArgumentException>(() => AssistedInference.BuildRequest(AiService.Claude, "gpt-5-mini", groups, [], false, false, []), "Claude model");
        Throws<ArgumentException>(() => AssistedInference.BuildRequest(AiService.OpenAI, "claude-opus-5-5", groups, [], false, false, []), "OpenAI model");

        var answer = JsonSerializer.Serialize(new
        {
            proposals = new[] { new { evidence_id = groups[0].Id, action = "assign", issue = "BC-7", title = "", reason = "VS Code evidence", description = "" } }
        });
        string Envelope(string stop, params object[] content) => JsonSerializer.Serialize(new { stop_reason = stop, content });
        var text = new { type = "text", text = answer };
        HttpRequestMessage? seen = null;
        var proposals = Suggest(HttpStatusCode.OK, Envelope("end_turn", new { type = "thinking", thinking = "" },
            new { type = "fallback", from = new { model = "a" }, to = new { model = "b" } }, text), body, request => seen = request);
        Equal("BC-7", proposals.Single().Issue);
        Equal("api.anthropic.com", seen!.RequestUri!.Host);
        Equal("sk-ant-test", seen.Headers.GetValues("x-api-key").Single());
        Equal(AssistedInference.AnthropicVersion, seen.Headers.GetValues("anthropic-version").Single());
        Equal(AssistedInference.FallbackBeta, seen.Headers.GetValues("anthropic-beta").Single());
        True(seen.Headers.Authorization == null);
        Suggest(HttpStatusCode.OK, Envelope("end_turn", text), haiku, request => seen = request);
        True(!seen!.Headers.Contains("anthropic-beta"));

        Throws<InvalidOperationException>(() => Suggest(HttpStatusCode.OK, Envelope("refusal"), body), "Claude declined");
        Throws<InvalidOperationException>(() => Suggest(HttpStatusCode.OK, Envelope("max_tokens", text), body), "incomplete");
        Throws<InvalidOperationException>(() => Suggest(HttpStatusCode.OK, Envelope("end_turn", text, text), body), "suggestions list");
        Throws<InvalidOperationException>(() => Suggest(HttpStatusCode.OK, """{"content":[]}""", body), "unreadable");
        Throws<InvalidOperationException>(() => Suggest(HttpStatusCode.Unauthorized,
            """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", body),
            "Claude returned HTTP 401: invalid x-api-key");

        AssistedProposal[] Suggest(HttpStatusCode code, string response, string request, Action<HttpRequestMessage>? inspect = null)
        {
            using var handler = new StubHandler(message =>
            {
                inspect?.Invoke(message);
                return new HttpResponseMessage(code) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
            });
            using var client = new HttpClient(handler);
            return new AssistedInference(client).SuggestAsync(AiService.Claude, request, "sk-ant-test", groups, candidates,
                CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    public static void RequestPrivacy()
    {
        var rows = new[]
        {
            Row(0, 600, "VS Code") with { Context = "BC-7 secret customer file https://intra.example/doc" },
            Row(600, 1200, "Outlook") with { Context = "Re: contract renewal" }
        };
        var groups = AssistedEvidence.Group(rows);
        SentEvidence[] sent = [new(Origin.AddMinutes(5), "Quarterly invoice sk-abcdef123"), new(Origin.AddHours(3), "Unrelated afternoon mail")];
        True(!AssistedInference.BuildRequest("gpt-test", groups, [], false, true, sent).Contains("Unrelated afternoon"));
        var minimal = AssistedInference.BuildRequest("gpt-test", groups, [], false, false, sent);
        True(!minimal.Contains("secret customer") && !minimal.Contains("contract renewal") && !minimal.Contains("Quarterly invoice"));
        var titles = AssistedInference.BuildRequest("gpt-test", groups, [], true, false, sent);
        True(titles.Contains("secret customer") && !titles.Contains("intra.example") && !titles.Contains("contract renewal"));
        var subjects = AssistedInference.BuildRequest("gpt-test", groups, [], false, true, sent);
        True(!subjects.Contains("secret customer") && subjects.Contains("contract renewal") &&
             subjects.Contains("Quarterly invoice") && !subjects.Contains("sk-abcdef123"));
        True(minimal.Contains("\"store\": false"));
        Throws<ArgumentException>(() => AssistedInference.BuildRequest("gpt test; rm", groups, [], false, false, []), "model");
    }

    public static void OutlookPaging()
    {
        var zone = TimeZoneInfo.Utc;
        string Item(int minute, string subject) => JsonSerializer.Serialize(new
        {
            subject, sentDateTime = Origin.AddMinutes(minute).UtcDateTime.ToString("O")
        });
        string Page(string? next, params string[] items) =>
            $"{{\"value\":[{string.Join(',', items)}]{(next == null ? "" : $",\"@odata.nextLink\":{JsonSerializer.Serialize(next)}")}}}";
        const string Next = "https://graph.microsoft.com/v1.0/me/mailFolders/sentitems/messages?$skiptoken=abc";

        // Two good pages, with an out-of-day item filtered out.
        var requested = new List<Uri>();
        var result = Read(request =>
        {
            requested.Add(request.RequestUri!);
            return requested.Count == 1
                ? Ok(Page(Next, Item(60, "First"), Item(-30, "Yesterday")))
                : Ok(Page(null, Item(120, "Second")));
        });
        Equal(2, result.Items.Length);
        True(!result.Incomplete && result.Items.All(item => item.Subject is "First" or "Second"));
        True(requested[0].Query.Contains("%24select=subject%2CsentDateTime") || requested[0].Query.Contains("$select=subject,sentDateTime"));

        // A next link that leaves the Sent Items endpoint is not followed.
        requested.Clear();
        result = Read(request =>
        {
            requested.Add(request.RequestUri!);
            return Ok(Page("https://attacker.example/v1.0/me/mailFolders/sentitems/messages", Item(60, "First")));
        });
        Equal(1, requested.Count);
        True(result.Incomplete && result.Items.Length == 1);
        requested.Clear();
        result = Read(request =>
        {
            requested.Add(request.RequestUri!);
            return Ok(Page("https://graph.microsoft.com/v1.0/me/messages?$skiptoken=x", Item(60, "First")));
        });
        Equal(1, requested.Count);
        True(result.Incomplete);

        // A later page failure keeps earlier evidence and reports it as partial; a first-page failure is an error.
        var calls = 0;
        result = Read(_ => ++calls == 1 ? Ok(Page(Next, Item(60, "First"))) : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        True(result.Incomplete && result.Items.Single().Subject == "First");
        Throws<InvalidOperationException>(() => Read(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), "HTTP 401");

        // Malformed Graph bodies become a readable message on the first page and a partial result later.
        foreach (var bad in new[] { "<html>", "[]", "{}", """{"value":{}}""" })
            Throws<InvalidOperationException>(() => Read(_ => Ok(bad)), "unreadable");
        calls = 0;
        result = Read(_ => ++calls == 1 ? Ok(Page(Next, Item(60, "First"))) : Ok("<html>"));
        True(result.Incomplete && result.Items.Length == 1);
        result = Read(_ => Ok("""{"value":[1,{"sentDateTime":5},{"sentDateTime":"2026-01-01T01:00:00Z","subject":7}],"@odata.nextLink":3}"""));
        True(!result.Incomplete && result.Items.Single().Subject == "");

        // Paging is bounded: three pages at most, and more pages mean incomplete.
        calls = 0;
        result = Read(_ => { calls++; return Ok(Page(Next, Item(calls, $"Mail {calls}"))); });
        Equal(3, calls);
        True(result.Incomplete && result.Items.Length == 3);

        SentEvidenceResult Read(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            using var handler = new StubHandler(request =>
            {
                Equal("Bearer", request.Headers.Authorization!.Scheme);
                return respond(request);
            });
            using var client = new HttpClient(handler);
            return OutlookEvidence.ReadAsync(client, "token", Day, zone, CancellationToken.None).GetAwaiter().GetResult();
        }

        static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    public static void TeamsLinks()
    {
        True(AssistedEvidence.TryTeamsLink(
            "https://teams.microsoft.com/l/message/19:abc@thread.v2/1700000000000?context=%7B%7D", out _));
        foreach (var bad in new[]
                 {
                     "http://teams.microsoft.com/l/message/19:abc@thread.v2/1",
                     "https://teams.microsoft.com.evil.example/l/message/a/b",
                     "https://user@teams.microsoft.com/l/message/a/b",
                     "https://teams.microsoft.com:444/l/message/a/b",
                     "https://teams.microsoft.com/l/meetup-join/a/b",
                     "https://teams.microsoft.com/l/message/a/b/c",
                     "file://teams.microsoft.com/l/message/a/b"
                 })
            True(!AssistedEvidence.TryTeamsLink(bad, out _));
    }

    public static void McpOverStdio() => WithFolder(folder =>
    {
        using (var store = new WorkStore(folder))
        {
            store.ReplaceRows([], [
                Row(0, 600, "VS Code") with { Context = "BC-7 private title" },
                Row(600, 900, "Outlook · Mail") with { Context = "Re: private subject" },
                Row(900, 1200, "VS Code") with { Status = Disposition.Billable, Task = new TaskLabel("Client", "BC-7") }
            ]);
            store.CheckpointWal();
        }
        var exe = Path.Combine(AppContext.BaseDirectory, "WorklogDesk.Windows.exe");
        True(File.Exists(exe));

        var plain = Session(exe, folder, includeContext: false);
        True(plain.Tools.Contains("\"worklog_activity\"") && plain.Tools.Contains("\"readOnlyHint\":true"));
        using (var tools = JsonDocument.Parse(plain.Tools))
            Equal(1, tools.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength());
        Equal(2, plain.Rows.GetArrayLength());
        True(!plain.Raw.Contains("private title") && !plain.Raw.Contains("private subject") && !plain.Raw.Contains("BC-7"));

        var context = Session(exe, folder, includeContext: true);
        True(context.Raw.Contains("private title") && !context.Raw.Contains("private subject"));

        // The interface refuses to run when stdio is not redirected or the arguments are wrong.
        using var refused = Process.Start(new ProcessStartInfo(exe, ["--mcp", "not-a-date"])
        {
            RedirectStandardError = true, RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false
        })!;
        True(refused.WaitForExit(15_000));
        Equal(2, refused.ExitCode);
    });

    private sealed record McpResult(string Tools, JsonElement Rows, string Raw);

    private static McpResult Session(string exe, string folder, bool includeContext)
    {
        var arguments = new List<string> { "--mcp", Day.ToString("yyyy-MM-dd"), "--data", folder };
        if (includeContext)
            arguments.Add("--include-context");
        using var process = Process.Start(new ProcessStartInfo(exe, arguments)
        {
            RedirectStandardError = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false)
        })!;
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            Send(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new
            {
                protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" }
            } });
            Receive(1);
            Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
            Send(new { jsonrpc = "2.0", id = 2, method = "tools/list" });
            var tools = Receive(2);
            Send(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "worklog_activity", arguments = new { } } });
            var call = Receive(3);
            using var document = JsonDocument.Parse(call);
            var result = document.RootElement.GetProperty("result");
            True(!result.TryGetProperty("isError", out var isError) || !isError.GetBoolean());
            var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
            using var payload = JsonDocument.Parse(text);
            return new(tools, payload.RootElement.GetProperty("Rows").Clone(), text);
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(10_000))
                process.Kill(true);
            _ = errors.Wait(5_000);
        }

        void Send(object message)
        {
            process.StandardInput.WriteLine(JsonSerializer.Serialize(message));
            process.StandardInput.Flush();
        }

        string Receive(int id)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var read = process.StandardOutput.ReadLineAsync();
                if (!read.Wait(TimeSpan.FromSeconds(30)) || read.Result == null)
                    break;
                using var message = JsonDocument.Parse(read.Result);
                if (message.RootElement.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number &&
                    value.GetInt32() == id)
                    return read.Result;
            }
            throw new Exception($"No MCP response for request {id}. stderr: {(errors.IsCompleted ? errors.Result : "")}");
        }
    }

    private static Allocation Row(int start, int end, string app) =>
        new(Guid.NewGuid().ToString("N"), Day, Origin.AddSeconds(start), Origin.AddSeconds(end), app,
            SpanKind.Foreground, Disposition.Unassigned, new TaskLabel("", ""));

    private static void WithFolder(Action<string> check)
    {
        var folder = Directory.CreateTempSubdirectory("WorklogDesk-AssistedTests-");
        try { check(folder.FullName); }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            folder.Delete(true);
        }
    }

    private static void ThrowsHandled(Action action, string label)
    {
        // Matches the exception types AssistedReviewDialog reports to the user rather than crashing.
        try { action(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or JsonException or KeyNotFoundException)
        {
            return;
        }
        throw new Exception($"Expected a handled rejection for {label}.");
    }

    private static void Throws<T>(Action action, string fragment) where T : Exception
    {
        try { action(); }
        catch (T error) when (error.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception($"Expected {typeof(T).Name} containing \"{fragment}\".");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}; got {actual}.");
    }

    private static void True(bool value, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!value)
            throw new Exception($"Expected condition to hold (AssistedTests line {line}).");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(respond(request));
    }
}
