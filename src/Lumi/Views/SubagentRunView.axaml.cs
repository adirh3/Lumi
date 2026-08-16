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
    private INotifyCollectionChanged? _observedTimeline;
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

        if (_observedTimeline is not null)
        {
            _observedTimeline.CollectionChanged -= OnTimelineChanged;
            _observedTimeline = null;
        }
        _observedRunId = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChatViewModel.SelectedSubagentRun))
            return;

        ObserveSelectedRun();
    }

    private void ObserveSelectedRun()
    {
        if (_observedTimeline is not null)
        {
            _observedTimeline.CollectionChanged -= OnTimelineChanged;
            _observedTimeline = null;
        }

        var run = _observedViewModel?.SelectedSubagentRun;
        // A transcript rebuild swaps in a fresh instance of the SAME run; only a genuinely
        // different run starts the reader back at the top.
        var isDifferentRun = !string.Equals(_observedRunId, run?.StableId, StringComparison.Ordinal);
        _observedRunId = run?.StableId;

        if (run is null)
            return;

        _observedTimeline = run.Timeline;
        _observedTimeline.CollectionChanged += OnTimelineChanged;

        if (isDifferentRun)
            ResetScroll();
    }

    /// <summary>Keeps a running agent's transcript pinned to its newest step.</summary>
    private void OnTimelineChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_observedViewModel?.SelectedSubagentRun?.IsInProgress != true)
            return;

        Dispatcher.UIThread.Post(() => FindScroller()?.ScrollToEnd(), DispatcherPriority.Background);
    }

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
