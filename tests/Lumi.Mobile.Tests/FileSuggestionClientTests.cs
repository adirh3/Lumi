using System.Net;
using System.Text;
using System.Text.Json;
using Lumi.Mobile.Services;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class FileSuggestionClientTests
{
    [Fact]
    public async Task ClientRequestsAuthenticatedFileSuggestionsForExplicitChat()
    {
        var chatId = Guid.NewGuid();
        var handler = new SuggestionHandler(new RemoteFileSuggestions
        {
            Items =
            [
                new RemoteChip
                {
                    Name = "ChatView.axaml",
                    Glyph = "📄",
                    Description = "src/Lumi/Views",
                    Value = @"C:\repo\src\Lumi\Views\ChatView.axaml"
                }
            ]
        });
        await using var client = new LumiRemoteClient(
            "device",
            "Phone",
            handler,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            new TrustedRouteVerifier());
        client.Configure("http://100.64.0.1:62135", "secret-token");
        client.MarkProtocolCompatibleForTests(fileSuggestions: true);

        var result = await client.GetFileSuggestionsAsync(
            chatId,
            projectId: null,
            "chat",
            CancellationToken.None);

        Assert.Equal("ChatView.axaml", Assert.Single(result!.Items).Name);
        Assert.Equal(
            $"/lumi/file-suggestions?q=chat&chatId={chatId}",
            handler.RequestUri?.PathAndQuery);
        Assert.Equal("secret-token", handler.DeviceToken);
    }

    private sealed class SuggestionHandler(RemoteFileSuggestions response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? DeviceToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            DeviceToken = request.Headers.TryGetValues(
                RemoteProtocol.DeviceTokenHeader,
                out var values)
                ? values.SingleOrDefault()
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(
                        response,
                        RemoteJsonContext.Default.RemoteFileSuggestions),
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class TrustedRouteVerifier : IRemoteRouteVerifier
    {
        public bool IsTrustedTailscaleRoute(IPAddress targetAddress) => true;
    }
}
