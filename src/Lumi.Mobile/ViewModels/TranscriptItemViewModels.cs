using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
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
    private long? _entranceRequestedAt;

    protected TranscriptItemViewModel(RemoteTranscriptItem item)
    {
        Id = item.Id;
        Kind = item.Kind;
    }

    public string Id { get; }

    public string Kind { get; }

    internal void RequestEntranceAnimation() => _entranceRequestedAt = Stopwatch.GetTimestamp();

    internal bool TryConsumeEntranceAnimation()
    {
        var requestedAt = _entranceRequestedAt;
        _entranceRequestedAt = null;
        return requestedAt is { } timestamp
               && Stopwatch.GetElapsedTime(timestamp) <= TimeSpan.FromSeconds(2);
    }

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

    /// <summary>The time beside "You" over the bubble, as the desktop shows it.</summary>
    public string TimestampText => TranscriptTime.Format(Timestamp);

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
        var content = RemoteUserMessageContent.Parse(item.Text, item.Author);
        Text = content.Text;
        Author = string.IsNullOrWhiteSpace(item.Author) ? "You" : item.Author!;
        Timestamp = item.Timestamp;
        SteerState = item.SteerState;

        Attachments.Clear();
        foreach (var attachment in (item.Attachments ?? []).Concat(content.Attachments.Where(file =>
                     item.Attachments?.Any(existing => existing.Path == file.Path) != true)))
            Attachments.Add(attachment);

        OnPropertyChanged(nameof(HasAttachments));
    }

    partial void OnSteerStateChanged(string? value)
    {
        OnPropertyChanged(nameof(HasSteerStatus));
        OnPropertyChanged(nameof(SteerStatusText));
    }

    partial void OnTimestampChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(TimestampText));
}

/// <summary>A message's time as the desktop transcript writes it.</summary>
internal static class TranscriptTime
{
    public static string Format(DateTimeOffset? timestamp) =>
        timestamp?.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture) ?? "";
}

public sealed partial class JobWakeItemViewModel : TranscriptItemViewModel
{
    private readonly Action<JobWakeItemViewModel>? _open;
    [ObservableProperty] private RemoteJobWake _details = RemoteJobWake.Parse(null, "");
    [ObservableProperty] private string _timestampText = "";

    public string Summary => Details.WakeSignal.StartsWith('{') || Details.WakeSignal.StartsWith('[')
        ? "The job woke this chat."
        : Details.WakeSignal;
    public bool HasInstructions => Details.Instructions.Length > 0;
    public bool HasOutput => Details.OutputText.Length > 0;
    public bool HasExitCode => Details.ExitCode.Length > 0;
    public bool HasStarted => Details.StartedText.Length > 0;
    public bool HasCompleted => Details.CompletedText.Length > 0;

    public JobWakeItemViewModel(RemoteTranscriptItem item, Action<JobWakeItemViewModel>? open = null)
        : base(item)
    {
        _open = open;
        Update(item);
    }

    public override void Update(RemoteTranscriptItem item)
    {
        Details = RemoteJobWake.Parse(item.Author, item.Text ?? "");
        TimestampText = item.Timestamp?.ToLocalTime().ToString("t") ?? "";
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasInstructions));
        OnPropertyChanged(nameof(HasOutput));
        OnPropertyChanged(nameof(HasExitCode));
        OnPropertyChanged(nameof(HasStarted));
        OnPropertyChanged(nameof(HasCompleted));
    }

    [RelayCommand]
    private void Open() => _open?.Invoke(this);
}

public sealed partial class AssistantItemViewModel : TranscriptItemViewModel
{
    private readonly Dictionary<string, string> _pendingInteractionStates = [];
    [ObservableProperty] private IReadOnlyDictionary<string, string>? _interactionStates;
    [ObservableProperty] private string _interactionLanguage = "en";

    internal void StageInteractionState(string blockId, string json)
    {
        _pendingInteractionStates[blockId] = json;
        var values = InteractionStates is null ? new Dictionary<string, string>()
            : new Dictionary<string, string>(InteractionStates);
        values[blockId] = json;
        InteractionStates = values;
    }

