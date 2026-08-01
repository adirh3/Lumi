using System;
using System.Collections.Generic;
using System.Linq;
using Lumi.Models;

namespace Lumi.Services;

/// <summary>
/// The result of planning a fork: the new chat, plus everything the caller needs to finish wiring
/// it up.
/// </summary>
/// <param name="Chat">The unsaved forked chat.</param>
/// <param name="ComposerPrefill">
/// Text to seed the fork's composer with, or null. Set when forking from a user message: the fork
/// stops *before* that message so the transcript ends on an answer, and the message itself becomes
/// an editable draft ("fork and edit").
/// </param>
/// <param name="RetainedUserTurns">
/// How many user turns the forked transcript contains. This is the single source of truth for
/// where the server-side session fork must be cut, so the copied transcript and the model's
/// memory can never disagree about how much history the branch has.
/// </param>
public readonly record struct ForkPlan(Chat Chat, string? ComposerPrefill, int RetainedUserTurns);

/// <summary>
/// Builds a forked copy of a <see cref="Chat"/> — an independent chat that carries the source
/// chat's setup (project, agent, skills, MCP servers, worktree, model preferences) and its
/// transcript, optionally truncated at a chosen message.
///
/// <para><b>Where the branch is cut.</b> Forking from an <i>assistant</i> message keeps everything
/// up to and including it, so the branch ends on an answer with an empty composer — the shape
/// ChatGPT, Gemini and Open WebUI all use. Forking from a <i>user</i> message instead cuts
/// <i>before</i> it and hands the text back as a composer draft: the alternative would be a
/// transcript that dead-ends on an unanswered question, which is both a worse starting point and
/// impossible to mirror on the server side.</para>
///
/// <para><b>How the fork keeps working memory.</b> This factory produces the Lumi-side copy only,
/// and deliberately leaves <see cref="Chat.CopilotSessionId"/> <c>null</c>. The caller
/// (<c>MainViewModel.ForkChatAsync</c>) then tries to fork the source's Copilot session
/// server-side via <c>CopilotService.ForkSessionAsync</c>, cutting it at
/// <see cref="ForkPlan.RetainedUserTurns"/>; on success it assigns the forked session id and the
/// new chat continues with the model's real conversation state — no replay.</para>
///
/// <para>A native fork is not always possible: the source session must be live, which in practice
/// means the source chat is the one currently open. When it fails, the null session id is not a
/// loss — it is exactly the shape <c>ChatViewModel.ShouldReplayTranscriptAfterSessionReset</c>
/// already recognises (messages present + no session id), so the fork's first send replays the
/// copied transcript through <c>BuildSessionRecoveryReplayPrompt</c>, using the same mechanism
/// Lumi uses to recover a lost session.</para>
/// </summary>
public static class ChatForkFactory
{
    /// <summary>Origin marker for a branch taken from one message ("Fork from here").</summary>
    private const string ForkMarker = "fork";

    /// <summary>Origin marker for a whole-chat duplicate ("Duplicate chat").</summary>
    private const string CopyMarker = "copy";

    /// <summary>
    /// Plans an unsaved fork of <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The chat being forked. Not modified.</param>
    /// <param name="sourceMessages">
    /// The source chat's messages. Passed explicitly because a chat's messages are lazily loaded
    /// from its side file, so the caller must ensure they are present first.
    /// </param>
    /// <param name="throughMessageId">
    /// The message the user forked from, or null to copy the whole transcript. An assistant message
    /// is kept; a user message is excluded and returned as
    /// <see cref="ForkPlan.ComposerPrefill"/> instead. An unknown id copies everything.
    /// </param>
    public static ForkPlan CreateFork(
        Chat source,
        IReadOnlyList<ChatMessage> sourceMessages,
        Guid? throughMessageId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceMessages);

        var now = DateTimeOffset.Now;
        var fork = new Chat
        {
            // Id is a fresh Guid from the initialiser: it names the chat's side file, so it must
            // never be shared with the source.
            // A whole-chat duplicate and a branch taken from one message are different actions to
            // the user, so their titles say which one produced this chat.
            Title = BuildForkTitle(source.Title, throughMessageId is null ? CopyMarker : ForkMarker),
            ProjectId = source.ProjectId,
            AgentId = source.AgentId,
            CreatedAt = now,
            UpdatedAt = now,

            // Setup carried over so the fork is immediately usable in the same context.
            ActiveSkillIds = [..source.ActiveSkillIds],
            ActiveExternalSkillNames = [..source.ActiveExternalSkillNames],
            ActiveMcpServerNames = [..source.ActiveMcpServerNames],
            HasExplicitMcpServerSelection = source.HasExplicitMcpServerSelection,
            SdkAgentName = source.SdkAgentName,
            WorktreePath = source.WorktreePath,
            LastModelUsed = source.LastModelUsed,
            LastReasoningEffortUsed = source.LastReasoningEffortUsed,
            LastContextWindowTierUsed = source.LastContextWindowTierUsed,
            PlanContent = source.PlanContent,

            // Breadcrumb back to the parent, and which action created this chat.
            ForkedFromChatId = source.Id,
            ForkedFromTitle = source.Title,
            ForkedFromMessage = throughMessageId is not null,

            // Deliberately NOT copied:
            // - CopilotSessionId / SessionProviderSignature: sharing a server session would make
            //   both chats write into the same conversation. The caller assigns a *forked* session
            //   id when the native fork succeeds; leaving it null opts into transcript replay.
            // - Token and context counters: they describe the source session's usage.
            // - FollowUpSuggestions: they belong to the source's last assistant turn.
            // - IsPinned: a fork starts unpinned so it doesn't displace the original.
        };

