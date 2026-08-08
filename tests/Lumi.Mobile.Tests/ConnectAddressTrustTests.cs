using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class ConnectAddressTrustTests
{
    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("100.85.249.111:47665")]
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
}
