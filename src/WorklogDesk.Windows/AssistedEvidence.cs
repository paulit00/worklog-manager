using System.Text.RegularExpressions;
using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal static partial class AssistedEvidence
{
    public static bool CanAssign(Allocation row) => row.Status == Disposition.Unassigned && !row.Approved &&
        row.Kind is SpanKind.Foreground or SpanKind.Manual;

    /// <summary>The app family used for privacy rules. Mail and chat opened in a browser count as Outlook and Teams.</summary>
    public static string AppFamily(string app)
    {
        if (app.Contains("Outlook web", StringComparison.Ordinal))
            return "Outlook";
        if (app.Contains("Teams web", StringComparison.Ordinal))
            return "Teams";
        return app.Split('·', 2)[0].Trim() switch
        {
            "Browser" => "Browser",
            "VS Code" => "VS Code",
            "Remote Desktop" => "Remote Desktop",
            "Teams" => "Teams",
            "Outlook" => "Outlook",
            "Terminal / AI" => "Terminal",
            "Manual" or "Manual timer" or "Manual entry" => "Manual",
            _ => "Other"
        };
    }

    /// <summary>Collapses whitespace, drops control characters and caps the length.</summary>
    public static string Text(string input, int length)
    {
        var clean = string.Join(' ', input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(ch => !char.IsControl(ch)).ToArray();
        return new string(clean, 0, Math.Min(length, clean.Length));
    }

    /// <summary>The titles stored in a row's context, in capture order.</summary>
    public static string[] Titles(string context) => context.Length == 0 ? [] :
        context.Split(" · ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The one redactor for anything that may leave the PC: window titles, meeting and mail subjects, Jira summaries.
    /// Jira keys, dates, times and version numbers are protected first, then links, email addresses, phone numbers
    /// and secrets are removed.
    /// </summary>
    public static string Redact(string value, int cap)
    {
        var kept = new List<string>();
        var text = Protected().Replace(value, match =>
        {
            kept.Add(match.Value);
            return $"\u0001{kept.Count - 1}\u0002";
        });
        text = Url().Replace(text, "[redacted]");
        text = Email().Replace(text, "[redacted]");
        text = Secret().Replace(text, "[redacted]");
        text = LongToken().Replace(text, "[redacted]");
        text = Phone().Replace(text, "[redacted]");
        text = Placeholder().Replace(text, match => kept[int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)]);
        return Text(text, cap);
    }

    /// <summary>
    /// A coarse token naming what a title is about, stable across files and pages: the RDP host, the VS Code
    /// workspace, the Teams chat, the first segment of a browser title, or the first segment otherwise.
    /// </summary>
    public static string Token(string family, string title)
    {
        var parts = title.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "";
        var token = family switch
        {
            "VS Code" or "Teams" => parts[^1],
            _ => parts[0]
        };
        return Text(token, 60).ToLowerInvariant();
    }

    /// <summary>
    /// True when a browser title looks like mail: Outlook or Gmail named, an email address, a mail folder, or a
    /// segment equal to the Microsoft account's display name or address (Outlook on the web shows "Subject - Name").
    /// </summary>
    public static bool MailLike(string title, string? displayName, string? address)
    {
        if (title.Contains('@') || title.Contains("Outlook", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Gmail", StringComparison.OrdinalIgnoreCase))
            return true;
        var parts = title.Split([" - ", " — ", " | "], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (MailFolder().IsMatch(part))
                return true;
            if (displayName is { Length: > 2 } && part.Equals(displayName, StringComparison.OrdinalIgnoreCase) ||
                address is { Length: > 2 } && part.Equals(address, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>A browser title naming Microsoft Teams (older rows, before the capture label existed).</summary>
    public static bool TeamsLike(string title) => title.Split(" - ", StringSplitOptions.TrimEntries)
        .Any(part => part.Equals("Microsoft Teams", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\b[A-Z][A-Z0-9]{1,9}-\d+\b|\b\d{4}-\d{2}-\d{2}\b|\b\d{1,2}:\d{2}(?::\d{2})?\b|\b\d+(?:\.\d+)+\b", RegexOptions.CultureInvariant)]
    private static partial Regex Protected();

    [GeneratedRegex(@"\u0001(\d+)\u0002", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"(?i)\b(?:https?://|www\.)[^\s\u0001]+", RegexOptions.CultureInvariant)]
    private static partial Regex Url();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"(?i)\b(?:sk-|gh[pousr]_|bearer\s+)[^\s\u0001]+", RegexOptions.CultureInvariant)]
    private static partial Regex Secret();

    // High-entropy strings only (mixed case and digits, or long hex), so branch names and paths survive.
    [GeneratedRegex(@"(?=[A-Za-z0-9+=]*\d)(?=[A-Za-z0-9+=]*[a-z])(?=[A-Za-z0-9+=]*[A-Z])[A-Za-z0-9+=]{32,}|\b[0-9a-fA-F]{32,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex LongToken();

    [GeneratedRegex(@"\+\d[\d\s-]{6,}\d|\(\d{1,4}\)[\d\s-]{5,}\d|(?<![\w\u0002])\d(?:[\s-]?\d){8,}(?![\w\u0001])", RegexOptions.CultureInvariant)]
    private static partial Regex Phone();

    [GeneratedRegex(@"^(?:Mail|Inbox|Calendar|Sent Items|Drafts|Deleted Items)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MailFolder();
}
