using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.ViewModels;

namespace Lumi.Services.Remote;

/// <summary>
/// Fans live desktop state out to connected phones over Server-Sent Events.
/// </summary>
/// <remarks>
/// SSE rather than WebSockets on purpose: <see cref="HttpListener"/>'s WebSocket support is
/// Windows-only, and Lumi ships on Linux and macOS too. SSE is a plain chunked HTTP response, so
/// the same server code works on every desktop platform and every mobile client.
///
/// The hub only ever pushes small notifications — status, a coalesced "this transcript changed"
/// tick, and hot streaming text. Bulk data is pulled over the regular request endpoints. That keeps
/// a dropped or slow phone from ever back-pressuring the UI thread.
/// </remarks>
internal sealed class RemoteEventHub : IDisposable
{
    /// <summary>Matches the desktop's own streaming UI throttle so phones feel the same cadence.</summary>
    private static readonly TimeSpan CoalesceInterval = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Phones keep a background stream for at most a minute so a short app switch resumes without a
    /// reconnect. Nothing is written to background streams, so one still open after this long was
    /// abandoned (app killed or frozen, network gone) and would otherwise hold a connection slot.
    /// </summary>
    internal static readonly TimeSpan BackgroundStreamLimit = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<Guid, RemoteEventClient> _clients = new();
    private readonly DataStore _dataStore;
    private readonly MainViewModel _main;
    private readonly Func<IReadOnlyList<string>> _modelsProvider;
    private readonly string? _revisionEpoch;
    private readonly TimeProvider _time;
    private readonly DispatcherTimer _coalesceTimer;
    private readonly ITimer _keepAliveTimer;
    private readonly Dictionary<ChatViewModel, SurfaceObserver> _surfaceObservers =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Guid> _statusDirtyChatIds = [];
    private readonly HashSet<Guid> _transcriptDirtyChatIds = [];
    private readonly HashSet<Guid> _deletedChatIds = [];
    private readonly HashSet<Guid> _chatStateDirtyIds = [];
    private readonly Dictionary<Guid, ChatRowState> _chatRowStates = [];
    private readonly ConcurrentDictionary<Guid, long> _chatVersions = new();

    private bool _chatsDirty;
    private bool _libraryDirty;
    private bool _snapshotDirty;
    private string? _lastLibraryJson;
    // Row hashes of the last whole first page sent to phones without row patches.
    private IReadOnlyDictionary<Guid, ulong>? _lastFullPageRows;
    private long _revision;
    private bool _disposed;

    public RemoteEventHub(
        DataStore dataStore,
        MainViewModel main,
        Func<IReadOnlyList<string>> modelsProvider,
        string? revisionEpoch = null,
        TimeProvider? time = null)
    {
        _dataStore = dataStore;
        _main = main;
        _modelsProvider = modelsProvider;
        _revisionEpoch = revisionEpoch;
        _time = time ?? TimeProvider.System;

        _coalesceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = CoalesceInterval };
        _coalesceTimer.Tick += (_, _) => FlushPending();

        _keepAliveTimer = _time.CreateTimer(
            _ => SweepKeepAlive(),
            null,
            KeepAliveInterval,
            KeepAliveInterval);

