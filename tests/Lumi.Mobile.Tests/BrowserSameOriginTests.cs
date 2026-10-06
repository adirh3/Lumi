using System.Net;
using Lumi.Mobile.Browser;
using Lumi.Mobile.Services;
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
        Assert.Equal(RemoteLinkState.Error, client.State);
        Assert.Equal("test-pairing-token", client.Token);
        Assert.Contains("Microsoft sign-in", client.StateMessage);
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

    private sealed class CaptureHandler(
        HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json",
        bool isLumiResponse = false) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
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
