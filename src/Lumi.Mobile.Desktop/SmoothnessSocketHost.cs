#if DEBUG || LUMI_MOBILE_SMOOTHNESS
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Threading;
using Lumi.Mobile.Views;

namespace Lumi.Mobile.Smoothness;

/// <summary>Loopback-only control channel for the isolated benchmark; Android uses adb forwarding.</summary>
internal sealed class SmoothnessSocketHost : IDisposable
{
    private readonly TcpListener _listener;
    private readonly MobileSmoothnessSession _session;
    private readonly CancellationTokenSource _lifetime = new();

    internal SmoothnessSocketHost(
        MobileShellView view, int port, Func<double>? nativeClock = null, bool softKeyboardExpected = false,
        Func<NativeComposerEditorHost, Avalonia.Point, bool>? nativeEditorHitTest = null)
    {
        _session = new MobileSmoothnessSession(view, nativeClock, softKeyboardExpected, nativeEditorHitTest);
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _ = RunAsync();
    }

    internal void ObserveHostTouch(double x, double y, bool pressed) => _session.ObserveHostTouch(x, y, pressed);
    internal void ObserveKeyboardRequest() => _session.ObserveKeyboardRequest();

    private async Task RunAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                try
                {
                    await HandleClientAsync(client);
                }
                catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
                {
                    Trace.TraceWarning("[Mobile] Smoothness request timed out.");
                }
                catch (Exception error) when (error is IOException or InvalidDataException)
                {
                    Trace.TraceWarning($"[Mobile] Smoothness request disconnected or was invalid: {error.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (SocketException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            Trace.TraceError($"[Mobile] Smoothness control channel stopped: {error}");
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await using var stream = client.GetStream();
        var bytes = new byte[4096];
        var length = 0;
        while (length < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(length, 1), deadline.Token);
            if (count == 0)
                throw new InvalidDataException("Incomplete benchmark request.");
            if (bytes[length++] == (byte)'\n')
                break;
        }
        if (length == bytes.Length && bytes[length - 1] != (byte)'\n')
            throw new InvalidDataException("Benchmark request exceeds 4 KiB.");
        var request = Encoding.UTF8.GetString(bytes, 0, length);
        var result = await Dispatcher.UIThread.InvokeAsync(() => _session.CommandAsync(request));
        await stream.WriteAsync(Encoding.UTF8.GetBytes(result + "\n"), deadline.Token);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _listener.Stop();
        _session.Dispose();
        _lifetime.Dispose();
    }
}
#endif
