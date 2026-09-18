using System.Net;
using Lumi.Mobile.Browser;
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

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
