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
