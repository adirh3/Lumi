namespace Lumi.Remote.Protocol;

public sealed class RemoteChat
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public Guid? ProjectId { get; set; }
    public string? ProjectName { get; set; }
    public Guid? AgentId { get; set; }
    public string? AgentName { get; set; }
    public string? AgentGlyph { get; set; }
    public int MessageCount { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsPinned { get; set; }
    public bool IsRunning { get; set; }
    public bool HasUnreadMessages { get; set; }
    public string? LastModelUsed { get; set; }
    public string? Preview { get; set; }
}

/// <summary>A date bucket ("Today", "Yesterday", ...) mirroring the desktop sidebar grouping.</summary>
public sealed class RemoteChatGroup
{
    public string Label { get; set; } = "";
    public List<RemoteChat> Chats { get; set; } = [];
}

/// <summary>A bounded, server-filtered page of chat summaries.</summary>
public sealed class RemoteChatPage
{
    public int Offset { get; set; }
    public int TotalCount { get; set; }
    public bool HasMore { get; set; }
    public string? Query { get; set; }
    public Guid? ProjectId { get; set; }
    public string PinnedGroupLabel { get; set; } = "Pinned";
    public string TodayGroupLabel { get; set; } = "Today";
    public List<RemoteChatGroup> Groups { get; set; } = [];
    public List<Guid> RemovedChatIds { get; set; } = [];
}

/// <summary>Live per-chat runtime state pushed on <see cref="RemoteProtocol.Events.ChatStatus"/>.</summary>
public sealed class RemoteChatStatus
{
    public Guid ChatId { get; set; }
    public bool IsBusy { get; set; }
    public bool IsStreaming { get; set; }
    public string? StatusText { get; set; }
    public string? Model { get; set; }
    public long ContextCurrentTokens { get; set; }
    public long ContextTokenLimit { get; set; }
    public string? PlanContent { get; set; }
    public List<string> Suggestions { get; set; } = [];

    // ── Composer configuration ────────────────────────────────────────────────────────────────
    // Everything below mirrors what the desktop composer shows for the open chat, so the phone can
    // offer the same choices instead of silently inheriting whatever the PC was last set to.

    /// <summary>Display name of the reasoning-effort level ("Balanced", "Thorough", ...).</summary>
    public string? Quality { get; set; }

    /// <summary>Reasoning-effort levels this model supports. Empty when the model has no choice.</summary>
    public List<string> QualityLevels { get; set; } = [];

    /// <summary>Display name of the selected context-window tier ("Standard", "Long context").</summary>
    public string? ContextWindowTier { get; set; }

    /// <summary>Context-window tiers this model supports. Empty when the model offers only one.</summary>
    public List<string> ContextWindowTiers { get; set; } = [];

    public string? AgentName { get; set; }
    public Guid? AgentId { get; set; }
    public string? AgentGlyph { get; set; }
    public string? ProjectName { get; set; }
    public Guid? ProjectId { get; set; }
    public bool UsesWorktree { get; set; }

    /// <summary>Skills currently attached to the chat, by name.</summary>
    public List<string> SkillNames { get; set; } = [];

    /// <summary>MCP servers currently attached to the chat, by name.</summary>
    public List<string> McpNames { get; set; } = [];
    public List<RemoteChip> AvailableAgents { get; set; } = [];
    public List<RemoteChip> AvailableSkills { get; set; } = [];
    public List<RemoteChip> AvailableMcps { get; set; } = [];
    public List<RemoteChip> AvailableProjects { get; set; } = [];
    public bool HasComposerCatalogs { get; set; }
}

public sealed class RemoteConnectionStatus
{
    public bool IsConnected { get; set; }
    public string? Status { get; set; }
}

/// <summary>
/// One selectable entry in a composer picker (agent, skill, MCP server or project). Carries the
/// glyph and description the desktop chip shows so the phone's picker looks identical.
/// </summary>
public sealed class RemoteChip
{
    public string Name { get; set; } = "";
    public string? Glyph { get; set; }
    public string? Description { get; set; }
    public string? Value { get; set; }
}

public sealed class RemoteToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Input { get; set; }
    public string? Output { get; set; }
    /// <summary>research | work | verify | other.</summary>
    public string Category { get; set; } = "other";
    /// <summary>InProgress | Completed | Failed | Stopped.</summary>
    public string Status { get; set; } = "Completed";
    public double? DurationMs { get; set; }
}

public sealed class RemoteFileChange
{
    /// <summary>Display-safe path relative to the active workspace when possible.</summary>
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    /// <summary>Created | Modified | Deleted.</summary>
    public string Operation { get; set; } = "Modified";
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
}

public sealed class RemoteActivityDetails
{
    public Guid ChatId { get; set; }
    public string ActivityId { get; set; } = "";
    public List<RemoteToolCall> Tools { get; set; } = [];
    public int TotalFileChangeCount { get; set; }
    public List<RemoteFileChange> FileChanges { get; set; } = [];
}

