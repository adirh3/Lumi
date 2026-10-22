using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using Microsoft.Data.Sqlite;

namespace Lumi.Services.Storage;

/// <summary>
/// Moves transcripts from Lumi's legacy per-chat JSON files (<c>chats/{id}.json</c>) into the chat
/// database. Each chat is imported in a streaming pass — a few hundred messages per write, so even a
/// 150 MB file never sits in memory — and only marked imported once every message landed. A chat is
/// imported on demand before anything reads it or adds to it; the rest are imported in the
/// background, newest first. A file damaged part-way keeps what was read before the damage; one that
/// cannot be read (locked, no access) is read again at every start until it can be, and its messages
/// then go in before anything added to the chat meanwhile. Legacy files are never changed. Once
/// nothing is pending, the legacy folder is renamed to a backup, whose files are deleted only with
/// their chats.
/// </summary>
internal sealed class LegacyChatImporter : IDisposable
{
    private const int ChunkSize = 400;
    internal const string BackupFolderName = "chats.pre-sqlite-backup";

    /// <summary>
    /// The position messages added to a chat whose legacy file could not be read start from. Every
    /// position below stays free for the legacy transcript, so it goes in before them once it can be
    /// read, and nothing has to move.
    /// </summary>
    internal const long UnreadableAppendBase = 1L << 32;

    private readonly ChatStore _store;
    private readonly string _chatsDirectory;
    private readonly string _backupDirectory;
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();
    private readonly ConcurrentDictionary<Guid, Lazy<Task>> _imports = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<TimeSpan> _sweepCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _sweepStarted;

