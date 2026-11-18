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
        if (query.Mine || projectKeys.Length == 0)
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

internal static class CredentialVault
{
    public static string? Read(string target)
    {
        if (!CredRead(target, 1, 0, out var pointer))
        {
            var code = Marshal.GetLastWin32Error();
            if (code == 1168)
                return null;
            throw new InvalidOperationException($"Windows could not read the saved Jira token ({code}).");
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

    public static void Write(JiraSettings config, string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new Credential
            {
                Type = 1, Target = config.CredentialTarget, User = config.Email, Blob = pointer,
                BlobSize = bytes.Length, Persist = 2
            };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException("Windows could not store the Jira token.");
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
            throw new InvalidOperationException("Windows could not remove the Jira token.");
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