    internal void ConfirmInteractionState(string blockId, string json)
    {
        if (_pendingInteractionStates.GetValueOrDefault(blockId) == json)
            _pendingInteractionStates.Remove(blockId);
    }

    private readonly Action<AssistantItemViewModel>? _openSources;
    private readonly Func<string, string?>? _displayModel;
    private readonly Func<
        string,
        string,
        IReadOnlyList<RemoteInlineImage>,
        CancellationToken,
        Task<string>>? _resolveInlineImages;
    private readonly Action<string, IReadOnlyList<RemoteInlineImage>>? _releaseInlineImages;
    private CancellationTokenSource? _imageResolutionCts;
    private IReadOnlyList<RemoteInlineImage>? _inlineImages;
    private IReadOnlyList<RemoteInlineImage>? _leasedInlineImages;
    private long _imageResolutionVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAnswerActions))]
    private string _text = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAnswerActions))]
    private bool _isStreaming;
    [ObservableProperty] private string? _model;
    [ObservableProperty] private DateTimeOffset? _timestamp;

    /// <summary>The settled reply that closes its turn; only it carries Copy and Share.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAnswerActions))]
    private bool _isFinalAnswer;

    internal string SourceText { get; private set; } = "";

    public ObservableCollection<RemoteSource> Sources { get; } = [];

    /// <summary>Files Lumi made in the turn this answer closes, shown with it as on the desktop.</summary>
    public ObservableCollection<RemoteAttachment> Files { get; } = [];

    public bool HasFiles => Files.Count > 0;

    internal void ShowFiles(IReadOnlyList<RemoteAttachment> files)
    {
        if (Files.Count == files.Count
            && Files.Zip(files).All(static pair => pair.First.MessageId == pair.Second.MessageId
                                                   && pair.First.FileName == pair.Second.FileName))
            return;

        Files.Clear();
        foreach (var file in files)
            Files.Add(file);
        OnPropertyChanged(nameof(HasFiles));
    }

    public bool HasSources => Sources.Count > 0;
    public bool ShowAnswerActions => IsFinalAnswer && !IsStreaming && !string.IsNullOrWhiteSpace(Text);

    /// <summary>Which model answered and when, beside the answer's actions: "Claude Opus 5.5 · 16:41".</summary>
    public string FooterText
    {
        get
        {
            var model = string.IsNullOrWhiteSpace(Model) ? null : _displayModel?.Invoke(Model) ?? Model;
            var time = TranscriptTime.Format(Timestamp);
            return string.Join(" · ", new[] { model, time }.Where(static part => !string.IsNullOrWhiteSpace(part)));
        }
    }

    public bool CanShare => Services.MobilePlatformServices.TextShare.IsAvailable;
    public string SelectionText => RemoteMarkdownImages.ToSelectionText(Text);
    public string SourceCountText => Sources.Count == 1 ? "1 source" : $"{Sources.Count} sources";

    /// <summary>The sites the sources come from, beside the sources chip's count.</summary>
    public string SourceDomains => string.Join(" · ", Sources
        .Select(source => SourceHost(source.Url))
        .Where(static host => host.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(3));

    public AssistantItemViewModel(
        RemoteTranscriptItem item,
        Action<AssistantItemViewModel>? openSources = null,
        Func<
            string,
            string,
            IReadOnlyList<RemoteInlineImage>,
            CancellationToken,
            Task<string>>? resolveInlineImages = null,
        Action<string, IReadOnlyList<RemoteInlineImage>>? releaseInlineImages = null,
        Func<string, string?>? displayModel = null)
        : base(item)
    {
        _openSources = openSources;
        _resolveInlineImages = resolveInlineImages;
        _releaseInlineImages = releaseInlineImages;
        _displayModel = displayModel;
        Update(item);
    }

    public override void Update(RemoteTranscriptItem item)
    {
        var sourceText = item.Text ?? "";
        // A transcript captured before deltas this phone already applied must not shorten a
        // still-streaming answer: what the phone shows is a strict extension of it.
        if (IsStreaming
            && item.IsStreaming
            && SourceText.Length > sourceText.Length
            && SourceText.StartsWith(sourceText, StringComparison.Ordinal))
        {
            sourceText = SourceText;
        }

        var images = !item.IsStreaming && item.InlineImages is { Count: > 0 }
            ? item.InlineImages
            : null;
        var retainImages = string.Equals(SourceText, sourceText, StringComparison.Ordinal)
                           && IsStreaming == item.IsStreaming
                           && HaveSameInlineImages(_inlineImages, images);
        if (!retainImages)
        {
            CancelImageResolution();
            ReleaseInlineImages();
            SourceText = sourceText;
            Text = sourceText;
            _inlineImages = images?.Select(image => new RemoteInlineImage
            {
                Index = image.Index,
                FileName = image.FileName
            }).ToArray();
        }
        IsStreaming = item.IsStreaming;
        Model = item.Model;
        Timestamp = item.Timestamp;
        // The chat may learn its models' names after the answer arrived.
        OnPropertyChanged(nameof(FooterText));
        InteractionLanguage = item.InteractionLanguage ?? "en";
        var states = item.InteractionStates is null ? new Dictionary<string, string>()
            : new Dictionary<string, string>(item.InteractionStates);
        foreach (var pending in _pendingInteractionStates.ToArray())
        {
            if (states.GetValueOrDefault(pending.Key) == pending.Value)
                _pendingInteractionStates.Remove(pending.Key);
            else
                states[pending.Key] = pending.Value;
        }
        InteractionStates = states;

        Sources.Clear();
        foreach (var source in item.Sources ?? [])
            Sources.Add(source);
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(SourceCountText));
        OnPropertyChanged(nameof(SourceDomains));

        if (_inlineImages is { Count: > 0 }
            && _resolveInlineImages is not null
            && _imageResolutionCts is null
            && (_leasedInlineImages?.Count ?? 0) < _inlineImages.Count)
        {
            var pendingImages = _inlineImages.Where(image =>
                _leasedInlineImages?.Any(leased => leased.Index == image.Index) != true).ToArray();
            var version = Interlocked.Increment(ref _imageResolutionVersion);
            var current = new CancellationTokenSource();
            _imageResolutionCts = current;
            _ = ResolveInlineImagesAsync(
                sourceText,
                Text,
                pendingImages,
                version,
                current);
        }
    }

    internal void ApplyStreamText(string sourceText)
    {
        CancelImageResolution();
        ReleaseInlineImages();
        _inlineImages = null;
        SourceText = sourceText;
        Text = sourceText;
        IsStreaming = true;
    }

    public override void Dispose()
    {
        CancelImageResolution();
        ReleaseInlineImages();
        _inlineImages = null;
    }

    partial void OnTextChanged(string value) =>
        OnPropertyChanged(nameof(SelectionText));

    private void CancelImageResolution()
    {
        Interlocked.Increment(ref _imageResolutionVersion);
        var previous = Interlocked.Exchange(ref _imageResolutionCts, null);
        previous?.Cancel();
    }

    private static bool HaveSameInlineImages(
        IReadOnlyList<RemoteInlineImage>? current,
        IReadOnlyList<RemoteInlineImage>? incoming)
    {
        if (current is null || incoming is null)
            return current is null && incoming is null;
        if (current.Count != incoming.Count)
            return false;
        for (var index = 0; index < current.Count; index++)
        {
            if (current[index].Index != incoming[index].Index
                || !string.Equals(current[index].FileName, incoming[index].FileName, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private async Task ResolveInlineImagesAsync(
        string sourceText,
        string renderText,
        IReadOnlyList<RemoteInlineImage> images,
        long version,
        CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        var leaseTransferred = false;
        try
        {
            var resolved = await _resolveInlineImages!(
                Id,
                renderText,
                images,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;
            var previousTargets = RemoteMarkdownImages.Find(renderText)
                .ToDictionary(image => image.Index, image => image.Target);
            var resolvedTargets = RemoteMarkdownImages.Find(resolved)
                .ToDictionary(image => image.Index, image => image.Target);
            var resolvedImages = images.Where(image =>
                previousTargets.TryGetValue(image.Index, out var previousTarget)
                && resolvedTargets.TryGetValue(image.Index, out var resolvedTarget)
                && !string.Equals(previousTarget, resolvedTarget, StringComparison.Ordinal)).ToArray();
            if (resolvedImages.Length == 0)
            {
                Trace.TraceWarning($"[Mobile] No inline images could be resolved for message {Id}.");
                return;
            }

            void ApplyResolvedText()
            {
                if (version == Volatile.Read(ref _imageResolutionVersion)
                    && string.Equals(SourceText, sourceText, StringComparison.Ordinal))
                {
                    Text = resolved;
                    _leasedInlineImages = (_leasedInlineImages ?? []).Concat(resolvedImages).ToArray();
                    leaseTransferred = true;
                }
            }

            if (Dispatcher.UIThread.CheckAccess())
                ApplyResolvedText();
            else
                await Dispatcher.UIThread.InvokeAsync(ApplyResolvedText);
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
            if (!leaseTransferred)
                _releaseInlineImages?.Invoke(Id, images);
            Interlocked.CompareExchange(
                ref _imageResolutionCts,
                null,
                cancellation);
            cancellation.Dispose();
        }
    }

    private void ReleaseInlineImages()
    {
        if (_leasedInlineImages is not { } images)
            return;

        _leasedInlineImages = null;
        _releaseInlineImages?.Invoke(Id, images);
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _text = "";
    [ObservableProperty] private string _label = "Thinking";
    [ObservableProperty] private bool _isStreaming;

    /// <summary>The thought's first line, beside the chip's label as on the desktop.</summary>
    public string Summary
    {
        get
        {
            var text = Text.TrimStart();
            var end = text.IndexOfAny(['\r', '\n']);
            var first = (end >= 0 ? text[..end] : text).Trim().TrimStart('#', '*', '-', ' ');
            return first.Length > 120 ? first[..120] : first;
        }
    }

    public ReasoningItemViewModel(RemoteTranscriptItem item) : base(item) => Update(item);

    public override void Update(RemoteTranscriptItem item)
    {
        var text = item.Text ?? "";
        // Same rule as answers: an older capture never shortens reasoning that is still streaming.
        if (IsStreaming
            && item.IsStreaming
            && Text.Length > text.Length
            && Text.StartsWith(text, StringComparison.Ordinal))
        {
            text = Text;
        }

        Text = text;
        Label = string.IsNullOrWhiteSpace(item.Label) ? "Thinking" : item.Label!;
        IsStreaming = item.IsStreaming;
    }
}

public sealed partial class ToolCallViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string? _iconKey;
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
        IconKey = tool.Icon;
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
    [ObservableProperty] private string? _iconKey;
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
        IconKey = tool.Icon;
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

    /// <summary>The folder the file lives in, quieter beside its name.</summary>
    public string Directory
    {
        get
        {
            var separator = Path.LastIndexOfAny(['/', '\\']);
            return separator > 0 ? Path[..separator] : "";
        }
    }

    /// <summary>The desktop's icon for what happened to the file.</summary>
    public string IconKey => Operation switch
    {
        "Created" => "create",
        "Deleted" => "deleted",
        _ => "edit"
    };

    public bool HasLinesAdded => LinesAdded > 0;
    public bool HasLinesRemoved => LinesRemoved > 0;
    public string LinesAddedText => $"+{LinesAdded}";
    public string LinesRemovedText => $"\u2212{LinesRemoved}";
    public bool HasStats => LinesAdded > 0 || LinesRemoved > 0;
}

/// <summary>
/// One contiguous segment of technical work. Raw tool input/output is loaded only when opened.
/// </summary>
public sealed partial class ActivitySummaryItemViewModel : TranscriptItemViewModel
{
    private readonly Func<ActivitySummaryItemViewModel, Task>? _openAction;
    private long _detailsVersion;

    [ObservableProperty] private string _activityId = "";
    [ObservableProperty] private string _label = "Working...";
    [ObservableProperty] private string? _iconKey;
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

    /// <summary>The chip's label: what is happening now, or how the work went.</summary>
    public string ChipLabel
    {
        get
        {
            if (IsRunning)
                return string.IsNullOrWhiteSpace(Label) ? "Working..." : Label;

            var duration = ActivityStepViewModel.FormatDuration(DurationMs);
            var prefix = Status switch
            {
                "Failed" => "Finished with an issue",
                "Stopped" => "Stopped",
                _ => "Worked"
            };
            if (duration.Length > 0)
                return $"{prefix} for {duration}";
            if (ActionCount <= 0 && HasFileChanges)
                return FileSummary;
            // Work without steps is the model thinking; the desktop names it ("Thought through the response").
            return ActionCount <= 0 && Label is { Length: > 0 } label && label != "Activity" && !label.EndsWith("...", StringComparison.Ordinal)
                ? label
                : prefix;
        }
    }

    /// <summary>How many steps, in the chip's quieter voice beside its label.</summary>
    public string? ChipMeta => ActionCount switch
    {
        <= 0 => null,
        1 => "1 action",
        _ => $"{ActionCount} actions"
    };

    public int LinesAdded => FileChanges.Sum(static change => change.LinesAdded);
    public int LinesRemoved => FileChanges.Sum(static change => change.LinesRemoved);
    public bool HasLineStats => LinesAdded > 0 || LinesRemoved > 0;
    public bool HasLinesRemoved => LinesRemoved > 0;
    public string LinesAddedText => $"+{LinesAdded}";
    public string LinesRemovedText => $"\u2212{LinesRemoved}";

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
        // Work without steps is the model thinking; a host predating icons sends none.
        IconKey = item.Icon ?? (item.ActionCount is null or <= 0 ? "think" : null);
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

        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsSucceeded));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(HasFileChanges));
        OnPropertyChanged(nameof(FileSummary));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ChipLabel));
        OnPropertyChanged(nameof(ChipMeta));
        OnPropertyChanged(nameof(LinesAdded));
        OnPropertyChanged(nameof(LinesRemoved));
        OnPropertyChanged(nameof(HasLineStats));
        OnPropertyChanged(nameof(HasLinesRemoved));
        OnPropertyChanged(nameof(LinesAddedText));
        OnPropertyChanged(nameof(LinesRemovedText));

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
        var sections = new List<ActivitySectionViewModel>();
        foreach (var tool in details.Tools)
        {
            var category = string.IsNullOrWhiteSpace(tool.Category) ? "other" : tool.Category;
            var step = new ActivityStepViewModel(tool)
            {
                ShowTechnicalDetails = IsTechnicalDetailsVisible
            };
            // Categories are headings, not a sort order: reviewing work must not move a later
            // research action ahead of the edit or verification that preceded it.
            if (sections.Count > 0 && sections[^1].Category == category)
                sections[^1].Steps.Add(step);
            else
                sections.Add(new ActivitySectionViewModel(category, [step]));
        }
        foreach (var section in sections)
            Sections.Add(section);

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
        OnPropertyChanged(nameof(ChipLabel));
    }

    partial void OnLabelChanged(string value)
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ChipLabel));
    }

    partial void OnActionCountChanged(int value)
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ChipLabel));
        OnPropertyChanged(nameof(ChipMeta));
    }

    partial void OnDurationMsChanged(double? value)
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ChipLabel));
    }
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
    [ObservableProperty] private bool _hasAnswerDraft;

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
            Task<string>>? resolveInlineImages = null,
        Action<string, IReadOnlyList<RemoteInlineImage>>? releaseInlineImages = null,
        Action<JobWakeItemViewModel>? openJobWake = null,
        Func<string, string?>? displayModel = null) => item.Kind switch
    {
        RemoteProtocol.ItemKinds.User when RemoteJobWake.IsJobWake(item.Author, item.Text) =>
            new JobWakeItemViewModel(item, openJobWake),
        RemoteProtocol.ItemKinds.User when item.InteractionReceipt is not null => new InteractionReceiptItemViewModel(item),
        RemoteProtocol.ItemKinds.User => new UserTurnItemViewModel(item),
        RemoteProtocol.ItemKinds.Activity => new ActivitySummaryItemViewModel(item, openActivity),
        RemoteProtocol.ItemKinds.Reasoning => new ReasoningItemViewModel(item),
        RemoteProtocol.ItemKinds.ToolGroup or RemoteProtocol.ItemKinds.Tool => new ToolGroupItemViewModel(item),
        RemoteProtocol.ItemKinds.Terminal => new TerminalItemViewModel(item),
        RemoteProtocol.ItemKinds.Question => new QuestionItemViewModel(item),
        RemoteProtocol.ItemKinds.Input => new InputRequestItemViewModel(item),
        RemoteProtocol.ItemKinds.Error => new ErrorItemViewModel(item),
        RemoteProtocol.ItemKinds.File => new FileItemViewModel(item),

        // Unknown kinds degrade to plain assistant text rather than breaking the transcript.
        _ => new AssistantItemViewModel(
            item,
            openSources,
            resolveInlineImages,
            releaseInlineImages,
            displayModel)
    };

    /// <summary>True when an existing row can be updated in place instead of being replaced.</summary>
    public static bool CanReuse(TranscriptItemViewModel existing, RemoteTranscriptItem item) =>
        existing.Id == item.Id && existing.Kind == item.Kind
        && (existing is InteractionReceiptItemViewModel) == (item.InteractionReceipt is not null)
        && (existing is JobWakeItemViewModel) == (item.Kind == RemoteProtocol.ItemKinds.User
            && RemoteJobWake.IsJobWake(item.Author, item.Text));
}

