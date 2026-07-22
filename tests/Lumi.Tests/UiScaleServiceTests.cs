using System.Text.Json;
using Avalonia.Input;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class UiScaleServiceTests
{
    [Fact]
    public void BrowserScaleLevels_MatchChromiumZoomSteps()
    {
        var expectedLevels = new[]
        {
            25, 33, 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400, 500
        };

        var actualLevels = Enumerable.Range(0, UiScaleService.LevelCount)
            .Select(UiScaleService.GetScalePercentAtLevelIndex);

        Assert.Equal(expectedLevels, actualLevels);
    }

    [Fact]
    public void GetScale_UsesPercentageAndSupportsFiveHundredPercent()
    {
        Assert.Equal(1, UiScaleService.GetScale(UiScaleService.DefaultScalePercent), 6);
        Assert.Equal(5, UiScaleService.GetScale(UiScaleService.MaximumScalePercent), 6);
    }

    [Theory]
    [InlineData(14, 100)]
    [InlineData(16, 110)]
    [InlineData(18, 125)]
    public void MigrateLegacyFontSize_UsesNearestBrowserLevel(int fontSize, int expectedScalePercent)
    {
        Assert.Equal(expectedScalePercent, UiScaleService.MigrateLegacyFontSize(fontSize));
    }

    [Fact]
    public void AdjustScalePercent_StepsAndClampsAtBrowserLimits()
    {
        Assert.Equal(110, UiScaleService.AdjustScalePercent(100, 1));
        Assert.Equal(90, UiScaleService.AdjustScalePercent(100, -1));
        Assert.Equal(500, UiScaleService.AdjustScalePercent(500, 1));
        Assert.Equal(25, UiScaleService.AdjustScalePercent(25, -1));
    }

    [Fact]
    public void LegacyFontSizeJson_MigratesToTheNewScaleField()
    {
        var settings = JsonSerializer.Deserialize(
            """{"fontSize":16}""",
            AppDataJsonContext.Default.UserSettings);

        Assert.NotNull(settings);
        Assert.Equal(16, settings.LegacyFontSize);

        settings.UiScalePercent = UiScaleService.MigrateLegacyFontSize(settings.LegacyFontSize);
        settings.LegacyFontSize = 0;
        var migratedJson = JsonSerializer.Serialize(settings, AppDataJsonContext.Default.UserSettings);

        Assert.Contains("\"uiScalePercent\": 110", migratedJson);
        Assert.DoesNotContain("\"fontSize\"", migratedJson);
    }

    [Fact]
    public void GetShortcutDelta_HandlesMainAndKeypadZoomKeys()
    {
        var primaryModifier = OperatingSystem.IsMacOS()
            ? KeyModifiers.Meta
            : KeyModifiers.Control;

        Assert.Equal(1, UiScaleService.GetShortcutDelta(Key.OemPlus, primaryModifier));
        Assert.Equal(1, UiScaleService.GetShortcutDelta(Key.Add, primaryModifier | KeyModifiers.Shift));
        Assert.Equal(-1, UiScaleService.GetShortcutDelta(Key.OemMinus, primaryModifier));
        Assert.Equal(-1, UiScaleService.GetShortcutDelta(Key.Subtract, primaryModifier));
        Assert.Equal(0, UiScaleService.GetShortcutDelta(Key.OemPlus, primaryModifier | KeyModifiers.Alt));
        Assert.Equal(0, UiScaleService.GetShortcutDelta(Key.OemPlus, KeyModifiers.None));
    }
}
