using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
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
    private Button?[] _navButtons = [];
    private Panel? _renameOverlay;
    private TextBox? _renameTextBox;
    private bool _suppressSelectionSync;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _onboardingPanel = this.FindControl<Panel>("OnboardingPanel");
        _mainPanel = this.FindControl<DockPanel>("MainPanel");
        _chatIsland = this.FindControl<Border>("ChatIsland");

        _pages =
        [
            _chatIsland,
            this.FindControl<Control>("PageProjects"),
            this.FindControl<Control>("PageSkills"),
            this.FindControl<Control>("PageAgents"),
            this.FindControl<Control>("PageSettings"),
        ];

        _navButtons =
        [
            this.FindControl<Button>("NavChat"),
            this.FindControl<Button>("NavProjects"),
            this.FindControl<Button>("NavSkills"),
            this.FindControl<Button>("NavAgents"),
            this.FindControl<Button>("NavSettings"),
        ];

        _renameOverlay = this.FindControl<Panel>("RenameOverlay");
        _renameTextBox = this.FindControl<TextBox>("RenameTextBox");
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
            }, DispatcherPriority.Loaded);

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
            };

            // When chat groups are rebuilt, re-attach ListBox handlers and sync selection
            vm.ChatGroups.CollectionChanged += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    AttachListBoxHandlers();
                    SyncListBoxSelection(vm.ActiveChatId);
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
}
