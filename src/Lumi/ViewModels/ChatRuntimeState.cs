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
    private bool _isSessionActive;

    public Chat? Chat { get; init; }

    /// <summary>Main-assistant activity only. Background work never makes the assistant busy.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
                return;

            _isBusy = value;
            if (value)
                IsSessionActive = true;
            if (Chat is not null)
                Chat.IsRunning = value;
        }
    }

    /// <summary>
    /// Session work survives assistant.idle. Only session.idle, a completed stop, or a terminal
    /// failure releases it; display readiness must never determine resource ownership.
    /// </summary>
    public bool IsSessionActive
    {
        get => _isSessionActive;
        set
        {
            _isSessionActive = value;
            if (Chat is not null)
                Chat.IsSessionActive = value;
        }
    }

    public bool IsStreaming { get; set; }

    /// <summary>
    /// Tracks the main assistant turn independently of presentation-only streaming updates such as
    /// compaction. Child-agent turn boundaries must not clear the main turn's state.
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
    /// (and blocks session recovery/cleanup) until the sub-agent actually finishes.</summary>
    public int ActiveSubagentExecutionDepth;

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
        => IsSessionActive
           || IsBusy
           || IsStreaming
           || IsStopping
           || HasPendingBackgroundWork
           || ActiveToolCount > 0
           || ActiveSubagentExecutionDepth > 0
           || PendingSessionUserMessageCount > 0;

    /// <summary>
    /// The interrupt request and its UI cleanup own this barrier, not session events.
    /// A successful SDK abort need not emit session.idle (notably for background-only work).
    /// </summary>
    public bool IsStopping
        => StopOperation is { IsCompleted: false } || AbortOperation is { IsCompleted: false };

    public Task<string?>? StopOperation { get; set; }

    public Task<bool>? AbortOperation { get; set; }

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
