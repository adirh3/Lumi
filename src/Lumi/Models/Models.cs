using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;
using Lumi.Localization;

namespace Lumi.Models;

/// <summary>
/// Transient delivery status for a user message sent while a turn is running. Session-only
/// (never persisted) — it exists so the transcript can confirm whether a mid-turn message actually
/// landed in the live turn (<see cref="Steered"/>) versus a normal turn-start message.
/// <see cref="Queued"/> covers a message that could not be injected safely yet (for example, the
/// session is still starting up or a nested sub-agent owns the active trajectory) and is waiting to
/// be delivered.
/// </summary>
public enum MessageSteerState
{
    None,
    Queued,
    Steering,
    Steered,
    Failed
}

public enum SessionFailureDisposition
{
    Fatal,
    RetrySameSession,
    RebuildSession
}

public enum EmbeddedAppKind
{
    /// <summary>A connector tool's HTML interface (MCP Apps).</summary>
    McpApp,
    /// <summary>A web surface a Copilot extension serves for a canvas instance.</summary>
    Canvas,
}

/// <summary>
/// An interactive app a tool call opened, kept on that tool's message so the transcript and the
/// Workspace can reopen it. Only what identifies the app and what the tool exchanged is saved; its
/// interface is fetched again from the chat's live session when it opens.
/// </summary>
public sealed class EmbeddedApp
{
    public EmbeddedAppKind Kind { get; set; }
    public string Title { get; set; } = "";
    /// <summary>The connector or extension providing the app, as the user knows it.</summary>
    public string? SourceName { get; set; }
    /// <summary>The Copilot session a canvas was last open in (resume reopens only its own).</summary>
    public string? SessionId { get; set; }

    public string? ServerName { get; set; }
    public string? ResourceUri { get; set; }
    /// <summary>The tool's arguments (an MCP App) or the canvas's open input.</summary>
    public string? InputJson { get; set; }
    public string? ResultJson { get; set; }
    /// <summary>What the app asked to add to the chat's next turn (<c>ui/update-model-context</c>).</summary>
    public string? ModelContextJson { get; set; }

    public string? ExtensionId { get; set; }
    public string? CanvasId { get; set; }
    public string? InstanceId { get; set; }
    public bool IsClosed { get; set; }

    [JsonIgnore] public string? Html { get; set; }
    [JsonIgnore] public string? MetadataJson { get; set; }
    /// <summary>The canvas's current address, valid only while its extension process runs.</summary>
    [JsonIgnore] public string? Url { get; set; }

    public EmbeddedApp Clone() => (EmbeddedApp)MemberwiseClone();
}

public class ChatMessage
{
    private Guid _id = Guid.NewGuid();
    public Guid Id { get => _id; set => SetPersisted(ref _id, value); }
    private string _role = "user";
    public string Role { get => _role; set => SetPersisted(ref _role, value); } // user, assistant, system, tool, reasoning, error
    private string _content = "";
    public string Content { get => _content; set => SetPersisted(ref _content, value); }
    private string? _author;
    public string? Author { get => _author; set => SetPersisted(ref _author, value); }
    private string? _remoteRequestId;
    /// <summary>Authenticated mobile request that created this user message, when applicable.</summary>
    public string? RemoteRequestId { get => _remoteRequestId; set => SetPersisted(ref _remoteRequestId, value); }
    private DateTimeOffset _timestamp = DateTimeOffset.Now;
    public DateTimeOffset Timestamp { get => _timestamp; set => SetPersisted(ref _timestamp, value); }
    private string? _toolName;
    public string? ToolName { get => _toolName; set => SetPersisted(ref _toolName, value); }
    private string? _toolSkillName;
    /// <summary>Display name for a native skill call; the original runtime arguments remain in Content.</summary>
    public string? ToolSkillName { get => _toolSkillName; set => SetPersisted(ref _toolSkillName, value); }
    private string? _toolCallId;
    public string? ToolCallId { get => _toolCallId; set => SetPersisted(ref _toolCallId, value); }
    private string? _parentToolCallId;
    public string? ParentToolCallId { get => _parentToolCallId; set => SetPersisted(ref _parentToolCallId, value); }
    private string? _toolStatus;
    public string? ToolStatus { get => _toolStatus; set => SetPersisted(ref _toolStatus, value); } // InProgress, Completed, Failed, Stopped
    private string? _toolOutput;
    public string? ToolOutput { get => _toolOutput; set => SetPersisted(ref _toolOutput, value); }
    private EmbeddedApp? _embeddedApp;
    /// <summary>The app this tool opened. Changing it in place takes <see cref="MarkChanged"/> to be saved.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmbeddedApp? EmbeddedApp { get => _embeddedApp; set => SetPersisted(ref _embeddedApp, value); }

    private DateTimeOffset? _toolStartedAt;
    /// <summary>
    /// Wall-clock instant this tool call started, stamped from the SDK's tool-start event. Persisted
    /// so elapsed time is a property of the command itself rather than of the UI session that
    /// happened to observe it — switching or reopening a chat no longer restarts the clock.
    /// </summary>
    public DateTimeOffset? ToolStartedAt { get => _toolStartedAt; set => SetPersisted(ref _toolStartedAt, value); }

    private double? _toolDurationMs;
    /// <summary>
    /// Final duration of this tool call in milliseconds, frozen when the call reaches a terminal
    /// status. Null while it is still running, or when its start was never observed.
    /// </summary>
    public double? ToolDurationMs { get => _toolDurationMs; set => SetPersisted(ref _toolDurationMs, value); }

