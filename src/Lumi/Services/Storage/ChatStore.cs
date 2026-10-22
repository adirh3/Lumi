using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Lumi.Models;
using Microsoft.Data.Sqlite;

namespace Lumi.Services.Storage;

/// <summary>
/// Repository over <see cref="ChatDatabase"/>: chat rows and their transcripts. Writes are queued to
/// a single writer and applied in order; reads run on pooled connections, first waiting for the
/// chat's queued writes so a reader always sees what this process already saved. Chats whose
/// transcript still sits in a legacy JSON file are imported before they are read.
/// </summary>
internal sealed partial class ChatStore : IDisposable
{
    private readonly ChatDatabase _database;
    private readonly ChatStoreWriter _writer;
    private readonly ConcurrentDictionary<Guid, long> _contentVersions = new();
    private readonly ConcurrentDictionary<Guid, int> _messageCounts = new();
    private readonly ConcurrentDictionary<Guid, long> _nextSeqs = new();
    private readonly ConcurrentDictionary<Guid, ChatImportState> _importStates = new();
    private bool _disposed;

    public ChatStore(string databasePath)
    {
        _database = new ChatDatabase(databasePath);
        _writer = new ChatStoreWriter(_database);
    }

    internal ChatStoreWriter Writer => _writer;

    /// <summary>Imports a chat's legacy transcript before it is read; set by the legacy importer.</summary>
    internal Func<Guid, Task>? EnsureImportedAsync { get; set; }

    /// <summary>Raised on a background thread after a chat's stored transcript changed (written, imported or deleted).</summary>
    public event Action<Guid>? ChatContentCommitted;

    // ── Chat rows ───────────────────────────────────────────────────────────

    /// <summary>Reads every chat row. Used once at startup.</summary>
    public IReadOnlyList<StoredChat> LoadChats()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, metadata, message_count, next_seq, content_version, import_state FROM chats ORDER BY updated_at DESC;";
        using var reader = command.ExecuteReader();
        var chats = new List<StoredChat>();
        while (reader.Read())
        {
            var id = ReadGuid(reader, 0);
            Chat chat;
            try
            {
                chat = ChatStorageJson.DeserializeChat(reader.GetString(1));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
            {
                Trace.TraceWarning("[ChatStore] Skipping chat {0} with unreadable metadata: {1}", id, ex.Message);
                continue;
            }

            chat.Id = id;
            var stored = new StoredChat(
                chat,
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                (ChatImportState)reader.GetInt32(5));
            chat.MessageCount = stored.MessageCount;
            _contentVersions[id] = stored.ContentVersion;
            _messageCounts[id] = stored.MessageCount;
            _nextSeqs[id] = stored.NextSeq;
            _importStates[id] = stored.ImportState;
            chats.Add(stored);
        }

        return chats;
    }

    /// <summary>
    /// Queues metadata updates for chats, leaving their transcript counters alone. Only chats with a
    /// row are updated: a chat gets its row with its first transcript write, and a late update for a
    /// chat deleted meanwhile must not bring it back.
    /// </summary>
    public Task UpdateChatMetadataAsync(IReadOnlyList<(Guid Id, long UpdatedAtMs, string MetadataJson)> chats)
    {
        if (chats.Count == 0)
            return Task.CompletedTask;

        return _writer.Enqueue(null, (connection, transaction) =>
        {
            using var command = CreateCommand(connection, transaction,
                "UPDATE chats SET updated_at = $updated, metadata = $metadata WHERE id = $id;");
            var id = command.Parameters.Add("$id", SqliteType.Blob);
            var updated = command.Parameters.Add("$updated", SqliteType.Integer);
            var metadata = command.Parameters.Add("$metadata", SqliteType.Text);
            command.Prepare();
            foreach (var chat in chats)
            {
                id.Value = chat.Id.ToByteArray();
                updated.Value = chat.UpdatedAtMs;
                metadata.Value = chat.MetadataJson;
                command.ExecuteNonQuery();
            }
        });
    }

    /// <summary>Queues rows for chats whose transcript is still in a legacy file, unless they already exist.</summary>
    internal Task InsertPendingChatsAsync(IReadOnlyList<(Guid Id, long UpdatedAtMs, string MetadataJson, int MessageCount)> chats)
    {
        if (chats.Count == 0)
            return Task.CompletedTask;

        return _writer.Enqueue(null, (connection, transaction) =>
        {
            using var command = CreateCommand(connection, transaction, """
                INSERT INTO chats (id, updated_at, message_count, import_state, metadata)
                VALUES ($id, $updated, $count, 1, $metadata)
                ON CONFLICT(id) DO NOTHING;
                """);
            var id = command.Parameters.Add("$id", SqliteType.Blob);
            var updated = command.Parameters.Add("$updated", SqliteType.Integer);
            var count = command.Parameters.Add("$count", SqliteType.Integer);
            var metadata = command.Parameters.Add("$metadata", SqliteType.Text);
            command.Prepare();
            foreach (var chat in chats)
            {
                id.Value = chat.Id.ToByteArray();
                updated.Value = chat.UpdatedAtMs;
                count.Value = chat.MessageCount;
                metadata.Value = chat.MetadataJson;
                command.ExecuteNonQuery();
            }
        });
    }

