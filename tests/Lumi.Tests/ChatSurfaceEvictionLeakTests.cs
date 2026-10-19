using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

// Ground-truth memory regression coverage for the "Lumi grows huge over a long session" report.
//
// Every distinct chat the user opens gets its own ChatViewModel *surface* from ChatSessionStore, and
// each surface retains that chat's ENTIRE view-model graph (Messages + TranscriptTurns +
// ChatMessageViewModel per message) for as long as the surface is alive. The idle pool is bounded
// (DefaultMaxIdleCachedSurfaces), so opening many chats over hours MUST evict + dispose the oldest
// surfaces and let the GC reclaim their whole transcript graph. If an evicted+disposed surface stays
// pinned by any app-lifetime reference (Copilot events, the DataStore, the pool, the registry, a
// dispatcher timer closure, ...), then every chat opened over a 37-hour session accumulates forever —
// which is exactly the unbounded Gen2 growth observed on the real instance.
//
// These tests keep the app-lifetime singletons (CopilotService, DataStore, ChatSessionStore,
// ChatSurfaceRegistry) ALIVE across the GC check, so a leak through any of them is caught rather than
// masked by tearing the pool down first. They run headless so the real transcript graph is built.
[Collection("Headless UI")]
public sealed class ChatSurfaceEvictionLeakTests
{
    [Fact]
    public async Task EvictedPersistedChat_UnloadsMessageModels_AndReloadsUnchangedHistory()
    {
        using var session = HeadlessTestSession.Start();
        DataStore dataStore = null!;
        Chat chat = null!;
        ChatSurfaceRegistry registry = null!;
        ChatSessionStore store = null!;
        WeakReference[] weakMessages = [];
        Guid[] expectedIds = [];
        string[] expectedContent = [];
        await session.Dispatch(async () =>
        {
            dataStore = new DataStore();
            dataStore.Data.Settings.AutoSaveChats = true;
            chat = CreateChat("persisted eviction", messageCount: 60);
            chat.Messages[0].Content = new string('x', 200_000);
            expectedIds = chat.Messages.Select(message => message.Id).ToArray();
            expectedContent = chat.Messages.Select(message => message.Content).ToArray();
            dataStore.Data.Chats.Add(chat);
            await dataStore.SaveChatAsync(chat);
            registry = new ChatSurfaceRegistry();
            store = new ChatSessionStore(
                dataStore, TestCopilot.Shared, registry, LoadPersistedTranscript,
                maxIdleCachedSurfaces: 0);

            weakMessages = await OpenAndEvictPersistedChatAsync(store, chat);
            await WaitForMessageUnloadAsync(chat);
            Assert.Equal(expectedIds.Length, chat.MessageCount);
            Assert.Equal(0, chat.Messages.Capacity);
        }, CancellationToken.None);

        await DrainUntilCollectedAsync(session, () => weakMessages.All(reference => !reference.IsAlive));
        Assert.All(weakMessages, reference => Assert.False(reference.IsAlive));
        await session.Dispatch(async () =>
        {
            var reloaded = await store.AcquireChatAsync(chat);
            Assert.Equal(expectedIds, chat.Messages.Select(message => message.Id));
            Assert.Equal(expectedContent, chat.Messages.Select(message => message.Content));
            Assert.Equal(expectedIds.Length, reloaded.Messages.Count);
            store.Dispose();
            registry.Dispose();
        }, CancellationToken.None);
        GC.KeepAlive(dataStore);
    }

    [Fact]
    public async Task EvictedChat_WithAutoSaveDisabled_KeepsUnsavedModels()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var dataStore = new DataStore();
            dataStore.Data.Settings.AutoSaveChats = false;
            var chat = CreateChat("unsaved eviction", messageCount: 2);
            dataStore.Data.Chats.Add(chat);
            using var registry = new ChatSurfaceRegistry();
            using var store = new ChatSessionStore(
                dataStore, TestCopilot.Shared, registry, LoadTranscript,
                maxIdleCachedSurfaces: 0);

