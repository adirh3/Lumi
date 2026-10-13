using Lumi.Localization;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class LocComputerNameTests
{
    public LocComputerNameTests() => Loc.Load("en");

    [Theory]
    [InlineData(true, false, "Mac")]
    [InlineData(false, true, "PC")]
    [InlineData(false, false, "Linux computer")]
    public void ComputerName_MatchesThePlatform(bool isMac, bool isWindows, string expected)
    {
        Assert.Equal(expected, Loc.GetComputerName(isMac, isWindows));
    }

    [Fact]
    public void AdaptComputerName_OnlyExpandsTheExplicitPlaceholder()
    {
        Assert.Equal($"Use your {Loc.ComputerName} and keep {{0}}.",
            Loc.AdaptComputerName("Use your {computer} and keep {0}."));
        Assert.Equal("Connect to MY-PC. The PC address is saved.",
            Loc.AdaptComputerName("Connect to MY-PC. The PC address is saved."));
        Assert.Equal("הכל נשאר במחשב שלך", Loc.AdaptComputerName("הכל נשאר במחשב שלך"));
    }

    [Theory]
    [InlineData("Onboarding_LearnDescription")]
    [InlineData("Onboarding_LearnPrivacy")]
    [InlineData("Onboarding_ScanningYourPC")]
    [InlineData("Onboarding_ExploringComputer")]
    [InlineData("SettingDesc_RemoteAccess")]
    [InlineData("Remote_TransportUnavailable")]
    [InlineData("Remote_DevTunnelPhoneTipsDescription")]
    [InlineData("Remote_DevTunnelStartFailed")]
    [InlineData("SettingDesc_MobileChoose")]
    [InlineData("Remote_SetupAndroidPanelDesc")]
    public void ComputerCopy_UsesTheCurrentHostName(string key)
    {
        var computerName = OperatingSystem.IsMacOS() ? "Mac"
            : OperatingSystem.IsWindows() ? "PC" : "Linux computer";
        var value = Loc.Get(key);

        Assert.Contains(computerName, value);
        Assert.DoesNotContain("{computer}", value);
    }

    [Fact]
    public void TypedComputerCopy_MatchesTheLocalizedLookup()
    {
        var computerName = OperatingSystem.IsMacOS() ? "Mac"
            : OperatingSystem.IsWindows() ? "PC" : "Linux computer";

        Assert.Contains(computerName, Loc.Onboarding_ScanningYourPC);
        Assert.Equal(Loc.Get("Onboarding_ScanningYourPC"), Loc.Onboarding_ScanningYourPC);
    }
}
