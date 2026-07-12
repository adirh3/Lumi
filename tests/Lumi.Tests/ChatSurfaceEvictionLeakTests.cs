using System;
using System.Collections.Generic;
using System.Linq;
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
            copilot = new CopilotService();
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
            copilot = new CopilotService();
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
