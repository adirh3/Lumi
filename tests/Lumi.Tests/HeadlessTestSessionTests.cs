using Xunit;

namespace Lumi.Tests;

public sealed class HeadlessTestSessionTests
{
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
