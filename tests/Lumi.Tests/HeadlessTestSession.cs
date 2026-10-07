using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless;

namespace Lumi.Tests;

internal sealed class HeadlessTestSession : IDisposable
{
    private readonly HeadlessUnitTestSession _inner;
    private readonly bool _ownsSession;

    private HeadlessTestSession(HeadlessUnitTestSession inner, bool ownsSession = true)
    {
        _inner = inner;
        _ownsSession = ownsSession;
    }

    public static HeadlessTestSession Start()
        => Start(typeof(HeadlessTestApp));

    public static HeadlessTestSession Start(Type appType)
    {
        ArgumentNullException.ThrowIfNull(appType);

        // Reuse the UI thread for cached geometries while retaining PerTest application isolation.
        if (appType == typeof(HeadlessTestApp))
            return new HeadlessTestSession(
                HeadlessUnitTestSession.GetOrStartForAssembly(appType.Assembly),
                ownsSession: false);

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
        if (!_ownsSession)
            return;

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
