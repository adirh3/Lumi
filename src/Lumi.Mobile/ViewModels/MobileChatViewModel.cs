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
    private readonly IRemoteCommandSink _sink;
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private string? _preferredModel;
    private long _revision = -1;
    private string? _revisionEpoch;
    private long _hostGeneration;
    private long _blankSurfaceGeneration;
    private long _surfaceActivationGeneration;
    private long _statusVersion;
    private readonly Dictionary<ChatSurfaceIdentity, DraftState> _drafts = [];
    private readonly Dictionary<ChatSurfaceIdentity, ChatSurfaceIdentity> _surfaceMappings = [];
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

    // ── Composer configuration. Setting any of these configures the chat on the PC. ──
    [ObservableProperty] private string? _model;
    [ObservableProperty] private string? _quality;
    [ObservableProperty] private string? _contextWindowTier;
    [ObservableProperty] private string? _agentName;
    [ObservableProperty] private string? _agentValue;
    [ObservableProperty] private string _agentGlyph = "◉";
    [ObservableProperty] private string? _projectName;
    [ObservableProperty] private string? _projectValue;

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

    public MobileChatViewModel(IRemoteCommandSink sink)
    {
        _sink = sink;

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
        IsRunSettingsSheetOpen ||
        IsModelSheetOpen ||
        IsContextSheetOpen ||
        IsEffortSheetOpen ||
        IsPlanOpen;

    /// <summary>Closes the visually topmost chat sheet.</summary>
    internal bool DismissTopmostSheet()
    {
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

    partial void OnProjectNameChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(ProjectValue))
            PushIdentityConfiguration("project", value ?? "", "projectId");
    }

    partial void OnProjectValueChanged(string? value) =>
        PushIdentityConfiguration("projectId", value ?? "", "project");

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

        _applyingServerState = true;
        try
        {
            _pendingConfiguration.Clear();
            _pendingRetry = null;

            ChatId = chatId;
            Title = title;
            Turns.Clear();
            Suggestions.Clear();
            SkillChips.Clear();
            McpChips.Clear();
            QualityLevels.Clear();
            ContextWindowTiers.Clear();
            ErrorText = null;
            TranscriptErrorText = null;
            PlanContent = null;
            ResetVisibleActivityProgress();
            Model = model ?? (chatId == Guid.Empty ? _preferredModel : null);
            Quality = null;
            ContextWindowTier = null;
            AgentValue = null;
            AgentName = null;
            ProjectValue = null;
            ProjectName = null;
            IsBusy = false;
            IsStreaming = false;
            IsLoading = false;
            IsUploading = false;
            _revision = -1;
            _pendingEchoBaselineRevision = null;
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

            var created = new TranscriptTurnViewModel(incoming.Id);
            created.Apply(incoming);

            if (i < Turns.Count)
                Turns[i] = created;
            else
                Turns.Add(created);
        }

        while (Turns.Count > transcript.Turns.Count)
            Turns.RemoveAt(Turns.Count - 1);

        if (pendingEcho is not null)
            Turns.Add(pendingEcho);

        if (!retainPendingEcho && HasNewVisibleResponseActivity(transcript))
            MarkVisibleResponseActivity();

        if (statusVersionAtRequest is null || statusVersionAtRequest == StatusVersion)
            ApplyStatus(transcript.Status, trackVersion: false);
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void MarkNewerActivityAvailable()
    {
        if (ChatId != Guid.Empty)
            HasNewerActivity = true;
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

        var holdingProgress = _awaitingVisibleActivity && !reportsWorking && wasWorking;
        if (!holdingProgress)
        {
            IsBusy = status.IsBusy;
            IsStreaming = status.IsStreaming;
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
                                assistant.Text,
                                delta,
                                RemoteProtocol.MobileAssistantTextLimit,
                                out var assistantText))
                        {
                            return false;
                        }
                        assistant.Text = assistantText;
                        assistant.IsStreaming = true;
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
        var prompt = attached.Length == 0
            ? text
            : string.Join(
                "\n",
                new[] { text }
                    .Where(part => part.Length > 0)
                    .Concat(["Attached files:"])
                    .Concat(attached.Select(file => file.Path)));

        Attachments.Clear();
        OnPropertyChanged(nameof(HasAttachments));
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
                 && !IsTypedTimeout(result)
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
                IsTypedTimeout(result),
                effectiveSteer,
                result.Error ?? "Lumi could not send that message.",
                echo);

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
    }

    private void RestoreFailedSendForSurface(
        ChatSurfaceIdentity surface,
        long activation,
        SendPayload sentPayload,
        PendingChatConfiguration sentConfiguration,
        string requestId,
        bool isTimeout,
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
        var retry = isTimeout && sentPayload.Matches(promptText, attachments)
            ? new PendingRetry(requestId, sentPayload, sentConfiguration.Clone(), steer)
            : null;
        var restored = new DraftState(
            promptText,
            [.. attachments],
            error,
            configuration,
            retry);

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
            if (ChatId != Guid.Empty && HasPendingConfiguration)
                _ = FlushPendingConfigurationAsync();
        }
    }

    private static bool IsTypedTimeout(RemoteCommandResult result) => result.IsTimeout;

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
        var turn = new TranscriptTurnViewModel(EchoTurnId);
        turn.Items.Add(new UserTurnItemViewModel(new RemoteTranscriptItem
        {
            Id = EchoTurnId,
            Kind = RemoteProtocol.ItemKinds.User,
            Text = text,
            SteerState = steer ? "Steering" : null
        }));

        Turns.Add(turn);
        OnPropertyChanged(nameof(IsEmpty));
        return turn;
    }

    private void RemovePendingEcho(TranscriptTurnViewModel? turn)
    {
        if (turn is null)
            return;

        var removed = Turns.Remove(turn);
        _pendingEchoBaselineRevision = null;
        if (removed)
            OnPropertyChanged(nameof(IsEmpty));
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
                Turns.RemoveAt(i);
                removed = true;
            }
        }

        _pendingEchoBaselineRevision = null;
        if (removed)
            OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Raised when sending into an empty surface caused the desktop to create a chat, even if the
    /// first turn itself failed after creation.
    /// </summary>
    public event Action<Guid, long>? ChatCreated;

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
        StatusText = "Stopping…";
        RemoteCommandResult result;
        try
        {
            result = await _sink.SendCommandAsync(
                new RemoteCommand(RemoteProtocol.Actions.StopGeneration)
                    .With("chatId", chatId.ToString()));
        }
        catch (Exception ex)
        {
            if (!IsCurrentSurfaceActivation(surface, activation))
                return;
            ErrorText = $"Could not stop this turn: {ex.Message}";
            StatusText = previousStatus;
            return;
        }

        if (!IsCurrentSurfaceActivation(surface, activation))
            return;

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
        if (string.IsNullOrEmpty(ProjectName) && string.IsNullOrEmpty(ProjectValue))
            PushConfiguration("project", "");
        else
        {
            ProjectValue = null;
            ProjectName = null;
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

    private sealed record PendingRetry(
        string RequestId,
        SendPayload Payload,
        PendingChatConfiguration Configuration,
        bool Steer)
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
