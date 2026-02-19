using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.ViewModels;

namespace Lumi.Views;

public partial class MainWindow : Window
{
    private Panel? _onboardingPanel;
    private DockPanel? _mainPanel;
    private Border? _chatIsland;
    private Control?[] _pages = [];
    private Panel?[] _sidebarPanels = [];
    private Button?[] _navButtons = [];
    private Panel? _renameOverlay;
    private TextBox? _renameTextBox;
    private StackPanel? _projectFilterBar;
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
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _onboardingPanel = this.FindControl<Panel>("OnboardingPanel");
        _mainPanel = this.FindControl<DockPanel>("MainPanel");
        _chatIsland = this.FindControl<Border>("ChatIsland");

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

            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.IsDarkTheme))
                {
                    if (Application.Current is not null)
                        Application.Current.RequestedThemeVariant = vm.IsDarkTheme
                            ? ThemeVariant.Dark
                            : ThemeVariant.Light;
                }
                else if (args.PropertyName == nameof(MainViewModel.IsOnboarded))
                {
                    UpdateOnboarding(vm.IsOnboarded);
                }
                else if (args.PropertyName == nameof(MainViewModel.SelectedNavIndex))
                {
                    ShowPage(vm.SelectedNavIndex);
                    UpdateNavHighlight(vm.SelectedNavIndex);

                    // Refresh composer catalogs when switching to chat tab
                    if (vm.SelectedNavIndex == 0)
                    {
                        var chatView = this.FindControl<ChatView>("PageChat");
                        chatView?.PopulateComposerCatalogs(vm.ChatVM);
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
            if (!lb.Classes.Contains("sidebar-list")) continue;
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
            if (!otherLb.Classes.Contains("sidebar-list")) continue;
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
                if (!lb.Classes.Contains("sidebar-list")) continue;

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

    private void RebuildProjectFilterBar(MainViewModel vm)
    {
        if (_projectFilterBar is null) return;
        _projectFilterBar.Children.Clear();

        var isAll = !vm.SelectedProjectFilter.HasValue;

        // "All" pill
        var allBtn = new Button
        {
            Content = "All",
            Padding = new Thickness(10, 4),
            MinHeight = 0,
            MinWidth = 0,
            FontSize = 11,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(0),
            Focusable = false
        };
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
                FontSize = 11,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(0),
                Focusable = false
            };
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
            if (menuItem.Header is not string header || header != "Move to Project") continue;

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
            countLabel.Text = count > 0 ? $"{count} chat{(count == 1 ? "" : "s")}" : "";
        }
    }
}
