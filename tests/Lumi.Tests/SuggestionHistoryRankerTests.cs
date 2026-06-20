using System;
using System.Collections.Generic;
using System.Linq;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class SuggestionHistoryRankerTests
{
    private static readonly DateTimeOffset Origin = new(2026, 4, 23, 8, 0, 0, TimeSpan.Zero);

    private static UserPromptHistoryItem Item(string content, DateTimeOffset timestamp)
        => new(Guid.NewGuid(), Guid.NewGuid(), content, timestamp);

    private static IEnumerable<UserPromptHistoryItem> Repeat(string content, int count, DateTimeOffset start)
        => Enumerable.Range(0, count).Select(i => Item(content, start.AddSeconds(i)));

    // ── Frequency + dedup ─────────────────────────────────────────────────────

    [Fact]
    public void Rank_OrdersByFrequencyThenRecency()
    {
        var history = new List<UserPromptHistoryItem>();
        history.AddRange(Repeat("commit and push to main", 5, Origin));
        history.AddRange(Repeat("run code review", 3, Origin.AddHours(1)));
        history.AddRange(Repeat("open google.com in the browser", 3, Origin.AddHours(2)));

        var ranked = SuggestionHistoryRanker.RankFrequentRequests(history, 8);

        Assert.Equal("commit and push to main", ranked[0].Content);
        Assert.Equal(5, ranked[0].Count);
        // Tie on count (3 vs 3) is broken by recency: "open google" is newer than "run code review".
        Assert.Equal("open google.com in the browser", ranked[1].Content);
        Assert.Equal("run code review", ranked[2].Content);
    }

    [Fact]
    public void Rank_MergesLeadingFillerVariants()
    {
        var history = new List<UserPromptHistoryItem>();
        history.AddRange(Repeat("commit and push to main", 4, Origin));
        history.AddRange(Repeat("ok commit and push to main", 3, Origin.AddMinutes(10)));
        history.AddRange(Repeat("nice, commit and push to main", 2, Origin.AddMinutes(20)));

        var ranked = SuggestionHistoryRanker.RankFrequentRequests(history, 8);

        var commit = Assert.Single(ranked, r => r.Content.Contains("commit and push to main", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(9, commit.Count); // 4 + 3 + 2 collapsed into one request
    }

    [Fact]
    public void Rank_DisplaysMostRecentOriginalCasing()
    {
        var history = new List<UserPromptHistoryItem>
        {
            Item("Run Code Review", Origin),
            Item("run code review", Origin.AddMinutes(5)),
        };

        var ranked = SuggestionHistoryRanker.RankFrequentRequests(history, 8);

        Assert.Equal("run code review", Assert.Single(ranked).Content); // latest wins
    }

    // ── Noise filtering ───────────────────────────────────────────────────────

    [Fact]
    public void Rank_DropsBackgroundJobTriggers()
    {
        var history = Repeat("Background job triggered: Extra OLED TV deal monitor until Monday", 6, Origin).ToList();

        var ranked = SuggestionHistoryRanker.RankFrequentRequests(history, 8);

        Assert.Empty(ranked);
    }

    [Fact]
    public void Rank_DropsTrivialAndTooShortPrompts()
    {
        var history = new List<UserPromptHistoryItem>();
        history.AddRange(Repeat("continue", 30, Origin));
        history.AddRange(Repeat("ping", 20, Origin));
        history.AddRange(Repeat("hey", 15, Origin));
        history.AddRange(Repeat("did you check today?", 10, Origin));
        history.AddRange(Repeat("commit and push to main", 4, Origin));

        var ranked = SuggestionHistoryRanker.RankFrequentRequests(history, 8);

        Assert.Equal("commit and push to main", Assert.Single(ranked).Content);
    }

    [Fact]
    public void Rank_DropsOverlyLongPrompts()
    {
        var longPrompt = new string('x', 200);
        var history = Repeat(longPrompt, 5, Origin).ToList();

        var ranked = SuggestionHistoryRanker.RankFrequentRequests(history, 8);

        Assert.Empty(ranked);
    }

    // ── Recurrence + limits ───────────────────────────────────────────────────

    [Fact]
    public void Rank_ExcludesOneOffRequests()
    {
        var history = new List<UserPromptHistoryItem>
        {
            Item("write me a long unique essay about computing history", Origin),
            Item("plan a road trip across northern italy next week", Origin.AddMinutes(1)),
        };
        history.AddRange(Repeat("commit and push to main", 2, Origin.AddMinutes(2)));

        var ranked = SuggestionHistoryRanker.RankFrequentRequests(history, 8);

        Assert.Equal("commit and push to main", Assert.Single(ranked).Content);
    }

    [Fact]
    public void Rank_RespectsMaxItems()
    {
        var history = new List<UserPromptHistoryItem>();
        for (var i = 0; i < 12; i++)
            history.AddRange(Repeat($"recurring request number {i:00}", 2 + i, Origin.AddMinutes(i)));

        var ranked = SuggestionHistoryRanker.RankFrequentRequests(history, 5);

        Assert.Equal(5, ranked.Count);
    }

    [Fact]
    public void Rank_HonorsCustomMinOccurrences()
    {
        var history = new List<UserPromptHistoryItem>();
        history.AddRange(Repeat("first recurring action item", 3, Origin));
        history.AddRange(Repeat("second recurring action item", 4, Origin.AddMinutes(5)));

        var atLeastFour = SuggestionHistoryRanker.RankFrequentRequests(history, 8, minOccurrences: 4);

        Assert.Equal("second recurring action item", Assert.Single(atLeastFour).Content);
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    [Fact]
    public void Render_OmitsCountsAndBulletsEachRequest()
    {
        var history = new List<UserPromptHistoryItem>();
        history.AddRange(Repeat("commit and push to main", 5, Origin));
        history.AddRange(Repeat("run code review", 3, Origin.AddHours(1)));

        var block = SuggestionHistoryRanker.BuildFrequentRequestsBlock(history, 8);

        Assert.NotNull(block);
        Assert.Equal("- commit and push to main\n- run code review", block!.Replace("\r\n", "\n"));
        Assert.DoesNotContain("5x", block);
        Assert.DoesNotContain("used", block);
    }

    [Fact]
    public void Build_ReturnsNullWhenNothingQualifies()
    {
        var history = new List<UserPromptHistoryItem>
        {
            Item("continue", Origin),
            Item("a one-off question asked exactly once here", Origin.AddMinutes(1)),
        };

        var block = SuggestionHistoryRanker.BuildFrequentRequestsBlock(history, 8);

        Assert.Null(block);
    }

    [Fact]
    public void Build_ReturnsNullForEmptyHistory()
    {
        Assert.Null(SuggestionHistoryRanker.BuildFrequentRequestsBlock(Array.Empty<UserPromptHistoryItem>(), 8));
    }

    // ── Guard rails ───────────────────────────────────────────────────────────

    [Fact]
    public void Rank_ThrowsForNonPositiveMaxItems()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SuggestionHistoryRanker.RankFrequentRequests(Array.Empty<UserPromptHistoryItem>(), 0));
    }

    [Fact]
    public void Rank_ThrowsForNullHistory()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SuggestionHistoryRanker.RankFrequentRequests(null!, 8));
    }
}
