using System;
using System.Reflection;
using Lumi.Models;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Regression tests for the sub-agent false-idle busy bug. The SDK completes the wrapping
/// <c>task</c> tool as soon as a sub-agent is spawned, so <see cref="ChatRuntimeState.ActiveToolCount"/>
/// drops to 0 while the sub-agent keeps streaming for tens of seconds. The busy-state machine must
/// treat <see cref="ChatRuntimeState.ActiveSubagentExecutionDepth"/> as active work so the post-tool
/// reconciliation safety net does not prematurely mark the turn terminal (which showed the session as
/// idle while the sub-agent was still running).
/// </summary>
public sealed class BusyStateSubagentTests
{
    [Theory]
    [InlineData("task")]
    [InlineData("agent:explore")]
    public void ResolveToolStartStatus_EarlySuccessfulSubagentWrapper_RemainsInProgress(string toolName)
    {
        var status = ChatViewModel.ResolveToolStartStatus(toolName, "Completed");

        Assert.Equal("InProgress", status);
    }

    [Fact]
    public void ResolveToolStartStatus_FailedSubagentWrapper_RemainsFailed()
    {
        var status = ChatViewModel.ResolveToolStartStatus("task", "Failed");

        Assert.Equal("Failed", status);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("agent:explore")]
    public void ShouldApplyToolExecutionCompletionStatus_SuccessfulSubagentWrapper_IsDeferred(string toolName)
    {
        Assert.False(ChatViewModel.ShouldApplyToolExecutionCompletionStatus(toolName, success: true));
    }

    [Fact]
    public void ShouldApplyToolExecutionCompletionStatus_NormalToolOrFailure_IsApplied()
    {
        Assert.True(ChatViewModel.ShouldApplyToolExecutionCompletionStatus("web_search", success: true));
        Assert.True(ChatViewModel.ShouldApplyToolExecutionCompletionStatus("task", success: false));
    }

    [Fact]
    public void ShouldReconcileSubagentToolsOnTurnEnd_NestedSubagentTurn_IsDeferred()
    {
        // A sub-agent's own turns raise assistant.turn.end while it is still working, so turn end
        // must not settle the chat's sub-agent tools until every agent has reported in.
        Assert.False(ChatViewModel.ShouldReconcileSubagentToolsOnTurnEnd(1));

        // Fan-out: still deferred until the LAST agent reports.
        Assert.False(ChatViewModel.ShouldReconcileSubagentToolsOnTurnEnd(2));

        Assert.True(ChatViewModel.ShouldReconcileSubagentToolsOnTurnEnd(0));
    }

    [Fact]
    public void SettlingARunningSubagentEarly_PermanentlyDestroysItsRealDuration()
    {
        // Why the turn-end guard has to exist. A sub-agent's first inner turn ends milliseconds in;
        // the unguarded reconcile settled the card there, and because MarkToolFinished is one-shot
        // the authoritative subagent.completed 20 seconds later could no longer correct it — which
        // is exactly how a 20-second run came to render as "Done 131 ms".
        var startedAt = DateTimeOffset.UtcNow;
        var agent = CreateToolMessage("agent:explore", "InProgress", "agent-1");
        agent.MarkToolStarted(startedAt);
        var chat = new Chat { Title = "early settle", Messages = [agent] };

        // The premature turn-end reconcile, moments after the agent started.
        ChatViewModel.SetInProgressSubagentStatuses(chat, "Completed");
        var frozenDurationMs = agent.ToolDurationMs;
        Assert.NotNull(frozenDurationMs);

        // The real terminal event, 20 seconds later, is now powerless.
        Assert.False(agent.MarkToolFinished(startedAt.AddSeconds(20)));
        Assert.Equal(frozenDurationMs, agent.ToolDurationMs);
        Assert.True(agent.ToolDurationMs < 1_000, "the run's real duration was lost");
    }

    [Fact]
    public void SessionIdle_SettlesASubagentWhoseTerminalEventNeverArrived()
    {
        // Turn end now defers while the depth is up, so idle is the last line of defence: without
        // it a lost subagent.completed leaves the card spinning forever, persisted that way.
        var startedAt = DateTimeOffset.UtcNow;
        var agent = CreateToolMessage("agent:explore", "InProgress", "agent-1");
        agent.MarkToolStarted(startedAt);
        var chat = new Chat { Title = "lost terminal event", Messages = [agent] };

        // The depth stayed pinned, so turn end correctly declined to settle the card.
        var runtime = new ChatRuntimeState { Chat = chat, ActiveSubagentExecutionDepth = 1 };
        Assert.False(ChatViewModel.ShouldReconcileSubagentToolsOnTurnEnd(
            runtime.ActiveSubagentExecutionDepth));
        Assert.Equal("InProgress", agent.ToolStatus);

        // session.idle marks the runtime terminal (which zeroes the depth), then settles the card.
        InvokeMarkRuntimeTerminal(runtime);
        Assert.Equal(0, runtime.ActiveSubagentExecutionDepth);
        ChatViewModel.SetInProgressSubagentStatuses(chat, "Completed");

        Assert.Equal("Completed", agent.ToolStatus);
        Assert.NotNull(agent.ToolDurationMs);
    }

