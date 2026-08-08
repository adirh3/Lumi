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
                var dataStore = new DataStore(new AppData());
                using var main = new MainViewModel(
                    dataStore,
                    TestCopilot.Shared,
                    new UpdateService(),
                    initializeCopilotOnStartup: false);
                using var hub = new RemoteEventHub(dataStore, main, () => []);
                Dispatcher.UIThread.RunJobs();

                var viewModel = main.ChatVM;

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
                var client = hub.AddClient(stream, "test-device");
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
