using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

internal static class TranscriptIds
{
    public static string Create(string prefix) => $"{prefix}:{Guid.NewGuid():N}";
}

// ── Base ─────────────────────────────────────────────

/// <summary>Base class for all items displayed in the chat transcript.</summary>
public abstract partial class TranscriptItem : ObservableObject
{
    private bool _isItemVisible = true;

    protected TranscriptItem(string stableId)
    {
        StableId = stableId;
    }

    public string StableId { get; }

    /// <summary>
    /// Controls visibility of the host container in the turn layout.
    /// When false, the item takes zero space (no gap from StackPanel spacing).
    /// </summary>
    public bool IsItemVisible
    {
        get => _isItemVisible;
        set => SetProperty(ref _isItemVisible, value);
    }
}

// ── User message ─────────────────────────────────────

public partial class UserMessageItem : TranscriptItem
{
    private readonly ChatMessageViewModel _source;
    private readonly Action<ChatMessage, bool>? _resendAction;

    [ObservableProperty] private string _content;
    [ObservableProperty] private string _timestampText;

    public string? Author => _source.Author;
    public ChatMessage Message => _source.Message;
    public List<FileAttachmentItem> Attachments { get; }
    public List<SkillReference> Skills { get; }
    public List<SkillChipItem> SkillChips { get; }
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasSkills => Skills.Count > 0;
    public List<FileAttachmentItem>? DisplayAttachments => HasAttachments ? Attachments : null;
    public List<SkillChipItem>? DisplaySkills => HasSkills ? SkillChips : null;

    /// <summary>Command invoked when user clicks Edit on the message. Sets EditText to current content.</summary>
    public ICommand BeginEditCommand { get; }

    /// <summary>Command invoked when user confirms an edit. Parameter is the new text string.</summary>
    public ICommand ConfirmEditCommand { get; }

    /// <summary>Command invoked when user clicks Regenerate/Retry on the message.</summary>
    public ICommand ResendCommand { get; }

    public UserMessageItem(ChatMessageViewModel source, bool showTimestamps, List<SkillReference>? filteredSkills = null, Action<ChatMessage, bool>? resendAction = null, Action<SkillReference>? openSkillAction = null)
        : base($"message:user:{source.Message.Id}")
    {
        _source = source;
        _resendAction = resendAction;
        _content = source.Content;
        _timestampText = showTimestamps ? source.TimestampText : "";
        Attachments = source.Message.Attachments.Select(fp => new FileAttachmentItem(fp)).ToList();
        Skills = filteredSkills ?? source.Message.ActiveSkills.ToList();
        SkillChips = Skills.Select(s => new SkillChipItem(s, () => openSkillAction?.Invoke(s))).ToList();

        BeginEditCommand = new RelayCommand(() => { /* Strata handles entering edit mode internally */ });
        ConfirmEditCommand = new RelayCommand<string>(text => EditAndResend(text ?? Content));
        ResendCommand = new RelayCommand(ResendFromMessage);
    }

    public void ResendFromMessage() => _resendAction?.Invoke(_source.Message, false);

    public void EditAndResend(string newContent)
    {
        _source.Message.Content = newContent;
        _source.NotifyContentChanged();
        Content = newContent;
        _resendAction?.Invoke(_source.Message, true);
    }
}

// ── Skill chip (clickable; opens the skill markdown in the right-side island) ──

/// <summary>
/// A loaded-skill chip shown under a message. Clicking it opens the skill's
/// markdown in the preview island (same surface used for plans).
/// </summary>
public partial class SkillChipItem
{
    private readonly Action? _openAction;

    public string Name { get; }
    public string Description { get; }

    public SkillChipItem(SkillReference skill, Action? openAction)
    {
        Name = skill.Name;
        Description = skill.Description;
        _openAction = openAction;
    }

    [RelayCommand]
    private void Open() => _openAction?.Invoke();
}

// ── Inline "skill loaded" chip (shown mid-turn when a skill is fetched at runtime) ──

/// <summary>
/// A turn-level item that renders a single skill chip inline, at the point in the
/// conversation where the skill was loaded (e.g. via a fetch_skill tool call).
/// </summary>
public sealed class SkillLoadedItem : TranscriptItem
{
    public SkillChipItem Chip { get; }

    public SkillLoadedItem(SkillChipItem chip, string? stableId = null)
        : base(stableId ?? TranscriptIds.Create("skill-loaded"))
    {
        Chip = chip;
    }
}

// ── Background job wake event ──────────────────────────

public sealed class JobWakeItem : TranscriptItem
{
    public string JobName { get; }
    public string TimestampText { get; }
    public string Instructions { get; }
    public string WakeSignal { get; }
    public string ExitCode { get; }
    public string StartedText { get; }
    public string CompletedText { get; }
    public string OutputText { get; }
    public bool HasInstructions => !string.IsNullOrWhiteSpace(Instructions);
    public bool HasExitCode => !string.IsNullOrWhiteSpace(ExitCode);
    public bool HasStarted => !string.IsNullOrWhiteSpace(StartedText);
    public bool HasCompleted => !string.IsNullOrWhiteSpace(CompletedText);
    public bool HasOutput => !string.IsNullOrWhiteSpace(OutputText);

