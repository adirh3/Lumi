using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

public class ChatGroup
{
    public string Label { get; set; } = "";
    public ObservableCollection<Chat> Chats { get; set; } = [];
}

public sealed record DetachedChatWindowRequest(
    Chat? Chat,
    ChatWindowViewModel WindowVM,
    Action ReleaseSurface);

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;
    /// <summary>A dedicated BrowserService for Settings cookie import/clear (not tied to any chat).</summary>
    private readonly BrowserService _settingsBrowserService;
    private readonly BackgroundJobService _backgroundJobService;
    private readonly bool _ownsBackgroundJobService;
    private readonly ChatSurfaceRegistry _chatSurfaceRegistry;
    private readonly ChatSessionStore _chatSessionStore;
    private readonly bool _ownsChatSessionStore;
    private readonly bool _ownsChatSurfaceRegistry;
    private readonly HashSet<Chat> _runningStateSubscriptions = [];
    private readonly GlobalSearchService _globalSearchService;
    private readonly CancellationTokenSource _searchIndexCts = new();
    private bool _isDisposed;
    private bool _isRefreshingCopilotState;
    private bool _isSyncingDefaultModelSelectionFromChat;
    private readonly ChatNavigationHistory _chatNavigationHistory = new();

    private const int ChatPageSize = 50;
    private int _chatLoadLimit = ChatPageSize;
    [ObservableProperty] private bool _hasMoreChats;

    [ObservableProperty] private int _selectedNavIndex;
    [ObservableProperty] private bool _isDarkTheme = true;
    [ObservableProperty] private bool _isCompactDensity;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _connectionStatus = Loc.Status_Disconnected;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private bool _isOnboarded;
    [ObservableProperty] private string _onboardingName = "";
    [ObservableProperty] private int _onboardingSexIndex; // 0=Male, 1=Female, 2=Prefer not to say
    [ObservableProperty] private int _onboardingLanguageIndex; // index into Loc.AvailableLanguages
    [ObservableProperty] private Guid? _selectedProjectFilter;
    [ObservableProperty] private bool _isSidebarCollapsed;
    [ObservableProperty] private bool _isAgentDebugMapDismissed;

    public bool IsGlobalUpdateBannerVisible => SettingsVM.ShouldShowUpdateBanner
        && (SelectedNavIndex != 7 || SettingsVM.SelectedPageIndex != SettingsViewModel.AboutPageIndex);

    public bool IsAgentDebugMapVisible
    {
        get
        {
#if DEBUG
            return !IsAgentDebugMapDismissed;
#else
            return false;
#endif
        }
    }

    partial void OnIsAgentDebugMapDismissedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAgentDebugMapVisible));
    }

    public string AgentDebugCurrentPage => DescribeNavPage(SelectedNavIndex);

    /// <summary>
    /// Single source of truth for the nav-index → page mapping. Consumed by the debug overlay and
    /// by the UI responsiveness harness so the two can never silently drift apart.
    /// </summary>
    internal static string DescribeNavPage(int index) => index switch
    {
        0 => "0 Chat (#PageChat, #Composer, #Transcript)",
        1 => "1 Jobs (#PageJobs)",
        2 => "2 Projects (#PageProjects)",
        3 => "3 Skills (#PageSkills)",
        4 => "4 Lumis (#PageAgents)",
        5 => "5 Memories (#PageMemories)",
        6 => "6 MCP Servers (#PageMcpServers)",
        7 => "7 Settings (#PageSettings)",
        _ => $"{index} Unknown"
    };

    public string AgentDebugMapText =>
        "Debug-only agent map\n" +
        "Nav: #NavChat=0, #NavJobs=1, #NavProjects=2, #NavSkills=3, #NavAgents=4, #NavMemories=5, #NavMcpServers=6, #NavSettings=7\n" +
        "Chat controls: #PageChat, #ChatShell, #Transcript, #Composer, #SearchInput\n" +
        "CLI: --skip-onboarding --debug-agent-harness opens fixture, --test-chat-stress checks tools, --test-mcp-native checks SDK MCP";

    partial void OnSelectedNavIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsGlobalUpdateBannerVisible));
        OnPropertyChanged(nameof(AgentDebugCurrentPage));
        if (value == 1)
        {
            JobsVM.SetPreferredChat(ChatVM.CurrentChat);
            JobsVM.RefreshFromStore();
        }
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    /// <summary>State-aware tooltip for the sidebar collapse/expand toggle.</summary>
    public string SidebarToggleTooltip =>
        IsSidebarCollapsed ? Loc.Sidebar_ExpandTooltip : Loc.Sidebar_CollapseTooltip;

    /// <summary>
    /// Whether the collapsed icon rail (quick nav shortcuts shown in place of the hidden sidebar)
    /// should be visible. Only when onboarded and collapsed.
    /// </summary>
    public bool ShowSidebarRail => IsOnboarded && IsSidebarCollapsed;

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(SidebarToggleTooltip));
        OnPropertyChanged(nameof(ShowSidebarRail));
        // Persistence is handled by the primary window's view (see MainWindow.PersistSidebarCollapsed)
        // so secondary windows don't clobber the saved layout preference.
    }

    partial void OnIsOnboardedChanged(bool value) => OnPropertyChanged(nameof(ShowSidebarRail));

    [ObservableProperty] private Guid? _activeChatId;

    // Sub-ViewModels
    private ChatViewModel _chatVM = null!;
    public ChatViewModel ChatVM
    {
        get => _chatVM;
        private set => SetProperty(ref _chatVM, value);
    }
    public BackgroundJobsViewModel JobsVM { get; }
    public SkillsViewModel SkillsVM { get; }
    public AgentsViewModel AgentsVM { get; }
    public ProjectsViewModel ProjectsVM { get; }
    public MemoriesViewModel MemoriesVM { get; }
    public McpServersViewModel McpServersVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public OnboardingViewModel OnboardingVM { get; }
    public SearchOverlayViewModel SearchOverlayVM { get; }
    public GitHubLoginViewModel LoginVM { get; }

    /// <summary>The browser service used for Settings cookie import/clear.</summary>
    public BrowserService SettingsBrowserService => _settingsBrowserService;

    /// <summary>The application data store.</summary>
    public DataStore DataStore => _dataStore;

    public BackgroundJobService BackgroundJobService => _backgroundJobService;
    public ChatSurfaceRegistry ChatSurfaceRegistry => _chatSurfaceRegistry;

    // Grouped chat list for sidebar
    public ObservableCollection<ChatGroup> ChatGroups { get; } = [];

    // Project list for filter
    public ObservableCollection<Project> Projects { get; } = [];

    public MainViewModel(
        DataStore dataStore,
        CopilotService copilotService,
        UpdateService updateService,
        bool forceOnboarding = false,
        BackgroundJobService? backgroundJobService = null,
        bool startBackgroundJobs = true,
        ChatSurfaceRegistry? chatSurfaceRegistry = null,
        ChatSessionStore? chatSessionStore = null,
        GlobalSearchService? globalSearchService = null
#if DEBUG
        , bool openAgentDebugHarness = false,
        bool skipOnboarding = false
#endif
        )
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        _settingsBrowserService = new BrowserService();
        _chatSurfaceRegistry = chatSurfaceRegistry ?? new ChatSurfaceRegistry();
        _ownsChatSurfaceRegistry = chatSurfaceRegistry is null;
        _globalSearchService = globalSearchService ?? new GlobalSearchService(
            () => _dataStore.Data,
            _dataStore.GetChatSearchSnapshot,
            releaseChatSnapshot: _dataStore.EvictChatSearchSnapshot,
            chatFileTimestampProvider: _dataStore.GetChatFileTimestamp);
        _chatSessionStore = chatSessionStore ?? new ChatSessionStore(dataStore, copilotService, _chatSurfaceRegistry, _globalSearchService);
        _ownsChatSessionStore = chatSessionStore is null;

        var settings = _dataStore.Data.Settings;
        _isDarkTheme = settings.IsDarkTheme;
        _isCompactDensity = settings.IsCompactDensity;
        _isSidebarCollapsed = settings.SidebarCollapsed;
        _userName = settings.UserName ?? "";
