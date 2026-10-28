using Lumi.Localization;
using Xunit;

namespace Lumi.Tests;

public sealed class LocLanguageTests
{
    [Theory]
    [InlineData("es", "es")]
    [InlineData("es-MX", "es")]
    [InlineData("de-DE", "de")]
    [InlineData("fr-CA", "fr")]
    [InlineData("ja-JP", "ja")]
    [InlineData("ko-KR", "ko")]
    [InlineData("ar-SA", "ar")]
    [InlineData("pt", "pt-BR")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("zh", "zh-Hans")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("zh-Hans-CN", "zh-Hans")]
    [InlineData("zh-Hant", "en")]
    [InlineData("zh-TW", "en")]
    [InlineData("unsupported", "en")]
    public void StartupSelectsSupportedCatalogAndCulture(string requested, string expected)
    {
        try
        {
            Loc.Load("he");
            Loc.Load(requested);
            Assert.Equal(expected, Loc.Culture.Name);
            Assert.Equal(expected is "he" or "ar", Loc.IsRightToLeft);
            if (expected == "en")
                Assert.Equal("Profile", Loc.Settings_Profile);
            else
                Assert.NotEqual("Profile", Loc.Settings_Profile);
            Assert.Contains(Loc.AvailableLanguages, language => language.Code == expected);
        }
        finally
        {
            Loc.Load("en");
        }
    }

    [Fact]
    public void DesktopAndMobileOfferTheSameLanguageChoices()
        => Assert.Equal(Loc.AvailableLanguages, Lumi.Mobile.Localization.ChatSessionStrings.AvailableLanguages);

    [Fact]
    public void LoadingEnglishAfterAnotherLanguageRestoresEnglish()
    {
        try
        {
            Loc.Load("he");
            Assert.NotEqual("Profile", Loc.Settings_Profile);
            Assert.True(Loc.IsRightToLeft);

            Loc.Load("en");
            Assert.Equal("Profile", Loc.Settings_Profile);
            Assert.Equal("Profile", Loc.Get(nameof(Loc.Settings_Profile)));
            Assert.False(Loc.IsRightToLeft);
        }
        finally
        {
            Loc.Load("en");
        }
    }

    [Fact]
    public void GenericComputerCopyStaysNeutralAcrossLanguages()
    {
        string[] keys =
        [
            nameof(Loc.Onboarding_CapPcChip),
            nameof(Loc.Onboarding_CapPcTitle),
            nameof(Loc.Onboarding_WorkspacePrivacy),
            nameof(Loc.SettingDesc_RemoteAccess),
            nameof(Loc.Remote_TransportUnavailable),
            nameof(Loc.Remote_DevTunnelPhoneTipsDescription),
            nameof(Loc.Remote_DevTunnelStartFailed),
            nameof(Loc.SettingDesc_MobileChoose),
            nameof(Loc.Remote_SetupAndroidPanelDesc),
            nameof(Loc.Discover_TrustTitle),
            nameof(Loc.Discover_RunsLocal),
            nameof(Loc.Discover_RuntimeMissing),
            nameof(Loc.Remote_LocalHostRequired),
            nameof(Loc.Remote_ModelUnavailable),
            nameof(Loc.Remote_NoActivePairingCode),
            nameof(Loc.Remote_PairingAttemptsExhausted)
        ];

        try
        {
            foreach (var language in new[] { "en", "he", "en" })
            {
                Loc.Load(language);
                foreach (var key in keys)
                {
                    var value = Loc.Get(key);
                    Assert.DoesNotContain("{computer}", value);
                    Assert.DoesNotMatch(@"\bPC\b", value);
                    if (language == "en")
                        Assert.Contains("computer", value);
                }

                Assert.Contains(Loc.Settings_Mobile, Loc.Remote_NoActivePairingCode);
            }

            Assert.Equal("Your computer", Loc.Onboarding_CapPcChip);
            Assert.Equal("Runs on this computer with DEV-PC",
                Loc.Get(nameof(Loc.Discover_RunsLocal), "DEV-PC"));
            Assert.Equal("Runs on this computer with {{computer}}",
                Loc.Get(nameof(Loc.Discover_RunsLocal), "{{computer}}"));
        }
        finally
        {
            Loc.Load("en");
        }
    }
}
