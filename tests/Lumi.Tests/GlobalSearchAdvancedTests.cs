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
/// Integration coverage for the time-aware, full-coverage, recency-weighted search overhaul:
/// temporal filtering/browse, content coverage beyond the recent-chat cap, background warming,
/// on-disk persistence, staleness invalidation, recency ranking, and scale.
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
        IReadOnlyDictionary<Guid, ChatSearchSnapshot>? snapshots = null,
        Action<Guid>? onProviderCall = null,
        Func<Guid, DateTimeOffset?>? timestampProvider = null)
    {
        return new GlobalSearchService(
            () => data,
            chat =>
            {
                onProviderCall?.Invoke(chat.Id);
                if (snapshots is not null && snapshots.TryGetValue(chat.Id, out var snapshot))
                    return snapshot;
                return new ChatSearchSnapshot { Version = $"empty:{chat.Id}" };
            },
            () => Now,
            chatFileTimestampProvider: timestampProvider);
    }

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
    public async Task Search_InteractiveMode_DoesNotCoverOldContent_ButFullDoes()
    {
        var chats = new List<Chat>();
        var snapshots = new Dictionary<Guid, ChatSearchSnapshot>();
        for (var i = 0; i < 40; i++)
        {
            var chat = ColdChat($"Conversation {i}", Now.AddDays(-i));
            chats.Add(chat);
            snapshots[chat.Id] = Snapshot($"v-{i}", i == 39 ? "rare zphyxmarker token" : "ordinary text");
        }

        var service = CreateService(new AppData { Chats = chats }, snapshots);

        // Interactive only builds the 16 most-recent cold chats, so the oldest is not covered yet.
        var interactive = await service.SearchAsync("zphyxmarker", GlobalSearchExecutionMode.Interactive);
        Assert.Empty(interactive);

        // Full covers the entire history.
        var full = await service.SearchAsync("zphyxmarker", GlobalSearchExecutionMode.Full);
        Assert.Same(chats[39], Assert.Single(full).Item);
    }

    [Fact]
    public async Task Warm_ThenFastMode_FindsOldChatByContentWithoutFurtherReads()
    {
        var chats = new List<Chat>();
        var snapshots = new Dictionary<Guid, ChatSearchSnapshot>();
        for (var i = 0; i < 40; i++)
        {
            var chat = ColdChat($"Conversation {i}", Now.AddDays(-i));
            chats.Add(chat);
            snapshots[chat.Id] = Snapshot($"v-{i}", i == 39 ? "the gribbletoken appears here" : "nothing special");
        }

        var providerCalls = 0;
        var service = CreateService(new AppData { Chats = chats }, snapshots, _ => providerCalls++);

        await service.WarmChatContentAsync();
        Assert.Equal(40, service.IndexedChatCount);
        var callsAfterWarm = providerCalls;

        // Fast is the cheapest live phase; after warming it must still find old content with no new reads.
        var fast = await service.SearchAsync("gribbletoken", GlobalSearchExecutionMode.Fast);

        Assert.Same(chats[39], Assert.Single(fast).Item);
        Assert.Equal(callsAfterWarm, providerCalls);
    }

    [Fact]
    public async Task ContentIndex_PersistsAcrossServiceInstances()
    {
        var chats = new List<Chat>();
        var snapshots = new Dictionary<Guid, ChatSearchSnapshot>();
        for (var i = 0; i < 10; i++)
        {
            var chat = ColdChat($"Conversation {i}", Now.AddDays(-i));
            chats.Add(chat);
            snapshots[chat.Id] = Snapshot($"v-{i}", i == 9 ? "persistedneedle lives here" : "filler content");
        }

        var indexPath = Path.Combine(Path.GetTempPath(), $"lumi-index-{Guid.NewGuid():N}.bin");
        try
        {
            var first = CreateService(new AppData { Chats = chats }, snapshots);
            await first.WarmChatContentAsync();
            first.SaveChatContentIndex(indexPath);

            // Second instance whose provider must never be touched — proves the load served the match.
            var secondCalls = 0;
            var second = CreateService(new AppData { Chats = chats }, snapshots, _ => secondCalls++);
            var loaded = second.LoadChatContentIndex(indexPath);
            Assert.Equal(10, loaded);

            var fast = await second.SearchAsync("persistedneedle", GlobalSearchExecutionMode.Fast);

            Assert.Same(chats[9], Assert.Single(fast).Item);
            Assert.Equal(0, secondCalls);
        }
        finally
        {
            if (File.Exists(indexPath))
                File.Delete(indexPath);
        }
    }

    [Fact]
    public async Task InvalidateChatContent_RefreshesStaleEntryOnNextFullPass()
    {
        var chat = ColdChat("Weekly sync", Now.AddHours(-2));
        var snapshots = new Dictionary<Guid, ChatSearchSnapshot>
        {
            [chat.Id] = Snapshot("v1", "old alpha content")
        };

        var service = CreateService(new AppData { Chats = [chat] }, snapshots);

        await service.WarmChatContentAsync();
        Assert.NotEmpty(await service.SearchAsync("alpha", GlobalSearchExecutionMode.Fast));

        // Content changes on disk; the cached entry is now stale.
        snapshots[chat.Id] = Snapshot("v2", "new betacontent body");

        // Without invalidation the stale entry persists.
        Assert.Empty(await service.SearchAsync("betacontent", GlobalSearchExecutionMode.Fast));

        service.InvalidateChatContent(chat.Id);

        // A full pass rebuilds the entry and now matches the new content.
        var full = await service.SearchAsync("betacontent", GlobalSearchExecutionMode.Full);
        Assert.Same(chat, Assert.Single(full).Item);
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

        await service.WarmChatContentAsync();
        Assert.Equal(1200, service.IndexedChatCount);

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

    [Fact]
    public async Task Warm_RevalidatesStaleEntry_WhenFileTimestampChanges()
    {
        var chat = ColdChat("Weekly sync", Now.AddHours(-2));
        var snapshots = new Dictionary<Guid, ChatSearchSnapshot>
        {
            [chat.Id] = Snapshot("v1", "old alphaword content")
        };
        var timestamps = new Dictionary<Guid, DateTimeOffset> { [chat.Id] = Now.AddHours(-2) };

        var service = CreateService(
            new AppData { Chats = [chat] },
            snapshots,
            timestampProvider: id => timestamps.TryGetValue(id, out var ts) ? ts : null);

        await service.WarmChatContentAsync();
        Assert.NotEmpty(await service.SearchAsync("alphaword", GlobalSearchExecutionMode.Fast));

        // The file changes on disk (another instance edited it) AND its last-write time advances.
        snapshots[chat.Id] = Snapshot("v2", "new betaword body");
        timestamps[chat.Id] = Now.AddMinutes(-1);

        // A fresh warm must notice the newer timestamp and rebuild the stale entry.
        await service.WarmChatContentAsync();

        Assert.NotEmpty(await service.SearchAsync("betaword", GlobalSearchExecutionMode.Fast));
        Assert.Empty(await service.SearchAsync("alphaword", GlobalSearchExecutionMode.Fast));
    }

    [Fact]
    public async Task Warm_DoesNotReindex_WhenFileTimestampUnchanged()
    {
        var chat = ColdChat("Weekly sync", Now.AddHours(-2));
        var snapshots = new Dictionary<Guid, ChatSearchSnapshot>
        {
            [chat.Id] = Snapshot("v1", "old alphaword content")
        };
        var timestamps = new Dictionary<Guid, DateTimeOffset> { [chat.Id] = Now.AddHours(-2) };

        var providerCalls = 0;
        var service = CreateService(
            new AppData { Chats = [chat] },
            snapshots,
            onProviderCall: _ => providerCalls++,
            timestampProvider: id => timestamps.TryGetValue(id, out var ts) ? ts : null);

        await service.WarmChatContentAsync();
        var callsAfterFirstWarm = providerCalls;

        // Content changes but the timestamp does NOT — an unchanged file must not be re-read.
        snapshots[chat.Id] = Snapshot("v2", "new betaword body");
        await service.WarmChatContentAsync();

        Assert.Equal(callsAfterFirstWarm, providerCalls);
        Assert.Empty(await service.SearchAsync("betaword", GlobalSearchExecutionMode.Fast));
    }

    [Fact]
    public void LoadChatContentIndex_OnCorruptFile_ReturnsZeroWithoutThrowing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lumi-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var service = CreateService(new AppData());

            // Random garbage with no valid header.
            var garbage = Path.Combine(dir, "garbage.bin");
            File.WriteAllBytes(garbage, [0xFF, 0x00, 0x12, 0x34, 0x56, 0x78, 0x9A]);
            Assert.Equal(0, service.LoadChatContentIndex(garbage));

            // Valid magic but an absurd entry count with no entries following it.
            var truncated = Path.Combine(dir, "truncated.bin");
            using (var writer = new BinaryWriter(File.Create(truncated), Encoding.UTF8))
            {
                writer.Write("LCI3");
                writer.Write(int.MaxValue);
            }
            Assert.Equal(0, service.LoadChatContentIndex(truncated));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