public sealed class RemoteAttachment
{
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? Extension { get; set; }
    /// <summary>Authoritative announce_file message ID. Null for ordinary user attachments.</summary>
    public Guid? MessageId { get; set; }
}

public sealed class RemoteSource
{
    public string Title { get; set; } = "";
    public string? Snippet { get; set; }
    public string? Url { get; set; }
}

public sealed class RemoteInlineImage
{
    public int Index { get; set; }
    public string FileName { get; set; } = "";
}

public sealed class RemoteQuestion
{
    public string QuestionId { get; set; } = "";
    public string Text { get; set; } = "";
    public List<string> Options { get; set; } = [];
    public bool AllowFreeText { get; set; } = true;
    public bool AllowMultiSelect { get; set; }
    public bool IsAnswered { get; set; }
    public string? Answer { get; set; }
}

/// <summary>
/// One renderable row in the mobile transcript. A single flat shape (rather than a polymorphic
/// hierarchy) keeps the wire format trim-safe, source-generator friendly and forward compatible:
/// an unknown <see cref="Kind"/> degrades to plain text instead of failing to deserialize.
/// </summary>
public sealed class RemoteTranscriptItem
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = RemoteProtocol.ItemKinds.Assistant;
    public string? Text { get; set; }
    public string? Author { get; set; }
    public string? RequestId { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    /// <summary>None | Queued | Steering | Steered | Failed for a mid-turn user message.</summary>
    public string? SteerState { get; set; }
    public bool IsStreaming { get; set; }
    /// <summary>Header label for grouped kinds, or a linked-chat title embedded in an answer.</summary>
    public string? Label { get; set; }
    public string? Status { get; set; }
    public double? DurationMs { get; set; }
    public string? Model { get; set; }
    public string? ActivityId { get; set; }
    public int? ActionCount { get; set; }
    public long? DetailVersion { get; set; }
    public int? FileChangeCount { get; set; }
    public List<RemoteToolCall>? Tools { get; set; }
    public List<RemoteFileChange>? FileChanges { get; set; }
    public List<RemoteAttachment>? Attachments { get; set; }
    public List<RemoteSource>? Sources { get; set; }
    public List<RemoteInlineImage>? InlineImages { get; set; }
    public RemoteQuestion? Question { get; set; }
    public Guid? LinkedChatId { get; set; }
}

/// <summary>One user turn plus everything the assistant produced in response.</summary>
public sealed class RemoteTranscriptTurn
{
    public string Id { get; set; } = "";
    public List<RemoteTranscriptItem> Items { get; set; } = [];
}

public sealed class RemoteTranscript
{
    public Guid ChatId { get; set; }
    public string Title { get; set; } = "";
    /// <summary>
    /// Stable for one running server generation. A changed value starts a new revision sequence.
    /// Null is the backward-compatible legacy shape and keeps ordinary monotonic revision handling.
    /// </summary>
    public string? RevisionEpoch { get; set; }
    /// <summary>Increments on every transcript mutation so clients can skip redundant renders.</summary>
    public long Revision { get; set; }
    /// <summary>Zero-based index of the first raw chat message represented by this window.</summary>
    public int WindowStartMessageIndex { get; set; }
    /// <summary>Exclusive zero-based index immediately after the last raw message in this window.</summary>
    public int WindowEndMessageIndex { get; set; }
    public int TotalRawMessageCount { get; set; }
    public bool HasEarlierMessages { get; set; }
    public bool HasLaterMessages { get; set; }
    /// <summary>True when this response represents the current tail of the chat.</summary>
    public bool IsLatestWindow { get; set; } = true;
    public List<RemoteTranscriptTurn> Turns { get; set; } = [];
    public RemoteChatStatus Status { get; set; } = new();
}

/// <summary>Hot-path streaming delta so the phone updates without refetching the transcript.</summary>
public sealed class RemoteStreamDelta
{
    public Guid ChatId { get; set; }
    public string ItemId { get; set; } = "";
    /// <summary>Expected current client text length. -1 means replace the row wholesale.</summary>
    public int Offset { get; set; }
    /// <summary>Only the newly appended bounded text.</summary>
    public string Text { get; set; } = "";
    public bool IsReasoning { get; set; }
}

/// <summary>
/// Tells the phone that a chat's transcript changed shape and must be refetched. Both sides go
/// through this DTO on purpose: it used to be hand-written JSON on the server and parsed as a bare
/// GUID on the client, so every invalidation was silently dropped and phones never refreshed.
/// </summary>
public sealed class RemoteTranscriptInvalidated
{
    public Guid ChatId { get; set; }
    public string? RevisionEpoch { get; set; }
    public long Revision { get; set; }
}
