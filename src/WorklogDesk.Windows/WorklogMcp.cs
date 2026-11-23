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

internal sealed record McpActivitySnapshot(DateOnly Date, McpActivity[] Rows, bool Truncated)
{
    public static McpActivitySnapshot Read(string folder, DateOnly date, bool context)
    {
        var path = Path.Combine(folder, "worklog.db");
        if (!File.Exists(path))
            throw new InvalidOperationException("No Worklog Desk database was found.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        if (command.ExecuteScalar() is not 5L)
            throw new InvalidOperationException("This interface requires Worklog Desk database version 5.");
        command.CommandText = context ? """
            SELECT a.started,a.ended,a.app,a.kind,c.payload FROM allocation a
            LEFT JOIN context c ON a.context_id=c.id
            WHERE a.day=$date AND a.status=$unassigned AND a.approved=0 AND a.kind IN ($foreground,$manual,$uncertain)
            ORDER BY a.started,a.id LIMIT 1001
            """ : """
            SELECT a.started,a.ended,a.app,a.kind,NULL FROM allocation a
            WHERE a.day=$date AND a.status=$unassigned AND a.approved=0 AND a.kind IN ($foreground,$manual,$uncertain)
            ORDER BY a.started,a.id LIMIT 1001
            """;
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$unassigned", (int)Disposition.Unassigned);
        command.Parameters.AddWithValue("$foreground", (int)SpanKind.Foreground);
        command.Parameters.AddWithValue("$manual", (int)SpanKind.Manual);
        command.Parameters.AddWithValue("$uncertain", (int)SpanKind.Uncertain);
        var rows = new List<McpActivity>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (rows.Count == 1_000)
                return new(date, rows.ToArray(), true);
            var start = new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero);
            var end = new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero);
            var source = AssistedEvidence.AppFamily(reader.GetString(2));
            var windowContext = context && source != "Outlook" && !reader.IsDBNull(4) ? Decode((byte[])reader[4]) : null;
            rows.Add(new($"row-{rows.Count + 1}", start, end, (long)(end - start).TotalMilliseconds,
                source, ((SpanKind)reader.GetInt32(3)).ToString(), windowContext));
        }
        return new(date, rows.ToArray(), false);

        static string Decode(byte[] payload)
        {
            var clear = ProtectedData.Unprotect(payload, null, DataProtectionScope.CurrentUser);
            try { return AssistedEvidence.Text(JsonSerializer.Deserialize<string>(clear) ?? "", 160); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
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
        catch (Exception error) when (error is InvalidOperationException or CryptographicException or JsonException or IOException or UnauthorizedAccessException)
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