    public JobWakeItem(ChatMessageViewModel source, bool showTimestamps)
        : base($"message:job-wake:{source.Message.Id}")
    {
        var content = source.Content;
        JobName = ExtractJobName(source, content);
        TimestampText = showTimestamps ? source.TimestampText : "";
        Instructions = ExtractSection(content, "Job instructions:", "Trigger context:");
        var triggerContext = ExtractSection(content, "Trigger context:", "Respond as Lumi");
        WakeSignal = ExtractWakeSignal(triggerContext);
        ExitCode = ExtractExitCode(triggerContext);
        StartedText = ExtractLineValue(triggerContext, "Started:");
        CompletedText = ExtractLineValue(triggerContext, "Completed:");
        OutputText = ExtractSection(triggerContext, "Full script output:", null);
    }

    public static bool IsJobWakeMessage(ChatMessageViewModel source)
        => source.Role == "user"
           && (source.Author?.StartsWith("Lumi Job - ", StringComparison.OrdinalIgnoreCase) ?? false)
           && source.Content.StartsWith("Background job triggered:", StringComparison.OrdinalIgnoreCase);

    public string SearchText => string.Join('\n', new[]
        {
            JobName,
            Instructions,
            WakeSignal,
            ExitCode,
            StartedText,
            CompletedText,
            OutputText
        }
        .Where(static value => !string.IsNullOrWhiteSpace(value)));

    private static string ExtractJobName(ChatMessageViewModel source, string content)
    {
        const string authorPrefix = "Lumi Job - ";
        if (source.Author?.StartsWith(authorPrefix, StringComparison.OrdinalIgnoreCase) == true)
            return source.Author[authorPrefix.Length..].Trim();

        var firstLine = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        const string contentPrefix = "Background job triggered:";
        return firstLine.StartsWith(contentPrefix, StringComparison.OrdinalIgnoreCase)
            ? firstLine[contentPrefix.Length..].Trim()
            : "Background job";
    }

    private static string ExtractSection(string content, string startMarker, string? endMarker)
    {
        var start = content.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return "";

        start += startMarker.Length;
        var end = endMarker is null
            ? content.Length
            : content.IndexOf(endMarker, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            end = content.Length;

        return content[start..end].Trim();
    }

    private static string ExtractWakeSignal(string triggerContext)
    {
        var signalSource = ExtractBefore(triggerContext, "Full script output:");
        var line = signalSource
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static value =>
                !value.StartsWith("Wake script exited", StringComparison.OrdinalIgnoreCase)
                && !value.StartsWith("Started:", StringComparison.OrdinalIgnoreCase)
                && !value.StartsWith("Completed:", StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(line) ? "Wake signal received." : Preview(line, 260);
    }

    private static string ExtractBefore(string content, string marker)
    {
        var index = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? content : content[..index];
    }

    private static string ExtractExitCode(string content)
    {
        var wakeLine = content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static value => value.StartsWith("Wake script exited with code", StringComparison.OrdinalIgnoreCase));
        if (wakeLine is not null)
        {
            const string prefix = "Wake script exited with code";
            return wakeLine[prefix.Length..].Trim().TrimEnd('.');
        }

        return ExtractLineValue(content, "Exit code:");
    }

    private static string ExtractLineValue(string content, string prefix)
    {
        var line = content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return line is null ? "" : line[prefix.Length..].Trim();
    }

    private static string Preview(string text, int maxLength)
    {
        var normalized = text.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..Math.Max(0, maxLength - 1)].TrimEnd() + "...";
    }
}

// ── Assistant message ────────────────────────────────

public partial class AssistantMessageItem : TranscriptItem
{
    private readonly ChatMessageViewModel _source;
    private readonly Action<SkillReference>? _openSkillAction;

    [ObservableProperty] private string _content;
    [ObservableProperty] private string _timestampText;
    [ObservableProperty] private bool _isStreaming;

    // Extras — populated when streaming ends
    [ObservableProperty] private bool _hasSkills;
    [ObservableProperty] private bool _hasFileAttachments;
    [ObservableProperty] private bool _hasSources;
    [ObservableProperty] private string _sourcesLabel = "";

    partial void OnContentChanged(string value) => IsItemVisible = !string.IsNullOrWhiteSpace(value);

    partial void OnHasSkillsChanged(bool value) => OnPropertyChanged(nameof(DisplaySkills));
    partial void OnHasFileAttachmentsChanged(bool value) => OnPropertyChanged(nameof(DisplayFileAttachments));
    partial void OnHasSourcesChanged(bool value) => OnPropertyChanged(nameof(DisplaySourcesSection));

    public string? Author => _source.Author;
    public string? ModelName => _source.ModelName;
    internal Guid MessageId => _source.Message.Id;
    public ObservableCollection<SkillReference> Skills { get; } = [];
    public ObservableCollection<SkillChipItem> SkillChips { get; } = [];
    public ObservableCollection<FileAttachmentItem> FileAttachments { get; } = [];
    public ObservableCollection<SourceItem> Sources { get; } = [];
    public ObservableCollection<SkillChipItem>? DisplaySkills => HasSkills ? SkillChips : null;
    public ObservableCollection<FileAttachmentItem>? DisplayFileAttachments => HasFileAttachments ? FileAttachments : null;
    public AssistantMessageItem? DisplaySourcesSection => HasSources ? this : null;

    public AssistantMessageItem(ChatMessageViewModel source, bool showTimestamps, Action<SkillReference>? openSkillAction = null)
        : base($"message:assistant:{source.Message.Id}")
    {
        _source = source;
        _openSkillAction = openSkillAction;
        _content = source.Content;
        _timestampText = showTimestamps ? source.TimestampText : "";
        _isStreaming = source.IsStreaming;

        // Hide the bubble when content is only whitespace (e.g. the SDK sends "\n\n"
        // before tool calls / reasoning). Shows automatically when real content arrives.
        IsItemVisible = !string.IsNullOrWhiteSpace(_content);

        // Only subscribe while content is still changing (streaming).
        // Once streaming ends, content is final — unsubscribe to avoid leaks.
        if (source.IsStreaming)
            source.PropertyChanged += OnSourcePropertyChanged;
    }