        Dispatcher.UIThread.Post(Attach);
    }

    /// <summary>Monotonic transcript revision so clients can drop stale renders.</summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>
    /// Bumped whenever this chat's transcript projection may have changed (message add/remove/update,
    /// rebuild or save). Together with a fingerprint of in-memory messages it versions transcripts.
    /// </summary>
    internal long GetChatVersion(Guid chatId) =>
        _chatVersions.TryGetValue(chatId, out var version) ? version : 0;

    public int ClientCount => _clients.Count;

    // ── Client registry ─────────────────────────────────────────────────────────────────────

    public RemoteEventClient AddClient(
        Stream stream,
        string deviceId,
        RemoteEventFrame? initialFrame = null,
        RemoteEventSubscription? subscription = null)
    {
        var client = new RemoteEventClient(
            stream,
            deviceId,
            subscription ?? new RemoteEventSubscription(),
            _time);
        // Bootstrap is never coalesced with later partial catalog snapshots. The first frame defines
        // the client's complete cache and must be delivered exactly once.
        if (initialFrame is { } frame && !client.Enqueue(frame))
        {
            client.Dispose();
            throw new InvalidDataException("The initial remote snapshot exceeds the event queue limit.");
        }
        _clients[client.Id] = client;
        Dispatcher.UIThread.Post(ReconcileMessageObservers);
        return client;
    }

    public void RemoveClient(RemoteEventClient client)
    {
        _clients.TryRemove(client.Id, out _);
        client.Dispose();
        Dispatcher.UIThread.Post(ReconcileMessageObservers);
    }

    /// <summary>
    /// Pings foreground phones so a dead link is found by its failed write, and closes background
    /// streams held past <see cref="BackgroundStreamLimit"/>, which are never written to.
    /// </summary>
    internal void SweepKeepAlive()
    {
        var ping = new RemoteEventFrame(RemoteProtocol.Events.Ping, "{}");
        foreach (var client in _clients.Values)
        {
            if (client.IsForeground
                    ? !client.Enqueue(ping, RemoteProtocol.Events.Ping)
                    : client.IsAbandonedInBackground(BackgroundStreamLimit))
            {
                RemoveClient(client);
            }
        }
    }

    public async Task UpdateSubscriptionAsync(
        string deviceId,
        RemoteEventSubscription subscription)
    {
        var changes = new List<SubscriptionChange>();
        foreach (var client in _clients.Values)
        {
            if (string.Equals(client.DeviceId, deviceId, StringComparison.Ordinal)
                && client.TryUpdateSubscription(subscription, out var previous, out var current))
            {
                changes.Add(new SubscriptionChange(client, previous, current));
            }
        }

        if (changes.Count == 0)
            return;

        // A phone coming back to the foreground on its existing stream needs proof the stream is
        // still alive; a ping is the cheapest frame that provides it.
        foreach (var change in changes)
        {
            if (!change.Previous.IsForeground
                && change.Current.IsForeground
                && !change.Client.Enqueue(
                    new RemoteEventFrame(RemoteProtocol.Events.Ping, "{}"),
                    RemoteProtocol.Events.Ping))
            {
                RemoveClient(change.Client);
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (changes.Any(static change =>
                    ObservedChatId(change.Previous) != ObservedChatId(change.Current)))
            {
                ReconcileMessageObservers();
            }

            foreach (var change in changes)
            {
                if (!change.Previous.IsForeground && change.Current.IsForeground)
                    CatchUpReturningClient(change.Client);
                PrimeNewChatScope(change);
            }
        });
    }

    private void PrimeNewChatScope(SubscriptionChange change)
    {
        var previousChatId = ObservedChatId(change.Previous);
        var currentChatId = ObservedChatId(change.Current);
        if (currentChatId is not { } chatId || currentChatId == previousChatId)
            return;

        if (_dataStore.Data.Chats.FirstOrDefault(chat => chat.Id == chatId) is { } chat
            && !change.Client.EnqueueStatus(BuildStatusFrames(chat, includeSlim: false)))
        {
            RemoveClient(change.Client);
        }
    }

    /// <summary>
    /// Serializes one status for every phone. Compact-status phones that already hold the same plan
    /// and composer catalogs receive the slim frame instead.
    /// </summary>
    private RemoteStatusFrames BuildStatusFrames(Chat chat, bool includeSlim)
    {
        var owner = RemoteProjector.ResolveChatOwner(_main, chat.Id);
        var status = RemoteProjector.BuildStatus(_dataStore, owner ?? _main.ChatVM, chat);
        var full = StatusFrameBytes(status);

        var catalogs = new RemoteContentHash();
        catalogs.Add(status.HasComposerCatalogs);
        foreach (var chips in new[]
                 {
                     status.AvailableAgents, status.AvailableSkills,
                     status.AvailableMcps, status.AvailableProjects
                 })
        {
            catalogs.Add(chips.Count);
            foreach (var chip in chips)
            {
                catalogs.Add(chip.Name);
                catalogs.Add(chip.Glyph);
                catalogs.Add(chip.Description);
                catalogs.Add(chip.Value);
            }
        }

        var plan = new RemoteContentHash();
        plan.Add(status.PlanContent);

        byte[]? slim = null;
        if (includeSlim)
        {
            status.PlanContent = null;
            status.IsPlanOmitted = true;
            status.HasComposerCatalogs = false;
            status.AvailableAgents = [];
            status.AvailableSkills = [];
            status.AvailableMcps = [];
            status.AvailableProjects = [];
            slim = StatusFrameBytes(status);
        }

        return new RemoteStatusFrames(
            chat.Id,
            full,
            slim,
            catalogs.Value,
            plan.Value,
            new ChatListActivity(
                status.IsBusy || status.IsStreaming,
                status.IsSessionActive || status.IsBusy || status.IsStreaming,
                status.HasUnreadMessages));

        static byte[] StatusFrameBytes(RemoteChatStatus value) =>
            Encoding.UTF8.GetBytes(new RemoteEventFrame(
                RemoteProtocol.Events.ChatStatus,
                JsonSerializer.Serialize(value, RemoteJsonContext.Default.RemoteChatStatus)).ToWire());
    }

    /// <summary>
    /// Hashes each first-page row together with its group, so a regrouped row counts as changed. A
    /// running chat's activity time, preview and message count move at every agent step; they count
    /// once the run ends, so a busy chat does not wake every phone that shows the chat list.
    /// </summary>
    internal static RemoteChatPageFrames BuildChatPageFrames(RemoteChatPage page, RemoteEventFrame fullFrame)
    {
        var rows = new Dictionary<Guid, ulong>();
        foreach (var group in page.Groups)
        {
            foreach (var chat in group.Chats)
            {
                var hash = new RemoteContentHash();
                hash.Add(group.Label);
                hash.Add(JsonSerializer.SerializeToUtf8Bytes(
                    chat.IsRunning ? WithoutStepActivity(chat) : chat,
                    RemoteJsonContext.Default.RemoteChat));
                rows[chat.Id] = hash.Value;
            }
        }

        return new RemoteChatPageFrames(page, rows, Encoding.UTF8.GetBytes(fullFrame.ToWire()));
    }

    /// <summary>A running row without the fields every agent step moves. Copies every other field.</summary>
    private static RemoteChat WithoutStepActivity(RemoteChat chat) => new()
    {
        Id = chat.Id,
        Title = chat.Title,
        ProjectId = chat.ProjectId,
        ProjectName = chat.ProjectName,
        AgentId = chat.AgentId,
        AgentName = chat.AgentName,
        AgentGlyph = chat.AgentGlyph,
        IsPinned = chat.IsPinned,
        IsRunning = chat.IsRunning,
        IsSessionActive = chat.IsSessionActive,
        HasUnreadMessages = chat.HasUnreadMessages,
        LastModelUsed = chat.LastModelUsed
    };

    private static bool HaveSameRows(
        IReadOnlyDictionary<Guid, ulong> rows,
        IReadOnlyDictionary<Guid, ulong>? previous) =>
        previous is not null
        && rows.Count == previous.Count
        && rows.All(row => previous.TryGetValue(row.Key, out var hash) && hash == row.Value);

    private static Guid? ObservedChatId(RemoteEventSubscription subscription) =>
        subscription.IsForeground ? subscription.ChatId : null;

    public void Broadcast(
        RemoteEventFrame frame,
        string? coalesceKey = null,
        Func<RemoteEventClient, bool>? predicate = null)
    {
        if (_clients.IsEmpty)
            return;

        foreach (var client in _clients.Values)
        {
            if (predicate is not null && !predicate(client))
                continue;
            if (!client.Enqueue(frame, coalesceKey))
                RemoveClient(client);
        }
    }

    public void BroadcastJson<T>(
        string eventName,
        T payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        string? coalesceKey = null,
        Func<RemoteEventClient, bool>? predicate = null)
    {
        if (_clients.IsEmpty)
            return;

        Broadcast(
            new RemoteEventFrame(eventName, JsonSerializer.Serialize(payload, typeInfo)),
            coalesceKey,
            predicate);
    }

    internal bool HasChatSubscriber(Guid chatId) =>
        _clients.Values.Any(client => client.WantsChat(chatId));

    internal bool HasChatListSubscriber() =>
        _clients.Values.Any(static client => client.WantsChatList);

    internal bool HasLibrarySubscriber() =>
        _clients.Values.Any(static client => client.WantsLibrary);

    // ── Desktop state subscriptions ─────────────────────────────────────────────────────────

    private void Attach()
    {
        if (_disposed)
            return;

        _main.PropertyChanged += OnMainPropertyChanged;
        _main.ChatGroups.CollectionChanged += OnChatGroupsChanged;
        _main.ChatDeleted += OnChatDeleted;
        _main.ChatActivityOrReadStateChanged += OnChatActivityOrReadStateChanged;
        _dataStore.ChatContentChanged += OnChatContentChanged;
        _dataStore.IndexSaved += OnLibraryChanged;

        ReconcileSurfaces();

        _coalesceTimer.Start();
    }

    private void Detach()
    {
        _main.PropertyChanged -= OnMainPropertyChanged;
        _main.ChatGroups.CollectionChanged -= OnChatGroupsChanged;
        _main.ChatDeleted -= OnChatDeleted;
        _main.ChatActivityOrReadStateChanged -= OnChatActivityOrReadStateChanged;
        _dataStore.ChatContentChanged -= OnChatContentChanged;
        _dataStore.IndexSaved -= OnLibraryChanged;

        foreach (var observer in _surfaceObservers.Values.ToList())
            DetachSurface(observer);

        _coalesceTimer.Stop();
    }

    /// <summary>
    /// Tracks every registered chat surface, including detached windows. The registry deliberately
    /// has no event stream, so the existing 100 ms coalescing tick also reconciles additions/removals.
    /// </summary>
    private void ReconcileSurfaces()
    {
        var active = new HashSet<ChatViewModel>(
            _main.ChatSurfaceRegistry.SnapshotSurfaces(),
            ReferenceEqualityComparer.Instance);
        foreach (var observer in _surfaceObservers.Values
                     .Where(observer => !active.Contains(observer.Surface))
                     .ToList())
        {
            DetachSurface(observer);
        }

        foreach (var surface in active)
        {
            if (!_surfaceObservers.ContainsKey(surface))
                AttachSurface(surface);
        }
    }

    private void AttachSurface(ChatViewModel surface)
    {
        var observer = new SurfaceObserver(this, surface);
        _surfaceObservers.Add(surface, observer);

        surface.PropertyChanged += observer.PropertyChanged;
        surface.Messages.CollectionChanged += observer.MessagesChanged;
        surface.ActiveSkillChips.CollectionChanged += observer.ComposerChipsChanged;
        surface.ActiveMcpChips.CollectionChanged += observer.ComposerChipsChanged;
        surface.AvailableAgentChips.CollectionChanged += observer.ComposerChipsChanged;
        surface.AvailableSkillChips.CollectionChanged += observer.ComposerChipsChanged;
        surface.AvailableMcpChips.CollectionChanged += observer.ComposerChipsChanged;
        surface.AvailableProjectChips.CollectionChanged += observer.ComposerChipsChanged;
        surface.ChatUpdated += observer.ChatUpdated;
        surface.TranscriptRebuilt += observer.TranscriptRebuilt;
        surface.FeatureManagementStateChanged += observer.LibraryChanged;

        SynchronizeObservedMessages(observer);
    }

    private void DetachSurface(SurfaceObserver observer)
    {
        if (!_surfaceObservers.Remove(observer.Surface))
            return;

        var surface = observer.Surface;
        surface.PropertyChanged -= observer.PropertyChanged;
        surface.Messages.CollectionChanged -= observer.MessagesChanged;
        surface.ActiveSkillChips.CollectionChanged -= observer.ComposerChipsChanged;
        surface.ActiveMcpChips.CollectionChanged -= observer.ComposerChipsChanged;
        surface.AvailableAgentChips.CollectionChanged -= observer.ComposerChipsChanged;
        surface.AvailableSkillChips.CollectionChanged -= observer.ComposerChipsChanged;
        surface.AvailableMcpChips.CollectionChanged -= observer.ComposerChipsChanged;
        surface.AvailableProjectChips.CollectionChanged -= observer.ComposerChipsChanged;
        surface.ChatUpdated -= observer.ChatUpdated;
        surface.TranscriptRebuilt -= observer.TranscriptRebuilt;
        surface.FeatureManagementStateChanged -= observer.LibraryChanged;

        foreach (var message in observer.Messages)
            message.PropertyChanged -= observer.MessagePropertyChanged;
        observer.Messages.Clear();
    }

    private static void ObserveMessage(SurfaceObserver observer, ChatMessageViewModel message)
    {
        if (observer.Messages.Add(message))
            message.PropertyChanged += observer.MessagePropertyChanged;
    }

    private void SynchronizeObservedMessages(SurfaceObserver observer)
    {
        if (observer.CurrentChatId is not { } chatId || !HasChatSubscriber(chatId))
        {
            foreach (var message in observer.Messages.ToArray())
            {
                message.PropertyChanged -= observer.MessagePropertyChanged;
                observer.Messages.Remove(message);
            }
            return;
        }

        var activeMessages = new HashSet<ChatMessageViewModel>(
            observer.Surface.Messages,
            ReferenceEqualityComparer.Instance);
        foreach (var message in observer.Messages.Where(message => !activeMessages.Contains(message)).ToArray())
        {
            message.PropertyChanged -= observer.MessagePropertyChanged;
            observer.Messages.Remove(message);
        }

        foreach (var message in activeMessages)
            ObserveMessage(observer, message);
    }

    private void ReconcileMessageObservers()
    {
        if (_disposed)
            return;

        foreach (var observer in _surfaceObservers.Values)
            SynchronizeObservedMessages(observer);
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsConnected):
            case nameof(MainViewModel.ConnectionStatus):
                BroadcastConnection();
                break;
            case nameof(MainViewModel.ActiveChatId):
                MarkChatsDirty();
                break;
            case nameof(MainViewModel.ChatVM):
                ReconcileSurfaces();
                MarkChatsDirty();
                MarkStatusDirty(_main.ChatVM.CurrentChat?.Id);
                MarkTranscriptDirty(_main.ChatVM.CurrentChat?.Id);
                break;
        }
    }

    private void OnChatGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e) => MarkChatsDirty();

    private void OnChatActivityOrReadStateChanged(Guid chatId)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnChatActivityOrReadStateChanged(chatId));
            return;
        }

        if (HasChatListSubscriber())
        {
            _chatStateDirtyIds.Add(chatId);
            MarkChatsDirty();
        }
        MarkStatusDirty(chatId);
    }

    private void OnChatDeleted(Guid chatId)
    {
        if (!HasChatListSubscriber())
            return;

        _deletedChatIds.Add(chatId);
        MarkChatsDirty();
    }

    /// <summary>Active skills / MCP servers are collections, so they never raise PropertyChanged.</summary>
    private void OnComposerChipsChanged(SurfaceObserver observer) =>
        MarkStatusDirty(observer.Surface.CurrentChat?.Id);

    private void OnChatPropertyChanged(SurfaceObserver observer, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChatViewModel.IsBusy):
            case nameof(ChatViewModel.IsAwaitingInput):
            case nameof(ChatViewModel.IsSessionActive):
                MarkChatsDirty();
                MarkStatusDirty(observer.Surface.CurrentChat?.Id);
                break;
            case nameof(ChatViewModel.IsStreaming):
            case nameof(ChatViewModel.StatusText):
            case nameof(ChatViewModel.SelectedModel):
            case nameof(ChatViewModel.ContextCurrentTokens):
            case nameof(ChatViewModel.ContextTokenLimit):
            case nameof(ChatViewModel.PlanContent):
            case nameof(ChatViewModel.SuggestionA):
            case nameof(ChatViewModel.SuggestionB):
            case nameof(ChatViewModel.SuggestionC):
            // Composer configuration. Without these the phone's pickers would show whatever was
            // true when it last opened the chat and never notice a change made on the PC. The
            // quality and context-window tiers in particular have no other trigger: changing them
            // on the desktop touches neither the transcript nor the chat list.
            case nameof(ChatViewModel.SelectedQuality):
            case nameof(ChatViewModel.QualityLevels):
            case nameof(ChatViewModel.SelectedContextWindowTier):
            case nameof(ChatViewModel.ContextWindowTiers):
            case nameof(ChatViewModel.SelectedAgentName):
            case nameof(ChatViewModel.SelectedAgentGlyph):
            case nameof(ChatViewModel.SelectedProjectName):
                MarkStatusDirty(observer.Surface.CurrentChat?.Id);
                break;
            case nameof(ChatViewModel.ModelCatalogVersion):
                _snapshotDirty = true;
                break;
            case nameof(ChatViewModel.CurrentChat):
            {
                var previousChatId = observer.CurrentChatId;
                observer.CurrentChatId = observer.Surface.CurrentChat?.Id;
                MarkChatsDirty();
                MarkStatusDirty(previousChatId);
                MarkStatusDirty(observer.CurrentChatId);
                MarkTranscriptDirty(previousChatId);
                MarkTranscriptDirty(observer.CurrentChatId);
                SynchronizeObservedMessages(observer);
                break;
            }
        }
    }

    private void OnMessagesChanged(SurfaceObserver observer)
    {
        SynchronizeObservedMessages(observer);
        MarkTranscriptDirty(observer.Surface.CurrentChat?.Id);
    }

    private void OnMessagePropertyChanged(
        SurfaceObserver observer,
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (sender is not ChatMessageViewModel message)
            return;

        var chatId = observer.Surface.CurrentChat?.Id ?? observer.CurrentChatId;
        var hasChatSubscriber = chatId is { } observedChatId && HasChatSubscriber(observedChatId);

        // Streaming assistant/reasoning text goes out on the hot path without pulling the whole
        // transcript. Each phone keeps its own slot: the frame is built when that phone's writer
        // reaches it, from the text the phone already received, so a backlog merges instead of
        // dropping a chunk.
        if (e.PropertyName == nameof(ChatMessageViewModel.Content)
            && message.IsStreaming
            && hasChatSubscriber
            && chatId is { } streamingChatId)
        {
            var isReasoning = message.Message.Role == "reasoning";
            if (isReasoning && !_dataStore.Data.Settings.ShowReasoning)
                return;

            var bounded = RemoteProtocol.TruncateForMobile(
                              message.Content,
                              isReasoning
                                  ? RemoteProtocol.MobileReasoningTextLimit
                                  : RemoteProtocol.MobileAssistantTextLimit)
                          ?? "";
            var messageId = message.Message.Id;
            foreach (var client in _clients.Values)
            {
                if (!client.WantsChat(streamingChatId)
                    || (isReasoning && client.WantsCompactTranscript))
                {
                    continue;
                }

                if (!client.EnqueueStreamText(streamingChatId, messageId, bounded, isReasoning))
                    RemoveClient(client);
            }

            return;
        }

        MarkTranscriptDirty(chatId);
    }

    private void OnChatUpdated(SurfaceObserver observer)
    {
        MarkChatListDirtyIfChanged(observer.Surface.CurrentChat?.Id);
        MarkStatusDirty(observer.Surface.CurrentChat?.Id);
    }

    private void OnTranscriptRebuilt(SurfaceObserver observer) =>
        MarkTranscriptDirty(observer.Surface.CurrentChat?.Id);

    private void OnLibraryChanged() => MarkLibraryDirty();

    /// <summary>
    /// Queues a library broadcast. Three unrelated paths can change the library and none of them knows
    /// about the others: the agent raises <c>FeatureManagementStateChanged</c>, a phone edit goes
    /// straight through <see cref="RemoteCommandRouter"/>, and a desktop CRUD page just mutates
    /// <c>AppData</c> and saves. All three are funnelled here, so the phone never shows a stale list.
    /// </summary>
    internal void MarkLibraryDirty()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(MarkLibraryDirty);
            return;
        }

        _libraryDirty = true;
        // A backgrounded phone's new-chat setups and pickers depend on the library too.
        MarkCatalogsMissedInBackground();
        if (_clients.Values.Any(static client => client.WantsNewChatExperience))
            _snapshotDirty = true;
    }

    /// <summary>Phones backgrounded on a kept stream receive the catalogs when they return.</summary>
    private void MarkCatalogsMissedInBackground()
    {
        foreach (var client in _clients.Values)
        {
            if (!client.IsForeground)
                client.MarkCatalogsMissed();
        }
    }

    private void OnChatContentChanged(Guid chatId)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnChatContentChanged(chatId));
            return;
        }

        MarkChatListDirtyIfChanged(chatId);
        MarkTranscriptDirty(chatId);
    }

    private void MarkChatListDirtyIfChanged(Guid? chatId)
    {
        if (!HasChatListSubscriber())
            return;

        if (chatId is not { } id)
            return;

        var chat = _dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == id);
        if (chat is null)
        {
            _chatRowStates.Remove(id);
            MarkChatsDirty();
            return;
        }

        var state = BuildChatRowState(chat);
        if (!_chatRowStates.TryGetValue(id, out var previous) || previous != state)
        {
            _chatRowStates[id] = state;
            MarkChatsDirty();
        }
    }

    private void MarkChatsDirty()
    {
        if (HasChatListSubscriber())
            _chatsDirty = true;
    }

    private static ChatRowState BuildChatRowState(Chat chat) => new(
        chat.Title,
        chat.UpdatedAt,
        chat.ProjectId,
        chat.AgentId,
        chat.IsPinned,
        chat.HasUnreadMessages,
        chat.LastModelUsed);

    private void MarkStatusDirty(Guid? chatId)
    {
        if (chatId is { } id && HasChatSubscriber(id))
            _statusDirtyChatIds.Add(id);
    }

    private void MarkTranscriptDirty(Guid? chatId)
    {
        Interlocked.Increment(ref _revision);
        if (chatId is not { } id)
            return;

        _chatVersions.AddOrUpdate(id, 1, static (_, version) => version + 1);
        if (HasChatSubscriber(id))
            _transcriptDirtyChatIds.Add(id);
    }

    // ── Coalesced flush ─────────────────────────────────────────────────────────────────────

    private void FlushPending()
    {
        ReconcileSurfaces();

        if (_clients.IsEmpty)
        {
            _chatsDirty = _libraryDirty = _snapshotDirty = false;
            _statusDirtyChatIds.Clear();
            _transcriptDirtyChatIds.Clear();
            _deletedChatIds.Clear();
            _chatStateDirtyIds.Clear();
            return;
        }

        if (_chatsDirty)
        {
            _chatsDirty = false;
            if (HasChatListSubscriber())
            {
                var page = RemoteProjector.BuildChatPage(
                    _dataStore,
                    _main,
                    offset: 0,
                    limit: RemoteProtocol.ChatPageSize,
                    query: null,
                    projectId: null);
                foreach (var chat in _dataStore.Data.Chats)
                    _chatRowStates[chat.Id] = BuildChatRowState(chat);
                page.RemovedChatIds = [.. _deletedChatIds];
                var pageJson = JsonSerializer.Serialize(page, RemoteJsonContext.Default.RemoteChatPage);
                var fullFrame = new RemoteEventFrame(RemoteProtocol.Events.Chats, pageJson);
                var rowFrames = BuildChatPageFrames(page, fullFrame);
                // Older phones re-sort their whole list on every page, so a page whose rows only
                // moved through agent steps is not sent to them at all.
                var fullPageChanged = page.RemovedChatIds.Count > 0
                                      || !HaveSameRows(rowFrames.RowHashes, _lastFullPageRows);
                var sentFullPage = false;
                foreach (var client in _clients.Values)
                {
                    if (!client.WantsChatList)
                        continue;

                    // Row-patch phones merge pushes, so they receive only rows that changed since
                    // their own previous page; older phones receive the whole first page.
                    bool queued;
                    if (client.WantsChatRowPatches)
                    {
                        queued = client.EnqueueChatPage(rowFrames);
                    }
                    else if (fullPageChanged)
                    {
                        queued = client.Enqueue(fullFrame, RemoteProtocol.Events.Chats);
                        sentFullPage = true;
                    }
                    else
                    {
                        continue;
                    }

                    if (!queued)
                        RemoveClient(client);
                }

                if (sentFullPage)
                    _lastFullPageRows = rowFrames.RowHashes;
            }
            _deletedChatIds.Clear();
        }

        if (_snapshotDirty)
        {
            _snapshotDirty = false;
            // Marked first: a phone that returns during this flush is either marked or included.
            MarkCatalogsMissedInBackground();
            if (_clients.Values.Any(static client => client.IsForeground))
                Broadcast(CatalogFrame(), RemoteProtocol.Events.Snapshot, static client => client.IsForeground);
        }

        if (_statusDirtyChatIds.Count > 0 || _chatStateDirtyIds.Count > 0)
        {
            var dirtyChatIds = _statusDirtyChatIds.Union(_chatStateDirtyIds).ToArray();
            _statusDirtyChatIds.Clear();
            foreach (var chatId in dirtyChatIds)
            {
                // A first-page list patch cannot update an older row the phone has already paged in.
                // Reuse per-chat coalescing so these read/running transitions are not lost when a
                // slower client replaces its pending first-page patch.
                var includeChatList = _chatStateDirtyIds.Contains(chatId);
                if (!HasChatSubscriber(chatId) && !(includeChatList && HasChatListSubscriber()))
                    continue;

                var chat = _dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == chatId);
                if (chat is null)
                    continue;

                bool Wants(RemoteEventClient client) =>
                    client.WantsChat(chatId) || (includeChatList && client.WantsChatList);
                var frames = BuildStatusFrames(
                    chat,
                    includeSlim: _clients.Values.Any(client => client.WantsCompactStatus && Wants(client)));
                foreach (var client in _clients.Values)
                {
                    // A phone that shows only the list hears about this chat when its row's running
                    // or unread state changes; the running flag that flickers at every agent step does not.
                    if (Wants(client)
                        && !client.EnqueueStatus(frames, listOnly: !client.WantsChat(chatId)))
                    {
                        RemoveClient(client);
                    }
                }
            }
            _chatStateDirtyIds.Clear();
        }

        if (_transcriptDirtyChatIds.Count > 0)
        {
            var dirtyChatIds = _transcriptDirtyChatIds.ToArray();
            _transcriptDirtyChatIds.Clear();
            foreach (var chatId in dirtyChatIds)
            {
                if (!HasChatSubscriber(chatId))
                    continue;

                BroadcastJson(
                    RemoteProtocol.Events.TranscriptInvalidated,
                    new RemoteTranscriptInvalidated
                    {
                        ChatId = chatId,
                        RevisionEpoch = _revisionEpoch,
                        Revision = Revision
                    },
                    RemoteJsonContext.Default.RemoteTranscriptInvalidated,
                    $"transcript:{chatId:N}",
                    client => client.WantsChat(chatId));
            }
        }

        if (_libraryDirty)
        {
            _libraryDirty = false;
            if (!HasLibrarySubscriber())
            {
                _lastLibraryJson = null;
                return;
            }

            var libraryJson = JsonSerializer.Serialize(
                RemoteProjector.BuildLibrary(_dataStore),
                RemoteJsonContext.Default.RemoteLibrary);
            if (!string.Equals(libraryJson, _lastLibraryJson, StringComparison.Ordinal))
            {
                _lastLibraryJson = libraryJson;
                Broadcast(
                    new RemoteEventFrame(RemoteProtocol.Events.Library, libraryJson),
                    RemoteProtocol.Events.Library,
                    static client => client.WantsLibrary);
            }
        }
    }

    private void BroadcastConnection()
    {
        if (!_clients.IsEmpty)
            Broadcast(ConnectionFrame(), RemoteProtocol.Events.Connection, static client => client.IsForeground);
    }

    private RemoteEventFrame ConnectionFrame() => new(
        RemoteProtocol.Events.Connection,
        JsonSerializer.Serialize(
            new RemoteConnectionStatus
            {
                IsConnected = _main.IsConnected,
                Status = _main.ConnectionStatus
            },
            RemoteJsonContext.Default.RemoteConnectionStatus));

    /// <summary>The partial snapshot carrying model and composer catalogs and the new-chat setup.</summary>
    private RemoteEventFrame CatalogFrame()
    {
        var catalogs = RemoteProjector.BuildSnapshot(
            _dataStore,
            _main,
            _modelsProvider(),
            includeChatList: false,
            includeLibrary: false,
            isPartial: true);
        RemoteContentVersions.ApplySectionVersions(catalogs);
        return new RemoteEventFrame(
            RemoteProtocol.Events.Snapshot,
            JsonSerializer.Serialize(catalogs, RemoteJsonContext.Default.RemoteSnapshot));
    }

    /// <summary>
    /// A phone returning on its kept stream missed what is pushed to foreground phones only: the
    /// desktop's connection state and, if they changed meanwhile, the catalogs.
    /// </summary>
    private void CatchUpReturningClient(RemoteEventClient client)
    {
        var queued = client.Enqueue(ConnectionFrame(), RemoteProtocol.Events.Connection)
                     && (!client.TakeMissedCatalogs()
                         || client.Enqueue(CatalogFrame(), RemoteProtocol.Events.Snapshot));
        if (!queued)
            RemoveClient(client);
    }

    private sealed class SurfaceObserver
    {
        public SurfaceObserver(RemoteEventHub owner, ChatViewModel surface)
        {
            Surface = surface;
            CurrentChatId = surface.CurrentChat?.Id;
            PropertyChanged = (_, args) => owner.OnChatPropertyChanged(this, args);
            MessagesChanged = (_, _) => owner.OnMessagesChanged(this);
            ComposerChipsChanged = (_, _) => owner.OnComposerChipsChanged(this);
            MessagePropertyChanged = (sender, args) =>
                owner.OnMessagePropertyChanged(this, sender, args);
            ChatUpdated = () => owner.OnChatUpdated(this);
            TranscriptRebuilt = () => owner.OnTranscriptRebuilt(this);
            LibraryChanged = owner.OnLibraryChanged;
        }

        public ChatViewModel Surface { get; }
        public Guid? CurrentChatId { get; set; }
        public HashSet<ChatMessageViewModel> Messages { get; } =
            new(ReferenceEqualityComparer.Instance);
        public PropertyChangedEventHandler PropertyChanged { get; }
        public NotifyCollectionChangedEventHandler MessagesChanged { get; }
        public NotifyCollectionChangedEventHandler ComposerChipsChanged { get; }
        public PropertyChangedEventHandler MessagePropertyChanged { get; }
        public Action ChatUpdated { get; }
        public Action TranscriptRebuilt { get; }
        public Action LibraryChanged { get; }
    }

    private readonly record struct ChatRowState(
        string Title,
        DateTimeOffset UpdatedAt,
        Guid? ProjectId,
        Guid? AgentId,
        bool IsPinned,
        bool HasUnreadMessages,
        string? LastModelUsed);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _keepAliveTimer.Dispose();

        if (Dispatcher.UIThread.CheckAccess())
            Detach();
        else
            Dispatcher.UIThread.Post(Detach);

        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
    }

    private readonly record struct SubscriptionChange(
        RemoteEventClient Client,
        RemoteEventSubscription Previous,
        RemoteEventSubscription Current);
}