/// <summary>
/// Finished work folded behind one line, Strata's turn summary: "Worked for 2m 14s · 6 actions". A
/// local disclosure; the wire and canonical items remain flat for streaming and paging.
/// </summary>
public sealed partial class WorkSummaryItemViewModel : TranscriptItemViewModel
{
    private readonly Action _expansionChanged;
    [ObservableProperty] private string _label = "Worked";
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _hasFailures;

    public ObservableCollection<TranscriptItemViewModel> Items { get; } = [];

    public WorkSummaryItemViewModel(RemoteTranscriptItem item, Action expansionChanged) : base(item)
    {
        _expansionChanged = expansionChanged;
        Update(item);
    }

    public override void Update(RemoteTranscriptItem item)
    {
        var duration = ActivityStepViewModel.FormatDuration(item.DurationMs);
        var label = duration.Length > 0 ? $"Worked for {duration}" : item.DurationMs is null ? "Worked" : "Worked for 0s";
        Label = item.ActionCount switch
        {
            null or <= 0 => label,
            1 => $"{label} · 1 action",
            var count => $"{label} · {count} actions"
        };
        HasFailures = item.Status == "Failed";
    }

    partial void OnIsExpandedChanged(bool value) => _expansionChanged();

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>One user turn plus everything the assistant produced in response.</summary>
public sealed partial class TranscriptTurnViewModel : ObservableObject, IDisposable
{
    private readonly Func<ActivitySummaryItemViewModel, Task>? _openActivity;
    private readonly Action<AssistantItemViewModel>? _openSources;
    private readonly Action<JobWakeItemViewModel>? _openJobWake;
    private readonly Func<
        string,
        string,
        IReadOnlyList<RemoteInlineImage>,
        CancellationToken,
        Task<string>>? _resolveInlineImages;
    private readonly Action<string, IReadOnlyList<RemoteInlineImage>>? _releaseInlineImages;
    private readonly Func<string, string?>? _displayModel;
    private WorkSummaryItemViewModel? _workSummary;
    private readonly HashSet<string> _workItemIds = new(StringComparer.Ordinal);

