using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Localization;
using Lumi.Services.Byok;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatGptAuthServiceTests
{
    private static readonly Uri Callback = new("http://127.0.0.1:43123/auth/callback");

    [Fact]
    public void AuthorizationUri_NewAccount_RegistersLumiWithTheOpenSourceContract()
    {
        var uri = ChatGptAuthService.CreateAuthorizationUri(Callback, "challenge", "state", "nonce", "urn:uuid:host", null);
        var query = Query(uri.Query);

        Assert.Equal(ChatGptAuthService.AuthorizeEndpoint, uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("dynamic_agent_client", query["client_id"]);
        Assert.Equal("Lumi", query["agent_name_hint"]);
        Assert.Equal("urn:uuid:host", query["ext_agent_host_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(Callback.AbsoluteUri, query["redirect_uri"]);
        Assert.Equal("openid profile email offline_access resource.invoke chatgpt.tokens.use.direct", query["scope"]);
        Assert.Equal("https://api.openai.com/v1", query["resource"]);
        Assert.Equal("state", query["state"]);
        Assert.Equal("nonce", query["nonce"]);
        Assert.Equal("challenge", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(query.ContainsKey("login_hint"));
        Assert.False(query.ContainsKey("id_token_hint"));
    }

    [Fact]
    public void AuthorizationUri_ReturningAccount_ReusesItsIssuedClient()
    {
        var uri = ChatGptAuthService.CreateAuthorizationUri(Callback, "challenge", "state", "nonce", "urn:uuid:host",
            new ChatGptAccount("oaiapp_saved", "user-123", "ada@example.com"));
        var query = Query(uri.Query);

        Assert.Equal("oaiapp_saved", query["client_id"]);
        Assert.Equal("ada@example.com", query["login_hint"]);
        Assert.Equal("urn:uuid:host", query["ext_agent_host_id"]);
        Assert.False(query.ContainsKey("agent_name_hint"));
    }

    [Theory]
    [InlineData(null, "oaiapp_new", "oaiapp_new")]
    [InlineData("oaiapp_saved", null, "oaiapp_saved")]
    [InlineData("oaiapp_saved", "oaiapp_saved", "oaiapp_saved")]
    public void IssuedClient_IsTheRegistrationsOwn(string? saved, string? returned, string expected)
    {
        var callback = returned is null ? new Dictionary<string, string>() : new() { ["client_id"] = returned };
        var existing = saved is null ? null : new ChatGptAccount(saved, "user", null);
        Assert.Equal(expected, ChatGptAuthService.ResolveIssuedClientId(callback, existing));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "dynamic_agent_client")]
    [InlineData(null, "has space")]
    [InlineData("oaiapp_saved", "oaiapp_other")]
    public void IssuedClient_RejectsIncompleteOrForeignRegistrations(string? saved, string? returned)
    {
        Loc.Load("en");
        var callback = returned is null ? new Dictionary<string, string>() : new() { ["client_id"] = returned };
        var existing = saved is null ? null : new ChatGptAccount(saved, "user", null);
        Assert.Throws<ProviderOAuthException>(() => ChatGptAuthService.ResolveIssuedClientId(callback, existing));
    }

    [Fact]
    public async Task SignIn_RealLoopback_RegistersExchangesAndVerifiesTheIdToken()
    {
        Loc.Load("en");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var fake = new FakeChatGpt();
        using var http = new HttpClient(fake);
        using var service = new ChatGptAuthService(http);
        using var local = LocalHttpClient();
        Dictionary<string, string>? authorization = null;
        var before = DateTimeOffset.UtcNow;

        var signIn = await service.SignInAsync("urn:uuid:host", null, async uri =>
        {
            authorization = Query(uri.Query);
            fake.Nonce = authorization["nonce"];
            await ApproveAsync(local, authorization, "&client_id=oaiapp_test&scope=" + Uri.EscapeDataString(FakeChatGpt.PlanScopes),
                timeout.Token);
        }, timeout.Token);

        var callback = new Uri(authorization!["redirect_uri"]);
        Assert.Equal("127.0.0.1", callback.Host);
        Assert.Equal("/auth/callback", callback.AbsolutePath);
        var exchange = Assert.Single(fake.Grants("authorization_code"));
        Assert.Equal("oaiapp_test", exchange.Form["client_id"]);
        Assert.Equal("one-time-code", exchange.Form["code"]);
        Assert.Equal(callback.AbsoluteUri, exchange.Form["redirect_uri"]);
        Assert.Equal("https://api.openai.com/v1", exchange.Form["resource"]);
        Assert.Equal(authorization["code_challenge"], Challenge(exchange.Form["code_verifier"]));
        Assert.False(exchange.Form.ContainsKey("client_secret"));

        Assert.Equal(new ChatGptAccount("oaiapp_test", "user-123", "ada@example.com"), signIn.Account);
        Assert.Equal("access-1", signIn.Tokens.AccessToken);
        Assert.Equal("refresh-1", signIn.Tokens.RefreshToken);
        Assert.True(signIn.Tokens.CanUsePlan);
        Assert.InRange(signIn.Tokens.ExpiresAt, before.AddHours(1), DateTimeOffset.UtcNow.AddHours(1));
        Assert.DoesNotContain("access-1", signIn.ToString());
        Assert.DoesNotContain("refresh-1", signIn.Tokens.ToString());
    }

    [Fact]
    public async Task SignIn_WithoutPlanPermission_DoesNotConnect()
    {
        Loc.Load("en");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var fake = new FakeChatGpt { GrantedScopes = "openid profile email offline_access" };
        using var http = new HttpClient(fake);
        using var service = new ChatGptAuthService(http);
        using var local = LocalHttpClient();

        var error = await Assert.ThrowsAsync<ProviderOAuthException>(() => service.SignInAsync("urn:uuid:host", null,
            async uri =>
            {
                var authorization = Query(uri.Query);
                fake.Nonce = authorization["nonce"];
                await ApproveAsync(local, authorization, "&client_id=oaiapp_test", timeout.Token);
            }, timeout.Token));
        Assert.Equal(Loc.Get("ChatGpt_PlanNotEnabled"), error.Message);
    }

    [Fact]
    public async Task SignIn_ReturningAccount_RejectsADifferentChatGptAccount()
    {
        Loc.Load("en");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var fake = new FakeChatGpt { ClientId = "oaiapp_saved", Subject = "someone-else" };
        using var http = new HttpClient(fake);
        using var service = new ChatGptAuthService(http);
        using var local = LocalHttpClient();
        var existing = new ChatGptAccount("oaiapp_saved", "user-123", "ada@example.com");

        var error = await Assert.ThrowsAsync<ProviderOAuthException>(() => service.SignInAsync("urn:uuid:host", existing,
            async uri =>
            {
                var authorization = Query(uri.Query);
                Assert.Equal("oaiapp_saved", authorization["client_id"]);
                fake.Nonce = authorization["nonce"];
                await ApproveAsync(local, authorization, "", timeout.Token);
            }, timeout.Token));
        Assert.Equal(Loc.Get("ChatGpt_DifferentAccount"), error.Message);
        Assert.Equal("oaiapp_saved", Assert.Single(fake.Grants("authorization_code")).Form["client_id"]);
    }

    [Fact]
    public async Task SignIn_DeniedConsent_NeverExchangesACode()
    {
        Loc.Load("en");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var fake = new FakeChatGpt();
        using var http = new HttpClient(fake);
        using var service = new ChatGptAuthService(http);
        using var local = LocalHttpClient();

        await Assert.ThrowsAsync<ProviderOAuthException>(() => service.SignInAsync("urn:uuid:host", null, async uri =>
        {
            var authorization = Query(uri.Query);
            using var response = await local.GetAsync(authorization["redirect_uri"] + "?error=access_denied&state="
                + Uri.EscapeDataString(authorization["state"]), timeout.Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }, timeout.Token));
        Assert.Empty(fake.Grants("authorization_code"));
    }

    [Fact]
    public async Task IdToken_RequiresOpenAiSignatureIssuerAudienceExpiryAndNonce()
    {
        Loc.Load("en");
        using var fake = new FakeChatGpt();
        using var http = new HttpClient(fake);
        using var service = new ChatGptAuthService(http);
        var now = DateTimeOffset.UtcNow;

        var account = await service.ValidateIdTokenAsync(fake.IdToken("oaiapp_test", "nonce"), "oaiapp_test", "nonce", now,
            CancellationToken.None);
        Assert.Equal("user-123", account.Subject);

        var valid = fake.IdToken("oaiapp_test", "nonce");
        var parts = valid.Split('.');
        var tampered = parts[0] + "." + parts[1] + "." + parts[2][..^4] + "AAAA";
        foreach (var token in new[]
                 {
                     tampered,
                     fake.IdToken("oaiapp_test", "nonce", issuer: "https://example.com"),
                     fake.IdToken("oaiapp_other", "nonce"),
                     fake.IdToken("oaiapp_test", "nonce", expires: now.AddHours(-1)),
                     fake.IdToken("oaiapp_test", "another-nonce"),
                     "not-a-jwt",
                 })
            await Assert.ThrowsAsync<ProviderOAuthException>(() =>
                service.ValidateIdTokenAsync(token, "oaiapp_test", "nonce", now, CancellationToken.None));
    }

    [Fact]
    public async Task Refresh_RotatesTheRefreshTokenWithTheIssuedClient()
    {
        using var fake = new FakeChatGpt();
        using var http = new HttpClient(fake);
        using var service = new ChatGptAuthService(http);

        var tokens = await service.RefreshAsync("oaiapp_test", "refresh-0", CancellationToken.None);

        Assert.Equal("access-1", tokens.AccessToken);
        Assert.Equal("refresh-1", tokens.RefreshToken);
        var request = Assert.Single(fake.Grants("refresh_token"));
        Assert.Equal("oaiapp_test", request.Form["client_id"]);
        Assert.Equal("refresh-0", request.Form["refresh_token"]);
        Assert.Equal("https://api.openai.com/v1", request.Form["resource"]);
        Assert.False(request.Form.ContainsKey("scope"));
    }

    [Theory]
    [InlineData(400, "{\"error\":\"invalid_grant\"}", true)]
    [InlineData(400, "{\"error\":{\"code\":\"refresh_token_reused\"}}", true)]
    [InlineData(401, "{}", true)]
    [InlineData(429, "{}", false)]
    [InlineData(503, "{\"detail\":\"busy\"}", false)]
    public async Task Refresh_SeparatesTerminalFromTemporaryFailures(int status, string body, bool requiresSignIn)
    {
        Loc.Load("en");
        using var fake = new FakeChatGpt { RefreshResponse = () => FakeChatGpt.Json(body, (HttpStatusCode)status) };
        using var http = new HttpClient(fake);
        using var service = new ChatGptAuthService(http);

        var error = await Assert.ThrowsAsync<ChatGptRefreshException>(() =>
            service.RefreshAsync("oaiapp_test", "refresh-0", CancellationToken.None));
        Assert.Equal(requiresSignIn, error.RequiresSignIn);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true, 1)]
    [InlineData(HttpStatusCode.BadRequest, false, 1)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false, 3)]
    public async Task Revoke_EndsTheRenewableSession(HttpStatusCode status, bool confirmed, int attempts)
    {
        using var fake = new FakeChatGpt { RevokeStatus = status };
        using var http = new HttpClient(fake);
        using var service = new ChatGptAuthService(http);

        Assert.Equal(confirmed, await service.RevokeAsync("oaiapp_test", "refresh-9", CancellationToken.None));

        var requests = fake.Requests.Where(request => request.Url == ChatGptAuthService.RevocationEndpoint).ToList();
        Assert.Equal(attempts, requests.Count);
        Assert.Equal("refresh-9", requests[0].Form["token"]);
        Assert.Equal("refresh_token", requests[0].Form["token_type_hint"]);
        Assert.Equal("oaiapp_test", requests[0].Form["client_id"]);
    }

    [Fact]
    public async Task Models_ListsThePlansDisplayedModelsInOrder()
    {
        using var fake = new FakeChatGpt();
        using var http = new HttpClient(fake);
        using var service = new ChatGptAuthService(http);

        var models = await service.GetModelsAsync("access-1", CancellationToken.None);

        Assert.Equal(["gpt-6.1-sol", "gpt-5.5"], models.Select(model => model.Id));
        var sol = models[0];
        Assert.Equal("GPT-6.1 Sol", sol.Name);
        Assert.Equal(272000, sol.ContextTokens);
        Assert.True(sol.SupportsTools);
        Assert.Equal(["low", "medium", "high"], sol.SupportedReasoningEfforts!);
        Assert.Equal("medium", sol.DefaultReasoningEffort);
        Assert.Null(models[1].SupportedReasoningEfforts);
        Assert.Equal("Bearer access-1", Assert.Single(fake.Requests).Authorization);
    }

    [Fact]
    public void Catalog_MarksTheModelsThatTakeTheResponsesLiteForm()
    {
        using var json = JsonDocument.Parse("""
            {"models":[{"slug":"gpt-6.1-sol","use_responses_lite":true},{"slug":"gpt-5.5","use_responses_lite":false},
                       {"slug":"hidden","visibility":"hide","use_responses_lite":true},{"slug":"plain"}]}
            """);
        Assert.Equal(["gpt-6.1-sol", "hidden"],
            ChatGptAuthService.ParseResponsesLiteModels(json.RootElement).OrderBy(slug => slug, StringComparer.Ordinal));
    }

    private static async Task ApproveAsync(
        HttpClient local, Dictionary<string, string> authorization, string extra, CancellationToken cancellationToken)
    {
        using var response = await local.GetAsync(authorization["redirect_uri"] + "?code=one-time-code&state="
            + Uri.EscapeDataString(authorization["state"]) + extra, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static HttpClient LocalHttpClient()
        => new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };

    internal static Dictionary<string, string> Query(string query) => query.TrimStart('?').Split('&')
        .Select(pair => pair.Split('=', 2)).ToDictionary(
            pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
            pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));

    private static string Challenge(string verifier) => Convert.ToBase64String(
        SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
