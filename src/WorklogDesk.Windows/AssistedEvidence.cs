using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal sealed record EvidenceGroup(string Id, Allocation[] Rows, TaskLabel? Nearby)
{
    public bool Assignable => Rows.All(AssistedEvidence.CanAssign);
    public string Source => AssistedEvidence.AppFamily(Rows[0].App);
    public long Seconds => Rows.Sum(row => row.Milliseconds) / 1_000;
    public DateTimeOffset Start => Rows[0].Start;
    public DateTimeOffset End => Rows[^1].End;
}

internal static class AssistedEvidence
{
    public static bool CanAssign(Allocation row) => row.Status == Disposition.Unassigned && !row.Approved &&
        row.Kind is SpanKind.Foreground or SpanKind.Manual;

    public static EvidenceGroup[] Group(IReadOnlyList<Allocation> day)
    {
        var hints = Timeline.Suggest(day);
        var groups = new List<EvidenceGroup>();
        var pending = new List<Allocation>();
        foreach (var row in day.OrderBy(item => item.Start).ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            if (row.Status != Disposition.Unassigned || row.Approved || row.Kind is SpanKind.Idle or SpanKind.Incidental)
            {
                Finish();
                continue;
            }
            if (pending.Count > 0)
            {
                var last = pending[^1];
                if (last.End != row.Start || last.Kind != row.Kind || last.App != row.App || last.Task != row.Task ||
                    last.Context != row.Context || hints.GetValueOrDefault(last.Id) != hints.GetValueOrDefault(row.Id))
                    Finish();
            }
            pending.Add(row);
        }
        Finish();
        return groups.ToArray();

        void Finish()
        {
            if (pending.Count == 0)
                return;
            groups.Add(new EvidenceGroup($"activity-{groups.Count + 1}", pending.ToArray(), hints.GetValueOrDefault(pending[0].Id)));
            pending.Clear();
        }
    }

    public static string AppFamily(string app) => app.Split('·', 2)[0].Trim() switch
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

    public static string Text(string input, int length)
    {
        var clean = string.Join(' ', input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(ch => !char.IsControl(ch)).ToArray();
        return new string(clean, 0, Math.Min(length, clean.Length));
    }

    public static bool TryTeamsLink(string input, out Uri? uri)
    {
        uri = null;
        if (input.Length > 2_048 || input.Contains('\\') || !Uri.TryCreate(input, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps || !parsed.IsDefaultPort || parsed.UserInfo.Length != 0 ||
            parsed.IdnHost is not ("teams.microsoft.com" or "teams.cloud.microsoft" or "teams.live.com"))
            return false;
        var path = parsed.AbsolutePath.Split('/');
        if (path.Length != 5 || path[1] != "l" || path[2] != "message" ||
            path.Skip(3).Any(part => !System.Text.RegularExpressions.Regex.IsMatch(part,
                @"\A([A-Za-z0-9_@.:-]|%3[Aa]|%40)+\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
            return false;
        uri = parsed;
        return true;
    }
}
