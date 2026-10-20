using System;
using System.IO;
using Avalonia;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

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

    [Fact]
    public void DisplayResultsDecodeEscapesWithoutChangingNonstringJson()
    {
        Assert.Equal("a\nb\"c", NativeBrowserLogic.ReadDisplayResult("\"a\\nb\\\"c\""));
        Assert.Equal("{\"ok\":true}", NativeBrowserLogic.ReadDisplayResult("{\"ok\":true}"));
    }
}
