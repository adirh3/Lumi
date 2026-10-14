using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Remote.Protocol;

namespace Lumi.Services.Remote;

/// <summary>
/// One connected SSE consumer. Frames are buffered in a bounded queue and drained by the client's
/// own writer loop, so a phone on a bad link can never block the desktop UI thread.
/// </summary>
/// <remarks>
/// Hot state (streaming text, chat status, chat-list rows) is queued as a per-phone slot and turned
/// into a frame only when the writer reaches it. A slow phone therefore receives one merged frame
/// built from what it actually received before — never a later chunk whose predecessor was dropped —
/// and every batch the writer drains is sent with a single flush, at most once per
/// <see cref="RemoteEventSubscription.StreamIntervalMs"/>.
/// </remarks>
internal sealed class RemoteEventClient : IDisposable
{
    internal const int MaxQueuedFrames = 128;
    internal const int MaxQueuedBytes = RemoteProtocol.MaxSseFrameBytes;
    private const int MaxTrackedStreams = 16;
    private const int StreamSlotReservedBytes = RemoteProtocol.MobileAssistantTextLimit * 3 + 1024;
    private const string ChatPageKey = "chat-page";
    private const long NotInBackground = long.MinValue;
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);

    private readonly Stream _stream;
    private readonly TimeProvider _time;
    private readonly object _queueGate = new();
    private readonly LinkedList<QueuedFrame> _queue = [];
    private readonly Dictionary<string, LinkedListNode<QueuedFrame>> _coalesced = [];
    private readonly Dictionary<string, StreamSlot> _streams = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, PendingStatus> _pendingStatus = [];
    private readonly Dictionary<Guid, (ulong Catalogs, ulong Plan)> _deliveredStatus = [];
    // Running/unread markers this phone's chat list was last sent, per chat.
    private readonly Dictionary<Guid, ChatListActivity> _deliveredListActivity = [];
    private readonly SemaphoreSlim _signal = new(0);
    private RemoteChatPageFrames? _pendingChatPage;
    private Dictionary<Guid, ulong>? _deliveredChatRows;
    private long _streamUseCounter;
    private int _queuedBytes;
    private RemoteEventSubscription _subscription;
    private bool _overflowed;
    private bool _disposed;
    private long _lastFlushTimestamp;
    private long _backgroundSince = NotInBackground;
    private int _missedCatalogs;
    private bool _hasFlushed;
    private long _writtenFrames;
    private long _flushCount;

    public RemoteEventClient(
        Stream stream,
        string deviceId,
        RemoteEventSubscription? subscription = null,
        TimeProvider? time = null)
    {
        _stream = stream;
        _time = time ?? TimeProvider.System;
        DeviceId = deviceId;
        _subscription = (subscription ?? new RemoteEventSubscription()).Copy();
        if (!_subscription.IsForeground)
            _backgroundSince = _time.GetTimestamp();
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string DeviceId { get; }
    internal int QueuedBytes { get { lock (_queueGate) return _queuedBytes; } }
    internal int QueuedFrames { get { lock (_queueGate) return _queue.Count; } }
    internal long WrittenFrames => Interlocked.Read(ref _writtenFrames);
    internal long FlushCount => Interlocked.Read(ref _flushCount);
    internal bool IsForeground => Volatile.Read(ref _subscription).IsForeground;
    internal bool WantsNewChatExperience
    {
        get
        {
            var subscription = Volatile.Read(ref _subscription);
            return subscription.IsForeground && subscription.ChatId is null;
        }
    }
    internal bool WantsChatList
    {
        get
        {
            var subscription = Volatile.Read(ref _subscription);
            return subscription.IsForeground && subscription.IncludeChatList;
        }
    }
    internal bool WantsLibrary
    {
        get
        {
            var subscription = Volatile.Read(ref _subscription);
            return subscription.IsForeground && subscription.IncludeLibrary;
        }
    }
    internal bool WantsCompactTranscript =>
        Volatile.Read(ref _subscription).CompactTranscript;
    internal bool WantsCompactStatus =>
        Volatile.Read(ref _subscription).CompactStatus;
    internal bool WantsChatRowPatches =>
        Volatile.Read(ref _subscription).ChatRowPatches;

    internal TimeSpan StreamInterval =>
        TimeSpan.FromMilliseconds(Math.Clamp(
            Volatile.Read(ref _subscription).StreamIntervalMs,
            0,
            RemoteProtocol.MaxStreamIntervalMs));

    internal bool WantsChat(Guid chatId)
    {
        var subscription = Volatile.Read(ref _subscription);
        return subscription.IsForeground
               && subscription.ChatId == chatId;
    }

    internal bool TryUpdateSubscription(
        RemoteEventSubscription subscription,
        out RemoteEventSubscription previous,
        out RemoteEventSubscription current)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        while (true)
        {
            var observed = Volatile.Read(ref _subscription);
            if (subscription.Generation < observed.Generation)
            {
                previous = observed.Copy();
                current = previous;
                return false;
            }

            var replacement = subscription.Copy();
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _subscription, replacement, observed),
                    observed))
            {
                previous = observed.Copy();
                current = replacement.Copy();
                if (previous.IsForeground != current.IsForeground)
                {
                    Volatile.Write(
                        ref _backgroundSince,
                        current.IsForeground ? NotInBackground : _time.GetTimestamp());
                }

                ResetDeliveredScope(previous, current);
                return true;
            }
        }
    }

    /// <summary>True once this stream has stayed in the background for <paramref name="limit"/>.</summary>
    internal bool IsAbandonedInBackground(TimeSpan limit)
    {
        var since = Volatile.Read(ref _backgroundSince);
        return since != NotInBackground && _time.GetElapsedTime(since) >= limit;
    }

    /// <summary>Records that a catalog push skipped this phone while it was in the background.</summary>
    internal void MarkCatalogsMissed() => Volatile.Write(ref _missedCatalogs, 1);

    internal bool TakeMissedCatalogs() => Interlocked.Exchange(ref _missedCatalogs, 0) == 1;

    internal RemoteEventSubscription SnapshotSubscription() =>
        Volatile.Read(ref _subscription).Copy();

    /// <summary>Queues a prebuilt frame. A coalesce key replaces the older queued frame.</summary>
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
                _queuedBytes -= existing.Value.ReservedBytes;
            }

            if (!TryReserveLocked(bytes.Length))
                return false;

            var node = _queue.AddLast(new QueuedFrame(coalesceKey, bytes, null, bytes.Length));
            if (coalesceKey is not null)
                _coalesced[coalesceKey] = node;
        }

        Signal();
        return true;
    }

    /// <summary>
    /// Records the latest bounded text of a streaming message. The frame is built when written, from
    /// the text this phone already received: an append when it is a prefix, otherwise a replacement.
    /// </summary>
    public bool EnqueueStreamText(Guid chatId, Guid messageId, string text, bool isReasoning)
    {
        var key = $"stream:{chatId:N}:{messageId:N}";
        lock (_queueGate)
        {
            if (_disposed || _overflowed)
                return false;

            if (!_streams.TryGetValue(key, out var slot))
            {
                TrimStreamsLocked();
                slot = new StreamSlot(chatId, messageId);
                _streams[key] = slot;
            }

            slot.Latest = text;
            slot.IsReasoning = isReasoning;
            slot.LastUse = ++_streamUseCounter;
            if (!_coalesced.ContainsKey(key))
            {
                if (!TryReserveLocked(StreamSlotReservedBytes))
                    return false;

                _coalesced[key] = _queue.AddLast(new QueuedFrame(
                    key,
                    null,
                    () => BuildStreamFrameLocked(slot),
                    StreamSlotReservedBytes));
            }
        }

        Signal();
        return true;
    }

    /// <summary>
    /// Records the latest status of a chat. Compact-status phones receive the slim variant once they
    /// already hold the same plan and composer catalogs. <paramref name="listOnly"/> marks a status
    /// queued only for this phone's chat list; it is dropped when the list already shows the same
    /// running and unread state, as between the steps of a running agent.
    /// </summary>
    public bool EnqueueStatus(RemoteStatusFrames frames, bool listOnly = false)
    {
        var key = $"status:{frames.ChatId:N}";
        lock (_queueGate)
        {
            if (_disposed || _overflowed)
                return false;

            var reserved = frames.Full.Length;
            if (_coalesced.Remove(key, out var existing))
            {
                _queue.Remove(existing);
                _queuedBytes -= existing.Value.ReservedBytes;
                if (_pendingStatus.TryGetValue(frames.ChatId, out var pending))
                    listOnly &= pending.ListOnly;
            }

            if (!TryReserveLocked(reserved))
                return false;

            _pendingStatus[frames.ChatId] = new PendingStatus(frames, listOnly);
            var chatId = frames.ChatId;
            _coalesced[key] = _queue.AddLast(new QueuedFrame(
                key,
                null,
                () => BuildStatusFrameLocked(chatId),
                reserved));
        }

        Signal();
        return true;
    }

    /// <summary>
    /// Records the desktop's latest first chat page for a row-patch phone. When written, only rows
    /// that differ from what this phone last received are sent.
    /// </summary>
    public bool EnqueueChatPage(RemoteChatPageFrames frames)
    {
        lock (_queueGate)
        {
            if (_disposed || _overflowed)
                return false;

            var reserved = frames.Full.Length;
            if (_coalesced.Remove(ChatPageKey, out var existing))
            {
                _queue.Remove(existing);
                _queuedBytes -= existing.Value.ReservedBytes;
                if (_pendingChatPage is { } pending)
                {
                    // Deletions accumulate until a page reaches the phone.
                    frames = frames.WithRemovedChatIds(pending.Page.RemovedChatIds);
                }
            }

            if (!TryReserveLocked(reserved))
                return false;

            _pendingChatPage = frames;
            _coalesced[ChatPageKey] = _queue.AddLast(new QueuedFrame(
                ChatPageKey,
                null,
                BuildChatPageFrameLocked,
                reserved));
        }

        Signal();
        return true;
    }

    /// <summary>Writes batches until the client disconnects or the server shuts down.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var batch = new List<byte[]>();
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

                var interval = StreamInterval;
                if (interval > TimeSpan.Zero && _hasFlushed)
                {
                    var elapsed = _time.GetElapsedTime(_lastFlushTimestamp);
                    if (elapsed < interval)
                        await Task.Delay(interval - elapsed, _time, cancellationToken).ConfigureAwait(false);
                }

                // Everything queued by now goes out in this batch; the releases for it are spent.
                while (_signal.CurrentCount > 0 && _signal.Wait(0))
                {
                }

                batch.Clear();
                lock (_queueGate)
                {
                    while (_queue.First is { } node)
                    {
                        _queue.RemoveFirst();
                        if (node.Value.CoalesceKey is { } key
                            && _coalesced.TryGetValue(key, out var coalesced)
                            && ReferenceEquals(coalesced, node))
                        {
                            _coalesced.Remove(key);
                        }

                        _queuedBytes -= node.Value.ReservedBytes;
                        var bytes = node.Value.Bytes ?? node.Value.Build?.Invoke();
                        if (bytes is { Length: > 0 })
                            batch.Add(bytes);
                    }
                }

                if (batch.Count == 0)
                    continue;

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(WriteTimeout);
                foreach (var bytes in batch)
                    await _stream.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);

                await _stream.FlushAsync(deadline.Token).ConfigureAwait(false);
                _lastFlushTimestamp = _time.GetTimestamp();
                _hasFlushed = true;
                Interlocked.Add(ref _writtenFrames, batch.Count);
                Interlocked.Increment(ref _flushCount);
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
            _streams.Clear();
            _pendingStatus.Clear();
            _deliveredStatus.Clear();
            _deliveredListActivity.Clear();
            _pendingChatPage = null;
            _deliveredChatRows = null;
            _queuedBytes = 0;
        }
        try { _signal.Release(); } catch (ObjectDisposedException) { }
        _signal.Dispose();
    }

    private void Signal()
    {
        try { _signal.Release(); }
        catch (ObjectDisposedException) { }
    }

    private bool TryReserveLocked(int bytes)
    {
        if (bytes > MaxQueuedBytes
            || _queue.Count >= MaxQueuedFrames
            || _queuedBytes + bytes > MaxQueuedBytes)
        {
            _overflowed = true;
            _queue.Clear();
            _coalesced.Clear();
            _queuedBytes = 0;
            try { _signal.Release(); } catch (ObjectDisposedException) { }
            return false;
        }

        _queuedBytes += bytes;
        return true;
    }

    /// <summary>
    /// A new observed chat starts from a clean slate: the next stream frame is a full replacement,
    /// the next status carries its plan and catalogs, and the next chat-list push is a full page.
    /// </summary>
    private void ResetDeliveredScope(RemoteEventSubscription previous, RemoteEventSubscription current)
    {
        var previousChat = previous.IsForeground ? previous.ChatId : null;
        var currentChat = current.IsForeground ? current.ChatId : null;
        lock (_queueGate)
        {
            if (previousChat != currentChat)
            {
                foreach (var key in _streams
                             .Where(pair => !_coalesced.ContainsKey(pair.Key))
                             .Select(static pair => pair.Key)
                             .ToList())
                {
                    _streams.Remove(key);
                }

                _deliveredStatus.Clear();
            }

            if (!(previous.IsForeground && previous.IncludeChatList)
                && current.IsForeground
                && current.IncludeChatList)
            {
                // The phone reloads its list when it shows it again.
                _deliveredChatRows = null;
                _deliveredListActivity.Clear();
            }
        }
    }

    private void TrimStreamsLocked()
    {
        if (_streams.Count < MaxTrackedStreams)
            return;

        foreach (var key in _streams
                     .Where(pair => !_coalesced.ContainsKey(pair.Key))
                     .OrderBy(static pair => pair.Value.LastUse)
                     .Take(_streams.Count - MaxTrackedStreams + 1)
                     .Select(static pair => pair.Key)
                     .ToList())
        {
            _streams.Remove(key);
        }
    }

    private byte[]? BuildStreamFrameLocked(StreamSlot slot)
    {
        var latest = slot.Latest;
        var sent = slot.Sent;
        int offset;
        string chunk;
        if (sent is not null && latest.StartsWith(sent, StringComparison.Ordinal))
        {
            offset = sent.Length;
            chunk = latest[sent.Length..];
        }
        else
        {
            offset = -1;
            chunk = latest;
        }

        slot.Sent = latest;
        if (chunk.Length == 0)
            return null;

        var json = JsonSerializer.Serialize(new RemoteStreamDelta
        {
            ChatId = slot.ChatId,
            ItemId = slot.MessageId.ToString("N"),
            Offset = offset,
            Text = chunk,
            IsReasoning = slot.IsReasoning
        }, RemoteJsonContext.Default.RemoteStreamDelta);
        return Encoding.UTF8.GetBytes(
            new RemoteEventFrame(RemoteProtocol.Events.StreamDelta, json).ToWire());
    }

    private byte[]? BuildStatusFrameLocked(Guid chatId)
    {
        if (!_pendingStatus.Remove(chatId, out var pending))
            return null;

        var frames = pending.Frames;
        if (pending.ListOnly
            && _deliveredListActivity.TryGetValue(chatId, out var shown)
            && shown == frames.ListActivity)
        {
            return null;
        }

        _deliveredListActivity[chatId] = frames.ListActivity;
        if (!WantsCompactStatus)
            return frames.Full;

        if (frames.Slim is not null
            && _deliveredStatus.TryGetValue(chatId, out var delivered)
            && delivered.Catalogs == frames.CatalogHash
            && delivered.Plan == frames.PlanHash)
        {
            return frames.Slim;
        }

        _deliveredStatus[chatId] = (frames.CatalogHash, frames.PlanHash);
        return frames.Full;
    }

    private byte[]? BuildChatPageFrameLocked()
    {
        var frames = _pendingChatPage;
        _pendingChatPage = null;
        if (frames is null)
            return null;

        if (_deliveredChatRows is null)
        {
            _deliveredChatRows = new Dictionary<Guid, ulong>(frames.RowHashes);
            return frames.Full;
        }

        var changed = new HashSet<Guid>();
        foreach (var (chatId, hash) in frames.RowHashes)
        {
            if (!_deliveredChatRows.TryGetValue(chatId, out var delivered) || delivered != hash)
                changed.Add(chatId);
        }

        foreach (var removed in frames.Page.RemovedChatIds)
            _deliveredChatRows.Remove(removed);

        if (changed.Count == 0 && frames.Page.RemovedChatIds.Count == 0)
            return null;

        foreach (var chatId in changed)
            _deliveredChatRows[chatId] = frames.RowHashes[chatId];

        var page = frames.Page;
        var patch = new RemoteChatPage
        {
            Offset = page.Offset,
            TotalCount = page.TotalCount,
            HasMore = page.HasMore,
            Query = page.Query,
            ProjectId = page.ProjectId,
            PinnedGroupLabel = page.PinnedGroupLabel,
            TodayGroupLabel = page.TodayGroupLabel,
            IsPatch = true,
            RemovedChatIds = [.. page.RemovedChatIds],
            Groups = page.Groups
                .Select(group => new RemoteChatGroup
                {
                    Label = group.Label,
                    Chats = group.Chats.Where(chat => changed.Contains(chat.Id)).ToList()
                })
                .Where(static group => group.Chats.Count > 0)
                .ToList()
        };
        var json = JsonSerializer.Serialize(patch, RemoteJsonContext.Default.RemoteChatPage);
        return Encoding.UTF8.GetBytes(new RemoteEventFrame(RemoteProtocol.Events.Chats, json).ToWire());
    }

    private sealed record QueuedFrame(
        string? CoalesceKey,
        byte[]? Bytes,
        Func<byte[]?>? Build,
        int ReservedBytes);

    private readonly record struct PendingStatus(RemoteStatusFrames Frames, bool ListOnly);

    private sealed class StreamSlot(Guid chatId, Guid messageId)
    {
        public Guid ChatId { get; } = chatId;
        public Guid MessageId { get; } = messageId;
        public string Latest { get; set; } = "";
        public string? Sent { get; set; }
        public bool IsReasoning { get; set; }
        public long LastUse { get; set; }
    }
}

