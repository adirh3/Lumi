using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

// Regression coverage for the idle-transcript memory leak: a pool of idle-cached chat surfaces used
// to keep the full rendered transcript (hundreds of live StrataChatMessage / markdown controls) for
// EVERY cached chat at once. ChatSessionStore now sheds the realized transcript controls of surfaces
// that fall deeper than MaxRealizedIdleSurfaces in the idle LRU, keeping only their lightweight
// view-models. These tests run headless because the stand-in realized hosts are real Avalonia panels.
[Collection("Headless UI")]
public sealed class IdleSurfaceControlSheddingTests
{
    [Fact]
    public async Task DeepIdleCachedSurfaces_ShedRealizedTranscriptControls_WhileActiveAndNewestIdleKeepThem()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var chatA = CreateChat("A");
            var chatB = CreateChat("B");
            var chatC = CreateChat("C");

            var dataStore = new DataStore(new AppData
            {
                Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false },
                Chats = [chatA, chatB, chatC]
            });
            using var registry = new ChatSurfaceRegistry();
            using var sessionStore = new ChatSessionStore(
                dataStore,
                new CopilotService(),
                registry,
                LoadTranscript,
                // Large idle cache so nothing is evicted/disposed: this isolates control shedding
                // (keeping the surface + its view-models alive) from surface eviction.
                maxIdleCachedSurfaces: 8);

            // Acquire, realize, and release A. As the only (newest) idle surface it is spared, so its
            // built transcript stays hot for an instant switch-back.
            var surfaceA = await sessionStore.AcquireChatAsync(chatA);
            SeedRealizedHosts(surfaceA);
            sessionStore.Release(surfaceA);
            Assert.True(HasAnyRealizedHost(surfaceA));

            // Acquire, realize, and release B. A is now a deeper idle surface -> its controls are shed;
            // B is the newest idle surface -> spared.
            var surfaceB = await sessionStore.AcquireChatAsync(chatB);
            SeedRealizedHosts(surfaceB);
            sessionStore.Release(surfaceB);

            Assert.NotSame(surfaceA, surfaceB);
            Assert.False(HasAnyRealizedHost(surfaceA));
            Assert.True(HasAnyRealizedHost(surfaceB));

            // Acquire and realize C but keep it ACTIVE (hosted). The active surface is never idle, so
            // it always retains its realized controls.
            var surfaceC = await sessionStore.AcquireChatAsync(chatC);
            SeedRealizedHosts(surfaceC);
            Assert.True(HasAnyRealizedHost(surfaceC));

            // Releasing C makes it the newest idle surface (spared) and pushes B deeper -> B is shed.
            sessionStore.Release(surfaceC);
            Assert.False(HasAnyRealizedHost(surfaceA));
            Assert.False(HasAnyRealizedHost(surfaceB));
            Assert.True(HasAnyRealizedHost(surfaceC));

            // Switching back to A re-acquires it: the surface (and its view-models) survived, so it is
            // ready to re-realize its transcript from the same turns instead of being rebuilt from
            // scratch or showing a blank transcript.
            var reacquiredA = await sessionStore.AcquireChatAsync(chatA);
            Assert.Same(surfaceA, reacquiredA);
            Assert.NotEmpty(reacquiredA.MountedTranscriptTurns);
        }, CancellationToken.None);
    }

    private static Task LoadTranscript(ChatViewModel surface, Chat chat)
    {
        // Minimal, reliable stand-in for ChatViewModel.LoadChatAsync's message pump: populate the
        // display message list from the chat and build the transcript, so the surface has mounted
        // turns to realize — without depending on the DataStore/session async load pipeline.
        surface.CurrentChat = chat;
        surface.Messages.Clear();
        foreach (var message in chat.Messages)
            surface.Messages.Add(new ChatMessageViewModel(message));
        surface.RebuildTranscript();
        return Task.CompletedTask;
    }

    private static Chat CreateChat(string title)
    {
        var chat = new Chat { Title = title };
        chat.Messages.Add(new ChatMessage { Role = "user", Content = $"question in {title}" });
        chat.Messages.Add(new ChatMessage { Role = "assistant", Content = $"answer in {title}" });
        return chat;
    }

    private static void SeedRealizedHosts(ChatViewModel surface)
    {
        // Stand in for "this surface's mounted turns have realized (built) their controls" — the heavy
        // state the fix must shed for deep-idle surfaces.
        var turns = surface.MountedTranscriptTurns;
        Assert.NotEmpty(turns);
        foreach (var turn in turns)
            turn.RealizedItemsHost = new StackPanel();
    }

    private static bool HasAnyRealizedHost(ChatViewModel surface)
        => surface.MountedTranscriptTurns.Any(turn => turn.RealizedItemsHost is not null);
}