#if DEBUG
        _isOnboarded = !forceOnboarding && (settings.IsOnboarded || skipOnboarding);
#else
        _isOnboarded = settings.IsOnboarded && !forceOnboarding;
#endif

        // Shared GitHub login ViewModel
        LoginVM = new GitHubLoginViewModel(copilotService);

        // Onboarding ViewModel — available even if already onboarded (for --onboarding flag)
        OnboardingVM = new OnboardingViewModel(dataStore, copilotService);
        OnboardingVM.LoginVM = LoginVM;
        OnboardingVM.OnboardingCompleted += () =>
        {
            UserName = OnboardingVM.UserName;
            IsDarkTheme = OnboardingVM.IsDarkTheme;
            IsOnboarded = true;

            // Sync GitHub auth state if user signed in during onboarding
            if (LoginVM.IsAuthenticated)
                _ = SettingsVM?.RefreshAuthStatusAsync();

            // Refresh memories in case learning created some
            ChatVM?.RefreshComposerCatalogs();
        };
        OnboardingVM.ThemeChanged += isDark => IsDarkTheme = isDark;

        _chatVM = AcquireDraftChatSurface(SelectedProjectFilter);
        if (backgroundJobService is null)
        {
            _backgroundJobService = new BackgroundJobService(dataStore, _chatSurfaceRegistry, _chatSessionStore);
        }
        else
        {
            _backgroundJobService = backgroundJobService;
        }
        _ownsBackgroundJobService = backgroundJobService is null;
        JobsVM = new BackgroundJobsViewModel(dataStore, _backgroundJobService);
        SkillsVM = new SkillsViewModel(dataStore);
        AgentsVM = new AgentsViewModel(dataStore);
        ProjectsVM = new ProjectsViewModel(dataStore);
        MemoriesVM = new MemoriesViewModel(dataStore);
        McpServersVM = new McpServersViewModel(dataStore);
        SettingsVM = new SettingsViewModel(dataStore, copilotService, _settingsBrowserService, updateService);
        SettingsVM.LoginVM = LoginVM;
        SearchOverlayVM = new SearchOverlayViewModel(
            _globalSearchService,
            () => SelectedNavIndex);

        _dataStore.ChatContentChanged += OnDataStoreChatContentChanged;
        _dataStore.ChatsContentReset += OnDataStoreChatsContentReset;

        // Sync settings changes back to MainViewModel
        SettingsVM.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SettingsViewModel.IsUpdateAvailable)
                or nameof(SettingsViewModel.IsUpdateDownloading)
                or nameof(SettingsViewModel.IsUpdateReadyToRestart)
                or nameof(SettingsViewModel.ShouldShowUpdateBanner)
                or nameof(SettingsViewModel.SelectedPageIndex))
            {
                OnPropertyChanged(nameof(IsGlobalUpdateBannerVisible));
            }

            if (args.PropertyName == nameof(SettingsViewModel.IsDarkTheme))
                IsDarkTheme = SettingsVM.IsDarkTheme;
            else if (args.PropertyName == nameof(SettingsViewModel.IsCompactDensity))
                IsCompactDensity = SettingsVM.IsCompactDensity;
            else if ((args.PropertyName == nameof(SettingsViewModel.PreferredModel)
                      || args.PropertyName == nameof(SettingsViewModel.ReasoningEffort)
                      || args.PropertyName == nameof(SettingsViewModel.ContextWindowTier))
                     && !_isSyncingDefaultModelSelectionFromChat
                     && !string.IsNullOrWhiteSpace(SettingsVM.PreferredModel)
                     && (ChatVM.CurrentChat is null || ChatVM.CurrentChat.Messages.Count == 0))
                ChatVM.RestoreDefaultModelSelection();
            else if (args.PropertyName == nameof(SettingsViewModel.SendWithEnter))
                _chatSessionStore.ApplyToSurfaces(surface => surface.SendWithEnter = SettingsVM.SendWithEnter);
            else if (args.PropertyName is nameof(SettingsViewModel.ShowTimestamps)
                     or nameof(SettingsViewModel.ShowToolCalls)
                     or nameof(SettingsViewModel.ShowReasoning)
                     or nameof(SettingsViewModel.ExpandReasoningWhileStreaming))
                _chatSessionStore.ApplyToSurfaces(surface => surface.RebuildTranscript());
            else if (args.PropertyName == nameof(SettingsViewModel.IsAuthenticated))
            {
                if (SettingsVM.IsAuthenticated)
                    _ = RefreshCopilotStateAsync(refreshAuthStatus: false);
                else if (!_isRefreshingCopilotState && !IsConnecting)
                {
                    IsConnected = false;
                    ConnectionStatus = Loc.Status_Disconnected;
                }
            }
            else if (args.PropertyName == nameof(SettingsViewModel.UserName))
                UserName = SettingsVM.UserName;
        };

        SkillsVM.SkillsChanged += () =>
        {
            _chatSessionStore.ApplyToSurfaces(surface => surface.RefreshComposerCatalogs());
            RefreshFeatureManagementUi();
        };
        AgentsVM.AgentsChanged += () =>
        {
            _chatSessionStore.ApplyToSurfaces(surface => surface.RefreshComposerCatalogs());
            RefreshFeatureManagementUi();
        };

        JobsVM.JobsChanged += () =>
        {
            _backgroundJobService.Reschedule();
            RefreshFeatureManagementUi();
        };
        JobsVM.OpenChatRequested += jobChatId => _ = OpenChatByIdAsync(jobChatId);
        _backgroundJobService.JobsChanged += OnBackgroundJobServiceJobsChanged;

        SettingsVM.SettingsChanged += () =>
        {
            RefreshChatList();
        };

        AttachChatViewModel(ChatVM);

        // Feature-management changes can originate from any chat surface (the visible chat,
        // a chat still running after the user navigated away, a background-job chat, or a
        // detached window). Subscribe at the store level so the main window's collections
        // refresh no matter which surface executed the change.
        _chatSessionStore.SurfaceFeatureManagementStateChanged += OnChatFeatureManagementStateChanged;

        ProjectsVM.ProjectsChanged += () =>
        {
            _chatSessionStore.ApplyToSurfaces(surface =>
            {
                surface.InvalidateProjectSession();
                surface.RefreshComposerCatalogs();
            });
            RefreshFeatureManagementUi();
        };

        McpServersVM.McpConfigChanged += () =>
        {
            McpProxyRuntime.Shared.RetireUserRegistrationsExcept(_dataStore.Data.McpServers
                .Where(server => server.IsEnabled && !string.Equals(server.ServerType, "remote", StringComparison.OrdinalIgnoreCase))
                .Select(server => server.Id));
            _chatSessionStore.ApplyToSurfaces(surface =>
            {
                surface.InvalidateMcpSession();
                surface.PopulateDefaultMcps();
                surface.RefreshComposerCatalogs();
            });
            RefreshFeatureManagementUi();
        };
        LoadProjects();
        SubscribeChatRunningState();
        RefreshChatList();
        ChatVM.RefreshComposerCatalogs();
        if (_ownsBackgroundJobService && startBackgroundJobs)
            _backgroundJobService.Start();

