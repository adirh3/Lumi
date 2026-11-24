using Avalonia.Threading;

namespace Lumi.Mobile.Browser;

internal static class BrowserUiThread
{
    internal static async Task InvokeAsync(Action action)
    {
        var dispatcher = Dispatcher.UIThread;
        if (!dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(action);
            return;
        }

        // In the ordinary build, native edits must arrive before the following synchronous DOM key or click.
        using var context = AvaloniaSynchronizationContext.Ensure(dispatcher, DispatcherPriority.Normal);
        action();
    }

    internal static async Task<T> InvokeAsync<T>(Func<T> action)
    {
        var dispatcher = Dispatcher.UIThread;
        if (!dispatcher.CheckAccess())
            return await dispatcher.InvokeAsync(action);

        using var context = AvaloniaSynchronizationContext.Ensure(dispatcher, DispatcherPriority.Normal);
        return action();
    }
}
