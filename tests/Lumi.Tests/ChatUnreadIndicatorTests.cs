using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Regression: the sidebar unread dot (bound to <see cref="Chat.HasUnreadMessages"/>) stopped appearing
/// once chat surfaces became one <see cref="ChatViewModel"/> per chat. Background orchestration,
/// background jobs, and chats the user navigated away from all keep running on their own hidden surface
/// whose <c>CurrentChat</c> IS the target chat, so the old "am I showing a different chat?" test was
/// never true and no chat was ever marked unread. Unread state must follow what is actually on screen.
/// </summary>
[Collection("Headless UI")]
public class ChatUnreadIndicatorTests
{
    [Fact]
    public void IsChatOnScreen_IsFalseForHiddenSurfaceHoldingTheChat()
    {
        var chat = new Chat { Title = "Background" };
        using var surface = CreateSurface(chat);

        Assert.False(surface.IsDisplayedSurface);
        Assert.False(surface.IsChatOnScreen(chat.Id));
    }

    [Fact]
    public void IsChatOnScreen_IsTrueOnlyForTheDisplayedSurfacesCurrentChat()
    {
        var shown = new Chat { Title = "Shown" };
        var other = new Chat { Title = "Other" };
        using var surface = CreateSurface(shown, other);

        surface.AddDisplayHost();

        Assert.True(surface.IsChatOnScreen(shown.Id));
        Assert.False(surface.IsChatOnScreen(other.Id));
    }

    [Fact]
    public void BecomingDisplayed_ClearsUnreadOnTheShownChat()
    {
        var chat = new Chat { Title = "Unread", HasUnreadMessages = true };
        using var surface = CreateSurface(chat);

        surface.AddDisplayHost();

        Assert.False(chat.HasUnreadMessages);
    }

    [Fact]
    public void ASurfaceStaysDisplayedWhileAnotherWindowStillShowsIt()
    {
        var chat = new Chat { Title = "Shared" };
        using var surface = CreateSurface(chat);

        surface.AddDisplayHost();
        surface.AddDisplayHost();
        surface.RemoveDisplayHost();

        Assert.True(surface.IsChatOnScreen(chat.Id));

        surface.RemoveDisplayHost();

        Assert.False(surface.IsChatOnScreen(chat.Id));
    }

    [Fact]
    public void RemoveDisplayHost_DoesNotUnderflowPastHidden()
    {
        var chat = new Chat { Title = "Never shown" };
        using var surface = CreateSurface(chat);

        surface.RemoveDisplayHost();
        surface.AddDisplayHost();

        Assert.True(surface.IsChatOnScreen(chat.Id));
    }

    [Fact]
    public async Task OpeningAChatMarksItsSurfaceDisplayedAndDropsThePreviousOne()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var first = new Chat { Title = "First" };
            var second = new Chat { Title = "Second" };
            using var vm = CreateMainViewModel(first, second);

            Assert.True(await vm.OpenChatByIdAsync(first.Id));
            var firstSurface = vm.ChatVM;
            Assert.True(firstSurface.IsDisplayedSurface);

            Assert.True(await vm.OpenChatByIdAsync(second.Id));

            Assert.NotSame(firstSurface, vm.ChatVM);
            Assert.False(firstSurface.IsDisplayedSurface);
            Assert.True(vm.ChatVM.IsDisplayedSurface);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReopeningAChatWithACachedSurfaceClearsItsUnreadDot()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var background = new Chat { Title = "Background reply" };
            var visible = new Chat { Title = "Visible" };
            using var vm = CreateMainViewModel(background, visible);

            // Realize a surface for the background chat, then navigate away so it keeps that chat
            // loaded while hidden — exactly the state an orchestrated/background run leaves behind.
            Assert.True(await vm.OpenChatByIdAsync(background.Id));
            Assert.True(await vm.OpenChatByIdAsync(visible.Id));

