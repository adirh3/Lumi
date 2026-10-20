using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.ViewModels;

namespace Lumi.Views;

/// <summary>
/// The Workspace's Agents page: the index of every agent in the chat, or one agent's run rendered
/// like a normal chat. Opening a run starts where the reader wants to be — the newest work for a live
/// agent, the headline and task for a finished one — and a live run keeps following its newest step
/// while the reader stays at the bottom.
/// </summary>
public partial class SubagentRunView : UserControl
{
    /// <summary>How far from the bottom still counts as "following" a live run.</summary>
    private const double BottomFollowThreshold = 48;

    private ChatViewModel? _observedViewModel;
    private string? _observedRunId;
    private ScrollViewer? _runScroller;

    public SubagentRunView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _observedRunId = null; // another chat's runs start fresh
        Rebind();
    }

    // The Workspace re-hosts the same page each time the Agents page opens; coming back to the
    // same run keeps the reader's place.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
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
        OnSelectedRunChanged();
    }

    private void Unbind()
    {
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _observedViewModel = null;
        }

        AttachRunScroller(null);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChatViewModel.SelectedSubagentRun):
                OnSelectedRunChanged();
                break;
            case nameof(ChatViewModel.SubagentIndexRows):
                // A wholesale new list is another chat's (or a rebuilt) index: start at its top.
                this.FindControl<ScrollViewer>("RunIndexScroller")?.ScrollToHome();
                break;
        }
    }

    private void OnSelectedRunChanged()
    {
        var run = _observedViewModel?.SelectedSubagentRun;
        // A transcript rebuild swaps in a fresh instance of the SAME run; only a genuinely
        // different run moves the reader.
        var wasShowingRun = _observedRunId is not null;
        var isDifferentRun = !string.Equals(_observedRunId, run?.StableId, StringComparison.Ordinal);
        _observedRunId = run?.StableId;

        if (run is null)
            return;

        var reposition = isDifferentRun;
        // Paging from one run to the next keeps the page on screen, so the new run fades in rather
        // than snapping; arriving from the index already slides the whole page in.
        var crossfade = isDifferentRun && wasShowingRun;
        var followTail = run.IsInProgress;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_observedViewModel?.SelectedSubagentRun, run))
                return;

            // Re-attached on every showing: the page is re-hosted each time the Agents page opens.
            AttachRunScroller(FindRunScroller());
            if (!reposition || _runScroller is null)
                return;

            if (followTail)
                _runScroller.ScrollToEnd();
            else
                _runScroller.ScrollToHome();

            if (crossfade)
                _ = (_runCrossfade ??= CreateCrossfade()).RunAsync(_runScroller);
        }, DispatcherPriority.Background);
    }

    // Per view rather than static: Avalonia animations belong to the dispatcher that created them.
    private Animation? _runCrossfade;

    private static Animation CreateCrossfade() => new()
    {
        Duration = TimeSpan.FromMilliseconds(160),
        Easing = new CubicEaseOut(),
        FillMode = FillMode.Forward,
        Children =
        {
            new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(OpacityProperty, 0d) } },
            new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(OpacityProperty, 1d) } },
        },
    };

    /// <summary>
    /// The run page lives inside a data template (so nothing binds against a null selection), so its
    /// scroller is resolved from the visual tree rather than cached at load time.
    /// </summary>
    private ScrollViewer? FindRunScroller()
        => this.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault(static viewer => viewer.Name == "RunTranscriptScroller");

    private void AttachRunScroller(ScrollViewer? scroller)
    {
        if (ReferenceEquals(_runScroller, scroller))
            return;

        if (_runScroller is not null)
            _runScroller.ScrollChanged -= OnRunScrollChanged;
        _runScroller = scroller;
        if (_runScroller is not null)
            _runScroller.ScrollChanged += OnRunScrollChanged;
    }

    /// <summary>
    /// Keeps a live run pinned to its newest step, but only while the reader was already at the
    /// bottom when it grew: a busy agent extends the page constantly, and pulling the view down on
    /// every step would make anything above impossible to read.
    /// </summary>
    private void OnRunScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroller
            || e.ExtentDelta.Y <= 0
            || _observedViewModel?.SelectedSubagentRun?.IsInProgress != true)
        {
            return;
        }

        var previousBottom = scroller.Offset.Y - e.OffsetDelta.Y + scroller.Viewport.Height - e.ViewportDelta.Y;
        var previousExtent = scroller.Extent.Height - e.ExtentDelta.Y;
        if (previousBottom >= previousExtent - BottomFollowThreshold)
            scroller.ScrollToEnd();
    }
}
