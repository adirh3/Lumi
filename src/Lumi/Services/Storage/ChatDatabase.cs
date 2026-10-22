using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Lumi.Services.Storage;

/// <summary>
/// Lumi's chat database (<c>lumi.db</c>): chat metadata and every chat's transcript, one row per
/// message. Runs in WAL mode so readers never wait for the single writer, and keeps its page caches
/// small so memory use stays predictable however much history the profile holds.
/// </summary>
/// <remarks>
/// <c>PRAGMA user_version</c> is the schema a database was last upgraded to. <c>meta</c> also keeps
/// <c>min_reader_version</c>: the oldest schema a Lumi must know to use the database safely. A later
/// Lumi that only adds to the schema (a table, an index) leaves it alone, so earlier versions keep
/// opening the database; one that changes what existing rows mean raises it, and earlier versions
/// then refuse to open it instead of damaging it. Message facets (<c>kind</c>) are computed when a
/// row is written, so a change to <see cref="ChatMessageKinds.Classify"/> needs a schema upgrade that
/// reclassifies the stored rows. Likewise, data a later Lumi derives from messages (an index of their
/// text, say) is missing for rows written before it existed, or by an earlier Lumi since: the version
/// that adds it brings it up to date when it opens the database.
/// </remarks>
internal sealed class ChatDatabase
{
    public const int SchemaVersion = 1;

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

    /// <summary>Opens a pooled connection configured for Lumi. Callers dispose it.</summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "PRAGMA busy_timeout = 15000;" +
            "PRAGMA foreign_keys = ON;" +
            "PRAGMA synchronous = NORMAL;" +
            "PRAGMA temp_store = MEMORY;" +
            "PRAGMA cache_size = -4096;";
        command.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Releases pooled connections so the database files can be moved or deleted.</summary>
    public void ReleasePooledConnections()
    {
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteScalar();
        }

        var version = ReadUserVersion(connection);
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

        using var transaction = connection.BeginTransaction(deferred: false);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = SchemaV1;
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO meta (key, value) VALUES ('min_reader_version', $minimum) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value;" +
                $"PRAGMA user_version = {SchemaVersion};";
            command.Parameters.AddWithValue("$minimum", MinimumReaderVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

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
}
