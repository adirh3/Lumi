using System.Reflection;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
[Trait("Category", "CoreQA")]
public sealed class BackgroundJobBrowserTests
{
    [Theory]
    [InlineData(ChatLifecycleEventTypes.Idle)]
    [InlineData(ChatLifecycleEventTypes.Error)]
    [InlineData(ChatLifecycleEventTypes.Aborted)]
    public async Task TerminalSessionDisposesJobBrowserAndRestoresInteractiveTabs(string eventType)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var chat = new Chat();
            using var vm = CreateViewModel(chat);
            var interactive = vm.GetOrCreateBrowserService(chat.Id);
            var interactiveTab = interactive.ActiveTabId;
            vm.IsBrowserOpen = true;
            Guid? shownChat = null;
            vm.BrowserShowRequested += id => shownChat = id;
            vm.BeginChatLifecycleTurn(chat);
            vm.BeginJobBrowserSession(chat.Id);
            var job = vm.GetOrCreateBrowserService(chat.Id);

            Assert.NotSame(interactive, job);
            Assert.NotEqual(interactiveTab, job.ActiveTabId);
            Assert.False(job.HasController);
            vm.PublishTerminalChatLifecycleEventOnce(chat, eventType);

            Assert.Empty(job.Tabs);
            Assert.Same(interactive, vm.GetBrowserServiceForChat(chat.Id));
            Assert.Equal(interactiveTab, interactive.ActiveTabId);
            Assert.True(vm.HasUsedBrowser);
            Assert.True(vm.IsBrowserOpen);
            Assert.Equal(chat.Id, shownChat);
            await vm.ReleaseJobBrowserSessionAsync(chat.Id);
            Assert.Single(interactive.Tabs);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TurnEndKeepsBrowserUntilAttachedWorkReachesIdle()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var chat = new Chat();
            using var vm = CreateViewModel(chat);
            vm.BeginChatLifecycleTurn(chat);
            vm.BeginJobBrowserSession(chat.Id);
            var job = vm.GetOrCreateBrowserService(chat.Id);
            vm.IsBrowserOpen = true;
            vm.HasUsedBrowser = true;
            var hidden = false;
            vm.BrowserHideRequested += () => hidden = true;