    private void OnSourcePropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatMessageViewModel.Content))
        {
            Content = _source.Content;
            // Skip expensive height estimation during streaming — content changes too fast.
            // Height is recalculated once when streaming ends.
        }
        else if (e.PropertyName == nameof(ChatMessageViewModel.IsStreaming) && !_source.IsStreaming)
        {
            IsStreaming = false;
            _source.PropertyChanged -= OnSourcePropertyChanged;
        }
    }

    /// <summary>
    /// Populates extras from pending state. Called by the ChatViewModel
    /// when the assistant turn completes (or during transcript rebuild).
    /// </summary>
    public void ApplyExtras(
        List<FileAttachmentItem>? fileChips,
        HashSet<string>? shownSkillNames = null)
    {
        // Skills come from the persisted model. Keep the full list for data (clipboard).
        // Render a chip for each skill not already shown earlier in the transcript — e.g.
        // a skill attached to the user message, or one surfaced inline at its fetch_skill
        // load point. Skills the SDK reports via SkillInvokedEvent (no inline tool call)
        // still surface here at the end of the turn.
        Skills.Clear();
        SkillChips.Clear();
        foreach (var skill in _source.Message.ActiveSkills)
        {
            Skills.Add(skill);
            if (shownSkillNames is null || shownSkillNames.Add(skill.Name))
            {
                var captured = skill;
                SkillChips.Add(new SkillChipItem(captured, () => _openSkillAction?.Invoke(captured)));
            }
        }
        HasSkills = SkillChips.Count > 0;

        // File attachments
        if (fileChips is { Count: > 0 })
        {
            foreach (var fc in fileChips)
                FileAttachments.Add(fc);
        }
        HasFileAttachments = FileAttachments.Count > 0;

        // Sources come from the persisted model
        Sources.Clear();
        foreach (var src in _source.Message.Sources)
            Sources.Add(new SourceItem(src));
        HasSources = Sources.Count > 0;
        SourcesLabel = Sources.Count == 1 ? Loc.Sources_One : string.Format(Loc.Sources_N, Sources.Count);
    }

    /// <summary>
    /// Re-reads sources from the persisted model and updates the observable sources section in
    /// place. Used when web sources are attached after the turn completes (on session idle) so the
    /// sources can appear without rebuilding — and re-parsing — the whole transcript.
    /// </summary>
    public void RefreshSources()
    {
        Sources.Clear();
        foreach (var src in _source.Message.Sources)
            Sources.Add(new SourceItem(src));
        HasSources = Sources.Count > 0;
        SourcesLabel = Sources.Count == 1 ? Loc.Sources_One : string.Format(Loc.Sources_N, Sources.Count);
    }
}

// ── Error message ────────────────────────────────────

public partial class ErrorMessageItem : TranscriptItem
{
    [ObservableProperty] private string _content;
    [ObservableProperty] private string _timestampText;
    [ObservableProperty] private bool _showRetryButton;

    public string? Author { get; }

    // Observable so a Retry affordance attached AFTER the card has already rendered — e.g. when
    // UpdateStuckChatRetryAffordance heals a reopened, previously-bricked chat — actually notifies
    // the button's Command binding instead of silently leaving it command-less.
    [ObservableProperty] private ICommand? _retryCommand;

    public ErrorMessageItem(ChatMessageViewModel source, bool showTimestamps)
        : base($"message:error:{source.Message.Id}")
    {
        Author = source.Author;
        _content = source.Content;
        _timestampText = showTimestamps ? source.TimestampText : "";
    }

    public ErrorMessageItem(string content, string? author = null)
        : base(TranscriptIds.Create("error"))
    {
        Author = author;
        _content = content;
        _timestampText = "";
    }
}

// ── Reasoning block ──────────────────────────────────

public partial class ReasoningItem : TranscriptItem
{
    [ObservableProperty] private string _content;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isExpanded;

    private readonly ChatMessageViewModel? _source;
    private readonly bool _expandWhileStreaming;

    public ReasoningItem(ChatMessageViewModel source, bool expandWhileStreaming)
        : base($"message:reasoning:{source.Message.Id}")
    {
        _content = source.Content;
        _isActive = source.IsStreaming;
        _isExpanded = expandWhileStreaming && source.IsStreaming;

        // Only subscribe while streaming. Once done, content is final.
        if (source.IsStreaming)
        {
            _source = source;
            _expandWhileStreaming = expandWhileStreaming;
            source.PropertyChanged += OnSourcePropertyChanged;
        }
    }

    private void OnSourcePropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatMessageViewModel.Content) && _source is not null)
        {
            Content = _source.Content;
            // Skip expensive height estimation during streaming — recalculated when streaming ends.
        }
        else if (e.PropertyName == nameof(ChatMessageViewModel.IsStreaming) && _source is not null && !_source.IsStreaming)
        {
            IsActive = false;
            if (_expandWhileStreaming)
                IsExpanded = false;
            _source.PropertyChanged -= OnSourcePropertyChanged;
        }
    }
}

// ── Tool group (collapsible container of tool calls) ─

