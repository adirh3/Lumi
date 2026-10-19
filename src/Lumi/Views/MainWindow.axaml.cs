using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using StrataTheme;
using StrataTheme.Animation;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class MainWindow : Window
{
    private const double DefaultSidebarWidth = 280;
    private const double MinSidebarWidth = 240;
    private const double MaxSidebarWidth = 420;
    private const double BaseTitleBarHeight = 38;
    private static readonly TimeSpan ChatListDoubleClickThreshold = TimeSpan.FromMilliseconds(500);
    private const double ChatListDoubleClickMaxDistance = 8;
    private static readonly AttachedProperty<bool> ChatListHandlersAttachedProperty =
        AvaloniaProperty.RegisterAttached<MainWindow, ListBox, bool>("ChatListHandlersAttached");

    // Pill positions, in order. Ctrl+1..5 follow the same order.
    internal const int NavChatButton = 0;
    internal const int NavLibraryButton = 1;
    internal const int NavJobsButton = 2;
    internal const int NavCustomizeButton = 3;
    internal const int NavSettingsButton = 4;

    // Fixed sizes from the nav styles (Button.nav-icon, Border.nav-pill, NavRow), used to decide
    // whether the active place's name fits before laying it out.
    private const double NavIconButtonWidth = 31;
    private const double NavRowSpacing = 3;
    private const double NavPillPadding = 4;
    private const double NavPillBorder = 1;
    private const double NavLabelMargin = 9;
    private const double NavLabelMaxWidth = 96;

    private Panel? _onboardingPanel;
    private ContentControl? _onboardingHost;
    private DockPanel? _mainPanel;
    private Border? _acrylicFallback;
    private Border? _windowContentRoot;
    private Control?[] _pages = [];
    private Panel?[] _sidebarPanels = [];
    private Border? _capabilityDropOverlay;

    /// <summary>Skills, Lumis and MCP Servers: the pages that accept a dropped capability to import.</summary>
    private static readonly int[] CapabilityPageIndices = [3, 4, 6];
    private Button?[] _navButtons = [];
    private Button?[] _railNavButtons = [];
    private Panel? _renameOverlay;
    private TextBox? _renameTextBox;
    private Panel? _projectSwitcherRoot;
    private Button? _projectSwitchButton;
    private Border? _projectSwitchRevealHost;
    private StackPanel? _projectSwitchPanel;
    private TextBox? _projectFilterSearchBox;
    private StackPanel? _projectFilterResults;
    private TextBlock? _projectSwitchTitleText;
    private Border? _projectSwitchBadge;
    private PathIcon? _projectSwitchAllIcon;
    private TextBlock? _projectSwitchInitial;
    private TextBlock? _projectSwitchMetaText;
    private TextBlock? _projectFilterMoreText;
    private Border? _unreadInboxRoot;
    private Button? _unreadInboxToggle;
    private Border? _unreadRevealHost;
    private Border? _unreadPanel;
    private ScrollViewer? _chatListScroller;
    private readonly List<(Project Project, PropertyChangedEventHandler Handler)> _projectFilterHandlers = [];
    private int _projectSwitcherRefreshQueued;
    private int _chatListSyncQueued;
    private bool _revealAfterChatListSync;
    private ChatWorkspaceView? _chatWorkspace;
    private ChatView? _chatView;
    private ContentControl? _jobsHost;
    private ContentControl? _projectsHost;
    private ContentControl? _skillsHost;
    private ContentControl? _agentsHost;
    private ContentControl? _memoriesHost;
    private ContentControl? _mcpServersHost;
    private ContentControl? _discoverHost;
    private ContentControl? _settingsHost;
    private SettingsView? _settingsView;
    private ContentControl? _libraryHost;
    private Border? _sidebarBorder;
    private Border? _contentArea;
    private Thumb? _sidebarResizeThumb;
    private Border? _navPill;
    private Border? _navPillHost;
    private Panel? _sidebarNav;
    private TextBlock?[] _navLabels = [];
    private CancellationTokenSource? _sidebarAnimCts;
    private CancellationTokenSource? _navHoverIntentCts;
    private CancellationTokenSource? _projectSwitcherDrawerCts;
    private CancellationTokenSource? _unreadDrawerCts;
    private bool _suppressSelectionSync;
    private bool _isProjectChatListRevealArmed;
    private bool _isProjectChatListRevealQueued;
    private bool _isProjectSwitcherOpen;
    private bool _isUnreadPanelOpen;
    private int _chatListRevealVersion;
    private int _currentShellIndex = -1;
    private int _currentSidebarIndex = -1;
    private int _activeNavIndex = -1;
    private int _hoveredNavIndex = -1;
    private int _pendingNavHoverIndex = -1;
    private bool _navHoverInitPending;
    private double _expandedSidebarWidth = DefaultSidebarWidth;
    private Guid? _lastChatListClickChatId;
    private DateTimeOffset _lastChatListClickAt;
    private Point _lastChatListClickPosition;
    private sealed record ProjectFilterCandidate(Project Project, int ChatCount, DateTimeOffset? LastActivity, double SearchScore);

    private MainViewModel? _wiredVm;
    private const int NavHoverIntentDelayMs = 85;
    // How far the pointer must travel after a switch before the bar's tips come back.
    private const double NavTipResumeDistance = 12;
    private Point? _navTipsPausedAt;

    public bool IsPrimaryWindow { get; set; } = true;
    public int SecondaryWindowCascadeIndex { get; set; }
    public PixelPoint? SecondaryWindowAnchorPosition { get; set; }

    private void OnDevTunnelInstallDialogPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == StrataDialog.IsDialogOpenProperty && sender is StrataDialog { IsDialogOpen: true } dialog)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (dialog.IsDialogOpen)
                    this.FindControl<Button>("DevTunnelInstallCancelButton")?.Focus();
            }, DispatcherPriority.Loaded);
        }
    }

    public MainWindow()
    {
        InitializeComponent();
#if !WINDOWS
        // Native controls live outside UiScaleHost; BrowserView supplies transformed physical bounds.
        var scaledContent = Content as Control;
        Content = null;
        var nativeWebViewLayer = new Canvas { Name = "NativeWebViewLayer", ClipToBounds = true };
        var nativeHostRoot = new Grid { Name = "NativeWebViewHostRoot" };
        if (scaledContent is not null)
            nativeHostRoot.Children.Add(scaledContent);
        nativeHostRoot.Children.Add(nativeWebViewLayer);
        Content = nativeHostRoot;
        BrowserService.RegisterHostLayer(this, nativeWebViewLayer);
#endif
        WindowChromeInterop.EnableNativeMinMaxAnimations(this);
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = BaseTitleBarHeight;

#if DEBUG
        try
        {
            var exeDir = AppContext.BaseDirectory;
            var repoRoot = FindGitRoot(exeDir);
            if (repoRoot is not null)
            {
                var dirName = Path.GetFileName(repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (dirName is not null && dirName.Contains("-wt-"))
                    Title = dirName;
            }
        }
        catch { /* best-effort debug title */ }
#endif

        // Force transparent background after theme styles are applied
        Background = Avalonia.Media.Brushes.Transparent;
        TransparencyBackgroundFallback = Avalonia.Media.Brushes.Transparent;

        // Give the window an icon on platforms that don't get one from the executable (Linux taskbar,
        // macOS window/menu). No-op on Windows so its embedded .ico stays authoritative.
        Services.AppIcon.ApplyWindowIcon(this);

        // Watch for window state changes that affect chrome/layout.
        this.PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
                OnWindowStateChanged();
            else if (e.Property == TopLevel.ActualTransparencyLevelProperty)
                UpdateTransparencyFallbackOpacity();
        };

        Opened += (_, _) =>
        {
            if (IsPrimaryWindow)
                RestoreWindowBounds();
            else
                PlaceSecondaryWindow();
            UpdateTransparencyFallbackOpacity();
            ApplyWindowContentPaddingForState();
        };
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        DisposeCancellationTokenSource(ref _sidebarAnimCts);
        DisposeCancellationTokenSource(ref _navHoverIntentCts);
        DisposeCancellationTokenSource(ref _titleAnimCts);
        DisposeCancellationTokenSource(ref _chatListRevealCts);
        DisposeCancellationTokenSource(ref _projectSwitcherDrawerCts);
        DisposeCancellationTokenSource(ref _unreadDrawerCts);
        _chatWorkspace?.Dispose();
    }

    private static CancellationTokenSource ReplaceCancellationTokenSource(ref CancellationTokenSource? source)
    {
        DisposeCancellationTokenSource(ref source);
        source = new CancellationTokenSource();
        return source;
    }

#if DEBUG
    private static string? FindGitRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
