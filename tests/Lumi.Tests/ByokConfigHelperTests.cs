using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class ByokConfigHelperTests
{
    private static UserSettings ValidByokSettings() => new()
    {
        IsByokEnabled = true,
        ByokProviderType = "openai",
        ByokBaseUrl = "https://api.openai.com/v1",
        ByokModelId = "gpt-4o",
    };

    // ── IsByokConfigured ──

    [Fact]
    public void IsByokConfigured_ReturnsTrue_WhenAllRequiredFieldsSet()
    {
        Assert.True(ByokConfigHelper.IsByokConfigured(ValidByokSettings()));
    }

    [Fact]
    public void IsByokConfigured_ReturnsFalse_WhenDisabled()
    {
        var settings = ValidByokSettings();
        settings.IsByokEnabled = false;
        Assert.False(ByokConfigHelper.IsByokConfigured(settings));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsByokConfigured_ReturnsFalse_WhenProviderTypeMissing(string? value)
    {
        var settings = ValidByokSettings();
        settings.ByokProviderType = value;
        Assert.False(ByokConfigHelper.IsByokConfigured(settings));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsByokConfigured_ReturnsFalse_WhenBaseUrlMissing(string? value)
    {
        var settings = ValidByokSettings();
        settings.ByokBaseUrl = value;
        Assert.False(ByokConfigHelper.IsByokConfigured(settings));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsByokConfigured_ReturnsFalse_WhenModelIdMissing(string? value)
    {
        var settings = ValidByokSettings();
        settings.ByokModelId = value;
        Assert.False(ByokConfigHelper.IsByokConfigured(settings));
    }

    [Fact]
    public void IsByokConfigured_AcceptsTrimmedValues()
    {
        var settings = ValidByokSettings();
        settings.ByokProviderType = "  openai  ";
        settings.ByokBaseUrl = "  https://api.openai.com/v1  ";
        settings.ByokModelId = "  gpt-4o  ";
        Assert.True(ByokConfigHelper.IsByokConfigured(settings));
    }

    [Fact]
    public void IsByokConfigured_AcceptsHttpBaseUrl_ForLocalProviders()
    {
        var settings = ValidByokSettings();
        settings.ByokBaseUrl = "http://localhost:11434/v1";
        Assert.True(ByokConfigHelper.IsByokConfigured(settings));
    }

    // ── ValidateBaseUrl ──

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("https://my-resource.openai.azure.com/openai/deployments/gpt-4o/chat/completions?api-version=2024-02-01")]
    public void ValidateBaseUrl_ReturnsNull_ForValidUrls(string url)
    {
        Assert.Null(ByokConfigHelper.ValidateBaseUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateBaseUrl_ReturnsError_ForMissingUrl(string? url)
    {
        Assert.NotNull(ByokConfigHelper.ValidateBaseUrl(url));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("api.openai.com/v1")]
    [InlineData("//api.openai.com/v1")]
    public void ValidateBaseUrl_ReturnsError_ForMalformedUrl(string url)
    {
        Assert.NotNull(ByokConfigHelper.ValidateBaseUrl(url));
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("file:///etc/passwd")]
    public void ValidateBaseUrl_ReturnsError_ForUnsupportedScheme(string url)
    {
        Assert.NotNull(ByokConfigHelper.ValidateBaseUrl(url));
    }

    // ── GetByokModelId / IsByokModel / StripByokPrefix ──

    [Fact]
    public void GetByokModelId_ReturnsPrefixedId()
    {
        var settings = new UserSettings { ByokModelId = "gpt-4o" };
        Assert.Equal("byok:gpt-4o", ByokConfigHelper.GetByokModelId(settings));
    }

    [Theory]
    [InlineData("byok:gpt-4o", true)]
    [InlineData("BYOK:gpt-4o", false)]
    [InlineData("gpt-4o", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsByokModel_ReturnsCorrectResult(string? modelId, bool expected)
    {
        Assert.Equal(expected, ByokConfigHelper.IsByokModel(modelId));
    }

    [Theory]
    [InlineData("byok:gpt-4o", "gpt-4o")]
    [InlineData("byok:claude-3-opus-20240229", "claude-3-opus-20240229")]
    [InlineData("gpt-4o", null)]
    [InlineData(null, null)]
    public void StripByokPrefix_ReturnsCorrectResult(string? modelId, string? expected)
    {
        Assert.Equal(expected, ByokConfigHelper.StripByokPrefix(modelId));
    }

    // ── BuildProviderConfig ──

    [Fact]
    public void BuildProviderConfig_ReturnsNull_WhenNotConfigured()
    {
        var settings = new UserSettings { IsByokEnabled = false };
        Assert.Null(ByokConfigHelper.BuildProviderConfig(settings));
    }

    [Fact]
    public void BuildProviderConfig_UsesSettingsApiKey_WhenPresent()
    {
        var settings = ValidByokSettings();
        settings.ByokApiKey = "sk-test-key";
        var config = ByokConfigHelper.BuildProviderConfig(settings);
        Assert.NotNull(config);
        Assert.Equal("sk-test-key", config.ApiKey);
    }

    [Fact]
    public void BuildProviderConfig_FallsBackToEnvVar_WhenSettingsKeyBlank()
    {
        const string envVar = "LUMI_BYOK_API_KEY";
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "env-key-123");
            var settings = ValidByokSettings();
            settings.ByokApiKey = null;
            var config = ByokConfigHelper.BuildProviderConfig(settings);
            Assert.NotNull(config);
            Assert.Equal("env-key-123", config.ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, original);
        }
    }

    [Fact]
    public void BuildProviderConfig_SettingsApiKey_TakesPrecedenceOverEnvVar()
    {
        const string envVar = "LUMI_BYOK_API_KEY";
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "env-key");
            var settings = ValidByokSettings();
            settings.ByokApiKey = "settings-key";
            var config = ByokConfigHelper.BuildProviderConfig(settings);
            Assert.NotNull(config);
            Assert.Equal("settings-key", config.ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, original);
        }
    }

    [Fact]
    public void BuildProviderConfig_NoApiKey_ForLocalProviders()
    {
        const string envVar = "LUMI_BYOK_API_KEY";
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, null);
            var settings = ValidByokSettings();
            settings.ByokBaseUrl = "http://localhost:11434/v1";
            settings.ByokApiKey = null;
            var config = ByokConfigHelper.BuildProviderConfig(settings);
            Assert.NotNull(config);
            Assert.True(string.IsNullOrEmpty(config.ApiKey));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, original);
        }
    }

    [Fact]
    public void BuildProviderConfig_SetsAzureOptions_ForAzureProvider()
    {
        var settings = ValidByokSettings();
        settings.ByokProviderType = "azure";
        settings.ByokAzureApiVersion = "2024-02-01";
        var config = ByokConfigHelper.BuildProviderConfig(settings);
        Assert.NotNull(config);
        Assert.NotNull(config.Azure);
        Assert.Equal("2024-02-01", config.Azure.ApiVersion);
    }

    [Fact]
    public void BuildProviderConfig_NoAzureOptions_ForNonAzureProvider()
    {
        var settings = ValidByokSettings();
        settings.ByokProviderType = "openai";
        settings.ByokAzureApiVersion = "2024-02-01";
        var config = ByokConfigHelper.BuildProviderConfig(settings);
        Assert.NotNull(config);
        Assert.Null(config.Azure);
    }

    [Fact]
    public void BuildProviderConfig_SetsWireApi_WhenSpecified()
    {
        var settings = ValidByokSettings();
        settings.ByokWireApi = "responses";
        var config = ByokConfigHelper.BuildProviderConfig(settings);
        Assert.NotNull(config);
        Assert.Equal("responses", config.WireApi);
    }

    [Fact]
    public void BuildProviderConfig_DefaultsWireApi_WhenBlank()
    {
        var settings = ValidByokSettings();
        settings.ByokWireApi = null;
        var config = ByokConfigHelper.BuildProviderConfig(settings);
        Assert.NotNull(config);
        Assert.Null(config.WireApi);
    }

    // ── TryBuildForSession ──

    [Fact]
    public void TryBuildForSession_ReturnsNull_ForNonByokModel()
    {
        var settings = ValidByokSettings();
        var result = ByokConfigHelper.TryBuildForSession(settings, "gpt-4o", out var actualModelId);
        Assert.Null(result);
        Assert.Equal("gpt-4o", actualModelId);
    }

    [Fact]
    public void TryBuildForSession_ReturnsProvider_ForByokModel()
    {
        var settings = ValidByokSettings();
        var result = ByokConfigHelper.TryBuildForSession(settings, "byok:gpt-4o", out var actualModelId);
        Assert.NotNull(result);
        Assert.Equal("gpt-4o", actualModelId);
        Assert.Equal("openai", result.Type);
    }

    [Fact]
    public void TryBuildForSession_ReturnsNull_WhenByokNotConfigured()
    {
        var settings = new UserSettings { IsByokEnabled = false };
        var result = ByokConfigHelper.TryBuildForSession(settings, "byok:gpt-4o", out var actualModelId);
        Assert.Null(result);
        Assert.Equal("byok:gpt-4o", actualModelId);
    }

    // ── GetByokModelDisplayName ──

    [Fact]
    public void GetByokModelDisplayName_IncludesProviderAndModel()
    {
        var settings = new UserSettings { ByokModelId = "gpt-4o", ByokProviderType = "openai" };
        var displayName = ByokConfigHelper.GetByokModelDisplayName(settings);
        Assert.Contains("gpt-4o", displayName);
        Assert.Contains("OPENAI", displayName);
        Assert.Contains("BYOK", displayName);
    }

    [Fact]
    public void GetByokModelDisplayName_HandlesNullFields()
    {
        var settings = new UserSettings { ByokModelId = null, ByokProviderType = null };
        var displayName = ByokConfigHelper.GetByokModelDisplayName(settings);
        Assert.NotEmpty(displayName);
    }
}
