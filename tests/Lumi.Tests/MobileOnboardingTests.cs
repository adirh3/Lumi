using Lumi.Services.Remote;
using Lumi.Views.Controls;
using System.Net;
using Xunit;

namespace Lumi.Tests;

public sealed class MobileOnboardingTests
{
    [Fact]
    public void ConnectionChoiceSelectsOnlyTheRequestedTransport()
    {
        var addresses = new[]
        {
            "http://172.20.0.1:62145",
            "http://100.85.249.111:62145",
            "http://192.168.1.42:62145"
        };

        var tailscaleEndpoint = MobileOnboardingLinks.SelectEndpoint(
            addresses,
            MobileOnboardingTransport.Tailscale);
        var wifiEndpoint = MobileOnboardingLinks.SelectEndpoint(
            addresses,
            MobileOnboardingTransport.LocalNetwork);

        Assert.NotNull(tailscaleEndpoint);
        Assert.Equal(MobileOnboardingTransport.Tailscale, tailscaleEndpoint.Transport);
        Assert.Equal("http://100.85.249.111:62145", tailscaleEndpoint.BaseUrl);

        Assert.NotNull(wifiEndpoint);
        Assert.Equal(MobileOnboardingTransport.LocalNetwork, wifiEndpoint.Transport);
        Assert.Equal("http://192.168.1.42:62145", wifiEndpoint.BaseUrl);
    }

    [Fact]
    public void TailscaleChoiceDoesNotSilentlyFallBackToLocalWifi()
    {
        var endpoint = MobileOnboardingLinks.SelectEndpoint(
            ["http://192.168.1.42:62145"],
            MobileOnboardingTransport.Tailscale);

        Assert.Null(endpoint);
    }

    [Fact]
    public void DevTunnelChoiceRequiresItsOwnHttpsOriginAndNeverFallsBack()
    {
        const string origin = "https://private-47654.uks1.devtunnels.ms";
        var endpoint = MobileOnboardingLinks.SelectEndpoint(
            ["http://192.168.1.42:47654", "http://100.64.0.10:47654", origin],
            MobileOnboardingTransport.DevTunnel);
        Assert.NotNull(endpoint);
        Assert.Equal(MobileOnboardingTransport.DevTunnel, endpoint.Transport);
        Assert.Equal(origin + "/app/", MobileOnboardingLinks.BuildWebAppUrl(endpoint.BaseUrl));
        Assert.Null(MobileOnboardingLinks.SelectEndpoint(
            ["http://192.168.1.42:47654", "http://private-47654.uks1.devtunnels.ms",
                "https://private-47654.uks1.devtunnels.ms.attacker.test"],
            MobileOnboardingTransport.DevTunnel));
    }

    [Fact]
    public void WebAndAndroidLinksTargetTheSelectedDesktop()
    {
        const string baseUrl = "http://192.168.1.42:62145";

        var web = MobileOnboardingLinks.BuildWebAppUrl(baseUrl);
        var android = MobileOnboardingLinks.BuildAndroidInstallUrl(
            baseUrl,
            "1.2.3");

        Assert.Equal("http://192.168.1.42:62145/app/", web);
        Assert.Equal(
            "http://192.168.1.42:62145/app/android.html?version=1.2.3",
            android);
    }

    [Fact]
    public void GatewayBackedLanAddressBeatsHostOnlyVirtualAdapter()
    {
        var addresses = new[]
        {
            "http://192.168.56.1:62145",
            "http://10.0.0.25:62145"
        };
        var gatewayBacked = new HashSet<IPAddress>
        {
            IPAddress.Parse("10.0.0.25")
        };

        var endpoint = MobileOnboardingLinks.SelectEndpoint(
            addresses,
            MobileOnboardingTransport.LocalNetwork,
            gatewayBacked);

        Assert.NotNull(endpoint);
        Assert.Equal("http://10.0.0.25:62145", endpoint.BaseUrl);
    }

    [Fact]
    public void QrMatrixIsGeneratedForTheCompleteSetupUrl()
    {
        const string url = "http://192.168.1.42:62145/app/";

        var modules = QrCodeControl.CreateModules(url);

        Assert.NotNull(modules);
        Assert.Equal(modules.GetLength(0), modules.GetLength(1));
        Assert.InRange(modules.GetLength(0), 21, 177);
        Assert.Contains(true, modules.Cast<bool>());
        Assert.Contains(false, modules.Cast<bool>());
    }
}
