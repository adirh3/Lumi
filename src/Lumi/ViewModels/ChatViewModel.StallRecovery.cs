using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    private static readonly TimeSpan SilentTurnRecoveryTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PostToolReconciliationDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PostToolActiveRecoveryDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PersistedRecoveryTailQuietPeriod = TimeSpan.FromMilliseconds(500);
    private const int PostToolReconciliationMaxAttempts = 3;

    private enum PostToolRecoveryProbeResult
    {
        NoChange,
        ActiveWorkObserved,
        Applied
    }

    private async Task<IReadOnlyList<SessionEvent>?> TryGetSessionEventsAsync(
        CopilotSession session,
        CancellationToken ct)
    {
        try
        {
            return await session.GetEventsAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    private async Task<PendingTurnRecoveryAnalysis> AnalyzePendingTurnRecoveryAsync(
        CopilotSession session,
        int expectedSessionUserMessageCount,
        CancellationToken ct)
    {
        var persistedSnapshot = await PendingTurnRecoveryAnalyzer.TryAnalyzeSessionLogSnapshotAsync(
            session.SessionId,
            expectedSessionUserMessageCount,
            ct);
        var persistedAnalysis = persistedSnapshot?.Analysis;
        // Prefer a decisive local snapshot over retransferring the entire session history, which can
        // be hundreds of megabytes for long-running Sol chats. Active tools remain a polling signal;
        // terminal/completed snapshots require a quiet tail so a temporary EOF cannot end a session.
        if (persistedAnalysis is { UserMessageObserved: true, ActiveToolCount: > 0 })
            return persistedAnalysis;

        if (persistedAnalysis is { UserMessageObserved: true }
            && (persistedAnalysis.TerminalState != PendingTurnTerminalState.None
                || persistedAnalysis.AssistantTurnEnded))
        {
            if (persistedSnapshot is not null
                && await PendingTurnRecoveryAnalyzer.IsLogSnapshotStableAsync(
                    persistedSnapshot,
                    PersistedRecoveryTailQuietPeriod,
                    ct))
            {
                return persistedAnalysis;
            }

            // Never let an unstable persisted terminal override fresher live state in Merge.
            persistedAnalysis = null;
        }

        PendingTurnRecoveryAnalysis? liveAnalysis = null;
        var liveEvents = await TryGetSessionEventsAsync(session, ct);
        if (liveEvents is not null)
            liveAnalysis = PendingTurnRecoveryAnalyzer.Analyze(liveEvents, expectedSessionUserMessageCount);

        return PendingTurnRecoveryAnalyzer.Merge(liveAnalysis, persistedAnalysis);
    }

    private void SyncRecoveredAssistantMessages(
        Chat chat,
        IReadOnlyList<RecoveredAssistantMessage> recoveredAssistantMessages)
    {
        if (recoveredAssistantMessages.Count == 0)
            return;

        var author = chat.AgentId.HasValue
            ? _dataStore.Data.Agents.FirstOrDefault(agent => agent.Id == chat.AgentId.Value)?.Name ?? Loc.Author_Lumi
            : Loc.Author_Lumi;
        foreach (var assistantMessage in recoveredAssistantMessages)
        {
            var recoveredMessage = new ChatMessage
            {
                Role = "assistant",
                Author = author,
                Content = assistantMessage.Content,
                IsStreaming = false,
                Model = ResolveSelectedModelForChat(chat)
            };
            chat.Messages.Add(recoveredMessage);

            if (CurrentChat?.Id == chat.Id)
                Messages.Add(new ChatMessageViewModel(recoveredMessage));
        }

        if (CurrentChat?.Id == chat.Id)
            ScrollToEndRequested?.Invoke();

        QueueSaveChat(chat, saveIndex: true, touchIndex: true);
    }

    private async Task FinalizeRecoveredAssistantMessagesAsync(Chat chat)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var runtime = GetOrCreateRuntimeState(chat.Id);
            ReconcileInProgressSubagentTools(chat, "Completed");
            PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.TurnEnd);
            MarkRuntimeTerminal(runtime);
            PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.Idle);
            if (CurrentChat?.Id == chat.Id)
                ApplyDisplayedRuntimeState(runtime);
        });
    }

    private async Task<bool> WaitForRecoveredTurnAsync(
        CopilotSession session,
        Chat chat,
        int expectedSessionUserMessageCount,
        int assistantCountBeforeRecovery,
        CancellationToken ct)
    {
        var sawRecoveredTurnActivity = false;
        var turnActivity = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = session.On<SessionEvent>(evt =>
        {
            switch (evt)
            {
                case AssistantTurnStartEvent:
                case AssistantReasoningEvent:
                case AssistantReasoningDeltaEvent:
                case AssistantMessageDeltaEvent:
                case AssistantMessageEvent:
                case ToolExecutionStartEvent:
                case ToolExecutionPartialResultEvent:
                case ToolExecutionProgressEvent:
                case ToolExecutionCompleteEvent:
                case AssistantTurnEndEvent:
                    sawRecoveredTurnActivity = true;
                    turnActivity.TrySetResult(true);
                    break;
                case SessionIdleEvent:
                    turnActivity.TrySetResult(true);
                    break;
                case SessionErrorEvent err:
                    turnActivity.TrySetException(new InvalidOperationException(err.Data.Message));
                    break;
            }
        });

        try
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(TimeSpan.FromSeconds(8));
            await turnActivity.Task.WaitAsync(waitCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }

        var recoveredAnalysis = await AnalyzePendingTurnRecoveryAsync(
            session,
            expectedSessionUserMessageCount,
            ct);
        if (await ApplyRecoveredTurnStateAsync(chat, recoveredAnalysis))
            return true;

        if (recoveredAnalysis.ActiveToolCount > 0)
        {
            SchedulePostToolReconciliation(chat.Id, treatCompletedTurnAsIdle: true);
            return true;
        }

        return sawRecoveredTurnActivity || CountCompletedAssistantMessages(chat) > assistantCountBeforeRecovery;
    }

    private void PreparePendingTurnTracking(
        Chat chat,
        int expectedSessionUserMessageCount,
        int localAssistantMessageCount)
    {
        var runtime = GetOrCreateRuntimeState(chat.Id);
        CancellationTokenSource? oldPostToolReconciliationCts;

        lock (runtime)
        {
            oldPostToolReconciliationCts = runtime.PostToolReconciliationCts;
            runtime.PostToolReconciliationCts = null;
            runtime.PendingTurnSequence++;
            runtime.PendingSessionUserMessageCount = expectedSessionUserMessageCount;
            runtime.PendingAssistantMessageCount = localAssistantMessageCount;
            runtime.ActiveToolCount = 0;
            Volatile.Write(ref runtime.DeferSteersUntilNextTurn, false);
            Volatile.Write(ref runtime.AssistantTurnStarted, false);
            runtime.ManualStopRequested = false;
        }

        oldPostToolReconciliationCts?.Cancel();
        oldPostToolReconciliationCts?.Dispose();
    }

    private void ClearPendingTurnTracking(Guid chatId)
    {
        if (!_runtimeStates.TryGetValue(chatId, out var runtime))
            return;

        CancellationTokenSource? postToolReconciliationCts;
        lock (runtime)
        {
            postToolReconciliationCts = runtime.PostToolReconciliationCts;
            runtime.PostToolReconciliationCts = null;
            runtime.PendingSessionUserMessageCount = 0;
            runtime.PendingAssistantMessageCount = 0;
            runtime.ActiveToolCount = 0;
            runtime.PendingTurnSequence++;
        }

        postToolReconciliationCts?.Cancel();
        postToolReconciliationCts?.Dispose();
    }

    private bool AdjustPendingToolCount(Guid chatId, int delta)
    {
        if (!_runtimeStates.TryGetValue(chatId, out var runtime))
            return false;

        lock (runtime)
        {
            if (runtime.PendingSessionUserMessageCount <= 0)
                return false;

            var previousCount = runtime.ActiveToolCount;
            runtime.ActiveToolCount = Math.Max(0, runtime.ActiveToolCount + delta);
            return delta < 0 && previousCount > 0 && runtime.ActiveToolCount == 0;
        }
    }

    private void SetManualStopRequested(Guid chatId, bool requested)
    {
        var runtime = GetOrCreateRuntimeState(chatId);
        lock (runtime)
            runtime.ManualStopRequested = requested;
    }

    private void ClearManualStopRequested(Guid chatId)
    {
        if (!_runtimeStates.TryGetValue(chatId, out var runtime))
            return;

        lock (runtime)
            runtime.ManualStopRequested = false;
    }

    /// <summary>
    /// True when the user stopped the turn that is now terminating.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT one-shot. A single abort is observed by more than one terminal handler (the
    /// <c>AbortEvent</c> stream handler and the recovery probe), and a flag that cleared itself on first
    /// read left whichever handler ran second seeing <c>false</c> — classifying a user stop as a broken
    /// session, which surfaced the false "Copilot stopped responding" banner and discarded the user's
    /// queued sends. <see cref="PreparePendingTurnTracking"/> resets it when the next turn starts, so the
    /// intent cannot leak past the turn it belongs to.
    /// </remarks>
    private bool WasManualStopRequested(Guid chatId)
    {
        if (!_runtimeStates.TryGetValue(chatId, out var runtime))
            return false;

        lock (runtime)
            return runtime.ManualStopRequested;
    }

    private string GetUnexpectedAbortMessage()
        => _copilotService.State is ConnectionState.Disconnected or ConnectionState.Error
            ? "Connection to Copilot was lost."
            : Loc.Status_CopilotStoppedResponding;

    /// <summary>Surfaces an unexpected mid-turn abort as a retryable connection-loss style failure.
    /// Call only on the UI thread.</summary>
    private void ApplyUnexpectedAbortState(Chat chat, string message, bool updateDisplayedChatUi = true)
    {
        InvalidateLocalSessionCache(chat);

        var runtime = GetOrCreateRuntimeState(chat.Id);
        ReconcileInProgressSubagentTools(chat, "Failed", updateDisplayedChatUi);
        MarkRuntimeTerminal(runtime, message);
        // The run died before delivering anything deferred; the user gets abort/retry instead.
        FailQueuedBusySends(chat.Id);

        if (updateDisplayedChatUi && CurrentChat?.Id == chat.Id)
        {
            _transcriptBuilder.HideTypingIndicator();
            _transcriptBuilder.CloseCurrentToolGroup();
            _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
            _transcriptBuilder.FlushPendingFileEdits();
            IsBusy = false;
            IsStreaming = false;
            StatusText = runtime.StatusText;
            _transcriptBuilder.AddConnectionLostError(
                message,
                new RelayCommand(() => _ = RetryAfterConnectionLossAsync()));
            ScrollToEndRequested?.Invoke();
        }

        QueueSaveChat(chat, saveIndex: false, releaseIfInactive: CurrentChat?.Id != chat.Id);
    }

    private void SetPendingToolCount(Guid chatId, int count)
    {
        if (!_runtimeStates.TryGetValue(chatId, out var runtime))
            return;

        lock (runtime)
        {
            if (runtime.PendingSessionUserMessageCount <= 0)
                return;

            runtime.ActiveToolCount = Math.Max(0, count);
        }
    }

    private void SetPendingSessionUserMessageCount(Guid chatId, int expectedSessionUserMessageCount)
    {
        if (!_runtimeStates.TryGetValue(chatId, out var runtime))
            return;

        lock (runtime)
        {
            if (runtime.PendingSessionUserMessageCount <= 0)
                return;

            runtime.PendingSessionUserMessageCount = Math.Max(1, expectedSessionUserMessageCount);
        }
    }

    private void SchedulePostToolReconciliation(Guid chatId, bool treatCompletedTurnAsIdle = false)
    {
        if (!_runtimeStates.TryGetValue(chatId, out var runtime))
            return;

        CancellationTokenSource? oldReconciliationCts;
        CancellationTokenSource? newReconciliationCts;
        long sequence;
        lock (runtime)
        {
            var ready = IsPostToolReconciliationEligible(runtime, treatCompletedTurnAsIdle);
            if (!ready)
                return;

            oldReconciliationCts = runtime.PostToolReconciliationCts;
            newReconciliationCts = new CancellationTokenSource();
            runtime.PostToolReconciliationCts = newReconciliationCts;
            sequence = runtime.PendingTurnSequence;
        }

        oldReconciliationCts?.Cancel();
        oldReconciliationCts?.Dispose();
        _ = RunPostToolReconciliationAsync(chatId, sequence, newReconciliationCts, treatCompletedTurnAsIdle);
    }

    private async Task RunPostToolReconciliationAsync(
        Guid chatId,
        long sequence,
        CancellationTokenSource reconciliationCts,
        bool treatCompletedTurnAsIdle = false)
    {
        try
        {
            var failedAttempts = 0;
            var nextDelay = PostToolReconciliationDelay;
            while (failedAttempts < PostToolReconciliationMaxAttempts)
            {
                await Task.Delay(nextDelay, reconciliationCts.Token);

                if (!_runtimeStates.TryGetValue(chatId, out var runtime))
                    return;

                lock (runtime)
                {
                    var stillEligible = IsPostToolReconciliationEligible(runtime, treatCompletedTurnAsIdle);
                    if (runtime.PendingTurnSequence != sequence || !stillEligible)
                        return;
                }

                try
                {
                    using var recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(reconciliationCts.Token);
                    recoveryCts.CancelAfter(SilentTurnRecoveryTimeout);
                    var result = await TryApplyCurrentTurnRecoveryAsync(
                        chatId,
                        sequence,
                        recoveryCts.Token,
                        treatCompletedTurnAsIdle);
                    if (result == PostToolRecoveryProbeResult.Applied)
                        return;

                    if (result == PostToolRecoveryProbeResult.ActiveWorkObserved)
                    {
                        nextDelay = PostToolActiveRecoveryDelay;
                        continue;
                    }
                }
                catch (OperationCanceledException) when (!reconciliationCts.IsCancellationRequested)
                {
                    // A timed-out probe should not cancel the remaining reconciliation attempts.
                }

                failedAttempts++;
                nextDelay = PostToolReconciliationDelay;
            }
        }
        catch (OperationCanceledException) when (reconciliationCts.IsCancellationRequested)
        {
        }
        finally
        {
            if (_runtimeStates.TryGetValue(chatId, out var runtime))
            {
                lock (runtime)
                {
                    if (ReferenceEquals(runtime.PostToolReconciliationCts, reconciliationCts))
                        runtime.PostToolReconciliationCts = null;
                }
            }

            reconciliationCts.Dispose();
        }
    }

    private async Task<PostToolRecoveryProbeResult> TryApplyCurrentTurnRecoveryAsync(
        Guid chatId,
        long sequence,
        CancellationToken ct,
        bool treatCompletedTurnAsIdle = false)
    {
        var chat = _dataStore.Data.Chats.FirstOrDefault(c => c.Id == chatId);
        if (chat is null || !_runtimeStates.TryGetValue(chatId, out var runtime))
            return PostToolRecoveryProbeResult.NoChange;

        int pendingSessionUserMessageCount;
        lock (runtime)
        {
            if (runtime.PendingSessionUserMessageCount <= 0 || runtime.PendingTurnSequence != sequence)
                return PostToolRecoveryProbeResult.NoChange;

            pendingSessionUserMessageCount = runtime.PendingSessionUserMessageCount;
        }

        var currentSession = _sessionCache.GetValueOrDefault(chatId);
        if (currentSession is null)
            return PostToolRecoveryProbeResult.NoChange;

        var analysis = await AnalyzePendingTurnRecoveryAsync(
            currentSession,
            pendingSessionUserMessageCount,
            ct);

        lock (runtime)
        {
            var stillEligible = IsPostToolReconciliationEligible(runtime, treatCompletedTurnAsIdle);
            if (runtime.PendingTurnSequence != sequence || !stillEligible)
                return PostToolRecoveryProbeResult.NoChange;
        }

        if (await ApplyRecoveredTurnStateAsync(chat, analysis, treatCompletedTurnAsIdle))
            return PostToolRecoveryProbeResult.Applied;

        return analysis.UserMessageObserved && analysis.ActiveToolCount > 0
            ? PostToolRecoveryProbeResult.ActiveWorkObserved
            : PostToolRecoveryProbeResult.NoChange;
    }

    private async Task<bool> ApplyRecoveredTurnStateAsync(
        Chat chat,
        PendingTurnRecoveryAnalysis analysis,
        bool treatCompletedTurnAsIdle = false)
    {
        if (!analysis.UserMessageObserved)
            return false;

        await ApplyRecoveredToolStatusesAsync(chat, analysis);
        await SyncRecoveredTurnAssistantMessagesAsync(chat, analysis);

        switch (analysis.TerminalState)
        {
            case PendingTurnTerminalState.Idle:
                await ApplyRecoveredIdleAsync(chat);
                return true;

            case PendingTurnTerminalState.Error:
                await ApplyRecoveredErrorAsync(chat, analysis.ErrorMessage ?? Loc.Status_CopilotStoppedResponding);
                return true;

            case PendingTurnTerminalState.Abort:
                await ApplyRecoveredAbortAsync(chat);
                return true;

            case PendingTurnTerminalState.Shutdown:
                await ApplyRecoveredShutdownAsync(chat);
                return true;
        }

        if (analysis.ActiveToolCount > 0)
            return false;

        SetPendingToolCount(chat.Id, 0);

        if (treatCompletedTurnAsIdle && CanTreatCompletedTurnAsIdle(analysis))
        {
            await ApplyRecoveredIdleAsync(chat);
            return true;
        }

        return false;
    }

    private static bool ShouldRecoverCompletedTurnIfIdleIsMissing(ChatRuntimeState runtime)
        => runtime.PendingSessionUserMessageCount > 0
           && runtime.ActiveToolCount == 0
           && runtime.ActiveSubagentExecutionDepth == 0
           && !runtime.HasPendingBackgroundWork
           && !runtime.IsStreaming;

    /// <summary>Eligibility for the post-tool reconciliation safety net. The non-idle branch
    /// must also be blocked while a sub-agent is executing or the model is actively streaming —
    /// the wrapping <c>task</c> tool completes immediately, so <see cref="ChatRuntimeState.ActiveToolCount"/>
    /// alone does not reflect sub-agent work and would otherwise let recovery mark the turn terminal early.</summary>
    private static bool IsPostToolReconciliationEligible(ChatRuntimeState runtime, bool treatCompletedTurnAsIdle)
        => treatCompletedTurnAsIdle
            ? ShouldRecoverCompletedTurnIfIdleIsMissing(runtime)
            : runtime.PendingSessionUserMessageCount > 0
              && runtime.ActiveToolCount == 0
              && runtime.ActiveSubagentExecutionDepth == 0
              && !runtime.IsStreaming;

    private static bool CanTreatCompletedTurnAsIdle(PendingTurnRecoveryAnalysis analysis)
        => analysis.UserMessageObserved
           && analysis.TerminalState == PendingTurnTerminalState.None
           && analysis.AssistantTurnEnded
           && analysis.ActiveToolCount == 0;

    private async Task ApplyRecoveredToolStatusesAsync(Chat chat, PendingTurnRecoveryAnalysis analysis)
    {
        if (analysis.CompletedToolCallIds.Count == 0
            && analysis.FailedToolCallIds.Count == 0
            && analysis.StoppedToolCallIds.Count == 0)
            return;

        // Recovery is the terminal event this tool will ever get, so freeze its duration here too —
        // otherwise a recovered call renders (and persists) with no time at all. Captured before the
        // dispatch for the same reason as the live event stamps, and self-no-ops for calls whose real
        // completion arrived first.
        var recoveredAt = DateTimeOffset.UtcNow;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            void ApplyStatus(IReadOnlyCollection<string> toolCallIds, string status)
            {
                ApplyRecoveredToolStatusToMessages(chat, toolCallIds, status, recoveredAt);
                if (CurrentChat?.Id != chat.Id)
                    return;

                foreach (var vm in Messages.Where(m =>
                             m.Message.ToolCallId is { } toolCallId
                             && toolCallIds.Contains(toolCallId)))
                {
                    vm.NotifyToolStatusChanged();
                }
            }

            ApplyStatus(analysis.StoppedToolCallIds, "Stopped");
            ApplyStatus(analysis.CompletedToolCallIds, "Completed");
            ApplyStatus(analysis.FailedToolCallIds, "Failed");
        });
    }

    private static void ApplyRecoveredToolStatusToMessages(
        Chat chat,
        IReadOnlyCollection<string> toolCallIds,
        string status,
        DateTimeOffset recoveredAt)
    {
        foreach (var toolCallId in toolCallIds)
        {
            foreach (var message in chat.Messages.Where(m => m.ToolCallId == toolCallId))
            {
                message.MarkToolFinished(recoveredAt);
                message.ToolStatus = status;
            }
        }
    }

    private async Task SyncRecoveredTurnAssistantMessagesAsync(Chat chat, PendingTurnRecoveryAnalysis analysis)
    {
        if (!_runtimeStates.TryGetValue(chat.Id, out var runtime) || analysis.AssistantMessages.Count == 0)
            return;

        int pendingAssistantBaseline;
        lock (runtime)
            pendingAssistantBaseline = runtime.PendingAssistantMessageCount;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var existingTurnAssistantCount = Math.Max(
                0,
                CountCompletedAssistantMessages(chat) - pendingAssistantBaseline);
            var recoveredMessages = analysis.AssistantMessages
                .Skip(existingTurnAssistantCount)
                .ToList();
            SyncRecoveredAssistantMessages(chat, recoveredMessages);
        });
    }

    private async Task ApplyRecoveredIdleAsync(Chat chat)
    {
        ClearManualStopRequested(chat.Id);
        ClearPendingTurnTracking(chat.Id);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var runtime = GetOrCreateRuntimeState(chat.Id);
            // A recovered Idle terminal means the turn genuinely completed, so mirror the main
            // SessionIdleEvent handler and resolve any still-pending steer as delivered (never stuck).
            ResolvePendingSteersAsDelivered(chat.Id);
            ReconcileInProgressSubagentTools(chat, "Completed");
            PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.TurnEnd);
            MarkRuntimeTerminal(runtime);
            PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.Idle);

            if (CurrentChat?.Id == chat.Id)
            {
                _transcriptBuilder.HideTypingIndicator();
                _transcriptBuilder.CloseCurrentToolGroup();
                _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
                _transcriptBuilder.FlushPendingFileEdits();
                IsBusy = false;
                IsStreaming = false;
                StatusText = string.Empty;
            }

            QueueChatCompletionFollowUps(chat);
            QueueSaveChat(chat, saveIndex: false, releaseIfInactive: CurrentChat?.Id != chat.Id);
            CompleteSessionIdleWait(chat.Id);
        });

        // Mirror the main SessionIdleEvent handler: the chat is free again.
        ScheduleQueuedBusySendDrain(chat.Id);
    }

    private async Task ApplyRecoveredErrorAsync(Chat chat, string message)
    {
        ClearManualStopRequested(chat.Id);
        ClearPendingTurnTracking(chat.Id);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var runtime = GetOrCreateRuntimeState(chat.Id);
            // Mirror the main session.error handler: the turn errored before the agent could consume any
            // pending steer, so resolve them as "Not delivered".
            ResolvePendingSteersAsFailed(chat.Id);
            ReconcileInProgressSubagentTools(chat, "Failed");
            MarkRuntimeTerminal(runtime, string.Format(Loc.Status_Error, message));
            PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.Error, message);
            // Mirror the main session.error handler rather than auto-firing into a failed session.
            FailQueuedBusySends(chat.Id);

            if (CurrentChat?.Id == chat.Id)
            {
                _transcriptBuilder.HideTypingIndicator();
                _transcriptBuilder.CloseCurrentToolGroup();
                _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
                _transcriptBuilder.FlushPendingFileEdits();
                IsBusy = false;
                IsStreaming = false;
                StatusText = runtime.StatusText;
            }

            var errorMsg = new ChatMessage
            {
                Role = "error",
                Author = Loc.Author_Lumi,
                Content = runtime.StatusText
            };
            chat.Messages.Add(errorMsg);
            if (CurrentChat?.Id == chat.Id)
            {
                var vm = new ChatMessageViewModel(errorMsg);
                Messages.Add(vm);
                ScrollToEndRequested?.Invoke();
            }

            QueueSaveChat(chat, saveIndex: false, releaseIfInactive: CurrentChat?.Id != chat.Id);
        });

    }

    private async Task ApplyRecoveredAbortAsync(Chat chat)
    {
        var wasUserStopRequested = WasManualStopRequested(chat.Id);
        ClearPendingTurnTracking(chat.Id);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // A recovered Abort terminal (user stop OR unexpected/connection-lost abort) tore the turn down
            // before the agent consumed any pending steer, so resolve them as "Not delivered". Placed before
            // the branch so it also covers the early-returning unexpected-abort path.
            ResolvePendingSteersAsFailed(chat.Id);

            if (!wasUserStopRequested)
            {
                // ApplyUnexpectedAbortState resolves any deferred sends.
                ApplyUnexpectedAbortState(chat, GetUnexpectedAbortMessage());
                PublishTerminalChatLifecycleEventOnce(
                    chat,
                    ChatLifecycleEventTypes.Aborted,
                    "The recovered chat run aborted unexpectedly.");
                return;
            }

            var runtime = GetOrCreateRuntimeState(chat.Id);
            MarkInProgressToolsStopped(chat);
            MarkRuntimeTerminal(runtime, Loc.Status_Stopped);
            PublishTerminalChatLifecycleEventOnce(
                chat,
                ChatLifecycleEventTypes.Aborted,
                "The recovered chat run was stopped by the user.");

            if (CurrentChat?.Id == chat.Id)
            {
                _transcriptBuilder.HideTypingIndicator();
                _transcriptBuilder.CloseCurrentToolGroup();
                _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
                IsBusy = false;
                IsStreaming = false;
                StatusText = runtime.StatusText;
            }

            QueueSaveChat(chat, saveIndex: false, releaseIfInactive: CurrentChat?.Id != chat.Id);
        });

        // Recovery idle is a real "chat is free" transition, not only a post-Stop one.
        ScheduleQueuedBusySendDrain(chat.Id);
    }

    private async Task ApplyRecoveredShutdownAsync(Chat chat)
    {
        ClearManualStopRequested(chat.Id);
        ClearPendingTurnTracking(chat.Id);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Mirror the main SessionShutdownEvent handler: the subscription is about to be detached, so
            // resolve any pending steer as "Not delivered" now — nothing else will ever run to unstick it.
            ResolvePendingSteersAsFailed(chat.Id);
            ReconcileInProgressSubagentTools(chat, "Stopped");
            PublishTerminalChatLifecycleEventOnce(
                chat,
                ChatLifecycleEventTypes.Aborted,
                "The recovered Copilot session shut down before completing.");
            DetachSessionAfterRemoteShutdown(
                chat,
                wasActive: string.Equals(_activeSession?.SessionId, chat.CopilotSessionId, StringComparison.Ordinal));
            QueueSaveChat(chat, saveIndex: true, releaseIfInactive: CurrentChat?.Id != chat.Id, touchIndex: true);
        });
    }
}
