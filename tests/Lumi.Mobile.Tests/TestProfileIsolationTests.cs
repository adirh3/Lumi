using System;
using System.IO;
using Lumi.Mobile.Services;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class TestProfileIsolationTests
{
    [Fact]
    public void ADefaultSettingsStoreNeverOpensTheRealPairing()
    {
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LumiMobile");

        _ = new MobileSettingsStore();

        Assert.Equal(TestProfile.Root, Environment.GetEnvironmentVariable("LUMI_MOBILE_DATA_DIR"));
        Assert.True(Directory.Exists(TestProfile.Root));
        Assert.False(TestProfile.Root.StartsWith(real, StringComparison.OrdinalIgnoreCase));
    }
}