            background.HasUnreadMessages = true;

            // The cached surface still has this chat loaded, so LoadChatAsync is skipped on reopen —
            // showing the surface is what has to clear the dot.
            Assert.True(await vm.OpenChatByIdAsync(background.Id));

            Assert.False(background.HasUnreadMessages);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ABackgroundSurfaceAcquiredForARunDoesNotCountAsOnScreen()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var visible = new Chat { Title = "Visible" };
            var background = new Chat { Title = "Background" };
            var store = CreateDataStore(visible, background);
            using var registry = new ChatSurfaceRegistry();
            using var sessionStore = new ChatSessionStore(store, TestCopilot.Shared, registry);

            var backgroundSurface = await sessionStore.AcquireChatAsync(background);

            Assert.Equal(background.Id, backgroundSurface.CurrentChat?.Id);
            Assert.False(backgroundSurface.IsDisplayedSurface);
            Assert.False(backgroundSurface.IsChatOnScreen(background.Id));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ASecondWindowNavigatingAwayLeavesTheFirstWindowsChatOnScreen()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var shared = new Chat { Title = "Shared" };
            var elsewhere = new Chat { Title = "Elsewhere" };
            var store = CreateDataStore(shared, elsewhere);
            using var registry = new ChatSurfaceRegistry();
            using var sessionStore = new ChatSessionStore(store, TestCopilot.Shared, registry);

            var windowA = CreateMainViewModel(store, registry, sessionStore);
            var windowB = CreateMainViewModel(store, registry, sessionStore);

            Assert.True(await windowA.OpenChatByIdAsync(shared.Id));
            Assert.True(await windowB.OpenChatByIdAsync(shared.Id));

            // Both windows share the one surface per chat.
            Assert.Same(windowA.ChatVM, windowB.ChatVM);

            Assert.True(await windowB.OpenChatByIdAsync(elsewhere.Id));

            Assert.True(windowA.ChatVM.IsChatOnScreen(shared.Id));

            // Closing the second window must not revoke the first window's display host either.
            windowB.Dispose();

            Assert.True(windowA.ChatVM.IsChatOnScreen(shared.Id));
            windowA.Dispose();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ClosingTheOnlyWindowShowingAChatMakesItHiddenAgain()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var chat = new Chat { Title = "Only window" };
            var store = CreateDataStore(chat);
            using var registry = new ChatSurfaceRegistry();
            using var sessionStore = new ChatSessionStore(store, TestCopilot.Shared, registry);

            var window = CreateMainViewModel(store, registry, sessionStore);
            Assert.True(await window.OpenChatByIdAsync(chat.Id));
            var surface = window.ChatVM;
            Assert.True(surface.IsChatOnScreen(chat.Id));

            window.Dispose();

            // The surface stays cached and registered, so it must report itself hidden — otherwise a
            // later background run resolved onto it would never mark the chat unread.
            Assert.False(surface.IsChatOnScreen(chat.Id));
        }, CancellationToken.None);
    }

    private static DataStore CreateDataStore(params Chat[] chats)
    {
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            },
            Chats = [.. chats]
        };
        return new DataStore(data);
    }

    private static MainViewModel CreateMainViewModel(params Chat[] chats)
        => new(
            CreateDataStore(chats),
            TestCopilot.Shared,
            new UpdateService(),
            startBackgroundJobs: false);

    private static MainViewModel CreateMainViewModel(
        DataStore store,
        ChatSurfaceRegistry registry,
        ChatSessionStore sessionStore)
        => new(
            store,
            TestCopilot.Shared,
            new UpdateService(),
            startBackgroundJobs: false,
            chatSurfaceRegistry: registry,
            chatSessionStore: sessionStore);

    private static ChatViewModel CreateSurface(params Chat[] chats)
        => new(CreateDataStore(chats), TestCopilot.Shared)
        {
            CurrentChat = chats.FirstOrDefault()
        };
}
