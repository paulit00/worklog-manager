using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal sealed record JiraSettings(string Site, string Email, string CloudId, string Projects, string WaitingStatus)
{
    public Uri ApiBase => CloudId.Length == 0
        ? new Uri(Site)
        : new Uri($"https://api.atlassian.com/ex/jira/{Guid.Parse(CloudId):D}/");

    public string CredentialTarget => $"WorklogDesk/Jira/{new Uri(Site).IdnHost}/{Email.ToLowerInvariant()}";
    public string TempoCredentialTarget => $"WorklogDesk/Tempo/{new Uri(Site).IdnHost}/{Email.ToLowerInvariant()}";

    public static JiraSettings Create(string site, string email, string cloudId, string projects, string waiting)
    {
        if (!Uri.TryCreate(site.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !Regex.IsMatch(uri.IdnHost, @"\A[a-z0-9][a-z0-9-]{0,62}\.atlassian\.net\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new ArgumentException("Enter the Jira Cloud site root, such as https://company.atlassian.net.");
        email = email.Trim();
        if (email.Length is 0 or > 254 || !email.Contains('@') || email.Contains(':') ||
            email.Any(char.IsWhiteSpace) || email.Any(char.IsControl))
            throw new ArgumentException("Enter the Atlassian account email.");
        cloudId = cloudId.Trim();
        if (cloudId.Length > 0 && !Guid.TryParse(cloudId, out _))
            throw new ArgumentException("Cloud ID must be a GUID for a scoped token.");
        var keys = ProjectKeys(projects);
        if (keys.Length > 30 || keys.Any(key => !Regex.IsMatch(key, @"\A[A-Z][A-Z0-9_]{0,31}\z")))
            throw new ArgumentException("Enter up to 30 comma-separated Jira project keys.");
        waiting = waiting.Trim();
        if (waiting.Length is 0 or > 120 || waiting.Any(char.IsControl))
            throw new ArgumentException("Enter the exact Waiting status name.");
        return new JiraSettings(uri.GetLeftPart(UriPartial.Authority) + "/", email, cloudId, string.Join(",", keys), waiting);
    }

    public static string[] ProjectKeys(string projects) => projects.Split(',',
        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(value => value.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();
}

internal sealed record JiraIssue(string Key, string Summary, string Status);
internal sealed record JiraResults(IReadOnlyList<JiraIssue> Issues, string? Next);
internal sealed record JiraQuery(string Text, bool Mine, bool WaitingOnly, string? Next = null);
internal sealed record JiraProject(string Key, string Name);
internal sealed record JiraIssueType(string Id, string Name);
internal sealed record NewJiraIssue(string ProjectKey, string IssueTypeId, string Summary, string Description, string? AssigneeAccountId);

internal sealed class JiraCatalog : IDisposable
{
    private readonly HttpClient client;
    private readonly JiraSettings config;

    public JiraCatalog(JiraSettings config, string token, HttpMessageHandler? handler = null)
    {
        this.config = JiraSettings.Create(config.Site, config.Email, config.CloudId, config.Projects, config.WaitingStatus);
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw new ArgumentException("A valid Jira API token is required.");
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = this.config.ApiBase,
            Timeout = TimeSpan.FromSeconds(12),
            MaxResponseContentBufferSize = 2 * 1024 * 1024
        };
        var auth = Encoding.UTF8.GetBytes($"{this.config.Email}:{token}");
        try
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(auth));
        }
        finally { CryptographicOperations.ZeroMemory(auth); }
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> AccountAsync(CancellationToken cancel)
    {
        using var response = await Read("rest/api/3/myself", cancel).ConfigureAwait(false);
        return response.RootElement.GetProperty("accountId").GetString()
            ?? throw new InvalidDataException("Jira did not return an account ID.");
    }

    /// <summary>The numeric issue ID Tempo needs. Works for any issue the account can read, including colleagues'.</summary>
    public async Task<long> IssueIdAsync(string key, CancellationToken cancel)
    {
        if (!TaskLabel.ValidIssue(key))
            throw new ArgumentException($"{key} is not a Jira issue key.");
        using var issue = await Read("rest/api/3/issue/" + Uri.EscapeDataString(key) + "?fields=summary", cancel).ConfigureAwait(false);
        return long.Parse(issue.RootElement.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Projects the account can see, matching a name or key. Empty text lists the first page.</summary>
    public async Task<IReadOnlyList<JiraProject>> ProjectsAsync(string text, CancellationToken cancel)
    {
        var term = text.Trim();
        if (term.Length > 80 || term.Any(char.IsControl))
            throw new ArgumentException("Keep the project search under 80 characters.");
        using var response = await Read("rest/api/3/project/search?maxResults=30&query=" + Uri.EscapeDataString(term), cancel)
            .ConfigureAwait(false);
        return response.RootElement.GetProperty("values").EnumerateArray()
            .Select(item => new JiraProject(item.GetProperty("key").GetString() ?? "", item.GetProperty("name").GetString() ?? ""))
            .Where(project => project.Key.Length > 0).ToArray();
    }

    /// <summary>Issue types that can be created in the project, without subtasks.</summary>
    public async Task<IReadOnlyList<JiraIssueType>> IssueTypesAsync(string projectKey, CancellationToken cancel)
    {
        using var response = await Read("rest/api/3/issue/createmeta/" + Uri.EscapeDataString(projectKey) + "/issuetypes", cancel)
            .ConfigureAwait(false);
        var root = response.RootElement;
        var list = root.TryGetProperty("issueTypes", out var types) ? types : root.GetProperty("values");
        return list.EnumerateArray()
            .Where(item => !(item.TryGetProperty("subtask", out var subtask) && subtask.ValueKind == JsonValueKind.True))
            .Select(item => new JiraIssueType(item.GetProperty("id").GetString() ?? "", item.GetProperty("name").GetString() ?? ""))
            .Where(type => type.Id.Length > 0).ToArray();
    }

    /// <summary>
    /// Creates an issue, assigned to the given account when Jira allows it on create; otherwise assigns right after.
    /// A create that Jira doesn't confirm is looked up among the user's issues from the last minutes before reporting.
    /// </summary>
    public async Task<(JiraIssue Issue, string? Warning)> CreateIssueAsync(NewJiraIssue issue, CancellationToken cancel,
        bool recoverByTitle = true)
    {
        if (issue.Summary.Trim().Length is 0 or > 250)
            throw new ArgumentException("Enter a summary of up to 250 characters.");
        try
        {
            var created = await Create(issue, withAssignee: issue.AssigneeAccountId != null, cancel).ConfigureAwait(false);
            return (created, null);
        }
        catch (JiraFieldException error) when (issue.AssigneeAccountId != null && error.Fields.Contains("assignee"))
        {
            var created = await Create(issue, withAssignee: false, cancel).ConfigureAwait(false);
            try
            {
                await Send(HttpMethod.Put, $"rest/api/3/issue/{Uri.EscapeDataString(created.Key)}/assignee",
                    new { accountId = issue.AssigneeAccountId }, cancel).ConfigureAwait(false);
                return (created, null);
            }
            catch (InvalidOperationException)
            {
                return (created, $"{created.Key} was created, but Jira didn't allow assigning it to you.");
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            if (!recoverByTitle)
                throw new InvalidOperationException("Jira did not confirm creation. Check Jira before trying again.", error);
            // The issue may exist even though the reply was lost. Look for it before anyone creates it again.
            var recent = await SearchAsync(new JiraQuery(issue.Summary, Mine: false, WaitingOnly: false), CancellationToken.None)
                .ConfigureAwait(false);
            var match = recent.Issues.FirstOrDefault(item => item.Key.StartsWith(issue.ProjectKey + "-", StringComparison.Ordinal) &&
                StringComparer.OrdinalIgnoreCase.Equals(item.Summary.Trim(), issue.Summary.Trim()));
            if (match != null)
                return (match, "Jira was slow to confirm, so this is the issue found with that summary. Check it is the new one.");
            throw new InvalidOperationException("Jira didn't confirm the new issue. Search for it before creating it again.", error);
        }
    }

    private async Task<JiraIssue> Create(NewJiraIssue issue, bool withAssignee, CancellationToken cancel)
    {
        var fields = new Dictionary<string, object>
        {
            ["project"] = new { key = issue.ProjectKey },
            ["issuetype"] = new { id = issue.IssueTypeId },
            ["summary"] = issue.Summary.Trim()
        };
        if (withAssignee)
            fields["assignee"] = new { accountId = issue.AssigneeAccountId };
        if (issue.Description.Trim().Length > 0)
            fields["description"] = new
            {
                type = "doc", version = 1,
                content = issue.Description.Trim().Split('\n').Select(line => new
                {
                    type = "paragraph",
                    content = line.Trim().Length == 0 ? Array.Empty<object>() : new object[] { new { type = "text", text = line.TrimEnd('\r') } }
                }).ToArray()
            };
        using var response = await Send(HttpMethod.Post, "rest/api/3/issue", new { fields }, cancel).ConfigureAwait(false);
        var key = response.RootElement.GetProperty("key").GetString();
        if (key == null || !TaskLabel.ValidIssue(key))
            throw new InvalidDataException("Jira returned an invalid issue key.");
        return new JiraIssue(key, issue.Summary.Trim(), "New");
    }

    private async Task<JsonDocument> Send(HttpMethod method, string path, object body, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request, cancel).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return bytes.Length == 0 ? JsonDocument.Parse("{}") : JsonDocument.Parse(bytes);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Jira rejected the credentials. Check the token type, expiry and account.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("Jira is limiting requests. Wait briefly and try again.");
        var messages = new List<string>();
        var fieldNames = new List<string>();
        try
        {
            using var error = JsonDocument.Parse(bytes);
            if (error.RootElement.TryGetProperty("errorMessages", out var list) && list.ValueKind == JsonValueKind.Array)
                messages.AddRange(list.EnumerateArray().Select(item => item.GetString() ?? "").Where(text => text.Length > 0));
            if (error.RootElement.TryGetProperty("errors", out var fields) && fields.ValueKind == JsonValueKind.Object)
                foreach (var field in fields.EnumerateObject())
                {
                    fieldNames.Add(field.Name);
                    messages.Add($"{field.Name}: {field.Value.GetString()}");
                }
        }
        catch (JsonException) { }
        var text = messages.Count > 0 ? string.Join("; ", messages.Take(4)) : $"HTTP {(int)response.StatusCode}";
        if (fieldNames.Count > 0)
            throw new JiraFieldException("Jira didn't accept the issue: " + text, fieldNames);
        throw new InvalidOperationException(response.StatusCode == HttpStatusCode.Forbidden
            ? "Your Jira account can't do that here: " + text : "Jira didn't accept the request: " + text);
    }

    public async Task<JiraResults> SearchAsync(JiraQuery query, CancellationToken cancel)
    {
        var term = query.Text.Trim();
        var key = term.ToUpperInvariant();
        if (TaskLabel.ValidIssue(key))
        {
            using var issue = await Read("rest/api/3/issue/" + Uri.EscapeDataString(key) + "?fields=summary,status", cancel)
                .ConfigureAwait(false);
            return new JiraResults([ParseIssue(issue.RootElement)], null);
        }
        if (term.Length > 120 || term.Any(char.IsControl))
            throw new ArgumentException("Keep Jira search text under 120 characters.");

        var projectKeys = JiraSettings.ProjectKeys(config.Projects);
        var filters = new List<string>();
        if (projectKeys.Length > 0)
            filters.Add("project in (" + string.Join(",", projectKeys.Select(Quote)) + ")");
        // Without configured projects an empty search lists your own issues; text searches everyone's.
        if (query.Mine || projectKeys.Length == 0 && term.Length == 0)
            filters.Add("assignee = currentUser()");
        filters.Add(query.WaitingOnly ? $"status = {Quote(config.WaitingStatus)}" : "statusCategory != Done");
        foreach (var word in term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(12))
            filters.Add("summary ~ " + Quote(Quote(word)));
        var jql = string.Join(" AND ", filters) + " ORDER BY updated DESC";
        var next = string.IsNullOrEmpty(query.Next) ? "" : "&nextPageToken=" + Uri.EscapeDataString(query.Next);
        using var response = await Read("rest/api/3/search/jql?fields=summary,status&maxResults=25&jql=" +
            Uri.EscapeDataString(jql) + next, cancel).ConfigureAwait(false);
        var root = response.RootElement;
        var issues = root.GetProperty("issues").EnumerateArray().Select(ParseIssue).ToArray();
        var continuation = root.TryGetProperty("nextPageToken", out var continuationToken) ? continuationToken.GetString() : null;
        return new JiraResults(issues, continuation);
    }

    public async Task<JiraResults> SearchAssistedAsync(string userText, string? selectedProject, CancellationToken cancel)
    {
        var configured = JiraSettings.ProjectKeys(config.Projects);
        if (selectedProject != null && !configured.Contains(selectedProject, StringComparer.Ordinal))
            throw new InvalidOperationException("The selected project is outside the projects configured for assisted Jira search.");
        var projects = selectedProject == null ? configured : [selectedProject];
        if (projects.Length == 0)
            throw new InvalidOperationException("Configure Jira project keys before using assisted search.");
        var text = userText.Trim();
        if (text.Length is 0 or > 250 || text.Any(char.IsControl))
            throw new ArgumentException("Enter search terms first.");
        var key = text.ToUpperInvariant();
        var filter = "project in (" + string.Join(",", projects.Select(Quote)) + ")";
        if (selectedProject == null && TaskLabel.ValidIssue(key))
        {
            if (!projects.Contains(key[..key.LastIndexOf('-')], StringComparer.Ordinal))
                throw new ArgumentException("That issue is outside your configured Jira projects.");
            filter += " AND key = " + Quote(key);
        }
        else
        {
            var words = System.Text.RegularExpressions.Regex.Matches(text, @"[\p{L}\p{N}]{2,32}")
                .Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray();
            if (words.Length == 0)
                throw new ArgumentException("Enter at least one searchable word.");
            filter += " AND (" + string.Join(" OR ", words.Select(word => "summary ~ " + Quote(Quote(word)))) + ")";
        }
        using var response = await Read("rest/api/3/search/jql?fields=summary,status&maxResults=20&jql=" +
            Uri.EscapeDataString(filter + " ORDER BY updated DESC"), cancel).ConfigureAwait(false);
        var root = response.RootElement;
        return new JiraResults(root.GetProperty("issues").EnumerateArray().Select(ParseIssue)
            .Where(issue => TaskLabel.ValidIssue(issue.Key) && projects.Contains(issue.Key[..issue.Key.LastIndexOf('-')], StringComparer.Ordinal))
            .Take(20).ToArray(), root.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null);
    }

    public void Dispose() => client.Dispose();

    private async Task<JsonDocument> Read(string path, CancellationToken cancel)
    {
        using var response = await client.GetAsync(path, cancel).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Jira rejected the credentials. Check the token type, expiry and account.");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("The configured user or token cannot read this Jira resource.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("Jira could not find an accessible issue or site.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("Jira is limiting requests. Wait briefly and try again.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Jira returned HTTP {(int)response.StatusCode}.");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
        return JsonDocument.Parse(bytes);
    }

    private static JiraIssue ParseIssue(JsonElement item)
    {
        var fields = item.GetProperty("fields");
        var key = item.GetProperty("key").GetString();
        if (key == null || !TaskLabel.ValidIssue(key))
            throw new InvalidDataException("Jira returned an invalid issue key.");
        return new JiraIssue(key, fields.GetProperty("summary").GetString() ?? "",
            fields.GetProperty("status").GetProperty("name").GetString() ?? "");
    }

    private static string Quote(string text) => "\"" +
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

/// <summary>Jira rejected specific fields; the message names them.</summary>
internal sealed class JiraFieldException(string message, IReadOnlyList<string> fields) : InvalidOperationException(message)
{
    public IReadOnlyList<string> Fields { get; } = fields;
}

internal static class CredentialVault
{
    public static string? Read(string target)
    {
        if (!CredRead(target, 1, 0, out var pointer))
        {
            var code = Marshal.GetLastWin32Error();
            if (code == 1168)
                return null;
            throw new InvalidOperationException($"Windows could not read the saved credential ({code}).");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var bytes = new byte[credential.BlobSize];
            try
            {
                Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public static void Write(JiraSettings config, string token) => Write(config.CredentialTarget, config.Email, token);

    public static void Write(string target, string user, string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new Credential
            {
                Type = 1, Target = target, User = user, Blob = pointer,
                BlobSize = bytes.Length, Persist = 2
            };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException("Windows could not store the token.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Marshal.FreeHGlobal(pointer);
        }
    }

    public static void Delete(string target)
    {
        if (!CredDelete(target, 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new InvalidOperationException("Windows could not remove the saved credential.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags, Type;
        public string Target, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int BlobSize;
        public nint Blob;
        public int Persist, AttributeCount;
        public nint Attributes;
        public string Alias, User;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out nint pointer);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(nint pointer);
}
