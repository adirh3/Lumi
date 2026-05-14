using System.Reflection;
using System.Threading;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class DataStoreChatMessageMergeTests
{
    [Fact]
    public void MergeLoadedChatMessages_RestoresPersistedHistoryBeforeLiveMessages()
    {
        var persistedUser = new ChatMessage { Role = "user", Content = "persisted question" };
        var persistedAssistant = new ChatMessage { Role = "assistant", Content = "persisted answer" };
        var liveAssistant = new ChatMessage { Role = "assistant", Content = "live answer" };
        var chat = new Chat { Title = "partial" };
        chat.Messages.Add(liveAssistant);

        var changed = DataStore.MergeLoadedChatMessages(chat, [persistedUser, persistedAssistant]);

        Assert.True(changed);
        Assert.Equal([persistedUser, persistedAssistant, liveAssistant], chat.Messages);
    }

    [Fact]
    public void MergeLoadedChatMessages_PreservesLiveVersionForPersistedMessageIds()
    {
        var messageId = Guid.NewGuid();
        var persisted = new ChatMessage { Id = messageId, Role = "assistant", Content = "old content" };
        var live = new ChatMessage { Id = messageId, Role = "assistant", Content = "new content" };
        var chat = new Chat { Title = "live" };
        chat.Messages.Add(live);

        var changed = DataStore.MergeLoadedChatMessages(chat, [persisted]);

        Assert.False(changed);
        Assert.Single(chat.Messages);
        Assert.Same(live, chat.Messages[0]);
        Assert.Equal("new content", chat.Messages[0].Content);
    }

    [Fact]
    public async Task EvictChatMessagesAsync_WaitsAsynchronouslyForChatMessageLock()
    {
        var chat = new Chat { Title = "cached" };
        chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "cached answer" });
        var store = new DataStore(new AppData { Chats = [chat] });
        using var loadLockLease = InvokePrivate<IDisposable>(store, "RentChatLoadLock", chat.Id);
        var loadLock = GetLeaseSemaphore(loadLockLease);
        var releasedLock = false;

        await loadLock.WaitAsync();
        try
        {
            var eviction = store.EvictChatMessagesAsync(chat);

            Assert.False(eviction.IsCompleted);
            loadLock.Release();
            releasedLock = true;

            var evicted = await eviction.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.True(evicted);
            Assert.Empty(chat.Messages);
        }
        finally
        {
            if (!releasedLock)
                loadLock.Release();
        }
    }

    [Fact]
    public async Task EvictChatMessagesAsync_RechecksPredicateAfterWaitingForChatMessageLock()
    {
        var chat = new Chat { Title = "cached" };
        chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "cached answer" });
        var store = new DataStore(new AppData { Chats = [chat] });
        using var loadLockLease = InvokePrivate<IDisposable>(store, "RentChatLoadLock", chat.Id);
        var loadLock = GetLeaseSemaphore(loadLockLease);
        var releasedLock = false;

        await loadLock.WaitAsync();
        try
        {
            var shouldEvict = true;
            var eviction = store.EvictChatMessagesAsync(chat, () => shouldEvict);

            shouldEvict = false;
            Assert.False(eviction.IsCompleted);
            loadLock.Release();
            releasedLock = true;

            var evicted = await eviction.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.False(evicted);
            Assert.Single(chat.Messages);
            Assert.Equal("cached answer", chat.Messages[0].Content);
        }
        finally
        {
            if (!releasedLock)
                loadLock.Release();
        }
    }

    [Fact]
    public void RemoveChatLoadLock_DisposesOnlyAfterActiveLeaseReleases()
    {
        var chatId = Guid.NewGuid();
        var store = new DataStore(new AppData());
        var loadLockLease = InvokePrivate<IDisposable>(store, "RentChatLoadLock", chatId);
        var loadLock = GetLeaseSemaphore(loadLockLease);

        store.RemoveChatLoadLock(chatId);

        Assert.True(loadLock.Wait(0));
        loadLock.Release();

        loadLockLease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => loadLock.Wait(0));
    }

    private static T InvokePrivate<T>(object instance, string name, params object[] args)
        => (T)(instance.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(instance, args)
            ?? throw new InvalidOperationException($"Method {name} was not found."));

    private static SemaphoreSlim GetLeaseSemaphore(object lease)
        => (SemaphoreSlim)(lease.GetType()
            .GetProperty("Semaphore", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(lease)
            ?? throw new InvalidOperationException("Semaphore property was not found."));
}