    public TranscriptTurnViewModel(
        string id,
        Func<ActivitySummaryItemViewModel, Task>? openActivity = null,
        Action<AssistantItemViewModel>? openSources = null,
        Func<
            string,
            string,
            IReadOnlyList<RemoteInlineImage>,
            CancellationToken,
            Task<string>>? resolveInlineImages = null,
        Action<string, IReadOnlyList<RemoteInlineImage>>? releaseInlineImages = null,
        Action<JobWakeItemViewModel>? openJobWake = null,
        Func<string, string?>? displayModel = null)
    {
        Id = id;
        _openActivity = openActivity;
        _openSources = openSources;
        _resolveInlineImages = resolveInlineImages;
        _releaseInlineImages = releaseInlineImages;
        _openJobWake = openJobWake;
        _displayModel = displayModel;
        Items.CollectionChanged += (_, _) =>
        {
            if (!_applying)
                RefreshDisplayItems();
        };
    }

    public string Id { get; }

    public ObservableCollection<TranscriptItemViewModel> Items { get; } = [];

    public ObservableCollection<TranscriptItemViewModel> DisplayItems { get; } = [];
    private bool _applying;

    public void Apply(RemoteTranscriptTurn turn)
    {
        _applying = true;
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
                _resolveInlineImages,
                _releaseInlineImages,
                _openJobWake,
                _displayModel);
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

