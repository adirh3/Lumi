using System;
using System.Collections.Concurrent;
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
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Lumi.Services.Discover;
using Lumi.Services.Sharing;

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
    /// <summary>
    /// Display-name cache for BYOK picker tokens, populated by <see cref="InjectByokModels"/>.
    /// Read by <see cref="ChatViewModel.FormatModelDisplay"/> which has no access to <c>UserSettings</c>.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> _byokDisplayCache = new(StringComparer.Ordinal);

    /// <summary>Snapshot of the display cache for read-only consumers (e.g. ChatViewModel).</summary>
    internal static IReadOnlyDictionary<string, string> ByokDisplayCache => _byokDisplayCache;

    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;
    /// <summary>A dedicated BrowserService for Settings cookie import/clear (not tied to any chat).</summary>
    private readonly BrowserService _settingsBrowserService;
    /// <summary>OS credential store for BYOK API keys (CredentialStore mode).</summary>
    private readonly Lumi.Services.Byok.ISecureKeyStore _secureKeyStore;
    private readonly BackgroundJobService _backgroundJobService;
    private readonly bool _ownsBackgroundJobService;
    private readonly ChatSurfaceRegistry _chatSurfaceRegistry;
    private readonly ChatSessionStore _chatSessionStore;
    private readonly bool _ownsChatSessionStore;
    private readonly ChatOrchestrationService _chatOrchestrationService;
    private readonly bool _ownsChatSurfaceRegistry;
    private readonly HashSet<Chat> _runningStateSubscriptions = [];
    private readonly GlobalSearchService _globalSearchService;
    private readonly CancellationTokenSource _searchIndexCts = new();
    private bool _isDisposed;
    private bool _isRefreshingCopilotState;
    private bool _wasExternalOnly;
    private bool _isSyncingDefaultModelSelectionFromChat;

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
        4 => "4 Agents (#PageAgents)",
        5 => "5 Memories (#PageMemories)",
        6 => "6 MCP Servers (#PageMcpServers)",
        7 => "7 Settings (#PageSettings)",
        8 => "8 Library (#PageLibrary)",
        9 => "9 Discover (#PageDiscover)",
        _ => $"{index} Unknown"
    };

    public string AgentDebugMapText =>
        "Debug-only agent map\n" +
        "Nav: #NavChat=0, #NavLibrary=8, #NavJobs=1, #NavCustomize (last section), #NavSettings=7 (or Memories=5)\n" +
        "Customize: #CustomizeDiscoverTab=9, #CustomizeLumisTab=4, #CustomizeSkillsTab=3, #CustomizeMcpTab=6, #CustomizeProjectsTab=2\n" +
        "Settings: #SettingsMemoriesItem=5 · Chat list project switcher: #ProjectSwitchButton, #ProjectSwitcherManageButton, #ProjectEditButton\n" +
        "Chat controls: #PageChat, #ChatShell, #Transcript, #Composer, #SearchInput\n" +
        "CLI: --skip-onboarding --debug-agent-harness opens fixture, --test-chat-stress checks tools, --test-mcp-native checks SDK MCP";

    partial void OnSelectedNavIndexChanged(int value)
    {
        // Settings reopens on the page used last; when that is Memories, its own page opens instead.
        if (value == 7 && SettingsVM is not null && SettingsTarget == MemoriesNavIndex)
        {
            SelectedNavIndex = MemoriesNavIndex;
            return;
        }

        if (value == MemoriesNavIndex && SettingsVM is not null)
        {
            // Memories shows no Settings search results, so opening it ends the search.
            if (SettingsVM.SelectedPageIndex != SettingsViewModel.MemoriesPageIndex)
                SettingsVM.SelectedPageIndex = SettingsViewModel.MemoriesPageIndex;
            SettingsVM.SearchQuery = "";
        }

        OnPropertyChanged(nameof(IsGlobalUpdateBannerVisible));
        OnPropertyChanged(nameof(AgentDebugCurrentPage));
        NotifyCustomizeState();
        if (IsCustomizeIndex(value))
        {
            LastCustomizeNavIndex = value;
            _ = RefreshExternalCapabilitiesAsync(force: false);
            if (value == DiscoverNavIndex)
                _ = DiscoverVM.EnsureLoadedAsync();
        }

        if (value == 1)
        {
            JobsVM.SetPreferredChat(ChatVM.CurrentChat);
            JobsVM.RefreshFromStore();
        }
        else if (value == LibraryNavIndex)
        {
            _ = LibraryVM.EnsureLoadedAsync();
        }
        else if (value == 7 && SettingsVM is not null
                 && SettingsVM.SelectedPageIndex == SettingsViewModel.AiModelsPageIndex)
        {
            _ = SettingsVM.RefreshAuthStatusAsync();
        }

        QueueNavigationRecord();
    }

    /// <summary>Nav index of the Library page, the second place in the nav pill.</summary>
    public const int LibraryNavIndex = 8;

    /// <summary>Nav index of Customize → Discover. The other Customize sections keep their page indices.</summary>
    public const int DiscoverNavIndex = 9;

    /// <summary>
    /// Nav index of Settings → Memories. Memories is about the user rather than what Lumi can do, so it
    /// is a Settings page: it keeps the Settings sidebar and lights Settings in the pill.
    /// </summary>
    public const int MemoriesNavIndex = 5;

    /// <summary>
    /// Customize groups the pages that set up how Lumi works: agents, skills, connectors and projects,
    /// plus Discover. Projects are switched from the chat list and set up here, the way agents, skills
    /// and connectors are picked in a chat and set up here. The indices predate the group, so global
    /// search, the debug bridge and saved shortcuts keep addressing the same pages.
    /// </summary>
    public static bool IsCustomizeIndex(int index) => index is 2 or 3 or 4 or 6 or DiscoverNavIndex;

    /// <summary>The Customize section the nav button returns to.</summary>
    public int LastCustomizeNavIndex { get; set; } = 4;

    public bool IsCustomizeOpen => IsCustomizeIndex(SelectedNavIndex);
    public bool IsDiscoverSection => SelectedNavIndex == DiscoverNavIndex;
    public bool IsLumisSection => SelectedNavIndex == 4;
    public bool IsSkillsSection => SelectedNavIndex == 3;
    public bool IsMcpSection => SelectedNavIndex == 6;
    public bool IsProjectsSection => SelectedNavIndex == 2;

    /// <summary>What the Customize area lists as available everywhere: the same scope as a chat with no project.</summary>
    private static readonly CapabilityQuery CustomizeCapabilityQuery =
        new([Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)]);

    [RelayCommand]
    private void OpenCustomize() => SelectedNavIndex = CustomizeTarget;

    /// <summary>The Customize section the Customize place opens: the one used last.</summary>
    internal int CustomizeTarget => IsCustomizeIndex(LastCustomizeNavIndex) ? LastCustomizeNavIndex : 4;

    /// <summary>
    /// The page the Settings place opens: the Settings page used last, which may be Memories. A search
    /// typed in the Settings list shows its results on the Settings page, even from Memories.
    /// </summary>
    internal int SettingsTarget =>
        SettingsVM.SelectedPageIndex == SettingsViewModel.MemoriesPageIndex && !SettingsVM.IsSearching
            ? MemoriesNavIndex
            : 7;

    /// <summary>
    /// A Customize section row. Clicking the section already open takes it back to its overview, the
    /// way the "All …" row used to.
    /// </summary>
    [RelayCommand]
    private void OpenCustomizeSection(string indexText)
    {
        if (!int.TryParse(indexText, out var index) || !IsCustomizeIndex(index))
            return;

        if (SelectedNavIndex != index)
        {
            SelectedNavIndex = index;
            return;
        }

        switch (index)
        {
            case 2: ProjectsVM.CloseDetailCommand.Execute(null); break;
            case 3: SkillsVM.CloseDetailCommand.Execute(null); break;
            case 4: AgentsVM.CloseDetailCommand.Execute(null); break;
            case 6: McpServersVM.CloseDetailCommand.Execute(null); break;
            case DiscoverNavIndex: DiscoverVM.CloseDetailCommand.Execute(null); break;
        }
    }

    /// <summary>Settings reopens on the page used last, which may be Memories.</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        if (SettingsVM.ShouldAutoNavigateToUpdateCenter)
            SettingsVM.OpenUpdateCenter();

        SelectedNavIndex = SettingsTarget;
    }

    /// <summary>
    /// Keeps the Settings list and the open page in step: picking Memories in the list opens its page,
    /// picking another Settings page while Memories is open returns to Settings, and a search typed in
    /// the list shows its results on the Settings page.
    /// </summary>
    private void SyncSettingsPageWithNav()
    {
        if (SelectedNavIndex is 7 or MemoriesNavIndex)
            SelectedNavIndex = SettingsTarget;
    }

    private void NotifyCustomizeState()
    {
        OnPropertyChanged(nameof(IsCustomizeOpen));
        OnPropertyChanged(nameof(IsDiscoverSection));
        OnPropertyChanged(nameof(IsLumisSection));
        OnPropertyChanged(nameof(IsSkillsSection));
        OnPropertyChanged(nameof(IsMcpSection));
        OnPropertyChanged(nameof(IsProjectsSection));
    }

    /// <summary>
    /// Loads what plugins and the user's Copilot folder provide and hands it to the Skills, Lumis
    /// and MCP sections. Uses the chats' own capability catalog, so the pages list exactly what a
    /// new chat can use.
    /// </summary>
    private async Task RefreshExternalCapabilitiesAsync(bool force)
    {
        if (_isDisposed)
            return;

        try
        {
            await _chatSessionStore.CapabilityCatalog.LoadAsync(CustomizeCapabilityQuery, forceRefresh: force);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"[Customize] Capability refresh failed: {ex.Message}");
        }

        if (Dispatcher.UIThread.CheckAccess())
            ApplyExternalCapabilities();
        else
            Dispatcher.UIThread.Post(ApplyExternalCapabilities);
    }

    private CapabilitySnapshot? GetCustomizeCapabilities()
        => _isDisposed ? null : _chatSessionStore.CapabilityCatalog.GetSnapshot(CustomizeCapabilityQuery);

    private void ApplyExternalCapabilities()
    {
        if (GetCustomizeCapabilities() is not { } snapshot)
            return;

        SkillsVM.External.Update(snapshot);
        AgentsVM.External.Update(snapshot);
        McpServersVM.External.Update(snapshot);
        DiscoverVM.NotifyCapabilitiesChanged();
    }

    private void OnPluginsChanged()
        => Dispatcher.UIThread.Post(() =>
        {
            _ = RefreshExternalCapabilitiesAsync(force: true);
            _ = SetupsVM.RefreshAsync();
        });

    partial void OnIsConnectedChanged(bool value) => DiscoverVM?.NotifyConnectionChanged(value);

    /// <summary>
    /// A connection starts a new capability generation. Customize reloads only after that reset, or
    /// the reset would discard its result and leave the sections without plugin and Copilot items.
    /// </summary>
    private void RefreshCapabilitiesAfterConnection()
    {
        _chatSessionStore.RefreshCapabilitiesAfterConnectionChange();
        if (IsCustomizeOpen)
            _ = RefreshExternalCapabilitiesAsync(force: false);
    }

    /// <summary>Opens a Customize section with a plugin's parts unfolded in its sidebar.</summary>
    private void ShowPluginParts(string pluginName, CapabilityKind kind)
    {
        var (index, external) = kind switch
        {
            CapabilityKind.Agent => (4, AgentsVM.External),
            CapabilityKind.McpServer => (6, McpServersVM.External),
            _ => (3, SkillsVM.External),
        };

        SelectedNavIndex = index;
        external.Reveal(pluginName);
    }

    private void ManageInDiscover(string pluginName)
    {
        SelectedNavIndex = DiscoverNavIndex;
        _ = DiscoverVM.OpenInstalledPluginAsync(pluginName);
    }

    /// <summary>A setup's "Try it": a new chat with the prompt ready to review. Nothing is sent.</summary>
    private void StartSetupDraft(string prompt)
    {
        NewChat();
        ChatVM.PromptText = prompt;
    }

    /// <summary>A setup that is there but turned off: its plugin in Discover, or the Connectors section.</summary>
    private void ManageSetup(SetupSuggestionItem setup)
    {
        if (setup.Installation is { } plugin)
            ManageInDiscover(plugin.Name);
        else
            SelectedNavIndex = 6;
    }

    /// <summary>Nav index of the Jobs (automations) page.</summary>
    private const int JobsNavIndex = 1;

    [RelayCommand]
    private void OpenLibrary() => SelectedNavIndex = LibraryNavIndex;

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    /// <summary>State-aware tooltip for the sidebar collapse/expand toggle.</summary>
    public string SidebarToggleTooltip =>
        Loc.AdaptKeyboardHint(IsSidebarCollapsed ? Loc.Sidebar_ExpandTooltip : Loc.Sidebar_CollapseTooltip);

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

    partial void OnIsOnboardedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSidebarRail));
        SetupsVM.IsWelcome = !value;
    }

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

    /// <summary>Customize → Discover: plugins and MCP servers to add.</summary>
    public DiscoverViewModel DiscoverVM { get; }

    /// <summary>Well-known setups (such as Microsoft 365) and the one suggested now.</summary>
    public SetupSuggestionsViewModel SetupsVM { get; }

    /// <summary>The share sheet for prompts, skills, Lumis and MCP servers (one per window).</summary>
    public ShareSheetViewModel ShareVM { get; }

    /// <summary>The import sheet: SKILL.md, Lumi packs and MCP configs, with a receipt before anything is added.</summary>
    public ImportSheetViewModel ImportVM { get; }

    /// <summary>"Copied for chat" confirmations and the offer to preview a Lumi code found on the clipboard.</summary>
    public CapabilityNoticeViewModel NoticeVM { get; }

    public ChatTagsViewModel ChatTagsVM { get; }
    public LibraryViewModel LibraryVM { get; }
    public SettingsViewModel SettingsVM { get; }

    /// <summary>The welcome. Each replay from Settings swaps in a fresh one.</summary>
    public OnboardingViewModel OnboardingVM
    {
        get => _onboardingVM;
        private set => SetProperty(ref _onboardingVM, value);
    }

    private OnboardingViewModel _onboardingVM;

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
        GlobalSearchService? globalSearchService = null,
        ProjectGitSyncService? projectGitSyncService = null,
        Lumi.Services.Byok.ISecureKeyStore? secureKeyStore = null
