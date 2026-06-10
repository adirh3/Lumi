using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;

namespace Lumi.Services;

public sealed record McpProxyServerDefinition(
    string Key,
    string Name,
    McpStdioServerConfig Config);

/// <summary>
/// Process-wide multiplexer that lets every Copilot session share a single upstream
/// stdio MCP process instead of spawning one process per session.
///
/// <para><b>Why this exists.</b> The Copilot CLI starts one MCP process per session
/// (verified empirically against CLI 1.0.60: two concurrent sessions spawn two distinct
/// server PIDs, whether configured at client level or per session). When a user runs
/// several chats in parallel with many MCP servers, that means re-launching every server
/// for every chat — slow startup and wasted resources. This proxy registers each local
/// stdio server once, exposes it as a loopback HTTP MCP endpoint, and fans the N SDK
/// clients onto one shared upstream stdio connection, rewriting JSON-RPC ids.</para>
///
/// <para><b>Trade-off.</b> A single shared upstream cannot faithfully answer
/// per-client server→client requests (sampling / elicitation / roots), because those are
/// semantically tied to one specific client/session. The proxy therefore advertises
/// <i>no</i> client capabilities to the upstream (see
/// <see cref="JsonRpc.UpstreamInitializeParams"/>), so well-behaved servers never attempt
/// those features over the shared connection. A server that genuinely needs interactive
/// server→client features should be marked <c>RunIsolated</c> (see
/// <see cref="Lumi.Models.McpServer.RunIsolated"/>): it then bypasses the proxy and runs
/// natively per session, where the CLI and Lumi's elicitation/permission handlers serve
/// those requests directly.</para>
///
/// <para><b>Isolation.</b> The route <i>key</i> (built by
/// <see cref="McpSessionPlanner"/>) encodes the working directory and environment, so two
/// chats that use the same logical server with different effective directories get
/// independent shared pools rather than colliding on one process whose cwd is "last
/// registered wins".</para>
/// </summary>
public sealed class McpProxyRuntime : IAsyncDisposable
{
    public static McpProxyRuntime Shared { get; } = new();

    /// <summary>
    /// A newly registered route is shielded from <see cref="RetainProxyRoutes"/> for this long so
    /// a concurrent build can't have its just-created pool reclaimed before it records the key.
    /// </summary>
    private static readonly TimeSpan RouteRetentionGrace = TimeSpan.FromSeconds(30);

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
        var retainedIds = activeLocalServerIds.ToHashSet();
        List<McpProxyRegistration> staleRegistrations = [];

        lock (_gate)
        {
            if (_disposed)
                return;

            foreach (var (key, registration) in _registrationsByKey.ToArray())
            {
                if (!key.StartsWith("lumi:", StringComparison.Ordinal))
                    continue;

                // Keys are "lumi:{serverId}" or "lumi:{serverId}:{contextHash}". A single
                // logical server can have several context pools alive at once (one per
                // working directory / env), so retire by the server-id segment rather than
                // by exact key — otherwise every context variant of an active server would
                // be torn down on each config change.
                if (TryParseUserServerId(key, out var serverId) && retainedIds.Contains(serverId))
                    continue;

                _registrationsByKey.Remove(key);
                _registrationsByRoute.Remove(registration.RouteId);
                staleRegistrations.Add(registration);
            }
        }

