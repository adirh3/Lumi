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

namespace Lumi.Views;

public partial class MainWindow : Window
{
    private const double DefaultSidebarWidth = 280;
    private const double MinSidebarWidth = 240;
    private const double MaxSidebarWidth = 420;
    private static readonly Thickness NavButtonBasePadding = new(6, 0);
    private static readonly Thickness NavButtonCompactPadding = new(1, 0);
    private const double NavLabelGap = 3;
    private const double NavLabelMaxWidth = 52;

    private Panel? _onboardingPanel;
    private DockPanel? _mainPanel;
    private Border? _acrylicFallback;
    private Border? _chatIsland;
    private Border? _windowContentRoot;
    private Control?[] _pages = [];
    private Panel?[] _sidebarPanels = [];
    private Button?[] _navButtons = [];
    private Panel? _renameOverlay;
    private TextBox? _renameTextBox;
    private Border? _projectSwitcherRoot;
    private Button? _projectSwitchButton;
    private Border? _projectSwitchRevealHost;
    private Border? _projectSwitchPanel;
    private TextBox? _projectFilterSearchBox;
    private StackPanel? _projectFilterResults;
    private TextBlock? _projectSwitchTitleText;
    private TextBlock? _projectSwitchSubtitleText;
    private TextBlock? _projectSwitchCountText;
    private TextBlock? _projectFilterMoreText;
    private ScrollViewer? _chatListScroller;
    private readonly List<(Project Project, PropertyChangedEventHandler Handler)> _projectFilterHandlers = [];
    private ChatView? _chatView;
    private BrowserView? _browserView;
    private ContentControl? _browserHost;
    private ContentControl? _jobsHost;
    private ContentControl? _projectsHost;
    private ContentControl? _skillsHost;
    private ContentControl? _agentsHost;
    private ContentControl? _memoriesHost;
    private ContentControl? _mcpServersHost;
    private ContentControl? _settingsHost;
    private SettingsView? _settingsView;
    private Border? _browserIsland;
    private GridSplitter? _browserSplitter;
    private Grid? _chatContentGrid;
    private Border? _sidebarBorder;
    private Thumb? _sidebarResizeThumb;
    private Border? _navPill;
    private TextBlock?[] _navLabels = [];
    private Rect[] _navHitRegions = [];
    private ImplicitAnimationCollection?[] _navButtonOffsetAnimations = [];
    private CancellationTokenSource? _sidebarAnimCts;
    private CancellationTokenSource? _navHoverIntentCts;
    private Border? _diffIsland;
    private ContentControl? _diffHost;
    private DiffView? _diffView;
    private TextBlock? _diffFileNameText;
    private List<GitFileChangeViewModel>? _lastGitChangesList;
    private Border? _planIsland;
    private CancellationTokenSource? _previewAnimCts;
    private CancellationTokenSource? _projectSwitcherDrawerCts;
    private bool _suppressSelectionSync;
    private bool _isProjectChatListRevealArmed;
    private bool _isProjectChatListRevealQueued;
    private bool _isProjectSwitcherOpen;
    private int _chatListRevealVersion;
    private CancellationTokenSource? _browserAnimCts;
    private CancellationTokenSource? _shellAnimCts;
    private int _currentShellIndex = -1;
    private int _activeNavIndex = -1;
    private int _hoveredNavIndex = -1;
    private int _pendingNavHoverIndex = -1;
    private int _navPillScaleAnimationVersion;
    private bool _isNavPillWidthLocked;
    private double _expandedSidebarWidth = DefaultSidebarWidth;
    private sealed record ProjectFilterCandidate(Project Project, int ChatCount, DateTimeOffset? LastActivity, double SearchScore);

    private double[] _navBaseButtonWidths = [];
    private double[] _navMinButtonWidths = [];
    private MainViewModel? _wiredVm;
    private const int NavHoverIntentDelayMs = 85;

    public MainWindow()
    {
        InitializeComponent();
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 38;

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
            RestoreWindowBounds();
            UpdateTransparencyFallbackOpacity();
            ApplyWindowContentPaddingForState();
        };
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        DisposeCancellationTokenSource(ref _shellAnimCts);
        DisposeCancellationTokenSource(ref _sidebarAnimCts);
        DisposeCancellationTokenSource(ref _navHoverIntentCts);
        DisposeCancellationTokenSource(ref _titleAnimCts);
        DisposeCancellationTokenSource(ref _browserAnimCts);
        DisposeCancellationTokenSource(ref _previewAnimCts);
        DisposeCancellationTokenSource(ref _chatListRevealCts);
        DisposeCancellationTokenSource(ref _projectSwitcherDrawerCts);
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
        _mainPanel = this.FindControl<DockPanel>("MainPanel");
        _acrylicFallback = this.FindControl<Border>("AcrylicFallback");
        _chatIsland = this.FindControl<Border>("ChatIsland");
        _windowContentRoot = this.FindControl<Border>("WindowContentRoot");
        _sidebarBorder = this.FindControl<Border>("SidebarBorder");
        _sidebarResizeThumb = this.FindControl<Thumb>("SidebarResizeThumb");
        _navPill = this.FindControl<Border>("NavPill");

        _pages =
        [
            this.FindControl<Grid>("ChatContentGrid"),         // 0 = Chat (container grid)
            this.FindControl<Control>("PageJobs"),             // 1
            this.FindControl<Control>("PageProjects"),         // 2
            this.FindControl<Control>("PageSkills"),           // 3
            this.FindControl<Control>("PageAgents"),           // 4
            this.FindControl<Control>("PageMemories"),         // 5
            this.FindControl<Control>("PageMcpServers"),       // 6
            this.FindControl<Control>("PageSettings"),         // 7
        ];

        _sidebarPanels =
        [
            this.FindControl<Panel>("SidebarChat"),            // 0
            this.FindControl<Panel>("SidebarJobs"),            // 1
            this.FindControl<Panel>("SidebarProjects"),        // 2
            this.FindControl<Panel>("SidebarSkills"),          // 3
            this.FindControl<Panel>("SidebarAgents"),          // 4
            this.FindControl<Panel>("SidebarMemories"),        // 5
            this.FindControl<Panel>("SidebarMcpServers"),      // 6
            this.FindControl<Panel>("SidebarSettings"),        // 7
        ];

        _navButtons =
        [
            this.FindControl<Button>("NavChat"),
            this.FindControl<Button>("NavJobs"),
            this.FindControl<Button>("NavProjects"),
            this.FindControl<Button>("NavSkills"),
            this.FindControl<Button>("NavAgents"),
            this.FindControl<Button>("NavMemories"),
            this.FindControl<Button>("NavMcpServers"),
            this.FindControl<Button>("NavSettings"),
        ];

        ApplyAgentAutomationLandmarks();

        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        WireNavHoverEvents();
        Dispatcher.UIThread.Post(InitializeNavHoverVisuals, DispatcherPriority.Loaded);

        _renameOverlay = this.FindControl<Panel>("RenameOverlay");
        _renameTextBox = this.FindControl<TextBox>("RenameTextBox");
        _projectSwitcherRoot = this.FindControl<Border>("ProjectSwitcherRoot");
        _projectSwitchButton = this.FindControl<Button>("ProjectSwitchButton");
        _projectSwitchRevealHost = this.FindControl<Border>("ProjectSwitchRevealHost");
        _projectSwitchPanel = this.FindControl<Border>("ProjectSwitchPanel");
        _projectFilterSearchBox = this.FindControl<TextBox>("ProjectFilterSearchBox");
        _projectFilterResults = this.FindControl<StackPanel>("ProjectFilterResults");
        _projectSwitchTitleText = this.FindControl<TextBlock>("ProjectSwitchTitleText");
        _projectSwitchSubtitleText = this.FindControl<TextBlock>("ProjectSwitchSubtitleText");
        _projectSwitchCountText = this.FindControl<TextBlock>("ProjectSwitchCountText");
        _projectFilterMoreText = this.FindControl<TextBlock>("ProjectFilterMoreText");

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

        _chatView = this.FindControl<ChatView>("PageChat");

        // Set OS-specific shortcut label
        var shortcutLabel = this.FindControl<TextBlock>("SearchButtonShortcut");
        if (shortcutLabel is not null && RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            shortcutLabel.Text = "⌘K";

        _browserHost = this.FindControl<ContentControl>("BrowserHost");
        _jobsHost = this.FindControl<ContentControl>("PageJobsHost");
        _projectsHost = this.FindControl<ContentControl>("PageProjectsHost");
        _skillsHost = this.FindControl<ContentControl>("PageSkillsHost");
        _agentsHost = this.FindControl<ContentControl>("PageAgentsHost");
        _memoriesHost = this.FindControl<ContentControl>("PageMemoriesHost");
        _mcpServersHost = this.FindControl<ContentControl>("PageMcpServersHost");
        _settingsHost = this.FindControl<ContentControl>("PageSettingsHost");
        _browserIsland = this.FindControl<Border>("BrowserIsland");
        _browserSplitter = this.FindControl<GridSplitter>("BrowserSplitter");
        _chatContentGrid = this.FindControl<Grid>("ChatContentGrid");
        _diffIsland = this.FindControl<Border>("DiffIsland");
        _diffHost = this.FindControl<ContentControl>("DiffHost");
        _diffFileNameText = this.FindControl<TextBlock>("DiffFileNameText");
        _planIsland = this.FindControl<Border>("PlanIsland");

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
            "Agent map: navigation indices are Chat=0, Jobs=1, Projects=2, Skills=3, Lumis=4, Memories=5, MCP Servers=6, Settings=7.");

