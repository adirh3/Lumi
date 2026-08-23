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

    // NOTE: Avalonia's HeadlessUnitTestSession.Dispatch(Func<Task>) awaits the dispatched body to
    // completion but does NOT surface exceptions it faults with, so an assertion failure inside an
    // `async () => { ... }` body is silently swallowed and the test still passes. Tests that need to
    // assert on UI-thread state should capture the results inside the body and assert OUTSIDE this
    // call (see StrataCollapsibleReparentClickTests). See the suite-wide note in the task summary.
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
