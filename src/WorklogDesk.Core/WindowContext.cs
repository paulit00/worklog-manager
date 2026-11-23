using System.Text.RegularExpressions;

namespace WorklogDesk.Core;

/// <summary>
/// Turns a window title into review context and, where the title names one, a Jira issue key.
/// Pure and allocation-light so it can run on every five-second capture sample.
/// </summary>
public static partial class WindowContext
{
    public const int MaxLength = 160;

    // Tokens that look like issue keys but name standards, hashes, encodings or Windows updates.
    private static readonly HashSet<string> NotProjects = new(StringComparer.Ordinal)
    {
        "UTF", "ISO", "IEC", "SHA", "AES", "RSA", "MD", "RFC", "CVE", "CWE", "TLS", "SSL", "HTTP", "KB", "WIN",
        "X", "GPT", "EN", "IP", "COVID", "ES", "ECMA", "IEEE", "PEP", "JSR", "MS", "AZ", "SQL", "NET", "PS"
    };

    private static readonly string[] AppSuffixes =
    [
        "Microsoft Edge", "Microsoft​ Edge", "Google Chrome", "Mozilla Firefox", "Brave", "Visual Studio Code",
        "Visual Studio Code - Insiders", "Remote Desktop Connection", "Outlook", "Microsoft Teams", "Teams",
        "Windows PowerShell", "PowerShell", "Command Prompt", "Jira", "Jira Service Management"
    ];

    /// <summary>Removes unread counters and the application name so the title reads as what was open.</summary>
    public static string Normalize(string title)
    {
        var text = MorePages().Replace(UnreadCounter().Replace(title.Trim(), ""), "");
        var parts = text.Split([" - ", " — ", " | "], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count > 1 && AppSuffixes.Contains(parts[^1], StringComparer.OrdinalIgnoreCase))
            parts.RemoveAt(parts.Count - 1);
        // Edge appends the profile name before the browser name, for example "Page - Work - Microsoft Edge".
        if (parts.Count > 1 && parts[^1].Length <= 24 && parts[^1].StartsWith("Profile", StringComparison.OrdinalIgnoreCase))
            parts.RemoveAt(parts.Count - 1);
        var result = string.Join(" - ", parts);
        return result.Length <= MaxLength ? result : result[..(MaxLength - 1)] + "…";
    }

    private static readonly string[] BrowserNames = ["Microsoft Edge", "Microsoft​ Edge", "Google Chrome", "Mozilla Firefox", "Brave"];

    /// <summary>
    /// Names the web app behind a raw browser title, "Outlook web" or "Teams web", or null. The browser name, the
    /// Edge profile and the "and N more pages" tab counter are removed first, so "Subject - Jane - Outlook - Work -
    /// Microsoft Edge" is recognised.
    /// </summary>
    public static string? WebApp(string rawTitle)
    {
        var text = MorePages().Replace(rawTitle.Trim(), "");
        var parts = text.Split([" - ", " — ", " | "], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count > 1 && BrowserNames.Contains(parts[^1], StringComparer.OrdinalIgnoreCase))
            parts.RemoveAt(parts.Count - 1);
        // The profile name, when present, sits between the app name and the browser name.
        foreach (var part in parts.Skip(1).TakeLast(2))
        {
            if (part.Equals("Outlook", StringComparison.OrdinalIgnoreCase) || part.StartsWith("Outlook (", StringComparison.OrdinalIgnoreCase))
                return "Outlook web";
            if (part.Equals("Microsoft Teams", StringComparison.OrdinalIgnoreCase) || part.Equals("Teams", StringComparison.OrdinalIgnoreCase))
                return "Teams web";
        }
        return null;
    }

    /// <summary>
    /// Finds one unambiguous Jira key in a title. With configured projects only their keys count;
    /// otherwise common look-alikes such as UTF-8 or SHA-256 are ignored. Two different keys return null.
    /// </summary>
    public static string? DetectIssue(string title, IReadOnlySet<string>? projects = null)
    {
        string? found = null;
        foreach (Match match in IssueKey().Matches(title))
        {
            var project = match.Groups[1].Value;
            if (projects is { Count: > 0 } ? !projects.Contains(project) : NotProjects.Contains(project))
                continue;
            var key = match.Value;
            if (found != null && found != key)
                return null;
            found = key;
        }
        return found;
    }

    /// <summary>Reads the issue summary from a Jira tab title such as "[BC-123] Restore failed - Jira".</summary>
    public static string JiraSummary(string normalizedTitle, string key)
    {
        var marker = $"[{key}]";
        var index = normalizedTitle.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return "";
        var summary = normalizedTitle[(index + marker.Length)..].Trim();
        var cut = summary.IndexOf(" - ", StringComparison.Ordinal);
        if (cut >= 0)
            summary = summary[..cut];
        return summary.Length <= 200 ? summary : summary[..200];
    }

    /// <summary>Adds a title to a span's running context, keeping at most three distinct titles.</summary>
    public static string Append(string context, string title)
    {
        if (title.Length == 0 || context == title)
            return context;
        if (context.Length == 0)
            return title;
        var parts = context.Split(" · ");
        if (parts.Contains(title, StringComparer.Ordinal) || parts.Length >= 3)
            return context;
        var joined = context + " · " + title;
        return joined.Length <= MaxLength * 2 ? joined : context;
    }

    [GeneratedRegex(@"\A\(\d+\)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex UnreadCounter();

    // Edge appends "and 3 more pages" (or tabs) to the active tab's title in a window with several tabs.
    [GeneratedRegex(@"\s+and \d+ more (?:pages?|tabs?)(?=\s+[-—|]\s+|\z)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MorePages();

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])([A-Z][A-Z0-9]{1,9})-([1-9][0-9]{0,6})(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex IssueKey();
}
