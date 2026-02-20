using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
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
    private Border? _chatIsland;
    private Panel? _contentArea;
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
    private bool _suppressSelectionSync;

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
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _onboardingPanel = this.FindControl<Panel>("OnboardingPanel");
        _mainPanel = this.FindControl<DockPanel>("MainPanel");
        _chatIsland = this.FindControl<Border>("ChatIsland");
        _contentArea = this.FindControl<Panel>("ContentArea");

        _pages =
        [
            _chatIsland,                                       // 0 = Chat
            this.FindControl<Control>("PageProjects"),         // 1
            this.FindControl<Control>("PageSkills"),           // 2
            this.FindControl<Control>("PageAgents"),           // 3
            this.FindControl<Control>("PageMemories"),         // 4
            this.FindControl<Control>("PageSettings"),         // 5
        ];

        _sidebarPanels =
        [
            this.FindControl<Panel>("SidebarChat"),            // 0
            this.FindControl<Panel>("SidebarProjects"),        // 1
            this.FindControl<Panel>("SidebarSkills"),          // 2
            this.FindControl<Panel>("SidebarAgents"),          // 3
            this.FindControl<Panel>("SidebarMemories"),        // 4
            this.FindControl<Panel>("SidebarSettings"),        // 5
        ];

        _navButtons =
        [
            this.FindControl<Button>("NavChat"),
            this.FindControl<Button>("NavProjects"),
            this.FindControl<Button>("NavSkills"),
            this.FindControl<Button>("NavAgents"),
            this.FindControl<Button>("NavMemories"),
            this.FindControl<Button>("NavSettings"),
        ];

        _renameOverlay = this.FindControl<Panel>("RenameOverlay");
        _renameTextBox = this.FindControl<TextBox>("RenameTextBox");
        _projectFilterBar = this.FindControl<StackPanel>("ProjectFilterBar");

        _onboardingSexCombo = this.FindControl<ComboBox>("OnboardingSexCombo");
        _onboardingLanguageCombo = this.FindControl<ComboBox>("OnboardingLanguageCombo");
        _chatSearchBox = this.FindControl<TextBox>("ChatSearchBox");
        _chatView = this.FindControl<ChatView>("PageChat");

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
            vm.SelectedNavIndex = 5;
            e.Handled = true;
            return;
        }

        // ── Ctrl+1..6 — Tab navigation ──
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

    /// <summary>Handle WindowState changes: when minimized + tray enabled, hide to tray.
    /// When maximized, add extra margin to compensate for the hidden resize border.</summary>
    private void OnWindowStateChanged()
    {
        if (WindowState == WindowState.Minimized
            && DataContext is MainViewModel vm
            && vm.SettingsVM.MinimizeToTray)
        {
            HideToTray();
        }

        // On Windows, maximized windows extend ~7px beyond screen edges
        // to hide resize borders, so add extra margin to compensate.
        if (_contentArea is not null)
        {
            _contentArea.Margin = WindowState == WindowState.Maximized
                ? new Thickness(4, 38, 6, 6)
                : new Thickness(0, 32, 0, 0);
        }
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
            }, DispatcherPriority.Loaded);

            // Wire ProjectsVM chat open to navigate to chat tab
            vm.ProjectsVM.ChatOpenRequested += chat => vm.OpenChatFromProjectCommand.Execute(chat);

            // Wire settings for density and font size
            vm.SettingsVM.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SettingsViewModel.FontSize))
                    ApplyFontSize(vm.SettingsVM.FontSize);
            };

            // Apply initial font size
            ApplyFontSize(vm.SettingsVM.FontSize);

            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.IsDarkTheme))
                {
                    if (Application.Current is not null)
                        Application.Current.RequestedThemeVariant = vm.IsDarkTheme
                            ? ThemeVariant.Dark
                            : ThemeVariant.Light;
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
                        }, DispatcherPriority.Loaded);
                    }
                }
                else if (args.PropertyName == nameof(MainViewModel.ActiveChatId))
                {
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

        // When projects tab is shown, update chat counts
        if (index == 1 && DataContext is MainViewModel vm)
        {
            Dispatcher.UIThread.Post(() => ApplyProjectChatCounts(vm), DispatcherPriority.Loaded);
        }

        // When settings tab is shown, refresh stats
        if (index == 5 && DataContext is MainViewModel svm)
        {
            if (svm.SettingsVM.SelectedPageIndex < 0)
                svm.SettingsVM.SelectedPageIndex = 0;
            svm.SettingsVM.RefreshStats();
        }
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
            Padding = new Thickness(10, 4),
            MinHeight = 0,
            MinWidth = 0,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(0),
            Focusable = false
        };
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
                Padding = new Thickness(10, 4),
                MinHeight = 0,
                MinWidth = 0,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(0),
                Focusable = false
            };
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
}
