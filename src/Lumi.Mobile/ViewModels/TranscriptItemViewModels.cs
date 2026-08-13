using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.ViewModels;

/// <summary>
/// Base for one renderable row. Concrete subclasses exist per kind so the transcript can use plain
/// <c>DataTemplate DataType=</c> matching instead of a hand-rolled template selector.
/// </summary>
public abstract partial class TranscriptItemViewModel : ObservableObject, IDisposable
{
    protected TranscriptItemViewModel(RemoteTranscriptItem item)
    {
        Id = item.Id;
        Kind = item.Kind;
    }

    public string Id { get; }

    public string Kind { get; }

    /// <summary>Applies a newer server projection of the same row in place, keeping the control alive.</summary>
    public abstract void Update(RemoteTranscriptItem item);

    public virtual void Dispose()
    {
    }
}

public sealed partial class UserTurnItemViewModel : TranscriptItemViewModel
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _author = "You";
    [ObservableProperty] private DateTimeOffset? _timestamp;
    [ObservableProperty] private string? _steerState;

    public ObservableCollection<RemoteAttachment> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    public bool HasSteerStatus => SteerState is "Queued" or "Steering" or "Steered" or "Failed";

    public string SteerStatusText => SteerState switch
    {
        "Queued" => "Queued...",
        "Steering" => "Steering...",
        "Steered" => "Steered",
        "Failed" => "Could not steer",
        _ => ""
    };

    public UserTurnItemViewModel(RemoteTranscriptItem item) : base(item) => Update(item);

    public override void Update(RemoteTranscriptItem item)
    {
        Text = item.Text ?? "";
        Author = string.IsNullOrWhiteSpace(item.Author) ? "You" : item.Author!;
        Timestamp = item.Timestamp;
        SteerState = item.SteerState;

        Attachments.Clear();
        foreach (var attachment in item.Attachments ?? [])
            Attachments.Add(attachment);

        OnPropertyChanged(nameof(HasAttachments));
    }

    partial void OnSteerStateChanged(string? value)
    {
        OnPropertyChanged(nameof(HasSteerStatus));
        OnPropertyChanged(nameof(SteerStatusText));
    }
}

public sealed partial class AssistantItemViewModel : TranscriptItemViewModel
{
    private readonly Action<AssistantItemViewModel>? _openSources;
    private readonly Func<
        string,
        string,
        IReadOnlyList<RemoteInlineImage>,
        CancellationToken,
        Task<string>>? _resolveInlineImages;
    private CancellationTokenSource? _imageResolutionCts;
    private long _imageResolutionVersion;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string? _model;

    internal string SourceText { get; private set; } = "";

    public ObservableCollection<RemoteSource> Sources { get; } = [];

    public bool HasSources => Sources.Count > 0;
    public string SelectionText => RemoteMarkdownImages.ToSelectionText(Text);
    public string SourceCountText => Sources.Count == 1 ? "1 source" : $"{Sources.Count} sources";
    public string SourceSummary
    {
        get
        {
            var hosts = Sources
                .Select(source => SourceHost(source.Url))
                .Where(static host => host.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToArray();
            return hosts.Length > 0 ? string.Join(" · ", hosts) : SourceCountText;
        }
    }

    public AssistantItemViewModel(
        RemoteTranscriptItem item,
        Action<AssistantItemViewModel>? openSources = null,
        Func<
            string,
            string,
            IReadOnlyList<RemoteInlineImage>,
            CancellationToken,
            Task<string>>? resolveInlineImages = null)
        : base(item)
    {
        _openSources = openSources;
        _resolveInlineImages = resolveInlineImages;
        Update(item);
    }

    public override void Update(RemoteTranscriptItem item)
    {
        CancelImageResolution();
        var sourceText = item.Text ?? "";
        SourceText = sourceText;
        Text = sourceText;
        IsStreaming = item.IsStreaming;
        Model = item.Model;

        Sources.Clear();
        foreach (var source in item.Sources ?? [])
            Sources.Add(source);
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(SourceCountText));
        OnPropertyChanged(nameof(SourceSummary));

        if (!item.IsStreaming
            && item.InlineImages is { Count: > 0 }
            && _resolveInlineImages is not null)
        {
            var version = Interlocked.Increment(ref _imageResolutionVersion);
            var current = new CancellationTokenSource();
            _imageResolutionCts = current;
            _ = ResolveInlineImagesAsync(
                sourceText,
                item.InlineImages,
                version,
                current);
        }
    }