    public LegacyChatImporter(ChatStore store, string chatsDirectory, IEnumerable<Guid> pendingChatIds)
    {
        _store = store;
        _chatsDirectory = chatsDirectory;
        _backupDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(chatsDirectory))!, BackupFolderName);
        foreach (var id in pendingChatIds)
            _pending[id] = 0;
        store.EnsureImportedAsync = EnsureImportedAsync;
    }

    /// <summary>Raised after a chat's transcript was imported, with its message count.</summary>
    public event Action<Guid, int>? ChatImported;

    public bool HasPending => !_pending.IsEmpty;

    public int PendingCount => _pending.Count;

    /// <summary>Completes with the sweep's duration once the background pass has finished.</summary>
    public Task<TimeSpan> SweepCompleted => _sweepCompleted.Task;

    public bool IsPending(Guid chatId) => _pending.ContainsKey(chatId);

    /// <summary>Imports the chat now if it is still pending; completes immediately otherwise.</summary>
    public Task EnsureImportedAsync(Guid chatId)
    {
        if (!_pending.ContainsKey(chatId))
            return Task.CompletedTask;
        return _imports.GetOrAdd(chatId, id => new Lazy<Task>(() => Task.Run(() => ImportAsync(id)))).Value;
    }

    /// <summary>
    /// Stops importing a chat that is being deleted: waits for an import already under way, then drops
    /// it from the pending set so the background pass skips it.
    /// </summary>
    public async Task ForgetAsync(Guid chatId)
    {
        _pending.TryRemove(chatId, out _);
        if (_imports.TryGetValue(chatId, out var running) && running.IsValueCreated)
            await WaitForImportAsync(chatId, running.Value).ConfigureAwait(false);
    }

    /// <summary>Stops importing every chat (all of them are being deleted), once imports under way end.</summary>
    public async Task ForgetAllAsync()
    {
        _pending.Clear();
        foreach (var (chatId, running) in _imports.ToArray())
        {
            if (running.IsValueCreated)
                await WaitForImportAsync(chatId, running.Value).ConfigureAwait(false);
        }
    }

    private static async Task WaitForImportAsync(Guid chatId, Task import)
    {
        try { await import.ConfigureAwait(false); }
        catch (Exception ex) { Trace.TraceWarning("[ChatStore] Import of deleted chat {0} failed: {1}", chatId, ex.Message); }
    }

    /// <summary>Deletes a deleted chat's legacy file, wherever it is kept (the legacy folder or its backup).</summary>
    public void DeleteLegacyFile(Guid chatId)
    {
        foreach (var directory in new[] { _chatsDirectory, _backupDirectory })
        {
            try
            {
                if (!Directory.Exists(directory))
                    continue;
                // A file moved into an existing backup carries a timestamp: {id}.{yyyyMMddHHmmss}.json.
                foreach (var file in Directory.EnumerateFiles(directory, $"{chatId}*.json"))
                    File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("[ChatStore] Could not delete the legacy file of chat {0}: {1}", chatId, ex.Message);
            }
        }
    }

    /// <summary>Deletes the legacy folder and its backup (every chat was deleted).</summary>
    public void DeleteLegacyFiles()
    {
        foreach (var directory in new[] { _chatsDirectory, _backupDirectory })
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("[ChatStore] Could not delete the legacy chats in {0}: {1}", directory, ex.Message);
            }
        }
    }

    /// <summary>Imports every pending chat in the background, in the given order, then retires the legacy folder.</summary>
    public void StartBackgroundImport(IReadOnlyList<Guid> order, TimeSpan delay)
    {
        if (Interlocked.Exchange(ref _sweepStarted, 1) != 0)
            return;

        var token = _shutdown.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                var stopwatch = Stopwatch.StartNew();
                var imported = 0;
                foreach (var chatId in order.Concat(_pending.Keys.ToArray()).Distinct())
                {
                    token.ThrowIfCancellationRequested();
                    if (!_pending.ContainsKey(chatId))
                        continue;
                    try
                    {
                        await EnsureImportedAsync(chatId).ConfigureAwait(false);
                        imported++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One chat failing (or deleted meanwhile) must not stop the others.
                        Trace.TraceWarning("[ChatStore] Legacy import of {0} failed; it is retried next start: {1}", chatId, ex.Message);
                    }
                }

                if (imported > 0)
                    Trace.TraceInformation("[ChatStore] Imported {0} legacy chats in {1:0.0}s.", imported, stopwatch.Elapsed.TotalSeconds);
                RetireLegacyFolder();
                _sweepCompleted.TrySetResult(stopwatch.Elapsed);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _sweepCompleted.TrySetCanceled(token);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("[ChatStore] Background legacy import stopped: {0}", ex);
                _sweepCompleted.TrySetException(ex);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Imports one chat's legacy file. Safe to repeat after an interrupted attempt. A file damaged
    /// part-way keeps the messages read before the damage; a file that cannot be read (locked, no
    /// access) leaves the chat <see cref="ChatImportState.Unreadable"/>, to be read again at the next
    /// start. Messages added to the chat since are kept, after the legacy transcript.
    /// </summary>
    internal async Task ImportAsync(Guid chatId)
    {
        try
        {
            // A chat deleted meanwhile is no longer pending; its file is not read again.
            if (!_pending.ContainsKey(chatId))
                return;

            var file = LocateLegacyFile(chatId);
            var state = ChatImportState.Imported;

            // A previous attempt may have stopped part-way; start over from an empty transcript.
            await _store.Writer.Enqueue(chatId, (connection, transaction) =>
                DeleteLegacyMessages(connection, transaction, chatId)).ConfigureAwait(false);

            if (file is not null)
            {
                try
                {
                    var (count, damage) = await ImportFileAsync(chatId, file).ConfigureAwait(false);
                    if (damage is not null)
                    {
                        Trace.TraceWarning("[ChatStore] {0} is damaged after {1} messages; they are kept and the file is left as it is: {2}",
                            file, count, damage.Message);
                        state = ChatImportState.Failed;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Trace.TraceWarning("[ChatStore] Could not read {0}; it is read again at the next start: {1}", file, ex.Message);
                    await _store.Writer.Enqueue(chatId, (connection, transaction) =>
                        DeleteLegacyMessages(connection, transaction, chatId)).ConfigureAwait(false);
                    state = ChatImportState.Unreadable;
                }
            }

            int? stored = null;
            await _store.Writer.Enqueue(chatId, (connection, transaction) =>
            {
                using var command = ChatStore.CreateCommand(connection, transaction, """
                    UPDATE chats SET
                        import_state = $state,
                        message_count = (SELECT COUNT(*) FROM messages WHERE chat_key = chats.chat_key),
                        next_seq = MAX(IFNULL((SELECT MAX(seq) + 1 FROM messages WHERE chat_key = chats.chat_key), 1), $first),
                        content_version = (SELECT IFNULL(MAX(content_version), 0) + 1 FROM chats)
                    WHERE id = $id
                    RETURNING message_count;
                    """);
                command.Parameters.AddWithValue("$state", (int)state);
                command.Parameters.AddWithValue("$first", state == ChatImportState.Unreadable ? UnreadableAppendBase : 1L);
                command.Parameters.AddWithValue("$id", chatId.ToByteArray());
                // No row: the chat was deleted meanwhile.
                stored = command.ExecuteScalar() is { } count and not DBNull ? Convert.ToInt32(count) : null;
            }).ConfigureAwait(false);

            // Counters first: once the chat is no longer pending, writers rely on them.
            if (stored is { } messageCount)
                _store.NoteImported(chatId, messageCount, state);
            _pending.TryRemove(chatId, out _);
            if (stored is { } importedCount)
                ChatImported?.Invoke(chatId, importedCount);
        }
        finally
        {
            _imports.TryRemove(chatId, out _);
        }
    }

    /// <summary>
    /// Streams the file into the chat's transcript, a chunk per write. Returns how many messages were
    /// stored and, when the file is damaged part-way, the error that ended the read: the messages
    /// before it are stored, as each one is only yielded once it was read whole.
    /// </summary>
    private async Task<(int Count, Exception? Damage)> ImportFileAsync(Guid chatId, string file)
    {
        await using var stream = new FileStream(
            file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var seq = 0L;
        var chunk = new List<EncodedMessage>(ChunkSize);
        Exception? damage = null;
        try
        {
            await foreach (var message in JsonSerializer.DeserializeAsyncEnumerable(
                               stream, AppDataJsonContext.Default.ChatMessage, _shutdown.Token).ConfigureAwait(false))
            {
                if (message is null)
                    continue;
                chunk.Add(EncodedMessage.From(message, ++seq));
                if (chunk.Count >= ChunkSize)
                {
                    await WriteChunkAsync(chatId, chunk).ConfigureAwait(false);
                    chunk = new List<EncodedMessage>(ChunkSize);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            damage = ex;
        }

        if (chunk.Count > 0)
            await WriteChunkAsync(chatId, chunk).ConfigureAwait(false);
        return ((int)seq, damage);
    }

    private Task WriteChunkAsync(Guid chatId, List<EncodedMessage> chunk)
        => _store.Writer.Enqueue(chatId, (connection, transaction) =>
        {
            var chatKey = FindChatKey(connection, transaction, chatId)
                ?? throw new InvalidOperationException($"Chat {chatId} has no row to import into.");
            ChatStore.UpsertMessages(connection, transaction, chatKey, chunk);
        });

    // The legacy transcript's positions; messages added while the file could not be read stay.
    private static void DeleteLegacyMessages(SqliteConnection connection, SqliteTransaction transaction, Guid chatId)
    {
        using var command = ChatStore.CreateCommand(connection, transaction,
            "DELETE FROM messages WHERE chat_key = (SELECT chat_key FROM chats WHERE id = $id) AND seq < $base;");
        command.Parameters.AddWithValue("$id", chatId.ToByteArray());
        command.Parameters.AddWithValue("$base", UnreadableAppendBase);
        command.ExecuteNonQuery();
    }

    private static long? FindChatKey(SqliteConnection connection, SqliteTransaction transaction, Guid chatId)
    {
        using var command = ChatStore.CreateCommand(connection, transaction, "SELECT chat_key FROM chats WHERE id = $id;");
        command.Parameters.AddWithValue("$id", chatId.ToByteArray());
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    internal string? LocateLegacyFile(Guid chatId)
    {
        foreach (var directory in new[] { _chatsDirectory, _backupDirectory })
        {
            var path = Path.Combine(directory, $"{chatId}.json");
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    /// <summary>
    /// Renames the legacy folder once every chat is imported. Its files — including ones no chat
    /// ever referenced — stay in the backup folder; a chat's file is deleted with the chat.
    /// </summary>
    internal void RetireLegacyFolder()
    {
        if (!_pending.IsEmpty || !Directory.Exists(_chatsDirectory))
            return;

        try
        {
            if (!Directory.Exists(_backupDirectory))
            {
                Directory.Move(_chatsDirectory, _backupDirectory);
                return;
            }

            foreach (var file in Directory.EnumerateFiles(_chatsDirectory))
            {
                var target = Path.Combine(_backupDirectory, Path.GetFileName(file));
                if (File.Exists(target))
                    target = Path.Combine(_backupDirectory, $"{Path.GetFileNameWithoutExtension(file)}.{DateTime.UtcNow:yyyyMMddHHmmss}{Path.GetExtension(file)}");
                File.Move(file, target);
            }

            if (!Directory.EnumerateFileSystemEntries(_chatsDirectory).Any())
                Directory.Delete(_chatsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("[ChatStore] Could not retire the legacy chats folder yet: {0}", ex.Message);
        }
    }

    public void Dispose()
    {
        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }
        _shutdown.Dispose();
    }
}
