using System;
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
    private ComboBox? _onboardingSexCombo;
    private ComboBox? _onboardingLanguageCombo;
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
    private CancellationTokenSource? _previewAnimCts;
    private bool _suppressSelectionSync;
    private CancellationTokenSource? _browserAnimCts;
    private CancellationTokenSource? _shellAnimCts;
    private int _currentShellIndex = -1;

    public MainWindow()
    {
        InitializeComponent();
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
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
                UpdateAcrylicFallbackOpacity();
        };

        Opened += (_, _) =>
        {
            UpdateAcrylicFallbackOpacity();
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

        _onboardingSexCombo = this.FindControl<ComboBox>("OnboardingSexCombo");
        _onboardingLanguageCombo = this.FindControl<ComboBox>("OnboardingLanguageCombo");
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

        // Populate onboarding ComboBoxes
        if (_onboardingSexCombo is not null)
        {
            _onboardingSexCombo.ItemsSource = new[]
            {
                Loc.Onboarding_SexMale,
                Loc.Onboarding_SexFemale,
                Loc.Onboarding_SexPreferNot,
            };
            _onboardingSexCombo.PlaceholderText = Loc.Onboarding_Sex;
            _onboardingSexCombo.SelectedIndex = 0;
        }

        if (_onboardingLanguageCombo is not null)
        {
            _onboardingLanguageCombo.ItemsSource =
                Loc.AvailableLanguages.Select(l => $"{l.DisplayName} ({l.Code})").ToArray();
            _onboardingLanguageCombo.PlaceholderText = Loc.Onboarding_Language;
            _onboardingLanguageCombo.SelectedIndex = 0;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // If minimize-to-tray is enabled, hide instead of closing
        if (DataContext is MainViewModel vm && vm.SettingsVM.MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
        }

        base.OnClosing(e);
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
        if (_windowContentRoot is null)
            return;

        _windowContentRoot.Padding = WindowState == WindowState.Maximized
            ? new Thickness(6)
            : new Thickness(0);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MainViewModel vm)
        {
            UpdateOnboarding(vm.IsOnboarded);
            ShowPage(vm.SelectedNavIndex);
            UpdateNavHighlight(vm.SelectedNavIndex);

            // Attach ListBox handlers once layout is ready
            Dispatcher.UIThread.Post(() =>
            {
                AttachListBoxHandlers();
                SyncListBoxSelection(vm.ActiveChatId);
                RebuildProjectFilterBar(vm);
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

            // Refresh composer MCP catalogs when MCP config changes (install/delete/toggle)
            vm.McpServersVM.McpConfigChanged += () =>
            {
                Dispatcher.UIThread.Post(() => _chatView?.PopulateComposerCatalogs(vm.ChatVM));
            };

            // Wire settings for density and font size
            vm.SettingsVM.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SettingsViewModel.FontSize))
                    ApplyFontSize(vm.SettingsVM.FontSize);
            };

            // Apply initial font size
            ApplyFontSize(vm.SettingsVM.FontSize);

            // Wire browser panel show/hide
            vm.ChatVM.BrowserShowRequested += () =>
            {
                Dispatcher.UIThread.Post(() => { ShowBrowserPanel(); vm.ChatVM.IsBrowserOpen = IsBrowserOpen; });
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
            vm.ChatVM.DiffShowRequested += (filePath, oldText, newText) =>
            {
                Dispatcher.UIThread.Post(() => ShowDiffPanel(filePath, oldText, newText));
            };
            vm.ChatVM.DiffHideRequested += () =>
            {
                Dispatcher.UIThread.Post(() => HideDiffPanel());
            };

            // Close diff button
            var closeDiffBtn = this.FindControl<Button>("CloseDiffButton");
            if (closeDiffBtn is not null)
                closeDiffBtn.Click += (_, _) => { HideDiffPanel(); if (DataContext is MainViewModel m) m.ChatVM.IsDiffOpen = false; };

            // Sync initial browser theme
            vm.BrowserService.SetTheme(vm.IsDarkTheme);

            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.IsDarkTheme))
                {
                    if (Application.Current is not null)
                        Application.Current.RequestedThemeVariant = vm.IsDarkTheme
                            ? ThemeVariant.Dark
                            : ThemeVariant.Light;
                    vm.BrowserService.SetTheme(vm.IsDarkTheme);
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
                        var chatView = this.FindControl<ChatView>("PageChat");
                        chatView?.PopulateComposerCatalogs(vm.ChatVM);

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
                    // Hide browser/diff when switching chats — each chat starts fresh
                    HideBrowserPanel();
                    HideDiffPanel();
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
        if (_mainPanel is not null) _mainPanel.IsVisible = isOnboarded;
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
        }
        else if (_browserIsland is { IsVisible: true })
        {
            // Returning to chat with browser open — show the overlay and refresh bounds
            if (DataContext is MainViewModel vm2 && vm2.BrowserService.Controller is not null)
                vm2.BrowserService.Controller.IsVisible = true;
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
            await Task.Delay(30, CancellationToken.None);
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
            var btn = new Button
            {
                Content = project.Name,
            };
            btn.Classes.Add("project-pill");
            btn[!Avalonia.Controls.Primitives.TemplatedControl.FontSizeProperty] = btn.GetResourceObservable("Font.SizeCaption").ToBinding();
            btn.Classes.Add(isActive ? "accent" : "subtle");
            var p = project; // capture
            btn.Click += (_, _) => vm.SelectProjectFilterCommand.Execute(p);
            _projectFilterBar.Children.Add(btn);
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

    private void UpdateAcrylicFallbackOpacity()
    {
        if (_acrylicFallback is null) return;

        _acrylicFallback.Opacity = ActualTransparencyLevel == WindowTransparencyLevel.None
            ? 0.88
            : 0.8;
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

    private void EnsureBrowserViewLoaded(MainViewModel vm)
    {
        if (_browserView is not null) return;
        if (_browserHost is null) return;

        _browserView = new BrowserView();
        _browserHost.Content = _browserView;
        _browserView.SetBrowserService(vm.BrowserService, vm.DataStore);
    }

    private async void ShowBrowserPanel()
    {
        if (_browserIsland is null || _chatContentGrid is null || _chatIsland is null) return;
        if (_browserIsland.IsVisible) return;

        // Hide diff panel if open (they share column 2)
        if (_diffIsland is { IsVisible: true })
        {
            _diffIsland.IsVisible = false;
            _diffIsland.Opacity = 1;
            _diffIsland.RenderTransform = null;
            if (DataContext is MainViewModel vmd) vmd.ChatVM.IsDiffOpen = false;
        }

        // Cancel any in-progress animation
        var ct = ReplaceCancellationTokenSource(ref _browserAnimCts).Token;

        var vm = DataContext as MainViewModel;

        // Ensure we're on the Chat tab
        if (vm is not null && vm.SelectedNavIndex != 0)
            vm.SelectedNavIndex = 0;

        if (vm is not null)
            EnsureBrowserViewLoaded(vm);

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

        if (vm?.BrowserService.Controller is not null)
            vm.BrowserService.Controller.IsVisible = true;
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
        if (DataContext is MainViewModel vm && vm.BrowserService.Controller is not null)
            vm.BrowserService.Controller.IsVisible = false;

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
        if (_browserSplitter is not null)
            _browserSplitter.IsVisible = false;

        // Reset to single-column layout
        var defs = _chatContentGrid.ColumnDefinitions;
        while (defs.Count < 3)
            defs.Add(new ColumnDefinition());
        defs[0].Width = new GridLength(1, GridUnitType.Star);
        defs[1].Width = new GridLength(0);
        defs[2].Width = new GridLength(0);

        if (_chatIsland is not null)
            Grid.SetColumn(_chatIsland, 0);
        Grid.SetColumn(_browserIsland, 2);
    }

    /// <summary>Whether the diff panel is currently visible.</summary>
    private bool IsDiffOpen => _diffIsland is { IsVisible: true };

    private void EnsureDiffViewLoaded()
    {
        if (_diffView is not null) return;
        if (_diffHost is null) return;

        _diffView = new DiffView();
        _diffHost.Content = _diffView;
    }

    private async void ShowDiffPanel(string filePath, string? oldText, string? newText)
    {
        if (_diffIsland is null || _chatContentGrid is null || _chatIsland is null) return;

        // Hide browser panel if it's open (they share column 2)
        if (_browserIsland is { IsVisible: true })
        {
            _browserIsland.IsVisible = false;
            _browserIsland.Opacity = 1;
            _browserIsland.RenderTransform = null;
            if (_browserSplitter is not null) _browserSplitter.IsVisible = false;
            if (DataContext is MainViewModel vmb)
            {
                if (vmb.BrowserService.Controller is not null)
                    vmb.BrowserService.Controller.IsVisible = false;
                vmb.ChatVM.IsBrowserOpen = false;
            }
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
            _diffFileNameText.Text = System.IO.Path.GetFileName(filePath);

        // Set diff content
        _diffView?.SetDiff(filePath, oldText, newText);

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

        // Reset to single-column if browser isn't open either
        if (!(_browserIsland?.IsVisible ?? false))
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
}
