using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Mobile.Layout;
using Lumi.Mobile.Services;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.ViewModels;

/// <summary>
/// What is showing over the chat. The chat surface is always mounted underneath — this app is a
/// chat, and everything else is a page pushed on top of it, exactly like ChatGPT / Claude.
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

/// <summary>A project the user can put the conversation into, straight from the drawer.</summary>
public sealed partial class ProjectPickViewModel : ObservableObject
{
    [ObservableProperty] private bool _isActive;

    public required Guid Id { get; init; }

    public required string Name { get; init; }
}

/// <summary>
/// One entry in the drawer's horizontal "experiences" strip. Mirrors ChatGPT's simplified sidebar,
/// where capabilities sit in a scrolling row above the chat history instead of consuming a tab.
/// </summary>
public sealed class ExperienceViewModel
{
    public required string Name { get; init; }

    public required string Glyph { get; init; }

    public required LibrarySection Section { get; init; }
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
    IAsyncDisposable
{
    private readonly MobileSettingsStore _store;
    private readonly MobileConnectionSettings _settings;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _connectionLifetime = new();
    private long _connectionGeneration;
    private readonly SemaphoreSlim _snapshotRefreshGate = new(1, 1);
    private long _snapshotRefreshVersion;

    private readonly object _transcriptRefreshSync = new();
    private readonly Stack<int> _newerTranscriptCursors = new();
    private TranscriptRefreshRequest? _pendingTranscriptRefresh;
    private TranscriptRefreshRequest? _activeTranscriptRefreshRequest;
    private TranscriptRefreshRequest? _applyingTranscriptRefreshRequest;
    private CancellationTokenSource? _activeTranscriptRefreshCts;
    private Task? _transcriptRefreshLoop;
    private long _transcriptRequestVersion;
    private long _transcriptSurfaceGeneration;
    private bool _hasDeferredNewerActivity;
    private bool _canAdoptDesktopActiveChat = true;

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
        long StatusVersion)
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
    private bool _isConnected;

