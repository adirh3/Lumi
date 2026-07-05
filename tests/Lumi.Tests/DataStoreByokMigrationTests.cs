using System;
using System.Collections.Generic;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class DataStoreByokMigrationTests
{
    // ── MigrateByokAutoInjectedApiKey ──
    //
    // One-time migration that promotes a Foundry endpoint's auto-injected api-key into a
    // dynamic ${apiKey} header placeholder, so the runtime value resolves from ApiKeyMode
    // (stored / env var) without persisting plaintext in Headers.

    [Fact]
    public void Migrate_PromotesApiKeyHeader_ForFoundryEndpoint_WithStoredKey()
    {
        var settings = MakeFoundrySettings("foundry", ByokApiKeyMode.Stored, apiKey: "stored-key-abc");

        var changed = DataStore.MigrateByokAutoInjectedApiKey(settings);

        Assert.True(changed);
        Assert.Equal(ByokConfigHelper.ApiKeyHeaderToken, settings.ByokEndpoints[0].Headers["api-key"]);
    }

    [Fact]
    public void Migrate_IsIdempotent()
    {
        var settings = MakeFoundrySettings("foundry", ByokApiKeyMode.Stored, apiKey: "stored-key-abc");

        Assert.True(DataStore.MigrateByokAutoInjectedApiKey(settings));
        // Second run is a no-op — the dynamic placeholder is already in place.
        Assert.False(DataStore.MigrateByokAutoInjectedApiKey(settings));
        Assert.Equal(ByokConfigHelper.ApiKeyHeaderToken, settings.ByokEndpoints[0].Headers["api-key"]);
    }

    [Fact]
    public void Migrate_DoesNothing_ForNonAzureEndpoint()
    {
        // Plain OpenAI endpoints must NOT get an api-key header — the SDK's bearer token is
        // enough and adding api-key would actually break the request.
        var settings = new UserSettings();
        settings.ByokEndpoints.Add(new ByokEndpoint
        {
            Id = "openai-direct",
            Name = "OpenAI Direct",
            ProviderType = "openai",
            BaseUrl = "https://api.openai.com/v1",
            WireApi = "completions",
            ApiKeyMode = ByokApiKeyMode.Stored,
            ApiKey = "sk-test",
        });

        var changed = DataStore.MigrateByokAutoInjectedApiKey(settings);

        Assert.False(changed);
        Assert.Empty(settings.ByokEndpoints[0].Headers);
    }

    [Fact]
    public void Migrate_DoesNothing_WhenNoKeyResolvable()
    {
        // Foundry endpoint with ApiKeyMode=None: nothing to promote.
        var settings = MakeFoundrySettings("foundry-no-key", ByokApiKeyMode.None);

        var changed = DataStore.MigrateByokAutoInjectedApiKey(settings);

        Assert.False(changed);
        Assert.False(settings.ByokEndpoints[0].Headers.ContainsKey("api-key"));
    }

    [Fact]
    public void Migrate_PreservesUserHeaders_AndDoesNotOverwriteExplicitApiKey()
    {
        // Custom headers survive; an explicit user-set api-key with a DIFFERENT value is preserved.
        var settings = MakeFoundrySettings("foundry-custom", ByokApiKeyMode.Stored, apiKey: "stored-key");
        settings.ByokEndpoints[0].Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Tenant"] = "engineering",
            ["api-key"] = "user-override-key",
        };

        var changed = DataStore.MigrateByokAutoInjectedApiKey(settings);

        Assert.False(changed);
        Assert.Equal("engineering", settings.ByokEndpoints[0].Headers["X-Tenant"]);
        Assert.Equal("user-override-key", settings.ByokEndpoints[0].Headers["api-key"]);
    }

    [Fact]
    public void Migrate_PromotesHeader_ForEnvVarKeyMode()
    {
        var envVar = "LUMI_TEST_BYOK_MIGRATION_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envVar, "env-key-xyz");
        try
        {
            var settings = MakeFoundrySettings("foundry-env", ByokApiKeyMode.EnvVar);
            settings.ByokEndpoints[0].ApiKeyEnvVar = envVar;

            var changed = DataStore.MigrateByokAutoInjectedApiKey(settings);

            Assert.True(changed);
            Assert.Equal(ByokConfigHelper.ApiKeyHeaderToken, settings.ByokEndpoints[0].Headers["api-key"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public void Migrate_DoesNotPromotePlaintext_ForCredentialStoreMode()
    {
        Environment.SetEnvironmentVariable(ByokConfigHelper.FallbackApiKeyEnvVar, null);
        var settings = MakeFoundrySettings(
            "foundry-credential-store",
            ByokApiKeyMode.CredentialStore,
            apiKey: "stale-plaintext-key");

        var changed = DataStore.MigrateByokAutoInjectedApiKey(settings);

        Assert.False(changed);
        Assert.False(settings.ByokEndpoints[0].Headers.ContainsKey("api-key"));
    }

    private static UserSettings MakeFoundrySettings(string id, ByokApiKeyMode mode, string? apiKey = null)
    {
        var settings = new UserSettings();
        settings.ByokEndpoints.Add(new ByokEndpoint
        {
            Id = id,
            Name = id,
            ProviderType = "openai",
            BaseUrl = "https://csd-faip-001-resource.services.ai.azure.com/openai/v1",
            WireApi = "completions",
            ApiKeyMode = mode,
            ApiKey = apiKey ?? "",
        });
        return settings;
    }
}
