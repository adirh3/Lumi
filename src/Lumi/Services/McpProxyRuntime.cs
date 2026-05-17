using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot.SDK;

namespace Lumi.Services;

public sealed record McpProxyServerDefinition(
    string Key,
    string Name,
    McpStdioServerConfig Config);

public sealed class McpProxyRuntime : IAsyncDisposable
{
    public static McpProxyRuntime Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, McpProxyRegistration> _registrationsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, McpProxyRegistration> _registrationsByRoute = new(StringComparer.Ordinal);
    private readonly string _routeToken = Guid.NewGuid().ToString("N");

    private HttpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private int _port;
    private bool _disposed;

    public McpHttpServerConfig Register(McpProxyServerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Key))
            throw new ArgumentException("MCP proxy definition key cannot be empty.", nameof(definition));

        McpProxyRegistration? staleRegistration = null;
        McpProxyRegistration activeRegistration;
        int port;
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(McpProxyRuntime));

            EnsureListenerStartedLocked();

            var fingerprint = ComputeFingerprint(definition.Config);
            if (!_registrationsByKey.TryGetValue(definition.Key, out var currentRegistration)
                || !string.Equals(currentRegistration.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                if (currentRegistration is not null)
                {
                    _registrationsByRoute.Remove(currentRegistration.RouteId);
                    staleRegistration = currentRegistration;
                }

                currentRegistration = new McpProxyRegistration(
                    definition,
                    RouteId: Hash(definition.Key)[..24],
                    Fingerprint: fingerprint);
                _registrationsByKey[definition.Key] = currentRegistration;
                _registrationsByRoute[currentRegistration.RouteId] = currentRegistration;
            }

            activeRegistration = currentRegistration;
            port = _port;
        }

        if (staleRegistration is not null)
            RetireRegistrationInBackground(staleRegistration);

        return new McpHttpServerConfig
            {
                Url = $"http://127.0.0.1:{port}/mcp/{_routeToken}/{activeRegistration.RouteId}",
                Tools = definition.Config.Tools?.ToList() ?? ["*"],
                Timeout = definition.Config.Timeout
            };
    }

    public void RetireUserRegistrationsExcept(IEnumerable<Guid> activeLocalServerIds)
    {
        ArgumentNullException.ThrowIfNull(activeLocalServerIds);
        var retainedKeys = activeLocalServerIds
            .Select(id => "lumi:" + id)
            .ToHashSet(StringComparer.Ordinal);
        List<McpProxyRegistration> staleRegistrations = [];

        lock (_gate)
        {
            if (_disposed)
                return;

            foreach (var (key, registration) in _registrationsByKey.ToArray())
            {
                if (!key.StartsWith("lumi:", StringComparison.Ordinal) || retainedKeys.Contains(key))
                    continue;

                _registrationsByKey.Remove(key);
                _registrationsByRoute.Remove(registration.RouteId);
                staleRegistrations.Add(registration);
            }
        }

        foreach (var registration in staleRegistrations)
            RetireRegistrationInBackground(registration);
    }

    public async ValueTask DisposeAsync()
    {
        HttpListener? listener;
        CancellationTokenSource? cts;
        Task? listenerTask;
        List<McpProxyRegistration> registrations;

        lock (_gate)
        {
            _disposed = true;
            listener = _listener;
            cts = _listenerCts;
            listenerTask = _listenerTask;
            registrations = _registrationsByKey.Values.ToList();
            _registrationsByKey.Clear();
            _registrationsByRoute.Clear();
            _listener = null;
            _listenerCts = null;
            _listenerTask = null;
            _port = 0;
        }

        cts?.Cancel();
        if (listener is not null)
        {
            try { listener.Stop(); }
            catch { }
            listener.Close();
        }

        if (listenerTask is not null)
        {
            try { await listenerTask.ConfigureAwait(false); }
            catch { }
        }

        foreach (var registration in registrations)
            await registration.DisposeAsync().ConfigureAwait(false);

        cts?.Dispose();
    }

    private void EnsureListenerStartedLocked()
    {
        if (_listener is { IsListening: true })
            return;

        Exception? lastError = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var port = GetFreeLoopbackPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                var cts = new CancellationTokenSource();
                _listener = listener;
                _listenerCts = cts;
                _listenerTask = Task.Run(() => ListenAsync(listener, cts.Token));
                _port = port;
                return;
            }
            catch (Exception ex) when (ex is HttpListenerException or SocketException)
            {
                lastError = ex;
                listener.Close();
            }
        }

        throw new InvalidOperationException("Failed to start the local MCP proxy listener.", lastError);
    }

    private async Task ListenAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch when (cancellationToken.IsCancellationRequested || !listener.IsListening)
            {
                break;
            }

            _ = Task.Run(() => RunRequestHandlerAsync(context, cancellationToken));
        }
    }

    private async Task RunRequestHandlerAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            await HandleRequestAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("MCP proxy request handler failed: {0}", ex);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            await using var registrationLease = ResolveRegistrationLease(context.Request.Url?.AbsolutePath);
            if (registrationLease is null)
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.NotFound, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.MethodNotAllowed, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.MethodNotAllowed, cancellationToken).ConfigureAwait(false);
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
            var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.BadRequest, cancellationToken).ConfigureAwait(false);
                return;
            }

            var responseJson = await registrationLease.Registration.Connection.HandleClientMessageAsync(body, cancellationToken).ConfigureAwait(false);
            if (responseJson is null)
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.Accepted, cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(context.Response, responseJson, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            await WriteJsonAsync(context.Response, JsonRpc.Error(null, -32700, ex.Message), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context.Response, JsonRpc.Error(null, -32000, ex.Message), cancellationToken).ConfigureAwait(false);
        }
    }

    private McpProxyRegistrationLease? ResolveRegistrationLease(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3
            || !segments[0].Equals("mcp", StringComparison.Ordinal)
            || !segments[1].Equals(_routeToken, StringComparison.Ordinal))
        {
            return null;
        }

        lock (_gate)
        {
            return _registrationsByRoute.TryGetValue(segments[2], out var registration)
                ? registration.TryAcquireLease()
                : null;
        }
    }

    private static Task WriteStatusAsync(HttpListenerResponse response, HttpStatusCode statusCode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        response.StatusCode = (int)statusCode;
        response.Close();
        return Task.CompletedTask;
    }

    private static void RetireRegistrationInBackground(McpProxyRegistration registration)
    {
        var task = registration.RetireAsync().AsTask();
        if (task.IsCompletedSuccessfully)
            return;

        _ = task.ContinueWith(
            static t => Trace.TraceWarning("MCP proxy registration cleanup failed: {0}", t.Exception),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, string json, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/json";
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        response.Close();
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string ComputeFingerprint(McpStdioServerConfig config)
    {
        var builder = new StringBuilder();
        builder.AppendLine(config.Command);
        builder.AppendLine(config.Cwd);
        builder.AppendLine(config.Timeout?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var arg in config.Args ?? [])
            builder.Append("arg:").AppendLine(arg);
        foreach (var tool in config.Tools ?? [])
            builder.Append("tool:").AppendLine(tool);
        foreach (var pair in (config.Env ?? new Dictionary<string, string>()).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            builder.Append("env:").Append(pair.Key).Append('=').AppendLine(pair.Value);
        return Hash(builder.ToString());
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class McpProxyRegistration(McpProxyServerDefinition definition, string RouteId, string Fingerprint) : IAsyncDisposable
    {
        private readonly object _leaseGate = new();
        private int _activeLeases;
        private bool _retired;
        private Task? _disposeTask;
        private TaskCompletionSource<object?>? _disposeCompletion;

        public string RouteId { get; } = RouteId;

        public string Fingerprint { get; } = Fingerprint;

        public McpStdioServerConnection Connection { get; } = new(definition);

        public McpProxyRegistrationLease? TryAcquireLease()
        {
            lock (_leaseGate)
            {
                if (_retired)
                    return null;

                _activeLeases++;
                return new McpProxyRegistrationLease(this);
            }
        }

        public ValueTask RetireAsync()
        {
            Task? disposeTask;
            lock (_leaseGate)
            {
                _retired = true;
                if (_activeLeases > 0)
                {
                    _disposeCompletion ??= new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return new ValueTask(_disposeCompletion.Task);
                }

                disposeTask = StartDisposeLocked();
            }

            return new ValueTask(disposeTask);
        }

        public ValueTask DisposeAsync()
            => RetireAsync();

        internal ValueTask ReleaseLeaseAsync()
        {
            Task? disposeTask = null;
            TaskCompletionSource<object?>? disposeCompletion = null;
            lock (_leaseGate)
            {
                _activeLeases--;
                if (_activeLeases < 0)
                    throw new InvalidOperationException("MCP proxy registration lease was released more than once.");

                if (_activeLeases == 0 && _retired)
                {
                    disposeTask = StartDisposeLocked();
                    disposeCompletion = _disposeCompletion;
                }
            }

            if (disposeTask is null)
                return ValueTask.CompletedTask;

            return disposeCompletion is null
                ? new ValueTask(disposeTask)
                : CompleteDisposeAsync(disposeTask, disposeCompletion);
        }

        private Task StartDisposeLocked()
            => _disposeTask ??= Connection.DisposeAsync().AsTask();

        private static async ValueTask CompleteDisposeAsync(Task disposeTask, TaskCompletionSource<object?> disposeCompletion)
        {
            try
            {
                await disposeTask.ConfigureAwait(false);
                disposeCompletion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                disposeCompletion.TrySetException(ex);
                throw;
            }
        }
    }

    private sealed class McpProxyRegistrationLease(McpProxyRegistration registration) : IAsyncDisposable
    {
        private int _disposed;

        public McpProxyRegistration Registration { get; } = registration;

        public ValueTask DisposeAsync()
            => Interlocked.Exchange(ref _disposed, 1) == 0
                ? Registration.ReleaseLeaseAsync()
                : ValueTask.CompletedTask;
    }
}

internal sealed class McpStdioServerConnection : IAsyncDisposable
{
    private readonly McpProxyServerDefinition _definition;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private readonly List<Task> _retiredIoTasks = [];
    private readonly int _timeoutMilliseconds;

    private CancellationTokenSource _ioCts = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private int _nextId;
    private int _processGeneration;
    private JsonElement? _initializeResult;
    private bool _disposed;

    public McpStdioServerConnection(McpProxyServerDefinition definition)
    {
        _definition = definition;
        _timeoutMilliseconds = definition.Config.Timeout is > 0 ? definition.Config.Timeout.Value : 60_000;
    }

    public async Task<string?> HandleClientMessageAsync(string body, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(body);
        var message = document.RootElement.Clone();
        var hasId = message.TryGetProperty("id", out var clientId);
        var method = message.TryGetProperty("method", out var methodElement)
            ? methodElement.GetString()
            : null;

        if (!hasId)
        {
            if (!string.Equals(method, "notifications/initialized", StringComparison.Ordinal))
                await ForwardNotificationIfRunningAsync(message, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (string.Equals(method, "initialize", StringComparison.Ordinal))
        {
            try
            {
                var initParams = message.TryGetProperty("params", out var p) ? p.Clone() : (JsonElement?)null;
                var initResult = await EnsureInitializedAsync(initParams, cancellationToken).ConfigureAwait(false);
                return JsonRpc.Response(clientId, initResult);
            }
            catch (Exception ex)
            {
                return JsonRpc.Error(clientId, -32000, ex.Message);
            }
        }

        try
        {
            await EnsureInitializedAsync(null, cancellationToken).ConfigureAwait(false);
            var response = await ForwardRequestAsync(message, cancellationToken).ConfigureAwait(false);
            return JsonRpc.ReplaceId(response, clientId);
        }
        catch (Exception ex)
        {
            return JsonRpc.Error(clientId, -32000, ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Process? process;
        Task? stdoutTask;
        Task? stderrTask;
        Task[] retiredIoTasks;

        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            process = _process;
            stdoutTask = _stdoutTask;
            stderrTask = _stderrTask;
            retiredIoTasks = _retiredIoTasks.ToArray();
            _retiredIoTasks.Clear();
            _process = null;
            _stdin = null;
            _stdoutTask = null;
            _stderrTask = null;
            _initializeResult = null;
            _disposed = true;
            CompletePendingWithError(new ObjectDisposedException(_definition.Name));
            _ioCts.Cancel();
        }
        finally
        {
            _lifecycleLock.Release();
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }

            process.Dispose();
        }

        await IgnoreAsync(stdoutTask).ConfigureAwait(false);
        await IgnoreAsync(stderrTask).ConfigureAwait(false);
        foreach (var retiredIoTask in retiredIoTasks)
            await IgnoreAsync(retiredIoTask).ConfigureAwait(false);
        _lifecycleLock.Dispose();
        _writeLock.Dispose();
        _ioCts.Dispose();
    }

    private async Task<JsonElement> EnsureInitializedAsync(JsonElement? clientParams, CancellationToken cancellationToken)
    {
        if (_initializeResult is { } existing && IsProcessRunning(_process))
            return existing;

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_initializeResult is { } current && IsProcessRunning(_process))
                return current;

            if (_process is not null && !IsProcessRunning(_process))
                ResetStoppedProcess();

            StartProcess();
            var initParams = clientParams ?? JsonRpc.DefaultInitializeParams();
            var initResponse = await SendRequestAsync("initialize", initParams, cancellationToken).ConfigureAwait(false);
            if (!initResponse.TryGetProperty("result", out var result))
                throw new InvalidOperationException($"MCP server '{_definition.Name}' did not return an initialize result.");

            _initializeResult = result.Clone();
            await SendNotificationAsync("notifications/initialized", null, cancellationToken).ConfigureAwait(false);
            return _initializeResult.Value;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void StartProcess()
    {
        ThrowIfDisposed();
        if (_process is { HasExited: false })
            return;

        if (string.IsNullOrWhiteSpace(_definition.Config.Command))
            throw new InvalidOperationException($"MCP server '{_definition.Name}' does not have a command.");

        var startInfo = new ProcessStartInfo
        {
            FileName = _definition.Config.Command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(_definition.Config.Cwd))
            startInfo.WorkingDirectory = _definition.Config.Cwd;

        foreach (var arg in _definition.Config.Args ?? [])
            startInfo.ArgumentList.Add(arg);

        if (_definition.Config.Env is not null)
        {
            foreach (var (key, value) in _definition.Config.Env)
                startInfo.Environment[key] = value;
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start MCP server '{_definition.Name}'.");

        _process = process;
        _stdin = process.StandardInput;
        var generation = Interlocked.Increment(ref _processGeneration);
        _stdoutTask = Task.Run(() => ReadStdoutAsync(process, generation, _ioCts.Token), _ioCts.Token);
        _stderrTask = Task.Run(() => DrainAsync(process.StandardError, _ioCts.Token), _ioCts.Token);
    }

    private void ResetStoppedProcess()
    {
        var process = _process;
        var oldIoCts = _ioCts;
        var oldStdoutTask = _stdoutTask;
        var oldStderrTask = _stderrTask;
        _process = null;
        _stdin = null;
        _stdoutTask = null;
        _stderrTask = null;
        _initializeResult = null;
        _ioCts = new CancellationTokenSource();
        CompletePendingWithError(new IOException($"MCP server '{_definition.Name}' stopped."));

        oldIoCts.Cancel();
        TrackRetiredIoTasks(oldIoCts, oldStdoutTask, oldStderrTask);

        if (process is not null)
        {
            try { process.Dispose(); }
            catch { }
        }
    }

    private void TrackRetiredIoTasks(CancellationTokenSource ioCts, Task? stdoutTask, Task? stderrTask)
    {
        _retiredIoTasks.RemoveAll(static task => task.IsCompleted);
        _retiredIoTasks.Add(DisposeRetiredIoAsync(ioCts, stdoutTask, stderrTask));
    }

    private static async Task DisposeRetiredIoAsync(CancellationTokenSource ioCts, Task? stdoutTask, Task? stderrTask)
    {
        try
        {
            await IgnoreAsync(stdoutTask).ConfigureAwait(false);
            await IgnoreAsync(stderrTask).ConfigureAwait(false);
        }
        finally
        {
            ioCts.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(_definition.Name);
    }

    private static bool IsProcessRunning(Process? process)
    {
        if (process is null)
            return false;

        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<JsonElement> ForwardRequestAsync(JsonElement clientMessage, CancellationToken cancellationToken)
    {
        var internalId = Interlocked.Increment(ref _nextId);
        var request = JsonRpc.WithId(clientMessage, internalId);
        var key = internalId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pending)
            _pending[key] = tcs;

        try
        {
            await SendRawAsync(request, cancellationToken).ConfigureAwait(false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeoutMilliseconds);
            return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_pending)
                _pending.Remove(key);
        }
    }

    private async Task<JsonElement> SendRequestAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        var internalId = Interlocked.Increment(ref _nextId);
        var request = JsonRpc.Request(internalId, method, parameters);
        var key = internalId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pending)
            _pending[key] = tcs;

        try
        {
            await SendRawAsync(request, cancellationToken).ConfigureAwait(false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeoutMilliseconds);
            return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_pending)
                _pending.Remove(key);
        }
    }

    private async Task ForwardNotificationIfRunningAsync(JsonElement clientMessage, CancellationToken cancellationToken)
    {
        if (_process is not { HasExited: false })
            return;

        await SendRawAsync(clientMessage.GetRawText(), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendNotificationAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
        => await SendRawAsync(JsonRpc.Notification(method, parameters), cancellationToken).ConfigureAwait(false);

    private async Task SendRawAsync(string json, CancellationToken cancellationToken)
    {
        if (_stdin is null)
            throw new InvalidOperationException($"MCP server '{_definition.Name}' is not running.");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stdin.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadStdoutAsync(Process process, int generation, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (generation != Volatile.Read(ref _processGeneration))
                    break;

                HandleServerLine(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            CompletePendingWithErrorForGeneration(generation, ex);
        }
        finally
        {
            CompletePendingWithErrorForGeneration(generation, new IOException($"MCP server '{_definition.Name}' stopped."));
        }
    }

    private void CompletePendingWithErrorForGeneration(int generation, Exception error)
    {
        if (generation == Volatile.Read(ref _processGeneration))
            CompletePendingWithError(error);
    }

    private void HandleServerLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var message = doc.RootElement.Clone();
        if (message.TryGetProperty("id", out var id) && !message.TryGetProperty("method", out _))
        {
            var key = JsonRpc.IdKey(id);
            TaskCompletionSource<JsonElement>? tcs;
            lock (_pending)
                _pending.TryGetValue(key, out tcs);
            tcs?.TrySetResult(message);
            return;
        }

        if (message.TryGetProperty("id", out var requestId) && message.TryGetProperty("method", out _))
        {
            _ = SendRawAsync(JsonRpc.Error(requestId, -32601, "Server-to-client MCP requests are not supported by Lumi's local proxy."), CancellationToken.None);
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null) { }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch { }
    }

    private void CompletePendingWithError(Exception error)
    {
        TaskCompletionSource<JsonElement>[] pending;
        lock (_pending)
        {
            pending = _pending.Values.ToArray();
            _pending.Clear();
        }

        foreach (var tcs in pending)
            tcs.TrySetException(error);
    }

    private static async Task IgnoreAsync(Task? task)
    {
        if (task is null)
            return;

        try { await task.ConfigureAwait(false); }
        catch { }
    }
}

internal static class JsonRpc
{
    public static JsonElement DefaultInitializeParams()
    {
        using var document = JsonDocument.Parse("""
            {
              "protocolVersion": "2025-06-18",
              "capabilities": {},
              "clientInfo": {
                "name": "lumi-mcp-proxy",
                "version": "1"
              }
            }
            """);
        return document.RootElement.Clone();
    }

    public static string Request(int id, string method, JsonElement? parameters)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method
        };
        if (parameters is not null)
            obj["params"] = JsonNode.Parse(parameters.Value.GetRawText());
        return obj.ToJsonString();
    }

    public static string Notification(string method, JsonElement? parameters)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method
        };
        if (parameters is not null)
            obj["params"] = JsonNode.Parse(parameters.Value.GetRawText());
        return obj.ToJsonString();
    }

    public static string WithId(JsonElement message, int id)
    {
        var obj = JsonNode.Parse(message.GetRawText())!.AsObject();
        obj["id"] = id;
        return obj.ToJsonString();
    }

    public static string ReplaceId(JsonElement message, JsonElement id)
    {
        var obj = JsonNode.Parse(message.GetRawText())!.AsObject();
        obj["id"] = JsonNode.Parse(id.GetRawText());
        return obj.ToJsonString();
    }

    public static string Response(JsonElement id, JsonElement result)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["result"] = JsonNode.Parse(result.GetRawText())
        };
        return obj.ToJsonString();
    }

    public static string Error(JsonElement? id, int code, string message)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id is null ? null : JsonNode.Parse(id.Value.GetRawText()),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
        return obj.ToJsonString();
    }

    public static string IdKey(JsonElement id)
        => id.ValueKind switch
        {
            JsonValueKind.String => id.GetString() ?? string.Empty,
            JsonValueKind.Number => id.GetRawText(),
            _ => id.GetRawText()
        };
}
