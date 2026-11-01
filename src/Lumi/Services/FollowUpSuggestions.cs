using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lumi.Services;

/// <summary>An earlier exchange of a chat, as follow-up suggestions read it.</summary>
public sealed record FollowUpTurn(string User, string Assistant);

/// <summary>What the follow-up suggestion model knows about the moment a reply finished.</summary>
public sealed record FollowUpSuggestionContext
{
    /// <summary>The finished reply the chips follow.</summary>
    public required string AssistantMessage { get; init; }

    /// <summary>The user's message that started the exchange.</summary>
    public string? UserMessage { get; init; }

    /// <summary>Exchanges before it, oldest first.</summary>
    public IReadOnlyList<FollowUpTurn> EarlierTurns { get; init; } = [];

    public string? ProjectName { get; init; }

    /// <summary>What the user usually sends at moments like this, most likely first
    /// (<see cref="SuggestionHistoryRanker.RankUsualReplies"/>).</summary>
    public IReadOnlyList<SuggestionHistoryRanker.UsualReply> UsualReplies { get; init; } = [];
}

/// <summary>
/// The chips under a finished reply: what the user will most likely send next, one click away.
///
/// The design follows a replay of real chat history (<c>SuggestionEvalHarness</c>), which scores suggestions by
/// whether one of them says what the user actually sent next:
/// <list type="bullet">
/// <item>Replies often end by offering the next step ("Want me to fix the failing tests?"), and users mostly answer
/// that ending — so the model always sees a long reply's end as well as its beginning, and answers the offer first.</item>
/// <item>Users repeat themselves where they work: in a coding project the same few requests ("commit and push to
/// main", "run code review") make up a large share of replies. The model gets them in the user's own words and
/// uses them where they fit; showing them regardless made many chips nonsensical.</item>
/// <item>The exchanges before the latest one, and the project's name, help it tell where the work stands.</item>
/// <item>A turn can end while the assistant is still working: its last text announces a step it never took ("Now
/// let me check the logs:"). Users then mostly just say "continue", which the model offers only when told to.</item>
/// </list>
/// The reply goes to the model as it was written. Rewriting Lumi's cards and charts as text, and flagging turns that
/// ended while tools were still running, were both tried in the replay and gave nothing beyond that rule: the model
/// reads the JSON fine, and the reply's own ending already shows that the work stopped.
/// </summary>
internal static class FollowUpSuggestions
{
    internal const int MaxSuggestions = 3;

    /// <summary>How many exchanges before the latest one the model sees.</summary>
    internal const int EarlierTurnsShown = 2;

    /// <summary>How many of the user's usual replies the model sees.</summary>
    internal const int UsualRepliesShown = 5;

    private const int ReplyHead = 500;
    private const int ReplyTail = 1500;
    private const int UserMessageLength = 800;
    private const int EarlierUserLength = 300;
    private const int EarlierReplyLength = 400;

