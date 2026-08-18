using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.ViewModels;

namespace Lumi.Views;

/// <summary>
/// Read-only transcript of a delegated sub-agent's run, hosted in the right-hand split-view island.
/// Shows either the index of every agent in the chat or one agent's full run rendered like a normal
/// chat, and follows the tail while that agent is still working.
/// </summary>
public partial class SubagentRunView : UserControl
{
    private INotifyCollectionChanged? _observedTurns;
    private ChatViewModel? _observedViewModel;
    private string? _observedRunId;

    public SubagentRunView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Rebind();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unbind();
        base.OnDetachedFromVisualTree(e);
    }

    private void Rebind()
    {
        Unbind();

        if (DataContext is not ChatViewModel viewModel)
            return;

        _observedViewModel = viewModel;
        _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ObserveSelectedRun();
    }

    private void Unbind()
    {
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _observedViewModel = null;
        }

        UnobserveTurns();
        _observedRunId = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatViewModel.SelectedSubagentRun)
            or nameof(ChatViewModel.SubagentRunTurns))
        {
            ObserveSelectedRun();
        }
    }

    private void ObserveSelectedRun()
    {
        UnobserveTurns();

        var run = _observedViewModel?.SelectedSubagentRun;
        // A transcript rebuild swaps in a fresh instance of the SAME run; only a genuinely
        // different run starts the reader back at the top.
        var isDifferentRun = !string.Equals(_observedRunId, run?.StableId, StringComparison.Ordinal);
        _observedRunId = run?.StableId;

        if (run is null || _observedViewModel is null)
            return;

        _observedTurns = _observedViewModel.SubagentRunTurns;
        _observedTurns.CollectionChanged += OnTurnsChanged;

        if (isDifferentRun)
            ResetScroll();
        else
            FollowTail();
    }

    private void UnobserveTurns()
    {
        if (_observedTurns is null)
            return;

        _observedTurns.CollectionChanged -= OnTurnsChanged;
        _observedTurns = null;
    }

    /// <summary>Keeps a running agent's transcript pinned to its newest step.</summary>
    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e) => FollowTail();

    /// <summary>
    /// Follows a live run's tail, but only while the reader is already at the bottom. A running
    /// agent rebuilds this transcript continuously, and yanking the view back down on every rebuild
    /// would make a busy agent impossible to read.
    /// </summary>
    private void FollowTail()
    {
        if (_observedViewModel?.SelectedSubagentRun?.IsInProgress != true)
            return;

        var scroller = FindScroller();
        if (scroller is not null && !IsAtBottom(scroller))
            return;

        Dispatcher.UIThread.Post(() => FindScroller()?.ScrollToEnd(), DispatcherPriority.Background);
    }

    private static bool IsAtBottom(ScrollViewer scroller)
        => scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - BottomFollowThreshold;

    /// <summary>How far from the bottom still counts as "following" the run.</summary>
    private const double BottomFollowThreshold = 48;

    /// <summary>Resets the view to the top when a different run is opened.</summary>
    private void ResetScroll()
        => Dispatcher.UIThread.Post(() => FindScroller()?.ScrollToHome(), DispatcherPriority.Background);

    /// <summary>
    /// The run transcript lives inside a data template (so nothing binds against a null selection),
    /// so its scroller is resolved from the visual tree rather than cached at load time.
    /// </summary>
    private ScrollViewer? FindScroller()
        => this.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault(static viewer => viewer.Name == "RunTranscriptScroller");
}