    private string? _planSnapshot;
    /// <summary>
    /// The agent's plan right after this <c>sql</c> call changed it, read back from the session's todos
    /// table (<c>session.todos_changed</c>), as a <c>todoList</c> JSON array of titles and statuses. Null
    /// for every other message; a call that carries it shows as a plan update instead of a query.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlanSnapshot { get => _planSnapshot; set => SetPersisted(ref _planSnapshot, value); }
    private Guid? _linkedChatId;
    public Guid? LinkedChatId { get => _linkedChatId; set => SetPersisted(ref _linkedChatId, value); }
    private string? _linkedChatTitle;
    public string? LinkedChatTitle { get => _linkedChatTitle; set => SetPersisted(ref _linkedChatTitle, value); }
    private string? _questionId;
    public string? QuestionId { get => _questionId; set => SetPersisted(ref _questionId, value); }
    private string? _questionText;
    public string? QuestionText { get => _questionText; set => SetPersisted(ref _questionText, value); }
    private string? _questionOptions;
    public string? QuestionOptions { get => _questionOptions; set => SetPersisted(ref _questionOptions, value); }
    private bool? _questionAllowFreeText;
    public bool? QuestionAllowFreeText { get => _questionAllowFreeText; set => SetPersisted(ref _questionAllowFreeText, value); }
    private bool? _questionAllowMultiSelect;
    public bool? QuestionAllowMultiSelect { get => _questionAllowMultiSelect; set => SetPersisted(ref _questionAllowMultiSelect, value); }
    private Dictionary<string, string>? _interactionStates;
    /// <summary>User-owned UI state keyed by a response block's stable id.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? InteractionStates { get => _interactionStates; set => SetPersisted(ref _interactionStates, value); }
    private string? _inputKind;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InputKind { get => _inputKind; set => SetPersisted(ref _inputKind, value); }
    private string? _inputDefinition;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InputDefinition { get => _inputDefinition; set => SetPersisted(ref _inputDefinition, value); }
    private string? _inputAnswer;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InputAnswer { get => _inputAnswer; set => SetPersisted(ref _inputAnswer, value); }
    private bool _inputCanceled;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool InputCanceled { get => _inputCanceled; set => SetPersisted(ref _inputCanceled, value); }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InteractionReceipt? InteractionReceipt
    {
        get => _interactionReceipt;
        set
        {
            if (ReferenceEquals(_interactionReceipt, value))
                return;
            if (_interactionReceipt is not null)
                _interactionReceipt.PropertyChanged -= OnInteractionReceiptChanged;
            _interactionReceipt = value;
            if (value is not null)
                value.PropertyChanged += OnInteractionReceiptChanged;
            MarkChanged();
        }
    }

    private InteractionReceipt? _interactionReceipt;

    // A receipt's status lives on its own object, so its changes are changes to this message too.
    private void OnInteractionReceiptChanged(object? sender, PropertyChangedEventArgs e) => MarkChanged();
    private bool _isStreaming;
    public bool IsStreaming { get => _isStreaming; set => SetPersisted(ref _isStreaming, value); }

    private bool? _isProgress;
    /// <summary>
    /// Whether this assistant message was a progress update written while working (true) or part of
    /// the answer (false), decided from the SDK's evidence when the message arrived. Null when there was
    /// no such evidence — history recorded before Lumi kept this, or a message cut off before it
    /// finished — whose role is then inferred from what followed it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsProgress { get => _isProgress; set => SetPersisted(ref _isProgress, value); }

    /// <summary>
    /// Session-only id of the model call that produced this assistant message. A reply split around
    /// reasoning arrives as several messages sharing it, which are classified together.
    /// </summary>
    [JsonIgnore]
    internal string? ApiCallId { get; set; }

    private string? _model;
    public string? Model { get => _model; set => SetPersisted(ref _model, value); }
    private string? _reasoningEffort;
    public string? ReasoningEffort { get => _reasoningEffort; set => SetPersisted(ref _reasoningEffort, value); }
    private string? _contextWindowTier;
    public string? ContextWindowTier { get => _contextWindowTier; set => SetPersisted(ref _contextWindowTier, value); }
    private Guid? _agentId;
    public Guid? AgentId { get => _agentId; set => SetPersisted(ref _agentId, value); }
    private string? _sdkAgentName;
    public string? SdkAgentName { get => _sdkAgentName; set => SetPersisted(ref _sdkAgentName, value); }
    private bool _hasAgentSelection;
    public bool HasAgentSelection { get => _hasAgentSelection; set => SetPersisted(ref _hasAgentSelection, value); }
    private List<string> _activeMcpServerNames = [];
    public List<string> ActiveMcpServerNames { get => _activeMcpServerNames; set => SetPersisted(ref _activeMcpServerNames, value); }
    private bool _hasMcpSelection;
    public bool HasMcpSelection { get => _hasMcpSelection; set => SetPersisted(ref _hasMcpSelection, value); }
    private List<string> _attachments = [];
    public List<string> Attachments { get => _attachments; set => SetPersisted(ref _attachments, value); }
    private List<SearchSource> _sources = [];
    public List<SearchSource> Sources { get => _sources; set => SetPersisted(ref _sources, value); }
    private List<SkillReference> _activeSkills = [];
    public List<SkillReference> ActiveSkills { get => _activeSkills; set => SetPersisted(ref _activeSkills, value); }
    private MessageReply? _replyTo;
    /// <summary>
    /// The earlier assistant message (or the excerpt of it) this user message replies to, or null for
    /// an ordinary message. The quote travels with the message so the model receives it on every
    /// send, resend, and transcript replay.
    /// </summary>
    public MessageReply? ReplyTo { get => _replyTo; set => SetPersisted(ref _replyTo, value); }
    private List<ChatReference>? _referencedChats;
    /// <summary>
    /// Other chats the user brought into this message, or null for none. Their transcripts are read
    /// into the prompt when the message is sent (and on resend), so the model sees what they hold.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ChatReference>? ReferencedChats { get => _referencedChats; set => SetPersisted(ref _referencedChats, value); }
    private SessionFailureDisposition? _failureDisposition;
    /// <summary>
    /// Recovery decision captured from a structured session error. Persisted so reopening a chat
    /// does not have to infer behavior from localized display text.
    /// </summary>
    public SessionFailureDisposition? FailureDisposition { get => _failureDisposition; set => SetPersisted(ref _failureDisposition, value); }

    /// <summary>A local send held by Pause, restored to the FIFO when this chat is loaded.</summary>
    public bool IsPendingPausedSend { get => _isPendingPausedSend; set => SetPersisted(ref _isPendingPausedSend, value); }
    private bool _isPendingPausedSend;

    /// <summary>
    /// The message's position key in the chat's stored transcript (1-based, ordered), or 0 while it
    /// has never been stored. Assigned by the chat store; a copy made with <see cref="Clone"/> is new.
    /// </summary>
    [JsonIgnore]
    internal long StorageSeq { get; set; }

    /// <summary>Increases whenever a persisted field changes, so saves can write only changed messages.</summary>
    [JsonIgnore]
    internal long Revision { get; private set; }