        _applying = false;
        _workItemIds.Clear();
        var finalAnswerIndex = turn.FinalAnswerId is { } finalId
            ? turn.Items.FindIndex(item => item.Id == finalId
                && item.Kind == RemoteProtocol.ItemKinds.Assistant && !item.IsStreaming)
            : -1;
        for (var index = 0; index < Items.Count; index++)
        {
            if (Items[index] is AssistantItemViewModel answer)
                answer.IsFinalAnswer = index == finalAnswerIndex;
        }
        // Once Lumi stops, its work folds up to its last word even when that is no answer (a steered,
        // stopped or cut-off turn), as on the desktop; until then each update stays in view.
        var foldEnd = finalAnswerIndex >= 0 || turn.IsSettled != true
            ? finalAnswerIndex
            : turn.Items.FindLastIndex(static item => item.Kind == RemoteProtocol.ItemKinds.Assistant
                && !item.IsStreaming && !string.IsNullOrWhiteSpace(item.Text));
        for (var index = 0; index < foldEnd; index++)
        {
            var item = turn.Items[index];
            // Keep every recorded answer part, questions, errors and files accessible, and work still
            // running in the background in view, as the desktop does.
            if (item.Kind is RemoteProtocol.ItemKinds.Assistant
                or RemoteProtocol.ItemKinds.Activity
                or RemoteProtocol.ItemKinds.Reasoning
                or RemoteProtocol.ItemKinds.ToolGroup
                or RemoteProtocol.ItemKinds.Tool
                or RemoteProtocol.ItemKinds.Terminal
                && (item.Kind != RemoteProtocol.ItemKinds.Assistant || item.IsProgress != false)
                && !IsStillWorking(item))
            {
                _workItemIds.Add(item.Id);
            }
        }
        // The thinking behind the answer can be recorded just after it; it folds with the work it ended.
        if (finalAnswerIndex >= 0)
        {
            for (var index = finalAnswerIndex + 1; index < turn.Items.Count; index++)
            {
                var item = turn.Items[index];
                if (item is { Kind: RemoteProtocol.ItemKinds.Reasoning, IsStreaming: false }
                    || item.Kind == RemoteProtocol.ItemKinds.Activity
                    && item.ActionCount is null or <= 0
                    && (item.FileChangeCount ?? item.FileChanges?.Count ?? 0) == 0
                    && !IsStillWorking(item))
                {
                    _workItemIds.Add(item.Id);
                }
            }
        }
        // Without an answer, a lone step already reads as one line of its own, as on the desktop.
        else if (_workItemIds.Count < 2)
            _workItemIds.Clear();

