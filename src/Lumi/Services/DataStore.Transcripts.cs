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
    private string? _damagedChatsCopy;

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
    /// legacy transcripts in the background, and where a damaged database was moved aside (see
    /// <see cref="OpenOrSetAside"/>), if one was: what still reads in it is copied over by
    /// <see cref="RecoverChatsAsync"/>.
    /// </summary>
    internal static (ChatStore Store, List<Chat> Chats, LegacyChatImporter Importer, string? DamagedCopy) OpenChatStorage(
        string databaseFile,
        IReadOnlyList<Chat> legacyIndexChats,
        string legacyChatsDirectory,
        Action? backupLegacyIndex)
    {
        var (store, stored, damagedCopy) = OpenOrSetAside(databaseFile);
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
        return (store, stored.Select(static chat => chat.Chat).ToList(), importer, damagedCopy);
    }

    /// <summary>
    /// Opens the chat database. One SQLite reports damaged (when it opens, when its chats are listed,
    /// or when damage a read ran into last time is confirmed by a check of the whole file) is moved
    /// aside and replaced by a new one, so Lumi still starts; anything else that fails still fails. A
    /// recovery Lumi was closed during goes on (<see cref="ChatDatabase.PendingRecovery"/>).
    /// </summary>
    private static (ChatStore Store, IReadOnlyList<StoredChat> Chats, string? DamagedCopy) OpenOrSetAside(string databaseFile)
    {
        ChatStore? store = null;
        string damage;
        try
        {
            store = new ChatStore(databaseFile);
            if (!ChatDatabase.HasDamageMarker(databaseFile))
                return (store, store.LoadChats(), ChatDatabase.PendingRecovery(databaseFile));
            if (store.IsIntact())
            {
                store.ClearDamageMarker();
                return (store, store.LoadChats(), ChatDatabase.PendingRecovery(databaseFile));
            }

            damage = "a check of the whole file found damage";
        }
        catch (Exception ex) when (ChatDatabase.IsDamage(ex))
        {
            damage = ex.ToString();
        }

        Trace.TraceError("[ChatStore] The chat database is damaged; Lumi moves it aside and starts a new one: {0}", damage);
        store?.Dispose();
        var copy = ChatDatabase.SetAside(databaseFile);
        store = new ChatStore(databaseFile);
        return (store, store.LoadChats(), copy);
    }

    /// <summary>A damaged chat database moved aside at startup whose chats are not copied over yet.</summary>
    internal bool HasChatsToRecover => _damagedChatsCopy is not null;

    /// <summary>
    /// True when this start upgrades the chat database once (<see cref="ChatStore.NeedsUpgrade"/>).
    /// Every write waits behind the upgrade, so Lumi lets it finish before opening a window
    /// (<see cref="FinishChatStorageUpgradeAsync"/>).
    /// </summary>
    internal bool UpgradesChatStorage { get; }

    private readonly Task _chatStorageUpkeep = Task.CompletedTask;

    /// <summary>Completes once this start's upkeep of the chat database has run; a failure is logged and tried again next start.</summary>
    internal async Task FinishChatStorageUpgradeAsync()
    {
        try
        {
            await _chatStorageUpkeep.ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Logged where it was started.
        }
    }

    /// <summary>
    /// True when a read failed because the chat database is damaged. The failure was noted, so the
    /// next start checks the file and recovers what still reads.
    /// </summary>
    internal static bool IsDamage(Exception exception) => ChatDatabase.IsDamage(exception);

    /// <summary>
    /// Copies what still reads in the damaged database moved aside at startup into the new one (see
    /// <see cref="ChatSalvage"/>) and lists the chats it recovered. Run once, before any window uses the
    /// chat list; the copy itself runs on a background thread. Never fails: Lumi starts with whatever
    /// was recovered. When the disk has no room for the copy, nothing is copied and the recovery waits
    /// for a later start (<see cref="ChatDatabase.PendingRecovery"/>): <c>Recovered</c> is null and
    /// <c>SpaceNeeded</c> says how much room it needs.
    /// </summary>
    internal async Task<(ChatSalvage.Result? Recovered, long SpaceNeeded)> RecoverChatsAsync()
    {
        if (_damagedChatsCopy is not { } copy || _chatStore is null)
            return (new ChatSalvage.Result(), 0);

        _damagedChatsCopy = null;
        var store = _chatStore;
        try
        {
            // The copy can grow as large as what reads in the damaged file, plus its text index. Cut
            // short by a full disk, it would leave the rest behind for good.
            var spaceNeeded = new FileInfo(copy).Length / 8 * 9;
            if (!store.HasRoomFor(spaceNeeded))
            {
                Trace.TraceWarning("[ChatStore] Not enough disk space to recover the chats in {0} yet.", copy);
                return (null, spaceNeeded);
            }

            var result = await Task.Run(() => store.Salvage(copy)).ConfigureAwait(true);
            var known = _data.Chats.Select(static chat => chat.Id).ToHashSet();
            foreach (var stored in store.LoadChats())
            {
                if (known.Add(stored.Chat.Id))
                    _data.Chats.Add(stored.Chat);
            }

            CleanOrphanedChats();
            Trace.TraceInformation("[ChatStore] Recovered {0} chats ({1} messages) from {2}.", result.Chats, result.Messages, copy);
            return (result, 0);
        }
        catch (Exception ex)
        {
            // The damaged file is kept; what was copied before the failure stays.
            Trace.TraceError("[ChatStore] Recovering chats from {0} failed: {1}", copy, ex);
            return (new ChatSalvage.Result(), 0);
        }
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

    // Chat search used to keep its own copy of every chat's text here; the chat database indexes it now.
    private static void RemoveRetiredSearchIndex()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(AppDir, "search-content-index.bin*"))
                TryDeleteFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("[ChatStore] Could not remove the retired search index: {0}", ex.Message);
        }
    }

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

    /// <summary>
    /// Searches a chat's stored history (before its live tail) for <paramref name="query"/>, ignoring
    /// case, in the text its transcript shows (see <see cref="TranscriptSearchText"/>). Returns each
    /// matching message's position and how often it matches, in order. Only messages the chat
    /// database finds the query in are read.
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

        var candidates = await _chatStore.ReadSearchCandidatesAsync(chat.Id, query, window.TailStartSeq, includeReasoning)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        // Older messages shown on screen are the ones searched: they may hold edits not stored yet.
        var matches = new List<(long, int)>();
        foreach (var message in MapAttached(chat, candidates))
        {
            if (TranscriptSearchText.Of(message, includeReasoning) is { Length: > 0 } text
                && TranscriptSearchText.CountOccurrences(text, query) is > 0 and var count)
            {
                matches.Add((message.StorageSeq, count));
            }
        }

        return matches;
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
    /// The chats whose messages contain <paramref name="text"/>, ignoring case and accents (see
    /// <see cref="ChatSearchText"/>): stored messages through the chat database's text index, and
    /// messages it does not have yet (a reply still arriving, edits not saved) in memory.
    /// </summary>
    public IReadOnlyDictionary<Guid, ChatContentHit> FindChatsContaining(string text)
    {
        var hits = new Dictionary<Guid, ChatContentHit>();
        if (!ChatSearchText.CanLookUp(text))
            return hits;

        if (_chatStore is not null)
        {
            try
            {
                foreach (var match in _chatStore.FindText(text))
                {
                    hits[match.ChatId] = new ChatContentHit(
                        match.UserMessageKey > 0,
                        match.AnswerMessageKey > 0,
                        match.UserMessageKey > 0 ? match.UserMessageKey : match.AnswerMessageKey,
                        null);
                }
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
            {
                Trace.TraceWarning("[ChatStore] Searching chat text failed: {0}", ex.Message);
            }
        }

        foreach (var (chatId, message) in SnapshotUnstoredMessages())
        {
            if (ChatSearchText.Of(message) is not { } messageText || !ChatSearchText.Contains(messageText, text))
                continue;

            var isUser = message.Role == "user";
            hits.TryGetValue(chatId, out var hit);
            var merged = hit with { InUserMessage = hit.InUserMessage || isUser, InAnswer = hit.InAnswer || !isUser };
            // The user's words are the ones to quote when there are any, as they weigh more.
            if (isUser || hit is { MessageKey: 0, Text: null })
                merged = merged with { MessageKey = 0, Text = messageText };
            hits[chatId] = merged;
        }

        return hits;
    }

    /// <summary>The text of the message a search hit points at, or null when it is gone.</summary>
    public string? ReadHitText(ChatContentHit hit)
    {
        if (hit.Text is not null)
            return hit.Text;
        if (_chatStore is null || hit.MessageKey <= 0)
            return null;

        try
        {
            return _chatStore.ReadSearchText(hit.MessageKey);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
        {
            Trace.TraceWarning("[ChatStore] Reading a search hit failed: {0}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Messages the chat database does not hold as they are: new messages and unsaved edits in
    /// memory (every message, for a store without a database). Copies, as search runs off the UI thread.
    /// </summary>
    private List<(Guid ChatId, ChatMessage Message)> SnapshotUnstoredMessages()
    {
        var messages = new List<(Guid, ChatMessage)>();
        foreach (var chat in SnapshotChatsByRecency())
        {
            // A chat whose transcript is not loaded holds nothing in memory.
            if (chat.Messages.Count == 0)
                continue;
            foreach (var message in chat.Messages.ToArray())
            {
                if (message.StorageSeq <= 0 || message.HasUnpersistedChanges)
                    messages.Add((chat.Id, message));
            }
        }

        return messages;
    }

    /// <summary>
    /// A monotonic marker of a chat's stored content, or null when nothing is stored. Changes whenever
    /// the chat's transcript is written, so views of it can tell when to refresh.
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

    // ── Deleting ────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes a chat's stored transcript and row, and its legacy copy from before the chat
    /// database. Its transcript state stays as a tombstone, so a late save cannot bring it back.
    /// </summary>
    public async Task DeleteChatFileAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        MarkTranscriptDeleted(GetTranscriptState(chatId));
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
}
