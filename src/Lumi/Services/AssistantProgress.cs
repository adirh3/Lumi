using System;
using System.Collections.Generic;
using Lumi.Models;

namespace Lumi.Services;

/// <summary>
/// Tells progress updates — the narration an assistant writes while it works — from its answer.
/// Phased models (GPT) label every message; other models reveal a progress update by requesting
/// tools with it, since the work continues after it.
/// </summary>
internal static class AssistantProgress
{
    public const string CommentaryPhase = "commentary";
    public const string FinalAnswerPhase = "final_answer";

    /// <summary>Classifies one completed assistant message from the SDK's evidence.</summary>
    public static bool IsProgress(string? phase, bool requestedTools)
        => phase switch
        {
            CommentaryPhase => true,
            FinalAnswerPhase => false,
            _ => requestedTools,
        };

    /// <summary>
    /// A reply split around reasoning arrives as several messages of one model call, and only the
    /// last piece carries the tool requests. When an unphased piece requested tools, the earlier
    /// pieces of the same call (back to the turn's user message) were progress too. Returns the
    /// messages this demoted.
    /// </summary>
    public static List<ChatMessage> DemoteEarlierPiecesOfCall(
        IReadOnlyList<ChatMessage> messages,
        string? phase,
        bool requestedTools,
        string? apiCallId)
    {
        var demoted = new List<ChatMessage>();
        if (!requestedTools || !string.IsNullOrEmpty(phase) || string.IsNullOrEmpty(apiCallId))
            return demoted;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message.Role == "user")
                break;
            if (message.Role != "assistant" || message.IsStreaming)
                continue;
            if (!string.Equals(message.ApiCallId, apiCallId, StringComparison.Ordinal))
                break;
            if (message.IsProgress == true)
                continue;

            message.IsProgress = true;
            demoted.Add(message);
        }

        return demoted;
    }
}