        if (_workItemIds.Count > 0)
        {
            var folded = Items.Where(item => _workItemIds.Contains(item.Id)).ToArray();
            var summary = new RemoteTranscriptItem
            {
                Id = $"work-{folded[0].Id}",
                Kind = "work",
                DurationMs = turn.WorkDurationMs,
                ActionCount = folded.OfType<ActivitySummaryItemViewModel>().Sum(static activity => activity.ActionCount)
                              + folded.OfType<ToolGroupItemViewModel>().Sum(static group => group.Tools.Count)
                              + folded.Count(static item => item is TerminalItemViewModel),
                Status = folded.Any(static item => item is ActivitySummaryItemViewModel { IsFailed: true })
                    ? "Failed"
                    : "Completed"
            };
            if (_workSummary?.Id == summary.Id)
                _workSummary.Update(summary);
            else
                _workSummary = new WorkSummaryItemViewModel(summary, RefreshDisplayItems);
        }
        else
            _workSummary = null;

        RefreshDisplayItems();
    }

    internal void ResumeStreaming()
    {
        if (_workSummary is null)
            return;
        _workItemIds.Clear();
        _workSummary.Items.Clear();
        _workSummary = null;
        RefreshDisplayItems();
    }

    private static bool IsStillWorking(RemoteTranscriptItem item) =>
        item.IsStreaming
        || string.Equals(item.Status, "InProgress", StringComparison.Ordinal)
        || item.Tools?.Any(static tool => string.Equals(tool.Status, "InProgress", StringComparison.Ordinal)) == true;

