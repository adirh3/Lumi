using System;
using System.Collections.Generic;
using Lumi.Models;

namespace Lumi.Services.Storage;

/// <summary>
/// Facets a stored message can carry so features that summarize a whole chat (its agents, files,
/// skills, questions...) can find the few rows they need through one small index instead of reading
/// the transcript. Classified when the row is written, partly from <see cref="ToolDisplayHelper"/>'s
/// tool lists: changing those changes what new rows store. A run's step that carries any facet shows
/// outside the run, so an older exchange's build reads it (<see cref="ViewModels.HistoryExchangeRows"/>).
/// </summary>
internal static class ChatMessageKinds
{
    public const string Agent = "agent";
    public const string Deliverable = "deliverable";
    public const string Edit = "edit";
    public const string Skill = "skill";
    public const string Question = "question";
    public const string Link = "link";
    public const string Intent = "intent";
    public const string Web = "web";
    public const string Image = "image";

    /// <summary>A file the session reported changed without an edit step of its own.</summary>
    public const string ChangedFile = "changed-file";

    /// <summary>A step that opened an app (an MCP App or a Copilot canvas, <see cref="ChatMessage.EmbeddedApp"/>).</summary>
    public const string App = "app";

    /// <summary>An answer whose interactive blocks (a form, a checklist) the reader edited.</summary>
    public const string Interaction = "interaction";

    public static string? Classify(ChatMessage message)
    {
        if (message.LinkedChatId is not null)
            return Link;
        if (message.EmbeddedApp is not null)
            return App;
        if (!string.Equals(message.Role, "tool", StringComparison.Ordinal) || message.ToolName is not { Length: > 0 } tool)
        {
            // An answer can carry skills too; it is then a skill row, which widget reads also look at.
            return message.ActiveSkills.Count > 0 ? Skill
                : message.Role == "assistant" && message.InteractionStates is { Count: > 0 } ? Interaction
                : null;
        }
        if (ToolDisplayHelper.IsSubagentTool(tool))
            return Agent;
        if (tool is "announce_file")
            return Deliverable;
        if (ToolDisplayHelper.IsFileEditTool(tool))
            return Edit;
        if (tool is ToolDisplayHelper.WorkspaceFileChangedToolName)
            return ChangedFile;
        if (tool is "skill" or "fetch_skill")
            return Skill;
        if (tool is "ask_question" or "ask_input")
            return Question;
        if (tool is "report_intent")
            return Intent;
        if (tool is "web_search" or "web_fetch" or "lumi_fetch")
            return Web;
        if (tool is "view" or "read_file" or "read" && LooksLikeImagePath(message.Content))
            return Image;
        return null;
    }

