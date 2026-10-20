using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Services.Byok;
using Xunit;

namespace Lumi.Tests;

public sealed class ProviderOAuthServiceTests
{
    [Fact]
    public void Pkce_UsesS256AndIndependentRandomState()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            ProviderOAuthService.CreateCodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var first = ProviderOAuthService.CreatePkceParameters();
        var second = ProviderOAuthService.CreatePkceParameters();
        Assert.Equal(43, first.Verifier.Length);
        Assert.Equal(43, first.State.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", first.Verifier);
        Assert.Matches("^[A-Za-z0-9_-]+$", first.State);
        Assert.Equal(Challenge(first.Verifier), first.Challenge);
        Assert.NotEqual(first.Verifier, first.State);
        Assert.NotEqual(first.Verifier, second.Verifier);
        Assert.NotEqual(first.State, second.State);
    }

    [Theory]
    [InlineData(ProviderOAuthKind.OpenRouter, "https://openrouter.ai/auth", "https://openrouter.ai/api/v1/auth/keys")]
    [InlineData(ProviderOAuthKind.NanoGpt, "https://nano-gpt.com/auth", "https://nano-gpt.com/api/v1/auth/keys")]
    [InlineData(ProviderOAuthKind.Poe, "https://poe.com/oauth/authorize", "https://api.poe.com/token")]
    public async Task Authenticate_RealLoopback_UsesOfficialFieldsAndReturnsAppKey(
        ProviderOAuthKind kind, string authorizeEndpoint, string tokenEndpoint)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var local = LocalHttpClient();
        Dictionary<string, string>? authorization = null;
        Uri? callback = null;
        using var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(tokenEndpoint, request.RequestUri!.AbsoluteUri);
            Assert.Empty(request.RequestUri.Query);
            Assert.NotNull(request.Content);
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            Dictionary<string, string> fields;
            if (kind == ProviderOAuthKind.Poe)
            {
                Assert.Equal("application/x-www-form-urlencoded", request.Content.Headers.ContentType!.MediaType);
                fields = Query(body);
                Assert.Equal("public-client-id", fields["client_id"]);
                Assert.Equal(callback!.AbsoluteUri, fields["redirect_uri"]);
                Assert.Equal("authorization_code", fields["grant_type"]);
            }
            else
            {
                Assert.Equal("application/json", request.Content.Headers.ContentType!.MediaType);
                using var json = JsonDocument.Parse(body);
                fields = json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
                if (kind == ProviderOAuthKind.OpenRouter)
                    Assert.Equal("S256", fields["code_challenge_method"]);
                else
                    Assert.Equal("authorization_code", fields["grant_type"]);
            }
            Assert.Equal("one-time-code", fields["code"]);
            Assert.Equal(authorization!["code_challenge"], Challenge(fields["code_verifier"]));
            Assert.False(fields.ContainsKey("client_secret"));
            Assert.DoesNotContain(fields["code_verifier"], string.Join("&", authorization.Select(p => p.Value)));
            return JsonResponse(kind == ProviderOAuthKind.Poe
                ? "{\"api_key\":\"app-only-key\",\"api_key_expires_in\":3600}"
                : "{\"key\":\"app-only-key\"}");
        });
        using var http = new HttpClient(handler);
        using var service = new ProviderOAuthService(http);
        var before = DateTimeOffset.UtcNow;
        var credential = await service.AuthenticateAsync(new ProviderOAuthOptions(kind, "public-client-id"), async uri =>
        {
            Assert.Equal(authorizeEndpoint, uri.GetLeftPart(UriPartial.Path));
            authorization = Query(uri.Query);
            Assert.Equal("S256", authorization["code_challenge_method"]);
            callback = Callback(uri);
            Assert.Equal("http", callback.Scheme);
            Assert.Equal("127.0.0.1", callback.Host);
            Assert.InRange(callback.Port, 1024, 65535);
            Assert.Equal("/easy-connect/callback", callback.AbsolutePath);
            if (kind == ProviderOAuthKind.NanoGpt)
            {
                Assert.Equal("api.use models.read", authorization["scope"]);
                Assert.Equal("Lumi", authorization["client_name"]);
                Assert.False(authorization.ContainsKey("client_id"));
            }
            if (kind == ProviderOAuthKind.Poe)
            {
                Assert.Equal("apikey:create", authorization["scope"]);
                Assert.Equal("code", authorization["response_type"]);
            }
            Assert.DoesNotContain("app-only-key", uri.AbsoluteUri);
            using var response = await local.GetAsync(
                callback + "?code=one-time-code&state=" + Uri.EscapeDataString(authorization["state"]), timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadAsStringAsync(timeout.Token);
            Assert.Contains("Return to Lumi", page);
            Assert.DoesNotContain("one-time-code", page);
            Assert.DoesNotContain(authorization["state"], page);
            Assert.DoesNotContain("app-only-key", page);
        }, timeout.Token);

        Assert.Equal("app-only-key", credential.ApiKey);
        if (kind == ProviderOAuthKind.Poe)
            Assert.InRange(credential.ExpiresAt!.Value, before.AddHours(1), DateTimeOffset.UtcNow.AddHours(1));
        else
            Assert.Null(credential.ExpiresAt);
        Assert.DoesNotContain(credential.ApiKey, credential.ToString());
        Assert.Equal(1, handler.RequestCount);
        AssertListenerClosed(callback!.Port);
    }

    [Theory]
    [InlineData("/not-the-callback", "code=private-code&state={state}")]
    [InlineData("/easy-connect/callback", "code=private-code&state=wrong-state")]
    [InlineData("/easy-connect/callback", "code=private-code")]
    [InlineData("/easy-connect/callback", "state={state}")]
    [InlineData("/easy-connect/callback", "code=private-code&code=replay-code&state={state}")]
    [InlineData("/easy-connect/callback", "code=private-code&state={state}&state=wrong-state")]
    [InlineData("/easy-connect/callback", "error=access_denied&error_description=private-error&state={state}")]
    public async Task Authenticate_InvalidCallback_NeverExchangesOrExposesParameters(string path, string query)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var local = LocalHttpClient();
        using var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse("{\"key\":\"should-not-exist\"}")));
        using var http = new HttpClient(handler);
        using var service = new ProviderOAuthService(http);
        Uri? callback = null;
        string? state = null;

        var error = await Assert.ThrowsAsync<ProviderOAuthException>(() => service.AuthenticateAsync(
            new ProviderOAuthOptions(ProviderOAuthKind.NanoGpt), async uri =>
            {
                callback = Callback(uri);
                state = Query(uri.Query)["state"];
                var address = callback.GetLeftPart(UriPartial.Authority) + path + "?" +
                    query.Replace("{state}", Uri.EscapeDataString(state));
                using var response = await local.GetAsync(address, timeout.Token);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }, timeout.Token));

        Assert.Equal(0, handler.RequestCount);
        Assert.DoesNotContain("private-code", error.ToString());
        Assert.DoesNotContain("private-error", error.ToString());
        Assert.DoesNotContain(state!, error.ToString());
        AssertListenerClosed(callback!.Port);
    }

    [Fact]
    public async Task Authenticate_OpenRouterStatelessDenial_FailsWithoutExchanging()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var local = LocalHttpClient();
        using var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse("{}")));
        using var http = new HttpClient(handler);
        using var service = new ProviderOAuthService(http);
        var error = await Assert.ThrowsAsync<ProviderOAuthException>(() => service.AuthenticateAsync(
            new ProviderOAuthOptions(ProviderOAuthKind.OpenRouter), async uri =>
            {
                using var response = await local.GetAsync(Callback(uri) + "?error=access_denied", timeout.Token);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }, timeout.Token));
        Assert.Contains("not approved", error.Message);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Authenticate_Cancellation_ClosesRealLoopbackListener()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse("{}")));
        using var http = new HttpClient(handler);
        using var service = new ProviderOAuthService(http);
        var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var authentication = service.AuthenticateAsync(new ProviderOAuthOptions(ProviderOAuthKind.OpenRouter), uri =>
        {
            opened.SetResult(Callback(uri));
            return Task.CompletedTask;
        }, cancellation.Token);
        var callback = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authentication.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, handler.RequestCount);
        AssertListenerClosed(callback.Port);
    }

    [Fact]
    public async Task Authenticate_Timeout_ClosesRealLoopbackListener()
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse("{}")));
        using var http = new HttpClient(handler);
        using var service = new ProviderOAuthService(http, TimeSpan.FromMilliseconds(200));
        Uri? callback = null;
        await Assert.ThrowsAsync<ProviderOAuthException>(() => service.AuthenticateAsync(
            new ProviderOAuthOptions(ProviderOAuthKind.NanoGpt), uri =>
            {
                callback = Callback(uri);
                return Task.CompletedTask;
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, handler.RequestCount);
        AssertListenerClosed(callback!.Port);
    }

    [Fact]
    public async Task Dispose_CancelsPendingFlow_AndDoesNotOwnInjectedHttpClient()
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse("{}")));
        using var http = new HttpClient(handler);
        var service = new ProviderOAuthService(http);
        Uri? callback = null;
        var authentication = service.AuthenticateAsync(new ProviderOAuthOptions(ProviderOAuthKind.OpenRouter), uri =>
        {
            callback = Callback(uri);
            return Task.CompletedTask;
        }, CancellationToken.None);
        service.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authentication.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(handler.WasDisposed);
        AssertListenerClosed(callback!.Port);
        using var response = await http.GetAsync("https://unit.test/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.AuthenticateAsync(
            new ProviderOAuthOptions(ProviderOAuthKind.OpenRouter), _ => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task Authenticate_PoeWithoutClientId_DoesNotOpenBrowser()
    {
        using var service = new ProviderOAuthService();
        var opened = false;
        var error = await Assert.ThrowsAsync<ProviderOAuthException>(() => service.AuthenticateAsync(
            new ProviderOAuthOptions(ProviderOAuthKind.Poe), _ =>
            {
                opened = true;
                return Task.CompletedTask;
            }, CancellationToken.None));
        Assert.False(opened);
        Assert.Contains("public Client ID", error.Message);
    }

    [Theory]
    [InlineData(302, "redirected")]
    [InlineData(403, "Start sign-in again")]
    [InlineData(429, "Wait")]
    [InlineData(503, "temporarily unavailable")]
    public async Task Authenticate_HttpFailure_IsActionableAndDoesNotExposeResponse(int status, string action)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var local = LocalHttpClient();
        using var handler = new RecordingHandler((_, _) => Task.FromResult(
            new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private-provider-key") }));
        using var http = new HttpClient(handler);
        using var service = new ProviderOAuthService(http);
        var error = await Assert.ThrowsAsync<ProviderOAuthException>(() => service.AuthenticateAsync(
            new ProviderOAuthOptions(ProviderOAuthKind.OpenRouter),
            uri => ApproveAsync(local, uri, timeout.Token), timeout.Token));
        Assert.Contains(action, error.Message);
        Assert.DoesNotContain("private-provider-key", error.ToString());
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Authenticate_UnknownLengthOversizedResponse_IsRejected()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var local = LocalHttpClient();
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(new byte[ProviderOAuthService.MaximumResponseBytes + 1])),
        }));
        using var http = new HttpClient(handler);
        using var service = new ProviderOAuthService(http);
        var error = await Assert.ThrowsAsync<ProviderOAuthException>(() => service.AuthenticateAsync(
            new ProviderOAuthOptions(ProviderOAuthKind.NanoGpt),
            uri => ApproveAsync(local, uri, timeout.Token), timeout.Token));
        Assert.Contains("invalid key response", error.Message);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Authenticate_BrowserFailure_IsSanitizedAndClosesListener()
    {
        using var service = new ProviderOAuthService();
        Uri? callback = null;
        var error = await Assert.ThrowsAsync<ProviderOAuthException>(() => service.AuthenticateAsync(
            new ProviderOAuthOptions(ProviderOAuthKind.OpenRouter), uri =>
            {
                callback = Callback(uri);
                throw new InvalidOperationException("private-browser-token");
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain("private-browser-token", error.ToString());
        AssertListenerClosed(callback!.Port);
    }

    [Theory]
    [InlineData(ProviderOAuthKind.OpenRouter, "{}")]
    [InlineData(ProviderOAuthKind.NanoGpt, "{\"key\":\"\"}")]
    [InlineData(ProviderOAuthKind.OpenRouter, "{\"key\":\"private\\r\\nkey\"}")]
    [InlineData(ProviderOAuthKind.NanoGpt, "{\"error\":\"invalid_grant\",\"error_description\":\"private-token\"}")]
    [InlineData(ProviderOAuthKind.Poe, "{\"api_key\":\"private-key\"}")]
    [InlineData(ProviderOAuthKind.Poe, "{\"api_key\":\"private-key\",\"api_key_expires_in\":-1}")]
    [InlineData(ProviderOAuthKind.Poe, "{\"api_key\":\"private-key\",\"api_key_expires_in\":\"forever\"}")]
    [InlineData(ProviderOAuthKind.Poe, "{\"api_key\":\"private-key\",\"api_key_expires_in\":9223372036854775807}")]
    [InlineData(ProviderOAuthKind.OpenRouter, "not-json-private-token")]
    public void ParseCredential_RejectsInvalidResponsesWithoutLeakingSecrets(ProviderOAuthKind kind, string json)
    {
        var error = Assert.Throws<ProviderOAuthException>(() =>
            ProviderOAuthService.ParseCredential(kind, Encoding.UTF8.GetBytes(json), DateTimeOffset.UtcNow));
        Assert.DoesNotContain("private", error.ToString());
    }

    [Fact]
    public void ParseCredential_PoeExplicitNullExpiry_ReturnsNonExpiringKey()
    {
        var credential = ProviderOAuthService.ParseCredential(ProviderOAuthKind.Poe,
            Encoding.UTF8.GetBytes("{\"api_key\":\"app-key\",\"api_key_expires_in\":null}"), DateTimeOffset.UtcNow);
        Assert.Equal("app-key", credential.ApiKey);
        Assert.Null(credential.ExpiresAt);
    }

    private static HttpClient LocalHttpClient()
        => new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static async Task ApproveAsync(HttpClient local, Uri authorization, CancellationToken cancellationToken)
    {
        using var response = await local.GetAsync(Callback(authorization) +
            "?code=test-code&state=" + Uri.EscapeDataString(Query(authorization.Query)["state"]), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Uri Callback(Uri authorization)
    {
        var values = Query(authorization.Query);
        return new Uri(values.TryGetValue("callback_url", out var callback) ? callback : values["redirect_uri"]);
    }

    private static Dictionary<string, string> Query(string query) => query.TrimStart('?').Split('&')
        .Select(pair => pair.Split('=', 2)).ToDictionary(
            pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
            pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));

    private static string Challenge(string verifier) => Convert.ToBase64String(
        SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void AssertListenerClosed(int port)
    {
        Assert.DoesNotContain(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(),
            endpoint => endpoint.Address.Equals(IPAddress.Loopback) && endpoint.Port == port);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public bool WasDisposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return send(request, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
