using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class SuggestionHistoryTests
{
    [Fact]
    public async Task SuggestionHistory_ConcurrentSurfacesShareOneSnapshot()
    {
        var chat = new Chat
        {
            Messages =
            [
                new ChatMessage { Role = "user", Content = "Run code review on these changes" },
                new ChatMessage { Role = "user", Content = "Run code review on these changes" }
            ]
        };
        var store = new DataStore(new AppData { Chats = [chat] });
        var snapshots = new Dictionary<Guid, IReadOnlyList<ChatMessage>> { [chat.Id] = chat.Messages.ToArray() };

        var histories = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => store.GetSuggestionHistoryAsync(snapshots)));

        Assert.Equal(2, histories[0].Count);
        Assert.All(histories, history => Assert.Same(histories[0], history));
    }

    [Fact]
    public async Task SuggestionHistory_DropsRejectedPayloadsWithoutChangingRankedRequests()
    {
        var chat = new Chat();
        var prompts = new[]
        {
            "Commit and push to main",
            "ok, commit and push to main",
            "Run code review on these changes",
            "Run code review on these changes",
            "continue",
            "Background job triggered: scheduled review",
            new string('x', 1_000_000),
        };
        var timestamp = DateTimeOffset.UtcNow;
        chat.Messages = prompts.Select((content, index) => new ChatMessage
        {
            Role = "user",
            Content = content,
            Timestamp = timestamp.AddSeconds(index)
        }).ToList();
        chat.UpdatedAt = chat.Messages[^1].Timestamp;
        var store = new DataStore(new AppData { Chats = [chat] });
        var snapshots = new Dictionary<Guid, IReadOnlyList<ChatMessage>> { [chat.Id] = chat.Messages.ToArray() };
        var raw = await store.GetUserPromptHistoryAsync(DataStore.SuggestionHistoryScanLimit, snapshots);

        var cached = await store.GetSuggestionHistoryAsync(snapshots);

        Assert.Equal(4, cached.Count);
        Assert.Equal(
            SuggestionHistoryRanker.RankFrequentRequests(raw, 8),
            SuggestionHistoryRanker.RankFrequentRequests(cached, 8));
        var excludedId = chat.Messages[1].Id;
        Assert.Equal(
            SuggestionHistoryRanker.RankFrequentRequests(raw.Where(item => item.MessageId != excludedId), 8),
            SuggestionHistoryRanker.RankFrequentRequests(cached.Where(item => item.MessageId != excludedId), 8));
        Assert.All(cached, item => Assert.True(item.Content.Length < 100));
    }

    [Fact]
    public async Task SuggestionHistory_StaleSnapshotReturnsImmediatelyAndRefreshesOnce()
    {
        var chat = new Chat
        {
            Messages = [new ChatMessage { Role = "user", Content = "Run code review on these changes" }]
        };
        var store = new DataStore(new AppData { Chats = [chat] });
        var original = await store.GetSuggestionHistoryAsync(
            new Dictionary<Guid, IReadOnlyList<ChatMessage>> { [chat.Id] = chat.Messages.ToArray() });
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "Commit and push to main" });
        typeof(DataStore).GetField("_suggestionHistoryAt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(store, DateTimeOffset.UtcNow.AddMinutes(-3));
        var snapshots = new Dictionary<Guid, IReadOnlyList<ChatMessage>> { [chat.Id] = chat.Messages.ToArray() };

        var stale = await store.GetSuggestionHistoryAsync(snapshots);
        var refresh = (Task<IReadOnlyList<UserPromptHistoryItem>>)typeof(DataStore)
            .GetField("_suggestionHistoryRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        var fresh = await refresh;

        Assert.Same(original, stale);
        Assert.Equal(2, fresh.Count);
        Assert.Same(fresh, await store.GetSuggestionHistoryAsync(snapshots));
    }

    [Fact]
    public async Task SuggestionHistory_IsIsolatedPerStoreAndClearedWithChats()
    {
        var chat = new Chat
        {
            Messages = [new ChatMessage { Role = "user", Content = "Run code review on these changes" }]
        };
        var store = new DataStore(new AppData { Chats = [chat] });
        var snapshots = new Dictionary<Guid, IReadOnlyList<ChatMessage>> { [chat.Id] = chat.Messages.ToArray() };
        Assert.Single(await store.GetSuggestionHistoryAsync(snapshots));
        var otherStore = new DataStore(new AppData());
        Assert.Empty(await otherStore.GetSuggestionHistoryAsync(snapshots));

        store.Data.Chats.Clear();
        await store.DeleteAllChatFilesAsync();

        Assert.Empty(await store.GetSuggestionHistoryAsync(new Dictionary<Guid, IReadOnlyList<ChatMessage>>()));
    }

    [Fact]
    public async Task SuggestionHistory_ClearedDuringRefresh_DoesNotReturnDeletedHistory()
    {
        var chat = new Chat();
        var store = new DataStore(new AppData { Chats = [chat] });
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        var snapshots = new Dictionary<Guid, IReadOnlyList<ChatMessage>>
        {
            [chat.Id] = new BlockingMessageSnapshot(
                [new ChatMessage { Role = "user", Content = "Run code review on these changes" }],
                reading, resume)
        };

        var history = store.GetSuggestionHistoryAsync(snapshots);
        try
        {
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            store.Data.Chats.Clear();
            await store.DeleteAllChatFilesAsync();
        }
        finally
        {
            resume.Set();
        }

        Assert.Empty(await history.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(await store.GetSuggestionHistoryAsync(new Dictionary<Guid, IReadOnlyList<ChatMessage>>()));
    }

    [Fact]
    public async Task SuggestionHistory_DoesNotRetainRejectedPromptPayloadOrInputSnapshots()
    {
        var chat = new Chat();
        var store = new DataStore(new AppData { Chats = [chat] });
        var rejected = await CacheHistoryWithRejectedPayloadAsync(store, chat);
        await Task.Yield();
        for (var attempt = 0; attempt < 3 && rejected.IsAlive; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            await Task.Yield();
        }

        Assert.False(rejected.IsAlive);
        GC.KeepAlive(store);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> CacheHistoryWithRejectedPayloadAsync(DataStore store, Chat chat)
    {
        var message = new ChatMessage { Role = "user", Content = new string('x', 1_000_000) };
        var rejected = new WeakReference(message.Content);
        await store.GetSuggestionHistoryAsync(new Dictionary<Guid, IReadOnlyList<ChatMessage>>
        {
            [chat.Id] = [message, new ChatMessage { Role = "user", Content = "Run code review on these changes" }]
        });
        return rejected;
    }

    private sealed class BlockingMessageSnapshot(
        IReadOnlyList<ChatMessage> messages,
        TaskCompletionSource reading,
        ManualResetEventSlim resume) : IReadOnlyList<ChatMessage>
    {
        public int Count => messages.Count;
        public ChatMessage this[int index] => messages[index];

        public IEnumerator<ChatMessage> GetEnumerator()
        {
            reading.TrySetResult();
            if (!resume.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The history refresh was not released.");
            return messages.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task GetUserPromptHistory_ReturnsRecentUserMessagesAcrossLoadedChats()
    {
        var older = new DateTimeOffset(2026, 4, 23, 8, 0, 0, TimeSpan.Zero);
        var newer = older.AddHours(1);
        var codeReviewMessageId = Guid.NewGuid();
        var pushMessageId = Guid.NewGuid();
        var chatA = new Chat
        {
            Id = Guid.NewGuid(),
            Messages =
            [
                new ChatMessage
                {
                    Id = codeReviewMessageId,
                    Role = "user",
                    Content = "Run code review",
                    Timestamp = older
                },
                new ChatMessage
                {
                    Id = Guid.NewGuid(),
                    Role = "assistant",
                    Content = "Code review finished.",
                    Timestamp = older.AddMinutes(1)
                }
            ]
        };
        var chatB = new Chat
        {
            Id = Guid.NewGuid(),
            Messages =
            [
                new ChatMessage
                {
                    Id = pushMessageId,
                    Role = "user",
                    Content = "Push to main",
                    Timestamp = newer
                }
            ]
        };

        var store = new DataStore(new AppData { Chats = [chatA, chatB] });
        var snapshots = new Dictionary<Guid, IReadOnlyList<ChatMessage>>
        {
            [chatA.Id] = chatA.Messages.ToList(),
            [chatB.Id] = chatB.Messages.ToList()
        };

        var history = await store.GetUserPromptHistoryAsync(maxMessages: 10, snapshots);

        Assert.Equal(["Push to main", "Run code review"], history.Select(static item => item.Content));
        Assert.Equal([pushMessageId, codeReviewMessageId], history.Select(static item => item.MessageId));
    }
}
