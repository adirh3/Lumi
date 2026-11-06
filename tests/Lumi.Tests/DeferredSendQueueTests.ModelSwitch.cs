using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using StreamJsonRpc;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Changing the model while Lumi works. The fake runtime behaves as the real one was measured to: a new
/// model picked while a turn runs waits in the queue until the turn ends, an abort drops it with the turn,
/// and a reasoning effort (or the model already running) applies at once.
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
    public async Task SwitchNow_WhenTheSessionRejectsThePick_LeavesItForTheNextMessage()
    {
        await TestCopilot.Shared.ConnectAsync();
        using var host = DeferredSendHost.Create();
        using var rpc = StartWorkingTurn(host);
        Assert.True(await host.PickModelAsync(PickedModel));
        rpc.RejectImmediateModelSwitches = true;

        await host.ViewModel.SwitchModelNowCommand.ExecuteAsync(null);

        // The work went on with the model it had, and says so instead of claiming the switch.
        Assert.Equal(1, rpc.EmptyBatchCount);
        Assert.Equal(RunningModel, rpc.LiveModel);
        Assert.True(host.Runtime.PendingModelSwitch!.NeedsNewSession);
        Assert.Equal(ModelSwitchNotice.Pending, host.ViewModel.ModelSwitchNotice);
        Assert.EndsWith(Loc.Get(nameof(Loc.ModelSwitch_NextMessage), string.Empty), host.ViewModel.ModelSwitchNoticeText);
        Assert.False(host.ViewModel.CanSwitchModelNow);
        Assert.True(host.ConsumeSessionRefresh());
    }

    [Fact]
    public async Task SwitchStillWaitingWhenTheNextMessageStarts_IsReplacedNotDuplicated()
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

            await host.SendCoreAsync("next step");
            await rpc.SendReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, rpc.AbortCount);
            Assert.Equal([PickedModel], rpc.QueuedModelSwitches);
            Assert.Equal(["2"], host.Runtime.PendingModelSwitch!.QueueIds);
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

        /// <summary>The session refuses to switch models between turns, as for a model it can't serve.</summary>
        public bool RejectImmediateModelSwitches { get; set; }

        /// <summary>Model switches applied before the next continuation of an interrupted turn.</summary>
        public int ModelAppliedBeforeContinuations { get; private set; }

        /// <summary>Starts a turn: until an abort ends it, a new model waits in the queue.</summary>
        public void StartTurn() => _turnAborts = AbortCount;

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
            if (TurnRunning && !string.Equals(model, LiveModel, StringComparison.Ordinal))
            {
                var id = (++_nextQueueId).ToString(CultureInfo.InvariantCulture);
                _queuedModelSwitches.Add((id, model, AbortCount));
                Operations.Add($"queue:{model}");
                return new { modelId = LiveModel, status = "applied", deferred = true, queueId = id };
            }

            if (RejectImmediateModelSwitches)
                throw new LocalRpcException($"Model '{model}' is not available.");

            LiveModel = model;
            LiveEffort = request.GetValueOrDefault("reasoningEffort") as string;
            if (AbortCount > EmptyBatchCount)
                ModelAppliedBeforeContinuations++;
            Operations.Add($"model:{model}");
            return new { modelId = model, status = "applied", deferred = false };
        }

        [JsonRpcMethod("session.queue.pendingItems", UseSingleObjectParameterDeserialization = true)]
        public object PendingQueueItems(object request)
        {
            DropAbortedModelSwitches();
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
