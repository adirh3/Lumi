using System.Net;
using System.Text;
using System.Text.Json;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

[Trait("Category", "CoreQA")]
public sealed class ConnectAddressTrustTests
{
    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("100.64.12.34:47665")]
    [InlineData("100.127.255.254")]
    [InlineData("[fd7a:115c:a1e0::1234]:47665")]
    public void TailscaleAddressesDoNotRequireTheLanTrustToggle(string address)
    {
        Assert.False(ConnectViewModel.RequiresTrustedAddressConfirmation(
            LumiRemoteClient.NormalizeBaseUrl(address)));
    }

    [Theory]
    [InlineData("192.168.1.20:47653")]
    [InlineData("10.0.0.12")]
    [InlineData("172.16.1.3")]
    [InlineData("my-pc.local")]
    public void LanAndUnverifiedHostNamesRequireExplicitTrust(string address)
    {
        Assert.True(ConnectViewModel.RequiresTrustedAddressConfirmation(
            LumiRemoteClient.NormalizeBaseUrl(address)));
    }

    [Theory]
    [InlineData("127.0.0.1:47653")]
    [InlineData("localhost:47653")]
    public void LoopbackDoesNotRequireConfirmation(string address)
    {
        Assert.False(ConnectViewModel.RequiresTrustedAddressConfirmation(
            LumiRemoteClient.NormalizeBaseUrl(address)));
    }

    [Fact]
    public async Task DisablingLanTrustClearsDiscoveryResultsAndBlocksTheirUse()
    {
        await using var client = new LumiRemoteClient("device", "Phone");
        var viewModel = new ConnectViewModel(
            client,
            new LumiDiscoveryClient(),
            (_, _) => Task.CompletedTask)
        {
            AllowInsecureLanDiscovery = true
        };
        var host = new DiscoveredHostViewModel
        {
            HostName = "LAN PC",
            UserName = "User",
            BaseUrl = "http://192.168.1.20:47653"
        };
        viewModel.Hosts.Add(host);

        viewModel.AllowInsecureLanDiscovery = false;
        await viewModel.ChooseHostCommand.ExecuteAsync(host);

        Assert.Empty(viewModel.Hosts);
        Assert.True(viewModel.IsFindStep);
        Assert.Contains("unencrypted", viewModel.ErrorText);
    }

    [Fact]
    public async Task PairingCodeKeepsSixAsciiDigitsAndOnlyThenEnablesSubmit()
    {
        await using var client = new LumiRemoteClient("device", "Phone");
        var viewModel = new ConnectViewModel(
            client,
            new LumiDiscoveryClient(),
            (_, _) => Task.CompletedTask);

        viewModel.PairingCode = "12a34-5678";

        Assert.Equal("123456", viewModel.PairingCode);
        Assert.True(viewModel.CanSubmitCode);

        viewModel.PairingCode = "12345";
        Assert.False(viewModel.CanSubmitCode);
    }

    [Fact]
    public void AndroidConnectionLinkCarriesTheDesktopAddress()
    {
        var link =
            "lumi://connect?server=http%3A%2F%2F192.168.1.42%3A62145";

        Assert.True(MobileConnectionLaunchRequest.TryParse(link, out var request));
        Assert.NotNull(request);
        Assert.Equal("http://192.168.1.42:62145", request.BaseUrl);
    }

    [Theory]
    [InlineData("https://example.com/connect")]
    [InlineData("lumi://connect?server=file%3A%2F%2FC%3A%2Fdata")]
    [InlineData("lumi://connect?server=http%3A%2F%2Fuser%3Apass%40host")]
    [InlineData("lumi://connect?server=http%3A%2F%2Flocalhost%3A47653")]
    [InlineData("lumi://connect?server=http%3A%2F%2F127.0.0.1%3A47653")]
    [InlineData("lumi://connect?server=http%3A%2F%2F%5B%3A%3A1%5D%3A47653")]
    public void InvalidAndroidConnectionLinksAreRejected(string link)
    {
        Assert.False(MobileConnectionLaunchRequest.TryParse(link, out _));
    }

    [Fact]
    public async Task LocalWifiSetupLinkPrefillsAddressButStillRequiresPhoneTrust()
    {
        await using var client = new LumiRemoteClient("device", "Phone");
        var request = new MobileConnectionLaunchRequest(
            "http://192.168.1.42:62145");
        var viewModel = new ConnectViewModel(
            client,
            new LumiDiscoveryClient(),
            (_, _) => Task.CompletedTask);

        await viewModel.ApplyLaunchRequestAsync(request);

        Assert.True(viewModel.IsFindStep);
        Assert.Equal(request.BaseUrl, viewModel.ManualAddress);
        Assert.False(viewModel.AllowInsecureLanDiscovery);
        Assert.Contains("Confirm", viewModel.StatusText);
    }

    [Fact]
    public async Task AndroidSetupLinkWaitsForExplicitContinueBeforeContactingEndpoint()
    {
        var handler = new CountingHelloHandler();
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2),
            new TrustedRouteVerifier());
        var request = new MobileConnectionLaunchRequest(
            "http://100.64.12.34:47653");
        var viewModel = new ConnectViewModel(
            client,
            new LumiDiscoveryClient(),
            (_, _) => Task.CompletedTask);

        await viewModel.ApplyLaunchRequestAsync(request);

        Assert.Equal(0, handler.RequestCount);
        Assert.True(viewModel.IsFindStep);
        Assert.Equal(request.BaseUrl, viewModel.ManualAddress);
        Assert.Contains("Continue", viewModel.StatusText);

        await viewModel.ConnectManuallyCommand.ExecuteAsync(null);

        Assert.Equal(1, handler.RequestCount);
        Assert.True(viewModel.IsCodeStep);
    }

    private sealed class TrustedRouteVerifier : IRemoteRouteVerifier
    {
        public bool IsTrustedTailscaleRoute(IPAddress targetAddress) => true;
    }

    private sealed class CountingHelloHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var json = JsonSerializer.Serialize(
                new RemoteHello
                {
                    ProtocolVersion = RemoteProtocol.Version,
                    Capabilities = [RemoteProtocol.Capabilities.ScopedEventsV1],
                    HostName = "Living Room PC"
                },
                RemoteJsonContext.Default.RemoteHello);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
