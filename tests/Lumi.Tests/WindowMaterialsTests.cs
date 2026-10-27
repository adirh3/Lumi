using Avalonia.Controls;
using Avalonia.Media;
using Lumi.Models;
using Lumi.Services.Appearance;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class WindowMaterialsTests
{
    [Fact]
    public void AutomaticMacMaterialRequestsTheSupportedNativeVibrancyMode()
    {
        Assert.Equal([WindowTransparencyLevel.AcrylicBlur],
            WindowMaterials.HintFor(UserSettings.WindowMaterialAuto, isMacOS: true));
        Assert.Equal([WindowTransparencyLevel.None],
            WindowMaterials.HintFor(UserSettings.WindowMaterialSolid, isMacOS: true));
    }

    [Fact]
    public void OtherPlatformsKeepTheirAutomaticMaterialFallbacks()
    {
        Assert.Equal(
            [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Mica, WindowTransparencyLevel.Blur],
            WindowMaterials.HintFor(UserSettings.WindowMaterialAuto, isMacOS: false));
        Assert.Equal([WindowTransparencyLevel.None],
            WindowMaterials.HintFor(UserSettings.WindowMaterialSolid, isMacOS: false));
    }

    public static TheoryData<WindowTransparencyLevel, bool, double> FallbackCases => new()
    {
        { WindowTransparencyLevel.None, false, 0.88 },
        { WindowTransparencyLevel.None, true, 0.88 },
        { WindowTransparencyLevel.Mica, false, 0.62 },
        { WindowTransparencyLevel.AcrylicBlur, false, 0.8 },
        { WindowTransparencyLevel.AcrylicBlur, true, 0.62 },
        { WindowTransparencyLevel.Blur, false, 0.8 },
    };

    [Theory]
    [MemberData(nameof(FallbackCases))]
    public void FallbackOpacityRespectsTheActualMaterialAndPlatform(
        WindowTransparencyLevel level, bool isMacOS, double expected)
        => Assert.Equal(expected, WindowMaterials.FallbackOpacityFor(level, isMacOS));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothWindowTypesUseTheMaterialPolicyAtStartupAndAfterChangingIt(bool detached)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Window window = detached ? new ChatWindow() : new MainWindow();
            try
            {
                Assert.Equal(WindowMaterials.HintFor(UserSettings.WindowMaterialAuto), window.TransparencyLevelHint);

                WindowMaterials.Apply(window, UserSettings.WindowMaterialSolid);
                Assert.Equal([WindowTransparencyLevel.None], window.TransparencyLevelHint);

                WindowMaterials.Apply(window, UserSettings.WindowMaterialAuto);
                Assert.Equal(WindowMaterials.HintFor(UserSettings.WindowMaterialAuto), window.TransparencyLevelHint);
                Assert.Equal(Brushes.Transparent, window.Background);
            }
            finally
            {
                window.Close();
                window.Content = null;
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyingTheCurrentMaterialDoesNotReapplyNativeTransparency(bool detached)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Window window = detached ? new ChatWindow() : new MainWindow();
            try
            {
                var initialHints = window.TransparencyLevelHint;
                WindowMaterials.Apply(window, UserSettings.WindowMaterialAuto);
                Assert.Same(initialHints, window.TransparencyLevelHint);

                WindowMaterials.Apply(window, UserSettings.WindowMaterialSolid);
                var solidHints = window.TransparencyLevelHint;
                WindowMaterials.Apply(window, UserSettings.WindowMaterialSolid);
                Assert.Same(solidHints, window.TransparencyLevelHint);

                WindowMaterials.Apply(window, UserSettings.WindowMaterialAuto);
                var autoHints = window.TransparencyLevelHint;
                WindowMaterials.Apply(window, UserSettings.WindowMaterialAuto);
                Assert.Same(autoHints, window.TransparencyLevelHint);
            }
            finally
            {
                window.Close();
                window.Content = null;
            }
        }, CancellationToken.None);
    }
}