        foreach (var registration in staleRegistrations)
            RetireRegistrationInBackground(registration);
    }

    /// <summary>
    /// Retires every user (<c>lumi:</c>) and project (<c>project:</c>) registration whose key is
    /// not present in <paramref name="liveKeys"/>. The caller passes the union of route keys still
    /// referenced by live chat sessions, so once every chat that used a particular context
    /// (working directory / environment / project) is closed, its shared upstream process is
    /// reclaimed instead of lingering for the lifetime of the app. Built-in routes (e.g. the
    /// GitHub web-search bootstrap) use other prefixes and are never touched here.
    /// </summary>
    public void RetainProxyRoutes(IReadOnlyCollection<string> liveKeys)
        => RetainProxyRoutes(liveKeys, RouteRetentionGrace);

    internal void RetainProxyRoutes(IReadOnlyCollection<string> liveKeys, TimeSpan grace)
    {
        ArgumentNullException.ThrowIfNull(liveKeys);
        var live = new HashSet<string>(liveKeys, StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        List<McpProxyRegistration> staleRegistrations = [];

        lock (_gate)
        {
            if (_disposed)
                return;

            foreach (var (key, registration) in _registrationsByKey.ToArray())
            {
                if (!key.StartsWith("lumi:", StringComparison.Ordinal)
                    && !key.StartsWith("project:", StringComparison.Ordinal))
                    continue;

                if (live.Contains(key))
                    continue;

                // Protect a just-registered route: a concurrent session build may have created
                // it but not yet published its key into the live set, so never retire one that is
                // younger than the grace window.
                if (now - registration.CreatedUtc < grace)
                    continue;

                _registrationsByKey.Remove(key);
                _registrationsByRoute.Remove(registration.RouteId);
                staleRegistrations.Add(registration);
            }
        }

        foreach (var registration in staleRegistrations)
            RetireRegistrationInBackground(registration);
    }

    private static bool TryParseUserServerId(string key, out Guid serverId)
    {
        serverId = Guid.Empty;
        if (!key.StartsWith("lumi:", StringComparison.Ordinal))
            return false;

        var rest = key.AsSpan(5);
        var colon = rest.IndexOf(':');
        var idSpan = colon >= 0 ? rest[..colon] : rest;
        return Guid.TryParse(idSpan, out serverId);
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
        builder.AppendLine(config.WorkingDirectory);
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

        public DateTime CreatedUtc { get; } = DateTime.UtcNow;

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
    private const int DiagnosticLineLimit = 8;
    private const int DiagnosticLineMaxLength = 500;
    private const int DiagnosticTextMaxLength = 2_000;
    private static readonly Regex SensitiveDiagnosticPattern = new(
        @"(?i)(authorization|token|api[_-]?key|secret|password)(\s*[=:]\s*)([^\s,;]+)",
        RegexOptions.Compiled);
    private static readonly Regex BearerDiagnosticPattern = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.Compiled);

    private readonly McpProxyServerDefinition _definition;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private readonly List<Task> _retiredIoTasks = [];
    private readonly object _diagnosticOutputLock = new();
    private readonly Queue<string> _recentStdout = new();
    private readonly Queue<string> _recentStderr = new();
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
            // Capability honesty: the shared upstream serves many SDK clients at once, so
            // it must not be told the (per-client) sampling/elicitation/roots capabilities
            // that the proxy cannot faithfully route. Always initialize with empty client
            // capabilities and the proxy's own identity, preserving only the negotiated
            // protocol version from the first client.
            var initParams = JsonRpc.UpstreamInitializeParams(clientParams);
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
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(_definition.Config.WorkingDirectory))
            startInfo.WorkingDirectory = _definition.Config.WorkingDirectory;

        foreach (var arg in _definition.Config.Args ?? [])
            startInfo.ArgumentList.Add(arg);

        if (_definition.Config.Env is not null)
        {
            foreach (var (key, value) in _definition.Config.Env)
                startInfo.Environment[key] = value;
        }

        var configuredCommand = _definition.Config.Command;
        startInfo.FileName = ResolveCommandPath(configuredCommand, startInfo.Environment);

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start MCP server '{_definition.Name}'.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(BuildWin32ProcessStartErrorMessage(startInfo, configuredCommand, ex), ex);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException(BuildProcessStartErrorMessage(startInfo, configuredCommand), ex);
        }

        _process = process;
        _stdin = process.StandardInput;
        var generation = Interlocked.Increment(ref _processGeneration);
        _stdoutTask = Task.Run(() => ReadStdoutAsync(process, generation, _ioCts.Token), _ioCts.Token);
        _stderrTask = Task.Run(() => DrainStderrAsync(process.StandardError, _ioCts.Token), _ioCts.Token);
    }

    private string BuildProcessStartErrorMessage(ProcessStartInfo startInfo, string configuredCommand)
    {
        var cwd = string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
            ? Environment.CurrentDirectory
            : startInfo.WorkingDirectory;
        var pathEntryCount = CountPathEntries(startInfo.Environment);
        var pathContext = pathEntryCount is null
            ? ""
            : $" PATH entries searched: {pathEntryCount.Value}.";

        if (string.Equals(startInfo.FileName, configuredCommand, StringComparison.OrdinalIgnoreCase))
        {
            return $"Failed to start MCP server '{_definition.Name}'. Command '{configuredCommand}' was not found from working directory '{cwd}'. Install '{configuredCommand}' or add it to the PATH used by Lumi.{pathContext}";
        }

        return $"Failed to start MCP server '{_definition.Name}'. Command '{configuredCommand}' resolved to '{startInfo.FileName}' but could not be started from working directory '{cwd}'.{pathContext}";
    }

    private string BuildWin32ProcessStartErrorMessage(
        ProcessStartInfo startInfo,
        string configuredCommand,
        Win32Exception error)
    {
        var cwd = string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
            ? Environment.CurrentDirectory
            : startInfo.WorkingDirectory;
        var pathEntryCount = CountPathEntries(startInfo.Environment);
        var pathContext = pathEntryCount is null
            ? ""
            : $" PATH entries searched: {pathEntryCount.Value}.";

        return error.NativeErrorCode switch
        {
            2 or 3 => BuildProcessStartErrorMessage(startInfo, configuredCommand),
            5 => $"Failed to start MCP server '{_definition.Name}'. Access denied while starting command '{configuredCommand}' from working directory '{cwd}'.",
            193 => $"Failed to start MCP server '{_definition.Name}'. Command '{configuredCommand}' is not a valid executable for this platform.",
            _ => $"Failed to start MCP server '{_definition.Name}'. Command '{configuredCommand}' could not be started from working directory '{cwd}': {error.Message}.{pathContext}"
        };
    }

    private static string ResolveCommandPath(string command, IDictionary<string, string?> environment)
    {
        if (!OperatingSystem.IsWindows()
            || string.IsNullOrWhiteSpace(command)
            || HasDirectorySeparator(command)
            || Path.IsPathRooted(command))
        {
            return command;
        }

        var path = GetEnvironmentValue(environment, "PATH");
        if (string.IsNullOrWhiteSpace(path))
            return command;

        var candidateNames = GetWindowsCommandCandidateNames(command, environment);
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            var trimmedDirectory = directory.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(trimmedDirectory))
                continue;

            foreach (var candidateName in candidateNames)
            {
                var candidate = Path.Combine(trimmedDirectory, candidateName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return command;
    }

    private static bool HasDirectorySeparator(string command)
        => command.Contains(Path.DirectorySeparatorChar)
           || command.Contains(Path.AltDirectorySeparatorChar)
           || (OperatingSystem.IsWindows() && command.Contains('/'));

    private static IReadOnlyList<string> GetWindowsCommandCandidateNames(
        string command,
        IDictionary<string, string?> environment)
    {
        if (!string.IsNullOrEmpty(Path.GetExtension(command)))
            return [command];

        var pathExt = GetEnvironmentValue(environment, "PATHEXT");
        var extensions = string.IsNullOrWhiteSpace(pathExt)
            ? [".COM", ".EXE", ".BAT", ".CMD"]
            : pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        return extensions
            .Select(extension => command + extension)
            .Concat([command])
            .ToList();
    }

    private static string? GetEnvironmentValue(IDictionary<string, string?> environment, string key)
    {
        if (environment.TryGetValue(key, out var value))
            return value;

        foreach (var pair in environment)
        {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return null;
    }

    private static int? CountPathEntries(IDictionary<string, string?> environment)
    {
        var path = GetEnvironmentValue(environment, "PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return path
            .Split(Path.PathSeparator)
            .Count(entry => !string.IsNullOrWhiteSpace(entry));
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

                AddDiagnosticLine(_recentStdout, line);

                if (generation != Volatile.Read(ref _processGeneration))
                    break;

                try
                {
                    HandleServerLine(line);
                }
                catch (JsonException ex)
                {
                    throw CreateNonJsonStdoutException(line, ex);
                }
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
            CompletePendingWithErrorForGeneration(generation, CreateServerStoppedException(process));
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

        if (message.TryGetProperty("id", out var requestId) && message.TryGetProperty("method", out var methodElement))
        {
            var method = methodElement.GetString();
            _ = RespondToServerRequestAsync(requestId, method);
        }
    }

    /// <summary>
    /// Answers a server→client request on the shared upstream connection. Because the proxy
    /// advertises no client capabilities, well-behaved servers should never ask for
    /// sampling/elicitation here; the graceful answers below keep liveness/roots probes
    /// working and return an actionable error for the interactive features that require
    /// running the server in isolated (native per-session) mode.
    /// </summary>
    private Task RespondToServerRequestAsync(JsonElement requestId, string? method)
    {
        switch (method)
        {
            case "ping":
                return SendRawAsync(JsonRpc.Response(requestId, JsonRpc.EmptyObject()), CancellationToken.None);

            case "roots/list":
                return SendRawAsync(JsonRpc.Response(requestId, JsonRpc.EmptyRoots()), CancellationToken.None);

            case "sampling/createMessage":
            case "elicitation/create":
            case "elicitation/elicit":
                return SendRawAsync(
                    JsonRpc.Error(requestId, -32601,
                        $"MCP server '{_definition.Name}' requested '{method}', which needs a per-session connection. " +
                        "Enable \"Run in isolated mode\" for this server in Lumi so it runs directly instead of through the shared proxy."),
                    CancellationToken.None);

            default:
                return SendRawAsync(
                    JsonRpc.Error(requestId, -32601,
                        $"Server-to-client request '{method}' is not supported on Lumi's shared MCP proxy connection."),
                    CancellationToken.None);
        }
    }

    private async Task DrainStderrAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                AddDiagnosticLine(_recentStderr, line);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch { }
    }

    private InvalidOperationException CreateNonJsonStdoutException(string line, JsonException inner)
        => new(
            $"MCP server '{_definition.Name}' wrote non-JSON output to stdout during startup or initialization: {FormatDiagnosticLine(line)}{FormatCapturedOutput()}",
            inner);

    private IOException CreateServerStoppedException(Process process)
    {
        var builder = new StringBuilder($"MCP server '{_definition.Name}' stopped");
        if (TryGetExitCode(process) is { } exitCode)
            builder.Append(" with exit code ").Append(exitCode);
        builder.Append('.');
        builder.Append(FormatCapturedOutput());
        return new IOException(builder.ToString());
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void AddDiagnosticLine(Queue<string> target, string line)
    {
        var formatted = FormatDiagnosticLine(line);
        if (string.IsNullOrWhiteSpace(formatted))
            return;

        lock (_diagnosticOutputLock)
        {
            target.Enqueue(formatted);
            while (target.Count > DiagnosticLineLimit)
                target.Dequeue();
        }
    }

    private string FormatCapturedOutput()
    {
        string[] stdout;
        string[] stderr;
        lock (_diagnosticOutputLock)
        {
            stdout = _recentStdout.ToArray();
            stderr = _recentStderr.ToArray();
        }

        var parts = new List<string>();
        if (stdout.Length > 0)
            parts.Add("stdout: " + string.Join(" | ", stdout));
        if (stderr.Length > 0)
            parts.Add("stderr: " + string.Join(" | ", stderr));

        if (parts.Count == 0)
            return "";

        var text = " Recent output - " + string.Join("; ", parts);
        return text.Length <= DiagnosticTextMaxLength
            ? text
            : text[..DiagnosticTextMaxLength] + "...";
    }

    private static string FormatDiagnosticLine(string line)
    {
        var formatted = line.Replace('\r', ' ').Replace('\n', ' ').Trim();
        formatted = BearerDiagnosticPattern.Replace(formatted, "Bearer [redacted]");
        formatted = SensitiveDiagnosticPattern.Replace(formatted, "$1$2[redacted]");
        return formatted.Length <= DiagnosticLineMaxLength
            ? formatted
            : formatted[..DiagnosticLineMaxLength] + "...";
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

    /// <summary>
    /// Builds the initialize params the proxy sends to a shared upstream server. The client's
    /// protocol version is preserved when present, but capabilities are always emptied and the
    /// client identity is replaced with the proxy's, so the upstream never sees per-client
    /// server→client capabilities the shared connection cannot honor.
    /// </summary>
    public static JsonElement UpstreamInitializeParams(JsonElement? clientParams)
    {
        var protocolVersion = "2025-06-18";
        if (clientParams is { } cp
            && cp.ValueKind == JsonValueKind.Object
            && cp.TryGetProperty("protocolVersion", out var pv)
            && pv.ValueKind == JsonValueKind.String
            && pv.GetString() is { Length: > 0 } requested)
        {
            protocolVersion = requested;
        }

        var obj = new JsonObject
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "lumi-mcp-proxy",
                ["version"] = "1"
            }
        };
        using var document = JsonDocument.Parse(obj.ToJsonString());
        return document.RootElement.Clone();
    }

    public static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    public static JsonElement EmptyRoots()
    {
        using var document = JsonDocument.Parse("""{"roots":[]}""");
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
