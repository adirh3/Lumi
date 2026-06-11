using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class MultipleChatWindowsTests
{
    [Fact]
    public async Task OpenChatInNewWindowCommand_RaisesRequestedChatId()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var chat = new Chat { Title = "Detached chat" };
            chat.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
            var viewModel = CreateViewModel(chat);
            DetachedChatWindowRequest? request = null;
            viewModel.OpenChatWindowRequested += requested => request = requested;

            await viewModel.OpenChatInNewWindowCommand.ExecuteAsync(chat);

            Assert.Same(chat, request?.Chat);
            Assert.NotNull(request?.WindowVM);
            request?.WindowVM.Dispose();
            request?.ReleaseSurface();
            viewModel.Dispose();
        }, CancellationToken.None);
    }

    [Fact]
    public void OpenChatInNewWindowCommand_WithNoActiveChat_RequestsDraftWindow()
    {
        Loc.Load("en");
        var viewModel = CreateViewModel();
        DetachedChatWindowRequest? request = null;
        var requested = false;
        viewModel.OpenChatWindowRequested += requestedWindow =>
        {
            request = requestedWindow;
            Assert.Null(requestedWindow.Chat);
            Assert.NotNull(requestedWindow.WindowVM);
            requested = true;
        };

        viewModel.OpenChatInNewWindowCommand.Execute(null);

        Assert.True(requested);
        Assert.Null(viewModel.ChatVM.CurrentChat);
        request?.WindowVM.Dispose();
        request?.ReleaseSurface();
        viewModel.Dispose();
    }

    [Fact]
    public void OpenNewChatInNewWindowCommand_RequestsDraftWindowWithoutMovingActiveChat()
    {
        Loc.Load("en");
        var chat = new Chat { Title = "Still active in main" };
        var viewModel = CreateViewModel(chat);
        viewModel.ChatVM.CurrentChat = chat;
        DetachedChatWindowRequest? request = null;

        viewModel.OpenChatWindowRequested += requested => request = requested;

        viewModel.OpenNewChatInNewWindowCommand.Execute(null);

        Assert.Null(request?.Chat);
        Assert.NotNull(request?.WindowVM);
        Assert.Same(chat, viewModel.ChatVM.CurrentChat);
        request?.WindowVM.Dispose();
        request?.ReleaseSurface();
        viewModel.Dispose();
    }

    [Fact]
    public void OpenNewChatInNewWindowCommand_AppliesProjectFilterToDetachedDraft()
    {
        Loc.Load("en");
        var projectId = Guid.NewGuid();
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            },
            Projects =
            [
                new Project
                {
                    Id = projectId,
                    Name = "Coding project",
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                }
            ]
        };
        var viewModel = new MainViewModel(new DataStore(data), new CopilotService(), new UpdateService());
        DetachedChatWindowRequest? request = null;
        viewModel.OpenChatWindowRequested += requested => request = requested;

        viewModel.SelectedProjectFilter = projectId;
        viewModel.OpenNewChatInNewWindowCommand.Execute(null);

        Assert.NotNull(request);
        Assert.Null(request.Chat);
        Assert.Equal(projectId, request.WindowVM.ChatVM.ActiveProjectFilterId);
        Assert.Equal(projectId, GetPrivateField<Guid?>(request.WindowVM.ChatVM, "_pendingProjectId"));
        request.WindowVM.Dispose();
        request.ReleaseSurface();
        viewModel.Dispose();
    }

    [Fact]
    public async Task OpenChatInNewWindowCommand_TransfersActiveChatViewModelWhenDetachingActiveChat()
    {
        Loc.Load("en");
        var chat = new Chat { Title = "Active detached chat" };
        var viewModel = CreateViewModel(chat);
        var originalChatVm = viewModel.ChatVM;
        originalChatVm.CurrentChat = chat;
        originalChatVm.IsBusy = true;
        originalChatVm.IsStreaming = true;
        DetachedChatWindowRequest? request = null;
        viewModel.OpenChatWindowRequested += requested => request = requested;

        await viewModel.OpenChatInNewWindowCommand.ExecuteAsync(chat);

        Assert.Same(chat, request?.Chat);
        Assert.Same(originalChatVm, request?.WindowVM.ChatVM);
        Assert.NotSame(originalChatVm, viewModel.ChatVM);
        Assert.Null(viewModel.ChatVM.CurrentChat);
        Assert.True(originalChatVm.IsBusy);
        Assert.True(originalChatVm.IsStreaming);
        request?.WindowVM.Dispose();
        request?.ReleaseSurface();
        viewModel.Dispose();
    }

    [Fact]
    public async Task OpenChatInNewWindowCommand_DetachingActiveBusyChat_KeepsTransferredSurfaceAsChatOwner()
    {
        Loc.Load("en");
        var chat = new Chat { Title = "Busy owner" };
        var viewModel = CreateViewModel(chat);
        var originalChatVm = viewModel.ChatVM;
        originalChatVm.CurrentChat = chat;
        originalChatVm.IsBusy = true;
        DetachedChatWindowRequest? request = null;
        viewModel.OpenChatWindowRequested += requested => request = requested;

        await viewModel.OpenChatInNewWindowCommand.ExecuteAsync(chat);

        Assert.True(viewModel.ChatSurfaceRegistry.TryGetOwner(chat.Id, out var owner));
        Assert.Same(originalChatVm, owner);
        Assert.Same(originalChatVm, request?.WindowVM.ChatVM);
        request?.WindowVM.Dispose();
        request?.ReleaseSurface();
        viewModel.Dispose();
    }

    [Fact]
    public async Task OpenChatInNewWindowCommand_FocusingAlreadyDetachedChat_ClearsDuplicateMainSurfaceWithoutOpeningWindow()
    {
        Loc.Load("en");
        var chat = new Chat { Title = "Already detached" };
        var viewModel = CreateViewModel(chat);
        viewModel.ChatVM.CurrentChat = chat;
        var openRequested = false;
        viewModel.OpenChatWindowRequested += _ => openRequested = true;
        viewModel.DetachedChatFocusRequested += _ => true;

        await viewModel.OpenChatInNewWindowCommand.ExecuteAsync(chat);

        Assert.False(openRequested);
        Assert.Null(viewModel.ChatVM.CurrentChat);
        Assert.Null(viewModel.ActiveChatId);
        viewModel.Dispose();
    }

    [Fact]
    public async Task OpenChatInNewWindowCommand_DoesNotTransferMainViewModelForInactiveChat()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var mainChat = new Chat { Title = "Main chat" };
            mainChat.Messages.Add(new ChatMessage { Role = "user", Content = "main" });
            var inactiveChat = new Chat { Title = "Inactive detached chat" };
            inactiveChat.Messages.Add(new ChatMessage { Role = "user", Content = "inactive" });
            var viewModel = CreateViewModel(mainChat, inactiveChat);
            var originalChatVm = viewModel.ChatVM;
            await originalChatVm.LoadChatAsync(mainChat);
            DetachedChatWindowRequest? request = null;
            viewModel.OpenChatWindowRequested += requested => request = requested;

            await viewModel.OpenChatInNewWindowCommand.ExecuteAsync(inactiveChat);

            Assert.Same(inactiveChat, request?.Chat);
            Assert.NotSame(originalChatVm, request?.WindowVM.ChatVM);
            Assert.Same(originalChatVm, viewModel.ChatVM);
            Assert.Same(mainChat, viewModel.ChatVM.CurrentChat);
            request?.WindowVM.Dispose();
            request?.ReleaseSurface();
            viewModel.Dispose();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task OpenChatInNewWindowCommand_ReusesLiveOwnerForInactiveRunningChat()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var runningChat = new Chat { Title = "Running chat" };
            runningChat.Messages.Add(new ChatMessage { Role = "user", Content = "run" });
            var visibleChat = new Chat { Title = "Visible chat" };
            visibleChat.Messages.Add(new ChatMessage { Role = "user", Content = "visible" });
            var viewModel = CreateViewModel(runningChat, visibleChat);
            var originalChatVm = viewModel.ChatVM;
            await originalChatVm.LoadChatAsync(runningChat);
            var runtimeStates = GetPrivateField<Dictionary<Guid, ChatRuntimeState>>(originalChatVm, "_runtimeStates");
            var runningRuntime = new ChatRuntimeState { Chat = runningChat };
            runningRuntime.IsBusy = true;
            runningRuntime.IsStreaming = true;
            runtimeStates[runningChat.Id] = runningRuntime;

            Assert.True(await viewModel.OpenChatByIdAsync(visibleChat.Id));
            DetachedChatWindowRequest? request = null;
            viewModel.OpenChatWindowRequested += requested => request = requested;

            await viewModel.OpenChatInNewWindowCommand.ExecuteAsync(runningChat);

            Assert.NotNull(request);
            Assert.Same(originalChatVm, request.WindowVM.ChatVM);
            Assert.Same(visibleChat, viewModel.ChatVM.CurrentChat);
            Assert.True(runningChat.IsRunning);
            Assert.True(runningRuntime.IsBusy);
            request.WindowVM.Dispose();
            request.ReleaseSurface();
            viewModel.Dispose();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task OpenChatInNewWindowCommand_DetachingRunningChat_DoesNotFocusUnrelatedDetachedChat()
    {
        Loc.Load("en");
        var runningChat = new Chat { Title = "Running chat" };
        var unrelatedDetachedChat = new Chat { Title = "Already detached previous chat" };
        var viewModel = CreateViewModel(runningChat, unrelatedDetachedChat);
        var originalChatVm = viewModel.ChatVM;
        originalChatVm.CurrentChat = runningChat;
        var runtimeStates = GetPrivateField<Dictionary<Guid, ChatRuntimeState>>(originalChatVm, "_runtimeStates");
        var runningRuntime = new ChatRuntimeState { Chat = runningChat };
        runningRuntime.IsBusy = true;
        runtimeStates[runningChat.Id] = runningRuntime;
        var unrelatedChatFocused = false;
        viewModel.DetachedChatFocusRequested += chat =>
        {
            unrelatedChatFocused = chat.Id == unrelatedDetachedChat.Id;
            return unrelatedChatFocused;
        };
        DetachedChatWindowRequest? request = null;
        viewModel.OpenChatWindowRequested += requested => request = requested;

        await viewModel.OpenChatInNewWindowCommand.ExecuteAsync(runningChat);

        Assert.NotNull(request);
        Assert.False(unrelatedChatFocused);
        Assert.Same(originalChatVm, request.WindowVM.ChatVM);
        Assert.Null(viewModel.ChatVM.CurrentChat);
        request.WindowVM.Dispose();
        request.ReleaseSurface();
        viewModel.Dispose();
    }

    [Fact]
    public async Task OpenChatByIdAsync_FocusesDetachedWindowInsteadOfLoadingInMainSurface()
    {
        Loc.Load("en");
        var mainChat = new Chat { Title = "Main chat" };
        var detachedChat = new Chat { Title = "Already detached chat" };
        var viewModel = CreateViewModel(mainChat, detachedChat);
        viewModel.ChatVM.CurrentChat = mainChat;
        Chat? focusedChat = null;
        Guid? resyncedActiveChatId = null;
        viewModel.DetachedChatFocusRequested += chat =>
        {
            focusedChat = chat;
            return true;
        };
        viewModel.ChatSelectionSyncRequested += activeChatId => resyncedActiveChatId = activeChatId;

        var opened = await viewModel.OpenChatByIdAsync(detachedChat.Id);

        Assert.True(opened);
        Assert.Same(detachedChat, focusedChat);
        Assert.Same(mainChat, viewModel.ChatVM.CurrentChat);
        Assert.Equal(mainChat.Id, resyncedActiveChatId);
        viewModel.Dispose();
    }

    [Fact]
    public async Task OpenChatByIdAsync_SwitchingAwayFromRunningChat_KeepsLiveSurfaceOwnedByOriginalSession()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var runningProjectId = Guid.NewGuid();
            var selectedProjectId = Guid.NewGuid();
            var runningChat = new Chat { Title = "Running chat", ProjectId = runningProjectId };
            runningChat.Messages.Add(new ChatMessage { Role = "user", Content = "run" });
            var nextChat = new Chat { Title = "Next chat", ProjectId = selectedProjectId };
            nextChat.Messages.Add(new ChatMessage { Role = "user", Content = "next" });
            var viewModel = CreateViewModel(runningChat, nextChat);
            var originalChatVm = viewModel.ChatVM;
            await originalChatVm.LoadChatAsync(runningChat);
            var runtimeStates = GetPrivateField<Dictionary<Guid, ChatRuntimeState>>(originalChatVm, "_runtimeStates");
            var runningRuntime = new ChatRuntimeState { Chat = runningChat };
            runningRuntime.IsBusy = true;
            runtimeStates[runningChat.Id] = runningRuntime;

            var opened = await viewModel.OpenChatByIdAsync(nextChat.Id);

            Assert.True(opened);
            Assert.NotSame(originalChatVm, viewModel.ChatVM);
            Assert.Same(nextChat, viewModel.ChatVM.CurrentChat);
            Assert.True(viewModel.ChatSurfaceRegistry.TryGetLiveOwner(runningChat.Id, out var liveOwner));
            Assert.Same(originalChatVm, liveOwner);
            Assert.True(runningChat.IsRunning);
            viewModel.SelectedProjectFilter = selectedProjectId;

            var restored = await viewModel.OpenChatByIdAsync(runningChat.Id);

            Assert.True(restored);
            Assert.Equal(runningProjectId, runningChat.ProjectId);
            viewModel.Dispose();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChatSessionStore_ReturnsSingleSurfaceForSamePersistedChat()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var chat = new Chat { Title = "Shared chat" };
            chat.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
            using var registry = new ChatSurfaceRegistry();
            using var store = new ChatSessionStore(CreateDataStore(chat), new CopilotService(), registry);

            var first = await store.AcquireChatAsync(chat);
            var second = await store.AcquireChatAsync(chat);

            Assert.Same(first, second);
            Assert.True(registry.TryGetOwner(chat.Id, out var owner));
            Assert.Same(first, owner);

            store.Release(second);
            store.Release(first);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChatSessionStore_SerializesConcurrentAcquireLoadForSamePersistedChat()
    {
        var chat = new Chat { Title = "Concurrent shared chat" };
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
        using var registry = new ChatSurfaceRegistry();
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowLoadToComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCount = 0;
        using var store = new ChatSessionStore(
            CreateDataStore(chat),
            new CopilotService(),
            registry,
            async (surface, chatToLoad) =>
            {
                Interlocked.Increment(ref loadCount);
                loadStarted.TrySetResult();
                await allowLoadToComplete.Task;
                surface.CurrentChat = chatToLoad;
            });

        var firstAcquire = store.AcquireChatAsync(chat);
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondAcquire = store.AcquireChatAsync(chat);
        await Task.Delay(50);

        Assert.Equal(1, Volatile.Read(ref loadCount));

        allowLoadToComplete.SetResult();
        var first = await firstAcquire;
        var second = await secondAcquire;

        Assert.Same(first, second);
        Assert.Equal(1, Volatile.Read(ref loadCount));
        store.Release(second);
        store.Release(first);
    }

    [Fact]
    public async Task OpenChatByIdAsync_ShowsLoadingOnVisibleSurfaceWhileTargetSurfaceLoads()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var visibleChat = new Chat { Title = "Visible chat" };
            visibleChat.Messages.Add(new ChatMessage { Role = "user", Content = "visible" });
            var targetChat = new Chat { Title = "Target chat" };
            targetChat.Messages.Add(new ChatMessage { Role = "user", Content = "target" });
            var dataStore = CreateDataStore(visibleChat, targetChat);
            var copilotService = new CopilotService();
            using var registry = new ChatSurfaceRegistry();
            var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowLoadToComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var store = new ChatSessionStore(
                dataStore,
                copilotService,
                registry,
                async (surface, chatToLoad) =>
                {
                    loadStarted.TrySetResult();
                    await allowLoadToComplete.Task;
                    surface.CurrentChat = chatToLoad;
                });
            var viewModel = new MainViewModel(
                dataStore,
                copilotService,
                new UpdateService(),
                startBackgroundJobs: false,
                chatSurfaceRegistry: registry,
                chatSessionStore: store);
            viewModel.ChatVM.CurrentChat = visibleChat;
            var visibleSurface = viewModel.ChatVM;

            var openTask = viewModel.OpenChatByIdAsync(targetChat.Id);
            await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(visibleSurface.IsLoadingChat);

            allowLoadToComplete.SetResult();
            Assert.True(await openTask);

            Assert.False(visibleSurface.IsLoadingChat);
            Assert.Same(targetChat, viewModel.ChatVM.CurrentChat);
            viewModel.Dispose();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChatSessionStore_DisposesBrowserOnDeleteAfterRuntimeSwept()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var browserChat = new Chat { Title = "Browser chat" };
            var otherChat = new Chat { Title = "Other chat" };
            browserChat.Messages.Add(new ChatMessage { Role = "user", Content = "browse" });
            otherChat.Messages.Add(new ChatMessage { Role = "user", Content = "other" });
            using var registry = new ChatSurfaceRegistry();
            using var store = new ChatSessionStore(CreateDataStore(browserChat, otherChat), new CopilotService(), registry);

            var surface = await store.AcquireChatAsync(browserChat);

            // Give the chat a live per-chat browser session.
            var services = GetPrivateField<ConcurrentDictionary<Guid, BrowserService>>(surface, "_chatBrowserServices");
            services[browserChat.Id] = new BrowserService();

            // Switch the surface to a different chat: this sweeps the browser chat's runtime state,
            // but the browser session is intentionally kept alive for when the user returns.
            await surface.LoadChatAsync(otherChat);
            Assert.True(services.ContainsKey(browserChat.Id));
            Assert.NotEqual(browserChat.Id, surface.CurrentChat?.Id);
            Assert.False(surface.OwnsLiveChat(browserChat.Id));

            // Deleting the chat must tear its browser down instead of leaking it until app shutdown,
            // even though the surface neither displays nor owns the chat anymore.
            store.CleanupChat(browserChat.Id);
            Assert.False(services.ContainsKey(browserChat.Id));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChatSessionStore_KeepsRecentlyIdleSurfaceAvailableForFastSwitching()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var chat = new Chat { Title = "Running chat" };
            chat.Messages.Add(new ChatMessage { Role = "user", Content = "run" });
            using var registry = new ChatSurfaceRegistry();
            using var store = new ChatSessionStore(CreateDataStore(chat), new CopilotService(), registry);

            var first = await store.AcquireChatAsync(chat);
            var runtimeStates = GetPrivateField<Dictionary<Guid, ChatRuntimeState>>(first, "_runtimeStates");
            var runtime = new ChatRuntimeState { Chat = chat };
            runtime.IsBusy = true;
            runtimeStates[chat.Id] = runtime;
            first.IsBusy = true;

            store.Release(first);

            Assert.Contains(first, store.SnapshotSurfaces());

            var second = await store.AcquireChatAsync(chat);

            Assert.Same(first, second);

            runtime.IsBusy = false;
            first.IsBusy = false;
            store.Release(second);

            Assert.Contains(first, store.SnapshotSurfaces());

            var third = await store.AcquireChatAsync(chat);

            Assert.Same(first, third);
            store.Release(third);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChatSessionStore_EvictsLeastRecentlyUsedIdleSurfaceWhenCacheIsFull()
    {
        var firstChat = new Chat { Title = "First cached chat" };
        var secondChat = new Chat { Title = "Second cached chat" };
        var thirdChat = new Chat { Title = "Third cached chat" };
        firstChat.Messages.Add(new ChatMessage { Role = "user", Content = "first" });
        secondChat.Messages.Add(new ChatMessage { Role = "user", Content = "second" });
        thirdChat.Messages.Add(new ChatMessage { Role = "user", Content = "third" });
        using var registry = new ChatSurfaceRegistry();
        using var store = new ChatSessionStore(
            CreateDataStore(firstChat, secondChat, thirdChat),
            new CopilotService(),
            registry,
            static (surface, chat) =>
            {
                surface.CurrentChat = chat;
                return Task.CompletedTask;
            },
            maxIdleCachedSurfaces: 2);

        var first = await store.AcquireChatAsync(firstChat);
        store.Release(first);
        var second = await store.AcquireChatAsync(secondChat);
        store.Release(second);
        var firstAgain = await store.AcquireChatAsync(firstChat);
        store.Release(firstAgain);
        var third = await store.AcquireChatAsync(thirdChat);
        store.Release(third);

        var surfaces = store.SnapshotSurfaces();
        Assert.Same(first, firstAgain);
        Assert.Contains(first, surfaces);
        Assert.DoesNotContain(second, surfaces);
        Assert.Contains(third, surfaces);
        Assert.False(registry.TryGetOwner(secondChat.Id, out _));
    }

    [Fact]
    public async Task ChatSessionStore_ReleasesRetainedSurfaceWhenChatLoadFails()
    {
        var chat = new Chat { Title = "Broken load chat" };
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "break" });
        using var registry = new ChatSurfaceRegistry();
        using var store = new ChatSessionStore(
            CreateDataStore(chat),
            new CopilotService(),
            registry,
            static (_, _) => throw new InvalidOperationException("Simulated load failure."));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AcquireChatAsync(chat));

        Assert.Empty(store.SnapshotSurfaces());
        Assert.False(registry.TryGetOwner(chat.Id, out _));
    }

    [Fact]
    public void NewChatCommand_WhileCurrentChatIsRunning_KeepsLiveSurfaceOwnedByOriginalSession()
    {
        Loc.Load("en");
        var runningChat = new Chat { Title = "Running chat" };
        var viewModel = CreateViewModel(runningChat);
        var originalChatVm = viewModel.ChatVM;
        originalChatVm.CurrentChat = runningChat;
        var runtimeStates = GetPrivateField<Dictionary<Guid, ChatRuntimeState>>(originalChatVm, "_runtimeStates");
        var runningRuntime = new ChatRuntimeState { Chat = runningChat };
        runningRuntime.IsBusy = true;
        runtimeStates[runningChat.Id] = runningRuntime;

        viewModel.NewChatCommand.Execute(null);

        Assert.NotSame(originalChatVm, viewModel.ChatVM);
        Assert.Null(viewModel.ChatVM.CurrentChat);
        Assert.True(viewModel.ChatSurfaceRegistry.TryGetLiveOwner(runningChat.Id, out var liveOwner));
        Assert.Same(originalChatVm, liveOwner);
        viewModel.Dispose();
    }

    [Fact]
    public void DeleteChatCommand_RaisesDeletedChatId()
    {
        Loc.Load("en");
        var chat = new Chat { Title = "Deleted chat" };
        var viewModel = CreateViewModel(chat);
        Guid? deletedChatId = null;
        viewModel.ChatDeleted += chatId => deletedChatId = chatId;

        viewModel.DeleteChatCommand.Execute(chat);

        Assert.Equal(chat.Id, deletedChatId);
        viewModel.Dispose();
    }

    [Fact]
    public void ChatWindowViewModel_WindowTitleTracksChatTitle()
    {
        Loc.Load("en");
        var chat = new Chat { Title = "Initial title" };
        var chatVm = new ChatViewModel(new DataStore(new AppData { Chats = [chat] }), new CopilotService())
        {
            CurrentChat = chat
        };
        using var windowVm = new ChatWindowViewModel(chatVm);
        var changed = false;
        windowVm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatWindowViewModel.WindowTitle))
                changed = true;
        };

        chat.Title = "Updated title";

        Assert.True(changed);
        Assert.Equal("Updated title", windowVm.WindowTitle);
        chatVm.Dispose();
    }

    private static MainViewModel CreateViewModel(params Chat[] chats)
        => new(CreateDataStore(chats), new CopilotService(), new UpdateService());

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

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found.");
        return (T)field.GetValue(target)!;
    }
}