    internal const string SystemPrompt =
        "You predict the next message the user will send in a chat with an AI assistant, so the app can offer it as a one-click chip. " +
        "Propose exactly 3 messages, written as the user would type them. " +
        "How to choose, in this order: " +
        "1. Answer the end of the assistant's reply first. When it offers to do something, asks whether to proceed, or recommends a change, the first message accepts it as a direct request in the user's words (for \"Want me to fix the 4 failing tests?\" write \"Fix the 4 failing tests\"; for \"Should I go with the rail?\" write \"Go with the rail\"). When it asks the user to choose between options, give the main options as separate messages. When it asks a question only the user can answer, skip this rule. When the reply stops in the middle of the work — it announces a step it hasn't taken yet (\"Now let me check the logs:\", \"I'm running the tests now.\") — the first message asks it to continue (\"Continue\"). " +
        "2. You may get what this user usually sends at moments like this, in their own words. When one is a natural next step here, use it exactly as written. Ignore the ones that don't fit this conversation. " +
        "3. Fill the rest with the most likely next requests for this conversation: the obvious next step, a pointed question about what was just said, or a check of what was just done. Ground them in its specifics. Never re-ask what the reply already answered, and never write generic filler (\"Tell me more\", \"Thanks\"). " +
        "4. Write like the user: short (under 60 characters), direct and plain. No commit hashes, IDs, version numbers or long paths. Use the language of the conversation. " +
        "5. Make the 3 meaningfully different. " +
        "Output ONLY a JSON array of exactly 3 strings, nothing else.";

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>The question sent to the model: its rules, then what it needs to know about the moment.</summary>
    internal static string BuildQuestion(FollowUpSuggestionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var builder = new StringBuilder(SystemPrompt).AppendLine().AppendLine();

        if (!string.IsNullOrWhiteSpace(context.ProjectName))
            builder.Append("Project: ").AppendLine(Clip(context.ProjectName.Trim(), 80)).AppendLine();

        var earlier = context.EarlierTurns
            .Where(static turn => !string.IsNullOrWhiteSpace(turn.User) || !string.IsNullOrWhiteSpace(turn.Assistant))
            .TakeLast(EarlierTurnsShown)
            .ToList();
        if (earlier.Count > 0)
        {
            builder.AppendLine("Earlier in this chat (oldest first):");
            foreach (var turn in earlier)
            {
                if (!string.IsNullOrWhiteSpace(turn.User))
                    builder.Append("User: ").AppendLine(Flatten(Clip(turn.User.Trim(), EarlierUserLength)));
                if (!string.IsNullOrWhiteSpace(turn.Assistant))
                    builder.Append("Assistant: ").AppendLine(Flatten(Excerpt(turn.Assistant, EarlierReplyLength / 4, EarlierReplyLength * 3 / 4)));
            }

            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(context.UserMessage))
        {
            builder.AppendLine("Latest user message:");
            builder.AppendLine(Clip(context.UserMessage.Trim(), UserMessageLength));
            builder.AppendLine();
        }

        builder.AppendLine("Latest assistant reply:");
        builder.AppendLine(Excerpt(context.AssistantMessage, ReplyHead, ReplyTail));

        var usual = context.UsualReplies.Take(UsualRepliesShown).ToList();
        if (usual.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("What this user usually sends at moments like this (their own words, most likely first):");
            foreach (var reply in usual)
                builder.Append("- ").AppendLine(reply.Text);
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Reads the model's JSON array of suggestions, tolerating code fences and surrounding prose.</summary>
    internal static List<string>? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Trim();
        if (raw.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = raw.IndexOf('\n');
            if (firstNewline > 0)
                raw = raw[(firstNewline + 1)..];
            if (raw.EndsWith("```", StringComparison.Ordinal))
                raw = raw[..^3];
            raw = raw.Trim();
        }

        var suggestions = TryDeserialize(raw);
        if (suggestions is null)
        {
            var arrayStart = raw.IndexOf('[');
            var arrayEnd = raw.LastIndexOf(']');
            if (arrayStart >= 0 && arrayEnd > arrayStart)
                suggestions = TryDeserialize(raw[arrayStart..(arrayEnd + 1)]);
        }

        var normalized = suggestions?
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => Whitespace.Replace(s.Trim(), " "))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxSuggestions)
            .ToList();
        return normalized is { Count: > 0 } ? normalized : null;
    }

    /// <summary>
    /// A long reply as the model sees it: its beginning and its end, where offers and questions are. Short
    /// replies are kept whole.
    /// </summary>
    internal static string Excerpt(string? text, int headLength, int tailLength)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length <= headLength + tailLength)
            return trimmed;

        var headEnd = headLength;
        if (headEnd > 0 && char.IsHighSurrogate(trimmed[headEnd - 1]))
            headEnd--;
        var tailStart = trimmed.Length - tailLength;
        if (char.IsLowSurrogate(trimmed[tailStart]))
            tailStart++;
        return trimmed[..headEnd].TrimEnd() + "\n[…]\n" + trimmed[tailStart..].TrimStart();
    }

    private static List<string>? TryDeserialize(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize(raw, Lumi.Models.AppDataJsonContext.Default.ListString);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Clip(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;
        var end = maxLength;
        if (char.IsHighSurrogate(text[end - 1]))
            end--;
        return text[..end] + "…";
    }

    private static string Flatten(string text) => Whitespace.Replace(text, " ");
}
