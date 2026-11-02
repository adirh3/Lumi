using System;
using Lumi.Remote.Protocol;
using Lumi.Services.Remote;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// When the phone hears about a reply or a question: only once the user is away from the PC — locked,
/// or idle past the chosen time — and never for what they saw or stayed at the PC to deal with.
/// </summary>
public sealed class PhoneNotificationPolicyTests
{
    private static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(2);

    private static PhoneNotificationDecision Decide(
        TimeSpan age,
        TimeSpan idle,
        bool request = false,
        bool locked = false,
        bool phoneInForeground = false,
        bool openOnPhone = false,
        bool seenOnDesktop = false) =>
        PhoneNotificationCenter.Decide(new PhoneNotificationContext(
            request, age, locked, idle, AwayAfter, phoneInForeground, openOnPhone, seenOnDesktop));

    [Fact]
    public void AReplyWhileTheUserIsAwayGoesToThePhoneAtOnce() =>
        Assert.Equal(PhoneNotificationDecision.Deliver, Decide(TimeSpan.Zero, TimeSpan.FromMinutes(20)));

    [Fact]
    public void ALockedPcCountsAsAwayEvenRightAfterInput() =>
        Assert.Equal(PhoneNotificationDecision.Deliver, Decide(TimeSpan.Zero, TimeSpan.FromSeconds(3), locked: true));

    [Fact]
    public void NothingGoesToThePhoneWhileTheUserIsUsingThePc()
    {
        Assert.Equal(PhoneNotificationDecision.Wait, Decide(TimeSpan.Zero, TimeSpan.FromSeconds(2)));
        Assert.Equal(PhoneNotificationDecision.Wait, Decide(TimeSpan.Zero, TimeSpan.FromSeconds(2), request: true));
    }

    [Fact]
    public void AReplyTheUserSawOnThePcNeverGoesOut() =>
        Assert.Equal(PhoneNotificationDecision.Drop, Decide(TimeSpan.Zero, TimeSpan.FromSeconds(1), seenOnDesktop: true));

    [Fact]
    public void AQuestionTheUserSawStillWaitsForAnAnswer() =>
        Assert.Equal(
            PhoneNotificationDecision.Wait,
            Decide(TimeSpan.Zero, TimeSpan.FromSeconds(1), request: true, seenOnDesktop: true));

    [Fact]
    public void LeavingSoonAfterwardStillReachesThePhoneOnceAway()
    {
        // It happened five minutes ago; the user left two minutes ago without having dealt with it.
        Assert.Equal(PhoneNotificationDecision.Deliver, Decide(TimeSpan.FromMinutes(5), AwayAfter));
        // Two minutes ago they were still here: not away yet.
        Assert.Equal(PhoneNotificationDecision.Wait, Decide(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void StayingAtThePcLongAfterwardMeansTheUserHadTheirChance()
    {
        // The user kept working for twenty minutes after the reply, then walked away.
        var age = TimeSpan.FromMinutes(25);
        Assert.Equal(PhoneNotificationDecision.Drop, Decide(age, TimeSpan.FromMinutes(5)));
        Assert.Equal(PhoneNotificationDecision.Drop, Decide(age, TimeSpan.FromMinutes(5), request: true));
        Assert.Equal(PhoneNotificationDecision.Drop, Decide(age, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void LumiOpenOnThePhoneCountsAsAwayOncePcInputPauses()
    {
        Assert.Equal(
            PhoneNotificationDecision.Deliver,
            Decide(TimeSpan.Zero, PhoneNotificationCenter.PhoneGlanceIdle, phoneInForeground: true));
        Assert.Equal(
            PhoneNotificationDecision.Wait,
            Decide(TimeSpan.Zero, TimeSpan.FromSeconds(5), phoneInForeground: true));
    }

    [Fact]
    public void TheChatOpenOnThePhoneNeedsNoNotification() =>
        Assert.Equal(
            PhoneNotificationDecision.Drop,
            Decide(TimeSpan.Zero, TimeSpan.FromHours(1), locked: true, openOnPhone: true));
}

public sealed class PhoneNotificationTextTests
{
    [Fact]
    public void MarkdownBecomesTheWordsANotificationShows()
    {
        var text = PhoneNotificationText.FromMarkdown(
            "## Done\n\nI **updated** the [build script](https://example.com) and `ran` the *tests*.\n\n" +
            "- first\n* second\n> quoted\n\n![chart](c:\\chart.png)");

        Assert.Equal("Done\nI updated the build script and ran the tests.\n• first\n• second\nquoted\nchart", text);
    }

    [Fact]
    public void FencedBlocksAndTableRulesAreLeftOut()
    {
        var text = PhoneNotificationText.FromMarkdown(
            "Here is the plan:\n```chart\n{\"type\":\"bar\"}\n```\n| Name | Size |\n|---|---:|\n| a | 1 |\nThat's it.");

        Assert.Equal("Here is the plan:\nName · Size\na · 1\nThat's it.", text);
    }

    [Fact]
    public void SnakeCaseSurvivesEmphasisRemoval() =>
        Assert.Equal("Call my_helper_name now", PhoneNotificationText.FromMarkdown("Call my_helper_name *now*"));

    [Fact]
    public void LongTextIsCutWithAnEllipsis()
    {
        var text = PhoneNotificationText.FromMarkdown(new string('a', 5000));

        Assert.Equal(RemoteProtocol.MobileNotificationTextLimit, text.Length);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void ATitleStaysOnOneLine() =>
        Assert.Equal("Plan the trip", PhoneNotificationText.Line("  Plan\n the\ttrip ", 100));
}
