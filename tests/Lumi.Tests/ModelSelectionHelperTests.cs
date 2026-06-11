using System;
using System.Collections.Generic;
using System.Reflection;
using Lumi.Models;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public class ModelSelectionHelperTests
{
    [Fact]
    public void NormalizeEffort_PrefersHighWhenNoEffortIsStored()
    {
        var helperType = typeof(Chat).Assembly.GetType("Lumi.ViewModels.ModelSelectionHelper")
            ?? throw new InvalidOperationException("ModelSelectionHelper type was not found.");
        var normalizeMethod = helperType.GetMethod(
            "NormalizeEffort",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("NormalizeEffort method was not found.");

        var reasoningEfforts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.4"] = ["low", "medium", "high"]
        };
        var defaultEfforts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.4"] = "medium"
        };

        var result = (string?)normalizeMethod.Invoke(null, [null, "gpt-5.4", reasoningEfforts, defaultEfforts]);

        Assert.Equal("high", result);
    }

    [Fact]
    public void GetContextWindowTiers_ReturnsChoicesForDetectedModelsOnly()
    {
        var longContextModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gpt-5.4",
            "claude-sonnet-4.6"
        };

        var gptTiers = ModelSelectionHelper.GetContextWindowTiers("gpt-5.4", longContextModelIds);
        var claudeTiers = ModelSelectionHelper.GetContextWindowTiers("CLAUDE-SONNET-4.6", longContextModelIds);
        var unsupportedTiers = ModelSelectionHelper.GetContextWindowTiers("gpt-5.5", longContextModelIds);

        Assert.Equal(["Default", "Long"], gptTiers);
        Assert.Equal(["Default", "Long"], claudeTiers);
        Assert.Null(unsupportedTiers);
    }

    [Fact]
    public void NormalizeContextWindowTier_MapsDisplayToSdkValue()
    {
        var longContextModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gpt-5.4"
        };

        var longTier = ModelSelectionHelper.NormalizeContextWindowTier("Long", "gpt-5.4", longContextModelIds);
        var defaultTier = ModelSelectionHelper.NormalizeContextWindowTier("Default", "gpt-5.4", longContextModelIds);
        var unsupportedTier = ModelSelectionHelper.NormalizeContextWindowTier("Long", "claude-sonnet-4.6", longContextModelIds);

        Assert.Equal(ModelContextWindowTiers.LongContext, longTier);
        Assert.Equal(ModelContextWindowTiers.Default, defaultTier);
        Assert.Null(unsupportedTier);
    }
}
