using System;
using System.Collections.Generic;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Byok;
using Xunit;

namespace Lumi.Tests;

public sealed class ByokConfigHelperTests
{
    // ── Token helpers ──

    [Theory]
    [InlineData("byok:abc", true)]
    [InlineData("gpt-4o", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsByokModel_Recognition(string? token, bool expected)
        => Assert.Equal(expected, ByokConfigHelper.IsByokModel(token));

    [Fact]
    public void BuildAndParseModelToken_RoundTrip()
    {
        var model = new ByokModel { Id = "abc123", DisplayName = "My GPT" };
        var token = ByokConfigHelper.BuildModelToken(model);

        Assert.Equal("byok:abc123", token);
        Assert.True(ByokConfigHelper.TryParseModelToken(token, out var id));
        Assert.Equal("abc123", id);
    }

    [Theory]
    [InlineData("gpt-4o", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("byok:", false)]
    public void TryParseModelToken_RejectsInvalid(string? token, bool expected)
        => Assert.Equal(expected, ByokConfigHelper.TryParseModelToken(token, out _));

    // ── Endpoint validation / normalization ──

    [Fact]
    public void NormalizeEndpoint_TrimsAndCanonicalizesProviderFields()
    {
        var endpoint = new ByokEndpoint
        {
            Name = "  My Endpoint  ",
            ProviderType = "OpenAI",
            BaseUrl = "  https://api.example.com/v1  ",
            WireApi = "OpenAI",
            AzureApiVersion = "  2024-02-01  ",
            ApiKeyEnvVar = "  MY_KEY  ",
        };

        ByokConfigHelper.NormalizeEndpoint(endpoint);

        Assert.Equal("My Endpoint", endpoint.Name);
        Assert.Equal("openai", endpoint.ProviderType);
        Assert.Equal("https://api.example.com/v1", endpoint.BaseUrl);
        Assert.Equal("completions", endpoint.WireApi); // legacy "openai" migrates to canonical
        Assert.Equal("2024-02-01", endpoint.AzureApiVersion);
        Assert.Equal("MY_KEY", endpoint.ApiKeyEnvVar);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", null)]
    [InlineData("http://localhost:11434/v1", null)]
    [InlineData("", "required")]
    [InlineData("not-a-url", "absolute")]
    [InlineData("ftp://example.com", "http or https")]
    public void ValidateBaseUrl_Validation(string? url, string? expectedFragment)
    {
        var error = ByokConfigHelper.ValidateBaseUrl(url);
        if (expectedFragment is null)
            Assert.Null(error);
        else
            Assert.Contains(expectedFragment, error);
    }

    [Fact]
    public void ValidateEndpoint_RequiresAzureApiVersionForAzure()
    {
        var endpoint = MakeValidEndpoint("e1");
        endpoint.ProviderType = "azure";
        endpoint.AzureApiVersion = "";
        Assert.NotNull(ByokConfigHelper.ValidateEndpoint(endpoint));

        endpoint.AzureApiVersion = "2024-02-01";
        Assert.Null(ByokConfigHelper.ValidateEndpoint(endpoint));
    }

    [Fact]
    public void ValidateModel_RequiresAllFields()
    {
        var endpoint = MakeValidEndpoint("e1");
        var model = new ByokModel { EndpointId = "e1", ModelId = "gpt-4o", DisplayName = "GPT-4o" };
        Assert.Null(ByokConfigHelper.ValidateModel(model, endpoint));

        model.DisplayName = "";
        Assert.NotNull(ByokConfigHelper.ValidateModel(model, endpoint));
        model.DisplayName = "GPT-4o";

        model.ModelId = "";
        Assert.NotNull(ByokConfigHelper.ValidateModel(model, endpoint));
        model.ModelId = "gpt-4o";

        model.EndpointId = "";
        Assert.NotNull(ByokConfigHelper.ValidateModel(model, endpoint));
    }

    // ── API key resolution ──

    [Fact]
    public void ResolveApiKey_NoneMode_AlwaysReturnsNull()
    {
        var endpoint = MakeValidEndpoint("e1");
        endpoint.ApiKeyMode = ByokApiKeyMode.None;
        endpoint.ApiKey = "should-be-ignored";
        Assert.Null(ByokConfigHelper.ResolveApiKey(endpoint));
    }

    [Fact]
    public void ResolveApiKey_StoredMode_ReturnsPlaintextValue()
    {
        var endpoint = MakeValidEndpoint("e1");
        endpoint.ApiKeyMode = ByokApiKeyMode.Stored;
        endpoint.ApiKey = "secret-key";
        Assert.Equal("secret-key", ByokConfigHelper.ResolveApiKey(endpoint));
    }

    [Fact]
    public void ResolveApiKey_StoredMode_FallsBackToEnv_WhenPlaintextMissing()
    {
        var envVar = "BYOK_TEST_VAR_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envVar, "env-fallback-key");
        try
        {
            var endpoint = MakeValidEndpoint("e1");
            endpoint.ApiKeyMode = ByokApiKeyMode.Stored;
            endpoint.ApiKey = "";
            endpoint.ApiKeyEnvVar = envVar;

            Assert.Equal("env-fallback-key", ByokConfigHelper.ResolveApiKey(endpoint));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public void ResolveApiKey_EnvMode_IgnoresPlaintext_WhenBothConfigured()
    {
        var envVar = "BYOK_TEST_VAR_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envVar, "env-key-123");
        try
        {
            var endpoint = MakeValidEndpoint("e1");
            endpoint.ApiKeyMode = ByokApiKeyMode.EnvVar;
            endpoint.ApiKeyEnvVar = envVar;
            endpoint.ApiKey = "stored-key-xyz";

            Assert.Equal("env-key-123", ByokConfigHelper.ResolveApiKey(endpoint));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public void ResolveApiKey_CredentialStoreMode_IgnoresPlaintext_WhenStoreAndEnvAreEmpty()
    {
        Environment.SetEnvironmentVariable(ByokConfigHelper.FallbackApiKeyEnvVar, null);
        var endpoint = MakeValidEndpoint("e1");
        endpoint.ApiKeyMode = ByokApiKeyMode.CredentialStore;
        endpoint.ApiKeyEnvVar = "BYOK_TEST_MISSING_" + Guid.NewGuid().ToString("N");
        endpoint.ApiKey = "stale-plaintext-key";

        Assert.Null(ByokConfigHelper.ResolveApiKey(endpoint, new FakeSecureKeyStore()));
    }

    [Fact]
    public void ResolveApiKey_EnvVarReadsFromProcessEnvironment()
    {
        var envVar = "BYOK_TEST_VAR_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envVar, "env-key-123");
        try
        {
            var endpoint = MakeValidEndpoint("e1");
            endpoint.ApiKeyMode = ByokApiKeyMode.EnvVar;
            endpoint.ApiKeyEnvVar = envVar;
            Assert.Equal("env-key-123", ByokConfigHelper.ResolveApiKey(endpoint));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public void ResolveApiKey_FallsBackToGlobalEnvVar_WhenNoOtherSource()
    {
        // When ApiKeyEnvVar is empty and there's no stored key, the global fallback env var applies.
        Environment.SetEnvironmentVariable("LUMI_BYOK_API_KEY", "global-fallback");
        try
        {
            var endpoint = MakeValidEndpoint("e1");
            endpoint.ApiKeyMode = ByokApiKeyMode.EnvVar;
            endpoint.ApiKeyEnvVar = "";
            Assert.Equal("global-fallback", ByokConfigHelper.ResolveApiKey(endpoint));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LUMI_BYOK_API_KEY", null);
        }
    }

    // ── Credential Store mode ──

    [Fact]
    public async Task ResolveApiKey_CredentialStore_ReadsFromKeyStore()
    {
        var endpoint = MakeValidEndpoint("e1");
        endpoint.ApiKeyMode = ByokApiKeyMode.CredentialStore;
        var store = new FakeSecureKeyStore();
        await store.SetAsync(SecureKeyStoreFactory.EndpointKey("e1"), "os-stored-secret");

        Assert.Equal("os-stored-secret", ByokConfigHelper.ResolveApiKey(endpoint, store));
    }

    [Theory]
    [InlineData(null, false)]   // null store → degrade to env
    [InlineData(false, false)]  // empty supported store → degrade to env
    [InlineData(false, true)]   // unsupported store (with stale entry) → degrade to env
    public async Task ResolveApiKey_CredentialStore_FallsBackToEnv_WhenStoreUnavailable(bool? supportedFlag, bool seedStore)
    {
        Environment.SetEnvironmentVariable("LUMI_BYOK_API_KEY", "env-fallback");
        try
        {
            var endpoint = MakeValidEndpoint("e1");
            endpoint.ApiKeyMode = ByokApiKeyMode.CredentialStore;

            ISecureKeyStore? store = supportedFlag is null
                ? null
                : new FakeSecureKeyStore { IsSupported = supportedFlag.Value };
            if (store is not null && seedStore)
                await store.SetAsync(SecureKeyStoreFactory.EndpointKey("e1"), "ignored");

            Assert.Equal("env-fallback", ByokConfigHelper.ResolveApiKey(endpoint, store));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LUMI_BYOK_API_KEY", null);
        }
    }

    [Fact]
    public async Task ResolveApiKey_CredentialStore_PrefersOsKey_OverPlaintext()
    {
        // Migrating from Stored→CredentialStore: the OS entry wins over leftover plaintext.
        var endpoint = MakeValidEndpoint("e1");
        endpoint.ApiKeyMode = ByokApiKeyMode.CredentialStore;
        endpoint.ApiKey = "leftover-plaintext";
        var store = new FakeSecureKeyStore();
        await store.SetAsync(SecureKeyStoreFactory.EndpointKey("e1"), "fresh-os-key");

        Assert.Equal("fresh-os-key", ByokConfigHelper.ResolveApiKey(endpoint, store));
    }

    [Fact]
    public async Task TryBuildProviderConfig_CredentialStore_ThreadKeyStoreThroughToProviderConfig()
    {
        var endpoint = MakeValidEndpoint("e1");
        endpoint.ApiKeyMode = ByokApiKeyMode.CredentialStore;
        var settings = new UserSettings();
        settings.ByokEndpoints.Add(endpoint);
        settings.ByokModels.Add(new ByokModel { Id = "m1", EndpointId = "e1", ModelId = "gpt-4o", DisplayName = "GPT-4o" });
        var store = new FakeSecureKeyStore();
        await store.SetAsync(SecureKeyStoreFactory.EndpointKey("e1"), "resolved-os-key");

        var pc = ByokConfigHelper.TryBuildProviderConfig(settings, "byok:m1", out _, store);

        Assert.NotNull(pc);
        Assert.Equal("resolved-os-key", pc!.ApiKey);
    }

    // ── GetValidEndpoints / GetValidModels ──

    [Fact]
    public void GetValidEndpoints_FiltersDisabledAndInvalid()
    {
        var settings = new UserSettings();
        var valid = MakeValidEndpoint("e1");
        var disabled = MakeValidEndpoint("e2"); disabled.IsEnabled = false;
        var invalid = new ByokEndpoint { Name = "Bad", BaseUrl = "" };
        settings.ByokEndpoints.Add(valid);
        settings.ByokEndpoints.Add(disabled);
        settings.ByokEndpoints.Add(invalid);

        var result = ByokConfigHelper.GetValidEndpoints(settings);

        Assert.Single(result);
        Assert.Same(valid, result[0]);
    }

    [Fact]
    public void GetValidHelpers_DoNotMutateSourceFields()
    {
        // Regression guard: queries must not rewrite persisted settings.
        var settings = new UserSettings();
        var endpoint = new ByokEndpoint
        {
            Id = "e1",
            Name = "  My Endpoint  ",
            ProviderType = "OpenAI",
            BaseUrl = "  https://api.example.com/v1  ",
            WireApi = "OpenAI",
            ApiKeyMode = ByokApiKeyMode.None,
            IsEnabled = true,
        };
        var model = new ByokModel
        {
            Id = "m1",
            EndpointId = " e1 ",
            ModelId = " gpt-4o ",
            DisplayName = " GPT-4o ",
            IsEnabled = true,
        };
        settings.ByokEndpoints.Add(endpoint);
        settings.ByokModels.Add(model);

        _ = ByokConfigHelper.GetValidEndpoints(settings);
        _ = ByokConfigHelper.GetValidModels(settings);

        Assert.Equal("  My Endpoint  ", endpoint.Name);
        Assert.Equal("OpenAI", endpoint.ProviderType);
        Assert.Equal("  https://api.example.com/v1  ", endpoint.BaseUrl);
        Assert.Equal("OpenAI", endpoint.WireApi);
        Assert.Equal(" e1 ", model.EndpointId);
        Assert.Equal(" gpt-4o ", model.ModelId);
        Assert.Equal(" GPT-4o ", model.DisplayName);
    }

    [Fact]
    public void GetValidModels_ExcludesModelsWithMissingOrDisabledEndpoint()
    {
        var settings = new UserSettings();
        settings.ByokEndpoints.Add(MakeValidEndpoint("e1"));

        var good = new ByokModel { Id = "m1", EndpointId = "e1", ModelId = "gpt-4o", DisplayName = "GPT-4o", IsEnabled = true };
        var badEndpoint = new ByokModel { Id = "m2", EndpointId = "missing", ModelId = "x", DisplayName = "X", IsEnabled = true };
        var disabled = new ByokModel { Id = "m3", EndpointId = "e1", ModelId = "gpt-4o", DisplayName = "GPT-4o", IsEnabled = false };
        var noModelId = new ByokModel { Id = "m4", EndpointId = "e1", ModelId = "", DisplayName = "GPT-4o", IsEnabled = true };

        settings.ByokModels.Add(good);
        settings.ByokModels.Add(badEndpoint);
        settings.ByokModels.Add(disabled);
        settings.ByokModels.Add(noModelId);

        var result = ByokConfigHelper.GetValidModels(settings);

        Assert.Single(result);
        Assert.Same(good, result[0]);
    }

    // ── TryResolveModel / TryBuildProviderConfig ──

    [Theory]
    [InlineData("gpt-4o", false)]
    [InlineData(null, false)]
    [InlineData("byok:nonexistent", false)]
    public void TryResolveModel_FailsForInvalidOrUnknownToken(string? token, bool expected)
    {
        var settings = new UserSettings();
        Assert.Equal(expected, ByokConfigHelper.TryResolveModel(settings, token, out _, out _, out _));
    }

    [Fact]
    public void TryResolveModel_SucceedsForValidConfiguration()
    {
        var settings = new UserSettings();
        var endpoint = MakeValidEndpoint("e1");
        var model = new ByokModel { Id = "m1", EndpointId = "e1", ModelId = "gpt-4o", DisplayName = "GPT-4o" };
        settings.ByokEndpoints.Add(endpoint);
        settings.ByokModels.Add(model);

        Assert.True(ByokConfigHelper.TryResolveModel(
            settings, ByokConfigHelper.BuildModelToken(model),
            out var resolvedModel, out var resolvedEndpoint, out var actualModelId));

        Assert.Same(model, resolvedModel);
        Assert.Same(endpoint, resolvedEndpoint);
        Assert.Equal("gpt-4o", actualModelId);
    }

    [Fact]
    public void TryBuildProviderConfig_BuildsProviderWithExpectedFields()
    {
        var settings = new UserSettings();
        var endpoint = MakeValidEndpoint("e1");
        var model = new ByokModel { Id = "m1", EndpointId = "e1", ModelId = "gpt-4o", DisplayName = "GPT-4o" };
        settings.ByokEndpoints.Add(endpoint);
        settings.ByokModels.Add(model);

        var pc = ByokConfigHelper.TryBuildProviderConfig(settings, ByokConfigHelper.BuildModelToken(model), out var actualModelId);

        Assert.NotNull(pc);
        Assert.Equal("openai", pc!.Type);
        Assert.Equal("completions", pc.WireApi);
        Assert.Equal("https://api.example.com/v1", pc.BaseUrl);
        Assert.Equal("gpt-4o", actualModelId);
    }

    [Fact]
    public void TryBuildProviderConfig_SetsAzureApiVersion_ForAzureProvider()
    {
        var settings = new UserSettings();
        var endpoint = MakeValidEndpoint("e1");
        endpoint.ProviderType = "azure";
        endpoint.AzureApiVersion = "2024-02-01";
        var model = new ByokModel { Id = "m1", EndpointId = "e1", ModelId = "my-deployment", DisplayName = "My GPT" };
        settings.ByokEndpoints.Add(endpoint);
        settings.ByokModels.Add(model);

        var pc = ByokConfigHelper.TryBuildProviderConfig(settings, ByokConfigHelper.BuildModelToken(model), out _);

        Assert.NotNull(pc);
        Assert.Equal("azure", pc!.Type);
        Assert.NotNull(pc.Azure);
        Assert.Equal("2024-02-01", pc.Azure!.ApiVersion);
    }

    [Fact]
    public void TryBuildProviderConfig_OmitsWireApiForAnthropic()
    {
        var settings = new UserSettings();
        var endpoint = MakeValidEndpoint("e1");
        endpoint.ProviderType = "anthropic";
        endpoint.BaseUrl = "https://api.anthropic.com";
        endpoint.WireApi = "completions"; // ignored for anthropic
        var model = new ByokModel { Id = "m1", EndpointId = "e1", ModelId = "claude-sonnet-4.5", DisplayName = "Claude" };
        settings.ByokEndpoints.Add(endpoint);
        settings.ByokModels.Add(model);

        var pc = ByokConfigHelper.TryBuildProviderConfig(settings, ByokConfigHelper.BuildModelToken(model), out _);

        Assert.NotNull(pc);
        Assert.Equal("anthropic", pc!.Type);
        Assert.True(string.IsNullOrEmpty(pc.WireApi));
    }

    [Fact]
    public void TryBuildProviderConfig_ReturnsNullForStaleToken()
        => Assert.Null(ByokConfigHelper.TryBuildProviderConfig(new UserSettings(), "byok:nonexistent", out _));

    [Fact]
    public void ResolveSessionModelRoute_CanonicalTokenReturnsWireModelAndProvider()
    {
        var settings = new UserSettings();
        var endpoint = MakeValidEndpoint("e1");
        var model = new ByokModel { Id = "m1", EndpointId = "e1", ModelId = "wire-gpt", DisplayName = "GPT" };
        settings.ByokEndpoints.Add(endpoint);
        settings.ByokModels.Add(model);

        var route = ByokConfigHelper.ResolveSessionModelRoute(settings, "byok:m1");

        Assert.True(route.IsByok);
        Assert.False(route.IsInvalidByok);
        Assert.Equal("byok:m1", route.SelectionToken);
        Assert.Equal("wire-gpt", route.WireModelId);
    }

    [Fact]
    public void ResolveSessionModelRoute_BareCollisionWithoutLegacyEvidenceRemainsCopilot()
    {
        var settings = new UserSettings();
        settings.ByokEndpoints.Add(MakeValidEndpoint("e1"));
        settings.ByokModels.Add(new ByokModel
        {
            Id = "m1", EndpointId = "e1", ModelId = "gpt-4o", DisplayName = "Custom GPT",
        });

        var route = ByokConfigHelper.ResolveSessionModelRoute(settings, "gpt-4o");

        Assert.False(route.IsByok);
        Assert.False(route.IsInvalidByok);
        Assert.Equal("gpt-4o", route.SelectionToken);
        Assert.Equal("gpt-4o", route.WireModelId);
    }

    [Fact]
    public void ResolveSessionModelRoute_LegacyWireIdRequiresUniqueMatchingSignature()
    {
        var settings = new UserSettings();
        var endpoint = MakeValidEndpoint("e1");
        var model = new ByokModel { Id = "m1", EndpointId = "e1", ModelId = "wire-gpt", DisplayName = "GPT" };
        settings.ByokEndpoints.Add(endpoint);
        settings.ByokModels.Add(model);
        var provider = ByokConfigHelper.TryBuildProviderConfig(settings, "byok:m1", out _);
        var signature = ByokConfigHelper.BuildProviderSignature(provider);

        var route = ByokConfigHelper.ResolveSessionModelRoute(
            settings,
            "wire-gpt",
            "session-1",
            signature,
            allowLegacyByWireId: true);

        Assert.True(route.IsByok);
        Assert.False(route.IsInvalidByok);
        Assert.Equal("byok:m1", route.SelectionToken);
        Assert.Equal("wire-gpt", route.WireModelId);
    }

    [Fact]
    public void ResolveSessionModelRoute_LegacySignatureMismatchIsInvalid()
    {
        var settings = new UserSettings();
        settings.ByokEndpoints.Add(MakeValidEndpoint("e1"));
        settings.ByokModels.Add(new ByokModel
        {
            Id = "m1", EndpointId = "e1", ModelId = "wire-gpt", DisplayName = "GPT",
        });

        var route = ByokConfigHelper.ResolveSessionModelRoute(
            settings,
            "wire-gpt",
            "session-1",
            "different-signature",
            allowLegacyByWireId: true);

        Assert.False(route.IsByok);
        Assert.True(route.IsInvalidByok);
        Assert.Null(route.WireModelId);
    }

    [Fact]
    public void ResolveSessionModelRoute_AmbiguousLegacyWireIdIsInvalid()
    {
        var settings = new UserSettings();
        settings.ByokEndpoints.Add(MakeValidEndpoint("e1"));
        settings.ByokModels.Add(new ByokModel { Id = "m1", EndpointId = "e1", ModelId = "wire-gpt", DisplayName = "One" });
        settings.ByokModels.Add(new ByokModel { Id = "m2", EndpointId = "e1", ModelId = "wire-gpt", DisplayName = "Two" });
        var provider = ByokConfigHelper.TryBuildProviderConfig(settings, "byok:m1", out _);

        var route = ByokConfigHelper.ResolveSessionModelRoute(
            settings,
            "wire-gpt",
            "session-1",
            ByokConfigHelper.BuildProviderSignature(provider),
            allowLegacyByWireId: true);

        Assert.False(route.IsByok);
        Assert.True(route.IsInvalidByok);
    }

    [Fact]
    public void ResolveSessionModelRoute_ExplicitBareSelectionIgnoresLegacySignature()
    {
        var settings = new UserSettings();
        var endpoint = MakeValidEndpoint("e1");
        settings.ByokEndpoints.Add(endpoint);
        settings.ByokModels.Add(new ByokModel
        {
            Id = "m1", EndpointId = "e1", ModelId = "gpt-4o", DisplayName = "Custom GPT",
        });
        var provider = ByokConfigHelper.TryBuildProviderConfig(settings, "byok:m1", out _);

        var route = ByokConfigHelper.ResolveSessionModelRoute(
            settings,
            "gpt-4o",
            "session-1",
            ByokConfigHelper.BuildProviderSignature(provider));

        Assert.False(route.IsByok);
        Assert.False(route.IsInvalidByok);
        Assert.Equal("gpt-4o", route.WireModelId);
    }

    [Fact]
    public void ResolveSessionModelRoute_TwoByokModelsOnSameEndpoint_ReturnDistinctWireIdsNotTokens()
    {
        // Regression guard for PR #14 item 8: SwitchModelMidSessionAsync relies on
        // ResolveSessionModelRoute to translate the picker token (byok:{id}) into the provider's
        // wire model id before calling SetModelAsync. When two BYOK models share the same endpoint
        // (and therefore the same provider signature — the "no provider change detected" branch in
        // SwitchModelMidSessionAsync), the resolver must still return each model's DISTINCT wire
        // model id and NEVER the byok:{id} picker token. If it returned the token, switching
        // between the two same-signature models would send a raw "byok:{id}" string down to the
        // SDK instead of the provider-recognized wire id.
        //
        // This test pins the contract at the resolver level (the actual data source for
        // SwitchModelMidSessionAsync's SetModelAsync argument), since CopilotSession is an
        // external GitHub.Copilot SDK type that cannot be substituted in a unit test without a
        // mocking library.
        var settings = new UserSettings();
        var endpoint = MakeValidEndpoint("e1");
        settings.ByokEndpoints.Add(endpoint);
        settings.ByokModels.Add(new ByokModel
        {
            Id = "m1", EndpointId = "e1", ModelId = "wire-gpt-4o", DisplayName = "GPT-4o",
        });
        settings.ByokModels.Add(new ByokModel
        {
            Id = "m2", EndpointId = "e1", ModelId = "wire-gpt-4o-mini", DisplayName = "GPT-4o mini",
        });

        // Both models share endpoint e1, so their provider signatures must be identical — this is
        // what makes SwitchModelMidSessionAsync take the in-place SetModelAsync path rather than
        // forcing session recreation.
        var providerM1 = ByokConfigHelper.TryBuildProviderConfig(settings, "byok:m1", out _);
        var providerM2 = ByokConfigHelper.TryBuildProviderConfig(settings, "byok:m2", out _);
        Assert.Equal(
            ByokConfigHelper.BuildProviderSignature(providerM1),
            ByokConfigHelper.BuildProviderSignature(providerM2));

        // Switching to m1: SelectionToken is the canonical picker token, but WireModelId (the value
        // SwitchModelMidSessionAsync forwards to SetModelAsync) is the provider's wire id.
        var routeM1 = ByokConfigHelper.ResolveSessionModelRoute(settings, "byok:m1");
        Assert.True(routeM1.IsByok);
        Assert.False(routeM1.IsInvalidByok);
        Assert.Equal("byok:m1", routeM1.SelectionToken);
        Assert.Equal("wire-gpt-4o", routeM1.WireModelId);
        Assert.NotEqual(routeM1.SelectionToken, routeM1.WireModelId);

        // Switching to m2 on the same endpoint: distinct wire id, still never the token.
        var routeM2 = ByokConfigHelper.ResolveSessionModelRoute(settings, "byok:m2");
        Assert.True(routeM2.IsByok);
        Assert.False(routeM2.IsInvalidByok);
        Assert.Equal("byok:m2", routeM2.SelectionToken);
        Assert.Equal("wire-gpt-4o-mini", routeM2.WireModelId);
        Assert.NotEqual(routeM2.SelectionToken, routeM2.WireModelId);

        // The two same-signature models produce distinct wire ids — proving a same-signature
        // mid-session switch would actually change the model on the wire, not no-op or send a token.
        Assert.NotEqual(routeM1.WireModelId, routeM2.WireModelId);
    }

    // ── WireApi normalization ──

    [Theory]
    [InlineData("completions", "completions")]
    [InlineData("responses", "responses")]
    [InlineData("Responses", "responses")]
    [InlineData("COMPLETIONS", "completions")]
    [InlineData("openai", "completions")]      // legacy migrates to SDK default
    [InlineData("anthropic", "completions")]   // legacy migrates to SDK default
    [InlineData("", "completions")]
    [InlineData(null, "completions")]
    [InlineData("bogus", "completions")]
    public void NormalizeWireApi_Canonicalizes(string? input, string expected)
        => Assert.Equal(expected, ByokConfigHelper.NormalizeWireApi(input));

    // ── TestEndpoint ──

    [Fact]
    public void TestEndpoint_PassesForNoneMode()
    {
        var endpoint = MakeValidEndpoint("e1");
        endpoint.ApiKeyMode = ByokApiKeyMode.None;
        Assert.Null(ByokConfigHelper.TestEndpoint(endpoint));
    }

    [Fact]
    public void TestEndpoint_PassesWhenAnyKeyResolves()
    {
        // EnvVar mode must ignore stale plaintext and fail when no environment key exists.
        var stored = MakeValidEndpoint("e1");
        stored.ApiKeyMode = ByokApiKeyMode.EnvVar;
        stored.ApiKeyEnvVar = "THIS_ENV_VAR_SHOULD_NOT_EXIST";
        stored.ApiKey = "stored-key";
        Assert.NotNull(ByokConfigHelper.TestEndpoint(stored));

        // Env-mode endpoint with a real env var → passes.
        var envVar = "BYOK_TEST_ENDPOINT_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envVar, "k");
        try
        {
            var envEndpoint = MakeValidEndpoint("e2");
            envEndpoint.ApiKeyMode = ByokApiKeyMode.EnvVar;
            envEndpoint.ApiKeyEnvVar = envVar;
            Assert.Null(ByokConfigHelper.TestEndpoint(envEndpoint));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public void TestEndpoint_FailsWhenKeyMissing()
    {
        Environment.SetEnvironmentVariable("LUMI_BYOK_API_KEY", null);
        var envVar = "BYOK_TEST_MISSING_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envVar, null);

        var endpoint = MakeValidEndpoint("e1");
        endpoint.ApiKeyMode = ByokApiKeyMode.EnvVar;
        endpoint.ApiKeyEnvVar = envVar;
        var error = ByokConfigHelper.TestEndpoint(endpoint);

        Assert.NotNull(error);
        Assert.Contains(envVar, error);
        Assert.Contains(ByokConfigHelper.FallbackApiKeyEnvVar, error);

        // Stored mode with empty key also fails.
        endpoint.ApiKeyMode = ByokApiKeyMode.Stored;
        endpoint.ApiKey = "";
        Assert.NotNull(ByokConfigHelper.TestEndpoint(endpoint));
    }

    [Fact]
    public async Task TestEndpoint_CredentialStore_PassesWhenOsKeyPresent()
    {
        // OS store has the key → no need for env var or plaintext. This is the bug fix:
        // previously CredentialStore mode fell through to the env-var-only path and reported
        // a misleading "API key not found" error.
        Environment.SetEnvironmentVariable("LUMI_BYOK_API_KEY", null);
        try
        {
            var endpoint = MakeValidEndpoint("e1");
            endpoint.ApiKeyMode = ByokApiKeyMode.CredentialStore;
            endpoint.ApiKeyEnvVar = "THIS_ENV_VAR_SHOULD_NOT_EXIST";
            endpoint.ApiKey = "";

            var store = new FakeSecureKeyStore();
            await store.SetAsync(SecureKeyStoreFactory.EndpointKey("e1"), "os-stored-secret");

            Assert.Null(ByokConfigHelper.TestEndpoint(endpoint, store));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LUMI_BYOK_API_KEY", null);
        }
    }

    [Fact]
    public void TestEndpoint_CredentialStore_FallsBackToEnvWhenStoreEmpty()
    {
        // Store is supported but empty → degrade to env-var chain (mirrors ResolveApiKey).
        var envVar = "BYOK_TEST_OS_FALLBACK_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envVar, "env-key");
        try
        {
            var endpoint = MakeValidEndpoint("e1");
            endpoint.ApiKeyMode = ByokApiKeyMode.CredentialStore;
            endpoint.ApiKeyEnvVar = envVar;
            endpoint.ApiKey = "";

            var store = new FakeSecureKeyStore(); // empty store
            Assert.Null(ByokConfigHelper.TestEndpoint(endpoint, store));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public void TestEndpoint_CredentialStore_FailsWhenStoreEmptyAndNoFallback()
    {
        // Store empty, no env var, no plaintext → fail with a helpful message.
        Environment.SetEnvironmentVariable("LUMI_BYOK_API_KEY", null);
        var endpoint = MakeValidEndpoint("e1");
        endpoint.ApiKeyMode = ByokApiKeyMode.CredentialStore;
        endpoint.ApiKeyEnvVar = "";
        endpoint.ApiKey = "";

        var store = new FakeSecureKeyStore(); // empty store
        var error = ByokConfigHelper.TestEndpoint(endpoint, store);

        Assert.NotNull(error);
        Assert.Contains(ByokConfigHelper.FallbackApiKeyEnvVar, error);
    }

    [Fact]
    public void TestEndpoint_CredentialStore_IgnoresPlaintextWhenStoreAndEnvAreEmpty()
    {
        Environment.SetEnvironmentVariable(ByokConfigHelper.FallbackApiKeyEnvVar, null);
        var endpoint = MakeValidEndpoint("e1");
        endpoint.ApiKeyMode = ByokApiKeyMode.CredentialStore;
        endpoint.ApiKeyEnvVar = "";
        endpoint.ApiKey = "stale-plaintext-key";

        Assert.NotNull(ByokConfigHelper.TestEndpoint(endpoint, new FakeSecureKeyStore()));
    }

    [Theory]
    [InlineData(null)]   // no store passed (e.g. caller has no DI access)
    [InlineData(false)]  // store reports IsSupported = false (off-Windows)
    public void TestEndpoint_CredentialStore_DegradesGracefullyWithoutUsableStore(bool? supportedFlag)
    {
        // Without a usable store, CredentialStore mode behaves like Stored/EnvVar — it checks
        // plaintext + env vars. With nothing configured, it fails with a helpful message
        // rather than silently passing.
        Environment.SetEnvironmentVariable("LUMI_BYOK_API_KEY", null);
        try
        {
            var endpoint = MakeValidEndpoint("e1");
            endpoint.ApiKeyMode = ByokApiKeyMode.CredentialStore;
            endpoint.ApiKeyEnvVar = "";
            endpoint.ApiKey = "";

            ISecureKeyStore? store = supportedFlag is null
                ? null
                : new FakeSecureKeyStore { IsSupported = supportedFlag.Value };

            var error = ByokConfigHelper.TestEndpoint(endpoint, store);
            Assert.NotNull(error);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LUMI_BYOK_API_KEY", null);
        }
    }

    // ── Find / list helpers ──

    [Fact]
    public void FindHelpers_ReturnNullForUnknown()
    {
        var settings = new UserSettings();
        Assert.Null(ByokConfigHelper.FindEndpoint(settings, "missing"));
        Assert.Null(ByokConfigHelper.FindModel(settings, "missing"));
    }

    [Fact]
    public void GetModelsForEndpoint_ReturnsAllMatching()
    {
        var settings = new UserSettings();
        var m1 = new ByokModel { Id = "m1", EndpointId = "e1" };
        var m2 = new ByokModel { Id = "m2", EndpointId = "e2" };
        var m3 = new ByokModel { Id = "m3", EndpointId = "e1" };
        settings.ByokModels.Add(m1);
        settings.ByokModels.Add(m2);
        settings.ByokModels.Add(m3);

        var result = ByokConfigHelper.GetModelsForEndpoint(settings, "e1");

        Assert.Equal(2, result.Count);
        Assert.Contains(m1, result);
        Assert.Contains(m3, result);
    }

    // ── Custom headers ──

    [Fact]
    public void BuildProviderConfig_CopiesUserConfiguredHeaders()
    {
        var endpoint = MakeValidEndpoint("e1");
        endpoint.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["api-key"] = "k",
            ["X-Custom-Header"] = "v",
        };

        var pc = ByokConfigHelper.BuildProviderConfig(endpoint);

        Assert.NotNull(pc.Headers);
        Assert.Equal("k", pc.Headers!["api-key"]);
        Assert.Equal("v", pc.Headers["X-Custom-Header"]);
    }

    [Fact]
    public void BuildProviderConfig_ResolvesApiKeyHeaderToken_FromEndpointApiKey()
    {
        var endpoint = MakeValidEndpoint("e1");
        endpoint.ApiKeyMode = ByokApiKeyMode.Stored;
        endpoint.ApiKey = "stored-secret";
        endpoint.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["api-key"] = ByokConfigHelper.ApiKeyHeaderToken,
        };

        var pc = ByokConfigHelper.BuildProviderConfig(endpoint);

        Assert.NotNull(pc.Headers);
        Assert.Equal("stored-secret", pc.Headers!["api-key"]);
    }

    [Fact]
    public void BuildProviderConfig_ResolvesEnvHeaderToken_FromEnvironment()
    {
        var envName = "BYOK_HEADER_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, "env-header-secret");
        try
        {
            var endpoint = MakeValidEndpoint("e1");
            endpoint.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["api-key"] = $"${{env:{envName}}}",
            };

            var pc = ByokConfigHelper.BuildProviderConfig(endpoint);

            Assert.NotNull(pc.Headers);
            Assert.Equal("env-header-secret", pc.Headers!["api-key"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    [Fact]
    public void BuildProviderConfig_DropsHeader_WhenDynamicTokenCannotResolve()
    {
        var endpoint = MakeValidEndpoint("e1");
        endpoint.ApiKeyMode = ByokApiKeyMode.None;
        endpoint.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["api-key"] = ByokConfigHelper.ApiKeyHeaderToken,
            ["X-Keep"] = "value",
        };

        var pc = ByokConfigHelper.BuildProviderConfig(endpoint);

        Assert.NotNull(pc.Headers);
        Assert.False(pc.Headers!.ContainsKey("api-key"));
        Assert.Equal("value", pc.Headers["X-Keep"]);
    }

    [Fact]
    public void BuildProviderConfig_ThrowsForInvalidEndpoint()
    {
        var endpoint = MakeValidEndpoint("e1");
        endpoint.BaseUrl = "";

        Assert.Throws<InvalidOperationException>(() => ByokConfigHelper.BuildProviderConfig(endpoint));
    }

    // ── Azure AI Foundry detection ──

    [Theory]
    [InlineData("https://csd-faip-001-resource.services.ai.azure.com/openai/v1", true)]
    [InlineData("https://my-resource.openai.azure.com", true)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("https://api.z.ai/api/anthropic", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAzureAiFoundryUrl_Detection(string? url, bool expected)
        => Assert.Equal(expected, ByokConfigHelper.IsAzureAiFoundryUrl(url));

    // ── BuildProviderSignature (mid-session model switch detection) ──

    [Fact]
    public void BuildProviderSignature_NullProvider_ReturnsNull()
        => Assert.Null(ByokConfigHelper.BuildProviderSignature(null));

    [Fact]
    public void BuildProviderSignature_StableForSameProvider()
    {
        var endpoint = MakeValidEndpoint("e1");

        var pc1 = ByokConfigHelper.BuildProviderConfig(endpoint);
        var pc2 = ByokConfigHelper.BuildProviderConfig(endpoint);

        Assert.Equal(
            ByokConfigHelper.BuildProviderSignature(pc1),
            ByokConfigHelper.BuildProviderSignature(pc2));
    }

    [Fact]
    public void BuildProviderSignature_DiffersByRoutingRelevantFields()
    {
        // Each routing-relevant field change must invalidate the cached session signature.
        static ByokEndpoint Make(string id, string url = "https://api.example.com/v1", string type = "openai", string wire = "completions")
        {
            var ep = MakeValidEndpoint(id);
            ep.BaseUrl = url;
            ep.ProviderType = type;
            ep.WireApi = wire;
            return ep;
        }

        // Differs by BaseUrl.
        Assert.NotEqual(
            ByokConfigHelper.BuildProviderSignature(ByokConfigHelper.BuildProviderConfig(Make("a", "https://api.a.example.com/v1"))),
            ByokConfigHelper.BuildProviderSignature(ByokConfigHelper.BuildProviderConfig(Make("b", "https://api.b.example.com/v1"))));

        // Differs by Type.
        Assert.NotEqual(
            ByokConfigHelper.BuildProviderSignature(ByokConfigHelper.BuildProviderConfig(Make("o", type: "openai"))),
            ByokConfigHelper.BuildProviderSignature(ByokConfigHelper.BuildProviderConfig(Make("a", type: "anthropic"))));

        // Differs by WireApi.
        Assert.NotEqual(
            ByokConfigHelper.BuildProviderSignature(ByokConfigHelper.BuildProviderConfig(Make("c", wire: "completions"))),
            ByokConfigHelper.BuildProviderSignature(ByokConfigHelper.BuildProviderConfig(Make("r", wire: "responses"))));

        // Differs by ApiKey.
        var k1 = MakeValidEndpoint("k1"); k1.ApiKeyMode = ByokApiKeyMode.Stored; k1.ApiKey = "key-one";
        var k2 = MakeValidEndpoint("k2"); k2.ApiKeyMode = ByokApiKeyMode.Stored; k2.ApiKey = "key-two";
        Assert.NotEqual(
            ByokConfigHelper.BuildProviderSignature(ByokConfigHelper.BuildProviderConfig(k1)),
            ByokConfigHelper.BuildProviderSignature(ByokConfigHelper.BuildProviderConfig(k2)));

        // Differs by token limits.
        var endpoint = MakeValidEndpoint("t");
        var modelNoLimits = new ByokModel { EndpointId = "t", ModelId = "gpt-4o", DisplayName = "G" };
        var modelWithLimits = new ByokModel { EndpointId = "t", ModelId = "gpt-4o", DisplayName = "G", MaxOutputTokens = 4096 };
        Assert.NotEqual(
            ByokConfigHelper.BuildProviderSignature(ByokConfigHelper.BuildProviderConfig(endpoint, modelNoLimits)),
            ByokConfigHelper.BuildProviderSignature(ByokConfigHelper.BuildProviderConfig(endpoint, modelWithLimits)));
    }

    [Fact]
    public void BuildProviderSignature_DoesNotLeakApiKey()
    {
        var endpoint = MakeValidEndpoint("e1");
        endpoint.ApiKeyMode = ByokApiKeyMode.Stored;
        endpoint.ApiKey = "super-secret-credential-Fa36A6fO";

        var sig = ByokConfigHelper.BuildProviderSignature(ByokConfigHelper.BuildProviderConfig(endpoint));

        Assert.NotNull(sig);
        Assert.DoesNotContain("super-secret-credential-Fa36A6fO", sig);
    }

    // ── Advanced token limits (MaxOutputTokens / MaxPromptTokens / MaxRequestsPerMinute) ──

    [Fact]
    public void BuildProviderConfig_AppliesTokenLimits_WhenModelProvided()
    {
        var endpoint = MakeValidEndpoint("e1");
        var model = new ByokModel
        {
            EndpointId = "e1",
            ModelId = "gpt-4o",
            DisplayName = "GPT-4o",
            MaxOutputTokens = 4096,
            MaxPromptTokens = 128000,
        };

        var pc = ByokConfigHelper.BuildProviderConfig(endpoint, model);

        Assert.Equal(4096, pc.MaxOutputTokens);
        Assert.Equal(128000, pc.MaxPromptTokens);
    }

    [Fact]
    public void BuildProviderConfig_OmitsTokenLimits_WhenNullOrEmpty()
    {
        // null limits pass through (provider default); <=0 limits coerce to null so the SDK
        // never sees an invalid cap.
        var endpoint = MakeValidEndpoint("e1");
        var noLimits = new ByokModel { EndpointId = "e1", ModelId = "gpt-4o", DisplayName = "G" };
        var pcNoLimits = ByokConfigHelper.BuildProviderConfig(endpoint, noLimits);
        Assert.Null(pcNoLimits.MaxOutputTokens);
        Assert.Null(pcNoLimits.MaxPromptTokens);

        // No model at all → null (backward-compat).
        Assert.Null(ByokConfigHelper.BuildProviderConfig(endpoint).MaxOutputTokens);

        var invalid = new ByokModel { EndpointId = "e1", ModelId = "gpt-4o", DisplayName = "G", MaxOutputTokens = 0, MaxPromptTokens = -5 };
        var pcInvalid = ByokConfigHelper.BuildProviderConfig(endpoint, invalid);
        Assert.Null(pcInvalid.MaxOutputTokens);
        Assert.Null(pcInvalid.MaxPromptTokens);
    }

    [Fact]
    public void TryBuildProviderConfig_PassesTokenLimitsFromResolvedModel()
    {
        var settings = new UserSettings();
        var endpoint = MakeValidEndpoint("e1");
        var model = new ByokModel
        {
            Id = "m1",
            EndpointId = "e1",
            ModelId = "gpt-4o",
            DisplayName = "GPT-4o",
            MaxOutputTokens = 2048,
            MaxPromptTokens = 64000,
        };
        settings.ByokEndpoints.Add(endpoint);
        settings.ByokModels.Add(model);

        var pc = ByokConfigHelper.TryBuildProviderConfig(
            settings, ByokConfigHelper.BuildModelToken(model), out _);

        Assert.NotNull(pc);
        Assert.Equal(2048, pc!.MaxOutputTokens);
        Assert.Equal(64000, pc.MaxPromptTokens);
    }

    [Fact]
    public void NormalizeModel_CoercesNonPositiveLimitsToNull_AndPreservesPositive()
    {
        var coerced = new ByokModel
        {
            EndpointId = "e1",
            ModelId = "gpt-4o",
            DisplayName = "G",
            MaxOutputTokens = 0,
            MaxPromptTokens = -1,
            MaxRequestsPerMinute = -10,
        };
        ByokConfigHelper.NormalizeModel(coerced);
        Assert.Null(coerced.MaxOutputTokens);
        Assert.Null(coerced.MaxPromptTokens);
        Assert.Null(coerced.MaxRequestsPerMinute);

        var positive = new ByokModel
        {
            EndpointId = "e1",
            ModelId = "gpt-4o",
            DisplayName = "G",
            MaxOutputTokens = 1,
            MaxPromptTokens = 100,
            MaxRequestsPerMinute = 5,
        };
        ByokConfigHelper.NormalizeModel(positive);
        Assert.Equal(1, positive.MaxOutputTokens);
        Assert.Equal(100, positive.MaxPromptTokens);
        Assert.Equal(5, positive.MaxRequestsPerMinute);
    }

    // ── Helper ──

    private static ByokEndpoint MakeValidEndpoint(string id) => new()
    {
        Id = id,
        Name = "Endpoint " + id,
        ProviderType = "openai",
        BaseUrl = "https://api.example.com/v1",
        WireApi = "completions",
        ApiKeyMode = ByokApiKeyMode.None,
    };
}