    /// <summary>The <see cref="Revision"/> last written to storage; -1 forces the next save to write it.</summary>
    [JsonIgnore]
    internal long PersistedRevision { get; set; } = -1;

    /// <summary>True when the message holds changes storage has not received yet.</summary>
    [JsonIgnore]
    internal bool HasUnpersistedChanges => Revision != PersistedRevision;

    /// <summary>
    /// Records a change made inside one of the message's collections or nested objects, which the
    /// property setters cannot see (for example adding a source to <see cref="Sources"/>).
    /// </summary>
    public void MarkChanged() => Revision++;

    private void SetPersisted<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        Revision++;
    }

    private MessageSteerState _steerDelivery;

    /// <summary>Session-only steer delivery status (not serialized). Set when this message is steered
    /// into a running turn so the badge survives transcript/VM rebuilds within the session.</summary>
    [JsonIgnore]
    public MessageSteerState SteerDelivery
    {
        get => _steerDelivery;
        set
        {
            _steerDelivery = value;
            if (value != MessageSteerState.Queued)
                IsPendingPausedSend = false;
        }
    }

    /// <summary>
    /// Session-only availability for requesting immediate delivery of a locally queued message.
    /// A setup-time request waits for the first SDK turn to start before aborting it, so session/MCP
    /// setup is preserved.
    /// </summary>
    [JsonIgnore]
    public bool CanSendNowWhenQueued { get; set; }

    /// <summary>An explicit external send selection belongs to its queued message, not later composer edits.</summary>
    [JsonIgnore]
    internal bool HasExternalModelSelection { get; set; }

    /// <summary>
    /// Deep-copies this message, including its mutable collections, so the copy can be mutated or
    /// serialized without touching the original. Callers that need a *distinct* message (rather
    /// than a snapshot of this one) must overwrite <see cref="Id"/> afterwards.
    /// </summary>
    /// <remarks>
    /// Kept as the single copy site for <see cref="ChatMessage"/>: persistence snapshots and chat
    /// forking both use it, so a newly added field can't be silently dropped by one of them.
    /// </remarks>
    public ChatMessage Clone() => new()
    {
        Id = Id,
        Role = Role,
        Content = Content,
        Author = Author,
        RemoteRequestId = RemoteRequestId,
        Timestamp = Timestamp,
        ToolName = ToolName,
        ToolSkillName = ToolSkillName,
        ToolCallId = ToolCallId,
        ParentToolCallId = ParentToolCallId,
        ToolStatus = ToolStatus,
        ToolOutput = ToolOutput,
        EmbeddedApp = EmbeddedApp?.Clone(),
        ToolStartedAt = ToolStartedAt,
        ToolDurationMs = ToolDurationMs,
        PlanSnapshot = PlanSnapshot,
        LinkedChatId = LinkedChatId,
        LinkedChatTitle = LinkedChatTitle,
        QuestionId = QuestionId,
        QuestionText = QuestionText,
        QuestionOptions = QuestionOptions,
        QuestionAllowFreeText = QuestionAllowFreeText,
        QuestionAllowMultiSelect = QuestionAllowMultiSelect,
        InteractionStates = InteractionStates is null ? null : new Dictionary<string, string>(InteractionStates, StringComparer.Ordinal),
        InputKind = InputKind,
        InputDefinition = InputDefinition,
        InputAnswer = InputAnswer,
        InputCanceled = InputCanceled,
        InteractionReceipt = InteractionReceipt?.Clone(),
        IsStreaming = IsStreaming,
        IsProgress = IsProgress,
        ApiCallId = ApiCallId,
        Model = Model,
        ReasoningEffort = ReasoningEffort,
        ContextWindowTier = ContextWindowTier,
        HasExternalModelSelection = HasExternalModelSelection,
        AgentId = AgentId,
        SdkAgentName = SdkAgentName,
        HasAgentSelection = HasAgentSelection,
        ActiveMcpServerNames = [..ActiveMcpServerNames],
        HasMcpSelection = HasMcpSelection,
        Attachments = [..Attachments],
        FailureDisposition = FailureDisposition,
        IsPendingPausedSend = IsPendingPausedSend,
        ReplyTo = ReplyTo?.Clone(),
        ReferencedChats = ReferencedChats?.Select(static reference => reference.Clone()).ToList(),
        ActiveSkills = [..ActiveSkills.Select(static s => new SkillReference
        {
            Name = s.Name,
            Glyph = s.Glyph,
            Description = s.Description,
            Content = s.Content
        })],
        Sources = [..Sources.Select(static s => new SearchSource
        {
            Title = s.Title,
            Snippet = s.Snippet,
            Url = s.Url
        })]
    };

    /// <summary>Stamps the tool call's start instant. Idempotent: a replayed or duplicated start
    /// event keeps the original stamp so the measured duration stays honest.</summary>
    public void MarkToolStarted(DateTimeOffset startedAt) => ToolStartedAt ??= startedAt;

    /// <summary>Freezes the tool call's final duration. No-ops when the duration is already frozen
    /// (duplicate/out-of-order terminal events) or when the start was never observed, so a call
    /// reports no time rather than a fabricated one. Returns true when a duration was recorded.</summary>
    public bool MarkToolFinished(DateTimeOffset finishedAt)
    {
        if (ToolDurationMs is not null || ToolStartedAt is not { } startedAt)
            return false;

        ToolDurationMs = Math.Max(0, (finishedAt - startedAt).TotalMilliseconds);
        return true;
    }
}

/// <summary>
/// Links a user message to the earlier assistant message it replies to. <see cref="Quote"/> is the
/// excerpt the user selected (<see cref="IsSelection"/>), or the opening of the whole message when
/// the reply targets the message itself.
/// </summary>
public sealed class MessageReply
{
    /// <summary>Id of the assistant message being replied to.</summary>
    public Guid MessageId { get; set; }

    /// <summary>The quoted text shown with the reply and sent to the model.</summary>
    public string Quote { get; set; } = "";

    /// <summary>True when <see cref="Quote"/> is a user-selected excerpt rather than the whole message.</summary>
    public bool IsSelection { get; set; }

    /// <summary>Display name of the replied-to message's author (e.g. "Lumi" or an agent name).</summary>
    public string? Author { get; set; }

