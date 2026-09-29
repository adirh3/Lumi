using Avalonia.Threading;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class AppShutdownTests
{
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
