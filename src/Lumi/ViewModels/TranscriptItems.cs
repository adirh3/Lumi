using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    protected TranscriptItem(string stableId)
    {
        StableId = stableId;
    }

    public string StableId { get; }
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
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasSkills => Skills.Count > 0;
    public List<FileAttachmentItem>? DisplayAttachments => HasAttachments ? Attachments : null;
    public List<SkillReference>? DisplaySkills => HasSkills ? Skills : null;

    /// <summary>Command invoked when user clicks Edit on the message. Sets EditText to current content.</summary>
    public ICommand BeginEditCommand { get; }

    /// <summary>Command invoked when user confirms an edit. Parameter is the new text string.</summary>
    public ICommand ConfirmEditCommand { get; }

    /// <summary>Command invoked when user clicks Regenerate/Retry on the message.</summary>
    public ICommand ResendCommand { get; }

    public UserMessageItem(ChatMessageViewModel source, bool showTimestamps, Action<ChatMessage, bool>? resendAction = null)
        : base($"message:user:{source.Message.Id}")
    {
        _source = source;
        _resendAction = resendAction;
        _content = source.Content;
        _timestampText = showTimestamps ? source.TimestampText : "";
        Attachments = source.Message.Attachments.Select(fp => new FileAttachmentItem(fp)).ToList();
        Skills = source.Message.ActiveSkills.ToList();

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

// ── Assistant message ────────────────────────────────

public partial class AssistantMessageItem : TranscriptItem
{
    private readonly ChatMessageViewModel _source;

    [ObservableProperty] private string _content;
    [ObservableProperty] private string _timestampText;
    [ObservableProperty] private bool _isStreaming;

    // Extras — populated when streaming ends
    [ObservableProperty] private bool _hasSkills;
    [ObservableProperty] private bool _hasFileAttachments;
    [ObservableProperty] private bool _hasSources;
    [ObservableProperty] private string _sourcesLabel = "";

    partial void OnHasSkillsChanged(bool value) => OnPropertyChanged(nameof(DisplaySkills));
    partial void OnHasFileAttachmentsChanged(bool value) => OnPropertyChanged(nameof(DisplayFileAttachments));
    partial void OnHasSourcesChanged(bool value) => OnPropertyChanged(nameof(DisplaySourcesSection));

    public string? Author => _source.Author;
    public string? ModelName => _source.ModelName;
    public ObservableCollection<SkillReference> Skills { get; } = [];
    public ObservableCollection<FileAttachmentItem> FileAttachments { get; } = [];
    public ObservableCollection<SourceItem> Sources { get; } = [];
    public ObservableCollection<SkillReference>? DisplaySkills => HasSkills ? Skills : null;
    public ObservableCollection<FileAttachmentItem>? DisplayFileAttachments => HasFileAttachments ? FileAttachments : null;
    public AssistantMessageItem? DisplaySourcesSection => HasSources ? this : null;

    public AssistantMessageItem(ChatMessageViewModel source, bool showTimestamps)
        : base($"message:assistant:{source.Message.Id}")
    {
        _source = source;
        _content = source.Content;
        _timestampText = showTimestamps ? source.TimestampText : "";
        _isStreaming = source.IsStreaming;

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
        List<FileAttachmentItem>? fileChips)
    {
        // Skills come from the persisted model
        Skills.Clear();
        foreach (var skill in _source.Message.ActiveSkills)
            Skills.Add(skill);
        HasSkills = Skills.Count > 0;

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
}

// ── Error message ────────────────────────────────────

public partial class ErrorMessageItem : TranscriptItem
{
    [ObservableProperty] private string _content;
    [ObservableProperty] private string _timestampText;

    public string? Author { get; }

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

    public ObservableCollection<ToolCallItemBase> ToolCalls { get; } = [];

    public ToolGroupItem(string label, string? stableId = null)
        : base(stableId ?? TranscriptIds.Create("tool-group"))
    {
        _label = label;
    }
}

// ── Single tool (flattened — rendered as StrataThink pill) ─

public partial class SingleToolItem : TranscriptItem
{
    public ToolCallItemBase Inner { get; }

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
        _ => null
    };

    // Terminal-specific
    public string? TerminalCommand => Inner is TerminalPreviewItem tp ? tp.Command : null;
    public string? TerminalOutput => Inner is TerminalPreviewItem tp && !string.IsNullOrWhiteSpace(tp.Output) ? tp.Output : null;
    public bool IsTerminal => Inner is TerminalPreviewItem;

    public bool HasContent => !string.IsNullOrWhiteSpace(InputParameters) || !string.IsNullOrWhiteSpace(MoreInfo);

    public bool HasDiff => Inner is ToolCallItem { HasDiff: true };
    public ICommand? ShowDiffCommand => Inner is ToolCallItem tc ? tc.ShowDiffCommand : null;

    public SingleToolItem(ToolCallItemBase inner)
        : base($"single:{inner.StableId}")
    {
        Inner = inner;
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

    internal TodoProgressItem? TodoItem { get; set; }
    internal string TodoToolStatus { get; set; } = "InProgress";
    internal int TodoTotal { get; set; }
    internal int TodoCompleted { get; set; }
    internal int TodoFailed { get; set; }
    internal int TodoUpdateCount { get; set; }

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
    public bool HasTranscriptText => !string.IsNullOrWhiteSpace(TranscriptText);
    public bool HasReasoningText => !string.IsNullOrWhiteSpace(ReasoningText);
    public bool HasActivities => Activities.Count > 0;
    public bool HasProgressValue => ProgressValue >= 0;
    public bool IsInProgress => Status == StrataAiToolCallStatus.InProgress;
    public bool IsCompleted => Status == StrataAiToolCallStatus.Completed;
    public bool IsFailed => Status == StrataAiToolCallStatus.Failed;
    public string? DurationText => DurationMs <= 0 ? null : DurationMs >= 1000 ? $"{DurationMs / 1000:F1}s" : $"{DurationMs:F0} ms";

    public SubagentToolCallItem(string displayName, StrataAiToolCallStatus status, string? stableId = null)
        : base(stableId ?? TranscriptIds.Create("subagent"))
    {
        _displayName = displayName;
        _status = status;
        Activities.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasActivities));
    }

    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(Title));
    partial void OnTaskDescriptionChanged(string? value) => OnPropertyChanged(nameof(Title));
    partial void OnCurrentIntentChanged(string? value) => OnPropertyChanged(nameof(Title));
    partial void OnAgentDescriptionChanged(string? value) => OnPropertyChanged(nameof(HasDescription));
    partial void OnModelDisplayNameChanged(string? value) => OnPropertyChanged(nameof(HasModelName));
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
    [ObservableProperty] private bool _allowFreeText;
    [ObservableProperty] private bool _allowMultiSelect;
    [ObservableProperty] private string? _selectedAnswer;
    [ObservableProperty] private bool _isAnswered;

    public QuestionItem(string questionId, string question, string options, bool allowFreeText,
        Action<string, string>? submitAction = null, bool allowMultiSelect = false)
        : base($"question:{questionId}")
    {
        QuestionId = questionId;
        _question = question;
        _options = options;
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
    }

    [RelayCommand]
    private void Open()
    {
        try { Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true }); }
        catch { /* ignore if file doesn't exist */ }
    }

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

    public SourceItem(SearchSource source)
    {
        Title = source.Title;
        Url = source.Url;
        Domain = ExtractDomain(source.Url);
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
