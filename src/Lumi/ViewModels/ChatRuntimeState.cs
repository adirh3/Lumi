using Lumi.Models;

namespace Lumi.ViewModels;

internal enum ContextTokenLimitSource
{
    Unknown,
    Catalog,
    Session
}

internal sealed class ChatRuntimeState
{
    private bool _isBusy;

    public Chat? Chat { get; init; }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
                return;

            _isBusy = value;
            if (Chat is not null)
                Chat.IsRunning = value;
        }
    }

    public bool IsStreaming { get; set; }

    /// <summary>
    /// True while a live assistant turn is running. Set at turn initiation — the same point
    /// <see cref="IsStreaming"/> is set true (see <c>MarkRuntimeActive</c>, invoked on send / resend /
    /// <c>AssistantTurnStart</c>) — and cleared only at turn end / terminal / abort / error. This is the
    /// "a live assistant turn is running" signal used with submitted-turn tracking to decide whether a
    /// steer can be injected via immediate mode. Unlike <see cref="IsStreaming"/>, it is NOT cleared
    /// mid-turn by
    /// compaction, sub-agent, or background-task events (each of which forces <see cref="IsStreaming"/>
    /// to false for the rest of the turn). A separate sub-agent barrier below prevents immediate mode
    /// from delivering a user steer into the nested agent rather than the parent.
    /// </summary>
    public bool TurnInProgress { get; set; }

    public string StatusText { get; set; } = string.Empty;

    public long TotalInputTokens { get; set; }

    public long TotalOutputTokens { get; set; }

    /// <summary>Latest authoritative session context token count.</summary>
    public long ContextCurrentTokens { get; set; }

    public bool HasExactContextUsage { get; set; }

    /// <summary>Context window token limit from the active session, or catalog fallback before a session reports usage.</summary>
    public long ContextTokenLimit { get; set; }

    public ContextTokenLimitSource ContextTokenLimitSource { get; set; }

    public string? ContextTokenLimitModelId { get; set; }

    public string? ContextTokenLimitTier { get; set; }

    public string? ActiveModelId { get; set; }

    public string? ActiveContextWindowTier { get; set; }

    public int ActiveToolCount { get; set; }

    /// <summary>Number of sub-agents currently executing. The SDK completes the wrapping
    /// <c>task</c> tool as soon as a sub-agent is spawned, so <see cref="ActiveToolCount"/>
    /// drops to 0 while the sub-agent keeps streaming. This counter keeps the session busy
    /// (and blocks idle-recovery) until the sub-agent actually finishes.</summary>
    public int ActiveSubagentExecutionDepth;

    /// <summary>
    /// Set when a nested sub-agent starts and kept until a fresh user turn is prepared. Copilot SDK
    /// immediate mode targets the next LLM request in the session and has no parent-agent target, so
    /// steering after delegation can otherwise inject the user's message into that sub-agent (or a
    /// later sibling) instead of the root agent.
    /// </summary>
    public bool DeferSteersUntilNextTurn;

    /// <summary>
    /// True after the SDK emits AssistantTurnStart for the current submitted prompt. Unlike
    /// <see cref="TurnInProgress"/>, this stays false during worktree/session/MCP setup, so "Send now"
    /// can defer its abort until there is a real turn to interrupt.
    /// </summary>
    public bool AssistantTurnStarted;

    /// <summary>
    /// A setup-time "Send now" request waiting for <see cref="AssistantTurnStarted"/>. The queued
    /// message is already moved to the front; once the event arrives Lumi aborts the first turn and
    /// drains that message through the ready session.
    /// </summary>
    public bool SendQueuedNowWhenTurnStarts;

    /// <summary>
    /// Expected user-message count after the current prompt has been handed to the SDK. This remains
    /// zero during worktree/session/MCP setup, even though the UI is already busy.
    /// </summary>
    public int PendingSessionUserMessageCount { get; set; }

    public int PendingAssistantMessageCount { get; set; }

    public long PendingTurnSequence { get; set; }

    public long LifecycleTurnSequence { get; set; }

    public CancellationTokenSource? PostToolReconciliationCts { get; set; }

    /// <summary>True while the SDK has background shells/agents in flight.
    /// Keeps the session alive without blocking the UI until session.idle arrives.</summary>
    public bool HasPendingBackgroundWork { get; set; }

    /// <summary>Async shells still running in the background for this chat (root tool-call id →
    /// authoritative start time). Unlike the transcript builder's transient maps, this survives
    /// transcript rebuilds, so switching away and back re-materializes the live terminal card in its
    /// running state (visible, expanded, correct elapsed clock) instead of a folded "finished" pill.</summary>
    public Dictionary<string, DateTimeOffset> RunningBackgroundShells { get; } = new(StringComparer.Ordinal);

    public bool HasActiveWork
        => IsBusy
           || IsStreaming
           || AwaitingStopIdle
           || HasPendingBackgroundWork
           || ActiveToolCount > 0
           || ActiveSubagentExecutionDepth > 0
           || PendingSessionUserMessageCount > 0;

    /// <summary>
    /// True after a user Stop has been accepted by the SDK but before the resulting
    /// <c>session.idle</c>. Copilot's own abort contract waits for idle before sending again; keeping
    /// this state active prevents a queued or newly typed message from overlapping the abort tail.
    /// </summary>
    public bool AwaitingStopIdle { get; set; }

    /// <summary>True when the user explicitly clicked Stop for the current turn.
    /// Unexpected SDK aborts must not be mistaken for this state.</summary>
    public bool ManualStopRequested { get; set; }

    /// <summary>
    /// Armed when a normal turn-start user message is sent; the SDK echoes exactly one
    /// <c>UserMessageEvent</c> when the agent consumes that prompt. Steer-confirmation consumes (and clears)
    /// this flag on that first echo so the turn-start message is never mistaken for a steer consumption —
    /// steers are only ever injected AFTER the turn is already running. Reset at turn end / terminal so it
    /// can't leak into a later turn. UI-thread only.
    /// </summary>
    public bool ExpectTurnStartUserEcho { get; set; }

}
