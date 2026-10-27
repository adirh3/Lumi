using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class NativeBrowserLogicTests
{
    [Fact]
    public void CurrentPlatformAvailabilityRequiresPrivateNativeProfileSupport() =>
        Assert.Equal(
            OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOSVersionAtLeast(14),
            NativeBrowserLogic.IsEmbeddedBrowserAvailable);

    [Theory]
    [InlineData("\"hello\"", true, "hello")]
    [InlineData("null", true, null)]
    [InlineData("123", false, null)]
    [InlineData("true", false, null)]
    [InlineData("{}", false, null)]
    [InlineData("[]", false, null)]
    [InlineData("broken", false, null)]
    [InlineData("\"\\ud800\"", false, null)]
    [InlineData("\"\\udfff\"", false, null)]
    public void JsonStringReaderPreservesValuesAndRejectsUndecodableStrings(
        string input, bool expected, string? value)
    {
        Assert.Equal(expected, NativeBrowserLogic.TryReadString(input, out var actual));
        Assert.Equal(value, actual);
    }

    [Theory]
    [InlineData(false, "\"value\"", "\"value\"")]
    [InlineData(true, "\"\\\"value\\\"\"", "\"value\"")]
    [InlineData(true, "null", "null")]
    [InlineData(true, "\"\\ud800\"", "\"\\ud800\"")]
    public void ResultNormalizationNeverUsesReflectionSerialization(
        bool doubleEncoded, string input, string expected) =>
        Assert.Equal(expected, NativeBrowserLogic.NormalizeResult(input, doubleEncoded));

    [Fact]
    public void EncodingProbeRequiresTheActualKnownResult()
    {
        var single = "\"" + NativeBrowserLogic.EncodingProbe + "\"";
        var doubleEncoded = "\"\\\"" + NativeBrowserLogic.EncodingProbe + "\\\"\"";
        Assert.False(NativeBrowserLogic.DetectDoubleEncoding(single));
        Assert.True(NativeBrowserLogic.DetectDoubleEncoding(doubleEncoded));
        Assert.Throws<InvalidOperationException>(() => NativeBrowserLogic.DetectDoubleEncoding("null"));
        Assert.Throws<InvalidOperationException>(() => NativeBrowserLogic.DetectDoubleEncoding("unrelated"));
    }

    [Fact]
    public void AppleStoreIdentityIsStableAndIsolatedByProfileDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "lumi-native-store-test");
        var id = NativeBrowserLogic.GetAppleDataStoreId(root);
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(id, NativeBrowserLogic.GetAppleDataStoreId(root + Path.DirectorySeparatorChar));
        Assert.NotEqual(id, NativeBrowserLogic.GetAppleDataStoreId(root + "-isolated"));
        Assert.NotEqual(id, NativeBrowserLogic.GetAppleDataStoreId(Path.Combine(root, "another-profile")));
    }

    [Fact]
    public void OverlayConvertsPhysicalPixelsUsingTheActualHostTransform()
    {
        var physical = new Rect(400, 200, 1200, 900);
        Assert.Equal(new Rect(200, 100, 600, 450),
            NativeBrowserLogic.GetOverlayBounds(physical, 2, default));
        Assert.Equal(new Rect(120, 50, 400, 300),
            NativeBrowserLogic.GetOverlayBounds(physical, 2, new Point(20, 25), 1.5, 1.5));
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public async Task NativeHostKeepsPhysicalCoordinatesInEitherWindowDirection(FlowDirection direction)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var expected = new Rect(40, 60, 240, 180);
            var nativeView = new Border { Width = expected.Width, Height = expected.Height };
            var layer = NativeBrowserLogic.CreateHostLayer();
            layer.Children.Add(nativeView);
            var root = new Grid();
            root.Children.Add(new Border());
            root.Children.Add(layer);
            var window = new Window
            {
                Width = 640,
                Height = 480,
                FlowDirection = direction,
                Content = root,
            };
            window.Show();
            try
            {
                window.UpdateLayout();
                var scale = window.RenderScaling;
                var physical = new Rect(expected.X * scale, expected.Y * scale,
                    expected.Width * scale, expected.Height * scale);
                var bounds = NativeBrowserLogic.GetOverlayBounds(layer, physical, scale);

                Assert.NotNull(bounds);
                Assert.Equal(expected, bounds.Value);
                Assert.Equal(FlowDirection.LeftToRight, layer.FlowDirection);
                Assert.Equal(FlowDirection.LeftToRight, nativeView.FlowDirection);
                Canvas.SetLeft(nativeView, bounds.Value.X);
                Canvas.SetTop(nativeView, bounds.Value.Y);
                window.UpdateLayout();
                Assert.Equal(expected.TopLeft, nativeView.TranslatePoint(default, window));
                Assert.Equal(expected.BottomRight,
                    nativeView.TranslatePoint(new Point(expected.Width, expected.Height), window));
            }
            finally
            {
                window.Close();
                window.Content = null;
            }
        }, CancellationToken.None);
    }

    [Fact]
    public void DisplayResultsDecodeEscapesWithoutChangingNonstringJson()
    {
        Assert.Equal("a\nb\"c", NativeBrowserLogic.ReadDisplayResult("\"a\\nb\\\"c\""));
        Assert.Equal("{\"ok\":true}", NativeBrowserLogic.ReadDisplayResult("{\"ok\":true}"));
    }
}
