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
        => Start(typeof(HeadlessTestApp));

    public static HeadlessTestSession Start(Type appType)
    {
        ArgumentNullException.ThrowIfNull(appType);

        return new HeadlessTestSession(HeadlessUnitTestSession.StartNew(
            appType,
            AvaloniaTestIsolationLevel.PerTest));
    }

    public Task Dispatch(Action action, CancellationToken cancellationToken)
    {
        return _inner.Dispatch(action, cancellationToken);
    }

    // Avalonia has no Func<Task> overload: forwarding it directly returns Task<Task> as Task.
    // Select the async generic overload so the dispatcher pumps and propagates the whole operation.
    public Task Dispatch(Func<Task> action, CancellationToken cancellationToken)
    {
        return _inner.Dispatch<int>(async () =>
        {
            await action();
            return 0;
        }, cancellationToken);
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
