using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;
using Lumi.Localization;

namespace Lumi.Models;

/// <summary>
/// Transient delivery status for a user message that was steered into a running turn. Session-only
/// (never persisted) — it exists so the transcript can confirm whether a mid-turn message actually
/// landed in the live turn (<see cref="Steered"/>) versus a normal turn-start message.
/// </summary>
public enum MessageSteerState
{
    None,
    Steering,
    Steered,
    Failed
}

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
    public Guid? LinkedChatId { get; set; }
    public string? LinkedChatTitle { get; set; }
    public string? QuestionId { get; set; }
    public string? QuestionText { get; set; }
    public string? QuestionOptions { get; set; }
    public bool? QuestionAllowFreeText { get; set; }
    public bool? QuestionAllowMultiSelect { get; set; }
    public bool IsStreaming { get; set; }
    public string? Model { get; set; }
    public string? ReasoningEffort { get; set; }
    public string? ContextWindowTier { get; set; }
    public Guid? AgentId { get; set; }
    public string? SdkAgentName { get; set; }
    public bool HasAgentSelection { get; set; }
    public List<string> ActiveMcpServerNames { get; set; } = [];
    public bool HasMcpSelection { get; set; }
    public List<string> Attachments { get; set; } = [];
    public List<SearchSource> Sources { get; set; } = [];
    public List<SkillReference> ActiveSkills { get; set; } = [];

    /// <summary>Session-only steer delivery status (not serialized). Set when this message is steered
    /// into a running turn so the badge survives transcript/VM rebuilds within the session.</summary>
    [JsonIgnore]
    public MessageSteerState SteerDelivery { get; set; }
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

/// <summary>
/// How a <see cref="ByokEndpoint"/> resolves its API key at runtime.
/// </summary>
public enum ByokApiKeyMode
{
    /// <summary>No key is sent (e.g. local Ollama or public endpoints).</summary>
    None = 0,
    /// <summary>Read the key from a process environment variable.</summary>
    EnvVar = 1,
    /// <summary>Use a plaintext key stored in data.json. Insecure — UI must warn.</summary>
    Stored = 2,
    /// <summary>
    /// Store the key in the OS credential store (Windows Credential Manager). Nothing
    /// sensitive reaches <c>data.json</c>. Default mode on platforms that support it; the
    /// UI hides this option where <c>ISecureKeyStore.IsSupported</c> is false.
    /// </summary>
    CredentialStore = 3,
}

