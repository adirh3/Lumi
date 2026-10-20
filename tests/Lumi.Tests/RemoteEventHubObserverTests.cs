using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Text;
using System.Text.Json;
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
    public async Task DesktopReadAndActivityTransitionsReachPagedListSubscribersWithoutOpeningAChat()
    {
        using var session = HeadlessTestSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(() =>
        {
            try
            {
                var chat = Chat("Older unread");
                chat.UpdatedAt = DateTimeOffset.UtcNow.AddYears(-1);
                chat.HasUnreadMessages = true;
                var newer = Enumerable.Range(0, RemoteProtocol.ChatPageSize + 1)
                    .Select(index => Chat($"Newer {index}")).ToList();
                var dataStore = new DataStore(new AppData { Chats = [.. newer, chat] });
                using var main = new MainViewModel(
                    dataStore, TestCopilot.Shared, new UpdateService(), initializeCopilotOnStartup: false);
                main.ChatVM.CurrentChat = chat;
                using var hub = new RemoteEventHub(dataStore, main, () => []);
                Dispatcher.UIThread.RunJobs();
                var stream = new RecordingStream();
                using var client = hub.AddClient(stream, "read-state-fixture",
                    subscription: new RemoteEventSubscription { IncludeChatList = true, IsForeground = true });
                using var cancellation = new CancellationTokenSource();
                var writer = client.RunAsync(cancellation.Token);
                try
                {
                    Flush(hub);
                    WriteBarrierAndWait(hub, stream);
                    var baseline = stream.Text.Length;
                    var revision = hub.Revision;

                    // This is the actual desktop display path: no save or chat-list rebuild.
                    main.ChatVM.AddDisplayHost();
                    Flush(hub);
                    WriteBarrierAndWait(hub, stream);
                    var readStatus = LastStatus(stream.Text[baseline..]);
                    Assert.Equal(chat.Id, readStatus.ChatId);
                    Assert.False(readStatus.HasUnreadMessages);
                    Assert.False(chat.HasUnreadMessages);
                    Assert.Equal(revision, hub.Revision);
                    Assert.DoesNotContain($"event: {RemoteProtocol.Events.TranscriptInvalidated}", stream.Text[baseline..]);

                    var page = RemoteProjector.BuildChatPage(dataStore, main, 0,
                        RemoteProtocol.ChatPageSize, null, null);
                    Assert.DoesNotContain(page.Groups.SelectMany(group => group.Chats), row => row.Id == chat.Id);
                    Assert.False(RemoteProjector.BuildChat(dataStore, chat, main).HasUnreadMessages);

                    main.ChatVM.RemoveDisplayHost();
                    main.ChatVM.IsBusy = true;
                    chat.HasUnreadMessages = true;
                    Flush(hub);
                    WriteBarrierAndWait(hub, stream);
                    var unreadStatus = LastStatus(stream.Text);
                    Assert.True(unreadStatus.HasUnreadMessages);
                    Assert.True(unreadStatus.IsBusy);

                    main.ChatVM.IsBusy = false;
                    chat.IsSessionActive = false;
                    Flush(hub);
                    WriteBarrierAndWait(hub, stream);
                    foreach (var active in new[] { true, false })
                    {
                        baseline = stream.Text.Length;
                        chat.IsSessionActive = active;
                        Assert.Equal(active, main.ChatVM.IsSessionActive);
                        Flush(hub);
                        WriteBarrierAndWait(hub, stream);

                        var backgroundStatus = LastStatus(stream.Text[baseline..]);
                        Assert.Equal(chat.Id, backgroundStatus.ChatId);
                        Assert.Equal(active, backgroundStatus.IsSessionActive);
                        Assert.False(backgroundStatus.IsBusy);
                        Assert.True(backgroundStatus.HasUnreadMessages);
                        Assert.DoesNotContain(RemoteProjector.BuildChatPage(dataStore, main, 0,
                            RemoteProtocol.ChatPageSize, null, null).Groups.SelectMany(group => group.Chats),
                            row => row.Id == chat.Id);
                    }

                    baseline = stream.Text.Length;
                    Flush(hub);
                    WriteBarrierAndWait(hub, stream);
                    Assert.DoesNotContain($"event: {RemoteProtocol.Events.ChatStatus}", stream.Text[baseline..]);
                }
                finally
                {
                    cancellation.Cancel();
                    Pump(writer);
                }
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        }, CancellationToken.None);
        failure?.Throw();

        static RemoteChatStatus LastStatus(string wire)
        {
            var statusFrame = wire.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Last(frame => frame.StartsWith($"event: {RemoteProtocol.Events.ChatStatus}\n", StringComparison.Ordinal));
            var json = statusFrame.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
            return JsonSerializer.Deserialize(json, RemoteJsonContext.Default.RemoteChatStatus)!;
        }
    }

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

                // A running chat's agent steps move its activity time and preview, but they do not
                // wake a phone that shows the list...
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

                // ...its finish does, once...
                detached.IsBusy = false;
                detached.IsStreaming = false;
                Flush(hub);
                WriteBarrierAndWait(hub, mainStream);
                Assert.Equal(
                    chatFramesBeforeHiddenContent + 1,
                    CountEvent(mainStream.Text, RemoteProtocol.Events.Chats));

                // ...and a preview-only change of an idle chat is not a list change either.
                detachedChat.Preview = "another streaming preview";
                typeof(RemoteEventHub)
                    .GetMethod("OnChatContentChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(hub, [detachedChat.Id]);
                Flush(hub);
                WriteBarrierAndWait(hub, mainStream);
                Assert.Equal(chatFramesBeforeHiddenContent + 1,
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

    [Fact]
    public async Task CompactSubscribersNeverReceiveReasoningTextDeltas()
    {
        using var session = HeadlessTestSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(() =>
        {
            MainViewModel? main = null;
            try
            {
                var chat = Chat("Reasoning");
                var dataStore = new DataStore(new AppData { Chats = [chat] });
                main = new MainViewModel(
                    dataStore,
                    TestCopilot.Shared,
                    new UpdateService(),
                    initializeCopilotOnStartup: false);
                main.ChatVM.CurrentChat = chat;
                using var hub = new RemoteEventHub(dataStore, main, () => []);
                Dispatcher.UIThread.RunJobs();

                using var detailed = hub.AddClient(
                    Stream.Null,
                    "detailed",
                    subscription: new RemoteEventSubscription
                    {
                        ChatId = chat.Id,
                        IsForeground = true
                    });
                using var compact = hub.AddClient(
                    Stream.Null,
                    "compact",
                    subscription: new RemoteEventSubscription
                    {
                        ChatId = chat.Id,
                        CompactTranscript = true,
                        IsForeground = true
                    });
                Dispatcher.UIThread.RunJobs();

                var reasoning = new ChatMessageViewModel(new ChatMessage
                {
                    Role = "reasoning",
                    Content = "",
                    IsStreaming = true
                });
                main.ChatVM.Messages.Add(reasoning);
                var detailedBefore = detailed.QueuedFrames;
                var compactBefore = compact.QueuedFrames;

                reasoning.Content = "private reasoning chunk";

                Assert.Equal(detailedBefore + 1, detailed.QueuedFrames);
                Assert.Equal(compactBefore, compact.QueuedFrames);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                main?.Dispose();
            }
        }, CancellationToken.None);

        failure?.Throw();
    }

    [Fact]
    public async Task LiveTerminalDetailChangesInvalidateCompactTranscript()
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
                var chat = Chat("Terminal");
                var toolMessage = new ChatMessage
                {
                    Role = "tool",
                    ToolName = "powershell",
                    ToolCallId = "terminal-1",
                    ToolStatus = "InProgress",
                    Content = """{"command":"dotnet test"}"""
                };
                chat.Messages.Add(toolMessage);
                chat.MessageCount = chat.Messages.Count;
                var dataStore = new DataStore(new AppData { Chats = [chat] });
                main = new MainViewModel(
                    dataStore,
                    TestCopilot.Shared,
                    new UpdateService(),
                    initializeCopilotOnStartup: false);
                main.ChatVM.CurrentChat = chat;
                main.ChatVM.Messages.Add(new ChatMessageViewModel(toolMessage));
                using var hub = new RemoteEventHub(dataStore, main, () => []);
                Dispatcher.UIThread.RunJobs();

                var stream = new RecordingStream();
                var client = hub.AddClient(
                    stream,
                    "compact",
                    subscription: new RemoteEventSubscription
                    {
                        ChatId = chat.Id,
                        CompactTranscript = true,
                        IsForeground = true
                    });
                cancellation = new CancellationTokenSource();
                writer = client.RunAsync(cancellation.Token);
                typeof(RemoteEventHub)
                    .GetMethod(
                        "ReconcileMessageObservers",
                        BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(hub, null);
                var invalidationsBefore = CountEvent(
                    stream.Text,
                    RemoteProtocol.Events.TranscriptInvalidated);

                typeof(ChatViewModel)
                    .GetMethod(
                        "ApplyTerminalOutputAndNotify",
                        BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(main.ChatVM, [chat, "terminal-1", "partial output", false]);
                Flush(hub);
                WriteBarrierAndWait(hub, stream);

                Assert.Equal("partial output", toolMessage.ToolOutput);
                Assert.Equal(
                    invalidationsBefore + 1,
                    CountEvent(stream.Text, RemoteProtocol.Events.TranscriptInvalidated));
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

    [Fact]
    public async Task SessionOnlyChangesPublishReadyStatusAndChatListUpdates()
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
                var chat = Chat("Ready background session");
                var dataStore = new DataStore(new AppData { Chats = [chat] });
                main = new MainViewModel(
                    dataStore,
                    TestCopilot.Shared,
                    new UpdateService(),
                    initializeCopilotOnStartup: false);
                main.ChatVM.CurrentChat = chat;
                using var hub = new RemoteEventHub(dataStore, main, () => []);
                Dispatcher.UIThread.RunJobs();

                var stream = new RecordingStream();
                var client = hub.AddClient(
                    stream,
                    "session-observer",
                    subscription: new RemoteEventSubscription
                    {
                        ChatId = chat.Id,
                        IncludeChatList = true,
                        IsForeground = true
                    });
                cancellation = new CancellationTokenSource();
                writer = client.RunAsync(cancellation.Token);
                Flush(hub);
                WriteBarrierAndWait(hub, stream);

                foreach (var active in new[] { true, false })
                {
                    var before = stream.Text.Length;
                    main.ChatVM.IsSessionActive = active;
                    Assert.False(main.ChatVM.IsBusy);
                    Flush(hub);
                    WriteBarrierAndWait(hub, stream);

                    var update = stream.Text[before..];
                    Assert.Equal(1, CountEvent(update, RemoteProtocol.Events.ChatStatus));
                    Assert.Equal(1, CountEvent(update, RemoteProtocol.Events.Chats));
                    Assert.Contains($"\"isSessionActive\":{(active ? "true" : "false")}", update);
                    Assert.Contains("\"isBusy\":false", update);
                    Assert.Contains("\"isRunning\":false", update);
                }
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

    [Fact]
    public async Task BackgroundSubscriptionStaysQuietAndForegroundReturnPings()
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
                var chat = Chat("Resume on the same stream");
                var dataStore = new DataStore(new AppData { Chats = [chat] });
                main = new MainViewModel(
                    dataStore,
                    TestCopilot.Shared,
                    new UpdateService(),
                    initializeCopilotOnStartup: false);
                main.ChatVM.CurrentChat = chat;
                using var hub = new RemoteEventHub(dataStore, main, () => []);
                Dispatcher.UIThread.RunJobs();

                var stream = new RecordingStream();
                var client = hub.AddClient(
                    stream,
                    "phone",
                    subscription: new RemoteEventSubscription
                    {
                        Generation = 1,
                        ChatId = chat.Id,
                        IsForeground = true
                    });
                cancellation = new CancellationTokenSource();
                writer = client.RunAsync(cancellation.Token);
                Flush(hub);
                WriteBarrierAndWait(hub, stream);

                Pump(hub.UpdateSubscriptionAsync("phone", new RemoteEventSubscription
                {
                    Generation = 2,
                    ChatId = chat.Id,
                    IsForeground = false
                }));
                var background = stream.Text.Length;
                main.ChatVM.StatusText = "Working while the phone is away";
                main.ChatVM.IsBusy = true;
                Flush(hub);
                WriteBarrierAndWait(hub, stream);
                var quiet = stream.Text[background..];
                Assert.Equal(0, CountEvent(quiet, RemoteProtocol.Events.ChatStatus));
                Assert.Equal(0, CountEvent(quiet, RemoteProtocol.Events.Ping));

                var returned = stream.Text.Length;
                Pump(hub.UpdateSubscriptionAsync("phone", new RemoteEventSubscription
                {
                    Generation = 3,
                    ChatId = chat.Id,
                    IsForeground = true
                }));
                WriteBarrierAndWait(hub, stream);
                var resumed = stream.Text[returned..];
                Assert.Equal(1, CountEvent(resumed, RemoteProtocol.Events.Ping));
                Assert.Equal(1, CountEvent(resumed, RemoteProtocol.Events.ChatStatus));
                Assert.Contains("Working while the phone is away", resumed);
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

    [Fact]
    public async Task APhoneReturningOnItsKeptStreamCatchesUpOnWhatItMissed()
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
                var chat = Chat("Away and back");
                var dataStore = new DataStore(new AppData { Chats = [chat] });
                main = new MainViewModel(
                    dataStore,
                    TestCopilot.Shared,
                    new UpdateService(),
                    initializeCopilotOnStartup: false);
                main.ChatVM.CurrentChat = chat;
                using var hub = new RemoteEventHub(dataStore, main, () => []);
                Dispatcher.UIThread.RunJobs();

                var stream = new RecordingStream();
                var phone = hub.AddClient(stream, "phone", subscription: new RemoteEventSubscription
                {
                    Generation = 1,
                    ChatId = chat.Id,
                    IsForeground = true
                });
                // Another phone on the new-chat screen keeps catalog pushes flowing.
                hub.AddClient(new RecordingStream(), "other-phone", subscription: new RemoteEventSubscription
                {
                    Generation = 1,
                    IsForeground = true
                });
                cancellation = new CancellationTokenSource();
                writer = phone.RunAsync(cancellation.Token);
                Flush(hub);
                WriteBarrierAndWait(hub, stream);

                Pump(hub.UpdateSubscriptionAsync("phone", new RemoteEventSubscription
                {
                    Generation = 2,
                    ChatId = chat.Id,
                    IsForeground = false
                }));
                var away = stream.Text.Length;
                main.ConnectionStatus = "Copilot is ready";
                hub.MarkLibraryDirty();
                Flush(hub);
                WriteBarrierAndWait(hub, stream);
                var quiet = stream.Text[away..];
                Assert.Equal(0, CountEvent(quiet, RemoteProtocol.Events.Connection));
                Assert.Equal(0, CountEvent(quiet, RemoteProtocol.Events.Snapshot));

                var back = stream.Text.Length;
                Pump(hub.UpdateSubscriptionAsync("phone", new RemoteEventSubscription
                {
                    Generation = 3,
                    ChatId = chat.Id,
                    IsForeground = true
                }));
                WriteBarrierAndWait(hub, stream);
                var resumed = stream.Text[back..];
                Assert.Equal(1, CountEvent(resumed, RemoteProtocol.Events.Connection));
                Assert.Contains("Copilot is ready", resumed);
                Assert.Equal(1, CountEvent(resumed, RemoteProtocol.Events.Snapshot));

                // A return with nothing missed costs only the tiny connection state.
                Pump(hub.UpdateSubscriptionAsync("phone", new RemoteEventSubscription
                {
                    Generation = 4,
                    ChatId = chat.Id,
                    IsForeground = false
                }));
                var again = stream.Text.Length;
                Pump(hub.UpdateSubscriptionAsync("phone", new RemoteEventSubscription
                {
                    Generation = 5,
                    ChatId = chat.Id,
                    IsForeground = true
                }));
                WriteBarrierAndWait(hub, stream);
                var quickReturn = stream.Text[again..];
                Assert.Equal(1, CountEvent(quickReturn, RemoteProtocol.Events.Connection));
                Assert.Equal(0, CountEvent(quickReturn, RemoteProtocol.Events.Snapshot));
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

    [Fact]
    public async Task ALibraryChangeWhileEveryPhoneIsAwayIsCaughtUpOnReturn()
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
                // The desktop has a chat open; that is independent of the phone's screen.
                var chat = Chat("Open on the desktop");
                var dataStore = new DataStore(new AppData { Chats = [chat] });
                main = new MainViewModel(
                    dataStore,
                    TestCopilot.Shared,
                    new UpdateService(),
                    initializeCopilotOnStartup: false);
                main.ChatVM.CurrentChat = chat;
                using var hub = new RemoteEventHub(dataStore, main, () => []);
                Dispatcher.UIThread.RunJobs();

                // The only phone sits on the new-chat screen, whose setups come from the library.
                var stream = new RecordingStream();
                var phone = hub.AddClient(stream, "phone", subscription: new RemoteEventSubscription
                {
                    Generation = 1,
                    IsForeground = true
                });
                cancellation = new CancellationTokenSource();
                writer = phone.RunAsync(cancellation.Token);
                Pump(hub.UpdateSubscriptionAsync("phone", new RemoteEventSubscription
                {
                    Generation = 2,
                    IsForeground = false
                }));

                hub.MarkLibraryDirty();
                Flush(hub);
                WriteBarrierAndWait(hub, stream);
                var back = stream.Text.Length;
                Pump(hub.UpdateSubscriptionAsync("phone", new RemoteEventSubscription
                {
                    Generation = 3,
                    IsForeground = true
                }));
                WriteBarrierAndWait(hub, stream);

                Assert.Equal(1, CountEvent(stream.Text[back..], RemoteProtocol.Events.Snapshot));
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

    [Fact]
    public async Task KeepAliveClosesBackgroundStreamsThePhoneAbandoned()
    {
        using var session = HeadlessTestSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(() =>
        {
            MainViewModel? main = null;
            try
            {
                var time = new ManualTimeProvider();
                var dataStore = new DataStore(new AppData());
                main = new MainViewModel(
                    dataStore,
                    TestCopilot.Shared,
                    new UpdateService(),
                    initializeCopilotOnStartup: false);
                using var hub = new RemoteEventHub(dataStore, main, () => [], time: time);
                Dispatcher.UIThread.RunJobs();

                var live = hub.AddClient(new RecordingStream(), "phone", subscription: new RemoteEventSubscription
                {
                    Generation = 1,
                    IsForeground = true
                });
                hub.AddClient(new RecordingStream(), "killed-phone", subscription: new RemoteEventSubscription
                {
                    Generation = 1,
                    IsForeground = true
                });
                // The app went to the background, then was killed before it closed its stream. The
                // desktop writes nothing to background streams, so no failed write will ever reveal it.
                Pump(hub.UpdateSubscriptionAsync("killed-phone", new RemoteEventSubscription
                {
                    Generation = 2,
                    IsForeground = false
                }));

                time.Advance(TimeSpan.FromMinutes(1));
                Assert.Equal(2, hub.ClientCount);
                Assert.Equal(1, live.QueuedFrames);

                time.Advance(TimeSpan.FromMinutes(1));
                Assert.Equal(1, hub.ClientCount);
                Assert.Equal(1, live.QueuedFrames);
                Dispatcher.UIThread.RunJobs();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                main?.Dispose();
            }
        }, CancellationToken.None);

        failure?.Throw();
    }

    [Fact]
    public async Task ARunningChatsAgentStepsDoNotWakeAPhoneThatShowsTheList()
    {
        using var session = HeadlessTestSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(() =>
        {
            CancellationTokenSource? cancellation = null;
            var writers = new List<Task>();
            MainViewModel? main = null;
            try
            {
                var open = Chat("Open on the PC");
                var background = Chat("Agent working in the background");
                var dataStore = new DataStore(new AppData { Chats = [open, background] });
                main = new MainViewModel(
                    dataStore,
                    TestCopilot.Shared,
                    new UpdateService(),
                    initializeCopilotOnStartup: false);
                main.ChatVM.CurrentChat = open;
                using var hub = new RemoteEventHub(dataStore, main, () => []);
                Dispatcher.UIThread.RunJobs();

                var phoneStream = new RecordingStream();
                var olderStream = new RecordingStream();
                var phone = hub.AddClient(phoneStream, "phone", subscription: new RemoteEventSubscription
                {
                    Generation = 1,
                    IncludeChatList = true,
                    IsForeground = true,
                    ChatRowPatches = true,
                    CompactStatus = true
                });
                var older = hub.AddClient(olderStream, "older-phone", subscription: new RemoteEventSubscription
                {
                    Generation = 1,
                    IncludeChatList = true,
                    IsForeground = true
                });
                cancellation = new CancellationTokenSource();
                writers.Add(phone.RunAsync(cancellation.Token));
                writers.Add(older.RunAsync(cancellation.Token));
                Flush(hub);
                WriteBarrierAndWait(hub, phoneStream);
                WriteBarrierAndWait(hub, olderStream);

                (string Phone, string Older) Since((int Phone, int Older) mark)
                {
                    Flush(hub);
                    WriteBarrierAndWait(hub, phoneStream);
                    WriteBarrierAndWait(hub, olderStream);
                    return (phoneStream.Text[mark.Phone..], olderStream.Text[mark.Older..]);
                }

                // The run starts: both phones hear about it once.
                var mark = (phoneStream.Text.Length, olderStream.Text.Length);
                background.UpdatedAt = DateTimeOffset.Now;
                background.IsRunning = true;
                var started = Since(mark);
                Assert.Equal(1, CountEvent(started.Phone, RemoteProtocol.Events.Chats));
                Assert.Equal(1, CountEvent(started.Phone, RemoteProtocol.Events.ChatStatus));
                Assert.Equal(1, CountEvent(started.Older, RemoteProtocol.Events.Chats));

                // Every agent step drops and restores the running flag and saves the chat, which
                // moves its activity time, preview and message count. None of it reaches a phone.
                for (var step = 1; step <= 3; step++)
                {
                    mark = (phoneStream.Text.Length, olderStream.Text.Length);
                    background.IsRunning = false;
                    background.IsRunning = true;
                    background.Messages.Add(new ChatMessage { Role = "assistant", Content = $"Step {step} done" });
                    background.UpdatedAt = DateTimeOffset.Now.AddSeconds(step);
                    typeof(RemoteEventHub)
                        .GetMethod("OnChatContentChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(hub, [background.Id]);
                    var quiet = Since(mark);
                    Assert.Equal(0, CountEvent(quiet.Phone, RemoteProtocol.Events.Chats));
                    Assert.Equal(0, CountEvent(quiet.Phone, RemoteProtocol.Events.ChatStatus));
                    Assert.Equal(0, CountEvent(quiet.Older, RemoteProtocol.Events.Chats));
                    Assert.Equal(0, CountEvent(quiet.Older, RemoteProtocol.Events.ChatStatus));
                }

                // The run finishes with an unread reply: one update each, showing where it ended.
                mark = (phoneStream.Text.Length, olderStream.Text.Length);
                background.IsRunning = false;
                background.HasUnreadMessages = true;
                var finished = Since(mark);
                Assert.Equal(1, CountEvent(finished.Phone, RemoteProtocol.Events.Chats));
                Assert.Equal(1, CountEvent(finished.Phone, RemoteProtocol.Events.ChatStatus));
                Assert.Contains("Step 3 done", finished.Phone);
                Assert.Equal(1, CountEvent(finished.Older, RemoteProtocol.Events.Chats));
                Assert.Contains("Step 3 done", finished.Older);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                cancellation?.Cancel();
                foreach (var writer in writers)
                    Pump(writer);
                cancellation?.Dispose();
                main?.Dispose();
            }
        }, CancellationToken.None);

        failure?.Throw();
    }

    [Fact]
    public async Task CompactStatusPhonesStopReceivingAnUnchangedPlan()
    {
        using var session = HeadlessTestSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(() =>
        {
            CancellationTokenSource? cancellation = null;
            var writers = new List<Task>();
            MainViewModel? main = null;
            try
            {
                var chat = Chat("Plan heavy chat");
                var dataStore = new DataStore(new AppData { Chats = [chat] });
                main = new MainViewModel(
                    dataStore,
                    TestCopilot.Shared,
                    new UpdateService(),
                    initializeCopilotOnStartup: false);
                main.ChatVM.CurrentChat = chat;
                main.ChatVM.PlanContent = "1. Read the code\n2. Fix the race\n3. Prove it";
                using var hub = new RemoteEventHub(dataStore, main, () => []);
                Dispatcher.UIThread.RunJobs();

                var compactStream = new RecordingStream();
                var legacyStream = new RecordingStream();
                cancellation = new CancellationTokenSource();
                writers.Add(hub.AddClient(compactStream, "new-phone", subscription: new RemoteEventSubscription
                {
                    Generation = 1,
                    ChatId = chat.Id,
                    IsForeground = true,
                    CompactStatus = true
                }).RunAsync(cancellation.Token));
                writers.Add(hub.AddClient(legacyStream, "old-phone", subscription: new RemoteEventSubscription
                {
                    Generation = 1,
                    ChatId = chat.Id,
                    IsForeground = true
                }).RunAsync(cancellation.Token));
                Flush(hub);
                WriteBarrierAndWait(hub, compactStream);

                RemoteChatStatus LastStatus(RecordingStream stream)
                {
                    WriteBarrierAndWait(hub, stream);
                    var json = stream.Text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                        .Last(frame => frame.StartsWith($"event: {RemoteProtocol.Events.ChatStatus}\n", StringComparison.Ordinal))
                        .Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
                    return JsonSerializer.Deserialize(json, RemoteJsonContext.Default.RemoteChatStatus)!;
                }

                main.ChatVM.StatusText = "First";
                Flush(hub);
                var first = LastStatus(compactStream);
                Assert.Contains("Fix the race", first.PlanContent);
                Assert.False(first.IsPlanOmitted);

                main.ChatVM.StatusText = "Second";
                Flush(hub);
                var second = LastStatus(compactStream);
                Assert.Equal("Second", second.StatusText);
                Assert.Null(second.PlanContent);
                Assert.True(second.IsPlanOmitted);
                Assert.False(second.HasComposerCatalogs);
                var legacy = LastStatus(legacyStream);
                Assert.Equal("Second", legacy.StatusText);
                Assert.Contains("Fix the race", legacy.PlanContent);

                main.ChatVM.PlanContent = "1. Ship it";
                Flush(hub);
                var changed = LastStatus(compactStream);
                Assert.Equal("1. Ship it", changed.PlanContent);
                Assert.False(changed.IsPlanOmitted);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                cancellation?.Cancel();
                foreach (var writer in writers)
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
