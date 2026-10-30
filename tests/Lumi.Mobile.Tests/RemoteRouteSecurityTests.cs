using System.Net;
using System.Text;
using System.Text.Json;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class RemoteRouteSecurityTests
{
    [Fact]
    public void DefaultTransportDisablesProxiesAndRedirects()
    {
        using var handler = LumiRemoteClient.CreateDefaultHandler();

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.GZip, handler.AutomaticDecompression);
    }

    [Fact]
    public async Task UnverifiedTailscaleRouteFailsBeforeAnyTokenLeavesTheClient()
    {
        var inner = new CountingHandler();
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            inner,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2),
            new FixedRouteVerifier(false));
        client.Configure("http://100.64.12.34:47653", "secret-token");

        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);

        Assert.Null(snapshot);
        Assert.Equal(0, inner.RequestCount);
        Assert.Contains("Tailscale is not connected", client.StateMessage);
    }

    [Fact]
    public async Task VerifiedTailscaleRouteCanSend()
    {
        var inner = new CountingHandler();
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            inner,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2),
            new FixedRouteVerifier(true));
        client.Configure("http://100.64.12.34:47653", "secret-token");

        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(1, inner.RequestCount);
    }

    [Fact]
    public void TailscaleTopologyRequiresLocalAddressAndSpecificRoute()
    {
        var target = IPAddress.Parse("100.64.12.34");
        var specificRoute = new RemoteNetworkRoute(IPAddress.Parse("100.64.0.0"), 10);

        Assert.True(RemoteRouteSecurity.IsTrustedTailscaleTopology(
            target,
            [IPAddress.Parse("100.100.10.20")],
            [specificRoute]));
        Assert.False(RemoteRouteSecurity.IsTrustedTailscaleTopology(
            target,
            [IPAddress.Parse("10.0.0.2")],
            [specificRoute]));
    }

    [Fact]
    public void GenericFullTunnelIsNotTrustedAsTailscale()
    {
        var target = IPAddress.Parse("100.64.12.34");
        var defaultRoute = new RemoteNetworkRoute(IPAddress.Any, 0);

        Assert.False(RemoteRouteSecurity.IsTrustedTailscaleTopology(
            target,
            [IPAddress.Parse("10.0.0.2")],
            [defaultRoute]));
        Assert.False(RemoteRouteSecurity.IsTrustedTailscaleTopology(
            target,
            [IPAddress.Parse("100.100.10.20")],
            [defaultRoute]));
    }

    [Fact]
    public async Task UnauthorizedResponseClearsStoredCredentials()
    {
        var previousHostEnvironment = MobilePlatformServices.HostEnvironment;
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Lumi.Mobile.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            MobilePlatformServices.HostEnvironment =
                new FixedHostEnvironment("http://lumi.test");
            var store = new MobileSettingsStore(directory);
            var settings = store.Load();
            settings.BaseUrl = "http://lumi.test";
            settings.Token = "pc-a-token";
            settings.HostName = "PC A";
            store.Save(settings);

            var handler = new RevokedPairingHandler();
            await using var client = new LumiRemoteClient(
                settings.DeviceId,
                settings.DeviceName,
                handler);
            await using var shell = new MobileShellViewModel(
                client: client,
                store: store,
                post: action => action());

            Assert.True(shell.IsPaired);
            await shell.RefreshSnapshotAsync();

            Assert.False(shell.IsPaired);
            Assert.Equal("", client.BaseUrl);
            Assert.Null(client.Token);
            var persisted = store.Load();
            Assert.Equal("", persisted.BaseUrl);
            Assert.Equal("", persisted.Token);
            Assert.Equal("", persisted.HostName);
            await WaitUntilAsync(() => shell.Connect.IsCodeStep);
            Assert.Equal(1, handler.HelloRequests);
        }
        finally
        {
            MobilePlatformServices.HostEnvironment = previousHostEnvironment;
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnpairedHelloRestartsFixedEndpointOnboarding()
    {
        var previousHostEnvironment = MobilePlatformServices.HostEnvironment;
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Lumi.Mobile.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            MobilePlatformServices.HostEnvironment =
                new FixedHostEnvironment("http://lumi.test");
            var store = new MobileSettingsStore(directory);
            var settings = store.Load();
            settings.BaseUrl = "http://lumi.test";
            settings.Token = "revoked-token";
            settings.HostName = "PC A";
            store.Save(settings);

            var handler = new RevokedPairingHandler();
            await using var client = new LumiRemoteClient(
                settings.DeviceId,
                settings.DeviceName,
                handler);
            await using var shell = new MobileShellViewModel(
                client: client,
                store: store,
                post: action => action());

            await shell.StartAsync();

            Assert.False(shell.IsPaired);
            Assert.Null(client.Token);
            Assert.Equal(2, handler.HelloRequests);
            Assert.True(shell.Connect.IsCodeStep);
        }
        finally
        {
            MobilePlatformServices.HostEnvironment = previousHostEnvironment;
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixedRouteVerifier(bool trusted) : IRemoteRouteVerifier
    {
        public bool IsTrustedTailscaleRoute(IPAddress targetAddress) => trusted;
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var json = JsonSerializer.Serialize(
                new RemoteSnapshot
                {
                    Capabilities = [RemoteProtocol.Capabilities.ScopedEventsV1]
                },
                RemoteJsonContext.Default.RemoteSnapshot);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected onboarding state was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class FixedHostEnvironment(string baseUrl) : IMobileHostEnvironment
    {
        public bool HasFixedEndpoint => true;

        public string? FixedBaseUrl => baseUrl;

        public string FixedEndpointName => "This Lumi PC";
    }

    private sealed class RevokedPairingHandler : HttpMessageHandler
    {
        public int HelloRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath != RemoteProtocol.Routes.Hello)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

            HelloRequests++;
            var json = JsonSerializer.Serialize(
                new RemoteHello
                {
                    ProtocolVersion = RemoteProtocol.Version,
                    Capabilities = [RemoteProtocol.Capabilities.ScopedEventsV1],
                    HostName = "PC A",
                    IsPaired = false
                },
                RemoteJsonContext.Default.RemoteHello);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
