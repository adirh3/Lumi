using System;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.ViewModels;

namespace Lumi.Views;

public partial class SearchOverlay : UserControl
{
    private Border? _scrim;
    private Border? _searchCard;
    private TextBox? _searchInput;
    private ItemsControl? _resultsList;
    private ScrollViewer? _resultsScroller;
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
        _scrim = this.FindControl<Border>("Scrim");
        _searchCard = this.FindControl<Border>("OverlayCard");
        _searchInput = this.FindControl<TextBox>("SearchInput");
        _resultsList = this.FindControl<ItemsControl>("ResultsList");
        _resultsScroller = this.FindControl<ScrollViewer>("ResultsScroll");
        _emptyState = this.FindControl<Control>("EmptyState");
        _searchingState = this.FindControl<Control>("SearchingState");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != IsVisibleProperty || !change.GetNewValue<bool>())
            return;

        var now = Environment.TickCount64;
        if (now - _lastAnimateOpenTick < 300)
            return;

        _lastAnimateOpenTick = now;

        if (_searchCard is not null)
            _searchCard.Opacity = 0;
        if (_scrim is not null)
            _scrim.Opacity = 0;

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

            case nameof(SearchOverlayViewModel.SearchQuery):
            case nameof(SearchOverlayViewModel.SelectedCategory):
                PostDeferredViewUpdate(resetScroll: true);
                break;

            case nameof(SearchOverlayViewModel.IsSearching):
            case nameof(SearchOverlayViewModel.IsDeepSearching):
                UpdateEmptyState();
                break;

            case nameof(SearchOverlayViewModel.IsOpen)
                when sender is SearchOverlayViewModel { IsOpen: false }:
                _lastRenderedSelection = -1;
                _resetScrollOnNextUpdate = false;
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

        if (_searchingState is not null)
            _searchingState.IsVisible = vm.IsSearchPending && hasQuery && noResults;
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
        if (DataContext is SearchOverlayViewModel vm)
            vm.Close();

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

    private void OnBackToAllClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SearchOverlayViewModel vm)
            vm.SelectCategory(null);

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

    private void AnimateOpen()
    {
        if (_scrim is not null)
        {
            _scrim.Opacity = 1;
            var scrimVisual = ElementComposition.GetElementVisual(_scrim);
            if (scrimVisual?.Compositor is { } scrimCompositor)
            {
                var fade = scrimCompositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0f, 0f);
                fade.InsertKeyFrame(1f, 1f);
                fade.Duration = TimeSpan.FromMilliseconds(140);
                scrimVisual.StartAnimation("Opacity", fade);
            }
        }

        if (_searchCard is null)
            return;

        _searchCard.Opacity = 1;
        var visual = ElementComposition.GetElementVisual(_searchCard);
        if (visual?.Compositor is not { } compositor)
            return;

        var width = (float)_searchCard.Bounds.Width;
        var height = (float)_searchCard.Bounds.Height;
        if (width <= 0)
            width = 820;
        if (height <= 0)
            height = 600;

        visual.CenterPoint = new System.Numerics.Vector3(width / 2f, 0f, 0f);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new System.Numerics.Vector3(0.985f, 0.985f, 1f));
        scale.InsertKeyFrame(1f, new System.Numerics.Vector3(1f, 1f, 1f));
        scale.Duration = TimeSpan.FromMilliseconds(160);
        visual.StartAnimation("Scale", scale);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, 1f);
        opacity.Duration = TimeSpan.FromMilliseconds(140);
        visual.StartAnimation("Opacity", opacity);
    }
}
