using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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
    private StackPanel? _projectFilterBar;
    private readonly List<(Project Project, PropertyChangedEventHandler Handler)> _projectFilterHandlers = [];
    private TextBox? _chatSearchBox;
    private ChatView? _chatView;
    private BrowserView? _browserView;
    private ContentControl? _browserHost;
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
    private Border? _diffIsland;
    private ContentControl? _diffHost;
    private DiffView? _diffView;
    private TextBlock? _diffFileNameText;
    private List<GitFileChangeViewModel>? _lastGitChangesList;
    private Border? _planIsland;
    private CancellationTokenSource? _previewAnimCts;
    private bool _suppressSelectionSync;
    private CancellationTokenSource? _browserAnimCts;
    private CancellationTokenSource? _shellAnimCts;
    private int _currentShellIndex = -1;
    private MainViewModel? _wiredVm;

    public MainWindow()
    {
        InitializeComponent();
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 38;

        // Force transparent background after theme styles are applied
        Background = Avalonia.Media.Brushes.Transparent;
        TransparencyBackgroundFallback = Avalonia.Media.Brushes.Transparent;

        // Watch for minimize to hide to tray
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
        DisposeCancellationTokenSource(ref _titleAnimCts);
        DisposeCancellationTokenSource(ref _browserAnimCts);
        DisposeCancellationTokenSource(ref _previewAnimCts);
    }

    private static CancellationTokenSource ReplaceCancellationTokenSource(ref CancellationTokenSource? source)
    {
        DisposeCancellationTokenSource(ref source);
        source = new CancellationTokenSource();
        return source;
    }

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

        _pages =
        [
            this.FindControl<Grid>("ChatContentGrid"),         // 0 = Chat (container grid)
            this.FindControl<Control>("PageProjects"),         // 1
            this.FindControl<Control>("PageSkills"),           // 2
            this.FindControl<Control>("PageAgents"),           // 3
            this.FindControl<Control>("PageMemories"),         // 4
            this.FindControl<Control>("PageMcpServers"),       // 5
            this.FindControl<Control>("PageSettings"),         // 6
        ];

        _sidebarPanels =
        [
            this.FindControl<Panel>("SidebarChat"),            // 0
            this.FindControl<Panel>("SidebarProjects"),        // 1
            this.FindControl<Panel>("SidebarSkills"),          // 2
            this.FindControl<Panel>("SidebarAgents"),          // 3
            this.FindControl<Panel>("SidebarMemories"),        // 4
            this.FindControl<Panel>("SidebarMcpServers"),      // 5
            this.FindControl<Panel>("SidebarSettings"),        // 6
        ];

        _navButtons =
        [
            this.FindControl<Button>("NavChat"),
            this.FindControl<Button>("NavProjects"),
            this.FindControl<Button>("NavSkills"),
            this.FindControl<Button>("NavAgents"),
            this.FindControl<Button>("NavMemories"),
            this.FindControl<Button>("NavMcpServers"),
            this.FindControl<Button>("NavSettings"),
        ];

        _renameOverlay = this.FindControl<Panel>("RenameOverlay");
        _renameTextBox = this.FindControl<TextBox>("RenameTextBox");
        _projectFilterBar = this.FindControl<StackPanel>("ProjectFilterBar");

        _chatSearchBox = this.FindControl<TextBox>("ChatSearchBox");
        _chatView = this.FindControl<ChatView>("PageChat");
        _browserHost = this.FindControl<ContentControl>("BrowserHost");
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
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // If minimize-to-tray is enabled, hide instead of closing
        if (DataContext is MainViewModel vm && vm.SettingsVM.MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        SaveWindowBounds();
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

    private void SaveWindowBounds()
    {
        if (DataContext is not MainViewModel vm) return;
        var settings = vm.DataStore.Data.Settings;

        settings.IsMaximized = WindowState == WindowState.Maximized;

        // Save the normal (non-maximized) bounds so restore works correctly
        if (WindowState == WindowState.Normal)
        {
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;

            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            var scaling = screen?.Scaling ?? 1.0;
            settings.WindowLeft = Position.X / scaling;
            settings.WindowTop = Position.Y / scaling;
        }

        vm.DataStore.Save();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (DataContext is not MainViewModel vm || !vm.IsOnboarded) return;

        // Don't intercept shortcuts while recording a global hotkey
        var settingsPage = _settingsView;
        if (settingsPage?.IsRecordingHotkey == true) return;

        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
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

        // ── Ctrl+N — New chat ──
        if (ctrl && !shift && e.Key == Key.N)
        {
            vm.NewChatCommand.Execute(null);
            Dispatcher.UIThread.Post(() => _chatView?.FocusComposer(), DispatcherPriority.Input);
            e.Handled = true;
            return;
        }

        // ── Ctrl+L — Focus chat input ──
        if (ctrl && !shift && e.Key == Key.L)
        {
            if (vm.SelectedNavIndex != 0)
                vm.SelectedNavIndex = 0;
            Dispatcher.UIThread.Post(() => _chatView?.FocusComposer(), DispatcherPriority.Input);
            e.Handled = true;
            return;
        }

        // ── Ctrl+K — Focus chat search ──
        if (ctrl && !shift && e.Key == Key.K)
        {
            if (vm.SelectedNavIndex != 0)
                vm.SelectedNavIndex = 0;
            Dispatcher.UIThread.Post(() =>
            {
                _chatSearchBox?.Focus();
                _chatSearchBox?.SelectAll();
            }, DispatcherPriority.Input);
            e.Handled = true;
            return;
        }

        // ── Ctrl+, — Settings ──
        if (ctrl && !shift && e.Key == Key.OemComma)
        {
            vm.SelectedNavIndex = 6;
            e.Handled = true;
            return;
        }

        // ── Ctrl+1..7 — Tab navigation ──
        if (ctrl && !shift)
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
                _ => -1
            };
            if (tabIndex >= 0)
            {
                vm.SelectedNavIndex = tabIndex;
                e.Handled = true;
                return;
            }
        }

        // ── Escape — Clear search / deselect chat ──
        if (e.Key == Key.Escape && noMods)
        {
            // If search has text, clear it
            if (vm.SelectedNavIndex == 0 && !string.IsNullOrEmpty(vm.ChatSearchQuery))
            {
                vm.ChatSearchQuery = "";
                e.Handled = true;
                return;
            }
        }
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    /// <summary>Handle WindowState changes: when minimized + tray enabled, hide to tray.</summary>
    private void OnWindowStateChanged()
    {
        ApplyWindowContentPaddingForState();

        if (WindowState == WindowState.Minimized
            && DataContext is MainViewModel vm
            && vm.SettingsVM.MinimizeToTray)
        {
            HideToTray();
        }
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
            UpdateOnboarding(vm.IsOnboarded);
            ShowPage(vm.SelectedNavIndex);
            UpdateNavHighlight(vm.SelectedNavIndex);

            // Attach ListBox handlers once layout is ready
            Dispatcher.UIThread.Post(() =>
            {
                AttachListBoxHandlers();
                SyncListBoxSelection(vm.ActiveChatId);
                RebuildProjectFilterBar(vm);
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
                    RebuildProjectFilterBar(vm);
                }
            };

            // When project list changes, rebuild filter bar
            vm.Projects.CollectionChanged += (_, _) =>
            {
                Dispatcher.UIThread.Post(() => RebuildProjectFilterBar(vm), DispatcherPriority.Loaded);
            };

            // When chat groups are rebuilt, re-attach ListBox handlers, sync selection, and set project labels
            vm.ChatGroups.CollectionChanged += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    AttachListBoxHandlers();
                    SyncListBoxSelection(vm.ActiveChatId);
                    ApplyProjectLabelsToChats(vm);
                    ApplyMoveToProjectMenus(vm);
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

    private void UpdateNavHighlight(int index)
    {
        for (int i = 0; i < _navButtons.Length; i++)
        {
            var btn = _navButtons[i];
            if (btn is null) continue;

            if (i == index)
                btn.Classes.Add("active");
            else
                btn.Classes.Remove("active");
        }
    }

    private void NewChatButton_Click(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => _chatView?.FocusComposer(), DispatcherPriority.Input);
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

    private void RebuildProjectFilterBar(MainViewModel vm)
    {
        if (_projectFilterBar is null) return;

        // Unsubscribe all previous project PropertyChanged handlers
        foreach (var (project, handler) in _projectFilterHandlers)
            project.PropertyChanged -= handler;
        _projectFilterHandlers.Clear();

        _projectFilterBar.Children.Clear();

        var isAll = !vm.SelectedProjectFilter.HasValue;

        // "All" pill
        var allBtn = new Button
        {
            Content = Loc.Sidebar_All,
        };
        allBtn.Classes.Add("project-pill");
        allBtn[!Avalonia.Controls.Primitives.TemplatedControl.FontSizeProperty] = allBtn.GetResourceObservable("Font.SizeCaption").ToBinding();
        allBtn.Classes.Add(isAll ? "accent" : "subtle");
        allBtn.Click += (_, _) => vm.ClearProjectFilterCommand.Execute(null);
        _projectFilterBar.Children.Add(allBtn);

        // One pill per project
        foreach (var project in vm.Projects)
        {
            var isActive = vm.SelectedProjectFilter == project.Id;

            // Build pill content with busy indicator dot
            var dot = new Border
            {
                Name = "PillBusyDot",
                Width = 6, Height = 6,
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
                IsVisible = project.IsRunning && !isActive,
            };
            dot[!Border.BackgroundProperty] = dot.GetResourceObservable("Brush.AccentDefault").ToBinding();

            // Listen for project running state changes
            var capturedDot = dot;
            var capturedProject = project;
            var capturedIsActive = isActive;
            PropertyChangedEventHandler handler = (_, args) =>
            {
                if (args.PropertyName == nameof(Project.IsRunning))
                    Dispatcher.UIThread.Post(() => capturedDot.IsVisible = capturedProject.IsRunning && !capturedIsActive);
            };
            project.PropertyChanged += handler;
            _projectFilterHandlers.Add((project, handler));

            var nameText = new TextBlock { Text = project.Name, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            panel.Children.Add(dot);
            panel.Children.Add(nameText);

            var btn = new Button
            {
                Content = panel,
            };
            btn.Classes.Add("project-pill");
            btn[!Avalonia.Controls.Primitives.TemplatedControl.FontSizeProperty] = btn.GetResourceObservable("Font.SizeCaption").ToBinding();
            btn.Classes.Add(isActive ? "accent" : "subtle");
            var p = project; // capture
            btn.Click += (_, _) => vm.SelectProjectFilterCommand.Execute(p);
            _projectFilterBar.Children.Add(btn);

            if (isActive)
                Dispatcher.UIThread.Post(() => btn.BringIntoView(), DispatcherPriority.Loaded);
        }
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
        var sidebarProjects = _sidebarPanels.Length > 1 ? _sidebarPanels[1] : null;
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

    /// <summary>Whether the browser panel is currently visible.</summary>
    private bool IsBrowserOpen => _browserIsland is { IsVisible: true };

    private void EnsurePageViewLoaded(int index)
    {
        if (DataContext is not MainViewModel vm) return;

        switch (index)
        {
            case 1:
                if (_projectsHost is not null && _projectsHost.Content is null)
                    _projectsHost.Content = new ProjectsView { DataContext = vm.ProjectsVM };
                break;
            case 2:
                if (_skillsHost is not null && _skillsHost.Content is null)
                    _skillsHost.Content = new SkillsView { DataContext = vm.SkillsVM };
                break;
            case 3:
                if (_agentsHost is not null && _agentsHost.Content is null)
                    _agentsHost.Content = new AgentsView { DataContext = vm.AgentsVM };
                break;
            case 4:
                if (_memoriesHost is not null && _memoriesHost.Content is null)
                    _memoriesHost.Content = new MemoriesView { DataContext = vm.MemoriesVM };
                break;
            case 5:
                if (_mcpServersHost is not null && _mcpServersHost.Content is null)
                    _mcpServersHost.Content = new McpServersView { DataContext = vm.McpServersVM };
                break;
            case 6:
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
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Padding = new Thickness(10, 8),
                Background = Avalonia.Media.Brushes.Transparent,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
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
            _ = HighlightAllLinesAsync(file.Change.FullPath, diffView);
        }
        else
        {
            _ = LoadGitDiffWithLineNumbersAsync(file.Change, diffView);
        }
    }

    private async Task HighlightAllLinesAsync(string filePath, DiffView diffView)
    {
        var lineCount = await Task.Run(() =>
        {
            try { return System.IO.File.Exists(filePath) ? System.IO.File.ReadAllLines(filePath).Length : 0; }
            catch { return 0; }
        });
        var allLines = new HashSet<int>();
        for (int i = 0; i < lineCount; i++) allLines.Add(i);
        Dispatcher.UIThread.Post(() => diffView.SetFileDiffWithChangedLines(filePath, allLines));
    }

    private void OnDiffBreadcrumbClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (_lastGitChangesList is not null)
            ShowGitChangesPanel(_lastGitChangesList);
    }

    private async Task LoadGitDiffWithLineNumbersAsync(GitFileChange change, DiffView diffView)
    {
        var repoDir = System.IO.Path.GetDirectoryName(change.FullPath) ?? "";
        var diff = await GitService.GetFileDiffAsync(repoDir, System.IO.Path.GetFileName(change.FullPath));
        if (diff is null)
            diff = await GitService.GetFileDiffAsync(repoDir, change.RelativePath);

        var changedLines = new HashSet<int>();
        if (diff is not null)
        {
            // Parse @@ headers to get new-file line numbers for changed lines
            foreach (var line in diff.Split('\n'))
            {
                if (line.StartsWith("@@"))
                {
                    // Format: @@ -oldStart[,oldCount] +newStart[,newCount] @@
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"\+(\d+)(?:,(\d+))?");
                    if (match.Success)
                    {
                        var start = int.Parse(match.Groups[1].Value) - 1; // 0-indexed
                        // Track which lines in this hunk are actually added/modified
                        int currentLine = start;
                        // Find the content lines after this @@ header
                        var allLines = diff.Split('\n');
                        int idx = Array.IndexOf(allLines, line);
                        for (int i = idx + 1; i < allLines.Length; i++)
                        {
                            var hunkLine = allLines[i];
                            if (hunkLine.StartsWith("@@") || hunkLine.StartsWith("diff ")) break;
                            if (hunkLine.StartsWith('+') && !hunkLine.StartsWith("+++"))
                            {
                                changedLines.Add(currentLine);
                                currentLine++;
                            }
                            else if (hunkLine.StartsWith('-') && !hunkLine.StartsWith("---"))
                            {
                                // Removed line — doesn't advance new file position
                            }
                            else
                            {
                                // Context line
                                currentLine++;
                            }
                        }
                    }
                }
            }
        }

        Dispatcher.UIThread.Post(() => diffView.SetFileDiffWithChangedLines(change.FullPath, changedLines));
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