    internal void ApplyStreamText(string sourceText)
    {
        CancelImageResolution();
        SourceText = sourceText;
        Text = sourceText;
        IsStreaming = true;
    }

    public override void Dispose() => CancelImageResolution();

    partial void OnTextChanged(string value) =>
        OnPropertyChanged(nameof(SelectionText));

    private void CancelImageResolution()
    {
        Interlocked.Increment(ref _imageResolutionVersion);
        var previous = Interlocked.Exchange(ref _imageResolutionCts, null);
        previous?.Cancel();
    }

    private async Task ResolveInlineImagesAsync(
        string sourceText,
        IReadOnlyList<RemoteInlineImage> images,
        long version,
        CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            var resolved = await _resolveInlineImages!(
                Id,
                sourceText,
                images,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            void ApplyResolvedText()
            {
                if (version == Volatile.Read(ref _imageResolutionVersion)
                    && string.Equals(SourceText, sourceText, StringComparison.Ordinal))
                {
                    Text = resolved;
                }
            }

            if (Dispatcher.UIThread.CheckAccess())
                ApplyResolvedText();
            else
                Dispatcher.UIThread.Post(ApplyResolvedText);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Mobile] Could not resolve inline markdown images: {ex}");
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _imageResolutionCts,
                null,
                cancellation);
            cancellation.Dispose();
        }
    }

    [RelayCommand]
    private void OpenSources() => _openSources?.Invoke(this);

    private static string SourceHost(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)
            || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return "";
        }

        return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;
    }
}

public sealed partial class ReasoningItemViewModel : TranscriptItemViewModel
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _label = "Thinking";
    [ObservableProperty] private bool _isStreaming;

    public ReasoningItemViewModel(RemoteTranscriptItem item) : base(item) => Update(item);

    public override void Update(RemoteTranscriptItem item)
    {
        Text = item.Text ?? "";
        Label = string.IsNullOrWhiteSpace(item.Label) ? "Thinking" : item.Label!;
        IsStreaming = item.IsStreaming;
    }
}

public sealed partial class ToolCallViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string? _input;
    [ObservableProperty] private string? _output;
    [ObservableProperty] private string _status = "Completed";
    [ObservableProperty] private double? _durationMs;

    public string Id { get; private set; } = "";

    public bool IsRunning => Status == "InProgress";
    public bool IsSucceeded => Status == "Completed";
    public bool IsFailed => Status == "Failed";
    public bool IsStopped => Status == "Stopped";

    public ToolCallViewModel(RemoteToolCall tool) => Update(tool);

    public void Update(RemoteToolCall tool)
    {
        Id = tool.Id;
        Name = tool.Name;
        DisplayName = string.IsNullOrWhiteSpace(tool.DisplayName) ? tool.Name : tool.DisplayName!;
        Input = tool.Input;
        Output = tool.Output;
        Status = tool.Status;
        DurationMs = tool.DurationMs;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsSucceeded));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsStopped));
    }
}

public sealed partial class ActivityStepViewModel : ObservableObject
{
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _status = "Completed";
    [ObservableProperty] private string? _input;
    [ObservableProperty] private string? _output;
    [ObservableProperty] private bool _showTechnicalDetails;

    public string Id { get; private set; } = "";
    public string Category { get; private set; } = "other";
    public double? DurationMs { get; private set; }

    public bool IsRunning => Status == "InProgress";
    public bool IsSucceeded => Status == "Completed";
    public bool IsFailed => Status == "Failed";
    public bool IsStopped => Status == "Stopped";
    public bool HasTechnicalDetails =>
        !string.IsNullOrWhiteSpace(Input) || !string.IsNullOrWhiteSpace(Output);
    public string DurationText => FormatDuration(DurationMs);

    public ActivityStepViewModel(RemoteToolCall tool) => Update(tool);

