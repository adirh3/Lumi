using Lumi.Models;

namespace Lumi.Services;

internal static class ChatPreviewHelper
{
    internal const int MaxLength = 180;

    public static string? FromMessages(IReadOnlyList<ChatMessage> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (message.Role is not ("user" or "assistant"))
                continue;
            if (message.Role == "assistant" && message.IsStreaming)
                continue;

            var preview = FromContent(message.Content);
            if (preview is not null)
                return preview;
        }

        return null;
    }

    public static string? FromContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var normalized = string.Join(
            ' ',
            content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaxLength
            ? normalized
            : normalized[..(MaxLength - 1)] + "…";
    }
}