    private void RefreshDisplayItems()
    {
        // Files the turn made belong to the answer that closes it, as on the desktop; until there is
        // one they follow the work.
        var closing = Items.OfType<AssistantItemViewModel>().FirstOrDefault(static answer => answer.IsFinalAnswer);
        var files = Items.OfType<FileItemViewModel>().ToArray();
        foreach (var answer in Items.OfType<AssistantItemViewModel>())
            answer.ShowFiles(ReferenceEquals(answer, closing) ? files.SelectMany(static item => item.Files).ToArray() : []);

        var desired = new List<TranscriptItemViewModel>();
        var workItems = new List<TranscriptItemViewModel>();
        var addedSummary = false;
        foreach (var item in Items)
        {
            if (item is FileItemViewModel)
                continue;
            if (_workSummary is not null && _workItemIds.Contains(item.Id))
            {
                if (!addedSummary)
                {
                    desired.Add(_workSummary);
                    addedSummary = true;
                }
                workItems.Add(item);
                continue;
            }

            desired.Add(item);
        }

        // Keep deliverables below the reply while preserving canonical order for streaming and paging.
        if (closing is null)
            desired.AddRange(files);
        // Folded work is built the first time it is opened, then kept in step so it can close with
        // its content rather than emptying first.
        if (_workSummary is not null && (_workSummary.IsExpanded || _workSummary.Items.Count > 0))
            SynchronizeDisplayItems(_workSummary.Items, workItems);
        SynchronizeDisplayItems(DisplayItems, desired);
    }

    internal static void SynchronizeDisplayItems(
        ObservableCollection<TranscriptItemViewModel> target,
        IReadOnlyList<TranscriptItemViewModel> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < target.Count && ReferenceEquals(target[index], desired[index]))
                continue;
            var existing = target.IndexOf(desired[index]);
            if (existing >= 0)
                target.Move(existing, index);
            else
                target.Insert(index, desired[index]);
        }
        while (target.Count > desired.Count)
            target.RemoveAt(target.Count - 1);
    }

    public void Dispose()
    {
        foreach (var item in Items)
            item.Dispose();
        Items.Clear();
        DisplayItems.Clear();
        _workSummary?.Items.Clear();
        _workSummary = null;
        _workItemIds.Clear();
    }
}
