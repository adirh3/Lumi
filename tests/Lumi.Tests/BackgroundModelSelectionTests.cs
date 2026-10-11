using System.Globalization;
using GitHub.Copilot;
using Lumi.Converters;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class BackgroundModelSelectionTests
{
    private static ModelInfo M(string id, double? multiplier = null, string? policy = null) => new()
    {
        Id = id,
        Billing = multiplier is { } cost ? new ModelBilling { Multiplier = cost } : null,
        Policy = policy is not null ? new ModelPolicy { State = policy } : null
    };

    private static ModelInfo T(string id, long? batchSize, double? input, double? output, double? multiplier = null)
        => new()
        {
            Id = id,
            Billing = new ModelBilling
            {
                Multiplier = multiplier,
                TokenPrices = new() { BatchSize = batchSize, InputPrice = input, OutputPrice = output }
            }
        };

    [Fact]
    public void TokenPricesTakePriorityOverLegacyMultipliersAndModelNames()
    {
        var result = CopilotService.SelectCheapestModelId(
        [
            T("gpt-6-luna", 1000, 0.1, 0.3, multiplier: 0.25),
            T("model-cheaper-in-credits", 1000, 0.05, 0.1, multiplier: 1),
            M("legacy-price-only", 0)
        ]);

        Assert.Equal("model-cheaper-in-credits", result);
    }

    [Fact]
    public void TokenBillingBatchSizesAreNormalizedBeforeComparingCosts()
    {
        var result = CopilotService.SelectCheapestModelId(
        [
            T("small-batch", 1000, 0.1, 0.3),
            T("large-batch", 10000, 0.2, 0.6)
        ]);

        Assert.Equal("large-batch", result);
        Assert.Equal(0.08, CopilotService.GetModelTokenCost(T("large-batch", 10000, 0.2, 0.6))!.Value, 8);
    }

    [Fact]
    public void EqualTokenCostsPreferNewestLuna()
    {
        Assert.Equal("gpt-6-luna", CopilotService.SelectCheapestModelId(
        [
            T("gpt-5.6-luna", 1000, 0.1, 0.3),
            T("gpt-6-luna", 1000, 0.1, 0.3),
            T("gpt-6.1-sol", 1000, 0.2, 0.6)
        ]));
    }

    [Fact]
    public void InvalidOrIncompleteTokenPricesAreUnknownAndLegacyCatalogsStillWork()
    {
        Assert.Null(CopilotService.GetModelTokenCost(T("missing-batch", null, 0.1, 0.3)));
        Assert.Null(CopilotService.GetModelTokenCost(T("zero-batch", 0, 0.1, 0.3)));
        Assert.Null(CopilotService.GetModelTokenCost(T("missing-output", 1000, 0.1, null)));
        Assert.Null(CopilotService.GetModelTokenCost(T("invalid-input", 1000, double.NaN, 0.3)));
        Assert.Equal("legacy-cheapest", CopilotService.SelectCheapestModelId(
        [
            T("missing-batch", null, 0.1, 0.3, multiplier: 1),
            M("legacy-cheapest", 0.25)
        ]));
    }

    [Fact]
    public void CostTakesPriorityOverTheOldFastModelPreferenceAndNewerLuna()
    {
        var result = CopilotService.SelectCheapestModelId(
        [
            M("claude-opus-4.8", 10),
            M("gpt-5-mini", 0),
            M("gpt-5.4-mini", 1),
            M("gpt-6-luna", 0.25)
        ]);

        Assert.Equal("gpt-5-mini", result);
    }

    [Fact]
    public void EqualCostsPreferTheNewestLunaRatherThanCatalogOrder()
    {
        var result = CopilotService.SelectCheapestModelId(
        [
            M("gemini-3.5-flash", 0),
            M("gpt-5.6-luna", 0),
            M("gpt-6-luna", 0),
            M("gpt-5.4-mini", 0),
            M("gpt-6.1-sol", 1)
        ]);

        Assert.Equal("gpt-6-luna", result);
    }

    [Fact]
    public void LunaVersionsAreComparedNumericallyAndCanonicalCasingIsPreserved()
    {
        var result = CopilotService.SelectCheapestModelId(
        [
            M("gpt-6.9-luna", 0.25),
            M("GPT-6.10-Luna", 0.25),
            M("gpt-6-luna", 0.25)
        ]);

        Assert.Equal("GPT-6.10-Luna", result);
    }

    [Fact]
    public void PaidModelsAreRankedByActualMultiplier()
    {
        var result = CopilotService.SelectCheapestModelId(
        [
            M("gpt-6-luna", 1),
            M("model-cheap", 0.1),
            M("claude-haiku-4.5", 0.33)
        ]);

        Assert.Equal("model-cheap", result);
    }

    [Fact]
    public void UnknownCostsDoNotBeatReportedCosts()
    {
        var result = CopilotService.SelectCheapestModelId(
        [
            M("gpt-7-luna"),
            M("model-with-reported-cost", 1)
        ]);

        Assert.Equal("model-with-reported-cost", result);
    }

    [Fact]
    public void InvalidCostsAreTreatedAsUnknown()
    {
        var result = CopilotService.SelectCheapestModelId(
        [
            M("negative", -1),
            M("not-a-number", double.NaN),
            M("infinite", double.PositiveInfinity),
            M("known", 0.25)
        ]);

        Assert.Equal("known", result);
    }

    [Fact]
    public void AutoExcludesBlankByokAndNotYetEnabledModels()
    {
        var result = CopilotService.SelectCheapestModelId(
        [
            M("", 0),
            M("auto", 0),
            M("byok:private", 0),
            M("gpt-7-luna", 0, "unconfigured"),
            M("disabled", 0, "disabled"),
            M("gpt-6-luna", 0.25, " ENABLED ")
        ]);

        Assert.Equal("gpt-6-luna", result);
    }

    [Fact]
    public void MissingBillingPrefersNewestLunaThenLightweightNames()
    {
        Assert.Equal("gpt-6-luna", CopilotService.SelectCheapestModelId(
            [M("gpt-5-mini"), M("gpt-5.6-luna"), M("gpt-6-luna")]));
        Assert.Equal("some-flash-model", CopilotService.SelectCheapestModelId(
            [M("big-model"), M("some-flash-model")]));
    }

    [Fact]
    public void FinalTieBreakerIsStableAcrossCatalogReordering()
    {
        var first = CopilotService.SelectCheapestModelId([M("model-b", 1), M("model-a", 1)]);
        var reordered = CopilotService.SelectCheapestModelId([M("model-a", 1), M("model-b", 1)]);

        Assert.Equal("model-a", first);
        Assert.Equal(first, reordered);
    }

    [Fact]
    public void EmptyOrEntirelyUnavailableCatalogHasNoAutomaticSelection()
    {
        Assert.Null(CopilotService.SelectCheapestModelId([]));
        Assert.Null(CopilotService.SelectCheapestModelId([M("gpt-6-luna", 0, "disabled")]));
    }

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData("  Auto ", "auto")]
    [InlineData(" gpt-6-luna ", "gpt-6-luna")]
    [InlineData("byok:my-model", "byok:my-model")]
    public void SelectionNormalizationKeepsAutoAndExplicitChoices(string? value, string expected)
        => Assert.Equal(expected, CopilotService.NormalizeBackgroundModel(value));

    [Fact]
    public void AutoDisplayIsLocalized()
    {
        Assert.Equal(Loc.Setting_BackgroundModelAuto,
            ModelDisplayConverter.Instance.Convert(
                UserSettings.AutomaticBackgroundModel, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task BackgroundResolutionAndConfigPreparationPropagateCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TestCopilot.Shared.GetBackgroundModelIdAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TestCopilot.Shared.BuildLightweightSessionConfigAsync(
                new LightweightSessionOptions { SystemPrompt = "Cancelled fixture." }, cts.Token));
    }

    [Fact]
    public void ReusableHelperSignatureChangesWithModelProviderAndCapabilities()
    {
        var config = new SessionConfig { Model = "gpt-6-luna" };
        var original = CopilotService.GetLightweightSessionModelSignature(config);
        config.Model = "gpt-6-sol";
        Assert.NotEqual(original, CopilotService.GetLightweightSessionModelSignature(config));
        config.Model = "gpt-6-luna";
        Assert.Equal(original, CopilotService.GetLightweightSessionModelSignature(config));
        config.Provider = new ProviderConfig { BaseUrl = "https://example.com/v1" };
        var withProvider = CopilotService.GetLightweightSessionModelSignature(config);
        Assert.NotEqual(original, withProvider);
        config.ModelCapabilities = new()
        {
            Limits = new() { MaxContextWindowTokens = 128_000 }
        };
        Assert.NotEqual(withProvider, CopilotService.GetLightweightSessionModelSignature(config));
    }
}