public partial class ToolGroupItem : TranscriptItem
{
    [ObservableProperty] private string _label;
    [ObservableProperty] private string? _meta;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private double _progressValue = -1;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string? _streamingSummary;

    public ObservableCollection<ToolCallItemBase> ToolCalls { get; } = [];
    public bool HasStreamingSummary => !string.IsNullOrWhiteSpace(StreamingSummary);
    public ChatMessageViewModel? Source { get; set; }

    public ToolGroupItem(string label, string? stableId = null)
        : base(stableId ?? TranscriptIds.Create("tool-group"))
    {
        _label = label;
    }

    partial void OnStreamingSummaryChanged(string? value) => OnPropertyChanged(nameof(HasStreamingSummary));
}

// ── Single tool (flattened — rendered as StrataThink pill) ─

public partial class SingleToolItem : TranscriptItem
{
    public ToolCallItemBase Inner { get; }
    private readonly ChatMessageViewModel? _source;

    [ObservableProperty] private bool _isExpanded;

    public string Label => Inner switch
    {
        ToolCallItem tc => tc.ToolName,
        TerminalPreviewItem tp => tp.ToolName,
        TodoProgressItem todo => todo.ToolName,
        _ => ""
    };

    public bool IsActive => Inner switch
    {
        ToolCallItem tc => tc.Status == StrataAiToolCallStatus.InProgress,
        TerminalPreviewItem tp => tp.Status == StrataAiToolCallStatus.InProgress,
        TodoProgressItem todo => todo.Status == StrataAiToolCallStatus.InProgress,
        _ => false
    };

    public string? Meta
    {
        get
        {
            double ms = Inner switch
            {
                ToolCallItem tc => tc.DurationMs,
                TerminalPreviewItem tp => tp.DurationMs,
                _ => 0
            };
            if (ms <= 0) return null;
            return ms >= 1000 ? $"{ms / 1000:F1}s" : $"{ms:F0} ms";
        }
    }

    public string? InputParameters => Inner switch
    {
        ToolCallItem tc => tc.InputParameters,
        TodoProgressItem todo => todo.InputParameters,
        _ => null
    };

    public string? MoreInfo => Inner switch
    {
        ToolCallItem tc => tc.MoreInfo,
        _ when _source?.Message.ToolStatus == "Failed" => _source.Message.ToolOutput,
        _ => null
    };

    // Terminal-specific
    public string? TerminalCommand => Inner is TerminalPreviewItem tp ? tp.Command : null;
    public string? TerminalOutput => Inner is TerminalPreviewItem tp && !string.IsNullOrWhiteSpace(tp.Output) ? tp.Output : null;
    public bool IsTerminal => Inner is TerminalPreviewItem;

    /// <summary>The message this flattened tool came from (needed to re-wrap it in a tool group).</summary>
    public ChatMessageViewModel? Source => _source;

    public bool HasContent => !string.IsNullOrWhiteSpace(InputParameters) || !string.IsNullOrWhiteSpace(MoreInfo);

    public bool HasDiff => Inner is ToolCallItem { HasDiff: true };
    public ICommand? ShowDiffCommand => Inner is ToolCallItem tc ? tc.ShowDiffCommand : null;

    public SingleToolItem(ToolCallItemBase inner)
        : base($"single:{inner.StableId}")
    {
        Inner = inner;
    }

    public SingleToolItem(ToolCallItemBase inner, ChatMessageViewModel? source)
        : this(inner)
    {
        _source = source;
    }
}

// ── Base for items inside a tool group ───────────────

public abstract partial class ToolCallItemBase : ObservableObject
{
    protected ToolCallItemBase(string stableId)
    {
        StableId = stableId;
    }

    public string StableId { get; }
}

// ── Subagent card (standalone turn-level item) ─────────

public partial class SubagentToolCallItem : TranscriptItem
{
    [ObservableProperty] private string _displayName;
    [ObservableProperty] private string? _taskDescription;
    [ObservableProperty] private string? _agentDescription;
    [ObservableProperty] private string? _currentIntent;
    [ObservableProperty] private string? _modeLabel;
    [ObservableProperty] private string? _modelDisplayName;
    [ObservableProperty] private string? _transcriptText;
    [ObservableProperty] private string? _reasoningText;
    [ObservableProperty] private string? _meta;
    [ObservableProperty] private double _progressValue = -1;
    [ObservableProperty] private StrataAiToolCallStatus _status;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private double _durationMs;
    [ObservableProperty] private int _accentIndex = 1;

    internal TodoProgressItem? TodoItem { get; set; }
    internal string TodoToolStatus { get; set; } = "InProgress";
    internal int TodoTotal { get; set; }
    internal int TodoCompleted { get; set; }
    internal int TodoFailed { get; set; }
    internal int TodoUpdateCount { get; set; }

    /// <summary>Non-null when this subagent is rendered inside a parallel fan-out group.</summary>
    internal SubagentGroupItem? OwningGroup { get; set; }

    /// <summary>True when this subagent is one of several shown together in a group.</summary>
    public bool IsGrouped => OwningGroup is { IsGrouped: true };

    public ObservableCollection<ToolCallItemBase> Activities { get; } = [];

    public string Title
        => !string.IsNullOrWhiteSpace(CurrentIntent)
            ? CurrentIntent!
            : !string.IsNullOrWhiteSpace(TaskDescription)
                ? TaskDescription!
                : DisplayName;

