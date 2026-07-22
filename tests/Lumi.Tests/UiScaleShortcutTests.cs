using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class UiScaleShortcutTests
{
    [Fact]
    public async Task CtrlPlusAndMinus_AdjustScaleWhileScaleSliderIsFocused()
    {
        using var session = HeadlessTestSession.Start();

        bool sliderFocused = false;
        bool scaleControlBelowDescription = false;
        int scaleAfterPlus = 0;
        int scaleAfterMinus = 0;

        await session.Dispatch(async () =>
        {
            Loc.Load("en");

            var data = new AppData
            {
                Settings = new UserSettings
                {
                    IsOnboarded = true,
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false,
                    UiScalePercent = 100,
                }
            };
            var viewModel = new MainViewModel(
                new DataStore(data),
                new CopilotService(),
                new UpdateService(),
                startBackgroundJobs: false);
            var window = new MainWindow
            {
                DataContext = viewModel,
                Width = 1100,
                Height = 820,
            };

            window.Show();
            try
            {
                viewModel.SelectedNavIndex = 7;
                viewModel.SettingsVM.SelectedPageIndex = 2;
                await PumpAsync();

                var slider = window.GetVisualDescendants()
                    .OfType<Slider>()
                    .Single(control => control.Maximum == UiScaleService.MaximumLevelIndex);
                slider.Focus();
                await PumpAsync();
                sliderFocused = slider.IsFocused;

                PressKey(
                    window,
                    PhysicalKey.Equal,
                    RawInputModifiers.Control | RawInputModifiers.Shift);
                await PumpAsync();
                scaleAfterPlus = viewModel.SettingsVM.UiScalePercent;

                PressKey(window, PhysicalKey.Minus, RawInputModifiers.Control);
                await PumpAsync();
                scaleAfterMinus = viewModel.SettingsVM.UiScalePercent;

                viewModel.SettingsVM.UiScalePercent = 125;
                await PumpAsync();

                var scaleSetting = window.GetVisualDescendants()
                    .OfType<StrataSetting>()
                    .Single(control => control.Header == Loc.Setting_FontSize);
                var description = scaleSetting.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Single(control => control.Name == "PART_Description");
                var descriptionBottom = description.TranslatePoint(
                    new Point(0, description.Bounds.Height),
                    window);
                var sliderTop = slider.TranslatePoint(new Point(0, 0), window);
                scaleControlBelowDescription =
                    descriptionBottom is { } bottom
                    && sliderTop is { } top
                    && top.Y >= bottom.Y;
            }
            finally
            {
                window.Close();
                viewModel.Dispose();
            }
        }, CancellationToken.None);

        Assert.True(sliderFocused);
        Assert.Equal(110, scaleAfterPlus);
        Assert.Equal(100, scaleAfterMinus);
        Assert.True(scaleControlBelowDescription);
    }

    [Fact]
    public async Task BrowserNativeLayoutUsesTheTransformedUiScale()
    {
        using var session = HeadlessTestSession.Start();
        var results = new (double ExpectedScale, double RasterizationScale, double WidthScale)[2];

        await session.Dispatch(async () =>
        {
            var scales = new[] { 1.25, 5.0 };
            for (var i = 0; i < scales.Length; i++)
            {
                var scale = scales[i];
                var browser = new BrowserView
                {
                    Width = 400,
                    Height = 300,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                };
                var scaledRoot = new LayoutTransformControl
                {
                    LayoutTransform = new ScaleTransform(scale, scale),
                    Child = browser,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                };
                var window = new Window
                {
                    Width = 2400,
                    Height = 1800,
                    Content = scaledRoot,
                };

                window.Show();
                try
                {
                    await PumpAsync();
                    var topLevel = TopLevel.GetTopLevel(browser);
                    Assert.NotNull(topLevel);

                    var layout = browser.CalculateNativeWebViewLayout(topLevel!);
                    Assert.NotNull(layout);

                    results[i] = (
                        scale,
                        layout.Value.RasterizationScale / topLevel!.RenderScaling,
                        layout.Value.Width / (browser.Bounds.Width * topLevel.RenderScaling));
                }
                finally
                {
                    window.Close();
                }
            }
        }, CancellationToken.None);

        foreach (var result in results)
        {
            Assert.Equal(result.ExpectedScale, result.RasterizationScale, 3);
            Assert.Equal(result.ExpectedScale, result.WidthScale, 2);
        }
    }

    private static void PressKey(Window window, PhysicalKey key, RawInputModifiers modifiers)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