/// <summary>
/// One status update, serialized once per flush: the full frame every phone understands and, for
/// compact-status phones, a slim frame without the plan and composer catalogs.
/// <see cref="ListActivity"/> is what a chat-list row shows of it.
/// </summary>
internal sealed record RemoteStatusFrames(
    Guid ChatId,
    byte[] Full,
    byte[]? Slim,
    ulong CatalogHash,
    ulong PlanHash,
    ChatListActivity ListActivity = default);

/// <summary>The running and unread markers a phone's chat-list row shows for a chat.</summary>
internal readonly record struct ChatListActivity(bool IsRunning, bool IsSessionActive, bool? HasUnreadMessages);

/// <summary>The desktop's current first chat page with a content hash per row.</summary>
internal sealed record RemoteChatPageFrames(
    RemoteChatPage Page,
    IReadOnlyDictionary<Guid, ulong> RowHashes,
    byte[] Full)
{
    public RemoteChatPageFrames WithRemovedChatIds(IEnumerable<Guid> earlierRemovals)
    {
        var removed = Page.RemovedChatIds.Concat(earlierRemovals).Distinct().ToList();
        if (removed.Count == Page.RemovedChatIds.Count)
            return this;

        var page = new RemoteChatPage
        {
            Offset = Page.Offset,
            TotalCount = Page.TotalCount,
            HasMore = Page.HasMore,
            Query = Page.Query,
            ProjectId = Page.ProjectId,
            PinnedGroupLabel = Page.PinnedGroupLabel,
            TodayGroupLabel = Page.TodayGroupLabel,
            Groups = Page.Groups,
            RemovedChatIds = removed
        };
        var json = JsonSerializer.Serialize(page, RemoteJsonContext.Default.RemoteChatPage);
        return this with
        {
            Page = page,
            Full = Encoding.UTF8.GetBytes(new RemoteEventFrame(RemoteProtocol.Events.Chats, json).ToWire())
        };
    }
}