    public bool IsActive => Status == StrataAiToolCallStatus.InProgress;
    public bool HasDescription => !string.IsNullOrWhiteSpace(AgentDescription);
    public bool HasModelName => !string.IsNullOrWhiteSpace(ModelDisplayName);
    public bool HasModeLabel => !string.IsNullOrWhiteSpace(ModeLabel);
    public bool IsBackgroundMode => string.Equals(ModeLabel, "Background", StringComparison.OrdinalIgnoreCase);
    public bool HasTranscriptText => !string.IsNullOrWhiteSpace(TranscriptText);
    public bool HasReasoningText => !string.IsNullOrWhiteSpace(ReasoningText);
    public bool HasActivities => Activities.Count > 0;
    public bool HasProgressValue => ProgressValue >= 0;
    public bool IsInProgress => Status == StrataAiToolCallStatus.InProgress;
    public bool IsCompleted => Status == StrataAiToolCallStatus.Completed;
    public bool IsFailed => Status == StrataAiToolCallStatus.Failed;
    public string? DurationText => DurationMs <= 0 ? null : DurationMs >= 1000 ? $"{DurationMs / 1000:F1}s" : $"{DurationMs:F0} ms";

    /// <summary>Single uppercase glyph for the agent avatar (first letter of the agent's role/name).</summary>
    public string Initial
    {
        get
        {
            var source = !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName : Title;
            foreach (var ch in source)
                if (char.IsLetterOrDigit(ch))
                    return char.ToUpperInvariant(ch).ToString();
            return "•";
        }
    }

    /// <summary>"Role · Model" line shown under the agent's intent in a group lane.</summary>
    public string AgentSubtitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DisplayName) && !string.IsNullOrWhiteSpace(ModelDisplayName))
                return $"{DisplayName} · {ModelDisplayName}";
            return !string.IsNullOrWhiteSpace(ModelDisplayName) ? ModelDisplayName! : DisplayName;
        }
    }

    public SubagentToolCallItem(string displayName, StrataAiToolCallStatus status, string? stableId = null)
        : base(stableId ?? TranscriptIds.Create("subagent"))
    {
        _displayName = displayName;
        _status = status;
        Activities.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasActivities));
    }

    partial void OnDisplayNameChanged(string value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Initial));
        OnPropertyChanged(nameof(AgentSubtitle));
    }
    partial void OnTaskDescriptionChanged(string? value) => OnPropertyChanged(nameof(Title));
    partial void OnCurrentIntentChanged(string? value) => OnPropertyChanged(nameof(Title));
    partial void OnAgentDescriptionChanged(string? value) => OnPropertyChanged(nameof(HasDescription));
    partial void OnModelDisplayNameChanged(string? value)
    {
        OnPropertyChanged(nameof(HasModelName));
        OnPropertyChanged(nameof(AgentSubtitle));
    }
    partial void OnModeLabelChanged(string? value) { OnPropertyChanged(nameof(HasModeLabel)); OnPropertyChanged(nameof(IsBackgroundMode)); }
    partial void OnTranscriptTextChanged(string? value) => OnPropertyChanged(nameof(HasTranscriptText));
    partial void OnReasoningTextChanged(string? value) => OnPropertyChanged(nameof(HasReasoningText));
    partial void OnProgressValueChanged(double value) => OnPropertyChanged(nameof(HasProgressValue));
    partial void OnDurationMsChanged(double value) => OnPropertyChanged(nameof(DurationText));

    partial void OnStatusChanged(StrataAiToolCallStatus value)
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsInProgress));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));

        if (value != StrataAiToolCallStatus.InProgress)
            IsExpanded = false;
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

// ── Subagent group (parallel fan-out container) ────────

/// <summary>
/// Groups several sub-agents that were launched together (a parallel fan-out) into a single
/// scannable card with an aggregate header, instead of stacking disconnected agent cards.
/// Only created when 2+ sub-agents run back-to-back; a lone sub-agent stays a standalone card.
/// </summary>
public partial class SubagentGroupItem : TranscriptItem
{
    [ObservableProperty] private string _headerLabel = "";
    [ObservableProperty] private string? _meta;
    [ObservableProperty] private double _progressValue = -1;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _doneCount;
    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private int _failedCount;

    public ObservableCollection<SubagentToolCallItem> Subagents { get; } = [];

    public int Count => Subagents.Count;
    public bool IsGrouped => Subagents.Count >= 2;
    public bool HasProgressValue => ProgressValue >= 0;
    public bool HasRunning => RunningCount > 0;
    public bool HasFailed => FailedCount > 0;
    public bool ShowDoneBadge => !IsActive && FailedCount == 0 && TotalCount > 0;
    public bool ShowFailedBadge => !IsActive && FailedCount > 0;

    partial void OnProgressValueChanged(double value) => OnPropertyChanged(nameof(HasProgressValue));
    partial void OnRunningCountChanged(int value) => OnPropertyChanged(nameof(HasRunning));
    partial void OnFailedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(ShowDoneBadge));
        OnPropertyChanged(nameof(ShowFailedBadge));
    }
    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDoneBadge));
        OnPropertyChanged(nameof(ShowFailedBadge));
    }
    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(ShowDoneBadge));

    public SubagentGroupItem(string? stableId = null)
        : base(stableId ?? TranscriptIds.Create("subagent-group"))
    {
        Subagents.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(IsGrouped));
        };
    }
}

// ── Regular tool call ────────────────────────────────

public partial class ToolCallItem : ToolCallItemBase
{
    [ObservableProperty] private string _toolName;
    [ObservableProperty] private StrataAiToolCallStatus _status;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string? _inputParameters;
    [ObservableProperty] private string? _moreInfo;
    [ObservableProperty] private double _durationMs;
    [ObservableProperty] private bool _hasDiff;
    [ObservableProperty] private bool _isCompact;