    public void Update(RemoteToolCall tool)
    {
        Id = tool.Id;
        Category = string.IsNullOrWhiteSpace(tool.Category) ? "other" : tool.Category;
        DisplayName = string.IsNullOrWhiteSpace(tool.DisplayName) ? tool.Name : tool.DisplayName!;
        Status = tool.Status;
        Input = tool.Input;
        Output = tool.Output;
        DurationMs = tool.DurationMs;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsSucceeded));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(HasTechnicalDetails));
        OnPropertyChanged(nameof(DurationText));
    }

    internal static string FormatDuration(double? durationMs)
    {
        if (durationMs is null or <= 0)
            return "";

        var totalSeconds = Math.Max(1, (int)Math.Round(durationMs.Value / 1000));
        return totalSeconds < 60
            ? $"{totalSeconds}s"
            : $"{totalSeconds / 60}m {totalSeconds % 60}s";
    }
}

public sealed class ActivitySectionViewModel
{
    public ActivitySectionViewModel(string category, IEnumerable<ActivityStepViewModel> steps)
    {
        Category = category;
        Label = category switch
        {
            "research" => "Researched",
            "work" => "Implemented",
            "verify" => "Verified",
            _ => "Worked"
        };
        Steps = new ObservableCollection<ActivityStepViewModel>(steps);
    }

    public string Category { get; }
    public string Label { get; }
    public ObservableCollection<ActivityStepViewModel> Steps { get; }
    public string CountText => Steps.Count == 1 ? "1 action" : $"{Steps.Count} actions";
}

public sealed class ActivityFileChangeViewModel
{
    public ActivityFileChangeViewModel(RemoteFileChange change)
    {
        Path = change.Path;
        FileName = change.FileName;
        Operation = change.Operation;
        LinesAdded = Math.Max(0, change.LinesAdded);
        LinesRemoved = Math.Max(0, change.LinesRemoved);
    }

    public string Path { get; }
    public string FileName { get; }
    public string Operation { get; }
    public int LinesAdded { get; }
    public int LinesRemoved { get; }
    public string StatsText
    {
        get
        {
            var parts = new List<string>(2);
            if (LinesAdded > 0)
                parts.Add($"+{LinesAdded}");
            if (LinesRemoved > 0)
                parts.Add($"-{LinesRemoved}");
            return string.Join(" ", parts);
        }
    }
    public bool HasStats => LinesAdded > 0 || LinesRemoved > 0;
}

/// <summary>
/// One conversation-level disclosure for all technical work in a turn. The normal transcript stays
/// conversational; raw tool input/output is loaded only when the user opens this row.
/// </summary>
public sealed partial class ActivitySummaryItemViewModel : TranscriptItemViewModel
{
    private readonly Func<ActivitySummaryItemViewModel, Task>? _openAction;
    private long _detailsVersion;

    [ObservableProperty] private string _activityId = "";
    [ObservableProperty] private string _label = "Working...";
    [ObservableProperty] private string _status = "Completed";
    [ObservableProperty] private int _actionCount;
    [ObservableProperty] private double? _durationMs;
    [ObservableProperty] private long? _remoteDetailVersion;
    [ObservableProperty] private int _totalFileChangeCount;
    [ObservableProperty] private bool _isLoadingDetails;
    [ObservableProperty] private bool _detailsLoaded;
    [ObservableProperty] private string? _detailsError;
    [ObservableProperty] private bool _isTechnicalDetailsVisible;

    public ObservableCollection<ActivityFileChangeViewModel> FileChanges { get; } = [];
    public ObservableCollection<ActivityFileChangeViewModel> PreviewFileChanges { get; } = [];
    public ObservableCollection<ActivitySectionViewModel> Sections { get; } = [];
    internal long DetailsVersion => Volatile.Read(ref _detailsVersion);

    public bool IsRunning => Status == "InProgress";
    public bool IsSucceeded => Status == "Completed";
    public bool IsFailed => Status == "Failed";
    public bool IsStopped => Status == "Stopped";
    public bool HasFileChanges => FileChanges.Count > 0;
    public bool HasSections => Sections.Count > 0;
    public bool CanShowTechnicalDetails =>
        Sections.SelectMany(section => section.Steps).Any(step => step.HasTechnicalDetails);
    public string FileSummary =>
        TotalFileChangeCount == 1 ? "1 file changed" : $"{TotalFileChangeCount} files changed";
    public bool HasAdditionalFileChanges => TotalFileChangeCount > PreviewFileChanges.Count;
    public string AdditionalFileChangesText =>
        HasAdditionalFileChanges
            ? $"+{TotalFileChangeCount - PreviewFileChanges.Count} more"
            : "";
    public string SummaryText
    {
        get
        {
            if (IsRunning)
                return string.IsNullOrWhiteSpace(Label) ? "Working..." : Label;

            var duration = ActivityStepViewModel.FormatDuration(DurationMs);
            var actions = ActionCount switch
            {
                <= 0 => "",
                1 => "1 action",
                _ => $"{ActionCount} actions"
            };
            var prefix = Status switch
            {
                "Failed" => "Finished with an issue",
                "Stopped" => "Stopped",
                _ => "Worked"
            };

            if (duration.Length > 0)
                return actions.Length > 0
                    ? $"{prefix} for {duration} · {actions}"
                    : $"{prefix} for {duration}";
            if (actions.Length > 0)
                return $"{prefix} · {actions}";
            return HasFileChanges ? FileSummary : prefix;
        }
    }

