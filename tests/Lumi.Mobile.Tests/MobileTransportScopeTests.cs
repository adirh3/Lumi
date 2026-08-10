using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class MobileTransportScopeTests
{
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
            scopedEvents: true);
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
        Assert.DoesNotContain("chatId=", handler.LastEventQuery, StringComparison.Ordinal);

        var chatId = Guid.NewGuid();
        shell.Chat.Reset(chatId, "Visible");
        var chatSubscription = await handler.WaitForSubscriptionAsync(
            subscription => subscription.ChatId == chatId && !subscription.IncludeChatList);
        Assert.Equal(chatId, chatSubscription.ChatId);
        Assert.False(chatSubscription.IncludeChatList);

        var chatsBeforeDrawer = handler.ChatRequests;
        shell.IsDrawerOpen = true;
        var drawerSubscription = await handler.WaitForSubscriptionAsync(
            subscription => subscription.ChatId == chatId && subscription.IncludeChatList);
        Assert.True(drawerSubscription.IncludeChatList);
        await WaitUntilAsync(() => handler.ChatRequests > chatsBeforeDrawer);
        await Task.Delay(50);
        Assert.Equal(chatsBeforeDrawer + 1, handler.ChatRequests);
        Assert.Equal(0, handler.TranscriptRequests);

        shell.IsDrawerOpen = false;
        shell.Page = MobilePage.Library;
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
        bool scopedEvents) : HttpMessageHandler
    {
        private readonly List<TaskCompletionSource<RemoteEventSubscription>> _subscriptions = [];
        private int _eventRequests;

        public int HelloRequests { get; private set; }
        public int SnapshotRequests { get; private set; }
        public int TranscriptRequests { get; private set; }
        public int ChatRequests { get; private set; }
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
                return Json(
                    new RemoteHello
                    {
                        ProtocolVersion = protocolVersion,
                        Capabilities = scopedEvents
                            ? [RemoteProtocol.Capabilities.ScopedEventsV1]
                            : []
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
                var stream = new PrefixBlockingStream(
                    Encoding.UTF8.GetBytes(frame.ToWire()),
                    EventCancellationObserved);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(stream)
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

        private RemoteSnapshot Snapshot() => new()
        {
            ProtocolVersion = protocolVersion,
            Capabilities = scopedEvents
                ? [RemoteProtocol.Capabilities.ScopedEventsV1]
                : [],
            HostName = "Lumi PC"
        };

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
        TaskCompletionSource cancellationObserved) : Stream
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
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult();
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