    /// <summary>Deletes a chat and its transcript. Completes once the deletion is committed; fails if it could not be.</summary>
    public async Task DeleteChatAsync(Guid chatId)
    {
        await _writer.Enqueue(chatId, (connection, transaction) =>
        {
            using var command = CreateCommand(connection, transaction, "DELETE FROM chats WHERE id = $id;");
            command.Parameters.AddWithValue("$id", chatId.ToByteArray());
            command.ExecuteNonQuery();
            // Counters change with the rows, in the order writes commit.
            ForgetCounters(chatId);
        }).ConfigureAwait(false);
        ChatContentCommitted?.Invoke(chatId);
    }

    /// <summary>Deletes every chat and transcript. Completes once the deletion is committed.</summary>
    public Task DeleteAllChatsAsync()
        => _writer.Enqueue(null, (connection, transaction) =>
        {
            using var command = CreateCommand(connection, transaction, "DELETE FROM messages; DELETE FROM chats;");
            command.ExecuteNonQuery();
            _contentVersions.Clear();
            _messageCounts.Clear();
            _nextSeqs.Clear();
            _importStates.Clear();
        });

    private void ForgetCounters(Guid chatId)
    {
        _contentVersions.TryRemove(chatId, out _);
        _messageCounts.TryRemove(chatId, out _);
        _nextSeqs.TryRemove(chatId, out _);
        _importStates.TryRemove(chatId, out _);
    }

    /// <summary>
    /// Makes every committed write durable now (see <see cref="ChatDatabase.Checkpoint"/>): used before
    /// a step that relies on rows having reached the disk, such as dropping chats from the legacy index.
    /// </summary>
    public void Checkpoint() => _database.Checkpoint();

    // ── Transcript writes ───────────────────────────────────────────────────

