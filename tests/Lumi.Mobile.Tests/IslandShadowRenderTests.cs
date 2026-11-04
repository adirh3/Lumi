using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Lumi.Mobile.Layout;
using Xunit;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class IslandShadowRenderTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task MaskShadowPreservesTheExistingIslandElevation(bool dark, int scale)
    {
        using var session = HeadlessUnitTestSession.StartNew(
            typeof(MobileMotionRenderApp), AvaloniaTestIsolationLevel.PerTest);
        await session.Dispatch(() =>
        {
            var shadow = BoxShadows.Parse(dark
                ? "0 1 2 0 #40000000, 0 12 32 -12 #99000000"
                : "0 1 2 0 #0F1E1E3C, 0 10 30 -10 #2E1E1E3C");
            var background = new SolidColorBrush(Color.Parse(dark ? "#18181B" : "#F5F5F7"));
            var fill = new SolidColorBrush(Color.Parse(dark ? "#242428" : "#FAFAFB"));
            var stage = new Canvas { Background = background };
            var window = new Window
            {
                Width = 320, Height = 300, Content = stage,
                RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
            };
            try
            {
                window.Show();
                foreach (var opacity in new[] { 1d, 0.6d })
                {
                    var reference = new Border
                    {
                        Width = 192, Height = 160, CornerRadius = new CornerRadius(24),
                        Background = fill, BoxShadow = shadow, ClipToBounds = false, Opacity = opacity
                    };
                    var actual = new Island
                    {
                        Width = 192, Height = 160, Frame = 1, BoxShadow = shadow, Opacity = opacity
                    };
                    // The old image-filter shadow distorts at high bitmap DPI; its 1x appearance is the reference.
                    var expected = Capture(reference, 1);
                    var rendered = Capture(actual, scale);
                    double totalDifference = 0;
                    var maximumDifference = 0;
                    var channels = 0;
                    for (var y = 0; y < 300; y++)
                    for (var x = 0; x < 320; x++)
                    {
                        // Compare the shadow outside the pane, not the rim inside its fill.
                        if (x >= 64 && x < 256 && y >= 48 && y < 208)
                            continue;
                        var offset = (y * 320 + x) * 4;
                        for (var channel = 0; channel < 3; channel++)
                        {
                            var actualChannel = 0;
                            for (var dy = 0; dy < scale; dy++)
                            for (var dx = 0; dx < scale; dx++)
                                actualChannel += rendered[
                                    ((y * scale + dy) * 320 * scale + x * scale + dx) * 4 + channel];
                            var difference = Math.Abs(expected[offset + channel]
                                - actualChannel / (double)(scale * scale));
                            totalDifference += difference;
                            maximumDifference = Math.Max(maximumDifference, (int)Math.Ceiling(difference));
                            channels++;
                        }
                    }
                    var mean = totalDifference / channels;
                    Assert.True(mean <= 0.75 && maximumDifference <= 10,
                        $"Shadow changed at {scale}x / opacity {opacity}: mean {mean:F3}, max {maximumDifference}.");
                    var beneath = ((216 * scale) * 320 * scale + 160 * scale) * 4;
                    Assert.True(rendered[beneath] < background.Color.B,
                        "The optimized island must actually paint its shadow below the pane.");
                }
            }
            finally { window.Close(); }
            return true;

            byte[] Capture(Control control, int renderScale)
            {
                stage.Children.Clear();
                Canvas.SetLeft(control, 64);
                Canvas.SetTop(control, 48);
                stage.Children.Add(control);
                window.InvalidateMeasure();
                Dispatcher.UIThread.RunJobs();
                using var bitmap = new RenderTargetBitmap(
                    new PixelSize(320 * renderScale, 300 * renderScale),
                    new Vector(96 * renderScale, 96 * renderScale));
                bitmap.Render(window);
                var bytes = new byte[320 * 300 * renderScale * renderScale * 4];
                var buffer = Marshal.AllocHGlobal(bytes.Length);
                try
                {
                    bitmap.CopyPixels(new PixelRect(bitmap.PixelSize), buffer, bytes.Length, 320 * renderScale * 4);
                    Marshal.Copy(buffer, bytes, 0, bytes.Length);
                }
                finally { Marshal.FreeHGlobal(buffer); }
                return bytes;
            }
        }, CancellationToken.None);
    }
}
