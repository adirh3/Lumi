using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public void QueuedActivation_IsDeliveredWhenHandlerRegisters()
    {
        using var coordinator = SingleInstanceCoordinator.CreateForScope(
            $"test-{Guid.NewGuid():N}");
        var request = new AppActivationRequest(Guid.NewGuid());
        AppActivationRequest? received = null;

        Assert.True(coordinator.IsPrimaryInstance);
        coordinator.PublishActivation(request);
        coordinator.SetActivationHandler(value => received = value);

        Assert.Equal(request, received);
    }

    [Fact]
    public async Task SecondaryActivation_IsRedirectedToPrimaryInstance()
    {
        var scope = $"test-{Guid.NewGuid():N}";
        var primaryReady = new TaskCompletionSource<SingleInstanceCoordinator>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePrimary = new ManualResetEventSlim();
        Exception? primaryError = null;

        var primaryThread = new Thread(() =>
        {
            try
            {
                using var primary = SingleInstanceCoordinator.CreateForScope(scope);
                primaryReady.SetResult(primary);
                releasePrimary.Wait(TimeSpan.FromSeconds(15));
            }
            catch (Exception ex)
            {
                primaryError = ex;
                primaryReady.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstanceCoordinatorTests.Primary"
        };
        primaryThread.Start();

        var primary = await primaryReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var received = new TaskCompletionSource<AppActivationRequest>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            primary.SetActivationHandler(request => received.TrySetResult(request));

            using var secondary = SingleInstanceCoordinator.CreateForScope(scope);
            var expected = new AppActivationRequest(Guid.NewGuid());

            Assert.True(primary.IsPrimaryInstance);
            Assert.False(secondary.IsPrimaryInstance);
            Assert.Equal(
                ActivationRedirectResult.Accepted,
                await secondary.RedirectActivationAsync(
                expected,
                TimeSpan.FromSeconds(5)));
            Assert.Equal(
                expected,
                await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releasePrimary.Set();
            Assert.True(primaryThread.Join(TimeSpan.FromSeconds(5)));
        }

        Assert.Null(primaryError);
    }

    [Fact]
    public async Task StoppingPrimary_RejectsActivationAndAllowsTakeover()
    {
        var scope = $"test-{Guid.NewGuid():N}";
        var primaryReady = new TaskCompletionSource<SingleInstanceCoordinator>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePrimary = new ManualResetEventSlim();

        var primaryThread = new Thread(() =>
        {
            using var primary = SingleInstanceCoordinator.CreateForScope(scope);
            primaryReady.SetResult(primary);
            releasePrimary.Wait(TimeSpan.FromSeconds(15));
        })
        {
            IsBackground = true,
            Name = "SingleInstanceCoordinatorTests.ShutdownPrimary"
        };
        primaryThread.Start();

        var primary = await primaryReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var secondary = SingleInstanceCoordinator.CreateForScope(scope);

        try
        {
            primary.StopAcceptingActivations();

            Assert.Equal(
                ActivationRedirectResult.PrimaryShuttingDown,
                await secondary.RedirectActivationAsync(
                    new AppActivationRequest(null),
                    TimeSpan.FromSeconds(5)));

            releasePrimary.Set();
            Assert.True(secondary.TryBecomePrimary(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releasePrimary.Set();
            Assert.True(primaryThread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task RestartingPrimary_RejectsActivationWithoutOfferingTakeover()
    {
        var scope = $"test-{Guid.NewGuid():N}";
        var primaryReady = new TaskCompletionSource<SingleInstanceCoordinator>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePrimary = new ManualResetEventSlim();

        var primaryThread = new Thread(() =>
        {
            using var primary = SingleInstanceCoordinator.CreateForScope(scope);
            primaryReady.SetResult(primary);
            releasePrimary.Wait(TimeSpan.FromSeconds(15));
        })
        {
            IsBackground = true,
            Name = "SingleInstanceCoordinatorTests.RestartingPrimary"
        };
        primaryThread.Start();

        var primary = await primaryReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var secondary = SingleInstanceCoordinator.CreateForScope(scope);

        try
        {
            primary.StopAcceptingActivations(restartExpected: true);

            Assert.Equal(
                ActivationRedirectResult.PrimaryRestarting,
                await secondary.RedirectActivationAsync(
                    new AppActivationRequest(null),
                    TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releasePrimary.Set();
            Assert.True(primaryThread.Join(TimeSpan.FromSeconds(5)));
        }
    }
}
