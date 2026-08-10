using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Text;
using Avalonia.Threading;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.Services.Remote;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class RemoteEventHubObserverTests
{
    [Fact]
    public async Task RemovedReplacedAndResetMessages_StopInvalidatingRemoteState()
    {
        using var session = HeadlessTestSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(() =>
        {
            try
            {
                var chat = Chat("Observed");
                var dataStore = new DataStore(new AppData { Chats = [chat] });
                using var main = new MainViewModel(
                    dataStore,
                    TestCopilot.Shared,
                    new UpdateService(),
                    initializeCopilotOnStartup: false);
                using var hub = new RemoteEventHub(dataStore, main, () => []);
                Dispatcher.UIThread.RunJobs();

                var viewModel = main.ChatVM;
                viewModel.CurrentChat = chat;
                using var client = hub.AddClient(
                    Stream.Null,
                    "test-device",
                    subscription: new RemoteEventSubscription
                    {
                        ChatId = chat.Id,
                        IsForeground = true
                    });
                Dispatcher.UIThread.RunJobs();

                var removed = Message("removed");
                viewModel.Messages.Add(removed);
                var revisionBeforeLiveChange = hub.Revision;
                removed.Content = "live";
                Assert.Equal(revisionBeforeLiveChange + 1, hub.Revision);

                viewModel.Messages.Remove(removed);
                var revisionAfterRemoval = hub.Revision;
                removed.Content = "stale after removal";
                Assert.Equal(revisionAfterRemoval, hub.Revision);

                var replaced = Message("replaced");
                viewModel.Messages.Add(replaced);
                var replacement = Message("replacement");
                viewModel.Messages[^1] = replacement;
                var revisionAfterReplacement = hub.Revision;

                replaced.Content = "stale after replacement";
                Assert.Equal(revisionAfterReplacement, hub.Revision);

                replacement.Content = "live replacement";
                Assert.Equal(revisionAfterReplacement + 1, hub.Revision);

                viewModel.Messages.Clear();
                var revisionAfterReset = hub.Revision;
                replacement.Content = "stale after reset";
                Assert.Equal(revisionAfterReset, hub.Revision);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        }, CancellationToken.None);

        failure?.Throw();
    }

    [Fact]
    public async Task DetachedSurfaceStreamsStatusDeltasAndTranscriptInvalidation()
    {
        using var session = HeadlessTestSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(() =>
        {
            DetachedChatWindowRequest? request = null;
            CancellationTokenSource? streamCancellation = null;
            Task? streamTask = null;
            MainViewModel? main = null;
            try
            {
                var mainChat = Chat("Main");
                var detachedChat = Chat("Detached");
                var dataStore = new DataStore(new AppData { Chats = [mainChat, detachedChat] });
                main = new MainViewModel(
                    dataStore,
                    TestCopilot.Shared,
                    new UpdateService(),
                    initializeCopilotOnStartup: false);
                main.ChatVM.CurrentChat = mainChat;
                main.OpenChatWindowRequested += detachedRequest => request = detachedRequest;
                var detach = main.OpenChatInNewWindowCommand.ExecuteAsync(detachedChat);
                Pump(detach);

                Assert.NotNull(request);
                var detached = request.WindowVM.ChatVM;
                using var hub = new RemoteEventHub(
                    dataStore,
                    main,
                    () => [],
                    revisionEpoch: "server-generation-a");
                Dispatcher.UIThread.RunJobs();

                var stream = new RecordingStream();
                var client = hub.AddClient(
                    stream,
                    "test-device",
                    subscription: new RemoteEventSubscription
                    {
                        ChatId = detachedChat.Id,
                        IsForeground = true
                    });
                streamCancellation = new CancellationTokenSource();
                streamTask = client.RunAsync(streamCancellation.Token);

                detached.IsBusy = true;
                detached.IsStreaming = true;
                detached.StatusText = "Detached working";
                var streaming = Message("starting");
                streaming.IsStreaming = true;
                detached.Messages.Add(streaming);
                streaming.Content = "detached delta";

                typeof(RemoteEventHub)
                    .GetMethod("FlushPending", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(hub, null);
                Assert.True(
                    SpinWait.SpinUntil(
                        () => stream.Text.Contains(RemoteProtocol.Events.TranscriptInvalidated, StringComparison.Ordinal),
                        TimeSpan.FromSeconds(2)),
                    stream.Text);

                var wire = stream.Text;
                var chatId = detachedChat.Id.ToString();
                Assert.Contains($"event: {RemoteProtocol.Events.StreamDelta}", wire, StringComparison.Ordinal);
                Assert.Contains($"event: {RemoteProtocol.Events.ChatStatus}", wire, StringComparison.Ordinal);
                Assert.Contains($"event: {RemoteProtocol.Events.TranscriptInvalidated}", wire, StringComparison.Ordinal);
                Assert.Contains(
                    "\"revisionEpoch\":\"server-generation-a\"",
                    wire,
                    StringComparison.Ordinal);
                Assert.Contains(chatId, wire, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Detached working", wire, StringComparison.Ordinal);
                Assert.Contains("detached delta", wire, StringComparison.Ordinal);
                Assert.Same(mainChat, main.ChatVM.CurrentChat);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                streamCancellation?.Cancel();
                if (streamTask is not null)
                    Pump(streamTask);
                streamCancellation?.Dispose();
                request?.WindowVM.Dispose();
                request?.ReleaseSurface();
                main?.Dispose();
            }
        }, CancellationToken.None);

        failure?.Throw();
    }

    [Fact]
    public async Task ScopedClientsReceiveOnlyTheirVisibleChatAndVisibleCollections()
    {
        using var session = HeadlessTestSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(() =>
        {
            DetachedChatWindowRequest? request = null;
            MainViewModel? main = null;
            CancellationTokenSource? cancellation = null;
            Task? mainWriter = null;
            Task? detachedWriter = null;
            try
            {
                var mainChat = Chat("Main");
                var detachedChat = Chat("Detached");
                var dataStore = new DataStore(new AppData { Chats = [mainChat, detachedChat] });
                main = new MainViewModel(
                    dataStore,
                    TestCopilot.Shared,
                    new UpdateService(),
                    initializeCopilotOnStartup: false);
                main.ChatVM.CurrentChat = mainChat;
                main.OpenChatWindowRequested += detachedRequest => request = detachedRequest;
                Pump(main.OpenChatInNewWindowCommand.ExecuteAsync(detachedChat));
                Assert.NotNull(request);
                var detached = request.WindowVM.ChatVM;

                using var hub = new RemoteEventHub(dataStore, main, () => []);
                Dispatcher.UIThread.RunJobs();
                var mainStream = new RecordingStream();
                var detachedStream = new RecordingStream();
                var mainClient = hub.AddClient(
                    mainStream,
                    "main-device",
                    subscription: new RemoteEventSubscription
                    {
                        Generation = 1,
                        ChatId = mainChat.Id,
                        IsForeground = true
                    });
                var detachedClient = hub.AddClient(
                    detachedStream,
                    "detached-device",
                    subscription: new RemoteEventSubscription
                    {
                        Generation = 1,
                        ChatId = detachedChat.Id,
                        IsForeground = true
                    });
                cancellation = new CancellationTokenSource();
                mainWriter = mainClient.RunAsync(cancellation.Token);
                detachedWriter = detachedClient.RunAsync(cancellation.Token);

                detached.IsBusy = true;
                detached.IsStreaming = true;
                var streaming = Message("starting");
                streaming.IsStreaming = true;
                detached.Messages.Add(streaming);
                streaming.Content = "only detached sees this";
                Flush(hub);

                Assert.True(
                    SpinWait.SpinUntil(
                        () => detachedStream.Text.Contains(
                            RemoteProtocol.Events.StreamDelta,
                            StringComparison.Ordinal),
                        TimeSpan.FromSeconds(2)),
                    detachedStream.Text);
                Assert.DoesNotContain(
                    RemoteProtocol.Events.StreamDelta,
                    mainStream.Text,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    RemoteProtocol.Events.TranscriptInvalidated,
                    mainStream.Text,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    $"event: {RemoteProtocol.Events.Chats}",
                    detachedStream.Text,
                    StringComparison.Ordinal);

                var transcriptFramesBeforeDrawer = CountEvent(
                    mainStream.Text,
                    RemoteProtocol.Events.TranscriptInvalidated);
                Pump(hub.UpdateSubscriptionAsync(
                    "main-device",
                    new RemoteEventSubscription
                    {
                        Generation = 2,
                        ChatId = mainChat.Id,
                        IncludeChatList = true,
                        IsForeground = true
                    }));
                WriteBarrierAndWait(hub, mainStream);
                Assert.Equal(
                    transcriptFramesBeforeDrawer,
                    CountEvent(mainStream.Text, RemoteProtocol.Events.TranscriptInvalidated));
                Assert.Equal(0, CountEvent(mainStream.Text, RemoteProtocol.Events.Chats));

                main.ChatVM.IsBusy = true;
                Flush(hub);

                WriteBarrierAndWait(hub, mainStream);
                Assert.Equal(1, CountEvent(mainStream.Text, RemoteProtocol.Events.Chats));
                Assert.DoesNotContain(
                    $"event: {RemoteProtocol.Events.Chats}",
                    detachedStream.Text,
                    StringComparison.Ordinal);

                var chatFramesBeforeHiddenContent = CountEvent(
                    mainStream.Text,
                    RemoteProtocol.Events.Chats);
                detachedChat.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1);
                detachedChat.Preview = "streamed item changed";
                typeof(RemoteEventHub)
                    .GetMethod("OnChatContentChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(hub, [detachedChat.Id]);
                Flush(hub);
                WriteBarrierAndWait(hub, mainStream);
                Assert.Equal(
                    chatFramesBeforeHiddenContent,
                    CountEvent(mainStream.Text, RemoteProtocol.Events.Chats));
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                cancellation?.Cancel();
                if (mainWriter is not null)
                    Pump(mainWriter);
                if (detachedWriter is not null)
                    Pump(detachedWriter);
                cancellation?.Dispose();
                request?.WindowVM.Dispose();
                request?.ReleaseSurface();
                main?.Dispose();
            }
        }, CancellationToken.None);

        failure?.Throw();
    }

    [Fact]
    public async Task LibraryDedupResetsWhenChangesOccurWithoutSubscribers()
    {
        using var session = HeadlessTestSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(() =>
        {
            CancellationTokenSource? cancellation = null;
            Task? writer = null;
            MainViewModel? main = null;
            try
            {
                var project = new Project { Name = "Initial" };
                var dataStore = new DataStore(new AppData { Projects = [project] });
                main = new MainViewModel(
                    dataStore,
                    TestCopilot.Shared,
                    new UpdateService(),
                    initializeCopilotOnStartup: false);
                using var hub = new RemoteEventHub(dataStore, main, () => []);
                Dispatcher.UIThread.RunJobs();

                var stream = new RecordingStream();
                var client = hub.AddClient(
                    stream,
                    "test-device",
                    subscription: new RemoteEventSubscription
                    {
                        Generation = 1,
                        IncludeLibrary = true,
                        IsForeground = true
                    });
                cancellation = new CancellationTokenSource();
                writer = client.RunAsync(cancellation.Token);

                project.Name = "Broadcast state";
                MarkLibraryDirtyAndFlush(hub);
                WriteBarrierAndWait(hub, stream);
                Assert.Equal(1, CountEvent(stream.Text, RemoteProtocol.Events.Library));

                Pump(hub.UpdateSubscriptionAsync(
                    "test-device",
                    new RemoteEventSubscription
                    {
                        Generation = 2,
                        IsForeground = true
                    }));
                project.Name = "Fetched while hidden";
                MarkLibraryDirtyAndFlush(hub);

                Pump(hub.UpdateSubscriptionAsync(
                    "test-device",
                    new RemoteEventSubscription
                    {
                        Generation = 3,
                        IncludeLibrary = true,
                        IsForeground = true
                    }));
                project.Name = "Broadcast state";
                MarkLibraryDirtyAndFlush(hub);
                WriteBarrierAndWait(hub, stream);

                Assert.Equal(2, CountEvent(stream.Text, RemoteProtocol.Events.Library));
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                cancellation?.Cancel();
                if (writer is not null)
                    Pump(writer);
                cancellation?.Dispose();
                main?.Dispose();
            }
        }, CancellationToken.None);

        failure?.Throw();
    }

    private static void Flush(RemoteEventHub hub) =>
        typeof(RemoteEventHub)
            .GetMethod("FlushPending", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(hub, null);

    private static void MarkLibraryDirtyAndFlush(RemoteEventHub hub)
    {
        typeof(RemoteEventHub)
            .GetField("_libraryDirty", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(hub, true);
        Flush(hub);
    }

    private static void WriteBarrierAndWait(RemoteEventHub hub, RecordingStream stream)
    {
        var eventName = $"test-barrier-{Guid.NewGuid():N}";
        hub.Broadcast(new RemoteEventFrame(eventName, "{}"));
        Assert.True(
            SpinWait.SpinUntil(
                () => stream.Text.Contains($"event: {eventName}", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2)),
            stream.Text);
    }

    private static int CountEvent(string wire, string eventName) =>
        wire.Split($"event: {eventName}", StringSplitOptions.None).Length - 1;

    private static ChatMessageViewModel Message(string content) =>
        new(new ChatMessage
        {
            Role = "assistant",
            Content = content,
            Timestamp = DateTimeOffset.UtcNow
        });

    private static Chat Chat(string title)
    {
        var chat = new Chat { Title = title };
        chat.Messages.Add(new ChatMessage { Role = "user", Content = title });
        chat.MessageCount = 1;
        return chat;
    }

    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The headless UI operation did not complete.");
            Thread.Sleep(1);
        }

        task.GetAwaiter().GetResult();
    }

    private sealed class RecordingStream : Stream
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();

        public string Text
        {
            get
            {
                lock (_gate)
                    return _text.ToString();
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate)
                _text.Append(Encoding.UTF8.GetString(buffer, offset, count));
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
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