    public string TechnicalDetailsLabel =>
        IsTechnicalDetailsVisible ? "Hide technical details" : "Show technical details";

    public ActivitySummaryItemViewModel(
        RemoteTranscriptItem item,
        Func<ActivitySummaryItemViewModel, Task>? openAction = null)
        : base(item)
    {
        _openAction = openAction;
        Update(item);
    }

    public override void Update(RemoteTranscriptItem item)
    {
        var detailsChanged =
            ActionCount != Math.Max(0, item.ActionCount ?? 0)
            || !string.Equals(Status, item.Status ?? "Completed", StringComparison.Ordinal)
            || DurationMs != item.DurationMs
            || RemoteDetailVersion != item.DetailVersion
            || TotalFileChangeCount != (item.FileChangeCount ?? item.FileChanges?.Count ?? 0)
            || !FileChangesMatch(item.FileChanges);

        ActivityId = item.ActivityId ?? item.Id;
        Label = string.IsNullOrWhiteSpace(item.Label) ? "Working..." : item.Label!;
        Status = item.Status ?? "Completed";
        ActionCount = Math.Max(0, item.ActionCount ?? 0);
        DurationMs = item.DurationMs;
        RemoteDetailVersion = item.DetailVersion;
        TotalFileChangeCount = Math.Max(
            0,
            item.FileChangeCount ?? item.FileChanges?.Count ?? 0);

        FileChanges.Clear();
        foreach (var change in item.FileChanges ?? [])
            FileChanges.Add(new ActivityFileChangeViewModel(change));
        PreviewFileChanges.Clear();
        foreach (var change in FileChanges.Take(3))
            PreviewFileChanges.Add(change);

        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsSucceeded));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(HasFileChanges));
        OnPropertyChanged(nameof(FileSummary));
        OnPropertyChanged(nameof(HasAdditionalFileChanges));
        OnPropertyChanged(nameof(AdditionalFileChangesText));
        OnPropertyChanged(nameof(SummaryText));

        if (detailsChanged && DetailsLoaded)
        {
            DetailsLoaded = false;
            Sections.Clear();
            OnPropertyChanged(nameof(HasSections));
            OnPropertyChanged(nameof(CanShowTechnicalDetails));
        }
        if (detailsChanged)
            Interlocked.Increment(ref _detailsVersion);
    }

    private bool FileChangesMatch(IReadOnlyList<RemoteFileChange>? incoming)
    {
        if (FileChanges.Count != (incoming?.Count ?? 0))
            return false;
        if (incoming is null)
            return true;

        for (var index = 0; index < incoming.Count; index++)
        {
            var current = FileChanges[index];
            var next = incoming[index];
            if (!string.Equals(current.Path, next.Path, StringComparison.Ordinal)
                || !string.Equals(current.Operation, next.Operation, StringComparison.Ordinal)
                || current.LinesAdded != next.LinesAdded
                || current.LinesRemoved != next.LinesRemoved)
            {
                return false;
            }
        }

        return true;
    }

    public void ApplyDetails(RemoteActivityDetails details)
    {
        Sections.Clear();
        foreach (var category in new[] { "research", "work", "verify", "other" })
        {
            var steps = details.Tools
                .Where(tool => string.Equals(
                    string.IsNullOrWhiteSpace(tool.Category) ? "other" : tool.Category,
                    category,
                    StringComparison.Ordinal))
                .Select(tool =>
                {
                    var step = new ActivityStepViewModel(tool)
                    {
                        ShowTechnicalDetails = IsTechnicalDetailsVisible
                    };
                    return step;
                })
                .ToList();
            if (steps.Count > 0)
                Sections.Add(new ActivitySectionViewModel(category, steps));
        }

        DetailsError = null;
        DetailsLoaded = true;
        OnPropertyChanged(nameof(HasSections));
        OnPropertyChanged(nameof(CanShowTechnicalDetails));
    }

    [RelayCommand]
    private Task OpenAsync() => _openAction?.Invoke(this) ?? Task.CompletedTask;

    [RelayCommand]
    private void ToggleTechnicalDetails()
    {
        IsTechnicalDetailsVisible = !IsTechnicalDetailsVisible;
        foreach (var step in Sections.SelectMany(section => section.Steps))
            step.ShowTechnicalDetails = IsTechnicalDetailsVisible;
        OnPropertyChanged(nameof(TechnicalDetailsLabel));
    }

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsSucceeded));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(SummaryText));
    }

    partial void OnLabelChanged(string value) => OnPropertyChanged(nameof(SummaryText));
    partial void OnActionCountChanged(int value) => OnPropertyChanged(nameof(SummaryText));
    partial void OnDurationMsChanged(double? value) => OnPropertyChanged(nameof(SummaryText));
    partial void OnIsTechnicalDetailsVisibleChanged(bool value) =>
        OnPropertyChanged(nameof(TechnicalDetailsLabel));
}

