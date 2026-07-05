using System.Text.Json;
using Lumi.Models;
using Xunit;

namespace Lumi.Tests;

public sealed class ByokAdvancedSettingsSerializationTests
{
    // Verifies the advanced BYOK token-limit fields survive JSON round-trip through the
    // AppDataJsonContext source generator (both the "set" and "null/legacy" cases).

    private static ByokModel MakeModel() => new()
    {
        Id = "m1",
        EndpointId = "e1",
        ModelId = "gpt-4o",
        DisplayName = "GPT-4o",
    };

    [Fact]
    public void ByokModel_RoundTripsAdvancedSettings_WhenSet()
    {
        var original = MakeModel();
        original.MaxOutputTokens = 8192;
        original.MaxPromptTokens = 64000;
        original.MaxRequestsPerMinute = 5;

        var json = JsonSerializer.Serialize(original, AppDataJsonContext.Default.ByokModel);
        var deserialized = JsonSerializer.Deserialize(json, AppDataJsonContext.Default.ByokModel);

        Assert.NotNull(deserialized);
        Assert.Equal(8192, deserialized!.MaxOutputTokens);
        Assert.Equal(64000, deserialized.MaxPromptTokens);
        Assert.Equal(5, deserialized.MaxRequestsPerMinute);

        // JSON wire form uses camelCase and emits the values.
        Assert.Contains("\"maxOutputTokens\": 8192", json);
        Assert.Contains("\"maxPromptTokens\": 64000", json);
        Assert.Contains("\"maxRequestsPerMinute\": 5", json);
    }

    [Fact]
    public void ByokModel_RoundTripsNullAdvancedSettings_ForLegacyJson()
    {
        // Legacy JSON without the advanced fields must deserialize with nulls (no crash).
        var json = """{"id":"m1","endpointId":"e1","modelId":"gpt-4o","displayName":"GPT-4o","isEnabled":true}""";
        var deserialized = JsonSerializer.Deserialize(json, AppDataJsonContext.Default.ByokModel);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized!.MaxOutputTokens);
        Assert.Null(deserialized.MaxPromptTokens);
        Assert.Null(deserialized.MaxRequestsPerMinute);

        // A model with default (null) advanced fields serializes nulls as well.
        var withNulls = MakeModel();
        var nullJson = JsonSerializer.Serialize(withNulls, AppDataJsonContext.Default.ByokModel);
        Assert.Contains("\"maxOutputTokens\": null", nullJson);
        Assert.Contains("\"maxPromptTokens\": null", nullJson);
        Assert.Contains("\"maxRequestsPerMinute\": null", nullJson);
    }
}
