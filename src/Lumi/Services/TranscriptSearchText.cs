using System.Linq;
using Lumi.Models;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;

namespace Lumi.Services;

/// <summary>
/// The text in-chat search finds in a message, as the transcript shows it: what a user, assistant,
/// error or (when shown) reasoning message says, a job wake's details, a submitted form's receipt
/// and an input request's title and description. The search of the transcript on screen and the
/// search of stored history both read it, so an older exchange shows as many hits once it is built
/// as its stand-in promised.
/// </summary>
internal static class TranscriptSearchText
{
    /// <summary>The searchable text of <paramref name="message"/>, or null when it shows none.</summary>
    public static string? Of(ChatMessage message, bool includeReasoning)
    {
        switch (message.Role)
        {
            case "user" when RemoteJobWake.IsJobWake(message.Author, message.Content):
                return JobWake(RemoteJobWake.Parse(message.Author, message.Content));
            case "user" when message.InteractionReceipt is { } receipt:
                return Receipt(receipt);
            case "user" or "assistant" or "error":
                return message.Content;
            case "reasoning":
                return includeReasoning ? message.Content : null;
            case "tool" when message is { ToolName: "ask_input", InputKind: { } kind, InputDefinition: { } definition }:
                try
                {
                    return Input(InteractiveDefinition.Parse(kind, definition));
                }
                catch (System.Exception ex) when (ex is System.FormatException or System.Text.Json.JsonException)
                {
                    return null;
                }
            default:
                return null;
        }
    }

    public static string Receipt(InteractionReceipt receipt)
        => receipt.Title + "\n" + receipt.Summary + "\n" + receipt.Detail;

    public static string Input(InteractiveDefinition definition)
        => definition.Title + "\n" + definition.Description;

    public static string JobWake(RemoteJobWake details)
        => string.Join('\n', new[]
            {
                details.JobName,
                details.Instructions,
                details.WakeSignal,
                details.ExitCode,
                details.StartedText,
                details.CompletedText,
                details.OutputText,
            }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));

    /// <summary>How often <paramref name="query"/> occurs in <paramref name="text"/>, ignoring case, without overlaps.</summary>
    public static int CountOccurrences(string text, string query)
    {
        var count = 0;
        for (var at = text.IndexOf(query, System.StringComparison.OrdinalIgnoreCase);
             at >= 0;
             at = text.IndexOf(query, at + query.Length, System.StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }
}
