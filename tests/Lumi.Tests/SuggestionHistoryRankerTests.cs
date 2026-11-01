using System;
using System.Collections.Generic;
using System.Linq;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class SuggestionHistoryRankerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Chat = Guid.NewGuid();
    private static readonly Guid OtherChat = Guid.NewGuid();
    private static readonly Guid ChatOutsideProjects = Guid.NewGuid();
    private static readonly Guid OtherProjectChat = Guid.NewGuid();
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid OtherProject = Guid.NewGuid();

    private static readonly Dictionary<Guid, Guid?> Projects = new()
    {
        [Chat] = Project,
        [OtherChat] = Project,
        [ChatOutsideProjects] = null,
        [OtherProjectChat] = OtherProject,
    };

    private static SuggestionHistoryRanker.Scope In(Guid chatId)
        => new(chatId, Projects.GetValueOrDefault(chatId), id => Projects.GetValueOrDefault(id), Now);

    private static UserPromptHistoryItem Item(string content, DateTimeOffset timestamp, Guid? chatId = null)
        => new(chatId ?? OtherChat, Guid.NewGuid(), content, timestamp);

    private static IEnumerable<UserPromptHistoryItem> Repeat(string content, int count, DateTimeOffset start, Guid? chatId = null)
        => Enumerable.Range(0, count).Select(i => Item(content, start.AddMinutes(i), chatId));

    private static IReadOnlyList<SuggestionHistoryRanker.UsualReply> Rank(
        IEnumerable<UserPromptHistoryItem> history, Guid? chatId = null, int maxItems = 8, int minOccurrences = 2)
        => SuggestionHistoryRanker.RankUsualReplies(history, In(chatId ?? Chat), maxItems, minOccurrences);

    // ── Where and when it was asked ───────────────────────────────────────────

    [Fact]
    public void Rank_LearnsOnlyFromTheSameProject()
    {
        var history = new List<UserPromptHistoryItem>();
        history.AddRange(Repeat("open google.com in the browser", 6, Now.AddDays(-1), ChatOutsideProjects));
        history.AddRange(Repeat("deploy the site to production", 6, Now.AddDays(-1), OtherProjectChat));
        history.AddRange(Repeat("commit and push to main", 3, Now.AddDays(-1), OtherChat));

        var commit = Assert.Single(Rank(history));

        Assert.Equal("Commit and push to main", commit.Text);
        Assert.Equal(3, commit.Count);
    }

    [Fact]
    public void Rank_ForAChatOutsideProjects_ReturnsNothing()
    {
        var history = new List<UserPromptHistoryItem>();
        history.AddRange(Repeat("commit and push to main", 30, Now.AddDays(-1), OtherChat));
        history.AddRange(Repeat("open google.com in the browser", 20, Now.AddDays(-1), ChatOutsideProjects));

        Assert.Empty(Rank(history, ChatOutsideProjects));
    }

    [Fact]
    public void Rank_PrefersTheSameChatWithinAProject()
    {
        var history = new List<UserPromptHistoryItem>();
        history.AddRange(Repeat("run the integration tests again", 3, Now.AddDays(-2), OtherChat));
        history.AddRange(Repeat("show me the screenshot again", 3, Now.AddDays(-2), Chat));

        Assert.Equal("Show me the screenshot again", Rank(history)[0].Text);
    }

    [Fact]
    public void Rank_PrefersRecentRequests()
    {
        var history = new List<UserPromptHistoryItem>();
        history.AddRange(Repeat("commit and push to version/1.4", 4, Now.AddDays(-60)));
        history.AddRange(Repeat("commit and push to version/1.5", 4, Now.AddDays(-2)));

        var ranked = Rank(history);

        Assert.Equal("Commit and push to version/1.5", ranked[0].Text);
        Assert.True(ranked[0].Weight > ranked[1].Weight * 4);
    }

    // ── One request, the user's words ─────────────────────────────────────────

    [Fact]
    public void Rank_MergesLeadingFillerVariants()
    {
        var history = new List<UserPromptHistoryItem>();
        history.AddRange(Repeat("commit and push to main", 4, Now.AddHours(-5)));
        history.AddRange(Repeat("ok commit and push to main", 3, Now.AddHours(-4)));
        history.AddRange(Repeat("nice work, commit and push to main", 2, Now.AddHours(-3)));
        history.AddRange(Repeat("Looks good! Commit and push to main.", 1, Now.AddHours(-2)));

        var commit = Assert.Single(Rank(history));

        Assert.Equal(10, commit.Count);
        Assert.Equal("Commit and push to main", commit.Text);
    }

    [Fact]
    public void Rank_ShowsTheMostRecentWording()
    {
        var history = new List<UserPromptHistoryItem>
        {
            Item("Run Code Review", Now.AddHours(-2)),
            Item("run code review", Now.AddHours(-1)),
        };

        Assert.Equal("Run code review", Assert.Single(Rank(history)).Text);
    }

    // ── Noise ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Rank_DropsBackgroundJobTriggers()
        => Assert.Empty(Rank(Repeat("Background job triggered: Extra OLED TV deal monitor until Monday", 6, Now.AddHours(-1))));

    [Fact]
    public void Rank_DropsTrivialAndTooShortPrompts()
    {
        var history = new List<UserPromptHistoryItem>();
        history.AddRange(Repeat("continue", 30, Now.AddHours(-1)));
        history.AddRange(Repeat("ping", 20, Now.AddHours(-1)));
        history.AddRange(Repeat("hey", 15, Now.AddHours(-1)));
        history.AddRange(Repeat("ok, did you check today?", 10, Now.AddHours(-1)));
        history.AddRange(Repeat("commit and push to main", 4, Now.AddHours(-1)));

        Assert.Equal("Commit and push to main", Assert.Single(Rank(history)).Text);
    }

    [Fact]
    public void Rank_DropsOverlyLongPrompts()
        => Assert.Empty(Rank(Repeat(new string('x', 200), 5, Now.AddHours(-1))));

    [Fact]
    public void Rank_ExcludesOneOffRequests()
    {
        var history = new List<UserPromptHistoryItem>
        {
            Item("write me a long unique essay about computing history", Now.AddHours(-3), Chat),
            Item("plan a road trip across northern italy next week", Now.AddHours(-3), Chat),
        };
        history.AddRange(Repeat("commit and push to main", 2, Now.AddHours(-2)));

        Assert.Equal("Commit and push to main", Assert.Single(Rank(history)).Text);
    }

    [Fact]
    public void Rank_RespectsMaxItemsAndMinOccurrences()
    {
        var history = new List<UserPromptHistoryItem>();
        for (var i = 0; i < 12; i++)
            history.AddRange(Repeat($"recurring request number {i:00}", 2 + i, Now.AddHours(-i - 1)));

        Assert.Equal(5, Rank(history, maxItems: 5).Count);
        Assert.Equal(9, Rank(history, maxItems: 20, minOccurrences: 5).Count);
    }

    [Fact]
    public void Rank_ThrowsForInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Rank([], maxItems: 0));
        Assert.Throws<ArgumentNullException>(() => SuggestionHistoryRanker.RankUsualReplies(null!, In(Chat), 8));
    }

    [Theory]
    [InlineData("Commit and push to main", "commit and push to main")]
    [InlineData("nice work, commit and push to main!", "commit and push to main")]
    [InlineData("Ok. Run code review", "run code review")]
    [InlineData("  Did   you push to main?  ", "did you push to main")]
    [InlineData("let's work on the settings page", "work on the settings page")]
    [InlineData("well-known issue with the tab strip", "well-known issue with the tab strip")]
    [InlineData("nowhere to be found in the logs", "nowhere to be found in the logs")]
    [InlineData("thanks!", "thanks")]
    public void NormalizeRequest_KeepsOnlyWhatIsAskedFor(string message, string expected)
        => Assert.Equal(expected, SuggestionHistoryRanker.NormalizeRequest(message));
}
