using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Mobile.Layout;
using Lumi.Mobile.Services;
using Lumi.Remote.Protocol;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.ViewModels;

/// <summary>
/// What is showing over the chat. The chat surface is always mounted underneath; pages for the
/// other places sit over it so returning to a chat never costs a transcript rebuild.
/// </summary>
public enum MobilePage
{
    /// <summary>Nothing on top: the conversation owns the screen.</summary>
    Chat,

    Library,

    Settings,

    /// <summary>Full-screen search, the way every mobile app does it.</summary>
    Search
}

/// <summary>
/// The desktop's places, in the order its bar lists them. Library is a desktop place for files the
/// phone does not browse, so the phone offers the other four.
/// </summary>
public enum MobilePlace
{
    Chats,
    Jobs,
    Customize,
    Settings
}

/// <summary>A Settings page. None is the list itself on a phone; a wide window shows a page beside it.</summary>
public enum SettingsSection
{
    None,
    Appearance,
    Connection,
    About
}

public enum WebAppUpdateState
{
    Current,
    Downloading,
    Ready,
    Applying,
    Failed
}

/// <summary>A project the user can put the conversation into, straight from the drawer.</summary>
public sealed partial class ProjectPickViewModel : ObservableObject
{
    [ObservableProperty] private bool _isActive;

    public required Guid Id { get; init; }

    public required string Name { get; init; }
}

public sealed partial class ChatStartSetup(RemoteChatSetup setup) : ObservableObject
{
    // The desktop's tile: the project's initial on a tint of its hue (the hue the chat's mark will
    // wear once it starts), otherwise the agent's glyph or a sparkle on a quiet tile.
    private readonly ChatIdentityMark? _projectMark = setup.ProjectId is { } projectId
        ? new ChatIdentityMark(setup.Glyph, ChatIdentityMark.HueFor(projectId))
        : null;

    internal RemoteChatSetup Source { get; } = setup;
    public string Id => Source.Id;
    public Guid? ProjectId => Source.ProjectId;
    public Guid? AgentId => Source.AgentId;
    public string? Model => Source.Model;
    public string Title => Source.Title;
    public string Description => Source.Description;
    public string MarkGlyph => string.IsNullOrWhiteSpace(Source.Glyph) ? "✦" : Source.Glyph;
    public bool HasProjectMark => _projectMark is not null;
    public IBrush? MarkBackground => _projectMark?.Background;
    public IBrush? MarkForeground => _projectMark?.Foreground;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Source.Description);
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// The new-chat greeting split around the user's name, so the name can wear the accent as it does on
/// the desktop's launchpad. A greeting that does not contain the name stays whole in <see cref="Lead"/>.
/// </summary>
public sealed record WelcomeGreeting(string Lead, string Name, string Trail)
{
    public static WelcomeGreeting Split(string greeting, string? userName)
    {
        var name = userName?.Trim() ?? "";
        // The desktop inserts the name verbatim after the words of the greeting, so the last exact
        // match is the name rather than letters inside a word ("Ning" in "Good morning, Ning").
        var index = name.Length == 0 ? -1 : greeting.LastIndexOf(name, StringComparison.Ordinal);
        return index < 0
            ? new WelcomeGreeting(greeting, "", "")
            : new WelcomeGreeting(greeting[..index], name, greeting[(index + name.Length)..]);
    }
}


