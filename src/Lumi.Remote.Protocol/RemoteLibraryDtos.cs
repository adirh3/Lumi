namespace Lumi.Remote.Protocol;

using System.Text.Json.Serialization;

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

    /// <summary>
    /// Effective new-chat selections, resolved by the desktop against each model's capabilities.
    /// Labels use the same values as the option catalogs. An absent entry (older desktop) is unknown;
    /// a present entry with a null selection means that setting is not configurable for the model.
    /// </summary>
    public List<RemoteModelDefaults> ModelDefaults { get; set; } = [];

    /// <summary>
    /// The model picker's rows as the desktop's picker draws them: pinned models first in the order
    /// they were pinned, then the rest in catalog order, each with its account and the facts its row
    /// shows. Empty from a desktop without <see cref="RemoteProtocol.Capabilities.ModelPinsV1"/>.
    /// </summary>
    public List<RemoteModelOption> ModelOptions { get; set; } = [];

    /// <summary>Desktop-selected new-chat choices. Null on hosts without launchpad projection.</summary>
    public RemoteNewChatExperience? NewChat { get; set; }

    /// <summary>
    /// Whether the PC sends notifications to phones while the user is away. Null from a desktop
    /// without <see cref="RemoteProtocol.Capabilities.PhoneNotificationsV1"/>.
    /// </summary>
    public bool? PhoneNotificationsEnabled { get; set; }

    /// <summary>Minutes without activity after which the PC counts the user as away. Null when unknown.</summary>
    public int? PhoneNotificationAwayMinutes { get; set; }

    /// <summary>
    /// The PC's public Web Push key (VAPID, base64url): a browser subscribes to pushes with it and
    /// hands the PC the subscription (<see cref="RemoteProtocol.Actions.RegisterWebPush"/>). Null from
    /// a PC that cannot push to browsers.
    /// </summary>
    public string? WebPushKey { get; set; }
}

public sealed class RemoteNewChatExperience
{
    public string Greeting { get; set; } = "";

    /// <summary>What waits on the user or, with nothing waiting, the invitation; phones before the launchpad show it.</summary>
    public string Brief { get; set; } = "";
    public string SetupsTitle { get; set; } = "";
    public List<RemoteChatSetup> Setups { get; set; } = [];

    /// <summary>Three starters for the hour, for phones before the launchpad; it lists them under For you.</summary>
    public List<RemoteChatStarter> Starters { get; set; } = [];

    /// <summary>
    /// What a plain chat starts with: no project, no agent, the user's default model. Null from a desktop without
    /// <see cref="RemoteProtocol.Capabilities.LaunchpadV1"/>, as are the launchpad's other parts.
    /// </summary>
    public RemoteChatSetup? JustChat { get; set; }

    /// <summary>The tabs after For you, always in this order, each with its starters.</summary>
    public List<RemoteLaunchpadTab> Tabs { get; set; } = [];

    /// <summary>
    /// For you, and the line under the greeting, for each place a new chat can start: no project first, then the
    /// projects most recently used. A project without one starts from the first.
    /// </summary>
    public List<RemoteLaunchpadScope> Scopes { get; set; } = [];

    /// <summary>Other's list: the projects, most recently used first, then the agents, each with how it starts.</summary>
    public List<RemoteLaunchpadPlace> Places { get; set; } = [];

    /// <summary>The launchpad's own words, in the PC's language, so the phone's match its starters.</summary>
    public RemoteLaunchpadLabels? Labels { get; set; }

    /// <summary>How many chats wait on the user, and that said in words ("2 chats need you").</summary>
    public int NeedsYouCount { get; set; }
    public string? NeedsYou { get; set; }

    /// <summary>The chats waiting on the user, most urgent first (a question, then a reply), the first few of them.</summary>
    public List<RemoteLaunchpadChat> NeedsYouChats { get; set; } = [];
}

public sealed class RemoteChatSetup
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Glyph { get; set; } = "";
    public Guid? ProjectId { get; set; }
    public string? ProjectName { get; set; }
    public Guid? AgentId { get; set; }
    public string? AgentName { get; set; }
    public string? AgentGlyph { get; set; }
    public bool UseWorktree { get; set; }
    public string? Model { get; set; }
    public string? Quality { get; set; }

    /// <summary>
    /// What its capsule says beside its name: the agent working in the project, or the model; for one of a place's
    /// several setups, all that tells it apart ("Worktree · GPT 6.1 Sol").
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>Another setup in the row has its name, so it always says its <see cref="Detail"/>.</summary>
    public bool IsVariant { get; set; }

    /// <summary>The user kept it on new chats, rather than Lumi suggesting it from use.</summary>
    public bool IsKept { get; set; }

    /// <summary>The reasoning effort's id (<see cref="Quality"/> is its name), so the phone can name the setup back.</summary>
    public string? Effort { get; set; }

    /// <summary>Its project's folder is a git repository, so it can start in a worktree.</summary>
    public bool CanUseWorktree { get; set; }
}

public sealed class RemoteChatStarter
{
    public string Glyph { get; set; } = "";
    public string Label { get; set; } = "";
    public string Prompt { get; set; } = "";
}

/// <summary>A tab of the launchpad: a kind of task and its starters.</summary>
public sealed class RemoteLaunchpadTab
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public List<RemoteLaunchpadStarter> Starters { get; set; } = [];
}

