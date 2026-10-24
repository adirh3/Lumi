using Lumi.Models;
using Lumi.Qa;
using Xunit;

namespace Lumi.Tests;

[Trait("Category", "CoreQA")]
public sealed class QaSmokeAssertionTests
{
    [Fact]
    public void OneEnabledFeatureDoesNotSatisfyAllOnOrAllOff()
    {
        var settings = AllOff();
        settings.AutoGenerateTitles = true;
        Assert.False(QaSmokeAssertions.AiFeaturesMatch(settings, enabled: true));
        Assert.False(QaSmokeAssertions.AiFeaturesMatch(settings, enabled: false));
    }

    [Fact]
    public void EveryFeatureMustMatchTheRequestedState()
    {
        var settings = AllOff();
        Assert.True(QaSmokeAssertions.AiFeaturesMatch(settings, enabled: false));
        Assert.False(QaSmokeAssertions.AiFeaturesMatch(settings, enabled: true));
        settings.AutoGenerateTitles = true;
        settings.SuggestFollowUps = true;
        settings.EnableMemoryAutoSave = true;
        settings.EnableDailyBriefs = true;
        Assert.True(QaSmokeAssertions.AiFeaturesMatch(settings, enabled: true));
        Assert.False(QaSmokeAssertions.AiFeaturesMatch(settings, enabled: false));
        settings.EnableMemoryAutoSave = false;
        Assert.False(QaSmokeAssertions.AiFeaturesMatch(settings, enabled: true));
    }

    [Fact]
    public void EveryExpectedReplyMustAlreadyExistInTheReadSnapshot()
    {
        string[] expected = ["first reply", "second reply"];
        var messages = new List<ChatMessage>();
        Assert.False(QaSmokeAssertions.RepliesAreStored(messages, expected));
        messages.Add(new() { Role = "assistant", Content = expected[0] });
        Assert.False(QaSmokeAssertions.RepliesAreStored(messages, expected));
        messages.Add(new() { Role = "assistant", Content = expected[1] });
        Assert.True(QaSmokeAssertions.RepliesAreStored(messages, expected));
        Assert.False(QaSmokeAssertions.RepliesAreStored(messages, []));
    }

    [Theory]
    [InlineData("user", false, "reply")]
    [InlineData("tool", false, "reply")]
    [InlineData("assistant", true, "reply")]
    [InlineData("assistant", false, "a reply that is not the expected reply")]
    public void PromptToolPartialAndSubstringMatchesAreNotPersistedReplies(string role, bool streaming, string content)
        => Assert.False(QaSmokeAssertions.RepliesAreStored(
            [new ChatMessage { Role = role, IsStreaming = streaming, Content = content }], ["reply"]));

    private static UserSettings AllOff() => new()
    {
        AutoGenerateTitles = false, SuggestFollowUps = false, EnableMemoryAutoSave = false,
        EnableDailyBriefs = false
    };
}
