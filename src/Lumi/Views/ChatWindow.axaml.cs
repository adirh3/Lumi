using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Services;
using Lumi.ViewModels;

namespace Lumi.Views;

public partial class ChatWindow : Window
{
    private const double BaseTitleBarHeight = 38;
    private readonly ChatWindowViewModel? _viewModel;
    private ChatWorkspaceView? _chatWorkspace;

    public ChatWindow()
    {
        InitializeComponent();
        WindowChromeInterop.EnableNativeMinMaxAnimations(this);
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = BaseTitleBarHeight;
        Background = Brushes.Transparent;
        TransparencyBackgroundFallback = Brushes.Transparent;
        AppIcon.ApplyWindowIcon(this);
    }

    public ChatWindow(DataStore dataStore, ChatWindowViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        if (_chatWorkspace is not null)
            _chatWorkspace.DataStore = dataStore;
        ApplyUiScaleToChrome(dataStore.Data.Settings.UiScalePercent);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateAutomationName();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _chatWorkspace = this.FindControl<ChatWorkspaceView>("DetachedChatView");
    }

    public void FocusComposer()
    {
        Dispatcher.UIThread.Post(() => _chatWorkspace?.FocusComposer(), DispatcherPriority.Input);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        var scaleDelta = UiScaleService.GetShortcutDelta(e.Key, e.KeyModifiers);
        if (scaleDelta != 0 && Application.Current is App app && app.TryAdjustUiScale(scaleDelta))
            e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _chatWorkspace?.Dispose();
        base.OnClosed(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatWindowViewModel.WindowTitle))
            UpdateAutomationName();
    }

    private void UpdateAutomationName()
    {
        var windowTitle = _viewModel?.WindowTitle ?? Loc.Sidebar_NewChat;
        Title = windowTitle;
        AutomationProperties.SetName(this, $"{windowTitle} - Lumi");
    }

    internal void ApplyUiScaleToChrome(int scalePercent)
        => ExtendClientAreaTitleBarHeightHint = BaseTitleBarHeight * UiScaleService.GetScale(scalePercent);
}