    public MessageReply Clone() => new()
    {
        MessageId = MessageId,
        Quote = Quote,
        IsSelection = IsSelection,
        Author = Author
    };
}

/// <summary>
/// Another chat brought into a message as context. <see cref="Title"/> is the title when it was
/// referenced, so the message still reads sensibly after the chat is renamed or deleted.
/// </summary>
public sealed class ChatReference
{
    public Guid ChatId { get; set; }
    public string Title { get; set; } = "";

    public ChatReference Clone() => new() { ChatId = ChatId, Title = Title };
}

/// <summary>The immutable selection submitted by a user action, with independent reply lifecycle state.</summary>
public sealed class InteractionReceipt : INotifyPropertyChanged
{
    public Guid SourceMessageId { get; set; }
    public string BlockId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Action { get; init; } = "";
    private string _status = "Submitted";
    private string? _error;

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (_error == value) return;
            _error = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Error)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public InteractionReceipt Clone() => new()
    {
        SourceMessageId = SourceMessageId, BlockId = BlockId,
        Title = Title, Summary = Summary, Detail = Detail,
        Action = Action, Status = Status, Error = Error
    };
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
    /// Store the key in the OS credential store (Credential Manager, Keychain, or Secret Service). Nothing
    /// sensitive reaches <c>data.json</c>. Default mode on platforms that support it; the
    /// UI hides this option where <c>ISecureKeyStore.IsSupported</c> is false.
    /// </summary>
    CredentialStore = 3,
}

/// <summary>
/// The account an OAuth-connected endpoint signed in with. Identifies the account and the client
/// it authorized; never holds a token (those stay in the OS credential store).
/// </summary>
public sealed class ByokAccount
{
    /// <summary>The OAuth client issued to Lumi for this account, reused when signing in again.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>The account's stable identifier from its validated ID token.</summary>
    public string Subject { get; set; } = "";

    public string? Email { get; set; }

    public ByokAccount Clone() => new() { ClientId = ClientId, Subject = Subject, Email = Email };
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

    public string? ConnectionProviderId { get; set; }
    public DateTimeOffset? CredentialExpiresAt { get; set; }

    /// <summary>The account an OAuth sign-in connected (ChatGPT). Null for key-based endpoints.</summary>
    public ByokAccount? Account { get; set; }

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
    private bool _supportsReasoningEffort;
    private List<string> _supportedReasoningEfforts = [];
    private string? _defaultReasoningEffort;
    private int? _defaultContextWindowTokens;
    private int? _longContextWindowTokens;
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

    public bool SupportsReasoningEffort
    {
        get => _supportsReasoningEffort;
        set { if (_supportsReasoningEffort != value) { _supportsReasoningEffort = value; OnPropertyChanged(nameof(SupportsReasoningEffort)); } }
    }

    public List<string> SupportedReasoningEfforts
    {
        get => _supportedReasoningEfforts;
        set { _supportedReasoningEfforts = value ?? []; OnPropertyChanged(nameof(SupportedReasoningEfforts)); }
    }

    public string? DefaultReasoningEffort
    {
        get => _defaultReasoningEffort;
        set { if (_defaultReasoningEffort != value) { _defaultReasoningEffort = value; OnPropertyChanged(nameof(DefaultReasoningEffort)); } }
    }

    /// <summary>Total context-window capacity for the Default tier, not the prompt/compaction cap.</summary>
    public int? DefaultContextWindowTokens
    {
        get => _defaultContextWindowTokens;
        set { if (!Nullable.Equals(_defaultContextWindowTokens, value)) { _defaultContextWindowTokens = value; OnPropertyChanged(nameof(DefaultContextWindowTokens)); } }
    }

