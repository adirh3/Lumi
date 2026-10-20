using System.Text;
using System.Text.Json;
using Lumi.Remote.Protocol;
using Lumi.Services.Remote;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Per-phone delivery: streaming text, status and chat-list rows are built for each phone when its
/// writer reaches them, and every batch goes out with one flush at the phone's requested cadence.
/// </summary>
public sealed class RemoteEventDeliveryTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void AStreamLeftInTheBackgroundCountsAsAbandonedAfterTheLimit()
    {
        var time = new ManualTimeProvider();
        var limit = TimeSpan.FromMinutes(2);
        using var client = new RemoteEventClient(
            new RecordingStream(),
            "phone",
            new RemoteEventSubscription { Generation = 1, IsForeground = true },
            time);

        time.Advance(TimeSpan.FromHours(1));
        Assert.False(client.IsAbandonedInBackground(limit));

        Assert.True(client.TryUpdateSubscription(
            new RemoteEventSubscription { Generation = 2, IsForeground = false }, out _, out _));
        time.Advance(TimeSpan.FromSeconds(119));
        Assert.False(client.IsAbandonedInBackground(limit));

        // Returning to the foreground stops the clock; leaving again restarts it.
        Assert.True(client.TryUpdateSubscription(
            new RemoteEventSubscription { Generation = 3, IsForeground = true }, out _, out _));
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(client.IsAbandonedInBackground(limit));
        Assert.True(client.TryUpdateSubscription(
            new RemoteEventSubscription { Generation = 4, IsForeground = false }, out _, out _));
        Assert.True(client.TryUpdateSubscription(
            new RemoteEventSubscription { Generation = 5, IsForeground = false, ChatId = Guid.NewGuid() }, out _, out _));
        time.Advance(TimeSpan.FromSeconds(119));
        Assert.False(client.IsAbandonedInBackground(limit));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(client.IsAbandonedInBackground(limit));

        using var openedInBackground = new RemoteEventClient(
            new RecordingStream(),
            "phone",
            new RemoteEventSubscription { IsForeground = false },
            time);
        time.Advance(limit);
        Assert.True(openedInBackground.IsAbandonedInBackground(limit));
    }

    [Fact]
    public async Task BackloggedStreamTextMergesInsteadOfDroppingAChunk()
    {
        var chatId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var stream = new RecordingStream();
        using var client = new RemoteEventClient(stream, "phone");

        // The phone's writer is still busy while the desktop flushes twice. The old shared-offset
        // queue replaced "Hello " with an offset-6 "world" the phone could never apply.
        Assert.True(client.EnqueueStreamText(chatId, messageId, "Hello ", isReasoning: false));
        Assert.True(client.EnqueueStreamText(chatId, messageId, "Hello world", isReasoning: false));
        Assert.Equal(1, client.QueuedFrames);

        using var cancellation = new CancellationTokenSource();
        var writer = client.RunAsync(cancellation.Token);
        await WaitUntilAsync(() => Deltas(stream.Text).Count == 1);
        var first = Assert.Single(Deltas(stream.Text));
        Assert.Equal(-1, first.Offset);
        Assert.Equal("Hello world", first.Text);
        Assert.Equal(messageId.ToString("N"), first.ItemId);

        Assert.True(client.EnqueueStreamText(chatId, messageId, "Hello world!", isReasoning: false));
        await WaitUntilAsync(() => Deltas(stream.Text).Count == 2);
        var second = Deltas(stream.Text)[1];
        Assert.Equal(11, second.Offset);
        Assert.Equal("!", second.Text);

        // A non-append edit (for example final normalization) replaces the row instead.
        Assert.True(client.EnqueueStreamText(chatId, messageId, "Hi there", isReasoning: false));
        await WaitUntilAsync(() => Deltas(stream.Text).Count == 3);
        var third = Deltas(stream.Text)[2];
        Assert.Equal(-1, third.Offset);
        Assert.Equal("Hi there", third.Text);

        cancellation.Cancel();
        await writer;
    }

    [Fact]
    public async Task EachPhoneContinuesFromWhatItReceived()
    {
        var chatId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var earlyStream = new RecordingStream();
        var lateStream = new RecordingStream();
        using var early = new RemoteEventClient(earlyStream, "early");
        using var late = new RemoteEventClient(lateStream, "late");
        using var cancellation = new CancellationTokenSource();
        var earlyWriter = early.RunAsync(cancellation.Token);
        var lateWriter = late.RunAsync(cancellation.Token);

        early.EnqueueStreamText(chatId, messageId, "Hello", isReasoning: false);
        await WaitUntilAsync(() => Deltas(earlyStream.Text).Count == 1);

        early.EnqueueStreamText(chatId, messageId, "Hello world", isReasoning: false);
        late.EnqueueStreamText(chatId, messageId, "Hello world", isReasoning: false);
        await WaitUntilAsync(() => Deltas(earlyStream.Text).Count == 2 && Deltas(lateStream.Text).Count == 1);

        Assert.Equal((5, " world"), (Deltas(earlyStream.Text)[1].Offset, Deltas(earlyStream.Text)[1].Text));
        Assert.Equal((-1, "Hello world"), (Deltas(lateStream.Text)[0].Offset, Deltas(lateStream.Text)[0].Text));

        cancellation.Cancel();
        await Task.WhenAll(earlyWriter, lateWriter);
    }

    [Fact]
    public async Task PhoneCadenceSendsEachBatchWithOneFlush()
    {
        var chatId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var time = new ManualTimeProvider();
        var stream = new RecordingStream();
        using var client = new RemoteEventClient(
            stream,
            "phone",
            new RemoteEventSubscription { StreamIntervalMs = RemoteProtocol.MobileStreamIntervalMs },
            time);
        using var cancellation = new CancellationTokenSource();

        Assert.True(client.Enqueue(new RemoteEventFrame(RemoteProtocol.Events.Snapshot, "{}")));
        var writer = client.RunAsync(cancellation.Token);
        await WaitUntilAsync(() => client.FlushCount == 1);

        // Three desktop flushes inside one phone interval: the writer waits for the window, then
        // sends the merged text and the ping together.
        client.EnqueueStreamText(chatId, messageId, "a", isReasoning: false);
        client.Enqueue(new RemoteEventFrame(RemoteProtocol.Events.Ping, "{}"), RemoteProtocol.Events.Ping);
        client.EnqueueStreamText(chatId, messageId, "ab", isReasoning: false);
        client.EnqueueStreamText(chatId, messageId, "abc", isReasoning: false);
        await WaitUntilAsync(() => time.PendingTimerCount == 1);
        Assert.Equal(1, client.FlushCount);

        time.Advance(TimeSpan.FromMilliseconds(RemoteProtocol.MobileStreamIntervalMs));
        await WaitUntilAsync(() => client.FlushCount == 2);
        Assert.Equal(3, client.WrittenFrames);
        Assert.Equal(2, stream.FlushCount);
        var delta = Assert.Single(Deltas(stream.Text));
        Assert.Equal((-1, "abc"), (delta.Offset, delta.Text));

        cancellation.Cancel();
        await writer;
    }

    [Fact]
    public async Task DesktopCadenceIsKeptForPhonesThatDoNotAskForOne()
    {
        var time = new ManualTimeProvider();
        var stream = new RecordingStream();
        using var client = new RemoteEventClient(stream, "older-phone", time: time);
        using var cancellation = new CancellationTokenSource();
        var writer = client.RunAsync(cancellation.Token);

        Assert.True(client.Enqueue(new RemoteEventFrame("first", "{}")));
        await WaitUntilAsync(() => client.FlushCount == 1);
        Assert.True(client.Enqueue(new RemoteEventFrame("second", "{}")));
        await WaitUntilAsync(() => client.FlushCount == 2);
        Assert.Equal(0, time.PendingTimerCount);

        cancellation.Cancel();
        await writer;
    }

    [Fact]
    public async Task CompactStatusSendsTheSlimFrameOnlyWhenThePhoneHoldsPlanAndCatalogs()
    {
        var chatId = Guid.NewGuid();
        var otherChatId = Guid.NewGuid();
        var compactStream = new RecordingStream();
        var legacyStream = new RecordingStream();
        using var compact = new RemoteEventClient(
            compactStream,
            "new-phone",
            new RemoteEventSubscription { Generation = 1, ChatId = chatId, CompactStatus = true });
        using var legacy = new RemoteEventClient(
            legacyStream,
            "old-phone",
            new RemoteEventSubscription { Generation = 1, ChatId = chatId });
        using var cancellation = new CancellationTokenSource();
        var writers = Task.WhenAll(compact.RunAsync(cancellation.Token), legacy.RunAsync(cancellation.Token));

        async Task PushAsync(string marker, ulong catalogs, ulong plan)
        {
            var frames = new RemoteStatusFrames(
                chatId,
                StatusFrame($"full-{marker}"),
                StatusFrame($"slim-{marker}"),
                catalogs,
                plan);
            Assert.True(compact.EnqueueStatus(frames));
            Assert.True(legacy.EnqueueStatus(frames));
            await WaitUntilAsync(() =>
                compactStream.Text.Contains($"-{marker}\"", StringComparison.Ordinal)
                && legacyStream.Text.Contains($"-{marker}\"", StringComparison.Ordinal));
        }

        await PushAsync("1", catalogs: 1, plan: 1);
        Assert.Contains("full-1", compactStream.Text);

        await PushAsync("2", catalogs: 1, plan: 1);
        Assert.Contains("slim-2", compactStream.Text);
        Assert.Contains("full-2", legacyStream.Text);

        await PushAsync("3", catalogs: 1, plan: 2);
        Assert.Contains("full-3", compactStream.Text);

        await PushAsync("4", catalogs: 1, plan: 2);
        Assert.Contains("slim-4", compactStream.Text);

        // Looking at another chat and coming back starts from a full status again.
        Assert.True(compact.TryUpdateSubscription(
            new RemoteEventSubscription { Generation = 2, ChatId = otherChatId, CompactStatus = true },
            out _,
            out _));
        Assert.True(compact.TryUpdateSubscription(
            new RemoteEventSubscription { Generation = 3, ChatId = chatId, CompactStatus = true },
            out _,
            out _));
        await PushAsync("5", catalogs: 1, plan: 2);
        Assert.Contains("full-5", compactStream.Text);
        Assert.DoesNotContain("slim", legacyStream.Text);

        cancellation.Cancel();
        await writers;
    }

    [Fact]
    public async Task AListOnlyStatusIsSentOnlyWhenTheRowWouldChange()
    {
        var chatId = Guid.NewGuid();
        var stream = new RecordingStream();
        using var client = new RemoteEventClient(
            stream,
            "phone",
            new RemoteEventSubscription { Generation = 1, IsForeground = true, IncludeChatList = true });
        using var cancellation = new CancellationTokenSource();
        var writer = client.RunAsync(cancellation.Token);

        async Task<bool> DeliveredAsync(string marker, bool running, bool unread, bool listOnly = true)
        {
            Assert.True(client.EnqueueStatus(
                new RemoteStatusFrames(
                    chatId,
                    StatusFrame(marker),
                    null,
                    0,
                    0,
                    new ChatListActivity(running, running, unread)),
                listOnly));
            await BarrierAsync(client, stream);
            return stream.Text.Contains($"\"{marker}\"", StringComparison.Ordinal);
        }

        Assert.True(await DeliveredAsync("started", running: true, unread: false));
        // An agent step changes nothing the row shows.
        Assert.False(await DeliveredAsync("next-step", running: true, unread: false));
        Assert.True(await DeliveredAsync("finished", running: false, unread: true));

        // A phone with the chat open still receives every status.
        Assert.True(await DeliveredAsync("open-chat", running: false, unread: true, listOnly: false));

        // Hiding and showing the list again reloads it, so the next status is sent again.
        Assert.True(client.TryUpdateSubscription(
            new RemoteEventSubscription { Generation = 2, IsForeground = true }, out _, out _));
        Assert.True(client.TryUpdateSubscription(
            new RemoteEventSubscription { Generation = 3, IsForeground = true, IncludeChatList = true },
            out _,
            out _));
        Assert.True(await DeliveredAsync("list-reopened", running: false, unread: true));

        cancellation.Cancel();
        await writer;
    }

    [Fact]
    public async Task RowPatchPhonesReceiveOnlyChangedRowsAfterTheirFirstPage()
    {
        var rows = Enumerable.Range(0, 3)
            .Select(index => new RemoteChat
            {
                Id = Guid.NewGuid(),
                Title = $"Chat {index}",
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-index)
            })
            .ToList();
        var stream = new RecordingStream();
        using var client = new RemoteEventClient(
            stream,
            "phone",
            new RemoteEventSubscription { Generation = 1, IncludeChatList = true, ChatRowPatches = true });
        using var cancellation = new CancellationTokenSource();
        var writer = client.RunAsync(cancellation.Token);

        Assert.True(client.EnqueueChatPage(PageFrames(rows)));
        await WaitUntilAsync(() => ChatPages(stream.Text).Count == 1);
        var first = ChatPages(stream.Text)[0];
        Assert.False(first.IsPatch);
        Assert.Equal(3, first.Groups.Sum(group => group.Chats.Count));

        var renamed = Copy(rows[1], title: "Renamed");
        Assert.True(client.EnqueueChatPage(PageFrames([rows[0], renamed, rows[2]])));
        await WaitUntilAsync(() => ChatPages(stream.Text).Count == 2);
        var patch = ChatPages(stream.Text)[1];
        Assert.True(patch.IsPatch);
        var changed = Assert.Single(patch.Groups.SelectMany(group => group.Chats));
        Assert.Equal((renamed.Id, "Renamed"), (changed.Id, changed.Title));

        // Nothing changed: nothing is written.
        Assert.True(client.EnqueueChatPage(PageFrames([rows[0], renamed, rows[2]])));
        await BarrierAsync(client, stream);
        Assert.Equal(2, ChatPages(stream.Text).Count);

        var deleted = PageFrames([rows[0], renamed]);
        deleted.Page.RemovedChatIds.Add(rows[2].Id);
        Assert.True(client.EnqueueChatPage(deleted));
        await WaitUntilAsync(() => ChatPages(stream.Text).Count == 3);
        var removal = ChatPages(stream.Text)[2];
        Assert.True(removal.IsPatch);
        Assert.Equal([rows[2].Id], removal.RemovedChatIds);
        Assert.Empty(removal.Groups);

        // A phone that stops and restarts watching the list gets a whole page first.
        client.TryUpdateSubscription(
            new RemoteEventSubscription { Generation = 2, IncludeChatList = false, ChatRowPatches = true },
            out _,
            out _);
        client.TryUpdateSubscription(
            new RemoteEventSubscription { Generation = 3, IncludeChatList = true, ChatRowPatches = true },
            out _,
            out _);
        Assert.True(client.EnqueueChatPage(PageFrames([rows[0], renamed])));
        await WaitUntilAsync(() => ChatPages(stream.Text).Count == 4);
        Assert.False(ChatPages(stream.Text)[3].IsPatch);

        cancellation.Cancel();
        await writer;
    }

    [Fact]
    public async Task QueuedDeletionsSurviveACoalescedRowPatch()
    {
        var rows = Enumerable.Range(0, 3)
            .Select(index => new RemoteChat { Id = Guid.NewGuid(), Title = $"Chat {index}" })
            .ToList();
        var stream = new RecordingStream();
        using var client = new RemoteEventClient(
            stream,
            "phone",
            new RemoteEventSubscription { Generation = 1, IncludeChatList = true, ChatRowPatches = true });
        using var cancellation = new CancellationTokenSource();
        var writer = client.RunAsync(cancellation.Token);
        Assert.True(client.EnqueueChatPage(PageFrames(rows)));
        await WaitUntilAsync(() => ChatPages(stream.Text).Count == 1);

        // Two pushes queue before the writer reaches them; the first one's deletion must not be lost.
        cancellation.Cancel();
        await writer;
        var deleted = PageFrames([rows[0], rows[1]]);
        deleted.Page.RemovedChatIds.Add(rows[2].Id);
        Assert.True(client.EnqueueChatPage(deleted));
        Assert.True(client.EnqueueChatPage(PageFrames([rows[0], Copy(rows[1], title: "Later")])));

        using var resumed = new CancellationTokenSource();
        var resumedWriter = client.RunAsync(resumed.Token);
        await WaitUntilAsync(() => ChatPages(stream.Text).Count == 2);
        var patch = ChatPages(stream.Text)[1];
        Assert.Contains(rows[2].Id, patch.RemovedChatIds);
        Assert.Equal("Later", Assert.Single(patch.Groups.SelectMany(group => group.Chats)).Title);

        resumed.Cancel();
        await resumedWriter;
    }

    private static RemoteChat Copy(RemoteChat chat, string? title = null) => new()
    {
        Id = chat.Id,
        Title = title ?? chat.Title,
        UpdatedAt = chat.UpdatedAt,
        MessageCount = chat.MessageCount
    };

    private static RemoteChatPageFrames PageFrames(IReadOnlyList<RemoteChat> chats)
    {
        var page = new RemoteChatPage
        {
            TotalCount = chats.Count,
            Groups = [new RemoteChatGroup { Label = "Today", Chats = [.. chats] }]
        };
        return RemoteEventHub.BuildChatPageFrames(
            page,
            new RemoteEventFrame(
                RemoteProtocol.Events.Chats,
                JsonSerializer.Serialize(page, RemoteJsonContext.Default.RemoteChatPage)));
    }

    private static byte[] StatusFrame(string marker) =>
        Encoding.UTF8.GetBytes(new RemoteEventFrame(
            RemoteProtocol.Events.ChatStatus,
            JsonSerializer.Serialize(
                new RemoteChatStatus { StatusText = marker },
                RemoteJsonContext.Default.RemoteChatStatus)).ToWire());

    private static async Task BarrierAsync(RemoteEventClient client, RecordingStream stream)
    {
        var name = $"barrier-{Guid.NewGuid():N}";
        Assert.True(client.Enqueue(new RemoteEventFrame(name, "{}")));
        await WaitUntilAsync(() => stream.Text.Contains($"event: {name}", StringComparison.Ordinal));
    }

    private static List<RemoteStreamDelta> Deltas(string wire) =>
        Frames(wire, RemoteProtocol.Events.StreamDelta)
            .Select(json => JsonSerializer.Deserialize(json, RemoteJsonContext.Default.RemoteStreamDelta)!)
            .ToList();

    private static List<RemoteChatPage> ChatPages(string wire) =>
        Frames(wire, RemoteProtocol.Events.Chats)
            .Select(json => JsonSerializer.Deserialize(json, RemoteJsonContext.Default.RemoteChatPage)!)
            .ToList();

    private static IEnumerable<string> Frames(string wire, string eventName)
    {
        var reader = new RemoteEventFrame.Reader();
        foreach (var line in wire.Split('\n'))
        {
            if (reader.Push(line) is { } frame && frame.Event == eventName)
                yield return frame.Data;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The expected remote delivery state was not reached.");
            await Task.Delay(5);
        }
    }

    private sealed class RecordingStream : Stream
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();
        private int _flushCount;

        public string Text
        {
            get
            {
                lock (_gate)
                    return _text.ToString();
            }
        }

        public int FlushCount => Volatile.Read(ref _flushCount);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => Interlocked.Increment(ref _flushCount);

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _flushCount);
            return Task.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate)
                _text.Append(Encoding.UTF8.GetString(buffer, offset, count));
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                _text.Append(Encoding.UTF8.GetString(buffer.Span));
            return ValueTask.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
