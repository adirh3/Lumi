#if DEBUG
using System;
using System.Threading;
using System.Threading.Tasks;
using Lumi.UiPerf;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class UiResponsivenessProbeTests
{
    [Fact]
    public async Task ArmedSampleCapturesAnActionWithoutWaitingForThePeriodicInterval()
    {
        using var session = HeadlessTestSession.Start();
        using var probe = new UiResponsivenessProbe(intervalMs: 200);
        await session.Dispatch(async () =>
        {
            var sample = probe.SampleAsync();
            Assert.False(sample.IsCompleted);
            await sample.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, probe.SampleCount);
            Assert.Single(probe.LatenciesInWindow(0, probe.NowMs));
        }, CancellationToken.None);
    }
}
#endif
