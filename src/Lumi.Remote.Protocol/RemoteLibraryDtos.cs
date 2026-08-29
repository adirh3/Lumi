namespace Lumi.Remote.Protocol;

public sealed class RemoteProject
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Instructions { get; set; }
    public string? WorkingDirectory { get; set; }
    public int ChatCount { get; set; }
    public bool IsCodingProject { get; set; }
    public bool DefaultNewChatsUseWorktree { get; set; }
}

public sealed class RemoteSkill
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Content { get; set; }
    public string IconGlyph { get; set; } = "⚡";
    public bool IsBuiltIn { get; set; }
}

public sealed class RemoteLumi
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? SystemPrompt { get; set; }
    public string IconGlyph { get; set; } = "✦";
    public bool IsBuiltIn { get; set; }
    public int SkillCount { get; set; }
    public int ToolCount { get; set; }
}

public sealed class RemoteMemory
{
    public Guid Id { get; set; }
    public string Key { get; set; } = "";
    public string Content { get; set; } = "";
    public string? Category { get; set; }
    public string? Scope { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class RemoteMcpServer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string ServerType { get; set; } = "local";
    public string? Command { get; set; }
    public string? Url { get; set; }
    public bool IsEnabled { get; set; }
    public int ToolCount { get; set; }
}

public sealed class RemoteJob
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Guid ChatId { get; set; }
    public Guid? SourceChatId { get; set; }
    public string? SourceChatTitle { get; set; }
    public string TriggerType { get; set; } = "";
    public List<string> ChatEventTypes { get; set; } = [];
    public string? ScheduleSummary { get; set; }
    public bool IsEnabled { get; set; }
    public string? LastRunStatus { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
}

public sealed class RemoteSettings
{
    public string UserName { get; set; } = "";
    public bool IsDarkTheme { get; set; } = true;
    public string? PreferredModel { get; set; }
    public string? ReasoningEffort { get; set; }
    public bool SendWithEnter { get; set; } = true;
    public bool ShowToolCalls { get; set; } = true;
    public bool ShowReasoning { get; set; } = true;
    public bool ShowTimestamps { get; set; }
    public List<string> AvailableModels { get; set; } = [];

    /// <summary>Friendly model labels as <c>model-id=Display Name</c> lines.</summary>
    public List<string> ModelDisplayNames { get; set; } = [];

    /// <summary>
    /// Reasoning efforts each model supports, as <c>model=low,medium,high</c> lines.
    ///
    /// <para>The phone defers chat creation until the first message, so on a blank canvas there is
    /// no chat status to read effort levels from — and without this the effort control could not
    /// appear until after you had already sent something. A flat map keeps the DTO trivially
    /// serialisable while letting the phone answer "what can THIS model do" on its own.</para>
    /// </summary>
    public List<string> ModelReasoningEfforts { get; set; } = [];

    /// <summary>Context-window tiers each model supports, as <c>model=Default,Long context</c>.</summary>
    public List<string> ModelContextWindowTiers { get; set; } = [];

}

/// <summary>Everything the phone needs to render its library tabs, refreshed as a unit.</summary>
public sealed class RemoteLibrary
{
    public List<RemoteProject> Projects { get; set; } = [];
    public List<RemoteSkill> Skills { get; set; } = [];
    public List<RemoteLumi> Lumis { get; set; } = [];
    public List<RemoteMemory> Memories { get; set; } = [];
    public List<RemoteMcpServer> McpServers { get; set; } = [];
    public List<RemoteJob> Jobs { get; set; } = [];
}

/// <summary>Full, untruncated content for one editable library resource.</summary>
public sealed class RemoteLibraryItem
{
    public string Resource { get; set; } = "";
    public string Identifier { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Body { get; set; }
    public string? Glyph { get; set; }
    public string? WorkingDirectory { get; set; }
}

/// <summary>Bounded file autocomplete results for the mobile composer's <c>#</c> trigger.</summary>
public sealed class RemoteFileSuggestions
{
    public List<RemoteChip> Items { get; set; } = [];
}

/// <summary>The one-shot payload a client pulls on connect and whenever it resyncs.</summary>
public sealed class RemoteSnapshot
{
    public int ProtocolVersion { get; set; } = RemoteProtocol.Version;
    public List<string> Capabilities { get; set; } = [];
    /// <summary>True when omitted collections must preserve the client's current cached values.</summary>
    public bool IsPartial { get; set; }
    public string HostName { get; set; } = "";
    public bool IsConnected { get; set; }
    public string? ConnectionStatus { get; set; }
    public Guid? ActiveChatId { get; set; }
    public RemoteChat? ActiveChat { get; set; }
    public RemoteChatPage Chats { get; set; } = new();
    public RemoteLibrary Library { get; set; } = new();
    public RemoteSettings Settings { get; set; } = new();
}