#endif

    private static void DisposeCancellationTokenSource(ref CancellationTokenSource? source)
    {
        var previous = source;
        source = null;
        if (previous is null)
            return;

        try { previous.Cancel(); }
        catch (ObjectDisposedException) { }
        previous.Dispose();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _onboardingPanel = this.FindControl<Panel>("OnboardingPanel");
        _onboardingHost = this.FindControl<ContentControl>("OnboardingHost");
        _mainPanel = this.FindControl<DockPanel>("MainPanel");
        _acrylicFallback = this.FindControl<Border>("AcrylicFallback");
        _windowContentRoot = this.FindControl<Border>("WindowContentRoot");
        _sidebarBorder = this.FindControl<Border>("SidebarBorder");
        _contentArea = this.FindControl<Border>("ContentArea");
        _sidebarResizeThumb = this.FindControl<Thumb>("SidebarResizeThumb");
        _navPill = this.FindControl<Border>("NavPill");
        _navPillHost = this.FindControl<Border>("NavPillHost");
        _sidebarNav = this.FindControl<Panel>("SidebarNav");

        _pages =
        [
            this.FindControl<Control>("ChatContentGrid"),      // 0 = Chat workspace
            this.FindControl<Control>("PageJobs"),             // 1
            this.FindControl<Control>("PageProjects"),         // 2
            this.FindControl<Control>("PageSkills"),           // 3
            this.FindControl<Control>("PageAgents"),           // 4
            this.FindControl<Control>("PageMemories"),         // 5
            this.FindControl<Control>("PageMcpServers"),       // 6
            this.FindControl<Control>("PageSettings"),         // 7
            this.FindControl<Control>("PageLibrary"),          // 8
            this.FindControl<Control>("PageDiscover"),         // 9 = Customize → Discover
        ];

        _sidebarPanels =
        [
            this.FindControl<Panel>("SidebarChat"),            // 0
            this.FindControl<Panel>("SidebarJobs"),            // 1
            this.FindControl<Panel>("SidebarProjects"),        // 2
            this.FindControl<Panel>("SidebarSkills"),          // 3
            this.FindControl<Panel>("SidebarAgents"),          // 4
            null,                                              // 5 — Memories keeps the Settings sidebar (SidebarForPage)
            this.FindControl<Panel>("SidebarMcpServers"),      // 6
            this.FindControl<Panel>("SidebarSettings"),        // 7
            this.FindControl<Panel>("SidebarLibrary"),         // 8
            this.FindControl<Panel>("SidebarDiscover"),        // 9
        ];

        // Indexed by position in the nav pill, which no longer matches page indices: the Customize
        // sections (Projects among them) share one button and Memories lights Settings (see NavButtonForPage).
        _navButtons =
        [
            this.FindControl<Button>("NavChat"),
            this.FindControl<Button>("NavLibrary"),
            this.FindControl<Button>("NavJobs"),
            this.FindControl<Button>("NavCustomize"),
            this.FindControl<Button>("NavSettings"),
        ];

        _railNavButtons =
        [
            this.FindControl<Button>("RailChat"),
            this.FindControl<Button>("RailLibrary"),
            this.FindControl<Button>("RailJobs"),
            this.FindControl<Button>("RailCustomize"),
            this.FindControl<Button>("RailSettings"),
        ];

        ApplyAgentAutomationLandmarks();

        _capabilityDropOverlay = this.FindControl<Border>("CapabilityDropOverlay");
        foreach (var index in CapabilityPageIndices)
        {
            WireCapabilityDropTarget(_pages[index]);
            WireCapabilityDropTarget(_sidebarPanels[index]);
        }

        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        WireNavHoverEvents();
        Dispatcher.UIThread.Post(InitializeNavVisuals, DispatcherPriority.Loaded);

        _renameOverlay = this.FindControl<Panel>("RenameOverlay");
        _renameTextBox = this.FindControl<TextBox>("RenameTextBox");
        _projectSwitcherRoot = this.FindControl<Panel>("ProjectSwitcherRoot");
        _projectSwitchButton = this.FindControl<Button>("ProjectSwitchButton");
        _projectSwitchRevealHost = this.FindControl<Border>("ProjectSwitchRevealHost");
        _projectSwitchPanel = this.FindControl<StackPanel>("ProjectSwitchPanel");
        _projectFilterSearchBox = this.FindControl<TextBox>("ProjectFilterSearchBox");
        _projectFilterResults = this.FindControl<StackPanel>("ProjectFilterResults");
        _projectSwitchTitleText = this.FindControl<TextBlock>("ProjectSwitchTitleText");
        _projectSwitchBadge = this.FindControl<Border>("ProjectSwitchBadge");
        _projectSwitchAllIcon = this.FindControl<PathIcon>("ProjectSwitchAllIcon");
        _projectSwitchInitial = this.FindControl<TextBlock>("ProjectSwitchInitial");
        _projectSwitchMetaText = this.FindControl<TextBlock>("ProjectSwitchMetaText");
        _projectFilterMoreText = this.FindControl<TextBlock>("ProjectFilterMoreText");
        _unreadInboxRoot = this.FindControl<Border>("UnreadInboxRoot");
        _unreadInboxToggle = this.FindControl<Button>("UnreadInboxToggle");
        _unreadRevealHost = this.FindControl<Border>("UnreadRevealHost");
        _unreadPanel = this.FindControl<Border>("UnreadPanel");

        if (_projectSwitchButton is not null)
            _projectSwitchButton.Click += (_, _) => ToggleProjectSwitcher();
        if (_projectFilterSearchBox is not null)
        {
            _projectFilterSearchBox.TextChanged += (_, _) =>
            {
                if (DataContext is MainViewModel vm)
                    RefreshProjectSwitcher(vm);
            };
            _projectFilterSearchBox.KeyDown += OnProjectFilterSearchKeyDown;
        }

        _chatListScroller = this.FindControl<ScrollViewer>("ChatListScroller");
        if (_chatListScroller is not null)
            _chatListScroller.ScrollChanged += OnChatListScrollChanged;
        this.FindControl<ItemsControl>("ChatGroupsHost")?.AddHandler(
            RequestBringIntoViewEvent, OnChatListBringIntoView);

        _chatWorkspace = this.FindControl<ChatWorkspaceView>("ChatContentGrid");
        _chatView = _chatWorkspace?.ChatView;

        // Set OS-specific shortcut label
        var shortcutLabel = this.FindControl<TextBlock>("SearchButtonShortcut");
        if (shortcutLabel is not null && RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            shortcutLabel.Text = "⌘K";

        _jobsHost = this.FindControl<ContentControl>("PageJobsHost");
        _projectsHost = this.FindControl<ContentControl>("PageProjectsHost");
        _skillsHost = this.FindControl<ContentControl>("PageSkillsHost");
        _agentsHost = this.FindControl<ContentControl>("PageAgentsHost");
        _memoriesHost = this.FindControl<ContentControl>("PageMemoriesHost");
        _mcpServersHost = this.FindControl<ContentControl>("PageMcpServersHost");
        _settingsHost = this.FindControl<ContentControl>("PageSettingsHost");
        _libraryHost = this.FindControl<ContentControl>("PageLibraryHost");
        _discoverHost = this.FindControl<ContentControl>("PageDiscoverHost");

        if (_sidebarResizeThumb is not null)
        {
            _sidebarResizeThumb.DragDelta += OnSidebarResizeThumbDragDelta;
            _sidebarResizeThumb.DragCompleted += OnSidebarResizeThumbDragCompleted;
        }
    }

    private void ApplyAgentAutomationLandmarks()
    {
        AutomationProperties.SetName(this, "Lumi main window");
        AutomationProperties.SetHelpText(this,
            "Agent map: navigation indices are Chat=0, Jobs=1, Projects=2, Skills=3, Agents=4, Memories=5, MCP Servers=6, Settings=7, Library=8, Discover=9. The nav pill holds Chat, Library, Jobs, Customize and Settings. Skills, Agents, MCP Servers and Discover are sections of Customize; Memories is a Settings page; Projects open from the chat sidebar's project switcher.");

        var navNames = new[]
        {
            "NavChat - navigation index 0 - Chat",
            "NavLibrary - navigation index 8 - Library",
            "NavJobs - navigation index 1 - Background Jobs",
            "NavCustomize - Customize (Discover 9, Agents 4, Skills 3, MCP Servers 6)",
            "NavSettings - navigation index 7 - Settings (Memories 5 is a Settings page)",
        };
        for (var i = 0; i < _navButtons.Length && i < navNames.Length; i++)
        {
            if (_navButtons[i] is not { } button)
                continue;

            AutomationProperties.SetName(button, navNames[i]);
            AutomationProperties.SetHelpText(button, "Use this stable navigation landmark when driving Lumi with UI automation.");
        }

        var pageNames = new (string ControlName, string Name)[]
        {
            ("ChatContentGrid", "Page 0 Chat content grid"),
            ("PageChat", "Page 0 Chat view"),
            ("PageJobs", "Page 1 Background Jobs"),
            ("PageProjects", "Page 2 Projects"),
            ("PageSkills", "Page 3 Skills"),
            ("PageAgents", "Page 4 Agents"),
            ("PageMemories", "Page 5 Memories"),
            ("PageMcpServers", "Page 6 MCP Servers"),
            ("PageSettings", "Page 7 Settings"),
            ("PageLibrary", "Page 8 Library"),
            ("PageDiscover", "Page 9 Customize Discover"),
            ("AgentDebugMap", "Debug-only agent map and fixture launcher"),
        };
        foreach (var (controlName, name) in pageNames)
        {
            if (this.FindControl<Control>(controlName) is { } control)
            {
                AutomationProperties.SetName(control, name);
                AutomationProperties.SetHelpText(control, "Stable Lumi page landmark for coding agents and MCP diagnostics.");
            }
        }
    }

    // ── Drop a SKILL.md, Lumi pack or MCP config onto Skills / Lumis / MCP Servers to import it ──

    private void WireCapabilityDropTarget(Control? target)
    {
        if (target is null)
            return;

        DragDrop.SetAllowDrop(target, true);
        target.AddHandler(DragDrop.DragEnterEvent, OnCapabilityDragOver);
        target.AddHandler(DragDrop.DragOverEvent, OnCapabilityDragOver);
        target.AddHandler(DragDrop.DragLeaveEvent, OnCapabilityDragLeave);
        target.AddHandler(DragDrop.DropEvent, OnCapabilityDrop);
    }

    private bool CanImportDrop(DragEventArgs e)
        => DataContext is MainViewModel { ImportVM.IsOpen: false } vm
           && CapabilityPageIndices.Contains(vm.SelectedNavIndex)
           && CapabilityDropSupport.CanAccept(e);

    private void OnCapabilityDragOver(object? sender, DragEventArgs e)
    {
        var accepted = CanImportDrop(e);
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        if (_capabilityDropOverlay is not null)
            _capabilityDropOverlay.IsVisible = accepted;
    }

    private void OnCapabilityDragLeave(object? sender, DragEventArgs e)
    {
        if (_capabilityDropOverlay is not null && sender is Control target && !CapabilityDropSupport.IsInside(target, e))
            _capabilityDropOverlay.IsVisible = false;
    }

    private async void OnCapabilityDrop(object? sender, DragEventArgs e)
    {
        if (_capabilityDropOverlay is not null)
            _capabilityDropOverlay.IsVisible = false;
        if (!CanImportDrop(e) || DataContext is not MainViewModel vm)
            return;

        e.Handled = true;
        if (CapabilityDropSupport.GetItem(e) is { } item)
            await vm.ImportVM.OpenWithStorageItemAsync(item);
        else if (CapabilityDropSupport.GetText(e) is { } text)
            vm.ImportVM.OpenWithText(text, Loc.Import_FromDrop);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Shutdown has already disposed services and must not leave a hidden, unusable app.
        if (e.CloseReason is not (WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
            && IsPrimaryWindow && DataContext is MainViewModel vm && vm.SettingsVM.MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (IsPrimaryWindow)
        {
            foreach (var chatWindow in GetDesktopWindows().OfType<ChatWindow>().ToList())
                chatWindow.Close();
        }

        CaptureBoundsToSettings();
        base.OnClosing(e);
    }

#if !WINDOWS
    protected override void OnClosed(EventArgs e)
    {
        // Closing to the tray is cancelled in OnClosing and must keep its host registered.
        BrowserService.UnregisterHostLayer(this);
        base.OnClosed(e);
    }
#endif

    protected virtual IReadOnlyList<Window> GetDesktopWindows()
    {
        return Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows
            : [];
    }

    private void RestoreWindowBounds()
    {
        if (DataContext is not MainViewModel vm) return;
        var settings = vm.DataStore.Data.Settings;

        const double defaultWidth = 1320;
        const double defaultHeight = 860;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            Width = defaultWidth;
            Height = defaultHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var scaling = screen.Scaling;
        if (scaling <= 0)
            scaling = 1.0;

        var workArea = screen.WorkingArea;
        var maxW = Math.Max(1.0, workArea.Width / scaling);
        var maxH = Math.Max(1.0, workArea.Height / scaling);

        var w = settings.WindowWidth ?? defaultWidth;
        var h = settings.WindowHeight ?? defaultHeight;

        // Clamp to screen working area; on small displays max can be below MinWidth/MinHeight.
        var minW = Math.Min(MinWidth, maxW);
        var minH = Math.Min(MinHeight, maxH);
        w = Math.Clamp(w, minW, maxW);
        h = Math.Clamp(h, minH, maxH);

        Width = w;
        Height = h;

        if (settings.WindowLeft.HasValue && settings.WindowTop.HasValue)
        {
            var left = settings.WindowLeft.Value;
            var top = settings.WindowTop.Value;

            // Ensure at least 100px of the window is visible on any screen
            bool isVisible = false;
            foreach (var s in Screens.All)
            {
                var wa = s.WorkingArea;
                var waLeft = wa.X / s.Scaling;
                var waTop = wa.Y / s.Scaling;
                var waRight = waLeft + wa.Width / s.Scaling;
                var waBottom = waTop + wa.Height / s.Scaling;

                if (left + 100 > waLeft && left < waRight - 50 &&
                    top + 50 > waTop && top < waBottom - 50)
                {
                    isVisible = true;
                    break;
                }
            }

            if (isVisible)
            {
                Position = new PixelPoint((int)(left * scaling), (int)(top * scaling));
            }
            else
            {
                // Saved position is off-screen, center on current screen
                var cx = workArea.X + (workArea.Width - (int)(w * scaling)) / 2;
                var cy = workArea.Y + (workArea.Height - (int)(h * scaling)) / 2;
                Position = new PixelPoint(cx, cy);
            }
        }
        else
        {
            // No saved position — center on screen
            var cx = workArea.X + (workArea.Width - (int)(w * scaling)) / 2;
            var cy = workArea.Y + (workArea.Height - (int)(h * scaling)) / 2;
            Position = new PixelPoint(cx, cy);
        }

        if (settings.IsMaximized)
            WindowState = WindowState.Maximized;
    }

    private void PlaceSecondaryWindow()
    {
        if (DataContext is not MainViewModel vm) return;
        var settings = vm.DataStore.Data.Settings;

        const double defaultWidth = 1320;
        const double defaultHeight = 860;
        const int cascadeStep = 28;
        const int maxCascadeSteps = 8;

        var screen = (SecondaryWindowAnchorPosition is { } anchor ? Screens.ScreenFromPoint(anchor) : null)
            ?? (Owner is Window owner ? Screens.ScreenFromWindow(owner) : null)
            ?? Screens.ScreenFromWindow(this)
            ?? Screens.Primary;
        if (screen is null)
        {
            Width = settings.WindowWidth ?? defaultWidth;
            Height = settings.WindowHeight ?? defaultHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        var workArea = screen.WorkingArea;
        var maxW = Math.Max(1.0, workArea.Width / scaling);
        var maxH = Math.Max(1.0, workArea.Height / scaling);
        var width = Math.Clamp(settings.WindowWidth ?? defaultWidth, Math.Min(MinWidth, maxW), maxW);
        var height = Math.Clamp(settings.WindowHeight ?? defaultHeight, Math.Min(MinHeight, maxH), maxH);
        Width = width;
        Height = height;

        var offset = cascadeStep * Math.Max(1, SecondaryWindowCascadeIndex % maxCascadeSteps);
        int left;
        int top;

        if (SecondaryWindowAnchorPosition is { } anchorPosition)
        {
            left = anchorPosition.X + (int)(offset * scaling);
            top = anchorPosition.Y + (int)(offset * scaling);
        }
        else if (Owner is Window { IsVisible: true } ownerWindow)
        {
            left = ownerWindow.Position.X + (int)(offset * scaling);
            top = ownerWindow.Position.Y + (int)(offset * scaling);
        }
        else
        {
            left = workArea.X + (workArea.Width - (int)(width * scaling)) / 2 + (int)(offset * scaling);
            top = workArea.Y + (workArea.Height - (int)(height * scaling)) / 2 + (int)(offset * scaling);
        }

        var maxLeft = workArea.Right - (int)Math.Min(width * scaling, workArea.Width);
        var maxTop = workArea.Bottom - (int)Math.Min(height * scaling, workArea.Height);
        Position = new PixelPoint(
            Math.Clamp(left, workArea.X, Math.Max(workArea.X, maxLeft)),
            Math.Clamp(top, workArea.Y, Math.Max(workArea.Y, maxTop)));
    }

    /// <summary>
    /// Captures current window bounds into settings without persisting to disk.
    /// Must be called on the UI thread while the window is still visible.
    /// </summary>
    private void CaptureBoundsToSettings()
    {
        if (!IsPrimaryWindow)
            return;

        if (DataContext is not MainViewModel vm) return;
        var settings = vm.DataStore.Data.Settings;

        settings.IsMaximized = WindowState == WindowState.Maximized;

        if (WindowState == WindowState.Normal)
        {
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;

            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            var scaling = screen?.Scaling ?? 1.0;
            settings.WindowLeft = Position.X / scaling;
            settings.WindowTop = Position.Y / scaling;
        }

        settings.SidebarWidth = _expandedSidebarWidth;
    }

    private static double ClampSidebarWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width))
            return DefaultSidebarWidth;

        return Math.Clamp(width, MinSidebarWidth, MaxSidebarWidth);
    }

    private void ApplySavedSidebarWidth(MainViewModel vm)
    {
        var savedWidth = vm.DataStore.Data.Settings.SidebarWidth ?? DefaultSidebarWidth;
        _expandedSidebarWidth = ClampSidebarWidth(savedWidth);

        if (_sidebarBorder is not null)
            _sidebarBorder.Width = vm.IsSidebarCollapsed ? 0 : _expandedSidebarWidth;

        if (_contentArea is not null)
            _contentArea.Margin = GetContentAreaMargin(vm.IsSidebarCollapsed);

        if (_sidebarResizeThumb is not null)
            _sidebarResizeThumb.IsVisible = !vm.IsSidebarCollapsed;
    }

    private double GetCurrentSidebarWidth()
    {
        var width = _sidebarBorder?.Width ?? _expandedSidebarWidth;
        if (width <= 0)
            width = _expandedSidebarWidth;

        return ClampSidebarWidth(width);
    }

    private void ApplySidebarWidth(double width)
    {
        var clampedWidth = ClampSidebarWidth(width);
        _expandedSidebarWidth = clampedWidth;

        if (_sidebarBorder is not null)
            _sidebarBorder.Width = clampedWidth;

        if (IsPrimaryWindow && DataContext is MainViewModel vm)
            vm.DataStore.Data.Settings.SidebarWidth = clampedWidth;
    }

    private void PersistSidebarWidth()
    {
        if (!IsPrimaryWindow)
            return;

        if (DataContext is not MainViewModel vm)
            return;

        var clampedWidth = ClampSidebarWidth(_expandedSidebarWidth);
        vm.DataStore.Data.Settings.SidebarWidth = clampedWidth;
        _ = vm.DataStore.SaveAsync();
    }

    private void PersistSidebarCollapsed()
    {
        if (!IsPrimaryWindow)
            return;

        if (DataContext is not MainViewModel vm)
            return;

        vm.DataStore.Data.Settings.SidebarCollapsed = vm.IsSidebarCollapsed;
        _ = vm.DataStore.SaveAsync();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (DataContext is not MainViewModel vm) return;

        // Don't intercept shortcuts while recording a global hotkey
        var settingsPage = _settingsView;
        if (settingsPage?.IsRecordingHotkey == true) return;

        var scaleDelta = UiScaleService.GetShortcutDelta(e.Key, e.KeyModifiers);
        if (scaleDelta != 0)
        {
            var adjustedByApp = Application.Current is App app && app.TryAdjustUiScale(scaleDelta);
            if (!adjustedByApp)
            {
                vm.SettingsVM.UiScalePercent = UiScaleService.AdjustScalePercent(
                    vm.SettingsVM.UiScalePercent,
                    scaleDelta);
            }

            e.Handled = true;
            return;
        }

        if (!vm.IsOnboarded) return;

        if (TryHandleChatHistoryNavigationKey(vm, e))
            return;

        // Primary command modifier: Cmd (Meta) on macOS, Ctrl on Windows/Linux — so shortcuts
        // feel native on each platform. On Windows/Linux this is exactly the previous Control
        // check, so their behavior is unchanged.
        var ctrl = OperatingSystem.IsMacOS()
            ? (e.KeyModifiers & KeyModifiers.Meta) != 0
            : (e.KeyModifiers & KeyModifiers.Control) != 0;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var alt = (e.KeyModifiers & KeyModifiers.Alt) != 0;
        var noMods = e.KeyModifiers == KeyModifiers.None;

        // ── Rename dialog: Enter to confirm, Escape to cancel ──
        if (_renameOverlay?.IsVisible == true)
        {
            if (e.Key == Key.Enter && noMods)
            {
                vm.CommitRenameChatCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape && noMods)
            {
                vm.CancelRenameChatCommand.Execute(null);
                e.Handled = true;
                return;
            }
            return; // Block other shortcuts while rename dialog is open
        }

        // ── Search overlay is open: only allow Escape and let overlay handle its own keys ──
        if (vm.SearchOverlayVM.IsOpen)
        {
            if (e.Key == Key.Escape && noMods)
            {
                vm.SearchOverlayVM.Close();
                e.Handled = true;
            }
            // Don't process other main shortcuts while overlay is open
            return;
        }

        if (_isProjectSwitcherOpen && e.Key == Key.Escape && noMods)
        {
            SetProjectSwitcherOpen(false);
            e.Handled = true;
            return;
        }

        if (_isUnreadPanelOpen && e.Key == Key.Escape && noMods)
        {
            vm.CloseUnreadPanelCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // ── Ctrl+N — New chat ──
        if (ctrl && !alt && !shift && e.Key == Key.N)
        {
            vm.NewChatCommand.Execute(null);
            Dispatcher.UIThread.Post(() => _chatView?.FocusComposer(), DispatcherPriority.Input);
            e.Handled = true;
            return;
        }

        // ── Ctrl+Shift+N — Open active chat, or a new chat, in a new window ──
        if (ctrl && !alt && shift && e.Key == Key.N)
        {
            if (vm.ChatVM.CurrentChat is null)
                vm.OpenNewChatInNewWindowCommand.Execute(null);
            else
                vm.OpenChatInNewWindowCommand.Execute(vm.ChatVM.CurrentChat);
            e.Handled = true;
            return;
        }

        // ── Ctrl+Shift+D — Duplicate the active chat ──
        if (ctrl && !alt && shift && e.Key == Key.D)
        {
            if (vm.ChatVM.CurrentChat is not null)
                vm.DuplicateCurrentChatCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // ── Ctrl+L — Focus chat input ──
        if (ctrl && !alt && !shift && e.Key == Key.L)
        {
            if (vm.SelectedNavIndex != 0)
                vm.SelectedNavIndex = 0;
            Dispatcher.UIThread.Post(() => _chatView?.FocusComposer(), DispatcherPriority.Input);
            e.Handled = true;
            return;
        }

        // ── Ctrl+K — Open search overlay ──
        if (ctrl && !alt && !shift && e.Key == Key.K)
        {
            vm.SearchOverlayVM.Open();
            e.Handled = true;
            return;
        }

        // Ctrl+Alt+L / Ctrl+Alt+W — switch local/worktree before a chat starts
        var canSwitchWorktreeMode = vm.SelectedNavIndex == 0
            && vm.ChatVM.CurrentChat is null
            && vm.ChatVM.IsCodingProject;

        if (ctrl && alt && !shift && canSwitchWorktreeMode)
        {
            if (e.Key == Key.L)
            {
                vm.ChatVM.SwitchToLocalPreChatCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.W)
            {
                vm.ChatVM.SwitchToWorktreePreChatCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        // ── Ctrl+B — Toggle sidebar ──
        if (ctrl && !alt && !shift && e.Key == Key.B)
        {
            vm.ToggleSidebarCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // ── Ctrl+, — Settings ──
        if (ctrl && !alt && !shift && e.Key == Key.OemComma)
        {
            vm.OpenSettingsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // ── Ctrl+1..5 — the nav pill's places, in order ──
        if (ctrl && !alt && !shift)
        {
            var buttonIndex = e.Key switch
            {
                Key.D1 => NavChatButton,
                Key.D2 => NavLibraryButton,
                Key.D3 => NavJobsButton,
                Key.D4 => NavCustomizeButton,
                Key.D5 => NavSettingsButton,
                _ => -1
            };
            if (buttonIndex >= 0 && _navButtons[buttonIndex] is Button place)
            {
                // The place's own command, so a shortcut always does what clicking the place does.
                ExecuteNavButton(place);
                e.Handled = true;
                return;
            }
        }

    }

    private bool CanHandleChatHistoryNavigationInput(MainViewModel vm, object? source)
    {
        if (_renameOverlay?.IsVisible == true || vm.SearchOverlayVM.IsOpen)
            return false;

        if (source is Visual visual && visual.FindAncestorOfType<BrowserView>() is not null)
            return false;

        return true;
    }

    private void BeginChatHistoryNavigation(MainViewModel vm, int direction)
    {
        _ = vm.TryNavigateChatHistoryAsync(direction);
    }

    private bool TryHandleChatHistoryNavigationKey(MainViewModel vm, KeyEventArgs e)
    {
        if (!CanHandleChatHistoryNavigationInput(vm, e.Source))
            return false;

        var altOnly = e.KeyModifiers == KeyModifiers.Alt;
        var direction = e.Key switch
        {
            Key.BrowserBack => -1,
            Key.BrowserForward => 1,
            Key.Left when altOnly => -1,
            Key.Right when altOnly => 1,
            _ => 0
        };

        if (direction == 0)
            return false;

        BeginChatHistoryNavigation(vm, direction);
        e.Handled = true;
        return true;
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || DataContext is not MainViewModel vm || !CanHandleChatHistoryNavigationInput(vm, e.Source))
            return;

        if (_isProjectSwitcherOpen
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && !IsWithinProjectSwitcher(e.Source))
        {
            SetProjectSwitcherOpen(false);
        }

        if (_isUnreadPanelOpen
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && !IsWithinUnreadInbox(e.Source))
        {
            vm.CloseUnreadPanelCommand.Execute(null);
        }

        var point = e.GetCurrentPoint(this);
        var direction = point.Properties.IsXButton1Pressed
            ? -1
            : point.Properties.IsXButton2Pressed
                ? 1
                : 0;

        if (direction == 0)
            return;

        BeginChatHistoryNavigation(vm, direction);
        e.Handled = true;
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    /// <summary>Handle WindowState changes that affect window chrome/layout.</summary>
    private void OnWindowStateChanged()
    {
        ApplyWindowContentPaddingForState();
    }

    private void ApplyWindowContentPaddingForState()
    {
        // Avalonia 12 handles extended client area padding automatically —
        // no manual padding workaround needed.
    }

    private void AttachChatWorkspace(MainViewModel vm)
    {
        if (_chatWorkspace is null)
            return;

        _chatWorkspace.EnsureChatVisible = () =>
        {
            if (vm.SelectedNavIndex != 0)
                vm.SelectedNavIndex = 0;
        };
        _chatWorkspace.CanShowBrowserPanel = chatId => vm.ActiveChatId == chatId;
        _chatWorkspace.CanShowDesktopPanel = chatId => vm.ActiveChatId == chatId && vm.SelectedNavIndex == 0;
        _chatWorkspace.DataStore = vm.DataStore;

        if (!ReferenceEquals(_chatWorkspace.DataContext, vm.ChatVM))
            _chatWorkspace.DataContext = vm.ChatVM;

        _chatView = _chatWorkspace.ChatView;
        foreach (var svc in vm.ChatVM.ChatBrowserServices.Values)
            svc.SetTheme(vm.IsDarkTheme);
        _chatWorkspace?.CloseWorkspacePages();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MainViewModel vm && !ReferenceEquals(vm, _wiredVm))
        {
            _wiredVm = vm;
            ApplySavedSidebarWidth(vm);
            UpdateOnboarding(vm.IsOnboarded);
            ShowPage(vm.SelectedNavIndex);
            UpdateNavHighlight(vm.SelectedNavIndex);

            // Attach ListBox handlers once layout is ready
            Dispatcher.UIThread.Post(() =>
            {
                AttachListBoxHandlers();
                SyncListBoxSelection(vm.ActiveChatId);
                RefreshProjectSwitcher(vm);
                if (vm.IsOnboarded && vm.SelectedNavIndex == 0)
                {
                    // Delay so the user sees the textbox focus animation
                    _ = Task.Delay(350).ContinueWith(_ =>
                        Dispatcher.UIThread.Post(() => _chatView?.FocusComposer()),
                        TaskScheduler.Default);
                }
            }, DispatcherPriority.Loaded);

            // Wire ProjectsVM chat open to navigate to chat tab
            vm.ProjectsVM.ChatOpenRequested += chat => vm.OpenChatFromProjectCommand.Execute(chat);

            // Animate sidebar title when chat title changes (no full list rebuild)
            vm.ChatTitleChanged += (chatId, newTitle) =>
            {
                Dispatcher.UIThread.Post(() => AnimateSidebarTitle(chatId, newTitle));
            };
            vm.ChatSelectionSyncRequested += _ => QueueChatListSync(vm);

            // Wire search overlay result selection
            vm.SearchOverlayVM.ResultSelected += result => OnSearchResultSelected(vm, result);

            // Coming back to Lumi with a teammate's Lumi code on the clipboard offers a preview.
            Activated += (_, _) => _ = vm.NoticeVM.CheckClipboardAsync();

            // Keep native title-bar geometry aligned with the layout-scaled content.
            vm.SettingsVM.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SettingsViewModel.UiScalePercent))
                    ApplyUiScaleToChrome(vm.SettingsVM.UiScalePercent);
            };

            ApplyUiScaleToChrome(vm.SettingsVM.UiScalePercent);
            AttachChatWorkspace(vm);

            // Sync initial browser theme
            vm.SettingsBrowserService.SetTheme(vm.IsDarkTheme);
            foreach (var svc in vm.ChatVM.ChatBrowserServices.Values)
                svc.SetTheme(vm.IsDarkTheme);

            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.IsDarkTheme))
                {
                    if (Application.Current is not null)
                        Application.Current.RequestedThemeVariant = vm.IsDarkTheme
                            ? ThemeVariant.Dark
                            : ThemeVariant.Light;
                    vm.SettingsBrowserService.SetTheme(vm.IsDarkTheme);
                    foreach (var svc in vm.ChatVM.ChatBrowserServices.Values)
                        svc.SetTheme(vm.IsDarkTheme);
                }
                else if (args.PropertyName == nameof(MainViewModel.IsCompactDensity))
                {
                    ApplyDensity(vm.IsCompactDensity);
                }
                else if (args.PropertyName == nameof(MainViewModel.IsOnboarded))
                {
                    UpdateOnboarding(vm.IsOnboarded);
                }
                else if (args.PropertyName == nameof(MainViewModel.SelectedNavIndex))
                {
                    ShowPage(vm.SelectedNavIndex);
                    UpdateNavHighlight(vm.SelectedNavIndex);

                    // Refresh composer catalogs and re-attach list handlers when switching to chat tab
                    if (vm.SelectedNavIndex == 0)
                    {
                        vm.ChatVM.RefreshComposerCatalogs();
                        _chatWorkspace?.RestoreDesktopPanel();

                        QueueChatListSync(vm);
                        Dispatcher.UIThread.Post(() => _chatView?.FocusComposer(), DispatcherPriority.Loaded);
                    }
                }
                else if (args.PropertyName == nameof(MainViewModel.ActiveChatId))
                {
                    // Switching chats closes any Workspace page (the overview follows the preference)
                    _chatWorkspace?.CloseWorkspacePages();
                    _chatWorkspace?.RestoreDesktopPanel();
                    QueueChatListSync(vm);
                }
                else if (args.PropertyName == nameof(MainViewModel.ChatVM))
                {
                    AttachChatWorkspace(vm);
                    Dispatcher.UIThread.Post(() => _chatView?.FocusComposer(), DispatcherPriority.Loaded);
                }
                else if (args.PropertyName == nameof(MainViewModel.RenamingChat))
                {
                    var isRenaming = vm.RenamingChat is not null;
                    if (_renameOverlay is not null) _renameOverlay.IsVisible = isRenaming;
                    if (isRenaming && _renameTextBox is not null)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            _renameTextBox.Focus();
                            _renameTextBox.SelectAll();
                        }, DispatcherPriority.Input);
                    }
                }
                else if (args.PropertyName == nameof(MainViewModel.SelectedProjectFilter))
                {
                    UpdateProjectSwitcherSummary(vm);
                    QueueProjectSwitcherRefresh(vm);
                    QueueChatListSync(vm);
                    if (!_isProjectChatListRevealQueued)
                        QueueProjectChatListReveal();
                    _isProjectChatListRevealArmed = false;
                    _isProjectChatListRevealQueued = false;
                }
                else if (args.PropertyName == nameof(MainViewModel.IsUnreadPanelOpen))
                {
                    SetUnreadPanelOpen(vm.IsUnreadPanelOpen);
                }
                else if (args.PropertyName == nameof(MainViewModel.IsSidebarCollapsed))
                {
                    AnimateSidebarCollapse(vm.IsSidebarCollapsed);
                    PersistSidebarCollapsed();
                }
            };

            // When project list changes, rebuild filter bar
            vm.Projects.CollectionChanged += (_, _) =>
            {
                Dispatcher.UIThread.Post(() => RefreshProjectSwitcher(vm), DispatcherPriority.Loaded);
            };

            // Per-project unread pills live in the switcher rows, so keep them live while it is open.
            vm.UnreadStateChanged += () =>
            {
                if (_isProjectSwitcherOpen)
                    QueueProjectSwitcherRefresh(vm);
            };

            vm.ChatGroups.CollectionChanged += (_, _) =>
            {
                var shouldRevealChats = _isProjectChatListRevealArmed;
                if (shouldRevealChats)
                {
                    _isProjectChatListRevealArmed = false;
                    _isProjectChatListRevealQueued = true;
                }

                QueueChatListSync(vm, shouldRevealChats);
            };
        }
    }

    private void UpdateOnboarding(bool isOnboarded)
    {
        if (!isOnboarded && _onboardingHost is { Content: null })
            _onboardingHost.Content = new OnboardingView { Name = "OnboardingView" };

        (_onboardingHost?.Content as OnboardingView)?.SetActive(!isOnboarded);
        if (_onboardingPanel is not null) _onboardingPanel.IsVisible = !isOnboarded;
        if (_mainPanel is not null) _mainPanel.IsVisible = true;

        // Animate the main app entrance when onboarding completes
        if (isOnboarded && _mainPanel is not null)
        {
            AnimateShellSectionChange(_pages.FirstOrDefault(p => p?.IsVisible == true),
                _sidebarPanels.FirstOrDefault(p => p?.IsVisible == true));
        }
    }

    private void ShowPage(int index)
    {
        EnsurePageViewLoaded(index);

        var sectionChanged = _currentShellIndex != index;
        _currentShellIndex = index;
        var sidebarIndex = SidebarForPage(index);
        var sidebarChanged = _currentSidebarIndex != sidebarIndex;
        _currentSidebarIndex = sidebarIndex;

        for (int i = 0; i < _pages.Length; i++)
        {
            if (_pages[i] is not null)
                _pages[i]!.IsVisible = i == index;
        }

        // Show the matching sidebar panel
        for (int i = 0; i < _sidebarPanels.Length; i++)
        {
            if (_sidebarPanels[i] is not null)
                _sidebarPanels[i]!.IsVisible = i == sidebarIndex;
        }

        // Leaving the chat closes Workspace pages: the native browser can't float over other pages
        if (index != 0)
        {
            _chatWorkspace?.CloseWorkspacePages();
        }
        else if (_chatWorkspace?.IsBrowserOpen == true)
        {
            // Returning to chat with browser open — show the overlay and refresh bounds
            _chatWorkspace.ShowCurrentBrowserController();
        }

        // When projects tab is shown, refresh chat counts and the selected project's chats
        if (index == 2 && DataContext is MainViewModel vm)
            vm.ProjectsVM.RefreshSelectedProjectChats();

        // When settings tab is shown, refresh stats
        if (index == 6 && DataContext is MainViewModel svm)
        {
            if (svm.SettingsVM.SelectedPageIndex < 0)
                svm.SettingsVM.SelectedPageIndex = 0;
            svm.SettingsVM.RefreshStats();
        }

        if (sectionChanged && _mainPanel?.IsVisible == true)
        {
            // Memories shares the Settings sidebar, so moving between them only moves the page.
            AnimateShellSectionChange(_pages[index], sidebarChanged ? _sidebarPanels[sidebarIndex] : null);
        }
    }

    /// <summary>The sidebar a page shows. Memories is a Settings page, so it keeps the Settings list.</summary>
    internal static int SidebarForPage(int pageIndex)
        => pageIndex == MainViewModel.MemoriesNavIndex ? 7 : pageIndex;

    private void AnimateShellSectionChange(Control? page, Control? sidebar)
    {
        if (DataContext is MainViewModel { SettingsVM.ShowAnimations: false })
            return;

        // On the render thread: a page that takes a moment to build still rises in once it shows.
        if (page is not null)
            SlideFadeEntrance.PlayComposited(page, 10, TimeSpan.FromMilliseconds(240));
        if (sidebar is not null)
            SlideFadeEntrance.PlayComposited(sidebar, 6, TimeSpan.FromMilliseconds(190));
    }

    private async void AnimateSidebarCollapse(bool collapse)
    {
        if (_sidebarBorder is null) return;

        if (_sidebarResizeThumb is not null && !collapse)
            _sidebarResizeThumb.IsVisible = true;

        var ct = ReplaceCancellationTokenSource(ref _sidebarAnimCts).Token;
        var from = collapse ? GetCurrentSidebarWidth() : 0.0;
        var to = collapse ? 0.0 : _expandedSidebarWidth;

        var easing = new SplineEasing(0.16, 1, 0.3, 1);
        var duration = TimeSpan.FromMilliseconds(200);

        var anim = new Avalonia.Animation.Animation
        {
            Duration = duration,
            Easing = easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters = { new Setter(WidthProperty, from) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters = { new Setter(WidthProperty, to) }
                },
            }
        };

        // Inset the content area so the chat island keeps a margin from the window edge while the
        // sidebar is collapsed, and so the floating toggle button sits in a gutter to its left
        // instead of overlapping the chat title. Animated in sync with the width collapse.
        var toMargin = GetContentAreaMargin(collapse);
        Avalonia.Animation.Animation? marginAnim = null;
        if (_contentArea is not null)
        {
            marginAnim = new Avalonia.Animation.Animation
            {
                Duration = duration,
                Easing = easing,
                FillMode = FillMode.Forward,
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0),
                        Setters = { new Setter(MarginProperty, _contentArea.Margin) }
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1),
                        Setters = { new Setter(MarginProperty, toMargin) }
                    },
                }
            };
        }

        try
        {
            var widthTask = anim.RunAsync(_sidebarBorder, ct);
            var marginTask = marginAnim is not null && _contentArea is not null
                ? marginAnim.RunAsync(_contentArea, ct)
                : Task.CompletedTask;
            await Task.WhenAll(widthTask, marginTask);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }

        _sidebarBorder.Width = to;
        if (_contentArea is not null)
            _contentArea.Margin = toMargin;

        if (_sidebarResizeThumb is not null)
            _sidebarResizeThumb.IsVisible = !collapse;

        // The pill can't be measured while the sidebar is collapsed (zero width). Once it's expanded
        // again, size the active place's label that was deferred at startup/collapse.
        if (!collapse && _navHoverInitPending)
            Dispatcher.UIThread.Post(InitializeNavVisuals, DispatcherPriority.Render);
    }

    private static Thickness GetContentAreaMargin(bool collapsed)
        => collapsed ? new Thickness(54, 0, 6, 6) : new Thickness(0, 0, 6, 6);

    private void OnSidebarResizeThumbDragDelta(object? sender, VectorEventArgs e)
    {
        if (_sidebarBorder is null || DataContext is not MainViewModel vm || vm.IsSidebarCollapsed)
            return;

        ApplySidebarWidth(GetCurrentSidebarWidth() + e.Vector.X);
    }

    private void OnSidebarResizeThumbDragCompleted(object? sender, VectorEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.IsSidebarCollapsed)
            return;

        PersistSidebarWidth();
    }

    /// <summary>
    /// The pill position lit for a page. Projects are a Customize section (the chat list's switcher
    /// only switches between them) and Memories belongs to Settings (a Settings page).
    /// </summary>
    internal static int NavButtonForPage(int pageIndex) => pageIndex switch
    {
        0 => NavChatButton,
        MainViewModel.LibraryNavIndex => NavLibraryButton,
        1 => NavJobsButton,
        7 or MainViewModel.MemoriesNavIndex => NavSettingsButton,
        _ when MainViewModel.IsCustomizeIndex(pageIndex) => NavCustomizeButton,
        _ => -1,
    };

    /// <summary>The page a pill position opens; Customize and Settings return to the section used last.</summary>
    internal static int PageForNavButton(MainViewModel vm, int buttonIndex) => buttonIndex switch
    {
        NavChatButton => 0,
        NavLibraryButton => MainViewModel.LibraryNavIndex,
        NavJobsButton => 1,
        NavCustomizeButton => vm.CustomizeTarget,
        NavSettingsButton => vm.SettingsTarget,
        _ => -1,
    };

    /// <summary>
    /// Width the pill needs to name its active place, from the fixed sizes in the nav styles. When
    /// the sidebar is narrower than this, the active place keeps only its highlight.
    /// </summary>
    internal static double NavPillWidthWithLabel(int placeCount, double labelWidth)
        => placeCount * NavIconButtonWidth
           + Math.Max(0, placeCount - 1) * NavRowSpacing
           + 2 * (NavPillPadding + NavPillBorder)
           + NavLabelMargin
           + labelWidth;

    private void UpdateNavHighlight(int pageIndex)
    {
        var index = NavButtonForPage(pageIndex);
        _activeNavIndex = index;
        for (int i = 0; i < _navButtons.Length; i++)
        {
            if (_navButtons[i] is { } btn)
                SetClass(btn, "active", i == index);
        }

        for (int i = 0; i < _railNavButtons.Length; i++)
        {
            if (_railNavButtons[i] is { } btn)
                SetClass(btn, "active", i == index);
        }

        UpdateNavLabels();
    }

    private void InitializeNavVisuals()
    {
        // The pill lives inside the sidebar; while it is collapsed nothing can be measured, so the
        // labels are sized once it opens again (see AnimateSidebarCollapse).
        if (IsSidebarCollapsedNow())
        {
            _navHoverInitPending = true;
            return;
        }

        _navHoverInitPending = false;
        _navLabels = _navButtons
            .Select(button => button?
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(textBlock => textBlock.Classes.Contains("nav-label")))
            .ToArray();
        UpdateNavLabels();
    }

    private bool IsSidebarCollapsedNow()
        => DataContext is MainViewModel { IsSidebarCollapsed: true };

    /// <summary>Names the active place, unless the sidebar is too narrow to fit the label.</summary>
    private void UpdateNavLabels()
    {
        if (_navLabels.Length != _navButtons.Length)
            return;

        var labelIndex = -1;
        if (_activeNavIndex >= 0 && _activeNavIndex < _navLabels.Length && _navLabels[_activeNavIndex] is { } activeLabel)
        {
            var available = _navPillHost?.Bounds.Width ?? 0;
            var fits = available <= 0
                || NavPillWidthWithLabel(_navButtons.Length, MeasureNavLabelWidth(activeLabel)) <= available;
            if (fits)
                labelIndex = _activeNavIndex;
        }

        for (var i = 0; i < _navButtons.Length; i++)
        {
            if (_navButtons[i] is not Button button)
                continue;

            var labeled = i == labelIndex;
            SetClass(button, "labeled", labeled);
            if (_navLabels[i] is TextBlock label)
                label.MaxWidth = labeled ? MeasureNavLabelWidth(label) : 0;
        }
    }

    private void OnNavPillHostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
            UpdateNavLabels();
    }

    private void WireNavHoverEvents()
    {
        if (_navPill is not null)
        {
            _navPill.PointerEntered += OnNavPillPointerEntered;
            _navPill.PointerMoved += OnNavPillPointerMoved;
            _navPill.PointerExited += OnNavPillPointerExited;
            _navPill.PointerReleased += OnNavPillPointerReleased;
            _navPill.AddHandler(PointerPressedEvent, OnNavPillPointerPressed, RoutingStrategies.Tunnel);
        }

        if (_navPillHost is not null)
            _navPillHost.SizeChanged += OnNavPillHostSizeChanged;
    }

    private void OnNavPillPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Border pill)
            return;

        if (_navTipsPausedAt is { } pausedAt
            && Point.Distance(e.GetPosition(this), pausedAt) > NavTipResumeDistance)
            ResumeNavTips(pill);

        UpdateHoveredNavButton(pill, e);
    }

    /// <summary>
    /// A switch resizes the labels, so the places slide under a cursor that hasn't moved. A tip shown
    /// then would name whichever place drifted under it, not one you pointed at, so tips pause from
    /// the press until you move on (in window coordinates: the pill itself re-centres) or leave.
    /// </summary>
    private void PauseNavTips(Border pill, Point windowPosition)
    {
        _navTipsPausedAt = windowPosition;
        ToolTip.SetServiceEnabled(pill, false);
    }

    private void ResumeNavTips(Border pill)
    {
        if (_navTipsPausedAt is null)
            return;

        _navTipsPausedAt = null;
        pill.ClearValue(ToolTip.ServiceEnabledProperty);
    }

    private void UpdateHoveredNavButton(Border pill, PointerEventArgs e)
    {
        RequestHoveredNavButton(GetHoveredNavButtonIndex(pill, e.GetPosition(pill)));
    }

    /// <summary>
    /// The place nearest the pointer. The whole pill is a target, so the gaps between icons and the
    /// pill's rim still open the place beside them.
    /// </summary>
    private int GetHoveredNavButtonIndex(Border pill, Point pointerPosition)
    {
        var nearest = -1;
        var nearestDistance = double.MaxValue;
        for (var i = 0; i < _navButtons.Length; i++)
        {
            if (_navButtons[i] is not Button { IsVisible: true } button)
                continue;

            var topLeft = button.TranslatePoint(default, pill);
            if (topLeft is null)
                continue;

            var left = topLeft.Value.X;
            var right = left + button.Bounds.Width;
            var distance = pointerPosition.X < left ? left - pointerPosition.X
                : pointerPosition.X > right ? pointerPosition.X - right
                : 0;
            if (distance < nearestDistance)
            {
                nearest = i;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    private void OnNavPillPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border pill)
            return;

        var point = e.GetCurrentPoint(pill);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        var index = GetHoveredNavButtonIndex(pill, point.Position);
        if (index < 0 || _navButtons[index] is not Button button)
            return;

        PauseNavTips(pill, e.GetPosition(this));
        ExecuteNavButton(button);
        e.Handled = true;
    }

    private void SetHoveredNavButton(int index)
    {
        if (_hoveredNavIndex == index)
            return;

        _hoveredNavIndex = index;
        for (var i = 0; i < _navButtons.Length; i++)
        {
            if (_navButtons[i] is { } button)
                SetClass(button, "hovered", i == index);
        }
    }

    private void RequestHoveredNavButton(int index)
    {
        if (index == _hoveredNavIndex)
        {
            _pendingNavHoverIndex = -1;
            DisposeCancellationTokenSource(ref _navHoverIntentCts);
            return;
        }

        if (index < 0)
        {
            _pendingNavHoverIndex = -1;
            DisposeCancellationTokenSource(ref _navHoverIntentCts);
            SetHoveredNavButton(-1);
            return;
        }

        if (_pendingNavHoverIndex == index)
            return;

        _pendingNavHoverIndex = index;
        var cts = ReplaceCancellationTokenSource(ref _navHoverIntentCts);
        _ = ApplyNavHoverIntentAsync(index, cts.Token);
    }

    private async Task ApplyNavHoverIntentAsync(int index, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(NavHoverIntentDelayMs, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (cancellationToken.IsCancellationRequested || _pendingNavHoverIndex != index)
                return;

            _pendingNavHoverIndex = -1;
            SetHoveredNavButton(index);

            // Pre-warm the hovered page off the click path. Hovering a nav item signals intent to
            // open it, so inflate its (lazily-created, then cached) view now at Background priority.
            // By click time the heavy one-time XAML/template inflation is already paid, turning a
            // cold first-visit hitch (worst: the MCP servers page) into an instant warm switch.
            // Idempotent: EnsurePageViewLoaded no-ops once the page's host content exists.
            if (index > 0 && DataContext is MainViewModel hovered)
            {
                var page = PageForNavButton(hovered, index);
                if (page >= 0)
                    Dispatcher.UIThread.Post(() => EnsurePageViewLoaded(page), DispatcherPriority.Background);
            }
        }, DispatcherPriority.Input);
    }

    private static double MeasureNavLabelWidth(TextBlock label)
    {
        var probe = new TextBlock
        {
            Text = label.Text,
            FontFamily = label.FontFamily,
            FontSize = label.FontSize,
            FontStyle = label.FontStyle,
            FontWeight = label.FontWeight,
            FontStretch = label.FontStretch,
            LineHeight = label.LineHeight,
            TextWrapping = TextWrapping.NoWrap,
            FlowDirection = label.FlowDirection,
        };

        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Min(Math.Ceiling(probe.DesiredSize.Width), NavLabelMaxWidth);
    }
    private static void ExecuteNavButton(Button button)
    {
        var command = button.Command;
        var parameter = button.CommandParameter;
        if (command is null || !command.CanExecute(parameter))
            return;

        command.Execute(parameter);
    }

    private static void SetClass(StyledElement element, string className, bool enabled)
    {
        if (enabled)
        {
            if (!element.Classes.Contains(className))
                element.Classes.Add(className);

            return;
        }

        element.Classes.Remove(className);
    }

    private void OnNavPillPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Border pill) return;

        UpdateHoveredNavButton(pill, e);
        AnimateNavPillScale(pill, new Avalonia.Vector3D(1.04, 1.04, 1), TimeSpan.FromMilliseconds(250));
    }

    private void OnNavPillPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Border pill)
            return;

        // A press captures the pointer; when the places then slide under it, Avalonia clears
        // pointer-over and reports an exit although the pointer never left. Treating that as leaving
        // would drop the hover, shrink the pill mid-click and bring the paused tips back.
        if (e.Pointer.Captured is not null && new Rect(pill.Bounds.Size).Contains(e.GetPosition(pill)))
            return;

        LeaveNavPill(pill);
    }

    /// <summary>
    /// The exit a press swallows (see <see cref="OnNavPillPointerExited"/>) is the only one the pill
    /// gets while the pointer is captured, so dragging off the bar leaves it on release.
    /// </summary>
    private void OnNavPillPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Border pill && !new Rect(pill.Bounds.Size).Contains(e.GetPosition(pill)))
            LeaveNavPill(pill);
    }

    private void LeaveNavPill(Border pill)
    {
        RequestHoveredNavButton(-1);
        ResumeNavTips(pill);
        AnimateNavPillScale(pill, new Avalonia.Vector3D(1, 1, 1), TimeSpan.FromMilliseconds(250));
    }

    private static void AnimateNavPillScale(Border pill, Avalonia.Vector3D targetScale, TimeSpan duration)
    {
        var visual = ElementComposition.GetElementVisual(pill);
        if (visual is null) return;

        var w = pill.Bounds.Width;
        var h = pill.Bounds.Height;
        visual.CenterPoint = new Avalonia.Vector3D(w / 2, h / 2, 0);

        var compositor = visual.Compositor;
        var scaleAnim = compositor.CreateStableVector3DKeyFrameAnimation();
        scaleAnim.Target = "Scale";
        scaleAnim.InsertKeyFrame(1f, targetScale);
        scaleAnim.Duration = duration;

        // Let compositor time determine completion. Stopping after a wall-clock delay can
        // preserve a transient server value while the client still believes Scale is final.
        visual.StartAnimation("Scale", scaleAnim);
    }

    private void NewChatButton_Click(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => _chatView?.FocusComposer(), DispatcherPriority.Input);
    }

    private void SearchButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.SearchOverlayVM.Open();
    }

    private void OnOnboardingDragStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        BeginMoveDrag(e);
        e.Handled = true;
    }

    /// <summary>
    /// Root for chat-sidebar visual-tree walks. Scoped to the chat-list scroller subtree (just the
    /// visible chat rows) instead of the whole window — the window tree also contains the entire
    /// mounted transcript (hundreds of turn controls) and every management page, so walking <c>this</c>
    /// on every list rebuild/selection was orders of magnitude more work than the sidebar needs.
    /// </summary>
    private Visual ChatListWalkRoot => (Visual?)_chatListScroller ?? this;

    private void AttachListBoxHandlers()
    {
        foreach (var lb in ChatListWalkRoot.GetVisualDescendants().OfType<ListBox>())
        {
            if (!lb.Classes.Contains("chat-list")) continue;
            if (lb.GetValue(ChatListHandlersAttachedProperty)) continue;
            lb.SetValue(ChatListHandlersAttachedProperty, true);
            lb.SelectionChanged += OnChatListBoxSelectionChanged;
            lb.AddHandler(
                PointerPressedEvent,
                OnChatListPointerPressed,
                Avalonia.Interactivity.RoutingStrategies.Tunnel,
                handledEventsToo: true);
        }
    }

    private void OnChatListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source)
            return;

        var item = source.FindAncestorOfType<ListBoxItem>();
        if (item is null)
            return;

        var point = e.GetCurrentPoint(item);
        if (point.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            return;
        }

        if (!IsLeftPointerPress(point.Properties))
            return;

        if (item.DataContext is not Chat chat || DataContext is not MainViewModel vm)
            return;

        if (!IsChatListDoubleClick(chat, point.Position, e))
            return;

        if (vm.OpenChatInNewWindowCommand.CanExecute(chat))
            vm.OpenChatInNewWindowCommand.Execute(chat);
        e.Handled = true;
    }

    private bool IsChatListDoubleClick(Chat chat, Point position, PointerPressedEventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        var isSameChat = _lastChatListClickChatId == chat.Id;
        var isRecent = isSameChat && now - _lastChatListClickAt <= ChatListDoubleClickThreshold;
        var isNearby =
            Math.Abs(position.X - _lastChatListClickPosition.X) <= ChatListDoubleClickMaxDistance &&
            Math.Abs(position.Y - _lastChatListClickPosition.Y) <= ChatListDoubleClickMaxDistance;
        var isDoubleClick = e.ClickCount >= 2 || (isRecent && isNearby);

        _lastChatListClickChatId = chat.Id;
        _lastChatListClickAt = now;
        _lastChatListClickPosition = position;

        if (isDoubleClick)
            _lastChatListClickChatId = null;

        return isDoubleClick;
    }

    private static bool IsLeftPointerPress(PointerPointProperties properties)
    {
        return properties.IsLeftButtonPressed ||
               properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed;
    }

    private void OnJobRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsRightButtonPressed
            && properties.PointerUpdateKind != PointerUpdateKind.RightButtonPressed)
        {
            return;
        }

        e.Handled = true;
    }

    private void OnChatListBoxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionSync) return;
        if (sender is not ListBox lb) return;
        if (lb.SelectedItem is not Chat chat) return;

        var vm = DataContext as MainViewModel;
        if (vm is null) return;

        // Deselect other group ListBoxes
        _suppressSelectionSync = true;
        foreach (var otherLb in ChatListWalkRoot.GetVisualDescendants().OfType<ListBox>())
        {
            if (!otherLb.Classes.Contains("chat-list")) continue;
            if (otherLb != lb)
                otherLb.SelectedItem = null;
        }
        _suppressSelectionSync = false;

        vm.OpenChatCommand.Execute(chat);
    }

    private void SyncListBoxSelection(Guid? activeChatId)
    {
        _suppressSelectionSync = true;
        try
        {
            foreach (var lb in ChatListWalkRoot.GetVisualDescendants().OfType<ListBox>())
            {
                if (!lb.Classes.Contains("chat-list")) continue;

                if (!lb.GetValue(ChatListHandlersAttachedProperty))
                {
                    lb.SetValue(ChatListHandlersAttachedProperty, true);
                    lb.SelectionChanged += OnChatListBoxSelectionChanged;
                    lb.AddHandler(
                        PointerPressedEvent,
                        OnChatListPointerPressed,
                        Avalonia.Interactivity.RoutingStrategies.Tunnel,
                        handledEventsToo: true);
                }

                if (activeChatId is null)
                {
                    lb.SelectedItem = null;
                    continue;
                }

                Chat? match = null;
                foreach (var item in lb.Items)
                {
                    if (item is Chat c && c.Id == activeChatId.Value)
                    {
                        match = c;
                        break;
                    }
                }
                lb.SelectedItem = match;
            }
        }
        finally
        {
            _suppressSelectionSync = false;
        }
    }

    private void QueueChatListSync(MainViewModel vm, bool revealChats = false)
    {
        _revealAfterChatListSync |= revealChats;
        if (Interlocked.Exchange(ref _chatListSyncQueued, 1) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _chatListSyncQueued, 0);
            var shouldReveal = _revealAfterChatListSync;
            _revealAfterChatListSync = false;
            if (!ReferenceEquals(DataContext, vm))
                return;

            SyncListBoxSelection(vm.ActiveChatId);
            if (shouldReveal)
                QueueProjectChatListReveal();
        }, DispatcherPriority.Loaded);
    }

    private CancellationTokenSource? _titleAnimCts;
    private CancellationTokenSource? _chatListRevealCts;
    private const int ChatListRevealDelayMs = 40;
    private const int ChatListRevealDurationMs = 285;
    private const int ChatListRevealStaggerMs = 21;
    private const int ChatListRevealMaxDelayMs = 360;
    private const double ChatListRevealOffsetY = 10.0;
    private sealed record ChatListRevealTarget(ListBoxItem Item, Avalonia.Vector3D BaseOffset);

    private void ArmProjectChatListReveal()
    {
        _isProjectChatListRevealArmed = true;
        _isProjectChatListRevealQueued = false;
    }

    private void QueueProjectChatListReveal()
    {
        if (DataContext is not MainViewModel { SelectedNavIndex: 0 })
            return;

        var cts = ReplaceCancellationTokenSource(ref _chatListRevealCts);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(ChatListRevealDelayMs, cts.Token);
                await AnimateChatListRevealAsync(++_chatListRevealVersion, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }, DispatcherPriority.Loaded);
    }

    private async Task AnimateChatListRevealAsync(int revealVersion, CancellationToken ct)
    {
        if (_chatListScroller is null)
            return;

        var items = ChatListWalkRoot.GetVisualDescendants()
            .OfType<ListBoxItem>()
            .Where(IsChatListItem)
            .Select(item => new
            {
                Item = item,
                Top = item.TranslatePoint(default, _chatListScroller)?.Y ?? item.Bounds.Y
            })
            .OrderBy(candidate => candidate.Top)
            .Select(candidate => candidate.Item)
            .ToList();

        if (items.Count == 0)
            return;

        var targets = items
            .Select(item => (Item: item, Visual: ElementComposition.GetElementVisual(item)))
            .Where(target => target.Visual is not null)
            .Select(target => new ChatListRevealTarget(target.Item, target.Visual!.Offset))
            .ToList();

        foreach (var target in targets)
            PrepareChatListItemReveal(target);

        var animations = targets.Select((target, index) =>
            AnimateChatListItemRevealAsync(target, revealVersion, Math.Min(index * ChatListRevealStaggerMs, ChatListRevealMaxDelayMs), ct));
        await Task.WhenAll(animations);
    }

    private static void PrepareChatListItemReveal(ChatListRevealTarget target)
    {
        var visual = ElementComposition.GetElementVisual(target.Item);
        if (visual is null)
            return;

        visual.StopAnimation("Opacity");
        visual.StopAnimation("Offset");
        visual.Opacity = 0;
        visual.Offset = target.BaseOffset + new Avalonia.Vector3D(0, ChatListRevealOffsetY, 0);
    }

    private static bool IsChatListItem(ListBoxItem item)
    {
        return item.DataContext is Chat
            && item.FindAncestorOfType<ListBox>() is { } listBox
            && listBox.Classes.Contains("chat-list");
    }

    private async Task AnimateChatListItemRevealAsync(ChatListRevealTarget target, int revealVersion, int delayMs, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delayMs, ct);

            var visual = ElementComposition.GetElementVisual(target.Item);
            if (visual is null)
                return;

            var compositor = visual.Compositor;
            var startOffset = target.BaseOffset + new Avalonia.Vector3D(0, ChatListRevealOffsetY, 0);
            var settleOffset = target.BaseOffset + new Avalonia.Vector3D(0, 1, 0);

            var offsetAnim = compositor.CreateStableVector3DKeyFrameAnimation();
            offsetAnim.Target = "Offset";
            offsetAnim.InsertKeyFrame(0f, startOffset);
            offsetAnim.InsertKeyFrame(0.72f, settleOffset);
            offsetAnim.InsertKeyFrame(1f, target.BaseOffset);
            offsetAnim.Duration = TimeSpan.FromMilliseconds(ChatListRevealDurationMs);

            var opacityAnim = compositor.CreateStableScalarKeyFrameAnimation();
            opacityAnim.Target = "Opacity";
            opacityAnim.InsertKeyFrame(0f, 0f);
            opacityAnim.InsertKeyFrame(0.45f, 0.88f);
            opacityAnim.InsertKeyFrame(1f, 1f);
            opacityAnim.Duration = TimeSpan.FromMilliseconds(ChatListRevealDurationMs);

            visual.StartAnimation("Offset", offsetAnim);
            visual.StartAnimation("Opacity", opacityAnim);

            await Task.Delay(ChatListRevealDurationMs + 20, ct);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        finally
        {
            if (revealVersion == _chatListRevealVersion)
                ResetChatListItemRevealState(target);
        }
    }

    private static void ResetChatListItemRevealState(ChatListRevealTarget target)
    {
        try
        {
            var visual = ElementComposition.GetElementVisual(target.Item);
            if (visual is null)
                return;

            visual.StopAnimation("Opacity");
            visual.StopAnimation("Offset");
            visual.Opacity = 1;
            visual.Offset = target.BaseOffset;
        }
        catch (InvalidOperationException) { }
    }

    private async void AnimateSidebarTitle(Guid chatId, string newTitle)
    {
        // Cancel any in-flight title animation
        var cts = ReplaceCancellationTokenSource(ref _titleAnimCts);

        TextBlock? titleBlock = null;
        foreach (var lb in ChatListWalkRoot.GetVisualDescendants().OfType<ListBox>())
        {
            if (!lb.Classes.Contains("chat-list")) continue;
            foreach (var container in lb.GetVisualDescendants().OfType<ListBoxItem>())
            {
                if (container.DataContext is Chat chat && chat.Id == chatId)
                {
                    titleBlock = container.GetVisualDescendants().OfType<TextBlock>()
                        .FirstOrDefault(tb => tb.Name == "ChatTitleText");
                    break;
                }
            }
            if (titleBlock is not null) break;
        }

        if (titleBlock is null) return;

        ToolTip.SetTip(titleBlock, newTitle);

        // Typewriter: reveal characters one by one
        for (int i = 1; i <= newTitle.Length; i++)
        {
            if (cts.Token.IsCancellationRequested) break;
            titleBlock.Text = newTitle[..i];
            try { await Task.Delay(30, cts.Token); }
            catch (OperationCanceledException) { break; }
        }

        // Ensure final text is complete
        titleBlock.Text = newTitle;
    }

    /// <summary>Swap Strata density resources at runtime.</summary>
    public static void ApplyDensityStatic(bool compact)
    {
        var app = Application.Current;
        if (app is null) return;

        if (compact)
        {
            // Compact density values from Density.Compact.axaml
            app.Resources["Size.ControlHeightS"] = 24.0;
            app.Resources["Size.ControlHeightM"] = 30.0;
            app.Resources["Size.ControlHeightL"] = 36.0;
            app.Resources["Padding.ControlH"] = new Avalonia.Thickness(8, 0);
            app.Resources["Padding.Control"] = new Avalonia.Thickness(8, 4);
            app.Resources["Padding.ControlWide"] = new Avalonia.Thickness(12, 5);
            app.Resources["Padding.Section"] = new Avalonia.Thickness(12, 8);
            app.Resources["Padding.Card"] = new Avalonia.Thickness(14, 10);
            app.Resources["Font.SizeCaption"] = 11.0;
            app.Resources["Font.SizeBody"] = 13.0;
            app.Resources["Font.SizeBodyStrong"] = 13.0;
            app.Resources["Font.SizeSubtitle"] = 14.0;
            app.Resources["Font.SizeTitle"] = 17.0;
            app.Resources["Space.S"] = 6.0;
            app.Resources["Space.M"] = 8.0;
            app.Resources["Space.L"] = 12.0;
            app.Resources["Size.DataGridRowHeight"] = 28.0;
            app.Resources["Size.DataGridHeaderHeight"] = 32.0;
        }
        else
        {
            // Comfortable density values from Density.Comfortable.axaml
            app.Resources["Size.ControlHeightS"] = 28.0;
            app.Resources["Size.ControlHeightM"] = 36.0;
            app.Resources["Size.ControlHeightL"] = 44.0;
            app.Resources["Padding.ControlH"] = new Avalonia.Thickness(12, 0);
            app.Resources["Padding.Control"] = new Avalonia.Thickness(12, 6);
            app.Resources["Padding.ControlWide"] = new Avalonia.Thickness(16, 8);
            app.Resources["Padding.Section"] = new Avalonia.Thickness(16, 12);
            app.Resources["Padding.Card"] = new Avalonia.Thickness(20, 16);
            app.Resources["Font.SizeCaption"] = 12.0;
            app.Resources["Font.SizeBody"] = 14.0;
            app.Resources["Font.SizeBodyStrong"] = 14.0;
            app.Resources["Font.SizeSubtitle"] = 16.0;
            app.Resources["Font.SizeTitle"] = 20.0;
            app.Resources["Space.S"] = 8.0;
            app.Resources["Space.M"] = 12.0;
            app.Resources["Space.L"] = 16.0;
            app.Resources["Size.DataGridRowHeight"] = 36.0;
            app.Resources["Size.DataGridHeaderHeight"] = 40.0;
        }
    }

    private void ApplyDensity(bool compact)
    {
        ApplyDensityStatic(compact);
    }

    internal void ApplyUiScaleToChrome(int scalePercent)
        => ExtendClientAreaTitleBarHeightHint = BaseTitleBarHeight * UiScaleService.GetScale(scalePercent);

    /// <summary>Register/unregister the app for launch at login (cross-platform). An isolated
    /// profile never touches the registration, which belongs to the installed Lumi.</summary>
    public static void ApplyLaunchAtStartup(bool enable)
    {
        if (DataStore.UsesIsolatedProfile)
            return;

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (key is null) return;

                if (enable)
                    key.SetValue("Lumi", $"\"{exePath}\"");
                else
                    key.DeleteValue("Lumi", throwOnMissingValue: false);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var autostartDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config", "autostart");
                var desktopFile = Path.Combine(autostartDir, "lumi.desktop");

                if (enable)
                {
                    Directory.CreateDirectory(autostartDir);
                    // Quote the Exec value and escape the Desktop Entry reserved chars so paths with
                    // spaces or special characters launch correctly.
                    var execEscaped = exePath
                        .Replace("\\", "\\\\")
                        .Replace("\"", "\\\"")
                        .Replace("`", "\\`")
                        .Replace("$", "\\$");
                    File.WriteAllText(desktopFile,
                        $"[Desktop Entry]\nType=Application\nName=Lumi\nExec=\"{execEscaped}\"\nX-GNOME-Autostart-enabled=true\n");
                }
                else if (File.Exists(desktopFile))
                {
                    File.Delete(desktopFile);
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var launchAgentsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "LaunchAgents");
                var plistFile = Path.Combine(launchAgentsDir, "com.lumi.app.plist");

                if (enable)
                {
                    Directory.CreateDirectory(launchAgentsDir);
                    // Escape XML special characters so paths containing & < > etc. produce a valid plist.
                    var exePathXml = System.Security.SecurityElement.Escape(exePath);
                    File.WriteAllText(plistFile,
                        $"""
                        <?xml version="1.0" encoding="UTF-8"?>
                        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                        <plist version="1.0">
                        <dict>
                            <key>Label</key><string>com.lumi.app</string>
                            <key>ProgramArguments</key><array><string>{exePathXml}</string></array>
                            <key>RunAtLoad</key><true/>
                        </dict>
                        </plist>
                        """);
                }
                else if (File.Exists(plistFile))
                {
                    File.Delete(plistFile);
                }
            }
        }
        catch
        {
            // Silently ignore — user may not have access
        }
    }

    private void OnChatListBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        var inset = _sidebarNav?.Bounds.Height ?? 0;
        if (inset <= 0)
            return;

        // The viewport extends beneath the nav; bring rows clear of that covered edge.
        var target = e.TargetRect;
        e.TargetRect = new Rect(target.X, target.Y, target.Width, target.Height + inset);
    }

    private void OnChatListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_chatListScroller is null || DataContext is not MainViewModel vm || !vm.HasMoreChats) return;

        var distanceFromBottom = _chatListScroller.Extent.Height - _chatListScroller.Viewport.Height - _chatListScroller.Offset.Y;
        if (distanceFromBottom < 100)
            vm.LoadMoreChats();
    }

    private void ToggleProjectSwitcher()
    {
        SetProjectSwitcherOpen(!_isProjectSwitcherOpen);
    }

    /// <summary>New project / Manage projects leave the chat list, so the drawer closes behind them.</summary>
    private void OnProjectSwitcherActionClick(object? sender, RoutedEventArgs e)
        => SetProjectSwitcherOpen(false);

    private void SetProjectSwitcherOpen(bool isOpen)
    {
        if (_projectSwitchRevealHost is null || _projectSwitchPanel is null)
            return;

        // Only one sidebar drawer at a time — see SetUnreadPanelOpen.
        if (isOpen && _isUnreadPanelOpen && DataContext is MainViewModel unreadVm)
            unreadVm.CloseUnreadPanelCommand.Execute(null);

        if (_isProjectSwitcherOpen == isOpen && _projectSwitchRevealHost.IsVisible == isOpen)
        {
            if (isOpen)
            {
                if (DataContext is MainViewModel currentVm)
                    RefreshProjectSwitcher(currentVm);
                FocusProjectSwitcherSearch();
            }
            return;
        }

        _isProjectSwitcherOpen = isOpen;

        if (isOpen && DataContext is MainViewModel vm)
            RefreshProjectSwitcher(vm);

        // The surface changes with it, so it lifts off the chat list as it grows and settles back as it
        // closes.
        _projectSwitcherRoot?.Classes.Set("open", isOpen);

        var ct = ReplaceCancellationTokenSource(ref _projectSwitcherDrawerCts).Token;
        _ = AnimateProjectSwitcherDrawerAsync(isOpen, ct);

        if (isOpen)
            Dispatcher.UIThread.Post(FocusProjectSwitcherSearch, DispatcherPriority.Input);
    }

    private void FocusProjectSwitcherSearch()
    {
        _projectFilterSearchBox?.Focus();
        _projectFilterSearchBox?.SelectAll();
    }

    private async Task AnimateProjectSwitcherDrawerAsync(bool isOpen, CancellationToken ct)
    {
        if (_projectSwitchRevealHost is null || _projectSwitchPanel is null)
            return;

        var completed = await AnimateRevealDrawerAsync(
            _projectSwitchRevealHost,
            isOpen,
            GetProjectSwitcherTargetHeight,
            SwitcherMotion,
            PlayProjectSwitcherCascade,
            ct);

        if (completed && !isOpen)
            OnProjectSwitcherClosed();
    }

    /// <summary>
    /// As the switcher grows, its parts flow out of its header: each slides down from it and fades in a
    /// moment after the one above. On the render thread, so building the rows never holds it up.
    /// </summary>
    private void PlayProjectSwitcherCascade()
    {
        if (_projectSwitchPanel is null || DataContext is MainViewModel { SettingsVM.ShowAnimations: false })
            return;

        var index = 0;
        foreach (var part in ProjectSwitcherParts(_projectSwitchPanel))
        {
            var delay = SwitcherCascadeStagger * Math.Min(index++, SwitcherCascadeSteps);
            SlideFadeEntrance.PlayComposited(part, SwitcherCascadeOffset, SwitcherCascadeDuration, delay);
        }
    }

    /// <summary>The switcher's visible parts, top to bottom, with its list row by row.</summary>
    private static IEnumerable<Control> ProjectSwitcherParts(StackPanel panel)
    {
        foreach (var part in panel.Children)
        {
            if (!part.IsVisible)
                continue;

            if (part is ScrollViewer { Content: Panel list })
            {
                foreach (var row in list.Children)
                {
                    if (row.IsVisible)
                        yield return row;
                }
            }
            else
            {
                yield return part;
            }
        }
    }

    /// <summary>How a sidebar drawer opens and closes.</summary>
    /// <param name="FadesIn">
    /// Whether the drawer fades in as a whole. A drawer whose parts play their own entrance leaves that to them.
    /// </param>
    private sealed record DrawerMotion(
        TimeSpan Open, TimeSpan Close, Easing OpenEasing, Easing CloseEasing, bool FadesIn);

    // The unread inbox drops open under its pill.
    private static readonly DrawerMotion InboxMotion = new(
        TimeSpan.FromMilliseconds(210),
        TimeSpan.FromMilliseconds(130),
        new SplineEasing(0.22, 0.08, 0.18, 1.0),
        new SplineEasing(0.22, 0.08, 0.18, 1.0),
        FadesIn: true);

    // The project switcher bursts out of its header and settles gently while its parts flow out after it,
    // then folds back into the header faster than it opened.
    private static readonly DrawerMotion SwitcherMotion = new(
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(180),
        new SplineEasing(0.05, 0.7, 0.1, 1.0),
        new SplineEasing(0.3, 0.0, 0.8, 0.15),
        FadesIn: false);

    private const double SwitcherCascadeOffset = -8;
    private const int SwitcherCascadeSteps = 9;
    private static readonly TimeSpan SwitcherCascadeDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan SwitcherCascadeStagger = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// Height-and-fade reveal of a sidebar drawer, moving as its <paramref name="motion"/> says. Opening,
    /// it calls <paramref name="revealing"/> once the drawer is shown and measured, so parts with an
    /// entrance of their own can start it. Returns false when a newer toggle superseded this run.
    /// </summary>
    private static async Task<bool> AnimateRevealDrawerAsync(
        Border host,
        bool isOpen,
        Func<double> measureTargetHeight,
        DrawerMotion motion,
        Action? revealing,
        CancellationToken ct)
    {
        var openingOpacity = motion.FadesIn ? 0.0 : 1.0;
        if (isOpen)
        {
            host.IsVisible = true;
            host.Height = 0;
            host.Opacity = openingOpacity;
        }

        var startHeight = Math.Max(0, double.IsNaN(host.Height) ? host.Bounds.Height : host.Height);
        if (!isOpen && startHeight <= 0)
            startHeight = measureTargetHeight();

        var endHeight = isOpen ? measureTargetHeight() : 0;
        host.Height = startHeight;
        if (isOpen)
            revealing?.Invoke();

        var animation = new Animation
        {
            Duration = isOpen ? motion.Open : motion.Close,
            Easing = isOpen ? motion.OpenEasing : motion.CloseEasing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(Layoutable.HeightProperty, startHeight),
                        new Setter(OpacityProperty, isOpen ? openingOpacity : 1.0),
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(Layoutable.HeightProperty, endHeight),
                        new Setter(OpacityProperty, isOpen ? 1.0 : 0.0),
                    }
                },
            }
        };

        try
        {
            await animation.RunAsync(host, ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        if (ct.IsCancellationRequested)
            return false;

        if (isOpen)
        {
            host.Height = double.NaN;
            host.Opacity = 1;
        }
        else
        {
            host.Height = 0;
            host.Opacity = 0;
            host.IsVisible = false;
        }

        return true;
    }

    private double GetProjectSwitcherTargetHeight()
    {
        if (_projectSwitchPanel is null)
            return 0;

        // The list is as wide as the header it opens under.
        var width = _projectSwitchButton?.Bounds.Width ?? 0;
        if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
            width = 250;

        _projectSwitchPanel.Measure(new Size(width, double.PositiveInfinity));
        var height = _projectSwitchPanel.DesiredSize.Height;
        if (height <= 0 || double.IsNaN(height) || double.IsInfinity(height))
            height = _projectSwitchPanel.Bounds.Height;
        if (height <= 0 || double.IsNaN(height) || double.IsInfinity(height))
            height = 216;

        // The project list caps its own height; this only guards a runaway measure, and must leave room
        // for the search box, a full list, the "more" line and the New / Manage footer.
        return Math.Ceiling(Math.Clamp(height, 1, 360));
    }

    private void OnProjectSwitcherClosed()
    {
        Interlocked.Exchange(ref _projectSwitcherRefreshQueued, 0);

        foreach (var (project, handler) in _projectFilterHandlers)
            project.PropertyChanged -= handler;
        _projectFilterHandlers.Clear();

        if (_projectFilterSearchBox is not null && !string.IsNullOrEmpty(_projectFilterSearchBox.Text))
            _projectFilterSearchBox.Text = "";
    }

    // ── Unread inbox drawer ──

    private void SetUnreadPanelOpen(bool isOpen)
    {
        if (_unreadRevealHost is null || _unreadPanel is null)
            return;

        if (_isUnreadPanelOpen == isOpen && _unreadRevealHost.IsVisible == isOpen)
            return;

        _isUnreadPanelOpen = isOpen;

        if (_unreadInboxToggle is not null)
        {
            if (isOpen && !_unreadInboxToggle.Classes.Contains("open"))
                _unreadInboxToggle.Classes.Add("open");
            else if (!isOpen)
                _unreadInboxToggle.Classes.Remove("open");
        }

        // Opening the unread drawer while the project drawer is open would stack two panels over
        // the chat list, so they behave as one exclusive group.
        if (isOpen && _isProjectSwitcherOpen)
            SetProjectSwitcherOpen(false);

        var ct = ReplaceCancellationTokenSource(ref _unreadDrawerCts).Token;
        _ = AnimateUnreadDrawerAsync(isOpen, ct);
    }

    private async Task AnimateUnreadDrawerAsync(bool isOpen, CancellationToken ct)
    {
        if (_unreadRevealHost is null || _unreadPanel is null)
            return;

        var completed = await AnimateRevealDrawerAsync(
            _unreadRevealHost,
            isOpen,
            GetUnreadDrawerTargetHeight,
            InboxMotion,
            revealing: null,
            ct);

        if (completed && !isOpen)
            _unreadInboxToggle?.Classes.Remove("open");
    }

    private double GetUnreadDrawerTargetHeight()
    {
        if (_unreadPanel is null)
            return 0;

        var width = _unreadInboxToggle?.Bounds.Width
            ?? _unreadInboxRoot?.Bounds.Width
            ?? 255;
        if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
            width = 255;

        _unreadPanel.Measure(new Size(width, double.PositiveInfinity));
        var height = _unreadPanel.DesiredSize.Height;
        if (height <= 0 || double.IsNaN(height) || double.IsInfinity(height))
            height = _unreadPanel.Bounds.Height;
        if (height <= 0 || double.IsNaN(height) || double.IsInfinity(height))
            height = 240;

        return Math.Ceiling(Math.Clamp(height, 1, 340));
    }

    private bool IsWithinUnreadInbox(object? source)
    {
        if (_unreadInboxRoot is null || source is not Visual visual)
            return false;

        return IsVisualOrDescendantOf(visual, _unreadInboxRoot);
    }

    private void OnProjectFilterSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (e.Key == Key.Escape)
        {
            SetProjectSwitcherOpen(false);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter)
            return;

        var first = GetProjectFilterCandidates(vm, _projectFilterSearchBox?.Text, out _).FirstOrDefault();
        if (first is null)
            return;

        ArmProjectChatListReveal();
        vm.SelectProjectFilterCommand.Execute(first.Project);
        SetProjectSwitcherOpen(false);
        e.Handled = true;
    }

    private bool IsWithinProjectSwitcher(object? source)
    {
        if (_projectSwitcherRoot is null || source is not Visual visual)
            return false;

        return IsVisualOrDescendantOf(visual, _projectSwitcherRoot);
    }

    private static bool IsVisualOrDescendantOf(Visual visual, Visual ancestor)
    {
        return ReferenceEquals(visual, ancestor)
            || visual.GetVisualAncestors().Any(candidate => ReferenceEquals(candidate, ancestor));
    }

    private void RefreshProjectSwitcher(MainViewModel vm)
    {
        UpdateProjectSwitcherSummary(vm);

        if (_projectFilterResults is null)
            return;

        foreach (var (project, handler) in _projectFilterHandlers)
            project.PropertyChanged -= handler;
        _projectFilterHandlers.Clear();

        if (!_isProjectSwitcherOpen)
            return;

        _projectFilterResults.Children.Clear();

        var query = _projectFilterSearchBox?.Text;
        var candidates = GetProjectFilterCandidates(vm, query, out var totalMatches).ToList();
        var totalChats = vm.DataStore.Data.Chats.Count;

        var isAll = !vm.SelectedProjectFilter.HasValue;
        _projectFilterResults.Children.Add(CreateProjectFilterRow(
            vm,
            project: null,
            title: Loc.ProjectSwitcher_AllChats,
            subtitle: string.Format(Loc.ProjectSwitcher_AllSubtitle, vm.Projects.Count, totalChats),
            isSelected: isAll,
            isRunning: false,
            countText: totalChats > 0 ? totalChats.ToString(Loc.Culture) : "",
            unreadCount: vm.UnreadChatCount));

        if (candidates.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(query) ? Loc.ProjectSwitcher_NoProjects : Loc.ProjectSwitcher_NoMatches,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 8, 8, 4),
                FontSize = 11,
            };
            empty[!TextBlock.ForegroundProperty] = empty.GetResourceObservable("Brush.TextTertiary").ToBinding();
            _projectFilterResults.Children.Add(empty);
        }

        foreach (var candidate in candidates)
        {
            var project = candidate.Project;
            var isActive = vm.SelectedProjectFilter == project.Id;
            var row = CreateProjectFilterRow(
                vm,
                project,
                project.Name,
                FormatProjectFilterSubtitle(project, candidate.ChatCount, candidate.LastActivity),
                isActive,
                project.IsRunning,
                candidate.ChatCount > 0 ? candidate.ChatCount.ToString(Loc.Culture) : "",
                vm.GetProjectUnreadCount(project.Id));

            _projectFilterResults.Children.Add(row);

            var capturedRow = row;
            PropertyChangedEventHandler handler = (_, args) =>
            {
                if (args.PropertyName != nameof(Project.IsRunning))
                    return;

                if (DataContext is MainViewModel currentVm)
                    QueueProjectSwitcherRefresh(currentVm);
                else
                    Dispatcher.UIThread.Post(() => capturedRow.IsVisible = true);
            };
            project.PropertyChanged += handler;
            _projectFilterHandlers.Add((project, handler));

            if (isActive && _isProjectSwitcherOpen)
                Dispatcher.UIThread.Post(() => row.BringIntoView(), DispatcherPriority.Loaded);
        }

        if (_projectFilterMoreText is not null)
        {
            var hiddenCount = Math.Max(0, totalMatches - candidates.Count);
            _projectFilterMoreText.IsVisible = hiddenCount > 0;
            _projectFilterMoreText.Text = hiddenCount > 0
                ? string.Format(Loc.ProjectSwitcher_MoreResults, hiddenCount)
                : "";
        }
    }

    private void QueueProjectSwitcherRefresh(MainViewModel vm)
    {
        if (!_isProjectSwitcherOpen || Interlocked.Exchange(ref _projectSwitcherRefreshQueued, 1) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _projectSwitcherRefreshQueued, 0);
            if (_isProjectSwitcherOpen && ReferenceEquals(DataContext, vm))
                RefreshProjectSwitcher(vm);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The switcher names where the chat list is: every chat, with how many projects there are to
    /// switch to ("All chats" alone never said there were any), or one project on its own hue.
    /// </summary>
    private void UpdateProjectSwitcherSummary(MainViewModel vm)
    {
        if (_projectSwitchTitleText is null || _projectSwitchMetaText is null || _projectSwitchBadge is null
            || _projectSwitchAllIcon is null || _projectSwitchInitial is null)
            return;

        var selectedProject = vm.SelectedProjectFilter.HasValue
            ? vm.Projects.FirstOrDefault(project => project.Id == vm.SelectedProjectFilter.Value)
            : null;

        if (selectedProject is null)
        {
            _projectSwitchTitleText.Text = Loc.ProjectSwitcher_AllChats;
            _projectSwitchMetaText.Text = vm.Projects.Count > 0
                ? ManagementText.Count(vm.Projects.Count, Loc.ProjectSwitcher_CountOne, Loc.ProjectSwitcher_CountMany)
                : "";
            _projectSwitchMetaText.IsVisible = vm.Projects.Count > 0;
            _projectSwitchBadge.ClearValue(Border.BackgroundProperty);
            _projectSwitchAllIcon.IsVisible = true;
            _projectSwitchInitial.IsVisible = false;
            return;
        }

        var badge = LaunchpadProjectBadge.For(selectedProject);
        _projectSwitchTitleText.Text = selectedProject.Name;
        _projectSwitchMetaText.IsVisible = false;
        _projectSwitchBadge.Background = badge.Background;
        _projectSwitchInitial.Text = badge.Initial;
        _projectSwitchInitial.Foreground = badge.Foreground;
        _projectSwitchAllIcon.IsVisible = false;
        _projectSwitchInitial.IsVisible = true;
    }

    private IEnumerable<ProjectFilterCandidate> GetProjectFilterCandidates(
        MainViewModel vm,
        string? query,
        out int totalMatches)
    {
        var normalizedQuery = query?.Trim();
        var hasQuery = !string.IsNullOrWhiteSpace(normalizedQuery);

        var candidates = vm.Projects
            .Select(project => new ProjectFilterCandidate(
                project,
                vm.GetProjectChatCount(project.Id),
                vm.GetProjectLastActivity(project.Id),
                hasQuery ? ScoreProjectFilterCandidate(project, normalizedQuery!) : 0))
            .Where(candidate => !hasQuery || candidate.SearchScore > 0)
            .ToList();

        totalMatches = candidates.Count;
        var take = hasQuery ? 30 : 8;

        return candidates
            .OrderByDescending(candidate => vm.SelectedProjectFilter == candidate.Project.Id)
            .ThenByDescending(candidate => candidate.Project.IsRunning)
            .ThenByDescending(candidate => hasQuery ? candidate.SearchScore : 0)
            .ThenByDescending(candidate => candidate.LastActivity ?? candidate.Project.CreatedAt)
            .ThenBy(candidate => candidate.Project.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(take);
    }

    private static double ScoreProjectFilterCandidate(Project project, string query)
        => FuzzySearch.Score(query, project.Name, project.WorkingDirectory, project.Instructions);

    private Button CreateProjectFilterRow(
        MainViewModel vm,
        Project? project,
        string title,
        string subtitle,
        bool isSelected,
        bool isRunning,
        string countText,
        int unreadCount)
    {
        // The open project's gear comes with its row. The others only show on hover, so each is built
        // on its row's first hover, keeping the drawer quick to open; until then an empty slot of the
        // same size holds its place, so nothing shifts when it appears.
        Control? edit = project is null ? null
            : isSelected ? CreateProjectEditButton(vm, project, isCurrent: true)
            : new Border { Classes = { "project-switcher-row-edit" } };
        var content = CreateProjectFilterRowContent(project, title, isRunning, countText, unreadCount, edit);
        var button = new Button
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Content = content,
        };
        ToolTip.SetTip(button, unreadCount > 0
            ? $"{subtitle}\n{string.Format(Loc.Culture, Loc.Unread_ProjectTooltip, unreadCount)}"
            : subtitle);
        button.Classes.Add("project-switcher-row");
        if (isSelected)
            button.Classes.Add("selected");

        if (project is not null && edit is Border slot)
        {
            void BuildGear(object? sender, PointerEventArgs e)
            {
                button.PointerEntered -= BuildGear;
                var gear = CreateProjectEditButton(vm, project, isCurrent: false);
                DockPanel.SetDock(gear, Dock.Right);
                content.Children[content.Children.IndexOf(slot)] = gear;
            }

            button.PointerEntered += BuildGear;
        }

        button.Click += (_, _) =>
        {
            ArmProjectChatListReveal();
            if (project is null)
                vm.ClearProjectFilterCommand.Execute(null);
            else
                vm.SelectProjectFilterCommand.Execute(project);

            SetProjectSwitcherOpen(false);
        };

        return button;
    }

    /// <summary>
    /// A project row's gear: sets that project up in Customize → Projects instead of switching to it.
    /// The open project's gear stays in view and is the <c>ProjectEditButton</c> landmark. It has no
    /// tip of its own: the rows are rebuilt while open, and a tip on a rebuilt item can close after its
    /// control has left the tree.
    /// </summary>
    private Button CreateProjectEditButton(MainViewModel vm, Project project, bool isCurrent)
    {
        var edit = new Button { Content = new PathIcon { Data = GetIconGeometry("Icon.Gear") } };
        if (isCurrent)
            edit.Name = "ProjectEditButton";
        edit.Classes.Add("subtle");
        edit.Classes.Add("project-switcher-row-edit");
        AutomationProperties.SetName(edit, Loc.ProjectSwitcher_EditTooltip);
        edit.Click += (_, e) =>
        {
            // Handled first, so the row it sits on does not also switch the chat list to the project.
            e.Handled = true;
            SetProjectSwitcherOpen(false);
            vm.EditProjectCommand.Execute(project);
        };
        return edit;
    }

    private DockPanel CreateProjectFilterRowContent(
        Project? project,
        string title,
        bool isRunning,
        string countText,
        int unreadCount,
        Control? edit)
    {
        var dock = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(6, 5),
        };

        if (edit is not null)
        {
            DockPanel.SetDock(edit, Dock.Right);
            dock.Children.Add(edit);
        }

        // An unread count replaces the chat count: while a project has new replies, "where are they"
        // matters more than "how big is this project".
        Control? trailing = unreadCount > 0
            ? CreateProjectUnreadPill(unreadCount)
            : string.IsNullOrWhiteSpace(countText)
                ? null
                : new TextBlock { Text = countText, Classes = { "project-switcher-row-count" } };
        if (trailing is not null)
        {
            DockPanel.SetDock(trailing, Dock.Right);
            dock.Children.Add(trailing);
        }

        var badge = CreateProjectBadge(project, isRunning);
        DockPanel.SetDock(badge, Dock.Left);
        dock.Children.Add(badge);

        dock.Children.Add(new TextBlock { Text = title, Classes = { "project-switcher-row-title" } });
        return dock;
    }

    /// <summary>
    /// A row's identity, as on the switcher itself: every chat, or the project's initial on its own hue.
    /// A dot marks a project whose chats are working.
    /// </summary>
    private Border CreateProjectBadge(Project? project, bool isRunning)
    {
        var grid = new Grid();
        IBrush background;
        if (project is null)
        {
            grid.Children.Add(new PathIcon
            {
                Data = GetIconGeometry("Icon.Chat"),
                Width = 10,
                Height = 10,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = GetThemeBrush("Brush.AccentDefault", Brushes.DodgerBlue),
            });
            background = GetThemeBrush("Brush.AccentGlowSoft", Brushes.Transparent);
        }
        else
        {
            var identity = LaunchpadProjectBadge.For(project);
            grid.Children.Add(new TextBlock
            {
                Text = identity.Initial,
                FontSize = 10.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = identity.Foreground,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            background = identity.Background;
        }

        if (isRunning)
        {
            grid.Children.Add(new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(4),
                Background = GetThemeBrush("Brush.AccentDefault", Brushes.DodgerBlue),
                BorderBrush = GetThemeBrush("Brush.Surface0", Brushes.Black),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = new Thickness(0, -2, -2, 0),
            });
        }

        return new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 9, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Background = background,
            Child = grid,
        };
    }

    /// <summary>
    /// Accent count pill marking a project that has chats with new replies, so the switcher answers
    /// "which project should I go back to" without opening each one.
    /// </summary>
    private Border CreateProjectUnreadPill(int unreadCount)
    {
        var label = new TextBlock
        {
            Text = unreadCount > 99 ? "99+" : unreadCount.ToString(Loc.Culture),
            FontSize = 9.8,
            FontWeight = FontWeight.Bold,
            Foreground = GetThemeBrush("Brush.TextOnAccent", Brushes.White),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        return new Border
        {
            MinWidth = 20,
            Padding = new Thickness(5, 1),
            CornerRadius = new CornerRadius(999),
            Background = GetThemeBrush("Brush.AccentDefault", Brushes.DodgerBlue),
            BorderThickness = new Thickness(0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0),
            Child = label,
        };
    }

    private Geometry? GetIconGeometry(string resourceKey)
    {
        return this.TryFindResource(resourceKey, ActualThemeVariant, out var resource) && resource is Geometry geometry
            ? geometry
            : null;
    }

    private IBrush GetThemeBrush(string resourceKey, IBrush fallback)
    {
        return this.TryFindResource(resourceKey, ActualThemeVariant, out var resource) && resource is IBrush brush
            ? brush
            : fallback;
    }

    private string FormatProjectFilterSubtitle(Project project, int chatCount, DateTimeOffset? lastActivity)
    {
        var countText = chatCount switch
        {
            0 => Loc.ProjectSwitcher_NoChats,
            1 => string.Format(Loc.Project_ChatCount, chatCount),
            _ => string.Format(Loc.Project_ChatCounts, chatCount),
        };

        if (lastActivity.HasValue)
            return string.Format(Loc.ProjectSwitcher_UpdatedSubtitle, countText, lastActivity.Value.ToString("MMM d", Loc.Culture));

        if (!string.IsNullOrWhiteSpace(project.WorkingDirectory))
            return string.Format(Loc.ProjectSwitcher_WorkspaceSubtitle, countText, Path.GetFileName(project.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

        return countText;
    }

    /// <summary>
    /// Keeps the chat's context menu associated with its row's <see cref="Chat"/> and prepares its
    /// lightweight chat-specific actions. Dynamic submenus are populated only when the context menu
    /// opens so startup does not materialize every tag and project target for every realized chat row.
    /// </summary>
    private void OnChatRowDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is not Control row || row.ContextMenu is not ContextMenu menu) return;

        var chat = row.DataContext as Chat;
        menu.Tag = chat;
        if (chat is not null)
            UpdateChatPinMenuItem(menu, chat);
    }

    /// <summary>
    /// Rebuilds a chat's "Move to Project" submenu when its context menu opens so it always reflects the
    /// live project list and marks the chat's current location. Includes an "All projects" target that
    /// moves the chat out of any project.
    /// </summary>
    private void OnChatContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        // The menu's own DataContext is not yet inherited when Opening fires, so resolve the row's
        // Chat from the Tag we stashed on DataContextChanged (falling back to DataContext if present).
        if ((menu.Tag as Chat ?? menu.DataContext as Chat) is Chat chat)
        {
            UpdateChatPinMenuItem(menu, chat);
            TryPopulateChatTagSubmenu(menu, chat);
            TryPopulateMoveToProjectSubmenu(menu, chat);
        }
    }

    private void UpdateChatPinMenuItem(ContextMenu menu, Chat chat)
    {
        var pinMenu = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(item => item.Name == "TogglePinChatMenuItem");
        if (pinMenu is not null)
            ChatMenuBuilder.UpdatePinItem(pinMenu, chat, GetIconGeometry);
    }

    private void TryPopulateChatTagSubmenu(ContextMenu menu, Chat chat)
    {
        if (DataContext is not MainViewModel vm) return;

        var tagMenu = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(item => item.Name == "ChatTagMenu" || (item.Header as string) == Loc.Menu_ChatTag);
        if (tagMenu is not null)
            ChatMenuBuilder.PopulateTagMenu(tagMenu, vm, chat, GetIconGeometry);
    }

    /// <summary>Finds the "Move to Project" submenu within a chat context menu and (re)builds its targets.</summary>
    private void TryPopulateMoveToProjectSubmenu(ContextMenu menu, Chat chat)
    {
        if (DataContext is not MainViewModel vm) return;

        var moveMenu = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(mi => mi.Name == "MoveToProjectMenu" || (mi.Header as string) == Loc.Menu_MoveToProject);
        if (moveMenu is not null)
            ChatMenuBuilder.PopulateMoveToProjectMenu(moveMenu, vm, chat, GetIconGeometry);
    }

    /// <summary>
    /// The management sidebars follow their page's open item one way, so rebuilding a list or
    /// filtering it never closes the page. A row the user picks opens that item.
    /// </summary>
    private void OnManagementListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || e.AddedItems.Count != 1)
            return;

        switch (list.DataContext, e.AddedItems[0])
        {
            case (ProjectsViewModel vm, Project project) when !ReferenceEquals(vm.SelectedProject, project):
                vm.SelectedProject = project;
                break;
            case (SkillsViewModel vm, Skill skill) when !ReferenceEquals(vm.SelectedSkill, skill):
                vm.SelectedSkill = skill;
                break;
            case (AgentsViewModel vm, LumiAgent agent) when !ReferenceEquals(vm.SelectedAgent, agent):
                vm.SelectedAgent = agent;
                break;
            case (McpServersViewModel vm, McpServer server) when !ReferenceEquals(vm.SelectedServer, server):
                vm.SelectedServer = server;
                break;
        }
    }

    private void UpdateTransparencyFallbackOpacity()
    {
        if (_acrylicFallback is null) return;

        var opacity = 0.8;
        if (ActualTransparencyLevel == WindowTransparencyLevel.None)
            opacity = 0.88;
        else if (ActualTransparencyLevel == WindowTransparencyLevel.Mica)
            opacity = 0.62;
        else if (ActualTransparencyLevel == WindowTransparencyLevel.Blur && OperatingSystem.IsMacOS())
            // macOS uses the Blur level (native vibrancy); lighten the fallback so the vibrancy shows
            // through. Gated to macOS because Linux (KDE/KWin) can also resolve to Blur, and its fallback
            // opacity must stay unchanged. Windows never resolves to Blur (it gets AcrylicBlur/Mica).
            opacity = 0.62;

        _acrylicFallback.Opacity = opacity;
    }

    /// <summary>Handles navigation when a search result is selected from the command palette.</summary>
    private void OnSearchResultSelected(MainViewModel vm, SearchResultItem result)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Navigate to the appropriate tab
            vm.SelectedNavIndex = result.NavIndex;

            // Select the appropriate item based on type
            switch (result.Item)
            {
                case Chat chat:
                    vm.OpenChatCommand.Execute(chat);
                    break;

                case Project project:
                    vm.ProjectsVM.SelectedProject = project;
                    break;

                case Skill skill:
                    vm.SkillsVM.SelectedSkill = skill;
                    break;

                case LumiAgent agent:
                    vm.AgentsVM.SelectedAgent = agent;
                    break;

                case Memory memory:
                    vm.MemoriesVM.SelectedMemory = memory;
                    break;

                case BackgroundJob job:
                    vm.JobsVM.SelectedJob = job;
                    break;

                case McpServer server:
                    vm.McpServersVM.SelectedServer = server;
                    break;

                default:
                    // Settings result — navigate to the specific settings page
                    if (result.SettingsPageIndex >= 0)
                    {
                        vm.SettingsVM.SearchQuery = "";
                        vm.SettingsVM.SelectedPageIndex = result.SettingsPageIndex;
                    }
                    break;
            }
        });
    }

    /// <summary>Whether the browser panel is currently visible.</summary>
    private bool IsBrowserOpen => _chatWorkspace?.IsBrowserOpen == true;

    private void EnsurePageViewLoaded(int index)
    {
        if (DataContext is not MainViewModel vm) return;

        switch (index)
        {
            case 1:
                if (_jobsHost is not null && _jobsHost.Content is null)
                    _jobsHost.Content = new BackgroundJobsView { DataContext = vm.JobsVM };
                break;
            case 2:
                if (_projectsHost is not null && _projectsHost.Content is null)
                    _projectsHost.Content = new ProjectsView { DataContext = vm.ProjectsVM };
                break;
            case 3:
                if (_skillsHost is not null && _skillsHost.Content is null)
                    _skillsHost.Content = new SkillsView { DataContext = vm.SkillsVM };
                break;
            case 4:
                if (_agentsHost is not null && _agentsHost.Content is null)
                    _agentsHost.Content = new AgentsView { DataContext = vm.AgentsVM };
                break;
            case 5:
                if (_memoriesHost is not null && _memoriesHost.Content is null)
                    _memoriesHost.Content = new MemoriesView { DataContext = vm.MemoriesVM };
                break;
            case 6:
                if (_mcpServersHost is not null && _mcpServersHost.Content is null)
                    _mcpServersHost.Content = new McpServersView { DataContext = vm.McpServersVM };
                break;
            case 7:
                if (_settingsHost is not null && _settingsHost.Content is null)
                {
                    _settingsView = new SettingsView { DataContext = vm.SettingsVM };
                    _settingsHost.Content = _settingsView;
                }
                else if (_settingsHost is not null)
                {
                    _settingsView = _settingsHost.Content as SettingsView;
                }
                break;
            case 8:
                if (_libraryHost is not null && _libraryHost.Content is null)
                    _libraryHost.Content = new LibraryView { DataContext = vm.LibraryVM };
                break;
            case MainViewModel.DiscoverNavIndex:
                if (_discoverHost is not null && _discoverHost.Content is null)
                    _discoverHost.Content = new DiscoverView { DataContext = vm.DiscoverVM };
                break;
        }
    }
}
