using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class AppIconTests
{
    [Theory]
    [InlineData("/Applications/Lumi.app/Contents/MacOS/Lumi")]
    [InlineData("/Users/test/Downloads/Lumi Preview.app/Contents/MacOS/Lumi")]
    [InlineData("/private/var/folders/test/AppTranslocation/id/d/Lumi.app/Contents/MacOS/Lumi")]
    public void ShouldUseRuntimeDockIcon_BundledMacOsLaunch_PreservesBundleIcon(string processPath)
    {
        Assert.False(AppIcon.ShouldUseRuntimeDockIcon(isMacOS: true, processPath));
    }

    [Theory]
    [InlineData("/Users/test/Lumi/bin/Debug/net11.0/Lumi")]
    [InlineData("/Users/test/publish-osx-arm64/Lumi")]
    [InlineData("/usr/local/share/dotnet/dotnet")]
    [InlineData("/Users/test/example.app/bin/Debug/net11.0/Lumi")]
    public void ShouldUseRuntimeDockIcon_UnbundledMacOsLaunch_KeepsFallback(string processPath)
    {
        Assert.True(AppIcon.ShouldUseRuntimeDockIcon(isMacOS: true, processPath));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Lumi\Lumi.exe")]
    [InlineData("/opt/lumi/Lumi")]
    public void ShouldUseRuntimeDockIcon_NonMacOsLaunch_DoesNotOverrideIcon(string processPath)
    {
        Assert.False(AppIcon.ShouldUseRuntimeDockIcon(isMacOS: false, processPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ShouldUseRuntimeDockIcon_UnknownExecutablePath_LeavesExistingIcon(string? processPath)
    {
        Assert.False(AppIcon.ShouldUseRuntimeDockIcon(isMacOS: true, processPath));
    }
}
