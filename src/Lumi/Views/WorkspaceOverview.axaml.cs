using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Lumi.ViewModels;
using StrataTheme.Animation;

namespace Lumi.Views;

/// <summary>The Workspace home page: the chat's plan, agents, git changes and lists at a glance.</summary>
public partial class WorkspaceOverview : UserControl
{
    private static readonly TimeSpan EntranceDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan EntranceStagger = TimeSpan.FromMilliseconds(40);
    private const int EntranceStaggerLimit = 5;

    private ScrollViewer? _scroller;
    private Panel? _content;
    private ChatViewModel? _observed;

    public WorkspaceOverview()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _scroller = this.FindControl<ScrollViewer>("WsScroller");
        _content = this.FindControl<Panel>("WsContent");
        if (this.FindControl<TextBox>("WsSearchBox") is { } searchBox)
            searchBox.KeyDown += OnSearchKeyDown;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Observe(DataContext as ChatViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Observe(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (VisualRoot is not null)
            Observe(DataContext as ChatViewModel);
    }

    // Only while on screen, so a long-lived chat view-model never holds on to a discarded view.
    private void Observe(ChatViewModel? viewModel)
    {
        if (ReferenceEquals(_observed, viewModel))
            return;

        if (_observed is not null)
            _observed.PropertyChanged -= OnViewModelPropertyChanged;

        _observed = viewModel;
        if (viewModel is not null)
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChatViewModel.WorkspaceCategory))
            return;

        // Another kind starts at its top, not wherever the previous list was scrolled to.
        if (_scroller is not null)
            _scroller.Offset = default;

        PlayCategoryEntrance();
    }

    /// <summary>The kind just chosen rises into place, block by block.</summary>
    private void PlayCategoryEntrance()
    {
        if (_content is null || _observed is { AreAnimationsEnabled: false })
            return;

        var index = 0;
        foreach (var block in _content.Children)
        {
            if (block.IsVisible)
                SlideFadeEntrance.Play(block, offsetY: 8, EntranceDuration, EntranceStagger * Math.Min(index++, EntranceStaggerLimit));
        }
    }

    /// <summary>Escape clears an active search before it bubbles up to close anything else.</summary>
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not ChatViewModel { HasWorkspaceSearch: true } viewModel)
            return;

        viewModel.WorkspaceSearchText = "";
        e.Handled = true;
    }
}
