using System;
using System.Linq;
using System.Threading;
using Lumi.Models;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    private bool IsChatRuntimeActive(Guid chatId)
        => _runtimeStates.TryGetValue(chatId, out var runtime)
           && (runtime.IsBusy || runtime.IsStreaming || runtime.HasPendingBackgroundWork);

    private void ReleaseChatCancellation(Guid chatId, bool cancel)
    {
        if (!_ctsSources.TryGetValue(chatId, out var cts))
            return;

        if (cancel)
            cts.Cancel();

        cts.Dispose();
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

        if (canEvictMessages && chat.Messages.Count > 0)
            chat.Messages.Clear();
    }
}