        var (take, prefill) = ResolveCut(sourceMessages, throughMessageId);
        fork.Messages.AddRange(CopyMessages(sourceMessages, take));
        fork.MessageCount = fork.Messages.Count;

        var retainedUserTurns = fork.Messages.Count(static m => m.Role == "user");
        return new ForkPlan(fork, prefill, retainedUserTurns);
    }

    /// <summary>
    /// Works out how many leading messages the fork keeps, and whether the fork point should become
    /// a composer draft instead of a copied message.
    /// </summary>
    private static (int Take, string? Prefill) ResolveCut(
        IReadOnlyList<ChatMessage> sourceMessages,
        Guid? throughMessageId)
    {
        if (throughMessageId is not Guid cutOff)
            return (sourceMessages.Count, null);

        var index = -1;
        for (var i = 0; i < sourceMessages.Count; i++)
        {
            if (sourceMessages[i].Id == cutOff) { index = i; break; }
        }

        // An id that isn't in this transcript can't cut it; copying everything is the safe reading
        // of "fork this chat".
        if (index < 0)
            return (sourceMessages.Count, null);

        var target = sourceMessages[index];
        if (target.Role != "user")
            return (index + 1, null);

        // Forking from your own turn means "I want to ask this differently", so the branch stops at
        // the previous answer and the prompt comes back as an editable draft. Keeping it would end
        // the transcript on an unanswered question the model has no matching state for.
        return (index, target.Content);
    }

    /// <summary>
    /// Appends an origin marker, avoiding "(copy) (copy)" when copying a copy. Repeated copies of
    /// the same chat get "(copy 2)", "(copy 3)", … so siblings stay distinguishable.
    /// </summary>
    /// <param name="marker">
    /// The word describing how this chat came about — "copy" for a whole-chat duplicate, "fork" for
    /// a branch taken from one message. Keeping the two distinct is what makes the sidebar readable
    /// when a chat has both kinds of descendant.
    /// </param>
    public static string BuildForkTitle(string? sourceTitle, string marker = ForkMarker)
    {
        var title = string.IsNullOrWhiteSpace(sourceTitle) ? "Chat" : sourceTitle.Trim();
        var suffix = $" ({marker})";

        if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return title[..^suffix.Length] + $" ({marker} 2)";

        var numberedMarker = $" ({marker} ";
        if (title.EndsWith(')'))
        {
            var markerStart = title.LastIndexOf(numberedMarker, StringComparison.OrdinalIgnoreCase);
            if (markerStart >= 0)
            {
                var counterText = title[(markerStart + numberedMarker.Length)..^1];
                if (int.TryParse(counterText, out var counter) && counter > 0)
                    return title[..markerStart] + $" ({marker} {counter + 1})";
            }
        }

        return title + suffix;
    }

    private static List<ChatMessage> CopyMessages(IReadOnlyList<ChatMessage> sourceMessages, int take)
    {
        var copies = new List<ChatMessage>(take);
        for (var i = 0; i < take; i++)
        {
            var source = sourceMessages[i];

            // A message still streaming when the fork was taken has no complete content and no
            // matching live turn in the new chat, so it is dropped rather than frozen mid-word.
            if (source.IsStreaming && string.IsNullOrWhiteSpace(source.Content))
                continue;

            copies.Add(CopyMessage(source));
        }

        return copies;
    }

    /// <summary>
    /// Copies a message into the fork. The id is regenerated because transcript identity,
    /// de-duplication and stable transcript keys are all keyed by message id, and the source
    /// messages keep living in the original chat.
    /// </summary>
    private static ChatMessage CopyMessage(ChatMessage source)
    {
        var copy = source.Clone();
        copy.Id = Guid.NewGuid();

        // The fork is a static copy, so unfinished work must be normalised to a terminal state: a
        // tool left "InProgress" would spin forever, and an unanswered ask_question card would stay
        // clickable while wired to a session that no longer exists.
        copy.IsStreaming = false;
        copy.ToolStatus = NormalizeToolStatus(copy.ToolStatus);
        return copy;
    }

    private static string? NormalizeToolStatus(string? status)
    {
        if (status is null)
            return null;

        return status is "Completed" or "Failed" or "Stopped" ? status : "Stopped";
    }
}
