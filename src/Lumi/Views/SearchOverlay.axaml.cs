using System;
using System.Linq;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Services;
using Lumi.ViewModels;
using StrataTheme;

namespace Lumi.Views;

public partial class SearchOverlay : UserControl
{
    private static readonly Easing SettleEasing = new CubicEaseOut();
    private const double FilterWheelStep = 48;

    private Control? _cardHost;
    private Control? _stratumLine;
    private TextBox? _searchInput;
    private ItemsControl? _resultsList;
    private ScrollViewer? _resultsScroller;
    private ItemsControl? _filterList;
    private ScrollViewer? _filterScroller;
    private Control? _emptyState;
    private Control? _searchingState;
    private SearchOverlayViewModel? _subscribedVm;
    private int _lastRenderedSelection = -1;
    private long _lastAnimateOpenTick;
    private bool _isUserNavigating;
    private bool _pendingViewUpdate;
    private bool _resetScrollOnNextUpdate;

    public SearchOverlay()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _cardHost = this.FindControl<Control>("CardHost");
        _stratumLine = this.FindControl<Control>("StratumLine");
        _searchInput = this.FindControl<TextBox>("SearchOverlayInput");
        _resultsList = this.FindControl<ItemsControl>("ResultsList");
        _resultsScroller = this.FindControl<ScrollViewer>("ResultsScroll");
        _filterList = this.FindControl<ItemsControl>("FilterList");
        _filterScroller = this.FindControl<ScrollViewer>("FilterScroll");
        _emptyState = this.FindControl<Control>("EmptyState");
        _searchingState = this.FindControl<Control>("SearchingState");

        if (_filterScroller is { } filters)
        {
            filters.ScrollChanged += (_, _) => UpdateEdgeFades(filters, horizontal: true);
            // Tunnel, so a mouse wheel scrolls the chips sideways before the presenter reads it as vertical.
            filters.AddHandler(PointerWheelChangedEvent, OnFilterWheel, RoutingStrategies.Tunnel);
        }