    /// <summary>Queues one chat's transcript changes. Completes once they are committed.</summary>
    public Task WriteAsync(ChatWriteBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return _writer.Enqueue(batch.ChatId, (connection, transaction) =>
        {
            var chatKey = UpsertChatRow(connection, transaction, batch.ChatId, batch.UpdatedAtMs, batch.MetadataJson);

            if (batch.DeleteFromSeq is { } fromSeq)
            {
                using var truncate = CreateCommand(connection, transaction,
                    "DELETE FROM messages WHERE chat_key = $chat AND seq >= $from;");
                truncate.Parameters.AddWithValue("$chat", chatKey);
                truncate.Parameters.AddWithValue("$from", fromSeq);
                truncate.ExecuteNonQuery();
            }

            if (batch.DeletedSeqs.Count > 0)
            {
                using var delete = CreateCommand(connection, transaction,
                    "DELETE FROM messages WHERE chat_key = $chat AND seq = $seq;");
                delete.Parameters.AddWithValue("$chat", chatKey);
                var seq = delete.Parameters.Add("$seq", SqliteType.Integer);
                delete.Prepare();
                foreach (var deleted in batch.DeletedSeqs)
                {
                    seq.Value = deleted;
                    delete.ExecuteNonQuery();
                }
            }

            if (batch.Upserts.Count > 0)
                UpsertMessages(connection, transaction, chatKey, batch.Upserts);

            var (count, version, next) = UpdateTranscriptCounters(connection, transaction, chatKey, batch.NextSeq, batch.HasMessageChanges);
            _messageCounts[batch.ChatId] = count;
            _nextSeqs[batch.ChatId] = next;
            if (batch.HasMessageChanges)
                _contentVersions[batch.ChatId] = version;
        }).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && batch.HasMessageChanges)
                ChatContentCommitted?.Invoke(batch.ChatId);
            return task;
        }, TaskScheduler.Default).Unwrap();
    }

    internal static long UpsertChatRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid chatId,
        long updatedAtMs,
        string metadataJson)
    {
        using var command = CreateCommand(connection, transaction, UpsertChatSql);
        command.Parameters.AddWithValue("$id", chatId.ToByteArray());
        command.Parameters.AddWithValue("$updated", updatedAtMs);
        command.Parameters.AddWithValue("$metadata", metadataJson);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    internal static void UpsertMessages(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long chatKey,
        IReadOnlyList<EncodedMessage> messages)
    {
        using var command = CreateCommand(connection, transaction, """
            INSERT INTO messages (chat_key, seq, id, role, timestamp, tool_call_id, parent_tool_call_id,
                                  tool_name, kind, text_length, body)
            VALUES ($chat, $seq, $id, $role, $timestamp, $toolCall, $parent, $toolName, $kind, $length, $body)
            ON CONFLICT(chat_key, seq) DO UPDATE SET
                id = excluded.id,
                role = excluded.role,
                timestamp = excluded.timestamp,
                tool_call_id = excluded.tool_call_id,
                parent_tool_call_id = excluded.parent_tool_call_id,
                tool_name = excluded.tool_name,
                kind = excluded.kind,
                text_length = excluded.text_length,
                body = excluded.body;
            """);
        command.Parameters.AddWithValue("$chat", chatKey);
        var seq = command.Parameters.Add("$seq", SqliteType.Integer);
        var id = command.Parameters.Add("$id", SqliteType.Blob);
        var role = command.Parameters.Add("$role", SqliteType.Text);
        var timestamp = command.Parameters.Add("$timestamp", SqliteType.Integer);
        var toolCall = command.Parameters.Add("$toolCall", SqliteType.Text);
        var parent = command.Parameters.Add("$parent", SqliteType.Text);
        var toolName = command.Parameters.Add("$toolName", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        var length = command.Parameters.Add("$length", SqliteType.Integer);
        var body = command.Parameters.Add("$body", SqliteType.Text);
        command.Prepare();
        foreach (var message in messages)
        {
            seq.Value = message.Seq;
            id.Value = message.Id.ToByteArray();
            role.Value = message.Role;
            timestamp.Value = message.TimestampMs;
            toolCall.Value = (object?)message.ToolCallId ?? DBNull.Value;
            parent.Value = (object?)message.ParentToolCallId ?? DBNull.Value;
            toolName.Value = (object?)message.ToolName ?? DBNull.Value;
            kind.Value = (object?)message.Kind ?? DBNull.Value;
            length.Value = message.TextLength;
            body.Value = message.Body;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Refreshes a chat's message count and next position; a content change also takes a new version.</summary>
    internal static (int Count, long Version, long NextSeq) UpdateTranscriptCounters(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long chatKey,
        long nextSeq,
        bool contentChanged)
    {
        using var command = CreateCommand(connection, transaction, contentChanged
            ? """
              UPDATE chats SET
                  message_count = (SELECT COUNT(*) FROM messages WHERE chat_key = $chat),
                  next_seq = MAX(next_seq, $next, IFNULL((SELECT MAX(seq) + 1 FROM messages WHERE chat_key = $chat), 1)),
                  content_version = (SELECT IFNULL(MAX(content_version), 0) + 1 FROM chats)
              WHERE chat_key = $chat
              RETURNING message_count, content_version, next_seq;
              """
            : """
              UPDATE chats SET next_seq = MAX(next_seq, $next)
              WHERE chat_key = $chat
              RETURNING message_count, content_version, next_seq;
              """);
        command.Parameters.AddWithValue("$chat", chatKey);
        command.Parameters.AddWithValue("$next", nextSeq);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2)) : (0, 0, 1);
    }

    // ── Shared helpers ──────────────────────────────────────────────────────

    /// <summary>The chat's current content version: changes whenever its stored transcript changes.</summary>
    public long GetContentVersion(Guid chatId)
        => _contentVersions.TryGetValue(chatId, out var version) ? version : 0;

    /// <summary>Whether the chat's transcript is in the database, still in a legacy file, or failed to import.</summary>
    public ChatImportState GetImportState(Guid chatId)
        => _importStates.TryGetValue(chatId, out var state) ? state : ChatImportState.Imported;

    /// <summary>The next free transcript position for the chat, as of its last committed write.</summary>
    public long GetNextSeq(Guid chatId)
        => _nextSeqs.TryGetValue(chatId, out var next) ? next : 1;

    /// <summary>The number of messages stored for the chat, as of its last committed write.</summary>
    public int GetStoredMessageCount(Guid chatId)
        => _messageCounts.TryGetValue(chatId, out var count) ? count : 0;

    /// <summary>Refreshes the cached counters of a chat whose legacy transcript was just imported.</summary>
    internal void NoteImported(Guid chatId, int messageCount, ChatImportState state)
    {
        _messageCounts[chatId] = messageCount;
        _importStates[chatId] = state;
        var (version, next) = ReadNow(chatId, (connection, chatKey) => (
            ScalarLong(connection, "SELECT content_version FROM chats WHERE chat_key = $chat;", chatKey) ?? 0,
            ScalarLong(connection, "SELECT next_seq FROM chats WHERE chat_key = $chat;", chatKey) ?? 1), (0L, 1L));
        _contentVersions[chatId] = version;
        _nextSeqs[chatId] = next;
        ChatContentCommitted?.Invoke(chatId);
    }

    internal static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    internal static Guid ReadGuid(SqliteDataReader reader, int ordinal)
    {
        var bytes = (byte[])reader.GetValue(ordinal);
        return new Guid(bytes);
    }

    private const string UpsertChatSql = """
        INSERT INTO chats (id, updated_at, metadata) VALUES ($id, $updated, $metadata)
        ON CONFLICT(id) DO UPDATE SET updated_at = excluded.updated_at, metadata = excluded.metadata
        RETURNING chat_key;
        """;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _writer.Dispose();
        _database.ReleasePooledConnections();
    }
}