    /// <summary>Total context-window capacity for the Long tier, not the prompt/compaction cap.</summary>
    public int? LongContextWindowTokens
    {
        get => _longContextWindowTokens;
        set { if (!Nullable.Equals(_longContextWindowTokens, value)) { _longContextWindowTokens = value; OnPropertyChanged(nameof(LongContextWindowTokens)); } }
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
    /// Maximum number of input (prompt) tokens used as the prompt/input and compaction cap,
    /// distinct from the total context capacity in <c>ModelCapabilities.Limits.MaxContextWindowTokens</c>.
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

public class ChatTag : INotifyPropertyChanged
{
    public const string DefaultColor = "#6E8BFF";

    private string _name = "";
    private string _color = DefaultColor;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name
    {
        get => _name;
        set
        {
            var name = value ?? "";
            if (_name == name) return;
            _name = name;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }
    public string Color
    {
        get => _color;
        set
        {
            var color = value ?? DefaultColor;
            if (_color == color) return;
            _color = color;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Color)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public enum ResponseDetailLevel
{
    ModelDefault = 0,
    Concise = 1,
    Balanced = 2,
    Detailed = 3
}

/// <summary>How much a chat may do on its own. Each value is a native Copilot permission mode.</summary>
public enum ApprovalMode
{
    /// <summary>Native allow-all: acts without asking. Lumi's default and its behavior before approvals existed.</summary>
    FullAccess = 0,
    /// <summary>Native manual mode: asks before commands, edits, connectors and Lumi's actions.</summary>
    AskFirst = 1,
    /// <summary>Native assisted mode: Copilot's reviewer approves routine requests; the rest ask.</summary>
    AutoReview = 2
}

public enum ApprovalRuleAction
{
    Allow = 0,
    Ask = 1,
    Never = 2
}

/// <summary>What a rule covers. Each kind maps onto a native Copilot permission rule family.</summary>
public enum ApprovalRuleKind
{
    Command = 0,
    EditFiles = 1,
    ReadFiles = 2,
    Connector = 3,
    LumiTool = 4
}

/// <summary>What a stricter chat does with an approval Lumi needs while nobody is there to give it.</summary>
public enum AwayApprovalBehavior
{
    Wait = 0,
    Skip = 1
}

public sealed class ApprovalRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ApprovalRuleAction Action { get; set; }
    public ApprovalRuleKind Kind { get; set; }

    /// <summary>
    /// The command a request starts with, a file path, a connector ("server" or "server/tool"), or a
    /// Lumi tool or tool group. Empty covers every request of the kind.
    /// </summary>
    public string? Target { get; set; }

    /// <summary>True when an approval card remembered it, rather than the user adding it as a rule.</summary>
    public bool Remembered { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public class Chat : INotifyPropertyChanged
{
    private string _title = Loc.Sidebar_NewChat;
    private Guid? _tagId;
    private ChatTag? _tag;
    private bool _isRunning;
    private bool _isSessionActive;
    private bool _isAwaitingInput;
    private bool _isAwaitingApproval;
    private bool _isPaused;
    private bool _isPausePending;
    private bool _hasUnreadMessages;
    private bool _isPinned;
    private bool _showProjectBadge;
    private string? _projectBadgeText;
    private string? _projectBadgeColor;
    private List<string> _activeExternalSkillNames = [];
    private List<string> _followUpSuggestions = [];
    private List<ApprovalRule> _approvalRules = [];

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
    public Guid? TagId
    {
        get => _tagId;
        set
        {
            if (_tagId == value) return;
            _tagId = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagId)));
        }
    }
    [JsonIgnore]
    public ChatTag? Tag
    {
        get => _tag;
        set
        {
            if (ReferenceEquals(_tag, value)) return;
            _tag = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tag)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTag)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagColor)));
        }
    }
    [JsonIgnore]
    public bool HasTag => Tag is not null;
    [JsonIgnore]
    public string TagName => Tag?.Name ?? "";
    [JsonIgnore]
    public string TagColor => Tag?.Color ?? ChatTag.DefaultColor;

    internal void NotifyTagDetailsChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagColor)));
    }

    public string? CopilotSessionId { get; set; }

    /// <summary>The chat's own approval mode; null follows its project, then the user's default.</summary>
    public ApprovalMode? ApprovalMode { get; set; }

    /// <summary>Approvals remembered for this chat only.</summary>
    public List<ApprovalRule> ApprovalRules { get => _approvalRules; set => _approvalRules = value ?? []; }
    public bool PauseNeedsContinuation { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    /// <summary>
    /// Number of messages last persisted for this chat. Maintained on save/load so that
    /// "does this chat have content" checks keep working even when <see cref="Messages"/>
    /// has been unloaded from memory to reclaim RAM for inactive chats.
    /// </summary>
    public int MessageCount { get; set; }
    /// <summary>Bounded last user/assistant text persisted for list surfaces without loading history.</summary>
    public string? Preview { get; set; }

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
    /// <summary>Last accepted mobile send receipt, persisted for idempotency across desktop restarts.</summary>
    public string? LastRemoteDeviceId { get; set; }
    public string? LastRemoteRequestId { get; set; }

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

    /// <summary>Whether <see cref="ContextCurrentTokens"/> came from an authoritative session context snapshot.</summary>
    public bool HasExactContextUsage { get; set; }

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

    /// <summary>
    /// The chat this one was copied from, via "Duplicate chat" or a message-level fork. Drives the
    /// breadcrumb chip at the top of the new chat's transcript.
    /// </summary>
    public Guid? ForkedFromChatId { get; set; }

    /// <summary>
    /// Title of <see cref="ForkedFromChatId"/> captured at fork time, so the breadcrumb still
    /// reads sensibly after the parent chat is renamed or deleted.
    /// </summary>
    public string? ForkedFromTitle { get; set; }

    /// <summary>
    /// True when this chat branched from a specific message ("fork"), false when it is a whole-chat
    /// copy ("duplicate"). Only the breadcrumb wording depends on it — the two are otherwise
    /// identical — so chats saved before this existed simply read as duplicates.
    /// </summary>
    public bool ForkedFromMessage { get; set; }

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
        set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasBackgroundActivity)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowRunningIndicator)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanPause)));
        }
    }

    /// <summary>Runtime-only session activity, including work that outlives the assistant's reply.</summary>
    [JsonIgnore]
    public bool IsSessionActive
    {
        get => _isSessionActive;
        set
        {
            if (_isSessionActive == value) return;
            _isSessionActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSessionActive)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasBackgroundActivity)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanPause)));
        }
    }

    [JsonIgnore]
    public bool HasBackgroundActivity => IsSessionActive && !IsRunning && !IsPaused;

    /// <summary>User intent to hold the next safe session step, retained across app restarts.</summary>
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (_isPaused == value) return;
            _isPaused = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPaused)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasBackgroundActivity)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowRunningIndicator)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanPause)));
        }
    }

    [JsonIgnore]
    public bool IsPausePending
    {
        get => _isPausePending;
        set
        {
            if (_isPausePending == value) return;
            _isPausePending = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPausePending)));
        }
    }

    [JsonIgnore]
    public bool ShowRunningIndicator => IsRunning && !IsPaused;

    [JsonIgnore]
    public bool CanPause => (IsRunning || IsSessionActive) && !IsPaused;

    /// <summary>Runtime-only: the assistant is blocked on a question it asked the user.</summary>
    [JsonIgnore]
    public bool IsAwaitingInput
    {
        get => _isAwaitingInput;
        set
        {
            if (_isAwaitingInput == value) return;
            _isAwaitingInput = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAwaitingInput)));
        }
    }

    /// <summary>Runtime-only: an action waits for the user's approval.</summary>
    [JsonIgnore]
    public bool IsAwaitingApproval
    {
        get => _isAwaitingApproval;
        set
        {
            if (_isAwaitingApproval == value) return;
            _isAwaitingApproval = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAwaitingApproval)));
        }
    }

    /// <summary>Runtime-only flag for an unseen reply or a chat manually marked as unread.</summary>
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

    /// <summary>Runtime-only: the project's identity hue (#AARRGGBB) that tints the sidebar folder badge.</summary>
    [JsonIgnore]
    public string? ProjectBadgeColor
    {
        get => _projectBadgeColor;
        set { if (_projectBadgeColor == value) return; _projectBadgeColor = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProjectBadgeColor))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class Project : INotifyPropertyChanged
{
    private bool _isRunning;
    private int _chatCount;
    private List<string> _additionalContextDirectories = [];
    private List<ApprovalRule> _approvalRules = [];

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Instructions { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public bool AutoSyncMainBranchDaily { get; set; }
    public bool DefaultNewChatsUseWorktree { get; set; }
    public DateTimeOffset? LastMainBranchSyncAttemptAt { get; set; }
    public DateTimeOffset? LastMainBranchSyncAt { get; set; }
    public string? LastMainBranchSyncError { get; set; }
    public List<string> AdditionalContextDirectories
    {
        get => _additionalContextDirectories;
        set => _additionalContextDirectories = value ?? [];
    }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>The approval mode for the project's chats; null follows the user's default.</summary>
    public ApprovalMode? ApprovalMode { get; set; }

    /// <summary>Approval rules for the project's chats, added here or remembered from approval cards.</summary>
    public List<ApprovalRule> ApprovalRules { get => _approvalRules; set => _approvalRules = value ?? []; }

    /// <summary>Runtime-only flag indicating at least one chat in this project is actively generating a response.</summary>
    [JsonIgnore]
    public bool IsRunning
    {
        get => _isRunning;
        set { if (_isRunning == value) return; _isRunning = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning))); }
    }

    /// <summary>Runtime-only number of chats in this project, shown next to it in the Projects sidebar.</summary>
    [JsonIgnore]
    public int ChatCount
    {
        get => _chatCount;
        set { if (_chatCount == value) return; _chatCount = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChatCount))); }
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
    [JsonIgnore]
    public string DisplayDescription => IsBuiltIn ? Loc.GetBuiltInDescription(Name, Description) : Description;
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
    [JsonIgnore]
    public string DisplayDescription => IsBuiltIn ? Loc.GetBuiltInDescription(Name, Description) : Description;
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

    /// <summary>The MCP registry name (for example <c>io.github.owner/server</c>) this server was added from in Discover.</summary>
    public string? CatalogId { get; set; }
}