/// <summary>
/// BYOK endpoint: a reusable connection configuration (URL, provider type, auth) shared by one
/// or more <see cref="ByokModel"/> entries. The <see cref="Id"/> is the stable identity that
/// persists across renames — do not regenerate it on edit. Implements
/// <see cref="INotifyPropertyChanged"/> so the Settings UI can react to field edits live
/// (e.g. showing/hiding the API-key field when <see cref="ApiKeyMode"/> changes).
/// </summary>
public sealed class ByokEndpoint : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "";
    private string _providerType = "";
    private string _baseUrl = "";
    private string _wireApi = "";
    private string? _azureApiVersion;
    private bool _isEnabled = true;
    private ByokApiKeyMode _apiKeyMode = ByokApiKeyMode.CredentialStore;
    private string? _apiKeyEnvVar;
    private string? _apiKey;
    private Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

    public string Id
    {
        get => _id;
        set { if (_id != value) { _id = value; OnPropertyChanged(nameof(Id)); } }
    }

    public string Name
    {
        get => _name;
        set { var v = value ?? ""; if (_name != v) { _name = v; OnPropertyChanged(nameof(Name)); } }
    }

    /// <summary>"openai" | "azure" | "anthropic" — normalized to lower-case.</summary>
    public string ProviderType
    {
        get => _providerType;
        set { var v = value ?? ""; if (_providerType != v) { _providerType = v; OnPropertyChanged(nameof(ProviderType)); } }
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set { var v = value ?? ""; if (_baseUrl != v) { _baseUrl = v; OnPropertyChanged(nameof(BaseUrl)); } }
    }

    /// <summary>"completions" | "responses" — request format for openai/azure providers. Anthropic omits it.</summary>
    public string WireApi
    {
        get => _wireApi;
        set { var v = value ?? ""; if (_wireApi != v) { _wireApi = v; OnPropertyChanged(nameof(WireApi)); } }
    }

    /// <summary>Required when <see cref="ProviderType"/> == "azure".</summary>
    public string? AzureApiVersion
    {
        get => _azureApiVersion;
        set { if (_azureApiVersion != value) { _azureApiVersion = value; OnPropertyChanged(nameof(AzureApiVersion)); } }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (_isEnabled != value) { _isEnabled = value; OnPropertyChanged(nameof(IsEnabled)); } }
    }

    public ByokApiKeyMode ApiKeyMode
    {
        get => _apiKeyMode;
        set { if (_apiKeyMode != value) { _apiKeyMode = value; OnPropertyChanged(nameof(ApiKeyMode)); } }
    }

    public string? ApiKeyEnvVar
    {
        get => _apiKeyEnvVar;
        set { if (_apiKeyEnvVar != value) { _apiKeyEnvVar = value; OnPropertyChanged(nameof(ApiKeyEnvVar)); } }
    }

    /// <summary>
    /// Stored API key. Plaintext in data.json — INSECURE. Only used when
    /// <see cref="ApiKeyMode"/> == <see cref="ByokApiKeyMode.Stored"/>.
    /// UI must warn the user. Plan DPAPI wrapping for v2.
    /// </summary>
    public string? ApiKey
    {
        get => _apiKey;
        set { if (_apiKey != value) { _apiKey = value; OnPropertyChanged(nameof(ApiKey)); } }
    }

    /// <summary>
    /// Custom HTTP headers to include in every request to this endpoint's API. Use this for
    /// provider-specific extras like <c>api-key</c> on Azure AI Foundry, custom auth schemes,
    /// or tenant routing headers. The <see cref="ApiKey"/> (when set) is sent separately as
    /// <c>Authorization: Bearer</c> by the OpenAI-compatible client — Lumi no longer auto-injects
    /// any provider-specific auth headers.
    /// <para>Header NAMES are case-insensitive (RFC 7230 §3.2); values are sent verbatim.</para>
    /// </summary>
    public Dictionary<string, string> Headers
    {
        get => _headers;
        set
        {
            _headers = value ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Normalize any pre-existing case-insensitive dictionary to a case-insensitive one so
            // edits and JSON round-trips stay consistent.
            if (!_headers.Comparer.Equals(StringComparer.OrdinalIgnoreCase))
            {
                _headers = new Dictionary<string, string>(_headers, StringComparer.OrdinalIgnoreCase);
            }
            OnPropertyChanged(nameof(Headers));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// BYOK model entry: a user-selectable model attached to a <see cref="ByokEndpoint"/> via
/// <see cref="EndpointId"/>. The <see cref="Id"/> is the stable identity used in the
/// <c>byok:{Id}</c> picker token — do not regenerate it on edit/rename. Implements
/// <see cref="INotifyPropertyChanged"/> for live UI binding.
/// </summary>
public sealed class ByokModel : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _endpointId = "";
    private string _modelId = "";
    private string _displayName = "";
    private bool _isEnabled = true;
    private int? _maxOutputTokens;
    private int? _maxPromptTokens;
    private int? _maxRequestsPerMinute;

    public string Id
    {
        get => _id;
        set { if (_id != value) { _id = value; OnPropertyChanged(nameof(Id)); } }
    }

    public string EndpointId
    {
        get => _endpointId;
        set { var v = value ?? ""; if (_endpointId != v) { _endpointId = v; OnPropertyChanged(nameof(EndpointId)); } }
    }

    /// <summary>Wire model id sent to the provider API (e.g. "gpt-4o", deployment name).</summary>
    public string ModelId
    {
        get => _modelId;
        set { var v = value ?? ""; if (_modelId != v) { _modelId = v; OnPropertyChanged(nameof(ModelId)); } }
    }

    /// <summary>Human-readable name shown in the picker. Independent of <see cref="ModelId"/>.</summary>
    public string DisplayName
    {
        get => _displayName;
        set { var v = value ?? ""; if (_displayName != v) { _displayName = v; OnPropertyChanged(nameof(DisplayName)); } }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (_isEnabled != value) { _isEnabled = value; OnPropertyChanged(nameof(IsEnabled)); } }
    }

    /// <summary>
    /// Maximum number of output (completion) tokens the provider may generate per turn.
    /// <c>null</c> (the default) means "inherit the provider/SDK default" — Lumi does not
    /// set <c>ProviderConfig.MaxOutputTokens</c> at all, so existing behavior is preserved
    /// for users who never configure this field. Verified to reach the provider via
    /// <c>ProviderConfig.MaxOutputTokens</c> (GitHub.Copilot.SDK 1.0.1).
    /// </summary>
    public int? MaxOutputTokens
    {
        get => _maxOutputTokens;
        set { if (!Nullable.Equals(_maxOutputTokens, value)) { _maxOutputTokens = value; OnPropertyChanged(nameof(MaxOutputTokens)); } }
    }

    /// <summary>
    /// Maximum number of input (prompt) tokens allowed for a turn — caps the context window.
    /// <c>null</c> (the default) means "inherit the provider/SDK default". Applied through
    /// <c>ProviderConfig.MaxPromptTokens</c>.
    /// </summary>
    public int? MaxPromptTokens
    {
        get => _maxPromptTokens;
        set { if (!Nullable.Equals(_maxPromptTokens, value)) { _maxPromptTokens = value; OnPropertyChanged(nameof(MaxPromptTokens)); } }
    }

    /// <summary>
    /// Optional client-side requests-per-minute throttle for this model. <c>null</c> or
    /// <c>0</c> means "no limit" — the rate limiter is a pure passthrough and
    /// <c>session.SendAsync</c> is not wrapped/throttled at all (identical to current
    /// behavior). A positive value activates a sliding-window limiter keyed by
    /// <see cref="Id"/>. This is a per-model local guard, not a shared quota: if several
    /// BYOK models share one endpoint+key, each tracks its own budget.
    /// </summary>
    public int? MaxRequestsPerMinute
    {
        get => _maxRequestsPerMinute;
        set { if (!Nullable.Equals(_maxRequestsPerMinute, value)) { _maxRequestsPerMinute = value; OnPropertyChanged(nameof(MaxRequestsPerMinute)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
    private bool _isPinned;
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
    /// <summary>
    /// Number of messages last persisted for this chat. Maintained on save/load so that
    /// "does this chat have content" checks keep working even when <see cref="Messages"/>
    /// has been unloaded from memory to reclaim RAM for inactive chats.
    /// </summary>
    public int MessageCount { get; set; }

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

    /// <summary>
    /// Provider routing signature (<see cref="Lumi.Services.ByokConfigHelper.BuildProviderSignature"/>
    /// of the BYOK endpoint this chat's Copilot session was created/resumed with. Null means the
    /// session was created on GitHub's default backend (non-BYOK). Persists across restarts so
    /// <c>EnsureSessionAsync</c> can detect when a chat's existing server-side session belongs to
    /// a different endpoint than the user's current BYOK selection and force a fresh session create
    /// instead of resuming the old one (which would silently keep routing to the wrong backend).
    /// </summary>
    public string? SessionProviderSignature { get; set; }

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

    /// <summary>Whether this chat should stay at the top of its project chat list.</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value) return;
            _isPinned = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned)));
        }
    }

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
    public bool HasExplicitToolSelection { get; set; }
    [JsonIgnore]
    public bool HasToolRestrictions => HasExplicitToolSelection || ToolNames.Count > 0;
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
    public string TriggerDisplay => TriggerType == BackgroundJobTriggerTypes.Script
        ? Loc.Get("Jobs_TriggerScript")
        : Loc.Get("Jobs_TriggerTime");

    [JsonIgnore]
    public string ActivationDisplay => IsEnabled
        ? Loc.Get("Jobs_StateEnabled")
        : Loc.Get("Jobs_StatePaused");

    [JsonIgnore]
    public string ActivationActionAutomationName => Loc.Get(
        IsEnabled ? "Jobs_PauseNamed" : "Jobs_EnableNamed",
        Name);

    [JsonIgnore]
    public string RunNowAutomationName => Loc.Get("Jobs_RunNowNamed", Name);

    [JsonIgnore]
    public string DeleteAutomationName => Loc.Get("Jobs_DeleteNamed", Name);

    [JsonIgnore]
    public string LifecycleDisplay
    {
        get
        {
            if (IsRunning)
            {
                return LastRunStatus switch
                {
                    BackgroundJobRunStatuses.Watching => Loc.Get("Jobs_LifecycleWatching"),
                    BackgroundJobRunStatuses.Waiting => Loc.Get("Jobs_LifecycleWaiting"),
                    _ => Loc.Get("Jobs_LifecycleRunning")
                };
            }

            return LastRunStatus switch
            {
                BackgroundJobRunStatuses.Running => Loc.Get("Jobs_LifecycleInterrupted"),
                BackgroundJobRunStatuses.Watching => Loc.Get("Jobs_LifecycleInterrupted"),
                BackgroundJobRunStatuses.Waiting => Loc.Get("Jobs_LifecycleInterrupted"),
                BackgroundJobRunStatuses.Completed => Loc.Get("Jobs_LifecycleCompleted"),
                BackgroundJobRunStatuses.Skipped => Loc.Get("Jobs_LifecycleSkipped"),
                BackgroundJobRunStatuses.Failed => Loc.Get("Jobs_LifecycleFailed"),
                _ when RunCount == 0 => Loc.Get("Jobs_LifecycleNotRun"),
                _ => Loc.Get("Jobs_LifecycleIdle")
            };
        }
    }

    [JsonIgnore]
    public string UpcomingRunDisplay
    {
        get
        {
            if (IsRunning)
                return Loc.Get("Jobs_RunInProgress");

            if (!IsEnabled)
                return Loc.Get("Jobs_AutomationPaused");

            if (NextRunAt is { } nextRunAt)
            {
                return Loc.Get(
                    "Jobs_NextRunAt",
                    nextRunAt.ToLocalTime().ToString("g", Loc.Culture));
            }

            return TriggerType == BackgroundJobTriggerTypes.Script
                ? Loc.Get("Jobs_RunWhenStarted")
                : Loc.Get("Jobs_NoUpcomingRun");
        }
    }

    [JsonIgnore]
    public string LastRunTimeDisplay => LastRunAt is { } lastRunAt
        ? Loc.Get("Jobs_LastRunAt", lastRunAt.ToLocalTime().ToString("g", Loc.Culture))
        : Loc.Get("Jobs_NoCompletedRuns");

    [JsonIgnore]
    public string StatusDisplay => LifecycleDisplay;

    [JsonIgnore]
    public bool IsLifecycleInProgress => IsRunning;

    [JsonIgnore]
    public bool IsLifecycleInterrupted => !IsRunning
        && LastRunStatus is BackgroundJobRunStatuses.Running
            or BackgroundJobRunStatuses.Watching
            or BackgroundJobRunStatuses.Waiting;

    [JsonIgnore]
    public bool IsLifecycleCompleted => !IsRunning
        && LastRunStatus == BackgroundJobRunStatuses.Completed;

    [JsonIgnore]
    public bool IsLifecycleSkipped => !IsRunning
        && LastRunStatus == BackgroundJobRunStatuses.Skipped;

    [JsonIgnore]
    public bool IsLifecycleFinished => IsLifecycleCompleted || IsLifecycleSkipped;

    [JsonIgnore]
    public bool IsLifecycleFailed => !IsRunning
        && (LastRunStatus == BackgroundJobRunStatuses.Failed || IsLifecycleInterrupted);

    [JsonIgnore]
    public bool IsLifecycleNotRun => !IsRunning
        && RunCount == 0
        && LastRunStatus == BackgroundJobRunStatuses.Idle;

    [JsonIgnore]
    public bool IsLifecycleIdle => !IsLifecycleInProgress && !IsLifecycleFinished && !IsLifecycleFailed;

    [JsonIgnore]
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value)
                return;

            _isRunning = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
            NotifyPresentationStateChanged();
        }
    }

    internal void NotifyPresentationStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TriggerDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivationDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivationActionAutomationName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunNowAutomationName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DeleteAutomationName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LifecycleDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpcomingRunDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastRunTimeDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLifecycleInProgress)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLifecycleInterrupted)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLifecycleCompleted)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLifecycleSkipped)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLifecycleFinished)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLifecycleFailed)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLifecycleNotRun)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLifecycleIdle)));
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

    // ── BYOK (Bring Your Own Key) ──
    public List<ByokEndpoint> ByokEndpoints { get; set; } = [];
    public List<ByokModel> ByokModels { get; set; } = [];

    /// <summary>
    /// When true, Lumi will only use configured BYOK providers and blocks all requests that
    /// would otherwise hit GitHub's internal Copilot endpoints (chat inference, title
    /// generation, suggestions, memory agent, etc.). This is the BYOK Only (No Bring Your Own
    /// Key) block. A non-BYOK model selected with this flag on surfaces a clear error instead
    /// of silently routing through Copilot. Use this for a hard privacy guarantee: no
    /// conversation content ever leaves your own endpoints.
    /// Defaults to <c>false</c>. Existing data.json files that don't yet serialize this field
    /// pick up the default on load (System.Text.Json uses the C# initializer when a property
    /// is absent from the JSON).
    /// </summary>
    public bool UseBYOKOnly { get; set; } = false;

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