        if (_resultsScroller is { } results)
            results.ScrollChanged += (_, _) => UpdateEdgeFades(results, horizontal: false);
    }

    /// <summary>Animations follow Lumi's setting; the palette only ever lives in the main window.</summary>
    private bool AreAnimationsEnabled =>
        TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel { SettingsVM.ShowAnimations: false };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != IsVisibleProperty || !change.GetNewValue<bool>())
            return;

        var now = Environment.TickCount64;
        if (now - _lastAnimateOpenTick < 300)
            return;

        _lastAnimateOpenTick = now;

        if (_cardHost is not null)
            _cardHost.Opacity = 0;

        Dispatcher.UIThread.Post(() =>
        {
            _searchInput?.Focus();
            _searchInput?.SelectAll();
            AnimateOpen();
        }, DispatcherPriority.Render);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_subscribedVm is not null)
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;

        _subscribedVm = DataContext as SearchOverlayViewModel;
        if (_subscribedVm is not null)
            _subscribedVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(SearchOverlayViewModel.SelectedIndex):
                if (_isUserNavigating)
                    UpdateSelectionVisuals();
                else
                    PostDeferredViewUpdate();
                break;

            case nameof(SearchOverlayViewModel.FlatResults):
                _lastRenderedSelection = int.MinValue;
                PostDeferredViewUpdate();
                break;

            case nameof(SearchOverlayViewModel.SelectedCategory):
                PostDeferredViewUpdate(resetScroll: true);
                Dispatcher.UIThread.Post(BringSelectedFilterIntoView, DispatcherPriority.Loaded);
                break;

            case nameof(SearchOverlayViewModel.SearchQuery):
                PostDeferredViewUpdate(resetScroll: true);
                break;

            case nameof(SearchOverlayViewModel.IsSearching):
            case nameof(SearchOverlayViewModel.IsDeepSearching):
            case nameof(SearchOverlayViewModel.IsSearchIndicatorVisible):
                UpdateEmptyState();
                break;

            case nameof(SearchOverlayViewModel.IsOpen)
                when sender is SearchOverlayViewModel { IsOpen: false }:
                _lastRenderedSelection = -1;
                _resetScrollOnNextUpdate = false;
                if (_filterScroller is not null)
                    _filterScroller.Offset = default;
                break;
        }
    }

    private void PostDeferredViewUpdate(bool resetScroll = false)
    {
        _resetScrollOnNextUpdate |= resetScroll;
        if (_pendingViewUpdate)
            return;

        _pendingViewUpdate = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pendingViewUpdate = false;

            if (_resetScrollOnNextUpdate)
            {
                if (_resultsScroller is not null)
                    _resultsScroller.Offset = new Vector(_resultsScroller.Offset.X, 0);

                _resetScrollOnNextUpdate = false;
            }

            UpdateEmptyState();
            UpdateSelectionVisuals();
            Dispatcher.UIThread.Post(UpdateSelectionVisuals, DispatcherPriority.Background);
        }, DispatcherPriority.Render);
    }

    private void UpdateEmptyState()
    {
        if (DataContext is not SearchOverlayViewModel vm)
            return;

        var hasQuery = !string.IsNullOrWhiteSpace(vm.SearchQuery);
        var noResults = vm.ResultCount == 0;

        if (_emptyState is not null)
            _emptyState.IsVisible = !vm.IsSearchPending && hasQuery && noResults;

        // Gated on the debounced flag so a fast search leaves the body untouched instead of
        // flashing the centered "Searching..." panel between keystrokes.
        if (_searchingState is not null)
            _searchingState.IsVisible = vm.IsSearchIndicatorVisible && hasQuery && noResults;
    }

    private void UpdateSelectionVisuals()
    {
        if (DataContext is not SearchOverlayViewModel vm || _resultsList is null)
            return;

        var buttons = _resultsList
            .GetVisualDescendants()
            .OfType<Button>()
            .Where(static button => button.Classes.Contains("search-result-item"))
            .ToList();
        if (buttons.Count == 0 || vm.SelectedIndex == _lastRenderedSelection)
            return;

        _lastRenderedSelection = vm.SelectedIndex;
        for (var index = 0; index < buttons.Count; index++)
        {
            var button = buttons[index];
            var isSelected = index == vm.SelectedIndex;
            if (isSelected)
            {
                if (!button.Classes.Contains("selected"))
                    button.Classes.Add("selected");

                if (_isUserNavigating)
                    button.BringIntoView();
            }
            else
            {
                button.Classes.Remove("selected");
            }
        }
    }

    /// <summary>Keeps the chip in use visible when Ctrl+Tab moves to one scrolled out of the row.</summary>
    private void BringSelectedFilterIntoView()
    {
        var chip = _filterList?
            .GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(static button => button.Classes.Contains("selected"));
        chip?.BringIntoView();
    }

    /// <summary>A scroller fades out at whichever edge has more content beyond it.</summary>
    private static void UpdateEdgeFades(ScrollViewer scroller, bool horizontal)
    {
        var hidden = horizontal
            ? scroller.Extent.Width - scroller.Viewport.Width
            : scroller.Extent.Height - scroller.Viewport.Height;
        var offset = horizontal ? scroller.Offset.X : scroller.Offset.Y;
        scroller.Classes.Set("fade-start", hidden > 1 && offset > 1);
        scroller.Classes.Set("fade-end", hidden > 1 && hidden - offset > 1);
    }

    private void OnFilterWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_filterScroller is not { } scroller)
            return;

        var hidden = scroller.Extent.Width - scroller.Viewport.Width;
        if (hidden <= 1)
            return;

        var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
        scroller.Offset = new Vector(
            Math.Clamp(scroller.Offset.X - delta * FilterWheelStep, 0, hidden),
            scroller.Offset.Y);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is not SearchOverlayViewModel vm)
            return;

        switch (e.Key)
        {
            case Key.Escape:
                vm.Close();
                e.Handled = true;
                break;

            case Key.Down:
                MoveSelection(vm, 1);
                e.Handled = true;
                break;

            case Key.Up:
                MoveSelection(vm, -1);
                e.Handled = true;
                break;

            case Key.Enter:
                vm.SelectCurrent();
                e.Handled = true;
                break;

            case Key.Tab:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    vm.CycleCategory(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                }
                else
                {
                    MoveSelection(vm, e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                }

                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(SearchOverlayViewModel vm, int delta)
    {
        _isUserNavigating = true;
        try
        {
            vm.MoveSelection(delta);
        }
        finally
        {
            _isUserNavigating = false;
        }
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // The mouse's Back and Forward buttons are history input, not a click outside the palette, and
        // history stays put while the palette is open, so they leave it open.
        var button = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (DataContext is SearchOverlayViewModel vm
            && button is not (PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed))
        {
            vm.Close();
        }

        e.Handled = true;
    }

    private static void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
        => e.Handled = true;

    private void OnClearSearchClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SearchOverlayViewModel vm)
            vm.ClearSearch();

        _searchInput?.Focus();
        e.Handled = true;
    }

    private void OnFilterChipClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SearchCategoryFilterItem filter } ||
            DataContext is not SearchOverlayViewModel vm)
        {
            return;
        }

        vm.SelectCategory(filter.CategoryKey);
        _searchInput?.Focus();
        e.Handled = true;
    }

    private void OnShowAllClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SearchResultGroup group } ||
            DataContext is not SearchOverlayViewModel vm)
        {
            return;
        }

        vm.SelectCategory(group.CategoryKey);
        _searchInput?.Focus();
        e.Handled = true;
    }

    private void OnResultClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchResultItem item } &&
            DataContext is SearchOverlayViewModel vm)
        {
            vm.Close();
            vm.RaiseResultSelected(item);
        }
    }

    /// <summary>
    /// The glass drops in from just above its place and the Strata line draws itself under the field, both
    /// on the render thread.
    /// </summary>
    private void AnimateOpen()
    {
        var animate = AreAnimationsEnabled;

        if (_cardHost is not null)
        {
            _cardHost.Opacity = 1;
            if (animate)
                DropIn(_cardHost);
        }

        if (_stratumLine is not null && animate)
            DrawLine(_stratumLine);
    }

    private static void FadeIn(Visual target, TimeSpan duration)
    {
        var visual = ElementComposition.GetElementVisual(target);
        if (visual?.Compositor is not { } compositor)
            return;

        var fade = compositor.CreateStableScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, SettleEasing);
        fade.Duration = duration;
        visual.StartAnimation("Opacity", fade);
    }

    private static void DropIn(Control card)
    {
        var visual = ElementComposition.GetElementVisual(card);
        if (visual?.Compositor is not { } compositor)
            return;

        var width = (float)card.Bounds.Width;
        if (width <= 0)
            width = 640;

        visual.CenterPoint = new System.Numerics.Vector3(width / 2f, 0f, 0f);

        var duration = TimeSpan.FromMilliseconds(280);
        var scale = compositor.CreateStableVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new System.Numerics.Vector3(0.97f, 0.97f, 1f));
        scale.InsertKeyFrame(1f, System.Numerics.Vector3.One, SettleEasing);
        scale.Duration = duration;
        visual.StartAnimation("Scale", scale);

        var rise = compositor.CreateStableVector3DKeyFrameAnimation();
        rise.InsertKeyFrame(0f, new Vector3D(0, -10, 0));
        rise.InsertKeyFrame(1f, default, SettleEasing);
        rise.Duration = duration;
        visual.StartAnimation("Translation", rise);

        FadeIn(card, TimeSpan.FromMilliseconds(170));
    }

    private static void DrawLine(Control line)
    {
        var visual = ElementComposition.GetElementVisual(line);
        if (visual?.Compositor is not { } compositor)
            return;

        visual.CenterPoint = new System.Numerics.Vector3(0f, (float)line.Bounds.Height / 2f, 0f);

        var draw = compositor.CreateStableVector3KeyFrameAnimation();
        draw.InsertKeyFrame(0f, new System.Numerics.Vector3(0f, 1f, 1f));
        draw.InsertKeyFrame(1f, System.Numerics.Vector3.One, SettleEasing);
        draw.Duration = TimeSpan.FromMilliseconds(560);
        draw.DelayTime = TimeSpan.FromMilliseconds(90);
        draw.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Scale", draw);
    }
}

/// <summary>
/// The search category a palette element stands for, so the palette's styles can give it that category's hue.
/// </summary>
public sealed class SearchHue
{
    public static readonly AttachedProperty<GlobalSearchCategory?> CategoryProperty =
        AvaloniaProperty.RegisterAttached<SearchHue, Control, GlobalSearchCategory?>("Category");

    private SearchHue()
    {
    }

    public static GlobalSearchCategory? GetCategory(Control control) => control.GetValue(CategoryProperty);

    public static void SetCategory(Control control, GlobalSearchCategory? value) => control.SetValue(CategoryProperty, value);
}