public static class BackgroundJobTriggerTypes
{
    public const string Time = "time";
    public const string Script = "script";
    public const string ChatEvent = "chat_event";
}

public static class ChatLifecycleEventTypes
{
    public const string Any = "*";
    public const string TurnStart = "turn_start";
    public const string TurnEnd = "turn_end";
    public const string Idle = "idle";
    public const string Error = "error";
    public const string Aborted = "aborted";

    public static IReadOnlyList<string> Supported { get; } =
        [TurnStart, TurnEnd, Idle, Error, Aborted, Any];

    public static bool TryNormalize(
        IEnumerable<string>? values,
        out List<string> normalized,
        out string error,
        bool defaultToIdle = false,
        bool localizeError = true)
    {
        normalized = [];
        error = "";

        foreach (var value in values ?? [])
        {
            foreach (var token in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eventType = token.ToLowerInvariant() switch
                {
                    "*" or "all" or "any" => Any,
                    "turn_start" or "turn-start" or "turn_started" or "turn-started" or "start" => TurnStart,
                    "turn_end" or "turn-end" or "turn_ended" or "turn-ended"
                        or "turn_complete" or "turn-complete" or "turn_completed" or "turn-completed" or "end" => TurnEnd,
                    "idle" or "finished" or "chat_finished" or "chat-finished"
                        or "chat_complete" or "chat-complete" or "chat_completed" or "chat-completed" => Idle,
                    "error" or "failed" or "failure" => Error,
                    "abort" or "aborted" or "stopped" => Aborted,
                    _ => null
                };

                if (eventType is null)
                {
                    error = string.Format(localizeError ? Loc.Jobs_UnsupportedChatEvent
                        : Loc.GetEnglish(nameof(Loc.Jobs_UnsupportedChatEvent)), token);
                    normalized = [];
                    return false;
                }

                if (!normalized.Contains(eventType, StringComparer.Ordinal))
                    normalized.Add(eventType);
            }
        }

        if (normalized.Count == 0 && defaultToIdle)
            normalized.Add(Idle);

        return true;
    }

    public static bool Matches(IEnumerable<string>? filters, string eventType)
        => filters?.Any(filter => filter == Any || string.Equals(filter, eventType, StringComparison.Ordinal)) == true;

    public static string Describe(IEnumerable<string>? eventTypes)
    {
        var values = eventTypes?.ToArray() ?? [];
        return values.Length == 0 ? Idle : string.Join(", ", values);
    }
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

    /// <summary>
    /// The default script language for newly created jobs on the current OS. PowerShell on Windows
    /// (unchanged), the platform shell (<see cref="Command"/> → bash/sh) on Linux/macOS, where
    /// <c>pwsh</c> is not guaranteed to be installed and must not be the implicit default.
    /// </summary>
    public static string DefaultForCurrentOs()
        => OperatingSystem.IsWindows() ? PowerShell : Command;
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
    private long _configurationVersion;

    [JsonIgnore]
    internal object SyncRoot { get; } = new();

    [JsonIgnore]
    internal long ConfigurationVersion => System.Threading.Interlocked.Read(ref _configurationVersion);

    internal void MarkConfigurationChanged()
        => System.Threading.Interlocked.Increment(ref _configurationVersion);

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
    public string ScriptLanguage { get; set; } = BackgroundJobScriptLanguages.DefaultForCurrentOs();
    public Guid? SourceChatId { get; set; }
    public List<string> ChatEventTypes { get; set; } = [];
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
    public string TriggerDisplay => TriggerType switch
    {
        BackgroundJobTriggerTypes.Script => Loc.Get("Jobs_TriggerScript"),
        BackgroundJobTriggerTypes.ChatEvent => Loc.Get("Jobs_TriggerChatEvent"),
        _ => Loc.Get("Jobs_TriggerTime")
    };

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

            return TriggerType switch
            {
                BackgroundJobTriggerTypes.Script => Loc.Get("Jobs_RunWhenStarted"),
                BackgroundJobTriggerTypes.ChatEvent => Loc.Get("Jobs_WaitingForChatEvent"),
                _ => Loc.Get("Jobs_NoUpcomingRun")
            };
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

public static class MemorySources
{
    /// <summary>Written or reviewed by the daily context curator, which checks each memory against the chats
    /// it came from, so the keyword quality gate for per-turn memories does not apply to it.</summary>
    public const string Curator = "curator";