/// <summary>For you, and the line under the greeting, where a new chat starts (a project, or none).</summary>
public sealed class RemoteLaunchpadScope
{
    public Guid? ProjectId { get; set; }

    /// <summary>The project brief's opening sentence, or the invitation.</summary>
    public string Subtitle { get; set; } = "";
    public List<RemoteLaunchpadStarter> ForYou { get; set; } = [];
}

/// <summary>A conversation starter. Picking it puts its prompt in the composer; nothing is sent.</summary>
public sealed class RemoteLaunchpadStarter
{
    /// <summary>Its stable id ("plan.trip"), which the PC learns the user's habits by; null for a plan from a brief.</summary>
    public string? Id { get; set; }

    /// <summary>Its line icon, as a key the phone draws with Strata's icons ("globe").</summary>
    public string Icon { get; set; } = "";

    /// <summary>What its capsule says. A blank is a short bracketed label ("Plan a trip to [where?]").</summary>
    public string Label { get; set; } = "";

    /// <summary>What goes in the composer, blanks and all, for the user to fill in.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>Why For you offers it ("From Lumi's brief", "This morning"), when it says.</summary>
    public string? Reason { get; set; }
}

/// <summary>A project or agent in Other's list, and how a new chat there starts: the way the user last worked there.</summary>
public sealed class RemoteLaunchpadPlace
{
    public Guid Id { get; set; }
    public bool IsProject { get; set; }
    public string Title { get; set; } = "";

    /// <summary>When the user last worked there ("40m ago"), if ever.</summary>
    public string? Hint { get; set; }

    /// <summary>One of the user's kept setups is in this place.</summary>
    public bool IsKept { get; set; }

    public RemoteChatSetup Setup { get; set; } = new();
}

/// <summary>A chat waiting on the user, as the launchpad lists it.</summary>
public sealed class RemoteLaunchpadChat
{
    public Guid ChatId { get; set; }
    public string Title { get; set; } = "";

    /// <summary>It asks the user something; otherwise it has a reply the user hasn't read.</summary>
    public bool IsWaiting { get; set; }

    /// <summary>"Asked you something", "Replied".</summary>
    public string StateLabel { get; set; } = "";

    /// <summary>Its project and what it asks or said last.</summary>
    public string Meta { get; set; } = "";

    /// <summary>When it last changed ("5m ago").</summary>
    public string TimeLabel { get; set; } = "";
}

/// <summary>The launchpad's words, as the PC's launchpad says them.</summary>
public sealed class RemoteLaunchpadLabels
{
    public string ForYou { get; set; } = "";
    public string JustChat { get; set; } = "";
    public string Other { get; set; } = "";
    public string OtherTitle { get; set; } = "";
    public string OtherSearch { get; set; } = "";
    public string OtherProjects { get; set; } = "";
    public string OtherAgents { get; set; } = "";
    public string OtherEmpty { get; set; } = "";
    public string Keep { get; set; } = "";
    public string KeepOnNewChats { get; set; } = "";
    public string StartInWorktree { get; set; } = "";
    public string DontSuggest { get; set; } = "";

    /// <summary>The setup menu's model list ("Starts with").</summary>
    public string StartsWith { get; set; } = "";

    /// <summary>What opens a setup's menu ("Customize this setup").</summary>
    public string Customize { get; set; } = "";
}

public sealed class RemoteModelDefaults
{
    public string Model { get; set; } = "";
    public string? Quality { get; set; }
    public string? ContextWindowTier { get; set; }
}

/// <summary>One row of the desktop's model picker. Its texts are the desktop's own, already formatted.</summary>
public sealed class RemoteModelOption
{
    public string Model { get; set; } = "";
    /// <summary>Configured selections represented by this row, such as a Fusion routing tier.</summary>
    public List<string> SelectionAliases { get; set; } = [];

    /// <summary>The row's name. Unlike the model's display name it leaves out the account, which heads its group.</summary>
    public string? DisplayName { get; set; }

    /// <summary>The group the row is listed under: "GitHub Copilot", or the connected account's name.</summary>
    public string? ProviderName { get; set; }

    /// <summary>The connected account the model runs on; null for a Copilot model. Qualifies a pinned row's name.</summary>
    public string? Account { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsPinned { get; set; }

    /// <summary>Premium-request multiplier ("1×", "0.33×", "Included"); null when the catalog reports none.</summary>
    public string? Cost { get; set; }

    /// <summary>Largest context the model offers ("200K", "1M"); null when unknown.</summary>
    public string? ContextSize { get; set; }

    /// <summary>A short state beside the name, such as a model that still has to be enabled.</summary>
    public string? Status { get; set; }
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

    /// <summary>Content versions of <see cref="Chats"/>, <see cref="Library"/> and <see cref="Settings"/>.</summary>
    public string? ChatsVersion { get; set; }
    public string? LibraryVersion { get; set; }
    public string? SettingsVersion { get; set; }

    /// <summary>
    /// Sections left out because the request named their current version
    /// (<see cref="RemoteProtocol.SnapshotSections"/>). The phone keeps its cached copy of each.
    /// </summary>
    public List<string>? UnchangedSections { get; set; }
}
