using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services.Storage;

namespace Lumi.Services;

/// <summary>
/// What a chat holds in memory of its stored transcript. <see cref="Chat.Messages"/> is the chat's
/// live tail: a contiguous run of its newest messages, starting at an exchange (a user message), that
/// may still change. Everything before <see cref="TailStartSeq"/> stays in the database and is read
/// on demand. Older messages a surface brought back for display are attached here so their edits
/// (an answered form, a receipt's status) are saved with the chat.
/// </summary>
internal sealed class ChatTranscriptState
{
    public SemaphoreSlim LoadLock { get; } = new(1, 1);

    /// <summary>The tail was read from storage (or the chat is new), so the tail is complete.</summary>
    public bool IsLoaded { get; set; }

    /// <summary>Positions for appended messages are known even though the tail was never read.</summary>
    public bool HasAppendBase { get; set; }

    public bool IsDeleted { get; set; }
    public long TailStartSeq { get; set; } = 1;

    /// <summary>Stored messages before <see cref="TailStartSeq"/>: the tail's first absolute index.</summary>
    public int HistoryCount { get; set; }

    public long NextSeq { get; set; } = 1;

    /// <summary>User and answered assistant messages before <see cref="TailStartSeq"/>.</summary>
    public HistoryConversationCounts HistoryCounts { get; set; }

    public HashSet<long> PersistedTailSeqs { get; set; } = [];
    public Dictionary<long, ChatMessage> AttachedHistory { get; } = [];
}

/// <summary>A chat's tail position: messages before <see cref="TailStartSeq"/> are only in storage.</summary>
internal readonly record struct ChatTranscriptWindow(bool IsLoaded, long TailStartSeq, int HistoryCount)
{
    public bool HasHistory => HistoryCount > 0;
}

public partial class DataStore
{
    private static readonly string DatabaseFile = Path.Combine(AppDir, "lumi.db");
    private static readonly string LegacyIndexBackupFile = Path.Combine(AppDir, "data.pre-sqlite.json");
    private static readonly object LegacyIndexSync = new();
    private static readonly TimeSpan LegacyImportStartDelay = TimeSpan.FromSeconds(4);

    /// <summary>The chat database of the profile in use (tests open their own reader on it).</summary>
    internal static string ChatDatabasePath => DatabaseFile;

    private readonly ChatStore? _chatStore;
    private readonly LegacyChatImporter? _legacyImporter;
    private readonly object _transcriptStatesSync = new();
    private readonly Dictionary<Guid, ChatTranscriptState> _transcriptStates = [];

    /// <summary>The chat database, or null for a store that only lives in memory.</summary>
    internal ChatStore? ChatStore => _chatStore;

    /// <summary>
    /// True when part of the chat's legacy transcript could not be read (a damaged or unreadable
    /// file, see <see cref="ChatImportState"/>): its stored messages are not the whole conversation
    /// its Copilot session holds, so they cannot be matched to the session's turns by position.
    /// </summary>
    internal bool IsMissingLegacyMessages(Chat chat)
        => _chatStore?.GetImportState(chat.Id) is ChatImportState.Unreadable or ChatImportState.Failed;

    /// <summary>True when transcripts are kept in the chat database (and can be released from memory).</summary>
    internal bool PersistsTranscripts => _chatStore is not null;

    /// <summary>Chats still waiting to be moved from the legacy JSON layout.</summary>
    internal int PendingLegacyImports => _legacyImporter?.PendingCount ?? 0;

    /// <summary>
    /// Completes with the background migration's duration once every legacy chat was imported, or
    /// null when there was nothing to import (diagnostics and harnesses).
    /// </summary>
    internal Task<TimeSpan>? LegacyImportCompletion
        => _legacyImporter is { HasPending: true } importer ? importer.SweepCompleted : null;

    /// <summary>
    /// A store over in-memory index data whose transcripts live in <paramref name="chatStore"/>, with
    /// the <paramref name="legacyImporter"/> moving its legacy chats, if any. Chats already in the
    /// database are added to <paramref name="data"/>. Used by tests and harnesses.
    /// </summary>
    internal DataStore(AppData data, ChatStore chatStore, string? skillsDirectoryOverride = null, LegacyChatImporter? legacyImporter = null)
        : this(data, skillsDirectoryOverride)
    {
        _chatStore = chatStore;
        _legacyImporter = legacyImporter;
        _chatStore.ChatContentCommitted += OnChatContentCommitted;
        var known = _data.Chats.Select(static chat => chat.Id).ToHashSet();
        foreach (var stored in chatStore.LoadChats())
        {
            if (known.Add(stored.Chat.Id))
                _data.Chats.Add(stored.Chat);
        }
    }

    // ── Startup ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the chat database, imports chats the index still lists from the legacy JSON layout, and
    /// replaces <see cref="AppData.Chats"/> with the stored chats. Returns the importer that moves the
    /// legacy transcripts in the background.
    /// </summary>
    internal static (ChatStore Store, List<Chat> Chats, LegacyChatImporter Importer) OpenChatStorage(
        string databaseFile,
        IReadOnlyList<Chat> legacyIndexChats,
        string legacyChatsDirectory,
        Action? backupLegacyIndex)
    {
        var store = new ChatStore(databaseFile);
        var stored = store.LoadChats();
        var storedIds = stored.Select(static chat => chat.Chat.Id).ToHashSet();
        var newLegacyChats = legacyIndexChats.Where(chat => !storedIds.Contains(chat.Id)).ToList();
        if (newLegacyChats.Count > 0)
        {
            backupLegacyIndex?.Invoke();
            var backupDirectory = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(legacyChatsDirectory))!,
                LegacyChatImporter.BackupFolderName);
            var rows = new List<(Guid, long, string, int)>(newLegacyChats.Count);
            foreach (var chat in newLegacyChats)
            {
                // Legacy startup dropped index entries without a usable messages file; keep that rule.
                if (!HasUsableLegacyFile(legacyChatsDirectory, chat.Id) && !HasUsableLegacyFile(backupDirectory, chat.Id))
                    continue;
                rows.Add((chat.Id, chat.UpdatedAt.ToUnixTimeMilliseconds(), ChatStorageJson.SerializeChat(chat), chat.MessageCount));
            }

