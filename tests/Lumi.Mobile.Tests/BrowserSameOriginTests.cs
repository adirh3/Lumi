using System.Net;
using System.Text;
using System.Text.Json;
using Lumi.Mobile.Browser;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class BrowserSameOriginTests
{
    [Theory]
    [InlineData("https://private-47654.uks1.devtunnels.ms/api/hello", true)]
    [InlineData("https://private-47654.uks1.devtunnels.ms:443/api/hello", true)]
    [InlineData("https://other-47654.uks1.devtunnels.ms/api/hello", false)]
    [InlineData("https://private-47654.uks1.devtunnels.ms.attacker.test/api/hello", false)]
    [InlineData("http://private-47654.uks1.devtunnels.ms/api/hello", false)]
    [InlineData("https://private-47654.uks1.devtunnels.ms:8443/api/hello", false)]
    [InlineData("https://user@private-47654.uks1.devtunnels.ms/api/hello", false)]
    public async Task CredentialsNeverLeaveTheOriginThatServedThePwa(string target, bool allowed)
    {
        var transport = new CaptureHandler();
        using var client = new HttpClient(new BrowserSameOriginHandler(
            new Uri("https://private-47654.uks1.devtunnels.ms"), transport));
        client.DefaultRequestHeaders.Add(RemoteProtocol.DeviceTokenHeader, "test-pairing-token");
        if (allowed)
        {
            using var response = await client.GetAsync(target);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, transport.RequestCount);
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(target));
            Assert.Equal(0, transport.RequestCount);
        }
    }

    [Theory]
    [InlineData("http://100.64.0.10:47653")]
    [InlineData("https://my-pc.example.ts.net")]
    [InlineData("http://192.168.1.10:47653")]
    public async Task ExistingTailscaleAndExplicitLanPwaOriginsStillWork(string origin)
    {
        var transport = new CaptureHandler();
        using var client = new HttpClient(new BrowserSameOriginHandler(new Uri(origin), transport));
        using var response = await client.GetAsync(origin + "/api/hello");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, transport.RequestCount);
    }

    [Fact]
    public async Task LiveBrowserRequestsIncludeSameOriginCookiesAndNeverUseTheHttpCache()
    {
        const string origin = "https://private-47654.uks1.devtunnels.ms";
        var transport = new CaptureHandler();
        using var client = new HttpClient(new BrowserSameOriginHandler(new Uri(origin), transport));

        using var response = await client.GetAsync(origin + RemoteProtocol.Routes.Hello);

        Assert.NotNull(transport.FetchOptions);
        Assert.Equal("include", transport.FetchOptions["credentials"]);
        Assert.Equal("no-store", transport.FetchOptions["cache"]);
        Assert.Equal("manual", transport.FetchOptions["redirect"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect, "text/html")]
    [InlineData(HttpStatusCode.Unauthorized, "text/html")]
    [InlineData(HttpStatusCode.Forbidden, "text/html")]
    [InlineData(HttpStatusCode.Unauthorized, "application/json")]
    [InlineData(HttpStatusCode.Forbidden, "application/json")]
    [InlineData((HttpStatusCode)0, "application/octet-stream")]
    public async Task MicrosoftGatewaySignInIsNotMistakenForRevokedLumiPairing(
        HttpStatusCode status, string contentType)
    {
        var transport = new CaptureHandler(status, contentType);
        await using var client = new LumiRemoteClient("test-device", "Web fixture",
            new BrowserSameOriginHandler(new Uri("https://private-47654.uks1.devtunnels.ms"), transport));
        client.Configure("https://private-47654.uks1.devtunnels.ms", "test-pairing-token");
        Assert.Null(await client.HelloAsync(client.BaseUrl!, CancellationToken.None));
        Assert.Equal(RemoteLinkState.GatewaySignInRequired, client.State);
        Assert.Equal("test-pairing-token", client.Token);
        Assert.Contains("Microsoft sign-in", client.StateMessage);
        Assert.Contains("Reload web app", client.StateMessage);
        Assert.Contains("pairing is kept", client.StateMessage);
        Assert.Equal(1, transport.RequestCount);
    }

    [Fact]
    public async Task ActualLumiUnauthorizedJsonStillRequestsPairing()
    {
        var transport = new CaptureHandler(HttpStatusCode.Unauthorized, "application/json", isLumiResponse: true);
        using var client = new HttpClient(new BrowserSameOriginHandler(
            new Uri("https://private-47654.uks1.devtunnels.ms"), transport));
        using var response = await client.GetAsync("https://private-47654.uks1.devtunnels.ms/api/hello");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ReopeningWithExpiredGatewaySignInStopsRetryingWithoutLosingPairingOrDraft()
    {
        const string origin = "https://private-47654.uks1.devtunnels.ms";
        var transport = new CaptureHandler(HttpStatusCode.Unauthorized, "text/html");
        await using var client = new LumiRemoteClient("test-device", "Web fixture",
            new BrowserSameOriginHandler(new Uri(origin), transport));
        var store = new PairedStore(new MobileConnectionSettings
        {
            DeviceId = "test-device", DeviceName = "Web fixture",
            BaseUrl = origin, Token = "test-pairing-token", HostName = "Fixture PC"
        });
        await using var shell = new MobileShellViewModel(
            client, store: store, post: action => action());
        shell.Chat.PromptText = "Keep this unsent draft";

        await shell.NotifyApplicationActivatedAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, transport.RequestCount);
        Assert.True(shell.IsPaired);
        Assert.Equal("test-pairing-token", client.Token);
        Assert.Equal("test-pairing-token", store.Load().Token);
        Assert.Equal("Keep this unsent draft", shell.Chat.PromptText);
        Assert.Equal(0, store.SaveCount);
        Assert.True(shell.IsGatewaySignInRequired);
        Assert.Equal("Sign-in required", shell.ConnectionStateLabel);
        Assert.DoesNotContain("Reconnecting", shell.ConnectionBannerText);
    }

    [Fact]
    public async Task ExpiredGatewaySignInStopsTheEventStreamRetryWithoutRevokingPairing()
    {
        const string origin = "https://private-47654.uks1.devtunnels.ms";
        var transport = new CaptureHandler(HttpStatusCode.Unauthorized, "text/html");
        await using var client = new LumiRemoteClient("test-device", "Web fixture",
            new BrowserSameOriginHandler(new Uri(origin), transport));
        client.Configure(origin, "test-pairing-token");
        var signInRequired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += (state, _) =>
        {
            if (state == RemoteLinkState.GatewaySignInRequired)
                signInRequired.TrySetResult();
        };

        await client.StartEventStreamAsync();
        await signInRequired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        Assert.Equal(1, transport.RequestCount);
        Assert.Equal(RemoteLinkState.GatewaySignInRequired, client.State);
        Assert.Equal("test-pairing-token", client.Token);
    }

    [Fact]
    public async Task GatewayRejectedCommandIsNotRetriedOrReportedAsAnUnknownSend()
    {
        const string origin = "https://private-47654.uks1.devtunnels.ms";
        var transport = new CaptureHandler(HttpStatusCode.Unauthorized, "text/html");
        await using var client = new LumiRemoteClient("test-device", "Web fixture",
            new BrowserSameOriginHandler(new Uri(origin), transport));
        client.Configure(origin, "test-pairing-token");
        client.MarkProtocolCompatibleForTests();

        var result = await client.SendCommandAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(result.IsOutcomeUnknown);
        Assert.Equal(1, transport.RequestCount);
        Assert.Equal(RemoteLinkState.GatewaySignInRequired, client.State);
    }

    [Fact]
    public async Task GatewayRejectedConfirmationKeepsAnEarlierUnknownCommandOutcome()
    {
        const string origin = "https://private-47654.uks1.devtunnels.ms";
        var transport = new CaptureHandler(
            HttpStatusCode.Unauthorized, "text/html", loseFirstResponse: true);
        await using var client = new LumiRemoteClient("test-device", "Web fixture",
            new BrowserSameOriginHandler(new Uri(origin), transport));
        client.Configure(origin, "test-pairing-token");
        client.MarkProtocolCompatibleForTests();

        var result = await client.SendCommandAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(result.IsOutcomeUnknown);
        Assert.Equal(2, transport.RequestCount);
        Assert.Equal(RemoteLinkState.GatewaySignInRequired, client.State);
    }

    [Fact]
    public async Task AnUploadThatNeedsGatewaySignInSurfacesRecoveryWithoutUnpairing()
    {
        const string origin = "https://private-47654.uks1.devtunnels.ms";
        var transport = new CaptureHandler(HttpStatusCode.Unauthorized, "text/html");
        await using var client = new LumiRemoteClient("test-device", "Web fixture",
            new BrowserSameOriginHandler(new Uri(origin), transport));
        client.Configure(origin, "test-pairing-token");

        var result = await client.UploadAsync("fixture.txt", new byte[] { 1 }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(RemoteLinkState.GatewaySignInRequired, client.State);
        Assert.Equal("test-pairing-token", client.Token);
        Assert.Equal(1, transport.RequestCount);
    }

    [Fact]
    public async Task AutomaticGatewayRecoveryWaitsForThePendingQuestionAcknowledgment()
    {
        const string origin = "https://private-47654.uks1.devtunnels.ms";
        var previousHost = MobilePlatformServices.HostEnvironment;
        MobilePlatformServices.HostEnvironment = new RecoverableWebHost(origin);
        var transport = new PendingGatewayActionHandler();
        try
        {
            await using var client = new LumiRemoteClient("test-device", "Web fixture",
                new BrowserSameOriginHandler(new Uri(origin), transport));
            var store = new PairedStore(new MobileConnectionSettings
            {
                DeviceId = "test-device", DeviceName = "Web fixture",
                BaseUrl = origin, Token = "test-pairing-token", HostName = "Fixture PC"
            });
            await using var shell = new MobileShellViewModel(client, store: store, post: action => action());
            client.MarkProtocolCompatibleForTests();
            var recoveries = 0;
            shell.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(shell.IsGatewaySignInRequired)
                    or nameof(shell.CanAutomaticallyRecoverGatewaySignIn)
                    && shell.IsGatewaySignInRequired && shell.CanAutomaticallyRecoverGatewaySignIn)
                    recoveries++;
            };
            var action = shell.SendCommandAsync(new RemoteCommand(RemoteProtocol.Actions.AnswerQuestion)
                .With("questionId", "fixture-question").With("answer", "First"));
            await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            try
            {
                Assert.False(shell.CanApplyWebAppUpdate);
                Assert.Null(await client.GetSnapshotAsync(CancellationToken.None));
                Assert.True(shell.IsGatewaySignInRequired);
                Assert.False(action.IsCompleted);
                Assert.False(shell.CanAutomaticallyRecoverGatewaySignIn);
                Assert.Equal(0, recoveries);
                Assert.True(shell.ShowGatewaySignInAction);
                Assert.True(shell.ReloadWebAppCommand.CanExecute(null));
            }
            finally
            {
                transport.Release.TrySetResult();
                Assert.True((await action).Ok);
            }

            Assert.True(shell.CanApplyWebAppUpdate);
            Assert.True(shell.CanAutomaticallyRecoverGatewaySignIn);
            Assert.Equal(1, recoveries);
            Assert.Equal("test-pairing-token", store.Load().Token);
        }
        finally
        {
            transport.Release.TrySetResult();
            MobilePlatformServices.HostEnvironment = previousHost;
        }
    }

    private sealed class PairedStore(MobileConnectionSettings settings) : IMobileSettingsStore
    {
        public int SaveCount { get; private set; }

        public MobileConnectionSettings Load() => settings;

        public void Save(MobileConnectionSettings value) => SaveCount++;
    }

    private sealed class RecoverableWebHost(string origin) : IMobileHostEnvironment
    {
        public bool HasFixedEndpoint => true;
        public string? FixedBaseUrl => origin;
        public string FixedEndpointName => "Fixture PC";
        public Action? ReloadWebApp => () => { };
        public Action? ApplyWebAppUpdate => () => { };
    }

    private sealed class PendingGatewayActionHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath != RemoteProtocol.Routes.Command)
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("Sign in to Microsoft.", Encoding.UTF8, "text/html")
                };

            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            var command = JsonSerializer.Deserialize(json, RemoteJsonContext.Default.RemoteCommand);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new RemoteCommandResult
                {
                    Ok = true, RequestId = command?.RequestId
                }, RemoteJsonContext.Default.RemoteCommandResult), Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class CaptureHandler(
        HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json",
        bool isLumiResponse = false, bool loseFirstResponse = false) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);
        public IDictionary<string, object>? FetchOptions { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Options.TryGetValue(
                new HttpRequestOptionsKey<IDictionary<string, object>>("WebAssemblyFetchOptions"),
                out var fetchOptions);
            FetchOptions = fetchOptions;
            if (Interlocked.Increment(ref _requestCount) == 1 && loseFirstResponse)
                throw new HttpRequestException("The first command's response was lost.");
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, contentType)
            };
            if (isLumiResponse)
                response.Headers.Add(RemoteProtocol.ServerResponseHeader, RemoteProtocol.ServerResponseValue);
            return Task.FromResult(response);
        }
    }
}