#if DEBUG
        if (openAgentDebugHarness)
            OpenAgentDebugHarness();
#endif

        _chatNavigationHistory.Record(ChatVM.CurrentChat?.Id, SelectedProjectFilter);
        _ = InitializeAsync();
    }

    private void PrepareChatSurface(ChatViewModel surface)
    {
        surface.SendWithEnter = _dataStore.Data.Settings.SendWithEnter;
        surface.ActiveProjectFilterId = SelectedProjectFilter;
        if (_chatVM is not null)
            surface.CopyModelCatalogFrom(_chatVM);
    }

    private ChatViewModel AcquireDraftChatSurface(Guid? projectId)
        => _chatSessionStore.AcquireDraft(projectId, PrepareChatSurface);

    private Task<ChatViewModel> AcquireChatSurfaceAsync(Chat chat)
        => _chatSessionStore.AcquireChatAsync(chat, PrepareChatSurface);

    private void AttachChatViewModel(ChatViewModel chatVm)
    {
        chatVm.DefaultModelSelectionChanged += OnChatDefaultModelSelectionChanged;
        chatVm.ChatUpdated += OnChatUpdated;
        chatVm.ChatTitleChanged += OnChatTitleChanged;
        chatVm.PropertyChanged += OnChatViewModelPropertyChanged;
        chatVm.ComposerProjectFilterRequested += OnComposerProjectFilterRequested;
    }

    private void DetachChatViewModel(ChatViewModel chatVm)
    {
        chatVm.DefaultModelSelectionChanged -= OnChatDefaultModelSelectionChanged;
        chatVm.ChatUpdated -= OnChatUpdated;
        chatVm.ChatTitleChanged -= OnChatTitleChanged;
        chatVm.PropertyChanged -= OnChatViewModelPropertyChanged;
        chatVm.ComposerProjectFilterRequested -= OnComposerProjectFilterRequested;
    }

    private void ShowChatSurface(ChatViewModel surface)
    {
        var previous = ChatVM;
        if (ReferenceEquals(previous, surface))
        {
            _chatSessionStore.Release(surface);
            ActiveChatId = surface.CurrentChat?.Id;
            return;
        }

        DetachChatViewModel(previous);
        ChatVM = surface;
        AttachChatViewModel(surface);
        ActiveChatId = surface.CurrentChat?.Id;
        // Cached surfaces are reused without re-running LoadChatAsync, so re-establish the browser panel
        // here (after ActiveChatId is set) to keep the toggle button working after switching chats.
        surface.RestoreBrowserPanelForActiveChat();
        _chatSessionStore.Release(previous);
    }

    private void OnChatDefaultModelSelectionChanged(string model, string? reasoningEffort, string? contextWindowTier)
    {
        if (SettingsVM.PreferredModel == model
            && SettingsVM.ReasoningEffort == (reasoningEffort ?? string.Empty)
            && SettingsVM.ContextWindowTier == (contextWindowTier ?? SettingsVM.ContextWindowTier))
            return;

        _isSyncingDefaultModelSelectionFromChat = true;
        try
        {
            SettingsVM.SyncDefaultModelSelectionFromChat(model, reasoningEffort, contextWindowTier);
        }
        finally
        {
            _isSyncingDefaultModelSelectionFromChat = false;
        }
    }

    public void SyncDefaultModelSelectionFromChatSurface(string model, string? reasoningEffort, string? contextWindowTier)
        => OnChatDefaultModelSelectionChanged(model, reasoningEffort, contextWindowTier);

    private void OnChatUpdated()
    {
        SubscribeChatRunningState();
        RefreshChatList();
    }

    private void OnChatViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, ChatVM))
            return;

        if (args.PropertyName == nameof(ChatViewModel.CurrentChat))
            ActiveChatId = ChatVM.CurrentChat?.Id;
        else if (args.PropertyName == nameof(ChatViewModel.IsBusy))
            RefreshProjectRunningState();
    }

    private void OnChatFeatureManagementStateChanged()
    {
        _backgroundJobService.Reschedule();
        RefreshFeatureManagementUi();
    }

    private void OnComposerProjectFilterRequested(Guid? projectId)
    {
        if (projectId == SelectedProjectFilter)
            return;

        if (!projectId.HasValue)
        {
            ClearProjectFilterCommand.Execute(null);
            return;
        }

        var project = _dataStore.Data.Projects.FirstOrDefault(p => p.Id == projectId.Value);
        if (project is not null)
            SelectProjectFilterCommand.Execute(project);
    }

    private async Task InitializeAsync()
    {
        await RefreshCopilotStateAsync(refreshAuthStatus: true);
        _ = WarmSearchIndexAsync();
    }

    private void OnDataStoreChatContentChanged(Guid chatId)
        => _globalSearchService.InvalidateChatContent(chatId);

    private void OnDataStoreChatsContentReset()
        => _globalSearchService.PruneChatContent();

    /// <summary>
    /// Builds the full-coverage chat content index in the background so search can find any chat by
    /// its message content — not just the most recent few. The index is persisted between runs.
    /// </summary>
    private async Task WarmSearchIndexAsync()
    {
        try
        {
            await Task.Yield();
            var indexPath = DataStore.SearchContentIndexFile;
            var token = _searchIndexCts.Token;

            // Capture the chat list on the UI thread (List<Chat> is not thread-safe), then run all
            // disk I/O and indexing on a background thread so startup stays responsive.
            var chats = _dataStore.Data.Chats.ToArray();
            var liveChatIds = Array.ConvertAll(chats, static chat => chat.Id);

            await Task.Run(async () =>
            {
                try
                {
                    _globalSearchService.LoadChatContentIndex(indexPath);
                }
                catch
                {
                    // A missing or corrupt index just means we rebuild from scratch.
                }

                _globalSearchService.PruneChatContent(liveChatIds);
                await _globalSearchService.WarmChatContentAsync(chats, token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);

            try
            {
                _globalSearchService.SaveChatContentIndex(indexPath);
            }
            catch
            {
                // Persisting the index is best-effort; failure only costs a re-warm next launch.
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down — leave whatever was warmed so far.
        }
        catch
        {
            // Never let background indexing crash the app.
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _dataStore.ChatContentChanged -= OnDataStoreChatContentChanged;
        _dataStore.ChatsContentReset -= OnDataStoreChatsContentReset;
        _searchIndexCts.Cancel();
        try
        {
            _globalSearchService.SaveChatContentIndex(DataStore.SearchContentIndexFile);
        }
        catch
        {
            // Best-effort persistence on shutdown.
        }
        _searchIndexCts.Dispose();
        _backgroundJobService.JobsChanged -= OnBackgroundJobServiceJobsChanged;
        _chatSessionStore.SurfaceFeatureManagementStateChanged -= OnChatFeatureManagementStateChanged;
        if (_ownsBackgroundJobService)
            _backgroundJobService.Dispose();
        DetachChatViewModel(ChatVM);
        _chatSessionStore.Release(ChatVM);
        if (_ownsChatSessionStore)
            _chatSessionStore.Dispose();
        UnsubscribeChatRunningState();
        if (_ownsChatSurfaceRegistry)
            _chatSurfaceRegistry.Dispose();
        SettingsVM.Dispose();
        _ = _settingsBrowserService.DisposeAsync();
    }

    private async Task RefreshCopilotStateAsync(bool refreshAuthStatus)
    {
        if (_isRefreshingCopilotState)
            return;

        try
        {
            _isRefreshingCopilotState = true;
            IsConnecting = true;
            ConnectionStatus = Loc.Status_Connecting;

            if (!_copilotService.IsConnected)
                await _copilotService.ConnectAsync();

            if (refreshAuthStatus)
                await SettingsVM.RefreshAuthStatusAsync();

            var models = await _copilotService.GetModelsAsync();
            var contextWindowCatalog = await _copilotService.GetContextWindowCatalogAsync();
            var longContextModelIds = contextWindowCatalog.LongContextModelIds;
            var modelIds = models.Select(m => m.Id).ToList();

            IsConnected = true;
            ConnectionStatus = Loc.Status_Connected;

            // Auto-select best model on clean state (no user preference saved)
            var selected = ChatVM.SelectedModel;
            var isCleanState = string.IsNullOrWhiteSpace(selected)
                || !modelIds.Contains(selected);
            if (isCleanState)
                selected = ChatViewModel.PickBestModel(modelIds);

            _chatSessionStore.ApplyToSurfaces(surface =>
            {
                surface.AvailableModels.Clear();
                foreach (var id in modelIds)
                    surface.AvailableModels.Add(id);
                surface.UpdateModelCapabilities(models, longContextModelIds, contextWindowCatalog.Limits);
                surface.SelectedModel = selected;
            });
            SettingsVM.UpdateModelCapabilities(models, longContextModelIds);

            SettingsVM.UpdateAvailableModels(modelIds);
            if (isCleanState && selected is not null)
                SettingsVM.PreferredModel = selected;

            // Refresh account quota in background
            _ = ChatVM.RefreshQuotaAsync();

            // Refresh catalogs now that connection is established (discovers workspace/user Copilot agents)
            _chatSessionStore.ApplyToSurfaces(surface => surface.RefreshComposerCatalogs());
        }
        catch (Exception ex)
        {
            ConnectionStatus = string.Format(Loc.Status_ConnectionFailed, ex.Message);
            IsConnected = false;
        }
        finally
        {
            IsConnecting = false;
            _isRefreshingCopilotState = false;
        }
    }

    private void LoadProjects()
    {
        Projects.Clear();
        foreach (var p in _dataStore.Data.Projects.OrderBy(p => p.Name))
            Projects.Add(p);
    }

    private void RefreshFeatureManagementUi()
    {
        LoadProjects();
        JobsVM.RefreshFromStore();
        ProjectsVM.RefreshFromStore();
        SkillsVM.RefreshFromStore();
        AgentsVM.RefreshFromStore();
        MemoriesVM.RefreshFromStore();
        McpServersVM.RefreshFromStore();

        if (SelectedProjectFilter.HasValue
            && !_dataStore.Data.Projects.Any(project => project.Id == SelectedProjectFilter.Value))
            SelectedProjectFilter = null;

        RefreshChatList();
        RefreshProjectRunningState();
    }

    private void SubscribeChatRunningState()
    {
        var currentChats = _dataStore.Data.Chats.ToHashSet();
        foreach (var chat in _runningStateSubscriptions.Where(chat => !currentChats.Contains(chat)).ToList())
        {
            chat.PropertyChanged -= OnChatRunningChanged;
            _runningStateSubscriptions.Remove(chat);
        }

        foreach (var chat in _dataStore.Data.Chats)
        {
            if (_runningStateSubscriptions.Add(chat))
                chat.PropertyChanged += OnChatRunningChanged;
        }
    }

    private void UnsubscribeChatRunningState()
    {
        foreach (var chat in _runningStateSubscriptions)
            chat.PropertyChanged -= OnChatRunningChanged;

        _runningStateSubscriptions.Clear();
    }

    private void OnBackgroundJobServiceJobsChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isDisposed)
                RefreshFeatureManagementUi();
        });
    }

    private void OnChatRunningChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isDisposed)
            return;

        if (e.PropertyName == nameof(Chat.IsRunning))
            RefreshProjectRunningState();
    }

    /// <summary>Recalculates IsRunning for all projects based on current chat states.</summary>
    public void RefreshProjectRunningState()
    {
        var chats = _dataStore.Data.Chats;
        foreach (var project in Projects)
            project.IsRunning = chats.Any(c => c.ProjectId == project.Id && c.IsRunning);

        ProjectRunningStateChanged?.Invoke();
    }

    /// <summary>Fired when any project's IsRunning state may have changed.</summary>
    public event Action? ProjectRunningStateChanged;

    public event Action<Guid, string>? ChatTitleChanged;
    public event Action<DetachedChatWindowRequest>? OpenChatWindowRequested;
    public event Func<Chat, bool>? DetachedChatFocusRequested;
    public event Action<Guid?>? ChatSelectionSyncRequested;
    public event Action<Guid>? ChatDeleted;

    public void RefreshChatList()
    {
        _chatLoadLimit = ChatPageSize;
        RebuildChatGroups();
    }

    public void LoadMoreChats()
    {
        if (!HasMoreChats) return;
        _chatLoadLimit += ChatPageSize;
        RebuildChatGroups();
    }

    private void RebuildChatGroups()
    {
        var chats = _dataStore.Data.Chats.AsEnumerable();

        // Filter by project
        if (SelectedProjectFilter.HasValue)
            chats = chats.Where(c => c.ProjectId == SelectedProjectFilter.Value);

        var allOrdered = chats.OrderByDescending(c => c.UpdatedAt).ToList();
        HasMoreChats = allOrdered.Count > _chatLoadLimit;
        var ordered = allOrdered.Take(_chatLoadLimit).ToList();

        // Compute the project folder badge shown under each chat row. Badges only appear in the
        // "All projects" view (no active project filter). Setting these observable properties lets
        // the sidebar bind directly to each Chat, so the badge stays correct across virtualization,
        // container recycling, and group reshuffles (e.g. a chat bumped into "Today" on send).
        var showBadges = !SelectedProjectFilter.HasValue;
        foreach (var chat in ordered)
        {
            var name = showBadges ? GetProjectName(chat.ProjectId) : null;
            chat.ProjectBadgeText = name;
            chat.ShowProjectBadge = name is not null;
        }

        // Group by time period
        var now = DateTimeOffset.Now;
        var today = now.Date;
        var yesterday = today.AddDays(-1);
        var weekAgo = today.AddDays(-7);

        ChatGroups.Clear();

        var todayChats = ordered.Where(c => c.UpdatedAt.Date == today).ToList();
        var yesterdayChats = ordered.Where(c => c.UpdatedAt.Date == yesterday).ToList();
        var weekChats = ordered.Where(c => c.UpdatedAt.Date < yesterday && c.UpdatedAt.Date >= weekAgo).ToList();
        var olderChats = ordered.Where(c => c.UpdatedAt.Date < weekAgo).ToList();

        if (todayChats.Count > 0)
            ChatGroups.Add(new ChatGroup { Label = Loc.ChatGroup_Today, Chats = new(todayChats) });
        if (yesterdayChats.Count > 0)
            ChatGroups.Add(new ChatGroup { Label = Loc.ChatGroup_Yesterday, Chats = new(yesterdayChats) });
        if (weekChats.Count > 0)
            ChatGroups.Add(new ChatGroup { Label = Loc.ChatGroup_Previous7Days, Chats = new(weekChats) });
        if (olderChats.Count > 0)
            ChatGroups.Add(new ChatGroup { Label = Loc.ChatGroup_Older, Chats = new(olderChats) });
    }

    private void OnChatTitleChanged(Guid chatId, string newTitle)
    {
        // Update in-place without rebuilding the entire list
        ChatTitleChanged?.Invoke(chatId, newTitle);
    }

    private void SetDraftChatProjectContext(Guid? projectId)
    {
        ChatSessionStore.SetDraftProjectContext(ChatVM, projectId);
    }

    private async Task<bool> LoadChatAndShowAsync(Chat chat)
    {
        if (TryFocusDetachedChat(chat))
            return true;

        var visibleSurface = ChatVM;
        var shouldBridgeLoading = visibleSurface.CurrentChat?.Id != chat.Id;
        if (shouldBridgeLoading)
            visibleSurface.IsLoadingChat = true;

        try
        {
            var surface = await AcquireChatSurfaceAsync(chat);
            if (shouldBridgeLoading && !ReferenceEquals(visibleSurface, surface))
                visibleSurface.IsLoadingChat = false;

            ShowChatSurface(surface);
            if (ChatVM.CurrentChat?.Id != chat.Id)
                return false;
        }
        catch
        {
            if (shouldBridgeLoading)
                visibleSurface.IsLoadingChat = false;
            throw;
        }

        SelectedNavIndex = 0;
        return true;
    }

    private bool TryFocusDetachedChat(Chat chat)
    {
        var handlers = DetachedChatFocusRequested;
        if (handlers is null)
            return false;

        foreach (Func<Chat, bool> handler in handlers.GetInvocationList())
        {
            if (handler(chat))
            {
                SelectedNavIndex = 0;
                ChatSelectionSyncRequested?.Invoke(ActiveChatId);
                return true;
            }
        }

        return false;
    }

    private void ClearMainChatSurface()
    {
        ShowChatSurface(AcquireDraftChatSurface(SelectedProjectFilter));
    }

    public async Task<bool> OpenChatByIdAsync(Guid chatId)
    {
        var chat = _dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == chatId);
        if (chat is null)
            return false;

        try
        {
            return await LoadChatAndShowAsync(chat);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<bool> ApplyChatNavigationEntryAsync(ChatNavigationState entry)
    {
        if (SelectedProjectFilter != entry.ProjectFilterId)
            SelectedProjectFilter = entry.ProjectFilterId;
        else
            ChatVM.ActiveProjectFilterId = entry.ProjectFilterId;

        if (entry.ChatId is not Guid chatId)
        {
            ClearMainChatSurface();
            SetDraftChatProjectContext(entry.ProjectFilterId);
            SelectedNavIndex = 0;
            return true;
        }

        var chat = _dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == chatId);
        if (chat is null)
            return false;

        return await LoadChatAndShowAsync(chat);
    }

    public async Task<bool> TryNavigateChatHistoryAsync(int direction)
    {
        try
        {
            return await _chatNavigationHistory.TryNavigateAsync(
                direction,
                _dataStore.Data.Chats.Select(chat => chat.Id),
                ApplyChatNavigationEntryAsync);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [RelayCommand]
    private void NewChat()
    {
        // If the current chat is empty (no messages), just reuse it
        if (ChatVM.CurrentChat is not null
            && ChatVM.CurrentChat.Messages.Count == 0
            && !ChatVM.OwnsAnyLiveChat())
        {
            // Still update the project assignment if a filter is active
            SetDraftChatProjectContext(SelectedProjectFilter);
            SelectedNavIndex = 0;
            return;
        }

        ClearMainChatSurface();

        // Auto-assign the active project filter to new chats
        SetDraftChatProjectContext(SelectedProjectFilter);
        SelectedNavIndex = 0;
    }

    [RelayCommand]
    private void OpenNewWindow()
    {
        if (Avalonia.Application.Current is App app)
            app.OpenNewWindow();
    }

    [RelayCommand]
    private void OpenAgentDebugHarness()
    {
#if DEBUG
        ChatVM.LoadDebugTranscriptFixture();
        SelectedNavIndex = 0;
#endif
    }

    [RelayCommand]
    private void OpenBackgroundShellHarness()
    {
#if DEBUG
        ChatVM.LoadDebugBackgroundShellFixture();
        SelectedNavIndex = 0;
#endif
    }

    [RelayCommand]
    private void DismissAgentDebugMap()
    {
        IsAgentDebugMapDismissed = true;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenChat(Chat chat)
    {
        try
        {
            await LoadChatAndShowAsync(chat);
        }
        catch (OperationCanceledException)
        {
            // A newer chat selection superseded this open request.
        }
    }

    [RelayCommand]
    private void DeleteChat(Chat chat)
    {
        // If the chat has a worktree, ask the user whether to clean it up
        if (chat.WorktreePath is { Length: > 0 } wt && Directory.Exists(wt))
        {
            _pendingDeleteChat = chat;
            IsWorktreeDeleteDialogOpen = true;
            return;
        }

        PerformDeleteChat(chat, removeWorktree: false);
    }

    // ── Worktree cleanup dialog ──

    private Chat? _pendingDeleteChat;
    [ObservableProperty] private bool _isWorktreeDeleteDialogOpen;

    [RelayCommand]
    private async Task ConfirmDeleteWithWorktree()
    {
        if (_pendingDeleteChat is not null)
        {
            var chat = _pendingDeleteChat;
            _pendingDeleteChat = null;
            IsWorktreeDeleteDialogOpen = false;
            PerformDeleteChat(chat, removeWorktree: true);

            // Clean up worktree + branch in background
            if (chat.WorktreePath is { Length: > 0 } wt)
            {
                var projectDir = GetProjectDirForChat(chat);
                if (projectDir is not null)
                    await GitService.RemoveWorktreeAsync(projectDir, wt);
            }
        }
    }

    [RelayCommand]
    private void ConfirmDeleteWithoutWorktree()
    {
        if (_pendingDeleteChat is not null)
        {
            var chat = _pendingDeleteChat;
            _pendingDeleteChat = null;
            IsWorktreeDeleteDialogOpen = false;
            PerformDeleteChat(chat, removeWorktree: false);
        }
    }

    [RelayCommand]
    private void CancelDeleteWorktreeDialog()
    {
        _pendingDeleteChat = null;
        IsWorktreeDeleteDialogOpen = false;
    }

    private void PerformDeleteChat(Chat chat, bool removeWorktree)
    {
        var deletedActiveChat = ChatVM.CurrentChat?.Id == chat.Id;

        if (deletedActiveChat)
            ClearMainChatSurface();

        _chatSessionStore.CleanupChat(chat.Id);
        _dataStore.Data.Chats.Remove(chat);
        _dataStore.RemoveBackgroundJobsForChat(chat.Id);
        _backgroundJobService.Reschedule();
        _chatNavigationHistory.RemoveChat(chat.Id);
        _dataStore.MarkChatDeleted(chat.Id);
        _dataStore.DeleteChatFile(chat.Id);
        _ = _dataStore.SaveAsync();
        RefreshChatList();
        ChatDeleted?.Invoke(chat.Id);

    }

    private string? GetProjectDirForChat(Chat chat)
    {
        if (chat.ProjectId.HasValue)
        {
            var project = _dataStore.Data.Projects.FirstOrDefault(p => p.Id == chat.ProjectId.Value);
            if (project?.WorkingDirectory is { Length: > 0 } dir)
                return dir;
        }
        return null;
    }

    [ObservableProperty] private Chat? _renamingChat;
    [ObservableProperty] private string _renamingTitle = "";

    [RelayCommand]
    private void StartRenameChat(Chat? chat)
    {
        if (chat is null) return;
        RenamingChat = chat;
        RenamingTitle = chat.Title;
    }

    [RelayCommand]
    private void CommitRenameChat()
    {
        if (RenamingChat is null) return;
        var newTitle = RenamingTitle?.Trim();
        if (!string.IsNullOrEmpty(newTitle))
        {
            RenamingChat.Title = newTitle;
            _dataStore.MarkChatChanged(RenamingChat);
            _ = _dataStore.SaveAsync();
            RefreshChatList();
        }
        RenamingChat = null;
        RenamingTitle = "";
    }

    [RelayCommand]
    private void CancelRenameChat()
    {
        RenamingChat = null;
        RenamingTitle = "";
    }

    [RelayCommand]
    private void SetNav(string indexStr)
    {
        if (int.TryParse(indexStr, out var idx))
        {
            if (idx == 7 && SettingsVM.ShouldAutoNavigateToUpdateCenter)
                SettingsVM.OpenUpdateCenter();

            SelectedNavIndex = idx;
        }
    }

    [RelayCommand]
    private void OpenUpdateCenter()
    {
        SettingsVM.OpenUpdateCenter();
        SelectedNavIndex = 7;
    }

    [RelayCommand]
    private void ClearProjectFilter()
    {
        SelectedProjectFilter = null;
        ChatVM.ActiveProjectFilterId = null;

        // Also clear draft/new-chat project context immediately, even if
        // SelectedProjectFilter was already null (no PropertyChanged event).
        if (ChatVM.CurrentChat is null || ChatVM.CurrentChat.Messages.Count == 0)
            SetDraftChatProjectContext(null);
    }

    [RelayCommand]
    private void SelectProjectFilter(Project project)
    {
        SelectedProjectFilter = project.Id;
        ChatVM.ActiveProjectFilterId = project.Id;
    }

    [RelayCommand]
    private void AssignChatToProject(object? parameter)
    {
        // parameter is a two-element array: [Chat, Project]
        if (parameter is object[] args && args.Length == 2 && args[0] is Chat chat && args[1] is Project project)
        {
            if (chat.ProjectId == project.Id)
                return;

            chat.ProjectId = project.Id;
            _dataStore.MarkChatChanged(chat);
            // Refresh the live surface (which may null CopilotSessionId) BEFORE kicking off the save,
            // so the persisted index snapshot captures a consistent {ProjectId, CopilotSessionId} pair.
            NotifyProjectChangedForOpenSurfaces(chat);
            _ = _dataStore.SaveAsync();
            RefreshChatList();
        }
    }

    [RelayCommand]
    private void RemoveChatFromProject(Chat? chat)
    {
        if (chat is null || chat.ProjectId is null) return;
        chat.ProjectId = null;
        _dataStore.MarkChatChanged(chat);
        NotifyProjectChangedForOpenSurfaces(chat);
        _ = _dataStore.SaveAsync();
        RefreshChatList();
    }

    /// <summary>
    /// When a chat is moved between projects from the sidebar, any chat surface currently showing
    /// that chat must resync its live project context (composer chip, system prompt/session,
    /// working directory) — otherwise the open chat keeps the stale project until it's reopened.
    /// </summary>
    private void NotifyProjectChangedForOpenSurfaces(Chat chat)
    {
        _chatSessionStore.ApplyToSurfaces(surface =>
        {
            if (surface.CurrentChat is { } current && current.Id == chat.Id)
                surface.OnCurrentChatProjectChangedExternally();
        });
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenChatFromProject(Chat chat)
    {
        try
        {
            await LoadChatAndShowAsync(chat);
        }
        catch (OperationCanceledException)
        {
            // A newer chat selection superseded this open request.
        }
    }

    [RelayCommand]
    private async Task OpenChatInNewWindow(Chat? chat)
    {
        var targetChat = chat ?? ChatVM.CurrentChat;
        var request = targetChat is null
            ? CreateDetachedWindowRequest(AcquireDraftChatSurface(SelectedProjectFilter), null)
            : await CreateDetachedChatWindowRequestAsync(targetChat);

        if (request is not null)
            RaiseOpenChatWindowRequest(request);

        SelectedNavIndex = 0;
    }

    private async Task<DetachedChatWindowRequest?> CreateDetachedChatWindowRequestAsync(Chat targetChat)
    {
        if (TryFocusDetachedChat(targetChat))
        {
            if (ChatVM.CurrentChat?.Id == targetChat.Id)
                ClearMainChatSurface();

            return null;
        }

        ChatViewModel surface;
        if (ChatVM.CurrentChat?.Id == targetChat.Id)
        {
            surface = ChatVM;
            _chatSessionStore.Retain(surface);
            ClearMainChatSurface();
        }
        else
        {
            surface = await AcquireChatSurfaceAsync(targetChat);
        }

        return CreateDetachedWindowRequest(surface, targetChat);
    }

    private DetachedChatWindowRequest CreateDetachedWindowRequest(ChatViewModel surface, Chat? chat)
    {
        return new DetachedChatWindowRequest(
            chat,
            new ChatWindowViewModel(surface),
            () => _chatSessionStore.Release(surface));
    }

    private void RaiseOpenChatWindowRequest(DetachedChatWindowRequest request)
    {
        var handlers = OpenChatWindowRequested;
        if (handlers is not null)
        {
            handlers.Invoke(request);
            return;
        }

        request.WindowVM.Dispose();
        request.ReleaseSurface();
    }

    [RelayCommand]
    private void OpenNewChatInNewWindow()
    {
        RaiseOpenChatWindowRequest(
            CreateDetachedWindowRequest(AcquireDraftChatSurface(SelectedProjectFilter), null));
        SelectedNavIndex = 0;
    }

    /// <summary>Returns the project name for a given project ID, or null.</summary>
    public string? GetProjectName(Guid? projectId)
    {
        if (!projectId.HasValue) return null;
        return _dataStore.Data.Projects.FirstOrDefault(p => p.Id == projectId.Value)?.Name;
    }

    public int GetProjectChatCount(Guid projectId)
    {
        return _dataStore.Data.Chats.Count(chat => chat.ProjectId == projectId);
    }

    public DateTimeOffset? GetProjectLastActivity(Guid projectId)
    {
        return _dataStore.Data.Chats
            .Where(chat => chat.ProjectId == projectId)
            .OrderByDescending(chat => chat.UpdatedAt)
            .Select(chat => (DateTimeOffset?)chat.UpdatedAt)
            .FirstOrDefault();
    }

    public void RefreshProjects()
    {
        LoadProjects();
    }

    [RelayCommand]
    private async Task CompleteOnboarding()
    {
        if (string.IsNullOrWhiteSpace(OnboardingName)) return;

        var settings = _dataStore.Data.Settings;
        settings.UserName = OnboardingName.Trim();
        settings.UserSex = OnboardingSexIndex switch
        {
            0 => "male",
            1 => "female",
            _ => null
        };

        // Apply selected language
        var selectedLang = "en";
        if (OnboardingLanguageIndex >= 0 && OnboardingLanguageIndex < Loc.AvailableLanguages.Length)
        {
            selectedLang = Loc.AvailableLanguages[OnboardingLanguageIndex].Code;
            settings.Language = selectedLang;
        }

        settings.IsOnboarded = true;
        await _dataStore.SaveAsync();

        UserName = OnboardingName.Trim();
        IsOnboarded = true;

        // If a non-default language was selected, restart so the UI loads in that language
        if (selectedLang != "en")
        {
            SettingsVM.RestartAppCommand.Execute(null);
        }
    }

    partial void OnSelectedProjectFilterChanged(Guid? value)
    {
        RefreshChatList();
        ChatVM.ActiveProjectFilterId = value;

        if (_chatNavigationHistory.IsRestoring)
        {
            if (ChatVM.CurrentChat is null || ChatVM.CurrentChat.Messages.Count == 0)
                SetDraftChatProjectContext(value);
            return;
        }

        // If the current chat already belongs to the target project, keep it.
        if (ChatVM.CurrentChat is not null
            && ChatVM.CurrentChat.Messages.Count > 0
            && ChatVM.CurrentChat.ProjectId == value)
            return;

        // If we're in a new/empty chat (draft), stay in new-chat mode —
        // just update the project assignment without navigating away.
        if (ChatVM.CurrentChat is null || ChatVM.CurrentChat.Messages.Count == 0)
        {
            SetDraftChatProjectContext(value);
            _chatNavigationHistory.Record(ChatVM.CurrentChat?.Id, value);
            return;
        }

        // Try to open the most recent chat in the new project.
        if (value.HasValue)
        {
            var recent = _dataStore.Data.Chats
                .Where(c => c.ProjectId == value.Value && c.Messages.Count > 0)
                .OrderByDescending(c => c.UpdatedAt)
                .FirstOrDefault();
            if (recent is not null)
            {
                _ = OpenChat(recent);
                return;
            }
        }

        // No existing chat for this project (or clearing filter) — start a new chat.
        NewChat();
    }

    partial void OnActiveChatIdChanged(Guid? value)
    {
        if (_chatNavigationHistory.IsRestoring)
            return;

        _chatNavigationHistory.Record(value, SelectedProjectFilter);
    }

    partial void OnIsDarkThemeChanged(bool value)
    {
        _dataStore.Data.Settings.IsDarkTheme = value;
        _ = _dataStore.SaveAsync();
    }

    partial void OnIsCompactDensityChanged(bool value)
    {
        _dataStore.Data.Settings.IsCompactDensity = value;
        _ = _dataStore.SaveAsync();
    }
}
