#if !WINDOWS
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Lumi.Services;
using SkiaSharp;
using Xunit;

namespace Lumi.Tests;

public sealed class NativeWebViewPlatformTests
{
    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(5.0)]
    public void PageZoomValidation_AcceptsFinitePositiveUiScaleRatios(double zoom)
    {
        Assert.Null(Record.Exception(() => NativeWebViewPlatform.ValidatePageZoom(zoom)));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void PageZoomValidation_RejectsInvalidNativeZoomArguments(double zoom)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => NativeWebViewPlatform.ValidatePageZoom(zoom));
        Assert.Equal("zoom", error.ParamName);
    }

    [Theory]
    [InlineData(800, 600, 800, 600)]
    [InlineData(2048, 1024, 2048, 1024)]
    [InlineData(3840, 2160, 2048, 1152)]
    [InlineData(2160, 3840, 1152, 2048)]
    public void ScreenshotSizing_PreservesAspectRatioAndActualPngDimensions(
        int width, int height, int expectedWidth, int expectedHeight)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Magenta);
        var original = Screenshot(bitmap);

        var prepared = NativeWebViewPlatform.PrepareScreenshotForModel(original);

        Assert.Equal(expectedWidth, prepared.Width);
        Assert.Equal(expectedHeight, prepared.Height);
        Assert.Equal(original.TabId, prepared.TabId);
        Assert.Equal(original.Url, prepared.Url);
        Assert.InRange(prepared.PngBytes.Length, 1, NativeWebViewPlatform.MaxScreenshotPngBytes);
        Assert.Equal((expectedWidth, expectedHeight), NativeWebViewPlatform.ReadPngDimensions(prepared.PngBytes));
        if (width == expectedWidth && height == expectedHeight)
            Assert.Same(original, prepared);
        using var decoded = SKBitmap.Decode(prepared.PngBytes);
        Assert.Equal(expectedWidth, decoded.Width);
        Assert.Equal(expectedHeight, decoded.Height);
        Assert.Equal(SKColors.Magenta, decoded.GetPixel(0, 0));
    }

    [Fact]
    public void ScreenshotSizing_ReducesDetailedPngWithinReadableModelBudget()
    {
        using var bitmap = NoiseBitmap(2048, 1536);
        var original = Screenshot(bitmap);
        Assert.True(original.PngBytes.Length > NativeWebViewPlatform.MaxScreenshotPngBytes);

        var prepared = NativeWebViewPlatform.PrepareScreenshotForModel(original);

        Assert.InRange(prepared.PngBytes.Length, 1, NativeWebViewPlatform.MaxScreenshotPngBytes);
        Assert.InRange(Convert.ToBase64String(prepared.PngBytes).Length, 1, 4 * 1024 * 1024);
        Assert.InRange(prepared.Width, 1024, 2047);
        Assert.InRange(Math.Abs(prepared.Height - prepared.Width * 0.75), 0, 1);
        Assert.Equal((prepared.Width, prepared.Height), NativeWebViewPlatform.ReadPngDimensions(prepared.PngBytes));
        Assert.Equal(original.TabId, prepared.TabId);
        Assert.Equal(original.Url, prepared.Url);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(1000)]
    public void ScreenshotSizing_DoesNotShrinkBelowWindowsReadabilityFloor(int size)
    {
        using var bitmap = NoiseBitmap(size, size);
        var original = Screenshot(bitmap);
        Assert.True(original.PngBytes.Length > NativeWebViewPlatform.MaxScreenshotPngBytes);

        var error = Assert.Throws<InvalidOperationException>(
            () => NativeWebViewPlatform.PrepareScreenshotForModel(original));

        Assert.Contains("3 MiB PNG budget at a readable size", error.Message);
        Assert.Contains("look/find", error.Message);
    }

    [Fact]
    public void ScreenshotSizing_RejectsMetadataThatWouldMislabelNativePixels()
    {
        using var bitmap = new SKBitmap(32, 16);
        bitmap.Erase(SKColors.Blue);
        var mislabeled = Screenshot(bitmap) with { Width = 16, Height = 32 };

        var error = Assert.Throws<InvalidOperationException>(
            () => NativeWebViewPlatform.PrepareScreenshotForModel(mislabeled));

        Assert.Contains("metadata", error.Message);
    }

    [Fact]
    public void PngDimensions_RejectMissingHeaderAndEmptyViewport()
    {
        Assert.Throws<InvalidOperationException>(() => NativeWebViewPlatform.ReadPngDimensions([]));
        Assert.Throws<InvalidOperationException>(() => NativeWebViewPlatform.ReadPngDimensions(new byte[33]));
        using var bitmap = new SKBitmap(1, 1);
        bitmap.Erase(SKColors.Red);
        var bytes = Screenshot(bitmap).PngBytes;
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), 0);

        var error = Assert.Throws<InvalidOperationException>(() => NativeWebViewPlatform.ReadPngDimensions(bytes));
        Assert.Contains("empty", error.Message);
    }

    [Fact]
    public void GtkHandleSelection_UsesPublicWebViewPropertyNotX11WindowId()
    {
        var handle = new GtkHandle(41);
        Assert.NotEqual(handle.Handle, handle.WebKitWebView);

        Assert.Equal(handle.WebKitWebView, NativeWebViewPlatform.RequireGtkWebView(handle));
    }

    [Theory]
    [InlineData("XID")]
    [InlineData("WebKitWebView")]
    [InlineData("NSView")]
    public void GtkHandleSelection_RejectsUntypedPointerEvenIfDescriptorLooksCompatible(string descriptor)
    {
        Assert.Throws<PlatformNotSupportedException>(() =>
            NativeWebViewPlatform.RequireGtkWebView(new PlatformHandle(41, descriptor)));
    }

    [Fact]
    public void GtkHandleSelection_RejectsUninitializedAndWpeBackends()
    {
        Assert.Throws<PlatformNotSupportedException>(() => NativeWebViewPlatform.RequireGtkWebView(null));
        Assert.Throws<PlatformNotSupportedException>(() => NativeWebViewPlatform.RequireGtkWebView(new GtkHandle(0)));
        Assert.Throws<PlatformNotSupportedException>(() => NativeWebViewPlatform.RequireGtkWebView(new WpeHandle()));
    }

    [Fact]
    public void NativeDownloads_ReportUnsupportedPolicyAndDirectUrlAlternative()
    {
        var reason = NativeWebViewPlatform.NativeDownloadUnsupportedReason;

        Assert.Contains("unsupported on Linux and macOS", reason);
        Assert.Contains("curl with a verified direct HTTP(S) file URL", reason);
        Assert.Contains("browser-session cookies are not transferred automatically", reason);
        Assert.Contains("verify the response and saved file", reason);
    }

    private static BrowserScreenshot Screenshot(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return new BrowserScreenshot("tab-native", "https://example.test/viewport",
            bitmap.Width, bitmap.Height, png.ToArray());
    }

    private static SKBitmap NoiseBitmap(int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var pixels = new byte[bitmap.ByteCount];
        new Random(42).NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        return bitmap;
    }

    private sealed class GtkHandle(nint webView) : IGtkWebViewPlatformHandle
    {
        public nint WebKitWebView => webView;
        public nint Handle => 500;
        public string HandleDescriptor => "XID";
    }

    private sealed class WpeHandle : ILinuxWpePlatformHandle
    {
        public nint WebKitWebView => 41;
        public nint WpeViewBackend => 42;
        public nint Handle => 43;
        public string HandleDescriptor => "WPE";
    }
}
#endif
