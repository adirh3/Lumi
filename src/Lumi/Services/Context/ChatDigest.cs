using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lumi.Models;

namespace Lumi.Services.Context;

/// <summary>
/// Turns chat history into the compact text the context curator reads: the user's own words in full, a summary of
/// tool use, and Lumi's final replies, split into parts so long days are read deliberately.
/// </summary>
internal static class ChatDigest
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "are", "was", "were", "has", "have", "had", "not", "but",
        "you", "your", "our", "its", "his", "her", "they", "them", "then", "than", "when", "what", "which", "who", "how", "why",
        "all", "any", "can", "could", "should", "would", "will", "just", "also", "use", "used", "using", "make", "made", "does",
        "did", "done", "been", "being", "about", "after", "before", "there", "here", "each", "only", "more", "most", "some",
        "such", "very", "like", "want", "need", "new", "now", "one", "two", "get", "got", "let", "lets", "lumi",
    };

    /// <summary>Messages a background job or a managing chat sent, rather than the user. Their authors are set by
    /// <c>ChatViewModel</c> ("Lumi Job - name") and <c>ChatOrchestrationService</c> ("Lumi Manager").</summary>
    public static bool IsAutomated(ChatMessage message)
        => message.Author is { } author
           && (author.StartsWith("Lumi Job", StringComparison.Ordinal) || author.StartsWith("Lumi Manager", StringComparison.Ordinal));

    public static bool IsOwnerMessage(ChatMessage message)
        => message.Role == "user" && !IsAutomated(message) && !string.IsNullOrWhiteSpace(message.Content);

    public static bool IsReply(ChatMessage message)
        => message.Role == "assistant" && message.IsProgress != true && !string.IsNullOrWhiteSpace(message.Content);

    public sealed class Turn(ChatMessage? request)
    {
        public ChatMessage? Request { get; } = request;
        public List<ChatMessage> Tools { get; } = [];
        public List<ChatMessage> Replies { get; } = [];
    }

    public static List<Turn> SplitTurns(IEnumerable<ChatMessage> messages)
    {
        var turns = new List<Turn>();
        Turn? current = null;
        foreach (var message in messages)
        {
            if (message.Role == "user")
            {
                current = new Turn(message);
                turns.Add(current);
                continue;
            }

            if (current is null)
            {
                current = new Turn(null);
                turns.Add(current);
            }

            if (message.Role == "tool")
                current.Tools.Add(message);
            else if (IsReply(message))
                current.Replies.Add(message);
        }

        return turns;
    }

    public static string RenderTurn(Turn turn, string owner, int ownerLimit = 1_800, int replyLimit = 1_200, int replies = 2)
    {
        var sb = new StringBuilder();
        if (turn.Request is { } request)
        {
            var automated = IsAutomated(request);
            var who = automated ? $"Job prompt ({request.Author})" : owner + (request.RemoteRequestId is null ? "" : " (phone)");
            sb.AppendLine($"[{request.Timestamp.LocalDateTime:MMM d HH:mm}] {who}: {Trim(request.Content, automated ? 400 : ownerLimit)}");
        }

        if (turn.Tools.Count > 0)
        {
            var counts = string.Join(", ", turn.Tools
                .GroupBy(tool => tool.ToolName ?? "tool")
                .OrderByDescending(group => group.Count())
                .Take(5)
                .Select(group => $"{group.Key}×{group.Count()}"));
            var purposes = turn.Tools.Select(ToolPurpose).OfType<string>().Distinct().Take(4).ToList();
            sb.AppendLine($"  (tools: {counts}{(purposes.Count > 0 ? $"; e.g. {string.Join("; ", purposes)}" : "")})");
        }

        foreach (var reply in turn.Replies.TakeLast(replies))
            sb.AppendLine($"[{reply.Timestamp.LocalDateTime:MMM d HH:mm}] Lumi: {Trim(reply.Content, replyLimit)}");
        return sb.ToString();
    }

    /// <summary>Joins rendered turns into parts of at most <paramref name="maxChars"/>, in time order.</summary>
    public static List<string> SplitParts(IEnumerable<string> renderedTurns, int maxChars)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var turn in renderedTurns)
        {
            var text = turn.Length > maxChars ? turn[..maxChars] + "…\n" : turn;
            if (current.Length > 0 && current.Length + text.Length > maxChars)
            {
                parts.Add(current.ToString());
                current.Clear();
            }

            current.Append(text);
        }

        if (current.Length > 0 || parts.Count == 0)
            parts.Add(current.ToString());
        return parts;
    }

    /// <summary>Distinctive lowercase words of a text, for comparing memories.</summary>
    public static HashSet<string> Terms(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? []
            : Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}\.\-_#]+")
                .Select(term => term.Trim('.', '-', '_'))
                .Where(term => term.Length >= 3 && !StopWords.Contains(term))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string Trim(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var value = text.Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }

    public static string Excerpt(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var flat = Whitespace.Replace(text, " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    /// <summary>The purpose an agent gave a tool call (its description, intent or title), when it gave one.</summary>
    private static string? ToolPurpose(ChatMessage tool)
    {
        if (string.IsNullOrWhiteSpace(tool.Content) || !tool.Content.TrimStart().StartsWith('{'))
            return null;
        try
        {
            using var document = JsonDocument.Parse(tool.Content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var name in new[] { "description", "intent", "title", "prompt" })
            {
                if (document.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } text)
                    return Excerpt(text, 70);
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
