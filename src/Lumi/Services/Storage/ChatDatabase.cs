using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Lumi.Services.Storage;

/// <summary>
/// Lumi's chat database (<c>lumi.db</c>): chat metadata and every chat's transcript, one row per
/// message, and an index of what the messages say. Runs in WAL mode so readers never wait for the
/// single writer, and keeps its page caches small so memory use stays predictable however much
/// history the profile holds.
/// </summary>
/// <remarks>
/// <c>PRAGMA user_version</c> is the schema a database was last upgraded to. <c>meta</c> also keeps
/// <c>min_reader_version</c>: the oldest schema a Lumi must know to use the database safely. A later
/// Lumi that only adds to the schema (a table, an index) leaves it alone, so earlier versions keep
/// opening the database; one that changes what existing rows mean raises it, and earlier versions
/// then refuse to open it instead of damaging it. Message facets (<c>kind</c>) are computed when a
/// row is written, so a change to <see cref="ChatMessageKinds.Classify"/> needs a schema upgrade that
/// reclassifies the stored rows. Data derived from messages is kept by triggers instead: the text
/// index (<c>messages_text</c>, schema 2) follows every write, including an earlier Lumi's, so a
/// change to what it holds (<see cref="SearchTextSql"/>) needs an upgrade that rebuilds it.
/// </remarks>
internal sealed class ChatDatabase
{
    public const int SchemaVersion = 2;

    /// <summary>The <c>min_reader_version</c> this Lumi's schema requires (see the remarks).</summary>
    private const int MinimumReaderVersion = 1;

    private readonly string _connectionString;

    public ChatDatabase(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 60,
        }.ToString();