#if DEBUG
        , bool openAgentDebugHarness = false,
        bool skipOnboarding = false
#endif
        , bool initializeCopilotOnStartup = false
        )
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        _secureKeyStore = secureKeyStore ?? Lumi.Services.Byok.SecureKeyStoreFactory.Instance;
        _settingsBrowserService = new BrowserService();
        _chatSurfaceRegistry = chatSurfaceRegistry ?? new ChatSurfaceRegistry();
        _ownsChatSurfaceRegistry = chatSurfaceRegistry is null;
        _globalSearchService = globalSearchService ?? new GlobalSearchService(
            () => _dataStore.Data,
            _dataStore.GetChatSearchSnapshot,
            releaseChatSnapshot: _dataStore.EvictChatSearchSnapshot,
            chatFileTimestampProvider: _dataStore.GetChatFileTimestamp);
        _chatSessionStore = chatSessionStore ?? new ChatSessionStore(dataStore, copilotService, _chatSurfaceRegistry, _globalSearchService, _secureKeyStore);
        _ownsChatSessionStore = chatSessionStore is null;

        // Backs the manage_chats tool ("Lumi as a manager"): create/list/status/send/edit across chats.
        // The backend is owned by the (possibly shared) session store for the store's whole lifetime,
        // so every window observes the same instance and none disposes it per-window. We only subscribe.
        _chatOrchestrationService = _chatSessionStore.OrchestrationService;
        _chatOrchestrationService.ChatsChanged += OnOrchestrationChatsChanged;

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

        _chatVM = AcquireDraftChatSurface(SelectedProjectFilter);
        _chatVM.AddDisplayHost();
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
        ChatTagsVM = new ChatTagsViewModel(dataStore);
        SkillsVM = new SkillsViewModel(dataStore);
        AgentsVM = new AgentsViewModel(dataStore);
        ProjectsVM = new ProjectsViewModel(dataStore, projectGitSyncService);
        MemoriesVM = new MemoriesViewModel(dataStore);
        McpServersVM = new McpServersViewModel(dataStore);
        var pluginCatalog = new PluginCatalogService(copilotService);
        SetupsVM = new SetupSuggestionsViewModel(dataStore, copilotService, pluginCatalog, GetCustomizeCapabilities)
        {
            IsWelcome = !_isOnboarded,
        };
        DiscoverVM = new DiscoverViewModel(
            dataStore,
            pluginCatalog,
            new McpRegistryClient(),
            GetCustomizeCapabilities)
        {
            Setups = SetupsVM,
        };
        SetupsVM.TryRequested += StartSetupDraft;
        SetupsVM.ManageRequested += ManageSetup;
        ShareVM = new ShareSheetViewModel(dataStore);
        ImportVM = new ImportSheetViewModel(dataStore);
        NoticeVM = new CapabilityNoticeViewModel(
            dataStore,
            ClipboardHelper.GetTextAsync,
            () => IsOnboarded && !ImportVM.IsOpen && !ShareVM.IsOpen);
        LibraryVM = new LibraryViewModel(
            dataStore,
            async chatId => await OpenChatByIdAsync(chatId));
        SettingsVM = new SettingsViewModel(dataStore, copilotService, _settingsBrowserService, updateService, _secureKeyStore);
        SettingsVM.LoginVM = LoginVM;
        // Available even when already onboarded, for the --onboarding flag; someone who has been
        // through it before can leave it early.
        _onboardingVM = CreateOnboardingViewModel(isReplay: settings.IsOnboarded);
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

            if (args.PropertyName is nameof(SettingsViewModel.SelectedPageIndex) or nameof(SettingsViewModel.IsSearching))
                SyncSettingsPageWithNav();

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
            else if (args.PropertyName == nameof(SettingsViewModel.SuggestFollowUps))
                _chatSessionStore.ApplyToSurfaces(surface => surface.ApplySuggestFollowUpsSetting());
            else if (args.PropertyName == nameof(SettingsViewModel.IsAuthenticated))
            {
                if (SettingsVM.IsAuthenticated)
                    _ = RefreshCopilotStateAsync(refreshAuthStatus: false);
                else if (!_isRefreshingCopilotState && !IsConnecting)
                {
                    IsConnected = _copilotService.IsExternalOnly && _copilotService.IsConnected;
                    ConnectionStatus = IsConnected ? Loc.Status_Connected : Loc.Status_Disconnected;
                }
            }
            else if (args.PropertyName == nameof(SettingsViewModel.UserName))
                UserName = SettingsVM.UserName;
            else if (args.PropertyName == nameof(SettingsViewModel.UseBYOKOnly))
                InjectByokModels();
        };

        SkillsVM.SkillsChanged += () =>
        {
            _chatSessionStore.ApplyToSurfaces(surface =>
            {
                surface.InvalidateSystemPromptSession();
                surface.RefreshComposerCatalogs();
            });
            RefreshFeatureManagementUi();
        };
        AgentsVM.AgentsChanged += () =>
        {
            _chatSessionStore.ApplyToSurfaces(surface =>
            {
                surface.InvalidateAgentSession();
                surface.RefreshComposerCatalogs();
            });
            RefreshFeatureManagementUi();
        };

        JobsVM.JobsChanged += () =>
        {
            _backgroundJobService.Reschedule();
            RefreshFeatureManagementUi(refreshJobs: false);
        };
        JobsVM.OpenChatRequested += jobChatId => _ = OpenChatByIdAsync(jobChatId);
        _backgroundJobService.JobsChanged += OnBackgroundJobServiceJobsChanged;
        _copilotService.ModelCatalogChanged += OnModelCatalogChanged;

        SettingsVM.AmbientPresenceChanged += ApplyAmbientPresenceSetting;
        SettingsVM.PresenceAnimationChanged += ApplyPresenceAnimationSetting;
        SettingsVM.PresenceBrightnessChanged += ApplyPresenceBrightnessSetting;
        SettingsVM.ReplayWelcomeRequested += ReplayWelcome;
        SettingsVM.SettingsChanged += () =>
        {
            RefreshChatList();
        };
        SettingsVM.ApprovalPolicyChanged += () =>
        {
            _chatSessionStore.ApplyToSurfaces(surface => surface.OnApprovalPolicyChanged());
            ProjectsVM.RefreshApprovals();
        };
        SettingsVM.SystemPromptSettingsChanged += () =>
        {
            _chatSessionStore.ApplyToSurfaces(surface => surface.InvalidateSystemPromptSession());
        };
        SettingsVM.McpDiscoveryRefreshRequested += () =>
        {
            _chatSessionStore.ApplyToSurfaces(surface => surface.InvalidateMcpSession());
        };
        SettingsVM.ByokConfigurationChanged += () =>
        {
            InjectByokModels();
            RefreshChatList();
            // Only a switch into or out of running on external accounts alone needs a reconnect pass.
            var isExternalOnly = _copilotService.IsExternalOnly;
            if (isExternalOnly != _wasExternalOnly && !_isRefreshingCopilotState)
                _ = RefreshCopilotStateAsync(refreshAuthStatus: false);
            _wasExternalOnly = isExternalOnly;
        };

        AttachChatViewModel(ChatVM);

        // Feature-management changes can originate from any chat surface (the visible chat,
        // a chat still running after the user navigated away, a background-job chat, or a
        // detached window). Subscribe at the store level so the main window's collections
        // refresh no matter which surface executed the change.
        _chatSessionStore.SurfaceFeatureManagementStateChanged += OnChatFeatureManagementStateChanged;
        _chatSessionStore.SurfaceApprovalsRemembered += OnChatApprovalsRemembered;

        ProjectsVM.ProjectsChanged += () =>
        {
            _chatSessionStore.ApplyToSurfaces(surface =>
            {
                surface.InvalidateProjectSession();
                surface.RefreshCapabilities();
            });
            RefreshFeatureManagementUi();
        };
        ProjectsVM.ApprovalPolicyChanged += () =>
        {
            _chatSessionStore.ApplyToSurfaces(surface => surface.OnApprovalPolicyChanged());
            SettingsVM.RefreshApprovalRules();
        };
        ProjectsVM.AddApprovalRuleRequested += projectId =>
        {
            SettingsVM.SelectedPageIndex = SettingsViewModel.PermissionsPageIndex;
            SelectedNavIndex = 7;
            SettingsVM.StartAddApprovalRuleFor(projectId);
        };

        McpServersVM.McpConfigChanged += () =>
        {
            _chatSessionStore.ApplyMcpConfigurationChange();
            RefreshFeatureManagementUi();
        };

        DiscoverVM.McpServersChanged += () =>
        {
            _chatSessionStore.ApplyMcpConfigurationChange();
            RefreshFeatureManagementUi();
        };
        DiscoverVM.OpenMcpServerRequested += (id, needsSetup) =>
        {
            SelectedNavIndex = 6;
            if (needsSetup)
                McpServersVM.OpenForSetup(id);
            else
                McpServersVM.OpenById(id);
        };
        DiscoverVM.ShowPluginPartsRequested += ShowPluginParts;
        SkillsVM.DiscoverRequested += () => SelectedNavIndex = DiscoverNavIndex;
        AgentsVM.DiscoverRequested += () => SelectedNavIndex = DiscoverNavIndex;
        McpServersVM.DiscoverRequested += () =>
        {
            SelectedNavIndex = DiscoverNavIndex;
            DiscoverVM.ShowServersCommand.Execute(null);
        };
        SkillsVM.External.ManageInDiscoverRequested += ManageInDiscover;
        AgentsVM.External.ManageInDiscoverRequested += ManageInDiscover;
        McpServersVM.External.ManageInDiscoverRequested += ManageInDiscover;
        _copilotService.PluginsChanged += OnPluginsChanged;

        SkillsVM.ShareRequested += ShareVM.OpenFor;
        AgentsVM.ShareRequested += ShareVM.OpenFor;
        McpServersVM.ShareRequested += ShareVM.OpenFor;
        SkillsVM.ImportRequested += ImportVM.Open;
        AgentsVM.ImportRequested += ImportVM.Open;
        McpServersVM.ImportRequested += ImportVM.Open;
        ImportVM.Imported += OnCapabilitiesImported;
        ImportVM.OpenItemRequested += OpenImportedCapability;
        ImportVM.ChatWithLumiRequested += StartChatWithImportedLumi;
        ImportVM.PromptUseRequested += StartChatWithSharedPrompt;
        AgentsVM.ChatRequested += agent => StartChatWithImportedLumi(agent.Id);
        ProjectsVM.NewChatRequested += StartChatInProject;
        SkillsVM.OpenAgentRequested += OpenLumiPage;
        McpServersVM.OpenAgentRequested += OpenLumiPage;
        AgentsVM.OpenSkillRequested += id =>
        {
            SelectedNavIndex = 3;
            SkillsVM.OpenById(id);
        };
        AgentsVM.OpenMcpServerRequested += id =>
        {
            SelectedNavIndex = 6;
            McpServersVM.OpenById(id);
        };
        MemoriesVM.OpenProjectRequested += id =>
        {
            SelectedNavIndex = 2;
            ProjectsVM.OpenById(id);
        };
        SkillsVM.CopyForChatRequested += (skill, unsaved) => _ = CopyForChatAsync(ShareVM.QuickCopyForChatAsync(skill, unsaved));
        AgentsVM.CopyForChatRequested += (agent, unsaved) => _ = CopyForChatAsync(ShareVM.QuickCopyForChatAsync(agent, unsaved));
        McpServersVM.CopyForChatRequested += (server, unsaved) => _ = CopyForChatAsync(ShareVM.QuickCopyForChatAsync(server, unsaved));
        NoticeVM.PreviewRequested += text => ImportVM.OpenWithText(text, Loc.Import_FromClipboard);
        LoadProjects();
        SubscribeChatRunningState();
        RefreshChatList();
        if (_ownsBackgroundJobService && startBackgroundJobs)
            _backgroundJobService.Start();

