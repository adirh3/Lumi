using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

/// <summary>
/// Chat forking: turning the open chat into an independent branch. Lumi prefers a real server-side
/// Copilot session fork so the branch continues with the model's actual working memory; the chat
/// copy itself is built by <see cref="Lumi.Services.ChatForkFactory"/> and owned by MainViewModel.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>
    /// Seeds the composer with an editable draft and puts the caret at the end. Used by "fork and
    /// edit", where the user's own turn becomes the opening prompt of the new branch instead of a
    /// copied message that nothing has answered.
    /// </summary>
    internal void SetComposerDraft(string draft)
    {
        PromptText = draft;
        FocusComposerAtEndRequested?.Invoke();
    }

    /// <summary>
    /// Raised when the user picks a fork action on a transcript message. Carries the chat to
    /// fork and the message to fork through. Handled by <c>MainViewModel</c>, which owns chat
    /// creation and navigation.
    /// </summary>
    public event Action<Chat, Guid>? ForkChatRequested;

    /// <summary>
    /// Invoked by the view when a message raises Strata's fork request, so the transcript does not
    /// need a direct reference to <c>MainViewModel</c>.
    /// </summary>
    public void RequestForkFromMessage(Guid messageId)
    {
        if (CurrentChat is { } chat)
            ForkChatRequested?.Invoke(chat, messageId);
    }

    /// <summary>
    /// The current chat's breadcrumb back to its source, or null when it was not created by
    /// duplicating or forking.
    /// </summary>
    private ForkOrigin? GetCurrentForkOrigin()
        => CurrentChat?.ForkedFromChatId is Guid originId
            ? new ForkOrigin(originId, CurrentChat.ForkedFromTitle, CurrentChat.ForkedFromMessage)
            : null;

    /// <summary>
    /// Describes how a chat surface relates to a server session that is about to be forked.
    /// </summary>
    internal enum ForkSessionHold
    {
        /// <summary>The surface does not hold this session.</summary>
        None,

        /// <summary>The surface holds a usable live handle, which the fork may borrow.</summary>
        Live,

        /// <summary>
        /// The surface owns the session but has detached its handle for recovery. Nobody may resume
        /// or fork it: the id is absent from the SDK registry, so a second resume would succeed and
        /// then destroy the session out from under the recovery that is about to re-adopt it.
        /// </summary>
        Recovering
    }

    /// <summary>
    /// Reports whether this surface holds <paramref name="sessionId"/>, and lends its live handle
    /// when it has one. Forking borrows the handle instead of paying for a second resume, and — more
    /// importantly — this is what keeps a server session from ever being held twice at once.
    /// </summary>
    /// <remarks>
    /// All three holders are checked, because a surface can own a session it is not displaying:
    /// <c>_activeSession</c> is only the visible chat, <c>_sessionCache</c> also covers chats that
    /// are merely running or were left cached by <c>ClearChat</c>, and <c>_sessionsPendingResume</c>
    /// holds handles detached mid-recovery. Missing the last two used to let the fork resume a
    /// second handle for the same session.
    /// </remarks>
    internal ForkSessionHold GetForkSessionHold(string sessionId, out CopilotSession? live)
    {
        live = null;

        if (_sessionsPendingResume.Values.Any(s => Matches(s, sessionId)))
            return ForkSessionHold.Recovering;

        if (_activeSession is { } active && Matches(active, sessionId))
        {
            live = active;
            return ForkSessionHold.Live;
        }

        foreach (var cached in _sessionCache.Values)
        {
            if (!Matches(cached, sessionId)) continue;
            live = cached;
            return ForkSessionHold.Live;
        }

        return ForkSessionHold.None;

        static bool Matches(CopilotSession session, string sessionId)
            => string.Equals(session.SessionId, sessionId, StringComparison.Ordinal);
    }

    /// <summary>
    /// How long the native fork may take before the duplicate stops waiting and falls back to
    /// replaying its copied transcript.
    /// </summary>
    /// <remarks>
    /// A fork from a live handle costs ~20ms and one that has to resume its own ~180ms, so this is
    /// never reached in normal use. It exists because resuming a session whose data is missing — or
    /// whose CLI host is wedged — can leave the call outstanding indefinitely, and a duplicate that
    /// never opens is far worse than one that replays its transcript.
    /// </remarks>
    private static readonly TimeSpan NativeForkBudget = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Forks <paramref name="sessionId"/> at <paramref name="retainedUserTurns"/> user turns, using
    /// an already-live handle when one is supplied and otherwise resuming a short-lived bare handle.
    /// Returns the forked session id, or null when the branch point cannot be resolved, the attempt
    /// fails, or it outruns <see cref="NativeForkBudget"/> — the caller then leaves
    /// <c>CopilotSessionId</c> null and the transcript-replay path takes over.
    /// </summary>
    /// <param name="live">
    /// A handle some surface already holds for this session, or null to resume one. Reusing a live
    /// handle both skips the resume and keeps the "one holder per session" invariant intact.
    /// </param>
    internal static async Task<string?> ForkSessionAtTurnAsync(
        CopilotService copilot,
        string sessionId,
        CopilotSession? live,
        int retainedUserTurns,
        string? name,
        CancellationToken ct = default)
    {
        var attempt = live is not null
            ? ForkFromHandleAsync(live, ct)
            : copilot.UseResumedSessionReadOnlyAsync(sessionId, ForkFromHandleAsync, ct);

        try
        {
            // Bounded by wall clock rather than by cancelling the attempt: a resume that has stopped
            // responding is exactly the case that would ignore a token too. The abandoned attempt is
            // left to settle on its own — it releases its own handle if it ever completes.
            return await attempt.WaitAsync(NativeForkBudget, ct);
        }
        catch (Exception ex)
        {
            Observe(attempt);
            Debug.WriteLine($"[Lumi] Native fork unavailable for session {sessionId}: {ex.Message}");
            return null;
        }

        async Task<string?> ForkFromHandleAsync(CopilotSession session, CancellationToken token)
        {
            var events = await session.GetEventsAsync(token);

            // The first turn the fork must NOT contain is the (retainedUserTurns)-th genuine user
            // turn, so the cut is the event just before it. Injected SDK/CLI user messages have no
            // local counterpart and are skipped, so the ordinals line up.
            var cut = PendingTurnRecoveryAnalyzer.SelectForkCutEvent(events, retainedUserTurns);
            if (!cut.Resolved)
                return null;

            return await copilot.ForkSessionAsync(sessionId, cut.Event?.Id.ToString(), name, token);
        }

        // Keeps an abandoned or already-failed attempt from surfacing as an unobserved fault.
        static void Observe(Task task)
            => _ = task.ContinueWith(
                static t => Debug.WriteLine(
                    $"[Lumi] Abandoned native fork settled: {t.Exception?.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }
}