    private static bool LooksLikeImagePath(string? arguments)
    {
        if (string.IsNullOrEmpty(arguments))
            return false;
        foreach (var extension in ImageExtensions)
        {
            if (arguments.Contains(extension, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".ico"];
}

/// <summary>A message prepared for a row: its indexed columns plus the JSON body.</summary>
internal sealed record EncodedMessage(
    long Seq,
    Guid Id,
    string Role,
    long TimestampMs,
    string? ToolCallId,
    string? ParentToolCallId,
    string? ToolName,
    string? Kind,
    int TextLength,
    string Body)
{
    public static EncodedMessage From(ChatMessage message, long seq)
        => new(
            seq,
            message.Id,
            message.Role ?? "",
            message.Timestamp.ToUnixTimeMilliseconds(),
            message.ToolCallId,
            message.ParentToolCallId,
            message.ToolName,
            ChatMessageKinds.Classify(message),
            message.Content?.Length ?? 0,
            ChatStorageJson.SerializeMessage(message));
}

/// <summary>One write to a chat: its row, messages to upsert and messages to remove, applied atomically.</summary>
internal sealed class ChatWriteBatch
{
    public required Guid ChatId { get; init; }

    /// <summary>The chat's metadata JSON, so the row exists even before the index is saved.</summary>
    public required string MetadataJson { get; init; }

    public required long UpdatedAtMs { get; init; }
    public required long NextSeq { get; init; }
    public List<EncodedMessage> Upserts { get; } = [];
    public List<long> DeletedSeqs { get; } = [];

    /// <summary>When set, every message at or after this position is removed before the upserts.</summary>
    public long? DeleteFromSeq { get; init; }

    public bool HasMessageChanges => Upserts.Count > 0 || DeletedSeqs.Count > 0 || DeleteFromSeq is not null;
}

/// <summary>A chat's row as stored: its metadata and transcript counters.</summary>
internal sealed record StoredChat(
    Chat Chat,
    int MessageCount,
    long NextSeq,
    long ContentVersion,
    ChatImportState ImportState);

internal enum ChatImportState
{
    /// <summary>The transcript lives in the database.</summary>
    Imported = 0,

    /// <summary>The transcript is still in the legacy <c>chats/{id}.json</c> file.</summary>
    Pending = 1,

    /// <summary>
    /// The legacy file is damaged part-way. What was read before the damage is stored; the file is
    /// kept untouched.
    /// </summary>
    Failed = 2,

    /// <summary>
    /// The legacy file could not be read (locked, no access). It is read again at every start until it
    /// can be. Messages added meanwhile are stored from <see cref="LegacyChatImporter.UnreadableAppendBase"/>
    /// on, so the legacy transcript goes in before them.
    /// </summary>
    Unreadable = 3,
}

/// <summary>
/// A user, assistant, system or error message's place in a transcript, without its body. A user
/// message also comes with its author, how its text starts and whether it is a form's receipt: what
/// tells a message of the user's own from a job's wake (see <c>TranscriptBuilder.ShowsAsUserMessage</c>).
/// </summary>
internal readonly record struct ChatOutlineEntry(
    long Seq,
    Guid Id,
    string Role,
    DateTimeOffset Timestamp,
    int TextLength,
    string? Author = null,
    string? TextStart = null,
    bool IsReceipt = false);

/// <summary>The newest exchange of a chat, read to become its live tail.</summary>
internal sealed record ChatTailRead(
    IReadOnlyList<ChatMessage> Messages,
    long TailStartSeq,
    int HistoryCount,
    long NextSeq,
    HistoryConversationCounts HistoryCounts);

/// <summary>User messages and answered assistant messages in a chat's history (before its tail).</summary>
internal readonly record struct HistoryConversationCounts(int UserMessages, int AnsweredAssistantMessages, int UserMessagesWithText = 0);

/// <summary>A recent user prompt read across chats for suggestions.</summary>
internal readonly record struct StoredUserPrompt(
    Guid ChatId,
    Guid MessageId,
    string Content,
    DateTimeOffset Timestamp);

/// <summary>A stored tool call's id, its parent's and its tool, without its body.</summary>
internal readonly record struct StoredToolCall(
    string ToolCallId,
    string? ParentToolCallId,
    string? ToolName);

/// <summary>
/// What one file edit changed, as SQLite counts it from the stored edit (see
/// <see cref="ChatStore.ReadOverviewAsync"/>): its file, and lines added and removed as
/// <see cref="ToolDisplayHelper.ExtractAllDiffs"/> and the transcript's file changes count them.
/// </summary>
internal readonly record struct StoredFileEdit(
    long Seq,
    string FilePath,
    int LinesAdded,
    int LinesRemoved,
    bool IsCreate);

/// <summary>
/// What an overview of a chat's history lists: its conversation and steps (see
/// <see cref="ChatStore.ReadOverviewAsync"/>), and what each of its file edits changed. Private copies.
/// </summary>
internal sealed record ChatOverview(List<ChatMessage> Messages, List<StoredFileEdit> FileEdits)
{
    public static ChatOverview Empty => new([], []);
}

/// <summary>A stored message without its body: its position, role, facet and tool call links.</summary>
internal readonly record struct StoredMessageHeader(
    long Seq,
    string Role,
    string? Kind,
    string? ToolName,
    string? ToolCallId,
    string? ParentToolCallId);

/// <summary>
/// An older exchange without its message bodies: its messages' headers, the headers of the steps its
/// runs took after it (a background run that went on working), and the tool calls of earlier
/// exchanges that its steps continue.
/// </summary>
internal sealed record ExchangeOutline(
    IReadOnlyList<StoredMessageHeader> Messages,
    IReadOnlyList<StoredMessageHeader> LaterRunSteps,
    IReadOnlyList<StoredToolCall> EarlierToolCalls)
{
    public static ExchangeOutline Empty { get; } = new([], [], []);
}

/// <summary>An older exchange read for its build: its outline, and the messages picked from it, oldest first.</summary>
internal sealed record ExchangeRead(
    ExchangeOutline Outline,
    List<ChatMessage> Messages,
    List<ChatMessage> LaterRunSteps);
