using Avalonia;
using Avalonia.Media;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class HeadlessTestSessionTests
{
    [Fact]
    public async Task DefaultSessions_ReuseTheUiThreadButIsolateApplications()
    {
        Application? firstApplication = null;
        Geometry? geometry = null;
        var ownerThread = 0;
        using (var first = HeadlessTestSession.Start())
        {
            await first.Dispatch(() =>
            {
                firstApplication = Assert.IsType<HeadlessTestApp>(Application.Current);
                ownerThread = Environment.CurrentManagedThreadId;
                geometry = Geometry.Parse("M0,0 L10,0 L10,10 Z");
                Assert.True(geometry.Bounds.Width > 0);
            }, CancellationToken.None);
        }

        using var second = HeadlessTestSession.Start();
        await second.Dispatch(() =>
        {
            Assert.Equal(ownerThread, Environment.CurrentManagedThreadId);
            Assert.IsType<HeadlessTestApp>(Application.Current);
            Assert.NotSame(firstApplication, Application.Current);
            Assert.NotNull(geometry);
            Assert.True(geometry.Bounds.Width > 0);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AsyncDispatch_WaitsForTheBodyAndPropagatesItsException()
    {
        var ui = HeadlessTestSession.Start();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("Failure after an asynchronous continuation.");
        try
        {
            var dispatch = ui.Dispatch(async () =>
            {
                entered.SetResult();
                await resume.Task;
                throw expected;
            }, CancellationToken.None);

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(dispatch.IsCompleted);
            resume.SetResult();

            var actual = await Assert.ThrowsAsync<InvalidOperationException>(
                () => dispatch.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(expected, actual);
        }
        finally
        {
            resume.TrySetResult();
            await Task.Run(ui.Dispose);
        }
    }
}
