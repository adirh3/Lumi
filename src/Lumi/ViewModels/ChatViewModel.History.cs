using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    /// <summary>
    /// A transcript builder wired to this surface. The chat's own builder follows the live tail and
    /// reports sub-agent runs; history builds each older exchange on its own.
    /// </summary>
    internal TranscriptBuilder CreateTranscriptBuilder(Action? subagentRunsChanged)
        => new(
            _dataStore,
            showDiffAction: item => DiffShowRequested?.Invoke(item),
            submitQuestionAnswerAction: SubmitQuestionAnswer,
            beginEditMessageAction: BeginComposerEdit,
            resendFromMessageAction: ResendFromMessageAsync,
            openSkillAction: OpenSkillPreview,
            resolveSkill: name => FindSkillReferenceByName(name),
            openChatAction: id => OpenChatRequested?.Invoke(id),
            getSelectedModel: () => SelectedModel,
            sendSteeredNowAsync: SendSteeredNowAsync,
            openSubagentRunAction: OpenSubagentRun,
            subagentRunsChanged: subagentRunsChanged,
            resolveFilePath: path => CurrentChat is { } chat
                ? ResolveWorkspaceFileChangedPath(chat, path)
                : path,
            openReplySourceAction: RequestReplySourceJump,
            appItemFor: GetAppItem);

    /// <summary>True when <paramref name="message"/> belongs to the chat's stored history, before its live tail.</summary>
    private bool IsStoredHistoryMessage(Chat chat, ChatMessage message)
        => message.StorageSeq > 0
           && ReferenceEquals(_history.Chat, chat)
           && message.StorageSeq < _dataStore.GetTranscriptWindow(chat).TailStartSeq;

    /// <summary>
    /// What an edit of <paramref name="message"/> is made in. An older message is cut from storage
    /// with everything after it and sent again as a new one, so its edit goes in a copy that keeps its
    /// place: nothing saved before the cut rewrites the stored message, and a cut that fails leaves it
    /// as it was.
    /// </summary>
    private ChatMessage EditTargetFor(Chat chat, ChatMessage message)
    {
        if (!IsStoredHistoryMessage(chat, message))
            return message;

        var copy = message.Clone();
        copy.StorageSeq = message.StorageSeq;
        return copy;
    }

    /// <summary>True while the open chat has stored history before its live tail.</summary>
    internal bool HasTranscriptHistory => _history.HasHistory && ReferenceEquals(_history.Chat, CurrentChat);

    internal TranscriptHistory TranscriptHistory => _history;

    /// <summary>Raised after older exchanges were built into the transcript or released from it.</summary>
    internal event Action? TranscriptHistoryChanged;

    // Older exchanges are built after the transcript was laid out, so give them the state the
    // surface applies to every turn.
    private void OnHistoryTurnsHydrated(IReadOnlyList<TranscriptTurn> turns)
    {
        UpdateUserMessageEditState();
        UpdateReplySourceHighlight();
        PlaceHistoryPlanCard();
    }

    // A saved plan's card follows the chat's last answer. When the live tail has none (its message
    // went unanswered), that answer is in an older exchange, which gets the card each time it is built.
    private HistoryPlanCard? _historyPlanCard;

    private sealed class HistoryPlanCard(string stableId)
    {
        public string StableId { get; } = stableId;

        /// <summary>The newest older exchange with an answer, once it was built.</summary>
        public TranscriptHistorySegment? Segment { get; set; }

        /// <summary>Newer exchanges that were built and have no answer.</summary>
        public HashSet<TranscriptHistorySegment> Unanswered { get; } = [];
    }

    /// <summary>Shows the chat's saved plan as a card after the chat's last answer.</summary>
    private void RestorePlanCard()
    {
        _historyPlanCard = null;
        if (HasTranscriptHistory
            && !TranscriptTurns.Any(static turn => turn.HistorySegment is null && TranscriptBuilder.ShowsAnswer(turn)))
        {
            _historyPlanCard = new HistoryPlanCard(TranscriptIds.Create("plan-card"));
            PlaceHistoryPlanCard();
            return;
        }

        _transcriptBuilder.AppendPlanCardToLastTurn(Loc.Plan_Title, () => PlanShowRequested?.Invoke());
    }

    private void PlaceHistoryPlanCard()
    {
        if (_historyPlanCard is not { } plan || !HasTranscriptHistory)
            return;

        if (plan.Segment is null)
        {
            var segments = _history.Segments;
            for (var i = segments.Count - 1; i >= 0 && plan.Segment is null; i--)
            {
                var segment = segments[i];
                if (plan.Unanswered.Contains(segment))
                    continue;
                if (segment.Turns is not { } built)
                    return;
                if (built.Any(TranscriptBuilder.ShowsAnswer))
                    plan.Segment = segment;
                else
                    plan.Unanswered.Add(segment);
            }

            if (plan.Segment is null)
            {
                // No exchange has an answer: the card goes last.
                _historyPlanCard = null;
                _transcriptBuilder.AppendPlanCardToLastTurn(Loc.Plan_Title, () => PlanShowRequested?.Invoke());
                return;
            }
        }

        if (plan.Segment.Turns is { } turns && !turns.Any(turn => turn.Items.Any(item => item.StableId == plan.StableId)))
            TranscriptBuilder.TryAppendAfterLastAnswer(turns, new PlanCardItem(Loc.Plan_Title, () => PlanShowRequested?.Invoke(), plan.StableId));
    }

    /// <summary>
    /// Every message the transcript currently shows: the live tail and the older exchanges that are
    /// built. Lookups for a message the reader acted on (an answer's form, a question) search these,
    /// so they find the very object the transcript renders.
    /// </summary>
    private IEnumerable<ChatMessage> EnumerateShownMessages(Chat chat)
    {
        foreach (var message in chat.Messages)
            yield return message;
        if (ReferenceEquals(_history.Chat, chat))
        {
            foreach (var message in _history.HydratedMessages)
                yield return message;
        }
    }

    /// <summary>
    /// Makes sure a message of the open chat is part of the transcript: a message of the live tail
    /// already is; one of an older exchange has that exchange built. Returns false when the message
    /// is not in the chat.
    /// </summary>
    internal async Task<bool> EnsureMessageInTranscriptAsync(Guid messageId, bool includeFoldedSteps = false)
    {
        if (CurrentChat is not { } chat)
            return false;
        if (chat.Messages.Any(message => message.Id == messageId))
            return true;
        if (!HasTranscriptHistory)
            return false;

        var revealed = await _history.RevealMessageAsync(messageId).ConfigureAwait(true);
        if (revealed is null || !ReferenceEquals(CurrentChat, chat))
            return false;

        // Older exchanges keep the steps of their collapsed rows unbuilt; build them when the message
        // may be one of those steps.
        if (includeFoldedSteps && _history.FindHydratedSegment(messageId) is { } segment)
            await _history.BuildFoldedStepsAsync(segment).ConfigureAwait(true);
        return ReferenceEquals(CurrentChat, chat);
    }

    /// <summary>True when <paramref name="turn"/> shows (or stands in for) an older exchange.</summary>
    internal static bool IsHistoryTurn(TranscriptTurn turn) => turn.HistorySegment is not null;

    /// <summary>
    /// True when <paramref name="turn"/> stands in for an older exchange that is not built and starts with
    /// a message of the user's own: jumps between the user's messages stop there, building it first
    /// (<see cref="BuildHistoryExchangeAsync"/>).
    /// </summary>
    internal static bool StandsInForUserMessage(TranscriptTurn turn)
        => turn.HistorySegment is TranscriptHistorySegment { StartsWithUserMessage: true } && TranscriptHistory.IsPlaceholder(turn);

    /// <summary>Builds the older exchange <paramref name="placeholder"/> stands in for: its turns, or null when it is not one.</summary>
    internal async Task<IReadOnlyList<TranscriptTurn>?> BuildHistoryExchangeAsync(TranscriptTurn placeholder)
        => placeholder.HistorySegment is TranscriptHistorySegment segment
            ? await _history.EnsureHydratedAsync(segment).ConfigureAwait(true)
            : null;

    /// <summary>
    /// Rebuilds the transcript for a setting that changes how it is built (tool calls, reasoning,
    /// timestamps): the older exchanges already built are released too, and are built again with the
    /// setting as they come back into view.
    /// </summary>
    internal void RebuildTranscriptAndHistory()
    {
        _history.ReleaseAll();
        RebuildTranscript();
    }

    /// <summary>
    /// Storage could not cut the history to send an older message again (a full disk, say): the chat
    /// stays as stored — an edit was made in a copy (<see cref="EditTargetFor"/>) — and the edit goes
    /// back into the composer to be sent again.
    /// </summary>
    private void RestoreUnsentHistoryEdit(ChatMessage userMessage, bool wasEdited)
    {
        StatusText = Loc.Get("Chat_ResendSaveFailed");
        if (wasEdited)
            ReturnEditToComposer(userMessage);
    }

    /// <summary>
    /// Puts an edit that was not sent back in the composer: as the edit it was, or — when the chat
    /// cannot be edited now, because another reply has started — as a draft, so the text is kept and
    /// the selection the edit gave the chat reaches its session with the next turn.
    /// </summary>
    private void ReturnEditToComposer(ChatMessage edited)
    {
        BeginComposerEdit(edited);
        if (IsEditingMessage)
            return;

        PromptText = edited.Content;
        if (CurrentChat is { } chat)
            InvalidateSessionConfiguration(chat);
    }

    /// <summary>
    /// How many messages may build up before the newest exchange of a live tail before they move to
    /// the history: below this, a long session costs little enough to keep as it is.
    /// </summary>
    internal const int TailRollThreshold = 300;

    /// <summary>
    /// Moves the exchanges before the newest one from the live tail into the chat's history once the
    /// assistant is ready and they have grown long, leaving memory as reopening the chat would: the
    /// newest exchange live, the older ones owned by the history, which releases them once the reader
    /// leaves them. Their turns stay on screen as they are. A chat that is not settled — answering,
    /// holding sends, unsaved content or being rebuilt — is left for the next idle.
    /// </summary>
    internal async Task RollTailIntoHistoryAsync(Chat chat)
    {
        if (FindRollableTail(chat) is not { } keepFrom)
            return;

        try
        {
            // What moves must be stored, and the Workspace lists the moved exchanges from storage:
            // their rows are read before anything changes.
            var (count, last) = (chat.Messages.Count, chat.Messages[^1]);
            await _dataStore.SaveChatAsync(chat).ConfigureAwait(true);
            bool Unchanged() => FindRollableTail(chat) == keepFrom && chat.Messages.Count == count && ReferenceEquals(chat.Messages[^1], last);
            if (!Unchanged())
                return;

            var rows = await ReadWorkspaceHistoryRowsAsync(chat, chat.Messages[keepFrom].StorageSeq).ConfigureAwait(true);
            if (Unchanged())
                CommitTailRoll(chat, keepFrom, rows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceWarning("[Transcript] Could not move older exchanges into history: {0}", ex.Message);
        }
    }

    // Where the tail would start after a roll — its newest exchange, with any sends held by Pause
    // after it, as a reopened chat reads it — or null when the chat should not roll now.
    private int? FindRollableTail(Chat chat)
    {
        if (!ReferenceEquals(CurrentChat, chat)
            || !_dataStore.PersistsTranscripts
            || !_dataStore.Data.Settings.AutoSaveChats
            || IsBusy || IsStreaming || IsLoadingChat || IsEditingMessage
            || _isBulkLoadingMessages || _transcriptBuilder.IsRebuildingTranscript
            || HasPendingAssistantWork(chat.Id) || _inProgressMessages.ContainsKey(chat.Id)
            || _history.Chat is { } shown && !ReferenceEquals(shown, chat)
            || !_dataStore.GetTranscriptWindow(chat).IsLoaded)
        {
            return null;
        }

        var keepFrom = chat.Messages.FindLastIndex(static message => message.Role == "user" && !message.IsPendingPausedSend);

        // While the session still works in the background, an exchange that work can change — a tool
        // call still running, such as a background agent, or a shell still running — stays live, and
        // so does everything after it.
        if (keepFrom > 0 && _runtimeStates.TryGetValue(chat.Id, out var runtime) && runtime.IsSessionActive)
        {
            var shells = runtime.RunningBackgroundShells;
            var firstLive = chat.Messages.FindIndex(0, keepFrom, message =>
                message.ToolStatus == "InProgress"
                || message.ToolCallId is { } toolCallId && shells.ContainsKey(toolCallId));
            if (firstLive >= 0)
                keepFrom = chat.Messages.FindLastIndex(firstLive, static message => message.Role == "user" && !message.IsPendingPausedSend);
        }

        if (keepFrom < TailRollThreshold)
            return null;
        for (var i = 0; i < keepFrom; i++)
        {
            if (chat.Messages[i].IsPendingPausedSend)
                return null;
        }

        return keepFrom;
    }

    // Moves the messages before keepFrom into the history: storage's window first, then the turns
    // showing them become the history's, the live builder and view models let go of them, and the
    // Workspace lists them from the rows just read.
    private void CommitTailRoll(Chat chat, int keepFrom, WorkspaceHistoryRows rows)
    {
        var rolled = chat.Messages.GetRange(0, keepFrom);
        var tailStartSeq = chat.Messages[keepFrom].StorageSeq;
        if (SplitRolledExchanges(rolled, chat.Messages[keepFrom]) is not { } exchanges)
        {
            Trace.TraceWarning("[Transcript] The live tail's turns do not line up with its exchanges; it stays live.");
            return;
        }

        if (!_dataStore.TryRollTail(chat, keepFrom))
            return;

        _history.AdoptRolledTail(chat, TranscriptTurns, exchanges, chat.Messages, tailStartSeq, GetForkOrigin(chat));
        ResetWorkspaceHistory();
        _workspaceHistory = CreateWorkspaceHistoryItems(chat, rows);
        // The Workspace lists a moved run from its latest step; the steps this session counted stay.
        foreach (var run in _workspaceHistory.Runs)
        {
            if (run.ToolCallId is { } toolCallId
                && _transcriptBuilder.SubagentRuns.FirstOrDefault(live => live.ToolCallId == toolCallId) is { } live)
            {
                run.KnownStepCount = live.StepCount;
            }
        }

        _transcriptBuilder.ForgetMessages(rolled, exchanges.SelectMany(static exchange => exchange.Turns).ToList());

        var rolledSet = rolled.ToHashSet(ReferenceEqualityComparer.Instance);
        _isBulkLoadingMessages = true;
        try
        {
            var kept = Messages.Where(viewModel => !rolledSet.Contains(viewModel.Message)).ToList();
            Messages.Clear();
            foreach (var viewModel in kept)
                Messages.Add(viewModel);
        }
        finally
        {
            _isBulkLoadingMessages = false;
        }

        RebuildWorkspacePanel();
        RefreshSubagentRunState();
        RefreshHeaderDetails();
    }

    /// <summary>
    /// The rolled messages' exchanges with the live turns that show them, in order: each exchange
    /// from its user message's turn up to the next one's (what precedes the first user message joins
    /// the first exchange), the last one up to the kept exchange's first turn. Null when a user
    /// message has no turn of its own, so turns and messages cannot be matched.
    /// </summary>
    private List<(List<TranscriptTurn> Turns, List<ChatMessage> Messages)>? SplitRolledExchanges(List<ChatMessage> rolled, ChatMessage kept)
    {
        // A user message starts a turn of its own (see TranscriptBuilder.ProcessChatMessage).
        var turns = TranscriptTurns.Where(static turn => turn.HistorySegment is null).ToList();
        var keptStart = IndexOfExchangeTurn(turns, kept);
        if (keptStart < 0 || rolled[0].Role == "user" && IndexOfExchangeTurn(turns, rolled[0]) < 0)
            return null;

        var exchanges = new List<(List<TranscriptTurn>, List<ChatMessage>)>();
        var turnIndex = 0;
        for (var start = 0; start < rolled.Count;)
        {
            var end = start + 1;
            while (end < rolled.Count && rolled[end].Role != "user")
                end++;
            var turnEnd = end < rolled.Count ? IndexOfExchangeTurn(turns, rolled[end]) : keptStart;
            if (turnEnd < turnIndex)
                return null;
            exchanges.Add((turns.GetRange(turnIndex, turnEnd - turnIndex), rolled.GetRange(start, end - start)));
            turnIndex = turnEnd;
            start = end;
        }

        return exchanges;
    }

    private static int IndexOfExchangeTurn(List<TranscriptTurn> turns, ChatMessage user)
    {
        var message = $"turn:message:{user.Id}";
        var jobWake = $"turn:job-wake:{user.Id}";
        return turns.FindIndex(turn => turn.StableId == message || turn.StableId == jobWake);
    }

    /// <summary>
    /// Searches the open chat's older exchanges in storage. Returns, in transcript order, each
    /// exchange with matches and how many — the hits it will show once it is built.
    /// </summary>
    internal async Task<IReadOnlyList<(TranscriptHistorySegment Segment, int Count)>> SearchStoredHistoryAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (CurrentChat is not { } chat || !HasTranscriptHistory)
            return [];

        var matches = await _dataStore.SearchHistoryTextAsync(
            chat, query, includeReasoning: _dataStore.Data.Settings.ShowReasoning, cancellationToken).ConfigureAwait(true);
        if (!ReferenceEquals(CurrentChat, chat))
            return [];

        var result = new List<(TranscriptHistorySegment Segment, int Count)>();
        foreach (var (seq, occurrences) in matches)
        {
            if (_history.FindSegment(seq) is not { } segment)
                continue;
            if (result.Count > 0 && ReferenceEquals(result[^1].Segment, segment))
                result[^1] = (segment, result[^1].Count + occurrences);
            else
                result.Add((segment, occurrences));
        }

        return result;
    }

    /// <summary>
    /// Builds an older exchange with the steps of its collapsed rows, so search hits inside it can be
    /// found and revealed. Returns its turns, or null when it is no longer part of the chat.
    /// </summary>
    internal async Task<IReadOnlyList<TranscriptTurn>?> BuildExchangeForSearchAsync(TranscriptHistorySegment segment)
    {
        var turns = await _history.EnsureHydratedAsync(segment).ConfigureAwait(true);
        if (turns is null)
            return null;
        await _history.BuildFoldedStepsAsync(segment).ConfigureAwait(true);
        return segment.Turns;
    }

    /// <summary>Makes sure the turn with the given stable id is in the transcript, building its exchange if needed.</summary>
    internal async Task<TranscriptTurn?> EnsureTurnInTranscriptAsync(string stableId)
    {
        // Workspace rows of older exchanges name the message and step they jump to.
        if (stableId.StartsWith(StoredJumpPrefix, StringComparison.Ordinal))
            return await ResolveStoredJumpAsync(stableId).ConfigureAwait(true);

        var turn = TranscriptTurns.FirstOrDefault(candidate => candidate.StableId == stableId);
        if (turn is not null || !HasTranscriptHistory)
            return turn;

        // Turn ids derive from the message that starts them ("turn:message:{id}").
        const string messagePrefix = "turn:message:";
        if (stableId.StartsWith(messagePrefix, StringComparison.Ordinal)
            && Guid.TryParse(stableId.AsSpan(messagePrefix.Length), out var messageId)
            && await EnsureMessageInTranscriptAsync(messageId).ConfigureAwait(true))
        {
            return TranscriptTurns.FirstOrDefault(candidate => candidate.StableId == stableId);
        }

        return null;
    }
}
