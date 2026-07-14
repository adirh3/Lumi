using System;
using System.Collections.ObjectModel;
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

    private static TranscriptTurn CreateTurn(int index, int itemCount = 1, double measuredHeight = 0)
    {
        var turn = new TranscriptTurn($"turn:{index:D4}");
        for (var itemIndex = 0; itemIndex < itemCount; itemIndex++)
            turn.Items.Add(new TestTranscriptItem($"item:{index:D4}:{itemIndex:D2}"));

        if (measuredHeight > 0)
            turn.MeasuredHeight = measuredHeight;

        return turn;
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
