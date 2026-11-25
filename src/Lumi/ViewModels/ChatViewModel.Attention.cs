using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

/// <summary>
/// The moments a chat needs the user: the desktop's own notification, and the same moment published
/// on the shared <see cref="ChatEventHub"/> so a paired phone can be told while the user is away.
/// </summary>
public partial class ChatViewModel
{
    internal IReadOnlyList<PendingChatRequest> GetPendingInputs()
    {
        var requests = new List<PendingChatRequest>();
        lock (_pendingQuestionsSync)
        {
            foreach (var (id, chatId) in _pendingQuestionChatIds)
            {
                if (!_pendingQuestions.TryGetValue(id, out var completion) || completion.Task.IsCompleted)
                    continue;
                var chat = _dataStore.Data.Chats.Find(candidate => candidate.Id == chatId);
                var message = chat?.Messages.LastOrDefault(candidate => candidate.QuestionId == id);
                if (chat is not null && message is not null)
                    requests.Add(new(chat, id,
                        message.ToolName == "ask_input" ? InputRequestItem.StableIdFor(message.Id) : QuestionItem.StableIdFor(id),
                        message.Timestamp, message.QuestionText ?? "",
                        message.ToolName == "ask_input" ? ChatAttentionKind.Input : ChatAttentionKind.Question));
            }
        }
        foreach (var approval in _permissionRequests.Values.Where(request => request.IsPending))
        {
            var chat = _dataStore.Data.Chats.Find(candidate => candidate.Id == approval.ChatId);
            if (chat is not null)
                requests.Add(new(chat, approval.RequestId, $"approval:{approval.SessionId}:{approval.RequestId}",
                    approval.RequestedAt, approval.Title, ChatAttentionKind.Approval));
        }
        return requests;
    }

#if DEBUG
    internal async Task RunAttentionQuestionFixtureAsync(Chat chat)
    {
        try
        {
            var answer = await BuildAskQuestionTool(chat.Id).InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
            {
                ["question"] = "Should the inbox include every project, or only the selected project?",
                ["options"] = new[] { "Every project", "Selected project only" },
                ["allowFreeText"] = true, ["allowMultiSelect"] = false
            });
            chat.Messages.Add(new() { Role = "assistant", Content = $"Thanks! The fixture received: {answer}" });
            chat.HasUnreadMessages = true;
        }
        catch (OperationCanceledException) { }
    }
#endif

    private void NotifyQuestionAsked(ChatAttention attention)
    {
        _chatEvents.PublishAttention(attention);
        if (!_dataStore.Data.Settings.NotificationsEnabled)
            return;

        var chatTitle = _dataStore.Data.Chats.FirstOrDefault(c => c.Id == attention.ChatId)?.Title;
        NotificationService.ShowQuestion(attention.Text, chatTitle, attention.ChatId);
    }

    /// <summary>The words that ended the latest exchange, or null when it ended without any.</summary>
    internal static string? LatestAnswerText(Chat chat)
    {
        for (var index = chat.Messages.Count - 1; index >= 0; index--)
        {
            var message = chat.Messages[index];
            if (message.Role == "user")
                break;
            if (message.Role == "assistant"
                && message.IsProgress != true
                && !string.IsNullOrWhiteSpace(message.Content))
            {
                return message.Content;
            }
        }

        return null;
    }
}
