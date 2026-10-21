using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class VoiceLanguageCatalogTests
{
    [Fact]
    public void DetectionComesFirstThenTheUsersOwnLanguagesThenEveryOther()
    {
        var choices = VoiceLanguageCatalog.Build("Auto-detect", "", ["en-US", "en-US", "he"]);

        Assert.Equal(["", "en", "he"], choices.Suggested.Select(language => language.Code));
        Assert.Equal(["Auto-detect", "English", "עברית"], choices.Suggested.Select(language => language.Name));
        Assert.Same(choices.Suggested[0], choices.Selected);
        Assert.DoesNotContain(choices.More, language => language.Code is "" or "en" or "he");
        Assert.Contains(choices.More, language => language.Code == "ar" && language.Name == "العربية");
        Assert.Equal(99, choices.Suggested.Count - 1 + choices.More.Count);
        Assert.Equal(99, choices.Suggested.Skip(1).Concat(choices.More).Select(language => language.Code).Distinct().Count());
    }

    [Fact]
    public void EnglishIsAlwaysOfferedAndAChosenLanguageJoinsTheSuggestions()
    {
        var choices = VoiceLanguageCatalog.Build("Auto-detect", "fr", ["he-IL"]);

        Assert.Equal(["", "he", "en", "fr"], choices.Suggested.Select(language => language.Code));
        Assert.Equal("fr", choices.Selected.Code);
        Assert.Equal("Français", choices.Selected.Name);
        Assert.Equal("Français", choices.Selected.ToString());
    }

    [Fact]
    public void AnUnknownChoiceFallsBackToDetection()
    {
        var choices = VoiceLanguageCatalog.Build("Auto-detect", "xx", []);
        Assert.Equal("", choices.Selected.Code);
        Assert.Equal(["", "en"], choices.Suggested.Select(language => language.Code));
    }

    [Theory]
    [InlineData("he-IL", "he")]
    [InlineData("iw", "he")]
    [InlineData("nb-NO", "no")]
    [InlineData("fil-PH", "tl")]
    [InlineData("jv", "jw")]
    [InlineData("zh-Hans-CN", "zh")]
    [InlineData("pt_BR", "pt")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void CultureNamesMapToWhisperCodes(string? culture, string? code)
        => Assert.Equal(code, VoiceLanguageCatalog.ToWhisperCode(culture));
}
