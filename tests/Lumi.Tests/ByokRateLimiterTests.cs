using System;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class ByokRateLimiterTests
{
    [Fact]
    public async Task Acquire_NoModelId_IsNoOp()
    {
        // A null/empty model id means non-BYOK: never block, never allocate state.
        var limiter = new ByokRateLimiter();
        await limiter.AcquireSendSlotAsync(null, limit: 5, CancellationToken.None);
        await limiter.AcquireSendSlotAsync("", limit: 5, CancellationToken.None);
        Assert.False(limiter.IsRateLimited(null));
        Assert.False(limiter.IsRateLimited(""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Acquire_NoOrNonPositiveLimit_IsNoOp(int? limit)
    {
        // limit <= 0 or null means "unlimited" — pure passthrough, no state.
        var limiter = new ByokRateLimiter();
        await limiter.AcquireSendSlotAsync("m1", limit, CancellationToken.None);
        Assert.False(limiter.IsRateLimited("m1"));
    }

    [Fact]
    public async Task Acquire_UnderLimit_DoesNotBlock()
    {
        var limiter = new ByokRateLimiter();
        // Consume 3 of 5 allowed slots — should not block.
        for (var i = 0; i < 3; i++)
            await limiter.AcquireSendSlotAsync("m1", limit: 5, CancellationToken.None);

        Assert.True(limiter.IsRateLimited("m1"));
    }

    [Fact]
    public async Task Acquire_AtLimit_BlocksUntilWindowElapses()
    {
        // Use a short window so the test doesn't wait a full minute. The limiter honors the
        // injected window even though production uses 60s.
        var window = TimeSpan.FromMilliseconds(250);
        var limiter = new ByokRateLimiter(TimeProvider.System, window);

        // Fill the window: 1 allowed per window.
        await limiter.AcquireSendSlotAsync("m1", limit: 1, CancellationToken.None);

        // Second acquire must block until the window elapses (250ms). Give it a generous grace
        // period so the test is not flaky on a slow CI runner.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await limiter.AcquireSendSlotAsync("m1", limit: 1, cts.Token);

        Assert.True(limiter.IsRateLimited("m1"));
    }

    [Fact]
    public async Task Acquire_CancellationPropagatesWhileBlocked()
    {
        // Long window so a blocked acquire stays blocked until we cancel.
        var window = TimeSpan.FromMinutes(1);
        var limiter = new ByokRateLimiter(TimeProvider.System, window);

        // Fill the window.
        await limiter.AcquireSendSlotAsync("m1", limit: 1, CancellationToken.None);

        // Second acquire blocks indefinitely because the window never elapses.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var acquireTask = limiter.AcquireSendSlotAsync("m1", limit: 1, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquireTask);
    }

    [Fact]
    public void Clear_RemovesLimiterState()
    {
        var limiter = new ByokRateLimiter();
        // Without an actual acquire, IsRateLimited is false; clear is a no-op-safe call.
        limiter.Clear("never-existed");
        Assert.False(limiter.IsRateLimited("never-existed"));
    }

    [Fact]
    public async Task Acquire_DifferentModelsHaveIndependentWindows()
    {
        // Two models each with limit=1 should not block each other.
        var limiter = new ByokRateLimiter();
        await limiter.AcquireSendSlotAsync("m1", limit: 1, CancellationToken.None);
        // m2 should still have its own slot available.
        await limiter.AcquireSendSlotAsync("m2", limit: 1, CancellationToken.None);

        Assert.True(limiter.IsRateLimited("m1"));
        Assert.True(limiter.IsRateLimited("m2"));
    }

    [Fact]
    public async Task Acquire_SharedLimiterInstance_EnforcesCombinedRpmAcrossConsumers()
    {
        // Regression guard for the PR #14 review point: the RPM limiter must be process-wide
        // (shared across chat surfaces), keyed by BYOK model id, so two surfaces using the SAME
        // model share a single sliding window and together respect the configured RPM — they
        // cannot each open their own independent window and double the effective rate.
        //
        // Setup: a single limiter instance (what ChatSessionStore injects into every surface),
        // limit=2 per 60s window for model "m1". Two logical consumers (consumer A and consumer B,
        // representing two ChatViewModel surfaces) each acquire against the same model id.
        var window = TimeSpan.FromMilliseconds(250);
        var sharedLimiter = new ByokRateLimiter(TimeProvider.System, window);

        // Consumer A takes the first slot, consumer B takes the second. Together they have now
        // exhausted the shared model's RPM budget for this window — a per-surface limiter would
        // still have 1 slot left for each consumer.
        await sharedLimiter.AcquireSendSlotAsync("m1", limit: 2, CancellationToken.None);
        await sharedLimiter.AcquireSendSlotAsync("m1", limit: 2, CancellationToken.None);

        // A third acquire from either consumer must BLOCK: the shared window is full. We assert
        // this by observing a cancellation (rather than waiting out the window), which proves the
        // acquire did not return immediately the way an independent per-consumer window would.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sharedLimiter.AcquireSendSlotAsync("m1", limit: 2, cts.Token));

        Assert.True(sharedLimiter.IsRateLimited("m1"));

        // After the window elapses, the third acquire succeeds — proving it was the shared window
        // (not a permanent block) that held it back.
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await sharedLimiter.AcquireSendSlotAsync("m1", limit: 2, cts2.Token);
    }

    [Fact]
    public async Task Acquire_LimitChangeTakesEffectOnNextAcquire()
    {
        // Raising the limit on an already-throttled model should let the next acquire through
        // immediately (the limiter re-reads limit on every call, not just on first use).
        var limiter = new ByokRateLimiter();
        await limiter.AcquireSendSlotAsync("m1", limit: 1, CancellationToken.None);

        // Now raise to 10 — the next acquire should not block.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await limiter.AcquireSendSlotAsync("m1", limit: 10, cts.Token);
        Assert.True(limiter.IsRateLimited("m1"));
    }
}