    public string? DiffFilePath { get; set; }
    public string? DiffToolName { get; set; }
    public List<(string? OldText, string? NewText)>? DiffEdits { get; set; }
    public string? DiffOriginalContent { get; set; }
    public string? DiffCurrentContent { get; set; }
    public Action<FileChangeItem>? ShowFileChangeAction { get; set; }

    public ToolCallItem(string toolName, StrataAiToolCallStatus status, string? stableId = null)
        : base(stableId ?? TranscriptIds.Create("tool"))
    {
        _toolName = toolName;
        _status = status;
    }

    [RelayCommand]
    private void ShowDiff()
    {
        if (DiffFilePath is null || ShowFileChangeAction is null) return;
        var isCreate = DiffToolName is not null && ToolDisplayHelper.IsFileCreateTool(DiffToolName);
        var item = new FileChangeItem(DiffFilePath, isCreate, null);
        if (DiffEdits is not null)
            foreach (var (old, @new) in DiffEdits)
                item.AddEdit(old, @new);
        if (DiffOriginalContent is not null || DiffCurrentContent is not null)
            item.SetSnapshots(DiffOriginalContent, DiffCurrentContent);
        ShowFileChangeAction.Invoke(item);
    }
}

// ── Terminal preview (powershell output) ─────────────

public partial class TerminalPreviewItem : ToolCallItemBase
{
    [ObservableProperty] private string _toolName;
    [ObservableProperty] private string _command;
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private StrataAiToolCallStatus _status;
    [ObservableProperty] private double _durationMs;
    [ObservableProperty] private bool _isExpanded;

    /// <summary>True when the tool call has returned but the shell it launched (an <c>async</c>
    /// command the agent left running past its turn) is still executing in the background. Keeps the
    /// card visibly "running" instead of prematurely reading "Completed".</summary>
    [ObservableProperty] private bool _isRunningInBackground;

    /// <summary>The shell's authoritative start time (from the Tasks API), used to anchor the live
    /// "running in background" elapsed clock. Bound to the card's <c>RunningSince</c> so the readout
    /// stays correct across control recreation (chat switch, virtualization) and manual collapse —
    /// the clock is derived from this fixed instant rather than from when the control last loaded.</summary>
    [ObservableProperty] private DateTimeOffset? _backgroundStartedUtc;

    public TerminalPreviewItem(string toolName, string command, StrataAiToolCallStatus status, string? stableId = null)
        : base(stableId ?? TranscriptIds.Create("terminal"))
    {
        _toolName = toolName;
        _command = command;
        _status = status;
    }
}

// ── Todo progress (inside tool group) ────────────────

public partial class TodoProgressItem : ToolCallItemBase
{
    [ObservableProperty] private string _toolName;
    [ObservableProperty] private StrataAiToolCallStatus _status;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string? _inputParameters;
    [ObservableProperty] private string? _moreInfo;

    public TodoProgressItem(string toolName, StrataAiToolCallStatus status, string? stableId = null)
        : base(stableId ?? TranscriptIds.Create("todo"))
    {
        _toolName = toolName;
        _status = status;
    }
}

// ── Question card ────────────────────────────────────

public partial class QuestionItem : TranscriptItem
{
    private readonly Action<string, string>? _submitAction;
    private bool _isSubmitting;

    public string QuestionId { get; }
    [ObservableProperty] private string _question;
    [ObservableProperty] private string _options;
    [ObservableProperty] private IList<string>? _optionsList;
    [ObservableProperty] private bool _allowFreeText;
    [ObservableProperty] private bool _allowMultiSelect;
    [ObservableProperty] private string? _selectedAnswer;
    [ObservableProperty] private bool _isAnswered;
    [ObservableProperty] private bool _isExpired;

    public QuestionItem(string questionId, string question, IList<string> optionsList, bool allowFreeText,
        Action<string, string>? submitAction = null, bool allowMultiSelect = false)
        : base($"question:{questionId}")
    {
        QuestionId = questionId;
        _question = question;
        _options = string.Join(",", optionsList);
        _optionsList = optionsList;
        _allowFreeText = allowFreeText;
        _allowMultiSelect = allowMultiSelect;
        _submitAction = submitAction;
    }

    partial void OnIsAnsweredChanged(bool value)
    {
        if (value && !_isSubmitting && !string.IsNullOrEmpty(SelectedAnswer))
        {
            _isSubmitting = true;
            _submitAction?.Invoke(QuestionId, SelectedAnswer);
            _isSubmitting = false;
        }
    }

    public void Submit(string answer)
    {
        _isSubmitting = true;
        SelectedAnswer = answer;
        IsAnswered = true;
        _submitAction?.Invoke(QuestionId, answer);
        _isSubmitting = false;
    }
}

// ── Typing indicator ─────────────────────────────────

public partial class TypingIndicatorItem : TranscriptItem
{
    [ObservableProperty] private string _label;
    [ObservableProperty] private bool _isActive;

    public TypingIndicatorItem(string label)
        : base("typing-indicator")
    {
        _label = label;
        _isActive = true;
    }
}

// ── Turn summary (collapses tool groups + reasoning) ─

public partial class TurnSummaryItem : TranscriptItem
{
    [ObservableProperty] private string _label;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _hasFailures;

    public ObservableCollection<TranscriptItem> InnerItems { get; } = [];

