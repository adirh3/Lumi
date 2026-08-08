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

    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<Guid, RemoteEventClient> _clients = new();
    private readonly DataStore _dataStore;
    private readonly MainViewModel _main;
    private readonly Func<IReadOnlyList<string>> _modelsProvider;
    private readonly string? _revisionEpoch;
    private readonly DispatcherTimer _coalesceTimer;
    private readonly Timer _keepAliveTimer;
    private readonly Dictionary<ChatViewModel, SurfaceObserver> _surfaceObservers =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Guid> _statusDirtyChatIds = [];
    private readonly HashSet<Guid> _transcriptDirtyChatIds = [];
    private readonly HashSet<Guid> _deletedChatIds = [];
    private readonly Dictionary<Guid, ChatRowState> _chatRowStates = [];

    private bool _chatsDirty;
    private bool _libraryDirty;
    private bool _snapshotDirty;
    private string? _lastLibraryJson;
    private long _revision;
    private bool _disposed;

    public RemoteEventHub(
        DataStore dataStore,
        MainViewModel main,
        Func<IReadOnlyList<string>> modelsProvider,
        string? revisionEpoch = null)
    {
        _dataStore = dataStore;
        _main = main;
        _modelsProvider = modelsProvider;
        _revisionEpoch = revisionEpoch;

        _coalesceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = CoalesceInterval };
        _coalesceTimer.Tick += (_, _) => FlushPending();

        _keepAliveTimer = new Timer(_ => Broadcast(
                new RemoteEventFrame(RemoteProtocol.Events.Ping, "{}"),
                RemoteProtocol.Events.Ping),
            null, KeepAliveInterval, KeepAliveInterval);

        Dispatcher.UIThread.Post(Attach);
    }

    /// <summary>Monotonic transcript revision so clients can drop stale renders.</summary>
    public long Revision => Interlocked.Read(ref _revision);

    public int ClientCount => _clients.Count;

    // ── Client registry ─────────────────────────────────────────────────────────────────────

    public RemoteEventClient AddClient(
        Stream stream,
        string deviceId,
        RemoteEventFrame? initialFrame = null)
    {
        var client = new RemoteEventClient(stream, deviceId);
        if (initialFrame is { } frame && !client.Enqueue(frame, RemoteProtocol.Events.Snapshot))
        {
            client.Dispose();
            throw new InvalidDataException("The initial remote snapshot exceeds the event queue limit.");
        }
        _clients[client.Id] = client;
        return client;
    }

    public void RemoveClient(RemoteEventClient client)
    {
        _clients.TryRemove(client.Id, out _);
        client.Dispose();
    }

    public void Broadcast(RemoteEventFrame frame, string? coalesceKey = null)
    {
        if (_clients.IsEmpty)
            return;

        foreach (var client in _clients.Values)
        {
            if (!client.Enqueue(frame, coalesceKey))
                RemoveClient(client);
        }
    }

    public void BroadcastJson<T>(
        string eventName,
        T payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        string? coalesceKey = null)
    {
        if (_clients.IsEmpty)
            return;

        Broadcast(
            new RemoteEventFrame(eventName, JsonSerializer.Serialize(payload, typeInfo)),
            coalesceKey);
    }

    // ── Desktop state subscriptions ─────────────────────────────────────────────────────────

    private void Attach()
    {
        if (_disposed)
            return;

        _main.PropertyChanged += OnMainPropertyChanged;
        _main.ChatGroups.CollectionChanged += OnChatGroupsChanged;
        _main.ChatDeleted += OnChatDeleted;
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
        {
            message.PropertyChanged += observer.MessagePropertyChanged;
            observer.StreamedText.Remove(message.Message.Id);
        }
    }

    private static void SynchronizeObservedMessages(SurfaceObserver observer)
    {
        var activeMessages = new HashSet<ChatMessageViewModel>(
            observer.Surface.Messages,
            ReferenceEqualityComparer.Instance);
        foreach (var message in observer.Messages.Where(message => !activeMessages.Contains(message)).ToArray())
        {
            message.PropertyChanged -= observer.MessagePropertyChanged;
            observer.Messages.Remove(message);
            observer.StreamedText.Remove(message.Message.Id);
        }

        foreach (var message in activeMessages)
            ObserveMessage(observer, message);
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
                _chatsDirty = true;
                break;
            case nameof(MainViewModel.ChatVM):
                ReconcileSurfaces();
                _chatsDirty = true;
                MarkStatusDirty(_main.ChatVM.CurrentChat?.Id);
                MarkTranscriptDirty(_main.ChatVM.CurrentChat?.Id);
                break;
        }
    }

    private void OnChatGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e) => _chatsDirty = true;

    private void OnChatDeleted(Guid chatId)
    {
        _deletedChatIds.Add(chatId);
        _chatsDirty = true;
    }

    /// <summary>Active skills / MCP servers are collections, so they never raise PropertyChanged.</summary>
    private void OnComposerChipsChanged(SurfaceObserver observer) =>
        MarkStatusDirty(observer.Surface.CurrentChat?.Id);

    private void OnChatPropertyChanged(SurfaceObserver observer, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChatViewModel.IsBusy):
                _chatsDirty = true;
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
                _chatsDirty = true;
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

        // Streaming assistant/reasoning text goes out on the hot path as a self-contained delta so
        // the phone repaints without pulling the whole transcript on every token batch.
        if (e.PropertyName == nameof(ChatMessageViewModel.Content)
            && message.IsStreaming
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
            var offset = -1;
            var chunk = bounded;
            if (observer.StreamedText.TryGetValue(messageId, out var previous)
                && bounded.AsSpan().StartsWith(previous.AsSpan(), StringComparison.Ordinal))
            {
                offset = previous.Length;
                chunk = bounded[previous.Length..];
            }

            observer.StreamedText[messageId] = bounded;
            if (chunk.Length == 0)
                return;

            BroadcastJson(RemoteProtocol.Events.StreamDelta, new RemoteStreamDelta
            {
                ChatId = streamingChatId,
                ItemId = messageId.ToString("N"),
                Offset = offset,
                Text = chunk,
                IsReasoning = isReasoning
            }, RemoteJsonContext.Default.RemoteStreamDelta, $"stream:{streamingChatId:N}:{messageId:N}");
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
        if (chatId is not { } id)
            return;

        var chat = _dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == id);
        if (chat is null)
        {
            _chatRowStates.Remove(id);
            _chatsDirty = true;
            return;
        }

        var state = new ChatRowState(
            chat.Title,
            chat.ProjectId,
            chat.AgentId,
            chat.UpdatedAt,
            chat.IsPinned,
            chat.IsRunning,
            chat.HasUnreadMessages,
            chat.LastModelUsed,
            chat.Preview);
        if (!_chatRowStates.TryGetValue(id, out var previous) || previous != state)
        {
            _chatRowStates[id] = state;
            _chatsDirty = true;
        }
    }

    private void MarkStatusDirty(Guid? chatId)
    {
        if (chatId is { } id)
            _statusDirtyChatIds.Add(id);
    }

    private void MarkTranscriptDirty(Guid? chatId)
    {
        Interlocked.Increment(ref _revision);
        if (chatId is { } id)
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
            return;
        }

        if (_chatsDirty)
        {
            _chatsDirty = false;
            var page = RemoteProjector.BuildChatPage(
                _dataStore,
                _main,
                offset: 0,
                limit: RemoteProtocol.ChatPageSize,
                query: null,
                projectId: null);
            page.RemovedChatIds = [.. _deletedChatIds];
            _deletedChatIds.Clear();
            BroadcastJson(
                RemoteProtocol.Events.Chats,
                page,
                RemoteJsonContext.Default.RemoteChatPage,
                RemoteProtocol.Events.Chats);
        }

        if (_snapshotDirty)
        {
            _snapshotDirty = false;
            BroadcastJson(
                RemoteProtocol.Events.Snapshot,
                RemoteProjector.BuildSnapshot(_dataStore, _main, _modelsProvider()),
                RemoteJsonContext.Default.RemoteSnapshot,
                RemoteProtocol.Events.Snapshot);
        }

        if (_statusDirtyChatIds.Count > 0)
        {
            var dirtyChatIds = _statusDirtyChatIds.ToArray();
            _statusDirtyChatIds.Clear();
            foreach (var chatId in dirtyChatIds)
            {
                var chat = _dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == chatId);
                if (chat is null)
                    continue;

                var owner = RemoteProjector.ResolveChatOwner(_main, chatId);
                BroadcastJson(
                    RemoteProtocol.Events.ChatStatus,
                    RemoteProjector.BuildStatus(_dataStore, owner ?? _main.ChatVM, chat),
                    RemoteJsonContext.Default.RemoteChatStatus,
                    $"status:{chatId:N}");
            }
        }

        if (_transcriptDirtyChatIds.Count > 0)
        {
            var dirtyChatIds = _transcriptDirtyChatIds.ToArray();
            _transcriptDirtyChatIds.Clear();
            foreach (var chatId in dirtyChatIds)
            {
                BroadcastJson(
                    RemoteProtocol.Events.TranscriptInvalidated,
                    new RemoteTranscriptInvalidated
                    {
                        ChatId = chatId,
                        RevisionEpoch = _revisionEpoch,
                        Revision = Revision
                    },
                    RemoteJsonContext.Default.RemoteTranscriptInvalidated,
                    $"transcript:{chatId:N}");
            }
        }

        if (_libraryDirty)
        {
            _libraryDirty = false;
            var libraryJson = JsonSerializer.Serialize(
                RemoteProjector.BuildLibrary(_dataStore),
                RemoteJsonContext.Default.RemoteLibrary);
            if (!string.Equals(libraryJson, _lastLibraryJson, StringComparison.Ordinal))
            {
                _lastLibraryJson = libraryJson;
                Broadcast(
                    new RemoteEventFrame(RemoteProtocol.Events.Library, libraryJson),
                    RemoteProtocol.Events.Library);
            }
        }
    }

    private void BroadcastConnection()
    {
        BroadcastJson(
            RemoteProtocol.Events.Connection,
            new RemoteConnectionStatus
            {
                IsConnected = _main.IsConnected,
                Status = _main.ConnectionStatus
            },
            RemoteJsonContext.Default.RemoteConnectionStatus,
            RemoteProtocol.Events.Connection);
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
        public Dictionary<Guid, string> StreamedText { get; } = [];
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
        Guid? ProjectId,
        Guid? AgentId,
        DateTimeOffset UpdatedAt,
        bool IsPinned,
        bool IsRunning,
        bool HasUnreadMessages,
        string? LastModelUsed,
        string? Preview);

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
}

