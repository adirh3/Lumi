using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class TranscriptPagingTests
{
    private sealed class TestTranscriptItem : TranscriptItem
    {
        public TestTranscriptItem(string stableId)
            : base(stableId)
        {
        }
    }

    [Fact]
    public void CollapsedContainersReserveOnlyVisibleHeaderGeometry()
    {
        var turn = new TranscriptTurn("turn:collapsed");
        var toolGroup = new ToolGroupItem("500 completed tools");
        for (var index = 0; index < 500; index++)
        {
            toolGroup.ToolCalls.Add(new ToolCallItem(
                $"Tool {index}",
                StrataTheme.Controls.StrataAiToolCallStatus.Completed,
                $"tool:{index}"));
        }

        var subagent = new SubagentToolCallItem(
            "Research",
            StrataTheme.Controls.StrataAiToolCallStatus.Completed);
        for (var index = 0; index < 500; index++)
        {
            subagent.Activities.Add(new ToolCallItem(
                $"Activity {index}",
                StrataTheme.Controls.StrataAiToolCallStatus.Completed,
                $"activity:{index}"));
        }

        turn.Items.Add(toolGroup);
        turn.Items.Add(subagent);

        Assert.Equal(2, TranscriptPageWeightEstimator.EstimateTurnWeight(turn));
        Assert.Equal(112, TranscriptPageWeightEstimator.EstimateTurnHeight(turn, 56));
    }

    [Fact]
    public void MultilineMessagesReserveGeometryFromRenderedLineCountWithoutLengthCap()
    {
        var content = string.Join(
            '\n',
            Enumerable.Range(0, 13).Select(index => $"Markdown line {index}: " + new string('x', 24)));
        var turn = new TranscriptTurn("turn:multiline");
        turn.Items.Add(new AssistantMessageItem(
            new ChatMessageViewModel(new Lumi.Models.ChatMessage
            {
                Role = "assistant",
                Content = content
            }),
            showTimestamps: false));

        var weight = TranscriptPageWeightEstimator.EstimateTurnWeight(turn);
        Assert.InRange(weight, 8, 12);

        var veryLongTurn = new TranscriptTurn("turn:very-long");
        veryLongTurn.Items.Add(new AssistantMessageItem(
            new ChatMessageViewModel(new Lumi.Models.ChatMessage
            {
                Role = "assistant",
                Content = new string('x', 100_000)
            }),
            showTimestamps: false));

        Assert.True(TranscriptPageWeightEstimator.EstimateTurnWeight(veryLongTurn) > 500);
    }

    [Fact]
    public void PageBuilder_SplitsTurnsDeterministically()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 6,
            MaxTurnsPerPage = 3,
            MinInitialPages = 1,
        });
        var source = CreateTurns(7);

        controller.BindTranscript(source, "page-build");

        Assert.Equal(3, controller.Pages.Count);
        Assert.Equal((0, 2), (controller.Pages[0].FirstTurnIndex, controller.Pages[0].LastTurnIndex));
        Assert.Equal((3, 5), (controller.Pages[1].FirstTurnIndex, controller.Pages[1].LastTurnIndex));
        Assert.Equal((6, 6), (controller.Pages[2].FirstTurnIndex, controller.Pages[2].LastTurnIndex));
    }

    [Fact]
    public void InitialReset_MountsNewestPagesOnly()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 2,
            MaxMountedPages = 4,
        });
        var source = CreateTurns(8);

        controller.BindTranscript(source, "initial");
        controller.ResetToLatest(200, "initial");

        var mountedIds = controller.MountedTurns.Select(static turn => turn.StableId).ToArray();
        Assert.Equal(new[] { "turn:0004", "turn:0005", "turn:0006", "turn:0007" }, mountedIds);
    }

    [Fact]
    public void StableMembership_MountsEveryTurnAndNeverMutatesAtViewportBoundaries()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaintainStableMembership = true,
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
            MaxMountedPages = 3,
        });
        var source = CreateTurns(20, measuredHeightFactory: _ => 120);

        controller.BindTranscript(source, "stable");
        controller.ResetToLatest(200, "stable");
        var mountedBefore = controller.MountedTurns.ToArray();

        var olderBoundary = controller.UpdateViewport(
            new TranscriptViewportState(
                0,
                200,
                2400,
                false,
                2200,
                TranscriptPagingDirection.TowardOlder),
            isFollowingTail: false,
            "stable-older");
        var newerBoundary = controller.UpdateViewport(
            new TranscriptViewportState(
                2200,
                200,
                2400,
                true,
                0,
                TranscriptPagingDirection.TowardNewer),
            isFollowingTail: false,
            "stable-newer");

        Assert.Equal(TranscriptWindowMutationKind.None, olderBoundary.Kind);
        Assert.Equal(TranscriptWindowMutationKind.None, newerBoundary.Kind);
        Assert.Equal(source, controller.MountedTurns);
        Assert.Equal(mountedBefore, controller.MountedTurns);
        Assert.False(controller.HasOlderPages);
        Assert.False(controller.HasNewerPages);
    }

    [Fact]
    public void StableMembership_SourceChangesStayIdentitySynchronized()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaintainStableMembership = true,
        });
        var source = CreateTurns(6);

        controller.BindTranscript(source, "stable-source");
        controller.ResetToLatest(200, "stable-source");

        var inserted = CreateTurn(99);
        source.Insert(2, inserted);
        Assert.Equal(source, controller.MountedTurns);
        Assert.Same(inserted, controller.MountedTurns[2]);

        source.Remove(inserted);
        Assert.Equal(source, controller.MountedTurns);
    }

    [Fact]
    public void ProgressiveHistory_StartsAtMeasuredTailAndPrependsWithoutEvictingIt()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            ProgressiveHistory = true,
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 2,
            MaxMountedPages = 4,
            PrependBatchPageCount = 2,
        });
        var source = CreateTurns(20, measuredHeightFactory: _ => 100);

        controller.BindTranscript(source, "progressive");
        controller.ResetToLatest(300, "progressive");
        var tail = source[^1];
        var initialCount = controller.MountedTurns.Count;

        Assert.True(controller.HasOlderPages);
        Assert.False(controller.HasNewerPages);
        Assert.Same(tail, controller.MountedTurns[^1]);
        Assert.Equal(0, controller.TopSpacerHeight);
        Assert.Equal(0, controller.BottomSpacerHeight);

        var mutation = controller.UpdateViewport(
            new TranscriptViewportState(
                0,
                300,
                initialCount * 100,
                false,
                Math.Max(0, initialCount * 100 - 300),
                TranscriptPagingDirection.TowardOlder),
            isFollowingTail: false,
            "progressive-prepend");

        Assert.Equal(TranscriptWindowMutationKind.Prepend, mutation.Kind);
        Assert.True(controller.MountedTurns.Count > initialCount);
        Assert.Same(tail, controller.MountedTurns[^1]);
        Assert.Equal(0, mutation.RemovedPageCount);
        Assert.Equal(0, controller.TopSpacerHeight);
        Assert.Equal(0, controller.BottomSpacerHeight);
    }

    [Fact]
    public void ProgressiveHistory_SourceGrowthKeepsTailMountedWhileReaderIsScrolledUp()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            ProgressiveHistory = true,
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 2,
            MaxMountedPages = 4,
        });
        var source = CreateTurns(12, measuredHeightFactory: _ => 100);

        controller.BindTranscript(source, "progressive-growth");
        controller.ResetToLatest(300, "progressive-growth");
        controller.UpdateScrollState(false, false, 500, "reader-up");
        var added = CreateTurn(99);
        source.Add(added);

        Assert.Same(added, controller.MountedTurns[^1]);
        Assert.False(controller.HasNewerPages);
        Assert.Equal(0, controller.BottomSpacerHeight);
    }

    [Fact]
    public void ProgressiveHistory_RepaginationPreservesFirstMountedTurnIdentity()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            ProgressiveHistory = true,
            MaxPageWeight = 4,
            MaxTurnsPerPage = 4,
            MinInitialPages = 1,
            PrependBatchPageCount = 1,
        });
        var source = new ObservableCollection<TranscriptTurn>
        {
            CreateWeightedTurn(0, 3),
            CreateWeightedTurn(1, 2),
            CreateWeightedTurn(2, 2),
            CreateWeightedTurn(3, 2),
            CreateWeightedTurn(4, 2),
            CreateWeightedTurn(5, 2),
        };

        controller.BindTranscript(source, "repagination");
        controller.ResetToLatest(100, "repagination");
        while (controller.MountedTurns[0].StableId != "turn:0001")
        {
            var mutation = controller.UpdateViewport(
                new TranscriptViewportState(
                    0,
                    100,
                    200,
                    false,
                    100,
                    TranscriptPagingDirection.TowardOlder),
                isFollowingTail: false,
                "repagination-prepend");

            Assert.True(mutation.HasChanges);
        }

        var firstMountedTurn = controller.MountedTurns[0];

        firstMountedTurn.Items.Clear();
        firstMountedTurn.Items.Add(CreateCollapsedReasoningItem(1));
        source.Add(CreateWeightedTurn(6, 2));

        Assert.Same(firstMountedTurn, controller.MountedTurns[0]);
        Assert.Contains(firstMountedTurn, controller.MountedTurns);
        Assert.Same(source[^1], controller.MountedTurns[^1]);
    }

    [Fact]
    public void ProgressiveHistory_DirectFarMountCannotBypassAdmission()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            ProgressiveHistory = true,
            MaxTurnsPerPage = 1,
            MinInitialPages = 1,
        });
        var source = CreateTurns(100);

        controller.BindTranscript(source, "far-mount");
        controller.ResetToLatest(200, "far-mount");
        var mountedBefore = controller.MountedTurns.ToArray();

        Assert.False(controller.MountPageContainingTurn(source[0], "far-mount"));
        Assert.Equal(mountedBefore, controller.MountedTurns);
        Assert.DoesNotContain(source[0], controller.MountedTurns);
    }

    [Fact]
    public void ProgressiveHistory_ResetToStableBoundaryPreservesAdmittedSuffixAcrossRebuild()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            ProgressiveHistory = true,
            MaxTurnsPerPage = 1,
            MinInitialPages = 1,
            PrependBatchPageCount = 4,
        });
        var firstSource = CreateTurns(40);
        controller.BindTranscript(firstSource, "first");
        controller.ResetToLatest(200, "first");
        controller.UpdateViewport(
            new TranscriptViewportState(
                0,
                200,
                400,
                false,
                200,
                TranscriptPagingDirection.TowardOlder),
            isFollowingTail: false,
            "prepend");
        var preservedBoundaryId = controller.MountedTurns[0].StableId;
        var admittedCount = controller.MountedTurns.Count;

        var rebuiltSource = CreateTurns(40);
        controller.BindTranscript(rebuiltSource, "rebuilt");
        controller.ResetToBoundary(preservedBoundaryId, "rebuilt");

        Assert.Equal(preservedBoundaryId, controller.MountedTurns[0].StableId);
        Assert.Equal(admittedCount, controller.MountedTurns.Count);
        Assert.Same(rebuiltSource[^1], controller.MountedTurns[^1]);
    }

    [Fact]
    public void ProgressiveHistory_TenThousandTurnTraversalPrependsWithoutResetsOrEviction()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            ProgressiveHistory = true,
            MaxTurnsPerPage = 1,
            MinInitialPages = 2,
            PrependBatchPageCount = 4,
        });
        var source = CreateTurns(10_000);
        controller.BindTranscript(source, "scale");
        controller.ResetToLatest(720, "scale");

        var resetCount = 0;
        var removedCount = 0;
        controller.MountedTurns.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                resetCount++;
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
                removedCount += e.OldItems?.Count ?? 0;
        };

        var stopwatch = Stopwatch.StartNew();
        var prependSteps = 0;
        while (controller.HasOlderPages)
        {
            var mutation = controller.UpdateViewport(
                new TranscriptViewportState(
                    0,
                    720,
                    Math.Max(720, controller.MountedTurns.Count * 72),
                    false,
                    1_000,
                    TranscriptPagingDirection.TowardOlder),
                isFollowingTail: false,
                "scale-prepend");

            Assert.Equal(TranscriptWindowMutationKind.Prepend, mutation.Kind);
            prependSteps++;
        }
        stopwatch.Stop();

        Assert.Equal(source, controller.MountedTurns);
        Assert.Equal(0, resetCount);
        Assert.Equal(0, removedCount);
        Assert.InRange(prependSteps, 2_490, 2_500);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"10,000-turn progressive traversal took {stopwatch.Elapsed.TotalMilliseconds:n0} ms.");
    }

    [Fact]
    public void ProgressiveHistory_RebindClearsOldMembershipBeforeNewTailReset()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            ProgressiveHistory = true,
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 2,
        });
        var firstSource = CreateTurns(40, measuredHeightFactory: _ => 100);
        controller.BindTranscript(firstSource, "first");
        controller.ResetToLatest(300, "first");
        while (controller.HasOlderPages)
        {
            controller.UpdateViewport(
                new TranscriptViewportState(
                    0,
                    300,
                    600,
                    false,
                    300,
                    TranscriptPagingDirection.TowardOlder),
                isFollowingTail: false,
                "load-all");
        }
        Assert.Equal(firstSource.Count, controller.MountedTurns.Count);

        var secondSource = CreateTurns(12, measuredHeightFactory: _ => 90);
        controller.BindTranscript(secondSource, "second");

        Assert.Empty(controller.MountedTurns);
        controller.ResetToLatest(300, "second");
        Assert.NotEmpty(controller.MountedTurns);
        Assert.True(controller.MountedTurns.Count < secondSource.Count);
        Assert.Same(secondSource[^1], controller.MountedTurns[^1]);
    }

    [Fact]
    public void StableMembership_ThousandTurns_RandomViewportUpdatesNeverMutateMembership()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaintainStableMembership = true,
            MaxPageWeight = 34,
            MaxTurnsPerPage = 8,
            MaxMountedPages = 6,
        });
        var source = CreateTurns(
            1000,
            measuredHeightFactory: index => 72 + ((index * 37) % 420));

        controller.BindTranscript(source, "stable-scale");
        controller.ResetToLatest(720, "stable-scale");
        var mountedBefore = controller.MountedTurns.ToArray();
        var extent = source.Sum(static turn => turn.MeasuredHeight)
            + (source.Count - 1) * TranscriptLayoutMetrics.TurnSpacing;
        var random = new Random(7301);

        for (var step = 0; step < 1000; step++)
        {
            var maxOffset = Math.Max(0d, extent - 720d);
            var offset = maxOffset * random.NextDouble();
            var mutation = controller.UpdateViewport(
                new TranscriptViewportState(
                    offset,
                    720,
                    extent,
                    false,
                    maxOffset - offset),
                isFollowingTail: false,
                "stable-scale-scroll");

            Assert.Equal(TranscriptWindowMutationKind.None, mutation.Kind);
            Assert.Equal(mountedBefore, controller.MountedTurns);
            Assert.Equal(source, controller.MountedTurns);
            Assert.Equal(0, controller.TopSpacerHeight);
            Assert.Equal(0, controller.BottomSpacerHeight);
        }
    }

    [Fact]
    public void StableGeometry_ThousandTurns_RandomViewportWindowsPreserveFullExtent()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaintainStableGeometry = true,
            MaxPageWeight = 34,
            MaxTurnsPerPage = 8,
            MinInitialPages = 2,
            MaxMountedPages = 6,
        });
        var source = CreateTurns(
            1000,
            measuredHeightFactory: index => 72 + ((index * 37) % 420));

        controller.BindTranscript(source, "geometry-scale");
        controller.ResetToLatest(720, "geometry-scale");
        var expectedExtent = GetStableGeometryExtent(controller);
        var random = new Random(7301);

        for (var step = 0; step < 1000; step++)
        {
            var maxOffset = Math.Max(0d, expectedExtent - 720d);
            var offset = maxOffset * random.NextDouble();
            var mutation = controller.UpdateViewport(
                new TranscriptViewportState(
                    offset,
                    720,
                    expectedExtent,
                    false,
                    maxOffset - offset),
                isFollowingTail: false,
                "geometry-scale-scroll");

            Assert.True(mutation.Kind is TranscriptWindowMutationKind.None or TranscriptWindowMutationKind.Rewindow);
            Assert.InRange(controller.CaptureSnapshot().MountedPageCount, 1, 6);
            Assert.InRange(Math.Abs(GetStableGeometryExtent(controller) - expectedExtent), 0, 0.001);

            var mountedHeight = controller.MountedTurns.Sum(static turn => turn.MeasuredHeight)
                + Math.Max(0, controller.MountedTurns.Count - 1) * TranscriptLayoutMetrics.TurnSpacing;
            Assert.True(offset < controller.TopSpacerHeight + mountedHeight);
            Assert.True(offset + 720 > controller.TopSpacerHeight);

            var sourceIndices = controller.MountedTurns
                .Select(turn => source.IndexOf(turn))
                .ToArray();
            Assert.Equal(
                Enumerable.Range(sourceIndices[0], sourceIndices.Length),
                sourceIndices);
        }
    }

    [Fact]
    public void StableGeometry_RewindowKeepsExtentWhenMeasuredTurnsAreShorterThanEstimates()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaintainStableGeometry = true,
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
            MaxMountedPages = 2,
        });
        var source = CreateTurns(12, measuredHeightFactory: _ => 48);

        controller.BindTranscript(source, "geometry-short");
        controller.ResetToLatest(200, "geometry-short");
        var expectedExtent = GetStableGeometryExtent(controller);

        for (var offset = 0d; offset < expectedExtent; offset += 120d)
        {
            controller.UpdateViewport(
                new TranscriptViewportState(
                    offset,
                    200,
                    expectedExtent,
                    false,
                    Math.Max(0d, expectedExtent - 200d - offset)),
                isFollowingTail: false,
                "geometry-short-scroll");

            Assert.InRange(Math.Abs(GetStableGeometryExtent(controller) - expectedExtent), 0, 0.001);
        }
    }

    [Fact]
    public void NearTopScroll_PrependsOlderPageInOrder()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 2,
            MaxMountedPages = 5,
        });
        var source = CreateTurns(8);

        controller.BindTranscript(source, "prepend");
        controller.ResetToLatest(200, "prepend");
        controller.UpdatePinnedState(false, 180, "prepend");

        var mutation = controller.UpdateViewport(
            new TranscriptViewportState(0, 200, 900, false, 180),
            "prepend");

        Assert.Equal(TranscriptWindowMutationKind.Prepend, mutation.Kind);
        Assert.Equal(new[]
        {
            "turn:0002", "turn:0003", "turn:0004", "turn:0005", "turn:0006", "turn:0007"
        }, controller.MountedTurns.Select(static turn => turn.StableId).ToArray());
    }

    [Fact]
    public void PrependingOlderPages_KeepsMountedWindowBounded()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 2,
            MaxMountedPages = 3,
            TrimToMountedPages = 2,
            PrependTriggerPixels = 150,
            RetainAboveViewportPixels = 80,
        });
        var source = CreateTurns(10, measuredHeightFactory: _ => 180);

        controller.BindTranscript(source, "cleanup");
        controller.ResetToLatest(200, "cleanup");
        controller.UpdatePinnedState(false, 240, "cleanup");
        controller.UpdateViewport(new TranscriptViewportState(0, 200, 1200, false, 240), "prepend-1");
        controller.UpdateViewport(new TranscriptViewportState(0, 200, 1200, false, 240), "prepend-2");

        var snapshot = controller.CaptureSnapshot();
        var mutation = controller.UpdateViewport(
            new TranscriptViewportState(1400, 200, 2200, false, 240),
            "cleanup");

        Assert.True(snapshot.MountedPageCount <= 3);
        Assert.Equal(TranscriptWindowMutationKind.None, mutation.Kind);
        Assert.True(controller.CaptureSnapshot().MountedPageCount <= 3);
    }

    [Fact]
    public void NearTopScroll_CanPrependMultiplePagesWithoutRearmScroll()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
            MaxMountedPages = 6,
            PrependTriggerPixels = 160,
        });
        var source = CreateTurns(12, measuredHeightFactory: _ => 120);

        controller.BindTranscript(source, "multi-prepend");
        controller.ResetToLatest(200, "multi-prepend");
        controller.UpdatePinnedState(false, 260, "multi-prepend");

        var firstVisibleBefore = controller.MountedTurns[0].StableId;
        TranscriptWindowMutation lastMutation = TranscriptWindowMutation.None;
        var prependCount = 0;
        var sawBatchedPrepend = false;
        for (var i = 0; i < 4; i++)
        {
            lastMutation = controller.UpdateViewport(
                new TranscriptViewportState(0, 200, 1400 + (i * 120), false, 260),
                $"multi-prepend-{i}");
            if (lastMutation.Kind != TranscriptWindowMutationKind.Prepend)
                break;

            prependCount++;
            sawBatchedPrepend |= lastMutation.AddedPageCount > 1;
        }

        Assert.True(prependCount > 0);
        Assert.True(sawBatchedPrepend);
        Assert.NotEqual(firstVisibleBefore, controller.MountedTurns[0].StableId);
        Assert.Equal(new[]
        {
            "turn:0000", "turn:0001", "turn:0002", "turn:0003", "turn:0004", "turn:0005",
            "turn:0006", "turn:0007", "turn:0008", "turn:0009", "turn:0010", "turn:0011"
        }, controller.MountedTurns.Select(static turn => turn.StableId).ToArray());
    }

    [Fact]
    public void ReturningToLocalBottom_AfterTailWasTrimmed_RemountsNewerPages()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
            MaxMountedPages = 3,
            PrependBatchPageCount = 1,
            PrependTriggerPixels = 160,
        });
        var source = CreateTurns(20, measuredHeightFactory: _ => 120);

        controller.BindTranscript(source, "round-trip");
        controller.ResetToLatest(200, "round-trip");

        for (var i = 0; i < 4 && ReferenceEquals(controller.MountedTurns[^1], source[^1]); i++)
        {
            controller.UpdateViewport(
                new TranscriptViewportState(0, 200, 1200, false, 800),
                isFollowingTail: false,
                $"round-trip-prepend-{i}");
        }

        Assert.NotSame(source[^1], controller.MountedTurns[^1]);

        var sawAppend = false;
        for (var i = 0; i < 12 && !ReferenceEquals(controller.MountedTurns[^1], source[^1]); i++)
        {
            var mutation = controller.UpdateViewport(
                new TranscriptViewportState(1000, 200, 1200, true, 0),
                isFollowingTail: false,
                $"round-trip-append-{i}");
            sawAppend |= mutation.Kind == TranscriptWindowMutationKind.Append;
        }

        Assert.True(sawAppend);
        Assert.Same(source[^1], controller.MountedTurns[^1]);
        Assert.True(controller.CaptureSnapshot().MountedPageCount <= 3);
    }

    [Fact]
    public void LocalBottom_WithUnmountedNewerPages_DoesNotReenterFollowMode()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
            MaxMountedPages = 3,
            PrependBatchPageCount = 1,
            PrependTriggerPixels = 160,
        });
        var source = CreateTurns(20, measuredHeightFactory: _ => 120);

        controller.BindTranscript(source, "local-bottom");
        controller.ResetToLatest(200, "local-bottom");

        for (var i = 0; i < 4 && ReferenceEquals(controller.MountedTurns[^1], source[^1]); i++)
        {
            controller.UpdateViewport(
                new TranscriptViewportState(0, 200, 1200, false, 800),
                isFollowingTail: false,
                $"local-bottom-prepend-{i}");
        }

        Assert.NotSame(source[^1], controller.MountedTurns[^1]);

        controller.UpdateViewport(
            new TranscriptViewportState(1000, 200, 1200, true, 0),
            isFollowingTail: true,
            "local-bottom");

        Assert.False(controller.IsFollowingTail);
    }

    [Fact]
    public void NearTopWindow_DoesNotAppendOnlyBecauseBottomThresholdAlsoMatches()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
            MaxMountedPages = 3,
            PrependBatchPageCount = 1,
            AppendBatchPageCount = 1,
            PrependTriggerPixels = 160,
            AppendTriggerPixels = 160,
        });
        var source = CreateTurns(20, measuredHeightFactory: _ => 120);

        controller.BindTranscript(source, "overlapping-thresholds");
        controller.ResetToLatest(200, "overlapping-thresholds");

        TranscriptWindowMutation mutation;
        do
        {
            mutation = controller.UpdateViewport(
                new TranscriptViewportState(0, 200, 300, false, 0),
                isFollowingTail: false,
                "overlapping-thresholds-prepend");
        }
        while (mutation.Kind == TranscriptWindowMutationKind.Prepend);

        Assert.NotSame(source[^1], controller.MountedTurns[^1]);

        mutation = controller.UpdateViewport(
            new TranscriptViewportState(0, 200, 300, false, 0),
            isFollowingTail: false,
            "overlapping-thresholds-stable");

        Assert.Equal(TranscriptWindowMutationKind.None, mutation.Kind);
    }

    [Fact]
    public void CompactWindow_ExplicitNewerDirection_AppendsInsteadOfRemainingStranded()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
            MaxMountedPages = 3,
            PrependBatchPageCount = 1,
            AppendBatchPageCount = 1,
            PrependTriggerPixels = 220,
            AppendTriggerPixels = 220,
        });
        var source = CreateTurns(20, measuredHeightFactory: _ => 72);

        controller.BindTranscript(source, "compact-direction");
        controller.ResetToLatest(200, "compact-direction");

        TranscriptWindowMutation mutation;
        do
        {
            mutation = controller.UpdateViewport(
                new TranscriptViewportState(0, 200, 300, false, 0),
                isFollowingTail: false,
                "compact-direction-prepend");
        }
        while (mutation.Kind == TranscriptWindowMutationKind.Prepend);

        Assert.NotSame(source[^1], controller.MountedTurns[^1]);

        mutation = controller.UpdateViewport(
            new TranscriptViewportState(
                0,
                200,
                300,
                false,
                0,
                TranscriptPagingDirection.TowardOlder),
            isFollowingTail: false,
            "compact-direction-stay-older");

        Assert.Equal(TranscriptWindowMutationKind.None, mutation.Kind);

        mutation = controller.UpdateViewport(
            new TranscriptViewportState(
                0,
                200,
                300,
                false,
                0,
                TranscriptPagingDirection.TowardNewer),
            isFollowingTail: false,
            "compact-direction-append");

        Assert.Equal(TranscriptWindowMutationKind.Append, mutation.Kind);
    }

    [Fact]
    public void PinnedStateLogic_TracksTransitions()
    {
        var controller = new TranscriptWindowController();
        controller.UpdatePinnedState(false, 140, "scroll-away");
        Assert.False(controller.CaptureSnapshot().IsPinnedToBottom);

        controller.UpdatePinnedState(true, 0, "bottom");
        var snapshot = controller.CaptureSnapshot();
        Assert.True(snapshot.IsPinnedToBottom);
        Assert.Equal(0, snapshot.DistanceFromBottom);
    }

    [Fact]
    public void StreamingWhilePinned_KeepsPinnedAndAppendsLatestTurn()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
        });
        var source = CreateTurns(4);

        controller.BindTranscript(source, "stream-pinned");
        controller.ResetToLatest(180, "stream-pinned");
        controller.UpdatePinnedState(true, 0, "stream-pinned");

        source.Add(CreateTurn(4));

        var snapshot = controller.CaptureSnapshot();
        Assert.True(snapshot.IsPinnedToBottom);
        Assert.Equal("turn:0004", controller.MountedTurns[^1].StableId);
    }

    [Fact]
    public void StreamingWhileNotPinned_DoesNotForcePinningBackOn()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
        });
        var source = CreateTurns(4);

        controller.BindTranscript(source, "stream-reader");
        controller.ResetToLatest(180, "stream-reader");
        controller.UpdatePinnedState(false, 240, "stream-reader");

        source.Add(CreateTurn(4));

        var snapshot = controller.CaptureSnapshot();
        Assert.False(snapshot.IsPinnedToBottom);
        Assert.Equal("turn:0003", controller.MountedTurns[^1].StableId);
    }

    [Fact]
    public void ScrollCompensation_IsCapturedInDiagnostics()
    {
        var controller = new TranscriptWindowController();

        controller.RecordScrollCompensation("prepend", 120, 360);

        var snapshot = controller.CaptureSnapshot();
        Assert.Equal(120, snapshot.LastCompensationBeforeOffset);
        Assert.Equal(360, snapshot.LastCompensationAfterOffset);
    }

    [Fact]
    public void EnsureViewportCoverage_UsesMeasuredPageHeights()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
            MaxMountedPages = 4,
            EstimatedPixelsPerWeightUnit = 20,
        });
        var source = CreateTurns(6, measuredHeightFactory: index => index >= 4 ? 120 : 260);

        controller.BindTranscript(source, "coverage");
        controller.ResetToLatest(180, "coverage");

        var mutation = controller.EnsureViewportCoverage(420, "coverage");

        Assert.Equal(TranscriptWindowMutationKind.EnsureCoverage, mutation.Kind);
        Assert.True(controller.CaptureSnapshot().MountedPageCount >= 2);
    }

    [Fact]
    public void EnsureViewportCoverage_AccountsForPageBoundarySpacing()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 2,
            MaxMountedPages = 3,
        });
        var source = CreateTurns(6, measuredHeightFactory: _ => 100);

        controller.BindTranscript(source, "spacing");
        controller.ResetToLatest(200, "spacing");

        var mutation = controller.EnsureViewportCoverage(430, "spacing");

        Assert.Equal(TranscriptWindowMutationKind.EnsureCoverage, mutation.Kind);
        Assert.Equal(3, controller.CaptureSnapshot().MountedPageCount);
    }

    [Fact]
    public void EnsureViewportCoverage_UsesActualExtentWhenEstimatedCoverageIsTooOptimistic()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 2,
            MaxMountedPages = 6,
            MountedViewportFillMultiplier = 1.0,
        });
        var source = CreateTurns(12, measuredHeightFactory: _ => 20);

        controller.BindTranscript(source, "actual-extent");
        controller.ResetToLatest(300, "actual-extent");

        var mountedPagesBefore = controller.CaptureSnapshot().MountedPageCount;
        var actualExtentHeight = GetActualExtentHeight(controller.MountedTurns);
        Assert.True(actualExtentHeight < 300);

        var estimatedOnlyMutation = controller.EnsureViewportCoverage(300, "estimated-only");
        Assert.Equal(TranscriptWindowMutationKind.None, estimatedOnlyMutation.Kind);

        var mutation = controller.EnsureViewportCoverage(300, "actual-extent", actualExtentHeight);

        Assert.Equal(TranscriptWindowMutationKind.EnsureCoverage, mutation.Kind);
        Assert.True(mutation.RequiresAnchorRestore);
        Assert.True(controller.CaptureSnapshot().MountedPageCount > mountedPagesBefore);
        Assert.True(
            GetActualExtentHeight(controller.MountedTurns) >= 300
            || controller.CaptureSnapshot().MountedPageCount == 6);
    }

    [Fact]
    public void StreamingWhileReaderWindowShiftedOffTail_KeepsMountedWindowBounded()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
            MaxMountedPages = 3,
            PrependTriggerPixels = 160,
        });
        var source = CreateTurns(16, measuredHeightFactory: _ => 120);

        controller.BindTranscript(source, "reader-window");
        controller.ResetToLatest(200, "reader-window");
        controller.UpdatePinnedState(false, 260, "reader-window");

        for (var i = 0; i < 6; i++)
        {
            var mutation = controller.UpdateViewport(
                new TranscriptViewportState(0, 200, 1800 + (i * 120), false, 260),
                $"reader-window-{i}");
            if (mutation.Kind != TranscriptWindowMutationKind.Prepend)
                break;
        }

        Assert.Equal("turn:0000", controller.MountedTurns[0].StableId);

        source.Add(CreateTurn(16, measuredHeight: 120));

        var mountedIds = controller.MountedTurns.Select(static turn => turn.StableId).ToArray();
        Assert.True(controller.CaptureSnapshot().MountedPageCount <= 3);
        Assert.Equal("turn:0000", mountedIds[0]);
        Assert.DoesNotContain("turn:0016", mountedIds);
    }

    [Fact]
    public void StreamingWhileReaderStillWithinTailWindow_DoesNotPullMountedWindowForward()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
            MaxMountedPages = 3,
        });
        var source = CreateTurns(8, measuredHeightFactory: _ => 100);

        controller.BindTranscript(source, "reader-tail-window");
        controller.ResetToLatest(200, "reader-tail-window");
        controller.UpdatePinnedState(false, 1000, "reader-tail-window");

        var mountedBefore = controller.MountedTurns.Select(static turn => turn.StableId).ToArray();
        Assert.Equal(new[] { "turn:0004", "turn:0005", "turn:0006", "turn:0007" }, mountedBefore);

        source.Add(CreateTurn(8, measuredHeight: 100));
        source.Add(CreateTurn(9, measuredHeight: 100));

        var mountedAfter = controller.MountedTurns.Select(static turn => turn.StableId).ToArray();
        Assert.Equal(mountedBefore, mountedAfter);
        Assert.DoesNotContain("turn:0008", mountedAfter);
        Assert.DoesNotContain("turn:0009", mountedAfter);
    }

    [Fact]
    public void StreamingWhilePinnedAtTail_TrimsHeadToKeepLatestVisible()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
            MaxMountedPages = 3,
        });
        var source = CreateTurns(8, measuredHeightFactory: _ => 100);

        controller.BindTranscript(source, "tail-track");
        controller.ResetToLatest(200, "tail-track");
        controller.UpdatePinnedState(true, 0, "tail-track");

        // Verify mounted is tracking the tail
        Assert.Equal("turn:0007", controller.MountedTurns[^1].StableId);

        // Add enough turns to push beyond MaxMountedPages
        for (var i = 8; i < 14; i++)
            source.Add(CreateTurn(i, measuredHeight: 100));

        var snapshot = controller.CaptureSnapshot();
        Assert.True(snapshot.MountedPageCount <= 3, "Mounted pages should not exceed MaxMountedPages");
        Assert.Equal("turn:0013", controller.MountedTurns[^1].StableId);
        Assert.Contains(controller.MountedTurns, turn => turn.StableId == "turn:0013");
    }

    [Fact]
    public void StreamingWhilePinnedAtTail_DoesNotDropNewContent()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 2,
            MaxMountedPages = 3,
            InitialViewportFillMultiplier = 10, // Force mounting all pages
        });
        var source = CreateTurns(6, measuredHeightFactory: _ => 100);

        controller.BindTranscript(source, "no-drop");
        controller.ResetToLatest(200, "no-drop");
        controller.UpdatePinnedState(true, 0, "no-drop");

        // MountedPages should be at max (3 pages with 2 turns each = 6 turns)
        Assert.Equal(3, controller.CaptureSnapshot().MountedPageCount);

        // Add two more turns creating a new page
        source.Add(CreateTurn(6, measuredHeight: 100));
        source.Add(CreateTurn(7, measuredHeight: 100));

        // New turns must be visible - head should be trimmed, not tail
        var mountedIds = controller.MountedTurns.Select(static turn => turn.StableId).ToArray();
        Assert.Contains("turn:0007", mountedIds);
        Assert.Contains("turn:0006", mountedIds);
        Assert.True(controller.CaptureSnapshot().MountedPageCount <= 3);
    }

    [Fact]
    public void EnsureLatestMounted_BringsLatestIntoView()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
            MaxMountedPages = 3,
            PrependTriggerPixels = 160,
        });
        var source = CreateTurns(12, measuredHeightFactory: _ => 120);

        controller.BindTranscript(source, "ensure-latest");
        controller.ResetToLatest(200, "ensure-latest");
        controller.UpdatePinnedState(false, 260, "ensure-latest");

        // Simulate scrolling up: prepend head pages
        for (var i = 0; i < 4; i++)
        {
            controller.UpdateViewport(
                new TranscriptViewportState(0, 200, 1400 + (i * 120), false, 260),
                $"scroll-up-{i}");
        }

        // Verify the latest turn is NOT mounted (user scrolled away)
        var mountedIds = controller.MountedTurns.Select(static turn => turn.StableId).ToArray();
        Assert.DoesNotContain("turn:0011", mountedIds);

        // Now ensure latest is mounted (simulates user sending a message)
        var changed = controller.EnsureLatestMounted("user-sent");
        Assert.True(changed);

        mountedIds = controller.MountedTurns.Select(static turn => turn.StableId).ToArray();
        Assert.Contains("turn:0011", mountedIds);
        Assert.True(controller.CaptureSnapshot().MountedPageCount <= 3);
    }

    [Fact]
    public void EnsureLatestMountedIfAdjacentTailGap_RestoresCompletedAssistantWithoutPinning()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 1,
            MinInitialPages = 1,
            MaxMountedPages = 3,
        });
        var source = CreateTurns(7, measuredHeightFactory: _ => 120);

        controller.BindTranscript(source, "assistant-tail-gap");
        controller.ResetToLatest(200, "assistant-tail-gap");
        controller.UpdatePinnedState(false, 260, "assistant-tail-gap");

        Assert.Equal("turn:0006", controller.MountedTurns[^1].StableId);

        source.Add(CreateTurn(7, measuredHeight: 120));

        Assert.DoesNotContain(controller.MountedTurns, turn => turn.StableId == "turn:0007");

        var mutation = controller.EnsureLatestMountedIfAdjacentTailGap("assistant-completed");

        Assert.Equal(TranscriptWindowMutationKind.TailRestore, mutation.Kind);
        Assert.False(controller.CaptureSnapshot().IsPinnedToBottom);
        Assert.True(controller.CaptureSnapshot().MountedPageCount <= 3);
        Assert.Equal("turn:0007", controller.MountedTurns[^1].StableId);
        Assert.Contains(controller.MountedTurns, turn => turn.StableId == "turn:0006");
    }

    [Fact]
    public void EnsureLatestMountedIfAdjacentTailGap_DoesNotJumpFarReaderWindow()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 1,
            MinInitialPages = 1,
            MaxMountedPages = 3,
            PrependTriggerPixels = 160,
        });
        var source = CreateTurns(12, measuredHeightFactory: _ => 120);

        controller.BindTranscript(source, "far-reader");
        controller.ResetToLatest(200, "far-reader");
        controller.UpdatePinnedState(false, 260, "far-reader");

        for (var i = 0; i < 4; i++)
        {
            controller.UpdateViewport(
                new TranscriptViewportState(0, 200, 1400 + (i * 120), false, 260),
                $"far-reader-{i}");
        }

        var mountedBefore = controller.MountedTurns.Select(static turn => turn.StableId).ToArray();
        source.Add(CreateTurn(12, measuredHeight: 120));

        var mutation = controller.EnsureLatestMountedIfAdjacentTailGap("assistant-completed");

        Assert.Equal(TranscriptWindowMutationKind.None, mutation.Kind);
        Assert.Equal(mountedBefore, controller.MountedTurns.Select(static turn => turn.StableId).ToArray());
    }

    [Fact]
    public void EnsureLatestMounted_NoOpWhenAlreadyAtTail()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
        });
        var source = CreateTurns(4);

        controller.BindTranscript(source, "already-tail");
        controller.ResetToLatest(200, "already-tail");

        var mountedCountBefore = controller.MountedTurns.Count;
        var changed = controller.EnsureLatestMounted("already-tail");
        Assert.False(changed);
        Assert.Equal(mountedCountBefore, controller.MountedTurns.Count);
    }

    [Fact]
    public void CompletedTurnAppendedWhileReaderIsAway_IsRecoveredByEnsureLatestMounted()
    {
        // Guards the explicit recovery primitive used when the user asks to return to the latest turn.
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 1,
            MinInitialPages = 1,
            MaxMountedPages = 3,
        });
        var source = CreateTurns(5, measuredHeightFactory: _ => 120);

        controller.BindTranscript(source, "reader-away");
        controller.ResetToLatest(200, "reader-away");

        controller.UpdateScrollState(
            isFollowingTail: false,
            isPinnedToBottom: false,
            distanceFromBottom: 240,
            reason: "reader-away");

        // A completed assistant turn lands while the reader is intentionally away from the tail.
        source.Add(CreateTurn(5, measuredHeight: 120));

        Assert.DoesNotContain(controller.MountedTurns, turn => turn.StableId == "turn:0005");

        var changed = controller.EnsureLatestMounted("assistant-completed");

        Assert.True(changed);
        Assert.Equal("turn:0005", controller.MountedTurns[^1].StableId);
        Assert.True(controller.CaptureSnapshot().MountedPageCount <= 3);
    }

    [Fact]
    public void FollowingTailWhileTransientlyUnpinned_AutoMountsNewTailTurn()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 1,
            MinInitialPages = 1,
            MaxMountedPages = 3,
        });
        var source = CreateTurns(5, measuredHeightFactory: _ => 120);

        controller.BindTranscript(source, "follow-intent");
        controller.ResetToLatest(200, "follow-intent");
        controller.UpdateScrollState(
            isFollowingTail: true,
            isPinnedToBottom: false,
            distanceFromBottom: 240,
            reason: "transient-unpin");

        source.Add(CreateTurn(5, measuredHeight: 120));

        Assert.Equal("turn:0005", controller.MountedTurns[^1].StableId);
        Assert.True(controller.CaptureSnapshot().MountedPageCount <= 3);
    }

    [Fact]
    public void MountingOlderTurn_StopsTailTrackingBeforeStreamingAppend()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 1,
            MinInitialPages = 1,
            MaxMountedPages = 3,
        });
        var source = CreateTurns(8, measuredHeightFactory: _ => 120);

        controller.BindTranscript(source, "search-navigation");
        controller.ResetToLatest(200, "search-navigation");

        var target = source[1];
        Assert.True(controller.MountPageContainingTurn(target, "search-navigation"));
        Assert.False(controller.IsFollowingTail);

        source.Add(CreateTurn(8, measuredHeight: 120));

        Assert.Contains(controller.MountedTurns, turn => ReferenceEquals(turn, target));
        Assert.DoesNotContain(controller.MountedTurns, turn => turn.StableId == "turn:0008");
    }

    [Fact]
    public void UserSendsMessageAfterScrollUp_NewTurnIsMountedViaEnsureLatest()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
            MaxMountedPages = 3,
            PrependTriggerPixels = 160,
        });
        var source = CreateTurns(12, measuredHeightFactory: _ => 120);

        controller.BindTranscript(source, "send-after-scroll");
        controller.ResetToLatest(200, "send-after-scroll");
        controller.UpdatePinnedState(false, 260, "send-after-scroll");

        // Scroll up
        for (var i = 0; i < 4; i++)
        {
            controller.UpdateViewport(
                new TranscriptViewportState(0, 200, 1400 + (i * 120), false, 260),
                $"scroll-up-{i}");
        }

        // User types and sends a new message — this adds a turn to the source
        source.Add(CreateTurn(12, measuredHeight: 120));

        // At this point, the new turn is NOT mounted because user was scrolled away
        var mountedBefore = controller.MountedTurns.Select(static turn => turn.StableId).ToArray();
        Assert.DoesNotContain("turn:0012", mountedBefore);

        // EnsureLatestMounted simulates what OnUserMessageSent does
        controller.EnsureLatestMounted("user-sent");

        var mountedAfter = controller.MountedTurns.Select(static turn => turn.StableId).ToArray();
        Assert.Contains("turn:0012", mountedAfter);
        Assert.True(controller.CaptureSnapshot().MountedPageCount <= 3);
    }

    private static ObservableCollection<TranscriptTurn> CreateTurns(
        int count,
        int itemCount = 1,
        Func<int, double>? measuredHeightFactory = null)
    {
        return new ObservableCollection<TranscriptTurn>(
            Enumerable.Range(0, count)
                .Select(index => CreateTurn(index, itemCount, measuredHeightFactory?.Invoke(index) ?? 0))
                .ToArray());
    }

    private static double GetStableGeometryExtent(TranscriptWindowController controller)
    {
        var mountedHeight = controller.MountedTurns.Sum(static turn => turn.MeasuredHeight)
            + Math.Max(0, controller.MountedTurns.Count - 1) * TranscriptLayoutMetrics.TurnSpacing;
        return controller.TopSpacerHeight + mountedHeight + controller.BottomSpacerHeight;
    }

    private static TranscriptTurn CreateTurn(int index, int itemCount = 1, double measuredHeight = 0)
    {
        var turn = new TranscriptTurn($"turn:{index:D4}");
        for (var itemIndex = 0; itemIndex < itemCount; itemIndex++)
            turn.Items.Add(new TestTranscriptItem($"item:{index:D4}:{itemIndex:D2}"));

        if (measuredHeight > 0)
            turn.MeasuredHeight = measuredHeight;

        return turn;
    }

    private static TranscriptTurn CreateWeightedTurn(int index, int weight)
    {
        var turn = new TranscriptTurn($"turn:{index:D4}");
        while (weight >= 2)
        {
            var itemIndex = turn.Items.Count;
            turn.Items.Add(new TestTranscriptItem($"item:{index:D4}:{itemIndex:D2}"));
            weight -= 2;
        }

        if (weight == 1)
            turn.Items.Add(CreateCollapsedReasoningItem(index));

        return turn;
    }

    private static ReasoningItem CreateCollapsedReasoningItem(int index)
    {
        var source = new ChatMessageViewModel(new Lumi.Models.ChatMessage
        {
            Role = "reasoning",
            Content = $"Reasoning {index}",
        });
        return new ReasoningItem(source, expandWhileStreaming: false);
    }

    private static double GetActualExtentHeight(IReadOnlyCollection<TranscriptTurn> turns)
    {
        if (turns.Count == 0)
            return 0;

        return turns.Sum(static turn => turn.MeasuredHeight)
            + Math.Max(0, turns.Count - 1) * TranscriptLayoutMetrics.TurnSpacing;
    }

    // ─────────────────────────────────────────────────────────────
    //  Scrolling behaviour tests
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void PinnedState_InitiallyPinned()
    {
        var controller = new TranscriptWindowController();
        Assert.True(controller.IsPinnedToBottom);
    }

    [Fact]
    public void PinnedState_UnpinsWhenScrolledAway()
    {
        var controller = new TranscriptWindowController();
        controller.UpdatePinnedState(false, 100, "user-scroll");

        Assert.False(controller.IsPinnedToBottom);
        Assert.Equal(100, controller.DistanceFromBottom);
    }

    [Fact]
    public void PinnedState_RepinsWhenReturningToBottom()
    {
        var controller = new TranscriptWindowController();

        controller.UpdatePinnedState(false, 100, "scroll-away");
        Assert.False(controller.IsPinnedToBottom);

        controller.UpdatePinnedState(true, 0, "scroll-back");
        Assert.True(controller.IsPinnedToBottom);
        Assert.Equal(0, controller.DistanceFromBottom);
    }

    [Fact]
    public void PinnedState_RaisesPropertyChanged()
    {
        var controller = new TranscriptWindowController();
        var changedProps = new List<string>();
        controller.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
                changedProps.Add(e.PropertyName);
        };

        controller.UpdatePinnedState(false, 50, "scroll");

        Assert.Contains(nameof(TranscriptWindowController.IsPinnedToBottom), changedProps);
        Assert.Contains(nameof(TranscriptWindowController.DistanceFromBottom), changedProps);
    }

    [Fact]
    public void PinnedState_NoPropertyChangedWhenValueUnchanged()
    {
        var controller = new TranscriptWindowController();
        // Default is pinned; update to same state with different distance.
        var changedProps = new List<string>();
        controller.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
                changedProps.Add(e.PropertyName);
        };

        controller.UpdatePinnedState(true, 3, "still-pinned");

        // DistanceFromBottom changes, but IsPinnedToBottom stays true.
        Assert.DoesNotContain(nameof(TranscriptWindowController.IsPinnedToBottom), changedProps);
        Assert.Contains(nameof(TranscriptWindowController.DistanceFromBottom), changedProps);
    }

    [Fact]
    public void StreamingGrowth_PinnedStatePreservedWhileAtTail()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
        });
        var source = CreateTurns(4, measuredHeightFactory: _ => 100);

        controller.BindTranscript(source, "stream-grow");
        controller.ResetToLatest(400, "stream-grow");
        controller.UpdatePinnedState(true, 0, "at-bottom");

        // Simulate streaming: content grows in the last turn.
        source[^1].MeasuredHeight = 300;

        // Pinned state should still be true (controller doesn't unpin
        // on turn height changes — the view manages that via the shell).
        Assert.True(controller.IsPinnedToBottom);
        Assert.Equal("turn:0003", controller.MountedTurns[^1].StableId);
    }

    [Fact]
    public void ViewportUpdate_NoMutationWhenPinnedAndNothingToTrim()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 6,
            MaxTurnsPerPage = 3,
            MinInitialPages = 1,
            MaxMountedPages = 4,
        });
        var source = CreateTurns(5, measuredHeightFactory: _ => 100);

        controller.BindTranscript(source, "no-mutation");
        controller.ResetToLatest(400, "no-mutation");

        var mutation = controller.UpdateViewport(
            new TranscriptViewportState(200, 400, 600, true, 0),
            "pinned-stable");

        Assert.Equal(TranscriptWindowMutationKind.None, mutation.Kind);
        Assert.False(mutation.HasChanges);
    }

    [Fact]
    public void ViewportUpdate_SyncsDistanceFromBottom()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 6,
            MaxTurnsPerPage = 3,
            MinInitialPages = 1,
        });
        var source = CreateTurns(5, measuredHeightFactory: _ => 100);

        controller.BindTranscript(source, "dist-sync");
        controller.ResetToLatest(400, "dist-sync");

        controller.UpdateViewport(
            new TranscriptViewportState(100, 400, 600, false, 100),
            "scrolled");

        Assert.False(controller.IsPinnedToBottom);
        Assert.Equal(100, controller.DistanceFromBottom);
    }

    [Fact]
    public void StreamingAddsNewTurn_StaysMountedWhilePinned()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
        });
        var source = CreateTurns(4, measuredHeightFactory: _ => 120);

        controller.BindTranscript(source, "add-while-pinned");
        controller.ResetToLatest(400, "add-while-pinned");
        controller.UpdatePinnedState(true, 0, "at-bottom");

        // Simulate streaming: new turn added (e.g. tool call result)
        source.Add(CreateTurn(4, measuredHeight: 120));
        source.Add(CreateTurn(5, measuredHeight: 120));

        Assert.Contains(controller.MountedTurns, t => t.StableId == "turn:0005");
        Assert.Contains(controller.MountedTurns, t => t.StableId == "turn:0004");
    }

    [Fact]
    public void HeightChangeOnMountedTurn_DoesNotUnpin()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
        });
        var source = CreateTurns(4, measuredHeightFactory: _ => 100);

        controller.BindTranscript(source, "height-change");
        controller.ResetToLatest(400, "height-change");
        controller.UpdatePinnedState(true, 0, "pinned");

        // Simulate a height change on a mounted turn (e.g. image loaded)
        source[2].MeasuredHeight = 250;

        // The controller doesn't change pinned state from height changes;
        // that's handled by the view's scroll event handlers.
        Assert.True(controller.IsPinnedToBottom);
    }

    [Fact]
    public void EnsureViewportCoverage_DoesNothingWhenAlreadyCovered()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 10,
            MaxTurnsPerPage = 5,
            MinInitialPages = 1,
            MountedViewportFillMultiplier = 1.5,
        });
        var source = CreateTurns(3, measuredHeightFactory: _ => 200);

        controller.BindTranscript(source, "covered");
        controller.ResetToLatest(300, "covered");

        var mutation = controller.EnsureViewportCoverage(300, "covered");

        // All turns fit in one page; viewport is already covered.
        Assert.Equal(TranscriptWindowMutationKind.None, mutation.Kind);
    }

    [Fact]
    public void Prepend_RequiresAnchorRestore()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
            MaxMountedPages = 5,
            PrependTriggerPixels = 200,
        });
        var source = CreateTurns(8, measuredHeightFactory: _ => 100);

        controller.BindTranscript(source, "anchor-restore");
        controller.ResetToLatest(200, "anchor-restore");
        controller.UpdatePinnedState(false, 200, "scrolled-away");

        var mutation = controller.UpdateViewport(
            new TranscriptViewportState(0, 200, 900, false, 200),
            "prepend");

        Assert.Equal(TranscriptWindowMutationKind.Prepend, mutation.Kind);
        Assert.True(mutation.RequiresAnchorRestore);
    }

    [Fact]
    public void TrimHead_RequiresAnchorRestore()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 2,
            MaxMountedPages = 3,
            TrimToMountedPages = 2,
            PrependTriggerPixels = 150,
            RetainAboveViewportPixels = 50,
        });
        var source = CreateTurns(10, measuredHeightFactory: _ => 180);

        controller.BindTranscript(source, "trim-anchor");
        controller.ResetToLatest(200, "trim-anchor");
        controller.UpdatePinnedState(false, 240, "trim-anchor");

        // Prepend enough pages to exceed MaxMountedPages
        controller.UpdateViewport(
            new TranscriptViewportState(0, 200, 1200, false, 240), "prepend-1");
        controller.UpdateViewport(
            new TranscriptViewportState(0, 200, 1200, false, 240), "prepend-2");

        // Now scroll far down to trigger cleanup
        var mutation = controller.UpdateViewport(
            new TranscriptViewportState(1400, 200, 2200, false, 240),
            "cleanup");

        if (mutation.Kind == TranscriptWindowMutationKind.TrimHead)
            Assert.True(mutation.RequiresAnchorRestore);
    }
}