    public TurnSummaryItem(string label, string? stableId = null)
        : base(stableId ?? TranscriptIds.Create("turn-summary"))
    {
        _label = label;
    }
}

// ── File attachment display item ─────────────────────

public partial class FileAttachmentItem : ObservableObject
{
    private readonly Action<string>? _removeAction;

    public string FilePath { get; }
    public string FileName { get; }
    public string? FileSize { get; }
    public bool IsRemovable { get; }
    public Avalonia.Media.Imaging.Bitmap? IconImage { get; }

    public FileAttachmentItem(string filePath, bool isRemovable = false, Action<string>? removeAction = null)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        IsRemovable = isRemovable;
        _removeAction = removeAction;

        try
        {
            var info = new FileInfo(filePath);
            if (info.Exists)
                FileSize = ToolDisplayHelper.FormatFileSize(info.Length);
        }
        catch { /* ignore */ }

        IconImage = Services.FileIconHelper.GetFileIcon(filePath);
    }

    [RelayCommand]
    private void Open()
    {
        try { Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true }); }
        catch { /* ignore if file doesn't exist */ }
    }

    /// <summary>Opens the containing folder, selecting the file when it still exists.</summary>
    [RelayCommand]
    private void ShowInFolder()
    {
        try
        {
            // explorer.exe /select only honors backslash separators, and announced paths
            // frequently arrive with forward slashes from tool output — normalize first.
            var path = FilePath;
            try { path = Path.GetFullPath(FilePath); } catch { /* keep original */ }

            if (OperatingSystem.IsWindows() && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true
                });
                return;
            }

            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    /// <summary>Copies the file itself so it can be pasted into another folder.</summary>
    [RelayCommand]
    private Task Copy() => Services.ClipboardHelper.CopyFileAsync(FilePath);

    /// <summary>Copies the full file path as text.</summary>
    [RelayCommand]
    private Task CopyPath() => Services.ClipboardHelper.CopyTextAsync(FilePath);

    /// <summary>Copies just the file name as text.</summary>
    [RelayCommand]
    private Task CopyName() => Services.ClipboardHelper.CopyTextAsync(FileName);

    [RelayCommand]
    private void Remove()
    {
        if (IsRemovable)
            _removeAction?.Invoke(FilePath);
    }
}

// ── Source citation display item ─────────────────────

public partial class SourceItem : ObservableObject
{
    public string Title { get; }
    public string Domain { get; }
    public string Url { get; }

    /// <summary>Single glyph shown in the source's favicon-style chip.</summary>
    public string InitialLetter { get; }

    /// <summary>Deterministic accent fill for the favicon-style chip (stable per domain).</summary>
    public Avalonia.Media.IBrush AccentBrush { get; }

    public SourceItem(SearchSource source)
    {
        Title = source.Title;
        Url = source.Url;
        Domain = ExtractDomain(source.Url);
        InitialLetter = ComputeInitial(Domain, Title);
        AccentBrush = ComputeAccent(Domain);
    }

    [RelayCommand]
    private void Open()
    {
        try { Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private static string ExtractDomain(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host.Replace("www.", "");
        return url;
    }

    private static string ComputeInitial(string domain, string title)
    {
        var basis = !string.IsNullOrWhiteSpace(domain) ? domain : title;
        foreach (var ch in basis)
        {
            if (char.IsLetterOrDigit(ch))
                return char.ToUpperInvariant(ch).ToString();
        }
        return "?";
    }

    private static readonly Avalonia.Media.Color[] AccentPalette =
    [
        Avalonia.Media.Color.FromRgb(0x6E, 0x8B, 0xFF),
        Avalonia.Media.Color.FromRgb(0x35, 0xC2, 0xA8),
        Avalonia.Media.Color.FromRgb(0xF2, 0xA1, 0x4E),
        Avalonia.Media.Color.FromRgb(0xF2, 0x6D, 0x8B),
        Avalonia.Media.Color.FromRgb(0xA9, 0x7B, 0xFF),
        Avalonia.Media.Color.FromRgb(0x4E, 0xB6, 0xF2),
        Avalonia.Media.Color.FromRgb(0x5F, 0xCB, 0x7A),
        Avalonia.Media.Color.FromRgb(0xFF, 0x8A, 0x5C),
    ];

    private static Avalonia.Media.IBrush ComputeAccent(string key)
    {
        // FNV-1a over the domain → stable index (process-independent, unlike GetHashCode).
        uint hash = 2166136261;
        foreach (var ch in key)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        var color = AccentPalette[(int)(hash % (uint)AccentPalette.Length)];
        return new Avalonia.Media.SolidColorBrush(color);
    }
}

// ── File change display item ─────────────────────────

public partial class FileChangeItem : ObservableObject
{
    public string FilePath { get; }
    public string FileName { get; }
    public string ActionIcon { get; }
    public string ActionLabel { get; }
    public string? Directory { get; }
    public bool IsCreate { get; }
    public int LinesAdded { get; private set; }
    public int LinesRemoved { get; private set; }
    public string StatsAdded => $"+{LinesAdded}";
    public string StatsRemoved => LinesRemoved > 0 ? $"−{LinesRemoved}" : "";
    public bool HasRemovals => LinesRemoved > 0;
    public string? OriginalContent { get; private set; }
    public string? CurrentContent { get; private set; }
    public bool HasSnapshots { get; private set; }

    /// <summary>All edits applied to this file (old text → new text pairs).</summary>
    public List<(string? OldText, string? NewText)> Edits { get; } = [];

    private readonly Action<FileChangeItem>? _showDiffAction;

    public FileChangeItem(string filePath, bool isCreate, Action<FileChangeItem>? showDiffAction = null)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Directory = Path.GetDirectoryName(filePath);
        IsCreate = isCreate;
        _showDiffAction = showDiffAction;

        ActionIcon = isCreate ? "📄" : "📝";
        ActionLabel = isCreate ? Loc.FileChange_Created : Loc.FileChange_Modified;
    }

    /// <summary>Adds an edit and updates line stats.</summary>
    public void AddEdit(string? oldText, string? newText)
    {
        Edits.Add((oldText, newText));
        LinesAdded += CountLines(newText);
        LinesRemoved += CountLines(oldText);
        OnPropertyChanged(nameof(LinesAdded));
        OnPropertyChanged(nameof(LinesRemoved));
        OnPropertyChanged(nameof(StatsAdded));
        OnPropertyChanged(nameof(StatsRemoved));
        OnPropertyChanged(nameof(HasRemovals));
    }

    public void SetSnapshots(string? originalContent, string? currentContent)
    {
        OriginalContent = originalContent;
        CurrentContent = currentContent;
        HasSnapshots = true;
    }

    public void MergeFrom(FileChangeItem other)
    {
        foreach (var (oldText, newText) in other.Edits)
            AddEdit(oldText, newText);

        if (other.HasSnapshots)
            SetSnapshots(HasSnapshots ? OriginalContent : other.OriginalContent, other.CurrentContent);
    }

    /// <summary>
    /// For created files where we couldn't extract content from tool args,
    /// read the file to get accurate line count.
    /// </summary>
    public void EnsureStatsForCreatedFile()
    {
        if (!IsCreate || LinesAdded > 0) return;
        try
        {
            if (File.Exists(FilePath))
                LinesAdded = File.ReadAllLines(FilePath).Length;
        }
        catch { /* ignore */ }
    }

    private static int CountLines(string? text)
        => string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Length;

    [RelayCommand]
    private void ShowDiff() => _showDiffAction?.Invoke(this);
}

// ── Git file change display item ─────────────────────

public partial class GitFileChangeViewModel : ObservableObject
{
    public GitFileChange Change { get; }
    private readonly Action<GitFileChangeViewModel>? _showDiffAction;