public sealed partial class ToolGroupItemViewModel : TranscriptItemViewModel
{
    [ObservableProperty] private string _label = "Working";
    [ObservableProperty] private bool _isRunning;

    public ObservableCollection<ToolCallViewModel> Tools { get; } = [];

    public ToolGroupItemViewModel(RemoteTranscriptItem item) : base(item) => Update(item);

    public override void Update(RemoteTranscriptItem item)
    {
        Label = string.IsNullOrWhiteSpace(item.Label) ? "Working" : item.Label!;

        var incoming = item.Tools ?? [];
        for (var i = 0; i < incoming.Count; i++)
        {
            if (i < Tools.Count)
                Tools[i].Update(incoming[i]);
            else
                Tools.Add(new ToolCallViewModel(incoming[i]));
        }

        while (Tools.Count > incoming.Count)
            Tools.RemoveAt(Tools.Count - 1);

        IsRunning = Tools.Any(t => t.IsRunning);
    }
}

public sealed partial class TerminalItemViewModel : TranscriptItemViewModel
{
    [ObservableProperty] private string _command = "";
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private string _status = "Completed";
    [ObservableProperty] private double? _durationMs;
    [ObservableProperty] private string _toolName = "terminal";

    public TerminalItemViewModel(RemoteTranscriptItem item) : base(item) => Update(item);

    public override void Update(RemoteTranscriptItem item)
    {
        var tool = item.Tools is { Count: > 0 } tools ? tools[0] : null;
        Command = tool?.Input ?? item.Text ?? "";
        Output = tool?.Output ?? "";
        Status = tool?.Status ?? item.Status ?? "Completed";
        DurationMs = tool?.DurationMs ?? item.DurationMs;
        ToolName = string.IsNullOrWhiteSpace(item.Label) ? tool?.Name ?? "terminal" : item.Label!;
    }
}

public sealed partial class QuestionItemViewModel : TranscriptItemViewModel
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _allowFreeText = true;
    [ObservableProperty] private bool _allowMultiSelect;
    [ObservableProperty] private bool _isAnswered;
    [ObservableProperty] private string? _answer;

    public string QuestionId { get; private set; } = "";

    public ObservableCollection<string> Options { get; } = [];

    public QuestionItemViewModel(RemoteTranscriptItem item) : base(item) => Update(item);

    public override void Update(RemoteTranscriptItem item)
    {
        var question = item.Question;
        QuestionId = question?.QuestionId ?? item.Id;
        Text = question?.Text ?? item.Text ?? "";
        AllowFreeText = question?.AllowFreeText ?? true;
        AllowMultiSelect = question?.AllowMultiSelect ?? false;
        IsAnswered = question?.IsAnswered ?? false;
        Answer = question?.Answer;

        Options.Clear();
        foreach (var option in question?.Options ?? [])
            Options.Add(option);

    }
}

public sealed partial class ErrorItemViewModel : TranscriptItemViewModel
{
    [ObservableProperty] private string _text = "";

