using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal sealed record SourceMapping(string App, string Alias, TaskLabel Task);

internal sealed record Preferences(
    bool Recording = false, bool Browser = false, bool Developer = true, bool Remote = true,
    bool Communications = false, int IdleMinutes = 2, int WorkdayMinutes = 480,
    string TimeZoneId = "", RoundingMode Rounding = RoundingMode.IssueDay,
    SourceMapping[]? Mappings = null, JiraSettings? Jira = null, bool WindowTitles = true, string Theme = "Dark",
    bool Tempo = false, string LastProject = "", CompletionReminder? CompletionReminder = null,
    string[]? AssistedTitlesOff = null, DateTimeOffset? MailLabelCutoff = null, int AssistedOverheadPercent = 50,
    bool AssistedNoticeShown = false, string OutlookTenant = "", string OutlookClient = "", string AiProvider = "",
    string AiModel = "");

internal sealed record RequestDraft(string Id, DateOnly Day, TaskLabel Task);
internal sealed record ExportReceipt(string Issue, WorkInterval[] Intervals);

internal sealed partial class WorkStore : IDisposable
{
    private readonly SqliteConnection connection;
    private const int SchemaVersion = 5;
    private const int ContextRetentionDays = 30;
    private readonly Dictionary<TaskLabel, string> knownTasks = [];
    private readonly Dictionary<string, string> knownContexts = new(StringComparer.Ordinal);
    public long CompletionRevision { get; private set; }
    public event Action? CompletionChanging;

