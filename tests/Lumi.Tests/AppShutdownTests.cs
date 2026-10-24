using Avalonia.Threading;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class AppShutdownTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void OnlyApplicationRequestsAndNativeMacQuitMayBeDeferred(
        bool applicationInitiated, bool isMacOS, bool expected)
    {
        Assert.Equal(expected, App.ShouldDeferShutdown(applicationInitiated, isMacOS));
    }

    [Fact]
    public async Task OsSessionCleanupDoesNotWaitIndefinitelyForTheDispatcher()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var finishedInTime = App.RunOsShutdownCleanup(async () =>
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
                completed.SetResult();
            }, TimeSpan.FromMilliseconds(50));

            Assert.False(finishedInTime);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }, CancellationToken.None);
    }

    [Fact]
    public void OsSessionCleanupCanCompleteWithinItsBudget()
    {
        var cleaned = false;
        Assert.True(App.RunOsShutdownCleanup(
            () => { cleaned = true; return Task.CompletedTask; }, TimeSpan.FromSeconds(2)));
        Assert.True(cleaned);
    }

    [Fact]
    public async Task CleanupDefersUntilTheNativeShutdownCallbackCanReturn()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var uiCleanupStarted = false;
            var servicesCleanedUp = false;
            var cleanup = App.RunShutdownCleanupAsync(
                () =>
                {
                    Assert.True(Dispatcher.UIThread.CheckAccess());
                    uiCleanupStarted = true;
                },
                () =>
                {
                    Assert.False(Dispatcher.UIThread.CheckAccess());
                    Assert.True(uiCleanupStarted);
                    servicesCleanedUp = true;
                    return Task.CompletedTask;
                });

            Assert.False(uiCleanupStarted);
            Assert.False(cleanup.IsCompleted);
            await cleanup;
            Assert.True(servicesCleanedUp);
            Assert.True(Dispatcher.UIThread.CheckAccess());
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CleanupAllowsPendingServicesToFinishOnTheDispatcher()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var dispatcherWorkCompleted = false;
            await App.RunShutdownCleanupAsync(
                static () => { },
                async () =>
                {
                    await Dispatcher.UIThread.InvokeAsync(
                        () => dispatcherWorkCompleted = true,
                        DispatcherPriority.Normal,
                        timeout.Token);
                });

            Assert.True(dispatcherWorkCompleted);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task UiCleanupFailureStillSavesAndDisposesServices()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var failure = new InvalidOperationException("UI cleanup failed.");
            var servicesCleanedUp = false;
            var observed = await Assert.ThrowsAsync<InvalidOperationException>(
                () => App.RunShutdownCleanupAsync(
                    () => throw failure,
                    () =>
                    {
                        servicesCleanedUp = true;
                        return Task.CompletedTask;
                    }));

            Assert.Same(failure, observed);
            Assert.True(servicesCleanedUp);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ServiceCleanupFailureIsNotSwallowed()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var failure = new InvalidOperationException("Service cleanup failed.");
            var observed = await Assert.ThrowsAsync<InvalidOperationException>(
                () => App.RunShutdownCleanupAsync(
                    static () => { },
                    () => Task.FromException(failure)));

            Assert.Same(failure, observed);
        }, CancellationToken.None);
    }
}