        var navNames = new[]
        {
            "NavChat - navigation index 0 - Chat",
            "NavJobs - navigation index 1 - Background Jobs",
            "NavProjects - navigation index 2 - Projects",
            "NavSkills - navigation index 3 - Skills",
            "NavAgents - navigation index 4 - Lumis",
            "NavMemories - navigation index 5 - Memories",
            "NavMcpServers - navigation index 6 - MCP Servers",
            "NavSettings - navigation index 7 - Settings",
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
            ("PageAgents", "Page 4 Lumis"),
            ("PageMemories", "Page 5 Memories"),
            ("PageMcpServers", "Page 6 MCP Servers"),
            ("PageSettings", "Page 7 Settings"),
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

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // If tray behavior is enabled, hide instead of closing.
        if (DataContext is MainViewModel vm && vm.SettingsVM.MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        CaptureBoundsToSettings();
        base.OnClosing(e);
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

    /// <summary>
    /// Captures current window bounds into settings without persisting to disk.
    /// Must be called on the UI thread while the window is still visible.
    /// </summary>
    private void CaptureBoundsToSettings()
    {
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

        if (DataContext is MainViewModel vm)
            vm.DataStore.Data.Settings.SidebarWidth = clampedWidth;
    }

    private void PersistSidebarWidth()
    {
        if (DataContext is not MainViewModel vm)
            return;

        var clampedWidth = ClampSidebarWidth(_expandedSidebarWidth);
        vm.DataStore.Data.Settings.SidebarWidth = clampedWidth;
        _ = vm.DataStore.SaveAsync();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (DataContext is not MainViewModel vm || !vm.IsOnboarded) return;

        // Don't intercept shortcuts while recording a global hotkey
        var settingsPage = _settingsView;
        if (settingsPage?.IsRecordingHotkey == true) return;

        if (TryHandleChatHistoryNavigationKey(vm, e))
            return;

        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
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

        // ── Ctrl+N — New chat ──
        if (ctrl && !alt && !shift && e.Key == Key.N)
        {
            vm.NewChatCommand.Execute(null);
            Dispatcher.UIThread.Post(() => _chatView?.FocusComposer(), DispatcherPriority.Input);
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
            vm.SelectedNavIndex = 7;
            e.Handled = true;
            return;
        }

        // ── Ctrl+1..8 — Tab navigation ──
        if (ctrl && !alt && !shift)
        {
            var tabIndex = e.Key switch
            {
                Key.D1 => 0,
                Key.D2 => 1,
                Key.D3 => 2,
                Key.D4 => 3,
                Key.D5 => 4,
                Key.D6 => 5,
                Key.D7 => 6,
                Key.D8 => 7,
                _ => -1
            };
            if (tabIndex >= 0)
            {
                vm.SelectedNavIndex = tabIndex;
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
                ApplyProjectLabelsToChats(vm);
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

            // Wire search overlay result selection
            vm.SearchOverlayVM.ResultSelected += result => OnSearchResultSelected(vm, result);

            // Wire settings for density and font size
            vm.SettingsVM.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SettingsViewModel.FontSize))
                    ApplyFontSize(vm.SettingsVM.FontSize);
            };

            // Apply initial font size
            ApplyFontSize(vm.SettingsVM.FontSize);

            // Wire browser panel show/hide (per-chat aware)
            vm.ChatVM.BrowserShowRequested += (chatId) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    // Only show browser panel if the requesting chat is currently active
                    if (vm.ActiveChatId == chatId)
                    {
                        ShowBrowserPanel(chatId);
                        vm.ChatVM.IsBrowserOpen = IsBrowserOpen;
                    }
                });
            };
            vm.ChatVM.BrowserHideRequested += () =>
            {
                Dispatcher.UIThread.Post(() => { HideBrowserPanel(); vm.ChatVM.IsBrowserOpen = IsBrowserOpen; });
            };

            // Close browser button
            var closeBrowserBtn = this.FindControl<Button>("CloseBrowserButton");
            if (closeBrowserBtn is not null)
                closeBrowserBtn.Click += (_, _) => { HideBrowserPanel(); vm.ChatVM.IsBrowserOpen = false; };

            // Wire diff panel show/hide
            vm.ChatVM.DiffShowRequested += (item) =>
            {
                Dispatcher.UIThread.Post(() => ShowDiffPanel(item));
            };
            vm.ChatVM.DiffHideRequested += () =>
            {
                Dispatcher.UIThread.Post(() => HideDiffPanel());
            };

            // Close diff button
            var closeDiffBtn = this.FindControl<Button>("CloseDiffButton");
            if (closeDiffBtn is not null)
                closeDiffBtn.Click += (_, _) => { HideDiffPanel(); if (DataContext is MainViewModel m) m.ChatVM.IsDiffOpen = false; };

            // Wire git changes panel show
            vm.ChatVM.GitChangesShowRequested += (files) =>
            {
                Dispatcher.UIThread.Post(() => ShowGitChangesPanel(files));
            };

            // Wire plan panel show/hide
            vm.ChatVM.PlanShowRequested += () =>
            {
                Dispatcher.UIThread.Post(() => ShowPlanPanel());
            };
            vm.ChatVM.PlanHideRequested += () =>
            {
                Dispatcher.UIThread.Post(() => HidePlanPanel());
            };

            // Plan close button
            var closePlanBtn = this.FindControl<Button>("ClosePlanButton");
            if (closePlanBtn is not null)
                closePlanBtn.Click += (_, _) => { HidePlanPanel(); if (DataContext is MainViewModel m) m.ChatVM.IsPlanOpen = false; };

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

                        Dispatcher.UIThread.Post(() =>
                        {
                            AttachListBoxHandlers();
                            SyncListBoxSelection(vm.ActiveChatId);
                            _chatView?.FocusComposer();
                        }, DispatcherPriority.Loaded);
                    }
                }
                else if (args.PropertyName == nameof(MainViewModel.ActiveChatId))
                {
                    // Hide browser/diff/plan when switching chats
                    HideBrowserPanel();
                    HideDiffPanel();
                    HidePlanPanel();
                    Dispatcher.UIThread.Post(() => SyncListBoxSelection(vm.ActiveChatId),
                        DispatcherPriority.Loaded);
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
                    RefreshProjectSwitcher(vm);
                    if (!_isProjectChatListRevealQueued)
                        QueueProjectChatListReveal();
                    _isProjectChatListRevealQueued = false;
                }
                else if (args.PropertyName == nameof(MainViewModel.IsSidebarCollapsed))
                {
                    AnimateSidebarCollapse(vm.IsSidebarCollapsed);
                }
            };

            // When project list changes, rebuild filter bar
            vm.Projects.CollectionChanged += (_, _) =>
            {
                Dispatcher.UIThread.Post(() => RefreshProjectSwitcher(vm), DispatcherPriority.Loaded);
            };

            // When chat groups are rebuilt, re-attach ListBox handlers, sync selection, and set project labels
            vm.ChatGroups.CollectionChanged += (_, _) =>
            {
                var shouldRevealChats = _isProjectChatListRevealArmed;
                if (shouldRevealChats)
                {
                    _isProjectChatListRevealArmed = false;
                    _isProjectChatListRevealQueued = true;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    AttachListBoxHandlers();
                    SyncListBoxSelection(vm.ActiveChatId);
                    ApplyProjectLabelsToChats(vm);
                    ApplyMoveToProjectMenus(vm);
                    if (shouldRevealChats)
                        QueueProjectChatListReveal();
                }, DispatcherPriority.Loaded);
            };
        }
    }

    private void UpdateOnboarding(bool isOnboarded)
    {
        if (_onboardingPanel is not null) _onboardingPanel.IsVisible = !isOnboarded;
        if (_mainPanel is not null) _mainPanel.IsVisible = true;

        // Animate the main app entrance when onboarding completes
        if (isOnboarded && _mainPanel is not null)
        {
            AnimateShellSectionChange(_pages.FirstOrDefault(p => p?.IsVisible == true),
                _sidebarPanels.FirstOrDefault(p => p?.IsVisible == true), CancellationToken.None);
        }
    }

    private void ShowPage(int index)
    {
        EnsurePageViewLoaded(index);

        var sectionChanged = _currentShellIndex != index;
        _currentShellIndex = index;

        for (int i = 0; i < _pages.Length; i++)
        {
            if (_pages[i] is not null)
                _pages[i]!.IsVisible = i == index;
        }

        // Show the matching sidebar panel
        for (int i = 0; i < _sidebarPanels.Length; i++)
        {
            if (_sidebarPanels[i] is not null)
                _sidebarPanels[i]!.IsVisible = i == index;
        }

        // Hide/show browser/diff when navigating away from / back to chat
        if (index != 0)
        {
            // Leaving chat — fully close preview panels
            HideBrowserPanel();
            HideDiffPanel();
            HidePlanPanel();
        }
        else if (_browserIsland is { IsVisible: true })
        {
            // Returning to chat with browser open — show the overlay and refresh bounds
            _browserView?.ShowCurrentController();
            Dispatcher.UIThread.Post(() => _browserView?.RefreshBounds(), DispatcherPriority.Loaded);
        }

        // When projects tab is shown, update chat counts and refresh selected project chats
        if (index == 1 && DataContext is MainViewModel vm)
        {
            vm.ProjectsVM.RefreshSelectedProjectChats();
            Dispatcher.UIThread.Post(() => ApplyProjectChatCounts(vm), DispatcherPriority.Loaded);
        }

        // When settings tab is shown, refresh stats
        if (index == 6 && DataContext is MainViewModel svm)
        {
            if (svm.SettingsVM.SelectedPageIndex < 0)
                svm.SettingsVM.SelectedPageIndex = 0;
            svm.SettingsVM.RefreshStats();
        }

        // When MCP tab is shown and no server is selected/editing, auto-open browse catalog
        if (index == 5 && DataContext is MainViewModel mcpvm)
        {
            if (!mcpvm.McpServersVM.IsEditing && mcpvm.McpServersVM.SelectedServer is null)
                mcpvm.McpServersVM.BrowseCatalogCommand.Execute(null);
        }

        if (sectionChanged && _mainPanel?.IsVisible == true)
        {
            var shellCt = ReplaceCancellationTokenSource(ref _shellAnimCts).Token;
            AnimateShellSectionChange(_pages[index], _sidebarPanels[index], shellCt);
        }
    }

    private async void AnimateShellSectionChange(Control? page, Control? sidebar, CancellationToken ct)
    {
        await Task.WhenAll(
            AnimateShellEntranceAsync(page, 10.0, TimeSpan.FromMilliseconds(240), ct),
            AnimateShellEntranceAsync(sidebar, 6.0, TimeSpan.FromMilliseconds(190), ct));
    }

    private static async Task AnimateShellEntranceAsync(
        Control? control,
        double offsetY,
        TimeSpan duration,
        CancellationToken ct)
    {
        if (control is null || !control.IsVisible) return;

        control.RenderTransform = new TranslateTransform(0, offsetY);
        control.Opacity = 0;

        var anim = new Avalonia.Animation.Animation
        {
            Duration = duration,
            Easing = new SplineEasing(0.24, 0.08, 0.24, 1.0),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0.0),
                        new Setter(TranslateTransform.YProperty, offsetY),
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1.0),
                        new Setter(TranslateTransform.YProperty, 0.0),
                    }
                },
            }
        };

        try
        {
            await anim.RunAsync(control, ct);
        }
        catch (OperationCanceledException)
        {
            // Ignore; next navigation animation takes over.
        }
        catch (ObjectDisposedException)
        {
            // Ignore; control was disposed during a rapid section transition.
        }
        catch (InvalidOperationException)
        {
            // Ignore; visual tree changed while the animation was running.
        }

        control.Opacity = 1;
        control.RenderTransform = null;
    }

    private async void AnimateSidebarCollapse(bool collapse)
    {
        if (_sidebarBorder is null) return;

        if (_sidebarResizeThumb is not null && !collapse)
            _sidebarResizeThumb.IsVisible = true;

        var ct = ReplaceCancellationTokenSource(ref _sidebarAnimCts).Token;
        var from = collapse ? GetCurrentSidebarWidth() : 0.0;
        var to = collapse ? 0.0 : _expandedSidebarWidth;

        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(200),
            Easing = new SplineEasing(0.16, 1, 0.3, 1),
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

        try
        {
            await anim.RunAsync(_sidebarBorder, ct);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }

        _sidebarBorder.Width = to;

        if (_sidebarResizeThumb is not null)
            _sidebarResizeThumb.IsVisible = !collapse;
    }

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

    private void UpdateNavHighlight(int index)
    {
        _activeNavIndex = index;
        for (int i = 0; i < _navButtons.Length; i++)
        {
            var btn = _navButtons[i];
            if (btn is null) continue;

            if (i == index)
                btn.Classes.Add("active");
            else
                btn.Classes.Remove("active");
        }

        if (_hoveredNavIndex >= 0)
            ApplyNavButtonLayout(_hoveredNavIndex);
    }

    private void InitializeNavHoverVisuals()
    {
        CaptureNavLayoutMetrics();
        InitNavLabelVisuals();
        LockNavPillWidth();
        ApplyNavButtonLayout(-1);
    }

    private void InitNavLabelVisuals()
    {
        EnsureNavButtonOffsetAnimationCacheSize();

        for (var i = 0; i < _navButtons.Length; i++)
            SetNavButtonOffsetAnimation(i, enabled: true);
    }

    private void EnsureNavButtonOffsetAnimationCacheSize()
    {
        if (_navButtonOffsetAnimations.Length != _navButtons.Length)
            Array.Resize(ref _navButtonOffsetAnimations, _navButtons.Length);
    }

    private void CaptureNavLayoutMetrics()
    {
        if (_navButtons.Length == 0)
            return;

        if (_navButtons.Any(static button => button?.Bounds.Width <= 0))
        {
            Dispatcher.UIThread.Post(InitializeNavHoverVisuals, DispatcherPriority.Render);
            return;
        }

        _navLabels = new TextBlock?[_navButtons.Length];
        _navHitRegions = new Rect[_navButtons.Length];
        _navBaseButtonWidths = new double[_navButtons.Length];
        _navMinButtonWidths = new double[_navButtons.Length];
        EnsureNavButtonOffsetAnimationCacheSize();
        var centers = new double[_navButtons.Length];

        for (var i = 0; i < _navButtons.Length; i++)
        {
            if (_navButtons[i] is not Button button)
                continue;

            _navLabels[i] = button
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(textBlock => textBlock.Classes.Contains("nav-label"));

            _navBaseButtonWidths[i] = button.Bounds.Width;
            var contentWidth = Math.Max(0, button.Bounds.Width - (NavButtonBasePadding.Left + NavButtonBasePadding.Right));
            _navMinButtonWidths[i] = contentWidth + NavButtonCompactPadding.Left + NavButtonCompactPadding.Right;

            if (_navPill is not null)
            {
                var topLeft = button.TranslatePoint(default, _navPill);
                centers[i] = (topLeft?.X ?? 0) + (button.Bounds.Width / 2d);
            }
        }

        if (_activeNavIndex < 0)
            _activeNavIndex = Array.FindIndex(_navButtons, button => button?.Classes.Contains("active") == true);

        if (_navPill is null)
            return;

        for (var i = 0; i < _navHitRegions.Length; i++)
        {
            var left = i == 0 ? 0 : (centers[i - 1] + centers[i]) / 2d;
            var right = i == _navHitRegions.Length - 1 ? _navPill.Bounds.Width : (centers[i] + centers[i + 1]) / 2d;
            _navHitRegions[i] = new Rect(left, 0, Math.Max(0, right - left), _navPill.Bounds.Height);
        }
    }

    private void LockNavPillWidth()
    {
        if (_isNavPillWidthLocked || _navPill is null)
            return;

        var width = _navPill.Bounds.Width;
        if (width <= 0)
        {
            Dispatcher.UIThread.Post(LockNavPillWidth, DispatcherPriority.Render);
            return;
        }

        _navPill.Width = Math.Ceiling(width);
        _isNavPillWidthLocked = true;
    }

    private void WireNavHoverEvents()
    {
        if (_navPill is not null)
        {
            _navPill.PointerEntered += OnNavPillPointerEntered;
            _navPill.PointerMoved += OnNavPillPointerMoved;
            _navPill.PointerExited += OnNavPillPointerExited;
            _navPill.AddHandler(PointerPressedEvent, OnNavPillPointerPressed, RoutingStrategies.Tunnel);
        }
    }

    private void OnNavPillPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Border pill)
            return;

        UpdateHoveredNavButton(pill, e);
    }

    private void UpdateHoveredNavButton(Border pill, PointerEventArgs e)
    {
        RequestHoveredNavButton(GetHoveredNavButtonIndex(pill, e.GetPosition(pill)));
    }

    private int GetHoveredNavButtonIndex(Border pill, Point pointerPosition)
    {
        if (_navHitRegions.Length == _navButtons.Length)
        {
            for (var i = 0; i < _navHitRegions.Length; i++)
            {
                if (_navButtons[i] is null)
                    continue;

                if (_navHitRegions[i].Contains(pointerPosition))
                    return i;
            }
        }

        for (var i = 0; i < _navButtons.Length; i++)
        {
            if (_navButtons[i] is not Button button || !button.IsVisible)
                continue;

            var topLeft = button.TranslatePoint(default, pill);
            if (topLeft is null)
                continue;

            var bounds = new Rect(topLeft.Value, button.Bounds.Size);
            if (bounds.Contains(pointerPosition))
                return i;
        }

        return -1;
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

        ExecuteNavButton(button);
        e.Handled = true;
    }

    private void SetHoveredNavButton(int index)
    {
        if (_hoveredNavIndex == index)
            return;

        _hoveredNavIndex = index;
        ApplyNavButtonLayout(index);
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
        }, DispatcherPriority.Input);
    }

    private void ApplyNavButtonLayout(int hoveredIndex)
    {
        if (_navBaseButtonWidths.Length != _navButtons.Length || _navMinButtonWidths.Length != _navButtons.Length)
            CaptureNavLayoutMetrics();

        if (_navBaseButtonWidths.Length != _navButtons.Length || _navMinButtonWidths.Length != _navButtons.Length)
            return;

        var reductions = hoveredIndex >= 0
            ? DistributeNavReduction(hoveredIndex, GetHoveredNavExpansionWidth(hoveredIndex))
            : new double[_navButtons.Length];

        for (var i = 0; i < _navButtons.Length; i++)
        {
            if (_navButtons[i] is not Button button)
                continue;

            var isHovered = i == hoveredIndex;
            SetNavButtonOffsetAnimation(i, enabled: !isHovered);
            SetClass(button, "hovered", isHovered);
            button.Padding = GetNavButtonPadding(reductions[i]);

            if (i < _navLabels.Length && _navLabels[i] is TextBlock label)
                label.MaxWidth = isHovered ? MeasureNavLabelWidth(label) : 0;
        }
    }

    private void SetNavButtonOffsetAnimation(int index, bool enabled)
    {
        if (index < 0 || index >= _navButtons.Length || _navButtons[index] is not Button button)
            return;

        var visual = ElementComposition.GetElementVisual(button);
        if (visual is null)
            return;

        if (!enabled)
        {
            if (visual.ImplicitAnimations is not null)
                visual.ImplicitAnimations = null;
            return;
        }

        EnsureNavButtonOffsetAnimationCacheSize();

        var implicitAnims = _navButtonOffsetAnimations[index];
        if (implicitAnims is null)
        {
            implicitAnims = CreateNavButtonOffsetAnimation(visual);
            _navButtonOffsetAnimations[index] = implicitAnims;
        }

        if (!ReferenceEquals(visual.ImplicitAnimations, implicitAnims))
            visual.ImplicitAnimations = implicitAnims;
    }

    private static ImplicitAnimationCollection CreateNavButtonOffsetAnimation(CompositionVisual visual)
    {
        var compositor = visual.Compositor;
        var offsetAnim = compositor.CreateVector3DKeyFrameAnimation();
        offsetAnim.Target = "Offset";
        offsetAnim.InsertExpressionKeyFrame(1f, "this.FinalValue");
        offsetAnim.Duration = TimeSpan.FromMilliseconds(140);

        var implicitAnims = compositor.CreateImplicitAnimationCollection();
        implicitAnims["Offset"] = offsetAnim;
        return implicitAnims;
    }

    private double[] DistributeNavReduction(int hoveredIndex, double requiredReduction)
    {
        var reductions = new double[_navButtons.Length];
        var remaining = requiredReduction;
        var remainingIndices = Enumerable.Range(0, _navButtons.Length)
            .Where(index => index != hoveredIndex && index != _activeNavIndex && _navButtons[index] is not null)
            .ToList();

        while (remaining > 0.01 && remainingIndices.Count > 0)
        {
            var share = remaining / remainingIndices.Count;
            for (var i = remainingIndices.Count - 1; i >= 0; i--)
            {
                var index = remainingIndices[i];
                var remainingCapacity = (_navBaseButtonWidths[index] - _navMinButtonWidths[index]) - reductions[index];
                if (remainingCapacity <= 0)
                {
                    remainingIndices.RemoveAt(i);
                    continue;
                }

                var appliedReduction = Math.Min(share, remainingCapacity);
                reductions[index] += appliedReduction;
                remaining -= appliedReduction;
                if (remainingCapacity - appliedReduction <= 0.01)
                    remainingIndices.RemoveAt(i);
            }
        }

        return reductions;
    }

    private double GetHoveredNavExpansionWidth(int hoveredIndex)
    {
        if (hoveredIndex < 0 || hoveredIndex >= _navLabels.Length || _navLabels[hoveredIndex] is not TextBlock label)
            return 0;

        var labelWidth = MeasureNavLabelWidth(label);
        var desiredExpansionWidth = labelWidth > 0 ? labelWidth + NavLabelGap : 0;
        return Math.Min(desiredExpansionWidth, GetAvailableNavReductionWidth(hoveredIndex));
    }

    private double GetAvailableNavReductionWidth(int hoveredIndex)
    {
        var availableWidth = 0d;
        for (var i = 0; i < _navButtons.Length; i++)
        {
            if (i == hoveredIndex || i == _activeNavIndex || _navButtons[i] is null)
                continue;

            availableWidth += _navBaseButtonWidths[i] - _navMinButtonWidths[i];
        }

        return availableWidth;
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
        return Math.Min(probe.DesiredSize.Width, NavLabelMaxWidth);
    }

    private static Thickness GetNavButtonPadding(double widthReduction)
    {
        var horizontalPadding = Math.Clamp(
            NavButtonBasePadding.Left - (widthReduction / 2d),
            NavButtonCompactPadding.Left,
            NavButtonBasePadding.Left);

        return new Thickness(horizontalPadding, NavButtonBasePadding.Top, horizontalPadding, NavButtonBasePadding.Bottom);
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
        AnimateNavPillScale(pill, new Avalonia.Vector3D(1.12, 1.12, 1), TimeSpan.FromMilliseconds(250));
    }

    private void OnNavPillPointerExited(object? sender, PointerEventArgs e)
    {
        RequestHoveredNavButton(-1);

        if (sender is Border pill)
            AnimateNavPillScale(pill, new Avalonia.Vector3D(1, 1, 1), TimeSpan.FromMilliseconds(250));
    }

    private async void AnimateNavPillScale(Border pill, Avalonia.Vector3D targetScale, TimeSpan duration)
    {
        var visual = ElementComposition.GetElementVisual(pill);
        if (visual is null) return;

        var version = ++_navPillScaleAnimationVersion;
        var w = pill.Bounds.Width;
        var h = pill.Bounds.Height;
        visual.CenterPoint = new Avalonia.Vector3D(w / 2, h / 2, 0);
        visual.StopAnimation("Scale");

        var compositor = visual.Compositor;
        var scaleAnim = compositor.CreateVector3DKeyFrameAnimation();
        scaleAnim.Target = "Scale";
        scaleAnim.InsertKeyFrame(1f, targetScale);
        scaleAnim.Duration = duration;
        visual.StartAnimation("Scale", scaleAnim);

        try
        {
            await Task.Delay(duration + TimeSpan.FromMilliseconds(20));
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (version != _navPillScaleAnimationVersion || !pill.IsAttachedToVisualTree())
            return;

        var currentVisual = ElementComposition.GetElementVisual(pill);
        if (currentVisual is null)
            return;

        currentVisual.StopAnimation("Scale");
        currentVisual.Scale = targetScale;
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

    private void AttachListBoxHandlers()
    {
        foreach (var lb in this.GetVisualDescendants().OfType<ListBox>())
        {
            if (!lb.Classes.Contains("chat-list")) continue;
            if (lb.Tag is "hooked") continue;
            lb.Tag = "hooked";
            lb.SelectionChanged += OnChatListBoxSelectionChanged;

            // Intercept right-click to prevent selection change.
            // Use ContainerPrepared to hook each ListBoxItem as it's created.
            lb.ContainerPrepared += (_, args) =>
            {
                if (args.Container is ListBoxItem item)
                {
                    item.AddHandler(
                        PointerPressedEvent,
                        (_, pe) =>
                        {
                            if (pe.GetCurrentPoint(item).Properties.IsRightButtonPressed)
                                pe.Handled = true;
                        },
                        Avalonia.Interactivity.RoutingStrategies.Tunnel,
                        handledEventsToo: true);
                }
            };

            // Hook items already materialized
            foreach (var item in lb.GetVisualDescendants().OfType<ListBoxItem>())
            {
                item.AddHandler(
                    PointerPressedEvent,
                    (_, pe) =>
                    {
                        if (pe.GetCurrentPoint(item).Properties.IsRightButtonPressed)
                            pe.Handled = true;
                    },
                    Avalonia.Interactivity.RoutingStrategies.Tunnel,
                    handledEventsToo: true);
            }
        }
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
        foreach (var otherLb in this.GetVisualDescendants().OfType<ListBox>())
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
            foreach (var lb in this.GetVisualDescendants().OfType<ListBox>())
            {
                if (!lb.Classes.Contains("chat-list")) continue;

                if (lb.Tag is not "hooked")
                {
                    lb.Tag = "hooked";
                    lb.SelectionChanged += OnChatListBoxSelectionChanged;
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

        var items = this.GetVisualDescendants()
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

            var offsetAnim = compositor.CreateVector3DKeyFrameAnimation();
            offsetAnim.Target = "Offset";
            offsetAnim.InsertKeyFrame(0f, startOffset);
            offsetAnim.InsertKeyFrame(0.72f, settleOffset);
            offsetAnim.InsertKeyFrame(1f, target.BaseOffset);
            offsetAnim.Duration = TimeSpan.FromMilliseconds(ChatListRevealDurationMs);

            var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
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
        foreach (var lb in this.GetVisualDescendants().OfType<ListBox>())
        {
            if (!lb.Classes.Contains("chat-list")) continue;
            foreach (var container in lb.GetVisualDescendants().OfType<ListBoxItem>())
            {
                if (container.DataContext is Chat chat && chat.Id == chatId)
                {
                    titleBlock = container.GetVisualDescendants().OfType<TextBlock>()
                        .FirstOrDefault(tb => tb.Name != "ProjectLabel" &&
                                             tb.GetValue(DockPanel.DockProperty) != Dock.Bottom);
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
        // Re-apply font size override only if it was explicitly changed from default
        if (DataContext is MainViewModel vm && vm.SettingsVM.IsFontSizeModified)
            ApplyFontSize(vm.SettingsVM.FontSize);
    }

    /// <summary>Override font size resources proportionally from the base body size.</summary>
    private void ApplyFontSize(int bodySize)
    {
        var app = Application.Current;
        if (app is null) return;

        // Scale other sizes relative to the body size (default body=14)
        app.Resources["Font.SizeCaption"] = (double)(bodySize - 2);
        app.Resources["Font.SizeBody"] = (double)bodySize;
        app.Resources["Font.SizeBodyStrong"] = (double)bodySize;
        app.Resources["Font.SizeSubtitle"] = (double)(bodySize + 2);
        app.Resources["Font.SizeTitle"] = (double)(bodySize + 6);
    }

    /// <summary>Register/unregister the app for launch at login (cross-platform).</summary>
    public static void ApplyLaunchAtStartup(bool enable)
    {
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
                    File.WriteAllText(desktopFile,
                        $"[Desktop Entry]\nType=Application\nName=Lumi\nExec={exePath}\nX-GNOME-Autostart-enabled=true\n");
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
                    File.WriteAllText(plistFile,
                        $"""
                        <?xml version="1.0" encoding="UTF-8"?>
                        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                        <plist version="1.0">
                        <dict>
                            <key>Label</key><string>com.lumi.app</string>
                            <key>ProgramArguments</key><array><string>{exePath}</string></array>
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

    private void SetProjectSwitcherOpen(bool isOpen)
    {
        if (_projectSwitchRevealHost is null || _projectSwitchPanel is null)
            return;

        if (isOpen && DataContext is MainViewModel vm)
            RefreshProjectSwitcher(vm);

        if (_isProjectSwitcherOpen == isOpen && _projectSwitchRevealHost.IsVisible == isOpen)
        {
            if (isOpen)
                FocusProjectSwitcherSearch();
            return;
        }

        _isProjectSwitcherOpen = isOpen;

        if (_projectSwitchButton is not null)
        {
            if (isOpen && !_projectSwitchButton.Classes.Contains("open"))
                _projectSwitchButton.Classes.Add("open");
            else if (!isOpen)
                _projectSwitchButton.Classes.Remove("open");
        }

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

        var host = _projectSwitchRevealHost;

        if (isOpen)
        {
            host.IsVisible = true;
            host.Height = 0;
            host.Opacity = 0;
        }

        var startHeight = Math.Max(0, double.IsNaN(host.Height) ? host.Bounds.Height : host.Height);
        if (!isOpen && startHeight <= 0)
            startHeight = GetProjectSwitcherTargetHeight();

        var endHeight = isOpen ? GetProjectSwitcherTargetHeight() : 0;
        host.Height = startHeight;

        var animation = new Animation
        {
            Duration = isOpen ? TimeSpan.FromMilliseconds(210) : TimeSpan.FromMilliseconds(130),
            Easing = new SplineEasing(0.22, 0.08, 0.18, 1.0),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(Layoutable.HeightProperty, startHeight),
                        new Setter(OpacityProperty, isOpen ? 0.0 : 1.0),
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
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
            return;

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
            OnProjectSwitcherClosed();
        }
    }

    private double GetProjectSwitcherTargetHeight()
    {
        if (_projectSwitchPanel is null)
            return 0;

        var width = _projectSwitchButton?.Bounds.Width
            ?? _projectSwitcherRoot?.Bounds.Width
            ?? 255;
        if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
            width = 255;

        _projectSwitchPanel.Measure(new Size(width, double.PositiveInfinity));
        var height = _projectSwitchPanel.DesiredSize.Height;
        if (height <= 0 || double.IsNaN(height) || double.IsInfinity(height))
            height = _projectSwitchPanel.Bounds.Height;
        if (height <= 0 || double.IsNaN(height) || double.IsInfinity(height))
            height = 216;

        return Math.Ceiling(Math.Clamp(height, 1, 288));
    }

    private void OnProjectSwitcherClosed()
    {
        _projectSwitchButton?.Classes.Remove("open");

        foreach (var (project, handler) in _projectFilterHandlers)
            project.PropertyChanged -= handler;
        _projectFilterHandlers.Clear();

        if (_projectFilterSearchBox is not null && !string.IsNullOrEmpty(_projectFilterSearchBox.Text))
            _projectFilterSearchBox.Text = "";
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

        _projectFilterResults.Children.Clear();

        var query = _projectFilterSearchBox?.Text;
        var candidates = GetProjectFilterCandidates(vm, query, out var totalMatches).ToList();
        var totalChats = vm.DataStore.Data.Chats.Count;

        var isAll = !vm.SelectedProjectFilter.HasValue;
        _projectFilterResults.Children.Add(CreateProjectFilterRow(
            vm,
            project: null,
            title: Loc.ProjectSwitcher_AllProjects,
            subtitle: string.Format(Loc.ProjectSwitcher_AllSubtitle, vm.Projects.Count, totalChats),
            isSelected: isAll,
            isRunning: false,
            countText: totalChats > 0 ? totalChats.ToString(Loc.Culture) : ""));

        if (candidates.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = Loc.ProjectSwitcher_NoMatches,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
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
                candidate.ChatCount > 0 ? candidate.ChatCount.ToString(Loc.Culture) : "");

            _projectFilterResults.Children.Add(row);

            var capturedRow = row;
            PropertyChangedEventHandler handler = (_, args) =>
            {
                if (args.PropertyName != nameof(Project.IsRunning))
                    return;

                Dispatcher.UIThread.Post(() =>
                {
                    if (DataContext is MainViewModel currentVm)
                        RefreshProjectSwitcher(currentVm);
                    else
                        capturedRow.IsVisible = true;
                });
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

    private void UpdateProjectSwitcherSummary(MainViewModel vm)
    {
        if (_projectSwitchTitleText is null || _projectSwitchSubtitleText is null || _projectSwitchCountText is null)
            return;

        var selectedProject = vm.SelectedProjectFilter.HasValue
            ? vm.Projects.FirstOrDefault(project => project.Id == vm.SelectedProjectFilter.Value)
            : null;

        _projectSwitchCountText.Text = vm.Projects.Count.ToString(Loc.Culture);

        if (selectedProject is null)
        {
            _projectSwitchTitleText.Text = Loc.ProjectSwitcher_AllProjects;
            _projectSwitchSubtitleText.Text = string.Format(
                Loc.ProjectSwitcher_AllSubtitle,
                vm.Projects.Count,
                vm.DataStore.Data.Chats.Count);
            return;
        }

        var chatCount = vm.GetProjectChatCount(selectedProject.Id);
        _projectSwitchTitleText.Text = selectedProject.Name;
        _projectSwitchSubtitleText.Text = FormatProjectFilterSubtitle(
            selectedProject,
            chatCount,
            vm.GetProjectLastActivity(selectedProject.Id));
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
        string countText)
    {
        var button = new Button
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Content = CreateProjectFilterRowContent(project is null, title, subtitle, isSelected, isRunning, countText),
        };
        ToolTip.SetTip(button, subtitle);
        button.Classes.Add("project-switcher-row");
        if (isSelected)
            button.Classes.Add("selected");

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

    private Control CreateProjectFilterRowContent(
        bool isAllProjects,
        string title,
        string subtitle,
        bool isSelected,
        bool isRunning,
        string countText)
    {
        var dock = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(7, 4),
        };

        if (!string.IsNullOrWhiteSpace(countText))
        {
            var statePill = CreateProjectStatePill(countText);
            DockPanel.SetDock(statePill, Dock.Right);
            dock.Children.Add(statePill);
        }

        if (isSelected)
        {
            var rail = CreateProjectSelectedRail();
            DockPanel.SetDock(rail, Dock.Left);
            dock.Children.Add(rail);
        }

        var glyph = CreateProjectGlyph(isAllProjects, isSelected, isRunning);
        DockPanel.SetDock(glyph, Dock.Left);
        dock.Children.Add(glyph);

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        titleBlock.Foreground = GetThemeBrush(isSelected ? "Brush.TextPrimary" : "Brush.TextSecondary", Brushes.Gray);

        dock.Children.Add(titleBlock);

        return dock;
    }

    private Border CreateProjectSelectedRail()
    {
        return new Border
        {
            Width = 3,
            Height = 16,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 6, 0),
            Background = GetThemeBrush("Brush.AccentDefault", Brushes.DodgerBlue),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
    }

    private Border CreateProjectGlyph(bool isAllProjects, bool isSelected, bool isRunning)
    {
        var icon = new PathIcon
        {
            Data = GetIconGeometry(isAllProjects ? "Icon.Browse" : "Icon.Folder"),
            Width = 12,
            Height = 12,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = GetThemeBrush(isSelected ? "Brush.AccentDefault" : "Brush.TextTertiary", Brushes.Gray),
        };

        var grid = new Grid();
        grid.Children.Add(icon);

        if (isRunning)
        {
            grid.Children.Add(new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(4),
                Background = GetThemeBrush("Brush.AccentDefault", Brushes.DodgerBlue),
                BorderBrush = GetThemeBrush("Brush.Surface1", Brushes.Black),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = new Thickness(0, -1, -1, 0),
            });
        }

        return new Border
        {
            Width = 26,
            Height = 22,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 6, 0),
            Background = GetThemeBrush(
                isRunning ? "Brush.ControlDefault" : "Brush.Transparent",
                Brushes.Transparent),
            BorderBrush = GetThemeBrush(
                isRunning ? "Brush.BorderSubtle" : "Brush.Transparent",
                Brushes.Transparent),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
    }

    private Border CreateProjectStatePill(string countText)
    {
        var label = new TextBlock
        {
            Text = countText,
            FontSize = 9.8,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetThemeBrush("Brush.TextTertiary", Brushes.Gray),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        return new Border
        {
            MinWidth = 20,
            Padding = new Thickness(5, 1),
            CornerRadius = new CornerRadius(999),
            Background = GetThemeBrush("Brush.ControlDefault", Brushes.Transparent),
            BorderBrush = GetThemeBrush("Brush.BorderSubtle", Brushes.Transparent),
            BorderThickness = new Thickness(1),
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

    /// <summary>Sets the ProjectLabel TextBlock on each chat ListBoxItem to show the project name.</summary>
    private void ApplyProjectLabelsToChats(MainViewModel vm)
    {
        // Only show project labels when NOT filtering by a specific project
        var showLabels = !vm.SelectedProjectFilter.HasValue;

        foreach (var lb in this.GetVisualDescendants().OfType<ListBox>())
        {
            if (!lb.Classes.Contains("sidebar-list")) continue;

            foreach (var item in lb.GetVisualDescendants().OfType<ListBoxItem>())
            {
                if (item.DataContext is not Chat chat) continue;
                var label = item.GetVisualDescendants().OfType<TextBlock>()
                    .FirstOrDefault(t => t.Name == "ProjectLabel");
                if (label is null) continue;

                if (showLabels && chat.ProjectId.HasValue)
                {
                    var name = vm.GetProjectName(chat.ProjectId);
                    label.Text = name ?? "";
                    label.IsVisible = name is not null;
                }
                else
                {
                    label.IsVisible = false;
                }
            }
        }
    }

    /// <summary>Populates the "Move to Project" context menu items for each chat.</summary>
    private void ApplyMoveToProjectMenus(MainViewModel vm)
    {
        foreach (var menuItem in this.GetVisualDescendants().OfType<MenuItem>())
        {
            if (menuItem.Header is not string header || header != Loc.Menu_MoveToProject) continue;

            menuItem.Items.Clear();
            foreach (var project in vm.Projects)
            {
                var p = project; // capture
                var mi = new MenuItem { Header = project.Name };
                mi.Click += (_, _) =>
                {
                    // Find the chat from the context menu's DataContext
                    var chat = (menuItem.Parent as ContextMenu)?.DataContext as Chat
                        ?? menuItem.DataContext as Chat;
                    if (chat is not null)
                        vm.AssignChatToProjectCommand.Execute(new object[] { chat, p });
                };
                menuItem.Items.Add(mi);
            }
        }
    }

    /// <summary>Sets the chat count TextBlock for each project in the sidebar.</summary>
    private void ApplyProjectChatCounts(MainViewModel vm)
    {
        var sidebarProjects = _sidebarPanels.Length > 2 ? _sidebarPanels[2] : null;
        if (sidebarProjects is null) return;

        foreach (var item in sidebarProjects.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (item.DataContext is not Project project) continue;
            var countLabel = item.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Name == "ProjectChatCount");
            if (countLabel is null) continue;

            var count = vm.ProjectsVM.GetChatCount(project.Id);
            countLabel.Text = count > 0 ? (count == 1 ? string.Format(Loc.Project_ChatCount, count) : string.Format(Loc.Project_ChatCounts, count)) : "";
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
    private bool IsBrowserOpen => _browserIsland is { IsVisible: true };

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
        }
    }

    private void EnsureBrowserViewLoaded(MainViewModel vm, BrowserService browserService)
    {
        if (_browserView is null)
        {
            if (_browserHost is null) return;
            _browserView = new BrowserView();
            _browserHost.Content = _browserView;
        }
        _browserView.SetBrowserService(browserService, vm.DataStore);
    }

    private async void ShowBrowserPanel(Guid chatId)
    {
        if (_browserIsland is null || _chatContentGrid is null || _chatIsland is null) return;

        var vm = DataContext as MainViewModel;
        if (vm is null) return;

        // Get the per-chat browser service
        var browserService = vm.ChatVM.GetBrowserServiceForChat(chatId);
        if (browserService is null) return;

        // Hide diff panel if open (they share column 2)
        if (_diffIsland is { IsVisible: true })
        {
            _diffIsland.IsVisible = false;
            _diffIsland.Opacity = 1;
            _diffIsland.RenderTransform = null;
            vm.ChatVM.IsDiffOpen = false;
        }

        // Hide plan panel if open (they share column 2)
        if (_planIsland is { IsVisible: true })
        {
            _planIsland.IsVisible = false;
            _planIsland.Opacity = 1;
            _planIsland.RenderTransform = null;
            vm.ChatVM.IsPlanOpen = false;
        }

        // Switch to the correct per-chat BrowserService
        EnsureBrowserViewLoaded(vm, browserService);

        // If browser panel is already visible (switching chats), just refresh bounds
        if (_browserIsland.IsVisible)
        {
            // Hide old controller, show new one
            _browserView?.RefreshBounds();
            if (browserService.Controller is not null)
                browserService.Controller.IsVisible = true;
            return;
        }

        // Cancel any in-progress animation
        var ct = ReplaceCancellationTokenSource(ref _browserAnimCts).Token;

        // Ensure we're on the Chat tab
        if (vm.SelectedNavIndex != 0)
            vm.SelectedNavIndex = 0;

        // Switch to split layout: chat (1*) | splitter (Auto) | browser (1*)
        const double browserOffsetX = 40.0;

        var defs = _chatContentGrid.ColumnDefinitions;
        while (defs.Count < 3)
            defs.Add(new ColumnDefinition());
        defs[0].Width = new GridLength(1, GridUnitType.Star);
        defs[1].Width = GridLength.Auto;
        defs[2].Width = new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(_chatIsland, 0);
        Grid.SetColumn(_browserIsland, 2);

        // Prepare initial state — transparent + shifted from the outer edge
        _browserIsland.RenderTransform = new TranslateTransform(browserOffsetX, 0);
        _browserIsland.Opacity = 0;
        _browserIsland.IsVisible = true;
        if (_browserSplitter is not null)
            _browserSplitter.IsVisible = true;

        // Animate fade-in + slide from the outer edge (both on the Border visual)
        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(300),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0.0),
                        new Setter(TranslateTransform.XProperty, browserOffsetX),
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1.0),
                        new Setter(TranslateTransform.XProperty, 0.0),
                    }
                },
            }
        };

        try { await anim.RunAsync(_browserIsland, ct); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;

        // Finalize — clear transform and show WebView2 overlay after animation
        _browserIsland.Opacity = 1;
        _browserIsland.RenderTransform = null;

        if (browserService.Controller is not null)
            browserService.Controller.IsVisible = true;
        Dispatcher.UIThread.Post(() => _browserView?.RefreshBounds(), DispatcherPriority.Loaded);
    }

    /// <summary>Hides the browser panel and returns to single-column chat layout.</summary>
    private async void HideBrowserPanel()
    {
        if (_browserIsland is null || _chatContentGrid is null) return;
        if (!_browserIsland.IsVisible) return;

        // Cancel any in-progress animation
        var ct = ReplaceCancellationTokenSource(ref _browserAnimCts).Token;

        // Hide WebView2 overlay immediately so it doesn't float during animation
        _browserView?.ClearBrowserService();

        const double browserOffsetX = 40.0;

        // Animate fade-out + slide to the outer edge
        _browserIsland.RenderTransform = new TranslateTransform(0, 0);

        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(200),
            Easing = new CubicEaseIn(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1.0),
                        new Setter(TranslateTransform.XProperty, 0.0),
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0.0),
                        new Setter(TranslateTransform.XProperty, browserOffsetX),
                    }
                },
            }
        };

        try { await anim.RunAsync(_browserIsland, ct); }
        catch (OperationCanceledException) { /* cancelled — cleanup below */ }

        // Collapse regardless of cancellation
        _browserIsland.IsVisible = false;
        _browserIsland.Opacity = 1;
        _browserIsland.RenderTransform = null;
        if (_browserSplitter is not null && !(_diffIsland?.IsVisible ?? false) && !(_planIsland?.IsVisible ?? false))
            _browserSplitter.IsVisible = false;

        // Reset to single-column layout if nothing else is open
        if (!(_diffIsland?.IsVisible ?? false) && !(_planIsland?.IsVisible ?? false))
        {
            var defs = _chatContentGrid.ColumnDefinitions;
            while (defs.Count < 3)
                defs.Add(new ColumnDefinition());
            defs[0].Width = new GridLength(1, GridUnitType.Star);
            defs[1].Width = new GridLength(0);
            defs[2].Width = new GridLength(0);

            if (_chatIsland is not null)
                Grid.SetColumn(_chatIsland, 0);
        }
        Grid.SetColumn(_browserIsland, 2);
    }

    /// <summary>Whether the diff panel is currently visible.</summary>
    private bool IsDiffOpen => _diffIsland is { IsVisible: true };

    private void EnsureDiffViewLoaded()
    {
        if (_diffHost is null) return;
        if (_diffView is null)
            _diffView = new DiffView();
        // Always restore DiffView as the host content (may have been swapped for git changes list)
        if (_diffHost.Content != _diffView)
            _diffHost.Content = _diffView;
    }

    private async void ShowDiffPanel(FileChangeItem fileChange)
    {
        if (_diffIsland is null || _chatContentGrid is null || _chatIsland is null) return;

        // Hide browser panel if it's open (they share column 2)
        if (_browserIsland is { IsVisible: true })
        {
            _browserIsland.IsVisible = false;
            _browserIsland.Opacity = 1;
            _browserIsland.RenderTransform = null;
            if (_browserSplitter is not null) _browserSplitter.IsVisible = false;
            _browserView?.ClearBrowserService();
            if (DataContext is MainViewModel vmb)
                vmb.ChatVM.IsBrowserOpen = false;
        }

        // Hide plan panel if open (they share column 2)
        if (_planIsland is { IsVisible: true })
        {
            _planIsland.IsVisible = false;
            _planIsland.Opacity = 1;
            _planIsland.RenderTransform = null;
            if (DataContext is MainViewModel vmp) vmp.ChatVM.IsPlanOpen = false;
        }

        // Cancel any in-progress animation
        var ct = ReplaceCancellationTokenSource(ref _previewAnimCts).Token;

        var vm = DataContext as MainViewModel;

        // Ensure we're on the Chat tab
        if (vm is not null && vm.SelectedNavIndex != 0)
            vm.SelectedNavIndex = 0;

        EnsureDiffViewLoaded();

        // Update header text
        if (_diffFileNameText is not null)
            _diffFileNameText.Text = System.IO.Path.GetFileName(fileChange.FilePath);

        // Set diff content
        _diffView?.SetFileChangeDiff(fileChange);

        // Switch to split layout
        const double offsetX = 40.0;
        var defs = _chatContentGrid.ColumnDefinitions;
        while (defs.Count < 3) defs.Add(new ColumnDefinition());
        defs[0].Width = new GridLength(1, GridUnitType.Star);
        defs[1].Width = GridLength.Auto;
        defs[2].Width = new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(_chatIsland, 0);
        Grid.SetColumn(_diffIsland, 2);

        _diffIsland.RenderTransform = new TranslateTransform(offsetX, 0);
        _diffIsland.Opacity = 0;
        _diffIsland.IsVisible = true;
        if (_browserSplitter is not null) _browserSplitter.IsVisible = true;

        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(300),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.0), new Setter(TranslateTransform.XProperty, offsetX) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1.0), new Setter(TranslateTransform.XProperty, 0.0) } },
            }
        };

        try { await anim.RunAsync(_diffIsland, ct); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;

        _diffIsland.Opacity = 1;
        _diffIsland.RenderTransform = null;

        if (vm is not null) vm.ChatVM.IsDiffOpen = true;
    }

    private async void HideDiffPanel()
    {
        if (_diffIsland is null || _chatContentGrid is null) return;
        if (!_diffIsland.IsVisible) return;

        var ct = ReplaceCancellationTokenSource(ref _previewAnimCts).Token;

        const double offsetX = 40.0;
        _diffIsland.RenderTransform = new TranslateTransform(0, 0);

        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(200),
            Easing = new CubicEaseIn(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1.0), new Setter(TranslateTransform.XProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.0), new Setter(TranslateTransform.XProperty, offsetX) } },
            }
        };

        try { await anim.RunAsync(_diffIsland, ct); }
        catch (OperationCanceledException) { }

        _diffIsland.IsVisible = false;
        _diffIsland.Opacity = 1;
        _diffIsland.RenderTransform = null;
        if (_browserSplitter is not null && !(_browserIsland?.IsVisible ?? false))
            _browserSplitter.IsVisible = false;

        // Reset to single-column if nothing else is open
        if (!(_browserIsland?.IsVisible ?? false) && !(_planIsland?.IsVisible ?? false))
        {
            var defs = _chatContentGrid.ColumnDefinitions;
            while (defs.Count < 3) defs.Add(new ColumnDefinition());
            defs[0].Width = new GridLength(1, GridUnitType.Star);
            defs[1].Width = new GridLength(0);
            defs[2].Width = new GridLength(0);
            if (_chatIsland is not null) Grid.SetColumn(_chatIsland, 0);
        }

        if (DataContext is MainViewModel vm) vm.ChatVM.IsDiffOpen = false;
    }

    /// <summary>Shows a list of git changed files in the diff panel. Clicking a file opens its diff.</summary>
    private void ShowGitChangesPanel(List<GitFileChangeViewModel> files)
    {
        if (_diffIsland is null || _chatContentGrid is null || _chatIsland is null) return;

        _lastGitChangesList = files;

        var tertiaryBrush = Avalonia.Media.Brushes.Gray as Avalonia.Media.IBrush;
        if (this.TryFindResource("Brush.TextTertiary", this.ActualThemeVariant, out var tObj) && tObj is Avalonia.Media.IBrush tBrush)
            tertiaryBrush = tBrush;

        // Build a file list panel
        var listPanel = new StackPanel { Spacing = 2, Margin = new Thickness(8, 4) };
        foreach (var file in files)
        {
            var kindColor = file.Kind switch
            {
                GitChangeKind.Added or GitChangeKind.Untracked => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(63, 185, 80)),
                GitChangeKind.Deleted => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(248, 81, 73)),
                GitChangeKind.Renamed => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(88, 166, 255)),
                _ => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(210, 153, 34))
            };

            var row = new Button
            {
                Name = CreateGitDiffRowName(file.FileName, listPanel.Children.Count),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Padding = new Thickness(10, 8),
                Background = Avalonia.Media.Brushes.Transparent,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            AutomationProperties.SetName(row, $"Open diff for {file.FileName}");
            row.Classes.Add("subtle");

            var content = new DockPanel();

            // Status letter badge
            var badge = new Border
            {
                Width = 22, Height = 22,
                CornerRadius = new CornerRadius(4),
                Background = new Avalonia.Media.SolidColorBrush(kindColor.Color, 0.15),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock
                {
                    Text = file.KindIcon,
                    FontSize = 11,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    Foreground = kindColor,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                }
            };
            DockPanel.SetDock(badge, Dock.Left);
            content.Children.Add(badge);

            var textStack = new StackPanel { Spacing = 1, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock
            {
                Text = file.FileName,
                FontSize = 12,
                FontWeight = Avalonia.Media.FontWeight.Medium,
            });
            if (!string.IsNullOrEmpty(file.Directory))
            {
                textStack.Children.Add(new TextBlock
                {
                    Text = file.Directory,
                    FontSize = 10,
                    Foreground = tertiaryBrush,
                });
            }
            content.Children.Add(textStack);

            // Line stats on the right
            if (file.HasStats)
            {
                var statsPanel = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                DockPanel.SetDock(statsPanel, Dock.Right);
                if (file.LinesAdded > 0)
                    statsPanel.Children.Add(new TextBlock
                    {
                        Text = $"+{file.LinesAdded}",
                        FontSize = 11,
                        FontFamily = new Avalonia.Media.FontFamily("Cascadia Code, Consolas, monospace"),
                        Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(63, 185, 80)),
                    });
                if (file.LinesRemoved > 0)
                    statsPanel.Children.Add(new TextBlock
                    {
                        Text = $"−{file.LinesRemoved}",
                        FontSize = 11,
                        FontFamily = new Avalonia.Media.FontFamily("Cascadia Code, Consolas, monospace"),
                        Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(248, 81, 73)),
                    });
                // Insert before textStack so DockPanel docks it right
                content.Children.Insert(1, statsPanel);
            }

            row.Content = content;

            // Click opens the diff for this file with a back button
            var capturedFile = file;
            row.Click += (_, _) => ShowGitFileDiffWithBackNav(capturedFile);

            listPanel.Children.Add(row);
        }

        // Update header
        if (_diffFileNameText is not null)
            _diffFileNameText.Text = $"Changes ({files.Count})";

        // Show the list in the diff host (bypass EnsureDiffViewLoaded since we want custom content)
        if (_diffHost is not null)
        {
            _diffHost.Content = new ScrollViewer
            {
                Content = listPanel,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            };
        }

        // Show the diff panel (reuse the same show animation logic)
        ShowDiffPanelAnimated();
    }

    private static string CreateGitDiffRowName(string fileName, int rowIndex)
    {
        var safeFileName = new string(fileName.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        return $"GitDiffRow_{rowIndex}_{safeFileName}";
    }

    /// <summary>Opens a single file diff with breadcrumb back-nav in the header.</summary>
    private void ShowGitFileDiffWithBackNav(GitFileChangeViewModel file)
    {
        if (_diffHost is null) return;

        // Create a fresh DiffView for this file
        var diffView = new DiffView();
        _diffHost.Content = diffView;

        // Update header: clickable "Changes" breadcrumb + file name
        if (_diffFileNameText is not null)
        {
            _diffFileNameText.Text = null;
            _diffFileNameText.Inlines?.Clear();
            var inlines = _diffFileNameText.Inlines ??= new Avalonia.Controls.Documents.InlineCollection();

            var accentBrush = Avalonia.Media.Brushes.DodgerBlue as Avalonia.Media.IBrush;
            var tertiaryBrush = Avalonia.Media.Brushes.Gray as Avalonia.Media.IBrush;
            if (this.TryFindResource("Brush.AccentDefault", this.ActualThemeVariant, out var accentObj) && accentObj is Avalonia.Media.IBrush ab)
                accentBrush = ab;
            if (this.TryFindResource("Brush.TextTertiary", this.ActualThemeVariant, out var tertiaryObj) && tertiaryObj is Avalonia.Media.IBrush tb)
                tertiaryBrush = tb;

            var changesRun = new Avalonia.Controls.Documents.Run("Changes")
            {
                Foreground = accentBrush,
            };
            inlines.Add(changesRun);
            inlines.Add(new Avalonia.Controls.Documents.Run("  ›  ") { Foreground = tertiaryBrush });
            inlines.Add(new Avalonia.Controls.Documents.Run(file.FileName));

            _diffFileNameText.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);

            _diffFileNameText.PointerPressed -= OnDiffBreadcrumbClick;
            _diffFileNameText.PointerPressed += OnDiffBreadcrumbClick;
        }

        // Build the diff view
        if (file.Change.Kind is GitChangeKind.Added or GitChangeKind.Untracked)
        {
            _ = ShowAddedGitFileDiffAsync(file.Change.FullPath, diffView);
        }
        else
        {
            _ = LoadGitUnifiedDiffAsync(file.Change, diffView);
        }
    }

    private async Task ShowAddedGitFileDiffAsync(string filePath, DiffView diffView)
    {
        var content = await Task.Run(() =>
        {
            try { return System.IO.File.Exists(filePath) ? System.IO.File.ReadAllText(filePath) : string.Empty; }
            catch { return string.Empty; }
        });
        Dispatcher.UIThread.Post(() => diffView.SetSnapshotDiff(filePath, string.Empty, content));
    }

    private void OnDiffBreadcrumbClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (_lastGitChangesList is not null)
            ShowGitChangesPanel(_lastGitChangesList);
    }

    private async Task LoadGitUnifiedDiffAsync(GitFileChange change, DiffView diffView)
    {
        var repoDir = System.IO.Path.GetDirectoryName(change.FullPath) ?? "";
        var diff = await GitService.GetFileDiffAsync(repoDir, System.IO.Path.GetFileName(change.FullPath));
        if (diff is null)
            diff = await GitService.GetFileDiffAsync(repoDir, change.RelativePath);
        Dispatcher.UIThread.Post(() => diffView.SetUnifiedDiffText(change.FullPath, diff));
    }

    /// <summary>Shows the diff island with animation (shared by file diff and git changes list).</summary>
    private async void ShowDiffPanelAnimated()
    {
        if (_diffIsland is null || _chatContentGrid is null || _chatIsland is null) return;

        // Hide browser panel if it's open
        if (_browserIsland is { IsVisible: true })
        {
            _browserIsland.IsVisible = false;
            _browserIsland.Opacity = 1;
            _browserIsland.RenderTransform = null;
            if (_browserSplitter is not null) _browserSplitter.IsVisible = false;
            _browserView?.ClearBrowserService();
            if (DataContext is MainViewModel vmb)
                vmb.ChatVM.IsBrowserOpen = false;
        }

        // Hide plan panel if open
        if (_planIsland is { IsVisible: true })
        {
            _planIsland.IsVisible = false;
            _planIsland.Opacity = 1;
            _planIsland.RenderTransform = null;
            if (DataContext is MainViewModel vmp) vmp.ChatVM.IsPlanOpen = false;
        }

        var ct = ReplaceCancellationTokenSource(ref _previewAnimCts).Token;
        var vm = DataContext as MainViewModel;
        if (vm is not null && vm.SelectedNavIndex != 0)
            vm.SelectedNavIndex = 0;

        const double offsetX = 40.0;
        var defs = _chatContentGrid.ColumnDefinitions;
        while (defs.Count < 3) defs.Add(new ColumnDefinition());
        defs[0].Width = new GridLength(1, GridUnitType.Star);
        defs[1].Width = GridLength.Auto;
        defs[2].Width = new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(_chatIsland, 0);
        Grid.SetColumn(_diffIsland, 2);

        _diffIsland.RenderTransform = new TranslateTransform(offsetX, 0);
        _diffIsland.Opacity = 0;
        _diffIsland.IsVisible = true;
        if (_browserSplitter is not null) _browserSplitter.IsVisible = true;

        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(300),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.0), new Setter(TranslateTransform.XProperty, offsetX) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1.0), new Setter(TranslateTransform.XProperty, 0.0) } },
            }
        };

        try { await anim.RunAsync(_diffIsland, ct); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;

        _diffIsland.Opacity = 1;
        _diffIsland.RenderTransform = null;
        if (vm is not null) vm.ChatVM.IsDiffOpen = true;
    }

    private bool IsPlanOpen => _planIsland is { IsVisible: true };

    private async void ShowPlanPanel()
    {
        if (_planIsland is null || _chatContentGrid is null || _chatIsland is null) return;

        // Hide browser panel if open (they share column 2)
        if (_browserIsland is { IsVisible: true })
        {
            _browserIsland.IsVisible = false;
            _browserIsland.Opacity = 1;
            _browserIsland.RenderTransform = null;
            if (_browserSplitter is not null) _browserSplitter.IsVisible = false;
            _browserView?.ClearBrowserService();
            if (DataContext is MainViewModel vmb)
                vmb.ChatVM.IsBrowserOpen = false;
        }

        // Hide diff panel if open
        if (_diffIsland is { IsVisible: true })
        {
            _diffIsland.IsVisible = false;
            _diffIsland.Opacity = 1;
            _diffIsland.RenderTransform = null;
            if (DataContext is MainViewModel vmd) vmd.ChatVM.IsDiffOpen = false;
        }

        var ct = ReplaceCancellationTokenSource(ref _previewAnimCts).Token;

        var vm = DataContext as MainViewModel;
        if (vm is not null && vm.SelectedNavIndex != 0)
            vm.SelectedNavIndex = 0;

        // Switch to split layout
        const double offsetX = 40.0;
        var defs = _chatContentGrid.ColumnDefinitions;
        while (defs.Count < 3) defs.Add(new ColumnDefinition());
        defs[0].Width = new GridLength(1, GridUnitType.Star);
        defs[1].Width = GridLength.Auto;
        defs[2].Width = new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(_chatIsland, 0);
        Grid.SetColumn(_planIsland, 2);

        _planIsland.RenderTransform = new TranslateTransform(offsetX, 0);
        _planIsland.Opacity = 0;
        _planIsland.IsVisible = true;
        if (_browserSplitter is not null) _browserSplitter.IsVisible = true;

        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(300),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.0), new Setter(TranslateTransform.XProperty, offsetX) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1.0), new Setter(TranslateTransform.XProperty, 0.0) } },
            }
        };

        try { await anim.RunAsync(_planIsland, ct); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;

        _planIsland.Opacity = 1;
        _planIsland.RenderTransform = null;

        if (vm is not null) vm.ChatVM.IsPlanOpen = true;
    }

    private async void HidePlanPanel()
    {
        if (_planIsland is null || _chatContentGrid is null) return;
        if (!_planIsland.IsVisible) return;

        var ct = ReplaceCancellationTokenSource(ref _previewAnimCts).Token;

        const double offsetX = 40.0;
        _planIsland.RenderTransform = new TranslateTransform(0, 0);

        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(200),
            Easing = new CubicEaseIn(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1.0), new Setter(TranslateTransform.XProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.0), new Setter(TranslateTransform.XProperty, offsetX) } },
            }
        };

        try { await anim.RunAsync(_planIsland, ct); }
        catch (OperationCanceledException) { }

        _planIsland.IsVisible = false;
        _planIsland.Opacity = 1;
        _planIsland.RenderTransform = null;
        if (_browserSplitter is not null && !(_browserIsland?.IsVisible ?? false))
            _browserSplitter.IsVisible = false;

        // Reset to single-column if nothing else is open
        if (!(_browserIsland?.IsVisible ?? false) && !(_diffIsland?.IsVisible ?? false))
        {
            var defs = _chatContentGrid.ColumnDefinitions;
            while (defs.Count < 3) defs.Add(new ColumnDefinition());
            defs[0].Width = new GridLength(1, GridUnitType.Star);
            defs[1].Width = new GridLength(0);
            defs[2].Width = new GridLength(0);
            if (_chatIsland is not null) Grid.SetColumn(_chatIsland, 0);
        }

        if (DataContext is MainViewModel vm) vm.ChatVM.IsPlanOpen = false;
    }
}