    /// <summary>The source a memory has after the user edits it. One the curator checked stays the curator's, so the
    /// keyword gate for per-turn memories doesn't hide or archive it.</summary>
    public static string AfterUserEdit(string? source) => source == Curator ? Curator : "manual";
}

/// <summary>
/// Where things stand, in Lumi's words: one brief per project and one across all of them (<see cref="ProjectId"/>
/// null). The daily context curator keeps it current from the chats, and every new chat in its scope starts with it.
/// </summary>
public class ContextBrief
{
    public Guid? ProjectId { get; set; }
    public string Content { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>The curator has read this scope's chats up to here; its next run starts from this moment.</summary>
    public DateTimeOffset? CuratedThrough { get; set; }

    /// <summary>The user changed the text after the curator last wrote it; the curator keeps those edits in mind.</summary>
    public bool EditedByUser { get; set; }
}

/// <summary>
/// Lumi's own uses of AI, as Settings → AI Features lists them. Each runs in a helper session on the model the
/// user chose for it (<see cref="UserSettings.AiFeatureModels"/>), or on <see cref="UserSettings.BackgroundModel"/>.
/// </summary>
public static class AiFeatureIds
{
    public const string ChatTitles = "chat-titles";
    public const string FollowUps = "follow-ups";
    public const string Memories = "memories";
    public const string DailyBriefs = "daily-briefs";
    public const string SharedPrompts = "shared-prompts";
    public const string Welcome = "welcome";

    public static readonly IReadOnlyList<string> All =
        [ChatTitles, FollowUps, Memories, DailyBriefs, SharedPrompts, Welcome];
}

public class UserSettings
{
    private List<ApprovalRule> _approvalRules = [];
    private Dictionary<string, string> _aiFeatureModels = [];
    private List<string>? _personalSkillDirectories;

    // ── General ──
    public string? UserName { get; set; }
    public string? UserSex { get; set; } // "male", "female", or null (prefer not to say)
    public bool IsOnboarded { get; set; }
    /// <summary>Setup suggestions (<c>SetupScenario.Id</c>) the user set up or dismissed; they aren't suggested again.</summary>
    public List<string> HandledSetupSuggestions { get; set; } = [];
    /// <summary>Whether Lumi may check app names at startup for setup suggestions (off when welcome wasn't allowed to).</summary>
    public bool AllowAppSetupSuggestions { get; set; } = true;
    public bool DefaultsSeeded { get; set; }
    public bool CodingLumiSeeded { get; set; }
    public bool CodingLumiBrowserToolsSeeded { get; set; }
    public bool CurrentChatManagementToolSeeded { get; set; }
    public string Language { get; set; } = "en";
    public bool LaunchAtStartup { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; }
    public string GlobalHotkey { get; set; } = "";
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>
    /// Look on the clipboard for something to import when Lumi is activated (Lumi codes and packs) or
    /// the Import sheet opens, and offer a preview. Off by default on macOS, which can warn the user
    /// whenever an app reads the clipboard on its own.
    /// </summary>
    public bool OfferCopiedCapabilities { get; set; } = !OperatingSystem.IsMacOS();
    public string DismissedUpdateBannerToken { get; set; } = "";

    // ── Appearance ──
    public bool IsDarkTheme { get; set; } = true;

    /// <summary>Follow the operating system's light or dark mode; <see cref="IsDarkTheme"/> then mirrors it.</summary>
    public bool FollowSystemTheme { get; set; }

    public const string DefaultAccentColor = "lumi";

    /// <summary>Accent palette id (see <c>AccentPalettes</c>); "lumi" keeps Strata's own indigo.</summary>
    public string AccentColor { get; set; } = DefaultAccentColor;

    public const string WindowMaterialAuto = "auto";
    public const string WindowMaterialMica = "mica";
    public const string WindowMaterialSolid = "solid";

    /// <summary>Window backdrop: "auto" (acrylic, or the platform's blur), "mica" (Windows 11) or "solid".</summary>
    public string WindowMaterial { get; set; } = WindowMaterialAuto;

    public const string PresenceBrightnessSubtle = "subtle";
    public const string PresenceBrightnessBalanced = "balanced";
    public const string PresenceBrightnessVivid = "vivid";

    /// <summary>How bright the ambient presence glows: "subtle", "balanced" or "vivid".</summary>
    public string PresenceBrightness { get; set; } = PresenceBrightnessBalanced;

