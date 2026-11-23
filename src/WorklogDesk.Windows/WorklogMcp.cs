using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal sealed record McpActivity(string Id, DateTimeOffset Start, DateTimeOffset End, long RecordedMilliseconds,
    string Source, string Kind, string? WindowContext);

internal sealed record McpActivitySnapshot(DateOnly Date, McpActivity[] Rows, bool Truncated, string Episodes)
{
    public static McpActivitySnapshot Read(string folder, DateOnly date, bool context)
    {
        using var store = WorkStore.OpenReadOnly(folder);
        if (store.DiskVersion() != 5)
            throw new InvalidOperationException("This interface requires Worklog Desk database version 5.");
        var preferences = store.Preferences();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(preferences.TimeZoneId);
        // The same title rules as an AI request, without a Microsoft account: older browser titles are always held back.
        var policy = context
            ? new TitlePolicy(true, (preferences.AssistedTitlesOff ?? []).ToHashSet(StringComparer.Ordinal), false, false,
                preferences.MailLabelCutoff, null, null, new HashSet<string>())
            : TitlePolicy.Off;
        var all = store.Rows(date);
        var rows = new List<McpActivity>();
        var unresolved = all.Where(row => row.Status == Disposition.Unassigned && !row.Approved &&
            row.Kind is SpanKind.Foreground or SpanKind.Manual or SpanKind.Uncertain).ToArray();
        foreach (var row in unresolved.Take(1_000))
        {
            var titles = AssistedEvidence.Titles(row.Context).Where(title => DayEpisodes.Decide(row, title, policy) == TitleDecision.Sent)
                .Select(title => AssistedEvidence.Redact(title, 160)).ToArray();
            rows.Add(new($"row-{rows.Count + 1}", row.Start, row.End, row.Milliseconds, AssistedEvidence.AppFamily(row.App),
                row.Kind.ToString(), titles.Length == 0 ? null : string.Join(" · ", titles)));
        }
        string facts;
        try { facts = DayReconstruction.Facts(date, zone, DayEpisodes.Build(all, Timeline.DayStart(date, zone)), policy, [], [], [], []); }
        catch (InvalidOperationException error) { facts = JsonSerializer.Serialize(new { error = error.Message }); }
        return new(date, rows.ToArray(), unresolved.Length > 1_000, facts);
    }
}

internal static class WorklogMcp
{
    public static int Run(string[] args)
    {
        if (!Parse(args, out var day, out var folder, out var context) || !Console.IsInputRedirected || !Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("Use redirected stdio with --mcp YYYY-MM-DD [--data FOLDER] [--include-context].");
            return 2;
        }
        try
        {
            var snapshot = McpActivitySnapshot.Read(folder, day, context);
            var host = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            host.Logging.ClearProviders();
            host.Services.AddSingleton(snapshot);
            host.Services.AddMcpServer().WithStdioServerTransport().WithTools<McpActivityTools>();
            host.Build().RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch (SqliteException)
        {
            Console.Error.WriteLine("Could not read the Worklog Desk database. Open Worklog Desk to make its WAL index available, then relaunch this interface.");
            return 1;
        }
        catch (Exception error) when (error is InvalidOperationException or CryptographicException or JsonException or IOException or
            UnauthorizedAccessException or TimeZoneNotFoundException)
        {
            Console.Error.WriteLine("Could not open the selected Worklog Desk snapshot. Verify the data folder and database version.");
            return 1;
        }
    }

    private static bool Parse(string[] args, out DateOnly day, out string folder, out bool context)
    {
        day = default;
        context = false;
        folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorklogDesk");
        if (args.Length == 0 || !DateOnly.TryParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
            return false;
        var explicitFolder = false;
        for (var index = 1; index < args.Length; index++)
        {
            if (args[index] == "--include-context" && !context)
                context = true;
            else if (args[index] == "--data" && !explicitFolder && index + 1 < args.Length && Path.IsPathFullyQualified(args[index + 1]))
            {
                explicitFolder = true;
                folder = args[++index];
            }
            else
                return false;
        }
        return true;
    }
}

[McpServerToolType]
internal sealed class McpActivityTools
{
    [McpServerTool(Name = "worklog_episodes", ReadOnly = true),
     Description("The fixed date as an ordered timeline of episodes (activity runs, anchors already assigned to Jira, idle and excluded gaps). Titles appear only with --include-context and follow the app's privacy rules. Frozen at launch.")]
    public static string Episodes(McpActivitySnapshot snapshot) => snapshot.Episodes;

    [McpServerTool(Name = "worklog_activity", ReadOnly = true),
     Description("Read unresolved recorded activity from the fixed Worklog Desk date. The snapshot is frozen at launch. Times are UTC.")]
    public static string Activity(McpActivitySnapshot snapshot, int offset = 0)
    {
        if (offset < 0 || offset > snapshot.Rows.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        const int page = 50;
        var rows = snapshot.Rows.Skip(offset).Take(page).ToArray();
        return JsonSerializer.Serialize(new
        {
            snapshot.Date, Rows = rows, snapshot.Truncated,
            NextOffset = offset + rows.Length < snapshot.Rows.Length ? (int?)(offset + rows.Length) : null
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }
}
