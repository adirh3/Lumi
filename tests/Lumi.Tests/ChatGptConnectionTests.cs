using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Byok;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatGptConnectionTests
{
    [Fact]
    public void Catalog_OffersChatGptAtTheTopAsASignInOnlyPlan()
    {
        var recommended = ProviderCatalog.All.Where(profile => profile.Category == ProviderCategory.Recommended).ToList();
        Assert.Equal(["copilot", "chatgpt"], recommended.Select(profile => profile.Id));
        var chatGpt = ProviderCatalog.Find(ProviderCatalog.ChatGptId)!;
        Assert.True(chatGpt.IsChatGpt);
        Assert.True(chatGpt.HasBrowserSignIn);
        Assert.Equal(ProviderOAuthKind.ChatGpt, chatGpt.OAuth);
        Assert.Equal("responses", chatGpt.WireApi);
        Assert.Equal(ChatGptAuthService.UsageUrl, chatGpt.UsageUrl);
    }

    [Fact]
    public void Catalog_NeverMistakesAnOpenAiApiKeyEndpointForChatGpt()
        => Assert.Equal("openai", ProviderCatalog.FromUrl("https://api.openai.com/v1")?.Id);

    [Fact]
    public async Task ProviderConfig_RoutesChatGptThroughTheBridgeWithoutItsStoredToken()
    {
        var keys = new FakeSecureKeyStore();
        var endpoint = ChatGptEndpoint("routed");
        var model = new ByokModel { MaxPromptTokens = 9000 };
        await keys.SetAsync(SecureKeyStoreFactory.EndpointKey(endpoint.Id), "stored-refresh-token");

        var provider = ByokConfigHelper.BuildProviderConfig(endpoint, model, keys);

        Assert.StartsWith("http://127.0.0.1:", provider.BaseUrl);
        Assert.EndsWith("/chatgpt/routed/v1", provider.BaseUrl);
        Assert.Equal("responses", provider.WireApi);
        Assert.Equal("openai", provider.Type);
        Assert.Equal(9000, provider.MaxPromptTokens);
        Assert.NotEqual("stored-refresh-token", provider.ApiKey);
        Assert.Null(provider.Headers);
        Assert.Equal(ByokConfigHelper.BuildProviderSignature(provider),
            ByokConfigHelper.BuildProviderSignature(ByokConfigHelper.BuildProviderConfig(endpoint, model, keys)));
    }

    [Fact]
    public void ProviderSignature_OutlivesTheBridgesPortAndKeyButNotOtherLoopbackProviders()
    {
        static ProviderConfig Provider(string baseUrl, string key)
            => new() { Type = "openai", WireApi = "responses", BaseUrl = baseUrl, ApiKey = key };
        static string? Signature(ProviderConfig provider) => ByokConfigHelper.BuildProviderSignature(provider);

        // A later launch serves the same connection from another port with another key.
        Assert.Equal(Signature(Provider("http://127.0.0.1:50001/chatgpt/plan/v1", "launch-1")),
            Signature(Provider("http://127.0.0.1:50002/chatgpt/plan/v1", "launch-2")));
        Assert.NotEqual(Signature(Provider("http://127.0.0.1:50001/chatgpt/plan/v1", "launch-1")),
            Signature(Provider("http://127.0.0.1:50001/chatgpt/other/v1", "launch-1")));
        // A local server's address and key still define where a session's requests go.
        Assert.NotEqual(Signature(Provider("http://127.0.0.1:11434/v1", "key-1")),
            Signature(Provider("http://127.0.0.1:11434/v1", "key-2")));
        Assert.NotEqual(Signature(Provider("http://127.0.0.1:11434/v1", "key-1")),
            Signature(Provider("http://127.0.0.1:11435/v1", "key-1")));
    }

    [Fact]
    public void ProviderConfig_AsksToSignInAgainOnceChatGptSignInExpired()
    {
        Loc.Load("en");
        var endpoint = ChatGptEndpoint("expired");
        endpoint.CredentialExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        var error = Assert.Throws<InvalidOperationException>(() => ByokConfigHelper.BuildProviderConfig(endpoint));
        Assert.Equal(Loc.Get("ChatGpt_SignInExpired"), error.Message);
    }

    [Fact]
    public void Snapshot_KeepsTheHostIdAndTheSignedInAccount()
    {
        var source = new AppData();
        source.Settings.ChatGptHostId = "urn:uuid:host";
        source.Settings.ByokEndpoints.Add(ChatGptEndpoint("saved"));

        var snapshot = AppDataSnapshotFactory.CreateIndexSnapshot(source);

        Assert.Equal("urn:uuid:host", snapshot.Settings.ChatGptHostId);
        var account = Assert.Single(snapshot.Settings.ByokEndpoints).Account!;
        Assert.NotSame(source.Settings.ByokEndpoints[0].Account, account);
        Assert.Equal(("oaiapp_test", "user-123", "ada@example.com"), (account.ClientId, account.Subject, account.Email));
    }

    internal static ByokEndpoint ChatGptEndpoint(string id) => new()
    {
        Id = id,
        Name = "ChatGPT",
        ConnectionProviderId = ProviderCatalog.ChatGptId,
        ProviderType = "openai",
        BaseUrl = ChatGptAuthService.ApiBaseUrl,
        WireApi = "responses",
        ApiKeyMode = ByokApiKeyMode.CredentialStore,
        Account = new ByokAccount { ClientId = "oaiapp_test", Subject = "user-123", Email = "ada@example.com" },
    };
}