            vm.PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.TurnEnd);
            Assert.Same(job, vm.GetBrowserServiceForChat(chat.Id));
            Assert.Single(job.Tabs);

            vm.PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.Idle);
            Assert.Empty(job.Tabs);
            Assert.Null(vm.GetBrowserServiceForChat(chat.Id));
            Assert.False(vm.IsBrowserOpen);
            Assert.False(vm.HasUsedBrowser);
            Assert.True(hidden);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PausedJobKeepsItsBrowserUntilResumedSessionFinishes()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var chat = new Chat();
            using var vm = CreateViewModel(chat);
            vm.BeginChatLifecycleTurn(chat);
            vm.BeginJobBrowserSession(chat.Id);
            var job = vm.GetOrCreateBrowserService(chat.Id);
            chat.IsPaused = true;

            vm.MarkTurnAttended(chat.Id);
            vm.PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.Aborted);
            vm.PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.Idle);
            Assert.Same(job, vm.GetBrowserServiceForChat(chat.Id));
            Assert.Single(job.Tabs);

            chat.IsPaused = false;
            vm.BeginChatLifecycleTurn(chat);
            vm.PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.Idle);
            Assert.Empty(job.Tabs);
            Assert.Null(vm.GetBrowserServiceForChat(chat.Id));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task UserFollowUpRestoresOwnBrowserWithoutDisposingInFlightJobOperations()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var chat = new Chat();
            using var vm = CreateViewModel(chat);
            var interactive = vm.GetOrCreateBrowserService(chat.Id);
            vm.BeginJobBrowserSession(chat.Id);
            var job = vm.GetOrCreateBrowserService(chat.Id);

            vm.MarkTurnAttended(chat.Id);
            Assert.Same(interactive, vm.GetOrCreateBrowserService(chat.Id));
            Assert.Single(job.Tabs);

            vm.PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.Idle);
            Assert.Empty(job.Tabs);
            Assert.Single(interactive.Tabs);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RepeatedJobsLeaveNoReplacementTabAndDoNotTouchOtherChats()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var chat = new Chat();
            var other = new Chat();
            using var vm = CreateViewModel(chat, other);
            var interactive = vm.GetOrCreateBrowserService(other.Id);

            for (var run = 0; run < 3; run++)
            {
                vm.BeginChatLifecycleTurn(chat);
                vm.BeginJobBrowserSession(chat.Id);
                var job = vm.GetOrCreateBrowserService(chat.Id);
                vm.PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.Idle);
                Assert.Empty(job.Tabs);
                Assert.Null(vm.GetBrowserServiceForChat(chat.Id));
                Assert.Same(interactive, vm.GetBrowserServiceForChat(other.Id));
            }

            Assert.Single(vm.ChatBrowserServices);
            Assert.Single(interactive.Tabs);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SchedulerWaitsForPreviousJobResourcesWithoutBlockingAnInteractiveFollowUp()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var chat = new Chat();
            var store = new DataStore(new AppData
            {
                Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false },
                Chats = [chat]
            });
            using var vm = new ChatViewModel(store, TestCopilot.Shared) { CurrentChat = chat };
            using var jobs = new BackgroundJobService(store, vm);
            var isBusy = typeof(BackgroundJobService).GetMethod(
                "IsChatBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;

            vm.BeginJobBrowserSession(chat.Id);
            Assert.False(vm.IsChatBusyForSend(chat.Id));
            Assert.True((bool)isBusy.Invoke(jobs, [chat.Id])!);

            vm.MarkTurnAttended(chat.Id);
            Assert.False(vm.IsChatBusyForSend(chat.Id));
            Assert.True((bool)isBusy.Invoke(jobs, [chat.Id])!);

            await vm.ReleaseJobBrowserSessionAsync(chat.Id);
            Assert.False((bool)isBusy.Invoke(jobs, [chat.Id])!);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task InteractiveCompletionStillKeepsBrowser()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var chat = new Chat();
            using var vm = CreateViewModel(chat);
            var browser = vm.GetOrCreateBrowserService(chat.Id);
            vm.PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.Idle);
            Assert.Same(browser, vm.GetBrowserServiceForChat(chat.Id));
            Assert.Single(browser.Tabs);
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("ResetAfterCopilotReconnect")]
    [InlineData("CleanupSession")]
    public async Task RuntimeTeardownReleasesJobBrowser(string methodName)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var chat = new Chat();
            using var vm = CreateViewModel(chat);
            vm.BeginJobBrowserSession(chat.Id);
            var browser = vm.GetOrCreateBrowserService(chat.Id);
            var method = typeof(ChatViewModel).GetMethod(
                methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

            method.Invoke(vm, methodName == "CleanupSession" ? [chat.Id] : null);
            Assert.Empty(browser.Tabs);
            Assert.Null(vm.GetBrowserServiceForChat(chat.Id));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DisposingSurfaceReleasesJobAndSavedInteractiveBrowser()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var chat = new Chat();
            var vm = CreateViewModel(chat);
            var interactive = vm.GetOrCreateBrowserService(chat.Id);
            vm.BeginJobBrowserSession(chat.Id);
            var job = vm.GetOrCreateBrowserService(chat.Id);
            vm.Dispose();
            Assert.Empty(job.Tabs);
            Assert.Empty(interactive.Tabs);
        }, CancellationToken.None);
    }

    private static ChatViewModel CreateViewModel(params Chat[] chats)
        => new(new DataStore(new AppData
        {
            Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false },
            Chats = [.. chats]
        }), TestCopilot.Shared) { CurrentChat = chats[0] };
}
