using Lumi.Mobile.Services;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class RemoteConnectionSupervisorTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void RetryPausesDoubleFromOneSecondUpToAMinute()
    {
        using var supervisor = Create(new ManualTimeProvider());

        var pauses = Enumerable.Range(1, 9)
            .Select(failures => supervisor.NextDelay(failures)!.Value.TotalSeconds)
            .ToArray();

        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60, 60], pauses);
        Assert.False(supervisor.IsOffline);
    }

    [Theory]
    [InlineData(0.0, 6.4)]
    [InlineData(0.5, 8.0)]
    [InlineData(0.75, 8.8)]
    public void JitterSpreadsEachPauseByAtMostAFifth(double random, double expectedSeconds)
    {
        using var supervisor = Create(new ManualTimeProvider(), random: random);

        Assert.Equal(expectedSeconds, supervisor.NextDelay(4)!.Value.TotalSeconds, precision: 6);
    }

    [Fact]
    public void AnUnreachablePcIsGivenUpOnAfterTheConfiguredTime()
    {
        var time = new ManualTimeProvider();
        using var supervisor = Create(time, giveUpAfter: TimeSpan.FromMinutes(3));
        var offlineChanges = 0;
        supervisor.OfflineChanged += () => offlineChanges++;

        Assert.NotNull(supervisor.NextDelay(1));
        time.Freeze(TimeSpan.FromMinutes(2));
        Assert.NotNull(supervisor.NextDelay(5));
        time.Freeze(TimeSpan.FromMinutes(1));

        Assert.Null(supervisor.NextDelay(6));
        Assert.Null(supervisor.NextDelay(7));
        Assert.True(supervisor.IsOffline);
        Assert.Equal(1, offlineChanges);
    }

    [Fact]
    public async Task AnOfflineWaitEndsOnlyWhenRetried()
    {
        var time = new ManualTimeProvider();
        using var supervisor = Create(time, giveUpAfter: TimeSpan.Zero);
        Assert.Null(supervisor.NextDelay(1));

        var wait = supervisor.WaitAsync(null, CancellationToken.None);
        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);
        Assert.Equal(0, time.PendingTimerCount);

        supervisor.RetryNow();
        await wait.WaitAsync(WaitTimeout);
        Assert.False(supervisor.IsOffline);
    }

    [Fact]
    public async Task ARetryPauseEndsAtItsTimeOrWhenRetriedEarly()
    {
        var time = new ManualTimeProvider();
        using var supervisor = Create(time);

        var timed = supervisor.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        Assert.False(timed.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await timed.WaitAsync(WaitTimeout);

        var early = supervisor.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        supervisor.RetryNow();
        await early.WaitAsync(WaitTimeout);
        Assert.Equal(0, time.PendingTimerCount);
    }

    [Fact]
    public async Task CancellingAWaitStopsIt()
    {
        using var supervisor = Create(new ManualTimeProvider());
        using var cancellation = new CancellationTokenSource();

        var wait = supervisor.WaitAsync(null, cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(WaitTimeout));
    }

    [Fact]
    public void ConnectingStartsAFreshFailureStreak()
    {
        var time = new ManualTimeProvider();
        using var supervisor = Create(time, giveUpAfter: TimeSpan.FromMinutes(3));
        var offlineChanges = 0;
        supervisor.OfflineChanged += () => offlineChanges++;

        supervisor.NextDelay(1);
        time.Freeze(TimeSpan.FromMinutes(3));
        Assert.Null(supervisor.NextDelay(2));

        supervisor.OnConnected();
        Assert.False(supervisor.IsOffline);
        time.Freeze(TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromSeconds(1), supervisor.NextDelay(1));
        Assert.Equal(2, offlineChanges);
    }

    [Fact]
    public async Task NoNetworkWaitsOfflineUntilTheNetworkReturns()
    {
        var time = new ManualTimeProvider();
        var network = new PushedNetworkMonitor();
        using var supervisor = Create(time, network: network);
        var restored = 0;
        supervisor.NetworkRestored += () => restored++;

        network.Report(false);
        Assert.Equal(0, restored);
        Assert.Null(supervisor.NextDelay(1));
        Assert.True(supervisor.IsOffline);
        var wait = supervisor.WaitAsync(null, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);

        network.Report(true);
        await wait.WaitAsync(WaitTimeout);
        Assert.False(supervisor.IsOffline);
        Assert.Equal(1, restored);
    }

    [Fact]
    public async Task MovingToAnotherNetworkEndsARetryPause()
    {
        var time = new ManualTimeProvider();
        var network = new PushedNetworkMonitor();
        using var supervisor = Create(time, network: network);

        var wait = supervisor.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        network.Report(true);

        await wait.WaitAsync(WaitTimeout);
    }

    [Fact]
    public void ReturningInsideTheGraceKeepsTheStreamAndCancelsExpiry()
    {
        var time = new ManualTimeProvider();
        using var supervisor = Create(time);
        var expired = 0;

        supervisor.BeginBackgroundGrace(() => { expired++; return Task.CompletedTask; });
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.True(supervisor.IsInBackgroundGrace);

        Assert.True(supervisor.EndBackgroundGrace());
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, expired);
        Assert.False(supervisor.IsInBackgroundGrace);
    }

    [Fact]
    public void TheGraceExpiresOnceAfterItsWindow()
    {
        var time = new ManualTimeProvider();
        using var supervisor = Create(time);
        var expired = 0;

        supervisor.BeginBackgroundGrace(() => { expired++; return Task.CompletedTask; });
        time.Advance(TimeSpan.FromSeconds(60));
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(1, expired);
        Assert.False(supervisor.IsInBackgroundGrace);
        Assert.False(supervisor.EndBackgroundGrace());
    }

    [Fact]
    public void AProcessFrozenPastTheGraceDoesNotResumeItsStream()
    {
        var time = new ManualTimeProvider();
        using var supervisor = Create(time);
        var expired = 0;

        supervisor.BeginBackgroundGrace(() => { expired++; return Task.CompletedTask; });
        time.Freeze(TimeSpan.FromSeconds(61));

        Assert.False(supervisor.EndBackgroundGrace());
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, expired);
    }

    [Fact]
    public void LeavingAgainRestartsTheGraceWithoutTheEarlierExpiry()
    {
        var time = new ManualTimeProvider();
        using var supervisor = Create(time);
        var first = 0;
        var second = 0;

        supervisor.BeginBackgroundGrace(() => { first++; return Task.CompletedTask; });
        time.Advance(TimeSpan.FromSeconds(30));
        supervisor.BeginBackgroundGrace(() => { second++; return Task.CompletedTask; });
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal((0, 0), (first, second));

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal((0, 1), (first, second));
    }

    [Fact]
    public void ADisposedSupervisorNoLongerReactsToTheNetworkOrTheGrace()
    {
        var time = new ManualTimeProvider();
        var network = new PushedNetworkMonitor();
        var supervisor = Create(time, network: network);
        var restored = 0;
        var expired = 0;
        supervisor.NetworkRestored += () => restored++;
        supervisor.BeginBackgroundGrace(() => { expired++; return Task.CompletedTask; });

        supervisor.Dispose();
        network.Report(true);
        supervisor.BeginBackgroundGrace(() => { expired++; return Task.CompletedTask; });
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0, restored);
        Assert.Equal(0, expired);
        Assert.False(supervisor.IsInBackgroundGrace);
    }

    [Fact]
    public void TheStandaloneClientKeepsItsShortRetryPacing()
    {
        var pauses = Enumerable.Range(1, 6)
            .Select(failures => FixedRetryPolicy.Instance.NextDelay(failures)!.Value.TotalSeconds)
            .ToArray();

        Assert.Equal([1, 2, 4, 8, 10, 10], pauses);
    }

    private static RemoteConnectionSupervisor Create(
        ManualTimeProvider time,
        IRemoteNetworkMonitor? network = null,
        TimeSpan? giveUpAfter = null,
        double random = 0.5) =>
        new(new RemoteConnectionOptions
        {
            Time = time,
            Network = network ?? StaticNetworkMonitor.Available,
            Random = () => random,
            GiveUpAfter = giveUpAfter ?? RemoteConnectionOptions.Default.GiveUpAfter
        });
}
