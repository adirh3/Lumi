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
    /// authoritative "a live assistant turn is running" signal used to decide whether a steer can be
    /// injected via immediate mode. Unlike <see cref="IsStreaming"/>, it is NOT cleared mid-turn by
    /// compaction, sub-agent, or background-task events (each of which forces <see cref="IsStreaming"/>
    /// to false for the rest of the turn), so a message steered during any of those phases still routes
    /// to the running turn's next step boundary instead of silently falling back to the post-turn queue
    /// (which renders no bubble and only drains at turn end).
    /// </summary>
    public bool TurnInProgress { get; set; }

    public string StatusText { get; set; } = string.Empty;

    public long TotalInputTokens { get; set; }

    public long TotalOutputTokens { get; set; }

    /// <summary>Latest turn's input tokens — best proxy for current context window usage.</summary>
    public long ContextCurrentTokens { get; set; }

    /// <summary>Context window token limit from the active session, or catalog fallback before a session reports usage.</summary>
    public long ContextTokenLimit { get; set; }

    public ContextTokenLimitSource ContextTokenLimitSource { get; set; }

    public string? ActiveModelId { get; set; }

    public string? ActiveContextWindowTier { get; set; }

    public int ActiveToolCount { get; set; }

    /// <summary>Number of sub-agents currently executing. The SDK completes the wrapping
    /// <c>task</c> tool as soon as a sub-agent is spawned, so <see cref="ActiveToolCount"/>
    /// drops to 0 while the sub-agent keeps streaming. This counter keeps the session busy
    /// (and blocks idle-recovery) until the sub-agent actually finishes.</summary>
    public int ActiveSubagentExecutionDepth;

    public int PendingSessionUserMessageCount { get; set; }

    public int PendingAssistantMessageCount { get; set; }

    public long PendingTurnSequence { get; set; }

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
           || HasPendingBackgroundWork
           || ActiveToolCount > 0
           || ActiveSubagentExecutionDepth > 0
           || PendingSessionUserMessageCount > 0;

    /// <summary>True when the user explicitly clicked Stop for the current turn.
    /// Unexpected SDK aborts must not be mistaken for this state.</summary>
    public bool ManualStopRequested { get; set; }

}