    public string FileName => Change.FileName;
    public string? Directory => Change.Directory;
    public string KindIcon => Change.KindIcon;
    public string KindLabel => Change.KindLabel;
    public GitChangeKind Kind => Change.Kind;
    public int LinesAdded => Change.LinesAdded;
    public int LinesRemoved => Change.LinesRemoved;
    public bool HasStats => LinesAdded > 0 || LinesRemoved > 0;

    public GitFileChangeViewModel(GitFileChange change, Action<GitFileChangeViewModel>? showDiffAction = null)
    {
        Change = change;
        _showDiffAction = showDiffAction;
    }

    [RelayCommand]
    private void ShowDiff() => _showDiffAction?.Invoke(this);
}

// ── File changes summary (standalone transcript item at end of turn) ──

public partial class FileChangesSummaryItem : TranscriptItem
{
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _totalStatsAdded = "";
    [ObservableProperty] private string _totalStatsRemoved = "";
    [ObservableProperty] private bool _hasTotalRemovals;

    public ObservableCollection<FileChangeItem> FileChanges { get; } = [];

    public FileChangesSummaryItem(List<FileChangeItem> fileChanges, string? stableId = null)
        : base(stableId ?? TranscriptIds.Create("file-changes"))
    {
        foreach (var fc in fileChanges)
            FileChanges.Add(fc);

        RefreshSummary();
    }

    public void MergeChanges(IEnumerable<FileChangeItem> fileChanges)
    {
        foreach (var fileChange in fileChanges)
        {
            var existing = FileChanges.FirstOrDefault(existing =>
                string.Equals(existing.FilePath, fileChange.FilePath, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                FileChanges.Add(fileChange);
            else
                existing.MergeFrom(fileChange);
        }

        RefreshSummary();
    }

    private void RefreshSummary()
    {
        var fileChanges = FileChanges.ToList();
        var totalAdded = fileChanges.Sum(fc => fc.LinesAdded);
        var totalRemoved = fileChanges.Sum(fc => fc.LinesRemoved);
        TotalStatsAdded = $"+{totalAdded}";
        TotalStatsRemoved = totalRemoved > 0 ? $"−{totalRemoved}" : "";
        HasTotalRemovals = totalRemoved > 0;
        Label = fileChanges.Count == 1 ? Loc.FileChanges_One : string.Format(Loc.FileChanges_N, fileChanges.Count);
    }
}

// ── Plan card (inline indicator when agent creates/updates a plan) ──

public partial class PlanCardItem : TranscriptItem
{
    private readonly Action? _openAction;

    [ObservableProperty] private string _statusText;

    public PlanCardItem(string statusText, Action? openAction, string? stableId = null)
        : base(stableId ?? TranscriptIds.Create("plan-card"))
    {
        _statusText = statusText;
        _openAction = openAction;
    }

    [RelayCommand]
    private void Open() => _openAction?.Invoke();
}

// ── Turn model label (end of turn) ──────────────────

public partial class TurnModelItem : TranscriptItem
{
    [ObservableProperty] private string _modelName;

    public TurnModelItem(string modelName, string? stableId = null)
        : base(stableId ?? TranscriptIds.Create("turn-model"))
    {
        _modelName = modelName;
    }
}
