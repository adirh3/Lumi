using System.Collections.Generic;
using System.Linq;
using System.Text;
using Lumi.Models;

namespace Lumi.Services;

/// <summary>
/// One chat a message brings in, as read for the model. <see cref="Transcript"/> is null when the chat
/// no longer exists (<see cref="Exists"/> false) or was left out to keep the message within budget.
/// </summary>
public sealed record ReferencedChatContent(ChatReference Reference, string? Transcript, bool Exists);

/// <summary>
/// Words for chats brought into a message: the block that hands their transcripts to the model, and the
/// inline marker used when a transcript is flattened to text (read_chat, session-recovery replays).
/// </summary>
public static class ChatReferenceFormatter
{
    /// <summary>
    /// The context block appended to the message the model receives: a lead-in saying these are other
    /// conversations the user chose, then each chat under its title. Empty when there are none.
    /// </summary>
    public static string BuildPromptContext(IReadOnlyList<ReferencedChatContent> chats)
    {
        if (chats.Count == 0)
            return "";

        var builder = new StringBuilder();
        builder.Append("\n\n--- Referenced chats ---\n")
            .Append(chats.Count == 1
                ? "The user brought this earlier Lumi chat into the message as context."
                : "The user brought these earlier Lumi chats into the message as context.")
            .Append(" Each is a separate conversation, shown with its most recent messages. Use it to answer the")
            .Append(" message above; read_chat with a chat's id shows more of it.\n");

        foreach (var chat in chats)
        {
            builder.Append("\n=== Chat: ").Append(Quote(chat.Reference.Title)).Append(" ===\n");
            if (!string.IsNullOrWhiteSpace(chat.Transcript))
                builder.Append(chat.Transcript.Trim()).Append('\n');
            else if (!chat.Exists)
                builder.Append("(This chat no longer exists, so its messages are not available.)\n");
            else
                builder.Append("(Not included to keep this message short. Read it with read_chat, id: ")
                    .Append(chat.Reference.ChatId)
                    .Append(".)\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Inline marker for transcripts flattened to text, e.g. <c>(with chats: "Trip", "Budget") </c>;
    /// empty when the message brings in no chats.
    /// </summary>
    public static string DescribeForTranscript(IReadOnlyList<ChatReference>? references)
        => references is not { Count: > 0 }
            ? ""
            : $"(with chats: {string.Join(", ", references.Select(static reference => Quote(reference.Title)))}) ";

    private static string Quote(string? title)
        => "\"" + (string.IsNullOrWhiteSpace(title) ? "Untitled chat" : title.Trim()) + "\"";
}
