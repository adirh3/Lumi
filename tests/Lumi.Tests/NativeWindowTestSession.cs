#if WINDOWS
using System.Reflection;
using Avalonia;
using Avalonia.Threading;
using Xunit;

namespace Lumi.Tests;

[CollectionDefinition(nameof(NativeWindowTestSession), DisableParallelization = true)]
public sealed class NativeWindowTestCollection : ICollectionFixture<NativeWindowTestSession>;

// Win32's message-only window belongs to one STA for the life of the process.
// Share that dispatcher between native tests; do not start a new backend per test.
public sealed class NativeWindowTestSession : IAsyncLifetime
{
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task InitializeAsync()
    {
        if (!Environment.UserInteractive)
        {
            _stopped.SetResult();
            return Task.CompletedTask;
        }
        var thread = new Thread(Run) { IsBackground = true, Name = "Lumi native window tests STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return _ready.Task;
    }

    private void Run()
    {
        try
        {
            var reset = typeof(Dispatcher).GetMethod("ResetForUnitTests", BindingFlags.Static | BindingFlags.NonPublic)!;
            var resetApp = typeof(AppBuilder).GetMethod("ResetSetupForUnitTests", BindingFlags.Static | BindingFlags.NonPublic)!;
            var enterScope = typeof(AvaloniaLocator).GetMethod("EnterScope",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            using ((IDisposable)enterScope.Invoke(null, null)!)
            {
                try
                {
                    reset.Invoke(null, null);
                    AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
                    var dispatcher = Dispatcher.UIThread;
                    _ready.SetResult(dispatcher);
                    dispatcher.MainLoop(CancellationToken.None);
                }
                finally
                {
                    reset.Invoke(null, null);
                    resetApp.Invoke(null, null);
                }
            }
            _stopped.SetResult();
        }
        catch (Exception e)
        {
            _ready.TrySetException(e);
            _stopped.SetException(e);
        }
    }

    internal async Task RunAsync(Func<Task> body, CancellationToken cancellationToken)
    {
        var dispatcher = await _ready.Task.WaitAsync(cancellationToken);
        await dispatcher.InvokeAsync(body).WaitAsync(cancellationToken);
    }

    public Task DisposeAsync()
    {
        if (_ready.Task.IsCompletedSuccessfully && !_stopped.Task.IsCompleted)
            _ready.Task.Result.BeginInvokeShutdown(DispatcherPriority.Send);
        return _stopped.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
#endif