    public ErrorItemViewModel(RemoteTranscriptItem item) : base(item) => Update(item);

    public override void Update(RemoteTranscriptItem item) => Text = item.Text ?? "";
}

/// <summary>A file Lumi produced, surfaced as a tappable attachment chip.</summary>
public sealed partial class FileItemViewModel : TranscriptItemViewModel
{
    public FileItemViewModel(RemoteTranscriptItem item) : base(item) => Update(item);

    public ObservableCollection<RemoteAttachment> Files { get; } = [];

    public override void Update(RemoteTranscriptItem item)
    {
        Files.Clear();
        foreach (var file in item.Attachments ?? [])
            Files.Add(file);
    }
}

public static class TranscriptItemFactory
{
    public static TranscriptItemViewModel Create(
        RemoteTranscriptItem item,
        Func<ActivitySummaryItemViewModel, Task>? openActivity = null,
        Action<AssistantItemViewModel>? openSources = null,
        Func<
            string,
            string,
            IReadOnlyList<RemoteInlineImage>,
            CancellationToken,
            Task<string>>? resolveInlineImages = null) => item.Kind switch
    {
        RemoteProtocol.ItemKinds.User => new UserTurnItemViewModel(item),
        RemoteProtocol.ItemKinds.Activity => new ActivitySummaryItemViewModel(item, openActivity),
        RemoteProtocol.ItemKinds.Reasoning => new ReasoningItemViewModel(item),
        RemoteProtocol.ItemKinds.ToolGroup or RemoteProtocol.ItemKinds.Tool => new ToolGroupItemViewModel(item),
        RemoteProtocol.ItemKinds.Terminal => new TerminalItemViewModel(item),
        RemoteProtocol.ItemKinds.Question => new QuestionItemViewModel(item),
        RemoteProtocol.ItemKinds.Error => new ErrorItemViewModel(item),
        RemoteProtocol.ItemKinds.File => new FileItemViewModel(item),

        // Unknown kinds degrade to plain assistant text rather than breaking the transcript.
        _ => new AssistantItemViewModel(item, openSources, resolveInlineImages)
    };

    /// <summary>True when an existing row can be updated in place instead of being replaced.</summary>
    public static bool CanReuse(TranscriptItemViewModel existing, RemoteTranscriptItem item) =>
        existing.Id == item.Id && existing.Kind == item.Kind;
}

/// <summary>One user turn plus everything the assistant produced in response.</summary>
public sealed partial class TranscriptTurnViewModel : ObservableObject, IDisposable
{
    private readonly Func<ActivitySummaryItemViewModel, Task>? _openActivity;
    private readonly Action<AssistantItemViewModel>? _openSources;
    private readonly Func<
        string,
        string,
        IReadOnlyList<RemoteInlineImage>,
        CancellationToken,
        Task<string>>? _resolveInlineImages;

    public TranscriptTurnViewModel(
        string id,
        Func<ActivitySummaryItemViewModel, Task>? openActivity = null,
        Action<AssistantItemViewModel>? openSources = null,
        Func<
            string,
            string,
            IReadOnlyList<RemoteInlineImage>,
            CancellationToken,
            Task<string>>? resolveInlineImages = null)
    {
        Id = id;
        _openActivity = openActivity;
        _openSources = openSources;
        _resolveInlineImages = resolveInlineImages;
    }

    public string Id { get; }

    public ObservableCollection<TranscriptItemViewModel> Items { get; } = [];

    public void Apply(RemoteTranscriptTurn turn)
    {
        for (var i = 0; i < turn.Items.Count; i++)
        {
            var incoming = turn.Items[i];

            if (i < Items.Count && TranscriptItemFactory.CanReuse(Items[i], incoming))
            {
                Items[i].Update(incoming);
                continue;
            }

            var created = TranscriptItemFactory.Create(
                incoming,
                _openActivity,
                _openSources,
                _resolveInlineImages);
            if (i < Items.Count)
            {
                var replaced = Items[i];
                Items[i] = created;
                replaced.Dispose();
            }
            else
                Items.Add(created);
        }

        while (Items.Count > turn.Items.Count)
        {
            var removed = Items[^1];
            Items.RemoveAt(Items.Count - 1);
            removed.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var item in Items)
            item.Dispose();
        Items.Clear();
    }
}
