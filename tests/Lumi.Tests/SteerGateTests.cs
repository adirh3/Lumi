using System;
using System.Reflection;
using Lumi.Models;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Regression tests for the steering "dead-zone" gate bug. Immediate-mode steering must route to the
/// running turn whenever a live assistant turn is in progress. The old gate keyed on
/// <see cref="ChatRuntimeState.IsStreaming"/>, which is set only at AssistantTurnStart and force-cleared
/// mid-turn by compaction / sub-agent / background-task events (and never re-armed). That left long
/// windows where a mid-turn steer silently fell back to the post-turn queue: the user's message rendered
/// no bubble and was only delivered at turn end. The authoritative signal is
/// <see cref="ChatRuntimeState.TurnInProgress"/>, which stays true for the whole turn.
/// </summary>
public sealed class SteerGateTests
{
    [Fact]
    public void CanSteerImmediately_TrueInPostCompactionDeadZone()
    {
        // The exact reported window: a live turn is running (TurnInProgress) but compaction (or a
        // completed sub-agent / background-task drain) has force-cleared IsStreaming and there is no
        // active tool. The old gate returned false here → invisible, deferred message. It must be true.
        var runtime = new ChatRuntimeState
        {
            Chat = new Chat { Title = "deadzone" },
            TurnInProgress = true,
            IsStreaming = false,
            ActiveToolCount = 0,
            ActiveSubagentExecutionDepth = 0
        };

        Assert.True(InvokeCanSteerImmediately(runtime));
    }

    [Fact]
    public void CanSteerImmediately_TrueWhenToolActive()
    {
        // Defensive OR: an executing tool implies a live turn even if flags were set unusually.
        var runtime = new ChatRuntimeState
        {
            Chat = new Chat { Title = "tool" },
            TurnInProgress = false,
            IsStreaming = false,
            ActiveToolCount = 1,
            ActiveSubagentExecutionDepth = 0
        };

        Assert.True(InvokeCanSteerImmediately(runtime));
    }

    [Fact]
    public void CanSteerImmediately_TrueWhileSubagentExecuting()
    {
        // Defensive OR: a sub-agent completes its wrapping task tool immediately but keeps streaming;
        // depth > 0 must still count as a live, steerable turn.
        var runtime = new ChatRuntimeState
        {
            Chat = new Chat { Title = "subagent" },
            TurnInProgress = false,
            IsStreaming = false,
            ActiveToolCount = 0,
            ActiveSubagentExecutionDepth = 1
        };

        Assert.True(InvokeCanSteerImmediately(runtime));
    }

    [Fact]
    public void CanSteerImmediately_FalseWhenTurnEnded()
    {
        // No live turn: the runtime may still be busy draining background work, but there is no step
        // boundary to interject into. Steering must fall back to the deferred queue (fresh turn).
        var runtime = new ChatRuntimeState
        {
            Chat = new Chat { Title = "ended" },
            TurnInProgress = false,
            IsStreaming = false,
            ActiveToolCount = 0,
            ActiveSubagentExecutionDepth = 0,
            HasPendingBackgroundWork = true
        };

        Assert.False(InvokeCanSteerImmediately(runtime));
    }

    [Fact]
    public void MarkRuntimeWaitingForSessionIdle_ClearsTurnInProgress()
    {
        // Turn end funnels through this helper; it must drop TurnInProgress so a post-turn steer queues.
        var runtime = new ChatRuntimeState
        {
            Chat = new Chat { Title = "turn-end" },
            TurnInProgress = true,
            IsStreaming = true
        };

        InvokePrivateStatic(typeof(ChatViewModel), "MarkRuntimeWaitingForSessionIdle", runtime);

        Assert.False(runtime.TurnInProgress);
        Assert.False(runtime.IsStreaming);
        Assert.False(InvokeCanSteerImmediately(runtime));
    }

    [Fact]
    public void MarkRuntimeTerminal_ClearsTurnInProgress()
    {
        // Every terminal/abort/error path funnels through MarkRuntimeTerminal.
        var runtime = new ChatRuntimeState
        {
            Chat = new Chat { Title = "terminal" },
            TurnInProgress = true,
            IsBusy = true,
            IsStreaming = true,
            ActiveSubagentExecutionDepth = 2
        };

        InvokePrivateStatic(typeof(ChatViewModel), "MarkRuntimeTerminal", runtime, null);

        Assert.False(runtime.TurnInProgress);
        Assert.False(InvokeCanSteerImmediately(runtime));
    }

    [Fact]
    public void MarkRuntimeActive_WhenStreaming_SetsTurnInProgress()
    {
        // Turn initiation (send / resend / AssistantTurnStart) marks the runtime actively streaming. This
        // must set TurnInProgress so a rapid second message sent in the window BEFORE the server's
        // AssistantTurnStart still steers immediately (renders a bubble) instead of falling to the queue.
        var runtime = new ChatRuntimeState
        {
            Chat = new Chat { Title = "send" },
            TurnInProgress = false,
            IsStreaming = false
        };

        InvokePrivateStatic(typeof(ChatViewModel), "MarkRuntimeActive", runtime, "Thinking", true, false);

        Assert.True(runtime.TurnInProgress);
        Assert.True(InvokeCanSteerImmediately(runtime));
    }

    [Fact]
    public void MarkRuntimeActive_MidTurnNonStreamingUpdate_PreservesTurnInProgress()
    {
        // The crux of the fix: compaction / sub-agent / background-task events update the runtime with
        // isStreaming:false while the turn is still live. They must NOT clear TurnInProgress, or the
        // original dead-zone bug (invisible, turn-end-deferred steer) returns.
        var runtime = new ChatRuntimeState
        {
            Chat = new Chat { Title = "compacting" },
            TurnInProgress = true,
            IsStreaming = true
        };

        InvokePrivateStatic(typeof(ChatViewModel), "MarkRuntimeActive", runtime, "Compacting", false, false);

        Assert.False(runtime.IsStreaming);
        Assert.True(runtime.TurnInProgress);
        Assert.True(InvokeCanSteerImmediately(runtime));
    }

    [Fact]
    public void MarkRuntimeWaitingForSessionIdle_KeepBusyDrain_DoesNotResurrectTurnInProgress()
    {
        // When the turn ends but background work must drain, MarkRuntimeWaitingForSessionIdle clears
        // TurnInProgress and then re-marks the runtime busy via MarkRuntimeActive(isStreaming:false). That
        // keep-busy call must not resurrect TurnInProgress, so a post-turn steer still queues (no live turn).
        var runtime = new ChatRuntimeState
        {
            Chat = new Chat { Title = "draining" },
            TurnInProgress = true,
            IsStreaming = true,
            HasPendingBackgroundWork = true
        };

        InvokePrivateStatic(typeof(ChatViewModel), "MarkRuntimeWaitingForSessionIdle", runtime);

        Assert.True(runtime.IsBusy);
        Assert.False(runtime.TurnInProgress);
        Assert.False(InvokeCanSteerImmediately(runtime));
    }

    private static bool InvokeCanSteerImmediately(ChatRuntimeState runtime)
        => InvokePrivateStatic<bool>(typeof(ChatViewModel), "CanSteerImmediately", runtime);

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
}
