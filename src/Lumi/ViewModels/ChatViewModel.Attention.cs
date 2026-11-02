using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

/// <summary>
/// The moments a chat needs the user: the desktop's own notification, and the same moment published
/// on the shared <see cref="ChatEventHub"/> so a paired phone can be told while the user is away.
/// </summary>
public partial class ChatViewModel
{
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