    public WorkStore(string folder, bool inMemory = false)
    {
        if (!inMemory)
        {
            Directory.CreateDirectory(folder);
            this.folder = folder;
        }
        var path = inMemory ? ":memory:" : Path.Combine(folder, "worklog.db");
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            DefaultTimeout = 2
        }.ToString());
        connection.Open();
        using (var settings = Command("PRAGMA journal_mode=WAL; PRAGMA secure_delete=ON;"))
            settings.ExecuteNonQuery();
        long version;
        using (var readVersion = Command("PRAGMA user_version;"))
            version = (long)readVersion.ExecuteScalar()!;
        if (version > SchemaVersion)
            throw new InvalidOperationException("This database was created by a newer version of Worklog Desk.");
        using var migration = connection.BeginTransaction();
        using var schema = Command("""
            CREATE TABLE IF NOT EXISTS source (
                id TEXT PRIMARY KEY, started INTEGER NOT NULL, ended INTEGER NOT NULL, app TEXT NOT NULL, kind INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS source_end ON source(ended);
            CREATE TABLE IF NOT EXISTS allocation (
                id TEXT PRIMARY KEY, day TEXT NOT NULL, started INTEGER NOT NULL, ended INTEGER NOT NULL,
                status INTEGER NOT NULL, app TEXT NOT NULL, kind INTEGER NOT NULL,
                approved INTEGER NOT NULL, task_id TEXT
            );
            CREATE INDEX IF NOT EXISTS allocation_day ON allocation(day, started);
            CREATE INDEX IF NOT EXISTS allocation_task ON allocation(task_id) WHERE task_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS assigned_end ON allocation(ended) WHERE status IN (1,2);
            CREATE INDEX IF NOT EXISTS unassigned_day ON allocation(day) WHERE status=0;
            CREATE TABLE IF NOT EXISTS task (id TEXT PRIMARY KEY, payload BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS context (id TEXT PRIMARY KEY, payload BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS setting (name TEXT PRIMARY KEY, value BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS reconstruction (
                day TEXT NOT NULL, part INTEGER NOT NULL, created_day TEXT NOT NULL, payload BLOB NOT NULL,
                PRIMARY KEY (day, part)
            );
            CREATE TABLE IF NOT EXISTS request (id TEXT PRIMARY KEY, day TEXT NOT NULL, payload BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS request_day ON request(day);
            CREATE TABLE IF NOT EXISTS manual_tempo (id TEXT PRIMARY KEY, day TEXT NOT NULL, payload BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS manual_tempo_day ON manual_tempo(day);
            """, migration);
        schema.ExecuteNonQuery();
        if (version < 3)
        {
            using var addMode = Command("ALTER TABLE allocation ADD COLUMN approved_mode INTEGER; PRAGMA user_version=3;", migration);
            addMode.ExecuteNonQuery();
        }
        if (version < 4)
        {
            using var addKey = Command("ALTER TABLE allocation ADD COLUMN approved_key TEXT; PRAGMA user_version=4;", migration);
            addKey.ExecuteNonQuery();
        }
        if (version < 5)
        {
            using var addContext = Command("ALTER TABLE allocation ADD COLUMN context_id TEXT; PRAGMA user_version=5;", migration);
            addContext.ExecuteNonQuery();
        }
        migration.Commit();
        PurgeContext(DateOnly.FromDateTime(DateTime.Today).AddDays(-ContextRetentionDays));
    }

    /// <summary>Drops window context recorded before the cutoff day. Attribution and approved time stay.</summary>
    public void PurgeContext(DateOnly before)
    {
        using var transaction = connection.BeginTransaction();
        using var clear = Command("UPDATE allocation SET context_id=NULL WHERE context_id IS NOT NULL AND day < $day",
            transaction, ("$day", FormatDay(before)));
        var cleared = clear.ExecuteNonQuery();
        if (cleared > 0)
        {
            using var orphan = Command("DELETE FROM context WHERE NOT EXISTS (SELECT 1 FROM allocation WHERE context_id=context.id)", transaction);
            orphan.ExecuteNonQuery();
        }
        PurgeReconstructions(before, transaction);
        transaction.Commit();
        knownContexts.Clear();
    }

    public Preferences Preferences()
    {
        var preferences = Read<Preferences>("preferences") ?? new Preferences();
        return string.IsNullOrEmpty(preferences.TimeZoneId)
            ? preferences with { TimeZoneId = TimeZoneInfo.Local.Id }
            : preferences;
    }

    public void SavePreferences(Preferences preferences) => Save("preferences", preferences);

    public TaskLabel? Recover(TimeZoneInfo zone)
    {
        var spans = Read<WorkSpan[]>("checkpoint");
        if (spans is { Length: > 0 })
            SaveCapture(spans, [], zone);
        return spans?.LastOrDefault(span => span.Source.Kind == SpanKind.Manual)?.Source.Task;
    }

    public void SaveCapture(IReadOnlyList<WorkSpan> spans, IReadOnlyList<WorkSpan> checkpoint, TimeZoneInfo zone)
    {
        if (spans.Count > 0)
            CompletionChanging?.Invoke();
        using var transaction = connection.BeginTransaction();
        var taskIds = new Dictionary<TaskLabel, string>();
        foreach (var span in spans)
        {
            using var source = Command("INSERT OR IGNORE INTO source VALUES ($id,$start,$end,$app,$kind)", transaction,
                ("$id", span.Id.ToString("N")), ("$start", span.Start.UtcTicks), ("$end", span.End.UtcTicks),
                ("$app", span.Source.App), ("$kind", (int)span.Source.Kind));
            if (source.ExecuteNonQuery() == 0)
                continue;
            foreach (var row in Timeline.Allocate(span, zone))
            {
                var resolved = row.Status == Disposition.Billable && HasAssignedOverlap(row, transaction)
                    ? row with { Status = Disposition.Unassigned, Kind = SpanKind.Uncertain }
                    : row;
                SaveAllocation(resolved, transaction, taskIds);
            }
        }
        Save("checkpoint", checkpoint, transaction);
        transaction.Commit();
        if (spans.Count > 0)
            CompletionRevision++;
        RememberTasks(taskIds);
    }

    public List<Allocation> Rows(DateOnly day) => Rows(day, null);

    private List<Allocation> Rows(DateOnly day, SqliteTransaction? transaction)
    {
        using var command = Command("""
            SELECT a.id,a.started,a.ended,a.app,a.kind,a.status,a.approved,t.id,t.payload,a.approved_mode,a.approved_key,
                c.id,c.payload
            FROM allocation a LEFT JOIN task t ON a.task_id=t.id LEFT JOIN context c ON a.context_id=c.id
            WHERE day=$day ORDER BY started,a.id
            """,
            transaction, ("$day", FormatDay(day)));
        using var reader = command.ExecuteReader();
        var rows = new List<Allocation>();
        var tasks = new Dictionary<string, TaskLabel>(StringComparer.Ordinal);
        var contexts = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var context = "";
            if (!reader.IsDBNull(11))
            {
                var id = reader.GetString(11);
                if (!contexts.TryGetValue(id, out var cachedContext))
                    contexts[id] = cachedContext = Unprotect<string>((byte[])reader[12]);
                context = cachedContext;
            }
            var task = new TaskLabel();
            if (!reader.IsDBNull(7))
            {
                var id = reader.GetString(7);
                if (!tasks.TryGetValue(id, out var cached))
                    tasks[id] = cached = Unprotect<TaskLabel>((byte[])reader[8]);
                task = cached;
            }
            rows.Add(new Allocation(reader.GetString(0), day, new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
                new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero), reader.GetString(3), (SpanKind)reader.GetInt32(4),
                (Disposition)reader.GetInt32(5), task, reader.GetBoolean(6),
                reader.IsDBNull(9) ? null : (RoundingMode)reader.GetInt32(9), reader.IsDBNull(10) ? null : reader.GetString(10),
                context));
        }
        return rows;
    }

    public HashSet<DateOnly> UnreviewedDays()
    {
        using var command = Command("SELECT DISTINCT day FROM allocation WHERE status=$status AND kind<>$idle",
            null, ("$status", (int)Disposition.Unassigned), ("$idle", (int)SpanKind.Idle));
        using var reader = command.ExecuteReader();
        var days = new HashSet<DateOnly>();
        while (reader.Read())
            days.Add(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture));
        return days;
    }

    public DateOnly[] CompletionDatesBefore(DateOnly day)
    {
        using var command = Command("""
            SELECT day FROM allocation WHERE day<$day
            UNION SELECT day FROM manual_tempo WHERE day<$day
            UNION SELECT substr(name,-10) AS day FROM setting
                WHERE (name GLOB 'tempo-attempts-*' OR name GLOB 'tempo-pending-*') AND substr(name,-10)<$day
            ORDER BY day DESC
            """, null, ("$day", FormatDay(day)));
        using var reader = command.ExecuteReader();
        var days = new List<DateOnly>();
        while (reader.Read())
            days.Add(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture));
        return days.ToArray();
    }

    public CompletionDay Completion(DateOnly day, RoundingMode mode, bool inProgress = false) =>
        new(Rows(day), ManualEntries(day), mode, TempoAttempts(day), inProgress);

    public void ReplaceRows(IReadOnlyCollection<string> removed, IReadOnlyCollection<Allocation> updated,
        IReadOnlyCollection<Allocation>? expectedAssistedRows = null, IReadOnlyCollection<SegmentGuard>? guards = null,
        Func<IReadOnlyList<Allocation>, string?>? verify = null)
    {
        CompletionChanging?.Invoke();
        if (updated.Any(row => row.End <= row.Start))
            throw new InvalidOperationException("The end time must follow the start time.");
        using var transaction = connection.BeginTransaction();
        if (expectedAssistedRows != null)
        {
            if (removed.Count != 0)
                throw new InvalidOperationException("Assisted review can only assign or undo an assignment for unapproved recorded activity.");
            CheckAssistedEdit(expectedAssistedRows, updated, transaction);
            if (guards is { Count: > 0 } || verify != null)
            {
                var current = Rows(expectedAssistedRows.First().Day, transaction);
                if (guards is { Count: > 0 } && AssistedGuard.CheckSegments(current, guards) is { } changed)
                    throw new InvalidOperationException(changed);
                if (verify?.Invoke(current) is { } refused)
                    throw new InvalidOperationException(refused);
            }
        }
        var taskIds = new Dictionary<TaskLabel, string>();
        foreach (var id in removed)
        {
            using var delete = Command("DELETE FROM allocation WHERE id=$id", transaction, ("$id", id));
            delete.ExecuteNonQuery();
        }
        try
        {
            foreach (var row in updated)
            {
                if (row.Status is Disposition.Billable or Disposition.Internal && HasAssignedOverlap(row, transaction))
                    throw new InvalidOperationException("This interval overlaps time already assigned to a task.");
                SaveAllocation(row, transaction, taskIds);
            }
        }
        finally { knownContexts.Clear(); }
        using var cleanup = Command("""
            DELETE FROM task WHERE NOT EXISTS (SELECT 1 FROM allocation WHERE task_id=task.id);
            DELETE FROM context WHERE NOT EXISTS (SELECT 1 FROM allocation WHERE context_id=context.id);
            """, transaction);
        cleanup.ExecuteNonQuery();
        knownContexts.Clear();
        transaction.Commit();
        knownTasks.Clear();
        CompletionRevision++;
        RememberTasks(taskIds);
    }

    public List<ManualEntry> ManualEntries(DateOnly day) => ManualEntries(day, null);

    private List<ManualEntry> ManualEntries(DateOnly day, SqliteTransaction? transaction)
    {
        using var command = Command("SELECT payload FROM manual_tempo WHERE day=$day", transaction, ("$day", FormatDay(day)));
        using var reader = command.ExecuteReader();
        var entries = new List<ManualEntry>();
        while (reader.Read())
            entries.Add(Unprotect<ManualEntry>((byte[])reader[0]));
        return entries;
    }

    public void SaveManualEntry(DateOnly day, ManualEntry entry)
        => SaveManualEntries(day, [entry]);

    public void SaveManualEntries(DateOnly day, IReadOnlyList<ManualEntry> entries,
        IReadOnlyCollection<string>? replacedIds = null, IReadOnlyCollection<string>? clearAttempts = null)
    {
        CompletionChanging?.Invoke();
        var attempts = clearAttempts == null ? null : TempoAttempts(day);
        foreach (var key in clearAttempts ?? [])
            attempts!.Remove(key);
        using var transaction = connection.BeginTransaction();
        foreach (var id in replacedIds ?? [])
        {
            using var remove = Command("DELETE FROM manual_tempo WHERE id=$id AND day=$day", transaction,
                ("$id", id), ("$day", FormatDay(day)));
            remove.ExecuteNonQuery();
        }
        foreach (var entry in entries)
        {
            using var command = Command("""
                INSERT INTO manual_tempo VALUES ($id,$day,$payload)
                ON CONFLICT(id) DO UPDATE SET payload=excluded.payload
                """, transaction, ("$id", entry.Id), ("$day", FormatDay(day)), ("$payload", Protect(entry)));
            command.ExecuteNonQuery();
        }
        if (attempts != null)
            SaveTempoAttempts(day, attempts, transaction);
        transaction.Commit();
        CompletionRevision++;
    }

    public ExportReceipt[] Exports(DateOnly day) => Read<ExportReceipt[]>("export-" + FormatDay(day)) ?? [];
    public void RestoreExportReceipts(DateOnly day, ExportReceipt[] receipts) => Save("export-" + FormatDay(day), receipts);

    public void RecordExport(DateOnly day, IReadOnlyList<Worklog> lines)
    {
        var marked = Exports(day).Concat(lines.Select(line => new ExportReceipt(line.Issue, line.Intervals.ToArray())))
            .GroupBy(item => item.Issue, StringComparer.Ordinal)
            .Select(group => new ExportReceipt(group.Key, group.SelectMany(receipt => receipt.Intervals).Distinct().ToArray()))
            .ToArray();
        Save("export-" + FormatDay(day), marked);
    }

    // Exact approval keys of lines handed off by copy or export. Mark entered compares against them,
    // so a line that changed after the handoff cannot be recorded with a total Tempo never received.
    public string[] HandoffKeys(DateOnly day) => Read<string[]>("handoff-keys-" + FormatDay(day)) ?? [];
    public void RestoreHandoffKeys(DateOnly day, string[] keys) => Save("handoff-keys-" + FormatDay(day), keys);
    public void RecordHandoffKeys(DateOnly day, IEnumerable<string> keys) =>
        Save("handoff-keys-" + FormatDay(day), HandoffKeys(day).Concat(keys).Distinct(StringComparer.Ordinal).ToArray());

    public string[] ConfirmedHandoffKeys(DateOnly day) => Read<string[]>("manual-handoff-" + FormatDay(day)) ?? [];
    public void ConfirmHandoffKeys(DateOnly day, IEnumerable<string> keys) => Save("manual-handoff-" + FormatDay(day),
        ConfirmedHandoffKeys(day).Concat(keys).Distinct(StringComparer.Ordinal).ToArray());

    public Dictionary<string, TempoAttempt> TempoAttempts(DateOnly day) =>
        Read<Dictionary<string, TempoAttempt>>("tempo-attempts-" + FormatDay(day)) ??
        (Read<string[]>("tempo-pending-" + FormatDay(day)) ?? []).ToDictionary(key => key,
            _ => new TempoAttempt(TempoAttemptState.Pending), StringComparer.Ordinal);

    public string[] TempoPending(DateOnly day) => TempoAttempts(day)
        .Where(item => item.Value.State == TempoAttemptState.Pending).Select(item => item.Key).ToArray();

    public void SetTempoAttempt(DateOnly day, string key, TempoAttempt? attempt)
    {
        CompletionChanging?.Invoke();
        var attempts = TempoAttempts(day);
        if (attempt == null)
            attempts.Remove(key);
        else
            attempts[key] = attempt;
        SaveTempoAttempts(day, attempts);
        CompletionRevision++;
    }

    private void SaveTempoAttempts(DateOnly day, Dictionary<string, TempoAttempt> attempts, SqliteTransaction? transaction = null) =>
        Save("tempo-attempts-" + FormatDay(day), attempts, transaction);

    public CompletionJournal CompletionJournal() => Read<CompletionJournal>("completion-pilot") ?? new();
    public void SaveCompletionJournal(CompletionJournal journal) => Save("completion-pilot", journal);

    public string[] CorrectionCopies(DateOnly day) => Read<string[]>("correction-" + FormatDay(day)) ?? [];
    public void RestoreCorrectionCopies(DateOnly day, string[] keys) => Save("correction-" + FormatDay(day), keys);
    public void RecordCorrectionCopy(DateOnly day, string key) =>
        Save("correction-" + FormatDay(day), CorrectionCopies(day).Append(key).Distinct(StringComparer.Ordinal).ToArray());

    public void RemoveManualEntry(string id)
    {
        CompletionChanging?.Invoke();
        using var command = Command("DELETE FROM manual_tempo WHERE id=$id", null, ("$id", id));
        command.ExecuteNonQuery();
        CompletionRevision++;
    }

    public List<RequestDraft> Requests(DateOnly day)
    {
        using var command = Command("SELECT payload FROM request WHERE day=$day ORDER BY rowid",
            null, ("$day", FormatDay(day)));
        using var reader = command.ExecuteReader();
        var requests = new List<RequestDraft>();
        while (reader.Read())
            requests.Add(Unprotect<RequestDraft>((byte[])reader[0]));
        return requests;
    }

    public void SaveRequest(RequestDraft draft)
    {
        using var command = Command("""
            INSERT INTO request VALUES ($id,$day,$payload)
            ON CONFLICT(id) DO UPDATE SET payload=excluded.payload
            """, null, ("$id", draft.Id), ("$day", FormatDay(draft.Day)), ("$payload", Protect(draft)));
        command.ExecuteNonQuery();
    }

    public void ExpireSourcesAndReconstructions(DateTimeOffset utcNow)
    {
        using var transaction = connection.BeginTransaction();
        using var command = Command("DELETE FROM source WHERE ended < $end", transaction,
            ("$end", utcNow.AddDays(-30).UtcTicks));
        command.ExecuteNonQuery();
        PurgeReconstructions(DateOnly.FromDateTime(utcNow.LocalDateTime).AddDays(-ContextRetentionDays), transaction);
        transaction.Commit();
    }

    public void CheckpointWal()
    {
        using var command = Command("PRAGMA wal_checkpoint(TRUNCATE)");
        command.ExecuteNonQuery();
    }

    public void Dispose() => connection.Dispose();

    private bool HasAssignedOverlap(Allocation row, SqliteTransaction transaction)
    {
        using var command = Command("""
            SELECT 1 FROM allocation WHERE started < $end AND ended > $start AND id <> $id
                AND status IN (1,2) LIMIT 1
            """, transaction, ("$end", row.End.UtcTicks), ("$start", row.Start.UtcTicks), ("$id", row.Id));
        return command.ExecuteScalar() != null;
    }

    private void SaveAllocation(Allocation row, SqliteTransaction transaction, Dictionary<TaskLabel, string> taskIds)
    {
        string? taskId = null;
        if (row.Task != new TaskLabel() && !taskIds.TryGetValue(row.Task, out taskId))
        {
            if (!knownTasks.TryGetValue(row.Task, out taskId))
            {
                taskId = Guid.NewGuid().ToString("N");
                using var task = Command("INSERT INTO task VALUES ($id,$payload)", transaction,
                    ("$id", taskId), ("$payload", Protect(row.Task)));
                task.ExecuteNonQuery();
            }
            taskIds.Add(row.Task, taskId);
        }
        string? contextId = null;
        if (row.Context.Length > 0 && !knownContexts.TryGetValue(row.Context, out contextId))
        {
            contextId = Guid.NewGuid().ToString("N");
            using var context = Command("INSERT INTO context VALUES ($id,$payload)", transaction,
                ("$id", contextId), ("$payload", Protect(row.Context)));
            context.ExecuteNonQuery();
            if (knownContexts.Count > 512)
                knownContexts.Clear();
            knownContexts[row.Context] = contextId;
        }
        using var command = Command("""
            INSERT INTO allocation (id,day,started,ended,status,app,kind,approved,task_id,approved_mode,approved_key,context_id)
            VALUES ($id,$day,$start,$end,$status,$app,$kind,$approved,$task,$mode,$key,$context)
            ON CONFLICT(id) DO UPDATE SET day=excluded.day, started=excluded.started, ended=excluded.ended,
                status=excluded.status, app=excluded.app, kind=excluded.kind,
                approved=excluded.approved, task_id=excluded.task_id, approved_mode=excluded.approved_mode,
                approved_key=excluded.approved_key, context_id=excluded.context_id
            """, transaction, ("$context", contextId is null ? DBNull.Value : contextId), ("$id", row.Id), ("$day", FormatDay(row.Day)), ("$start", row.Start.UtcTicks),
            ("$end", row.End.UtcTicks), ("$status", (int)row.Status), ("$app", row.App), ("$kind", (int)row.Kind),
            ("$approved", row.Approved), ("$task", taskId is null ? DBNull.Value : taskId),
            ("$mode", row.ApprovedMode.HasValue ? (int)row.ApprovedMode.Value : DBNull.Value),
            ("$key", row.ApprovedKey is null ? DBNull.Value : row.ApprovedKey));
        command.ExecuteNonQuery();
    }

    private void RememberTasks(Dictionary<TaskLabel, string> taskIds)
    {
        if (knownTasks.Count + taskIds.Count > 128)
            knownTasks.Clear();
        foreach (var pair in taskIds.Take(128))
            knownTasks[pair.Key] = pair.Value;
    }

    private T? Read<T>(string name, SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT value FROM setting WHERE name=$name", transaction, ("$name", name));
        return command.ExecuteScalar() is byte[] bytes ? Unprotect<T>(bytes) : default;
    }

    private void Save<T>(string name, T value, SqliteTransaction? transaction = null)
    {
        using var command = Command("""
            INSERT INTO setting VALUES ($name,$value) ON CONFLICT(name) DO UPDATE SET value=excluded.value
            """, transaction, ("$name", name), ("$value", Protect(value)));
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null, params (string Key, object Value)[] values)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (key, value) in values)
            command.Parameters.AddWithValue(key, value);
        return command;
    }

    private static string FormatDay(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static byte[] Protect<T>(T value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value);
        try { return ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(json); }
    }

    private static T Unprotect<T>(byte[] bytes)
    {
        var json = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<T>(json)
                ?? throw new InvalidOperationException("A stored record could not be read.");
        }
        finally { CryptographicOperations.ZeroMemory(json); }
    }
}
