using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;

namespace Lumi.Mobile.ViewModels;

/// <summary>
/// The open chat: transcript, live status and composer state. All mutations funnel through
/// <see cref="ApplyTranscript"/> / <see cref="ApplyDelta"/> so the server stays the source of truth
/// and a dropped SSE frame can never leave the phone showing something the desktop isn't.
/// </summary>
public sealed partial class MobileChatViewModel : ObservableObject
{
    private static readonly TimeSpan PendingRetryReplayWindow = TimeSpan.FromMinutes(9);
    private readonly IRemoteCommandSink _sink;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<Action> _post;
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private string? _preferredModel;
    private long _revision = -1;
    private string? _revisionEpoch;
    private long _hostGeneration;
    private long _blankSurfaceGeneration;
    private long? _blankSendInFlightGeneration;
    private long _surfaceActivationGeneration;
    private long _statusVersion;
    private readonly Dictionary<Guid, string> _pendingStopRequestIds = [];
    private readonly Dictionary<ChatSurfaceIdentity, DraftState> _drafts = [];
    private readonly Dictionary<ChatSurfaceIdentity, ChatSurfaceIdentity> _surfaceMappings = [];
    private CancellationTokenSource? _fileSuggestionCts;
    private long _fileSuggestionVersion;
    private bool _restoringDraftState;

    /// <summary>
    /// Set while server state is being written into the selection properties. Their setters push a
    /// <c>configure_chat</c> command, so without this guard every status frame would bounce straight
    /// back to the desktop and the two ends would fight over the chat's configuration.
    /// </summary>
    private bool _applyingServerState;

    [ObservableProperty] private Guid _chatId;
    [ObservableProperty] private string _title = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _promptText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isLoading;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private string? _transcriptErrorText;
    [ObservableProperty] private long _contextCurrentTokens;
    [ObservableProperty] private long _contextTokenLimit;
    [ObservableProperty] private string? _planContent;
    [ObservableProperty] private int _windowStartMessageIndex;
    [ObservableProperty] private int _windowEndMessageIndex;
    [ObservableProperty] private int _totalRawMessageCount;
    [ObservableProperty] private bool _hasEarlierMessages;
    [ObservableProperty] private bool _hasLaterMessages;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isLatestWindow = true;
    [ObservableProperty] private bool _hasNewerActivity;

