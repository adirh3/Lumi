using System;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatScrollPolicyTests
{
    private static ChatScrollMetrics At(double distanceFromBottom)
    {
        const double extent = 2_000d;
        const double viewport = 500d;
        return new ChatScrollMetrics(extent - viewport - distanceFromBottom, extent, viewport);
    }

    [Fact]
    public void ReaderScrolledUp_ContentGrowthPreservesViewportAndMarksUnseen()
    {
        var policy = new ChatScrollPolicy();

        policy.OnUserScroll(At(240), offsetDeltaY: -40);
        var decision = policy.OnContentChanged(At(240), markAsUnseen: true);

        Assert.Equal(ChatScrollIntent.PreserveViewport, policy.Intent);
        Assert.Equal(ChatScrollAction.None, decision.Action);
        Assert.True(policy.HasUnseenContent);
    }

    [Fact]
    public void ReaderWhoExplicitlyLeftTail_MarksContentUnseenWithinBottomTolerance()
    {
        var policy = new ChatScrollPolicy();
        policy.OnUserScroll(At(4), offsetDeltaY: -4);

        var decision = policy.OnContentChanged(At(4), markAsUnseen: true);

        Assert.Equal(ChatScrollAction.None, decision.Action);
        Assert.False(policy.IsFollowingTail);
        Assert.True(policy.HasUnseenContent);
    }

    [Fact]
    public void FollowingTail_ContentGrowthRequestsBottomWithoutChangingGeneration()
    {
        var policy = new ChatScrollPolicy();
        policy.EnterFollowMode();
        var generation = policy.Generation;

        for (var i = 0; i < 20; i++)
        {
            var decision = policy.OnContentChanged(At(12), markAsUnseen: false);
            Assert.Equal(ChatScrollAction.ScrollToBottom, decision.Action);
            Assert.Equal(generation, decision.Generation);
        }

        Assert.Equal(generation, policy.Generation);
    }

    [Fact]
    public void SendingMessageOverridesScrolledUpReader()
    {
        var policy = new ChatScrollPolicy();
        policy.OnUserScroll(At(600), offsetDeltaY: -80);

        var decision = policy.RequestRevealSentMessage();

        Assert.Equal(ChatScrollIntent.RevealSentMessage, policy.Intent);
        Assert.True(policy.IsFollowingTail);
        Assert.Equal(ChatScrollAction.ScrollToBottom, decision.Action);
    }

    [Fact]
    public void InitialOpenHasExplicitBottomIntent()
    {
        var policy = new ChatScrollPolicy();
        policy.PreserveViewport();

        var decision = policy.RequestInitialBottom();
        policy.OnBottomLanded(At(0));

        Assert.Equal(ChatScrollAction.ScrollToBottom, decision.Action);
        Assert.Equal(ChatScrollIntent.FollowBottom, policy.Intent);
    }

    [Fact]
    public void EveryRealUserInputInvalidatesQueuedWork()
    {
        var policy = new ChatScrollPolicy();
        var queued = policy.OnContentChanged(At(0), markAsUnseen: false);

        policy.OnUserInput(leavesTail: true);

        Assert.NotEqual(queued.Generation, policy.Generation);
        Assert.False(policy.IsFollowingTail);
    }

    [Fact]
    public void CancellingQueuedContentFollow_MarksContentUnseen()
    {
        var policy = new ChatScrollPolicy();
        var queued = policy.OnContentChanged(At(40), markAsUnseen: true);

        policy.OnUserInput(leavesTail: true);

        Assert.Equal(ChatScrollAction.ScrollToBottom, queued.Action);
        Assert.NotEqual(queued.Generation, policy.Generation);
        Assert.False(policy.IsFollowingTail);
        Assert.True(policy.HasUnseenContent);
    }

    [Fact]
    public void LandingQueuedContentFollow_ClearsPendingUnseenState()
    {
        var policy = new ChatScrollPolicy();
        policy.OnContentChanged(At(40), markAsUnseen: true);
        policy.OnBottomLanded(At(0));

        policy.OnUserInput(leavesTail: true);

        Assert.False(policy.IsFollowingTail);
        Assert.False(policy.HasUnseenContent);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(0.4d)]
    [InlineData(7.9d)]
    public void FractionalBottomJitterDoesNotOscillateIntent(double distanceFromBottom)
    {
        var policy = new ChatScrollPolicy();

        policy.OnUserScroll(At(distanceFromBottom), offsetDeltaY: 1);

        Assert.True(policy.IsFollowingTail);
        Assert.True(policy.IsAtBottom(At(distanceFromBottom)));
    }

    [Fact]
    public void ContentAboveReaderProducesGenerationStampedCompensation()
    {
        var policy = new ChatScrollPolicy();
        policy.OnUserScroll(At(500), offsetDeltaY: -50);

        var decision = policy.OnContentAboveViewportResized(72);

        Assert.Equal(ChatScrollAction.CompensateViewport, decision.Action);
        Assert.Equal(72, decision.OffsetDelta);
        Assert.Equal(policy.Generation, decision.Generation);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidMetricsAreRejected(double invalid)
    {
        Assert.Throws<ArgumentException>(() => new ChatScrollMetrics(invalid, 100, 50));
    }
}
