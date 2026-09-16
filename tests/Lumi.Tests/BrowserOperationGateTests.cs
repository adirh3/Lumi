using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class BrowserOperationGateTests
{
    [Fact]
    public async Task CompleteBatchBlocksConcurrentSameTabOperation()
    {
        var gate = new BrowserOperationGate();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var batch = gate.RunAsync(async () =>
        {
            events.Add("first step");
            await release.Task;
            events.Add("last step");
            events.Add("snapshot");
            return "batch complete";
        });
        var competing = gate.RunAsync(() =>
        {
            events.Add("competing action");
            return Task.FromResult("other complete");
        });

        Assert.False(competing.IsCompleted);
        Assert.Equal(new[] { "first step" }, events);
        release.SetResult();
        await Task.WhenAll(batch, competing).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "first step", "last step", "snapshot", "competing action" }, events);
    }

    [Fact]
    public async Task SeparateTabsDoNotShareOperationLock()
    {
        var first = new BrowserOperationGate();
        var second = new BrowserOperationGate();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = first.RunAsync(async () => { await release.Task; return "first"; });
        Assert.Equal("second", await second.RunAsync(() => Task.FromResult("second")));
        Assert.False(blocked.IsCompleted);
        release.SetResult();
        await blocked;
    }

    [Fact]
    public async Task FailureReleasesOperationLock()
    {
        var gate = new BrowserOperationGate();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.RunAsync(() => throw new InvalidOperationException("expected")));
        Assert.Equal("next", await gate.RunAsync(() => Task.FromResult("next")));
    }

    [Fact]
    public async Task EscapedAsyncContextCannotBypassGate()
    {
        var gate = new BrowserOperationGate();
        var startEscaped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var escapedAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var escapedRan = false;
        Task<string>? escaped = null;
        await gate.RunAsync(() =>
        {
            escaped = Task.Run(async () =>
            {
                await startEscaped.Task;
                escapedAttempt.SetResult();
                return await gate.RunAsync(() => { escapedRan = true; return Task.FromResult("escaped"); });
            });
            return Task.FromResult("original complete");
        });
        var blocker = gate.RunAsync(async () => { await releaseBlocker.Task; return "blocker"; });
        startEscaped.SetResult();
        await escapedAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(escapedRan);
        releaseBlocker.SetResult();
        await Task.WhenAll(blocker, escaped!).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(escapedRan);
    }
}
