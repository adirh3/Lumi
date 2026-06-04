using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless;

namespace Lumi.Tests;

internal sealed class HeadlessTestSession : IDisposable
{
    private readonly HeadlessUnitTestSession _inner;

    private HeadlessTestSession(HeadlessUnitTestSession inner)
    {
        _inner = inner;
    }

    public static HeadlessTestSession Start()
    {
        return new HeadlessTestSession(HeadlessUnitTestSession.StartNew(
            typeof(HeadlessTestApp),
            AvaloniaTestIsolationLevel.PerTest));
    }

    public Task Dispatch(Action action, CancellationToken cancellationToken)
    {
        return _inner.Dispatch(action, cancellationToken);
    }

    public Task Dispatch(Func<Task> action, CancellationToken cancellationToken)
    {
        return _inner.Dispatch(action, cancellationToken);
    }

    public void Dispose()
    {
        try
        {
            _inner.Dispose();
        }
        catch (NullReferenceException)
        {
            // Avalonia.Headless can throw during PerTest teardown after
            // the test body has completed. Keep assertions meaningful while
            // avoiding random suite failures from the external harness cleanup.
        }
    }
}
