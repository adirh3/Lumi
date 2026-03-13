using System;
using System.Linq;
using System.Threading;
using Lumi.Models;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    private bool IsChatRuntimeActive(Guid chatId)
        => _runtimeStates.TryGetValue(chatId, out var runtime)
           && (runtime.IsBusy || runtime.IsStreaming);

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
    }

    private void ReleaseSessionResources(Guid chatId, bool cancelActiveRequest, bool deleteServerSession)
    {
        ReleaseChatCancellation(chatId, cancelActiveRequest);
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
