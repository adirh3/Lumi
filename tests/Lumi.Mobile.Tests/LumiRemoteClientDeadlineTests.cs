using System.Net;
using System.Text;
using System.Text.Json;
using Lumi.Mobile.Services;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class LumiRemoteClientDeadlineTests
{
    [Fact]
    public async Task CommandIsBlockedUntilACompatibleBootstrapCompletes()
    {
        var handler = new RecordingCommandHandler();
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            requestDeadline: TimeSpan.FromSeconds(1),
            uploadDeadline: TimeSpan.FromSeconds(1));
        client.Configure("http://lumi.test", "token");

        var result = await client.SendCommandAsync(
            new RemoteCommand(RemoteProtocol.Actions.CreateChat),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("compatible", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.Command);
    }

    [Fact]
    public async Task IncompatibleProtocolCannotBootstrapOrReceiveCommands()
    {
        var handler = new Protocol3Handler();
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            requestDeadline: TimeSpan.FromSeconds(1),
            uploadDeadline: TimeSpan.FromSeconds(1));

        var hello = await client.HelloAsync("http://lumi.test", CancellationToken.None);
        client.Configure("http://lumi.test", "token");
        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);
        var result = await client.SendCommandAsync(
            new RemoteCommand(RemoteProtocol.Actions.CreateChat),
            CancellationToken.None);

        Assert.Equal(3, hello?.ProtocolVersion);
        Assert.Null(snapshot);
        Assert.False(result.Ok);
        Assert.Null(handler.Command);
        Assert.Contains("compatible", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IncompatibleHelloClearsCapabilitiesFromTheSameHost()
    {
        var handler = new CompatibleThenIncompatibleHelloHandler();
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            requestDeadline: TimeSpan.FromSeconds(1),
            uploadDeadline: TimeSpan.FromSeconds(1));

        var compatible = await client.HelloAsync("http://lumi.test", CancellationToken.None);
        client.Configure("http://lumi.test", "token");
        Assert.NotNull(compatible);
        Assert.True(client.SupportsScopedEvents);

        var incompatible = await client.HelloAsync("http://lumi.test", CancellationToken.None);
        var command = await client.SendCommandAsync(
            new RemoteCommand(RemoteProtocol.Actions.CreateChat),
            CancellationToken.None);

        Assert.NotNull(incompatible);
        Assert.False(client.SupportsScopedEvents);
        Assert.Equal(0, client.ConnectedProtocolVersion);
        Assert.False(command.Ok);
        Assert.Null(handler.Command);
    }

    [Fact]
    public async Task FiniteRequestTimeoutReturnsTheExistingTimeoutMessage()
    {
        var handler = new BlockingHandler();
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            requestDeadline: TimeSpan.FromSeconds(1),
            uploadDeadline: TimeSpan.FromSeconds(2));
        client.Configure("http://lumi.test", "token");
        client.MarkProtocolCompatibleForTests();

        var command = new RemoteCommand(RemoteProtocol.Actions.CreateChat);
        var request = client.SendCommandAsync(command, CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var result = await request.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Contains("too long", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.IsTimeout);
        Assert.False(string.IsNullOrWhiteSpace(command.RequestId));
        Assert.Equal(command.RequestId, result.RequestId);
    }

    [Fact]
    public async Task TimedOutCommandConfirmsWithTheSameRequestIdInsteadOfReportingFailure()
    {
        var handler = new TimeoutThenSuccessHandler();
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            requestDeadline: TimeSpan.FromMilliseconds(50),
            uploadDeadline: TimeSpan.FromSeconds(1),
            commandConfirmationDeadline: TimeSpan.FromSeconds(1));
        client.Configure("http://lumi.test", "token");
        client.MarkProtocolCompatibleForTests();
        var command = new RemoteCommand(RemoteProtocol.Actions.SendMessage)
            .With("message", "hello");

        var result = await client.SendCommandAsync(command, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, handler.RequestIds.Count);
        Assert.False(string.IsNullOrWhiteSpace(command.RequestId));
        Assert.All(handler.RequestIds, id => Assert.Equal(command.RequestId, id));
    }

    [Fact]
    public async Task TransportFailureConfirmsWithTheSameRequestIdInsteadOfDuplicatingTheCommand()
    {
        var handler = new TransportFailureThenSuccessHandler();
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            requestDeadline: TimeSpan.FromSeconds(1),
            uploadDeadline: TimeSpan.FromSeconds(1),
            commandConfirmationDeadline: TimeSpan.FromSeconds(1));
        client.Configure("http://lumi.test", "token");
        client.MarkProtocolCompatibleForTests();
        var command = new RemoteCommand(RemoteProtocol.Actions.SendMessage)
            .With("message", "hello");

        var result = await client.SendCommandAsync(command, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, handler.RequestIds.Count);
        Assert.All(handler.RequestIds, id => Assert.Equal(command.RequestId, id));
    }

    [Fact]
    public async Task TimedOutRevocationIsNotRetriedBecauseTheTokenMayAlreadyBeInvalid()
    {
        var handler = new BlockingHandler();
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            requestDeadline: TimeSpan.FromMilliseconds(50),
            uploadDeadline: TimeSpan.FromSeconds(1),
            commandConfirmationDeadline: TimeSpan.FromSeconds(1));
        client.Configure("http://lumi.test", "token");
        client.MarkProtocolCompatibleForTests();

        var result = await client.SendCommandAsync(
            new RemoteCommand(RemoteProtocol.Actions.RevokeDevice),
            CancellationToken.None);

        Assert.True(result.IsTimeout);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FailedConfirmationKeepsTheOriginalTimeoutAndRequestId()
    {
        var handler = new TimeoutThenTransportFailureHandler();
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            requestDeadline: TimeSpan.FromMilliseconds(50),
            uploadDeadline: TimeSpan.FromSeconds(1),
            commandConfirmationDeadline: TimeSpan.FromSeconds(1));
        client.Configure("http://lumi.test", "token");
        client.MarkProtocolCompatibleForTests();
        var command = new RemoteCommand(RemoteProtocol.Actions.SendMessage)
            .With("message", "hello");

        var result = await client.SendCommandAsync(command, CancellationToken.None);

        Assert.True(result.IsTimeout);
        Assert.Equal(command.RequestId, result.RequestId);
        Assert.Equal(2, handler.RequestIds.Count);
        Assert.All(handler.RequestIds, id => Assert.Equal(command.RequestId, id));
    }

    [Fact]
    public async Task ConfirmationWithoutAnEchoedRequestIdRemainsAmbiguous()
    {
        var handler = new TimeoutThenGenericErrorHandler();
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            requestDeadline: TimeSpan.FromMilliseconds(50),
            uploadDeadline: TimeSpan.FromSeconds(1),
            commandConfirmationDeadline: TimeSpan.FromSeconds(1));
        client.Configure("http://lumi.test", "token");
        client.MarkProtocolCompatibleForTests();
        var command = new RemoteCommand(RemoteProtocol.Actions.SendMessage)
            .With("message", "hello");

        var result = await client.SendCommandAsync(command, CancellationToken.None);

        Assert.True(result.IsTimeout);
        Assert.Equal(command.RequestId, result.RequestId);
    }

    [Fact]
    public async Task CommandRequestIdIsAssignedOnceAndPreservedInTheResult()
    {
        var handler = new RecordingCommandHandler();
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            requestDeadline: TimeSpan.FromSeconds(1),
            uploadDeadline: TimeSpan.FromSeconds(1));
        client.Configure("http://lumi.test", "token");
        client.MarkProtocolCompatibleForTests();
        var command = new RemoteCommand(RemoteProtocol.Actions.CreateChat)
        {
            RequestId = "retry-this-id"
        };

        var result = await client.SendCommandAsync(command, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("retry-this-id", command.RequestId);
        Assert.Equal("retry-this-id", handler.Command?.RequestId);
        Assert.Equal("retry-this-id", result.RequestId);
        Assert.False(result.IsTimeout);
    }

    [Fact]
    public async Task SseReconnectDoesNotInvalidateAnAlreadyCompatibleCommandChannel()
    {
        var handler = new DroppingEventHandler();
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            requestDeadline: TimeSpan.FromSeconds(1),
            uploadDeadline: TimeSpan.FromSeconds(1));
        client.Configure("http://lumi.test", "token");
        client.MarkProtocolCompatibleForTests(scopedEvents: true);
        var bootstrapped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameReceived += frame =>
        {
            if (frame.Event == RemoteProtocol.Events.Snapshot)
                bootstrapped.TrySetResult();
        };

        await client.StartEventStreamAsync(new RemoteEventSubscription
        {
            Generation = 1,
            IsForeground = true
        });
        await bootstrapped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        var result = await client.SendCommandAsync(
            new RemoteCommand(RemoteProtocol.Actions.CreateChat),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.NotNull(handler.Command);
        await client.StopEventStreamAsync();
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsARequestTimeout()
    {
        var handler = new BlockingHandler();
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            requestDeadline: TimeSpan.FromSeconds(5),
            uploadDeadline: TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();

        var request = client.HelloAsync("http://lumi.test", cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.NotEqual(RemoteLinkState.Error, client.State);
    }

    [Fact]
    public async Task UploadUsesItsLongerDeadline()
    {
        var handler = new DelayedJsonHandler(TimeSpan.FromMilliseconds(120));
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            requestDeadline: TimeSpan.FromMilliseconds(40),
            uploadDeadline: TimeSpan.FromSeconds(1));
        client.Configure("http://lumi.test", "token");
        client.MarkProtocolCompatibleForTests();

        var command = await client.SendCommandAsync(
            new RemoteCommand(RemoteProtocol.Actions.CreateChat),
            CancellationToken.None);
        var upload = await client.UploadAsync("note.txt", "hello"u8.ToArray(), CancellationToken.None);

        Assert.Contains("too long", command.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(upload.Ok, upload.Error);
        Assert.Equal("note.txt", upload.FileName);
    }

    [Fact]
    public async Task EventStreamDoesNotUseTheFiniteRequestDeadline()
    {
        var handler = new BlockingHandler();
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            requestDeadline: TimeSpan.FromMilliseconds(40),
            uploadDeadline: TimeSpan.FromMilliseconds(80));
        client.Configure("http://lumi.test", "token");

        await client.StartEventStreamAsync();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(150);

        Assert.False(handler.CancellationObserved.Task.IsCompleted);

        await client.StopEventStreamAsync();
        await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task OversizedHandshakeResponseIsRejectedWithinProtocolLimit()
    {
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            new OversizedHandler(),
            requestDeadline: TimeSpan.FromSeconds(1),
            uploadDeadline: TimeSpan.FromSeconds(1));

        var hello = await client.HelloAsync("http://lumi.test", CancellationToken.None);

        Assert.Null(hello);
        Assert.Equal(RemoteLinkState.Error, client.State);
        Assert.Contains("protocol limit", client.StateMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequestTimeoutDoesNotDemoteAnActiveEventConnection()
    {
        var handler = new BlockingHandler();
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            requestDeadline: TimeSpan.FromMilliseconds(50),
            uploadDeadline: TimeSpan.FromSeconds(1));
        client.Configure("http://lumi.test", "token");
        typeof(LumiRemoteClient).GetProperty(nameof(LumiRemoteClient.State))!
            .SetValue(client, RemoteLinkState.Connected);

        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);

        Assert.Null(snapshot);
        Assert.Equal(RemoteLinkState.Connected, client.State);
        Assert.Contains("too long", client.StateMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StoredPairingRejectsMismatchedBootstrapProtocol()
    {
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            new MismatchedSnapshotHandler(),
            requestDeadline: TimeSpan.FromSeconds(1),
            uploadDeadline: TimeSpan.FromSeconds(1));
        client.Configure("http://lumi.test", "token");
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += (state, _) =>
        {
            if (state == RemoteLinkState.Error)
                failed.TrySetResult();
        };

        await client.StartEventStreamAsync();
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotEqual(RemoteLinkState.Connected, client.State);
        await client.StopEventStreamAsync();
    }

    private sealed class Protocol3Handler : HttpMessageHandler
    {
        public RemoteCommand? Command { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == RemoteProtocol.Routes.Hello)
            {
                return JsonResponse(
                    new RemoteHello { ProtocolVersion = 3, HostName = "Release Lumi" },
                    RemoteJsonContext.Default.RemoteHello);
            }

            if (request.RequestUri?.AbsolutePath == RemoteProtocol.Routes.Snapshot)
            {
                return JsonResponse(
                    new RemoteSnapshot { ProtocolVersion = 3, HostName = "Release Lumi" },
                    RemoteJsonContext.Default.RemoteSnapshot);
            }

            if (request.RequestUri?.AbsolutePath == RemoteProtocol.Routes.Command)
            {
                Command = JsonSerializer.Deserialize(
                    await request.Content!.ReadAsStringAsync(cancellationToken),
                    RemoteJsonContext.Default.RemoteCommand);
                return JsonResponse(
                    new RemoteCommandResult { Ok = true, RequestId = Command?.RequestId },
                    RemoteJsonContext.Default.RemoteCommandResult);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResponse<T>(
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

    private sealed class CompatibleThenIncompatibleHelloHandler : HttpMessageHandler
    {
        private int _helloCount;

        public RemoteCommand? Command { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == RemoteProtocol.Routes.Hello)
            {
                var isFirst = Interlocked.Increment(ref _helloCount) == 1;
                return JsonResponse(
                    new RemoteHello
                    {
                        ProtocolVersion = isFirst ? RemoteProtocol.Version : RemoteProtocol.Version - 1,
                        Capabilities = isFirst
                            ? [RemoteProtocol.Capabilities.ScopedEventsV1]
                            : []
                    },
                    RemoteJsonContext.Default.RemoteHello);
            }

            if (request.RequestUri?.AbsolutePath == RemoteProtocol.Routes.Command)
            {
                Command = JsonSerializer.Deserialize(
                    await request.Content!.ReadAsStringAsync(cancellationToken),
                    RemoteJsonContext.Default.RemoteCommand);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        }

        private static HttpResponseMessage JsonResponse<T>(
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

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Started.TrySetResult();
            using var registration = cancellationToken.Register(() =>
            {
                CancellationObserved.TrySetResult();
            });
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking handler completed without cancellation.");
        }

    }

    private sealed class DroppingEventHandler : HttpMessageHandler
    {
        public RemoteCommand? Command { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == RemoteProtocol.Routes.Events)
            {
                var snapshot = new RemoteSnapshot
                {
                    Capabilities = [RemoteProtocol.Capabilities.ScopedEventsV1]
                };
                var frame = new RemoteEventFrame(
                    RemoteProtocol.Events.Snapshot,
                    JsonSerializer.Serialize(snapshot, RemoteJsonContext.Default.RemoteSnapshot));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(frame.ToWire(), Encoding.UTF8, "text/event-stream")
                };
            }

            if (request.RequestUri?.AbsolutePath == RemoteProtocol.Routes.Command)
            {
                Command = JsonSerializer.Deserialize(
                    await request.Content!.ReadAsStringAsync(cancellationToken),
                    RemoteJsonContext.Default.RemoteCommand);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(
                            new RemoteCommandResult { Ok = true, RequestId = Command?.RequestId },
                            RemoteJsonContext.Default.RemoteCommandResult),
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        }
    }

    private sealed class TimeoutThenSuccessHandler : HttpMessageHandler
    {
        public List<string?> RequestIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var command = JsonSerializer.Deserialize(body, RemoteJsonContext.Default.RemoteCommand);
            RequestIds.Add(command?.RequestId);
            if (RequestIds.Count == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The first request should time out.");
            }

            var response = JsonSerializer.Serialize(
                new RemoteCommandResult
                {
                    Ok = true,
                    RequestId = command?.RequestId
                },
                RemoteJsonContext.Default.RemoteCommandResult);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class TimeoutThenTransportFailureHandler : HttpMessageHandler
    {
        public List<string?> RequestIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var command = JsonSerializer.Deserialize(body, RemoteJsonContext.Default.RemoteCommand);
            RequestIds.Add(command?.RequestId);
            if (RequestIds.Count == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The first request should time out.");
            }

            throw new HttpRequestException("connection reset");
        }
    }

    private sealed class TimeoutThenGenericErrorHandler : HttpMessageHandler
    {
        private int _requests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (++_requests == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The first request should time out.");
            }

            var response = JsonSerializer.Serialize(
                new RemoteCommandResult { Error = "Server unavailable." },
                RemoteJsonContext.Default.RemoteCommandResult);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class DelayedJsonHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);

            var upload = new RemoteUploadResponse
            {
                Ok = true,
                Path = @"C:\Temp\note.txt",
                FileName = "note.txt"
            };
            var command = new RemoteCommandResult { Ok = true };
            var json = request.RequestUri?.AbsolutePath == RemoteProtocol.Routes.Upload
                ? JsonSerializer.Serialize(upload, RemoteJsonContext.Default.RemoteUploadResponse)
                : JsonSerializer.Serialize(command, RemoteJsonContext.Default.RemoteCommandResult);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class RecordingCommandHandler : HttpMessageHandler
    {
        public RemoteCommand? Command { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Command = JsonSerializer.Deserialize(body, RemoteJsonContext.Default.RemoteCommand);
            var response = JsonSerializer.Serialize(
                new RemoteCommandResult
                {
                    Ok = true,
                    RequestId = Command?.RequestId
                },
                RemoteJsonContext.Default.RemoteCommandResult);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class TransportFailureThenSuccessHandler : HttpMessageHandler
    {
        public List<string?> RequestIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var command = JsonSerializer.Deserialize(body, RemoteJsonContext.Default.RemoteCommand);
            RequestIds.Add(command?.RequestId);
            if (RequestIds.Count == 1)
                throw new HttpRequestException("connection reset after write");

            var response = JsonSerializer.Serialize(
                new RemoteCommandResult
                {
                    Ok = true,
                    RequestId = command?.RequestId
                },
                RemoteJsonContext.Default.RemoteCommandResult);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class OversizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[RemoteProtocol.MaxHandshakeJsonBytes + 1])
            });
    }

    private sealed class MismatchedSnapshotHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var snapshot = new RemoteSnapshot { ProtocolVersion = RemoteProtocol.Version + 1 };
            var frame = new RemoteEventFrame(
               RemoteProtocol.Events.Snapshot,
               JsonSerializer.Serialize(snapshot, RemoteJsonContext.Default.RemoteSnapshot));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
               Content = new StringContent(frame.ToWire(), Encoding.UTF8, "text/event-stream")
            });
        }
    }
}
