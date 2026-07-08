using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Lumi.Models;

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Role { get; set; } = "user"; // user, assistant, system, tool, reasoning, error
    public string Content { get; set; } = "";
    public string? Author { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string? ToolName { get; set; }
    public string? ToolCallId { get; set; }
    public string? ParentToolCallId { get; set; }
    public string? ToolStatus { get; set; } // InProgress, Completed, Failed, Stopped
    public string? ToolOutput { get; set; }
    public string? QuestionId { get; set; }
    public string? QuestionText { get; set; }
    public string? QuestionOptions { get; set; }
    public bool? QuestionAllowFreeText { get; set; }
    public bool? QuestionAllowMultiSelect { get; set; }
    public bool IsStreaming { get; set; }
    public string? Model { get; set; }
    public List<string> Attachments { get; set; } = [];
    public List<SearchSource> Sources { get; set; } = [];
    public List<SkillReference> ActiveSkills { get; set; } = [];
}

public class SkillReference
{
    public string Name { get; set; } = "";
    public string Glyph { get; set; } = "\u26A1";
    public string Description { get; set; } = "";

    /// <summary>
    /// Full skill markdown as delivered by the SDK's <c>skill.invoked</c> event. Persisted on the
    /// chip so the preview renders directly, without re-scanning the filesystem — which is the only
    /// way builtin/plugin/remote skills (that have no reachable SKILL.md on this machine) resolve.
    /// </summary>
    public string? Content { get; set; }
}

public static class ModelContextWindowTiers
{
    public const string Default = "default";
    public const string LongContext = "long_context";
}

public class SearchSource
{
    public string Title { get; set; } = "";
    public string Snippet { get; set; } = "";
    public string Url { get; set; } = "";
}

public class Chat : INotifyPropertyChanged
{
    private string _title = "New Chat";
    private bool _isRunning;
    private bool _hasUnreadMessages;
    private bool _showProjectBadge;
    private string? _projectBadgeText;
    private List<string> _activeExternalSkillNames = [];
    private List<string> _followUpSuggestions = [];

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title
    {
        get => _title;
        set
        {
            var title = value ?? "";
            if (_title == title) return;
            _title = title;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        }
    }
    public Guid? ProjectId { get; set; }
    public Guid? AgentId { get; set; }
    public string? CopilotSessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    [JsonIgnore]
    public List<ChatMessage> Messages { get; set; } = [];
    public List<Guid> ActiveSkillIds { get; set; } = [];
    public List<string> ActiveExternalSkillNames
    {
        get => _activeExternalSkillNames;
        set => _activeExternalSkillNames = value ?? [];
    }
    public List<string> ActiveMcpServerNames { get; set; } = [];
    public bool HasExplicitMcpServerSelection { get; set; }

    /// <summary>Deprecated — session mode is no longer used. Kept for backward-compatible deserialization.</summary>
    public string? SessionMode { get; set; }

    /// <summary>Name of an SDK-discovered agent selected for this chat (not a Lumi agent).</summary>
    public string? SdkAgentName { get; set; }

    /// <summary>Git worktree path when this chat operates in worktree mode. Null means local mode.</summary>
    public string? WorktreePath { get; set; }

    /// <summary>Last model used in this chat. Restored as the selected model when the chat is reopened.</summary>
    public string? LastModelUsed { get; set; }

    /// <summary>Last reasoning effort used in this chat. Restored alongside the selected model when reopened.</summary>
    public string? LastReasoningEffortUsed { get; set; }

    /// <summary>Last context window tier used in this chat. Restored alongside the selected model when reopened.</summary>
    public string? LastContextWindowTierUsed { get; set; }

    /// <summary>Cumulative input tokens consumed across all turns of this chat.</summary>
    public long TotalInputTokens { get; set; }

    /// <summary>Cumulative output tokens consumed across all turns of this chat.</summary>
    public long TotalOutputTokens { get; set; }

    /// <summary>Latest known context window usage for this chat.</summary>
    public long ContextCurrentTokens { get; set; }

    /// <summary>Latest known context window token limit for this chat.</summary>
    public long ContextTokenLimit { get; set; }

    /// <summary>Persisted plan content (markdown) so it survives chat switches and app restarts.</summary>
    public string? PlanContent { get; set; }

    /// <summary>Generated follow-up suggestions for the latest completed assistant turn.</summary>
    public List<string> FollowUpSuggestions
    {
        get => _followUpSuggestions;
        set => _followUpSuggestions = value ?? [];
    }

