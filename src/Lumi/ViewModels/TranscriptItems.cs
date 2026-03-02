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

// ── Base ─────────────────────────────────────────────

/// <summary>Base class for all items displayed in the chat transcript.</summary>
public abstract partial class TranscriptItem : ObservableObject;

// ── User message ─────────────────────────────────────

public partial class UserMessageItem : TranscriptItem
{
    private readonly ChatMessageViewModel _source;
    private readonly Action<ChatMessage>? _resendAction;

    [ObservableProperty] private string _content;
    [ObservableProperty] private string _timestampText;

    public string? Author => _source.Author;
    public ChatMessage Message => _source.Message;
    public List<FileAttachmentItem> Attachments { get; }
    public List<SkillReference> Skills { get; }
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasSkills => Skills.Count > 0;

    /// <summary>Command invoked when user clicks Edit on the message. Sets EditText to current content.</summary>
    public ICommand BeginEditCommand { get; }

    /// <summary>Command invoked when user confirms an edit. Parameter is the new text string.</summary>
    public ICommand ConfirmEditCommand { get; }

    /// <summary>Command invoked when user clicks Regenerate/Retry on the message.</summary>
    public ICommand ResendCommand { get; }

    public UserMessageItem(ChatMessageViewModel source, bool showTimestamps, Action<ChatMessage>? resendAction = null)
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

    public void ResendFromMessage() => _resendAction?.Invoke(_source.Message);

    public void EditAndResend(string newContent)
    {
        _source.Message.Content = newContent;
        _source.NotifyContentChanged();
        Content = newContent;
        _resendAction?.Invoke(_source.Message);
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
    [ObservableProperty] private bool _hasFileChanges;
    [ObservableProperty] private string _sourcesLabel = "";
    [ObservableProperty] private string _fileChangesLabel = "";

    public string? Author => _source.Author;
    public ObservableCollection<SkillReference> Skills { get; } = [];
    public ObservableCollection<FileAttachmentItem> FileAttachments { get; } = [];
    public ObservableCollection<SourceItem> Sources { get; } = [];
    public ObservableCollection<FileChangeItem> FileChanges { get; } = [];

    public AssistantMessageItem(ChatMessageViewModel source, bool showTimestamps)
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
            Content = _source.Content;
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
        List<FileChangeItem>? fileChanges)
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

        // File changes
        if (fileChanges is { Count: > 0 })
        {
            foreach (var fc in fileChanges)
                FileChanges.Add(fc);
        }
        HasFileChanges = FileChanges.Count > 0;
        FileChangesLabel = FileChanges.Count == 1 ? Loc.FileChanges_One : string.Format(Loc.FileChanges_N, FileChanges.Count);
    }
}

// ── Error message ────────────────────────────────────

public partial class ErrorMessageItem : TranscriptItem
{
    [ObservableProperty] private string _content;
    [ObservableProperty] private string _timestampText;

    public string? Author { get; }

    public ErrorMessageItem(ChatMessageViewModel source, bool showTimestamps)
    {
        Author = source.Author;
        _content = source.Content;
        _timestampText = showTimestamps ? source.TimestampText : "";
    }

    public ErrorMessageItem(string content, string? author = null)
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
            Content = _source.Content;
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

    public ToolGroupItem(string label)
    {
        _label = label;
    }
}

// ── Base for items inside a tool group ───────────────

public abstract partial class ToolCallItemBase : ObservableObject;

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

    public string? DiffFilePath { get; init; }
    public string? DiffOldText { get; init; }
    public string? DiffNewText { get; init; }
    public Action<string, string?, string?>? ShowDiffAction { get; init; }

    public ToolCallItem(string toolName, StrataAiToolCallStatus status)
    {
        _toolName = toolName;
        _status = status;
    }

    [RelayCommand]
    private void ShowDiff()
    {
        if (DiffFilePath is not null)
            ShowDiffAction?.Invoke(DiffFilePath, DiffOldText, DiffNewText);
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

    public TerminalPreviewItem(string toolName, string command, StrataAiToolCallStatus status)
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

    public TodoProgressItem(string toolName, StrataAiToolCallStatus status)
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
    [ObservableProperty] private string? _selectedAnswer;
    [ObservableProperty] private bool _isAnswered;

    public QuestionItem(string questionId, string question, string options, bool allowFreeText,
        Action<string, string>? submitAction = null)
    {
        QuestionId = questionId;
        _question = question;
        _options = options;
        _allowFreeText = allowFreeText;
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

    public TurnSummaryItem(string label)
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

    private readonly string? _oldText;
    private readonly string? _newText;
    private readonly Action<string, string?, string?>? _showDiffAction;

    public FileChangeItem(string filePath, string toolName, string? oldText, string? newText,
        Action<string, string?, string?>? showDiffAction = null)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Directory = Path.GetDirectoryName(filePath);
        _oldText = oldText;
        _newText = newText;
        _showDiffAction = showDiffAction;

        var isCreate = ToolDisplayHelper.IsFileCreateTool(toolName);
        ActionIcon = isCreate ? "📄" : "📝";
        ActionLabel = isCreate ? Loc.FileChange_Created : Loc.FileChange_Modified;
    }

    [RelayCommand]
    private void ShowDiff() => _showDiffAction?.Invoke(FilePath, _oldText, _newText);
}
