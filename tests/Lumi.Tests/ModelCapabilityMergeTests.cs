using System;
using System.Collections.Generic;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// The composer's reasoning-effort picker is driven by per-model capabilities fetched from the SDK.
/// </summary>
public class ModelCapabilityMergeTests
{
    private static ChatViewModel NewSurface() =>
        new(new DataStore(new AppData { Settings = new UserSettings { PreferredModel = "claude-opus-5" } }),
            TestCopilot.Shared);

    private static List<ModelInfo> RealCatalog() =>
    [
        new() { Id = "auto" },
        new()
        {
            Id = "claude-opus-5",
            SupportedReasoningEfforts = ["low", "medium", "high", "xhigh", "max"],
            DefaultReasoningEffort = "medium"
        }
    ];

    // The BYOK refresh describes ONLY its own tokens, but UpdateModelCapabilities builds fresh
    // dictionaries and swaps them in. Calling it from that path therefore threw away every
    // SDK-provided reasoning effort for the real catalog -- and with no BYOK models configured it
    // passed an empty list and wiped the map outright, which is why the composer showed no effort
    // picker at all on either the desktop or the phone.
    [Fact]
    public void ABYOKRefresh_DoesNotEraseTheSdkCatalogsReasoningEfforts()
    {
        var surface = NewSurface();
        surface.UpdateModelCapabilities(RealCatalog());
        surface.SelectedModel = "claude-opus-5";

        Assert.NotNull(surface.QualityLevels);

        // Exactly what MainViewModel does when BYOK tokens are refreshed and none are configured.
        surface.UpdateModelCapabilities([], longContextModelIds: null, contextWindowLimits: null, merge: true);

        Assert.NotNull(surface.QualityLevels);
        var levels = surface.GetQualityLevelsFor("claude-opus-5");
        Assert.NotNull(levels);
        Assert.Equal(
            ["Low", "Medium", "High", "Xhigh", "Max"],
            levels);
    }

    [Fact]
    public void ABYOKRefresh_StillAddsItsOwnModels()
    {
        var surface = NewSurface();
        surface.UpdateModelCapabilities(RealCatalog());

        surface.UpdateModelCapabilities(
            [new ModelInfo { Id = "my-byok", SupportedReasoningEfforts = ["low", "high"] }],
            longContextModelIds: null,
            contextWindowLimits: null,
            merge: true);

        var byokLevels = surface.GetQualityLevelsFor("my-byok");
        var copilotLevels = surface.GetQualityLevelsFor("claude-opus-5");
        Assert.NotNull(byokLevels);
        Assert.NotNull(copilotLevels);
        Assert.Equal(["Low", "High"], byokLevels);
        Assert.Equal(
            ["Low", "Medium", "High", "Xhigh", "Max"],
            copilotLevels);
    }

    // A real catalog refresh must still REPLACE, so a model dropped from the account's entitlements
    // stops advertising efforts instead of lingering forever.
    [Fact]
    public void AFullCatalogRefresh_StillReplaces()
    {
        var surface = NewSurface();
        surface.UpdateModelCapabilities(RealCatalog());
        Assert.NotEmpty(surface.GetQualityLevelsFor("claude-opus-5") ?? []);

        surface.UpdateModelCapabilities([new ModelInfo { Id = "auto" }]);

        Assert.True(
            (surface.GetQualityLevelsFor("claude-opus-5") ?? []).Length == 0,
            "a model absent from the refreshed catalog must stop advertising efforts");
    }
}