            var surface = await store.AcquireChatAsync(chat);
            store.Release(surface);
            await Task.Yield();

            Assert.Equal(2, chat.Messages.Count);
            Assert.False(dataStore.HasStoredMessages(chat.Id));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task HostedOrActiveChat_IsNotUnloadedWhenIdleCacheIsDisabled()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var dataStore = new DataStore();
            dataStore.Data.Settings.AutoSaveChats = true;
            var chat = CreateChat("active eviction", messageCount: 2);
            dataStore.Data.Chats.Add(chat);
            await dataStore.SaveChatAsync(chat);
            using var registry = new ChatSurfaceRegistry();
            using var store = new ChatSessionStore(
                dataStore, TestCopilot.Shared, registry, LoadTranscript,
                maxIdleCachedSurfaces: 0);

            var surface = await store.AcquireChatAsync(chat);
            store.Retain(surface);
            store.Release(surface);
            Assert.Equal(2, chat.Messages.Count);
            Assert.Contains(surface, store.SnapshotSurfaces());

            var runtime = new ChatRuntimeState { Chat = chat, HasPendingBackgroundWork = true };
            var runtimes = (Dictionary<Guid, ChatRuntimeState>)typeof(ChatViewModel)
                .GetField("_runtimeStates", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(surface)!;
            runtimes[chat.Id] = runtime;
            store.Release(surface);
            await Task.Yield();
            Assert.Equal(2, chat.Messages.Count);
            Assert.Contains(surface, store.SnapshotSurfaces());
            runtime.HasPendingBackgroundWork = false;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EvictedChat_FailedSave_KeepsModelsAndExistingFile()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var dataStore = new DataStore();
            dataStore.Data.Settings.AutoSaveChats = true;
            var chat = CreateChat("failed eviction", messageCount: 2);
            dataStore.Data.Chats.Add(chat);
            await dataStore.SaveChatAsync(chat);
            var path = Path.Combine(DataStore.ChatsDir, $"{chat.Id}.json");
            var originalFile = await File.ReadAllBytesAsync(path);
            chat.Messages[0].Content = "Unsaved changes must survive the failed eviction save.";
            using var fileLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var registry = new ChatSurfaceRegistry();
            using var store = new ChatSessionStore(
                dataStore, TestCopilot.Shared, registry, LoadTranscript,
                maxIdleCachedSurfaces: 0);

            var surface = await store.AcquireChatAsync(chat);
            store.Release(surface);
            await Task.Yield();

            Assert.Equal(2, chat.Messages.Count);
            Assert.Equal("Unsaved changes must survive the failed eviction save.", chat.Messages[0].Content);
            Assert.Equal(originalFile, await File.ReadAllBytesAsync(path));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EvictedChat_ReacquiredDuringSave_DoesNotUnloadNewOwner()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var dataStore = new DataStore();
            dataStore.Data.Settings.AutoSaveChats = true;
            var chat = CreateChat("reacquired eviction", messageCount: 2);
            dataStore.Data.Chats.Add(chat);
            await dataStore.SaveChatAsync(chat);
            using var registry = new ChatSurfaceRegistry();
            using var store = new ChatSessionStore(
                dataStore, TestCopilot.Shared, registry, LoadTranscript,
                maxIdleCachedSurfaces: 0);
            var writeLock = (SemaphoreSlim)typeof(DataStore)
                .GetField("_writeLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(dataStore)!;
            var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            dataStore.ChatContentChanged += id =>
            {
                if (id == chat.Id)
                    saved.TrySetResult();
            };

            var surface = await store.AcquireChatAsync(chat);
            await writeLock.WaitAsync();
            ChatViewModel replacement;
            try
            {
                store.Release(surface);
                replacement = await store.AcquireChatAsync(chat);
                chat.Messages[0].Content = "Edited by the new owner while the old save was pending.";
            }
            finally
            {
                writeLock.Release();
            }

            await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                static () => { }, Avalonia.Threading.DispatcherPriority.Background);
            Assert.Equal(2, chat.Messages.Count);
            Assert.Equal("Edited by the new owner while the old save was pending.", chat.Messages[0].Content);
            Assert.Contains(replacement, store.SnapshotSurfaces());
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EvictedChat_ReacquiredAndEvictedDuringSave_PreservesNewChanges()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var dataStore = new DataStore();
            dataStore.Data.Settings.AutoSaveChats = true;
            var chat = CreateChat("reopened and evicted", messageCount: 2);
            dataStore.Data.Chats.Add(chat);
            await dataStore.SaveChatAsync(chat);
            using var registry = new ChatSurfaceRegistry();
            using var store = new ChatSessionStore(
                dataStore, TestCopilot.Shared, registry, LoadTranscript,
                maxIdleCachedSurfaces: 0);
            var writeLock = (SemaphoreSlim)typeof(DataStore)
                .GetField("_writeLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(dataStore)!;

            var firstOwner = await store.AcquireChatAsync(chat);
            await writeLock.WaitAsync();
            try
            {
                store.Release(firstOwner);
                var newOwner = await store.AcquireChatAsync(chat);
                chat.Messages[0].Content = "Changes from the reopened owner must be persisted.";
                store.Release(newOwner);
            }
            finally
            {
                writeLock.Release();
            }

            var release = (Task)typeof(ChatSessionStore)
                .GetField("_evictedMessagesRelease", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(store)!;
            await release.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(chat.Messages);
            var history = await dataStore.ReadChatMessagesAsync(chat);
            Assert.Equal(2, history.Count);
            Assert.Equal("Changes from the reopened owner must be persisted.", history[0].Content);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EvictedChats_SaveOneHistoryAtATime_AndSkipReacquiredQueuedChat()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var dataStore = new DataStore();
            dataStore.Data.Settings.AutoSaveChats = true;
            var first = CreateChat("first queued eviction", messageCount: 2);
            var second = CreateChat("second queued eviction", messageCount: 2);
            dataStore.Data.Chats.AddRange([first, second]);
            await dataStore.SaveChatAsync(first);
            await dataStore.SaveChatAsync(second);
            var secondSaveCount = 0;
            dataStore.ChatContentChanged += id =>
            {
                if (id == second.Id)
                    Interlocked.Increment(ref secondSaveCount);
            };
            using var registry = new ChatSurfaceRegistry();
            using var store = new ChatSessionStore(
                dataStore, TestCopilot.Shared, registry, LoadTranscript,
                maxIdleCachedSurfaces: 0);
            var writeLock = (SemaphoreSlim)typeof(DataStore)
                .GetField("_writeLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(dataStore)!;

            await writeLock.WaitAsync();
            try
            {
                store.Release(await store.AcquireChatAsync(first));
                store.Release(await store.AcquireChatAsync(second));
                await store.AcquireChatAsync(second);
                second.Messages[0].Content = "New owner; the queued eviction must not save or unload it.";
            }
            finally
            {
                writeLock.Release();
            }

            var release = (Task)typeof(ChatSessionStore)
                .GetField("_evictedMessagesRelease", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(store)!;
            await release.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(first.Messages);
            Assert.Equal(0, secondSaveCount);
            Assert.Equal(2, second.Messages.Count);
            Assert.Equal("New owner; the queued eviction must not save or unload it.", second.Messages[0].Content);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EvictedChatSurface_AndItsTranscriptGraph_AreGarbageCollected()
    {
        using var session = HeadlessTestSession.Start();

        // App-lifetime singletons: kept rooted through the GC assertion (as they are for the app's life)
        // so any leak that pins an evicted surface through them is exposed instead of hidden.
        CopilotService copilot = null!;
        DataStore dataStore = null!;
        ChatSurfaceRegistry registry = null!;
        ChatSessionStore store = null!;
        WeakReference weakSurface = null!;
        List<WeakReference> weakMessageVms = null!;
        int seededMessageCount = 0;

        await session.Dispatch(async () =>
        {
            copilot = TestCopilot.Shared;
            var chats = Enumerable.Range(0, 6).Select(i => CreateChat($"chat{i}", messageCount: 60)).ToList();
            seededMessageCount = chats[0].Messages.Count;
            dataStore = new DataStore(new AppData
            {
                Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false },
                Chats = [.. chats]
            });
            registry = new ChatSurfaceRegistry();

            // maxIdleCachedSurfaces:1 -> acquiring/releasing later chats evicts + disposes the oldest.
            store = new ChatSessionStore(dataStore, copilot, registry, LoadTranscript, maxIdleCachedSurfaces: 1);

            (weakSurface, weakMessageVms) = await OpenChatEvictItAndWeakReferenceGraph(store, chats);
        }, CancellationToken.None);

        await DrainUntilCollectedAsync(
            session,
            () => !weakSurface.IsAlive && weakMessageVms.All(static w => !w.IsAlive));

        Assert.False(
            weakSurface.IsAlive,
            "Evicted + disposed chat surface is still rooted — every chat opened over a session leaks its ChatViewModel.");

        var aliveVms = weakMessageVms.Count(w => w.IsAlive);
        Assert.True(
            aliveVms == 0,
            $"{aliveVms}/{seededMessageCount} ChatMessageViewModels of an evicted chat survived GC — the transcript graph leaks per opened chat.");

        // Keep the singletons rooted until AFTER the assertions so a leak through them is not masked.
        GC.KeepAlive(copilot);
        GC.KeepAlive(dataStore);
        GC.KeepAlive(registry);
        GC.KeepAlive(store);
        store.Dispose();
        registry.Dispose();
    }

    // Simulates a long session: open MANY distinct chats one after another through the pool, exactly
    // like a user browsing their history for hours. The number of LIVE ChatViewModel surfaces must stay
    // bounded by the pool cap (+ the active surface), not grow with the number of chats ever opened.
    [Fact]
    public async Task OpeningManyChatsInSequence_KeepsLiveSurfaceCountBounded()
    {
        using var session = HeadlessTestSession.Start();

        CopilotService copilot = null!;
        DataStore dataStore = null!;
        ChatSurfaceRegistry registry = null!;
        ChatSessionStore store = null!;
        List<WeakReference> allSurfaces = null!;
        const int idleCap = 3;
        const int chatsOpened = 40;

        await session.Dispatch(async () =>
        {
            copilot = TestCopilot.Shared;
            var chats = Enumerable.Range(0, chatsOpened).Select(i => CreateChat($"chat{i}", messageCount: 25)).ToList();
            dataStore = new DataStore(new AppData
            {
                Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false },
                Chats = [.. chats]
            });
            registry = new ChatSurfaceRegistry();
            store = new ChatSessionStore(dataStore, copilot, registry, LoadTranscript, maxIdleCachedSurfaces: idleCap);

            allSurfaces = await OpenChatsInSequence(store, chats);
        }, CancellationToken.None);

        await DrainUntilCollectedAsync(
            session,
            () => allSurfaces.Count(static w => w.IsAlive) <= idleCap + 1);

        var liveSurfaces = allSurfaces.Count(w => w.IsAlive);

        // The active surface plus at most `idleCap` idle-cached surfaces may survive; everything else
        // opened during the session must have been evicted, disposed, and collected.
        Assert.True(
            liveSurfaces <= idleCap + 1,
            $"{liveSurfaces} ChatViewModel surfaces are still alive after opening {chatsOpened} chats " +
            $"(pool cap {idleCap} + 1 active). Opened chats are accumulating instead of being reclaimed.");

        GC.KeepAlive(copilot);
        GC.KeepAlive(dataStore);
        GC.KeepAlive(registry);
        GC.KeepAlive(store);
        store.Dispose();
        registry.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference surface, List<WeakReference> messageVms)> OpenChatEvictItAndWeakReferenceGraph(
        ChatSessionStore store,
        List<Chat> chats)
    {
        // Open the target chat, build its full transcript graph, and weak-reference the surface + its VMs.
        var target = await store.AcquireChatAsync(chats[0]);
        var weakSurface = new WeakReference(target);
        var weakMessageVms = target.Messages.Select(m => new WeakReference(m)).ToList();
        store.Release(target); // -> idle-cached (single slot)
        target = null;

        // Open/close several more chats: overflowing the one idle slot evicts + disposes the first chat's
        // surface through the real pool lifecycle (TrimIdleCache -> UntrackSurface -> ChatViewModel.Dispose).
        for (var i = 1; i < chats.Count; i++)
        {
            var s = await store.AcquireChatAsync(chats[i]);
            store.Release(s);
        }

        return (weakSurface, weakMessageVms);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<List<WeakReference>> OpenChatsInSequence(ChatSessionStore store, List<Chat> chats)
    {
        var weakSurfaces = new List<WeakReference>(chats.Count);
        foreach (var chat in chats)
        {
            var surface = await store.AcquireChatAsync(chat);
            weakSurfaces.Add(new WeakReference(surface));
            store.Release(surface);
        }

        return weakSurfaces;
    }

    private static void ForceFullGc()
    {
        for (var i = 0; i < 6; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    // A forced blocking+compacting GC deterministically reclaims every *unrooted* object, so these
    // assertions pass reliably in isolation. Under a full Debug-build suite run, though, a just-detached
    // surface can stay momentarily reachable through a conservative stack slot or an in-flight
    // test/dispatcher frame left over from the code that just ran on this thread — a transient root that
    // clears the moment the stack unwinds and the UI queue drains. Re-check across a few attempts,
    // flushing the dispatcher and yielding off the creating thread between them. A genuine ownership leak
    // (a static root, an undisposed event handler, a live pool/registry entry) stays rooted through every
    // attempt, never satisfies `collected`, and still fails the caller's assertion below.
    private static async Task DrainUntilCollectedAsync(HeadlessTestSession session, Func<bool> collected)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            ForceFullGc();
            if (collected())
                return;

            await session.Dispatch(static () => { }, CancellationToken.None);
            await Task.Yield();
            await Task.Delay(25);
        }

        ForceFullGc();
    }

    // Faithful stand-in for ChatViewModel.LoadChatAsync's display pump: set the current chat, populate
    // the display message list, and build the real transcript (turns + items) — without depending on the
    // DataStore/session async load pipeline. Mirrors IdleSurfaceControlSheddingTests.LoadTranscript.
    private static Task LoadTranscript(ChatViewModel surface, Chat chat)
    {
        surface.CurrentChat = chat;
        surface.Messages.Clear();
        foreach (var message in chat.Messages)
            surface.Messages.Add(new ChatMessageViewModel(message));
        surface.RebuildTranscript();
        return Task.CompletedTask;
    }

    private static Task LoadPersistedTranscript(ChatViewModel surface, Chat chat) => surface.LoadChatAsync(chat);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> OpenAndEvictPersistedChatAsync(ChatSessionStore store, Chat chat)
    {
        var surface = await store.AcquireChatAsync(chat);
        var references = chat.Messages.Select(message => new WeakReference(message)).ToArray();
        store.Release(surface);
        return references;
    }

    private static async Task WaitForMessageUnloadAsync(Chat chat)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (chat.Messages.Count > 0)
            await Task.Delay(10, timeout.Token);
    }

    private static Chat CreateChat(string title, int messageCount)
    {
        var chat = new Chat { Title = title };
        for (var i = 0; i < messageCount; i++)
        {
            var role = i % 2 == 0 ? "user" : "assistant";
            chat.Messages.Add(new ChatMessage { Role = role, Content = $"{title} message {i} body text" });
        }

        return chat;
    }
}