    private static void InvokeMarkRuntimeTerminal(ChatRuntimeState runtime)
        => typeof(ChatViewModel)
            .GetMethod("MarkRuntimeTerminal", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [runtime, null]);

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Stopped")]
    public void SetInProgressSubagentStatuses_ChangesOnlySubagentMessages(string terminalStatus)
    {
        var task = CreateToolMessage("task", "InProgress", "agent-1");
        var namedAgent = CreateToolMessage("agent:explore", "InProgress", "agent-2");
        var normalTool = CreateToolMessage("web_search", "InProgress", "search-1");
        var alreadyTerminal = CreateToolMessage("task", "Completed", "agent-3");
        var chat = new Chat
        {
            Title = "terminal fallback",
            Messages = [task, namedAgent, normalTool, alreadyTerminal]
        };

        var changed = ChatViewModel.SetInProgressSubagentStatuses(chat, terminalStatus);

        Assert.Equal(2, changed.Count);
        Assert.Equal(terminalStatus, task.ToolStatus);
        Assert.Equal(terminalStatus, namedAgent.ToolStatus);
        Assert.Equal("InProgress", normalTool.ToolStatus);
        Assert.Equal("Completed", alreadyTerminal.ToolStatus);
    }

    [Fact]
    public void HasActiveWork_TrueWhileSubagentExecuting()
    {
        var runtime = new ChatRuntimeState
        {
            Chat = new Chat { Title = "subagent" },
            ActiveSubagentExecutionDepth = 1
        };

        Assert.True(runtime.HasActiveWork);
    }

    [Fact]
    public void ShouldKeepRuntimeBusyUntilSessionIdle_TrueWhileSubagentExecuting()
    {
        var runtime = new ChatRuntimeState
        {
            Chat = new Chat { Title = "subagent" },
            ActiveSubagentExecutionDepth = 1
        };

        var keepBusy = InvokePrivateStatic<bool>(
            typeof(ChatViewModel), "ShouldKeepRuntimeBusyUntilSessionIdle", runtime);

        Assert.True(keepBusy);
    }

    [Fact]
    public void ShouldRecoverCompletedTurnIfIdleIsMissing_FalseWhileSubagentExecuting()
    {
        // Mirrors the live repro: the wrapping task tool has completed (ActiveToolCount == 0)
        // and the turn looks ended, but the sub-agent is still executing.
        var runtime = new ChatRuntimeState
        {
            Chat = new Chat { Title = "subagent" },
            PendingSessionUserMessageCount = 1,
            ActiveToolCount = 0,
            ActiveSubagentExecutionDepth = 1,
            HasPendingBackgroundWork = false,
            IsStreaming = false
        };

        var recover = InvokePrivateStatic<bool>(
            typeof(ChatViewModel), "ShouldRecoverCompletedTurnIfIdleIsMissing", runtime);

        Assert.False(recover);
    }

    [Fact]
    public void PostToolReconciliation_NotEligibleWhileSubagentExecuting()
    {
        var runtime = new ChatRuntimeState
        {
            Chat = new Chat { Title = "subagent" },
            PendingSessionUserMessageCount = 1,
            ActiveToolCount = 0,
            ActiveSubagentExecutionDepth = 1,
            IsStreaming = false
        };

        var eligible = InvokePrivateStatic<bool>(
            typeof(ChatViewModel), "IsPostToolReconciliationEligible", runtime, false);

        Assert.False(eligible);
    }

    [Fact]
    public void PostToolReconciliation_NotEligibleWhileStreaming()
    {
        var runtime = new ChatRuntimeState
        {
            Chat = new Chat { Title = "streaming" },
            PendingSessionUserMessageCount = 1,
            ActiveToolCount = 0,
            ActiveSubagentExecutionDepth = 0,
            IsStreaming = true
        };

        var eligible = InvokePrivateStatic<bool>(
            typeof(ChatViewModel), "IsPostToolReconciliationEligible", runtime, false);

        Assert.False(eligible);
    }

    [Fact]
    public void PostToolReconciliation_EligibleWhenTurnTrulyStalled()
    {
        // Positive control: with no sub-agent and no streaming, the safety net must still be able
        // to recover a genuinely stalled turn (a missing session.idle).
        var runtime = new ChatRuntimeState
        {
            Chat = new Chat { Title = "stalled" },
            PendingSessionUserMessageCount = 1,
            ActiveToolCount = 0,
            ActiveSubagentExecutionDepth = 0,
            IsStreaming = false
        };

        var eligible = InvokePrivateStatic<bool>(
            typeof(ChatViewModel), "IsPostToolReconciliationEligible", runtime, false);

        Assert.True(eligible);
    }

    [Fact]
    public void MarkRuntimeTerminal_ResetsSubagentExecutionDepth()
    {
        var chat = new Chat { Title = "terminal" };
        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            IsStreaming = true,
            HasPendingBackgroundWork = true,
            ActiveSubagentExecutionDepth = 2
        };

        InvokePrivateStatic(typeof(ChatViewModel), "MarkRuntimeTerminal", runtime, null);

        Assert.Equal(0, runtime.ActiveSubagentExecutionDepth);
        Assert.False(runtime.IsBusy);
        Assert.False(runtime.IsStreaming);
        Assert.False(runtime.HasPendingBackgroundWork);
        Assert.False(chat.IsRunning);
    }

    private static T InvokePrivateStatic<T>(Type type, string name, params object?[] args)
        => (T)(type
            .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?.Invoke(null, args)
            ?? throw new InvalidOperationException($"Static method {name} was not found."));

    private static void InvokePrivateStatic(Type type, string name, params object?[] args)
    {
        var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Static method {name} was not found.");
        method.Invoke(null, args);
    }

    private static ChatMessage CreateToolMessage(string toolName, string status, string toolCallId)
        => new()
        {
            Role = "tool",
            ToolName = toolName,
            ToolStatus = status,
            ToolCallId = toolCallId,
            Content = "{}"
        };
}