/// <summary>
/// Root view model: owns the transport, translates SSE frames into view-model state, and drives
/// adaptive navigation. Everything the phone renders hangs off this object.
/// </summary>
public sealed partial class MobileShellViewModel :
    ObservableObject,
    IRemoteCommandSink,
    IRemoteLibraryDetailSink,
    IRemoteCatalogRefreshSink,
    IRemoteChatPageSink,
    IRemoteActivityDetailSink,
    IRemoteMarkdownImageSink,
    IRemoteFileSuggestionSink,
    IAsyncDisposable
{
    private readonly IMobileSettingsStore _store;
    private readonly Action? _reloadWebApp = MobilePlatformServices.HostEnvironment.ReloadWebApp;
    private readonly Action? _applyWebAppUpdate = MobilePlatformServices.HostEnvironment.ApplyWebAppUpdate;
    private readonly Action? _checkWebAppUpdate = MobilePlatformServices.HostEnvironment.CheckWebAppUpdate;
    private readonly MobileConnectionSettings _settings;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RemoteConnectionSupervisor _connection;
    private CancellationTokenSource _connectionLifetime = new();
    // User actions (send, stop, answers, uploads) belong to the paired PC, not to the foreground
    // stream: leaving the app must not abandon a message the user already sent.
    private CancellationTokenSource _hostLifetime = new();
    private long _connectionGeneration;
    private readonly SemaphoreSlim _snapshotRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _sendReconciliationGate = new(1, 1);
    private long _sendReconciliationVersion;
    private IReadOnlyList<Guid> _sendReconciliationCandidates = [];
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private long _snapshotRefreshVersion;
    private long _subscriptionGeneration;
    private long _lifecycleGeneration;
    private bool _isApplicationActive = true;
    private MobilePage _lastObservedPage = MobilePage.Chat;
    private readonly Stack<PageNavigation> _pageHistory = new();
    private bool _chatActivationOwnsSubscription;
    private int _commandsInFlight;

    private readonly object _transcriptRefreshSync = new();
    private readonly Stack<int> _newerTranscriptCursors = new();
    private readonly Dictionary<Guid, int> _readWatermarks = [];
    private TranscriptRefreshRequest? _pendingTranscriptRefresh;
    private TranscriptRefreshRequest? _activeTranscriptRefreshRequest;
    private TranscriptRefreshRequest? _applyingTranscriptRefreshRequest;
    private CancellationTokenSource? _activeTranscriptRefreshCts;
    private Task? _transcriptRefreshLoop;
    private long _transcriptRequestVersion;
    private long _transcriptSurfaceGeneration;
    private bool _hasDeferredNewerActivity;
    private bool _canAdoptDesktopActiveChat = true;

    // Streaming deltas that arrived while their row was missing or a transcript fetch was in
    // flight. They are replayed once that transcript lands instead of each forcing another fetch.
    private const int MaxHeldDeltas = 512;
    private readonly List<HeldDelta> _heldDeltas = [];
    private long _heldDeltaSequence;

    // Snapshot sections the phone holds, by the desktop's content version. A reconnect names them
    // so the desktop leaves unchanged sections out of the bootstrap.
    private readonly object _sectionCacheSync = new();
    private readonly Dictionary<string, string> _sectionVersions = new(StringComparer.Ordinal);
    private RemoteChatPage? _cachedChatPage;
    private RemoteLibrary? _cachedLibrary;
    private RemoteSettings? _cachedSettings;

    private sealed record HeldDelta(long Sequence, RemoteStreamDelta Delta);

    internal int BootstrapSnapshotCount { get; private set; }

    private enum TranscriptRefreshKind
    {
        Refresh,
        Earlier,
        Newer,
        Latest
    }

    private enum SnapshotSource
    {
        ManualRefresh,
        Bootstrap,
        CatalogEvent
    }

    private sealed record TranscriptRefreshRequest(
        Guid ChatId,
        int? BeforeMessageIndex,
        int PreviousWindowEndMessageIndex,
        TranscriptRefreshKind Kind,
        long SurfaceGeneration,
        long Version,
        long StatusVersion,
        string? KnownVersion = null)
    {
        public bool IsNavigation =>
            Kind is TranscriptRefreshKind.Earlier
                or TranscriptRefreshKind.Newer
                or TranscriptRefreshKind.Latest;
    }

    /// <summary>True while the phone's link to the desktop is live.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLive))]
    [NotifyPropertyChangedFor(nameof(ShowConnectionBanner))]
    [NotifyPropertyChangedFor(nameof(ConnectionBannerText))]
    [NotifyPropertyChangedFor(nameof(ConnectionStateLabel))]
    private bool _isConnected;

    /// <summary>
    /// True when the *desktop* reports its own Copilot session is ready. This is a different fact
    /// from <see cref="IsConnected"/> (the phone-to-desktop link) and must never be merged into it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLive))]
    [NotifyPropertyChangedFor(nameof(ShowConnectionBanner))]
    [NotifyPropertyChangedFor(nameof(ConnectionBannerText))]
    [NotifyPropertyChangedFor(nameof(ConnectionStateLabel))]
    private bool _isHostReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectionBanner))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(ShowMenuButton))]
    [NotifyPropertyChangedFor(nameof(ShowHeaderNewChat))]
    [NotifyPropertyChangedFor(nameof(ShowBackToChats))]
    [NotifyPropertyChangedFor(nameof(CanManageOpenChat))]
    [NotifyPropertyChangedFor(nameof(CanChangeProjectScope))]
    [NotifyPropertyChangedFor(nameof(CanApplyWebAppUpdate))]
    [NotifyCanExecuteChangedFor(nameof(ApplyWebAppUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmWebAppUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenProjectPickerCommand))]
    private bool _isPaired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionBannerText))]
    private string _hostName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionBannerText))]
    private string? _connectionMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionBannerText))]
    [NotifyPropertyChangedFor(nameof(ConnectionStateLabel))]
    [NotifyPropertyChangedFor(nameof(ShowGatewaySignInAction))]
    [NotifyPropertyChangedFor(nameof(ShowRetryConnectionAction))]
    private bool _isGatewaySignInRequired;

    [ObservableProperty] private MobileLayoutState _layout = MobileLayoutState.From(390, 844);
    [ObservableProperty] private ThemePreference _theme = ThemePreference.System;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnglishLanguage))]
    [NotifyPropertyChangedFor(nameof(IsHebrewLanguage))]
    [NotifyPropertyChangedFor(nameof(LanguageRestartRequired))]
    private string _language = "";
    [ObservableProperty] private string _userName = "";

    /// <summary>The page stacked over the conversation, if any.</summary>
    [ObservableProperty] private MobilePage _page = MobilePage.Chat;

    /// <summary>
    /// Overlay navigation state. Expanded layouts use the remembered sidebar preference instead.
    /// </summary>
    [ObservableProperty] private bool _isDrawerOpen;

    /// <summary>User collapsed the docked sidebar to give the conversation the full width.</summary>
    [ObservableProperty] private bool _isSidebarCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNavigationCoveringContent))]
    private bool _isDrawerPresented;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNavigationCoveringContent))]
    private bool _isDrawerMoving;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyWebAppUpdate))]
    [NotifyCanExecuteChangedFor(nameof(ApplyWebAppUpdateCommand))]
    private bool _isModalSheetPresented;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanDragDrawer))]
    [NotifyPropertyChangedFor(nameof(CanApplyWebAppUpdate))]
    [NotifyCanExecuteChangedFor(nameof(ApplyWebAppUpdateCommand))]
    private bool _isProjectPickerOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWebAppUpdateBanner))]
    [NotifyPropertyChangedFor(nameof(WebAppUpdateTitle))]
    [NotifyPropertyChangedFor(nameof(WebAppUpdateMessage))]
    [NotifyPropertyChangedFor(nameof(IsWebAppUpdating))]
    [NotifyPropertyChangedFor(nameof(ShowApplyWebAppUpdateAction))]
    [NotifyPropertyChangedFor(nameof(ShowCheckWebAppUpdateAction))]
    [NotifyCanExecuteChangedFor(nameof(ApplyWebAppUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmWebAppUpdateCommand))]
    private WebAppUpdateState _webAppUpdateState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanApplyWebAppUpdate))]
    [NotifyPropertyChangedFor(nameof(ShowApplyWebAppUpdateAction))]
    [NotifyCanExecuteChangedFor(nameof(ApplyWebAppUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmWebAppUpdateCommand))]
    private bool _isWebAppUpdateConfirmationOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyWebAppUpdate))]
    private bool _isWebAppUpdateRequested;

    public MobileShellViewModel(
        LumiRemoteClient? client = null,
        ILumiDiscoveryClient? discovery = null,
        IMobileSettingsStore? store = null,
        Action<Action>? post = null,
        RemoteConnectionOptions? connection = null)
    {
        _store = store ?? new MobileSettingsStore();
        _settings = _store.Load();
        Strings.Load(_settings.Language);
        _language = Strings.Language;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));

        Client = client ?? new LumiRemoteClient(_settings.DeviceId, _settings.DeviceName);
        Discovery = discovery ?? new LumiDiscoveryClient();
        _connection = new RemoteConnectionSupervisor(connection);
        Client.ReconnectPolicy = _connection;
        Client.KnownSnapshotSections = GetKnownSnapshotSections;
        _connection.OfflineChanged += () => _post(() =>
        {
            IsConnectionOffline = _connection.IsOffline || Client.State == RemoteLinkState.Offline;
            RetryConnectionCommand.NotifyCanExecuteChanged();
        });
        _connection.NetworkRestored += OnNetworkRestored;

        Theme = Enum.TryParse<ThemePreference>(_settings.Theme, ignoreCase: true, out var theme) ? theme : ThemePreference.System;

        ChatList = new ChatListViewModel(this);
        SearchChatList = new ChatListViewModel(this);
        Chat = new MobileChatViewModel(this, _post);
        Library = new LibraryViewModel(this);
        NewChatBrief = Strings.Get("NewChatBrief");
        StartSetupsTitle = Strings.Get("UsualSetups");
        Connect = new ConnectViewModel(
            Client,
            Discovery,
            OnPairedAsync,
            MobilePlatformServices.HostEnvironment);
        Connect.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectViewModel.Step))
                OnPropertyChanged(nameof(CanGoBack));
        };
        IsSidebarCollapsed = _settings.IsSidebarCollapsed;

        ChatList.ChatActivated += OnChatActivated;
        ChatList.ChatRemoved += OnChatRemoved;
        SearchChatList.ChatActivated += OnChatActivated;
        Client.StreamFrameReceived += OnFrameReceived;
        Client.StateChanged += OnClientStateChanged;

        // The details sheet keeps the chat identity; the header binds to the live model directly.
        Chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MobileChatViewModel.ChatId)
                or nameof(MobileChatViewModel.CanChangeProjectSelection))
            {
                OnPropertyChanged(nameof(CanChangeProjectScope));
                OpenProjectPickerCommand.NotifyCanExecuteChanged();
            }

            if (e.PropertyName is nameof(MobileChatViewModel.HasChat)
                or nameof(MobileChatViewModel.Title))
            {
                OnPropertyChanged(nameof(HeaderTitle));
                OnPropertyChanged(nameof(ChatDetailsTitle));
            }
            if (e.PropertyName is nameof(MobileChatViewModel.HasChat)
                or nameof(MobileChatViewModel.ChatId))
            {
                OnPropertyChanged(nameof(CanManageOpenChat));
                OnPropertyChanged(nameof(CanGoBack));
            }
            if (e.PropertyName is nameof(MobileChatViewModel.ProjectName)
                or nameof(MobileChatViewModel.ProjectValue)
                or nameof(MobileChatViewModel.AgentName)
                or nameof(MobileChatViewModel.AgentGlyph))
            {
                OnPropertyChanged(nameof(ChatHeaderMark));
                OnPropertyChanged(nameof(HasChatHeaderMark));
            }
            if (e.PropertyName is nameof(MobileChatViewModel.IsRunSettingsSheetOpen))
            {
                OnPropertyChanged(nameof(CanPinOpenChat));
                OnPropertyChanged(nameof(OpenChatPinLabel));
            }
            if (e.PropertyName is nameof(MobileChatViewModel.HasChat)
                or nameof(MobileChatViewModel.IsBusy)
                or nameof(MobileChatViewModel.IsLoading)
                or nameof(MobileChatViewModel.CanChangeProjectSelection))
                UseStartSetupCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(MobileChatViewModel.ProjectValue)
                or nameof(MobileChatViewModel.AgentValue)
                or nameof(MobileChatViewModel.Model)
                or nameof(MobileChatViewModel.Quality)
                or nameof(MobileChatViewModel.UseWorktree)
                or nameof(MobileChatViewModel.ChatId))
                RefreshSetupSelection();

            if (e.PropertyName is nameof(MobileChatViewModel.IsEmpty))
                OnPropertyChanged(nameof(IsWelcomeVisible));

            if (e.PropertyName is nameof(MobileChatViewModel.HasOpenSheet))
            {
                OnPropertyChanged(nameof(CanGoBack));
                OnPropertyChanged(nameof(CanDragDrawer));
            }

            if (e.PropertyName is nameof(MobileChatViewModel.HasSidePanelOpen))
                OnSidePanelChanged();

            if (e.PropertyName is nameof(MobileChatViewModel.HasUnsentWork)
                or nameof(MobileChatViewModel.HasOpenSheet)
                or nameof(MobileChatViewModel.CanChooseWorktree))
            {
                NotifyWebAppUpdateSafetyChanged();
            }

            if (e.PropertyName == nameof(MobileChatViewModel.IsGitChangesOpen) && CanReadChatTranscript)
                _ = RefreshTranscriptAsync();

            if (e.PropertyName is nameof(MobileChatViewModel.IsBusy)
                or nameof(MobileChatViewModel.IsStreaming))
            {
                var isRunning = Chat.IsBusy || Chat.IsStreaming;
                ChatList.SetRunning(Chat.ChatId, isRunning);
                SearchChatList.SetRunning(Chat.ChatId, isRunning);
            }

            if (e.PropertyName == nameof(MobileChatViewModel.IsSessionActive))
            {
                ChatList.SetSessionActive(Chat.ChatId, Chat.IsSessionActive);
                SearchChatList.SetSessionActive(Chat.ChatId, Chat.IsSessionActive);
            }

            if (e.PropertyName is nameof(MobileChatViewModel.ChatId))
            {
                SearchChatList.SelectedChatId = Chat.ChatId;
                QueueSubscriptionUpdate();
            }

        };

        Library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LibraryViewModel.HasOpenSurface))
            {
                OnPropertyChanged(nameof(CanGoBack));
                OnPropertyChanged(nameof(CanDragDrawer));
            }
            if (e.PropertyName is nameof(LibraryViewModel.HasOpenSurface)
                or nameof(LibraryViewModel.HasUnsavedChanges)
                or nameof(LibraryViewModel.IsSaving)
                or nameof(LibraryViewModel.IsActionBusy))
            {
                NotifyWebAppUpdateSafetyChanged();
            }
        };

        // Typing into the empty launch surface makes the desktop create a chat; adopt it so the
        // reply streams into the transcript the user is already looking at, and replay anything
        // they configured before the chat existed.
        Chat.ChatCreated += (chatId, blankGeneration) => _post(() =>
        {
            if (!Chat.TryAdoptCreatedChat(chatId, blankGeneration))
                return;

            ChatList.SelectedChatId = chatId;
            PromoteActiveChat(chatId, LatestUserPreview(), isNewChat: true);
            ChatList.SetRunning(chatId, Chat.IsBusy || Chat.IsStreaming);
            if (IsDrawerVisible)
                _ = ChatList.RefreshFromServerAsync();
            QueueSubscriptionUpdate();
            _ = Chat.FlushPendingConfigurationAsync();
            _ = RefreshTranscriptAsync();
        });
        Chat.ChatActivitySubmitted += (chatId, preview) =>
            _post(() => PromoteActiveChat(chatId, preview));
        Chat.ChatActivitySubmissionFailed += _failedChatId =>
            _post(() =>
            {
                _ = ChatList.RefreshFromServerAsync();
            });
        Chat.SendCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Chat.SendCommand.IsRunning) && !Chat.SendCommand.IsRunning)
                _post(() =>
                {
                    QueueSubscriptionUpdate();
                    _ = ReconcilePendingSendsAsync();
                });
        };

        Library.CloseRequested += GoBack;
        Library.ScopeChanged += NotifyPlaceChanged;
        // A job's page opens the chat it runs in, as the desktop's job page does.
        Library.ChatRequested += (chatId, title) =>
        {
            Library.CloseViewCommand.Execute(null);
            OnChatActivated(chatId, title, null);
        };

        if (_settings is { BaseUrl.Length: > 0, Token.Length: > 0 })
        {
            Client.Configure(_settings.BaseUrl, _settings.Token);
            HostName = _settings.HostName;
            IsPaired = true;
        }
    }

    public LumiRemoteClient Client { get; }
    public bool SupportsInteractiveResponses => Client.SupportsInteractiveResponses;
    public bool SupportsModelPins => Client.SupportsModelPins;

    public ILumiDiscoveryClient Discovery { get; }

    public ConnectViewModel Connect { get; }

    public ChatListViewModel ChatList { get; }

    public ChatListViewModel SearchChatList { get; }

    public MobileChatViewModel Chat { get; }

    public LibraryViewModel Library { get; }

    /// <summary>Projects the user can put the conversation into, straight from the drawer.</summary>
    public ObservableCollection<ProjectPickViewModel> Projects { get; } = [];

    public bool HasProjects => Projects.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartSetups))]
    private IReadOnlyList<ChatStartSetup> _startSetups = [];

    public bool HasStartSetups => StartSetups.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Greeting))]
    [NotifyPropertyChangedFor(nameof(GreetingParts))]
    private bool _hasDesktopNewChat;
    [ObservableProperty] private string _newChatBrief = "";
    [ObservableProperty] private string _startSetupsTitle = "";
    private string? _desktopGreeting;

    internal void ApplyNewChatExperience(RemoteNewChatExperience? experience)
    {
        _desktopGreeting = experience?.Greeting;
        HasDesktopNewChat = experience is not null;
        NewChatBrief = experience?.Brief ?? Strings.Get("NewChatBrief");
        StartSetupsTitle = experience?.SetupsTitle ?? Strings.Get("UsualSetups");
        StartSetups = experience?.Setups.Take(3).Select(setup => new ChatStartSetup(setup)).ToArray() ?? [];
        Chat.ApplyNewChatStarters(experience?.Starters);
        RefreshSetupSelection();
        OnPropertyChanged(nameof(Greeting));
        OnPropertyChanged(nameof(GreetingParts));
    }

    private void RefreshSetupSelection()
    {
        foreach (var setup in StartSetups)
        {
            var source = setup.Source;
            setup.IsSelected = !Chat.HasChat
                && source.ProjectId?.ToString() == Chat.ProjectValue
                && source.AgentId?.ToString() == Chat.AgentValue
                && source.UseWorktree == Chat.UseWorktree
                && (source.Model is null || source.Model == Chat.Model)
                && (source.Quality is null || source.Quality == Chat.Quality);
        }
    }

    private bool CanUseStartSetup() => !Chat.HasChat && !Chat.IsBusy && !Chat.IsLoading && Chat.CanChangeProjectSelection;

    [RelayCommand(CanExecute = nameof(CanUseStartSetup))]
    private void UseStartSetup(ChatStartSetup? setup)
    {
        if (!CanUseStartSetup())
        {
            Chat.ErrorText = Strings.Get("SetupsBeforeFirstMessage");
            return;
        }
        if (setup is null || !StartSetups.Contains(setup))
        {
            Chat.ErrorText = Strings.Get("SetupUnavailable");
            return;
        }

        // The project lens goes first because it also sets a blank chat's project; the setup, or the
        // draft it replaced, then has the last word.
        if (!setup.IsSelected)
        {
            if (!Chat.HasDraftBeforeSetup)
            {
                _activeProjectBeforeSetup = ActiveProjectId;
                Chat.RememberDraftBeforeSetup();
            }
            ActiveProjectId = setup.ProjectId;
            Chat.ApplyStartSetup(setup.Source);
        }
        else if (Chat.HasDraftBeforeSetup)
        {
            // As on the desktop, tapping the applied setup again puts back the draft it replaced. A
            // draft that simply matches a setup has nothing to put back.
            ActiveProjectId = _activeProjectBeforeSetup;
            Chat.RestoreDraftBeforeSetup();
        }

        RefreshSetupSelection();
        Chat.ErrorText = null;
    }

    /// <summary>The chat list's project when a setup was first applied to the draft.</summary>
    private Guid? _activeProjectBeforeSetup;

    // ── Chat action sheet ────────────────────────────────────────────────────────────────────

    private long _chatActionsVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanDragDrawer))]
    [NotifyPropertyChangedFor(nameof(CanApplyWebAppUpdate))]
    [NotifyCanExecuteChangedFor(nameof(ApplyWebAppUpdateCommand))]
    private bool _isChatActionsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageActionChat))]
    [NotifyCanExecuteChangedFor(nameof(PinActionChatCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteActionChatCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameActionChatCommand))]
    private ChatListItemViewModel? _actionChat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChatActionList))]
    [NotifyPropertyChangedFor(nameof(ChatActionsTitle))]
    [NotifyPropertyChangedFor(nameof(CanApplyWebAppUpdate))]
    [NotifyCanExecuteChangedFor(nameof(ApplyWebAppUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveChatNameCommand))]
    private bool _isRenamingChat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChatActionList))]
    [NotifyPropertyChangedFor(nameof(ChatActionsTitle))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmDeleteActionChatCommand))]
    private bool _isConfirmingChatDelete;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveChatNameCommand))]
    private string _chatNameDraft = "";

    [ObservableProperty] private string? _chatActionError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageActionChat))]
    [NotifyCanExecuteChangedFor(nameof(PinActionChatCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteActionChatCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameActionChatCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveChatNameCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmDeleteActionChatCommand))]
    [NotifyPropertyChangedFor(nameof(CanApplyWebAppUpdate))]
    [NotifyCanExecuteChangedFor(nameof(ApplyWebAppUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmWebAppUpdateCommand))]
    private bool _isChatActionBusy;

    public string ActionChatTitle => ActionChat?.Title ?? "";

    public string PinActionLabel => Strings.Get(ActionChat?.IsPinned == true ? "UnpinChat" : "PinChat");

    public string ChatActionsTitle => Strings.Get(IsRenamingChat ? "RenameChat"
        : IsConfirmingChatDelete ? "DeleteChatQuestion" : "ChatActions");

    public bool ShowChatActionList => !IsRenamingChat && !IsConfirmingChatDelete;

    public bool CanManageActionChat => ActionChat is not null && !IsChatActionBusy;

    private bool CanSaveChatName => CanManageActionChat && IsRenamingChat
        && !string.IsNullOrWhiteSpace(ChatNameDraft);

    private bool CanConfirmChatDelete => CanManageActionChat && IsConfirmingChatDelete;

    [RelayCommand]
    private void OpenChatActions(ChatListItemViewModel? chat)
    {
        if (chat is null)
            return;

        _chatActionsVersion++;
        ActionChat = chat;
        CancelChatAction();
        IsChatActionsOpen = true;
    }

    [RelayCommand]
    private void CloseChatActions() => IsChatActionsOpen = false;

    [RelayCommand(CanExecute = nameof(CanManageActionChat))]
    private async Task PinActionChatAsync()
    {
        if (ActionChat is not { } chat)
            return;

        if (await RunChatActionAsync(new RemoteCommand(RemoteProtocol.Actions.PinChat)
                .With("chatId", chat.Id.ToString()).With("pinned", (!chat.IsPinned).ToString())))
        {
            IsChatActionsOpen = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageActionChat))]
    private void DeleteActionChat()
    {
        ChatActionError = null;
        IsConfirmingChatDelete = true;
    }

    [RelayCommand(CanExecute = nameof(CanConfirmChatDelete))]
    private async Task ConfirmDeleteActionChatAsync()
    {
        if (ActionChat is not { } chat)
            return;

        if (await RunChatActionAsync(new RemoteCommand(RemoteProtocol.Actions.DeleteChat)
                .With("chatId", chat.Id.ToString())))
        {
            IsChatActionsOpen = false;
            // A list that is not on screen hears of the deletion only when it reloads, so the open chat
            // leaves at once, as on the desktop, and a single pane returns to the list.
            var wasOpen = Chat.ChatId == chat.Id;
            OnChatRemoved(chat.Id);
            if (wasOpen && !CanDockDrawer)
                ShowHome();
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageActionChat))]
    private void RenameActionChat()
    {
        IsDrawerOpen = false;
        ChatActionError = null;
        ChatNameDraft = ActionChatTitle;
        IsRenamingChat = true;
    }

    [RelayCommand(CanExecute = nameof(CanSaveChatName))]
    private async Task SaveChatNameAsync()
    {
        if (ActionChat is not { } chat)
            return;

        var title = ChatNameDraft.Trim();
        if (await RunChatActionAsync(new RemoteCommand(RemoteProtocol.Actions.RenameChat)
                .With("chatId", chat.Id.ToString()).With("title", title)))
        {
            chat.Title = title;
            if (Chat.ChatId == chat.Id)
                Chat.Title = title;
            IsChatActionsOpen = false;
        }
    }

    [RelayCommand]
    private void CancelChatAction()
    {
        IsRenamingChat = false;
        IsConfirmingChatDelete = false;
        ChatActionError = null;
    }

    private async Task<bool> RunChatActionAsync(RemoteCommand command)
    {
        var version = _chatActionsVersion;
        var connection = Volatile.Read(ref _connectionGeneration);
        IsChatActionBusy = true;
        ChatActionError = null;
        try
        {
            var result = await SendCommandAsync(command);
            if (version != _chatActionsVersion || !IsChatActionsOpen
                || connection != Volatile.Read(ref _connectionGeneration))
                return false;

            if (!result.Ok)
                ChatActionError = result.Error ?? Strings.Get("ActionIncomplete");
            return result.Ok;
        }
        finally
        {
            IsChatActionBusy = false;
        }
    }

    partial void OnIsChatActionsOpenChanged(bool value)
    {
        if (!value)
        {
            _chatActionsVersion++;
            CancelChatAction();
        }
    }

    partial void OnActionChatChanged(ChatListItemViewModel? value)
    {
        OnPropertyChanged(nameof(ActionChatTitle));
        OnPropertyChanged(nameof(PinActionLabel));
    }

    /// <summary>
    /// Project scope filters history and becomes the destination for a pending new chat.
    /// The picker has an explicit All projects choice rather than toggling the selected project off.
    /// </summary>
    [RelayCommand]
    private void SelectProject(ProjectPickViewModel? project)
    {
        if (project is null)
            return;
        if (Chat.ChatId == Guid.Empty && !Chat.CanChangeProjectSelection)
            return;

        ActiveProjectId = project.Id;
        IsProjectPickerOpen = false;

        // Stay in the drawer: the point of picking a project is to then choose one of ITS chats,
        // and closing here would make the user reopen the drawer to do it.
    }

    /// <summary>The stable project currently filtering the drawer, or null for everything.</summary>
    [ObservableProperty] private Guid? _activeProjectId;

    public string? ActiveProject =>
        ActiveProjectId is { } id
            ? Projects.FirstOrDefault(project => project.Id == id)?.Name
            : null;

    public bool HasActiveProject => ActiveProjectId.HasValue;

    /// <summary>The chat list's title, as the desktop's project switcher names it.</summary>
    public string ProjectSwitcherTitle => ActiveProject ?? Strings.Get("AllChats");

    /// <summary>"4 projects" beside All chats; nothing once one project is the lens.</summary>
    public string ProjectSwitcherMeta => HasActiveProject || Projects.Count == 0
        ? ""
        : Projects.Count == 1 ? Strings.Get("ProjectCountOne") : Strings.Get("ProjectCountMany", Projects.Count);

    /// <summary>The open project's initial on its hue; empty while every chat is in view.</summary>
    public ChatIdentityMark ProjectSwitcherMark => ActiveProjectId is { } id && ActiveProject is { } name
        ? ChatIdentityMark.Create(id, name, null, null) ?? ChatIdentityMark.Empty
        : ChatIdentityMark.Empty;

    /// <summary>The open chat's identity in its title pill: agent glyph or project initial on its hue.</summary>
    public ChatIdentityMark ChatHeaderMark => ChatIdentityMark.Create(
        Guid.TryParse(Chat.ProjectValue, out var projectId) ? projectId : null,
        Chat.ProjectName, Chat.AgentName, Chat.AgentGlyph) ?? ChatIdentityMark.Empty;

    public bool HasChatHeaderMark => !ReferenceEquals(ChatHeaderMark, ChatIdentityMark.Empty);

    /// <summary>
    /// New chat is the top right of the chat's title bar in every layout, balancing the back or sidebar
    /// button at its left. A docked list beside the chat leaves it to the chat's bar.
    /// </summary>
    public bool ShowHeaderNewChat => IsPaired;

    /// <summary>
    /// The chat list carries New chat beside search only when it is a screen of its own; docked beside
    /// a chat, the chat's title bar has it.
    /// </summary>
    public bool ShowListNewChat => !CanDockDrawer;

    private void NotifyProjectSwitcherChanged()
    {
        OnPropertyChanged(nameof(ProjectSwitcherTitle));
        OnPropertyChanged(nameof(ProjectSwitcherMeta));
        OnPropertyChanged(nameof(ProjectSwitcherMark));
    }

    public bool CanChangeProjectScope =>
        IsPaired && (Chat.ChatId != Guid.Empty || Chat.CanChangeProjectSelection);

    [RelayCommand(CanExecute = nameof(CanChangeProjectScope))]
    private void OpenProjectPicker() => IsProjectPickerOpen = true;

    partial void OnIsPairedChanged(bool value)
    {
        if (!value)
            IsProjectPickerOpen = false;
    }

    partial void OnActiveProjectIdChanged(Guid? value)
    {
        ChatList.ProjectFilterId = value;
        if (Page == MobilePage.Search)
            SearchChatList.ProjectFilterId = value;
        OnPropertyChanged(nameof(ActiveProject));
        OnPropertyChanged(nameof(HasActiveProject));
        NotifyProjectSwitcherChanged();
        SyncProjectSelection();

        // A project chosen while composing a blank chat is both a history lens and the destination
        // for that pending conversation. Keep the visible composer chip and first-send payload in
        // sync immediately rather than waiting for another New Chat tap.
        if (Chat.ChatId == Guid.Empty)
        {
            Chat.ProjectValue = value?.ToString();
            Chat.ProjectName = ActiveProject;
        }
    }

    [RelayCommand]
    private void ClearProject()
    {
        if (Chat.ChatId == Guid.Empty && !Chat.CanChangeProjectSelection)
            return;

        ActiveProjectId = null;
        IsProjectPickerOpen = false;
    }

    private void SyncProjectSelection()
    {
        foreach (var project in Projects)
            project.IsActive = project.Id == ActiveProjectId;
    }

    /// <summary>Rebuilds the drawer's project list only when it actually differs.</summary>
    private void SyncProjects(IReadOnlyList<RemoteProject> projects)
    {
        var changed = Projects.Count != projects.Count;
        if (!changed)
        {
            for (var i = 0; i < projects.Count; i++)
            {
                if (Projects[i].Id == projects[i].Id
                    && string.Equals(Projects[i].Name, projects[i].Name, StringComparison.Ordinal))
                    continue;
                changed = true;
                break;
            }
        }

        if (changed)
        {
            Projects.Clear();
            foreach (var project in projects)
                Projects.Add(new ProjectPickViewModel { Id = project.Id, Name = project.Name });

            OnPropertyChanged(nameof(HasProjects));
            OnPropertyChanged(nameof(ActiveProject));
            NotifyProjectSwitcherChanged();
        }

        SyncProjectSelection();
    }

    private void ApplyLibrary(RemoteLibrary library, bool reconcileSelections = true)
    {
        Library.Apply(library);
        SyncProjects(library.Projects);
        Chat.ApplyLibraryCatalogs(library, reconcileSelections);

        if (reconcileSelections
            && ActiveProjectId is { } activeProjectId
            && !library.Projects.Any(project => project.Id == activeProjectId))
        {
            ActiveProjectId = null;
        }
    }

    public string DeviceName => _settings.DeviceName;

    public bool CanReloadWebApp => _reloadWebApp is not null;

    public bool ShowGatewaySignInAction => IsGatewaySignInRequired && CanReloadWebApp;

    public bool CanAutomaticallyRecoverGatewaySignIn =>
        IsPaired
        && CanReloadWebApp
        && Volatile.Read(ref _commandsInFlight) == 0
        && Page == MobilePage.Chat
        && !Chat.HasUnsentWork
        && !Chat.HasOpenSheet
        && !Library.HasUnsavedChanges
        && !Library.IsSaving
        && !IsRenamingChat
        && !IsChatActionBusy;

    private bool CanStartWebAppUpdate =>
        _applyWebAppUpdate is not null
        && _isApplicationActive
        && IsPaired
        && Volatile.Read(ref _commandsInFlight) == 0
        && !Chat.HasPendingOperation
        && !Library.IsSaving
        && !Library.IsActionBusy
        && !IsChatActionBusy;

    public bool CanApplyWebAppUpdate =>
        CanStartWebAppUpdate
        && (IsWebAppUpdateRequested
            || (!IsKeyboardOpen
                && !Chat.HasUnsentWork
                && !Chat.HasOpenSheet
                && !Library.HasOpenSurface
                && !IsModalSheetPresented
                && !IsProjectPickerOpen
                && !IsChatActionsOpen
                && !IsRenamingChat
                && !IsDisconnectConfirmationOpen
                && !IsWebAppUpdateConfirmationOpen));

    public bool ShowWebAppUpdateBanner =>
        _applyWebAppUpdate is not null && WebAppUpdateState != WebAppUpdateState.Current;

    public bool IsWebAppUpdating =>
        WebAppUpdateState is WebAppUpdateState.Downloading or WebAppUpdateState.Applying;

    public bool ShowApplyWebAppUpdateAction =>
        WebAppUpdateState == WebAppUpdateState.Ready && !IsWebAppUpdateConfirmationOpen;

    public bool ShowCheckWebAppUpdateAction => WebAppUpdateState == WebAppUpdateState.Failed;

    public string WebAppUpdateTitle => WebAppUpdateState switch
    {
        WebAppUpdateState.Downloading => Strings.Get("UpdateDownloading"),
        WebAppUpdateState.Ready => Strings.Get("UpdateReady"),
        WebAppUpdateState.Applying => Strings.Get("UpdateApplying"),
        WebAppUpdateState.Failed => Strings.Get("UpdateFailed"),
        _ => ""
    };

    public string WebAppUpdateMessage => WebAppUpdateState switch
    {
        WebAppUpdateState.Downloading => Strings.Get("UpdateDownloadingHelp"),
        WebAppUpdateState.Ready => Strings.Get("UpdateReadyHelp"),
        WebAppUpdateState.Applying => Strings.Get("UpdateApplyingHelp"),
        WebAppUpdateState.Failed => Strings.Get("UpdateFailedHelp"),
        _ => ""
    };

    private bool CanApplyReadyWebAppUpdate =>
        ShowApplyWebAppUpdateAction && CanStartWebAppUpdate;

    private bool CanConfirmWebAppUpdate =>
        IsWebAppUpdateConfirmationOpen
        && WebAppUpdateState == WebAppUpdateState.Ready
        && CanStartWebAppUpdate;

    private bool CanCheckWebAppUpdate => _checkWebAppUpdate is not null;

    partial void OnWebAppUpdateStateChanged(WebAppUpdateState value)
    {
        if (value != WebAppUpdateState.Ready)
            IsWebAppUpdateConfirmationOpen = false;
        if (value is WebAppUpdateState.Current or WebAppUpdateState.Downloading or WebAppUpdateState.Failed)
        {
            IsWebAppUpdateRequested = false;
            OnPropertyChanged(nameof(CanApplyWebAppUpdate));
        }
    }

    private void NotifyWebAppUpdateSafetyChanged()
    {
        OnPropertyChanged(nameof(CanApplyWebAppUpdate));
        ApplyWebAppUpdateCommand.NotifyCanExecuteChanged();
        ConfirmWebAppUpdateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanApplyReadyWebAppUpdate))]
    private void ApplyWebAppUpdate()
    {
        if (CanApplyWebAppUpdate)
            _applyWebAppUpdate?.Invoke();
        else
            IsWebAppUpdateConfirmationOpen = true;
    }

    [RelayCommand(CanExecute = nameof(CanConfirmWebAppUpdate))]
    private void ConfirmWebAppUpdate()
    {
        IsWebAppUpdateRequested = true;
        IsWebAppUpdateConfirmationOpen = false;
        NotifyWebAppUpdateSafetyChanged();
        _applyWebAppUpdate?.Invoke();
    }

    [RelayCommand]
    private void CancelWebAppUpdate() => IsWebAppUpdateConfirmationOpen = false;

    [RelayCommand(CanExecute = nameof(CanCheckWebAppUpdate))]
    private void CheckWebAppUpdate() => _checkWebAppUpdate?.Invoke();

    [RelayCommand(CanExecute = nameof(CanReloadWebApp))]
    private void ReloadWebApp() => _reloadWebApp?.Invoke();

    /// <summary>
    /// The running build, shown in Settings. Without this there is no way to confirm which APK is
    /// actually on a phone — an install that silently kept the previous build looks identical to one
    /// that worked, and that ambiguity has already cost a debugging round.
    ///
    /// <para>Read from the ENTRY assembly, not this one: the version that matters is the platform
    /// head's, because that is what carries the Android versionName. This library's own version
    /// never changes and would always have reported 1.0.0.</para>
    /// </summary>
    public string AppVersion
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(MobileShellViewModel).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            return informational is { Length: > 0 }
                ? informational.Split('+')[0]
                : assembly.GetName().Version?.ToString() ?? Strings.Get("Unknown");
        }
    }

    /// <summary>Name for the drawer's account row; falls back to the device when unknown.</summary>
    public string AccountName => string.IsNullOrWhiteSpace(UserName) ? "Lumi" : UserName;

    /// <summary>Single letter for the account avatar disc.</summary>
    public string UserInitial => AccountName is { Length: > 0 } name
        ? name[..1].ToUpperInvariant()
        : "L";

    /// <summary>
    /// The empty-state line. Time-adaptive like Claude's, and personal when the desktop has told us
    /// who the user is — a greeting that knows the hour and your name reads as alive, and it is the
    /// only text on an otherwise blank screen so it has to earn its place.
    /// </summary>
    public string Greeting
    {
        get
        {
            if (HasDesktopNewChat && !string.IsNullOrWhiteSpace(_desktopGreeting))
                return _desktopGreeting;
            var part = DateTime.Now.Hour switch
            {
                < 5 => Strings.Get("GreetingLateNight"),
                < 12 => Strings.Get("GreetingMorning"),
                < 18 => Strings.Get("GreetingAfternoon"),
                _ => Strings.Get("GreetingEvening")
            };

            return string.IsNullOrWhiteSpace(UserName)
                ? Strings.Get("GreetingWithoutName", part)
                : Strings.Get("GreetingWithName", part, UserName);
        }
    }

    /// <summary>The greeting split around the user's name, so the welcome can accent it as the desktop does.</summary>
    public WelcomeGreeting GreetingParts => WelcomeGreeting.Split(Greeting, UserName);

    /// <summary>
    /// Chat identity above the labelled project or assistant settings affordance.
    /// </summary>
    public string HeaderTitle => Chat.HasChat && !string.IsNullOrWhiteSpace(Chat.Title)
        ? Chat.Title
        : "Lumi";

    /// <summary>The details popup is titled by the chat it configures; a blank chat is a new chat.</summary>
    public string ChatDetailsTitle => Chat.HasChat && !string.IsNullOrWhiteSpace(Chat.Title)
        ? Chat.Title
        : Strings.Get("NewChat");

    // ── The open chat's own actions ─────────────────────────────────────────────────────────
    //
    // The chat's details sheet also pins, renames and deletes it, as the desktop's header and
    // sidebar menu do, so a chat read full-screen never has to be found in the list first.

    public bool CanManageOpenChat => IsPaired && Chat.HasChat && Chat.ChatId != Guid.Empty;

    /// <summary>Pinning is offered when the list knows whether the open chat is pinned.</summary>
    public bool CanPinOpenChat => CanManageOpenChat && ChatList.FindLoadedChat(Chat.ChatId) is not null;

    public string OpenChatPinLabel =>
        CanManageOpenChat && ChatList.FindLoadedChat(Chat.ChatId) is { IsPinned: true }
            ? Strings.Get("UnpinChat")
            : Strings.Get("PinChat");

    /// <summary>The open chat as its list row when shown, else as its loaded summary.</summary>
    private ChatListItemViewModel? OpenChatRow()
    {
        if (!CanManageOpenChat)
            return null;
        if (ChatList.FindChat(Chat.ChatId) is { } row)
            return row;
        return ChatList.FindLoadedChat(Chat.ChatId) is { } loaded
            ? new ChatListItemViewModel(loaded)
            : new ChatListItemViewModel(new RemoteChat { Id = Chat.ChatId, Title = Chat.Title });
    }

    [RelayCommand]
    private void RenameOpenChat()
    {
        if (OpenChatRow() is not { } chat)
            return;
        Chat.IsRunSettingsSheetOpen = false;
        OpenChatActions(chat);
        RenameActionChat();
    }

    [RelayCommand]
    private void DeleteOpenChat()
    {
        if (OpenChatRow() is not { } chat)
            return;
        Chat.IsRunSettingsSheetOpen = false;
        OpenChatActions(chat);
        DeleteActionChat();
    }

    [RelayCommand]
    private async Task PinOpenChatAsync()
    {
        if (!CanManageOpenChat || ChatList.FindLoadedChat(Chat.ChatId) is not { } chat)
            return;
        Chat.IsRunSettingsSheetOpen = false;
        var chatId = chat.Id;
        var pinned = !chat.IsPinned;
        var result = await SendCommandAsync(new RemoteCommand(RemoteProtocol.Actions.PinChat)
            .With("chatId", chatId.ToString()).With("pinned", pinned.ToString()));
        if (result.Ok)
            ChatList.SetPinned(chatId, pinned);
        else if (Chat.ChatId == chatId)
            Chat.ErrorText = result.Error ?? Strings.Get("ActionIncomplete");
        OnPropertyChanged(nameof(OpenChatPinLabel));
    }

    partial void OnUserNameChanged(string value)
    {
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(UserInitial));
        OnPropertyChanged(nameof(Greeting));
        OnPropertyChanged(nameof(GreetingParts));
    }

    /// <summary>
    /// Search takes over the whole screen. An inline filter field is a desktop habit: on a phone the
    /// keyboard already eats half the display, so anything left around it is wasted.
    /// </summary>
    [RelayCommand]
    private void OpenSearch()
    {
        var previous = CaptureNavigation();
        SearchChatList.ProjectFilterId = ChatList.ProjectFilterId;
        SearchChatList.SearchText = "";
        SearchChatList.Apply(ChatList.SnapshotLoadedGroups());
        Page = MobilePage.Search;
        IsDrawerOpen = false;
        RememberNavigation(previous);
    }

    /// <summary>True only when the phone can actually reach a ready Lumi: link up AND host ready.</summary>
    public bool IsLive => IsConnected && IsHostReady;

    /// <summary>
    /// True when the phone stopped retrying because Lumi stayed unreachable. A network change, the
    /// app reopening or Retry resumes connecting.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionBannerText))]
    [NotifyPropertyChangedFor(nameof(ConnectionStateLabel))]
    [NotifyPropertyChangedFor(nameof(ShowRetryConnectionAction))]
    private bool _isConnectionOffline;

    public bool ShowRetryConnectionAction => IsConnectionOffline && !IsGatewaySignInRequired;

    /// <summary>What the link has cost since launch, shown in Settings.</summary>
    [ObservableProperty] private string _connectionDiagnostics = "";

    [RelayCommand]
    private Task RetryConnectionAsync()
    {
        _connection.RetryNow();
        IsConnectionOffline = false;
        return Client.IsEventStreamActive ? Task.CompletedTask : StartAsync();
    }

    public string ConnectionStateLabel => Strings.Get(IsGatewaySignInRequired ? "SignInRequired"
        : IsConnectionOffline && !IsConnected ? "Offline"
        : !IsConnected ? "Reconnecting"
        : IsHostReady ? "Connected" : "GettingReady");

    /// <summary>
    /// A paired phone must never fail silently. The chat remains readable while reconnecting, but a
    /// compact banner makes it clear why sending or fresh data is unavailable.
    /// </summary>
    public bool ShowConnectionBanner => IsPaired && !IsLive;

    public string ConnectionBannerText
    {
        get
        {
            if (IsGatewaySignInRequired)
                return ConnectionMessage ?? Strings.Get("MicrosoftSignInAttention");

            if (!IsConnected)
            {
                var host = string.IsNullOrWhiteSpace(HostName) ? Strings.Get("YourPc") : HostName;
                if (IsConnectionOffline)
                {
                    return _connection.IsNetworkAvailable
                        ? Strings.Get("CantReachHost", host)
                        : Strings.Get("PhoneOffline", host);
                }
                return string.IsNullOrWhiteSpace(ConnectionMessage)
                    ? Strings.Get("ReconnectingToHost", host)
                    : Strings.Get("ReconnectingToHostReason", host, ConnectionMessage);
            }

            return string.IsNullOrWhiteSpace(ConnectionMessage)
                ? Strings.Get("LumiGettingReady")
                : ConnectionMessage!;
        }
    }

    // ── Navigation ────────────────────────────────────────────────────────────────────────────
    //
    // The desktop's places, adapted to the window. With room for one pane the chat list is home:
    // a chat, or another place, opens over it and Back or a swipe from anywhere returns to it. With
    // room for two, the open place's list sits beside its content as the desktop sidebar does. The
    // chat surface is always mounted so coming back to it never rebuilds a transcript.

    public bool IsChatPage => Page == MobilePage.Chat;

    public bool IsLibraryPage => Page == MobilePage.Library;

    public bool IsSettingsPage => Page == MobilePage.Settings;

    public bool IsSearchPage => Page == MobilePage.Search;

    /// <summary>True when a page is stacked over the conversation.</summary>
    public bool HasPageOverlay => Page != MobilePage.Chat;

    /// <summary>The place the user is in, derived from the page and the Library's scope.</summary>
    public MobilePlace Place => Page switch
    {
        MobilePage.Library => Library.Scope switch
        {
            LibraryScope.Jobs => MobilePlace.Jobs,
            LibraryScope.Memories => MobilePlace.Settings,
            _ => MobilePlace.Customize
        },
        MobilePage.Settings => MobilePlace.Settings,
        _ => MobilePlace.Chats
    };

    public bool IsChatsPlace => Place == MobilePlace.Chats;

    public bool IsJobsPlace => Place == MobilePlace.Jobs;

    public bool IsCustomizePlace => Place == MobilePlace.Customize;

    public bool IsSettingsPlace => Place == MobilePlace.Settings;

    /// <summary>
    /// A place's own list sits beside its content. Not across a physical hinge, where the chat list
    /// keeps the leading pane and each place uses the other pane as one.
    /// </summary>
    public bool IsTwoPane => Layout.CanShowTwoPanes && !HasHingeGap;

    public bool IsSinglePane => !IsTwoPane;

    /// <summary>The window also has room for a supporting panel beside the chat.</summary>
    public bool IsThreePane => Layout.CanShowThreePanes;

    /// <summary>Width of a place's list column when it sits beside its content.</summary>
    public double ListPaneWidth => Layout.ListPaneWidth;

    /// <summary>
    /// At two-pane widths the chat list docks beside the chat instead of sliding over it. It is
    /// still collapsible: the list uses a meaningful share of the conversation width, so its
    /// visibility is controlled by the same button/gesture state and remembered between launches.
    /// </summary>
    public bool IsDrawerDocked => CanDockDrawer && !IsSidebarCollapsed;

    /// <summary>
    /// Wide enough to dock, whether or not the user has collapsed it. Only the chat list docks: the
    /// other places lay out their own list beside their content. Across a physical hinge the list
    /// keeps the leading pane for every place, since nothing can span the gap.
    /// </summary>
    public bool CanDockDrawer => Layout.CanShowTwoPanes && (Place == MobilePlace.Chats || HasHingeGap);

    public bool IsPhoneLayout => Layout.WidthClass == WidthSizeClass.Compact;

    /// <summary>The shared drawer drives the same gesture and button state in either layout.</summary>
    public bool IsNavigationOpen
    {
        get => CanDockDrawer ? !IsSidebarCollapsed : IsDrawerOpen;
        set
        {
            if (CanDockDrawer)
                IsSidebarCollapsed = !value;
            else
                IsDrawerOpen = value;
        }
    }

    public bool IsDrawerOverlay => !CanDockDrawer && IsDrawerOpen;

    /// <summary>Native input waits for moving pane geometry instead of painting at stale platform bounds.</summary>
    public bool IsNavigationCoveringContent => IsDrawerPresented && (!CanDockDrawer || IsDrawerMoving);

    /// <summary>
    /// Drawer gestures are suspended while another modal surface owns the pointer. This prevents a
    /// horizontal drift inside a model list or action sheet from stacking the navigation drawer over
    /// the active modal. A side panel holds the width the list would take, so it suspends them too.
    /// </summary>
    public bool CanDragDrawer =>
        !IsChatActionsOpen &&
        !IsProjectPickerOpen &&
        !IsDisconnectConfirmationOpen &&
        (Page != MobilePage.Chat || (!Chat.HasOpenSheet && !Chat.HasSidePanelOpen)) &&
        (Page != MobilePage.Library || !Library.HasOpenSurface);

    /// <summary>Scrim is shown for the sliding drawer only; a docked drawer never dims the chat.</summary>
    public bool ShowScrim => IsDrawerOverlay;

    /// <summary>Docked or slid open — either way the drawer's contents are on screen.</summary>
    public bool IsDrawerVisible => IsNavigationOpen;

    /// <summary>
    /// Phones use the full screen. Larger layouts keep a readable pane width or honour the hinge.
    /// </summary>
    public double DrawerWidth => CanDockDrawer
        ? Layout.ListPaneWidth
        : IsPhoneLayout ? Layout.Width + SafeArea.Left + SafeArea.Right : Math.Min(360, Layout.Width);

    /// <summary>Final reserved width. The view follows drawer progress between these endpoints.</summary>
    public double NavigationLeadingWidth => HasHingeGap
        ? Layout.HingePosition
        : IsDrawerDocked ? DrawerWidth : 0;

    /// <summary>The shared expander lives beside the chat title in both phone and tablet layouts.</summary>
    public bool ShowMenuButton => IsPaired;

    /// <summary>One pane: the button returns to the list. Two panes: it shows or hides the list.</summary>
    public bool ShowBackToChats => IsPaired && !CanDockDrawer;

    public string NavigationToggleLabel => Strings.Get(!CanDockDrawer ? "BackToChats"
        : IsNavigationOpen ? "HideChats" : "ShowChats");

    /// <summary>Physical hinge gap kept empty between a docked drawer and the conversation.</summary>
    public double HingeGapWidth => Layout.HingeSize;

    public bool HasHingeGap => Layout.HingeSize > 0;

    public double UsableContentHeight => Layout.HorizontalHingePosition > 0
        ? Layout.HorizontalHingePosition
        : Layout.Height;

    [RelayCommand]
    private void ToggleDrawer() => IsNavigationOpen = !IsNavigationOpen;

    [RelayCommand]
    private void CloseDrawer() => IsDrawerOpen = false;

    /// <summary>
    /// Shows the chat list: docked beside the chat on a wide window, as the whole screen otherwise.
    /// This is where the app opens and where Back returns when there is no previous page.
    /// </summary>
    public void ShowHome()
    {
        _pageHistory.Clear();
        Page = MobilePage.Chat;
        if (!CanDockDrawer && IsPaired)
            IsDrawerOpen = true;
        OnPropertyChanged(nameof(CanGoBack));
    }

    private bool _isOpeningAtLaunch;

    /// <summary>
    /// Opens on the chat list at launch or right after pairing. The startup snapshot brings the
    /// chats, so the list is not fetched separately ahead of the connection being ready.
    /// </summary>
    public void ShowLaunchHome()
    {
        _isOpeningAtLaunch = true;
        try
        {
            ShowHome();
            // A docked list is in view without the drawer opening; the snapshot brings its chats.
            FollowChatListVisibility();
        }
        finally
        {
            _isOpeningAtLaunch = false;
        }
    }

    [RelayCommand]
    private void SelectPlace(string? place)
    {
        if (Enum.TryParse<MobilePlace>(place, ignoreCase: true, out var parsed))
            SelectPlace(parsed);
    }

    public void SelectPlace(MobilePlace place)
    {
        switch (place)
        {
            case MobilePlace.Chats:
                ShowHome();
                break;
            case MobilePlace.Jobs:
                ShowLibraryScope(LibraryScope.Jobs);
                break;
            case MobilePlace.Customize:
                ShowLibraryScope(LibraryScope.Customize);
                break;
            case MobilePlace.Settings:
                var previous = CaptureNavigation();
                SettingsSection = SettingsSection.None;
                Page = MobilePage.Settings;
                IsDrawerOpen = false;
                RememberNavigation(previous);
                break;
        }
    }

    /// <summary>
    /// Opens the Library page for a place. With unsaved edits the page stays on them and asks first;
    /// the scope follows only when the user discards.
    /// </summary>
    private void ShowLibraryScope(LibraryScope scope)
    {
        var previous = CaptureNavigation();
        Library.RequestScope(scope);
        Page = MobilePage.Library;
        IsDrawerOpen = false;
        NotifyPlaceChanged();
        RememberNavigation(previous);
    }

    /// <summary>Memories is a page of Settings, kept with the Library editor that already serves it.</summary>
    [RelayCommand]
    private void ShowMemories() => ShowLibraryScope(LibraryScope.Memories);

    /// <summary>The Settings page in view. A phone starts on the list; a wide window shows a page beside it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    private SettingsSection _settingsSection;

    [RelayCommand]
    private void OpenSettingsSection(string? section)
    {
        if (Enum.TryParse<SettingsSection>(section, ignoreCase: true, out var parsed))
            SettingsSection = parsed;
    }

    /// <summary>The page a wide Settings shows beside its list: the chosen one, or Appearance.</summary>
    public SettingsSection VisibleSettingsSection => SettingsSection == SettingsSection.None && IsTwoPane
        ? SettingsSection.Appearance
        : SettingsSection;

    public bool ShowSettingsDetail => VisibleSettingsSection != SettingsSection.None;

    public bool IsAppearanceSectionVisible => VisibleSettingsSection == SettingsSection.Appearance;

    public bool IsConnectionSectionVisible => VisibleSettingsSection == SettingsSection.Connection;

    public bool IsAboutSectionVisible => VisibleSettingsSection == SettingsSection.About;

    public string SettingsDetailTitle => VisibleSettingsSection switch
    {
        SettingsSection.Appearance => Strings.Get("Appearance"),
        SettingsSection.Connection => Strings.Get("Connection"),
        SettingsSection.About => Strings.Get("About"),
        _ => Strings.Get("Settings")
    };

    partial void OnSettingsSectionChanged(SettingsSection value) => NotifySettingsSectionChanged();

    private void NotifySettingsSectionChanged()
    {
        OnPropertyChanged(nameof(VisibleSettingsSection));
        OnPropertyChanged(nameof(ShowSettingsDetail));
        OnPropertyChanged(nameof(IsAppearanceSectionVisible));
        OnPropertyChanged(nameof(IsConnectionSectionVisible));
        OnPropertyChanged(nameof(IsAboutSectionVisible));
        OnPropertyChanged(nameof(SettingsDetailTitle));
    }

    // ── The supporting panel ──────────────────────────────────────────────────────────────────
    //
    // With two panes the plan, sources and activity open beside the chat. Below three panes the
    // chat list steps aside while one is open, as the desktop's Workspace takes the sidebar's room,
    // and returns when it closes.

    public const double SidePanelWidth = 400;

    /// <summary>The chat keeps at least this much beside a side panel; narrower windows use sheets.</summary>
    private const double MinimumChatBesidePanel = 480;

    /// <summary>The plan, sources and activity can open beside the chat rather than over it.</summary>
    public bool CanShowSidePanels =>
        IsTwoPane && Layout.Width >= SidePanelWidth + 12 + MinimumChatBesidePanel;

    private bool _sidebarYieldedToPanel;
    private bool _isYieldingSidebar;

    /// <summary>The chat makes room on its trailing edge for an open side panel.</summary>
    public Thickness ChatSidePanelInset => Chat.HasSidePanelOpen
        ? new Thickness(0, 0, SidePanelWidth + 12, 0)
        : default;

    /// <summary>Where a side panel floats: clear of the status bar, the cutout and the gesture area.</summary>
    public Thickness SidePanelSheetMargin =>
        new(0, SafeArea.Top + 8, SafeArea.Right + 8, SafeArea.Bottom + 8);

    private void OnSidePanelChanged()
    {
        ReconcileSidebarWithSidePanel();
        OnPropertyChanged(nameof(ChatSidePanelInset));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanDragDrawer));
    }

    /// <summary>
    /// Below three panes the chat list steps aside while a side panel is open; it returns when the
    /// panel closes or the window grows room for all three.
    /// </summary>
    private void ReconcileSidebarWithSidePanel()
    {
        if (Chat.HasSidePanelOpen && !IsThreePane && IsDrawerDocked)
            YieldSidebar(collapsed: true);
        else if (_sidebarYieldedToPanel && (!Chat.HasSidePanelOpen || IsThreePane))
            YieldSidebar(collapsed: false);
    }

    private void YieldSidebar(bool collapsed)
    {
        _isYieldingSidebar = true;
        try
        {
            IsSidebarCollapsed = collapsed;
        }
        finally
        {
            _isYieldingSidebar = false;
        }

        _sidebarYieldedToPanel = collapsed;
    }

    private void CloseSidePanels()
    {
        Chat.IsPlanOpen = false;
        Chat.IsSourcesSheetOpen = false;
        Chat.IsActivitySheetOpen = false;
    }

    /// <summary>Raises everything that follows from the place and the window together.</summary>
    private void NotifyPlaceChanged()
    {
        OnPropertyChanged(nameof(Place));
        OnPropertyChanged(nameof(IsChatsPlace));
        OnPropertyChanged(nameof(IsJobsPlace));
        OnPropertyChanged(nameof(IsCustomizePlace));
        OnPropertyChanged(nameof(IsSettingsPlace));
        NotifyNavigationLayoutChanged();
    }

    private void NotifyNavigationLayoutChanged()
    {
        // Open state first: a drawer that closes while still docked and only then turns modal snaps
        // to its new layout, instead of sliding over the place that replaced it.
        OnPropertyChanged(nameof(IsNavigationOpen));
        OnPropertyChanged(nameof(CanDockDrawer));
        OnPropertyChanged(nameof(NavigationToggleLabel));
        OnPropertyChanged(nameof(IsDrawerDocked));
        OnPropertyChanged(nameof(IsNavigationCoveringContent));
        OnPropertyChanged(nameof(CanDragDrawer));
        OnPropertyChanged(nameof(IsDrawerOverlay));
        OnPropertyChanged(nameof(IsDrawerVisible));
        OnPropertyChanged(nameof(ShowScrim));
        OnPropertyChanged(nameof(ShowMenuButton));
        OnPropertyChanged(nameof(ShowBackToChats));
        OnPropertyChanged(nameof(ShowHeaderNewChat));
        OnPropertyChanged(nameof(ShowListNewChat));
        OnPropertyChanged(nameof(DrawerWidth));
        OnPropertyChanged(nameof(NavigationLeadingWidth));
        OnPropertyChanged(nameof(ProjectPickerSheetMargin));
        OnPropertyChanged(nameof(CanGoBack));
    }

    private bool _isChatListShown;

    /// <summary>
    /// The PC tells the phone about its chats only while the list is on screen, so the list reloads when it
    /// comes back into view (the drawer opening, the sidebar showing, a place or a new layout bringing it
    /// back), and not on every change while it stays in view. Callers run it after their subscription
    /// update, so the list is read once the PC already sends what changes after it.
    /// </summary>
    private void FollowChatListVisibility()
    {
        var shown = IsDrawerVisible;
        if (shown == _isChatListShown)
            return;

        _isChatListShown = shown;
        if (shown && IsPaired && !_isOpeningAtLaunch)
            _ = ChatList.RefreshFromServerAsync();
    }

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        if (!_isYieldingSidebar)
        {
            _settings.IsSidebarCollapsed = value;
            _store.Save(_settings);

            // Asking for the list back below three panes trades the supporting panel for it.
            _sidebarYieldedToPanel = false;
            if (!value && Chat.HasSidePanelOpen && !IsThreePane)
                CloseSidePanels();
        }

        IsDrawerOpen = false;

        NotifyNavigationLayoutChanged();
        OnPropertyChanged(nameof(HingeGapWidth));
        OnPropertyChanged(nameof(HasHingeGap));
        OnPropertyChanged(nameof(UsableContentHeight));
        QueueSubscriptionUpdate();
        FollowChatListVisibility();
    }

    [RelayCommand]
    private void ShowPage(string? page)
    {
        var previous = CaptureNavigation();
        if (Enum.TryParse<MobilePage>(page, ignoreCase: true, out var parsed))
            Page = parsed;

        // Opening a page from the drawer must dismiss it, or the page arrives behind the drawer.
        IsDrawerOpen = false;
        RememberNavigation(previous);
    }

    private readonly record struct PageNavigation(
        MobilePage Page, LibraryScope Scope, SettingsSection SettingsSection, bool IsDrawerOpen,
        Guid ChatId, string ChatTitle, string? Model);

    private PageNavigation CaptureNavigation() =>
        new(Page, Library.Scope, SettingsSection, IsDrawerOpen, Chat.ChatId, Chat.Title, Chat.Model);

    private void RememberNavigation(PageNavigation previous)
    {
        if (previous.Page == Page && (Page != MobilePage.Library || previous.Scope == Library.Scope))
            return;
        _pageHistory.Push(previous);
        OnPropertyChanged(nameof(CanGoBack));
    }

    private bool ReturnToPreviousPage()
    {
        if (!_pageHistory.TryPeek(out var previous))
            return false;
        if (previous.Page == MobilePage.Library && !Library.RequestScope(previous.Scope))
            return true;

        _pageHistory.Pop();
        if (previous.Page == MobilePage.Chat && previous.ChatId != Chat.ChatId)
        {
            ChatList.SelectedChatId = previous.ChatId;
            SearchChatList.SelectedChatId = previous.ChatId;
            _ = ActivateChatAsync(previous.ChatId, previous.ChatTitle, previous.Model, rememberPage: false);
        }
        else
            Page = previous.Page;
        if (previous.Page == MobilePage.Settings)
            SettingsSection = previous.SettingsSection;
        IsDrawerOpen = previous.IsDrawerOpen && !CanDockDrawer;
        OnPropertyChanged(nameof(CanGoBack));
        return true;
    }

    /// <summary>Dismisses whatever is stacked over the conversation.</summary>
    [RelayCommand]
    private void GoBack()
    {
        if (IsWebAppUpdateConfirmationOpen)
        {
            CancelWebAppUpdate();
            return;
        }

        if (!IsPaired && Connect.IsCodeStep)
        {
            Connect.BackCommand.Execute(null);
            return;
        }

        if (IsDisconnectConfirmationOpen)
        {
            IsDisconnectConfirmationOpen = false;
            return;
        }

        if (IsChatActionsOpen)
        {
            if (IsRenamingChat || IsConfirmingChatDelete)
                CancelChatAction();
            else
                IsChatActionsOpen = false;
            return;
        }

        if (IsProjectPickerOpen)
        {
            IsProjectPickerOpen = false;
            return;
        }

        if (Page == MobilePage.Library && Library.DismissTopmostSurface())
            return;

        if (Page == MobilePage.Chat && Chat.DismissTopmostSheet())
            return;

        if (Page == MobilePage.Settings && SettingsSection != SettingsSection.None && IsSinglePane)
        {
            SettingsSection = SettingsSection.None;
            return;
        }

        if (ReturnToPreviousPage())
            return;

        if (Page == MobilePage.Library && Library.Scope == LibraryScope.Memories)
        {
            Page = MobilePage.Settings;
            return;
        }

        if (HasPageOverlay)
        {
            ShowHome();
            return;
        }

        if (IsReadingChatOverHome)
            IsDrawerOpen = true;
    }

    /// <summary>
    /// One pane, a chat (or a new one) in view: Back returns to the chat list. However wide the
    /// pane, the list is home, so Back from it leaves the app rather than returning to the chat.
    /// </summary>
    private bool IsReadingChatOverHome =>
        IsPaired && Page == MobilePage.Chat && !CanDockDrawer && !IsDrawerOpen;

    /// <summary>True when the system back gesture has something of ours to dismiss.</summary>
    public bool CanGoBack =>
        (!IsPaired && Connect.IsCodeStep) ||
        IsWebAppUpdateConfirmationOpen ||
        IsDisconnectConfirmationOpen ||
        IsChatActionsOpen ||
        IsProjectPickerOpen ||
        _pageHistory.Count > 0 ||
        (Page == MobilePage.Library && Library.HasOpenSurface) ||
        (Page == MobilePage.Chat && (Chat.HasOpenSheet || Chat.HasSidePanelOpen)) ||
        HasPageOverlay ||
        IsReadingChatOverHome;

    partial void OnPageChanged(MobilePage value)
    {
        IsWebAppUpdateConfirmationOpen = false;
        IsDisconnectConfirmationOpen = false;
        IsProjectPickerOpen = false;
        var previousPage = _lastObservedPage;
        _lastObservedPage = value;
        OnPropertyChanged(nameof(IsChatPage));
        OnPropertyChanged(nameof(IsLibraryPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(IsSearchPage));
        OnPropertyChanged(nameof(HasPageOverlay));
        NotifyPlaceChanged();
        if (_chatActivationOwnsSubscription)
        {
            // ActivateChatAsync installs the exact chat scope before its authoritative fetch.
        }
        else if (value == MobilePage.Chat
                 && previousPage != MobilePage.Chat
                 && Chat.ChatId != Guid.Empty)
        {
            _ = ReacquireVisibleChatAsync(Chat.ChatId);
        }
        else
        {
            QueueSubscriptionUpdate();
        }
        FollowChatListVisibility();
        if (value == MobilePage.Library)
            _ = RefreshSnapshotAsync();
        if (value == MobilePage.Search)
            _ = SearchChatList.RefreshFromServerAsync();
        if (value == MobilePage.Settings)
            ConnectionDiagnostics = Client.Stats.Snapshot().Summary;
    }

    partial void OnIsDrawerOpenChanged(bool value)
    {
        if (value && Page == MobilePage.Chat)
            _pageHistory.Clear();
        OnPropertyChanged(nameof(IsNavigationOpen));
        OnPropertyChanged(nameof(NavigationToggleLabel));
        OnPropertyChanged(nameof(IsDrawerOverlay));
        OnPropertyChanged(nameof(IsDrawerVisible));
        OnPropertyChanged(nameof(ShowScrim));
        OnPropertyChanged(nameof(CanGoBack));
        QueueSubscriptionUpdate();
        FollowChatListVisibility();
        if (!value && CanReadChatTranscript)
            _ = RefreshTranscriptAsync();
    }

    partial void OnLayoutChanged(MobileLayoutState oldValue, MobileLayoutState newValue)
    {
        // Folding, unfolding or splitting the screen changes how many panes fit. Whatever was being
        // read stays in view; with nothing open, a single pane lands on the chat list.
        if (oldValue.CanShowTwoPanes != newValue.CanShowTwoPanes)
            IsDrawerOpen = !newValue.CanShowTwoPanes && IsPaired && Page == MobilePage.Chat && !Chat.HasChat;
        else if (oldValue.WidthClass != newValue.WidthClass && !IsPhoneLayout && Chat.HasChat)
            IsDrawerOpen = false;

        OnPropertyChanged(nameof(IsPhoneLayout));
        OnPropertyChanged(nameof(IsTwoPane));
        OnPropertyChanged(nameof(IsSinglePane));
        OnPropertyChanged(nameof(IsThreePane));
        OnPropertyChanged(nameof(ListPaneWidth));
        Chat.UseSidePanels = CanShowSidePanels;
        ReconcileSidebarWithSidePanel();
        OnPropertyChanged(nameof(SafeAreaSheetMargin));
        NotifyNavigationLayoutChanged();
        NotifySettingsSectionChanged();
        OnPropertyChanged(nameof(HingeGapWidth));
        OnPropertyChanged(nameof(HasHingeGap));
        OnPropertyChanged(nameof(UsableContentHeight));
        OnPropertyChanged(nameof(HasWelcomeSpace));
        OnPropertyChanged(nameof(IsWelcomeVisible));
        QueueSubscriptionUpdate();
        FollowChatListVisibility();
    }

    /// <summary>
    /// How the app picks its theme. Three-way rather than a dark/light switch because "match my
    /// phone" is what most people actually want, and on Android the OS flips it on a schedule the
    /// app has no other way to follow.
    /// </summary>
    public enum ThemePreference
    {
        System,
        Light,
        Dark
    }

    partial void OnThemeChanged(ThemePreference value)
    {
        _settings.Theme = value.ToString();
        _store.Save(_settings);

        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(ThemeLabel));
    }

    /// <summary>The theme in words, beside Appearance in the Settings list.</summary>
    public string ThemeLabel => Strings.Get(Theme.ToString());

    public bool IsEnglishLanguage => Language == "en";

    public bool IsHebrewLanguage => Language == "he";

    public bool LanguageRestartRequired => Language != Strings.Language;

    partial void OnLanguageChanged(string value)
    {
        _settings.Language = value;
        _store.Save(_settings);
    }

    [RelayCommand]
    private void SelectLanguage(string? language)
    {
        if (language is "en" or "he")
            Language = language;
    }

    /// <summary>Selected-state helpers for the settings segmented control.</summary>
    public bool IsSystemTheme => Theme == ThemePreference.System;

    public bool IsLightTheme => Theme == ThemePreference.Light;

    public bool IsDarkTheme => Theme == ThemePreference.Dark;

    [RelayCommand]
    private void SelectTheme(string? preference)
    {
        if (Enum.TryParse<ThemePreference>(preference, ignoreCase: true, out var parsed))
            Theme = parsed;
    }

    /// <summary>Called by the view whenever the visible surface changes size or posture.</summary>
    public void UpdateLayout(double width, double height, FoldPosture posture = FoldPosture.Flat,
        double hingeSize = 0, double hingePosition = 0)
    {
        var next = MobileLayoutState.From(width, height, posture, hingeSize, hingePosition).Following(Layout);
        if (next != Layout)
            Layout = next;
    }

    /// <summary>
    /// The OS safe area (status bar, display cutout, gesture bar) plus any keyboard intrusion.
    ///
    /// <para>Held here rather than applied as padding on the shell so that <b>surfaces bleed to the
    /// edges while only content is inset</b> — which is what makes an app look like it owns the
    /// screen instead of sitting in a letterbox. Padding the shell pushed the drawer, the top bar
    /// and the conversation background all inside the safe area, leaving a black band under the
    /// status bar; every native app paints its chrome up into it and only keeps text and controls
    /// clear.</para>
    /// </summary>
    [ObservableProperty] private Thickness _safeArea;

    /// <summary>Top inset only — for the surfaces that must clear the status bar / cutout.</summary>
    public Thickness SafeAreaTop => new(0, SafeArea.Top, 0, 0);

    /// <summary>Bottom inset only — the gesture bar, or the keyboard when it is open.</summary>
    public Thickness SafeAreaBottom => new(0, 0, 0, SafeArea.Bottom);

    /// <summary>Left/right insets, which a folded or rotated device can have.</summary>
    public Thickness SafeAreaSides => new(SafeArea.Left, 0, SafeArea.Right, 0);

    /// <summary>Keep the whole floating surface clear of cutouts, system bars and the keyboard.</summary>
    public Thickness SafeAreaSheetMargin
    {
        get
        {
            var gap = IsPhoneLayout ? 12 : 20;
            return new Thickness(SafeArea.Left + gap, SafeArea.Top + gap,
                SafeArea.Right + gap, SafeArea.Bottom + gap);
        }
    }

    /// <summary>Keep the picker over its navigation pane, including on a physical book hinge.</summary>
    public Thickness ProjectPickerSheetMargin
    {
        get
        {
            var margin = SafeAreaSheetMargin;
            return !CanDockDrawer ? margin : new Thickness(
                margin.Left, margin.Top,
                Math.Max(margin.Right, Layout.Width + SafeArea.Left - DrawerWidth + margin.Right),
                margin.Bottom);
        }
    }

    /// <summary>
    /// True while the software keyboard is up.
    ///
    /// <para>The welcome panel is centred in the remaining space, so when the IME lifts the composer
    /// the panel ends up sitting directly on top of it. Typing is also the moment the user has
    /// stopped needing a greeting or a suggestion — they are already writing — so the whole panel
    /// goes away rather than just its lower half.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcomeVisible))]
    [NotifyPropertyChangedFor(nameof(CanApplyWebAppUpdate))]
    [NotifyCanExecuteChangedFor(nameof(ApplyWebAppUpdateCommand))]
    private bool _isKeyboardOpen;

    /// <summary>
    /// Whether the welcome panel (orb, greeting, starters) should be on screen.
    ///
    /// <para>Hiding the parts individually left the orb and greeting behind, which is what the
    /// composer then collided with. This is one decision for the whole panel.</para>
    /// </summary>
    public bool HasWelcomeSpace => Layout.Height >= 500;

    public bool IsWelcomeVisible => Chat.IsEmpty && !IsKeyboardOpen && HasWelcomeSpace;

    partial void OnSafeAreaChanged(Thickness value)
    {
        OnPropertyChanged(nameof(DrawerWidth));
        OnPropertyChanged(nameof(SidePanelSheetMargin));
        OnPropertyChanged(nameof(SafeAreaTop));
        OnPropertyChanged(nameof(SafeAreaBottom));
        OnPropertyChanged(nameof(SafeAreaSides));
        OnPropertyChanged(nameof(SafeAreaSheetMargin));
        OnPropertyChanged(nameof(ProjectPickerSheetMargin));
    }

    /// <summary>
    /// Reconnects using stored credentials and starts the capability-scoped event stream.
    /// </summary>
    public async Task StartAsync()
    {
        var restartOnboarding = false;
        await _startGate.WaitAsync();
        try
        {
            if (!IsPaired)
            {
                await Connect.InitializeAsync();
                return;
            }

            if (!_isApplicationActive
                || Client.BaseUrl is not { Length: > 0 } baseUrl)
            {
                return;
            }

            var generation = Volatile.Read(ref _connectionGeneration);
            using var request = CreateConnectionRequest();

            // A host this phone already bootstrapped from proves its protocol again with the
            // stream's own snapshot, and a revoked token answers that request with 401. Skipping the
            // handshake saves a round trip on every resume.
            var startStream = true;
            if (!Client.HasCompatibleBootstrap)
            {
                RemoteHello? hello = null;
                try
                {
                    var failures = 0;
                    while (hello is null)
                    {
                        try
                        {
                            hello = await Client.HelloAsync(baseUrl, request.Token);
                        }
                        catch (OperationCanceledException) when (!request.IsCancellationRequested)
                        {
                            Trace.TraceWarning("[Mobile] The transport canceled the handshake attempt; retrying.");
                        }
                        if (hello is not null)
                            break;

                        if (!_isApplicationActive
                            || generation != Volatile.Read(ref _connectionGeneration)
                            || !IsPaired
                            || Client.State is RemoteLinkState.Unauthorized or RemoteLinkState.GatewaySignInRequired)
                        {
                            return;
                        }

                        // A transient resume/launch handshake failure happens before the SSE retry
                        // loop exists. The supervisor paces it like the stream, and stops trying
                        // while the PC stays unreachable until the network, a resume or Retry.
                        var delay = _connection.NextDelay(++failures);
                        await _connection.WaitAsync(delay, request.Token);
                    }
                }
                catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
                {
                    return;
                }
                if (!_isApplicationActive || generation != Volatile.Read(ref _connectionGeneration))
                    return;
                _connection.OnConnected();
                if (!RemoteProtocol.IsCompatibleVersion(hello.ProtocolVersion)
                    || !RemoteProtocol.HasRequiredCapabilities(hello.Capabilities))
                {
                    return;
                }

                if (!Client.SupportsScopedEvents)
                    return;

                if (!hello.IsPaired)
                {
                    ClearPairedState();
                    restartOnboarding = true;
                    startStream = false;
                }
            }

            if (startStream)
            {
                if (Client.IsEventStreamActive
                    && Client.State is RemoteLinkState.Connecting or RemoteLinkState.Connected)
                {
                    await Client.UpdateEventSubscriptionAsync(BuildEventSubscription(), request.Token);
                }
                else if (_isApplicationActive)
                {
                    await Client.StartEventStreamAsync(BuildEventSubscription());
                }
            }
        }
        finally
        {
            _startGate.Release();
        }

        if (restartOnboarding)
            await Connect.InitializeAsync();
    }

    public Task HandleConnectionLaunchAsync(MobileConnectionLaunchRequest request)
    {
        if (!IsPaired)
            return Connect.ApplyLaunchRequestAsync(request);

        var current = Client.BaseUrl is { Length: > 0 } baseUrl
            ? LumiRemoteClient.NormalizeBaseUrl(baseUrl)
            : "";
        if (string.Equals(current, request.BaseUrl, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        ConnectionMessage =
            Strings.Get("AlreadyPaired", HostName);
        return Task.CompletedTask;
    }

    private async Task OnPairedAsync(string baseUrl, string hostName)
    {
        await Client.StopEventStreamAsync();
        BeginConnectionGeneration();
        BeginHostGeneration();
        Client.Configure(baseUrl, Client.Token);
        ResetHostScopedState();
        _settings.BaseUrl = baseUrl;
        _settings.Token = Client.Token ?? "";
        _settings.HostName = hostName;
        _store.Save(_settings);

        HostName = hostName;
        _canAdoptDesktopActiveChat = true;
        IsPaired = true;

        // A newly paired phone opens on the PC's chats, the app's home.
        ShowLaunchHome();

        await StartAsync();
    }

    public async Task NotifyApplicationDeactivatedAsync()
    {
        var generation = Interlocked.Increment(ref _lifecycleGeneration);
        _isApplicationActive = false;
        NotifyWebAppUpdateSafetyChanged();
        LogTransportSummary();

        // A live stream on a desktop that keeps background subscriptions quiet survives a short app
        // switch: a file picker, the share sheet or a glance elsewhere resumes without reconnecting.
        // A handshake, a retry pause or an older desktop stops at once, as before.
        var keepStream = _connection.BackgroundGrace > TimeSpan.Zero
                         && Client.SupportsResumePing
                         && Client.IsEventStreamActive
                         && Client.State == RemoteLinkState.Connected;
        if (!keepStream)
            BeginConnectionGeneration();
        await _lifecycleGate.WaitAsync();
        try
        {
            if (generation != Volatile.Read(ref _lifecycleGeneration) || _isApplicationActive)
                return;

            if (keepStream)
            {
                _connection.BeginBackgroundGrace(OnBackgroundGraceExpiredAsync);
                await UpdateEventSubscriptionForConnectionAsync(BuildEventSubscription());
                return;
            }

            await Client.StopEventStreamAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task NotifyApplicationActivatedAsync()
    {
        var generation = Interlocked.Increment(ref _lifecycleGeneration);
        _isApplicationActive = true;
        NotifyWebAppUpdateSafetyChanged();
        var hadGrace = _connection.IsInBackgroundGrace;
        var resumedInGrace = _connection.EndBackgroundGrace();
        await _lifecycleGate.WaitAsync();
        try
        {
            if (generation != Volatile.Read(ref _lifecycleGeneration) || !_isApplicationActive)
                return;

            if (resumedInGrace
                && Client.IsEventStreamActive
                && Client.State == RemoteLinkState.Connected)
            {
                await ResumeKeptStreamAsync();
                return;
            }

            if (hadGrace && !resumedInGrace)
            {
                // The process was frozen past the grace window, so its timer never ran. The kept
                // stream may have died meanwhile; start a fresh one.
                BeginConnectionGeneration();
                await Client.StopEventStreamAsync();
            }

            // Reopening is a reason to try again at once, even after giving up.
            _connection.RetryNow();
            await StartAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task OnBackgroundGraceExpiredAsync()
    {
        try
        {
            await _lifecycleGate.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_isApplicationActive)
                return;

            BeginConnectionGeneration();
            await Client.StopEventStreamAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Brings a stream kept through the background grace back to the foreground: the desktop pushes
    /// a ping in response, and a stream that stays silent is replaced.
    /// </summary>
    private async Task ResumeKeptStreamAsync()
    {
        var framesBefore = Client.ReceivedFrameCount;
        var generation = Volatile.Read(ref _connectionGeneration);
        await UpdateEventSubscriptionForConnectionAsync(BuildEventSubscription());
        _ = VerifyResumedStreamAsync(framesBefore, generation);
        CatchUpAfterBackground();
    }

    private async Task VerifyResumedStreamAsync(long framesBefore, long generation)
    {
        try
        {
            await Task.Delay(_connection.ResumeProbeTimeout, _connection.Time, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (generation != Volatile.Read(ref _connectionGeneration) || !_isApplicationActive)
            return;

        if (Client.ReceivedFrameCount == framesBefore)
        {
            Trace.TraceWarning("[Mobile] The kept stream stayed silent after resuming; reconnecting.");
            await RestartEventStreamAsync();
        }
    }

    private async Task RestartEventStreamAsync()
    {
        try
        {
            await _lifecycleGate.WaitAsync(_lifetime.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (!_isApplicationActive)
                return;

            BeginConnectionGeneration();
            await Client.StopEventStreamAsync();
            await StartAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>The desktop kept the stream quiet while the app was away; read what may have moved.</summary>
    private void CatchUpAfterBackground()
    {
        if (Page == MobilePage.Chat && Chat.ChatId != Guid.Empty)
            _ = RefreshTranscriptAsync();
        if (IsDrawerVisible)
            _ = ChatList.RefreshFromServerAsync();
        if (Page == MobilePage.Search)
            _ = SearchChatList.RefreshFromServerAsync();
        if (Page == MobilePage.Library)
            _ = RefreshSnapshotAsync();
    }

    private void OnNetworkRestored() => _post(() =>
    {
        if (!_isApplicationActive || !IsPaired)
            return;

        if (Client.IsEventStreamActive && Client.State == RemoteLinkState.Connected)
        {
            // The tunnel may survive a network move, but a stream silent since then can be stuck
            // until its silence deadline. A fresh stream is cheap with cached sections.
            if (Client.TimeSinceLastFrame is not { } silent
                || silent >= _connection.StaleStreamAfterNetworkChange)
            {
                _ = RestartEventStreamAsync();
            }

            return;
        }

        if (!Client.IsEventStreamActive)
            _ = StartAsync();
    });

    private void LogTransportSummary()
    {
        var stats = Client.Stats.Snapshot();
        _post(() => ConnectionDiagnostics = stats.Summary);
        Trace.TraceInformation("[Mobile] Transport " + stats.Details);
    }

    private RemoteEventSubscription BuildEventSubscription() => new()
    {
        Generation = Interlocked.Increment(ref _subscriptionGeneration),
        ChatId = Page == MobilePage.Chat && Chat.ChatId != Guid.Empty
            ? Chat.ChatId
            : null,
        IncludeChatList = IsDrawerVisible || Page == MobilePage.Search ||
                          Chat.GetPendingSendReconciliations().Any(send => send.ChatId == Guid.Empty),
        IncludeLibrary = Page == MobilePage.Library,
        CompactTranscript = Client.SupportsCompactTranscript,
        IsForeground = _isApplicationActive,
        StreamIntervalMs = RemoteProtocol.MobileStreamIntervalMs,
        CompactStatus = true,
        ChatRowPatches = true
    };

    private void QueueSubscriptionUpdate()
    {
        if (!_isApplicationActive)
            return;

        var subscription = BuildEventSubscription();
        _ = UpdateEventSubscriptionForConnectionAsync(subscription);
    }

    private async Task UpdateEventSubscriptionForConnectionAsync(
        RemoteEventSubscription subscription)
    {
        using var request = CreateConnectionRequest();
        try
        {
            await Client.UpdateEventSubscriptionAsync(subscription, request.Token);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            // Navigation, host switching, or Android backgrounding superseded this scope.
        }
    }

    private async Task ReacquireVisibleChatAsync(Guid chatId)
    {
        await UpdateEventSubscriptionForConnectionAsync(BuildEventSubscription());
        // Under the list on a single pane the chat is fetched when the list uncovers it.
        if (!_isApplicationActive || Page != MobilePage.Chat || IsDrawerOverlay || Chat.ChatId != chatId)
            return;

        await RefreshTranscriptAsync();
    }

    private void ResetHostScopedState()
    {
        IsWebAppUpdateRequested = false;
        IsWebAppUpdateConfirmationOpen = false;
        _sendReconciliationCandidates = [];
        Interlocked.Increment(ref _sendReconciliationVersion);
        _readWatermarks.Clear();
        _pageHistory.Clear();
        ClearSnapshotSections();
        Client.Stats.Reset();
        // A new PC starts a fresh retry streak instead of inheriting the previous PC's give-up.
        _connection.RetryNow();
        IsConnectionOffline = false;
        IsDisconnectConfirmationOpen = false;
        _canAdoptDesktopActiveChat = true;
        ActiveProjectId = null;
        ResetTranscriptNavigation();
        Chat.ResetHostState();
        ApplyNewChatExperience(null);
        ChatList.SelectedChatId = Guid.Empty;
        ChatList.Apply([]);
        ChatList.SearchText = "";
        SearchChatList.SelectedChatId = Guid.Empty;
        SearchChatList.Apply([]);
        SearchChatList.SearchText = "";
        SearchChatList.ProjectFilterId = null;
        ApplyLibrary(new RemoteLibrary(), reconcileSelections: false);
        Library.ResetHostState();
        ActionChat = null;
        IsChatActionsOpen = false;
        Page = MobilePage.Chat;
        IsDrawerOpen = false;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanDragDrawer))]
    [NotifyPropertyChangedFor(nameof(CanApplyWebAppUpdate))]
    [NotifyCanExecuteChangedFor(nameof(ApplyWebAppUpdateCommand))]
    private bool _isDisconnectConfirmationOpen;

    [RelayCommand]
    private void OpenDisconnectConfirmation() => IsDisconnectConfirmationOpen = true;

    [RelayCommand]
    private void CancelDisconnect() => IsDisconnectConfirmationOpen = false;

    [RelayCommand]
    private async Task ForgetPcAsync()
    {
        if (Client.Token is { Length: > 0 })
        {
            try
            {
                var result = await Client.SendCommandAsync(
                    new RemoteCommand(RemoteProtocol.Actions.RevokeDevice),
                    _connectionLifetime.Token);
                if (!result.Ok)
                {
                    ConnectionMessage = result.Error ?? Strings.Get("RevokeFailed");
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                ConnectionMessage = Strings.Get("RevokeFailed");
                return;
            }
        }

        await ClearPairedStateAsync();
    }

    [RelayCommand]
    private Task ForgetPcLocallyAsync() => ClearPairedStateAsync();

    private async Task ClearPairedStateAsync()
    {
        await Client.StopEventStreamAsync();
        ClearPairedState();
    }

    private void ClearPairedState()
    {
        BeginConnectionGeneration();
        BeginHostGeneration();

        _settings.BaseUrl = "";
        _settings.Token = "";
        _settings.HostName = "";
        _store.Save(_settings);

        Client.Configure("", null);
        IsPaired = false;
        IsConnected = false;
        IsHostReady = false;
        HostName = "";
        Connect.Reset();
        ResetHostScopedState();
    }

    private Task RestartOnboardingAsync()
    {
        ClearPairedState();
        return Connect.InitializeAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsConnectionOffline)
            await RetryConnectionAsync();
        await RefreshSnapshotAsync();
        ConnectionDiagnostics = Client.Stats.Snapshot().Summary;
    }

    public async Task RefreshSnapshotAsync()
    {
        var requestVersion = Interlocked.Increment(ref _snapshotRefreshVersion);
        await _snapshotRefreshGate.WaitAsync();
        try
        {
            if (requestVersion != Volatile.Read(ref _snapshotRefreshVersion))
                return;

            var generation = Volatile.Read(ref _connectionGeneration);
            using var request = CreateConnectionRequest();
            RemoteSnapshot? snapshot;
            // The response may be older than chat-list pushes that land while it travels.
            var chatListReadStartedAt = ChatList.BeginFirstPageRead();
            var chatListReadHandedOff = false;
            try
            {
                try
                {
                    // Sections the phone already holds at their current version come back empty,
                    // so a navigation refresh only pays for what actually changed.
                    snapshot = await Client.GetSnapshotAsync(GetKnownSnapshotSections(), request.Token);
                }
                catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
                {
                    return;
                }
                if (snapshot is null)
                    return;

                chatListReadHandedOff = true;
                _post(() =>
                {
                    var applied = false;
                    try
                    {
                        if (generation == Volatile.Read(ref _connectionGeneration)
                            && requestVersion == Volatile.Read(ref _snapshotRefreshVersion))
                        {
                            ApplySnapshot(snapshot, SnapshotSource.ManualRefresh);
                            applied = true;
                        }
                    }
                    finally
                    {
                        ChatList.EndFirstPageRead(chatListReadStartedAt, applied);
                    }
                });
            }
            finally
            {
                if (!chatListReadHandedOff)
                    ChatList.EndFirstPageRead(chatListReadStartedAt, applied: false);
            }
        }
        finally
        {
            _snapshotRefreshGate.Release();
        }
    }

    /// <summary>The snapshot sections this phone holds, by the desktop's content version.</summary>
    private IReadOnlyDictionary<string, string> GetKnownSnapshotSections()
    {
        lock (_sectionCacheSync)
            return new Dictionary<string, string>(_sectionVersions, StringComparer.Ordinal);
    }

    /// <summary>A live push replaced part of a section, so its cached copy no longer matches a version.</summary>
    private void ForgetSnapshotSection(string section)
    {
        lock (_sectionCacheSync)
            _sectionVersions.Remove(section);
    }

    private void ClearSnapshotSections()
    {
        lock (_sectionCacheSync)
        {
            _sectionVersions.Clear();
            _cachedChatPage = null;
            _cachedLibrary = null;
            _cachedSettings = null;
        }
    }

    /// <summary>
    /// Picks the copy of each section to apply: the snapshot's own, or the cached one the desktop
    /// reported unchanged. Null means the section is not applied at all.
    /// </summary>
    private (RemoteChatPage? Chats, RemoteLibrary? Library, RemoteSettings Settings) ResolveSnapshotSections(
        RemoteSnapshot snapshot)
    {
        var unchanged = snapshot.UnchangedSections ?? [];
        lock (_sectionCacheSync)
        {
            RemoteChatPage? chats = null;
            RemoteLibrary? library = null;
            if (!snapshot.IsPartial)
            {
                chats = Resolve(
                    RemoteProtocol.SnapshotSections.Chats,
                    snapshot.Chats,
                    snapshot.ChatsVersion,
                    ref _cachedChatPage);
                library = Resolve(
                    RemoteProtocol.SnapshotSections.Library,
                    snapshot.Library,
                    snapshot.LibraryVersion,
                    ref _cachedLibrary);
            }

            var settings = Resolve(
                RemoteProtocol.SnapshotSections.Settings,
                snapshot.Settings,
                snapshot.SettingsVersion,
                ref _cachedSettings) ?? snapshot.Settings;
            return (chats, library, settings);
        }

        T? Resolve<T>(string section, T incoming, string? version, ref T? cached) where T : class
        {
            if (unchanged.Contains(section, StringComparer.Ordinal))
            {
                if (cached is null)
                    _sectionVersions.Remove(section);
                return cached;
            }

            cached = incoming;
            if (version is { Length: > 0 })
                _sectionVersions[section] = version;
            else
                _sectionVersions.Remove(section);
            return incoming;
        }
    }

    private void ApplySnapshot(RemoteSnapshot snapshot, SnapshotSource source)
    {
        var shouldAdoptDesktopChat =
            _canAdoptDesktopActiveChat &&
            !Chat.HasChat &&
            !Chat.HasStagedBlankState;
        var didAdoptDesktopChat = false;
        var (chats, library, settings) = ResolveSnapshotSections(snapshot);

        // Exactly one snapshot gets to choose the launch surface. Reconnects and later catalog
        // refreshes are data updates, not navigation commands.
        _canAdoptDesktopActiveChat = false;

        // A snapshot that omits the host name must not erase the name we paired with.
        if (!string.IsNullOrWhiteSpace(snapshot.HostName))
            HostName = snapshot.HostName;

        // The desktop's own Copilot state is NOT the phone's link state. Assigning it to
        // IsConnected here clobbered the link the instant the first snapshot landed, which left the
        // phone permanently "offline" whenever the PC was still warming up its Copilot session.
        IsHostReady = snapshot.IsConnected;

        // Only the link owner may describe the link; a snapshot can only describe the host.
        if (IsConnected)
            ConnectionMessage = snapshot.ConnectionStatus;

        UserName = settings.UserName;
        Chat.ApplyRemoteProtocolVersion(snapshot.ProtocolVersion);

        if (!snapshot.IsPartial)
        {
            if (chats is not null)
            {
                if (string.IsNullOrWhiteSpace(ChatList.SearchText) && ChatList.ProjectFilterId is null)
                    ChatList.Apply(chats);
                else
                    _ = ChatList.RefreshFromServerAsync();
            }
            if (Page == MobilePage.Search)
                _ = SearchChatList.RefreshFromServerAsync();
            if (library is not null)
                ApplyLibrary(library, reconcileSelections: false);
        }
        Chat.ApplyCatalogs(settings);
        ApplyNewChatExperience(settings.NewChat);

        // Adopt the desktop's active chat on first connect so the phone opens where the PC left off.
        if (!snapshot.IsPartial &&
            shouldAdoptDesktopChat &&
            snapshot.ActiveChatId is { } activeChatId &&
            activeChatId != Guid.Empty)
        {
            var activeChat = snapshot.ActiveChat
                ?? chats?.Groups
                    .SelectMany(group => group.Chats)
                    .FirstOrDefault(chat => chat.Id == activeChatId);

            ResetTranscriptNavigation();
            Chat.Reset(activeChatId, activeChat?.Title ?? Strings.Get("Chat"), activeChat?.LastModelUsed);
            Chat.IsLoading = true;
            ChatList.SelectedChatId = activeChatId;
            didAdoptDesktopChat = true;
        }

        if (source == SnapshotSource.Bootstrap)
            BootstrapSnapshotCount++;

        if (!snapshot.IsPartial)
            UpdateSendReconciliationCandidates(chats ?? new RemoteChatPage(), snapshot.ActiveChatId);
        QueueSubscriptionUpdate();

        if (Chat.ChatId != Guid.Empty
            && (source == SnapshotSource.Bootstrap
                || (source == SnapshotSource.ManualRefresh && didAdoptDesktopChat)))
        {
            _ = RefreshTranscriptAsync();
        }
    }

    private void UpdateSendReconciliationCandidates(RemoteChatPage page, Guid? activeChatId = null)
    {
        // These are candidates only. A running sidebar row or the desktop's active chat cannot
        // acknowledge a send; only its matching persisted user-message request ID can.
        _sendReconciliationCandidates = (activeChatId is { } active ? new[] { active } : [])
            .Concat(page.Groups.SelectMany(group => group.Chats)
                .OrderByDescending(chat => chat.UpdatedAt).Select(chat => chat.Id))
            .Concat(_sendReconciliationCandidates)
            .Where(id => id != Guid.Empty && !page.RemovedChatIds.Contains(id))
            .Distinct()
            .Take(8)
            .ToArray();
        _ = ReconcilePendingSendsAsync();
    }

    internal async Task ReconcilePendingSendsAsync()
    {
        if (!_isApplicationActive || !IsConnected || Chat.SendCommand.IsRunning)
            return;

        var version = Interlocked.Increment(ref _sendReconciliationVersion);
        var generation = Volatile.Read(ref _connectionGeneration);
        await _sendReconciliationGate.WaitAsync();
        try
        {
            if (version != Volatile.Read(ref _sendReconciliationVersion) ||
                generation != Volatile.Read(ref _connectionGeneration))
                return;

            var pending = Chat.GetPendingSendReconciliations();
            using var request = CreateConnectionRequest();
            foreach (var send in pending)
            {
                var candidates = send.ChatId == Guid.Empty
                    ? _sendReconciliationCandidates
                    : new[] { send.ChatId };
                foreach (var chatId in candidates)
                {
                    if (version != Volatile.Read(ref _sendReconciliationVersion))
                        return;

                    // Read the send's position, not an arbitrarily large reply's latest tail.
                    // A missing receipt stays ambiguous; this path never posts a command.
                    var transcript = await Client.GetTranscriptAsync(
                        chatId, send.BeforeMessageIndex, maxItems: 1, request.Token);
                    if (transcript?.ChatId != chatId ||
                        !transcript.Turns.SelectMany(turn => turn.Items).Any(item =>
                            item.Kind == RemoteProtocol.ItemKinds.User &&
                            string.Equals(item.RequestId, send.RequestId, StringComparison.Ordinal)))
                        continue;

                    PostForConnection(generation, () =>
                    {
                        if (Chat.TryReconcilePendingSend(send, transcript))
                        {
                            if (string.Equals(ConnectionMessage, send.ErrorText, StringComparison.Ordinal))
                                ConnectionMessage = null;
                            QueueSubscriptionUpdate();
                        }
                    });
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (
            generation != Volatile.Read(ref _connectionGeneration) || _lifetime.IsCancellationRequested)
        {
            // Backgrounding or changing PCs superseded this read-only confirmation pass.
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Mobile] Send receipt reconciliation failed: {ex}");
        }
        finally
        {
            _sendReconciliationGate.Release();
        }
    }

    private void OnChatActivated(Guid chatId, string title, string? model)
    {
        ChatList.SelectedChatId = chatId;
        SearchChatList.SelectedChatId = chatId;
        _ = ActivateChatAsync(chatId, title, model);
    }

    private void PromoteActiveChat(
        Guid chatId,
        string? preview,
        bool isNewChat = false)
    {
        ChatList.PromoteChat(new RemoteChat
        {
            Id = chatId,
            Title = Chat.Title,
            Preview = preview,
            ProjectId = Guid.TryParse(Chat.ProjectValue, out var projectId) ? projectId : null,
            ProjectName = Chat.ProjectName,
            AgentId = Guid.TryParse(Chat.AgentValue, out var agentId) ? agentId : null,
            AgentName = Chat.AgentName,
            AgentGlyph = Chat.AgentGlyph,
            MessageCount = 1,
            UpdatedAt = DateTimeOffset.Now,
            IsRunning = true,
            IsSessionActive = true,
            LastModelUsed = Chat.Model
        }, isNewChat);
    }

    private string? LatestUserPreview() => Chat.Turns
        .SelectMany(turn => turn.Items)
        .OfType<UserTurnItemViewModel>()
        .LastOrDefault()
        ?.Text;

    private async Task ActivateChatAsync(
        Guid chatId,
        string title,
        string? model,
        bool rememberPage = true)
    {
        var previous = CaptureNavigation();
        // From this point on the phone owns navigation. A delayed/reconnect snapshot must not jump
        // away from either the selected chat or a deliberately blank New Chat surface.
        _canAdoptDesktopActiveChat = false;
        if (chatId != Guid.Empty && Chat.ChatId == chatId)
        {
            Page = MobilePage.Chat;
            IsDrawerOpen = false;
            if (rememberPage)
                RememberNavigation(previous);
            await RefreshTranscriptAsync();
            return;
        }

        ResetTranscriptNavigation();
        var activation = _transcriptSurfaceGeneration;
        var connection = Volatile.Read(ref _connectionGeneration);
        Chat.Reset(chatId, title, model, isLoading: chatId != Guid.Empty);

        // Picking a chat is the drawer's whole purpose, so get out of the way and show it.
        _chatActivationOwnsSubscription = chatId != Guid.Empty;
        try
        {
            Page = MobilePage.Chat;
        }
        finally
        {
            _chatActivationOwnsSubscription = false;
        }
        IsDrawerOpen = false;
        if (rememberPage)
            RememberNavigation(previous);

        // A blank chat has nothing on the PC yet — it is created on first send. Asking the desktop
        // to open Guid.Empty would fail, and flagging it as loading would spin forever.
        if (chatId == Guid.Empty)
        {
            Chat.IsLoading = false;

            // Reset cleared the staged choices; re-stage the project lens so the chat this send
            // creates lands inside the project the user is currently looking at.
            if (ActiveProjectId is { } projectId)
            {
                Chat.ProjectValue = projectId.ToString();
                Chat.ProjectName = ActiveProject;
            }

            if (IsConnected)
                await RefreshSnapshotAsync();
            return;
        }

        Chat.IsLoading = true;
        await UpdateEventSubscriptionForConnectionAsync(BuildEventSubscription());
        if (activation != _transcriptSurfaceGeneration
            || connection != Volatile.Read(ref _connectionGeneration) || Chat.ChatId != chatId)
            return;

        await RefreshTranscriptAsync();
    }

    private bool IsChatTranscriptVisible => IsPaired && _isApplicationActive && Page == MobilePage.Chat
        && !IsDrawerOverlay && !Chat.IsGitChangesOpen;

    private bool CanReadChatTranscript => IsChatTranscriptVisible && !Chat.IsLoading;

    private async Task MarkChatReadAsync(Guid chatId, int messageCount, bool? hasUnreadMessages)
    {
        if (hasUnreadMessages == false
            || (hasUnreadMessages is null
                && _readWatermarks.TryGetValue(chatId, out var acknowledged)
                && acknowledged >= messageCount))
            return;

        var connection = Volatile.Read(ref _connectionGeneration);
        var surface = _transcriptSurfaceGeneration;
        var statusVersion = Chat.StatusVersion;
        try
        {
            var result = await SendCommandAsync(
                new RemoteCommand(RemoteProtocol.Actions.OpenChat)
                    .With("chatId", chatId.ToString())
                    .With("readThroughMessageCount", messageCount.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)));
            if (connection != Volatile.Read(ref _connectionGeneration)
                || surface != _transcriptSurfaceGeneration || Chat.ChatId != chatId)
                return;

            if (!result.Ok)
                Trace.TraceWarning($"[Mobile] Could not mark chat {chatId} read: {result.Error}");
            else
            {
                if (hasUnreadMessages is null)
                    _readWatermarks[chatId] = messageCount;
                // Do not clear a newer desktop read-state update that arrived during the request.
                if (Chat.StatusVersion == statusVersion && CanReadChatTranscript
                    && Chat.IsLatestWindow && Chat.TotalRawMessageCount <= messageCount)
                {
                    ChatList.SetUnread(chatId, false);
                    SearchChatList.SetUnread(chatId, false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The shell is shutting down.
        }
    }

    private void OnChatRemoved(Guid chatId)
    {
        // The deleted chat may be in Back history without being the current transcript.
        if (_pageHistory.Count > 0)
        {
            var remaining = _pageHistory
                .Where(page => page.Page != MobilePage.Chat || page.ChatId != chatId)
                .Reverse()
                .ToArray();
            _pageHistory.Clear();
            foreach (var page in remaining)
                _pageHistory.Push(page);
            OnPropertyChanged(nameof(CanGoBack));
        }

        if (Chat.ChatId != chatId)
            return;

        ResetTranscriptNavigation();
        ChatList.SelectedChatId = Guid.Empty;
        Chat.Reset(Guid.Empty, Strings.Get("NewChat"));
    }

    public Task RefreshTranscriptAsync()
    {
        if (Chat.ChatId == Guid.Empty)
            return Task.CompletedTask;

        if (TryDeferAutomaticTranscriptUpdate(out var navigation))
            return navigation;

        // Reconnects and catalog snapshots are synchronization hints, not navigation commands. An
        // older-history reader stays on that bounded page and gets a visible freshness cue instead.
        if (!Chat.IsLatestWindow)
        {
            Chat.MarkNewerActivityAvailable();
            return Task.CompletedTask;
        }

        return RequestTranscriptRefreshAsync(
            beforeMessageIndex: null,
            TranscriptRefreshKind.Refresh,
            showLoading: Chat.Turns.Count == 0,
            cancelActive: false);
    }

    [RelayCommand]
    private Task LoadEarlierActivityAsync()
    {
        if (!Chat.CanNavigateTranscript || !Chat.HasEarlierMessages || Chat.WindowStartMessageIndex <= 0)
            return Task.CompletedTask;

        return RequestTranscriptRefreshAsync(
            Chat.WindowStartMessageIndex,
            TranscriptRefreshKind.Earlier,
            showLoading: true,
            cancelActive: true);
    }

    [RelayCommand]
    private Task LoadNewerActivityAsync()
    {
        if (!Chat.CanNavigateTranscript || Chat.IsLatestWindow)
            return Task.CompletedTask;

        if (_newerTranscriptCursors.Count == 0)
            return ReturnToLatestAsync();

        var cursor = _newerTranscriptCursors.Peek();
        return RequestTranscriptRefreshAsync(
            cursor,
            TranscriptRefreshKind.Newer,
            showLoading: true,
            cancelActive: true);
    }

    [RelayCommand]
    private Task ReturnToLatestAsync()
    {
        if (!Chat.CanNavigateTranscript || Chat.IsLatestWindow)
            return Task.CompletedTask;

        return RequestTranscriptRefreshAsync(
            beforeMessageIndex: null,
            TranscriptRefreshKind.Latest,
            showLoading: true,
            cancelActive: true);
    }

    private Task RequestTranscriptRefreshAsync(
        int? beforeMessageIndex,
        TranscriptRefreshKind kind,
        bool showLoading,
        bool cancelActive)
    {
        var chatId = Chat.ChatId;
        if (chatId == Guid.Empty)
            return Task.CompletedTask;

        if (kind == TranscriptRefreshKind.Refresh
            && TryDeferAutomaticTranscriptUpdate(out var navigation))
        {
            return navigation;
        }

        if (showLoading)
        {
            Chat.IsLoading = true;
            Chat.TranscriptErrorText = null;
        }

        CancellationTokenSource? activeToCancel = null;
        Task loop;
        lock (_transcriptRefreshSync)
        {
            var request = new TranscriptRefreshRequest(
                chatId,
                beforeMessageIndex,
                Chat.WindowEndMessageIndex,
                kind,
                _transcriptSurfaceGeneration,
                ++_transcriptRequestVersion,
                Chat.StatusVersion,
                // An automatic refresh of the window already on screen asks the desktop whether
                // it changed at all; an unchanged answer costs no body, parse or projection.
                kind == TranscriptRefreshKind.Refresh && beforeMessageIndex is null
                    ? Chat.TranscriptVersion
                    : null);

            // One pending slot is enough: every trigger asks for the newest known view of one exact
            // chat/window, so replacing it coalesces reconnect and invalidation bursts.
            _pendingTranscriptRefresh = request;

            if (cancelActive)
                activeToCancel = _activeTranscriptRefreshCts;

            _transcriptRefreshLoop ??= Task.Run(RunTranscriptRefreshLoopAsync);
            loop = _transcriptRefreshLoop;
        }

        CancelSafely(activeToCancel);
        return loop;
    }

    private bool TryDeferAutomaticTranscriptUpdate(out Task navigation)
    {
        lock (_transcriptRefreshSync)
        {
            if (!HasUserNavigationLocked())
            {
                navigation = Task.CompletedTask;
                return false;
            }

            _hasDeferredNewerActivity = true;
            navigation = _transcriptRefreshLoop ?? Task.CompletedTask;
        }

        Chat.MarkNewerActivityAvailable();
        return true;
    }

    private bool HasUserNavigationLocked() =>
        _pendingTranscriptRefresh is { IsNavigation: true }
        || _activeTranscriptRefreshRequest is { IsNavigation: true }
        || _applyingTranscriptRefreshRequest is { IsNavigation: true };

    private async Task RunTranscriptRefreshLoopAsync()
    {
        while (true)
        {
            TranscriptRefreshRequest request;
            CancellationTokenSource requestCts;
            long heldWatermark;

            lock (_transcriptRefreshSync)
            {
                if (_pendingTranscriptRefresh is not { } pending)
                {
                    _transcriptRefreshLoop = null;
                    return;
                }

                request = pending;
                _pendingTranscriptRefresh = null;
                var connectionLifetime = _connectionLifetime;
                requestCts = CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetime.Token,
                    connectionLifetime.Token);
                _activeTranscriptRefreshRequest = request;
                _activeTranscriptRefreshCts = requestCts;
                // Deltas held before this read started are older than what it will capture.
                heldWatermark = _heldDeltaSequence;
            }

            RemoteTranscript? transcript = null;
            string? error = null;
            var wasCancelled = false;
            try
            {
                transcript = await Client
                    .GetTranscriptAsync(
                        request.ChatId,
                        request.BeforeMessageIndex,
                        request.Kind is TranscriptRefreshKind.Earlier or TranscriptRefreshKind.Newer
                            ? Client.SupportsCompactTranscript
                                ? RemoteProtocol.CompactTranscriptWindowVisibleItemLimit
                                : RemoteProtocol.TranscriptWindowRawMessageLimit
                            : Client.SupportsCompactTranscript
                                ? RemoteProtocol.InitialCompactTranscriptWindowVisibleItemLimit
                                : RemoteProtocol.InitialTranscriptWindowRawMessageLimit,
                        request.KnownVersion,
                        requestCts.Token)
                    .ConfigureAwait(false);

                if (transcript is null)
                    error = Client.StateMessage ?? Strings.Get("LoadActivityFailed");
                else if (transcript.ChatId != request.ChatId)
                {
                    transcript = null;
                    error = Strings.Get("WrongChatActivity");
                }
            }
            catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
            {
                wasCancelled = true;
            }
            catch (Exception ex)
            {
                error = Strings.Get("LoadActivityFailed");
                Trace.TraceWarning($"[Mobile] Transcript refresh failed: {ex}");
            }
            finally
            {
                lock (_transcriptRefreshSync)
                {
                    if (ReferenceEquals(_activeTranscriptRefreshCts, requestCts))
                        _activeTranscriptRefreshCts = null;
                    if (ReferenceEquals(_activeTranscriptRefreshRequest, request))
                        _activeTranscriptRefreshRequest = null;
                    if (!wasCancelled
                        && request.Version == _transcriptRequestVersion
                        && request.SurfaceGeneration == _transcriptSurfaceGeneration)
                    {
                        _applyingTranscriptRefreshRequest = request;
                    }
                }

                requestCts.Dispose();
            }

            if (wasCancelled)
                continue;

            _post(() =>
            {
                var queueTrailingLatestRefresh = false;
                var queueFullRefresh = false;
                try
                {
                    if (!IsCurrentTranscriptRequest(request))
                        return;

                    if (transcript is null)
                    {
                        Chat.TranscriptErrorText = error;
                        Chat.IsLoading = false;
                        ApplyDeferredNewerActivity(request);
                        return;
                    }

                    if (transcript.IsUnchanged)
                    {
                        if (!Chat.ApplyUnchangedTranscript(transcript, request.StatusVersion))
                        {
                            // The confirmation no longer describes what is on screen: read it all.
                            Chat.ForgetTranscriptVersion();
                            queueFullRefresh = true;
                        }

                        Chat.IsLoading = false;
                        if (!queueFullRefresh
                            && CanReadChatTranscript
                            && request.StatusVersion == Chat.StatusVersion)
                        {
                            _ = MarkChatReadAsync(
                                transcript.ChatId,
                                Chat.TotalRawMessageCount,
                                transcript.Status.HasUnreadMessages);
                        }

                        ReplayHeldDeltas(request.ChatId, heldWatermark);
                    }
                    else
                    {
                        ApplyTranscriptNavigation(request, transcript);
                        Chat.ApplyTranscript(transcript, request.StatusVersion);
                        Chat.IsLoading = false;
                        if (CanReadChatTranscript
                            && request.StatusVersion == Chat.StatusVersion
                            && transcript.IsLatestWindow)
                        {
                            _ = MarkChatReadAsync(
                                transcript.ChatId,
                                transcript.TotalRawMessageCount,
                                transcript.Status.HasUnreadMessages);
                        }

                        if (transcript.IsLatestWindow)
                            ReplayHeldDeltas(request.ChatId, heldWatermark);
                        else
                            ClearHeldDeltas();
                    }

                    queueTrailingLatestRefresh = ApplyDeferredNewerActivity(request);
                }
                finally
                {
                    lock (_transcriptRefreshSync)
                    {
                        if (ReferenceEquals(_applyingTranscriptRefreshRequest, request))
                            _applyingTranscriptRefreshRequest = null;
                    }
                }

                if (queueTrailingLatestRefresh || queueFullRefresh)
                {
                    _ = RequestTranscriptRefreshAsync(
                        beforeMessageIndex: null,
                        TranscriptRefreshKind.Refresh,
                        showLoading: false,
                        cancelActive: false);
                }
            });
        }
    }

    private bool ApplyDeferredNewerActivity(TranscriptRefreshRequest request)
    {
        if (!request.IsNavigation)
            return false;

        lock (_transcriptRefreshSync)
        {
            if (!_hasDeferredNewerActivity)
                return false;

            _hasDeferredNewerActivity = false;
        }

        Chat.MarkNewerActivityAvailable();
        return Chat.IsLatestWindow;
    }

    /// <summary>
    /// Keeps a delta the phone cannot place yet. One latest-window read is enough for all of them:
    /// a read already in flight is followed by a replay, so a busy stream never chains fetches.
    /// </summary>
    private void HoldDeltaForTranscript(RemoteStreamDelta delta)
    {
        bool readCovers;
        var overflowed = false;
        lock (_transcriptRefreshSync)
        {
            if (_heldDeltas.Count >= MaxHeldDeltas)
            {
                _heldDeltas.Clear();
                overflowed = true;
            }

            _heldDeltas.Add(new HeldDelta(++_heldDeltaSequence, delta));
            readCovers = IsLatestReadQueuedLocked(delta.ChatId, includeApplying: true);
        }

        Client.Stats.RecordDelta(readCovers && !overflowed
            ? StreamDeltaDisposition.Held
            : StreamDeltaDisposition.Refetched);
        if (!readCovers || overflowed)
            QueueTranscriptRefresh();
    }

    /// <summary>
    /// Replays held deltas against a transcript that just landed. Those it covers or extends are
    /// done; unresolved ones received before the read started are older than it and dropped; the
    /// rest wait for one more read.
    /// </summary>
    private void ReplayHeldDeltas(Guid chatId, long watermark)
    {
        List<HeldDelta> held;
        lock (_transcriptRefreshSync)
        {
            if (_heldDeltas.Count == 0)
                return;

            held = [.. _heldDeltas];
            _heldDeltas.Clear();
        }

        var unresolved = new List<HeldDelta>();
        foreach (var entry in held)
        {
            if (entry.Delta.ChatId != chatId || chatId != Chat.ChatId)
            {
                Client.Stats.RecordDelta(StreamDeltaDisposition.Dropped);
                continue;
            }

            switch (Chat.ApplyStreamDelta(entry.Delta))
            {
                case StreamDeltaOutcome.Applied:
                    Client.Stats.RecordDelta(StreamDeltaDisposition.Replayed);
                    break;
                case StreamDeltaOutcome.Missing or StreamDeltaOutcome.Gap when entry.Sequence > watermark:
                    unresolved.Add(entry);
                    break;
                default:
                    Client.Stats.RecordDelta(StreamDeltaDisposition.Dropped);
                    break;
            }
        }

        if (unresolved.Count == 0)
            return;

        bool readCovers;
        lock (_transcriptRefreshSync)
        {
            _heldDeltas.InsertRange(0, unresolved);
            readCovers = IsLatestReadQueuedLocked(chatId, includeApplying: false);
        }

        if (!readCovers)
            QueueTranscriptRefresh();
    }

    private void ClearHeldDeltas()
    {
        lock (_transcriptRefreshSync)
            _heldDeltas.Clear();
    }

    /// <summary>
    /// True when a latest-window read for this chat is queued or in flight (and, optionally, being
    /// applied), so a held delta will be replayed without asking for another read.
    /// </summary>
    private bool IsLatestReadQueuedLocked(Guid chatId, bool includeApplying) =>
        _activeTranscriptRefreshRequest is { BeforeMessageIndex: null } active && active.ChatId == chatId
        || _pendingTranscriptRefresh is { BeforeMessageIndex: null } pending && pending.ChatId == chatId
        || includeApplying
           && _applyingTranscriptRefreshRequest is { BeforeMessageIndex: null } applying
           && applying.ChatId == chatId;

    private bool IsCurrentTranscriptRequest(TranscriptRefreshRequest request)
    {
        lock (_transcriptRefreshSync)
        {
            return request.Version == _transcriptRequestVersion
                   && request.SurfaceGeneration == _transcriptSurfaceGeneration
                   && Chat.ChatId == request.ChatId;
        }
    }

    private void ApplyTranscriptNavigation(
        TranscriptRefreshRequest request,
        RemoteTranscript transcript)
    {
        switch (request.Kind)
        {
            case TranscriptRefreshKind.Earlier:
                if (request.PreviousWindowEndMessageIndex > 0)
                    _newerTranscriptCursors.Push(request.PreviousWindowEndMessageIndex);
                break;
            case TranscriptRefreshKind.Newer:
                if (_newerTranscriptCursors.Count > 0)
                    _newerTranscriptCursors.Pop();
                break;
            case TranscriptRefreshKind.Refresh:
                break;
            case TranscriptRefreshKind.Latest:
                _newerTranscriptCursors.Clear();
                break;
        }

        if (transcript.IsLatestWindow)
            _newerTranscriptCursors.Clear();
    }

    private void ResetTranscriptNavigation()
    {
        CancellationTokenSource? active;
        lock (_transcriptRefreshSync)
        {
            _transcriptSurfaceGeneration++;
            _transcriptRequestVersion++;
            _pendingTranscriptRefresh = null;
            _activeTranscriptRefreshRequest = null;
            _applyingTranscriptRefreshRequest = null;
            _hasDeferredNewerActivity = false;
            _heldDeltas.Clear();
            active = _activeTranscriptRefreshCts;
        }

        CancelSafely(active);
        _newerTranscriptCursors.Clear();
    }

    private static void CancelSafely(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
            return;

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion won the race and already released the per-request source.
        }
    }

    private void OnClientStateChanged(RemoteLinkState state, string? message)
    {
        var generation = Volatile.Read(ref _connectionGeneration);
        PostForConnection(generation, () =>
        {
            Chat.RefreshInteractiveCapabilities();
            ConnectionMessage = message;
            IsGatewaySignInRequired = state == RemoteLinkState.GatewaySignInRequired;

            switch (state)
            {
                case RemoteLinkState.Connected:
                    IsConnected = true;
                    IsConnectionOffline = false;
                    break;
                case RemoteLinkState.Unauthorized:
                    _ = RestartOnboardingAsync();
                    ConnectionMessage = message;
                    break;
                case RemoteLinkState.Offline:
                    IsConnected = false;
                    IsHostReady = false;
                    IsConnectionOffline = true;
                    break;
                default:
                    IsConnected = false;
                    IsHostReady = false;
                    break;
            }
        });
    }

    private void OnFrameReceived(RemoteEventFrame frame, bool isBootstrapSnapshot)
    {
        var generation = Volatile.Read(ref _connectionGeneration);
        void Post(Action action) => PostForConnection(generation, action);

        // Deserialize off the UI thread; only the (cheap) apply step is posted.
        switch (frame.Event)
        {
            case RemoteProtocol.Events.Ping:
                return;

            case RemoteProtocol.Events.Snapshot:
            {
                if (TryParse(frame.Data, RemoteJsonContext.Default.RemoteSnapshot) is { } snapshot)
                {
                    Post(() => ApplySnapshot(
                        snapshot,
                        isBootstrapSnapshot ? SnapshotSource.Bootstrap : SnapshotSource.CatalogEvent));
                }
                return;
            }

            case RemoteProtocol.Events.Chats:
            {
                if (TryParse(frame.Data, RemoteJsonContext.Default.RemoteChatPage) is { } page)
                {
                    ForgetSnapshotSection(RemoteProtocol.SnapshotSections.Chats);
                    Post(() =>
                    {
                        foreach (var removedChatId in page.RemovedChatIds)
                            OnChatRemoved(removedChatId);
                        UpdateSendReconciliationCandidates(page);
                        // Pushes update rows in place; filtered lists and search results only
                        // refresh the rows they show, instead of downloading the list again.
                        ChatList.ApplyLive(page);
                        if (Page == MobilePage.Search)
                            SearchChatList.ApplyLive(page);
                    });
                }
                return;
            }

            case RemoteProtocol.Events.Library:
            {
                if (TryParse(frame.Data, RemoteJsonContext.Default.RemoteLibrary) is { } library)
                {
                    ForgetSnapshotSection(RemoteProtocol.SnapshotSections.Library);
                    Post(() =>
                    {
                        ApplyLibrary(library);
                    });
                }
                return;
            }

            case RemoteProtocol.Events.ChatStatus:
            {
                if (TryParse(frame.Data, RemoteJsonContext.Default.RemoteChatStatus) is { } status)
                {
                    Post(() =>
                    {
                        Chat.ApplyStatus(status);
                        ChatList.SetRunning(status.ChatId, status.IsBusy || status.IsStreaming);
                        SearchChatList.SetRunning(status.ChatId, status.IsBusy || status.IsStreaming);
                        ChatList.SetSessionActive(
                            status.ChatId,
                            status.IsSessionActive || status.IsBusy || status.IsStreaming);
                        SearchChatList.SetSessionActive(
                            status.ChatId,
                            status.IsSessionActive || status.IsBusy || status.IsStreaming);
                        if (status.HasUnreadMessages is { } unread)
                        {
                            ChatList.SetUnread(status.ChatId, unread);
                            SearchChatList.SetUnread(status.ChatId, unread);
                            if (unread && status.ChatId == Chat.ChatId && IsChatTranscriptVisible)
                                QueueTranscriptRefresh();
                        }
                    });
                }
                return;
            }

            case RemoteProtocol.Events.Connection:
            {
                if (TryParse(frame.Data, RemoteJsonContext.Default.RemoteConnectionStatus) is { } connection)
                {
                    Post(() =>
                    {
                        IsHostReady = connection.IsConnected;
                        ConnectionMessage = connection.Status;
                    });
                }
                return;
            }

            case RemoteProtocol.Events.StreamDelta:
            {
                if (TryParse(frame.Data, RemoteJsonContext.Default.RemoteStreamDelta) is { } delta)
                {
                    if (delta.IsReasoning && Client.SupportsCompactTranscript)
                        return;

                    Post(() =>
                    {
                        if (delta.ChatId == Chat.ChatId
                            && TryDeferAutomaticTranscriptUpdate(out _))
                        {
                            return;
                        }

                        if (delta.ChatId == Chat.ChatId && !Chat.IsLatestWindow)
                        {
                            Chat.MarkNewerActivityAvailable();
                            return;
                        }

                        switch (Chat.ApplyStreamDelta(delta))
                        {
                            case StreamDeltaOutcome.Applied:
                                Client.Stats.RecordDelta(StreamDeltaDisposition.Applied);
                                break;
                            case StreamDeltaOutcome.Missing or StreamDeltaOutcome.Gap
                                when delta.ChatId == Chat.ChatId:
                                // The row is not here yet, or the text has a hole. Keep the delta
                                // for the transcript that will carry its predecessor instead of
                                // letting every frame of a busy stream request another fetch.
                                HoldDeltaForTranscript(delta);
                                break;
                        }
                    });
                }

                return;
            }

            case RemoteProtocol.Events.TranscriptInvalidated:
            {
                if (TryParse(frame.Data, RemoteJsonContext.Default.RemoteTranscriptInvalidated) is { } invalidated)
                    Post(() =>
                    {
                        if (invalidated.ChatId == Chat.ChatId)
                        {
                            if (TryDeferAutomaticTranscriptUpdate(out _))
                                return;

                            if (Chat.IsLatestWindow)
                                QueueTranscriptRefresh();
                            else
                                Chat.MarkNewerActivityAvailable();
                        }
                    });

                return;
            }
        }
    }

    private void QueueTranscriptRefresh()
    {
        if (Chat.ChatId == Guid.Empty)
            return;

        Chat.InvalidateTranscriptAuthority();
        if (TryDeferAutomaticTranscriptUpdate(out _))
            return;

        if (!Chat.IsLatestWindow)
        {
            Chat.MarkNewerActivityAvailable();
            return;
        }

        _ = RequestTranscriptRefreshAsync(
            beforeMessageIndex: null,
            TranscriptRefreshKind.Refresh,
            showLoading: false,
            cancelActive: false);
    }

    private static T? TryParse<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    internal void BeginPendingRemoteAction()
    {
        Interlocked.Increment(ref _commandsInFlight);
        _post(NotifyPendingRemoteActionSafetyChanged);
    }

    internal void EndPendingRemoteAction()
    {
        Interlocked.Decrement(ref _commandsInFlight);
        _post(NotifyPendingRemoteActionSafetyChanged);
    }

    private void NotifyPendingRemoteActionSafetyChanged()
    {
        NotifyWebAppUpdateSafetyChanged();
        OnPropertyChanged(nameof(CanAutomaticallyRecoverGatewaySignIn));
    }

    public async Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command)
    {
        BeginPendingRemoteAction();
        try
        {
            var generation = Volatile.Read(ref _connectionGeneration);
            // Commands outlive the foreground stream: a message sent just before the phone locks
            // still reaches the PC. Only switching or forgetting the PC abandons it.
            using var request = CreateHostRequest();
            RemoteCommandResult result;
            try
            {
                result = await Client.SendCommandAsync(command, request.Token);
            }
            catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
            {
                result = new RemoteCommandResult
                {
                    Error = Strings.Get("ConnectionChangedAction"),
                    RequestId = command.RequestId,
                    IsOutcomeUnknown = true
                };
            }

            if (!result.Ok && result.Error is { Length: > 0 } error)
                PostForConnection(generation, () => ConnectionMessage = error);

            return result;
        }
        finally
        {
            EndPendingRemoteAction();
        }
    }

    public async Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content)
    {
        // An upload the user started keeps going if they switch apps while it runs.
        using var request = CreateHostRequest();
        try
        {
            return await Client.UploadAsync(fileName, content, request.Token);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            return new RemoteUploadResponse { Error = Strings.Get("ConnectionChangedUpload") };
        }
    }

    public async Task<RemoteLibraryItem?> GetLibraryItemAsync(string resource, string identifier)
    {
        using var request = CreateConnectionRequest();
        try
        {
            return await Client.GetLibraryItemAsync(resource, identifier, request.Token);
        }

        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<RemoteFileSuggestions?> GetFileSuggestionsAsync(
        Guid? chatId,
        Guid? projectId,
        string query,
        CancellationToken cancellationToken)
    {
        using var request = CreateConnectionRequest();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            request.Token,
            cancellationToken);
        try
        {
            return await Client.GetFileSuggestionsAsync(
                chatId,
                projectId,
                query,
                linked.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public Task RefreshCatalogsAsync() => RefreshSnapshotAsync();

    public async Task<RemoteChatPage?> GetChatPageAsync(
        int offset,
        int limit,
        string? query,
        Guid? projectId,
        CancellationToken cancellationToken)
    {
        using var connectionRequest = CreateConnectionRequest();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            connectionRequest.Token,
            cancellationToken);
        try
        {
            return await Client.GetChatsAsync(offset, limit, query, projectId, linked.Token);
        }
        catch (OperationCanceledException) when (
            connectionRequest.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<string?> DownloadProducedFileAsync(
        Guid chatId,
        Guid messageId,
        string fileName)
    {
        using var request = CreateConnectionRequest();
        try
        {
            return await Client.DownloadProducedFileAsync(
                chatId,
                messageId,
                fileName,
                request.Token);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<string?> DownloadMarkdownImageAsync(
        Guid chatId,
        Guid messageId,
        int imageIndex,
        string fileName,
        CancellationToken cancellationToken)
    {
        using var request = CreateConnectionRequest();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            request.Token,
            cancellationToken);
        try
        {
            return await Client.DownloadMarkdownImageAsync(
                chatId,
                messageId,
                imageIndex,
                fileName,
                linked.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public void ReleaseMarkdownImages(
        Guid chatId,
        Guid messageId,
        IReadOnlyList<RemoteInlineImage> images) =>
        Client.ReleaseMarkdownImages(chatId, messageId, images);

    public async Task<RemoteActivityDetails?> GetActivityDetailsAsync(
        Guid chatId,
        string activityId)
    {
        using var request = CreateConnectionRequest();
        try
        {
            return await Client.GetActivityDetailsAsync(chatId, activityId, request.Token);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();

        Task? transcriptLoop;
        lock (_transcriptRefreshSync)
        {
            _pendingTranscriptRefresh = null;
            _transcriptRequestVersion++;
            transcriptLoop = _transcriptRefreshLoop;
        }

        if (transcriptLoop is not null)
        {
            try
            {
                await transcriptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The lifetime cancellation above is the expected shutdown path.
            }
        }

        ChatList.Dispose();
        SearchChatList.Dispose();
        _connection.Dispose();
        await Client.DisposeAsync();
        await _connectionLifetime.CancelAsync();
        _connectionLifetime.Dispose();
        await _hostLifetime.CancelAsync();
        _hostLifetime.Dispose();
        _startGate.Dispose();
        _lifecycleGate.Dispose();
        _lifetime.Dispose();
    }

    private void BeginConnectionGeneration()
    {
        CancellationTokenSource previous;
        lock (_transcriptRefreshSync)
        {
            previous = _connectionLifetime;
            _connectionLifetime = new CancellationTokenSource();
            Interlocked.Increment(ref _connectionGeneration);
        }
        try { previous.Cancel(); }
        catch (ObjectDisposedException) { }
        previous.Dispose();
    }

    /// <summary>Abandons user actions that belong to the previous PC.</summary>
    private void BeginHostGeneration()
    {
        CancellationTokenSource previous;
        lock (_transcriptRefreshSync)
        {
            previous = _hostLifetime;
            _hostLifetime = new CancellationTokenSource();
        }
        try { previous.Cancel(); }
        catch (ObjectDisposedException) { }
        previous.Dispose();
    }

    private CancellationTokenSource CreateConnectionRequest()
    {
        lock (_transcriptRefreshSync)
        {
            var connectionLifetime = _connectionLifetime;
            return CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token,
                connectionLifetime.Token);
        }
    }

    private CancellationTokenSource CreateHostRequest()
    {
        lock (_transcriptRefreshSync)
        {
            var hostLifetime = _hostLifetime;
            return CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token,
                hostLifetime.Token);
        }
    }

    private void PostForConnection(long generation, Action action) =>
        _post(() =>
        {
            if (generation == Volatile.Read(ref _connectionGeneration))
                action();
        });
}