        using var connection = OpenConnection();
        EnsureSchema(connection);
    }

    public string Path { get; }

    /// <summary>
    /// True while a database from before the text index (schema 1) waits for <see cref="BuildTextIndex"/>,
    /// which reads every stored message, so Lumi runs it on the writer while the startup window shows.
    /// </summary>
    public bool NeedsTextIndex => _needsTextIndex;

    private volatile bool _needsTextIndex;

    /// <summary>
    /// True while a file from before incremental vacuum waits for its one-time rewrite (see
    /// <c>ChatStore.SwitchToIncrementalVacuum</c>). Read when the file opens: a pooled connection
    /// reports the mode it saw when it was opened, not a later switch.
    /// </summary>
    public bool NeedsVacuumSwitch => _needsVacuumSwitch;

    private volatile bool _needsVacuumSwitch;

    internal void NoteVacuumSwitched() => _needsVacuumSwitch = false;

    /// <summary>Opens a pooled connection configured for Lumi. Callers dispose it.</summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "PRAGMA busy_timeout = 15000;" +
                "PRAGMA foreign_keys = ON;" +
                "PRAGMA synchronous = NORMAL;" +
                "PRAGMA temp_store = MEMORY;" +
                "PRAGMA cache_size = -4096;" +
                // After a large write (a deleted chat, a rebuilt file) the write-ahead log shrinks back.
                "PRAGMA journal_size_limit = 67108864;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Releases pooled connections so the database files can be moved or deleted.</summary>
    public void ReleasePooledConnections()
    {
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        var version = ReadUserVersion(connection);
        if (version == 0)
        {
            // A new database: pages freed by deletes can then go back to the disk (see ChatStore.ReclaimFreeSpace).
            // Only possible before the first table exists; an older database is switched by a rewrite.
            using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "PRAGMA auto_vacuum = INCREMENTAL;";
            vacuum.ExecuteNonQuery();
        }

        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteScalar();
            wal.CommandText = "PRAGMA auto_vacuum;";
            _needsVacuumSwitch = Convert.ToInt64(wal.ExecuteScalar()) == 0;
        }

        if (version > SchemaVersion)
        {
            // A newer Lumi upgraded the database. Additions it made are ignored here; a change to
            // what the rows mean is not something this version may write over.
            var minimumReader = ReadMinimumReaderVersion(connection) ?? version;
            if (minimumReader > SchemaVersion)
            {
                throw new InvalidDataException(
                    "Your chats were saved by a newer version of Lumi. Update Lumi to open them " +
                    $"(chat database schema {version} needs schema {minimumReader}; this version knows schema {SchemaVersion}).");
            }

            return;
        }

        if (version == SchemaVersion)
            return;

        if (version == 1)
        {
            _needsTextIndex = true;
            return;
        }

        using var transaction = connection.BeginTransaction(deferred: false);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = SchemaV1 + TextIndexSchema;
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO meta (key, value) VALUES ('min_reader_version', $minimum) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value;" +
                $"PRAGMA user_version = {SchemaVersion};";
            command.Parameters.AddWithValue("$minimum", MinimumReaderVersion.ToString(CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Upgrades a schema 1 database: adds the text index and indexes every stored message. Run by
    /// the writer, before any other write, so the triggers and the rows they missed meet exactly once.
    /// Returns false when there was nothing to do.
    /// </summary>
    internal static bool BuildTextIndex(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(command.ExecuteScalar()) != 1)
            return false;

        command.CommandText = TextIndexSchema + $"""
            INSERT INTO messages_text (rowid, text, chat_key, role)
            SELECT message_key, text, chat_key, role
            FROM (SELECT message_key, chat_key, role, {SearchTextSql("messages")} AS text
                  FROM messages WHERE {IsSearchableSql("messages")})
            WHERE text <> '';
            PRAGMA user_version = {SchemaVersion};
            """;
        command.ExecuteNonQuery();
        return true;
    }

    /// <summary>Called once the text index a schema 1 database waited for is committed.</summary>
    internal void NoteTextIndexBuilt() => _needsTextIndex = false;

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int? ReadMinimumReaderVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'min_reader_version';";
        try
        {
            return command.ExecuteScalar() is string value
                   && int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minimum)
                ? minimum
                : null;
        }
        catch (SqliteException)
        {
            // No meta table: whatever wrote this database did not say who may read it.
            return null;
        }
    }

    /// <summary>
    /// Copies committed writes from the write-ahead log into the database file and syncs it, so they
    /// survive a power loss even though everyday commits are not synced one by one.
    /// </summary>
    public void Checkpoint()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(FULL);";
        command.ExecuteNonQuery();
    }

    // ── Damage ──────────────────────────────────────────────────────────────

    /// <summary>True when SQLite reports the file as damaged, or as not a database at all.</summary>
    internal static bool IsDamage(Exception exception)
        => exception is SqliteException { SqliteErrorCode: SqliteCorrupt or SqliteNotADatabase };

    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private static string DamageMarkerOf(string path) => path + ".check";

    /// <summary>True when a read or write ran into damage (see <see cref="NoteDamage"/>) since the file was last checked.</summary>
    internal static bool HasDamageMarker(string path) => File.Exists(DamageMarkerOf(System.IO.Path.GetFullPath(path)));

    /// <summary>
    /// Remembers damage a read or write ran into, so the next start checks the whole file and copies
    /// what it can out of it if it is damaged (most damage is found while Lumi runs, not when it opens).
    /// </summary>
    internal void NoteDamage(Exception exception)
    {
        if (!IsDamage(exception) || File.Exists(DamageMarkerOf(Path)))
            return;

        Trace.TraceError("[ChatStore] The chat database is damaged; Lumi checks it at the next start: {0}", exception.Message);
        try
        {
            File.WriteAllText(DamageMarkerOf(Path), exception.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("[ChatStore] Could not note the damage: {0}", ex.Message);
        }
    }

    /// <summary>Checks the structure of the whole file (<c>PRAGMA quick_check</c>); true when it is intact.</summary>
    internal bool IsIntact()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check(1);";
        return command.ExecuteScalar() as string == "ok";
    }

    internal void ClearDamageMarker() => TryDelete(DamageMarkerOf(Path));

    private static string RecoveryMarkerOf(string path) => path + ".recover";

    /// <summary>
    /// A damaged database moved aside whose chats were not all copied over (Lumi was closed while it
    /// recovered them), or null. The recovery goes on from it at this start.
    /// </summary>
    internal static string? PendingRecovery(string path)
    {
        var marker = RecoveryMarkerOf(System.IO.Path.GetFullPath(path));
        try
        {
            if (!File.Exists(marker))
                return null;
            var copy = File.ReadAllText(marker).Trim();
            if (File.Exists(copy))
                return copy;
            File.Delete(marker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("[ChatStore] Could not read the unfinished recovery: {0}", ex.Message);
        }

        return null;
    }

    /// <summary>Forgets the damaged copy being recovered, once its chats were copied over.</summary>
    internal void ClearPendingRecovery() => TryDelete(RecoveryMarkerOf(Path));

    /// <summary>
    /// Moves a damaged database aside as <c>lumi.damaged-{time}.db</c>, with its write-ahead log, so a
    /// new one can take its place and what still reads can be copied out of it; notes it, so a
    /// recovery cut short goes on at the next start (<see cref="PendingRecovery"/>). Returns the new path.
    /// </summary>
    internal static string SetAside(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        SqliteConnection.ClearAllPools();
        var directory = System.IO.Path.GetDirectoryName(path)!;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = System.IO.Path.Combine(directory,
            $"{System.IO.Path.GetFileNameWithoutExtension(path)}.damaged-{stamp}{System.IO.Path.GetExtension(path)}");
        for (var attempt = 2; File.Exists(target); attempt++)
        {
            target = System.IO.Path.Combine(directory,
                $"{System.IO.Path.GetFileNameWithoutExtension(path)}.damaged-{stamp}-{attempt}{System.IO.Path.GetExtension(path)}");
        }

        // The log first: left beside a new database, its pages would be read as that database's.
        if (File.Exists(path + "-wal"))
            File.Move(path + "-wal", target + "-wal");
        File.Move(path, target);
        TryDelete(path + "-shm");
        TryDelete(DamageMarkerOf(path));
        try
        {
            File.WriteAllText(RecoveryMarkerOf(path), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The chats are still recovered now; only a recovery cut short would not go on.
            Trace.TraceWarning("[ChatStore] Could not note the recovery: {0}", ex.Message);
        }

        return target;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("[ChatStore] Could not delete {0}: {1}", path, ex.Message);
        }
    }

    // Body JSON is the last column so scans of the small columns never touch its overflow pages.
    private const string SchemaV1 = """
        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS chats (
            chat_key        INTEGER PRIMARY KEY,
            id              BLOB NOT NULL UNIQUE,
            updated_at      INTEGER NOT NULL,
            message_count   INTEGER NOT NULL DEFAULT 0,
            next_seq        INTEGER NOT NULL DEFAULT 1,
            content_version INTEGER NOT NULL DEFAULT 0,
            import_state    INTEGER NOT NULL DEFAULT 0,
            metadata        TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS chats_by_version ON chats(content_version);

        CREATE TABLE IF NOT EXISTS messages (
            message_key         INTEGER PRIMARY KEY,
            chat_key            INTEGER NOT NULL REFERENCES chats(chat_key) ON DELETE CASCADE,
            seq                 INTEGER NOT NULL,
            id                  BLOB NOT NULL,
            role                TEXT NOT NULL,
            timestamp           INTEGER NOT NULL,
            tool_call_id        TEXT,
            parent_tool_call_id TEXT,
            tool_name           TEXT,
            kind                TEXT,
            text_length         INTEGER NOT NULL,
            body                TEXT NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS messages_by_seq ON messages(chat_key, seq);
        CREATE INDEX IF NOT EXISTS messages_by_id ON messages(chat_key, id);
        CREATE INDEX IF NOT EXISTS messages_by_tool_call ON messages(chat_key, tool_call_id)
            WHERE tool_call_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS messages_by_parent ON messages(chat_key, parent_tool_call_id)
            WHERE parent_tool_call_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS messages_outline ON messages(chat_key, seq, role, text_length, timestamp, id)
            WHERE role IN ('user', 'assistant', 'system', 'error');
        CREATE INDEX IF NOT EXISTS messages_by_kind ON messages(chat_key, kind, seq)
            WHERE kind IS NOT NULL;
        CREATE INDEX IF NOT EXISTS messages_prompts ON messages(timestamp)
            WHERE role = 'user';
        """;

    /// <summary>
    /// Schema 2: an index of what messages say (<see cref="SearchTextSql"/>), kept by triggers in the
    /// transaction of every write. Trigram tokens find any run of three or more characters, inside
    /// words too, ignoring case and Latin accents (search looks words up as typed, see
    /// <see cref="ChatSearchText.Words"/>). The index stores no text
    /// (contentless), only each entry's chat and role, so a match needs no message row.
    /// </summary>
    internal static readonly string TextIndexSchema = $"""
        CREATE VIRTUAL TABLE IF NOT EXISTS messages_text USING fts5(
            text, chat_key UNINDEXED, role UNINDEXED,
            content = '', contentless_delete = 1, contentless_unindexed = 1,
            tokenize = 'trigram remove_diacritics 1');

        CREATE TRIGGER IF NOT EXISTS messages_text_insert AFTER INSERT ON messages
        WHEN {IsSearchableSql("new")}
        BEGIN
            INSERT INTO messages_text (rowid, text, chat_key, role)
            SELECT new.message_key, text, new.chat_key, new.role FROM (SELECT {SearchTextSql("new")} AS text)
            WHERE text <> '';
        END;

        CREATE TRIGGER IF NOT EXISTS messages_text_update AFTER UPDATE OF role, tool_name, body ON messages
        WHEN {IsSearchableSql("old")} OR {IsSearchableSql("new")}
        BEGIN
            DELETE FROM messages_text WHERE rowid = old.message_key;
            INSERT INTO messages_text (rowid, text, chat_key, role)
            SELECT new.message_key, text, new.chat_key, new.role FROM (SELECT {SearchTextSql("new")} AS text)
            WHERE {IsSearchableSql("new")} AND text <> '';
        END;

        CREATE TRIGGER IF NOT EXISTS messages_text_delete AFTER DELETE ON messages
        WHEN {IsSearchableSql("old")}
        BEGIN
            DELETE FROM messages_text WHERE rowid = old.message_key;
        END;
        """;

    /// <summary>Messages the text index holds: conversation messages and input requests.</summary>
    internal static string IsSearchableSql(string row)
        => $"({row}.role IN ('user', 'assistant', 'error') OR ({row}.role = 'tool' AND {row}.tool_name = 'ask_input'))";

    /// <summary>
    /// What the text index holds of a message, as SQL over the row named <paramref name="row"/>: a
    /// conversation message's text, a job wake's job name and a submitted form's receipt; an input
    /// request's title and description. That is all in-chat search finds in a message
    /// (<see cref="TranscriptSearchText"/>) but reasoning, which it reads one chat at a time, and what
    /// chat search finds in one. <see cref="ChatSearchText.Of"/> says the same of a message in memory.
    /// </summary>
    internal static string SearchTextSql(string row) => $"""
        CASE
            WHEN {row}.role IN ('user', 'assistant', 'error') THEN concat_ws(char(10),
                json_extract({row}.body, '$.content'),
                CASE WHEN json_extract({row}.body, '$.author') LIKE 'Lumi Job - %' THEN json_extract({row}.body, '$.author') END,
                json_extract({row}.body, '$.interactionReceipt.title'),
                json_extract({row}.body, '$.interactionReceipt.summary'),
                json_extract({row}.body, '$.interactionReceipt.detail'))
            WHEN {row}.role = 'tool' AND {row}.tool_name = 'ask_input'
                 AND json_valid(json_extract({row}.body, '$.inputDefinition')) THEN concat_ws(char(10),
                json_extract(json_extract({row}.body, '$.inputDefinition'), '$.title'),
                json_extract(json_extract({row}.body, '$.inputDefinition'), '$.description'))
        END
        """;
}
