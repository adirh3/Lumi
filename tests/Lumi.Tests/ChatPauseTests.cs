using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using GitHub.Copilot;
using StrataTheme.Controls;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ChatPauseTests
{
    [Fact]
    public async Task Gate_ResumesTheSameWaitingSteps_WithoutCancelingThem()
    {
        var gate = new ChatPauseGate();
        await gate.WaitAsync();
        gate.SetPaused(true);
        var first = gate.WaitAsync();
        var second = gate.WaitAsync();

        Assert.True(gate.IsWaiting);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        gate.SetPaused(false);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(gate.IsWaiting);

        gate.SetPaused(true);
        var next = gate.WaitAsync();
        Assert.False(next.IsCompleted);
        gate.SetPaused(false);
        await next.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Gate_CancelingOneWaiter_DoesNotResumeTheOthers()
    {
        var gate = new ChatPauseGate();
        gate.SetPaused(true);
        using var cancellation = new CancellationTokenSource();
        var canceled = gate.WaitAsync(cancellation.Token);
        var waiting = gate.WaitAsync();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.False(waiting.IsCompleted);
        Assert.True(gate.IsWaiting);
        gate.SetPaused(false);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Gate_StopCancelsHeldActions_RatherThanLettingThemExecute()
    {
        var gate = new ChatPauseGate();
        gate.SetPaused(true);
        var actionExecuted = false;
        var action = RunAsync();
        gate.CancelWaiters();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        Assert.False(actionExecuted);
        var later = gate.WaitAsync();
        Assert.False(later.IsCompleted);
        gate.SetPaused(false);
        await later.WaitAsync(TimeSpan.FromSeconds(5));

        async Task RunAsync()
        {
            await gate.WaitAsync();
            actionExecuted = true;
        }
    }

    [Fact]
    public async Task Runtime_HeldCallbackRetainsOwnership_WithoutAnAssistantSpinner()
    {
        var runtime = new ChatRuntimeState();
        runtime.PauseGate.SetPaused(true);
        var callback = runtime.PauseGate.WaitAsync();
        Assert.False(runtime.IsBusy);
        Assert.True(runtime.HasActiveWork);
        runtime.PauseGate.SetPaused(false);
        await callback;
        Assert.False(runtime.HasActiveWork);
    }

    [Fact]
    public async Task SdkHooks_NeverHoldToolCompletion()
    {
        var hooks = ChatViewModel.BuildSessionHooks();
        var before = hooks.OnPreToolUse!(new PreToolUseHookInput(), new HookInvocation());
        Assert.Equal("allow", (await before)!.PermissionDecision);
        Assert.Null(hooks.OnPostToolUse);
        Assert.Null(hooks.OnPostToolUseFailure);
    }

    [Fact]
    public async Task PausedExternalSend_IsRejectedBeforeConnectingOrAddingHistory()
    {
        var chat = new Chat { IsPaused = true };
        using var vm = new ChatViewModel(new DataStore(CreateData(chat)), TestCopilot.Shared)
        {
            CurrentChat = chat
        };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.SendExternalMessageAsync(chat, "External message", "Test"));
        Assert.Empty(chat.Messages);
        Assert.True(chat.IsPaused);
    }

    [Fact]
    public async Task PausedSend_IsVisibleAndQueued_AndIdleCannotDrainIt()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            Loc.Load("en");
            var chat = new Chat { IsPaused = true };
            var data = CreateData(chat);
            using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared)
            {
                CurrentChat = chat,
                PromptText = "Hold this message"
            };

            await vm.SendMessageCommand.ExecuteAsync(null);
            var message = Assert.Single(chat.Messages);
            Assert.Equal("Hold this message", message.Content);
            Assert.Equal(MessageSteerState.Queued, message.SteerDelivery);
            Assert.False(message.CanSendNowWhenQueued);
            Assert.Equal("", vm.PromptText);

            await (Task)typeof(ChatViewModel).GetMethod(
                "DrainQueuedBusySendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [chat.Id])!;
            Assert.Equal(MessageSteerState.Queued, message.SteerDelivery);
            Assert.True(chat.IsPaused);
            Assert.False(vm.IsBusy);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TogglePause_PreservesDraftAndRuntime_AndResumeAddsNoMessage()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            var chat = new Chat { CopilotSessionId = "same-session" };
            using var vm = new ChatViewModel(new DataStore(CreateData(chat)), TestCopilot.Shared)
            {
                CurrentChat = chat,
                PromptText = "unfinished draft",
                IsBusy = true,
                IsSessionActive = true
            };
            vm.TogglePauseCommand.Execute(null);
            Assert.True(chat.IsPaused);
            Assert.False(vm.IsComposerBusy);
            Assert.True(vm.IsBusy);
            vm.ResumeChatCommand.Execute(null);
            Assert.False(chat.IsPaused);
            Assert.True(vm.IsComposerBusy);
            Assert.Equal("unfinished draft", vm.PromptText);
            Assert.Equal("same-session", chat.CopilotSessionId);
            Assert.Empty(chat.Messages);
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(600)]
    [InlineData(1000)]
    public async Task PauseControls_RenderAndResumeWithoutChangingTheDraft(double width)
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            Loc.Load("en");
            var chat = new Chat();
            using var vm = new ChatViewModel(new DataStore(CreateData(chat)), TestCopilot.Shared)
            {
                CurrentChat = chat,
                PromptText = "Keep my draft",
                IsBusy = true,
                IsStreaming = true,
                IsSessionActive = true
            };
            var view = new ChatView { DataContext = vm };
            var window = new Window { Width = width, Height = 700, Content = view };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var toggle = view.FindControl<Button>("PauseChatButton")!;
                var panel = view.FindControl<Border>("ChatPauseStatusPanel")!;
                var composer = view.FindControl<StrataChatComposer>("Composer")!;
                Assert.False(panel.IsVisible);
                Assert.True(toggle.IsVisible);
                Assert.Null(view.FindControl<Button>("ChatPauseToggleButton"));
                toggle.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();

                Assert.True(panel.IsVisible);
                Assert.False(toggle.IsVisible);
                Assert.InRange(panel.Bounds.Width, 100, width);
                Assert.InRange(panel.Bounds.Height, 40, 160);
                Assert.False(composer.IsBusy);
                Assert.False(vm.IsTranscriptStreaming);
                Assert.True(vm.IsBusy);
                Assert.True(vm.IsSessionActive);
                Assert.True(view.FindControl<Button>("StopPausedChatButton")!.IsVisible);
                Assert.Equal("Keep my draft", composer.PromptText);
                var resume = view.FindControl<Button>("ResumeChatButton")!;
                Assert.True(resume.IsVisible);
                resume.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();

                Assert.False(panel.IsVisible);
                Assert.True(toggle.IsVisible);
                Assert.True(composer.IsBusy);
                Assert.True(vm.IsTranscriptStreaming);
                Assert.Equal("Keep my draft", composer.PromptText);
                Assert.Empty(chat.Messages);
                vm.IsBusy = false;
                vm.IsStreaming = false;
                vm.IsSessionActive = false;
                Dispatcher.UIThread.RunJobs();
                Assert.False(toggle.IsVisible);
                Assert.False(view.FindControl<Border>("CodingStrip")!.IsVisible);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SidebarContextMenu_PausesAndResumesItsChatWithoutSwitchingTheCurrentChat()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            Loc.Load("en");
            var current = new Chat { Title = "Current chat" };
            var target = new Chat { Title = "Other chat", IsSessionActive = true };
            var data = CreateData(current, target);
            data.Settings.IsOnboarded = true;
            using var vm = new MainViewModel(
                new DataStore(data), TestCopilot.Shared, new UpdateService(),
                startBackgroundJobs: false, initializeCopilotOnStartup: false);
            vm.ChatVM.CurrentChat = current;
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 800 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var row = window.GetVisualDescendants().OfType<Panel>()
                    .Single(panel => ReferenceEquals(panel.DataContext, target) && panel.ContextMenu is not null);
                var menu = row.ContextMenu!;
                menu.Open(row);
                Dispatcher.UIThread.RunJobs();
                var pause = menu.Items.OfType<MenuItem>().Single(item => item.Name == "PauseChatMenuItem");
                var resume = menu.Items.OfType<MenuItem>().Single(item => item.Name == "ResumeChatMenuItem");
                Assert.Same(vm.ToggleChatPauseCommand, pause.Command);
                Assert.Same(target, pause.CommandParameter);
                pause.Command!.Execute(pause.CommandParameter);
                Dispatcher.UIThread.RunJobs();

                Assert.True(target.IsPaused);
                Assert.False(current.IsPaused);
                Assert.False(pause.IsVisible);
                Assert.True(resume.IsVisible);
                await vm.ToggleChatPauseCommand.ExecuteAsync(resume.CommandParameter);
                Assert.False(target.IsPaused);
                Assert.Same(current, vm.ChatVM.CurrentChat);
                menu.Close();
            }
            finally
            {
                // Keep the owning dispatcher alive for the window's delayed startup focus.
                await Task.Delay(400);
                Dispatcher.UIThread.RunJobs();
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task GlobalControls_ShowOnlyUsableActions_AndDisappearWhenIdle()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            Loc.Load("en");
            var working = new Chat { IsRunning = true, IsSessionActive = true };
            var data = CreateData(working);
            data.Settings.IsOnboarded = true;
            using var vm = new MainViewModel(
                new DataStore(data), TestCopilot.Shared, new UpdateService(),
                startBackgroundJobs: false, initializeCopilotOnStartup: false);
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 800 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.Null(window.FindControl<Border>("ChatActivityBar"));
                var actions = window.FindControl<Button>("ChatActionsButton")!;
                var menu = Assert.IsType<MenuFlyout>(actions.Flyout);
                menu.ShowAt(actions);
                Dispatcher.UIThread.RunJobs();
                var pause = menu.Items.OfType<MenuItem>().Single(item => item.Name == "PauseAllChatsMenuItem");
                var resume = menu.Items.OfType<MenuItem>().Single(item => item.Name == "ResumeAllChatsMenuItem");
                Assert.True(pause.IsVisible);
                Assert.False(resume.IsVisible);
                pause.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();

                Assert.False(pause.IsVisible);
                Assert.True(resume.IsVisible);
                await vm.ResumeAllChatsCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();
                Assert.True(pause.IsVisible);
                Assert.False(resume.IsVisible);

                working.IsRunning = false;
                working.IsSessionActive = false;
                Dispatcher.UIThread.RunJobs();
                Assert.False(pause.IsVisible);
                Assert.False(resume.IsVisible);
                menu.Hide();
            }
            finally
            {
                await Task.Delay(400);
                Dispatcher.UIThread.RunJobs();
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TerminalRuntime_CancelsHeldCallback_WithoutLosingPersistedPauseIntent()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            var chat = new Chat();
            using var vm = new ChatViewModel(new DataStore(CreateData(chat)), TestCopilot.Shared)
            {
                CurrentChat = chat
            };
            var runtime = (ChatRuntimeState)typeof(ChatViewModel)
                .GetMethod("GetOrCreateRuntimeState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [chat.Id])!;
            runtime.IsSessionActive = true;
            vm.SetChatPaused(chat, true);
            var held = runtime.PauseGate.WaitAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.False(chat.IsPausePending);
            typeof(ChatViewModel).GetMethod(
                "MarkRuntimeTerminal", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [runtime, null]);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held);
            Dispatcher.UIThread.RunJobs();

            Assert.True(chat.IsPaused);
            Assert.False(chat.IsPausePending);
            Assert.False(runtime.HasActiveWork);
            Assert.False(vm.IsSessionActive);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PauseAll_IsAcrossProjects_AndDoesNotPauseIdleHistoryOrFutureChats()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            Loc.Load("en");
            var first = new Chat { IsSessionActive = true, ProjectId = Guid.NewGuid() };
            var background = new Chat { IsSessionActive = true, ProjectId = Guid.NewGuid() };
            var idle = new Chat();
            using var vm = new MainViewModel(
                new DataStore(CreateData(first, background, idle)), TestCopilot.Shared, new UpdateService());
            vm.SelectedProjectFilter = first.ProjectId;

            Assert.True(vm.PauseAllChatsCommand.CanExecute(null));
            vm.PauseAllChatsCommand.Execute(null);
            Assert.True(first.IsPaused);
            Assert.True(background.IsPaused);
            Assert.False(idle.IsPaused);
            Assert.False(vm.PauseAllChatsCommand.CanExecute(null));
            Assert.True(vm.ResumeAllChatsCommand.CanExecute(null));
            Assert.True(vm.HasChatActivity);

            await vm.ResumeAllChatsCommand.ExecuteAsync(null);
            Assert.False(first.IsPaused);
            Assert.False(background.IsPaused);
            Assert.False(vm.ResumeAllChatsCommand.CanExecute(null));
            Assert.True(vm.CanPauseAllChats);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RefreshedChatList_TracksPauseChangesOnNewChats()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            var store = new DataStore(CreateData());
            using var vm = new MainViewModel(
                store, TestCopilot.Shared, new UpdateService(), initializeCopilotOnStartup: false);
            var newChat = new Chat();
            store.Data.Chats.Add(newChat);
            vm.RefreshChatList();
            var changed = new List<string?>();
            vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

            vm.ToggleChatPauseCommand.Execute(newChat);

            Assert.True(newChat.IsPaused);
            Assert.Contains(nameof(MainViewModel.ChatActivitySummary), changed);
            Assert.True(vm.ResumeAllChatsCommand.CanExecute(null));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Store_PauseAndResumeReachDetachedAndBackgroundSurfaces()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            var first = new Chat();
            var second = new Chat();
            var data = new DataStore(CreateData(first, second));
            using var registry = new ChatSurfaceRegistry();
            using var store = new ChatSessionStore(data, TestCopilot.Shared, registry,
                (surface, chat) =>
                {
                    surface.CurrentChat = chat;
                    return Task.CompletedTask;
                });
            var firstSurface = await store.AcquireChatAsync(first);
            var secondSurface = await store.AcquireChatAsync(second);
            await store.SetChatPausedAsync(first, true);
            await store.SetChatPausedAsync(second, true);
            Assert.True(firstSurface.IsPaused);
            Assert.True(secondSurface.IsPaused);
            await store.SetChatPausedAsync(first, false);
            Assert.False(firstSurface.IsPaused);
            Assert.True(secondSurface.IsPaused);
            Assert.Empty(first.Messages);
            Assert.Empty(second.Messages);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Store_ResumingUnopenedPausedChat_LoadsItsPendingMessages()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            var chat = new Chat { IsPaused = true };
            var loaded = 0;
            using var registry = new ChatSurfaceRegistry();
            using var store = new ChatSessionStore(new DataStore(CreateData(chat)), TestCopilot.Shared, registry,
                (surface, target) =>
                {
                    loaded++;
                    surface.CurrentChat = target;
                    return Task.CompletedTask;
                });

            Assert.Null(await store.SetChatPausedAsync(chat, false));

            Assert.Equal(1, loaded);
            Assert.False(chat.IsPaused);
        }, CancellationToken.None);
    }

    [Fact]
    public void PauseIntent_IsPersisted_WithoutPersistingRuntimeActivity()
    {
        var source = CreateData(new Chat
        {
            IsPaused = true,
            PauseNeedsContinuation = true,
            IsPausePending = true,
            IsRunning = true,
            IsSessionActive = true
        });
        var snapshot = AppDataSnapshotFactory.CreateIndexSnapshot(source);
        var json = JsonSerializer.Serialize(snapshot, AppDataJsonContext.Default.AppData);
        var restored = JsonSerializer.Deserialize(json, AppDataJsonContext.Default.AppData)!;
        var chat = Assert.Single(restored.Chats);
        Assert.True(chat.IsPaused);
        Assert.True(chat.PauseNeedsContinuation);
        Assert.False(chat.IsPausePending);
        Assert.False(chat.IsRunning);
        Assert.False(chat.IsSessionActive);
        Assert.False(chat.ShowRunningIndicator);
        Assert.False(chat.HasBackgroundActivity);
    }

    private static AppData CreateData(params Chat[] chats) => new()
    {
        Settings = new UserSettings
        {
            AutoSaveChats = false,
            EnableMemoryAutoSave = false,
            NotificationsEnabled = false,
            AutoGenerateTitles = false
        },
        Chats = [.. chats]
    };
}