/// <summary>
/// One connected SSE consumer. Frames are buffered in a bounded queue and drained by the client's
/// own writer loop, so a phone on a bad link can never block the desktop UI thread.
/// </summary>
internal sealed class RemoteEventClient : IDisposable
{
    internal const int MaxQueuedFrames = 128;
    internal const int MaxQueuedBytes = RemoteProtocol.MaxSseFrameBytes;
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);

    private readonly Stream _stream;
    private readonly object _queueGate = new();
    private readonly LinkedList<QueuedFrame> _queue = [];
    private readonly Dictionary<string, LinkedListNode<QueuedFrame>> _coalesced = [];
    private readonly SemaphoreSlim _signal = new(0);
    private int _queuedBytes;
    private bool _overflowed;
    private bool _disposed;

    public RemoteEventClient(Stream stream, string deviceId)
    {
        _stream = stream;
        DeviceId = deviceId;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string DeviceId { get; }
    internal int QueuedBytes { get { lock (_queueGate) return _queuedBytes; } }
    internal int QueuedFrames { get { lock (_queueGate) return _queue.Count; } }

    public bool Enqueue(RemoteEventFrame frame, string? coalesceKey = null)
    {
        var bytes = Encoding.UTF8.GetBytes(frame.ToWire());
        lock (_queueGate)
        {
            if (_disposed || _overflowed)
                return false;

            if (coalesceKey is not null && _coalesced.Remove(coalesceKey, out var existing))
            {
                _queue.Remove(existing);
                _queuedBytes -= existing.Value.Bytes.Length;
            }

            if (bytes.Length > MaxQueuedBytes
                || _queue.Count >= MaxQueuedFrames
                || _queuedBytes + bytes.Length > MaxQueuedBytes)
            {
                _overflowed = true;
                _queue.Clear();
                _coalesced.Clear();
                _queuedBytes = 0;
                try { _signal.Release(); } catch (ObjectDisposedException) { }
                return false;
            }

            var queued = new QueuedFrame(bytes, coalesceKey);
            var node = _queue.AddLast(queued);
            if (coalesceKey is not null)
                _coalesced[coalesceKey] = node;
            _queuedBytes += bytes.Length;
        }

        try { _signal.Release(); }
        catch (ObjectDisposedException) { }
        return true;
    }

    /// <summary>Writes frames until the client disconnects or the server shuts down.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                while (TryDequeue(out var bytes))
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(WriteTimeout);
                    await _stream.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
                    await _stream.FlushAsync(deadline.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.IO.IOException
                                       or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            // Phone went away, went to sleep, or the server is stopping.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_queueGate)
        {
            _disposed = true;
            _queue.Clear();
            _coalesced.Clear();
            _queuedBytes = 0;
        }
        try { _signal.Release(); } catch (ObjectDisposedException) { }
        _signal.Dispose();
    }

    private bool TryDequeue(out byte[] bytes)
    {
        lock (_queueGate)
        {
            if (_queue.First is not { } node)
            {
                bytes = [];
                return false;
            }

            _queue.RemoveFirst();
            if (node.Value.CoalesceKey is { } key)
                _coalesced.Remove(key);
            _queuedBytes -= node.Value.Bytes.Length;
            bytes = node.Value.Bytes;
            return true;
        }
    }

    private sealed record QueuedFrame(byte[] Bytes, string? CoalesceKey);
}