    /// <summary>Plan sheet visibility. Chat-level state, opened from the top bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSheet))]
    private bool _isPlanOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSheet))]
    private bool _isActivitySheetOpen;

    [ObservableProperty] private ActivitySummaryItemViewModel? _selectedActivity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSheet))]
    private bool _isSourcesSheetOpen;

    [ObservableProperty] private AssistantItemViewModel? _selectedSourceAnswer;

    public bool HasPlan => !string.IsNullOrWhiteSpace(PlanContent);

    partial void OnPlanContentChanged(string? value)
    {
        OnPropertyChanged(nameof(HasPlan));

        // A plan that disappears must not leave an empty sheet hanging over the conversation.
        if (!HasPlan)
            IsPlanOpen = false;
    }

    [RelayCommand]
    private void TogglePlan() => IsPlanOpen = !IsPlanOpen;

    private async Task OpenActivityAsync(ActivitySummaryItemViewModel activity)
    {
        SelectedActivity = activity;
        IsActivitySheetOpen = true;
        if (activity.DetailsLoaded || activity.IsLoadingDetails)
            return;

        if (_sink is not IRemoteActivityDetailSink detailsSink)
        {
            activity.DetailsError = "Activity details are not available from this Lumi.";
            return;
        }

        var chatId = ChatId;
        var activityId = activity.ActivityId;
        var activation = Volatile.Read(ref _surfaceActivationGeneration);
        var detailsVersion = activity.DetailsVersion;
        var retry = false;
        activity.IsLoadingDetails = true;
        try
        {
            var details = await detailsSink.GetActivityDetailsAsync(chatId, activityId);
            if (ChatId != chatId
                || activation != Volatile.Read(ref _surfaceActivationGeneration)
                || !string.Equals(activity.ActivityId, activityId, StringComparison.Ordinal))
            {
                return;
            }
            if (activity.DetailsVersion != detailsVersion)
            {
                retry = true;
                return;
            }

            if (details is null)
            {
                activity.DetailsError = "Activity details could not be loaded.";
                return;
            }

            activity.ApplyDetails(details);
        }
        catch (Exception ex)
        {
            activity.DetailsError = $"Activity details could not be loaded: {ex.Message}";
        }
        finally
        {
            activity.IsLoadingDetails = false;
            if (retry
                && IsActivitySheetOpen
                && ReferenceEquals(SelectedActivity, activity))
            {
                _ = OpenActivityAsync(activity);
            }
        }
    }

    private void OpenSources(AssistantItemViewModel answer)
    {
        if (!answer.HasSources)
            return;

        SelectedSourceAnswer = answer;
        IsSourcesSheetOpen = true;
    }

    // ── Composer configuration. Setting any of these configures the chat on the PC. ──
    [ObservableProperty] private string? _model;
    [ObservableProperty] private string? _quality;
    [ObservableProperty] private string? _contextWindowTier;
    [ObservableProperty] private string? _agentName;
    [ObservableProperty] private string? _agentValue;
    [ObservableProperty] private string _agentGlyph = "◉";
    [ObservableProperty] private string? _projectName;
    [ObservableProperty] private string? _projectValue;
    [ObservableProperty] private bool _useWorktree;

    private readonly Dictionary<Guid, RemoteProject> _projectCatalog = [];
    private bool _worktreeChoiceExplicit;
    private bool _hasAuthoritativeTranscript;
    private bool _supportsAtomicWorktreeSelection = true;

    public bool CanChooseWorktree =>
        _supportsAtomicWorktreeSelection &&
        HasConfirmedEmptyHistory &&
        CanChangeProjectSelection &&
        _pendingRetry is null &&
        TryGetSelectedProject(out var project) &&
        project.IsCodingProject;

    public bool CanChangeProjectSelection =>
        _pendingRetry is null &&
        !IsCurrentBlankSendInFlight &&
        (ChatId != Guid.Empty || (!IsBusy && !IsStreaming)) &&
        !(ChatId != Guid.Empty &&
          UseWorktree &&
          _hasAuthoritativeTranscript &&
          TotalRawMessageCount > 0);

    public bool IsLocalWorkspaceSelected => !UseWorktree;

    public string WorkspaceSummary => UseWorktree ? "New worktree" : "Local checkout";

    public void ApplyRemoteProtocolVersion(int protocolVersion)
    {
        var supportsWorktrees = RemoteProtocol.IsCompatibleVersion(protocolVersion);
        if (_supportsAtomicWorktreeSelection == supportsWorktrees)
            return;

        _supportsAtomicWorktreeSelection = supportsWorktrees;
        if (!supportsWorktrees)
        {
            _pendingConfiguration.RemoveScalar("worktree");
            _worktreeChoiceExplicit = false;
            UseWorktree = false;
        }

        OnPropertyChanged(nameof(CanChooseWorktree));
        OnPropertyChanged(nameof(RunSettingsSummary));
    }

    /// <summary>
    /// Files the user attached but has not sent yet, as absolute paths ON THE PC.
    ///
    /// <para>Uploaded the moment they are picked rather than at send: an upload can fail or be slow,
    /// and finding that out after tapping send — with the message already gone — would be worse than
    /// finding out while still composing.</para>
    /// </summary>
    public ObservableCollection<PendingAttachment> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    internal long StatusVersion => Volatile.Read(ref _statusVersion);

    /// <summary>An upload in flight, so the composer can show progress instead of appearing to hang.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isUploading;

    /// <summary>
    /// Asks the view to open the file picker. The picker needs a TopLevel, which a view model has
    /// no business holding, so the view subscribes and calls back into <see cref="AttachFileAsync"/>.
    /// </summary>
    [RelayCommand]
    private void PickAttachment() => AttachmentPickRequested?.Invoke();

    public event Action? AttachmentPickRequested;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SearchFilesAsync(string? rawQuery)
    {
        _fileSuggestionCts?.Cancel();
        _fileSuggestionCts?.Dispose();
        var cts = new CancellationTokenSource();
        _fileSuggestionCts = cts;

        if (_sink is not IRemoteFileSuggestionSink suggestionSink)
        {
            AvailableFiles.Clear();
            return;
        }

        var version = Interlocked.Increment(ref _fileSuggestionVersion);
        var hostGeneration = Volatile.Read(ref _hostGeneration);
        var surface = CurrentSurface;
        var activation = Volatile.Read(ref _surfaceActivationGeneration);
        var query = rawQuery?.Trim() ?? "";
        var projectId = Guid.TryParse(ProjectValue, out var parsedProjectId)
            ? parsedProjectId
            : (Guid?)null;
        try
        {
            await Task.Delay(90, cts.Token);
            var suggestions = await suggestionSink.GetFileSuggestionsAsync(
                ChatId == Guid.Empty ? null : ChatId,
                projectId,
                query,
                cts.Token);
            var items = suggestions?.Items ?? [];
            _post(() =>
            {
                if (cts.IsCancellationRequested
                    || version != Volatile.Read(ref _fileSuggestionVersion)
                    || !IsCurrentHost(hostGeneration)
                    || !IsCurrentSurfaceActivation(surface, activation))
                {
                    return;
                }

                SyncCatalog(AvailableFiles, items, "📄");
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (version == Volatile.Read(ref _fileSuggestionVersion)
                && IsCurrentHost(hostGeneration)
                && IsCurrentSurfaceActivation(surface, activation))
            {
                _post(AvailableFiles.Clear);
            }
            Trace.TraceWarning($"[Mobile] File suggestions failed: {ex}");
        }
    }

    [RelayCommand]
    private void SelectFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        CancelFileSuggestionSearch();
        var fileName = FileNameFromRemotePath(filePath);
        ApplyUploadedAttachment(
            CurrentSurface,
            new PendingAttachment(
                string.IsNullOrWhiteSpace(fileName) ? filePath : fileName,
                filePath));
        AvailableFiles.Clear();
    }

    internal static string FileNameFromRemotePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var separator = normalized.LastIndexOf('/');
        return separator >= 0 && separator + 1 < normalized.Length
            ? normalized[(separator + 1)..]
            : normalized;
    }

    private void CancelFileSuggestionSearch()
    {
        _fileSuggestionCts?.Cancel();
        _fileSuggestionCts?.Dispose();
        _fileSuggestionCts = null;
        Interlocked.Increment(ref _fileSuggestionVersion);
    }

    /// <summary>
    /// Uploads a picked file and stages it for the next message.
    ///
    /// <para>Never throws. The only caller is an <c>async void</c> event handler, and the upload
    /// crosses the network — a dropped Wi-Fi association mid-send would otherwise escape as an
    /// unhandled exception and kill the process rather than showing a message.</para>
    /// </summary>
    public async Task AttachFileAsync(string fileName, ReadOnlyMemory<byte> content)
    {
        var hostGeneration = Volatile.Read(ref _hostGeneration);
        var surface = CurrentSurface;
        IsUploading = true;
        try
        {
            var result = await _sink.UploadAsync(fileName, content);
            if (!IsCurrentHost(hostGeneration))
                return;

            if (!result.Ok || result.Path is not { Length: > 0 } path)
            {
                ApplyUploadError(
                    surface,
                    result.Error ?? "Lumi could not receive that file.");
                return;
            }

            ApplyUploadedAttachment(
                surface,
                new PendingAttachment(result.FileName ?? fileName, path));
        }
        catch (Exception ex)
        {
            if (!IsCurrentHost(hostGeneration))
                return;

            ApplyUploadError(surface, "Could not send that file to your PC.");
            Trace.TraceWarning($"[Mobile] Upload failed: {ex}");
        }
        finally
        {
            if (IsCurrentHost(hostGeneration) && IsCurrentSurface(surface))
                IsUploading = false;
        }
    }

    [RelayCommand]
    private void RemoveAttachment(PendingAttachment? attachment)
    {
        if (attachment is not null && Attachments.Remove(attachment))
        {
            ClearPendingRetryIfPayloadChanged();
            OnPropertyChanged(nameof(HasAttachments));
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    public MobileChatViewModel(IRemoteCommandSink sink, Action<Action>? post = null)
        : this(sink, static () => DateTimeOffset.UtcNow, post)
    {
    }

    internal MobileChatViewModel(
        IRemoteCommandSink sink,
        Func<DateTimeOffset> now,
        Action<Action>? post = null)
    {
        _sink = sink;
        _now = now;
        _post = post ?? (static action => action());

        // The composer adds a picked skill / MCP straight into these collections, so watching them
        // is the only way to learn about a "+" menu selection. Adds made by ApplyStatus are ignored
        // via _applyingServerState, leaving only the ones the user actually made on the phone.
        SkillChips.CollectionChanged += (_, e) => PushChipAdditions(e, "addSkills");
        McpChips.CollectionChanged += (_, e) => PushChipAdditions(e, "addMcps");
    }

    private void PushChipAdditions(NotifyCollectionChangedEventArgs e, string key)
    {
        if (_applyingServerState || e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
            return;

        foreach (var item in e.NewItems)
        {
            if (ChipName(item) is { Length: > 0 } name)
                _ = ConfigureAsync(key, name);
        }
    }

    public ObservableCollection<TranscriptTurnViewModel> Turns { get; } = [];

    public ObservableCollection<string> Suggestions { get; } = [];

    /// <summary>The three static starters shown when the desktop has nothing more specific.</summary>
    private static readonly ChatStarter[] DefaultStarters =
    [
        new("M19 4h-1V2h-2v2H8V2H6v2H5a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2zm0 15H5V9h14v10zm-7-8h5v5h-5v-5z", "Plan my day"),
        new("M9.5 3a6.5 6.5 0 1 0 3.98 11.64L19.85 21 21 19.85l-6.36-6.37A6.5 6.5 0 0 0 9.5 3zm0 2a4.5 4.5 0 1 1 0 9 4.5 4.5 0 0 1 0-9z", "Research a topic"),
        new("M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8l-6-6zm0 2.5L17.5 8H14V4.5zM8 13h8v2H8v-2zm0 4h8v2H8v-2zm0-8h4v2H8V9z", "Create a document")
    ];

    /// <summary>
    /// What an empty chat offers before the user has typed anything. An empty canvas gives no clue
    /// what Lumi can actually do, and on a phone — where typing is the expensive part — a tap that
    /// fills the composer is worth far more than it is on a desktop.
    ///
    /// <para>These belong on the canvas, not in the composer. A suggestion chip stacked above the
    /// input steals height from the one control the user came for, and on a phone that is the
    /// difference between seeing two lines of your draft and seeing four.</para>
    /// </summary>
    public IReadOnlyList<ChatStarter> Starters => Suggestions.Count > 0
        ? [.. Suggestions.Take(3).Select(text => new ChatStarter(
            "M12 2c.7 5.2 2.8 7.3 8 8-5.2.7-7.3 2.8-8 8-.7-5.2-2.8-7.3-8-8 5.2-.7 7.3-2.8 8-8z",
            text))]
        : DefaultStarters;

    public ObservableCollection<string> AvailableModels { get; } = [];

    public ObservableCollection<string> QualityLevels { get; } = [];

    public ObservableCollection<string> ContextWindowTiers { get; } = [];

    /// <summary>Skills attached to this chat, as composer chips.</summary>
    public ObservableCollection<StrataComposerChip> SkillChips { get; } = [];

    /// <summary>MCP servers attached to this chat, as composer chips.</summary>
    public ObservableCollection<StrataComposerChip> McpChips { get; } = [];

    public ObservableCollection<StrataComposerChip> AvailableAgents { get; } = [];
    public ObservableCollection<StrataComposerChip> AvailableSkills { get; } = [];
    public ObservableCollection<StrataComposerChip> AvailableMcps { get; } = [];
    public ObservableCollection<StrataComposerChip> AvailableProjects { get; } = [];
    public ObservableCollection<StrataComposerChip> AvailableFiles { get; } = [];

    public bool HasChat => ChatId != Guid.Empty;

    /// <summary>
    /// True when the blank surface has become the user's work rather than the untouched launch
    /// surface. An initial desktop snapshot may choose the launch chat, but must never replace this.
    /// </summary>
    public bool HasStagedBlankState =>
        ChatId == Guid.Empty &&
        (PromptText.Length > 0 ||
         HasAttachments ||
         IsUploading ||
         IsBusy ||
         Turns.Count > 0 ||
         !_pendingConfiguration.IsEmpty);

    public bool IsEmpty => Turns.Count == 0 && !IsLoading;

    public bool CanNavigateTranscript => !IsLoading;

    public string NewerActivityLabel => HasNewerActivity
        ? "Newer activity available"
        : "Newer activity";

    public string WindowSummary => TotalRawMessageCount == 0
        ? ""
        : $"Activity {WindowStartMessageIndex + 1:N0}–{WindowEndMessageIndex:N0} of {TotalRawMessageCount:N0}";

    /// <summary>
    /// The transcript-level "thinking" row. It is asserted optimistically in the same frame as the
    /// user's message, then yields only once another visible response row can take over.
    /// </summary>
    public bool ShowThinking => (IsBusy || IsStreaming) && _awaitingVisibleActivity;

    /// <summary>
    /// What Lumi is doing right now, in words — the label on the transcript's thinking row. The
    /// difference between "something is happening" and "I have no idea if my tap registered".
    /// </summary>
    public string ProgressText => !string.IsNullOrWhiteSpace(StatusText)
        ? StatusText!
        : "Thinking…";

    /// <summary>A single option is not a choice — hide the picker rather than show a dead control.</summary>
    public bool HasQualityLevels => QualityLevels.Count > 1;

    /// <summary>
    /// What the composer's picker pill says: the model, plus the reasoning effort when the model
    /// actually offers a choice. Selecting a model is the most-changed setting in a chat, so the
    /// composer has to show what is currently in effect rather than making the user open a sheet to
    /// find out.
    /// </summary>
    public string ModelDisplayName => DisplayModel(Model) ?? "Model";

    public string ModelSummary => Model is { Length: > 0 }
        ? HasQualityLevels && Quality is { Length: > 0 } quality
            ? $"{ModelDisplayName} · {quality}"
            : ModelDisplayName
        : "Model";

    public bool HasContextWindowTiers => ContextWindowTiers.Count > 1;

    public string ContextWindowLabel => string.IsNullOrWhiteSpace(ContextWindowTier)
        ? "Default"
        : ContextWindowTier!;

    public string RunSettingsSummary
    {
        get
        {
            var parts = new List<string> { ModelDisplayName };
            if (HasQualityLevels)
                parts.Add(EffortLabel);
            if (HasContextWindowTiers
                && !string.Equals(ContextWindowLabel, "Default", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(ContextWindowLabel);
            }
            if (CanChooseWorktree)
                parts.Add(WorkspaceSummary);

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// Model / effort / context selection, as a sheet rather than the composer's anchored popup.
    ///
    /// <para>The desktop picker is a 160dp dropdown that assumes a cursor and opens against the
    /// composer. On a phone it rendered as a cramped popup over the keyboard, and the composer strip
    /// that hosted it was five sub-30dp targets wide. A sheet gives every option a full-width row.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSheet))]
    private bool _isModelSheetOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSheet))]
    private bool _isRunSettingsSheetOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSheet))]
    private bool _isContextSheetOpen;

    [RelayCommand]
    private void OpenRunSettingsSheet() => IsRunSettingsSheetOpen = true;

    [RelayCommand]
    private void SelectLocalWorkspace()
    {
        if (!CanChooseWorktree)
            return;

        _worktreeChoiceExplicit = true;
        if (UseWorktree)
            UseWorktree = false;
        else
            _pendingConfiguration.SetScalar("worktree", "false");
    }

    [RelayCommand]
    private void SelectNewWorktree()
    {
        if (!CanChooseWorktree)
            return;

        _worktreeChoiceExplicit = true;
        UseWorktree = true;
    }

    [RelayCommand]
    private void OpenModelFromRunSettings()
    {
        IsRunSettingsSheetOpen = false;
        OpenModelSheet();
    }

    [RelayCommand]
    private void OpenEffortFromRunSettings()
    {
        IsRunSettingsSheetOpen = false;
        OpenEffortSheet();
    }

    [RelayCommand]
    private void OpenContextFromRunSettings()
    {
        IsRunSettingsSheetOpen = false;
        RefreshContextWindowTiers();
        IsContextSheetOpen = true;
    }

    /// <summary>
    /// The pickable options, carrying their own selected state. A converter comparing each row
    /// against the current selection cannot work here — Avalonia's ConverterParameter is not
    /// bindable — and pushing the comparison into the view model keeps it testable besides.
    /// </summary>
    public ObservableCollection<PickerOption> ModelOptions { get; } = [];

    public ObservableCollection<PickerOption> QualityOptions { get; } = [];

    public ObservableCollection<PickerOption> ContextTierOptions { get; } = [];

    private void RefreshPickerOptions()
    {
        SyncOptions(ModelOptions, AvailableModels, Model, DisplayModel);
        SyncOptions(QualityOptions, QualityLevels, Quality);
        SyncOptions(ContextTierOptions, ContextWindowTiers, ContextWindowTier);

        void SyncOptions(
            ObservableCollection<PickerOption> target,
            IReadOnlyList<string> source,
            string? selected,
            Func<string, string?>? display = null)
        {
            while (target.Count > source.Count)
                target.RemoveAt(target.Count - 1);

            for (var i = 0; i < source.Count; i++)
            {
                var option = new PickerOption(
                    source[i],
                    string.Equals(source[i], selected, StringComparison.OrdinalIgnoreCase),
                    display?.Invoke(source[i]));
                if (i < target.Count)
                {
                    if (target[i] != option)
                        target[i] = option;
                }
                else
                {
                    target.Add(option);
                }
            }
        }
    }

    private string? DisplayModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;

        return _modelDisplayNames.TryGetValue(model, out var display) ? display : model;
    }

    [RelayCommand]
    private void OpenModelSheet()
    {
        RefreshPickerOptions();
        IsModelSheetOpen = true;
    }

    /// <summary>
    /// Reasoning effort as a slider position rather than a list.
    ///
    /// <para>Effort is an ordered scale — low through high — and a list of radio-ish rows hides that.
    /// A slider states the ordering, is one gesture instead of open-scroll-tap, and gives a much
    /// bigger target than three stacked rows.</para>
    /// </summary>
    public double EffortIndex
    {
        get => Math.Max(0, QualityLevels.IndexOf(Quality ?? ""));
        set
        {
            var index = (int)Math.Round(value);
            if (index < 0 || index >= QualityLevels.Count)
                return;

            if (!string.Equals(QualityLevels[index], Quality, StringComparison.Ordinal))
                Quality = QualityLevels[index];

            OnPropertyChanged();
            OnPropertyChanged(nameof(EffortLabel));
        }
    }

    /// <summary>Upper bound for the slider — one less than the number of levels.</summary>
    public double EffortMax => Math.Max(0, QualityLevels.Count - 1);

    public string EffortLabel => Quality is { Length: > 0 } quality
        ? char.ToUpperInvariant(quality[0]) + quality[1..]
        : "Default";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSheet))]
    private bool _isEffortSheetOpen;

    [RelayCommand]
    private void OpenEffortSheet()
    {
        OnPropertyChanged(nameof(EffortIndex));
        OnPropertyChanged(nameof(EffortMax));
        OnPropertyChanged(nameof(EffortLabel));
        IsEffortSheetOpen = true;
    }

    /// <summary>Picks a model and dismisses — a sheet that stays open after a choice feels broken.</summary>
    [RelayCommand]
    private void PickModel(string? model)
    {
        if (model is { Length: > 0 })
            Model = model;

        IsModelSheetOpen = false;
    }

    [RelayCommand]
    private void PickQuality(string? quality)
    {
        if (quality is { Length: > 0 })
            Quality = quality;

        RefreshPickerOptions();
    }

    [RelayCommand]
    private void PickContextTier(string? tier)
    {
        if (tier is { Length: > 0 })
            ContextWindowTier = tier;

        RefreshPickerOptions();
        IsContextSheetOpen = false;
    }

    /// <summary>Whether any modal sheet currently covers the conversation.</summary>
    public bool HasOpenSheet =>
        IsSourcesSheetOpen ||
        IsActivitySheetOpen ||
        IsRunSettingsSheetOpen ||
        IsModelSheetOpen ||
        IsContextSheetOpen ||
        IsEffortSheetOpen ||
        IsPlanOpen;

    /// <summary>Closes the visually topmost chat sheet.</summary>
    internal bool DismissTopmostSheet()
    {
        if (IsSourcesSheetOpen)
        {
            IsSourcesSheetOpen = false;
            return true;
        }

        if (IsActivitySheetOpen)
        {
            IsActivitySheetOpen = false;
            return true;
        }

        if (IsPlanOpen)
        {
            IsPlanOpen = false;
            return true;
        }

        if (IsEffortSheetOpen)
        {
            IsEffortSheetOpen = false;
            return true;
        }

        if (IsContextSheetOpen)
        {
            IsContextSheetOpen = false;
            return true;
        }

        if (IsModelSheetOpen)
        {
            IsModelSheetOpen = false;
            return true;
        }

        if (IsRunSettingsSheetOpen)
        {
            IsRunSettingsSheetOpen = false;
            return true;
        }

        return false;
    }

    // ── Context window ───────────────────────────────────────────────────────────────────────

    /// <summary>Fraction of the context window in use, 0-1. Zero when the limit is unknown.</summary>
    public double ContextFraction => ContextTokenLimit > 0
        ? Math.Clamp((double)ContextCurrentTokens / ContextTokenLimit, 0, 1)
        : 0;

    public int ContextPercent => (int)Math.Round(ContextFraction * 100);

    public bool HasContextUsage => ContextTokenLimit > 0;

    /// <summary>
    /// The meter earns its slot in a phone header only once the window is worth watching. Showing
    /// "3%" on a fresh chat is noise; the number becomes actionable as the conversation fills up.
    /// </summary>
    public bool ShowContextMeter => HasContextUsage && ContextFraction >= 0.35;

    public string ContextUsageText => HasContextUsage ? $"{ContextPercent}%" : "";

    /// <summary>"18.2K / 128K tokens" — the detail behind the meter.</summary>
    public string ContextDetailText => HasContextUsage
        ? $"{FormatTokens(ContextCurrentTokens)} / {FormatTokens(ContextTokenLimit)} tokens"
        : "";

    /// <summary>
    /// Pressure buckets driving the meter colour: normal below 60%, warn to 85%, critical above.
    /// Exposed as booleans so the view can toggle a style class directly, with no converter.
    /// </summary>
    public bool IsContextWarn => ContextFraction is >= 0.60 and < 0.85;

    public bool IsContextCritical => ContextFraction >= 0.85;

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000d:0.#}M",
        >= 1_000 => $"{tokens / 1_000d:0.#}K",
        _ => tokens.ToString()
    };

    partial void OnChatIdChanged(Guid value)
    {
        OnPropertyChanged(nameof(HasChat));
        OnPropertyChanged(nameof(CanChooseWorktree));
        OnPropertyChanged(nameof(RunSettingsSummary));
        _revision = -1;
        _revisionEpoch = null;
    }

    partial void OnPromptTextChanged(string value)
    {
        if (!_restoringDraftState)
            ClearPendingRetryIfPayloadChanged();
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanChooseWorktree));
        OnPropertyChanged(nameof(CanNavigateTranscript));
    }

    partial void OnHasNewerActivityChanged(bool value) =>
        OnPropertyChanged(nameof(NewerActivityLabel));

    partial void OnWindowStartMessageIndexChanged(int value) =>
        OnPropertyChanged(nameof(WindowSummary));

    partial void OnWindowEndMessageIndexChanged(int value) =>
        OnPropertyChanged(nameof(WindowSummary));

    partial void OnTotalRawMessageCountChanged(int value) =>
        OnPropertyChanged(nameof(WindowSummary));

    partial void OnIsBusyChanged(bool value) => RaiseWorkingChanged();

    partial void OnIsStreamingChanged(bool value) => RaiseWorkingChanged();

    partial void OnStatusTextChanged(string? value) => OnPropertyChanged(nameof(ProgressText));

    private void RaiseWorkingChanged()
    {
        OnPropertyChanged(nameof(ShowThinking));
        OnPropertyChanged(nameof(ProgressText));
    }

    partial void OnContextCurrentTokensChanged(long value) => RaiseContextChanged();

    partial void OnContextTokenLimitChanged(long value) => RaiseContextChanged();

    private void RaiseContextChanged()
    {
        OnPropertyChanged(nameof(ContextFraction));
        OnPropertyChanged(nameof(ContextPercent));
        OnPropertyChanged(nameof(HasContextUsage));
        OnPropertyChanged(nameof(ShowContextMeter));
        OnPropertyChanged(nameof(ContextUsageText));
        OnPropertyChanged(nameof(ContextDetailText));
        OnPropertyChanged(nameof(IsContextWarn));
        OnPropertyChanged(nameof(IsContextCritical));
    }

    // ── Selection setters: each pushes exactly the one field the user changed ──

    partial void OnModelChanged(string? value)
    {
        PushConfiguration("model", value);
        OnPropertyChanged(nameof(ModelDisplayName));
        OnPropertyChanged(nameof(ModelSummary));
        OnPropertyChanged(nameof(RunSettingsSummary));

        // Which efforts a model supports is a property of the model, so derive it from the catalog
        // rather than waiting for a chat status that may never carry them.
        RefreshEffortLevels();
        RefreshContextWindowTiers();
    }

    partial void OnQualityChanged(string? value)
    {
        PushConfiguration("quality", value);
        OnPropertyChanged(nameof(ModelSummary));
        OnPropertyChanged(nameof(RunSettingsSummary));
        OnPropertyChanged(nameof(EffortIndex));
        OnPropertyChanged(nameof(EffortLabel));
    }

    partial void OnContextWindowTierChanged(string? value)
    {
        PushConfiguration("contextWindowTier", value);
        OnPropertyChanged(nameof(ContextWindowLabel));
        OnPropertyChanged(nameof(RunSettingsSummary));
    }

    partial void OnAgentNameChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(AgentValue))
            PushIdentityConfiguration("agent", value ?? "", "agentId");
    }

    partial void OnAgentValueChanged(string? value) =>
        PushIdentityConfiguration("agentId", value ?? "", "agent");

    partial void OnProjectNameChanged(string? oldValue, string? newValue)
    {
        if (ShouldRejectProjectSelectionChange())
        {
            RestoreRejectedProjectSelection(ProjectValue, oldValue);
            return;
        }

        if (string.IsNullOrWhiteSpace(ProjectValue))
            ResetWorktreeChoiceForProjectChange();
        RefreshWorktreeChoice();
        if (string.IsNullOrWhiteSpace(ProjectValue))
            PushIdentityConfiguration("project", newValue ?? "", "projectId");
    }

    partial void OnProjectValueChanged(string? oldValue, string? newValue)
    {
        if (ShouldRejectProjectSelectionChange())
        {
            RestoreRejectedProjectSelection(oldValue, ProjectName);
            return;
        }

        ResetWorktreeChoiceForProjectChange();
        RefreshWorktreeChoice();
        PushIdentityConfiguration("projectId", newValue ?? "", "project");
    }

    partial void OnUseWorktreeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLocalWorkspaceSelected));
        OnPropertyChanged(nameof(WorkspaceSummary));
        OnPropertyChanged(nameof(RunSettingsSummary));

        if (_applyingServerState || _restoringDraftState || !CanChooseWorktree)
            return;

        _pendingConfiguration.SetScalar("worktree", value ? "true" : "false");
    }

    private void RefreshWorktreeChoice()
    {
        OnPropertyChanged(nameof(CanChooseWorktree));
        if (!HasConfirmedEmptyHistory)
            return;

        if (_pendingConfiguration.TryGetScalar("worktree", out var pendingWorktree)
            && bool.TryParse(pendingWorktree, out var usePendingWorktree))
        {
            _worktreeChoiceExplicit = true;
            UseWorktree = usePendingWorktree;
            return;
        }

        if (!TryGetSelectedProject(out var project))
        {
            ClearWorktreeChoice();
            return;
        }

        if (!project.IsCodingProject)
        {
            ClearWorktreeChoice();
            return;
        }

        if (!_worktreeChoiceExplicit)
        {
            UseWorktree = project.DefaultNewChatsUseWorktree;
            if (!_applyingServerState)
                _pendingConfiguration.SetScalar("worktree", UseWorktree ? "true" : "false");
        }
    }

    private void ResetWorktreeChoiceForProjectChange()
    {
        if (_applyingServerState || _restoringDraftState || !HasConfirmedEmptyHistory)
            return;

        _pendingConfiguration.RemoveScalar("worktree");
        _worktreeChoiceExplicit = false;
    }

    private bool ShouldRejectProjectSelectionChange() =>
        !_applyingServerState &&
        !_restoringDraftState &&
        !CanChangeProjectSelection;

    private void RestoreRejectedProjectSelection(string? projectValue, string? projectName)
    {
        _restoringDraftState = true;
        try
        {
            ProjectValue = projectValue;
            ProjectName = projectName;
        }
        finally
        {
            _restoringDraftState = false;
        }
    }

    private void ClearWorktreeChoice()
    {
        var shouldDetachExistingWorktree =
            ChatId != Guid.Empty
            && HasConfirmedEmptyHistory
            && UseWorktree
            && !_applyingServerState;
        _worktreeChoiceExplicit = false;
        UseWorktree = false;
        if (shouldDetachExistingWorktree)
            _pendingConfiguration.SetScalar("worktree", "false");
        else
            _pendingConfiguration.RemoveScalar("worktree");
    }

    private bool TryGetSelectedProject(out RemoteProject project)
    {
        if (Guid.TryParse(ProjectValue, out var projectId)
            && _projectCatalog.TryGetValue(projectId, out project!))
        {
            return true;
        }

        project = _projectCatalog.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, ProjectName, StringComparison.Ordinal))!;
        return project is not null;
    }

    private bool HasConfirmedEmptyHistory =>
        ChatId == Guid.Empty
            ? IsEmpty
            : _hasAuthoritativeTranscript && TotalRawMessageCount == 0 && IsEmpty;

    private void PushIdentityConfiguration(string key, string value, string supersededKey)
    {
        if (_applyingServerState)
            return;

        _pendingConfiguration.RemoveScalar(supersededKey);
        PushConfiguration(key, value);
    }

    private void PushConfiguration(string key, string? value)
    {
        if (_applyingServerState || _restoringDraftState || value is null)
            return;

        Interlocked.Increment(ref _statusVersion);

        // A phone launches into an empty surface, so the user reaches the pickers before any chat
        // exists. Remember the choice and replay it the moment the desktop creates one, instead of
        // silently dropping it — that is what made the model and agent pickers dead on a new chat.
        if (ChatId == Guid.Empty)
        {
            _pendingConfiguration.SetScalar(key, value);
            return;
        }

        // Keep every unconfirmed choice in one batch. SendAsync carries the same batch atomically,
        // so an immediate send cannot overtake a fire-and-forget configure request.
        _pendingConfiguration.SetScalar(key, value);
        _ = FlushPendingConfigurationAsync();
    }

    /// <summary>Choices made before the chat existed, replayed once it does.</summary>
    private readonly PendingChatConfiguration _pendingConfiguration = new();
    private PendingRetry? _pendingRetry;

    /// <summary>True when the user configured something the desktop has not been told about yet.</summary>
    public bool HasPendingConfiguration => !_pendingConfiguration.IsEmpty;

    private ChatSurfaceIdentity CurrentSurface =>
        new(ChatId, ChatId == Guid.Empty ? _blankSurfaceGeneration : 0);

    private bool IsCurrentBlankSendInFlight =>
        ChatId == Guid.Empty &&
        _blankSendInFlightGeneration == _blankSurfaceGeneration;

    private void LockBlankProjectSelection(ChatSurfaceIdentity surface)
    {
        if (!surface.IsBlank)
            return;

        _blankSendInFlightGeneration = surface.BlankGeneration;
        OnPropertyChanged(nameof(CanChooseWorktree));
    }

    private void ReleaseBlankProjectSelection(ChatSurfaceIdentity surface)
    {
        if (!surface.IsBlank ||
            _blankSendInFlightGeneration != surface.BlankGeneration)
        {
            return;
        }

        _blankSendInFlightGeneration = null;
        OnPropertyChanged(nameof(CanChooseWorktree));
    }

    private bool IsCurrentActivation(long activation) =>
        activation == _surfaceActivationGeneration;

    private bool IsCurrentHost(long generation) =>
        generation == Volatile.Read(ref _hostGeneration);

    private bool IsCurrentSurface(ChatSurfaceIdentity surface) =>
        ResolveSurface(CurrentSurface) == ResolveSurface(surface);

    private bool IsCurrentSurfaceActivation(
        ChatSurfaceIdentity surface,
        long activation) =>
        IsCurrentActivation(activation) &&
        IsCurrentSurface(surface);

    private ChatSurfaceIdentity ResolveSurface(ChatSurfaceIdentity surface)
    {
        while (_surfaceMappings.TryGetValue(surface, out var mapped) && mapped != surface)
            surface = mapped;

        return surface;
    }

    private ChatSurfaceIdentity MapSurfaceToChat(ChatSurfaceIdentity surface, Guid chatId)
    {
        surface = ResolveSurface(surface);
        if (!surface.IsBlank || chatId == Guid.Empty)
            return surface;

        var target = new ChatSurfaceIdentity(chatId, 0);
        _surfaceMappings[surface] = target;

        if (_drafts.Remove(surface, out var sourceDraft))
        {
            var targetDraft = GetDraft(target);
            StoreDraft(target, MergeDrafts(sourceDraft, targetDraft));
        }

        return target;
    }

    private static DraftState MergeDrafts(DraftState source, DraftState target)
    {
        var attachments = target.Attachments.ToList();
        foreach (var attachment in source.Attachments)
        {
            if (!attachments.Contains(attachment))
                attachments.Add(attachment);
        }

        var configuration = source.Configuration.Clone();
        configuration.MergeFrom(
            target.Configuration,
            overwriteScalars: true,
            overwriteCollectionValues: true);

        var promptText = target.PromptText.Length > 0
            ? target.PromptText
            : source.PromptText;
        var retry = target.PendingRetry ?? source.PendingRetry;
        if (retry is not null && !retry.Payload.Matches(promptText, attachments))
            retry = null;

        return new DraftState(
            promptText,
            [.. attachments],
            target.ErrorText ?? source.ErrorText,
            configuration,
            retry);
    }

    private void SaveDraft(ChatSurfaceIdentity surface)
    {
        StoreDraft(surface, CaptureCurrentDraft());
    }

    private void RestoreDraft(ChatSurfaceIdentity surface)
    {
        ApplyDraft(GetDraft(surface), restoreSelections: true);
    }

    private DraftState CaptureCurrentDraft() =>
        new(
            PromptText,
            [.. Attachments],
            ErrorText,
            _pendingConfiguration.Clone(),
            _pendingRetry?.Copy());

    private DraftState GetDraft(ChatSurfaceIdentity surface)
    {
        surface = ResolveSurface(surface);
        return _drafts.TryGetValue(surface, out var draft)
            ? draft.Copy()
            : DraftState.Empty;
    }

    private void StoreDraft(ChatSurfaceIdentity surface, DraftState draft)
    {
        surface = ResolveSurface(surface);
        if (draft.IsEmpty)
        {
            _drafts.Remove(surface);
            return;
        }

        _drafts[surface] = draft.Copy();
    }

    private void ApplyDraft(DraftState draft, bool restoreSelections)
    {
        _restoringDraftState = true;
        try
        {
            PromptText = draft.PromptText;
            Attachments.Clear();
            foreach (var attachment in draft.Attachments)
                Attachments.Add(attachment);

            ErrorText = draft.ErrorText;
            _pendingConfiguration.CopyFrom(draft.Configuration);
            _pendingRetry = draft.PendingRetry?.Copy();

            if (restoreSelections)
                RestorePendingConfigurationSelections();
        }
        finally
        {
            _restoringDraftState = false;
        }

        OnPropertyChanged(nameof(HasAttachments));
        SendCommand.NotifyCanExecuteChanged();
    }

    private void RestorePendingConfigurationSelections()
    {
        if (_pendingConfiguration.TryGetScalar("model", out var model))
            Model = model;
        if (_pendingConfiguration.TryGetScalar("quality", out var quality))
            Quality = quality;
        if (_pendingConfiguration.TryGetScalar("contextWindowTier", out var contextTier))
            ContextWindowTier = contextTier;
        if (_pendingConfiguration.TryGetScalar("agent", out var agent))
            AgentName = agent;
        if (_pendingConfiguration.TryGetScalar("agentId", out var agentId))
            AgentValue = agentId;
        if (_pendingConfiguration.TryGetScalar("project", out var project))
            ProjectName = project;
        if (_pendingConfiguration.TryGetScalar("projectId", out var projectId))
            ProjectValue = projectId;
        if (_pendingConfiguration.TryGetScalar("worktree", out var worktree)
            && bool.TryParse(worktree, out var useWorktree))
        {
            _worktreeChoiceExplicit = true;
            UseWorktree = useWorktree;
        }

        foreach (var skill in _pendingConfiguration.AddSkills)
            AddPendingChip(SkillChips, AvailableSkills, skill, "✦");
        foreach (var mcp in _pendingConfiguration.AddMcps)
            AddPendingChip(McpChips, AvailableMcps, mcp, "⚙");
        foreach (var skill in _pendingConfiguration.RemoveSkills)
            RemoveChipByName(SkillChips, skill);
        foreach (var mcp in _pendingConfiguration.RemoveMcps)
            RemoveChipByName(McpChips, mcp);
    }

    private static void AddPendingChip(
        ObservableCollection<StrataComposerChip> target,
        IReadOnlyList<StrataComposerChip> catalog,
        string name,
        string fallbackGlyph)
    {
        if (target.Any(chip => string.Equals(chip.Name, name, StringComparison.OrdinalIgnoreCase)))
            return;

        var known = catalog.FirstOrDefault(chip =>
            string.Equals(chip.Name, name, StringComparison.OrdinalIgnoreCase));
        target.Add(known ?? new StrataComposerChip(name, fallbackGlyph));
    }

    private void ApplyUploadedAttachment(
        ChatSurfaceIdentity surface,
        PendingAttachment attachment)
    {
        surface = ResolveSurface(surface);

        if (IsCurrentSurface(surface))
        {
            if (!Attachments.Contains(attachment))
                Attachments.Add(attachment);

            ClearPendingRetryIfPayloadChanged();
            OnPropertyChanged(nameof(HasAttachments));
            SendCommand.NotifyCanExecuteChanged();
            StoreDraft(surface, CaptureCurrentDraft());
            return;
        }

        var draft = GetDraft(surface);
        var attachments = draft.Attachments.ToList();
        if (!attachments.Contains(attachment))
            attachments.Add(attachment);

        var retry = draft.PendingRetry;
        if (retry is not null && !retry.Payload.Matches(draft.PromptText, attachments))
            retry = null;

        StoreDraft(surface, draft with
        {
            Attachments = [.. attachments],
            PendingRetry = retry
        });
    }

    private void ApplyUploadError(ChatSurfaceIdentity surface, string error)
    {
        surface = ResolveSurface(surface);
        if (IsCurrentSurface(surface))
        {
            ErrorText = error;
            StoreDraft(surface, CaptureCurrentDraft());
            return;
        }

        var draft = GetDraft(surface);
        StoreDraft(surface, draft with { ErrorText = error });
    }

    private void ClearPendingRetryIfPayloadChanged()
    {
        if (_pendingRetry is { } pending &&
            !pending.Payload.Matches(PromptText, Attachments))
        {
            _pendingRetry = null;
            OnPropertyChanged(nameof(CanChooseWorktree));
        }
    }

    /// <summary>
    /// Applies everything the user picked while the surface was empty. Called after the desktop
    /// creates a chat, before the first reply lands.
    /// </summary>
    public async Task FlushPendingConfigurationAsync()
    {
        await _configurationGate.WaitAsync();
        try
        {
            if (_pendingConfiguration.IsEmpty || ChatId == Guid.Empty)
                return;

            var surface = CurrentSurface;
            var activation = _surfaceActivationGeneration;
            var hostGeneration = Volatile.Read(ref _hostGeneration);
            var pending = _pendingConfiguration.Clone();
            // Worktree selection is consumed only by the first send. Configure-chat has no
            // worktree operation, so flushing other staged settings must leave this intent armed.
            pending.RemoveScalar("worktree");
            if (!_hasAuthoritativeTranscript || HasConfirmedEmptyHistory)
            {
                // Project and workspace jointly decide where the first coding turn runs. Applying
                // one before the other can strand an old worktree under a new project. Keep them
                // coupled while history is unknown and while the chat is authoritatively empty.
                pending.RemoveScalar("project");
                pending.RemoveScalar("projectId");
            }
            if (pending.IsEmpty)
                return;

            var command = new RemoteCommand(RemoteProtocol.Actions.ConfigureChat)
                .With("chatId", ChatId.ToString());
            pending.ApplyTo(command, includeCreationAliases: false);

            RemoteCommandResult result;
            try
            {
                result = await _sink.SendCommandAsync(command);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[Mobile] Pending chat configuration failed: {ex}");
                return;
            }

            if (!IsCurrentHost(hostGeneration) || !result.Ok)
                return;

            if (IsCurrentSurfaceActivation(surface, activation))
            {
                _pendingConfiguration.RemoveApplied(pending);
                return;
            }

            var draft = GetDraft(surface);
            var remaining = draft.Configuration.Clone();
            remaining.RemoveApplied(pending);
            StoreDraft(surface, draft with { Configuration = remaining });
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    /// <summary>Clears everything so a chat switch never flashes the previous conversation.</summary>
    public void Reset(Guid chatId, string title, string? model = null)
    {
        var previousSurface = CurrentSurface;
        SaveDraft(previousSurface);

        if (chatId == Guid.Empty)
            _blankSurfaceGeneration++;

        // Drafts belong to chats, but async completions belong to this particular activation. A user
        // who leaves chat A and later returns to A must not receive A's stale command/upload result.
        _surfaceActivationGeneration++;
        CancelFileSuggestionSearch();
        AvailableFiles.Clear();

        _applyingServerState = true;
        try
        {
            _pendingConfiguration.Clear();
            _pendingRetry = null;

            ChatId = chatId;
            Title = title;
            DisposeTurns();
            Turns.Clear();
            ChatSurfaceReset?.Invoke();
            Suggestions.Clear();
            SkillChips.Clear();
            McpChips.Clear();
            QualityLevels.Clear();
            ContextWindowTiers.Clear();
            ErrorText = null;
            TranscriptErrorText = null;
            PlanContent = null;
            IsActivitySheetOpen = false;
            SelectedActivity = null;
            IsSourcesSheetOpen = false;
            SelectedSourceAnswer = null;
            ResetVisibleActivityProgress();
            Model = model ?? (chatId == Guid.Empty ? _preferredModel : null);
            Quality = null;
            ContextWindowTier = null;
            AgentValue = null;
            AgentName = null;
            ProjectValue = null;
            ProjectName = null;
            UseWorktree = false;
            _worktreeChoiceExplicit = false;
            IsBusy = false;
            IsStreaming = false;
            IsLoading = false;
            IsUploading = false;
            _revision = -1;
            _pendingEchoBaselineRevision = null;
            _hasAuthoritativeTranscript = chatId == Guid.Empty;
            WindowStartMessageIndex = 0;
            WindowEndMessageIndex = 0;
            TotalRawMessageCount = 0;
            HasEarlierMessages = false;
            HasLaterMessages = false;
            IsLatestWindow = true;
            HasNewerActivity = false;

            RestoreDraft(CurrentSurface);
        }
        finally
        {
            _applyingServerState = false;
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanChooseWorktree));
        OnPropertyChanged(nameof(ShowThinking));
        OnPropertyChanged(nameof(HasQualityLevels));
        OnPropertyChanged(nameof(HasContextWindowTiers));
        RefreshEffortLevels();
        RefreshContextWindowTiers();

        if (ChatId != Guid.Empty && HasPendingConfiguration)
            _ = FlushPendingConfigurationAsync();
    }

    public void ResetHostState()
    {
        Interlocked.Increment(ref _hostGeneration);
        _pendingStopRequestIds.Clear();
        _drafts.Clear();
        _surfaceMappings.Clear();
        Reset(Guid.Empty, "New chat");
        _drafts.Clear();
        _surfaceMappings.Clear();
    }

    public void ApplyTranscript(RemoteTranscript transcript, long? statusVersionAtRequest = null)
    {
        if (transcript.ChatId != ChatId)
            return;

        var incomingEpoch = string.IsNullOrWhiteSpace(transcript.RevisionEpoch)
            ? null
            : transcript.RevisionEpoch;
        var epochChanged = incomingEpoch is not null
                           && !string.Equals(
                               incomingEpoch,
                               _revisionEpoch,
                               StringComparison.Ordinal);

        // Revisions are monotonic only inside one running desktop generation. A server restart starts
        // a new epoch at a low revision, while a legacy server with no epoch keeps the old monotonic
        // comparison so an absent additive field never weakens stale-response rejection.
        if (!epochChanged && transcript.Revision < _revision)
            return;

        if (incomingEpoch is not null)
            _revisionEpoch = incomingEpoch;
        _revision = transcript.Revision;
        Title = transcript.Title;
        WindowStartMessageIndex = transcript.WindowStartMessageIndex;
        WindowEndMessageIndex = transcript.WindowEndMessageIndex;
        TotalRawMessageCount = transcript.TotalRawMessageCount;
        HasEarlierMessages = transcript.HasEarlierMessages;
        HasLaterMessages = transcript.HasLaterMessages;
        IsLatestWindow = transcript.IsLatestWindow;
        if (transcript.IsLatestWindow)
            HasNewerActivity = false;
        TranscriptErrorText = null;

        // A transcript request already in flight when Send was tapped can arrive after the
        // optimistic bubble with the same pre-send revision. Reconcile its authoritative turns
        // without counting the local echo, then put the echo back at the tail. The first newer
        // revision replaces it normally.
        var retainPendingEcho = _pendingEchoBaselineRevision is { } echoRevision
                                && !epochChanged
                                && transcript.Revision <= echoRevision;
        var pendingEcho = retainPendingEcho
            ? Turns.FirstOrDefault(turn => turn.Id == EchoTurnId)
            : null;
        var selectedActivityId = IsActivitySheetOpen ? SelectedActivity?.ActivityId : null;
        var technicalDetailsWereVisible =
            SelectedActivity?.IsTechnicalDetailsVisible == true;
        var selectedSourceAnswerId =
            IsSourcesSheetOpen ? SelectedSourceAnswer?.Id : null;
        if (pendingEcho is not null)
            Turns.Remove(pendingEcho);
        else
        {
            ClearPendingEchoes();
        }

        for (var i = 0; i < transcript.Turns.Count; i++)
        {
            var incoming = transcript.Turns[i];

            if (i < Turns.Count && Turns[i].Id == incoming.Id)
            {
                Turns[i].Apply(incoming);
                continue;
            }

            var created = new TranscriptTurnViewModel(
                incoming.Id,
                OpenActivityAsync,
                OpenSources,
                ResolveInlineImagesAsync);
            created.Apply(incoming);

            if (i < Turns.Count)
            {
                var replaced = Turns[i];
                Turns[i] = created;
                replaced.Dispose();
            }
            else
                Turns.Add(created);
        }

        while (Turns.Count > transcript.Turns.Count)
        {
            var removed = Turns[^1];
            Turns.RemoveAt(Turns.Count - 1);
            removed.Dispose();
        }

        if (pendingEcho is not null)
            Turns.Add(pendingEcho);

        if (selectedActivityId is { Length: > 0 })
        {
            var reconciledActivity = Turns
                .SelectMany(turn => turn.Items)
                .OfType<ActivitySummaryItemViewModel>()
                .FirstOrDefault(activity => string.Equals(
                    activity.ActivityId,
                    selectedActivityId,
                    StringComparison.Ordinal));
            if (reconciledActivity is null)
            {
                IsActivitySheetOpen = false;
                SelectedActivity = null;
            }
            else
            {
                if (!ReferenceEquals(SelectedActivity, reconciledActivity))
                {
                    reconciledActivity.IsTechnicalDetailsVisible =
                        technicalDetailsWereVisible;
                    SelectedActivity = reconciledActivity;
                }

                if (!reconciledActivity.DetailsLoaded
                    && !reconciledActivity.IsLoadingDetails)
                {
                    _ = OpenActivityAsync(reconciledActivity);
                }
            }
        }
        if (selectedSourceAnswerId is { Length: > 0 })
        {
            var reconciledAnswer = Turns
                .SelectMany(turn => turn.Items)
                .OfType<AssistantItemViewModel>()
                .FirstOrDefault(answer => string.Equals(
                    answer.Id,
                    selectedSourceAnswerId,
                    StringComparison.Ordinal));
            if (reconciledAnswer is null || !reconciledAnswer.HasSources)
            {
                IsSourcesSheetOpen = false;
                SelectedSourceAnswer = null;
            }
            else
            {
                SelectedSourceAnswer = reconciledAnswer;
            }
        }

        _hasAuthoritativeTranscript = true;
        ReconcilePendingRetry(transcript, epochChanged);
        if (!retainPendingEcho && HasNewVisibleResponseActivity(transcript))
            MarkVisibleResponseActivity();

        if (transcript.TotalRawMessageCount > 0)
        {
            // Another surface may have completed the first turn while this phone still had a
            // pre-chat workspace choice staged. Once authoritative history exists, creating or
            // detaching a worktree is no longer a valid deferred operation.
            _pendingConfiguration.RemoveScalar("worktree");
            if (transcript.Status.UsesWorktree)
            {
                _pendingConfiguration.RemoveScalar("project");
                _pendingConfiguration.RemoveScalar("projectId");
            }
        }

        if (statusVersionAtRequest is null || statusVersionAtRequest == StatusVersion)
            ApplyStatus(transcript.Status, trackVersion: false);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanChooseWorktree));
        RefreshWorktreeChoice();
        if (transcript.TotalRawMessageCount > 0 && HasPendingConfiguration)
            _ = FlushPendingConfigurationAsync();
    }

    private void ReconcilePendingRetry(RemoteTranscript transcript, bool epochChanged)
    {
        if (_pendingRetry is not { } pending)
            return;

        var wasAccepted = transcript.Turns
            .SelectMany(static turn => turn.Items)
            .Any(item =>
                item.Kind == RemoteProtocol.ItemKinds.User &&
                string.Equals(item.RequestId, pending.RequestId, StringComparison.Ordinal));
        if (wasAccepted)
        {
            var draft = CaptureCurrentDraft();
            var remainingConfiguration = draft.Configuration.Clone();
            remainingConfiguration.RemoveApplied(pending.Configuration);
            var remainingAttachments = draft.Attachments
                .Where(attachment => !pending.Payload.Attachments.Contains(attachment))
                .ToArray();
            var completed = draft with
            {
                PromptText = string.Equals(
                    draft.PromptText,
                    pending.Payload.PromptText,
                    StringComparison.Ordinal)
                    ? ""
                    : draft.PromptText,
                Attachments = remainingAttachments,
                ErrorText = null,
                Configuration = remainingConfiguration,
                PendingRetry = null
            };

            StoreDraft(CurrentSurface, completed);
            ApplyDraft(completed, restoreSelections: true);
            return;
        }

        if (!epochChanged)
            return;

        var unsafeRetry = pending with { ReplayAllowed = false };
        var unresolved = CaptureCurrentDraft() with
        {
            ErrorText =
                "Lumi restarted before confirming this send. Refresh the transcript before retrying, or edit the message to send it as a new request.",
            PendingRetry = unsafeRetry
        };
        StoreDraft(CurrentSurface, unresolved);
        ApplyDraft(unresolved, restoreSelections: true);
    }

    public void MarkNewerActivityAvailable()
    {
        if (ChatId != Guid.Empty)
            HasNewerActivity = true;
    }

    public void InvalidateTranscriptAuthority()
    {
        if (ChatId == Guid.Empty)
            return;

        _hasAuthoritativeTranscript = false;
        // The invalidation may represent another surface completing the first turn. Do not keep a
        // first-turn-only workspace operation armed while history is unknown.
        _pendingConfiguration.RemoveScalar("worktree");
        OnPropertyChanged(nameof(CanChooseWorktree));
    }

    public void ApplyStatus(RemoteChatStatus status) => ApplyStatus(status, trackVersion: true);

    private void ApplyStatus(RemoteChatStatus status, bool trackVersion)
    {
        if (status.ChatId != Guid.Empty && status.ChatId != ChatId)
            return;
        if (trackVersion)
            Interlocked.Increment(ref _statusVersion);

        // Status and transcript frames are independent. The desktop can report busy and then idle
        // before the transcript carrying the first reasoning/tool/assistant row reaches the phone.
        // Preserve the working state across that gap; a visible response row, Stop, or an explicit
        // send failure is what ends the optimistic progress state.
        var reportsWorking = status.IsBusy || status.IsStreaming;
        var wasWorking = IsBusy || IsStreaming;
        if (reportsWorking && !wasWorking && !_hasVisibleResponseActivity)
            BeginAwaitingVisibleActivity();

        var statusChatId = status.ChatId == Guid.Empty ? ChatId : status.ChatId;
        var pendingStopCompleted = !reportsWorking &&
                                   statusChatId != Guid.Empty &&
                                   _pendingStopRequestIds.Remove(statusChatId);
        var holdingProgress = !pendingStopCompleted &&
                              _awaitingVisibleActivity &&
                              !reportsWorking &&
                              wasWorking;
        if (pendingStopCompleted)
        {
            ResetVisibleActivityProgress();
            ApplyWorkingState(isBusy: false, isStreaming: false);
        }
        else if (!holdingProgress)
        {
            ApplyWorkingState(status.IsBusy, status.IsStreaming);
            if (!reportsWorking)
                ResetVisibleActivityProgress();
        }

        StatusText = status.StatusText;
        ContextCurrentTokens = status.ContextCurrentTokens;
        ContextTokenLimit = status.ContextTokenLimit;
        PlanContent = status.PlanContent;

        _applyingServerState = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(status.Model))
                Model = status.Model;
            Quality = status.Quality;
            ContextWindowTier = status.ContextWindowTier;
            AgentValue = status.AgentId?.ToString();
            AgentName = status.AgentName;
            AgentGlyph = string.IsNullOrWhiteSpace(status.AgentGlyph) ? "◉" : status.AgentGlyph!;
            ProjectValue = status.ProjectId?.ToString();
            ProjectName = status.ProjectName;
            if (!_pendingConfiguration.TryGetScalar("worktree", out _))
            {
                // For an existing chat, both "local" and "worktree" are authoritative persisted
                // states. Project defaults apply only to a genuinely new surface or after the user
                // deliberately changes projects.
                _worktreeChoiceExplicit = ChatId != Guid.Empty;
                UseWorktree = status.UsesWorktree;
            }

            Sync(QualityLevels, status.QualityLevels);
            Sync(ContextWindowTiers, status.ContextWindowTiers);
            SyncChips(SkillChips, status.SkillNames, AvailableSkills, "✦");
            SyncChips(McpChips, status.McpNames, AvailableMcps, "⚙");
            if (status.HasComposerCatalogs)
            {
                SyncCatalog(AvailableAgents, status.AvailableAgents, "◉");
                SyncCatalog(AvailableSkills, status.AvailableSkills, "✦");
                SyncCatalog(AvailableMcps, status.AvailableMcps, "⚙");
                SyncCatalog(AvailableProjects, status.AvailableProjects, "▤");
            }
        }
        finally
        {
            _applyingServerState = false;
        }

        if (!_pendingConfiguration.IsEmpty)
        {
            _restoringDraftState = true;
            try
            {
                RestorePendingWorkspaceSelections();
            }
            finally
            {
                _restoringDraftState = false;
            }
        }

        OnPropertyChanged(nameof(HasQualityLevels));
        OnPropertyChanged(nameof(HasContextWindowTiers));
        RefreshEffortLevels();
        RefreshContextWindowTiers();

        // The sheet's rows carry their own selected state, so they have to be re-derived whenever
        // the desktop changes the selection or the catalog underneath us.
        RefreshPickerOptions();
        OnPropertyChanged(nameof(ModelSummary));
        OnPropertyChanged(nameof(RunSettingsSummary));
        OnPropertyChanged(nameof(EffortIndex));
        OnPropertyChanged(nameof(EffortMax));
        OnPropertyChanged(nameof(EffortLabel));

        Sync(Suggestions, status.Suggestions);
        OnPropertyChanged(nameof(Starters));
    }

    private void ApplyWorkingState(bool isBusy, bool isStreaming)
    {
        // Keep the combined working state continuously true when the server changes phase from
        // thinking/tools to text streaming (or back). Clearing the old flag before setting the new
        // one creates a false idle -> active edge that makes transcript observers jump to the tail.
        if (isBusy)
            IsBusy = true;
        if (isStreaming)
            IsStreaming = true;
        if (!isBusy)
            IsBusy = false;
        if (!isStreaming)
            IsStreaming = false;
    }

    private void RestorePendingWorkspaceSelections()
    {
        if (_pendingConfiguration.TryGetScalar("project", out var project))
            ProjectName = project;
        if (_pendingConfiguration.TryGetScalar("projectId", out var projectId))
            ProjectValue = projectId;
        if (_pendingConfiguration.TryGetScalar("worktree", out var worktree)
            && bool.TryParse(worktree, out var useWorktree))
        {
            _worktreeChoiceExplicit = true;
            UseWorktree = useWorktree;
        }
    }

    /// <summary>
    /// Loads the pickers' catalogs from a snapshot. Kept separate from <see cref="ApplyStatus"/>
    /// because the catalogs describe the PC (which models and skills exist at all), while the status
    /// describes this one chat (which of them it is using).
    /// </summary>
    public void ApplyCatalogs(RemoteSettings settings)
    {
        _preferredModel = settings.PreferredModel;
        Sync(AvailableModels, settings.AvailableModels);

        _modelDisplayNames.Clear();
        foreach (var entry in settings.ModelDisplayNames)
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
                continue;

            _modelDisplayNames[entry[..separator]] = entry[(separator + 1)..];
        }

        _modelEfforts.Clear();
        foreach (var entry in settings.ModelReasoningEfforts)
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
                continue;

            _modelEfforts[entry[..separator]] =
                entry[(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        }

        _modelContextTiers.Clear();
        foreach (var entry in settings.ModelContextWindowTiers)
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
                continue;

            _modelContextTiers[entry[..separator]] =
                entry[(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        }

        RefreshEffortLevels();
        RefreshContextWindowTiers();
        RefreshPickerOptions();
        OnPropertyChanged(nameof(ModelDisplayName));
        OnPropertyChanged(nameof(ModelSummary));
        OnPropertyChanged(nameof(RunSettingsSummary));

        if (ChatId == Guid.Empty && string.IsNullOrWhiteSpace(Model))
            Model = _preferredModel;
    }

    public void ApplyLibraryCatalogs(RemoteLibrary library, bool reconcileProjectSelection = true)
    {
        SyncCatalog(
            AvailableAgents,
            library.Lumis.Select(item => new RemoteChip
            {
                Name = item.Name,
                Glyph = item.IconGlyph,
                Description = item.Description,
                Value = item.Id.ToString()
            }).ToList(),
            "◉");
        SyncCatalog(
            AvailableSkills,
            library.Skills.Select(item => new RemoteChip
            {
                Name = item.Name,
                Glyph = item.IconGlyph,
                Description = item.Description,
                Value = item.Id.ToString()
            }).ToList(),
            "✦");
        SyncCatalog(
            AvailableMcps,
            library.McpServers.Where(item => item.IsEnabled).Select(item => new RemoteChip
            {
                Name = item.Name,
                Glyph = "⚙",
                Description = item.Description,
                Value = item.Id.ToString()
            }).ToList(),
            "⚙");
        ApplyProjectCatalog(library.Projects, reconcileProjectSelection);
    }

    public void ApplyProjectCatalog(
        IReadOnlyList<RemoteProject> projects,
        bool reconcileSelection = true)
    {
        _projectCatalog.Clear();
        foreach (var project in projects)
            _projectCatalog[project.Id] = project;

        SyncCatalog(
            AvailableProjects,
            projects.Select(item => new RemoteChip
            {
                Name = item.Name,
                Glyph = "▤",
                Description = item.Instructions,
                Value = item.Id.ToString()
            }).ToList(),
            "▤");

        if (reconcileSelection
            && ProjectName is { Length: > 0 } projectName
            && !projects.Any(project =>
                ProjectValue is { Length: > 0 } projectValue
                    ? string.Equals(project.Id.ToString(), projectValue, StringComparison.Ordinal)
                    : string.Equals(project.Name, projectName, StringComparison.Ordinal)))
        {
            _applyingServerState = true;
            try
            {
                ProjectName = null;
                ProjectValue = null;
            }
            finally
            {
                _applyingServerState = false;
            }
            _pendingConfiguration.RemoveScalar("project");
            _pendingConfiguration.RemoveScalar("projectId");
        }

        RefreshWorktreeChoice();
        RefreshPickerOptions();
    }

    /// <summary>Which efforts each model supports, so a chat that does not exist yet can still offer them.</summary>
    private readonly Dictionary<string, string[]> _modelEfforts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> _modelContextTiers = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _modelDisplayNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Recomputes the effort levels for the selected model from the PC's model catalog.
    ///
    /// <para>Which efforts exist is a property of the MODEL, not of the chat — only the current
    /// selection is chat state. Deriving them here therefore works before a chat exists, and also
    /// repairs an open chat whose status arrived without them (the desktop only reports levels for
    /// the chat it currently has active). Falls back to whatever the status supplied when the
    /// catalog has nothing to say about the model, so a BYOK or unknown model is left alone.</para>
    /// </summary>
    private void RefreshEffortLevels()
    {
        if (Model is not { Length: > 0 } model || !_modelEfforts.TryGetValue(model, out var levels))
        {
            // Nothing authoritative to apply. For a chat that does not exist yet there is also no
            // status to fall back on, so clear rather than leave a previous model's levels behind.
            if (ChatId == Guid.Empty)
                Sync(QualityLevels, []);
            else
                return;
        }
        else
        {
            Sync(QualityLevels, levels);
        }

        OnPropertyChanged(nameof(HasQualityLevels));
        OnPropertyChanged(nameof(EffortMax));
        OnPropertyChanged(nameof(EffortIndex));
        OnPropertyChanged(nameof(ModelSummary));
        OnPropertyChanged(nameof(RunSettingsSummary));
    }

    private void RefreshContextWindowTiers()
    {
        if (Model is not { Length: > 0 } model || !_modelContextTiers.TryGetValue(model, out var tiers))
        {
            if (ChatId == Guid.Empty)
                Sync(ContextWindowTiers, []);
            else
                return;
        }
        else
        {
            Sync(ContextWindowTiers, tiers);
        }

        OnPropertyChanged(nameof(HasContextWindowTiers));
        OnPropertyChanged(nameof(ContextWindowLabel));
        OnPropertyChanged(nameof(RunSettingsSummary));
        RefreshPickerOptions();
    }

    private static void SyncCatalog(
        ObservableCollection<StrataComposerChip> target,
        IReadOnlyList<RemoteChip> source,
        string fallbackGlyph)
    {
        if (target.Count == source.Count
            && target.Select(static chip => (chip.Name, chip.Value))
                .SequenceEqual(source.Select(static chip => (chip.Name, chip.Value))))
            return;

        target.Clear();
        foreach (var chip in source)
        {
            target.Add(new StrataComposerChip(
                chip.Name,
                string.IsNullOrWhiteSpace(chip.Glyph) ? fallbackGlyph : chip.Glyph!,
                SecondaryText: chip.Description,
                Value: chip.Value));
        }
    }

    /// <summary>
    /// Replaces a collection only when it actually differs. Rebuilding it wholesale would drop the
    /// composer's current selection on every status frame — several times a second on a busy chat.
    /// </summary>
    private static void Sync(ObservableCollection<string> target, IReadOnlyList<string> source)
    {
        if (Matches(target, source, static value => value))
            return;

        target.Clear();
        foreach (var value in source)
            target.Add(value);
    }

    /// <summary>Mirrors names onto chips, borrowing each glyph from the catalog when it is known.</summary>
    private static void SyncChips(
        ObservableCollection<StrataComposerChip> target,
        IReadOnlyList<string> names,
        IReadOnlyList<StrataComposerChip> catalog,
        string fallbackGlyph)
    {
        if (Matches(target, names, static chip => chip.Name))
            return;

        target.Clear();
        foreach (var name in names)
        {
            var glyph = catalog.FirstOrDefault(chip =>
                string.Equals(chip.Name, name, StringComparison.OrdinalIgnoreCase))?.Glyph;
            target.Add(new StrataComposerChip(name, string.IsNullOrWhiteSpace(glyph) ? fallbackGlyph : glyph!));
        }
    }

    private static bool Matches<T>(
        IReadOnlyList<T> target,
        IReadOnlyList<string> source,
        Func<T, string> key)
    {
        if (target.Count != source.Count)
            return false;

        for (var i = 0; i < source.Count; i++)
        {
            if (!string.Equals(key(target[i]), source[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Applies hot streaming text without a transcript refetch. Returns false when the target row is
    /// not present yet, which tells the caller to pull a fresh transcript instead.
    /// </summary>
    public bool ApplyDelta(RemoteStreamDelta delta)
    {
        if (delta.ChatId != ChatId)
            return true;

        foreach (var turn in Turns)
        {
            foreach (var item in turn.Items)
            {
                if (item.Id != delta.ItemId)
                    continue;

                switch (item)
                {
                    case AssistantItemViewModel assistant:
                        if (!ApplyStreamChunk(
                                assistant.SourceText,
                                delta,
                                RemoteProtocol.MobileAssistantTextLimit,
                                out var assistantText))
                        {
                            return false;
                        }
                        assistant.ApplyStreamText(assistantText);
                        MarkVisibleResponseActivity();
                        return true;
                    case ReasoningItemViewModel reasoning:
                        if (!ApplyStreamChunk(
                                reasoning.Text,
                                delta,
                                RemoteProtocol.MobileReasoningTextLimit,
                                out var reasoningText))
                        {
                            return false;
                        }
                        reasoning.Text = reasoningText;
                        reasoning.IsStreaming = true;
                        MarkVisibleResponseActivity();
                        return true;
                    default:
                        return false;
                }

            }
        }

        return false;
    }

    private static bool ApplyStreamChunk(
        string current,
        RemoteStreamDelta delta,
        int limit,
        out string text)
    {
        if (delta.Offset == -1)
        {
            text = RemoteProtocol.TruncateForMobile(delta.Text, limit) ?? "";
            return true;
        }

        if (delta.Offset != current.Length)
        {
            text = current;
            return false;
        }

        text = RemoteProtocol.TruncateForMobile(current + delta.Text, limit) ?? "";
        return true;
    }

    /// <summary>
    /// Send, or steer if a turn is already running.
    ///
    /// <para>With <c>SteerWhileBusy</c> the composer routes a mid-turn Send through the ordinary
    /// send command, so this has to notice the busy state itself. On the desktop steering injects
    /// the draft into the live turn; the remote path cannot do that, so it means abort-and-replace —
    /// which is the honest behaviour on a phone and, crucially, no longer just bounces off the
    /// server with "that chat is already running".</para>
    /// </summary>
    private bool CanSend() =>
        !IsLoading
        && IsLatestWindow
        && !IsUploading
        && (PromptText.Trim().Length > 0 || HasAttachments);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task SendAsync() => SendAsync(steer: IsBusy || IsStreaming);

    private async Task SendAsync(bool steer)
    {
        await _configurationGate.WaitAsync();
        try
        {
            await SendCoreAsync(steer);
        }
        finally
        {
            _configurationGate.Release();
            if (!_pendingConfiguration.IsEmpty && ChatId != Guid.Empty)
                _ = FlushPendingConfigurationAsync();
        }
    }

    private async Task SendCoreAsync(bool steer)
    {
        var hostGeneration = Volatile.Read(ref _hostGeneration);
        var surface = CurrentSurface;
        var activation = _surfaceActivationGeneration;
        if (surface.ChatId != Guid.Empty)
            _pendingStopRequestIds.Remove(surface.ChatId);
        LockBlankProjectSelection(surface);
        var draftText = PromptText;
        var text = draftText.Trim();

        // An attachment on its own is a complete message: "look at this".
        if (text.Length == 0 && !HasAttachments)
            return;

        var attached = Attachments.ToArray();
        var payload = new SendPayload(draftText, attached);
        var matchingRetry = _pendingRetry is { } pendingRetry &&
                            pendingRetry.Payload.Matches(draftText, attached)
            ? pendingRetry
            : null;
        if (matchingRetry is not null &&
            (!matchingRetry.ReplayAllowed ||
             _now() - matchingRetry.CreatedAtUtc >= PendingRetryReplayWindow))
        {
            ErrorText =
                "Lumi can no longer safely replay that send. Refresh the chat to confirm its outcome, or edit the message before sending again.";
            return;
        }
        if (surface.IsBlank && matchingRetry is null)
            _pendingStopBlankGeneration = null;
        var requestId = matchingRetry?.RequestId ?? Guid.NewGuid().ToString("N");
        // Reusing an idempotency key means replaying the same command. Configuration changed after
        // the timeout remains pending and is flushed after the original request is acknowledged.
        var pendingConfiguration = matchingRetry?.Configuration.Clone() ??
                                   _pendingConfiguration.Clone();
        var effectiveSteer = matchingRetry?.Steer ?? steer;

        PromptText = "";
        ErrorText = null;

        // Lumi reads files by path, so an attachment reaches it as a line naming where the file is.
        // Uploading put the bytes on the PC; this is what tells Lumi to go and look.
        var prompt = BuildSendPrompt(payload);

        Attachments.Clear();
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(CanChooseWorktree));
        SendCommand.NotifyCanExecuteChanged();

        // Optimistically flip to busy so the composer switches to Stop and the thinking row appears
        // with no round-trip lag. It stays until a real response row can replace it.
        BeginAwaitingVisibleActivity();
        IsBusy = true;

        // Paint the bubble NOW. The real one only arrives after an HTTP round trip, an SSE
        // invalidation and a transcript refetch — three hops on a phone's Wi-Fi — and staring at an
        // unchanged screen after tapping send is the single most damaging kind of latency, because
        // the user cannot tell whether the tap registered. The echo is replaced by the server's own
        // copy on the next transcript, so nothing here can drift.
        var echo = AddPendingEcho(text.Length > 0 ? text : attached[0].FileName, effectiveSteer);
        if (surface.ChatId != Guid.Empty)
            ChatActivitySubmitted?.Invoke(surface.ChatId, echo?.Items.OfType<UserTurnItemViewModel>().FirstOrDefault()?.Text ?? text);

        var command = new RemoteCommand(RemoteProtocol.Actions.SendMessage)
        {
            RequestId = requestId
        }.With("message", prompt);
        if (surface.ChatId != Guid.Empty)
        {
            command.With("chatId", surface.ChatId.ToString());
        }
        else
        {
            // Say so explicitly. Sending no chatId leaves the desktop to guess, and its guess is
            // "whichever chat I currently have open" — which posted the message into an unrelated
            // conversation. A deferred chat has no id yet, so the intent has to travel as a flag.
            command.With("newChat", "true");
        }

        pendingConfiguration.ApplyTo(command, includeCreationAliases: surface.IsBlank);

        if (effectiveSteer)
            command.With("steer", "true");

        RemoteCommandResult result;
        try
        {
            result = await _sink.SendCommandAsync(command);
        }
        catch (Exception ex)
        {
            if (!IsCurrentHost(hostGeneration))
                return;

            if (surface.IsBlank && _pendingStopBlankGeneration == surface.BlankGeneration)
                _pendingStopBlankGeneration = null;
            RestoreFailedSendForSurface(
                surface,
                activation,
                payload,
                pendingConfiguration,
                requestId,
                false,
                effectiveSteer,
                "Lumi could not send that message.",
                echo);
            if (!surface.IsBlank)
                ChatActivitySubmissionFailed?.Invoke(surface.ChatId);
            Trace.TraceWarning($"[Mobile] Send failed: {ex}");
            return;
        }

        if (!IsCurrentHost(hostGeneration))
            return;

        if (surface.IsBlank
            && result.ChatId is { } createdChatId
            && createdChatId != Guid.Empty
            && _pendingStopBlankGeneration == surface.BlankGeneration)
        {
            _pendingStopBlankGeneration = null;
            await StopCreatedChatAsync(createdChatId);
        }
        else if (surface.IsBlank
                 && !result.Ok
                 && !IsAmbiguousFailure(result)
                 && _pendingStopBlankGeneration == surface.BlankGeneration)
        {
            _pendingStopBlankGeneration = null;
        }

        var targetSurface = result.ChatId is { } resultChatId && resultChatId != Guid.Empty
            ? MapSurfaceToChat(surface, resultChatId)
            : ResolveSurface(surface);

        if (!result.Ok)
        {
            RestoreFailedSendForSurface(
                surface,
                activation,
                payload,
                pendingConfiguration,
                result.RequestId ?? requestId,
                IsAmbiguousFailure(result),
                effectiveSteer,
                result.Error ?? "Lumi could not send that message.",
                echo);
            if (!surface.IsBlank)
                ChatActivitySubmissionFailed?.Invoke(surface.ChatId);

            if (surface.IsBlank &&
                result.ChatId is { } failedCreatedId &&
                failedCreatedId != Guid.Empty)
            {
                ChatCreated?.Invoke(failedCreatedId, surface.BlankGeneration);
            }

            return;
        }

        CompleteSuccessfulSend(targetSurface, activation, pendingConfiguration);

        if (surface.IsBlank && result.ChatId is { } created && created != Guid.Empty)
            ChatCreated?.Invoke(created, surface.BlankGeneration);
        else if (surface.IsBlank)
            ReleaseBlankProjectSelection(surface);
    }

    private void RestoreFailedSendForSurface(
        ChatSurfaceIdentity surface,
        long activation,
        SendPayload sentPayload,
        PendingChatConfiguration sentConfiguration,
        string requestId,
        bool preserveRequestIdentity,
        bool steer,
        string error,
        TranscriptTurnViewModel? echo)
    {
        var originSurface = surface;
        surface = ResolveSurface(surface);
        var isOriginalActivation = IsCurrentActivation(activation);
        var shouldApplyToCurrent = IsCurrentSurface(surface);
        var draft = shouldApplyToCurrent ? CaptureCurrentDraft() : GetDraft(surface);
        var attachments = draft.Attachments.ToList();
        foreach (var attachment in sentPayload.Attachments)
        {
            if (!attachments.Contains(attachment))
                attachments.Add(attachment);
        }

        var promptText = draft.PromptText.Length == 0
            ? sentPayload.PromptText
            : draft.PromptText;
        var configuration = draft.Configuration.Clone();
        configuration.MergeFrom(
            sentConfiguration,
            overwriteScalars: false,
            overwriteCollectionValues: false);
        var retry = preserveRequestIdentity && sentPayload.Matches(promptText, attachments)
            ? new PendingRetry(
                requestId,
                sentPayload,
                sentConfiguration.Clone(),
                steer,
                _now(),
                ReplayAllowed: true)
            : null;
        var restored = new DraftState(
            promptText,
            [.. attachments],
            error,
            configuration,
            retry);

        if (!preserveRequestIdentity)
            ReleaseBlankProjectSelection(originSurface);

        StoreDraft(surface, restored);
        if (!shouldApplyToCurrent)
            return;

        ApplyDraft(restored, restoreSelections: true);
        if (isOriginalActivation)
        {
            ResetVisibleActivityProgress();
            IsBusy = false;
            RemovePendingEcho(echo);
        }
    }

    private void CompleteSuccessfulSend(
        ChatSurfaceIdentity surface,
        long activation,
        PendingChatConfiguration sentConfiguration)
    {
        surface = ResolveSurface(surface);
        var isActive = IsCurrentSurfaceActivation(surface, activation);
        var draft = isActive ? CaptureCurrentDraft() : GetDraft(surface);
        var remainingConfiguration = draft.Configuration.Clone();
        remainingConfiguration.RemoveApplied(sentConfiguration);
        var completed = draft with
        {
            ErrorText = null,
            Configuration = remainingConfiguration,
            PendingRetry = null
        };

        StoreDraft(surface, completed);
        if (isActive)
        {
            ErrorText = null;
            _pendingConfiguration.RemoveApplied(sentConfiguration);
            _pendingRetry = null;
            OnPropertyChanged(nameof(CanChooseWorktree));
            if (ChatId != Guid.Empty && HasPendingConfiguration)
                _ = FlushPendingConfigurationAsync();
        }
    }

    private static bool IsAmbiguousFailure(RemoteCommandResult result) =>
        result.IsTimeout || result.IsOutcomeUnknown;

    private bool _awaitingVisibleActivity;
    private bool _hasVisibleResponseActivity;
    private readonly HashSet<string> _visibleActivityBaselineItemIds = new(StringComparer.Ordinal);
    private string? _visibleActivityBaselineTurnId;
    private long? _pendingEchoBaselineRevision;
    private long? _pendingStopBlankGeneration;

    private void BeginAwaitingVisibleActivity()
    {
        _hasVisibleResponseActivity = false;
        _visibleActivityBaselineItemIds.Clear();

        var latestTurn = Turns.LastOrDefault(turn => turn.Id != EchoTurnId);
        _visibleActivityBaselineTurnId = latestTurn?.Id;
        if (latestTurn is not null)
        {
            foreach (var item in latestTurn.Items)
            {
                if (IsVisibleResponseActivity(item))
                    _visibleActivityBaselineItemIds.Add(item.Id);
            }
        }

        SetAwaitingVisibleActivity(true);
    }

    private void SetAwaitingVisibleActivity(bool value)
    {
        if (_awaitingVisibleActivity == value)
            return;

        _awaitingVisibleActivity = value;
        OnPropertyChanged(nameof(ShowThinking));
    }

    private void MarkVisibleResponseActivity()
    {
        _hasVisibleResponseActivity = true;
        _visibleActivityBaselineItemIds.Clear();
        _visibleActivityBaselineTurnId = null;
        SetAwaitingVisibleActivity(false);
    }

    private void ResetVisibleActivityProgress()
    {
        _hasVisibleResponseActivity = false;
        _visibleActivityBaselineItemIds.Clear();
        _visibleActivityBaselineTurnId = null;
        SetAwaitingVisibleActivity(false);
    }

    private bool HasNewVisibleResponseActivity(RemoteTranscript transcript)
    {
        if (transcript.Turns.Count == 0)
            return false;

        var latestTurn = transcript.Turns[^1];
        var isNewTurn = !string.Equals(
            latestTurn.Id,
            _visibleActivityBaselineTurnId,
            StringComparison.Ordinal);
        foreach (var item in latestTurn.Items)
        {
            if (!IsVisibleResponseActivity(item))
                continue;

            if (isNewTurn || !_visibleActivityBaselineItemIds.Contains(item.Id))
                return true;
        }

        return false;
    }

    private static bool IsVisibleResponseActivity(RemoteTranscriptItem item)
    {
        if (item.Kind == RemoteProtocol.ItemKinds.User)
            return false;

        return item.Kind is not (RemoteProtocol.ItemKinds.Assistant or RemoteProtocol.ItemKinds.Reasoning)
               || item.IsStreaming
               || !string.IsNullOrWhiteSpace(item.Text);
    }

    private static bool IsVisibleResponseActivity(TranscriptItemViewModel item) => item switch
    {
        UserTurnItemViewModel => false,
        AssistantItemViewModel assistant => assistant.IsStreaming || assistant.Text.Length > 0,
        ReasoningItemViewModel reasoning => reasoning.IsStreaming || reasoning.Text.Length > 0,
        _ => true
    };

    private async Task StopCreatedChatAsync(Guid chatId)
    {
        try
        {
            var result = await _sink.SendCommandAsync(
                new RemoteCommand(RemoteProtocol.Actions.StopGeneration)
                    .With("chatId", chatId.ToString()));
            if (!result.Ok)
                Trace.TraceWarning($"[Mobile] Could not stop newly created chat {chatId}: {result.Error}");
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Mobile] Could not stop newly created chat {chatId}: {ex}");
        }
    }

    /// <summary>Turn id used for the optimistic echo, so a real transcript can replace it wholesale.</summary>
    private const string EchoTurnId = "__pending_echo__";

    private TranscriptTurnViewModel? AddPendingEcho(string text, bool steer)
    {
        _pendingEchoBaselineRevision = _revision;
        var turn = new TranscriptTurnViewModel(
            EchoTurnId,
            OpenActivityAsync,
            OpenSources,
            ResolveInlineImagesAsync);
        turn.Items.Add(new UserTurnItemViewModel(new RemoteTranscriptItem
        {
            Id = EchoTurnId,
            Kind = RemoteProtocol.ItemKinds.User,
            Text = text,
            SteerState = steer ? "Steering" : null
        }));

        Turns.Add(turn);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanChooseWorktree));
        return turn;
    }

    private async Task<string> ResolveInlineImagesAsync(
        string messageId,
        string markdown,
        IReadOnlyList<RemoteInlineImage> images,
        CancellationToken cancellationToken)
    {
        if (_sink is not IRemoteMarkdownImageSink imageSink
            || ChatId == Guid.Empty
            || !Guid.TryParseExact(messageId, "N", out var parsedMessageId))
        {
            return markdown;
        }

        var chatId = ChatId;
        var downloads = images.Select(async image =>
        {
            var path = await imageSink.DownloadMarkdownImageAsync(
                chatId,
                parsedMessageId,
                image.Index,
                image.FileName,
                cancellationToken);
            return (image.Index, Path: path);
        });
        var resolved = await Task.WhenAll(downloads);
        var replacements = resolved
            .Where(result => !string.IsNullOrWhiteSpace(result.Path))
            .ToDictionary(
                result => result.Index,
                result => result.Path!);

        return RemoteMarkdownImages.RewriteTargets(markdown, replacements);
    }

    private void RemovePendingEcho(TranscriptTurnViewModel? turn)
    {
        if (turn is null)
            return;

        var removed = Turns.Remove(turn);
        _pendingEchoBaselineRevision = null;
        if (removed)
        {
            turn.Dispose();
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(CanChooseWorktree));
        }
    }

    /// <summary>
    /// Drops any optimistic echo still in the list. Called before applying a server transcript: the
    /// server's copy of that message is authoritative, and leaving the echo would double it.
    /// </summary>
    private void ClearPendingEchoes()
    {
        var removed = false;
        for (var i = Turns.Count - 1; i >= 0; i--)
        {
            if (Turns[i].Id == EchoTurnId)
            {
                var removedTurn = Turns[i];
                Turns.RemoveAt(i);
                removedTurn.Dispose();
                removed = true;
            }
        }

        _pendingEchoBaselineRevision = null;
        if (removed)
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(CanChooseWorktree));
        }
    }

    /// <summary>
    /// Raised when sending into an empty surface caused the desktop to create a chat, even if the
    /// first turn itself failed after creation.
    /// </summary>
    public event Action<Guid, long>? ChatCreated;

    public event Action<Guid, string>? ChatActivitySubmitted;

    public event Action<Guid>? ChatActivitySubmissionFailed;

    /// <summary>Raised whenever chat activation clears the visible transcript, even for the same id.</summary>
    public event Action? ChatSurfaceReset;

    private void DisposeTurns()
    {
        foreach (var turn in Turns)
            turn.Dispose();
    }

    /// <summary>
    /// Promotes the blank surface that issued a send to its server-assigned chat id.
    /// The generation check matters because the UI-thread post may run after the user tapped New Chat
    /// again, even though the command result itself originally completed on the right surface.
    /// </summary>
    public bool TryAdoptCreatedChat(Guid chatId, long blankGeneration)
    {
        if (chatId == Guid.Empty ||
            ChatId != Guid.Empty ||
            _blankSurfaceGeneration != blankGeneration)
        {
            return false;
        }

        var blankSurface = new ChatSurfaceIdentity(Guid.Empty, blankGeneration);
        MapSurfaceToChat(blankSurface, chatId);
        ReleaseBlankProjectSelection(blankSurface);
        ChatId = chatId;

        return true;
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (ChatId == Guid.Empty)
        {
            ResetVisibleActivityProgress();
            IsBusy = false;
            IsStreaming = false;
            _pendingStopBlankGeneration = _blankSurfaceGeneration;
            return;
        }

        var surface = CurrentSurface;
        var activation = _surfaceActivationGeneration;
        var chatId = ChatId;
        var previousStatus = StatusText;
        var requestId = _pendingStopRequestIds.TryGetValue(chatId, out var pendingRequestId)
            ? pendingRequestId
            : Guid.NewGuid().ToString("N");
        _pendingStopRequestIds[chatId] = requestId;
        StatusText = "Stopping…";
        var command = new RemoteCommand(RemoteProtocol.Actions.StopGeneration)
        {
            RequestId = requestId
        }.With("chatId", chatId.ToString());
        RemoteCommandResult result;
        try
        {
            result = await _sink.SendCommandAsync(command);
        }
        catch (Exception ex)
        {
            if (!IsCurrentSurfaceActivation(surface, activation))
                return;
            if (!_pendingStopRequestIds.TryGetValue(chatId, out var currentRequestId) ||
                !string.Equals(currentRequestId, requestId, StringComparison.Ordinal))
            {
                return;
            }
            ErrorText = $"Could not stop this turn: {ex.Message}";
            StatusText = "Stopping…";
            return;
        }

        if (!IsCurrentSurfaceActivation(surface, activation))
            return;
        if (!_pendingStopRequestIds.TryGetValue(chatId, out var activeRequestId) ||
            !string.Equals(activeRequestId, requestId, StringComparison.Ordinal))
        {
            return;
        }

        if (IsAmbiguousFailure(result))
        {
            ErrorText = result.Error;
            StatusText = "Stopping…";
            return;
        }

        if (_pendingStopRequestIds.TryGetValue(chatId, out var completedRequestId) &&
            string.Equals(completedRequestId, requestId, StringComparison.Ordinal))
        {
            _pendingStopRequestIds.Remove(chatId);
        }
        if (!result.Ok)
        {
            ErrorText = result.Error ?? "Lumi could not stop this turn.";
            StatusText = previousStatus;
            return;
        }

        ResetVisibleActivityProgress();
        IsBusy = false;
        IsStreaming = false;
        StatusText = null;
        if (!string.IsNullOrWhiteSpace(result.Error))
            ErrorText = result.Error;
    }

    /// <summary>
    /// Steering: replace the running turn with what the user just typed. The composer raises this
    /// when Send is pressed mid-turn, and without it the keystrokes went nowhere — the user could
    /// type while busy but never get the message in.
    /// </summary>
    [RelayCommand]
    private async Task StopAndSendAsync()
    {
        var text = PromptText.Trim();

        // With nothing typed, Send-while-busy just means Stop.
        if (text.Length == 0)
        {
            await StopAsync();
            return;
        }

        await SendAsync(steer: true);
    }

    [RelayCommand]
    private void UseSuggestion(string? suggestion)
    {
        if (!string.IsNullOrWhiteSpace(suggestion))
            PromptText = suggestion!;
    }

    [RelayCommand]
    private Task RemoveAgent()
    {
        if (string.IsNullOrEmpty(AgentName) && string.IsNullOrEmpty(AgentValue))
            PushConfiguration("agent", "");
        else
        {
            AgentValue = null;
            AgentName = null;
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task RemoveProject()
    {
        if (!CanChangeProjectSelection)
            return Task.CompletedTask;

        if (string.IsNullOrEmpty(ProjectName) && string.IsNullOrEmpty(ProjectValue))
            PushConfiguration("project", "");
        else
        {
            ProjectValue = null;
            ProjectName = null;
            ClearWorktreeChoice();
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task RemoveSkill(object? chip)
    {
        if (ChipName(chip) is not { Length: > 0 } name)
            return Task.CompletedTask;

        RemoveChipByName(SkillChips, name);
        return ConfigureAsync("removeSkills", name);
    }

    [RelayCommand]
    private Task RemoveMcp(object? chip)
    {
        if (ChipName(chip) is not { Length: > 0 } name)
            return Task.CompletedTask;

        RemoveChipByName(McpChips, name);
        return ConfigureAsync("removeMcps", name);
    }

    private static void RemoveChipByName(
        ObservableCollection<StrataComposerChip> chips,
        string name)
    {
        for (var i = chips.Count - 1; i >= 0; i--)
        {
            if (string.Equals(chips[i].Name, name, StringComparison.OrdinalIgnoreCase))
                chips.RemoveAt(i);
        }
    }

    private static string? ChipName(object? chip) => chip switch
    {
        StrataComposerChip typed => typed.Name,
        string text => text,
        _ => null
    };

    private Task ConfigureAsync(string key, string value)
    {
        Interlocked.Increment(ref _statusVersion);
        _pendingConfiguration.AddValue(key, value);
        return ChatId == Guid.Empty
            ? Task.CompletedTask
            : FlushPendingConfigurationAsync();
    }

    public Task AnswerQuestionAsync(string questionId, string answer) =>
        _sink.SendCommandAsync(
            new RemoteCommand(RemoteProtocol.Actions.AnswerQuestion)
                .With("chatId", ChatId.ToString())
                .With("questionId", questionId)
                .With("answer", answer));

    private readonly record struct ChatSurfaceIdentity(Guid ChatId, long BlankGeneration)
    {
        public bool IsBlank => ChatId == Guid.Empty;
    }

    private sealed record DraftState(
        string PromptText,
        PendingAttachment[] Attachments,
        string? ErrorText,
        PendingChatConfiguration Configuration,
        PendingRetry? PendingRetry)
    {
        public static DraftState Empty =>
            new("", [], null, new PendingChatConfiguration(), null);

        public bool IsEmpty =>
            PromptText.Length == 0 &&
            Attachments.Length == 0 &&
            string.IsNullOrWhiteSpace(ErrorText) &&
            Configuration.IsEmpty &&
            PendingRetry is null;

        public DraftState Copy() =>
            this with
            {
                Attachments = [.. Attachments],
                Configuration = Configuration.Clone(),
                PendingRetry = PendingRetry?.Copy()
            };
    }

    private sealed record SendPayload(string PromptText, PendingAttachment[] Attachments)
    {
        public bool Matches(string promptText, IReadOnlyList<PendingAttachment> attachments) =>
            string.Equals(PromptText, promptText, StringComparison.Ordinal) &&
            Attachments.SequenceEqual(attachments);

        public SendPayload Copy() =>
            this with { Attachments = [.. Attachments] };
    }

    private static string BuildSendPrompt(SendPayload payload)
    {
        var text = payload.PromptText.Trim();
        return payload.Attachments.Length == 0
            ? text
            : string.Join(
                "\n",
                new[] { text }
                    .Where(part => part.Length > 0)
                    .Concat(["Attached files:"])
                    .Concat(payload.Attachments.Select(file => file.Path)));
    }

    private sealed record PendingRetry(
        string RequestId,
        SendPayload Payload,
        PendingChatConfiguration Configuration,
        bool Steer,
        DateTimeOffset CreatedAtUtc,
        bool ReplayAllowed)
    {
        public PendingRetry Copy() =>
            this with
            {
                Payload = Payload.Copy(),
                Configuration = Configuration.Clone()
            };
    }

    private sealed class PendingChatConfiguration
    {
        private readonly Dictionary<string, string> _scalars = new(StringComparer.Ordinal);
        private readonly List<string> _addSkills = [];
        private readonly List<string> _addMcps = [];
        private readonly List<string> _removeSkills = [];
        private readonly List<string> _removeMcps = [];

        public IReadOnlyList<string> AddSkills => _addSkills;
        public IReadOnlyList<string> AddMcps => _addMcps;
        public IReadOnlyList<string> RemoveSkills => _removeSkills;
        public IReadOnlyList<string> RemoveMcps => _removeMcps;

        public bool IsEmpty =>
            _scalars.Count == 0 &&
            _addSkills.Count == 0 &&
            _addMcps.Count == 0 &&
            _removeSkills.Count == 0 &&
            _removeMcps.Count == 0;

        public void Clear()
        {
            _scalars.Clear();
            _addSkills.Clear();
            _addMcps.Clear();
            _removeSkills.Clear();
            _removeMcps.Clear();
        }

        public void SetScalar(string key, string value) => _scalars[key] = value;

        public bool TryGetScalar(string key, out string value) =>
            _scalars.TryGetValue(key, out value!);

        public void AddValue(string key, string value)
        {
            switch (key)
            {
                case "addSkills":
                    SetCollectionValue(_addSkills, _removeSkills, value);
                    break;
                case "addMcps":
                    SetCollectionValue(_addMcps, _removeMcps, value);
                    break;
                case "removeSkills":
                    SetCollectionValue(_removeSkills, _addSkills, value);
                    break;
                case "removeMcps":
                    SetCollectionValue(_removeMcps, _addMcps, value);
                    break;
                default:
                    SetScalar(key, value);
                    break;
            }
        }

        public PendingChatConfiguration Clone()
        {
            var clone = new PendingChatConfiguration();
            clone.CopyFrom(this);
            return clone;
        }

        public void CopyFrom(PendingChatConfiguration source)
        {
            if (ReferenceEquals(this, source))
                return;

            Clear();
            MergeFrom(
                source,
                overwriteScalars: true,
                overwriteCollectionValues: true);
        }

        public void MergeFrom(
            PendingChatConfiguration source,
            bool overwriteScalars,
            bool overwriteCollectionValues)
        {
            foreach (var (key, value) in source._scalars)
            {
                if (overwriteScalars || !_scalars.ContainsKey(key))
                    _scalars[key] = value;
            }

            MergeCollectionValues(
                _addSkills,
                _removeSkills,
                source._addSkills,
                source._removeSkills,
                overwriteCollectionValues);
            MergeCollectionValues(
                _addMcps,
                _removeMcps,
                source._addMcps,
                source._removeMcps,
                overwriteCollectionValues);
        }

        public void RemoveApplied(PendingChatConfiguration applied)
        {
            foreach (var (key, value) in applied._scalars)
            {
                if (_scalars.TryGetValue(key, out var current) &&
                    string.Equals(current, value, StringComparison.Ordinal))
                {
                    _scalars.Remove(key);
                }

            }

            foreach (var skill in applied._addSkills)
                Remove(_addSkills, skill);
            foreach (var mcp in applied._addMcps)
                Remove(_addMcps, mcp);
            foreach (var skill in applied._removeSkills)
                Remove(_removeSkills, skill);
            foreach (var mcp in applied._removeMcps)
                Remove(_removeMcps, mcp);
        }

        public void RemoveScalar(string key) => _scalars.Remove(key);

        public void ApplyTo(RemoteCommand command, bool includeCreationAliases)
        {
            foreach (var (key, value) in _scalars)
                command.With(key, value);

            // The creating-send path historically called this value reasoningEffort while the
            // configure-chat path calls it quality. Carry both until every desktop speaks one name.
            if (!_scalars.ContainsKey("reasoningEffort") &&
                _scalars.TryGetValue("quality", out var quality))
            {
                command.With("reasoningEffort", quality);
            }

            if (includeCreationAliases &&
                !_scalars.ContainsKey("projectName") &&
                _scalars.TryGetValue("project", out var project))
            {
                command.With("projectName", project);
            }

            if (_addSkills.Count > 0)
                command.WithList("addSkills", _addSkills);
            if (_addMcps.Count > 0)
                command.WithList("addMcps", _addMcps);
            if (_removeSkills.Count > 0)
                command.WithList("removeSkills", _removeSkills);
            if (_removeMcps.Count > 0)
                command.WithList("removeMcps", _removeMcps);
        }

        private static void MergeCollectionValues(
            List<string> targetAdds,
            List<string> targetRemoves,
            IReadOnlyList<string> sourceAdds,
            IReadOnlyList<string> sourceRemoves,
            bool overwriteValues)
        {
            foreach (var value in sourceAdds)
            {
                if (overwriteValues || !ContainsEither(targetAdds, targetRemoves, value))
                    SetCollectionValue(targetAdds, targetRemoves, value);
            }

            foreach (var value in sourceRemoves)
            {
                if (overwriteValues || !ContainsEither(targetAdds, targetRemoves, value))
                    SetCollectionValue(targetRemoves, targetAdds, value);
            }
        }

        private static void SetCollectionValue(
            List<string> selected,
            List<string> cancelled,
            string value)
        {
            Remove(cancelled, value);
            AddUnique(selected, value);
        }

        private static bool ContainsEither(
            IReadOnlyList<string> first,
            IReadOnlyList<string> second,
            string value) =>
            first.Any(existing =>
                string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)) ||
            second.Any(existing =>
                string.Equals(existing, value, StringComparison.OrdinalIgnoreCase));

        private static void AddUnique(List<string> values, string value)
        {
            if (!values.Any(existing =>
                    string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
            {
                values.Add(value);
            }
        }

        private static void Remove(List<string> values, string value) =>
            values.RemoveAll(existing =>
                string.Equals(existing, value, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Narrow seam so view models can issue commands without owning the transport.</summary>
public interface IRemoteCommandSink
{
    Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command);

    /// <summary>Sends a file to the PC. Lumi reads by path, so attaching means uploading first.</summary>
    Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content);
}

public interface IRemoteLibraryDetailSink
{
    Task<RemoteLibraryItem?> GetLibraryItemAsync(string resource, string identifier);
}

public interface IRemoteCatalogRefreshSink
{
    Task RefreshCatalogsAsync();
}

public interface IRemoteActivityDetailSink
{
    Task<RemoteActivityDetails?> GetActivityDetailsAsync(Guid chatId, string activityId);
}

public interface IRemoteMarkdownImageSink
{
    Task<string?> DownloadMarkdownImageAsync(
        Guid chatId,
        Guid messageId,
        int imageIndex,
        string fileName,
        CancellationToken cancellationToken);
}

public interface IRemoteFileSuggestionSink
{
    Task<RemoteFileSuggestions?> GetFileSuggestionsAsync(
        Guid? chatId,
        Guid? projectId,
        string query,
        CancellationToken cancellationToken);
}

/// <summary>A file already on the PC, waiting to be referenced by the next message.</summary>
/// <param name="FileName">What to show the user.</param>
/// <param name="Path">Where it landed on the PC, which is what Lumi needs.</param>
public sealed record PendingAttachment(string FileName, string Path);

/// <summary>A one-tap conversation starter offered on the empty chat canvas.</summary>
/// <param name="Glyph">Emoji shown ahead of the label.</param>
/// <param name="Text">The prompt text placed into the composer when tapped.</param>
public sealed record ChatStarter(string IconData, string Text);

/// <summary>One row in a picker sheet, carrying whether it is the current selection.</summary>
/// <param name="Name">The value, shown as the row's label.</param>
/// <param name="IsSelected">Whether this is the active choice, so the row can show a tick.</param>
public sealed record PickerOption(string Name, bool IsSelected, string? DisplayName = null)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
}
