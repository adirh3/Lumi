using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class NavigationHistoryTests
{
    [Fact]
    public async Task Record_IgnoresConsecutiveDuplicateStates()
    {
        var history = new NavigationHistory();
        var chat1 = Guid.NewGuid();
        var chat2 = Guid.NewGuid();

        history.Record(new ChatNavigationState(chat1, null));
        history.Record(new ChatNavigationState(chat1, null));
        history.Record(new ChatNavigationState(chat2, null));

        ChatNavigationState? visited = null;
        var movedBack = await history.TryNavigateAsync(
            -1,
            _ => true,
            entry =>
            {
                visited = Assert.IsType<ChatNavigationState>(entry);
                return Task.FromResult(true);
            });
        var movedBackAgain = await history.TryNavigateAsync(-1, _ => true, _ => Task.FromResult(true));

        Assert.True(movedBack);
        Assert.Equal(new ChatNavigationState(chat1, null), visited);
        Assert.False(movedBackAgain);
    }

    [Fact]
    public async Task Record_TruncatesForwardHistoryAfterBranching()
    {
        var history = new NavigationHistory();
        var chat1 = Guid.NewGuid();
        var chat2 = Guid.NewGuid();
        var chat3 = Guid.NewGuid();
        var chat4 = Guid.NewGuid();

        history.Record(new ChatNavigationState(chat1, null));
        history.Record(new ChatNavigationState(chat2, null));
        history.Record(new ChatNavigationState(chat3, null));

        await history.TryNavigateAsync(-1, _ => true, _ => Task.FromResult(true));

        history.Record(new ChatNavigationState(chat4, null));

        var movedForward = await history.TryNavigateAsync(1, _ => true, _ => Task.FromResult(true));

        ChatNavigationState? visited = null;
        var movedBack = await history.TryNavigateAsync(
            -1,
            _ => true,
            entry =>
            {
                visited = Assert.IsType<ChatNavigationState>(entry);
                return Task.FromResult(true);
            });

        Assert.False(movedForward);
        Assert.True(movedBack);
        Assert.Equal(new ChatNavigationState(chat2, null), visited);
    }

    [Fact]
    public async Task TryNavigateAsync_PrunesChatsThatNoLongerExist()
    {
        var history = new NavigationHistory();
        var chat1 = Guid.NewGuid();
        var chat2 = Guid.NewGuid();
        var chat3 = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        history.Record(new ChatNavigationState(chat1, null));
        history.Record(new ChatNavigationState(chat2, projectId));
        history.Record(new ChatNavigationState(chat3, null));

        ChatNavigationState? visited = null;
        var movedBack = await history.TryNavigateAsync(
            -1,
            entry => entry is not ChatNavigationState chat || chat.ChatId != chat2,
            entry =>
            {
                visited = Assert.IsType<ChatNavigationState>(entry);
                return Task.FromResult(true);
            });

        Assert.True(movedBack);
        Assert.Equal(new ChatNavigationState(chat1, null), visited);
    }

    [Fact]
    public async Task TryNavigateAsync_SetsRestoreFlagOnlyDuringCallback()
    {
        var history = new NavigationHistory();
        var chat1 = Guid.NewGuid();
        var chat2 = Guid.NewGuid();

        history.Record(new ChatNavigationState(chat1, null));
        history.Record(new ChatNavigationState(chat2, null));

        Assert.False(history.IsRestoring);

        var movedBack = await history.TryNavigateAsync(
            -1,
            _ => true,
            entry =>
            {
                Assert.True(history.IsRestoring);
                Assert.Equal(chat1, Assert.IsType<ChatNavigationState>(entry).ChatId);
                history.Record(new SettingsNavigationState(3));
                return Task.FromResult(true);
            });

        Assert.True(movedBack);
        Assert.False(history.IsRestoring);
        Assert.True(await history.TryNavigateAsync(1, _ => true, entry =>
        {
            Assert.Equal(chat2, Assert.IsType<ChatNavigationState>(entry).ChatId);
            return Task.FromResult(true);
        }));
    }

    [Fact]
    public async Task TryNavigateAsync_FollowsMixedPagesAndSkipsDeletedDetails()
    {
        var history = new NavigationHistory();
        var deleted = new ManagementNavigationState(3, Guid.NewGuid(), true);
        var settings = new SettingsNavigationState(3);
        var chat = new ChatNavigationState(Guid.NewGuid(), Guid.NewGuid());
        history.Record(chat);
        history.Record(deleted);
        history.Record(settings);

        Assert.True(await history.TryNavigateAsync(-1, entry => entry != deleted, entry =>
        {
            Assert.Equal(chat, entry);
            return Task.FromResult(true);
        }));
        Assert.True(await history.TryNavigateAsync(1, _ => true, entry =>
        {
            Assert.Equal(settings, entry);
            return Task.FromResult(true);
        }));
    }

    [Fact]
    public async Task TryNavigateAsync_DoesNotAdvanceWhenRestorationFailsOrIsCancelled()
    {
        var history = new NavigationHistory();
        var first = new SettingsNavigationState(0);
        history.Record(first);
        history.Record(new SettingsNavigationState(3));

        Assert.False(await history.TryNavigateAsync(-1, _ => true, _ => Task.FromResult(false)));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            history.TryNavigateAsync(-1, _ => true, _ => throw new OperationCanceledException()));
        Assert.False(history.IsRestoring);
        Assert.True(await history.TryNavigateAsync(-1, _ => true, entry =>
        {
            Assert.Equal(first, entry);
            return Task.FromResult(true);
        }));
    }

    [Fact]
    public async Task TryNavigateAsync_IgnoresOverlappingNavigation()
    {
        var history = new NavigationHistory();
        history.Record(new SettingsNavigationState(0));
        history.Record(new SettingsNavigationState(3));
        var completion = new TaskCompletionSource<bool>();
        var pending = history.TryNavigateAsync(-1, _ => true, _ => completion.Task);

        Assert.False(await history.TryNavigateAsync(-1, _ => true, _ => Task.FromResult(true)));
        completion.SetResult(true);
        Assert.True(await pending);
        Assert.False(history.IsRestoring);
    }

    [Fact]
    public async Task RemoveChat_KeepsOtherPages()
    {
        var history = new NavigationHistory();
        var chatId = Guid.NewGuid();
        var overview = new ManagementNavigationState(3, null, false);
        history.Record(overview);
        history.Record(new ChatNavigationState(chatId, null));
        history.Record(new SettingsNavigationState(0));
        history.RemoveChat(chatId);

        Assert.True(await history.TryNavigateAsync(-1, _ => true, entry =>
        {
            Assert.Equal(overview, entry);
            return Task.FromResult(true);
        }));
    }

    [Fact]
    public async Task TryNavigateAsync_OnlyChecksTheNextDestination()
    {
        var history = new NavigationHistory();
        for (var i = 0; i < 100; i++)
            history.Record(new SettingsNavigationState(i % 8));

        var checks = 0;
        Assert.True(await history.TryNavigateAsync(-1, _ =>
        {
            checks++;
            return true;
        }, _ => Task.FromResult(true)));
        Assert.Equal(1, checks);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task TryNavigateAsync_AnUnavailableCurrentEntryDoesNotSkipItsNeighbors(int direction)
    {
        var history = new NavigationHistory();
        var first = new SettingsNavigationState(0);
        var deleted = new ManagementNavigationState(3, Guid.NewGuid(), true);
        var last = new SettingsNavigationState(3);
        history.Record(first);
        history.Record(deleted);
        history.Record(last);
        Assert.True(await history.TryNavigateAsync(-1, _ => true, _ => Task.FromResult(true)));

        Assert.True(await history.TryNavigateAsync(direction, entry => entry != deleted, entry =>
        {
            Assert.Equal(direction < 0 ? first : last, entry);
            return Task.FromResult(true);
        }));
        Assert.True(await history.TryNavigateAsync(-direction, entry => entry != deleted, entry =>
        {
            Assert.Equal(direction < 0 ? last : first, entry);
            return Task.FromResult(true);
        }));
    }

    [Fact]
    public async Task RemoveChat_DuringRestorationKeepsTheCursorOnTheAppliedEntry()
    {
        var history = new NavigationHistory();
        var removedId = Guid.NewGuid();
        var last = new ChatNavigationState(Guid.NewGuid(), null);
        history.Record(new ChatNavigationState(removedId, null));
        history.Record(new SettingsNavigationState(3));
        history.Record(last);
        var completion = new TaskCompletionSource<bool>();
        var pending = history.TryNavigateAsync(-1, _ => true, _ => completion.Task);

        history.RemoveChat(removedId);
        completion.SetResult(true);
        Assert.True(await pending);
        Assert.True(await history.TryNavigateAsync(1, _ => true, entry =>
        {
            Assert.Same(last, entry);
            return Task.FromResult(true);
        }));
    }

    [Fact]
    public async Task RemoveChat_DuringItsRestorationDoesNotCommitADeletedDestination()
    {
        var history = new NavigationHistory();
        var first = new SettingsNavigationState(0);
        var removedId = Guid.NewGuid();
        history.Record(first);
        history.Record(new ChatNavigationState(removedId, null));
        history.Record(new SettingsNavigationState(3));
        var completion = new TaskCompletionSource<bool>();
        var pending = history.TryNavigateAsync(-1, _ => true, _ => completion.Task);

        history.RemoveChat(removedId);
        completion.SetResult(true);
        Assert.False(await pending);
        Assert.True(await history.TryNavigateAsync(-1, _ => true, entry =>
        {
            Assert.Same(first, entry);
            return Task.FromResult(true);
        }));
    }

    [Fact]
    public async Task Record_KeepsTheLatest200DestinationsAndReleasesEvictedCards()
    {
        var (history, card) = HistoryWithCard();
        Assert.True(card.IsAlive);
        for (var i = 0; i < 200; i++)
            history.Record(new SettingsNavigationState(i % 8));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(card.IsAlive);

        var expected = 198;
        while (await history.TryNavigateAsync(-1, _ => true, entry =>
        {
            Assert.Equal(expected % 8, Assert.IsType<SettingsNavigationState>(entry).SettingsPageIndex);
            expected--;
            return Task.FromResult(true);
        }))
        {
        }
        Assert.Equal(-1, expected);
        GC.KeepAlive(history);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (NavigationHistory History, WeakReference Card) HistoryWithCard()
    {
        var card = DiscoverCard.ForAddedServer(new McpServer { Name = "Past result" }, static _ => { });
        var history = new NavigationHistory();
        history.Record(new DiscoverNavigationState(DiscoverFilter.All, null, card, ""));
        return (history, new WeakReference(card));
    }
}