#if DEBUG
        if (openAgentDebugHarness)
            OpenAgentDebugHarness();
#endif

        InitializeNavigationHistory();
        if (initializeCopilotOnStartup)
        {
            // Keep saved model choices ready for the first frame; only defer connecting.
            InjectByokModels();
            Dispatcher.UIThread.Post(() =>
            {
                if (!_isDisposed)
                    _ = InitializeAsync();
            }, DispatcherPriority.Background);
        }
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

    internal void ApplyAmbientPresenceSetting(bool enabled)
        => _chatSessionStore.ApplyToSurfaces(surface => surface.ShowAmbientPresence = enabled);

    internal void ApplyPresenceAnimationSetting(bool enabled)
        => _chatSessionStore.ApplyToSurfaces(surface => surface.AnimatePresenceWhileWorking = enabled);

    internal void ApplyPresenceBrightnessSetting(double brightness)
        => _chatSessionStore.ApplyToSurfaces(surface => surface.PresenceBrightness = brightness);

    private Task<ChatViewModel> AcquireChatSurfaceAsync(Chat chat)
        => _chatSessionStore.AcquireChatAsync(chat, PrepareChatSurface);

    private void AttachChatViewModel(ChatViewModel chatVm)
    {
        // Only the chat this window shows offers its setup suggestion: a detached chat window has none,
        // so its buttons can't act on this window.
        chatVm.SetupSuggestions = SetupsVM;
        chatVm.DefaultModelSelectionChanged += OnChatDefaultModelSelectionChanged;
        chatVm.ChatUpdated += OnChatUpdated;
        chatVm.ChatTitleChanged += OnChatTitleChanged;
        chatVm.PropertyChanged += OnChatViewModelPropertyChanged;
        chatVm.ComposerProjectFilterRequested += OnComposerProjectFilterRequested;
        chatVm.OpenChatRequested += OnChatOpenChatRequested;
        chatVm.RevealChatRequested += OnChatRevealChatRequested;
        chatVm.OpenAutomationsRequested += OnChatOpenAutomationsRequested;
        chatVm.ForkChatRequested += OnChatForkRequested;
        chatVm.SharePromptRequested += OnChatSharePromptRequested;
        chatVm.PromptImportRequested += OnChatPromptImportRequested;
        chatVm.McpRepairRequested += StartMcpRepairChatAsync;
        chatVm.ConnectorsPageRequested += ShowConnectorsPage;
        chatVm.SkillsPageRequested += ShowSkillsPage;
        chatVm.ManageApprovalsRequested += ShowPermissionsPage;
    }

    private void DetachChatViewModel(ChatViewModel chatVm)
    {
        // A cached chat must not keep this window (through its suggestions) alive after it closes.
        if (ReferenceEquals(chatVm.SetupSuggestions, SetupsVM))
            chatVm.SetupSuggestions = null;
        chatVm.DefaultModelSelectionChanged -= OnChatDefaultModelSelectionChanged;
        chatVm.ChatUpdated -= OnChatUpdated;
        chatVm.ChatTitleChanged -= OnChatTitleChanged;
        chatVm.PropertyChanged -= OnChatViewModelPropertyChanged;
        chatVm.ComposerProjectFilterRequested -= OnComposerProjectFilterRequested;
        chatVm.OpenChatRequested -= OnChatOpenChatRequested;
        chatVm.RevealChatRequested -= OnChatRevealChatRequested;
        chatVm.OpenAutomationsRequested -= OnChatOpenAutomationsRequested;
        chatVm.ForkChatRequested -= OnChatForkRequested;
        chatVm.SharePromptRequested -= OnChatSharePromptRequested;
        chatVm.PromptImportRequested -= OnChatPromptImportRequested;
        chatVm.McpRepairRequested -= StartMcpRepairChatAsync;
        chatVm.ConnectorsPageRequested -= ShowConnectorsPage;
        chatVm.SkillsPageRequested -= ShowSkillsPage;
        chatVm.ManageApprovalsRequested -= ShowPermissionsPage;
    }

    private void OnChatOpenChatRequested(Guid chatId) => _ = OpenChatByIdAsync(chatId);

    private void OnChatRevealChatRequested(Guid chatId)
    {
        var chat = _dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == chatId);
        if (chat is not null)
            _ = RevealChatAsync(chat);
    }

    private void OnChatOpenAutomationsRequested() => ShowAutomations();

    /// <summary>Shows the Jobs page, where the user's automations live.</summary>
    public void ShowAutomations() => SelectedNavIndex = JobsNavIndex;

    /// <summary>
    /// From a chat's connector list: Discover's connectors, searching for what the user typed, or the
    /// Connectors section of Customize.
    /// </summary>
    public void ShowConnectorsPage(ConnectorsPageRequest request)
    {
        if (!request.Discover)
        {
            SelectedNavIndex = 6;
            return;
        }

        SelectedNavIndex = DiscoverNavIndex;
        DiscoverVM.ShowServersCommand.Execute(null);
        DiscoverVM.SearchQuery = request.Query ?? string.Empty;
    }

    /// <summary>Opens the Skills section of Customize, from a chat's Skills picker.</summary>
    public void ShowSkillsPage() => SelectedNavIndex = 3;

    /// <summary>Opens Settings → Permissions, from a chat's approval mode picker.</summary>
    public void ShowPermissionsPage()
    {
        SettingsVM.SelectedPageIndex = SettingsViewModel.PermissionsPageIndex;
        SelectedNavIndex = 7;
    }

    private void OnChatForkRequested(Chat chat, Guid throughMessageId)
        => _ = ForkChatAsync(chat, throughMessageId);

    private void OnChatSharePromptRequested(PromptShareSource source, Func<PromptShareSource, CancellationToken, Task<SharedPrompt>> prepare)
        => _ = ShareVM.OpenForPromptAsync(source, prepare);

    private void OnChatPromptImportRequested(string text)
    {
        NoticeVM.HideOffer();
        ImportVM.OpenWithText(text, Loc.Import_FromClipboard);
    }

    private void ShowChatSurface(ChatViewModel surface)
    {
        var previous = ChatVM;
        if (ReferenceEquals(previous, surface))
        {
            // Already this window's surface, so it already holds this window's display host.
            _chatSessionStore.Release(surface);
            if (surface.CurrentChat is { } chat)
                chat.HasUnreadMessages = false;
            ActiveChatId = surface.CurrentChat?.Id;
            return;
        }

        DetachChatViewModel(previous);
        ChatVM = surface;
        AttachChatViewModel(surface);
        // Display hosts are counted, so hand this window's host over to the new surface — the old one may
        // still be on screen in another window.
        surface.AddDisplayHost();
        previous.RemoveDisplayHost();
        ActiveChatId = surface.CurrentChat?.Id;
        surface.RefreshComposerCatalogs();
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
        RefreshFeatureManagementUi(preserveJobsEditor: true);
    }

    /// <summary>"Always in a project" reaches its other open chats, and both lists show the new approval.</summary>
    private void OnChatApprovalsRemembered()
    {
        _chatSessionStore.ApplyToSurfaces(surface => surface.OnApprovalPolicyChanged());
        SettingsVM.RefreshApprovalRules();
        ProjectsVM.RefreshApprovals();
    }

    internal async Task ApplyFeatureChangeAsync(
        FeatureChangeResult result,
        string resource,
        IReadOnlySet<Guid>? affectedProjectChatIds = null)
    {
        if (result.SyncSkillFiles)
            _dataStore.SyncSkillFiles();

        if (result.RenamedMcpOldName is { } oldName && result.RenamedMcpNewName is { } newName)
        {
            foreach (var chat in _dataStore.Data.Chats.Where(chat => chat.ActiveMcpServerNames.Contains(oldName)))
            {
                for (var index = 0; index < chat.ActiveMcpServerNames.Count; index++)
                {
                    if (string.Equals(chat.ActiveMcpServerNames[index], oldName, StringComparison.Ordinal))
                        chat.ActiveMcpServerNames[index] = newName;
                }
                _dataStore.MarkChatChanged(chat);
            }

            _chatSessionStore.ApplyToSurfaces(surface =>
            {
                if (!surface.ActiveMcpServerNames.Contains(oldName, StringComparer.Ordinal))
                    return;

                surface.RemoveMcpByName(oldName);
                surface.RegisterMcpByName(newName);
            });
        }

        if (result.DeletedMcpName is { } deletedName)
        {
            foreach (var chat in _dataStore.Data.Chats.Where(chat => chat.ActiveMcpServerNames.Contains(deletedName)))
            {
                chat.ActiveMcpServerNames.RemoveAll(name =>
                    string.Equals(name, deletedName, StringComparison.Ordinal));
                _dataStore.MarkChatChanged(chat);
            }

            _chatSessionStore.ApplyToSurfaces(surface => surface.RemoveMcpByName(deletedName));
        }

        McpProxyRuntime.Shared.RetireUserRegistrationsExcept(_dataStore.Data.McpServers
            .Where(server => server.IsEnabled
                             && !string.Equals(server.ServerType, "remote", StringComparison.OrdinalIgnoreCase))
            .Select(server => server.Id));

        _chatSessionStore.ApplyToSurfaces(surface =>
        {
            switch (resource)
            {
                case RemoteProtocol.Resources.Projects:
                    if (surface.CurrentChat is { } projectChat
                        && affectedProjectChatIds?.Contains(projectChat.Id) == true)
                    {
                        surface.OnCurrentChatProjectChangedExternally();
                    }
                    break;
                case RemoteProtocol.Resources.Lumis:
                    surface.InvalidateAgentSession();
                    break;
                case RemoteProtocol.Resources.Mcps:
                    surface.InvalidateMcpSession();
                    break;
                case RemoteProtocol.Resources.Skills:
                case RemoteProtocol.Resources.Memories:
                    surface.InvalidateSystemPromptSession();
                    break;
            }

            surface.RefreshComposerCatalogs();
        });

        await _dataStore.SaveAsync();
        _backgroundJobService.Reschedule();
        RefreshFeatureManagementUi(preserveJobsEditor: true);
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
        await RefreshExternalCapabilitiesAsync(force: false);
        if (IsOnboarded)
            await SetupsVM.CheckAtStartupAsync();
        else
            await SetupsVM.RefreshAsync();
    }

    private void OnDataStoreChatContentChanged(Guid chatId)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnDataStoreChatContentChanged(chatId));
            return;
        }

        _globalSearchService.InvalidateChatContent(chatId);
        LibraryVM.MarkDirty();
        if (!_dataStore.Data.Chats.Any(chat => chat.Id == chatId))
            RefreshChatList();
    }

    private void OnDataStoreChatsContentReset()
    {
        _globalSearchService.PruneChatContent();
        LibraryVM.MarkDirty();
    }

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
        SetupsVM.TryRequested -= StartSetupDraft;
        SetupsVM.ManageRequested -= ManageSetup;
        SetupsVM.Dispose();
        ShareVM.CloseCommand.Execute(null);
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
        _copilotService.ModelCatalogChanged -= OnModelCatalogChanged;
        _copilotService.PluginsChanged -= OnPluginsChanged;
        _chatSessionStore.SurfaceFeatureManagementStateChanged -= OnChatFeatureManagementStateChanged;
        _chatSessionStore.SurfaceApprovalsRemembered -= OnChatApprovalsRemembered;
        _chatOrchestrationService.ChatsChanged -= OnOrchestrationChatsChanged;
        if (_ownsBackgroundJobService)
            _backgroundJobService.Dispose();
        LoginVM.CancelSignIn();
        DetachChatViewModel(ChatVM);
        ChatVM.RemoveDisplayHost();
        _chatSessionStore.Release(ChatVM);
        if (_ownsChatSessionStore)
            _chatSessionStore.Dispose();
        UnsubscribeChatRunningState();
        foreach (var repairChat in _mcpRepairChats.Keys)
            repairChat.PropertyChanged -= OnMcpRepairChatChanged;
        _mcpRepairChats.Clear();
        if (_ownsChatSurfaceRegistry)
            _chatSurfaceRegistry.Dispose();
        ChatTagsVM.Dispose();
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

            _wasExternalOnly = _copilotService.IsExternalOnly;
            if (_wasExternalOnly)
            {
                if (_isDisposed)
                    return;
                InjectByokModels();
                if (ChatVM.CurrentChat is null || ChatVM.CurrentChat.Messages.Count == 0)
                    ChatVM.RestoreDefaultModelSelection();
                IsConnected = true;
                ConnectionStatus = Loc.Status_Connected;
                RefreshCapabilitiesAfterConnection();
                return;
            }

            var models = await _copilotService.GetModelsAsync();
            var contextWindowCatalog = await _copilotService.GetContextWindowCatalogAsync();

            if (_isDisposed)
                return;

            IsConnected = true;
            ConnectionStatus = Loc.Status_Connected;

            ApplyModelCatalog(models, contextWindowCatalog, resetSurfaceSelections: true);

            // Refresh account quota in background
            _ = ChatVM.RefreshQuotaAsync();

            // The runtime is now available, so start one new capability generation and refresh every
            // surface against it.
            RefreshCapabilitiesAfterConnection();
        }
        catch (Exception ex)
        {
            ConnectionStatus = string.Format(Loc.Status_ConnectionFailed, ex.Message);
            IsConnected = false;
            // Even when GitHub's catalog is unavailable, BYOK picks must remain in the picker.
            InjectByokModels();
        }
        finally
        {
            IsConnecting = false;
            _isRefreshingCopilotState = false;
        }
    }

    /// <summary>
    /// Pushes a model catalog into every chat surface and Settings.
    /// </summary>
    /// <param name="resetSurfaceSelections">
    /// True on (re)connect, where every surface adopts the resolved default model. False for a live
    /// catalog refresh, where each surface keeps the model the user already chose and only falls
    /// back when that model is no longer offered.
    /// </param>
    private void ApplyModelCatalog(
        List<GitHub.Copilot.ModelInfo> models,
        ModelContextWindowCatalog contextWindowCatalog,
        bool resetSurfaceSelections)
    {
        var longContextModelIds = contextWindowCatalog.LongContextModelIds;
        var modelIds = models
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Concat(ByokConfigHelper.GetValidModels(_dataStore.Data.Settings)
                .Select(ByokConfigHelper.BuildModelToken))
            .ToList();
        // Auto-select best model on clean state (no user preference saved)
        var selected = ChatVM.SelectedModel;
        var isCleanState = string.IsNullOrWhiteSpace(selected)
            || !modelIds.Contains(selected);
        if (isCleanState)
            selected = ResolveFallbackModel(modelIds);

        _chatSessionStore.ApplyToSurfaces(surface =>
        {
            surface.UpdateModelCapabilities(models, longContextModelIds, contextWindowCatalog.Limits);
            surface.ApplyAvailableModels(modelIds, selected);
            if (resetSurfaceSelections)
                surface.SelectedModel = selected;
        });

        SettingsVM.UpdateModelCapabilities(models, longContextModelIds);
        SettingsVM.UpdateAvailableModels(modelIds);
        if (isCleanState && selected is not null)
            SettingsVM.PreferredModel = selected;

        InjectByokModels();
    }

    /// <summary>
    /// Applies a catalog the Copilot service refreshed on demand (e.g. when the user opened the model
    /// picker) after a new model appeared. Only fires when the catalog actually changed, so this
    /// never disturbs the UI on a no-op refresh.
    /// </summary>
    private void OnModelCatalogChanged(ModelCatalogSnapshot snapshot)
    {
        var connectionGeneration = _copilotService.ConnectionGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed || _isRefreshingCopilotState
                || connectionGeneration != _copilotService.ConnectionGeneration)
                return;

            ApplyModelCatalog(snapshot.Models.ToList(), snapshot.ContextWindows, resetSurfaceSelections: false);

            // An initial catalog timeout can leave the UI disconnected while the CLI still works.
            // A successful refresh recovers that state without reconnecting or resetting chat choices.
            if (!IsConnected)
            {
                IsConnected = true;
                ConnectionStatus = Loc.Status_Connected;
                RefreshCapabilitiesAfterConnection();
            }
        });
    }

    /// <summary>
    /// Re-injects BYOK picker tokens into all chat surfaces and the Settings picker, then clears
    /// stale selections that point to deleted/invalid entries.
    /// </summary>
    private void InjectByokModels()
    {
        var settings = _dataStore.Data.Settings;
        var useByokOnly = settings.UseBYOKOnly;
        var validModels = ByokConfigHelper.GetValidModels(settings);
        var tokens = validModels
            .Select(ByokConfigHelper.BuildModelToken)
            .ToList();
        var byokModelCatalog = validModels
            .Select(model =>
            {
                var supportedEfforts = model.SupportsReasoningEffort
                    ? (model.SupportedReasoningEfforts ?? [])
                        .Where(static effort => !string.IsNullOrWhiteSpace(effort))
                        .Select(static effort => effort.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : [];
                var defaultEffort = supportedEfforts.FirstOrDefault(effort =>
                    string.Equals(effort, model.DefaultReasoningEffort?.Trim(), StringComparison.OrdinalIgnoreCase));

                var info = new ModelInfo
                {
                    Id = ByokConfigHelper.BuildModelToken(model),
                    Name = model.DisplayName,
                    SupportedReasoningEfforts = supportedEfforts,
                    DefaultReasoningEffort = defaultEffort,
                };
                if (model.DefaultContextWindowTokens is > 0)
                    info.Capabilities = new ModelCapabilities
                    { Limits = new ModelLimits { MaxContextWindowTokens = model.DefaultContextWindowTokens.Value } };
                return info;
            })
            .ToList();
        var byokLongContextModelIds = validModels
            .Where(static model => model.LongContextWindowTokens is > 0)
            .Select(ByokConfigHelper.BuildModelToken)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byokContextWindowLimits = validModels
            .Where(static model => model.DefaultContextWindowTokens is > 0 || model.LongContextWindowTokens is > 0)
            .ToDictionary(
                ByokConfigHelper.BuildModelToken,
                static model => new ModelContextWindowLimits(
                    model.DefaultContextWindowTokens is > 0 ? model.DefaultContextWindowTokens.Value : 0,
                    model.LongContextWindowTokens is > 0 ? model.LongContextWindowTokens.Value : null),
                StringComparer.OrdinalIgnoreCase);

        // Rebuild the display cache (idempotent — adds, replaces, and prunes missing entries).
        var newCacheEntries = validModels.ToDictionary(
            ByokConfigHelper.BuildModelToken,
            model => ModelOptionCatalog.GetByokDisplayName(model,
                settings.ByokEndpoints.FirstOrDefault(endpoint => endpoint.Id == model.EndpointId)),
            StringComparer.Ordinal);
        // Replace cache contents: remove stale entries that are no longer valid.
        foreach (var existing in _byokDisplayCache.Keys.ToList())
        {
            if (!newCacheEntries.ContainsKey(existing))
                _byokDisplayCache.TryRemove(existing, out _);
        }
        foreach (var kv in newCacheEntries)
            _byokDisplayCache[kv.Key] = kv.Value;

        // 1. Chat surfaces — refresh BYOK tokens. Copilot (non-BYOK) models are ALWAYS kept
        //    visible in the picker, even when UseBYOKOnly is enabled. The send guard blocks
        //    non-BYOK requests at send time rather than hiding the models or overwriting the
        //    user's selection.
        _chatSessionStore.ApplyToSurfaces(surface =>
        {
            // Drop stale BYOK tokens (no longer in the valid set). Non-BYOK entries are always kept.
            for (var i = surface.AvailableModels.Count - 1; i >= 0; i--)
            {
                var model = surface.AvailableModels[i];
                if (ByokConfigHelper.IsByokModel(model) && !tokens.Contains(model))
                    surface.AvailableModels.RemoveAt(i);
            }

            foreach (var t in tokens)
                if (!surface.AvailableModels.Contains(t))
                    surface.AvailableModels.Add(t);

            surface.UpdateModelCapabilities(
                byokModelCatalog,
                longContextModelIds: byokLongContextModelIds,
                contextWindowLimits: byokContextWindowLimits,
                merge: true);

            // Only fix stale/invalid selections — never overwrite a valid non-BYOK pick.
            // BYOK Only blocks at send time; the user keeps their current model visible.
            var currentSelection = surface.SelectedModel;
            if (ByokConfigHelper.IsByokModel(currentSelection) && !tokens.Contains(currentSelection!))
            {
                // Stale BYOK token (deleted model/endpoint) → fall back.
                surface.SelectedModel = useByokOnly
                    ? ResolveByokFallbackModel(tokens)
                    : ResolveFallbackModel(surface.AvailableModels);
            }
            else if (string.IsNullOrWhiteSpace(currentSelection) && useByokOnly && tokens.Count > 0)
            {
                // No selection at all under BYOK Only → pick the first BYOK model as a working default.
                surface.SelectedModel = ResolveByokFallbackModel(tokens);
            }
        });

        // 2. Settings VM — always show all models (Copilot + BYOK). BYOK Only blocks at send
        //    time; it does not hide Copilot models from the picker or force a BYOK selection.
        var combined = SettingsVM.AvailableModels
            .Where(m => !ByokConfigHelper.IsByokModel(m))
            .Concat(tokens)
            .ToList();

        // Only fix a stale/invalid PreferredModel — don't force a non-BYOK selection to BYOK.
        if (ByokConfigHelper.IsByokModel(SettingsVM.PreferredModel)
            && !tokens.Contains(SettingsVM.PreferredModel!))
        {
            SettingsVM.PreferredModel = ResolveFallbackModel(combined) ?? "";
        }
        SettingsVM.UpdateAvailableModels(combined);
        SettingsVM.UpdateModelCapabilities(byokModelCatalog, byokLongContextModelIds, merge: true);

        // 3. Clean up stale LastModelUsed references in chat history.
        CleanupStaleByokLastModels(tokens);
    }

    /// <summary>
    /// Resets each chat's <c>LastModelUsed</c> to <c>null</c> when it points to a BYOK token that
    /// is no longer valid. The chat falls back to the current <c>PreferredModel</c> on next open.
    /// </summary>
    private void CleanupStaleByokLastModels(IReadOnlyList<string> validTokens)
    {
        var validSet = new HashSet<string>(validTokens, StringComparer.Ordinal);
        var changed = false;
        foreach (var chat in _dataStore.Data.Chats)
        {
            if (!string.IsNullOrWhiteSpace(chat.LastModelUsed)
                && ByokConfigHelper.IsByokModel(chat.LastModelUsed)
                && !validSet.Contains(chat.LastModelUsed))
            {
                chat.LastModelUsed = null;
                _dataStore.MarkChatChanged(chat);
                changed = true;
            }
        }
        if (changed)
            _ = _dataStore.SaveAsync();
    }

    /// <summary>
    /// Picks a sensible non-BYOK fallback from <paramref name="availableModels"/>. Never returns a
    /// stale BYOK token — the BYOK layer must fail loudly rather than silently fall back to
    /// Copilot.
    /// </summary>
    private static string? ResolveFallbackModel(IReadOnlyList<string> availableModels)
    {
        var nonByok = availableModels.Where(m => !ByokConfigHelper.IsByokModel(m)).ToList();
        return nonByok.Count > 0 ? ChatViewModel.PickBestModel(nonByok) : null;
    }

    private static string? ResolveByokFallbackModel(IReadOnlyList<string> byokTokens)
        => byokTokens.FirstOrDefault();

    private void LoadProjects()
    {
        Projects.Clear();
        foreach (var p in _dataStore.Data.Projects.OrderBy(p => p.Name))
            Projects.Add(p);
    }

    /// <summary>
    /// An import can add skills, a Lumi and MCP servers in one step; refresh every chat surface the
    /// same way the individual editors do when they save one of those.
    /// </summary>
    private void OnCapabilitiesImported(ImportOutcome outcome)
    {
        _chatSessionStore.ApplyToSurfaces(surface =>
        {
            if (outcome.AddedSkills.Count > 0)
                surface.InvalidateSystemPromptSession();
            if (outcome.AddedLumi is not null)
                surface.InvalidateAgentSession();
            surface.RefreshComposerCatalogs();
        });

        if (outcome.AddedMcpServers.Count > 0)
            _chatSessionStore.ApplyMcpConfigurationChange();

        // An offer for what is on the clipboard is stale now: it was just imported, or passed over.
        NoticeVM.HideOffer();
        RefreshFeatureManagementUi();
    }

    private void OpenImportedCapability(SharedCapabilityKind kind, Guid id)
    {
        switch (kind)
        {
            case SharedCapabilityKind.Skill when _dataStore.Data.Skills.FirstOrDefault(skill => skill.Id == id) is { } skill:
                SelectedNavIndex = 3;
                if (!SkillsVM.Skills.Contains(skill))
                    SkillsVM.SearchQuery = "";
                SkillsVM.SelectedSkill = skill;
                break;

            case SharedCapabilityKind.McpServer when _dataStore.Data.McpServers.FirstOrDefault(server => server.Id == id) is { } server:
                SelectedNavIndex = 6;
                if (!McpServersVM.Servers.Contains(server))
                    McpServersVM.SearchQuery = "";
                McpServersVM.SelectedServer = server;
                break;
        }
    }

    private async Task CopyForChatAsync(Task<CapabilityPack?> copy)
    {
        if (await copy is not { } pack)
            return;

        NoticeVM.ShowCopied(CapabilityCardViewModel.ForPack(pack, ""));
    }

    private void StartChatWithImportedLumi(Guid agentId)
    {
        if (_dataStore.Data.Agents.FirstOrDefault(agent => agent.Id == agentId) is not { } agent)
            return;

        NewChatCommand.Execute(null);
        ChatVM.SelectedSdkAgentName = null;
        ChatVM.SetActiveAgent(agent);
    }

    private void StartChatWithSharedPrompt(PromptUseRequest request)
    {
        ClearMainChatSurface();
        SetDraftChatProjectContext(SelectedProjectFilter);
        ChatVM.PromptText = request.Text;
        foreach (var path in request.Attachments)
            ChatVM.AddAttachment(path);
        SelectedNavIndex = 0;
        NoticeVM.HideOffer();
    }

    /// <summary>"Fix with Lumi" chats this window opened, with the chat and connector each one repairs.</summary>
    private readonly Dictionary<Chat, (Guid? SourceChatId, string ConnectorName)> _mcpRepairChats = [];

    /// <summary>
    /// Opens a new chat that asks Lumi to fix a connector and sends the request, with the full report
    /// attached. A repair of the same connector that is still running is opened instead of a second one.
    /// </summary>
    internal async Task StartMcpRepairChatAsync(McpRepairRequest request)
    {
        var running = _mcpRepairChats.FirstOrDefault(pair =>
            pair.Key.IsRunning
            && pair.Value.SourceChatId == request.SourceChatId
            && string.Equals(pair.Value.ConnectorName, request.ConnectorName, StringComparison.OrdinalIgnoreCase)
            && _dataStore.Data.Chats.Contains(pair.Key)).Key;
        if (running is not null)
        {
            await LoadChatAndShowAsync(running);
            return;
        }

        var chat = request.Chat;
        var reportDirectory = Path.Combine(DataStore.AppDirectory, "diagnostics", chat.Id.ToString("N"));
        Directory.CreateDirectory(reportDirectory);
        var reportPath = Path.Combine(reportDirectory, request.ReportFileName);
        await File.WriteAllTextAsync(reportPath, request.ReportContent);

        _dataStore.Data.Chats.Add(chat);
        _dataStore.MarkChatChanged(chat);
        await _dataStore.SaveChatAsync(chat);
        await _dataStore.SaveAsync();
        RefreshChatList();
        ProjectsVM.RefreshSelectedProjectChats();

        if (!await LoadChatAndShowAsync(chat) || ChatVM.CurrentChat?.Id != chat.Id)
            throw new InvalidOperationException(Loc.Connector_RepairStartFailed);

        _mcpRepairChats[chat] = (request.SourceChatId, request.ConnectorName);
        chat.PropertyChanged += OnMcpRepairChatChanged;

        var repairSurface = ChatVM;
        repairSurface.AddAttachment(reportPath);
        repairSurface.SetComposerDraft(request.Message);
        await repairSurface.SendMessageCommand.ExecuteAsync(null);
    }

    /// <summary>When a repair turn ends, the original chat reconnects the connector with its next message.</summary>
    private void OnMcpRepairChatChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isDisposed
            || e.PropertyName != nameof(Chat.IsRunning)
            || sender is not Chat { IsRunning: false } chat
            || !_mcpRepairChats.TryGetValue(chat, out var repair)
            || repair.SourceChatId is not Guid sourceChatId)
            return;

        _chatSessionStore.ApplyToSurfaces(surface => surface.RetryMcpConnectorAfterRepair(sourceChatId, repair.ConnectorName));
    }

    private void OpenLumiPage(Guid agentId)
    {
        SelectedNavIndex = 4;
        AgentsVM.OpenById(agentId);
    }

    /// <summary>"New chat" on a project: the chat list narrows to the project and the draft joins it.</summary>
    private void StartChatInProject(Project project)
    {
        SelectProjectFilter(project);
        NewChat();
    }

    private void RefreshFeatureManagementUi(bool refreshJobs = true, bool preserveJobsEditor = false)
    {
        LoadProjects();
        if (refreshJobs)
            JobsVM.RefreshFromStore(preserveJobsEditor);
        ProjectsVM.RefreshFromStore();
        SkillsVM.RefreshFromStore();
        AgentsVM.RefreshFromStore();
        MemoriesVM.RefreshFromStore();
        McpServersVM.RefreshFromStore();
        DiscoverVM.RefreshAddedServers();
        if (IsCustomizeOpen)
            ApplyExternalCapabilities();

        if (SelectedProjectFilter.HasValue
            && !_dataStore.Data.Projects.Any(project => project.Id == SelectedProjectFilter.Value))
            SelectedProjectFilter = null;

        RefreshChatList();
        RefreshProjectRunningState();
        NotifyRunningJobs();
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
        RefreshChatPauseCommands();
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
                RefreshFeatureManagementUi(preserveJobsEditor: true);
        });
    }

    /// <summary>
    /// Fired when the orchestration service creates a managed chat or a managed run starts/finishes.
    /// Refreshes the sidebar chat list, wires running-state subscriptions for any new chats, and
    /// recomputes project running indicators so orchestrated work is visible in the UI.
    /// </summary>
    private void OnOrchestrationChatsChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed)
                return;
            SubscribeChatRunningState();
            RefreshChatList();
            ProjectsVM.RefreshSelectedProjectChats();
            RefreshProjectRunningState();
        });
    }

    private void OnChatRunningChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isDisposed)
            return;

        if (e.PropertyName is nameof(Chat.IsRunning) or nameof(Chat.IsPaused))
            RefreshProjectRunningState();
        else if (e.PropertyName == nameof(Chat.HasUnreadMessages))
            RefreshUnreadState();
        else if (e.PropertyName is not (nameof(Chat.IsSessionActive)
            or nameof(Chat.IsPaused) or nameof(Chat.IsPausePending)))
            return;

        RefreshChatPauseCommands();
        if (sender is Chat chat)
            ChatActivityOrReadStateChanged?.Invoke(chat.Id);
    }

    public event Action<Guid>? ChatActivityOrReadStateChanged;

    /// <summary>Recalculates IsRunning for all projects based on current chat states.</summary>
    public void RefreshProjectRunningState()
    {
        var chats = _dataStore.Data.Chats;
        foreach (var project in Projects)
            project.IsRunning = chats.Any(c => c.ProjectId == project.Id && c.ShowRunningIndicator);

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
        SubscribeChatRunningState();
        _chatLoadLimit = ChatPageSize;
        RebuildChatGroups();
        RefreshUnreadState();
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

        var allOrdered = chats
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.UpdatedAt)
            .ToList();
        HasMoreChats = allOrdered.Count > _chatLoadLimit;
        var ordered = allOrdered.Take(_chatLoadLimit).ToList();

        // Compute the project folder badge shown under each chat row. Badges only appear in the
        // "All projects" view (no active project filter). Setting these observable properties lets
        // the sidebar bind directly to each Chat, so the badge stays correct across virtualization,
        // container recycling, and group reshuffles (e.g. a chat bumped into "Today" on send).
        // The folder wears the project's own hue, as the chat header and the launchpad do.
        var showBadges = !SelectedProjectFilter.HasValue;
        foreach (var chat in ordered)
        {
            ChatTagsVM.ResolveTag(chat);
            var name = showBadges ? GetProjectName(chat.ProjectId) : null;
            chat.ProjectBadgeText = name;
            chat.ShowProjectBadge = name is not null;
            chat.ProjectBadgeColor = name is not null && chat.ProjectId is { } projectId
                ? LaunchpadProjectBadge.HueFor(projectId).ToString()
                : null;
        }

        // Group by time period
        var now = DateTimeOffset.Now;
        var today = now.Date;
        var yesterday = today.AddDays(-1);
        var weekAgo = today.AddDays(-7);

        var pinnedChats = ordered.Where(c => c.IsPinned).ToList();
        var unpinnedChats = ordered.Where(c => !c.IsPinned).ToList();
        var todayChats = unpinnedChats.Where(c => c.UpdatedAt.Date == today).ToList();
        var yesterdayChats = unpinnedChats.Where(c => c.UpdatedAt.Date == yesterday).ToList();
        var weekChats = unpinnedChats.Where(c => c.UpdatedAt.Date < yesterday && c.UpdatedAt.Date >= weekAgo).ToList();
        var olderChats = unpinnedChats.Where(c => c.UpdatedAt.Date < weekAgo).ToList();

        var groups = new List<ChatGroup>(5);
        var rowsChanged = false;
        AddGroup(Loc.ChatGroup_Pinned, pinnedChats);
        AddGroup(Loc.ChatGroup_Today, todayChats);
        AddGroup(Loc.ChatGroup_Yesterday, yesterdayChats);
        AddGroup(Loc.ChatGroup_Previous7Days, weekChats);
        AddGroup(Loc.ChatGroup_Older, olderChats);
        SynchronizeCollection(ChatGroups, groups);
        // Retained groups can change their rows without an outer collection notification.
        if (rowsChanged)
            ChatSelectionSyncRequested?.Invoke(ActiveChatId);

        void AddGroup(string label, List<Chat> items)
        {
            if (items.Count == 0)
                return;

            var group = ChatGroups.FirstOrDefault(candidate => candidate.Label == label)
                ?? new ChatGroup { Label = label };
            rowsChanged |= SynchronizeCollection(group.Chats, items);
            groups.Add(group);
        }
    }

    private static bool SynchronizeCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
        where T : class
    {
        if (target.SequenceEqual(desired))
            return false;

        var retained = desired.ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!retained.Contains(target[i]))
                target.RemoveAt(i);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i]))
                continue;

            var existingIndex = target.IndexOf(desired[i]);
            if (existingIndex >= 0)
                target.Move(existingIndex, i);
            else
                target.Insert(i, desired[i]);
        }
        return true;
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

            // Cached chat surfaces can be acquired synchronously. Yield one render turn before swapping
            // the transcript so selection and other composition animations are committed to the render
            // thread instead of waiting behind a potentially expensive cached transcript realization.
            if (shouldBridgeLoading)
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);

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
                chat.HasUnreadMessages = false;
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
        if (Program.OpenInteractiveControlsFixture)
        {
            var fixtures = InteractiveControlsFixture.Create(_dataStore);
            ChatVM.LoadDebugTranscriptFixture(fixtures.First(chat => chat.Title == "UI Lab - Forms"));
            RefreshChatList();
        }
        else
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
    private async Task DeleteChat(Chat chat)
    {
        if (IsChatFirstTurnReserved(chat.Id))
            return;

        // If the chat has a worktree, ask the user whether to clean it up. Forks share their
        // source's worktree, so only offer removal when no other chat still points at it —
        // otherwise deleting one branch would pull the directory out from under its siblings.
        if (chat.WorktreePath is { Length: > 0 } wt
            && Directory.Exists(wt)
            && !IsWorktreeSharedWithOtherChats(chat))
        {
            _pendingDeleteChat = chat;
            IsWorktreeDeleteDialogOpen = true;
            return;
        }

        await PerformDeleteChatAsync(chat);
    }

    /// <summary>
    /// True when another chat (e.g. a fork of this one, or the chat it was forked from) still
    /// references the same worktree directory.
    /// </summary>
    internal bool IsWorktreeSharedWithOtherChats(Chat chat)
    {
        if (chat.WorktreePath is not { Length: > 0 } worktreePath)
            return false;

        return _dataStore.Data.Chats.Any(other =>
            other.Id != chat.Id
            && string.Equals(other.WorktreePath, worktreePath, StringComparison.OrdinalIgnoreCase));
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
            if (IsChatFirstTurnReserved(chat.Id))
                return;

            _pendingDeleteChat = null;
            IsWorktreeDeleteDialogOpen = false;
            if (!await PerformDeleteChatAsync(chat, awaitMcpProxyRelease: true))
                return;

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
    private async Task ConfirmDeleteWithoutWorktree()
    {
        if (_pendingDeleteChat is not null)
        {
            var chat = _pendingDeleteChat;
            if (IsChatFirstTurnReserved(chat.Id))
                return;

            _pendingDeleteChat = null;
            IsWorktreeDeleteDialogOpen = false;
            await PerformDeleteChatAsync(chat);
        }
    }

    [RelayCommand]
    private void CancelDeleteWorktreeDialog()
    {
        _pendingDeleteChat = null;
        IsWorktreeDeleteDialogOpen = false;
    }

    internal async Task<bool> DeleteChatKeepingWorktreeAsync(Chat chat)
    {
        if (!_dataStore.Data.Chats.Contains(chat) || IsChatFirstTurnReserved(chat.Id))
            return false;

        return await PerformDeleteChatAsync(chat);
    }

    internal bool IsChatFirstTurnReserved(Guid chatId) =>
        _chatSessionStore
            .SnapshotSurfaces()
            .Any(surface => surface.IsExternalSendReserved(chatId));

    private async Task<bool> PerformDeleteChatAsync(Chat chat, bool awaitMcpProxyRelease = false)
    {
        if (IsChatFirstTurnReserved(chat.Id) || !_dataStore.Data.Chats.Contains(chat))
            return false;

        var deletedActiveChat = ChatVM.CurrentChat?.Id == chat.Id;
        var proxyReleaseTask = awaitMcpProxyRelease
            ? _chatSessionStore.BeginMcpProxyCleanupAsync(chat.Id)
            : Task.CompletedTask;

        _chatSessionStore.ApplyToSurfaces(surface =>
        {
            if (surface.CurrentChat?.Id == chat.Id)
                surface.ClearChat();
        });

        _chatSessionStore.CleanupChat(chat.Id);
        await proxyReleaseTask;
        if (deletedActiveChat)
            ClearMainChatSurface();
        _dataStore.Data.Chats.Remove(chat);
        _dataStore.RemoveBackgroundJobsForChat(chat.Id);
        _backgroundJobService.Reschedule();
        _navigationHistory.RemoveChat(chat.Id);
        _dataStore.MarkChatDeleted(chat.Id);
        await _dataStore.DeleteChatFileAsync(chat.Id);
        await _dataStore.SaveAsync();
        RefreshChatList();
        ChatDeleted?.Invoke(chat.Id);

        return true;
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

    [RelayCommand]
    private void ToggleChatPin(Chat? chat)
    {
        if (chat is null) return;

        chat.IsPinned = !chat.IsPinned;
        _dataStore.MarkChatChanged(chat);
        _ = _dataStore.SaveAsync();
        RefreshChatList();
        ProjectsVM.RefreshSelectedProjectChats();
    }

    [ObservableProperty] private Chat? _renamingChat;
    [ObservableProperty] private string _renamingTitle = "";

    /// <summary>
    /// Duplicates <paramref name="chat"/> into an independent chat and opens it.
    /// </summary>
    /// <param name="throughMessageId">
    /// The message the fork was requested from. An assistant message is kept (the branch ends on
    /// that answer); a user message is turned into a composer draft instead, so the branch never
    /// ends on an unanswered question. Null forks the whole transcript.
    /// </param>
    /// <remarks>
    /// The fork prefers a real server-side Copilot session fork, so it continues with the model's
    /// actual working memory; when that is unavailable it carries no session, which makes the
    /// copied transcript replay into its first send instead — see <see cref="ChatForkFactory"/>.
    /// The source chat is never modified either way.
    /// </remarks>
    public async Task<Chat?> ForkChatAsync(Chat? chat, Guid? throughMessageId = null)
    {
        if (chat is null) return null;

        // Every entry point funnels through here — sidebar menu, Ctrl+Shift+D, and the transcript's
        // fork request — and only the menu bindings get AsyncRelayCommand's own re-entrancy guard.
        // Without this, holding Ctrl+Shift+D would spawn a duplicate (and a server-side session
        // fork) per key repeat, and whichever finished first would clear the shared busy indicator.
        if (_isDuplicateInFlight) return null;
        _isDuplicateInFlight = true;

        // Duplicating does disk and (briefly) network work before it can navigate. It is normally
        // fast enough to feel instant, so the busy state is deliberately delayed rather than shown
        // up front — an indicator that flashes for 200ms reads as a glitch, while a silent
        // multi-second wait reads as a freeze. The scope clears the state however this exits.
        using var busy = BeginDuplicatingChat();

        Chat? fork = null;
        try
        {
            // Messages live in a per-chat side file and are unloaded while a chat is inactive, so a
            // fork of a non-active chat would otherwise copy an empty transcript.
            await _dataStore.LoadChatMessagesAsync(chat);

            var plan = ChatForkFactory.CreateFork(chat, chat.Messages, throughMessageId);
            fork = plan.Chat;

            // Prefer a real server-side session fork: the copy then inherits the model's actual
            // working memory, cut at the same point as the copied transcript. When that is not
            // possible the fork keeps a null session id, which is exactly what makes its first send
            // replay the copied transcript instead — see ChatForkFactory.
            var forkedSessionId = await ForkSourceSessionAsync(
                chat,
                plan.SessionForkCutUserTurns,
                fork.Title);
            if (!string.IsNullOrWhiteSpace(forkedSessionId))
            {
                fork.CopilotSessionId = forkedSessionId;
                // The forked session was created under the source's provider configuration, so it
                // carries the same signature; without it the fork would look stale and be recreated.
                fork.SessionProviderSignature = chat.SessionProviderSignature;
            }

            ChatForkFactory.ReconcileTag(fork, _dataStore.Data.ChatTags);
            _dataStore.Data.Chats.Add(fork);
            _dataStore.MarkChatChanged(fork);
            await _dataStore.SaveChatAsync(fork);
            await _dataStore.SaveAsync();

            RefreshChatList();
            ProjectsVM.RefreshSelectedProjectChats();

            var opened = await LoadChatAndShowAsync(fork);
            SelectedNavIndex = 0;

            // Forking from a user turn means "ask this differently", so the prompt comes back as an
            // editable draft. Set after navigation, and only if the fork is what actually opened —
            // otherwise the draft would land in whichever chat is on screen.
            if (opened && plan.ComposerPrefill is { Length: > 0 } draft)
                ChatVM.SetComposerDraft(draft, plan.ComposerReply, plan.ComposerReferencedChats);

            return fork;
        }
        catch (Exception ex)
        {
            // A half-created duplicate must not linger in the sidebar: it would look real until the
            // next restart and then vanish. The source chat is never touched, so dropping the copy
            // returns the app to exactly its pre-duplicate state.
            if (fork is not null)
                _dataStore.Data.Chats.Remove(fork);
            RefreshChatList();
            ProjectsVM.RefreshSelectedProjectChats();

            System.Diagnostics.Debug.WriteLine($"[Lumi] Duplicating chat '{chat.Title}' failed: {ex}");
            return null;
        }
        finally
        {
            _isDuplicateInFlight = false;
        }
    }

    /// <summary>
    /// Guards against overlapping duplicates from any entry point. Not a lock: every caller runs on
    /// the UI thread, so a plain flag is enough and a second request is simply ignored.
    /// </summary>
    private bool _isDuplicateInFlight;

    /// <summary>
    /// Forks the source chat's Copilot session so the duplicate inherits the model's real working
    /// memory. Returns null when that is not possible, which leaves the duplicate on the
    /// transcript-replay path.
    /// </summary>
    /// <remarks>
    /// Any surface that already holds the session lends its live handle: that skips a redundant
    /// resume and, more importantly, keeps the "one holder per server session" invariant that
    /// <see cref="CopilotService.ReleaseSessionAsync"/> depends on. Only when nobody holds it is a
    /// short-lived bare handle resumed instead — which is what lets a chat be duplicated from the
    /// sidebar, without opening it first, and still inherit memory. A session that is mid-recovery
    /// is left strictly alone: resuming a second handle for it would destroy the session the
    /// recovering surface is about to re-adopt, so the duplicate falls back to replay instead.
    /// </remarks>
    private async Task<string?> ForkSourceSessionAsync(
        Chat source,
        int? sessionForkCutUserTurns,
        string? name)
    {
        if (source.CopilotSessionId is not { Length: > 0 } sessionId)
            return null;

        CopilotSession? live = null;
        foreach (var surface in _chatSessionStore.SnapshotSurfaces())
        {
            // Not short-circuited on the first live handle: a "recovering" holder must win however
            // the surfaces happen to be ordered, because borrowing while another surface is
            // mid-recovery is the one outcome that destroys its session.
            switch (surface.GetForkSessionHold(sessionId, out var held))
            {
                case ChatViewModel.ForkSessionHold.Recovering:
                    return null;
                case ChatViewModel.ForkSessionHold.Live:
                    live ??= held;
                    break;
            }
        }

        return await ChatViewModel.ForkSessionAtTurnAsync(
            _copilotService, sessionId, live, sessionForkCutUserTurns, name);
    }

    /// <summary>
    /// True while a duplicate is being prepared AND it has taken long enough to be worth showing.
    /// Drives the chat surface's "Duplicating chat…" indicator.
    /// </summary>
    [ObservableProperty]
    private bool _isDuplicatingChat;

    /// <summary>
    /// How long a duplicate may run before the UI admits it is working. Below this the operation
    /// reads as instant, and an indicator that appears at all would only be a blink.
    /// </summary>
    private static readonly TimeSpan DuplicateBusyDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// How long the indicator stays up once shown, even if the duplicate finishes sooner. Measured
    /// duplicates land around 400-700ms, so without a floor the indicator would appear for a few
    /// dozen milliseconds and read as a flicker rather than as progress.
    /// </summary>
    private static readonly TimeSpan DuplicateBusyMinVisible = TimeSpan.FromMilliseconds(450);

    /// <summary>
    /// Starts a duplicate operation's busy state. The indicator is armed on a timer rather than set
    /// immediately, so fast duplicates never flash it; disposing clears it, honouring the minimum
    /// visible time when it did appear.
    /// </summary>
    private IDisposable BeginDuplicatingChat() => new DuplicateBusyScope(this);

    /// <summary>
    /// Owns one duplicate operation's busy indicator: raises it only if the operation is still
    /// running after <see cref="DuplicateBusyDelay"/>, keeps it up for at least
    /// <see cref="DuplicateBusyMinVisible"/> once raised, and always clears it on disposal.
    /// </summary>
    /// <remarks>
    /// Both timers hop back through <see cref="Dispatcher.UIThread"/> because the scope is disposed
    /// from whatever context the duplicate finished on, while the flag drives bindings.
    /// </remarks>
    private sealed class DuplicateBusyScope : IDisposable
    {
        private readonly MainViewModel _owner;
        private bool _finished;
        private DateTime _shownAtUtc;

        public DuplicateBusyScope(MainViewModel owner)
        {
            _owner = owner;
            _ = ArmAsync();
        }

        private async Task ArmAsync()
        {
            await Task.Delay(DuplicateBusyDelay).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (_finished) return;
                _shownAtUtc = DateTime.UtcNow;
                _owner.IsDuplicatingChat = true;
            });
        }

        public void Dispose()
        {
            if (_finished) return;
            _finished = true;

            var remaining = _shownAtUtc == default
                ? TimeSpan.Zero
                : DuplicateBusyMinVisible - (DateTime.UtcNow - _shownAtUtc);

            if (remaining <= TimeSpan.Zero)
            {
                _owner.IsDuplicatingChat = false;
                return;
            }

            _ = Task.Delay(remaining).ContinueWith(
                _ => Dispatcher.UIThread.Post(() => _owner.IsDuplicatingChat = false),
                TaskScheduler.Default);
        }
    }

    /// <summary>Duplicates a chat from the sidebar.</summary>
    [RelayCommand]
    private async Task DuplicateChat(Chat? chat) => await ForkChatAsync(chat);

    /// <summary>Duplicates the chat currently shown in the main surface (Ctrl+Shift+D).</summary>
    [RelayCommand]
    private async Task DuplicateCurrentChat() => await ForkChatAsync(ChatVM.CurrentChat);

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
            if (idx == 7)
            {
                OpenSettings();
                return;
            }

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

    // The switcher only switches; its actions open Customize → Projects, where projects are set up.

    /// <summary>The switcher's "New project": the Projects section opens straight into an empty editor.</summary>
    [RelayCommand]
    private void NewProjectFromSwitcher()
    {
        SelectedNavIndex = 2;
        ProjectsVM.NewProjectCommand.Execute(null);
    }

    /// <summary>The gear on a project in the switcher's list: opens that project, wherever the list is narrowed.</summary>
    [RelayCommand]
    private void EditProject(Project project)
    {
        SelectedNavIndex = 2;
        ProjectsVM.OpenById(project.Id);
    }

    /// <summary>The switcher's "Manage projects": every project at a glance.</summary>
    [RelayCommand]
    private void ManageProjects()
    {
        SelectedNavIndex = 2;
        ProjectsVM.CloseDetailCommand.Execute(null);
    }

    /// <summary>How many background jobs are running right now; lights the dot on Jobs.</summary>
    public int RunningJobCount => _dataStore.SnapshotBackgroundJobs().Count(static job => job.IsRunning);

    public bool HasRunningJobs => RunningJobCount > 0;

    public string JobsNavTip => Loc.AdaptKeyboardHint(RunningJobCount is var running and > 0
        ? string.Format(Loc.Culture, Loc.NavTip_JobsRunning, running)
        : Loc.NavTip_Jobs);

    private void NotifyRunningJobs()
    {
        OnPropertyChanged(nameof(RunningJobCount));
        OnPropertyChanged(nameof(HasRunningJobs));
        OnPropertyChanged(nameof(JobsNavTip));
    }

    [RelayCommand]
    private void AssignChatToProject(object? parameter)
    {
        // parameter is a two-element array: [Chat, Project]
        if (parameter is object[] args && args.Length == 2 && args[0] is Chat chat && args[1] is Project project)
        {
            if (chat.ProjectId == project.Id)
                return;
            if (IsChatProjectMutationReserved(chat.Id))
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
        if (IsChatProjectMutationReserved(chat.Id)) return;
        chat.ProjectId = null;
        _dataStore.MarkChatChanged(chat);
        NotifyProjectChangedForOpenSurfaces(chat);
        _ = _dataStore.SaveAsync();
        RefreshChatList();
    }

    private bool IsChatProjectMutationReserved(Guid chatId) =>
        IsChatFirstTurnReserved(chatId);

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

    private OnboardingViewModel CreateOnboardingViewModel(bool isReplay)
    {
        var onboarding = new OnboardingViewModel(_dataStore, _copilotService)
        {
            IsReplay = isReplay,
            LoginVM = LoginVM,
            EasyConnect = SettingsVM.EasyConnect,
            Setups = SetupsVM,
        };
        onboarding.OnboardingCompleted += OnOnboardingCompleted;
        onboarding.Dismissed += FinishOnboarding;
        onboarding.ThemeChanged += OnOnboardingThemeChanged;
        onboarding.ApprovalModeChosen += OnOnboardingApprovalModeChosen;
        onboarding.LanguageChangeRequested += OnOnboardingLanguageChangeRequested;
        return onboarding;
    }

    /// <summary>Opens the welcome again from the start. Whatever earlier runs set up stays.</summary>
    public void ReplayWelcome()
    {
        if (!IsOnboarded)
            return;

        var previous = OnboardingVM;
        OnboardingVM = CreateOnboardingViewModel(isReplay: true);
        previous.OnboardingCompleted -= OnOnboardingCompleted;
        previous.Dismissed -= FinishOnboarding;
        previous.ThemeChanged -= OnOnboardingThemeChanged;
        previous.ApprovalModeChosen -= OnOnboardingApprovalModeChosen;
        previous.LanguageChangeRequested -= OnOnboardingLanguageChangeRequested;
        previous.LoginVM = null;
        previous.EasyConnect = null;

        IsOnboarded = false;
    }

    // Through Settings, which applies it to the app, so its switch stays in step.
    private void OnOnboardingThemeChanged(bool isDark) => SettingsVM.IsDarkTheme = isDark;

    // Through Settings too, which saves it and applies it to live chats.
    private void OnOnboardingApprovalModeChosen(ApprovalMode mode) => SettingsVM.DefaultApprovalMode = mode;

    private void OnOnboardingCompleted(OnboardingCompletion completion)
    {
        var settings = _dataStore.Data.Settings;
        UserName = OnboardingVM.UserName.Trim();
        IsDarkTheme = OnboardingVM.IsDarkTheme;
        SettingsVM.UserName = settings.UserName ?? "";
        SettingsVM.UserSexIndex = settings.UserSex switch { "male" => 0, "female" => 1, _ => 2 };
        FinishOnboarding();

        if (!string.IsNullOrWhiteSpace(completion.StarterPrompt))
            StartFirstChat(completion.StarterPrompt, completion.StarterProjectId);
        else
            SelectedNavIndex = 0; // "Start using Lumi" leads to the chats, also from a replay in Settings.
    }

    /// <summary>Back to the app, which picks up the projects, memories and accounts onboarding set up.</summary>
    private void FinishOnboarding()
    {
        IsOnboarded = true;

        // Sync GitHub auth state if user signed in during onboarding
        if (LoginVM.IsAuthenticated)
            _ = SettingsVM.RefreshAuthStatusAsync();

        _chatSessionStore.ApplyToSurfaces(surface =>
        {
            surface.InvalidateProjectSession();
            surface.RefreshCapabilities();
        });
        RefreshFeatureManagementUi();
        ChatVM.RefreshComposerCatalogs();
    }

    /// <summary>
    /// Sends the starter from a new chat (in the starter's project, when it has one). After a replay the
    /// open chat may be a conversation, so the starter must never land in it.
    /// </summary>
    private void StartFirstChat(string prompt, Guid? projectId)
    {
        NewChat();
        // With the new chat still empty, choosing the project only moves it there.
        if (projectId is { } id && _dataStore.Data.Projects.Any(project => project.Id == id))
            SelectedProjectFilter = id;
        if (ChatVM.SelectSuggestionCommand.CanExecute(prompt))
            ChatVM.SelectSuggestionCommand.Execute(prompt);
    }

    /// <summary>Lumi's UI language is fixed at startup, so a new choice is saved and Lumi restarts in it.</summary>
    private void OnOnboardingLanguageChangeRequested(string language)
    {
        var settings = _dataStore.Data.Settings;
        settings.Language = language;
        if (!string.IsNullOrWhiteSpace(OnboardingVM.UserName))
            settings.UserName = OnboardingVM.UserName.Trim();
        _dataStore.Save();
        SettingsVM.RestartAppCommand.Execute(null);
    }

    partial void OnSelectedProjectFilterChanged(Guid? value)
    {
        RefreshChatList();
        ChatVM.ActiveProjectFilterId = value;

        // A reveal moves the filter *to* an already-chosen chat, so the auto-open below would
        // race it and win. The caller opens the chat itself right after.
        if (_isRevealingChat)
            return;

        if (_navigationHistory.IsRestoring)
        {
            if (ChatVM.CurrentChat is null || ChatVM.CurrentChat.Messages.Count == 0)
                SetDraftChatProjectContext(value);
            return;
        }

        // If the current chat already belongs to the target project, keep it.
        if (ChatVM.CurrentChat is not null
            && ChatVM.CurrentChat.Messages.Count > 0
            && ChatVM.CurrentChat.ProjectId == value)
        {
            if (SelectedNavIndex == 0)
                QueueNavigationRecord();
            return;
        }

        // If we're in a new/empty chat (draft), stay in new-chat mode —
        // just update the project assignment without navigating away.
        if (ChatVM.CurrentChat is null || ChatVM.CurrentChat.Messages.Count == 0)
        {
            SetDraftChatProjectContext(value);
            if (SelectedNavIndex == 0)
                QueueNavigationRecord();
            return;
        }

        // Try to open the most recent chat in the new project.
        if (value.HasValue)
        {
            var recent = _dataStore.Data.Chats
                .Where(c => c.ProjectId == value.Value && ChatHasContent(c))
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

    /// <summary>
    /// True when a chat has at least one message, working whether or not the chat's messages
    /// are currently loaded in memory. Inactive chats have their messages unloaded to reclaim
    /// RAM, so this falls back to the persisted count and, for pre-existing chats that predate
    /// that count, to the presence of a stored messages file.
    /// </summary>
    private bool ChatHasContent(Chat chat)
        => chat.Messages.Count > 0 || chat.MessageCount > 0 || _dataStore.HasStoredMessages(chat.Id);

    partial void OnActiveChatIdChanged(Guid? value)
    {
        if (SelectedNavIndex == 0)
            QueueNavigationRecord();
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