    public bool IsCompactDensity { get; set; }
    public int UiScalePercent { get; set; } = 100;
    [JsonPropertyName("fontSize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int LegacyFontSize { get; set; }
    public bool ShowAmbientPresence { get; set; } = true;
    public bool AnimatePresenceWhileWorking { get; set; }
    public bool ShowAnimations { get; set; } = true;

    // ── Chat ──
    public bool SendWithEnter { get; set; } = true;
    /// <summary>The language dictation listens for, as a Whisper language code; empty detects it.</summary>
    public string VoiceLanguage { get; set; } = "";
    public bool ShowTimestamps { get; set; } = true;
    public bool ShowToolCalls { get; set; } = true;
    public bool ShowReasoning { get; set; } = true;
    public bool ExpandReasoningWhileStreaming { get; set; } = true;
    public bool AutoGenerateTitles { get; set; } = true;

    /// <summary>Suggest follow-up prompts after Lumi replies. Each suggestion uses the background model.</summary>
    public bool SuggestFollowUps { get; set; } = true;

    /// <summary>Additional personal skill folders handed to Copilot, without copying their files.</summary>
    public List<string> PersonalSkillDirectories
    {
        get => _personalSkillDirectories ??= Services.Capabilities.PersonalSkillFolders.GetDefaults();
        set => _personalSkillDirectories = value;
    }

    // ── Permissions ──
    /// <summary>
    /// How much chats may do without asking. Full access was Lumi's only behavior before approvals
    /// existed, so it stays the default for new and existing users alike.
    /// </summary>
    public ApprovalMode DefaultApprovalMode { get; set; } = ApprovalMode.FullAccess;

    /// <summary>Approval rules for every chat.</summary>
    public List<ApprovalRule> ApprovalRules { get => _approvalRules; set => _approvalRules = value ?? []; }

    public AwayApprovalBehavior AwayApprovalBehavior { get; set; } = AwayApprovalBehavior.Wait;

    /// <summary>
    /// User preference for the companion Workspace panel. null = automatic (shows only on wide
    /// layouts when the chat has artifacts); true = always open; false = always closed. Set by the
    /// header toggle and persisted app-wide.
    /// </summary>
    public bool? WorkspacePanelOpen { get; set; }

    // ── AI & Models ──
    public string PreferredModel { get; set; } = "";

    public const string AutomaticBackgroundModel = "auto";

    /// <summary>Model for Lumi's internal AI helpers, independent of chat and agent model choices.</summary>
    public string BackgroundModel { get; set; } = AutomaticBackgroundModel;

    /// <summary>
    /// Models chosen for single AI features, by <see cref="AiFeatureIds"/> id. A feature without an entry runs on
    /// <see cref="BackgroundModel"/>.
    /// </summary>
    public Dictionary<string, string> AiFeatureModels { get => _aiFeatureModels; set => _aiFeatureModels = value ?? []; }

    /// <summary>The model chosen for <paramref name="feature"/>, or null when it follows <see cref="BackgroundModel"/>.</summary>
    public string? GetAiFeatureModel(string? feature)
        => feature is not null && AiFeatureModels.TryGetValue(feature, out var model) && !string.IsNullOrWhiteSpace(model)
            ? model.Trim()
            : null;

    /// <summary>
    /// Model ids the user pinned in the composer's model picker, in pin order. Pinned models are
    /// listed first in the picker. Ids that are no longer in the catalog are simply not rendered, so
    /// a retired favorite never has to be cleaned up.
    /// </summary>
    public List<string> FavoriteModelIds { get; set; } = [];
    public string ReasoningEffort { get; set; } = ""; // CLI-defined value, e.g. low, medium, high, xhigh, max
    public ResponseDetailLevel ResponseDetail { get; set; } = ResponseDetailLevel.ModelDefault;
    public string ContextWindowTier { get; set; } = ModelContextWindowTiers.Default;
    public string GlobalCustomInstructions { get; set; } = "";

    // ── BYOK (Bring Your Own Key) ──
    public List<ByokEndpoint> ByokEndpoints { get; set; } = [];
    public List<ByokModel> ByokModels { get; set; } = [];

    /// <summary>
    /// This computer's stable, opaque identifier for ChatGPT sign-in (<c>ext_agent_host_id</c>),
    /// created before the first sign-in and reused for every later one. Not a credential.
    /// </summary>
    public string? ChatGptHostId { get; set; }

    /// <summary>
    /// When true, Lumi routes all conversation/inference traffic to configured BYOK providers
    /// and blocks inference requests that would otherwise hit GitHub's internal Copilot
    /// endpoints (chat inference, title generation, suggestions, memory agent, background /
    /// orchestration sends). This is the BYOK Only block. A non-BYOK model selected with this
    /// flag on surfaces a clear error instead of silently routing through Copilot.
    /// Privacy guarantee: no conversation or inference content ever leaves your own endpoints.
    /// This guarantee is scoped to inference: GitHub Copilot is still used for sign-in, model
    /// discovery, and quota / entitlement checks, none of which carry conversation content.
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
    public bool UseLazyMcpInitialization { get; set; }

    public const int DefaultMcpToolTimeoutSeconds = 180;
    public const int MinMcpToolTimeoutSeconds = 1;
    public const int MaxMcpToolTimeoutSeconds = 86_400;

    // Applied to session configurations only; explicit server timeouts still take precedence.
    public int McpToolTimeoutSeconds { get; set; } = DefaultMcpToolTimeoutSeconds;

    public const string DefaultMcpRegistryUrl = "https://api.mcp.github.com";

    /// <summary>MCP registries (the official registry API) that Customize → Discover lists servers from.</summary>
    public List<string> McpRegistryUrls { get; set; } = [DefaultMcpRegistryUrl];

    // ── Privacy & Data ──
    public bool EnableMemoryAutoSave { get; set; } = true;
    public bool EnableMemoryAutoMaintenance { get; set; } = true;
    /// <summary>Each morning Lumi reviews the chats since its last run, updates the briefs and tidies memories.</summary>
    public bool EnableDailyBriefs { get; set; } = true;
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

    // ── Mobile companion (Lumi on your phone) ──
    /// <summary>
    /// When true Lumi listens on the local network so the Lumi mobile app can drive this
    /// desktop. Off by default; every device must still complete a pairing handshake.
    /// </summary>
    public bool RemoteAccessEnabled { get; set; }

    /// <summary>TCP port for the remote listener. 0 means "use the protocol default".</summary>
    public int RemoteAccessPort { get; set; }

    /// <summary>
    /// Legacy persisted security gate for the user's selected mobile transport. True means the user
    /// explicitly selected Local Wi-Fi; false means only verified Tailscale sockets are accepted.
    /// </summary>
    public bool RemoteAllowInsecureLan { get; set; }

    /// <summary>Use an owner-only Microsoft Dev Tunnel with a loopback-only listener instead of LAN/Tailscale.</summary>
    public bool RemoteUseDevTunnel { get; set; }

    /// <summary>
    /// Stable, random Dev Tunnel route for this Lumi profile. Once assigned, the cluster-qualified
    /// ID keeps the web-app URL unchanged while the tunnel itself remains short-lived.
    /// </summary>
    public string RemoteDevTunnelId { get; set; } = "";

    /// <summary>Devices that completed pairing and hold a long-lived token.</summary>
    public List<RemotePairedDevice> RemotePairedDevices { get; set; } = [];

    // ── Quota (cached, refreshed periodically) ──
    [JsonIgnore] public double? QuotaRemainingPercentage { get; set; }
    [JsonIgnore] public double? QuotaUsedRequests { get; set; }
    [JsonIgnore] public double? QuotaEntitlementRequests { get; set; }
    [JsonIgnore] public string? QuotaResetDate { get; set; }
}

/// <summary>A phone or tablet that has been authorized to control this Lumi desktop.</summary>
public class RemotePairedDevice
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Token { get; set; } = "";
    public DateTimeOffset PairedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastSeenAt { get; set; }
}

public class AppData
{
    public UserSettings Settings { get; set; } = new();
    public List<Chat> Chats { get; set; } = [];
    public List<ChatTag> ChatTags { get; set; } = [];
    public List<Project> Projects { get; set; } = [];
    public List<Skill> Skills { get; set; } = [];
    public List<LumiAgent> Agents { get; set; } = [];
    public List<McpServer> McpServers { get; set; } = [];
    public List<BackgroundJob> BackgroundJobs { get; set; } = [];
    public List<Memory> Memories { get; set; } = [];
    public List<ContextBrief> Briefs { get; set; } = [];
}
