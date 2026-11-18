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
using Lumi.Services.Appearance;
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
#if !WINDOWS
        // Keep this window's native host attached even when its Workspace browser page is closed.
        var scaledContent = Content as Control;
        Content = null;
        var nativeWebViewLayer = NativeBrowserLogic.CreateHostLayer();
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
        Background = Brushes.Transparent;
        TransparencyBackgroundFallback = Brushes.Transparent;
        AppIcon.ApplyWindowIcon(this);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ActualTransparencyLevelProperty)
                UpdateTransparencyFallbackOpacity();
        };
        Opened += (_, _) =>
        {
            UpdateTransparencyFallbackOpacity();
            _viewModel?.ChatVM.RestoreBrowserPanelForActiveChat();
        };
    }

    public ChatWindow(DataStore dataStore, ChatWindowViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        if (_chatWorkspace is not null)
        {
            _chatWorkspace.DataStore = dataStore;
            _chatWorkspace.IsPageContentVisible = !viewModel.HasOpenDialog;
        }
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

        if (_viewModel is { IsRenaming: true } && e.KeyModifiers == KeyModifiers.None
            && e.Key is Key.Enter or Key.Escape)
        {
            if (e.Key == Key.Enter)
                _viewModel.CommitRenameChatCommand.Execute(null);
            else
                _viewModel.CancelRenameChatCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (_viewModel is { IsWorktreeDeleteDialogOpen: true } && e.KeyModifiers == KeyModifiers.None
            && e.Key == Key.Escape)
        {
            _viewModel.CancelDeleteWorktreeDialogCommand.Execute(null);
            e.Handled = true;
            return;
        }

        var scaleDelta = UiScaleService.GetShortcutDelta(e.Key, e.KeyModifiers);
        if (scaleDelta != 0 && Application.Current is App app && app.TryAdjustUiScale(scaleDelta))
            e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _chatWorkspace?.Dispose();
#if !WINDOWS
        BrowserService.UnregisterHostLayer(this);
#endif
        base.OnClosed(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatWindowViewModel.WindowTitle))
            UpdateAutomationName();
        else if (e.PropertyName == nameof(ChatWindowViewModel.HasOpenDialog)
                 && _viewModel is { } viewModel && _chatWorkspace is { } workspace)
            workspace.IsPageContentVisible = !viewModel.HasOpenDialog;
        else if (e.PropertyName == nameof(ChatWindowViewModel.IsRenaming))
        {
            if (_viewModel?.IsRenaming == true)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_viewModel.IsRenaming && this.FindControl<TextBox>("RenameTextBox") is { } editor)
                    {
                        editor.Focus();
                        editor.SelectAll();
                    }
                }, DispatcherPriority.Input);
            }
            else
                FocusComposer();
        }
    }

    private void UpdateAutomationName()
    {
        var windowTitle = _viewModel?.WindowTitle ?? Loc.Sidebar_NewChat;
        Title = windowTitle;
        AutomationProperties.SetName(this, $"{windowTitle} - Lumi");
    }

    internal void ApplyUiScaleToChrome(int scalePercent)
        => ExtendClientAreaTitleBarHeightHint = BaseTitleBarHeight * UiScaleService.GetScale(scalePercent);

    private void UpdateTransparencyFallbackOpacity()
    {
        if (this.FindControl<Border>("AcrylicFallback") is { } fallback)
            fallback.Opacity = WindowMaterials.FallbackOpacityFor(ActualTransparencyLevel, OperatingSystem.IsMacOS());
    }
}
