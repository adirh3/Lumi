using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using GitHub.Copilot;
using Lumi.Models;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    private bool _isDisposed;

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _copilotService.Reconnected -= OnCopilotReconnected;
        _copilotService.SessionDeletedRemotely -= OnSessionDeletedRemotely;
        _transcriptWindow.PropertyChanged -= OnTranscriptWindowPropertyChanged;

        lock (_chatLoadSync)
        {
            _chatLoadRequestId++;
            try { _chatLoadCts?.Cancel(); }
            catch (ObjectDisposedException) { }
            _chatLoadCts?.Dispose();
            _chatLoadCts = null;
        }

        foreach (var chatId in _sessionCache.Keys
                     .Concat(_ctsSources.Keys)
                     .Concat(_runtimeStates.Keys)
                     .Concat(_chatBrowserServices.Keys)
                     .Distinct()
                     .ToList())
        {
            ReleaseSessionResources(chatId, cancelActiveRequest: true, deleteServerSession: false);
            RemoveSuggestionTracking(chatId);
            DisposeBrowserService(chatId);
        }

        _runtimeStates.Clear();
        _pendingQuestions.Clear();
        _queuedBusySendPrompts.Clear();
        _inProgressMessages.Clear();
        _voiceService.Dispose();
        _modelSelectionSaveCts?.Cancel();
        _modelSelectionSaveCts?.Dispose();
        _modelSelectionSaveCts = null;
        _modelSelectionSyncCts?.Cancel();
        _modelSelectionSyncCts?.Dispose();
        _modelSelectionSyncCts = null;
        _fileSearchCts?.Cancel();
        _fileSearchCts?.Dispose();
        _fileSearchCts = null;
        _gitRefreshThrottleCts?.Cancel();
        _gitRefreshThrottleCts?.Dispose();
        _gitRefreshThrottleCts = null;
    }

    private bool IsChatRuntimeActive(Guid chatId)
        => _runtimeStates.TryGetValue(chatId, out var runtime)
           && runtime.HasActiveWork;

    internal bool OwnsLiveChat(Guid chatId)
    {
        if (IsChatRuntimeActive(chatId)
            || _ctsSources.ContainsKey(chatId)
            || _inProgressMessages.ContainsKey(chatId))
            return true;

        var chat = _dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == chatId);
        return chat?.Messages.Any(message =>
            message.ToolName == "ask_question"
            && message.ToolStatus == "InProgress"
            && message.QuestionId is { Length: > 0 } questionId
            && _pendingQuestions.ContainsKey(questionId)) == true;
    }

    // A browser session outlives its chat's runtime state (it persists across chat switches), so a
    // surface can still hold one for a chat it no longer "owns". Deletion paths use this to ensure
    // the browser is torn down rather than leaking until app shutdown.
    internal bool HasBrowserService(Guid chatId) => _chatBrowserServices.ContainsKey(chatId);

    internal bool OwnsAnyLiveChat()
    {
        foreach (var chatId in _runtimeStates.Keys
                     .Concat(_ctsSources.Keys)
                     .Concat(_inProgressMessages.Keys)
                     .Distinct())
        {
            if (OwnsLiveChat(chatId))
                return true;
        }

        return _dataStore.Data.Chats.Any(chat =>
            chat.Messages.Any(message =>
                message.ToolName == "ask_question"
                && message.ToolStatus == "InProgress"
                && message.QuestionId is { Length: > 0 } questionId
                && _pendingQuestions.ContainsKey(questionId)));
    }

    private void QueueBusySendPrompt(Guid chatId, string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        _queuedBusySendPrompts[chatId] = prompt;
    }

    private async Task DrainQueuedBusySendAsync(Guid chatId)
    {
        if (!_queuedBusySendPrompts.Remove(chatId, out var prompt) || string.IsNullOrWhiteSpace(prompt))
            return;

        if (CurrentChat?.Id != chatId)
        {
            _chatDrafts[chatId] = prompt;
            return;
        }

        if (IsChatRuntimeActive(chatId))
        {
            _queuedBusySendPrompts[chatId] = prompt;
            return;
        }

        await SendMessageCore(prompt, consumeComposerPrompt: false);
    }

    private void ReleaseChatCancellation(Guid chatId, bool cancel)
    {
        if (!_ctsSources.Remove(chatId, out var cts))
            return;

        try
        {
            if (cancel)
                cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool ReleasePreviousTurnCancellation(Guid chatId)
    {
        if (!_ctsSources.ContainsKey(chatId))
            return false;

        if (IsChatRuntimeActive(chatId))
        {
            ReleaseChatCancellation(chatId, cancel: true);
            return true;
        }

        // The Copilot SDK may still hold the token after an idle turn. Drop our
        // reference, but don't cancel/dispose it while the session is being reused.
        _ctsSources.Remove(chatId);
        return false;
    }

    private void DropCompletedTurnState(Guid chatId, bool dropCancellation)
    {
        _inProgressMessages.Remove(chatId);

        if (!dropCancellation)
            return;

        // SessionIdle is emitted after background work is drained. Drop our
        // reference without cancelling/disposal, matching ReleasePreviousTurnCancellation.
        _ctsSources.Remove(chatId);
    }

    private void DisposeSessionSubscription(Guid chatId)
    {
        if (_sessionSubs.TryGetValue(chatId, out var sub))
        {
            sub.Dispose();
            _sessionSubs.Remove(chatId);
        }
    }

    private void RemoveSuggestionTracking(Guid chatId)
    {
        _suggestionGenerationInFlightChats.Remove(chatId);
        _lastSuggestedAssistantMessageByChat.Remove(chatId);
    }

    private void DisposeBrowserService(Guid chatId)
    {
        if (_chatBrowserServices.TryRemove(chatId, out var browserSvc))
        {
            _ = browserSvc.DisposeAsync();
        }
    }

    private void CancelPendingQuestions(Chat chat)
    {
        var pendingQuestionIds = chat.Messages
            .Where(static m => !string.IsNullOrWhiteSpace(m.QuestionId))
            .Select(static m => m.QuestionId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var questionId in pendingQuestionIds)
        {
            if (_pendingQuestions.TryGetValue(questionId, out var tcs))
            {
                tcs.TrySetCanceled();
                _pendingQuestions.Remove(questionId);
            }
        }

        // Mark unanswered ask_question tool messages as Failed so rebuild renders them as expired
        foreach (var msg in chat.Messages)
        {
            if (msg.ToolName == "ask_question"
                && msg.ToolStatus == "InProgress"
                && string.IsNullOrEmpty(msg.ToolOutput))
            {
                msg.ToolStatus = "Failed";
            }
        }

        // Expire any live QuestionItem cards in the current transcript
        ExpireUnansweredQuestions(chat.Id);
    }

    private bool MarkInProgressToolsStopped(Chat chat)
    {
        List<Guid>? stoppedMessageIds = null;

        foreach (var message in chat.Messages)
        {
            if (message.ToolStatus != "InProgress" || string.IsNullOrWhiteSpace(message.ToolName))
                continue;

            message.ToolStatus = "Stopped";
            (stoppedMessageIds ??= []).Add(message.Id);
        }

        if (stoppedMessageIds is null)
            return false;

        if (CurrentChat?.Id == chat.Id)
        {
            var stoppedIds = stoppedMessageIds.ToHashSet();
            foreach (var vm in Messages.Where(vm => stoppedIds.Contains(vm.Message.Id)))
                vm.NotifyToolStatusChanged();
        }

        return true;
    }

    /// <summary>Sets IsExpired on all unanswered QuestionItems in the live transcript for the given chat.</summary>
    private void ExpireUnansweredQuestions(Guid chatId)
    {
        if (CurrentChat?.Id != chatId) return;

        foreach (var turn in TranscriptTurns)
        {
            foreach (var item in turn.Items)
            {
                if (item is QuestionItem q && !q.IsAnswered && !q.IsExpired)
                    q.IsExpired = true;
            }
        }
    }

    private void ReleaseSessionResources(Guid chatId, bool cancelActiveRequest, bool deleteServerSession)
    {
        _queuedBusySendPrompts.Remove(chatId);
        ReleaseChatCancellation(chatId, cancelActiveRequest);
        ClearPendingTurnTracking(chatId);
        DisposeSessionSubscription(chatId);

        if (_sessionCache.TryGetValue(chatId, out var session))
        {
            var releaseTask = DisposeReleasedSessionAsync(session, deleteServerSession);
            _sessionReleaseTasks[chatId] = releaseTask;
            _ = releaseTask.ContinueWith(
                _ => Dispatcher.UIThread.Post(() =>
                {
                    if (_sessionReleaseTasks.TryGetValue(chatId, out var trackedTask)
                        && ReferenceEquals(trackedTask, releaseTask))
                    {
                        _sessionReleaseTasks.Remove(chatId);
                    }
                }),
                TaskScheduler.Default);
            _sessionCache.Remove(chatId);
        }

        _inProgressMessages.Remove(chatId);
    }

    private async Task DisposeReleasedSessionAsync(CopilotSession session, bool deleteServerSession)
    {
        try
        {
            if (deleteServerSession)
                await _copilotService.DisposeAndDeleteSessionAsync(session).ConfigureAwait(false);
            else
                await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Lumi] Failed to release Copilot session {session.SessionId}: {ex.Message}");
        }
    }

    private async Task AwaitPendingSessionReleaseAsync(Guid chatId, CancellationToken ct)
    {
        if (!_sessionReleaseTasks.TryGetValue(chatId, out var releaseTask))
            return;

        try
        {
            await releaseTask.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Lumi] Failed while waiting for released session for chat {chatId}: {ex.Message}");
        }

        if (releaseTask.IsCompleted
            && _sessionReleaseTasks.TryGetValue(chatId, out var trackedTask)
            && ReferenceEquals(trackedTask, releaseTask))
        {
            _sessionReleaseTasks.Remove(chatId);
        }
    }

    private void ReleaseInactiveChatState(Chat chat)
    {
        if (CurrentChat?.Id == chat.Id || IsChatRuntimeActive(chat.Id))
            return;

        CancelPendingQuestions(chat);
        ReleaseSessionResources(chat.Id, cancelActiveRequest: false, deleteServerSession: false);
        RemoveSuggestionTracking(chat.Id);
        // Intentionally keep the chat's BrowserService alive. A browser session belongs to the
        // chat, not its transient runtime state, so switching away and back restores the page
        // instead of losing the browser (and its toggle button). The service is disposed when the
        // chat is deleted (CleanupSession) or the app shuts down (Dispose).
        _runtimeStates.Remove(chat.Id);
    }

    /// <summary>
    /// Sweeps all tracked runtime states and releases any that belong to chats
    /// that are no longer active (not busy, not streaming, not the current chat).
    /// Call this on chat switch to catch states that event-driven cleanup missed
    /// (e.g. chats whose background work completed but cleanup was skipped).
    /// </summary>
    private void SweepInactiveChatStates()
    {
        var currentChatId = CurrentChat?.Id;
        var staleIds = _runtimeStates
            .Where(kvp => kvp.Key != currentChatId
                          && !kvp.Value.HasActiveWork)
            .Select(static kvp => kvp.Key)
            .ToList();

        foreach (var chatId in staleIds)
        {
            var chat = _dataStore.Data.Chats.FirstOrDefault(c => c.Id == chatId);
            if (chat is not null)
                ReleaseInactiveChatState(chat);
            else
            {
                // Chat was deleted but runtime state lingered — clean up directly
                ReleaseSessionResources(chatId, cancelActiveRequest: false, deleteServerSession: false);
                RemoveSuggestionTracking(chatId);
                DisposeBrowserService(chatId);
                _runtimeStates.Remove(chatId);
            }
        }
    }

}
