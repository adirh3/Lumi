using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class MobileTransportScopeTests
{
    [Fact]
    public async Task ResumeReconcilesWithTheRealDispatcherAndBoundShell()
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            Window? window = null;
            try
            {
                await VerifyAcceptedSendResumeAsync(true, false, queuedPosts: true, shell =>
                {
                    window = new Window
                    {
                        Width = 412, Height = 892,
                        Content = new MobileShellView { DataContext = shell }
                    };
                    window.Show();
                });
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window?.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public Task ResumeConfirmsAnAcceptedSendWhoseResponseWasClosedWithoutSendingAgain(
        bool newChat, bool newerDraft) => VerifyAcceptedSendResumeAsync(newChat, newerDraft);

    private async Task VerifyAcceptedSendResumeAsync(
        bool newChat, bool newerDraft, bool queuedPosts = false,
        Action<MobileShellViewModel>? showShell = null)
    {
        using var temp = new TempDirectory();
        var chatId = Guid.NewGuid();
        var unrelatedChatId = Guid.NewGuid();
        var snapshot = new RemoteSnapshot
        {
            ActiveChatId = queuedPosts ? unrelatedChatId : newChat ? null : chatId,
            ActiveChat = queuedPosts
                ? new RemoteChat { Id = unrelatedChatId, Title = "Previous desktop chat", MessageCount = 5 }
                : newChat ? null : new RemoteChat { Id = chatId, Title = "Existing", MessageCount = 5 }
        };
        var responseReading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteCommand? submitted = null;
        var receiptReads = 0;
        var handler = new TransportHandler(RemoteProtocol.Version, scopedEvents: true, compactTranscript: true)
        {
            SnapshotFactory = () => snapshot,
            TranscriptFactory = (id, before) =>
            {
                var accepted = id == chatId && submitted is not null;
                var receipt = accepted && before == (newChat ? 1 : 6);
                if (receipt)
                    Interlocked.Increment(ref receiptReads);
                return new RemoteTranscript
                {
                    ChatId = id,
                    Title = "Accepted mobile chat",
                    RevisionEpoch = "desktop",
                    Revision = accepted ? 2 : 1,
                    TotalRawMessageCount = accepted ? 500 : 5,
                    WindowStartMessageIndex = receipt ? (newChat ? 0 : 5) : accepted ? 450 : 0,
                    WindowEndMessageIndex = receipt ? before!.Value : accepted ? 500 : 5,
                    IsLatestWindow = !receipt,
                    Status = new RemoteChatStatus { ChatId = id, IsBusy = accepted, IsSessionActive = accepted },
                    Turns =
                    [
                        new()
                        {
                            Id = receipt ? "accepted-user-turn" : "latest-turn",
                            Items =
                            [
                                new()
                                {
                                    Id = receipt ? "accepted-user" : "latest-answer",
                                    Kind = receipt ? RemoteProtocol.ItemKinds.User : RemoteProtocol.ItemKinds.Assistant,
                                    Text = receipt ? "Sent from my phone" : "Working on the accepted message",
                                    RequestId = receipt ? submitted!.RequestId : null
                                }
                            ]
                        }
                    ]
                };
            },
            CommandResponseFactory = command =>
            {
                if (command.Action != RemoteProtocol.Actions.SendMessage)
                    return CommandResponse(command);

                submitted = command;
                var created = new RemoteChat
                {
                    Id = chatId, Title = "Accepted mobile chat", IsRunning = true,
                    IsSessionActive = true, MessageCount = 500, UpdatedAt = DateTimeOffset.UtcNow
                };
                snapshot = new RemoteSnapshot
                {
                    // The desktop's active chat is not evidence that this phone's send was accepted.
                    ActiveChatId = unrelatedChatId,
                    ActiveChat = new RemoteChat { Id = unrelatedChatId, Title = "Other desktop work" },
                    Chats = new RemoteChatPage
                    {
                        TotalCount = 1,
                        Groups = [new() { Label = "Today", Chats = [created] }]
                    }
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new PrefixBlockingStream(
                        [],
                        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                        socketErrorOnCancellation: true,
                        readStarted: responseReading))
                };
            }
        };
        await using var client = CreateClient(handler);
        await using var shell = new MobileShellViewModel(
            client, store: CreatePairedStore(temp.Path), post: queuedPosts ? null : action => action());
        showShell?.Invoke(shell);
        await shell.StartAsync();
        await WaitUntilAsync(() => shell.BootstrapSnapshotCount == 1 &&
                                   ((!queuedPosts && newChat) || (!shell.Chat.IsLoading && shell.Chat.TotalRawMessageCount == 5)));
        if (queuedPosts && newChat)
        {
            shell.ChatList.NewChatCommand.Execute(null);
            await WaitUntilAsync(() => shell.Chat.ChatId == Guid.Empty);
        }
        shell.Chat.PromptText = "Sent from my phone";
        var send = shell.Chat.SendCommand.ExecuteAsync(null);
        await responseReading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (newerDraft)
            shell.Chat.PromptText = "Keep this next draft";

        await shell.NotifyApplicationDeactivatedAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await send.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(shell.Chat.ErrorText);
        Assert.Equal(newerDraft ? "Keep this next draft" : "Sent from my phone", shell.Chat.PromptText);

        await shell.NotifyApplicationActivatedAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => shell.BootstrapSnapshotCount == 2);
        Assert.Contains(snapshot.Chats.Groups.SelectMany(group => group.Chats),
            chat => chat.Id == chatId && chat.IsRunning);
        try
        {
            await WaitUntilAsync(() => shell.Chat.ChatId == chatId && shell.Chat.ErrorText is null &&
                                       shell.Chat.IsBusy && !shell.Chat.IsLoading);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                $"Receipt recovery stalled: connected={shell.IsConnected}, client={client.State}, " +
                $"running={shell.Chat.SendCommand.IsRunning}, pending={shell.Chat.GetPendingSendReconciliations().Count}, " +
                $"receiptReads={receiptReads}, transcriptReads={handler.TranscriptRequests}, " +
                $"chat={shell.Chat.ChatId}, error={shell.Chat.ErrorText}", ex);
        }

        Assert.Equal(newerDraft ? "Keep this next draft" : "", shell.Chat.PromptText);
        Assert.Equal(chatId, shell.ChatList.SelectedChatId);
        Assert.Contains(shell.ChatList.Groups.SelectMany(group => group.Chats),
            chat => chat.Id == chatId && chat.IsRunning);
        Assert.Single(shell.ChatList.Groups.SelectMany(group => group.Chats), chat => chat.Id == chatId);
        Assert.True(receiptReads > 0, "The receipt is outside the latest transcript window.");
        Assert.Single(handler.Commands, command => command.Action == RemoteProtocol.Actions.SendMessage);
        Assert.DoesNotContain(handler.Commands, command => command.Action == RemoteProtocol.Actions.CreateChat);
    }

    [Fact]
    public async Task ResumeDoesNotTreatAnUnrelatedRunningSidebarChatAsSendAcceptance()
    {
        using var temp = new TempDirectory();
        var unrelatedId = Guid.NewGuid();
        var responseReading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TransportHandler(RemoteProtocol.Version, scopedEvents: true, compactTranscript: true)
        {
            SnapshotFactory = () => new RemoteSnapshot
            {
                Chats = new RemoteChatPage
                {
                    Groups = [new()
                    {
                        Label = "Today",
                        Chats = [new() { Id = unrelatedId, Title = "Continue", IsRunning = true, MessageCount = 1 }]
                    }]
                }
            },
            TranscriptFactory = (id, _) => new RemoteTranscript
            {
                ChatId = id,
                TotalRawMessageCount = 1,
                Status = new RemoteChatStatus { ChatId = id, IsBusy = true },
                Turns = [new()
                {
                    Id = "unrelated-turn",
                    Items = [new()
                    {
                        Id = "unrelated-user", Kind = RemoteProtocol.ItemKinds.User,
                        Text = "Continue", RequestId = "another-device-request"
                    }]
                }]
            },
            CommandResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new PrefixBlockingStream(
                    [],
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                    socketErrorOnCancellation: false,
                    readStarted: responseReading))
            }
        };
        await using var client = CreateClient(handler);
        await using var shell = new MobileShellViewModel(
            client, store: CreatePairedStore(temp.Path), post: action => action());
        await shell.StartAsync();
        await WaitUntilAsync(() => shell.BootstrapSnapshotCount == 1);
        shell.Chat.PromptText = "Continue";
        var send = shell.Chat.SendCommand.ExecuteAsync(null);
        await responseReading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await shell.NotifyApplicationDeactivatedAsync();
        await send;

        await shell.NotifyApplicationActivatedAsync();
        await WaitUntilAsync(() => shell.BootstrapSnapshotCount == 2);
        await shell.ReconcilePendingSendsAsync();

        Assert.True(handler.TranscriptRequests > 0);
        Assert.Equal(Guid.Empty, shell.Chat.ChatId);
        Assert.Equal("Continue", shell.Chat.PromptText);
        Assert.NotNull(shell.Chat.ErrorText);
        Assert.True(shell.Chat.SendCommand.CanExecute(null));
        Assert.Single(shell.Chat.GetPendingSendReconciliations());
        Assert.Single(handler.Commands, command => command.Action == RemoteProtocol.Actions.SendMessage);
        Assert.Contains("chats=true", handler.LastEventQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledSocketFailureRetainsAnUnknownOutcomeAndDoesNotRetryInBackground()
    {
        var responseReading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TransportHandler(RemoteProtocol.Version, scopedEvents: true, compactTranscript: true)
        {
            CommandResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new PrefixBlockingStream(
                    [],
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                    socketErrorOnCancellation: true,
                    readStarted: responseReading))
            }
        };
        await using var client = CreateClient(handler);
        client.Configure("http://100.85.249.111:47653", "token");
        client.MarkProtocolCompatibleForTests();
        using var cancellation = new CancellationTokenSource();
        var command = new RemoteCommand(RemoteProtocol.Actions.SendMessage).With("message", "Hello");

        var send = client.SendCommandAsync(command, cancellation.Token);
        await responseReading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = await send;

        Assert.False(result.Ok);
        Assert.True(result.IsOutcomeUnknown);
        Assert.Equal(command.RequestId, result.RequestId);
        Assert.Single(handler.Commands);
    }

    private static HttpResponseMessage CommandResponse(RemoteCommand command) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(
            new RemoteCommandResult { Ok = true, RequestId = command.RequestId },
            RemoteJsonContext.Default.RemoteCommandResult))
    };

    [Fact]
    public void PersistedCollapsedSidebarCanInitializeBeforeAnyConnection()
    {
        using var temp = new TempDirectory();
        var store = new MobileSettingsStore(temp.Path);
        store.Save(new MobileConnectionSettings
        {
            DeviceId = "device",
            DeviceName = "Phone",
            IsSidebarCollapsed = true
        });

        var exception = Record.Exception(() =>
        {
            var shell = new MobileShellViewModel(store: store, post: action => action());
            shell.DisposeAsync().AsTask().GetAwaiter().GetResult();
        });

        Assert.Null(exception);
    }

    [Fact]
    public async Task PartialCatalogSnapshotPreservesCachedChatsAndLibrary()
    {
        using var temp = new TempDirectory();
        await using var shell = new MobileShellViewModel(
            store: new MobileSettingsStore(temp.Path),
            post: action => action());
        var chatId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        ApplySnapshot(shell, new RemoteSnapshot
        {
            Chats = new RemoteChatPage
            {
                Groups =
                [
                    new RemoteChatGroup
                    {
                        Label = "Today",
                        Chats = [new RemoteChat { Id = chatId, Title = "Keep me" }]
                    }
                ]
            },
            Library = new RemoteLibrary
            {
                Projects = [new RemoteProject { Id = projectId, Name = "Keep project" }]
            }
        }, "Bootstrap");

        ApplySnapshot(shell, new RemoteSnapshot
        {
            IsPartial = true,
            Settings = new RemoteSettings { UserName = "Updated" }
        }, "CatalogEvent");

        Assert.Contains(
            shell.ChatList.Groups.SelectMany(group => group.Chats),
            chat => chat.Id == chatId);
        Assert.Contains(shell.Projects, project => project.Id == projectId);
        Assert.Equal("Updated", shell.UserName);
    }

    [Fact]
    public async Task ScopedEventsFollowTheVisibleSurfaceAndStopInBackground()
    {
        using var temp = new TempDirectory();
        var handler = new TransportHandler(
            protocolVersion: RemoteProtocol.Version,
            scopedEvents: true,
            compactTranscript: true);
        await using var client = CreateClient(handler);
        var store = CreatePairedStore(temp.Path);
        await using var shell = new MobileShellViewModel(
            client,
            store: store,
            post: action => action());

        await shell.StartAsync();
        await handler.EventRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => shell.IsConnected);
        await WaitUntilAsync(() => shell.BootstrapSnapshotCount > 0);
        Assert.Contains("chats=false", handler.LastEventQuery, StringComparison.Ordinal);
        Assert.Contains("compact=true", handler.LastEventQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("chatId=", handler.LastEventQuery, StringComparison.Ordinal);

        var chatId = Guid.NewGuid();
        shell.Chat.Reset(chatId, "Visible");
        var chatSubscription = await handler.WaitForSubscriptionAsync(
            subscription => subscription.ChatId == chatId && !subscription.IncludeChatList);
        Assert.Equal(chatId, chatSubscription.ChatId);
        Assert.False(chatSubscription.IncludeChatList);

        await SettleCountAsync(() => handler.TranscriptRequests);
        var chatsBeforeDrawer = handler.ChatRequests;
        var transcriptsBeforeDrawer = handler.TranscriptRequests;
        shell.IsDrawerOpen = true;
        var drawerSubscription = await handler.WaitForSubscriptionAsync(
            subscription => subscription.ChatId == chatId && subscription.IncludeChatList);
        Assert.True(drawerSubscription.IncludeChatList);
        await WaitUntilAsync(() => handler.ChatRequests > chatsBeforeDrawer);
        await Task.Delay(50);
        Assert.Equal(chatsBeforeDrawer + 1, handler.ChatRequests);
        Assert.Equal(transcriptsBeforeDrawer, handler.TranscriptRequests);

        shell.ShowPageCommand.Execute("Library");
        await handler.WaitForSubscriptionAsync(
            subscription => subscription.ChatId is null && subscription.IncludeLibrary);
        var transcriptsBeforeReturn = handler.TranscriptRequests;

        shell.Page = MobilePage.Chat;
        await handler.WaitForSubscriptionAsync(
            subscription => subscription.ChatId == chatId && !subscription.IncludeLibrary);
        await WaitUntilAsync(() => handler.TranscriptRequests > transcriptsBeforeReturn);
        await Task.Delay(50);
        Assert.Equal(transcriptsBeforeReturn + 1, handler.TranscriptRequests);

        var pause = shell.NotifyApplicationDeactivatedAsync();
        var resume = shell.NotifyApplicationActivatedAsync();
        await Task.WhenAll(pause, resume);
        await WaitUntilAsync(() => shell.IsConnected);
        Assert.True(handler.EventRequests >= 1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StartAndResumeRetryATransientHelloFailureWithoutRestartingOrLosingTheDraft(
        bool resume, bool transportCancellation)
    {
        using var temp = new TempDirectory();
        var handler = new TransportHandler(
            protocolVersion: RemoteProtocol.Version,
            scopedEvents: true,
            compactTranscript: true)
        {
            FailHelloRequest = resume ? 2 : 1,
            CancelFailedHelloRequest = transportCancellation
        };
        await using var client = CreateClient(handler);
        var store = CreatePairedStore(temp.Path);
        await using var shell = new MobileShellViewModel(
            client,
            store: store,
            post: action => action());

        if (resume)
        {
            await shell.StartAsync();
            await WaitUntilAsync(() => shell.BootstrapSnapshotCount == 1);
            await shell.NotifyApplicationDeactivatedAsync();
        }
        shell.Chat.PromptText = "Keep this unsent draft";

        // The network is unavailable for the launch/resume handshake, then immediately recovers.
        await shell.NotifyApplicationActivatedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => shell.IsConnected && shell.BootstrapSnapshotCount == (resume ? 2 : 1));

        Assert.Equal(resume ? 3 : 2, handler.HelloRequests);
        Assert.Equal("Keep this unsent draft", shell.Chat.PromptText);
        Assert.True(shell.IsPaired);
        Assert.Equal("token", store.Load().Token);
        Assert.Equal(0, handler.SnapshotRequests);
    }

    [Fact]
    public async Task PausingDuringHandshakeBackoffCancelsTheAttemptAndResumeCanReconnect()
    {
        using var temp = new TempDirectory();
        var handler = new TransportHandler(
            protocolVersion: RemoteProtocol.Version,
            scopedEvents: true,
            compactTranscript: true)
        {
            FailHelloRequest = 1
        };
        await using var client = CreateClient(handler);
        await using var shell = new MobileShellViewModel(
            client,
            store: CreatePairedStore(temp.Path),
            post: action => action());
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += (state, _) =>
        {
            if (state == RemoteLinkState.Error)
                failed.TrySetResult();
        };

        var start = shell.StartAsync();
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await shell.NotifyApplicationDeactivatedAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await start.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, handler.HelloRequests);
        Assert.Equal(0, handler.EventRequests);
        Assert.True(shell.IsPaired);

        await shell.NotifyApplicationActivatedAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => shell.IsConnected);
        Assert.Equal(2, handler.HelloRequests);
        Assert.Equal(1, handler.EventRequests);
    }

    [Fact]
    public async Task PausingDuringHandshakeCannotResurrectBackgroundTransport()
    {
        using var temp = new TempDirectory();
        var handler = new BlockingHelloHandler();
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            requestDeadline: TimeSpan.FromSeconds(5),
            uploadDeadline: TimeSpan.FromSeconds(1),
            routeVerifier: new TrustedRouteVerifier());
        await using var shell = new MobileShellViewModel(
            client,
            store: CreatePairedStore(temp.Path),
            post: action => action());

        var start = shell.StartAsync();
        await handler.HelloStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await shell.NotifyApplicationDeactivatedAsync();
        await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await start;

        Assert.Equal(0, handler.EventRequests);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected mobile transport state was not reached.");
            await Task.Delay(10);
        }
    }

    private static async Task<int> SettleCountAsync(Func<int> count)
    {
        var previous = count();
        for (var stableSamples = 0; stableSamples < 3;)
        {
            await Task.Delay(30);
            var current = count();
            if (current == previous)
            {
                stableSamples++;
                continue;
            }

            previous = current;
            stableSamples = 0;
        }

        return previous;
    }

    private static void ApplySnapshot(
        MobileShellViewModel shell,
        RemoteSnapshot snapshot,
        string sourceName)
    {
        var method = typeof(MobileShellViewModel).GetMethod(
            "ApplySnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sourceType = method.GetParameters()[1].ParameterType;
        method.Invoke(shell, [snapshot, Enum.Parse(sourceType, sourceName)]);
    }

    private static LumiRemoteClient CreateClient(TransportHandler handler) =>
        new(
            "device",
            "Phone",
            handler,
            requestDeadline: TimeSpan.FromSeconds(1),
            uploadDeadline: TimeSpan.FromSeconds(1),
            routeVerifier: new TrustedRouteVerifier());

    private static MobileSettingsStore CreatePairedStore(string directory)
    {
        var store = new MobileSettingsStore(directory);
        store.Save(new MobileConnectionSettings
        {
            DeviceId = "device",
            DeviceName = "Phone",
            BaseUrl = "http://100.85.249.111:47653",
            Token = "token",
            HostName = "Lumi PC"
        });
        return store;
    }

    private sealed class TransportHandler(
        int protocolVersion,
        bool scopedEvents,
        bool compactTranscript) : HttpMessageHandler
    {
        private readonly List<TaskCompletionSource<RemoteEventSubscription>> _subscriptions = [];
        private int _eventRequests;

        public int HelloRequests { get; private set; }
        public int FailHelloRequest { get; init; }
        public bool CancelFailedHelloRequest { get; init; }
        public int SnapshotRequests { get; private set; }
        public int TranscriptRequests { get; private set; }
        public int ChatRequests { get; private set; }
        public System.Collections.Concurrent.ConcurrentQueue<RemoteCommand> Commands { get; } = new();
        public Func<RemoteSnapshot>? SnapshotFactory { get; init; }
        public Func<Guid, int?, RemoteTranscript>? TranscriptFactory { get; init; }
        public Func<RemoteCommand, HttpResponseMessage>? CommandResponseFactory { get; init; }
        public int EventRequests => Volatile.Read(ref _eventRequests);
        public string LastEventQuery { get; private set; } = "";
        public TaskCompletionSource EventRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondEventRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EventCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TranscriptRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RemoteEventSubscription> WaitForSubscriptionAsync(int count)
        {
            lock (_subscriptions)
            {
                while (_subscriptions.Count < count)
                {
                    _subscriptions.Add(new TaskCompletionSource<RemoteEventSubscription>(
                        TaskCreationOptions.RunContinuationsAsynchronously));
                }

                return _subscriptions[count - 1].Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        public async Task<RemoteEventSubscription> WaitForSubscriptionAsync(
            Func<RemoteEventSubscription, bool> predicate)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (true)
            {
                lock (_subscriptions)
                {
                    var match = _subscriptions
                        .Where(completion => completion.Task.IsCompletedSuccessfully)
                        .Select(completion => completion.Task.Result)
                        .LastOrDefault(predicate);
                    if (match is not null)
                        return match;
                }

                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("The expected subscription was not received.");
                await Task.Delay(10);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (path == RemoteProtocol.Routes.Hello)
            {
                HelloRequests++;
                if (HelloRequests == FailHelloRequest)
                {
                    if (CancelFailedHelloRequest)
                        throw new TaskCanceledException("The transport canceled this attempt.");
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }
                return Json(
                    new RemoteHello
                    {
                        ProtocolVersion = protocolVersion,
                        Capabilities = Capabilities(),
                        IsPaired = true
                    },
                    RemoteJsonContext.Default.RemoteHello);
            }

            if (path == RemoteProtocol.Routes.Snapshot)
            {
                SnapshotRequests++;
                return Json(Snapshot(), RemoteJsonContext.Default.RemoteSnapshot);
            }

            if (path == RemoteProtocol.Routes.Transcript)
            {
                TranscriptRequests++;
                TranscriptRequested.TrySetResult();
                var chatId = Guid.TryParse(request.RequestUri?.Query
                        .Split("chatId=", StringSplitOptions.RemoveEmptyEntries)
                        .LastOrDefault()?
                        .Split('&')[0],
                    out var parsed)
                    ? parsed
                    : Guid.Empty;
                var before = int.TryParse(request.RequestUri?.Query
                    .Split("beforeMessageIndex=", StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Split('&')[0],
                    out var beforeIndex) ? beforeIndex : (int?)null;
                if (TranscriptFactory is { } transcriptFactory)
                    return Json(transcriptFactory(chatId, before), RemoteJsonContext.Default.RemoteTranscript);
                return Json(
                    new RemoteTranscript
                    {
                        ChatId = chatId,
                        Revision = TranscriptRequests,
                        Status = new RemoteChatStatus { ChatId = chatId }
                    },
                    RemoteJsonContext.Default.RemoteTranscript);
            }

            if (path == RemoteProtocol.Routes.Chats)
            {
                ChatRequests++;
                return Json(new RemoteChatPage(), RemoteJsonContext.Default.RemoteChatPage);
            }

            if (path == RemoteProtocol.Routes.Command)
            {
                var command = JsonSerializer.Deserialize(
                    await request.Content!.ReadAsStringAsync(cancellationToken),
                    RemoteJsonContext.Default.RemoteCommand)!;
                Commands.Enqueue(command);
                return CommandResponseFactory?.Invoke(command) ?? CommandResponse(command);
            }

            if (path == RemoteProtocol.Routes.Subscription)
            {
                var subscription = JsonSerializer.Deserialize(
                    await request.Content!.ReadAsStringAsync(cancellationToken),
                    RemoteJsonContext.Default.RemoteEventSubscription)!;
                TaskCompletionSource<RemoteEventSubscription> completion;
                lock (_subscriptions)
                {
                    completion = _subscriptions.Count > 0
                        ? _subscriptions.FirstOrDefault(item => !item.Task.IsCompleted)
                          ?? NewSubscriptionCompletion()
                        : NewSubscriptionCompletion();
                }
                completion.TrySetResult(subscription);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                };
            }

            if (path == RemoteProtocol.Routes.Events)
            {
                var eventRequest = Interlocked.Increment(ref _eventRequests);
                LastEventQuery = request.RequestUri?.Query ?? "";
                EventRequested.TrySetResult();
                if (eventRequest >= 2)
                    SecondEventRequested.TrySetResult();

                var frame = new RemoteEventFrame(
                    RemoteProtocol.Events.Snapshot,
                    JsonSerializer.Serialize(Snapshot(), RemoteJsonContext.Default.RemoteSnapshot));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new PrefixBlockingStream(
                        Encoding.UTF8.GetBytes(frame.ToWire()),
                        EventCancellationObserved))
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private TaskCompletionSource<RemoteEventSubscription> NewSubscriptionCompletion()
        {
            var completion = new TaskCompletionSource<RemoteEventSubscription>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _subscriptions.Add(completion);
            return completion;
        }

        private RemoteSnapshot Snapshot()
        {
            var snapshot = SnapshotFactory?.Invoke() ?? new RemoteSnapshot { HostName = "Lumi PC" };
            snapshot.ProtocolVersion = protocolVersion;
            snapshot.Capabilities = Capabilities();
            return snapshot;
        }

        private List<string> Capabilities()
        {
            var capabilities = new List<string>();
            if (scopedEvents)
                capabilities.Add(RemoteProtocol.Capabilities.ScopedEventsV1);
            if (compactTranscript)
                capabilities.Add(RemoteProtocol.Capabilities.CompactTranscriptV1);
            return capabilities;
        }

        private static HttpResponseMessage Json<T>(
            T value,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(value, typeInfo),
                    Encoding.UTF8,
                    "application/json")
            };
    }

    private sealed class PrefixBlockingStream(
        byte[] prefix,
        TaskCompletionSource cancellationObserved,
        bool socketErrorOnCancellation = false,
        TaskCompletionSource? readStarted = null) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_offset < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            try
            {
                readStarted?.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult();
                if (socketErrorOnCancellation)
                    throw new IOException("Socket closed");
                throw;
            }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingHelloHandler : HttpMessageHandler
    {
        public int EventRequests { get; private set; }
        public TaskCompletionSource HelloStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == RemoteProtocol.Routes.Hello)
            {
                HelloStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved.TrySetResult();
                    throw;
                }
            }

            if (request.RequestUri?.AbsolutePath == RemoteProtocol.Routes.Events)
                EventRequests++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class TrustedRouteVerifier : IRemoteRouteVerifier
    {
        public bool IsTrustedTailscaleRoute(IPAddress targetAddress) => true;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "lumi-mobile-transport-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
