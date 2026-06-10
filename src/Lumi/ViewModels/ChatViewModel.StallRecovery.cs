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
    private const int PostToolReconciliationMaxAttempts = 3;

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

    private bool ConsumeManualStopRequested(Guid chatId)
    {
        if (!_runtimeStates.TryGetValue(chatId, out var runtime))
            return false;

        lock (runtime)
        {
            var requested = runtime.ManualStopRequested;
            runtime.ManualStopRequested = false;
            return requested;
        }
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
        MarkRuntimeTerminal(runtime, message);

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
            for (var attempt = 0; attempt < PostToolReconciliationMaxAttempts; attempt++)
            {
                await Task.Delay(PostToolReconciliationDelay, reconciliationCts.Token);

                if (!_runtimeStates.TryGetValue(chatId, out var runtime))
                    return;

                lock (runtime)
                {
                    var stillEligible = IsPostToolReconciliationEligible(runtime, treatCompletedTurnAsIdle);
                    if (runtime.PendingTurnSequence != sequence || !stillEligible)
                        return;
                }

                using var recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(reconciliationCts.Token);
                recoveryCts.CancelAfter(SilentTurnRecoveryTimeout);
                if (await TryApplyCurrentTurnRecoveryAsync(
                        chatId,
                        sequence,
                        recoveryCts.Token,
                        treatCompletedTurnAsIdle))
                    return;
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

    private async Task<bool> TryApplyCurrentTurnRecoveryAsync(
        Guid chatId,
        long sequence,
        CancellationToken ct,
        bool treatCompletedTurnAsIdle = false)
    {
        var chat = _dataStore.Data.Chats.FirstOrDefault(c => c.Id == chatId);
        if (chat is null || !_runtimeStates.TryGetValue(chatId, out var runtime))
            return false;

        int pendingSessionUserMessageCount;
        lock (runtime)
        {
            if (runtime.PendingSessionUserMessageCount <= 0 || runtime.PendingTurnSequence != sequence)
                return false;

            pendingSessionUserMessageCount = runtime.PendingSessionUserMessageCount;
        }

        var currentSession = _sessionCache.GetValueOrDefault(chatId);
        if (currentSession is null)
            return false;

        var analysis = await AnalyzePendingTurnRecoveryAsync(
            currentSession,
            pendingSessionUserMessageCount,
            ct);

        return await ApplyRecoveredTurnStateAsync(
            chat,
            analysis,
            treatCompletedTurnAsIdle);
    }

    private async Task<bool> ApplyRecoveredTurnStateAsync(
        Chat chat,
        PendingTurnRecoveryAnalysis analysis,
        bool treatCompletedTurnAsIdle = false)
    {
        if (!analysis.UserMessageObserved)
            return false;

        await ApplyRecoveredToolStatusesAsync(chat, analysis);
        SetPendingToolCount(chat.Id, analysis.ActiveToolCount);
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
            return true;

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
        if (analysis.CompletedToolCallIds.Count == 0 && analysis.FailedToolCallIds.Count == 0)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var toolCallId in analysis.CompletedToolCallIds)
            {
                foreach (var message in chat.Messages.Where(m => m.ToolCallId == toolCallId))
                    message.ToolStatus = "Completed";

                if (CurrentChat?.Id == chat.Id)
                {
                    foreach (var vm in Messages.Where(m => m.Message.ToolCallId == toolCallId))
                        vm.NotifyToolStatusChanged();
                }
            }

            foreach (var toolCallId in analysis.FailedToolCallIds)
            {
                foreach (var message in chat.Messages.Where(m => m.ToolCallId == toolCallId))
                    message.ToolStatus = "Failed";

                if (CurrentChat?.Id == chat.Id)
                {
                    foreach (var vm in Messages.Where(m => m.Message.ToolCallId == toolCallId))
                        vm.NotifyToolStatusChanged();
                }
            }
        });
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
            MarkRuntimeTerminal(runtime);

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
        });

    }

    private async Task ApplyRecoveredErrorAsync(Chat chat, string message)
    {
        ClearManualStopRequested(chat.Id);
        ClearPendingTurnTracking(chat.Id);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var runtime = GetOrCreateRuntimeState(chat.Id);
            MarkRuntimeTerminal(runtime, string.Format(Loc.Status_Error, message));

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
                _transcriptBuilder.ProcessMessageToTranscript(vm);
                ScrollToEndRequested?.Invoke();
            }

            QueueSaveChat(chat, saveIndex: false, releaseIfInactive: CurrentChat?.Id != chat.Id);
        });

    }

    private async Task ApplyRecoveredAbortAsync(Chat chat)
    {
        var wasUserStopRequested = ConsumeManualStopRequested(chat.Id);
        ClearPendingTurnTracking(chat.Id);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!wasUserStopRequested)
            {
                ApplyUnexpectedAbortState(chat, GetUnexpectedAbortMessage());
                return;
            }

            var runtime = GetOrCreateRuntimeState(chat.Id);
            MarkInProgressToolsStopped(chat);
            MarkRuntimeTerminal(runtime, Loc.Status_Stopped);

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

        if (wasUserStopRequested)
            await DrainQueuedBusySendAsync(chat.Id);
    }

    private async Task ApplyRecoveredShutdownAsync(Chat chat)
    {
        ClearManualStopRequested(chat.Id);
        ClearPendingTurnTracking(chat.Id);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            DetachSessionAfterRemoteShutdown(
                chat,
                wasActive: string.Equals(_activeSession?.SessionId, chat.CopilotSessionId, StringComparison.Ordinal));
            QueueSaveChat(chat, saveIndex: true, releaseIfInactive: CurrentChat?.Id != chat.Id, touchIndex: true);
        });
    }
}
