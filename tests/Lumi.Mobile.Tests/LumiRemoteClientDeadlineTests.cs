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

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            using var registration = cancellationToken.Register(() =>
            {
                CancellationObserved.TrySetResult();
            });
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking handler completed without cancellation.");
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
                new RemoteCommandResult { Ok = true },
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
