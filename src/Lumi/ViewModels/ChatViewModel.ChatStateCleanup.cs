using System;
using System.Linq;
using System.Threading;
using Lumi.Models;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    private bool IsChatRuntimeActive(Guid chatId)
        => _runtimeStates.TryGetValue(chatId, out var runtime)
           && (runtime.IsBusy || runtime.IsStreaming || runtime.HasPendingBackgroundWork
               || runtime.PendingSessionUserMessageCount > 0);

    private void QueueBusySendPrompt(Guid chatId, string prompt, IEnumerable<string>? attachmentPaths = null)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        _queuedBusySendPrompts[chatId] = new QueuedBusySend(
            prompt,
            attachmentPaths?.Where(static path => !string.IsNullOrWhiteSpace(path)).ToList() ?? []);
    }

    private async Task DrainQueuedBusySendAsync(Guid chatId)
    {
        if (!_queuedBusySendPrompts.Remove(chatId, out var queuedSend) || string.IsNullOrWhiteSpace(queuedSend.Prompt))
            return;

        var prompt = queuedSend.Prompt;

        if (CurrentChat?.Id != chatId)
        {
            _chatDrafts[chatId] = prompt;
            return;
        }

        if (IsChatRuntimeActive(chatId))
        {
            _queuedBusySendPrompts[chatId] = queuedSend;
            return;
        }

        await SendMessageCore(prompt, consumeComposerPrompt: false, queuedSend.AttachmentPaths);
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
        if (_chatBrowserServices.TryGetValue(chatId, out var browserSvc))
        {
            _ = browserSvc.DisposeAsync();
            _chatBrowserServices.Remove(chatId);
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
            if (deleteServerSession)
                _ = _copilotService.DeleteSessionAsync(session.SessionId);

            _sessionCache.Remove(chatId);
        }

        _inProgressMessages.Remove(chatId);
    }

    private void ReleaseInactiveChatState(Chat chat, bool canEvictMessages)
    {
        if (CurrentChat?.Id == chat.Id || IsChatRuntimeActive(chat.Id))
            return;

        CancelPendingQuestions(chat);
        ReleaseSessionResources(chat.Id, cancelActiveRequest: false, deleteServerSession: false);
        RemoveSuggestionTracking(chat.Id);
        DisposeBrowserService(chat.Id);
        _runtimeStates.Remove(chat.Id);
        _dataStore.RemoveChatLoadLock(chat.Id);

        if (canEvictMessages && chat.Messages.Count > 0)
            chat.Messages.Clear();
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
                          && !kvp.Value.IsBusy
                          && !kvp.Value.IsStreaming
                          && !kvp.Value.HasPendingBackgroundWork)
            .Select(static kvp => kvp.Key)
            .ToList();

        foreach (var chatId in staleIds)
        {
            var chat = _dataStore.Data.Chats.FirstOrDefault(c => c.Id == chatId);
            if (chat is not null)
                ReleaseInactiveChatState(chat, canEvictMessages: true);
            else
            {
                // Chat was deleted but runtime state lingered — clean up directly
                ReleaseSessionResources(chatId, cancelActiveRequest: false, deleteServerSession: false);
                RemoveSuggestionTracking(chatId);
                DisposeBrowserService(chatId);
                _runtimeStates.Remove(chatId);
                _dataStore.RemoveChatLoadLock(chatId);
            }
        }
    }
}
