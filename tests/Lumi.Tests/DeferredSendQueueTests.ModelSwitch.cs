using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using StreamJsonRpc;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Changing the model while Lumi works. The fake runtime behaves as the real one was measured to: while a
/// turn runs, any pick but a change of reasoning effort alone (even the model already running) waits in the
/// queue until the turn ends, an abort drops it with the turn, and a new effort applies at once.
/// </summary>
public sealed partial class DeferredSendQueueTests
{
    private const string RunningModel = "gpt-5.6-luna";
    private const string PickedModel = "claude-haiku-5.5";

    [Fact]
    public async Task ModelPickedMidReply_WaitsForTheReply_AndSwitchNowContinuesTheWorkOnIt()
    {
        await TestCopilot.Shared.ConnectAsync();
        using var host = DeferredSendHost.Create();
        using var rpc = StartWorkingTurn(host);

        Assert.True(await host.PickModelAsync(PickedModel));

        Assert.Equal([$"queue:{PickedModel}"], rpc.Operations);
        Assert.Equal(RunningModel, rpc.LiveModel);
        Assert.Equal(ModelSwitchNotice.Pending, host.ViewModel.ModelSwitchNotice);
        Assert.Contains("Claude Haiku 5.5", host.ViewModel.ModelSwitchNoticeText);
        Assert.Contains("Claude Haiku 5.5", host.ViewModel.SwitchModelNowToolTip);

        // What the composer showed while the switch passed through its pause.
        var whilePaused = new System.Collections.Concurrent.ConcurrentQueue<(string Placeholder, bool ShowsPause, bool Busy, ModelSwitchNotice Notice)>();
        host.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.ComposerPlaceholder) && host.Chat.IsPaused)
            {
                whilePaused.Enqueue((host.ViewModel.ComposerPlaceholder, host.ViewModel.ShowPauseStatus,
                    host.ViewModel.IsComposerBusy, host.ViewModel.ModelSwitchNotice));
            }
        };

        await host.ViewModel.SwitchModelNowCommand.ExecuteAsync(null);

        // Switching passes through a pause, which the chat doesn't show as one.
        Assert.NotEmpty(whilePaused);
        Assert.All(whilePaused, state =>
        {
            Assert.NotEqual(Loc.Get("Chat_PausedPlaceholder"), state.Placeholder);
            Assert.False(state.ShowsPause);
            Assert.True(state.Busy);
            Assert.Equal(ModelSwitchNotice.Switching, state.Notice);
        });
        // The interruption dropped the queued switch, so the resume applied the pick before continuing
        // the same work, without another user message.
        Assert.Equal(1, rpc.AbortCount);
        Assert.Equal(PickedModel, rpc.LiveModel);
        Assert.Equal(1, rpc.ModelAppliedBeforeContinuations);
        Assert.Equal(1, rpc.EmptyBatchCount);
        Assert.Equal(0, rpc.BatchMessageCount);
        Assert.False(host.Chat.IsPaused);
        Assert.False(host.ViewModel.ShowPauseStatus);
        Assert.True(host.Runtime.IsBusy);
        Assert.Null(host.Runtime.PendingModelSwitch);
        Assert.False(host.Runtime.IsSwitchingModel);
        Assert.Equal(ModelSwitchNotice.Applied, host.ViewModel.ModelSwitchNotice);
        Assert.Contains("Claude Haiku 5.5", host.ViewModel.ModelSwitchNoticeText);
    }

    [Fact]
    public async Task SwitchNow_KeepsMessagesQueuedForAfterTheReply()
    {
        await TestCopilot.Shared.ConnectAsync();
        using var host = DeferredSendHost.Create();
        using var rpc = StartWorkingTurn(host);
        host.ViewModel.PromptText = "then write the tests";
        await host.ViewModel.QueueMessageCommand.ExecuteAsync(null);
        Assert.True(await host.PickModelAsync(PickedModel));

        await host.ViewModel.SwitchModelNowCommand.ExecuteAsync(null);

        // Switching passes through a pause but isn't one: the message still waits for the reply to end,
        // rather than being held in the transcript and steered into the resumed work.
        Assert.Equal(PickedModel, rpc.LiveModel);
        Assert.Equal("then write the tests", Assert.Single(host.ViewModel.QueuedSends).Text);
        Assert.Empty(host.QueuedPrompts());
        Assert.DoesNotContain(host.Chat.Messages, message => message.Content == "then write the tests");
    }

    [Fact]
    public async Task SwitchNow_WhenTheSessionRejectsThePick_DoesNotResumeOnThePreviousModel()
    {
        await TestCopilot.Shared.ConnectAsync();
        using var host = DeferredSendHost.Create();
        using var rpc = StartWorkingTurn(host);
        Assert.True(await host.PickModelAsync(PickedModel));
        rpc.RejectImmediateModelSwitches = true;

        await host.ViewModel.SwitchModelNowCommand.ExecuteAsync(null);

        Assert.Equal(0, rpc.EmptyBatchCount);
        Assert.Equal(RunningModel, rpc.LiveModel);
        Assert.Equal(PickedModel, host.Chat.LastModelUsed);
        Assert.True(host.Chat.IsPaused);
        Assert.Contains("Claude Haiku 5.5", host.ViewModel.PauseError);
    }

    [Fact]
    public async Task DeferredSwitch_HoldsTheNextSendWithoutStoppingBackgroundWorkOrLosingTheQueue()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            await TestCopilot.Shared.ConnectAsync();
            using var host = DeferredSendHost.Create();
            using var rpc = StartWorkingTurn(host, subscribe: true);
            Assert.True(await host.PickModelAsync(PickedModel));
            // The reply ends while background work goes on, so the runtime still holds the switch.
            host.Runtime.HasPendingBackgroundWork = true;
            rpc.Emit(new AssistantIdleEvent { Data = new AssistantIdleData() });
            Dispatcher.UIThread.RunJobs();

            var sending = host.SendCoreAsync("next step");
            await rpc.ModelQueueRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            host.QueuePrompt("after that");

            Assert.Equal(0, rpc.AbortCount);
            Assert.Equal(0, rpc.SendCount);
            Assert.Equal([PickedModel], rpc.QueuedModelSwitches);
            Assert.True(host.Runtime.HasPendingBackgroundWork);
            Assert.Equal(["after that"], host.QueuedPrompts());

            rpc.FinishTurnAndApplySwitch();
            await sending.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, rpc.AbortCount);
            Assert.Equal(1, rpc.SendCount);
            Assert.Equal(PickedModel, rpc.LiveModel);
            Assert.Equal(["after that"], host.QueuedPrompts());
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ModelPickedMidReply_ThenStopped_AppliesBeforeTheNextMessage()
    {
        await TestCopilot.Shared.ConnectAsync();
        using var host = DeferredSendHost.Create();
        using var rpc = StartWorkingTurn(host);
        Assert.True(await host.PickModelAsync(PickedModel));

        Assert.Null(await host.StopGenerationAsync());

        // The runtime dropped the queued switch with the stopped turn.
        Assert.Empty(rpc.QueuedModelSwitches);
        Assert.Equal(RunningModel, rpc.LiveModel);
        Assert.Equal(ModelSwitchNotice.None, host.ViewModel.ModelSwitchNotice);

        await host.SendCoreAsync("carry on");

        Assert.Equal(1, rpc.SendCount);
        Assert.Equal(PickedModel, rpc.LiveModel);
        Assert.Equal([$"queue:{PickedModel}", $"model:{PickedModel}", "send"], rpc.Operations);
        Assert.Null(host.Runtime.PendingModelSwitch);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SavedOpusWithLiveSol_OnlySendsIfTheRuntimeAppliesOpus(bool reject, bool ignore)
    {
        await TestCopilot.Shared.ConnectAsync();
        using var host = DeferredSendHost.Create();
        using var rpc = new AbortRpc
        {
            LiveModel = "gpt-6.2-sol",
            RejectImmediateModelSwitches = reject,
            IgnoreImmediateModelSwitches = ignore
        };
        host.Chat.LastModelUsed = "claude-opus-5.5";
        host.ViewModel.ApplyModelSelection(host.Chat.LastModelUsed, reasoningEffort: null);
        host.AttachSession(rpc);

        await host.SendCoreAsync("next step");

        Assert.Equal("claude-opus-5.5", host.Chat.LastModelUsed);
        if (reject || ignore)
        {
            Assert.Equal(0, rpc.SendCount);
            Assert.Contains(host.Chat.Messages, message =>
                message.Role == "error" && message.Content.Contains("Claude Opus 5.5"));
        }
        else
        {
            Assert.Equal(["model:claude-opus-5.5", "send"], rpc.Operations);
            Assert.Equal("claude-opus-5.5", rpc.LiveModel);
        }
    }

    [Fact]
    public async Task Picker_UpdatesTheCachedSessionEvenWithoutADisplayHandle()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            await TestCopilot.Shared.ConnectAsync();
            using var host = DeferredSendHost.Create();
            using var rpc = new AbortRpc { LiveModel = "gpt-6.2-sol" };
            host.ViewModel.ApplyModelSelection("gpt-6.2-sol", null);
            host.Chat.LastModelUsed = "gpt-6.2-sol";
            host.AttachSession(rpc);
            host.ClearDisplayedSession();

            host.ViewModel.SelectedModel = "claude-opus-5.5";

            Assert.Equal("claude-opus-5.5", await rpc.ModelApplied.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("claude-opus-5.5", host.Chat.LastModelUsed);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task UnappliedPickerSwitch_BlocksRepeatedSendsUntilTheRuntimeAcceptsIt()
    {
        await TestCopilot.Shared.ConnectAsync();
        using var host = DeferredSendHost.Create();
        using var rpc = new AbortRpc { LiveModel = "gpt-6.2-sol", IgnoreImmediateModelSwitches = true };
        host.ViewModel.ApplyModelSelection("gpt-6.2-sol", null);
        host.Chat.LastModelUsed = "gpt-6.2-sol";
        host.AttachSession(rpc);

        Assert.False(await host.PickModelAsync("claude-opus-5.5"));
        await host.SendCoreAsync("first follow-up");
        await host.SendCoreAsync("second follow-up");

        Assert.Equal(0, rpc.SendCount);
        Assert.Equal("gpt-6.2-sol", rpc.LiveModel);
        Assert.Equal("claude-opus-5.5", host.ViewModel.SelectedModel);
        Assert.Equal("claude-opus-5.5", host.Chat.LastModelUsed);

        rpc.IgnoreImmediateModelSwitches = false;
        await host.SendCoreAsync("try again");
        Assert.Equal(1, rpc.SendCount);
        Assert.Equal("claude-opus-5.5", rpc.LiveModel);
    }

    [Theory]
    [InlineData("claude-opus-5.5")]
    [InlineData("auto")]
    public async Task PickDuringTheModelCheck_UsesTheNewSelectionBeforeSending(string model)
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            await TestCopilot.Shared.ConnectAsync();
            using var host = DeferredSendHost.Create();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var rpc = new AbortRpc { LiveModel = "gpt-6.2-sol", HoldNextModelRead = release };
            host.Chat.LastModelUsed = rpc.LiveModel;
            host.ViewModel.ApplyModelSelection(rpc.LiveModel, null);
            host.AttachSession(rpc);

            var sending = host.SendCoreAsync("follow-up");
            await rpc.ModelReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            host.ViewModel.SelectedModel = model;
            release.TrySetResult();
            await sending.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, rpc.SendCount);
            Assert.Equal(model, rpc.ModelAtSend);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PauseDuringTheModelCheck_ResumesTheUnsentPromptWithoutAnEmptyContinuation()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            await TestCopilot.Shared.ConnectAsync();
            using var host = DeferredSendHost.Create();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var rpc = new AbortRpc { LiveModel = "gpt-6.2-sol", HoldNextModelRead = release };
            host.Chat.LastModelUsed = rpc.LiveModel;
            host.ViewModel.ApplyModelSelection(rpc.LiveModel, null);
            host.AttachSession(rpc);

            var sending = host.SendCoreAsync("keep this prompt");
            await rpc.ModelReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(await host.ViewModel.TrySetChatPausedAsync(host.Chat, paused: true));
            Assert.False(host.Chat.PauseNeedsContinuation);
            Assert.Equal(0, rpc.AbortCount);

            release.TrySetResult();
            Assert.Null(await host.ViewModel.TrySetChatPausedAsync(host.Chat, paused: false));
            await sending.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, rpc.SendCount);
            Assert.Equal(0, rpc.EmptyBatchCount);
            Assert.Contains("keep this prompt", Assert.Single(rpc.SentMessages).GetProperty("prompt").GetString());
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PlainAutoBeforeSend_ClearsThePreviouslyAppliedAutoTier()
    {
        await TestCopilot.Shared.ConnectAsync();
        using var host = DeferredSendHost.Create();
        using var rpc = new AbortRpc { LiveModel = "auto", LiveAutoTier = "hydrafusion-max" };
        host.Chat.LastModelUsed = "auto";
        host.ViewModel.ApplyModelSelection("auto", null);
        host.AttachSession(rpc);

        await host.SendCoreAsync("use the default Auto route");

        Assert.Equal(["model:auto", "send"], rpc.Operations);
        Assert.Null(rpc.LiveAutoTier);
    }

    [Fact]
    public async Task AssistantReply_UsesTheReportedModelInsteadOfThePicker()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            using var host = DeferredSendHost.Create();
            using var rpc = StartWorkingTurn(host, subscribe: true);
            rpc.Emit(new AssistantTurnStartEvent { Data = new AssistantTurnStartData { TurnId = "0" } });
            rpc.Emit(new AssistantMessageDeltaEvent
            {
                Data = new AssistantMessageDeltaData { MessageId = "answer", DeltaContent = "Output." }
            });
            Dispatcher.UIThread.RunJobs();
            rpc.Emit(new AssistantMessageEvent
            {
                Data = new AssistantMessageData { MessageId = "answer", Content = "Output.", Model = "gpt-6.2-sol" }
            });
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("gpt-6.2-sol", Assert.Single(host.Chat.Messages, m => m.Role == "assistant").Model);
            Assert.Equal(RunningModel, host.Chat.LastModelUsed);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PickingTheRunningModelAgain_TakesTheWaitingSwitchBackOutOfTheQueue()
    {
        await TestCopilot.Shared.ConnectAsync();
        using var host = DeferredSendHost.Create();
        using var rpc = StartWorkingTurn(host);
        Assert.True(await host.PickModelAsync("gpt-5.6-terra"));
        Assert.True(await host.PickModelAsync(PickedModel));

        // A newer pick replaces the one waiting, so the queue never holds two.
        Assert.Equal([PickedModel], rpc.QueuedModelSwitches);
        Assert.Equal(["2"], host.Runtime.PendingModelSwitch!.QueueIds);

        Assert.True(await host.PickModelAsync(RunningModel));

        Assert.Empty(rpc.QueuedModelSwitches);
        Assert.Equal(RunningModel, rpc.LiveModel);
        Assert.Null(host.Runtime.PendingModelSwitch);
        Assert.Equal(ModelSwitchNotice.Applied, host.ViewModel.ModelSwitchNotice);
    }

    [Fact]
    public async Task ReasoningEffortChangedMidReply_AppliesToTheRunningWork()
    {
        await TestCopilot.Shared.ConnectAsync();
        using var host = DeferredSendHost.Create();
        using var rpc = StartWorkingTurn(host);

        Assert.True(await host.PickModelAsync(RunningModel, "high"));

        Assert.Equal([$"model:{RunningModel}"], rpc.Operations);
        Assert.Equal("high", rpc.LiveEffort);
        Assert.Null(host.Runtime.PendingModelSwitch);
        Assert.Equal(ModelSwitchNotice.Applied, host.ViewModel.ModelSwitchNotice);
        Assert.False(host.ViewModel.IsModelSwitchPending);
    }

    [Fact]
    public async Task AutoPickedMidReply_IsTrackedThroughTheQueue()
    {
        await TestCopilot.Shared.ConnectAsync();
        using var host = DeferredSendHost.Create();
        using var rpc = StartWorkingTurn(host);

        // Plain Auto resets its routing, which only SetModelAsync can send; its queue item is read back.
        Assert.True(await host.PickModelAsync("auto"));

        Assert.Equal(["auto"], rpc.QueuedModelSwitches);
        Assert.Equal(["1"], host.Runtime.PendingModelSwitch!.QueueIds);
        Assert.Equal(ModelSwitchNotice.Pending, host.ViewModel.ModelSwitchNotice);
    }

    [Fact]
    public async Task AutoPickedAgainMidReply_StaysQueued()
    {
        await TestCopilot.Shared.ConnectAsync();
        using var host = DeferredSendHost.Create();
        using var rpc = StartWorkingTurn(host);
        rpc.LiveModel = "auto";

        Assert.True(await host.PickModelAsync("auto"));

        // A newer Auto tier can wait behind the one the session runs, and only this pick replaces it.
        Assert.Equal(["auto"], rpc.QueuedModelSwitches);
        Assert.NotNull(host.Runtime.PendingModelSwitch);
    }

    [Fact]
    public async Task ModelOnAnotherEndpointPickedMidReply_TakesOverWithTheNextMessage_WithoutSwitchNow()
    {
        await TestCopilot.Shared.ConnectAsync();
        using var host = DeferredSendHost.Create();
        using var rpc = StartWorkingTurn(host);
        var settings = host.DataStore.Data.Settings;
        settings.ByokEndpoints.Add(new ByokEndpoint
        {
            Id = "e1", Name = "Local", ProviderType = "openai", BaseUrl = "https://api.example.com/v1",
            WireApi = "completions", ApiKeyMode = ByokApiKeyMode.None, IsEnabled = true
        });
        var model = new ByokModel { Id = "m1", EndpointId = "e1", ModelId = "local-model", DisplayName = "Local", IsEnabled = true };
        settings.ByokModels.Add(model);

        // A session can't move to another endpoint, so only the next message's session can use the pick.
        Assert.False(await host.PickModelAsync(ByokConfigHelper.BuildModelToken(model)));

        Assert.Empty(rpc.Operations);
        Assert.True(host.Runtime.PendingModelSwitch!.NeedsNewSession);
        Assert.Equal(ModelSwitchNotice.Pending, host.ViewModel.ModelSwitchNotice);
        Assert.EndsWith(Loc.Get(nameof(Loc.ModelSwitch_NextMessage), string.Empty), host.ViewModel.ModelSwitchNoticeText);
        Assert.False(host.ViewModel.CanSwitchModelNow);

        await host.ViewModel.SwitchModelNowCommand.ExecuteAsync(null);

        Assert.Equal(0, rpc.AbortCount);
        Assert.False(host.Chat.IsPaused);
        Assert.True(host.Runtime.IsBusy);
    }

    [Fact]
    public async Task QueuedSwitchAppliedAtTheEndOfTheReply_NoLongerWaits()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            await TestCopilot.Shared.ConnectAsync();
            using var host = DeferredSendHost.Create();
            using var rpc = StartWorkingTurn(host, subscribe: true);
            Assert.True(await host.PickModelAsync(PickedModel));
            Assert.Equal(ModelSwitchNotice.Pending, host.ViewModel.ModelSwitchNotice);

            rpc.Emit(new SessionModelChangeEvent
            {
                Data = new SessionModelChangeData { PreviousModel = RunningModel, NewModel = PickedModel }
            });
            Dispatcher.UIThread.RunJobs();

            Assert.Null(host.Runtime.PendingModelSwitch);
            Assert.Equal(ModelSwitchNotice.None, host.ViewModel.ModelSwitchNotice);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ByokModelChange_KeepsOnlyTheSelectableAccountModelInThePicker()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            await TestCopilot.Shared.ConnectAsync();
            using var host = DeferredSendHost.Create();
            using var rpc = StartWorkingTurn(host, subscribe: true);
            var settings = host.DataStore.Data.Settings;
            settings.UseBYOKOnly = true;
            settings.ByokEndpoints.Add(new ByokEndpoint
            {
                Id = "e1", Name = "Local", ProviderType = "openai", BaseUrl = "https://api.example.com/v1",
                WireApi = "completions", ApiKeyMode = ByokApiKeyMode.None, IsEnabled = true
            });
            var model = new ByokModel { Id = "m1", EndpointId = "e1", ModelId = "local-model", DisplayName = "Local", IsEnabled = true };
            settings.ByokModels.Add(model);
            var selection = ByokConfigHelper.BuildModelToken(model);
            host.ViewModel.ApplyModelSelection(selection, reasoningEffort: null);
            host.Chat.LastModelUsed = selection;
            host.ViewModel.ApplyAvailableModels([selection], selection);

            var runtimeId = $"{ByokConfigHelper.SessionProviderName}/{model.Id}";
            host.Runtime.PendingModelSwitch = new PendingModelSwitch(runtimeId, []);
            rpc.Emit(new SessionModelChangeEvent
            {
                Data = new SessionModelChangeData { PreviousModel = RunningModel, NewModel = runtimeId }
            });
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(selection, Assert.Single(host.ViewModel.AvailableModels));
            Assert.Equal(selection, host.Runtime.ActiveModelId);
            Assert.Equal(selection, host.Chat.LastModelUsed);
            Assert.Null(host.Runtime.PendingModelSwitch);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SwitchNow_LabelsTheContinuedWorkWithTheNewModel()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            await TestCopilot.Shared.ConnectAsync();
            using var host = DeferredSendHost.Create();
            using var rpc = StartWorkingTurn(host, subscribe: true);
            rpc.Emit(new AssistantTurnStartEvent { Data = new AssistantTurnStartData { TurnId = "0" } });
            rpc.Emit(new AssistantMessageEvent
            {
                Data = new AssistantMessageData { MessageId = "before", Content = "Working on it." }
            });
            Dispatcher.UIThread.RunJobs();
            Assert.True(await host.PickModelAsync(PickedModel));

            await host.ViewModel.SwitchModelNowCommand.ExecuteAsync(null);
            rpc.Emit(new AssistantTurnStartEvent { Data = new AssistantTurnStartData { TurnId = "1" } });
            rpc.Emit(new AssistantMessageEvent
            {
                Data = new AssistantMessageData { MessageId = "after", Content = "Picking the step up again." }
            });
            Dispatcher.UIThread.RunJobs();

            var replies = host.Chat.Messages.Where(message => message.Role == "assistant").ToList();
            Assert.Equal([RunningModel, PickedModel], replies.Select(reply => reply.Model));
        }, CancellationToken.None);
    }

    /// <summary>A chat whose assistant is working on a turn the runtime runs on <see cref="RunningModel"/>.</summary>
    private static AbortRpc StartWorkingTurn(DeferredSendHost host, bool subscribe = false)
    {
        var rpc = new AbortRpc { LiveModel = RunningModel };
        host.ViewModel.ApplyModelSelection(RunningModel, reasoningEffort: null);
        host.Chat.LastModelUsed = RunningModel;
        host.AttachSession(rpc, subscribe);
        host.MarkRuntimeBusy();
        host.PrepareFreshTurn();
        host.ApplyDisplayedRuntimeState();
        rpc.StartTurn();
        return rpc;
    }

    private sealed partial class DeferredSendHost
    {
        public void ClearDisplayedSession() => SetField("_activeSession", null);

        /// <summary>What the composer's picker does: the chat keeps the pick, then the live session gets it.</summary>
        public Task<bool> PickModelAsync(string model, string? effort = null)
        {
            ViewModel.ApplyModelSelection(model, effort);
            Chat.LastModelUsed = model;
            Chat.LastReasoningEffortUsed = effort;
            var method = typeof(ChatViewModel).GetMethod("SwitchModelMidSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("SwitchModelMidSessionAsync was not found.");
            return (Task<bool>)method.Invoke(ViewModel, [model, effort, null, CancellationToken.None])!;
        }
    }

    private sealed partial class AbortRpc
    {
        private readonly List<(string Id, string Model, int Aborts)> _queuedModelSwitches = [];
        private int _nextQueueId;
        private int? _turnAborts;

        /// <summary>The model the runtime runs now.</summary>
        public string? LiveModel { get; set; }
        public string? LiveEffort { get; private set; }
        public string? LiveContextTier { get; private set; }
        public string? LiveAutoTier { get; set; }
        public string? ModelAtSend { get; private set; }
        public TaskCompletionSource? HoldNextModelRead { get; set; }
        public TaskCompletionSource ModelReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ModelQueueRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The session refuses to switch models between turns, as for a model it can't serve.</summary>
        public bool RejectImmediateModelSwitches { get; set; }
        public bool IgnoreImmediateModelSwitches { get; set; }
        public TaskCompletionSource<string> ModelApplied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Model switches applied before the next continuation of an interrupted turn.</summary>
        public int ModelAppliedBeforeContinuations { get; private set; }

        /// <summary>Starts a turn: until an abort ends it, a new model waits in the queue.</summary>
        public void StartTurn() => _turnAborts = AbortCount;

        public void FinishTurnAndApplySwitch()
        {
            _turnAborts = null;
            LiveModel = Assert.Single(_queuedModelSwitches).Model;
            _queuedModelSwitches.Clear();
        }

        /// <summary>The models still waiting in the queue.</summary>
        public IReadOnlyList<string> QueuedModelSwitches
        {
            get
            {
                DropAbortedModelSwitches();
                return _queuedModelSwitches.Select(item => item.Model).ToList();
            }
        }

        private bool TurnRunning => _turnAborts == AbortCount;

        private void DropAbortedModelSwitches() => _queuedModelSwitches.RemoveAll(item => item.Aborts != AbortCount);

        [JsonRpcMethod("session.model.switchTo", UseSingleObjectParameterDeserialization = true)]
        public object SwitchModel(Dictionary<string, object> request)
        {
            DropAbortedModelSwitches();
            var model = Assert.IsType<string>(request["modelId"]);
            var effort = request.GetValueOrDefault("reasoningEffort") as string;
            var effortOnly = string.Equals(model, LiveModel, StringComparison.Ordinal)
                             && effort is not null && !string.Equals(effort, LiveEffort, StringComparison.Ordinal);
            if (TurnRunning && !effortOnly)
            {
                var id = (++_nextQueueId).ToString(CultureInfo.InvariantCulture);
                _queuedModelSwitches.Add((id, model, AbortCount));
                Operations.Add($"queue:{model}");
                return new { modelId = LiveModel, status = "applied", deferred = true, queueId = id };
            }

            if (RejectImmediateModelSwitches)
                throw new LocalRpcException($"Model '{model}' is not available.");
            if (IgnoreImmediateModelSwitches)
                return new { modelId = LiveModel, status = "applied", deferred = false };

            LiveModel = model;
            LiveEffort = effort;
            LiveContextTier = request.GetValueOrDefault("contextTier") as string;
            if (request.TryGetValue("autoTier", out var tier))
                LiveAutoTier = tier as string;
            if (AbortCount > EmptyBatchCount)
                ModelAppliedBeforeContinuations++;
            Operations.Add($"model:{model}");
            ModelApplied.TrySetResult(model);
            return new { modelId = model, status = "applied", deferred = false };
        }

        [JsonRpcMethod("session.model.getCurrent", UseSingleObjectParameterDeserialization = true)]
        public async Task<object> GetCurrentModel(object request)
        {
            if (HoldNextModelRead is { } hold)
            {
                HoldNextModelRead = null;
                ModelReadStarted.TrySetResult();
                await hold.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            return new { modelId = LiveModel, reasoningEffort = LiveEffort, contextTier = LiveContextTier, autoTier = LiveAutoTier };
        }

        [JsonRpcMethod("session.queue.pendingItems", UseSingleObjectParameterDeserialization = true)]
        public object PendingQueueItems(object request)
        {
            DropAbortedModelSwitches();
            ModelQueueRead.TrySetResult();
            return new
            {
                items = _queuedModelSwitches
                    .Select(item => new { id = item.Id, kind = "command", displayText = "/model " + item.Model })
                    .ToArray(),
                steeringMessages = Array.Empty<string>()
            };
        }

        [JsonRpcMethod("session.queue.removeAt", UseSingleObjectParameterDeserialization = true)]
        public object RemoveQueueItem(Dictionary<string, object> request)
        {
            DropAbortedModelSwitches();
            var id = Assert.IsType<string>(request["id"]);
            if (_queuedModelSwitches.RemoveAll(item => item.Id == id) == 0)
                throw new LocalRpcException($"unknown queue item id '{id}'");
            return new { removed = true };
        }

        [JsonRpcMethod("session.plan.read", UseSingleObjectParameterDeserialization = true)]
        public object ReadPlan(object request) => new { exists = false };
    }
}
