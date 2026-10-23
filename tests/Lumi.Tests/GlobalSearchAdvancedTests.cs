using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Integration coverage for time-aware, full-coverage, recency-weighted search: temporal
/// filtering/browse, content found in any chat however old, recency ranking, and scale.
/// </summary>
public class GlobalSearchAdvancedTests
{
    // Friday, 12 June 2026, 12:00 UTC.
    private static readonly DateTimeOffset Now = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);

    private static Chat ColdChat(string title, DateTimeOffset updatedAt) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        UpdatedAt = updatedAt
    };

    private static ChatSearchSnapshot Snapshot(string version, string text) => new()
    {
        Version = version,
        Messages = [new ChatSearchMessage { Text = text, Timestamp = Now }]
    };

    private static GlobalSearchService CreateService(
        AppData data,
        IReadOnlyDictionary<Guid, ChatSearchSnapshot>? snapshots = null)
        => SearchTestContent.CreateService(data, snapshots, () => Now);

    [Fact]
    public async Task Search_Yesterday_ListsOnlyYesterdaysChats()
    {
        var yesterday = ColdChat("Standup notes", Now.AddDays(-1));
        var today = ColdChat("Today plan", Now.AddHours(-1));
        var lastWeek = ColdChat("Old thread", Now.AddDays(-6));

        var service = CreateService(new AppData { Chats = [yesterday, today, lastWeek] });

        var chatResults = (await service.SearchAsync("yesterday"))
            .Where(static match => match.Category == GlobalSearchCategory.Chats)
            .ToList();

        var match = Assert.Single(chatResults);
        Assert.Same(yesterday, match.Item);
    }

    [Fact]
    public async Task Search_MonthName_ScopesToThatMonth()
    {
        var may = ColdChat("Spring planning", new DateTimeOffset(2026, 5, 15, 9, 0, 0, TimeSpan.Zero));
        var june = ColdChat("Summer planning", new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero));

        var service = CreateService(new AppData { Chats = [may, june] });

        var chatResults = (await service.SearchAsync("may"))
            .Where(static match => match.Category == GlobalSearchCategory.Chats)
            .ToList();

        var match = Assert.Single(chatResults);
        Assert.Same(may, match.Item);
    }

    [Fact]
    public async Task Search_TimeScopedText_FiltersOutOfWindowTitleMatches()
    {
        var recent = ColdChat("Rollout plan", Now.AddDays(-3));
        var old = ColdChat("Rollout plan", Now.AddDays(-60));

        var service = CreateService(new AppData { Chats = [recent, old] });

        var chatResults = (await service.SearchAsync("rollout last 14 days"))
            .Where(static match => match.Category == GlobalSearchCategory.Chats)
            .ToList();

        var match = Assert.Single(chatResults);
        Assert.Same(recent, match.Item);
    }

    [Fact]
    public async Task Search_RecencyIntent_RanksNewestFirst()
    {
        var newest = ColdChat("Quarterly numbers", Now.AddHours(-1));
        var middle = ColdChat("Quarterly numbers", Now.AddDays(-3));
        var oldest = ColdChat("Quarterly numbers", Now.AddDays(-30));

        var service = CreateService(new AppData { Chats = [oldest, middle, newest] });

        var chatResults = (await service.SearchAsync("recent"))
            .Where(static match => match.Category == GlobalSearchCategory.Chats)
            .ToList();

        Assert.Same(newest, chatResults[0].Item);
        Assert.Same(middle, chatResults[1].Item);
        Assert.Same(oldest, chatResults[2].Item);
    }

    [Fact]
    public async Task Search_FullMode_FindsOldChatByContentBeyondRecentCap()
    {
        var chats = new List<Chat>();
        var snapshots = new Dictionary<Guid, ChatSearchSnapshot>();

        // 40 cold chats; only the OLDEST (well beyond the 16 most-recent cap) holds the keyword.
        for (var i = 0; i < 40; i++)
        {
            var chat = ColdChat($"Conversation {i}", Now.AddDays(-i));
            chats.Add(chat);
            snapshots[chat.Id] = Snapshot(
                $"v-{i}",
                i == 39
                    ? "Discussion about the quokkaproject migration steps."
                    : "Generic conversation about unrelated daily topics.");
        }

        var service = CreateService(new AppData { Chats = chats }, snapshots);

        var results = await service.SearchAsync("quokkaproject", GlobalSearchExecutionMode.Full);

        var match = Assert.Single(results);
        Assert.Same(chats[39], match.Item);
        Assert.True(match.IsContentMatch);
    }

    [Fact]
    public async Task Search_MultiTerm_AcrossTitleAndContent_WithinTimeWindow()
    {
        var chat = ColdChat("Alpha planning", Now.AddDays(-2));
        var snapshots = new Dictionary<Guid, ChatSearchSnapshot>
        {
            [chat.Id] = Snapshot("v1", "Beta rollout is ready for the next milestone.")
        };

        var service = CreateService(new AppData { Chats = [chat] }, snapshots);

        var results = await service.SearchAsync("alpha beta last 7 days", GlobalSearchExecutionMode.Full);

        var match = Assert.Single(results);
        Assert.Same(chat, match.Item);
        Assert.True(match.IsContentMatch);
    }

    [Fact]
    public async Task Search_ScalesToLargeHistory_AndFindsRareContent()
    {
        var chats = new List<Chat>(1200);
        var snapshots = new Dictionary<Guid, ChatSearchSnapshot>(1200);
        for (var i = 0; i < 1200; i++)
        {
            var chat = ColdChat($"Conversation {i}", Now.AddDays(-i));
            chats.Add(chat);
            snapshots[chat.Id] = Snapshot(
                $"v-{i}",
                i == 1000
                    ? "contains the zylphquark research summary"
                    : "routine conversation about meetings and todos");
        }

        var service = CreateService(new AppData { Chats = chats }, snapshots);

        var fast = await service.SearchAsync("zylphquark", GlobalSearchExecutionMode.Fast);

        Assert.Same(chats[1000], Assert.Single(fast).Item);
    }

    [Fact]
    public async Task Search_GenuineContentMatch_OutranksFuzzyTitleNoise()
    {
        // Newest chat whose TITLE only fuzzily resembles the query ("kubernetes" vs the "kubernets"
        // typo — edit distance 1, not a real substring) and whose content is unrelated.
        var fuzzyTitle = ColdChat("Kubernetes deployment", Now.AddHours(-1));
        // 40 days older, but its CONTENT contains the exact query term.
        var contentMatch = ColdChat("Infra notes", Now.AddDays(-40));

        var snapshots = new Dictionary<Guid, ChatSearchSnapshot>
        {
            [fuzzyTitle.Id] = Snapshot("v-fuzzy", "Unrelated discussion about scaling pods and nodes."),
            [contentMatch.Id] = Snapshot("v-content", "We finished the kubernets rollout last sprint.")
        };

        var service = CreateService(new AppData { Chats = [fuzzyTitle, contentMatch] }, snapshots);

        var chatResults = (await service.SearchAsync("kubernets", GlobalSearchExecutionMode.Full))
            .Where(static match => match.Category == GlobalSearchCategory.Chats)
            .ToList();

        var contentIndex = chatResults.FindIndex(match => ReferenceEquals(match.Item, contentMatch));
        var fuzzyIndex = chatResults.FindIndex(match => ReferenceEquals(match.Item, fuzzyTitle));

        Assert.True(contentIndex >= 0, "The genuine content match should be present.");
        Assert.True(fuzzyIndex >= 0, "The fuzzy title match should still be present.");
        // Despite being far older AND only a content hit, it outranks the fuzzy (newer) title noise.
        Assert.True(
            contentIndex < fuzzyIndex,
            $"Expected content match (#{contentIndex}) to rank above fuzzy title (#{fuzzyIndex}).");
        Assert.True(chatResults[contentIndex].IsContentMatch);
    }

    [Fact]
    public async Task Search_ExactTitleMatch_TopsContentAndFuzzyNoise()
    {
        var exactTitle = ColdChat("Kubernets runbook", Now.AddDays(-50));      // oldest, exact title token
        var contentMatch = ColdChat("Infra notes", Now.AddDays(-10));          // content hit
        var fuzzyTitle = ColdChat("Kubernetes deployment", Now.AddHours(-1));  // newest, fuzzy only

        var snapshots = new Dictionary<Guid, ChatSearchSnapshot>
        {
            [contentMatch.Id] = Snapshot("v-content", "Discussion of the kubernets migration plan.")
        };

        var service = CreateService(
            new AppData { Chats = [exactTitle, contentMatch, fuzzyTitle] },
            snapshots);

        var chatResults = (await service.SearchAsync("kubernets", GlobalSearchExecutionMode.Full))
            .Where(static match => match.Category == GlobalSearchCategory.Chats)
            .ToList();

        // The exact title match wins outright even though it is the oldest of the three.
        Assert.Same(exactTitle, chatResults[0].Item);

        // And the genuine content match still ranks above the fuzzy (newer) title noise.
        var contentIndex = chatResults.FindIndex(match => ReferenceEquals(match.Item, contentMatch));
        var fuzzyIndex = chatResults.FindIndex(match => ReferenceEquals(match.Item, fuzzyTitle));
        Assert.True(contentIndex >= 0, "The genuine content match should be present.");
        Assert.True(
            fuzzyIndex < 0 || contentIndex < fuzzyIndex,
            $"Expected content match (#{contentIndex}) to rank above fuzzy title (#{fuzzyIndex}).");
    }
}
