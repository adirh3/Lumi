using Lumi.Localization;
using Xunit;

namespace Lumi.Tests;

public sealed class LocLanguageTests
{
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
}
