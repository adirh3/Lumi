using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Lumi.Models;
using Lumi.ViewModels;
using StrataTheme.Animation;

namespace Lumi.Views;

/// <summary>The Workspace home page: the chat's plan, agents, git changes and lists at a glance.</summary>
public partial class WorkspaceOverview : UserControl
{
    private static readonly TimeSpan EntranceDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan EntranceStagger = TimeSpan.FromMilliseconds(40);
    private const int EntranceStaggerLimit = 5;
    private const double ChipWheelStep = 48;
    // The row fades out over this share of its width at each end (the Ws.ChipFade* brushes).
    private const double ChipFadeShare = 0.12;

    private ScrollViewer? _scroller;
    private Panel? _content;
    private ScrollViewer? _chipScroller;
    private ItemsControl? _chipBar;
    private ChatViewModel? _observed;
    private Chat? _chipRowChat;

    public WorkspaceOverview()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _scroller = this.FindControl<ScrollViewer>("WsScroller");
        _content = this.FindControl<Panel>("WsContent");
        _chipBar = this.FindControl<ItemsControl>("WsCategoryBar");
        _chipScroller = this.FindControl<ScrollViewer>("WsCategoryScroller");
        if (this.FindControl<TextBox>("WsSearchBox") is { } searchBox)
            searchBox.KeyDown += OnSearchKeyDown;

        if (_chipScroller is { } chips)
        {
            chips.ScrollChanged += (_, _) => UpdateChipFades(chips);
            // Tunnel, so a mouse wheel slides the chips sideways before the presenter reads it as vertical.
            chips.AddHandler(PointerWheelChangedEvent, OnChipWheel, RoutingStrategies.Tunnel);
            chips.AddHandler(GotFocusEvent, OnChipFocused);
        }
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
        if (viewModel is null)
            return;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        FollowChat(viewModel.CurrentChat);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.CurrentChat))
        {
            FollowChat(_observed?.CurrentChat);
            return;
        }

        if (e.PropertyName != nameof(ChatViewModel.WorkspaceCategory))
            return;

        // Another kind starts at its top, not wherever the previous list was scrolled to.
        if (_scroller is not null)
            _scroller.Offset = default;

        // A kind chosen elsewhere ("Show all", Escape) may sit beyond the edge of the chip row.
        Dispatcher.UIThread.Post(RevealSelectedChip, DispatcherPriority.Loaded);
        PlayCategoryEntrance();
    }

    /// <summary>
    /// Another chat's row starts at its beginning. The chat view-model also re-announces
    /// CurrentChat whenever this chat's details change (each model call, its project, skills),
    /// which must leave the row where the user slid it.
    /// </summary>
    private void FollowChat(Chat? chat)
    {
        if (ReferenceEquals(chat, _chipRowChat))
            return;

        _chipRowChat = chat;
        if (_chipScroller is not null)
            _chipScroller.Offset = default;
    }

    private void RevealSelectedChip()
    {
        var selected = _observed?.WorkspaceCategories.FirstOrDefault(static chip => chip.IsSelected);
        if (selected is { IsAvailable: true } && _chipBar?.ContainerFromItem(selected) is { } chip)
            RevealChip(chip);
    }

    /// <summary>Tabbing along the row keeps the focused chip on screen.</summary>
    private void OnChipFocused(object? sender, FocusChangedEventArgs e)
    {
        if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional && e.Source is Control chip)
            RevealChip(chip);
    }

    /// <summary>Slides the row just far enough that the chip clears the fade at its edge.</summary>
    private void RevealChip(Control chip)
    {
        var margin = (_chipScroller?.Viewport.Width ?? 0) * ChipFadeShare;
        chip.BringIntoView(new Rect(-margin, 0, chip.Bounds.Width + margin * 2, chip.Bounds.Height));
    }

    /// <summary>The row fades out at whichever end has more chips beyond it.</summary>
    private static void UpdateChipFades(ScrollViewer scroller)
    {
        var hidden = scroller.Extent.Width - scroller.Viewport.Width;
        var offset = scroller.Offset.X;
        scroller.Classes.Set("fade-start", hidden > 1 && offset > 1);
        scroller.Classes.Set("fade-end", hidden > 1 && hidden - offset > 1);
    }

    private void OnChipWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_chipScroller is not { } scroller)
            return;

        var hidden = scroller.Extent.Width - scroller.Viewport.Width;
        if (hidden <= 1)
            return;

        var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
        scroller.Offset = new Vector(
            Math.Clamp(scroller.Offset.X - delta * ChipWheelStep, 0, hidden),
            scroller.Offset.Y);
        e.Handled = true;
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
