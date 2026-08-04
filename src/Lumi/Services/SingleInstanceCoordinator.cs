using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Lumi.Services;

internal readonly record struct AppActivationRequest(Guid? ChatId);

internal enum ActivationRedirectResult
{
    Accepted,
    PrimaryUnavailable,
    PrimaryShuttingDown,
    PrimaryRestarting
}

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const byte ProtocolVersion = 1;
    private const byte ActivateMainWindowMessage = 0;
    private const byte ActivateChatMessage = 1;
    private const byte AcceptedResponse = 1;
    private const byte PrimaryShuttingDownResponse = 2;
    private const byte PrimaryRestartingResponse = 3;

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _listenerCts = new();
    private readonly object _activationSync = new();
    private readonly Queue<AppActivationRequest> _pendingActivations = [];
    private Task? _listenerTask;
    private Action<AppActivationRequest>? _activationHandler;
    private bool _acceptingActivations = true;
    private byte _rejectedActivationResponse = PrimaryShuttingDownResponse;
    private bool _ownsMutex;
    private volatile bool _disposed;

    private SingleInstanceCoordinator(string mutexName, string pipeName)
    {
        _mutex = new Mutex(initiallyOwned: false, mutexName);
        _pipeName = pipeName;

        if (TryAcquireMutex(TimeSpan.Zero))
            StartListener();
    }

    internal bool IsPrimaryInstance => _ownsMutex;

    internal static SingleInstanceCoordinator Create()
        => CreateForScope(ResolveInstanceScope());

    internal static SingleInstanceCoordinator CreateForScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..32];
        var mutexName = OperatingSystem.IsWindows()
            ? $@"Global\Lumi.SingleInstance.{hash}"
            : $"Lumi.SingleInstance.{hash}";
        var pipeName = $"Lumi.SingleInstance.{hash}";
        return new SingleInstanceCoordinator(mutexName, pipeName);
    }

    internal bool TryBecomePrimary(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_ownsMutex)
            return true;

        if (!TryAcquireMutex(timeout))
            return false;

        StartListener();
        return true;
    }

    internal async Task<ActivationRedirectResult> RedirectActivationAsync(
        AppActivationRequest request,
        TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_ownsMutex)
        {
            return PublishActivation(request)
                ? ActivationRedirectResult.Accepted
                : ActivationRedirectResult.PrimaryShuttingDown;
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var client = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await client.ConnectAsync(timeoutCts.Token);

            var processIdBytes = new byte[sizeof(int)];
            await client.ReadExactlyAsync(processIdBytes, timeoutCts.Token);
            TryGrantForegroundPermission(BitConverter.ToInt32(processIdBytes));

            var payload = CreatePayload(request);
            await client.WriteAsync(payload, timeoutCts.Token);
            await client.FlushAsync(timeoutCts.Token);

            var response = new byte[1];
            await client.ReadExactlyAsync(response, timeoutCts.Token);
            return response[0] switch
            {
                AcceptedResponse => ActivationRedirectResult.Accepted,
                PrimaryShuttingDownResponse => ActivationRedirectResult.PrimaryShuttingDown,
                PrimaryRestartingResponse => ActivationRedirectResult.PrimaryRestarting,
                _ => ActivationRedirectResult.PrimaryUnavailable
            };
        }
        catch (OperationCanceledException)
        {
            Trace.TraceWarning("[SingleInstance] Timed out redirecting activation to the primary process.");
            return ActivationRedirectResult.PrimaryUnavailable;
        }
        catch (IOException ex)
        {
            Trace.TraceWarning($"[SingleInstance] Activation redirect failed: {ex.Message}");
            return ActivationRedirectResult.PrimaryUnavailable;
        }
        catch (UnauthorizedAccessException ex)
        {
            Trace.TraceWarning($"[SingleInstance] Activation redirect was denied: {ex.Message}");
            return ActivationRedirectResult.PrimaryUnavailable;
        }
    }

    internal void StopAcceptingActivations(bool restartExpected = false)
    {
        lock (_activationSync)
        {
            _acceptingActivations = false;
            if (restartExpected)
                _rejectedActivationResponse = PrimaryRestartingResponse;
        }
    }

    internal void SetActivationHandler(Action<AppActivationRequest> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);

        List<AppActivationRequest> pending;
        lock (_activationSync)
        {
            _activationHandler = handler;
            pending = new List<AppActivationRequest>(_pendingActivations);
            _pendingActivations.Clear();
        }

        foreach (var request in pending)
            DeliverActivation(handler, request);
    }

    internal bool PublishActivation(AppActivationRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Action<AppActivationRequest>? handler;
        lock (_activationSync)
        {
            if (!_acceptingActivations)
                return false;

            handler = _activationHandler;
            if (handler is null)
            {
                _pendingActivations.Enqueue(request);
                return true;
            }
        }

        return DeliverActivation(handler, request);
    }

    private static string ResolveInstanceScope()
    {
        var appDataRoot = Environment.GetEnvironmentVariable("LUMI_APPDATA_DIR");
        if (string.IsNullOrWhiteSpace(appDataRoot))
            appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var appDirectory = Path.GetFullPath(Path.Combine(
            Environment.ExpandEnvironmentVariables(appDataRoot),
            "Lumi"));
        appDirectory = appDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        return OperatingSystem.IsWindows()
            ? appDirectory.ToUpperInvariant()
            : appDirectory;
    }

    private bool TryAcquireMutex(TimeSpan timeout)
    {
        try
        {
            _ownsMutex = _mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        return _ownsMutex;
    }

    private void StartListener()
    {
        if (_listenerTask is not null)
            return;

        _listenerTask = Task.Run(() => ListenAsync(_listenerCts.Token));
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(cancellationToken);
                await HandleConnectionAsync(server, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (EndOfStreamException ex)
            {
                Trace.TraceWarning($"[SingleInstance] Activation client disconnected early: {ex.Message}");
            }
            catch (IOException ex)
            {
                Trace.TraceWarning($"[SingleInstance] Activation pipe failed: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Trace.TraceError($"[SingleInstance] Cannot create activation pipe: {ex.Message}");
                break;
            }
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken)
    {
        var processIdBytes = BitConverter.GetBytes(Environment.ProcessId);
        await server.WriteAsync(processIdBytes, cancellationToken);
        await server.FlushAsync(cancellationToken);

        var (isValid, request) = await ReadActivationAsync(server, cancellationToken);
        var response = (byte)0;
        if (isValid)
        {
            response = PublishActivation(request)
                ? AcceptedResponse
                : GetRejectedActivationResponse();
        }

        await server.WriteAsync(
            new[] { response },
            cancellationToken);
        await server.FlushAsync(cancellationToken);
    }

    private byte GetRejectedActivationResponse()
    {
        lock (_activationSync)
            return _rejectedActivationResponse;
    }

    private static byte[] CreatePayload(AppActivationRequest request)
    {
        if (request.ChatId is not Guid chatId)
            return [ProtocolVersion, ActivateMainWindowMessage];

        var payload = new byte[2 + 16];
        payload[0] = ProtocolVersion;
        payload[1] = ActivateChatMessage;
        chatId.TryWriteBytes(payload.AsSpan(2));
        return payload;
    }

    private static async ValueTask<(bool IsValid, AppActivationRequest Request)> ReadActivationAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[2];
        await stream.ReadExactlyAsync(header, cancellationToken);

        if (header[0] != ProtocolVersion)
            return (false, default);

        if (header[1] == ActivateMainWindowMessage)
            return (true, new AppActivationRequest(null));

        if (header[1] != ActivateChatMessage)
            return (false, default);

        var chatIdBytes = new byte[16];
        await stream.ReadExactlyAsync(chatIdBytes, cancellationToken);
        return (true, new AppActivationRequest(new Guid(chatIdBytes)));
    }

    private static bool DeliverActivation(
        Action<AppActivationRequest> handler,
        AppActivationRequest request)
    {
        try
        {
            handler(request);
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"[SingleInstance] Activation handler failed: {ex}");
            return false;
        }
    }

    private static void TryGrantForegroundPermission(int primaryProcessId)
    {
        if (OperatingSystem.IsWindows())
            _ = AllowSetForegroundWindow(primaryProcessId);
    }

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _listenerCts.Cancel();

        if (_listenerTask is not null)
        {
            try
            {
                _listenerTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException ex)
            {
                Trace.TraceWarning($"[SingleInstance] Failed to release instance mutex: {ex.Message}");
            }
        }

        _mutex.Dispose();
        _listenerCts.Dispose();
    }
}
