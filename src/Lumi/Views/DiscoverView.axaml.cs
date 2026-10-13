using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Lumi.ViewModels;
using Lumi.Views.Management;

namespace Lumi.Views;

/// <summary>Customize → Discover: plugins and MCP servers to add to Lumi.</summary>
public partial class DiscoverView : UserControl
{
    /// <summary>How close to the end of the gallery the next page of cards is shown, in pixels.</summary>
    private const double RevealDistance = 600;

    private readonly ScrollViewer _galleryScroll;
    private DiscoverViewModel? _viewModel;
    private bool _isRevealCheckPending;

    public DiscoverView()
    {
        AvaloniaXamlLoader.Load(this);
        ManagementPage.ResetScrollOn(this, "DiscoverDetailScroll", nameof(DiscoverViewModel.OpenCard));
        ManagementPage.ResetScrollOn(this, "DiscoverScroll", nameof(DiscoverViewModel.Filter));
        ManagementPage.ResetScrollOn(this, "DiscoverScroll", nameof(DiscoverViewModel.SourceKey));

        _galleryScroll = this.FindControl<ScrollViewer>("DiscoverScroll")!;
        // Only scrolling or resizing asks here. Content growth asks through OnCardsChanged, so the
        // progress bar or Load more button appearing cannot start another fetch on its own.
        _galleryScroll.ScrollChanged += (_, e) =>
        {
            if (e.OffsetDelta.Y != 0 || e.ViewportDelta.Y != 0)
                RevealMoreIfNearEnd();
        };
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
                _viewModel.Cards.CollectionChanged -= OnCardsChanged;
            _viewModel = DataContext as DiscoverViewModel;
            if (_viewModel is not null)
                _viewModel.Cards.CollectionChanged += OnCardsChanged;
        };
    }

    private void OnCardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A batch that fits in the last row raises no scroll event; check again once it has laid out.
        // Only added cards ask: a fetch that adds none must not start another one.
        if (e.Action != NotifyCollectionChangedAction.Add || _isRevealCheckPending)
            return;

        _isRevealCheckPending = true;
        DispatcherTimer.RunOnce(() =>
        {
            _isRevealCheckPending = false;
            RevealMoreIfNearEnd();
        }, TimeSpan.FromMilliseconds(30), DispatcherPriority.Normal);
    }

    private void RevealMoreIfNearEnd()
    {
        // A hidden page keeps its last scroll position, which would keep asking for more off-screen.
        if (_viewModel is not { IsGrid: true, ShowGallery: true } vm || !_galleryScroll.IsEffectivelyVisible)
            return;

        if (_galleryScroll.Offset.Y + _galleryScroll.Viewport.Height >= _galleryScroll.Extent.Height - RevealDistance)
            vm.ShowMoreCards();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && DataContext is DiscoverViewModel vm)
        {
            if (vm.IsConfirmingRemove)
            {
                vm.CancelRemoveCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (vm.IsViewingDetail)
            {
                vm.CloseDetailCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (vm.IsSearching)
            {
                vm.ClearSearchCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);
    }
}
