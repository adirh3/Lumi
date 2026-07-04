using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.ViewModels;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class TranscriptPagingHeadlessTests
{
    private sealed class VisualTranscriptItem : TranscriptItem
    {
        public VisualTranscriptItem(string stableId, double desiredHeight, string text)
            : base(stableId)
        {
            DesiredHeight = desiredHeight;
            Text = text;
        }

        public double DesiredHeight { get; }
        public string Text { get; }
    }

    private readonly record struct Anchor(string StableId, double ViewportY);

    [Fact]
    public async Task OpeningLongTranscript_RealizesOnlyMountedTurns()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            TranscriptTurnControl.ResetDiagnostics();
            var controller = new TranscriptWindowController(new TranscriptPagingOptions
            {
                MaxPageWeight = 4,
                MaxTurnsPerPage = 2,
                MinInitialPages = 2,
                MaxMountedPages = 4,
            });
            var source = CreateVisualTurns(80);
            controller.BindTranscript(source, "ui-open");
            controller.ResetToLatest(260, "ui-open");

            var (window, shell, _) = await CreateHostAsync(controller);
            try
            {
                shell.ResetAutoScroll();
                shell.ScrollToEnd();
                await PumpAsync();
                _ = window.CaptureRenderedFrame();

                var realizedTurnCount = window.GetVisualDescendants().OfType<TranscriptTurnControl>().Count();
                var diagnostics = TranscriptTurnControl.CaptureDiagnostics();

                Assert.True(realizedTurnCount > 0);
                Assert.True(realizedTurnCount < source.Count);
                Assert.Equal(controller.MountedTurns.Count, realizedTurnCount);
                Assert.True(diagnostics.ControlCreateCount < source.Count);
                Assert.True(shell.VerticalOffset > 0);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PrependingOlderPage_RestoresVisibleAnchor()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var controller = new TranscriptWindowController(new TranscriptPagingOptions
            {
                MaxPageWeight = 4,
                MaxTurnsPerPage = 2,
                MinInitialPages = 2,
                MaxMountedPages = 5,
                PrependTriggerPixels = 120,
            });
            var source = CreateVisualTurns(24);
            controller.BindTranscript(source, "ui-prepend");
            controller.ResetToLatest(220, "ui-prepend");

            var (window, shell, scrollViewer) = await CreateHostAsync(controller);
            try
            {
                shell.ResetAutoScroll();
                shell.ScrollToEnd();
                await PumpAsync();

                shell.ScrollToVerticalOffset(0);
                await PumpAsync();

                controller.UpdatePinnedState(false, shell.CurrentDistanceFromBottom, "ui-prepend");
                var anchor = CaptureAnchor(window, scrollViewer);
                Assert.NotNull(anchor);

                var mutation = controller.UpdateViewport(
                    new TranscriptViewportState(
                        shell.VerticalOffset,
                        shell.ViewportHeight,
                        shell.ExtentHeight,
                        shell.IsPinnedToBottom,
                        shell.CurrentDistanceFromBottom),
                    "ui-prepend");

                Assert.Equal(TranscriptWindowMutationKind.Prepend, mutation.Kind);
                await PumpAsync();
                RestoreAnchor(window, shell, scrollViewer, anchor!.Value);
                await PumpAsync();

                var restored = CaptureAnchor(window, scrollViewer);
                Assert.NotNull(restored);
                Assert.Equal(anchor.Value.StableId, restored!.Value.StableId);
                Assert.InRange(Math.Abs(restored.Value.ViewportY - anchor.Value.ViewportY), 0, 1.5);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DetachedTurnControl_DoesNotProcessChangesUntilReattached()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            TranscriptTurnControl.ResetDiagnostics();
            var turn = new TranscriptTurn("turn:0000");
            turn.Items.Add(new VisualTranscriptItem("item:0000", 72, "One"));

            var control = new TranscriptTurnControl { Turn = turn };
            var host = new StackPanel();
            host.Children.Add(control);

            var window = new Window
            {
                Width = 480,
                Height = 320,
                Content = host,
            };
            window.DataTemplates.Add(new FuncDataTemplate<VisualTranscriptItem>((item, _) => new Border
            {
                Height = item.DesiredHeight,
                Child = new TextBlock { Text = item.Text },
            }));

            window.Show();
            await PumpAsync();
            Assert.Equal(1, GetHostedItemCount(control));

            host.Children.Clear();
            await PumpAsync();
            Assert.Equal(0, GetHostedItemCount(control));

            turn.Items.Add(new VisualTranscriptItem("item:0001", 72, "Two"));
            Assert.Equal(0, GetHostedItemCount(control));

            host.Children.Add(control);
            await PumpAsync();
            Assert.Equal(2, GetHostedItemCount(control));

            turn.Items.Add(new VisualTranscriptItem("item:0002", 72, "Three"));
            Assert.Equal(3, GetHostedItemCount(control));

            window.Close();
            await PumpAsync();
            Assert.Equal(0, GetHostedItemCount(control));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TailTrackingAddThatTrimsHead_PreservesSurvivingTurnHosts()
    {
        // Reproduces the finish-writing freeze deterministically. While pinned to a full mounted
        // window, a source change that advances the tail AND trims the head at the same time (the
        // typing turn being removed / the final turn being added when the assistant stops writing)
        // breaks the prefix/suffix reconcile at BOTH ends. The old diff then collapsed to "replace
        // the whole mounted range", releasing every surviving turn's realized host — so each was
        // re-realized, re-parsing its markdown and re-highlighting its code on the UI thread. The
        // identity-based reconcile must keep surviving turns' hosts and release only the turn that
        // truly left the window.
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            var controller = new TranscriptWindowController(new TranscriptPagingOptions
            {
                MaxPageWeight = 1000,
                MaxTurnsPerPage = 1,
                MinInitialPages = 1,
                MaxMountedPages = 3,
            });
            var source = CreateVisualTurns(6);
            controller.BindTranscript(source, "tail-trim");
            controller.ResetToLatest(2000, "tail-trim");
            controller.UpdatePinnedState(true, 0, "tail-trim");

            // Full mounted window tracking the tail: [t3, t4, t5].
            Assert.Equal(
                new[] { "turn:0003", "turn:0004", "turn:0005" },
                controller.MountedTurns.Select(static turn => turn.StableId).ToArray());

            // Stand in for "these mounted turns have already realized (parsed) their content".
            var hostT3 = new StackPanel();
            var hostT4 = new StackPanel();
            var hostT5 = new StackPanel();
            source[3].RealizedItemsHost = hostT3;
            source[4].RealizedItemsHost = hostT4;
            source[5].RealizedItemsHost = hostT5;

            // Appending a turn while pinned to the full window advances the tail and trims the head
            // in a single source change — the dual-end break. The reconcile runs synchronously here.
            var newTurn = new TranscriptTurn("turn:0006");
            newTurn.Items.Add(new VisualTranscriptItem("item:0006", 120, "Turn 6"));
            source.Add(newTurn);

            // Head trimmed, tail advanced: [t4, t5, t6].
            Assert.Equal(
                new[] { "turn:0004", "turn:0005", "turn:0006" },
                controller.MountedTurns.Select(static turn => turn.StableId).ToArray());

            // Surviving turns keep the exact same realized host (no tear-down, no re-parse). Under
            // the old prefix/suffix diff these were released to null.
            Assert.Same(hostT4, source[4].RealizedItemsHost);
            Assert.Same(hostT5, source[5].RealizedItemsHost);

            // The turn that genuinely left the mounted window is still released.
            Assert.Null(source[3].RealizedItemsHost);

            // Survivors are the very same turn objects (reference identity), in order.
            Assert.Same(source[4], controller.MountedTurns[0]);
            Assert.Same(source[5], controller.MountedTurns[1]);
            Assert.Same(source[6], controller.MountedTurns[2]);

            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReorderingMountedSourceTurns_DoesNotDuplicateOrReleaseHosts()
    {
        // Defensive: the reconcile must stay correct even if the source turns are reordered (a Move),
        // which the insertion-only path would otherwise turn into a duplicate insert. No production
        // path reorders turns today, but the reconcile must not depend on callers preserving order.
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            var controller = new TranscriptWindowController(new TranscriptPagingOptions
            {
                MaxPageWeight = 1000,
                MaxTurnsPerPage = 1,
                MinInitialPages = 1,
                MaxMountedPages = 3,
            });
            var source = CreateVisualTurns(6);
            controller.BindTranscript(source, "reorder");
            controller.ResetToLatest(2000, "reorder");
            controller.UpdatePinnedState(true, 0, "reorder");

            var turnT3 = source[3];
            var turnT4 = source[4];
            var turnT5 = source[5];
            Assert.Equal(
                new[] { turnT3, turnT4, turnT5 },
                controller.MountedTurns.ToArray());

            var hostT3 = new StackPanel();
            var hostT4 = new StackPanel();
            var hostT5 = new StackPanel();
            turnT3.RealizedItemsHost = hostT3;
            turnT4.RealizedItemsHost = hostT4;
            turnT5.RealizedItemsHost = hostT5;

            // Swap two mounted turns in the source. Desired mounted order becomes [t4, t3, t5].
            source.Move(3, 4);

            // Reconciled to the new order with NO duplication.
            Assert.Equal(
                new[] { turnT4, turnT3, turnT5 },
                controller.MountedTurns.ToArray());
            Assert.Equal(3, controller.MountedTurns.Count);
            Assert.Equal(3, controller.MountedTurns.Distinct().Count());

            // Every turn stayed mounted, so every host is preserved (none released, none rebuilt).
            Assert.Same(hostT3, turnT3.RealizedItemsHost);
            Assert.Same(hostT4, turnT4.RealizedItemsHost);
            Assert.Same(hostT5, turnT5.RealizedItemsHost);

            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReleaseRealizedHosts_ShedsEveryMountedHost_ButKeepsMountStructure()
    {
        // The idle-surface memory fix. When a chat surface is cached but no longer visible, the
        // session store sheds its realized transcript controls so a deep pool of idle chats doesn't
        // retain hundreds of live Avalonia controls each. ReleaseRealizedHosts must drop every mounted
        // turn's built host — so the heavy StrataChatMessage/markdown subtrees become collectible —
        // WITHOUT tearing down the paging/mount structure, so switching back re-realizes from the very
        // same turns (through the normal frame-budgeted path) instead of showing a blank transcript.
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            var controller = new TranscriptWindowController(new TranscriptPagingOptions
            {
                MaxPageWeight = 1000,
                MaxTurnsPerPage = 1,
                MinInitialPages = 1,
                MaxMountedPages = 3,
            });
            var source = CreateVisualTurns(6);
            controller.BindTranscript(source, "shed");
            controller.ResetToLatest(2000, "shed");
            controller.UpdatePinnedState(true, 0, "shed");

            // Full mounted window tracking the tail: [t3, t4, t5]. Stand in for "these mounted turns
            // have already realized (parsed and built) their content".
            var mountedBefore = controller.MountedTurns.ToArray();
            Assert.Equal(new[] { "turn:0003", "turn:0004", "turn:0005" }, mountedBefore.Select(static t => t.StableId).ToArray());
            foreach (var turn in mountedBefore)
                turn.RealizedItemsHost = new StackPanel();
            Assert.All(mountedBefore, turn => Assert.NotNull(turn.RealizedItemsHost));

            controller.ReleaseRealizedHosts("surface-idle");

            // Every realized host is dropped (across the whole source) so the controls can be collected.
            Assert.All(source, turn => Assert.Null(turn.RealizedItemsHost));

            // The mount structure is untouched: the same turns stay mounted, in order, ready to
            // re-realize from their items when the surface is shown again — no blank transcript.
            Assert.Equal(
                mountedBefore.Select(static t => t.StableId).ToArray(),
                controller.MountedTurns.Select(static t => t.StableId).ToArray());

            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    private static async Task<(Window Window, StrataChatShell Shell, ScrollViewer ScrollViewer)> CreateHostAsync(TranscriptWindowController controller)
    {
        var transcript = new ItemsControl
        {
            ItemsSource = controller.MountedTurns,
            ItemTemplate = new FuncDataTemplate<TranscriptTurn>((turn, _) => new TranscriptTurnControl { Turn = turn }),
        };
        transcript.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 8 });

        var shell = new StrataChatShell
        {
            Header = new TextBlock { Text = "Transcript Test" },
            Transcript = transcript,
            Composer = new Border { Height = 48 },
        };

        var window = new Window
        {
            Width = 900,
            Height = 700,
            Content = shell,
        };
        window.DataTemplates.Add(new FuncDataTemplate<VisualTranscriptItem>((item, _) => new Border
        {
            Height = item.DesiredHeight,
            Padding = new Thickness(12, 8),
            Child = new TextBlock { Text = item.Text },
        }));

        window.Show();
        await PumpAsync();
        await PumpAsync();

        var scrollViewer = shell.TranscriptScrollViewer;
        Assert.NotNull(scrollViewer);
        return (window, shell, scrollViewer!);
    }

    private static ObservableCollection<TranscriptTurn> CreateVisualTurns(int count)
    {
        return new ObservableCollection<TranscriptTurn>(
            Enumerable.Range(0, count)
                .Select(index =>
                {
                    var turn = new TranscriptTurn($"turn:{index:D4}");
                    var height = 72 + (index % 5) * 28;
                    turn.Items.Add(new VisualTranscriptItem($"item:{index:D4}", height, $"Turn {index}"));
                    return turn;
                })
                .ToArray());
    }

    private static Anchor? CaptureAnchor(Visual root, ScrollViewer scrollViewer)
    {
        foreach (var control in root.GetVisualDescendants().OfType<TranscriptTurnControl>())
        {
            if (control.Turn is null)
                continue;

            var point = control.TranslatePoint(default, scrollViewer);
            if (point is null)
                continue;

            if (point.Value.Y + control.Bounds.Height < 0)
                continue;

            return new Anchor(control.Turn.StableId, point.Value.Y);
        }

        return null;
    }

    private static void RestoreAnchor(Visual root, StrataChatShell shell, ScrollViewer scrollViewer, Anchor anchor)
    {
        var control = root.GetVisualDescendants()
            .OfType<TranscriptTurnControl>()
            .FirstOrDefault(candidate => candidate.Turn?.StableId == anchor.StableId);
        Assert.NotNull(control);

        var point = control!.TranslatePoint(default, scrollViewer);
        Assert.NotNull(point);

        var delta = point!.Value.Y - anchor.ViewportY;
        shell.ScrollToVerticalOffset(shell.VerticalOffset + delta);
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
    }

    [Fact]
    public async Task HeightChangeAboveViewport_CompensatedByScrollOffset()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var controller = new TranscriptWindowController(new TranscriptPagingOptions
            {
                MaxPageWeight = 4,
                MaxTurnsPerPage = 2,
                MinInitialPages = 3,
                MaxMountedPages = 6,
            });
            var source = CreateVisualTurns(20);
            controller.BindTranscript(source, "ui-anchor");
            controller.ResetToLatest(400, "ui-anchor");

            var (window, shell, scrollViewer) = await CreateHostAsync(controller);
            try
            {
                // Scroll to a middle position — not at the bottom.
                shell.ResetAutoScroll();
                shell.ScrollToEnd();
                await PumpAsync();

                var midOffset = shell.ExtentHeight / 2;
                shell.ScrollToVerticalOffset(midOffset);
                await PumpAsync();

                // Capture the first visible turn as our anchor.
                var anchorBefore = CaptureAnchor(window, scrollViewer);
                Assert.NotNull(anchorBefore);

                // Find a turn whose visual control is fully above the viewport.
                TranscriptTurnControl? aboveTurn = null;
                foreach (var ttc in window.GetVisualDescendants().OfType<TranscriptTurnControl>())
                {
                    if (ttc.Turn is null) continue;
                    var pt = ttc.TranslatePoint(default, scrollViewer);
                    if (pt is null) continue;
                    // Fully above viewport
                    if (pt.Value.Y + ttc.Bounds.Height < 0)
                    {
                        aboveTurn = ttc;
                        break;
                    }
                }

                Assert.NotNull(aboveTurn);
                var turnModel = aboveTurn!.Turn!;
                var heightBefore = turnModel.MeasuredHeight;

                // Add a new item to the turn — this will increase its rendered height.
                turnModel.Items.Add(new VisualTranscriptItem(
                    $"extra:{turnModel.StableId}", 120, "Extra content"));
                await PumpAsync();

                var heightAfter = turnModel.MeasuredHeight;
                var heightDelta = heightAfter - heightBefore;
                Assert.True(heightDelta > 1, "Turn height should have increased.");

                // Without compensation the anchor drifts by roughly heightDelta.
                var anchorDrifted = CaptureAnchor(window, scrollViewer);
                Assert.NotNull(anchorDrifted);
                Assert.Equal(anchorBefore!.Value.StableId, anchorDrifted!.Value.StableId);

                var drift = anchorDrifted.Value.ViewportY - anchorBefore.Value.ViewportY;
                Assert.True(Math.Abs(drift) > 1,
                    $"Expected viewport drift from height change, but drift was {drift:F1}px");

                // Apply height compensation (same logic as ChatView.OnMountedTurnPropertyChanged).
                shell.ScrollToVerticalOffset(shell.VerticalOffset + heightDelta);
                await PumpAsync();

                var anchorAfterCompensation = CaptureAnchor(window, scrollViewer);
                Assert.NotNull(anchorAfterCompensation);
                Assert.Equal(anchorBefore.Value.StableId, anchorAfterCompensation!.Value.StableId);
                Assert.InRange(
                    Math.Abs(anchorAfterCompensation.Value.ViewportY - anchorBefore.Value.ViewportY),
                    0, 2.0);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static int GetHostedItemCount(TranscriptTurnControl control)
    {
        return Assert.IsType<StackPanel>(control.Content).Children.Count;
    }
}