            store.InsertPendingChatsAsync(rows).GetAwaiter().GetResult();
            // Everyday commits are not synced one by one; these rows must reach the disk before the
            // index that listed the chats is written without them.
            store.Checkpoint();
            stored = store.LoadChats();
        }

        // A legacy file that could not be read is read again at every start until it can be.
        var pending = stored
            .Where(static chat => chat.ImportState is ChatImportState.Pending or ChatImportState.Unreadable)
            .Select(static chat => chat.Chat.Id)
            .ToList();
        var importer = new LegacyChatImporter(store, legacyChatsDirectory, pending);
        return (store, stored.Select(static chat => chat.Chat).ToList(), importer);
    }

    private static bool HasUsableLegacyFile(string directory, Guid chatId)
    {
        try
        {
            var file = new FileInfo(Path.Combine(directory, $"{chatId}.json"));
            // An empty JSON array "[]" is at most 4 bytes (possible BOM + brackets).
            return file.Exists && file.Length > 4;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static void BackupLegacyIndexOnce()
    {
        try
        {
            // Copied through a synced temporary file: the index is rewritten without its chats next.
            if (!File.Exists(LegacyIndexBackupFile) && File.Exists(DataFile))
                JsonFilePersistence.CopyFileAtomically(DataFile, LegacyIndexBackupFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("[ChatStore] Could not back up the legacy index: {0}", ex.Message);
        }
    }

    private void StartLegacyImport()
    {
        if (_legacyImporter is null)
            return;

        _legacyImporter.ChatImported += (chatId, count) =>
        {
            var chat = FindChatSnapshot(chatId);
            if (chat is not null && !GetTranscriptState(chatId).IsLoaded)
                chat.MessageCount = count;
        };
        if (!_legacyImporter.HasPending)
        {
            _legacyImporter.RetireLegacyFolder();
            return;
        }

        var order = SnapshotChatsByRecency().Select(static chat => chat.Id).ToList();
        _legacyImporter.StartBackgroundImport(order, LegacyImportStartDelay);
    }

    /// <summary>
    /// Removes chats with no stored messages. A chat's row is written before its first message, so a
    /// chat the app closed on before anything was said would otherwise linger as an empty entry.
    /// </summary>
    private void CleanOrphanedChats()
    {
        if (_chatStore is null)
            return;

        var removed = _data.Chats.RemoveAll(chat =>
        {
            // A legacy file that could not be read keeps its chat (opening empty), exactly as before.
            if (_chatStore.GetStoredMessageCount(chat.Id) > 0
                || _chatStore.GetImportState(chat.Id) != ChatImportState.Imported)
            {
                return false;
            }

            ObserveFailure(_chatStore.DeleteChatAsync(chat.Id), "Removing an empty chat");
            return true;
        });

        if (removed > 0)
            Trace.TraceInformation("[ChatStore] Removed {0} chats without messages.", removed);
    }

    // For writes nothing waits on: a failure is logged instead of going unobserved.
    private static void ObserveFailure(Task task, string what)
        => task.ContinueWith(
            static (failed, state) => Trace.TraceWarning("[ChatStore] {0} failed: {1}", state, failed.Exception?.GetBaseException().Message),
            what,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Updates the rows of chats whose metadata changed (a chat gets its row with its first
    /// transcript write) and removes deleted chats.
    /// </summary>
    private async Task PersistChatRowsAsync(IEnumerable<Guid> dirtyChatIds, IEnumerable<Guid> deletedChatIds)
    {
        var store = _chatStore!;
        var dirty = dirtyChatIds.ToHashSet();
        if (dirty.Count > 0)
        {
            var rows = new List<(Guid, long, string)>(dirty.Count);
            foreach (var chat in SnapshotChatsByRecency())
            {
                if (dirty.Contains(chat.Id))
                    rows.Add((chat.Id, chat.UpdatedAt.ToUnixTimeMilliseconds(), ChatStorageJson.SerializeChat(chat)));
            }

            await store.UpdateChatMetadataAsync(rows).ConfigureAwait(false);
        }

        // Deleted again here in case deleting them when they were removed failed.
        foreach (var chatId in deletedChatIds)
            await DeleteChatFileAsync(chatId).ConfigureAwait(false);
    }

    private void OnChatContentCommitted(Guid chatId) => ChatContentChanged?.Invoke(chatId);

    // ── Transcript state ────────────────────────────────────────────────────

    internal ChatTranscriptState GetTranscriptState(Guid chatId)
    {
        lock (_transcriptStatesSync)
        {
            if (!_transcriptStates.TryGetValue(chatId, out var state))
            {
                state = new ChatTranscriptState();
                // A store without a database holds whole transcripts in memory; so does a chat with
                // nothing stored yet (a new chat): its tail is all of it.
                if (_chatStore is null
                    || _chatStore.GetStoredMessageCount(chatId) == 0 && _legacyImporter?.IsPending(chatId) != true)
                {
                    state.IsLoaded = true;
                    state.HasAppendBase = true;
                    state.NextSeq = _chatStore?.GetNextSeq(chatId) ?? 1;
                }

                _transcriptStates[chatId] = state;
            }

            return state;
        }
    }

    /// <summary>Where a chat's live tail starts and how much stored history comes before it.</summary>
    internal ChatTranscriptWindow GetTranscriptWindow(Chat chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (_chatStore is null)
            return new ChatTranscriptWindow(true, 1, 0);

        var state = GetTranscriptState(chat.Id);
        lock (state)
            return new ChatTranscriptWindow(state.IsLoaded, state.TailStartSeq, state.IsLoaded ? state.HistoryCount : 0);
    }

    /// <summary>User messages in the whole chat: its stored history and its live tail.</summary>
    internal int CountUserMessages(Chat chat, bool withText = false)
        => withText
            ? HistoryCountsOf(chat).UserMessagesWithText
              + chat.Messages.Count(static message => message.Role == "user" && !string.IsNullOrWhiteSpace(message.Content))
            : HistoryCountsOf(chat).UserMessages + chat.Messages.Count(static message => message.Role == "user");

    /// <summary>Assistant messages with text in the whole chat: its stored history and its live tail.</summary>
    internal int CountAnsweredAssistantMessages(Chat chat)
        => HistoryCountsOf(chat).AnsweredAssistantMessages
           + chat.Messages.Count(static message => message.Role == "assistant" && !string.IsNullOrWhiteSpace(message.Content));

    private HistoryConversationCounts HistoryCountsOf(Chat chat)
    {
        if (_chatStore is null)
            return default;
        var state = GetTranscriptState(chat.Id);
        lock (state)
            return state.IsLoaded ? state.HistoryCounts : default;
    }

    /// <summary>
    /// The conversation (user, assistant, system and error messages) stored before the chat's live
    /// tail, oldest first: what a replacement session must be told when the old one is gone.
    /// </summary>
    internal async Task<List<ChatMessage>> ReadHistoryConversationAsync(Chat chat, CancellationToken cancellationToken = default)
    {
        var window = GetTranscriptWindow(chat);
        if (_chatStore is null || !window.HasHistory)
            return [];
        var messages = await _chatStore.ReadConversationAndKindsAsync(chat.Id, [], window.TailStartSeq)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return MapAttached(chat, messages);
    }

    /// <summary>The last stored message before <paramref name="beforeSeq"/>, or null.</summary>
    internal async Task<ChatMessage?> ReadLastStoredMessageBeforeAsync(Chat chat, long beforeSeq)
    {
        if (_chatStore is null)
            return null;
        var messages = await _chatStore.ReadRecentAsync(chat.Id, 1, conversationOnly: false, beforeSeq).ConfigureAwait(false);
        return messages.Count == 0 ? null : MapAttached(chat, messages)[0];
    }

    /// <summary>
    /// The chat's messages that are in memory, oldest first: older ones shown from storage (or edited
    /// there) and then the live tail.
    /// </summary>
    internal List<ChatMessage> GetLoadedMessages(Chat chat)
    {
        if (_chatStore is null)
            return chat.Messages.ToList();

        var state = GetTranscriptState(chat.Id);
        List<ChatMessage> messages;
        lock (state)
            messages = state.AttachedHistory.Values.OrderBy(static message => message.StorageSeq).ToList();
        messages.AddRange(chat.Messages);
        return messages;
    }

    /// <summary>
    /// The chat's messages holding an app a tool opened (<see cref="ChatMessage.EmbeddedApp"/>), oldest
    /// first. All of them are in memory while the chat is loaded: older ones are read with its tail and
    /// stay attached, so the app list, canvases and the model's app context find them, and changes to
    /// them are saved with the chat.
    /// </summary>
    internal List<ChatMessage> GetAppMessages(Chat chat)
        => GetLoadedMessages(chat).Where(static message => message.EmbeddedApp is not null).ToList();

    /// <summary>
    /// Every message of the chat that can hold widget states the reader edited, oldest first: the
    /// stored answers carrying them (through the <see cref="ChatMessageKinds.Interaction"/> facet, or
    /// the skill facet an answer with skills has — the user messages that share it are not read),
    /// older messages in memory — whose edits may not be stored yet — and the live tail.
    /// </summary>
    internal async Task<List<ChatMessage>> ReadInteractionMessagesAsync(Chat chat, CancellationToken cancellationToken = default)
    {
        var window = GetTranscriptWindow(chat);
        if (_chatStore is null || !window.HasHistory)
            return GetLoadedMessages(chat);

        var stored = await _chatStore.ReadByKindAsync(chat.Id, [ChatMessageKinds.Interaction, ChatMessageKinds.Skill], window.TailStartSeq, role: "assistant")
            .WaitAsync(cancellationToken).ConfigureAwait(true);
        var history = new SortedDictionary<long, ChatMessage>();
        foreach (var message in stored)
        {
            if (message.InteractionStates is { Count: > 0 })
                history[message.StorageSeq] = message;
        }
        var state = GetTranscriptState(chat.Id);
        lock (state)
        {
            foreach (var (seq, message) in state.AttachedHistory)
            {
                if (seq < state.TailStartSeq)
                    history[seq] = message;
            }
        }

        var messages = history.Values.ToList();
        messages.AddRange(chat.Messages);
        return messages;
    }

    /// <summary>Total messages in the chat: stored history plus the live tail.</summary>
    internal int GetTotalMessageCount(Chat chat)
    {
        if (_chatStore is null)
            return chat.Messages.Count;

        var state = GetTranscriptState(chat.Id);
        lock (state)
        {
            return state.IsLoaded || state.HasAppendBase
                ? state.HistoryCount + chat.Messages.Count
                : Math.Max(chat.MessageCount, chat.Messages.Count);
        }
    }

    // ── Loading ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a chat's live tail into <see cref="Chat.Messages"/> if it is not in memory yet: its newest
    /// exchange, with any sends held by Pause after it (see <see cref="ChatStore.ReadTailAsync"/>).
    /// Older history stays in the database. A store without a database keeps whole transcripts in
    /// memory already.
    /// </summary>
    public async Task LoadChatMessagesAsync(Chat chat, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (_chatStore is null)
            return;

        var state = GetTranscriptState(chat.Id);
        if (state.IsLoaded)
            return;

        await state.LoadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state.IsLoaded || state.IsDeleted)
                return;

            var tail = await _chatStore.ReadTailAsync(chat.Id).WaitAsync(cancellationToken).ConfigureAwait(false);
            // Apps a tool opened stay in memory with the tail, older ones included (see GetAppMessages).
            var apps = tail.HistoryCount > 0
                ? await _chatStore.ReadByKindAsync(chat.Id, [ChatMessageKinds.App], tail.TailStartSeq).WaitAsync(cancellationToken).ConfigureAwait(false)
                : [];
            lock (state)
            {
                ApplyLoadedTail(chat, state, tail);
                foreach (var app in apps)
                    state.AttachedHistory.TryAdd(app.StorageSeq, app);
            }
        }
        finally
        {
            state.LoadLock.Release();
        }
    }

    private static void ApplyLoadedTail(Chat chat, ChatTranscriptState state, ChatTailRead tail)
    {
        // Messages appended while the tail was not loaded (a job's prompt, a phone message) are newer
        // than anything stored; saved ones come back from storage, so keep the live objects for them.
        var live = chat.Messages.ToList();
        var liveBySeq = new Dictionary<long, ChatMessage>();
        foreach (var message in live)
        {
            if (message.StorageSeq > 0)
                liveBySeq.TryAdd(message.StorageSeq, message);
        }

        var merged = new List<ChatMessage>(tail.Messages.Count + live.Count);
        var used = new HashSet<ChatMessage>(ReferenceEqualityComparer.Instance);
        foreach (var loaded in tail.Messages)
        {
            var message = liveBySeq.TryGetValue(loaded.StorageSeq, out var existing) ? existing : loaded;
            used.Add(message);
            merged.Add(message);
        }

        foreach (var message in live)
        {
            if (!used.Contains(message) && (message.StorageSeq <= 0 || message.StorageSeq >= tail.TailStartSeq))
                merged.Add(message);
        }

        chat.Messages.Clear();
        chat.Messages.AddRange(merged);
        state.TailStartSeq = tail.TailStartSeq;
        state.HistoryCount = tail.HistoryCount;
        state.HistoryCounts = tail.HistoryCounts;
        var maxSeq = merged.Count == 0 ? 0 : merged.Max(static message => message.StorageSeq);
        state.NextSeq = Math.Max(Math.Max(tail.NextSeq, state.NextSeq), maxSeq + 1);
        state.PersistedTailSeqs = tail.Messages.Select(static message => message.StorageSeq).ToHashSet();
        state.IsLoaded = true;
        state.HasAppendBase = true;
        chat.MessageCount = state.HistoryCount + chat.Messages.Count;
    }

    /// <summary>
    /// Releases a chat's tail from memory once everything in it is stored. Returns false (and keeps
    /// it) when anything is still unsaved. The next load reads it back from the database.
    /// </summary>
    internal bool TryReleaseChatMessages(Chat chat)
    {
        if (_chatStore is null)
            return false;

        var state = GetTranscriptState(chat.Id);
        lock (state)
        {
            if (!state.IsLoaded && chat.Messages.Count == 0)
                return true;
            if (chat.Messages.Any(message => message.StorageSeq <= 0 || message.HasUnpersistedChanges
                                             || !state.PersistedTailSeqs.Contains(message.StorageSeq))
                || state.AttachedHistory.Values.Any(static message => message.HasUnpersistedChanges))
            {
                return false;
            }

            chat.MessageCount = state.HistoryCount + chat.Messages.Count;
            chat.Messages.Clear();
            chat.Messages.TrimExcess();
            state.IsLoaded = false;
            state.HasAppendBase = false;
            state.PersistedTailSeqs = [];
            state.AttachedHistory.Clear();
            return true;
        }
    }

    /// <summary>
    /// Moves the first <paramref name="count"/> messages of a chat's live tail into its stored
    /// history, so the tail is what reopening the chat would read. They stay attached, as messages a
    /// surface shows, until the surface lets them go. Changes nothing and returns false unless the
    /// tail is loaded and every message moved is stored as it is.
    /// </summary>
    internal bool TryRollTail(Chat chat, int count)
    {
        if (_chatStore is null || count <= 0 || count > chat.Messages.Count)
            return false;

        var state = GetTranscriptState(chat.Id);
        lock (state)
        {
            if (!state.IsLoaded || state.IsDeleted)
                return false;
            for (var i = 0; i < count; i++)
            {
                var message = chat.Messages[i];
                if (message.StorageSeq <= 0 || message.HasUnpersistedChanges || !state.PersistedTailSeqs.Contains(message.StorageSeq))
                    return false;
            }

            var tailStart = count < chat.Messages.Count && chat.Messages[count].StorageSeq > 0
                ? chat.Messages[count].StorageSeq
                : state.NextSeq;
            if (chat.Messages[count - 1].StorageSeq >= tailStart)
                return false;

            // Counted as the outline index counts them when the chat is read again.
            var (users, answered, usersWithText) = (0, 0, 0);
            for (var i = 0; i < count; i++)
            {
                var message = chat.Messages[i];
                state.PersistedTailSeqs.Remove(message.StorageSeq);
                state.AttachedHistory[message.StorageSeq] = message;
                if (message.Role == "user")
                {
                    users++;
                    usersWithText += message.Content.Length > 0 ? 1 : 0;
                }
                else if (message.Role == "assistant" && message.Content.Length > 0)
                {
                    answered++;
                }
            }

            var counts = state.HistoryCounts;
            state.HistoryCounts = new HistoryConversationCounts(
                counts.UserMessages + users, counts.AnsweredAssistantMessages + answered, counts.UserMessagesWithText + usersWithText);
            state.HistoryCount += count;
            state.TailStartSeq = tailStart;
            chat.Messages.RemoveRange(0, count);
            return true;
        }
    }

    // ── Saving ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Saves what changed in a chat: its row, new and edited messages of its tail (and of older
    /// messages shown again), and the removal of messages taken out of the tail. Only changed
    /// messages are serialized, on the caller's thread, so saving a long chat costs what changed.
    /// </summary>
    public async Task SaveChatAsync(Chat chat, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (_chatStore is null)
            return;

        // Messages added to a chat whose legacy transcript is still being moved follow that
        // transcript: it is imported first (resuming on the caller's thread, which owns the chat).
        if (_legacyImporter?.IsPending(chat.Id) == true)
            await _legacyImporter.EnsureImportedAsync(chat.Id).WaitAsync(cancellationToken);

        var write = PrepareChatWrite(chat);
        if (write is null)
            return;

        try
        {
            await _chatStore.WriteAsync(write.Batch).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            write.Revert();
            throw;
        }
    }

    private sealed class PreparedChatWrite(ChatWriteBatch batch, ChatTranscriptState state, List<ChatMessage> written)
    {
        public ChatWriteBatch Batch { get; } = batch;

        // A failed write is retried by the next save: its messages count as unsaved again and its
        // removals are remembered.
        public void Revert()
        {
            lock (state)
            {
                foreach (var message in written)
                    message.PersistedRevision = -1;
                foreach (var seq in Batch.DeletedSeqs)
                    state.PersistedTailSeqs.Add(seq);
            }
        }
    }

    private PreparedChatWrite? PrepareChatWrite(Chat chat)
    {
        var store = _chatStore!;
        var state = GetTranscriptState(chat.Id);
        lock (state)
        {
            if (state.IsDeleted)
                return null;

            if (!state.IsLoaded && !state.HasAppendBase)
            {
                // Appending to a chat whose tail was never read: new messages follow its stored history.
                var next = store.GetNextSeq(chat.Id);
                state.TailStartSeq = next;
                state.NextSeq = Math.Max(state.NextSeq, next);
                state.HistoryCount = store.GetStoredMessageCount(chat.Id);
                state.HasAppendBase = true;
            }

            var messages = chat.Messages.ToArray();
            var written = new List<ChatMessage>();
            var upserts = new List<EncodedMessage>();
            var seen = new HashSet<ChatMessage>(ReferenceEqualityComparer.Instance);
            var currentSeqs = new HashSet<long>();
            var lastSeq = state.TailStartSeq - 1;
            foreach (var message in messages)
            {
                if (!seen.Add(message))
                    continue;

                var seq = message.StorageSeq;
                if (seq <= lastSeq || seq < state.TailStartSeq || currentSeqs.Contains(seq))
                {
                    // New, moved, or out of order: give it the next position after everything stored.
                    seq = state.NextSeq++;
                    message.StorageSeq = seq;
                    message.PersistedRevision = -1;
                }

                if (message.HasUnpersistedChanges)
                {
                    upserts.Add(EncodedMessage.From(message, seq));
                    message.PersistedRevision = message.Revision;
                    written.Add(message);
                }

                currentSeqs.Add(seq);
                lastSeq = seq;
            }

            foreach (var (seq, message) in state.AttachedHistory)
            {
                if (seq >= state.TailStartSeq || !message.HasUnpersistedChanges)
                    continue;
                upserts.Add(EncodedMessage.From(message, seq));
                message.PersistedRevision = message.Revision;
                written.Add(message);
            }

            var batch = new ChatWriteBatch
            {
                ChatId = chat.Id,
                MetadataJson = ChatStorageJson.SerializeChat(chat),
                UpdatedAtMs = chat.UpdatedAt.ToUnixTimeMilliseconds(),
                NextSeq = state.NextSeq,
            };
            batch.Upserts.AddRange(upserts);
            foreach (var seq in state.PersistedTailSeqs)
            {
                if (!currentSeqs.Contains(seq))
                    batch.DeletedSeqs.Add(seq);
            }

            state.PersistedTailSeqs = currentSeqs;
            chat.MessageCount = state.HistoryCount + currentSeqs.Count;
            if (ChatPreviewHelper.FromMessages(messages) is { } preview)
                chat.Preview = preview;
            return new PreparedChatWrite(batch, state, written);
        }
    }

    /// <summary>
    /// Removes <paramref name="message"/> and everything after it from a chat whose message lies in
    /// its stored history (before the tail). The tail empties; the chat continues after the cut.
    /// </summary>
    internal async Task TruncateChatHistoryAsync(Chat chat, ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(message);
        if (_chatStore is null || message.StorageSeq <= 0)
            throw new InvalidOperationException("Only a stored message can start a history truncation.");

        // The cut takes effect at once and its write is queued before anything yields, so a message
        // added from now on comes after it instead of being cut with the rest.
        var cut = message.StorageSeq;
        Action restore;
        Task written;
        var state = GetTranscriptState(chat.Id);
        lock (state)
        {
            restore = CaptureTail(chat, state);
            chat.Messages.Clear();
            foreach (var seq in state.AttachedHistory.Keys.Where(seq => seq >= cut).ToList())
                state.AttachedHistory.Remove(seq);
            state.PersistedTailSeqs = [];
            state.TailStartSeq = state.NextSeq;
            state.IsLoaded = true;
            state.HasAppendBase = true;
            try
            {
                written = _chatStore.WriteAsync(new ChatWriteBatch
                {
                    ChatId = chat.Id,
                    MetadataJson = ChatStorageJson.SerializeChat(chat),
                    UpdatedAtMs = chat.UpdatedAt.ToUnixTimeMilliseconds(),
                    NextSeq = state.NextSeq,
                    DeleteFromSeq = cut,
                });
            }
            catch
            {
                restore();
                throw;
            }
        }

        try
        {
            await written.ConfigureAwait(true);
        }
        catch
        {
            // Nothing was cut: the chat keeps the tail and history it had, and what was added since.
            lock (state)
            {
                var added = chat.Messages.ToList();
                restore();
                chat.Messages.AddRange(added);
            }

            throw;
        }

        // Everything stored before the cut is the chat's history now.
        var historyCount = await _chatStore.CountAsync(chat.Id, beforeSeq: cut).ConfigureAwait(true);
        var historyCounts = await _chatStore.CountConversationAsync(chat.Id, cut).ConfigureAwait(true);
        lock (state)
        {
            state.HistoryCount = historyCount;
            state.HistoryCounts = historyCounts;
            chat.MessageCount = historyCount + chat.Messages.Count;
        }
    }

    // Restores a chat's tail and its window over storage to what they are now.
    private static Action CaptureTail(Chat chat, ChatTranscriptState state)
    {
        var messages = chat.Messages.ToList();
        var attached = state.AttachedHistory.ToList();
        var (persisted, historyCount, historyCounts, tailStart, isLoaded, hasAppendBase, messageCount) = (
            state.PersistedTailSeqs, state.HistoryCount, state.HistoryCounts, state.TailStartSeq,
            state.IsLoaded, state.HasAppendBase, chat.MessageCount);
        return () =>
        {
            chat.Messages.Clear();
            chat.Messages.AddRange(messages);
            state.AttachedHistory.Clear();
            foreach (var (seq, message) in attached)
                state.AttachedHistory[seq] = message;
            state.PersistedTailSeqs = persisted;
            state.HistoryCount = historyCount;
            state.HistoryCounts = historyCounts;
            state.TailStartSeq = tailStart;
            state.IsLoaded = isLoaded;
            state.HasAppendBase = hasAppendBase;
            chat.MessageCount = messageCount;
        };
    }

    // ── Older messages brought back for display ─────────────────────────────

    /// <summary>
    /// Registers older (stored-only) messages a surface is showing, so the same objects are returned
    /// to later reads and their edits are saved with the chat.
    /// </summary>
    internal void AttachHistoryMessages(Chat chat, IEnumerable<ChatMessage> messages)
    {
        if (_chatStore is null)
            return;

        var state = GetTranscriptState(chat.Id);
        lock (state)
        {
            foreach (var message in messages)
            {
                // The first instance stays the one whose edits are saved; lookups map to it.
                if (message.StorageSeq > 0 && message.StorageSeq < state.TailStartSeq)
                    state.AttachedHistory.TryAdd(message.StorageSeq, message);
            }
        }
    }

    /// <summary>
    /// Stops tracking shown older messages. One that still holds an edit storage has not received
    /// stays attached — the chat keeps the edit, and a later read of its exchange returns it — and is
    /// saved now when chats are saved automatically; a later detach releases it once saved. An app's
    /// message stays attached while the chat is loaded (see <see cref="GetAppMessages"/>).
    /// </summary>
    internal void DetachHistoryMessages(Chat chat, IEnumerable<ChatMessage> messages)
    {
        if (_chatStore is null)
            return;

        var state = GetTranscriptState(chat.Id);
        var hasUnsavedEdits = false;
        lock (state)
        {
            foreach (var message in messages)
            {
                if (!state.AttachedHistory.TryGetValue(message.StorageSeq, out var attached) || !ReferenceEquals(attached, message)
                    || message.EmbeddedApp is not null)
                    continue;
                if (message.HasUnpersistedChanges)
                    hasUnsavedEdits = true;
                else
                    state.AttachedHistory.Remove(message.StorageSeq);
            }
        }

        if (hasUnsavedEdits && _data.Settings.AutoSaveChats)
            ObserveFailure(SaveChatAsync(chat), $"Saving edits of chat {chat.Id}");
    }

    private List<ChatMessage> MapAttached(Chat chat, List<ChatMessage> stored)
    {
        var state = GetTranscriptState(chat.Id);
        lock (state)
        {
            if (state.AttachedHistory.Count == 0)
                return stored;
            for (var i = 0; i < stored.Count; i++)
            {
                if (state.AttachedHistory.TryGetValue(stored[i].StorageSeq, out var attached))
                    stored[i] = attached;
            }
        }

        return stored;
    }

    // ── Reading history ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns a chat's whole transcript: stored history plus the live tail. Reads the database;
    /// prefer the ranged reads below when only part of the history is needed.
    /// </summary>
    public async Task<IReadOnlyList<ChatMessage>> ReadChatMessagesAsync(
        Chat chat,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        var (window, tail) = SnapshotTail(chat);
        if (_chatStore is null)
            return tail;

        var history = await _chatStore.ReadAllAsync(chat.Id, window.IsLoaded ? window.TailStartSeq : long.MaxValue)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!window.IsLoaded)
            return MergeUnloadedTail(history, tail);
        MapAttached(chat, history);
        history.AddRange(tail);
        return history;
    }

    /// <summary>Reads only the stored history of a chat by id, without touching in-memory state.</summary>
    internal async Task<IReadOnlyList<ChatMessage>> ReadPersistedChatMessagesAsync(
        Guid chatId,
        CancellationToken cancellationToken)
    {
        if (_chatStore is null)
            return [];

        return await _chatStore.ReadAllAsync(chatId).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The newest <paramref name="count"/> messages of a chat (optionally only the conversation:
    /// user, assistant, system and error messages), oldest first.
    /// </summary>
    internal async Task<IReadOnlyList<ChatMessage>> ReadRecentChatMessagesAsync(
        Chat chat,
        int count,
        bool conversationOnly = false,
        CancellationToken cancellationToken = default)
    {
        var (window, tail) = SnapshotTail(chat);
        var fromTail = tail
            .Where(message => !conversationOnly || IsConversationRole(message.Role))
            .TakeLast(count)
            .ToList();
        if (_chatStore is null || fromTail.Count >= count)
            return fromTail;

        if (!window.IsLoaded)
        {
            var stored = await _chatStore.ReadRecentAsync(chat.Id, count, conversationOnly)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return MergeUnloadedTail(stored, tail)
                .Where(message => !conversationOnly || IsConversationRole(message.Role))
                .TakeLast(count)
                .ToList();
        }

        var older = await _chatStore.ReadRecentAsync(chat.Id, count - fromTail.Count, conversationOnly, window.TailStartSeq)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        MapAttached(chat, older);
        older.AddRange(fromTail);
        return older;
    }

    /// <summary>
    /// Reads an older exchange of a chat's stored history for its build (see
    /// <see cref="ChatStore.ReadExchangeAsync"/>): its outline, and the messages <paramref name="pick"/>
    /// chooses from it. The exchange's messages are the instances a surface shows when it shows them;
    /// the steps its runs took after it are private copies.
    /// </summary>
    internal async Task<ExchangeRead> ReadHistoryExchangeAsync(
        Chat chat,
        long fromSeq,
        long toSeq,
        Func<ExchangeOutline, IReadOnlySet<long>> pick,
        CancellationToken cancellationToken = default)
    {
        if (_chatStore is null)
            return new ExchangeRead(ExchangeOutline.Empty, [], []);
        var read = await _chatStore.ReadExchangeAsync(chat.Id, fromSeq, toSeq, pick)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        MapAttached(chat, read.Messages);
        return read;
    }

    /// <summary>The conversation outline of a chat's stored history (before its tail).</summary>
    internal async Task<List<ChatOutlineEntry>> ReadChatOutlineAsync(Chat chat, CancellationToken cancellationToken = default)
    {
        var window = GetTranscriptWindow(chat);
        if (_chatStore is null || !window.HasHistory)
            return [];
        return await _chatStore.ReadOutlineAsync(chat.Id, window.TailStartSeq).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A chat's conversation (user, assistant, system, error) plus the other messages carrying any of
    /// the given facets, across stored history and the live tail, oldest first.
    /// </summary>
    internal async Task<IReadOnlyList<ChatMessage>> ReadChatConversationAndKindsAsync(
        Chat chat,
        IReadOnlyList<string> kinds,
        CancellationToken cancellationToken = default)
    {
        var (window, tail) = SnapshotTail(chat);
        var fromTail = tail
            .Where(message => IsConversationRole(message.Role) || ChatMessageKinds.Classify(message) is { } kind && kinds.Contains(kind))
            .ToList();
        if (_chatStore is null)
            return fromTail;

        var beforeSeq = window.IsLoaded ? window.TailStartSeq : long.MaxValue;
        var stored = await _chatStore.ReadConversationAndKindsAsync(chat.Id, kinds, beforeSeq)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!window.IsLoaded)
            return MergeUnloadedTail(stored, fromTail);
        MapAttached(chat, stored);
        stored.AddRange(fromTail);
        return stored;
    }

    /// <summary>
    /// For each of the given sub-agent runs, the few stored steps its card summarizes — its latest
    /// step card, its latest intent and its first plan update, which may be in the live tail when a
    /// background run went on working — so an index of runs reads like the runs' cards without
    /// reading their work. Private copies, without their output.
    /// </summary>
    internal async Task<List<ChatMessage>> ReadHistoryRunGlanceStepsAsync(
        Chat chat,
        IReadOnlyCollection<string> runToolCallIds,
        IReadOnlyList<string> toolsWithoutCards,
        CancellationToken cancellationToken = default)
    {
        if (_chatStore is null || runToolCallIds.Count == 0)
            return [];
        return await _chatStore.ReadRunGlanceStepsAsync(chat.Id, runToolCallIds, toolsWithoutCards)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The tool calls stored before <paramref name="beforeSeq"/> that the given steps continue (see
    /// <see cref="ChatStore.ReadEarlierToolCallsAsync"/>), without their bodies.
    /// </summary>
    internal async Task<List<StoredToolCall>> ReadEarlierToolCallsAsync(
        Chat chat,
        IReadOnlyList<(string? ToolCallId, string? ParentToolCallId)> steps,
        long beforeSeq,
        CancellationToken cancellationToken = default)
    {
        if (_chatStore is null || steps.Count == 0)
            return [];
        return await _chatStore.ReadEarlierToolCallsAsync(chat.Id, steps, beforeSeq)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What an overview of a chat's stored history before <paramref name="beforeSeq"/> lists (see
    /// <see cref="ChatStore.ReadOverviewAsync"/>): its conversation, the history's steps carrying any of
    /// the given facets, runs as summaries, and what each file edit changed. Private copies: nothing is
    /// attached.
    /// </summary>
    internal async Task<ChatOverview> ReadHistoryOverviewAsync(
        Chat chat,
        IReadOnlyList<string> kinds,
        long beforeSeq,
        CancellationToken cancellationToken = default)
    {
        if (_chatStore is null)
            return ChatOverview.Empty;
        return await _chatStore.ReadOverviewAsync(chat.Id, kinds, beforeSeq)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A chat's stored messages at the given positions, oldest first: private copies, read
    /// synchronously, for work already off the UI thread.
    /// </summary>
    internal List<ChatMessage> ReadStoredMessagesNow(Chat chat, IReadOnlyCollection<long> seqs)
        => _chatStore?.ReadAtSeqsNow(chat.Id, seqs) ?? [];

    /// <summary>Stored-history messages carrying any of the given facets (see <see cref="ChatMessageKinds"/>).</summary>
    internal async Task<List<ChatMessage>> ReadChatHistoryByKindAsync(
        Chat chat,
        IReadOnlyList<string> kinds,
        CancellationToken cancellationToken = default)
    {
        var window = GetTranscriptWindow(chat);
        if (_chatStore is null || !window.HasHistory)
            return [];
        var messages = await _chatStore.ReadByKindAsync(chat.Id, kinds, window.TailStartSeq)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return MapAttached(chat, messages);
    }

    /// <summary>A tool call and everything nested under it, from history and the live tail, in order.</summary>
    internal async Task<IReadOnlyList<ChatMessage>> ReadChatToolDescendantsAsync(
        Chat chat,
        string rootToolCallId,
        CancellationToken cancellationToken = default)
    {
        var (window, tail) = SnapshotTail(chat);
        if (_chatStore is null || window.IsLoaded && !window.HasHistory)
            return CollectDescendants(tail, [rootToolCallId]);

        var stored = await _chatStore.ReadDescendantsAsync(chat.Id, rootToolCallId)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        var history = window.IsLoaded ? stored.Where(message => message.StorageSeq < window.TailStartSeq).ToList() : stored;
        // Steps in the tail can belong to calls stored before it: a nested run that went on working.
        var calls = new HashSet<string>(StringComparer.Ordinal) { rootToolCallId };
        foreach (var message in history)
        {
            if (message.ToolCallId is { Length: > 0 } toolCallId)
                calls.Add(toolCallId);
        }

        var fromTail = CollectDescendants(tail, calls);
        if (!window.IsLoaded)
            return MergeUnloadedTail(history, fromTail);
        MapAttached(chat, history);
        history.AddRange(fromTail);
        return history;
    }

    /// <summary>
    /// The messages among <paramref name="messages"/> that are one of <paramref name="calls"/> or
    /// nested under one of them, at any depth, in order. Nested calls found are added to the set.
    /// </summary>
    internal static List<ChatMessage> CollectDescendants(IReadOnlyList<ChatMessage> messages, HashSet<string> calls)
    {
        var result = new List<ChatMessage>();
        foreach (var message in messages)
        {
            if (message.ToolCallId is { Length: > 0 } toolCallId && calls.Contains(toolCallId)
                || message.ParentToolCallId is { } parent && calls.Contains(parent))
            {
                result.Add(message);
                if (message.ToolCallId is { Length: > 0 } nested)
                    calls.Add(nested);
            }
        }

        return result;
    }

    /// <summary>Finds a message by id in the tail, among shown older messages, or in storage.</summary>
    internal async Task<ChatMessage?> FindChatMessageAsync(Chat chat, Guid messageId, CancellationToken cancellationToken = default)
    {
        var (window, tail) = SnapshotTail(chat);
        var inTail = tail.LastOrDefault(message => message.Id == messageId);
        if (inTail is not null || _chatStore is null)
            return inTail;

        var state = GetTranscriptState(chat.Id);
        lock (state)
        {
            var attached = state.AttachedHistory.Values.FirstOrDefault(message => message.Id == messageId);
            if (attached is not null)
                return attached;
        }

        var found = await _chatStore.FindMessageAsync(chat.Id, messageId).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (found is not { Message: var message })
            return null;
        return window.IsLoaded && message.StorageSeq >= window.TailStartSeq ? null : message;
    }

    /// <summary>
    /// The chat's newest user, assistant, system or error message with <paramref name="role"/>
    /// (optionally only one with text): from the live tail, else from storage.
    /// </summary>
    internal async Task<ChatMessage?> ReadLastChatMessageAsync(
        Chat chat,
        string role,
        bool withText = false,
        CancellationToken cancellationToken = default)
    {
        var (window, tail) = SnapshotTail(chat);
        var inTail = tail.LastOrDefault(message => message.Role == role
                                                   && (!withText || !string.IsNullOrWhiteSpace(message.Content)));
        if (inTail is not null || _chatStore is null || (window.IsLoaded && !window.HasHistory))
            return inTail;

        var stored = await _chatStore
            .ReadLastConversationMessageAsync(chat.Id, role, withText, window.IsLoaded ? window.TailStartSeq : long.MaxValue)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return stored is null ? null : MapAttached(chat, [stored])[0];
    }

    /// <summary>
    /// The whole chat's messages per role (stored history and live tail), with how many of them have
    /// text. Counts in storage without reading the transcript.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, (int Count, int WithText)>> CountChatRolesAsync(
        Chat chat,
        CancellationToken cancellationToken = default)
    {
        var (window, tail) = SnapshotTail(chat);
        var counts = new Dictionary<string, (int Count, int WithText)>(StringComparer.Ordinal);
        void Add(string role, int count, int withText)
        {
            counts.TryGetValue(role, out var current);
            counts[role] = (current.Count + count, current.WithText + withText);
        }

        if (_chatStore is not null && !(window.IsLoaded && !window.HasHistory))
        {
            var stored = await _chatStore.CountRolesAsync(chat.Id, window.IsLoaded ? window.TailStartSeq : long.MaxValue)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (role, value) in stored)
                Add(role, value.Count, value.WithText);
            // A tail that was never loaded only holds appended messages; saved ones are counted already.
            if (!window.IsLoaded)
                tail = tail.Where(static message => message.StorageSeq <= 0).ToList();
        }

        foreach (var message in tail)
            Add(message.Role, 1, string.IsNullOrWhiteSpace(message.Content) ? 0 : 1);
        return counts;
    }

    private (ChatTranscriptWindow Window, List<ChatMessage> Tail) SnapshotTail(Chat chat)
    {
        if (_chatStore is null)
            return (new ChatTranscriptWindow(true, 1, 0), chat.Messages.ToList());

        var state = GetTranscriptState(chat.Id);
        lock (state)
        {
            return (new ChatTranscriptWindow(state.IsLoaded, state.TailStartSeq, state.IsLoaded ? state.HistoryCount : 0),
                chat.Messages.ToList());
        }
    }

    // A tail that was never loaded only holds appended messages; stored copies of them are replaced.
    private static List<ChatMessage> MergeUnloadedTail(List<ChatMessage> stored, List<ChatMessage> tail)
    {
        if (tail.Count == 0)
            return stored;
        var liveSeqs = tail.Where(static message => message.StorageSeq > 0).Select(static message => message.StorageSeq).ToHashSet();
        var merged = stored.Where(message => !liveSeqs.Contains(message.StorageSeq)).ToList();
        merged.AddRange(tail);
        return merged;
    }

    private static bool IsConversationRole(string? role)
        => role is "user" or "assistant" or "system" or "error";

    // The searchable text of the last chat searched, kept only weakly: typing a query searches the
    // same text several times, but it must never keep a long chat's text in memory.
    private WeakReference<HistorySearchText>? _historySearchText;

    private sealed record HistorySearchText(
        Guid ChatId, long Version, long BeforeSeq, bool IncludesReasoning, List<(long Seq, string Text)> Rows);

    /// <summary>
    /// Searches a chat's stored history (before its live tail) for <paramref name="query"/>, ignoring
    /// case, in the text its transcript shows (see <see cref="TranscriptSearchText"/>). Returns each
    /// matching message's position and how often it matches, in order.
    /// </summary>
    internal async Task<List<(long Seq, int Occurrences)>> SearchHistoryTextAsync(
        Chat chat,
        string query,
        bool includeReasoning,
        CancellationToken cancellationToken = default)
    {
        var window = GetTranscriptWindow(chat);
        if (_chatStore is null || !window.HasHistory || string.IsNullOrEmpty(query))
            return [];

        var version = _chatStore.GetContentVersion(chat.Id);
        List<(long Seq, string Text)> rows;
        if (_historySearchText?.TryGetTarget(out var cached) == true
            && cached.ChatId == chat.Id && cached.Version == version && cached.BeforeSeq == window.TailStartSeq
            && cached.IncludesReasoning == includeReasoning)
        {
            rows = cached.Rows;
        }
        else
        {
            var messages = await _chatStore.ReadSearchMessagesAsync(chat.Id, window.TailStartSeq)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            // Older messages shown on screen are the ones searched: they may hold edits not stored yet.
            MapAttached(chat, messages);
            rows = [];
            foreach (var message in messages)
            {
                if (TranscriptSearchText.Of(message, includeReasoning) is { Length: > 0 } text)
                    rows.Add((message.StorageSeq, text));
            }

            _historySearchText = new WeakReference<HistorySearchText>(
                new HistorySearchText(chat.Id, version, window.TailStartSeq, includeReasoning, rows));
        }

        return await Task.Run(() =>
        {
            var matches = new List<(long, int)>();
            foreach (var (seq, text) in rows)
            {
                if (TranscriptSearchText.CountOccurrences(text, query) is > 0 and var count)
                    matches.Add((seq, count));
            }

            return matches;
        }, cancellationToken).ConfigureAwait(false);
    }

    // ── Reads by chat id (remote clients, off the UI thread) ───────────────

    /// <summary>How many messages a chat has in storage, once any pending import has finished.</summary>
    internal async Task<int> CountStoredMessagesAsync(Guid chatId, CancellationToken cancellationToken = default)
        => _chatStore is null ? 0 : await _chatStore.CountAsync(chatId).WaitAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Stored messages by absolute position (0 = the chat's first message), read synchronously. Call
    /// from a background thread. The objects are private copies, not the ones a transcript shows.
    /// </summary>
    internal List<ChatMessage> ReadStoredMessagesByIndex(Guid chatId, int startIndex, int count)
        => _chatStore?.ReadByIndexNow(chatId, startIndex, count) ?? [];

    /// <summary>A stored message by id with its absolute position, or null.</summary>
    internal async Task<(ChatMessage Message, int Index)?> FindStoredMessageAsync(
        Guid chatId,
        Guid messageId,
        CancellationToken cancellationToken = default)
        => _chatStore is null
            ? null
            : await _chatStore.FindMessageAsync(chatId, messageId).WaitAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The stored messages that can make a local image path showable (see <see cref="ChatStore.ReadImageSourcesAsync"/>).</summary>
    internal async Task<IReadOnlyList<ChatMessage>> ReadImageSourceMessagesAsync(Guid chatId, CancellationToken cancellationToken = default)
        => _chatStore is null
            ? []
            : await _chatStore.ReadImageSourcesAsync(chatId).WaitAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// True when a chat has messages in the database (or still waiting in a legacy file), so callers
    /// can tell an empty chat from one whose transcript is simply not in memory.
    /// </summary>
    public bool HasStoredMessages(Guid chatId)
        => _chatStore is not null
           && (_chatStore.GetStoredMessageCount(chatId) > 0 || _legacyImporter?.IsPending(chatId) == true);

    // ── Suggestions ─────────────────────────────────────────────────────────

    /// <summary>Shares the frequent-request history across chat surfaces; stale data is refreshed
    /// once in the background. Rejected prompts never need to stay cached for the ranker.</summary>
    internal Task<IReadOnlyList<UserPromptHistoryItem>> GetSuggestionHistoryAsync(
        IReadOnlyDictionary<Guid, IReadOnlyList<ChatMessage>> loadedMessageSnapshots)
    {
        ArgumentNullException.ThrowIfNull(loadedMessageSnapshots);
        lock (_suggestionHistorySync)
        {
            var cached = _suggestionHistory;
            if (cached is not null && DateTimeOffset.UtcNow - _suggestionHistoryAt < SuggestionHistoryCacheTtl)
                return Task.FromResult(cached);

            var refresh = _suggestionHistoryRefresh;
            if (refresh is null || refresh.IsCompleted)
            {
                var generation = _suggestionHistoryGeneration;
                refresh = Task.Run(async () =>
                {
                    var history = await GetUserPromptHistoryAsync(
                        SuggestionHistoryScanLimit, loadedMessageSnapshots).ConfigureAwait(false);
                    IReadOnlyList<UserPromptHistoryItem> candidates = history
                        .Where(static item => SuggestionHistoryRanker.IsCandidate(item.Content))
                        .ToArray();
                    lock (_suggestionHistorySync)
                    {
                        if (generation != _suggestionHistoryGeneration)
                            return Array.Empty<UserPromptHistoryItem>();

                        _suggestionHistory = candidates;
                        _suggestionHistoryAt = DateTimeOffset.UtcNow;
                    }

                    return candidates;
                });
                _suggestionHistoryRefresh = refresh;
                _ = refresh.ContinueWith(
                    task => Trace.TraceWarning("[Chat] Suggestion history refresh failed: {0}", task.Exception),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return cached is null ? refresh : Task.FromResult(cached);
        }
    }

    /// <summary>
    /// Returns the newest user-authored prompts across chats for personalized follow-up suggestions:
    /// one indexed query over every stored chat, plus prompts still only in the given in-memory tails.
    /// </summary>
    public async Task<IReadOnlyList<UserPromptHistoryItem>> GetUserPromptHistoryAsync(
        int maxMessages,
        IReadOnlyDictionary<Guid, IReadOnlyList<ChatMessage>> loadedMessageSnapshots,
        CancellationToken cancellationToken = default)
    {
        if (maxMessages <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxMessages), "Message limit must be greater than zero.");

        var prompts = new Dictionary<(Guid, Guid), UserPromptHistoryItem>();
        if (_chatStore is not null)
        {
            var stored = await _chatStore.ReadUserPromptsAsync(maxMessages).WaitAsync(cancellationToken).ConfigureAwait(false);
            foreach (var prompt in stored)
            {
                if (!string.IsNullOrWhiteSpace(prompt.Content))
                    prompts[(prompt.ChatId, prompt.MessageId)] = new UserPromptHistoryItem(prompt.ChatId, prompt.MessageId, prompt.Content, prompt.Timestamp);
            }
        }

        var knownChats = SnapshotChatsByRecency().Select(static chat => chat.Id).ToHashSet();
        foreach (var (chatId, messages) in loadedMessageSnapshots)
        {
            if (!knownChats.Contains(chatId))
                continue;
            foreach (var message in messages)
            {
                if (!string.Equals(message.Role, "user", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(message.Content))
                    continue;
                prompts[(chatId, message.Id)] = new UserPromptHistoryItem(chatId, message.Id, message.Content, message.Timestamp);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return prompts.Values
            .Where(item => knownChats.Contains(item.ChatId))
            .OrderByDescending(static item => item.Timestamp)
            .Take(maxMessages)
            .ToList();
    }

    /// <summary>Takes a recency-ordered snapshot of the chat list. <see cref="_data"/>.Chats is a plain
    /// <see cref="List{T}"/> mutated on the UI thread, while this scan can run on a background thread, so a
    /// concurrent add/remove can throw <see cref="InvalidOperationException"/> mid-enumeration. Retrying a
    /// few times yields a consistent snapshot without locking the UI thread's collection.</summary>
    private List<Chat> SnapshotChatsByRecency()
    {
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return _data.Chats
                    .OrderByDescending(static chat => chat.UpdatedAt)
                    .ToList();
            }
            catch (InvalidOperationException) when (attempt < maxAttempts)
            {
                // Collection was modified during enumeration; retry with a fresh pass.
            }
        }
    }

    private Chat? FindChatSnapshot(Guid chatId)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return _data.Chats.FirstOrDefault(chat => chat.Id == chatId);
            }
            catch (InvalidOperationException) when (attempt < 4)
            {
            }
        }
    }

    // ── Search ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a search snapshot for a chat. Stored chats are read from the database (text only,
    /// extracted by SQLite) and cached per content version; a store without a database uses memory.
    /// </summary>
    public ChatSearchSnapshot GetChatSearchSnapshot(Chat chat) => GetChatSearchSnapshot(chat, conversationOnly: false);

    /// <summary>
    /// The snapshot the global search index is built from: only the user, assistant and error
    /// messages it indexes, so building an entry never reads a chat's tool output. Not cached; the
    /// index keeps what it needs.
    /// </summary>
    public ChatSearchSnapshot GetChatIndexSnapshot(Chat chat) => GetChatSearchSnapshot(chat, conversationOnly: true);

    private ChatSearchSnapshot GetChatSearchSnapshot(Chat chat, bool conversationOnly)
    {
        ArgumentNullException.ThrowIfNull(chat);

        if (_chatStore is null)
            // Copy once before indexing: callers (global search index, search_chats) run on
            // background threads while the UI thread appends/removes messages, so iterating the
            // live list would risk ArgumentOutOfRangeException / torn reads.
            return CreateLoadedChatSearchSnapshot(chat.Messages.ToArray());

        // Content not saved yet (Auto Save Chats is off, or a reply is still arriving) is newer than
        // storage: memory has the tail, and storage the history before it. Read fresh each time.
        if (TrySnapshotUnsavedTail(chat, out var tailStartSeq, out var unsaved))
            return CreateMergedSearchSnapshot(chat, conversationOnly, tailStartSeq, unsaved);

        var version = _chatStore.GetContentVersion(chat.Id);
        if (!conversationOnly)
        {
            lock (_chatSearchCacheSync)
            {
                if (_chatSearchCache.TryGetValue(chat.Id, out var cached) && cached.Version == version && version > 0)
                    return cached.Snapshot;
            }
        }

        if (ReadSearchRows(chat.Id, conversationOnly, long.MaxValue) is not { } rows)
            return new ChatSearchSnapshot { Version = $"io-error:{chat.Id}" };

        // Reading may have imported the chat, which gives it a version.
        version = _chatStore.GetContentVersion(chat.Id);
        var messages = new List<ChatSearchMessage>(rows.Count);
        foreach (var row in rows)
            AddSearchMessage(messages, row.Content, row.QuestionText, row.ToolName, row.Author, row.Role, row.Timestamp);

        var snapshot = new ChatSearchSnapshot { Version = $"db:{version}:{messages.Count}", Messages = messages.ToArray() };
        if (!conversationOnly)
        {
            lock (_chatSearchCacheSync)
                _chatSearchCache[chat.Id] = new CachedChatSearchSnapshot(version, snapshot);
        }

        return snapshot;
    }

    private static void AddSearchMessage(
        List<ChatSearchMessage> messages, string? content, string? questionText, string? toolName, string? author, string role, DateTimeOffset timestamp)
    {
        var text = BuildChatSearchText(content, questionText, toolName, author, role);
        if (!string.IsNullOrWhiteSpace(text))
            messages.Add(new ChatSearchMessage { Text = text, Role = role, Timestamp = timestamp });
    }

    private List<ChatSearchRow>? ReadSearchRows(Guid chatId, bool conversationOnly, long beforeSeq)
    {
        try
        {
            return _chatStore!.ReadSearchRows(chatId, conversationOnly, beforeSeq);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
        {
            Debug.WriteLine($"[Lumi] Search snapshot read failed for chat {chatId}: {ex.Message}");
            return null;
        }
    }

    // The stored history before the tail, then the tail as it is in memory.
    private ChatSearchSnapshot CreateMergedSearchSnapshot(Chat chat, bool conversationOnly, long tailStartSeq, ChatMessage[] tail)
    {
        var rows = ReadSearchRows(chat.Id, conversationOnly, tailStartSeq) ?? [];
        var messages = new List<ChatSearchMessage>(rows.Count + tail.Length);
        foreach (var row in rows)
            AddSearchMessage(messages, row.Content, row.QuestionText, row.ToolName, row.Author, row.Role, row.Timestamp);
        foreach (var message in tail)
        {
            if (!conversationOnly || message.Role is "user" or "assistant" or "error")
                AddSearchMessage(messages, message.Content, message.QuestionText, message.ToolName, message.Author, message.Role, message.Timestamp);
        }

        return new ChatSearchSnapshot { Version = $"unsaved:{messages.Count}", Messages = messages.ToArray() };
    }

    /// <summary>
    /// The chat's tail when it holds messages or edits storage has not received yet, with where the
    /// tail starts in storage. A copy, as callers run off the UI thread.
    /// </summary>
    private bool TrySnapshotUnsavedTail(Chat chat, out long tailStartSeq, out ChatMessage[] tail)
    {
        var state = GetTranscriptState(chat.Id);
        lock (state)
        {
            tailStartSeq = state.TailStartSeq;
            tail = chat.Messages.ToArray();
            if (!state.IsLoaded && !state.HasAppendBase)
                return false;
            foreach (var message in tail)
            {
                if (message.StorageSeq <= 0 || message.HasUnpersistedChanges)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The version the global search index keeps a chat's entry under: its stored content version,
    /// or null while the chat holds content storage has not received yet, so its entry is read fresh.
    /// </summary>
    public long? GetChatIndexVersion(Chat chat)
        => _chatStore is not null && TrySnapshotUnsavedTail(chat, out _, out _) ? null : GetChatContentVersion(chat.Id);

    private void RemoveChatSearchSnapshot(Guid chatId)
    {
        lock (_chatSearchCacheSync)
            _chatSearchCache.Remove(chatId);
    }

    /// <summary>Evicts a chat's cached full-text snapshot to bound memory (e.g., after it is indexed elsewhere).</summary>
    public void EvictChatSearchSnapshot(Guid chatId) => RemoveChatSearchSnapshot(chatId);

    /// <summary>
    /// A monotonic marker of a chat's stored content, or null when nothing is stored. Changes whenever
    /// the chat's transcript is written, so search indexes can detect stale entries across runs.
    /// </summary>
    public long? GetChatContentVersion(Guid chatId)
    {
        if (_chatStore is null)
            return null;
        if (_legacyImporter?.IsPending(chatId) == true)
            return null;
        var version = _chatStore.GetContentVersion(chatId);
        return version > 0 ? version : null;
    }

    private static ChatSearchSnapshot CreateLoadedChatSearchSnapshot(IReadOnlyList<ChatMessage> messages)
    {
        var signature = new HashCode();
        var searchMessages = new List<ChatSearchMessage>(messages.Count);
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var text = BuildChatSearchText(message.Content, message.QuestionText, message.ToolName, message.Author, message.Role);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            searchMessages.Add(new ChatSearchMessage
            {
                Text = text,
                Role = message.Role,
                Timestamp = message.Timestamp
            });

            signature.Add(index);
            signature.Add(message.Id);
            signature.Add(message.Role, StringComparer.OrdinalIgnoreCase);
            signature.Add(message.Timestamp.UtcTicks);
            signature.Add(text, StringComparer.Ordinal);
        }

        signature.Add(searchMessages.Count);
        return new ChatSearchSnapshot
        {
            Version = $"loaded:{searchMessages.Count}:{signature.ToHashCode()}",
            Messages = searchMessages.ToArray()
        };
    }

    private static string BuildChatSearchText(string? content, string? questionText, string? toolName, string? author, string? role)
    {
        var segments = new List<string>(4);

        if (!string.IsNullOrWhiteSpace(content))
            segments.Add(content);
        if (!string.IsNullOrWhiteSpace(questionText))
            segments.Add(questionText);
        if (!string.IsNullOrWhiteSpace(toolName))
            segments.Add(toolName);
        if (!string.IsNullOrWhiteSpace(author)
            && !string.Equals(author, role, StringComparison.OrdinalIgnoreCase))
        {
            segments.Add(author);
        }

        return string.Join(Environment.NewLine, segments);
    }

    // ── Deleting ────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes a chat's stored transcript and row, and its legacy copy from before the chat
    /// database. Its transcript state stays as a tombstone, so a late save cannot bring it back.
    /// </summary>
    public async Task DeleteChatFileAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        MarkTranscriptDeleted(GetTranscriptState(chatId));
        RemoveChatSearchSnapshot(chatId);
        if (_chatStore is null)
        {
            ChatContentChanged?.Invoke(chatId);
            return;
        }

        if (_legacyImporter is not null)
        {
            await _legacyImporter.ForgetAsync(chatId).WaitAsync(cancellationToken).ConfigureAwait(false);
            // File work, kept off the caller's (UI) thread: ForgetAsync usually completes at once.
            var importer = _legacyImporter;
            await Task.Run(() =>
            {
                importer.DeleteLegacyFile(chatId);
                RemoveFromLegacyIndex(LegacyIndexBackupFile, chatId);
            }, cancellationToken).ConfigureAwait(false);
        }

        await _chatStore.DeleteChatAsync(chatId).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes a deleted chat out of a legacy index — the one kept from before the chat database still
    /// lists its title and preview.
    /// </summary>
    internal static void RemoveFromLegacyIndex(string indexFile, Guid chatId)
    {
        lock (LegacyIndexSync)
        {
            try
            {
                if (!File.Exists(indexFile))
                    return;

                using var document = JsonDocument.Parse(File.ReadAllBytes(indexFile));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("chats", out var chats)
                    || chats.ValueKind != JsonValueKind.Array
                    || !chats.EnumerateArray().Any(chat => IsChat(chat, chatId)))
                {
                    return;
                }

                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    foreach (var property in root.EnumerateObject())
                    {
                        if (!property.NameEquals("chats"))
                        {
                            property.WriteTo(writer);
                            continue;
                        }

                        writer.WritePropertyName(property.Name);
                        writer.WriteStartArray();
                        foreach (var chat in property.Value.EnumerateArray())
                        {
                            if (!IsChat(chat, chatId))
                                chat.WriteTo(writer);
                        }

                        writer.WriteEndArray();
                    }

                    writer.WriteEndObject();
                }

                JsonFilePersistence.WriteFileAtomically(indexFile, buffer.WrittenSpan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Trace.TraceWarning("[ChatStore] Could not remove chat {0} from {1}: {2}", chatId, indexFile, ex.Message);
            }
        }

        static bool IsChat(JsonElement chat, Guid chatId)
            => chat.ValueKind == JsonValueKind.Object
               && chat.TryGetProperty("id", out var id)
               && id.ValueKind == JsonValueKind.String
               && id.TryGetGuid(out var value)
               && value == chatId;
    }

    /// <summary>
    /// Deletes every stored chat transcript, and the legacy chats and index kept from before the chat
    /// database. Every chat's transcript state becomes a tombstone. A store that only lives in memory
    /// owns no database, so it leaves the saved profile's chats alone.
    /// </summary>
    public async Task DeleteAllChatFilesAsync()
    {
        lock (_chatSearchCacheSync)
            _chatSearchCache.Clear();
        lock (_suggestionHistorySync)
        {
            _suggestionHistoryGeneration++;
            _suggestionHistory = null;
            _suggestionHistoryRefresh = null;
            _suggestionHistoryAt = DateTimeOffset.MinValue;
        }

        lock (_transcriptStatesSync)
        {
            foreach (var state in _transcriptStates.Values)
                MarkTranscriptDeleted(state);
        }

        if (_chatStore is not null)
        {
            // Queued before anything below yields: a chat created after Clear All saves after this.
            var deleted = _chatStore.DeleteAllChatsAsync();
            if (_legacyImporter is not null)
            {
                await _legacyImporter.ForgetAllAsync().ConfigureAwait(false);
                _legacyImporter.DeleteLegacyFiles();
                // Under the lock a chat's deletion rewrites the index with, so that cannot bring it back.
                lock (LegacyIndexSync)
                    TryDeleteFile(LegacyIndexBackupFile);
            }

            await deleted.ConfigureAwait(false);
        }

        ChatsContentReset?.Invoke();
    }

    private static void MarkTranscriptDeleted(ChatTranscriptState state)
    {
        lock (state)
        {
            state.IsDeleted = true;
            state.AttachedHistory.Clear();
            state.PersistedTailSeqs = [];
        }
    }

    private static void TryDeleteFile(string path)
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

    /// <summary>
    /// Stops the legacy import and waits for queued chat writes to reach the database, then closes it.
    /// Called once at shutdown; saves after it fail.
    /// </summary>
    internal void CloseChatStorage()
    {
        _legacyImporter?.Dispose();
        _chatStore?.Dispose();
    }

    private readonly record struct CachedChatSearchSnapshot(long Version, ChatSearchSnapshot Snapshot);
}
