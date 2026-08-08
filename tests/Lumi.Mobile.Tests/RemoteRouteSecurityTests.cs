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
        client.Configure("http://100.85.249.111:47653", "secret-token");

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
        client.Configure("http://100.85.249.111:47653", "secret-token");

        var snapshot = await client.GetSnapshotAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(1, inner.RequestCount);
    }

    [Fact]
    public void TailscaleTopologyRequiresLocalAddressAndSpecificRoute()
    {
        var target = IPAddress.Parse("100.85.249.111");
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
        var target = IPAddress.Parse("100.85.249.111");
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
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Lumi.Mobile.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new MobileSettingsStore(directory);
            var settings = store.Load();
            settings.BaseUrl = "http://127.0.0.1:47653";
            settings.Token = "pc-a-token";
            settings.HostName = "PC A";
            store.Save(settings);

            await using var client = new LumiRemoteClient(
                settings.DeviceId,
                settings.DeviceName,
                new UnauthorizedHandler());
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
        }
        finally
        {
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
            var json = JsonSerializer.Serialize(new RemoteSnapshot(), RemoteJsonContext.Default.RemoteSnapshot);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

    }

    private sealed class UnauthorizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }
}