    /// <summary>
    /// True when the *desktop* reports its own Copilot session is ready. This is a different fact
    /// from <see cref="IsConnected"/> (the phone-to-desktop link) and must never be merged into it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLive))]
    [NotifyPropertyChangedFor(nameof(ShowConnectionBanner))]
    [NotifyPropertyChangedFor(nameof(ConnectionBannerText))]
    private bool _isHostReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectionBanner))]
    private bool _isPaired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionBannerText))]
    private string _hostName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionBannerText))]
    private string? _connectionMessage;
    [ObservableProperty] private MobileLayoutState _layout = MobileLayoutState.From(390, 844);
    [ObservableProperty] private ThemePreference _theme = ThemePreference.System;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private bool _sendWithEnter;

    /// <summary>The page stacked over the conversation, if any.</summary>
    [ObservableProperty] private MobilePage _page = MobilePage.Chat;

    /// <summary>
    /// Navigation drawer state. On compact and medium widths the drawer slides over the chat and
    /// is dismissed by the scrim; at expanded widths it is docked permanently and this is ignored.
    /// </summary>
    [ObservableProperty] private bool _isDrawerOpen;

    /// <summary>User collapsed the docked sidebar to give the conversation the full width.</summary>
    [ObservableProperty] private bool _isSidebarCollapsed;

    public MobileShellViewModel(
        LumiRemoteClient? client = null,
        LumiDiscoveryClient? discovery = null,
        MobileSettingsStore? store = null,
        Action<Action>? post = null)
    {
        _store = store ?? new MobileSettingsStore();
        _settings = _store.Load();
        _post = post ?? (action => Dispatcher.UIThread.Post(action));

        Client = client ?? new LumiRemoteClient(_settings.DeviceId, _settings.DeviceName);
        Discovery = discovery ?? new LumiDiscoveryClient();

        Theme = Enum.TryParse<ThemePreference>(_settings.Theme, ignoreCase: true, out var theme) ? theme : ThemePreference.System;
        SendWithEnter = _settings.SendWithEnter;
        IsSidebarCollapsed = _settings.IsSidebarCollapsed;

        ChatList = new ChatListViewModel(this);
        Chat = new MobileChatViewModel(this);
        Library = new LibraryViewModel(this);
        Connect = new ConnectViewModel(Client, Discovery, OnPairedAsync);

        ChatList.ChatActivated += OnChatActivated;
        ChatList.ChatRemoved += OnChatRemoved;
        Client.StreamFrameReceived += OnFrameReceived;
        Client.StateChanged += OnClientStateChanged;

        // The header shows the chat title once a conversation exists, and the model before that.
        Chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MobileChatViewModel.HasChat)
                or nameof(MobileChatViewModel.Title)
                or nameof(MobileChatViewModel.Model)
                or nameof(MobileChatViewModel.ModelDisplayName))
            {
                OnPropertyChanged(nameof(HeaderTitle));
            }

            if (e.PropertyName is nameof(MobileChatViewModel.IsEmpty))
                OnPropertyChanged(nameof(IsWelcomeVisible));

            if (e.PropertyName is nameof(MobileChatViewModel.HasOpenSheet))
                OnPropertyChanged(nameof(CanGoBack));
        };

        Library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LibraryViewModel.HasOpenSurface))
                OnPropertyChanged(nameof(CanGoBack));
        };

        // Typing into the empty launch surface makes the desktop create a chat; adopt it so the
        // reply streams into the transcript the user is already looking at, and replay anything
        // they configured before the chat existed.
        Chat.ChatCreated += (chatId, blankGeneration) => _post(() =>
        {
            if (!Chat.TryAdoptCreatedChat(chatId, blankGeneration))
                return;

            ChatList.SelectedChatId = chatId;
            _ = Chat.FlushPendingConfigurationAsync();
            _ = RefreshTranscriptAsync();
        });

        Library.CloseRequested += () => Page = MobilePage.Chat;

        if (_settings is { BaseUrl.Length: > 0, Token.Length: > 0 })
        {
            Client.Configure(_settings.BaseUrl, _settings.Token);
            HostName = _settings.HostName;
            IsPaired = true;
        }
    }

    public LumiRemoteClient Client { get; }

    public LumiDiscoveryClient Discovery { get; }

    public ConnectViewModel Connect { get; }

    public ChatListViewModel ChatList { get; }

    public MobileChatViewModel Chat { get; }

    public LibraryViewModel Library { get; }

    /// <summary>Projects the user can put the conversation into, straight from the drawer.</summary>
    public ObservableCollection<ProjectPickViewModel> Projects { get; } = [];

    public bool HasProjects => Projects.Count > 0;

    // ── Chat action sheet (long-press on a drawer row) ────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    private bool _isChatActionsOpen;

    [ObservableProperty] private ChatListItemViewModel? _actionChat;

    public string ActionChatTitle => ActionChat?.Title ?? "";

    public string PinActionLabel => ActionChat?.IsPinned == true ? "Unpin" : "Pin";

    [RelayCommand]
    private void OpenChatActions(ChatListItemViewModel? chat)
    {
        if (chat is null)
            return;

        ActionChat = chat;
        IsChatActionsOpen = true;
    }

    [RelayCommand]
    private void CloseChatActions() => IsChatActionsOpen = false;

    [RelayCommand]
    private async Task PinActionChatAsync()
    {
        if (ActionChat is { } chat)
            await ChatList.TogglePinCommand.ExecuteAsync(chat);

        IsChatActionsOpen = false;
    }

    [RelayCommand]
    private async Task DeleteActionChatAsync()
    {
        if (ActionChat is { } chat)
            await ChatList.DeleteChatCommand.ExecuteAsync(chat);

        IsChatActionsOpen = false;
    }

    partial void OnActionChatChanged(ChatListItemViewModel? value)
    {
        OnPropertyChanged(nameof(ActionChatTitle));
        OnPropertyChanged(nameof(PinActionLabel));
    }

    /// <summary>
    /// Selecting a project makes it the lens you work through: the chat list filters to it, and a
    /// new chat started from here lands inside it. Tapping the active one clears the filter. This
    /// mirrors Lumi desktop and ChatGPT — a project is a workspace, not a tag on one message.
    /// </summary>
    [RelayCommand]
    private void SelectProject(ProjectPickViewModel? project)
    {
        if (project is null)
            return;

        ActiveProjectId = project.IsActive ? null : project.Id;

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

    partial void OnActiveProjectIdChanged(Guid? value)
    {
        ChatList.ProjectFilterId = value;
        OnPropertyChanged(nameof(ActiveProject));
        OnPropertyChanged(nameof(HasActiveProject));
        SyncProjectSelection();
    }

    [RelayCommand]
    private void ClearProject() => ActiveProjectId = null;

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
                : assembly.GetName().Version?.ToString() ?? "unknown";
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
            var part = DateTime.Now.Hour switch
            {
                < 5 => "Still up",
                < 12 => "Good morning",
                < 18 => "Good afternoon",
                _ => "Good evening"
            };

            return string.IsNullOrWhiteSpace(UserName) ? $"{part}." : $"{part}, {UserName}.";
        }
    }

    /// <summary>
    /// Centre of the top bar. The open chat's title matters more than the model once a conversation
    /// exists; before that, the model is the thing the user is most likely to want to change.
    /// </summary>
    public string HeaderTitle => Chat.HasChat && !string.IsNullOrWhiteSpace(Chat.Title)
        ? Chat.Title
        : "Lumi";

    partial void OnUserNameChanged(string value)
    {
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(UserInitial));
        OnPropertyChanged(nameof(Greeting));
    }

    /// <summary>Opens the library on the tapped experience.</summary>
    [RelayCommand]
    private void OpenExperience(ExperienceViewModel? experience)
    {
        if (experience is null)
            return;

        Library.Section = experience.Section;
        Page = MobilePage.Library;
        IsDrawerOpen = false;
    }

    /// <summary>
    /// Search takes over the whole screen. An inline filter field is a desktop habit: on a phone the
    /// keyboard already eats half the display, so anything left around it is wasted.
    /// </summary>
    [RelayCommand]
    private void OpenSearch()
    {
        ChatList.SearchText = "";
        Page = MobilePage.Search;
        IsDrawerOpen = false;
    }

    /// <summary>True only when the phone can actually reach a ready Lumi: link up AND host ready.</summary>
    public bool IsLive => IsConnected && IsHostReady;

    /// <summary>
    /// A paired phone must never fail silently. The chat remains readable while reconnecting, but a
    /// compact banner makes it clear why sending or fresh data is unavailable.
    /// </summary>
    public bool ShowConnectionBanner => IsPaired && !IsLive;

    public string ConnectionBannerText
    {
        get
        {
            if (!IsConnected)
            {
                var host = string.IsNullOrWhiteSpace(HostName) ? "your PC" : HostName;
                return $"Reconnecting to {host}...";
            }

            return string.IsNullOrWhiteSpace(ConnectionMessage)
                ? "Lumi is getting ready..."
                : ConnectionMessage!;
        }
    }

    // ── Navigation ────────────────────────────────────────────────────────────────────────────
    //
    // The chat surface is the app. It is always mounted; Library and Settings are pages pushed
    // over it, and the chat list lives in a drawer rather than a tab. This mirrors ChatGPT, Claude
    // and Gemini: on a phone the conversation deserves the whole screen, and a permanent tab bar
    // would spend ~56dp of it advertising destinations the user visits occasionally.

    public bool IsChatPage => Page == MobilePage.Chat;

    public bool IsLibraryPage => Page == MobilePage.Library;

    public bool IsSettingsPage => Page == MobilePage.Settings;

    public bool IsSearchPage => Page == MobilePage.Search;

    /// <summary>True when a page is stacked over the conversation.</summary>
    public bool HasPageOverlay => Page != MobilePage.Chat;

    /// <summary>
    /// At expanded widths the drawer can dock beside the chat instead of sliding over it. It is
    /// still collapsible: even on a tablet the list costs the conversation a third of its width,
    /// and reading is the dominant activity, so the hamburger stays and the state is remembered.
    /// </summary>
    public bool IsDrawerDocked => Layout.WidthClass == WidthSizeClass.Expanded && !IsSidebarCollapsed;

    /// <summary>Wide enough to dock, whether or not the user has collapsed it.</summary>
    public bool CanDockDrawer => Layout.WidthClass == WidthSizeClass.Expanded;

    /// <summary>The sliding drawer only exists when it is not docked.</summary>
    public bool IsDrawerOverlay => IsDrawerOpen && !IsDrawerDocked;

    /// <summary>Scrim is shown for the sliding drawer only; a docked drawer never dims the chat.</summary>
    public bool ShowScrim => IsDrawerOverlay;

    /// <summary>Docked or slid open — either way the drawer's contents are on screen.</summary>
    public bool IsDrawerVisible => IsDrawerDocked || IsDrawerOpen;

    /// <summary>
    /// Drawer width. Material specifies 320dp for a modal drawer, capped so it never swallows a
    /// small screen — but on a book-posture foldable the hinge wins: docking the drawer anywhere
    /// else would paint the conversation underneath the physical crease.
    /// </summary>
    public double DrawerWidth => IsDrawerDocked && Layout.HingeSize > 0 && Layout.HingePosition > 0
        ? Layout.HingePosition
        : Math.Min(320, Math.Max(260, Layout.Width - 56));

    /// <summary>The hamburger stays on every width — docking is a preference, not a lock.</summary>
    public bool ShowMenuButton => true;

    /// <summary>Physical hinge gap kept empty between a docked drawer and the conversation.</summary>
    public double HingeGapWidth => Layout.HingeSize;

    public bool HasHingeGap => Layout.HingeSize > 0;

    public bool HasCollapsedHingeLead => HasHingeGap && !IsDrawerDocked;

    public double CollapsedHingeLeadWidth => HasCollapsedHingeLead ? Layout.HingePosition : 0;

    public double UsableContentHeight => Layout.HorizontalHingePosition > 0
        ? Layout.HorizontalHingePosition
        : Layout.Height;

    [RelayCommand]
    private void ToggleDrawer()
    {
        // On a wide screen the hamburger collapses the docked sidebar rather than sliding a second
        // copy of it over the top.
        if (CanDockDrawer)
        {
            IsSidebarCollapsed = !IsSidebarCollapsed;
            return;
        }

        IsDrawerOpen = !IsDrawerOpen;
    }

    [RelayCommand]
    private void CloseDrawer() => IsDrawerOpen = false;

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        _settings.IsSidebarCollapsed = value;
        _store.Save(_settings);

        // Collapsing while an overlay copy was open would leave both states stale.
        IsDrawerOpen = false;

        OnPropertyChanged(nameof(IsDrawerDocked));
        OnPropertyChanged(nameof(IsDrawerOverlay));
        OnPropertyChanged(nameof(IsDrawerVisible));
        OnPropertyChanged(nameof(ShowScrim));
        OnPropertyChanged(nameof(DrawerWidth));
        OnPropertyChanged(nameof(HingeGapWidth));
        OnPropertyChanged(nameof(HasHingeGap));
        OnPropertyChanged(nameof(HasCollapsedHingeLead));
        OnPropertyChanged(nameof(CollapsedHingeLeadWidth));
        OnPropertyChanged(nameof(UsableContentHeight));
    }

    [RelayCommand]
    private void ShowPage(string? page)
    {
        if (Enum.TryParse<MobilePage>(page, ignoreCase: true, out var parsed))
            Page = parsed;

        // Opening a page from the drawer must dismiss it, or the page arrives behind the drawer.
        IsDrawerOpen = false;
    }

    /// <summary>Dismisses whatever is stacked over the conversation.</summary>
    [RelayCommand]
    private void GoBack()
    {
        if (IsChatActionsOpen)
        {
            IsChatActionsOpen = false;
            return;
        }

        if (IsDrawerOverlay)
        {
            IsDrawerOpen = false;
            return;
        }

        if (Page == MobilePage.Library && Library.DismissTopmostSurface())
            return;

        if (Page == MobilePage.Chat && Chat.DismissTopmostSheet())
            return;

        if (HasPageOverlay)
            Page = MobilePage.Chat;
    }

    /// <summary>True when the system back gesture has something of ours to dismiss.</summary>
    public bool CanGoBack =>
        IsChatActionsOpen ||
        (Page == MobilePage.Library && Library.HasOpenSurface) ||
        (Page == MobilePage.Chat && Chat.HasOpenSheet) ||
        IsDrawerOverlay ||
        HasPageOverlay;

    partial void OnPageChanged(MobilePage value)
    {
        OnPropertyChanged(nameof(IsChatPage));
        OnPropertyChanged(nameof(IsLibraryPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(IsSearchPage));
        OnPropertyChanged(nameof(HasPageOverlay));
        OnPropertyChanged(nameof(CanGoBack));
    }

    partial void OnIsDrawerOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDrawerOverlay));
        OnPropertyChanged(nameof(IsDrawerVisible));
        OnPropertyChanged(nameof(ShowScrim));
        OnPropertyChanged(nameof(CanGoBack));
    }

    partial void OnLayoutChanged(MobileLayoutState value)
    {
        IsDrawerOpen = false;
        OnPropertyChanged(nameof(CanDockDrawer));
        OnPropertyChanged(nameof(IsDrawerDocked));
        OnPropertyChanged(nameof(IsDrawerOverlay));
        OnPropertyChanged(nameof(IsDrawerVisible));
        OnPropertyChanged(nameof(ShowScrim));
        OnPropertyChanged(nameof(ShowMenuButton));
        OnPropertyChanged(nameof(DrawerWidth));
        OnPropertyChanged(nameof(HingeGapWidth));
        OnPropertyChanged(nameof(HasHingeGap));
        OnPropertyChanged(nameof(HasCollapsedHingeLead));
        OnPropertyChanged(nameof(CollapsedHingeLeadWidth));
        OnPropertyChanged(nameof(UsableContentHeight));
        OnPropertyChanged(nameof(HasWelcomeSpace));
        OnPropertyChanged(nameof(IsWelcomeVisible));
        OnPropertyChanged(nameof(CanGoBack));
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

    partial void OnSendWithEnterChanged(bool value)
    {
        _settings.SendWithEnter = value;
        _store.Save(_settings);
    }

    /// <summary>Called by the view whenever the visible surface changes size or posture.</summary>
    public void UpdateLayout(double width, double height, FoldPosture posture = FoldPosture.Flat,
        double hingeSize = 0, double hingePosition = 0)
    {
        var next = MobileLayoutState.From(width, height, posture, hingeSize, hingePosition);
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

    /// <summary>
    /// Margin for Strata's template-owned sheet title: its normal 20/2/20/10 spacing plus cutouts.
    /// </summary>
    public Thickness SafeAreaSheetTitleMargin => new(
        SafeArea.Left + 20,
        2,
        SafeArea.Right + 20,
        10);

    /// <summary>
    /// Margin for Strata's template-owned content presenter: its normal bottom gap plus the home bar.
    /// Keeping this outside the payload scroller prevents the inset from scrolling away.
    /// </summary>
    public Thickness SafeAreaSheetPresenterMargin => new(
        0,
        0,
        0,
        SafeArea.Bottom + 10);

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
        OnPropertyChanged(nameof(SafeAreaTop));
        OnPropertyChanged(nameof(SafeAreaBottom));
        OnPropertyChanged(nameof(SafeAreaSides));
        OnPropertyChanged(nameof(SafeAreaSheetTitleMargin));
        OnPropertyChanged(nameof(SafeAreaSheetPresenterMargin));
    }

    /// <summary>Reconnects using stored credentials, then starts the live stream.</summary>
    public async Task StartAsync()
    {
        if (!IsPaired)
            return;

        await Client.StartEventStreamAsync();
    }

    private async Task OnPairedAsync(string baseUrl, string hostName)
    {
        await Client.StopEventStreamAsync();
        BeginConnectionGeneration();
        Client.Configure(baseUrl, Client.Token);
        ResetHostScopedState();
        _settings.BaseUrl = baseUrl;
        _settings.Token = Client.Token ?? "";
        _settings.HostName = hostName;
        _store.Save(_settings);

        HostName = hostName;
        _canAdoptDesktopActiveChat = true;
        IsPaired = true;

        await Client.StartEventStreamAsync();
    }

    private void ResetHostScopedState()
    {
        _canAdoptDesktopActiveChat = true;
        ActiveProjectId = null;
        ResetTranscriptNavigation();
        Chat.ResetHostState();
        ChatList.SelectedChatId = Guid.Empty;
        ChatList.Apply([]);
        ChatList.SearchText = "";
        ApplyLibrary(new RemoteLibrary(), reconcileSelections: false);
        Library.ResetHostState();
        ActionChat = null;
        IsChatActionsOpen = false;
        Page = MobilePage.Chat;
        IsDrawerOpen = false;
    }

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
                    ConnectionMessage = result.Error ?? "Lumi could not revoke this phone. Try again while the PC is online.";
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                ConnectionMessage = "Lumi could not revoke this phone. Try again while the PC is online.";
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

        _settings.BaseUrl = "";
        _settings.Token = "";
        _settings.HostName = "";
        _store.Save(_settings);

        Client.Configure("", null);
        IsPaired = false;
        IsConnected = false;
        IsHostReady = false;
        HostName = "";
        ResetHostScopedState();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await RefreshSnapshotAsync();

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
        try
        {
            snapshot = await Client.GetSnapshotAsync(request.Token);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            return;
        }
        if (snapshot is null)
            return;

            PostForConnection(generation, () =>
            {
                if (requestVersion == Volatile.Read(ref _snapshotRefreshVersion))
                    ApplySnapshot(snapshot, SnapshotSource.ManualRefresh);
            });
        }
        finally
        {
            _snapshotRefreshGate.Release();
        }
    }

    private void ApplySnapshot(RemoteSnapshot snapshot, SnapshotSource source)
    {
        var shouldAdoptDesktopChat =
            _canAdoptDesktopActiveChat &&
            !Chat.HasChat &&
            !Chat.HasStagedBlankState;
        var didAdoptDesktopChat = false;

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

        UserName = snapshot.Settings.UserName;

        if (string.IsNullOrWhiteSpace(ChatList.SearchText) && ChatList.ProjectFilterId is null)
            ChatList.Apply(snapshot.Chats);
        else
            _ = ChatList.RefreshFromServerAsync();
        ApplyLibrary(snapshot.Library, reconcileSelections: false);
        Chat.ApplyCatalogs(snapshot.Settings);

        // Adopt the desktop's active chat on first connect so the phone opens where the PC left off.
        if (shouldAdoptDesktopChat &&
            snapshot.ActiveChatId is { } activeChatId &&
            activeChatId != Guid.Empty)
        {
            var activeChat = snapshot.ActiveChat
                ?? snapshot.Chats.Groups
                    .SelectMany(group => group.Chats)
                    .FirstOrDefault(chat => chat.Id == activeChatId);

            ResetTranscriptNavigation();
            Chat.Reset(activeChatId, activeChat?.Title ?? "Chat", activeChat?.LastModelUsed);
            Chat.IsLoading = true;
            ChatList.SelectedChatId = activeChatId;
            didAdoptDesktopChat = true;
        }

        if (source == SnapshotSource.Bootstrap)
            BootstrapSnapshotCount++;

        if (Chat.ChatId != Guid.Empty
            && (source == SnapshotSource.Bootstrap
                || (source == SnapshotSource.ManualRefresh && didAdoptDesktopChat)))
        {
            _ = RefreshTranscriptAsync();
        }
    }

    private void OnChatActivated(Guid chatId, string title, string? model)
    {
        // From this point on the phone owns navigation. A delayed/reconnect snapshot must not jump
        // away from either the selected chat or a deliberately blank New Chat surface.
        _canAdoptDesktopActiveChat = false;
        ResetTranscriptNavigation();
        Chat.Reset(chatId, title, model);

        // Picking a chat is the drawer's whole purpose, so get out of the way and show it.
        Page = MobilePage.Chat;
        IsDrawerOpen = false;

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

            return;
        }

        Chat.IsLoading = true;
        _ = RefreshTranscriptAsync();
    }

    private void OnChatRemoved(Guid chatId)
    {
        if (Chat.ChatId != chatId)
            return;

        ResetTranscriptNavigation();
        ChatList.SelectedChatId = Guid.Empty;
        Chat.Reset(Guid.Empty, "New chat");
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
                Chat.StatusVersion);

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
                            ? RemoteProtocol.TranscriptWindowRawMessageLimit
                            : RemoteProtocol.InitialTranscriptWindowRawMessageLimit,
                        requestCts.Token)
                    .ConfigureAwait(false);

                if (transcript is null)
                    error = Client.StateMessage ?? "Could not load this activity from your PC.";
                else if (transcript.ChatId != request.ChatId)
                {
                    transcript = null;
                    error = "Lumi returned activity for a different chat.";
                }
            }
            catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
            {
                wasCancelled = true;
            }
            catch (Exception ex)
            {
                error = "Could not load this activity from your PC.";
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
                try
                {
                    if (!IsCurrentTranscriptRequest(request))
                        return;

                    Chat.IsLoading = false;
                    if (transcript is null)
                    {
                        Chat.TranscriptErrorText = error;
                        ApplyDeferredNewerActivity(request);
                        return;
                    }

                    ApplyTranscriptNavigation(request, transcript);
                    Chat.ApplyTranscript(transcript, request.StatusVersion);
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

                if (queueTrailingLatestRefresh)
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
            ConnectionMessage = message;

            switch (state)
            {
                case RemoteLinkState.Connected:
                    IsConnected = true;
                    break;
                case RemoteLinkState.Unauthorized:
                    ClearPairedState();
                    ConnectionMessage = message;
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
                    Post(() =>
                    {
                        foreach (var removedChatId in page.RemovedChatIds)
                            OnChatRemoved(removedChatId);
                        _ = ChatList.RefreshFromServerAsync();
                    });
                }
                return;
            }

            case RemoteProtocol.Events.Library:
            {
                if (TryParse(frame.Data, RemoteJsonContext.Default.RemoteLibrary) is { } library)
                {
                    Post(() =>
                    {
                        ApplyLibrary(library);
                        _ = RefreshSnapshotAsync();
                    });
                }
                return;
            }

            case RemoteProtocol.Events.ChatStatus:
            {
                if (TryParse(frame.Data, RemoteJsonContext.Default.RemoteChatStatus) is { } status)
                    Post(() => Chat.ApplyStatus(status));
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

                        // A delta for a row we have never seen means the transcript shape changed;
                        // pull a fresh projection instead of guessing.
                        if (!Chat.ApplyDelta(delta) && delta.ChatId == Chat.ChatId)
                            QueueTranscriptRefresh();
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

    public async Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command)
    {
        var generation = Volatile.Read(ref _connectionGeneration);
        using var request = CreateConnectionRequest();
        RemoteCommandResult result;
        try
        {
            result = await Client.SendCommandAsync(command, request.Token);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            result = new RemoteCommandResult { Error = "The PC connection changed before this action completed." };
        }

        if (!result.Ok && result.Error is { Length: > 0 } error)
            PostForConnection(generation, () => ConnectionMessage = error);

        return result;
    }

    public async Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content)
    {
        using var request = CreateConnectionRequest();
        try
        {
            return await Client.UploadAsync(fileName, content, request.Token);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            return new RemoteUploadResponse { Error = "The PC connection changed during the upload." };
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
        await Client.DisposeAsync();
        await _connectionLifetime.CancelAsync();
        _connectionLifetime.Dispose();
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

    private void PostForConnection(long generation, Action action) =>
        _post(() =>
        {
            if (generation == Volatile.Read(ref _connectionGeneration))
                action();
        });
}