    /// <summary>Assistant message ID that produced <see cref="FollowUpSuggestions"/>.</summary>
    public Guid? FollowUpSuggestionAssistantMessageId { get; set; }

    /// <summary>Runtime-only flag indicating this chat is actively generating a response.</summary>
    [JsonIgnore]
    public bool IsRunning
    {
        get => _isRunning;
        set { if (_isRunning == value) return; _isRunning = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning))); }
    }

    /// <summary>Runtime-only flag indicating this chat has unread messages from an auto-triggered background task response.</summary>
    [JsonIgnore]
    public bool HasUnreadMessages
    {
        get => _hasUnreadMessages;
        set { if (_hasUnreadMessages == value) return; _hasUnreadMessages = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasUnreadMessages))); }
    }

    /// <summary>Runtime-only: whether the sidebar should show this chat's project folder badge (only in the "All projects" view).</summary>
    [JsonIgnore]
    public bool ShowProjectBadge
    {
        get => _showProjectBadge;
        set { if (_showProjectBadge == value) return; _showProjectBadge = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowProjectBadge))); }
    }

    /// <summary>Runtime-only: display name of this chat's project, shown in the sidebar folder badge.</summary>
    [JsonIgnore]
    public string? ProjectBadgeText
    {
        get => _projectBadgeText;
        set { if (_projectBadgeText == value) return; _projectBadgeText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProjectBadgeText))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class Project : INotifyPropertyChanged
{
    private bool _isRunning;
    private List<string> _additionalContextDirectories = [];

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Instructions { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public List<string> AdditionalContextDirectories
    {
        get => _additionalContextDirectories;
        set => _additionalContextDirectories = value ?? [];
    }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Runtime-only flag indicating at least one chat in this project is actively generating a response.</summary>
    [JsonIgnore]
    public bool IsRunning
    {
        get => _isRunning;
        set { if (_isRunning == value) return; _isRunning = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class Skill
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Content { get; set; } = ""; // Markdown instructions
    public string IconGlyph { get; set; } = "⚡";
    public bool IsBuiltIn { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public class LumiAgent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public string IconGlyph { get; set; } = "✦";
    public bool IsBuiltIn { get; set; }
    public bool IsLearningAgent { get; set; }
    public List<Guid> SkillIds { get; set; } = [];
    public List<string> ToolNames { get; set; } = [];
    public List<Guid> McpServerIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public class McpServer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ServerType { get; set; } = "local"; // "local" or "remote"

    // Local server (stdio) properties
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = [];

    // Remote server (SSE) properties
    public string Url { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = [];

    public List<string> Tools { get; set; } = [];
    public int? Timeout { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public static class BackgroundJobTriggerTypes
{
    public const string Time = "time";
    public const string Script = "script";
}

public static class BackgroundJobScheduleTypes
{
    public const string Interval = "interval";
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";
    public const string Once = "once";
    public const string Cron = "cron";
}

public static class BackgroundJobScriptLanguages
{
    public const string PowerShell = "powershell";
    public const string Python = "python";
    public const string Node = "node";
    public const string Command = "command";
}

public static class BackgroundJobRunStatuses
{
    public const string Idle = "Idle";
    public const string Running = "Running";
    public const string Watching = "Watching";
    public const string Completed = "Completed";
    public const string Skipped = "Skipped";
    public const string Failed = "Failed";
    public const string Waiting = "Waiting";
}

public class BackgroundJob : INotifyPropertyChanged
{
    private bool _isRunning;

    [JsonIgnore]
    internal object SyncRoot { get; } = new();

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChatId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string TriggerType { get; set; } = BackgroundJobTriggerTypes.Time;
    public string ScheduleType { get; set; } = BackgroundJobScheduleTypes.Interval;
    public int IntervalMinutes { get; set; } = 1440;
    public string DailyTime { get; set; } = "08:00";
    public string DaysOfWeek { get; set; } = "Mon,Tue,Wed,Thu,Fri";
    public int MonthlyDay { get; set; } = 1;
    public string CronExpression { get; set; } = "";
    public DateTimeOffset? RunAt { get; set; }
    public string ScriptContent { get; set; } = "";
    public string ScriptLanguage { get; set; } = BackgroundJobScriptLanguages.PowerShell;
    public bool IsEnabled { get; set; } = true;
    public bool IsTemporary { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastRunStartedAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public string LastRunStatus { get; set; } = BackgroundJobRunStatuses.Idle;
    public string LastRunSummary { get; set; } = "";
    public string LastScriptOutput { get; set; } = "";
    public int? LastScriptExitCode { get; set; }
    public int RunCount { get; set; }

    [JsonIgnore]
    public string TriggerDisplay => TriggerType == BackgroundJobTriggerTypes.Script ? "Wake script" : "Time";

    [JsonIgnore]
    public string StatusDisplay => IsEnabled ? LastRunStatus : LastRunStatus == BackgroundJobRunStatuses.Completed ? "Completed" : "Paused";

    [JsonIgnore]
    public bool IsRunning
    {
        get => _isRunning;
        set { if (_isRunning == value) return; _isRunning = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class Memory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = "";
    public string Content { get; set; } = "";
    public string Category { get; set; } = "General";
    public string Scope { get; set; } = MemoryScopes.Global;
    public Guid? ProjectId { get; set; }
    public string Status { get; set; } = MemoryStatuses.Active;
    public string? SourceChatId { get; set; }
    public string Source { get; set; } = "chat";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastReviewedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public int? Confidence { get; set; }
    public string? MaintenanceNote { get; set; }
}

public static class MemoryScopes
{
    public const string Global = "global";
    public const string Project = "project";
}

public static class MemoryStatuses
{
    public const string Active = "active";
    public const string Archived = "archived";
}

public class UserSettings
{
    // ── General ──
    public string? UserName { get; set; }
    public string? UserSex { get; set; } // "male", "female", or null (prefer not to say)
    public bool IsOnboarded { get; set; }
    public bool DefaultsSeeded { get; set; }
    public bool CodingLumiSeeded { get; set; }
    public string Language { get; set; } = "en";
    public bool LaunchAtStartup { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; }
    public string GlobalHotkey { get; set; } = "";
    public bool NotificationsEnabled { get; set; } = true;
    public string DismissedUpdateBannerToken { get; set; } = "";

    // ── Appearance ──
    public bool IsDarkTheme { get; set; } = true;
    public bool IsCompactDensity { get; set; }
    public int FontSize { get; set; } = 14;
    public bool ShowAnimations { get; set; } = true;

    // ── Chat ──
    public bool SendWithEnter { get; set; } = true;
    public bool ShowTimestamps { get; set; } = true;
    public bool ShowToolCalls { get; set; } = true;
    public bool ShowReasoning { get; set; } = true;
    public bool ExpandReasoningWhileStreaming { get; set; } = true;
    public bool AutoGenerateTitles { get; set; } = true;

    /// <summary>
    /// User preference for the companion Workspace panel. null = automatic (shows only on wide
    /// layouts when the chat has artifacts); true = always open; false = always closed. Set by the
    /// header toggle and persisted app-wide.
    /// </summary>
    public bool? WorkspacePanelOpen { get; set; }

    // ── AI & Models ──
    public string PreferredModel { get; set; } = "";
    public string ReasoningEffort { get; set; } = ""; // "", "low", "medium", "high", "xhigh"
    public string ContextWindowTier { get; set; } = ModelContextWindowTiers.Default;

    // ── MCP ──
    // When true, local MCP servers are routed through Lumi's shared proxy so they
    // start once and are reused across chats. When false (default), MCP servers are
    // passed directly to Copilot and initialized per session.
    public bool UseMcpProxy { get; set; }

    // ── Privacy & Data ──
    public bool EnableMemoryAutoSave { get; set; } = true;
    public bool EnableMemoryAutoMaintenance { get; set; } = true;
    public bool AutoSaveChats { get; set; } = true;

    // ── Window ──
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? SidebarWidth { get; set; }
    public bool SidebarCollapsed { get; set; }
    public bool IsMaximized { get; set; }

    // ── Browser ──
    public bool HasImportedBrowserCookies { get; set; }

    // ── Quota (cached, refreshed periodically) ──
    [JsonIgnore] public double? QuotaRemainingPercentage { get; set; }
    [JsonIgnore] public double? QuotaUsedRequests { get; set; }
    [JsonIgnore] public double? QuotaEntitlementRequests { get; set; }
    [JsonIgnore] public string? QuotaResetDate { get; set; }
}

public class AppData
{
    public UserSettings Settings { get; set; } = new();
    public List<Chat> Chats { get; set; } = [];
    public List<Project> Projects { get; set; } = [];
    public List<Skill> Skills { get; set; } = [];
    public List<LumiAgent> Agents { get; set; } = [];
    public List<McpServer> McpServers { get; set; } = [];
    public List<BackgroundJob> BackgroundJobs { get; set; } = [];
    public List<Memory> Memories { get; set; } = [];
}
