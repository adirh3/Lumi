using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Lumi.Services;

internal sealed partial class McpStdioServerConnection : IAsyncDisposable
{
    private const int InitializeTimeoutMilliseconds = 45_000;
    private const int DiagnosticLineLimit = 8;
    private const int DiagnosticLineMaxLength = 500;
    private const int DiagnosticTextMaxLength = 2_000;
    private const int InitializeRetryLimit = 1;
    private const int SessionRecoveryRetryLimit = 1;
    private static readonly Regex SensitiveDiagnosticPattern = new(
        @"(?i)(authorization|token|api[_-]?key|secret|password)(\s*[=:]\s*)([^\s,;]+)",
        RegexOptions.Compiled);
    private static readonly Regex BearerDiagnosticPattern = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.Compiled);
    private static readonly (int Code, string Message)[] RecoverableSessionLossErrors =
    [
        (-32_001, "Session not found"),
        (-32_600, "Session terminated")
    ];

    private readonly McpProxyServerDefinition _definition;
    private readonly McpDiscoveryCache _discoveryCache;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string?> _sessionRecoveryOutcomes = [];
    private readonly List<Task> _retiredProcessTasks = [];
    private readonly object _diagnosticOutputLock = new();
    private readonly object _discoverySignalGate = new();
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
    private int _sessionRecoveryAttempts;
    private int _sessionRecoverySuccesses;
    private int _sessionRecoveryFailures;
    private JsonElement? _initializeParams;
    private JsonElement? _initializeResult;
    private string? _discoveryKey;
    private JsonElement? _liveToolsResponse;
    private long _liveDiscoveryRevision = -1;
    private int _liveDiscoveryGeneration;
    private int _taintedGeneration = -1;
    private long _sourceRevision;
    private long _liveSourceRevision = -1;
    private bool _disposed;

    public McpStdioServerConnection(McpProxyServerDefinition definition, McpDiscoveryCache discoveryCache)
    {
        _definition = definition;
        _discoveryCache = discoveryCache;
        _timeoutMilliseconds = definition.Config.Timeout is > 0 ? definition.Config.Timeout.Value : 60_000;
    }

    internal static int GetInitializeTimeoutMilliseconds(int requestTimeoutMilliseconds)
        => Math.Min(requestTimeoutMilliseconds, InitializeTimeoutMilliseconds);

    private CancellationTokenSource CreateInitializeTimeoutSource(CancellationToken cancellationToken)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(GetInitializeTimeoutMilliseconds(_timeoutMilliseconds));
        return timeoutCts;
    }

    public async Task<string?> HandleClientMessageAsync(
        McpDiscoverySession client, string body, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(body);
        var message = document.RootElement.Clone();
        var hasId = message.TryGetProperty("id", out var clientId);
        var method = message.TryGetProperty("method", out var methodElement)
            ? methodElement.GetString()
            : null;

        if (!hasId)
        {
            if (method is not ("notifications/initialized" or "notifications/cancelled"))
                InvalidateDiscovery(taint: true);
            if (!string.Equals(method, "notifications/initialized", StringComparison.Ordinal))
                await ForwardNotificationIfRunningAsync(message, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (string.Equals(method, "server/discover", StringComparison.Ordinal))
            return JsonRpc.Error(clientId, -32601, "Method not found: server/discover. Use initialize.");

        if (string.Equals(method, "initialize", StringComparison.Ordinal))
        {
            try
            {
                var initParams = message.TryGetProperty("params", out var p) ? p.Clone() : (JsonElement?)null;
                var initResult = await EnsureInitializedAsync(client, initParams, cancellationToken).ConfigureAwait(false);
                return JsonRpc.Response(clientId, initResult);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return JsonRpc.Error(clientId, -32000,
                    $"MCP server '{_definition.Name}' initialization timed out after {GetInitializeTimeoutMilliseconds(_timeoutMilliseconds)} ms.{FormatCapturedOutput()}");
            }
            catch (Exception ex)
            {
                return JsonRpc.Error(clientId, -32000, ex.Message);
            }
        }

        try
        {
            var canRetryAfterInterruption = CanRetryAfterSessionInterruption(method);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var (response, processGeneration) = await ForwardRequestAsync(client, message, cancellationToken).ConfigureAwait(false);
                    if (!IsRecoverableSessionLossResponse(response))
                        return JsonRpc.ReplaceId(response, clientId);

                    await RecoverExpiredServerSessionAsync(processGeneration, cancellationToken).ConfigureAwait(false);
                    if (!canRetryAfterInterruption || attempt >= SessionRecoveryRetryLimit)
                        return JsonRpc.ReplaceId(response, clientId);
                }
                catch (McpServerSessionRetiredException ex)
                    when (canRetryAfterInterruption && attempt < SessionRecoveryRetryLimit)
                {
                    await RecoverExpiredServerSessionAsync(ex.ProcessGeneration, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (McpServerSessionRetiredException)
        {
            return JsonRpc.Error(
                clientId,
                -32000,
                $"MCP server '{_definition.Name}' restarted while '{method ?? "unknown"}' was in flight. Its outcome is unknown, so Lumi did not retry it.");
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
        Task[] retiredProcessTasks;

        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            process = _process;
            stdoutTask = _stdoutTask;
            stderrTask = _stderrTask;
            retiredProcessTasks = _retiredProcessTasks.ToArray();
            _retiredProcessTasks.Clear();
            _process = null;
            _stdin = null;
            _stdoutTask = null;
            _stderrTask = null;
            _initializeParams = null;
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
                await TerminateProcessTreeAndWaitAsync(process).ConfigureAwait(false);
            }
            finally
            {
                process.Dispose();
            }
        }

        await IgnoreAsync(stdoutTask).ConfigureAwait(false);
        await IgnoreAsync(stderrTask).ConfigureAwait(false);
        foreach (var retiredProcessTask in retiredProcessTasks)
            await retiredProcessTask.ConfigureAwait(false);
        _lifecycleLock.Dispose();
        _writeLock.Dispose();
        _ioCts.Dispose();
    }

    private async Task<JsonElement> EnsureInitializedAsync(
        McpDiscoverySession client, JsonElement? clientParams, CancellationToken cancellationToken)
    {
        using var initializeCts = CreateInitializeTimeoutSource(cancellationToken);
        var initializeCt = initializeCts.Token;
        await _lifecycleLock.WaitAsync(initializeCt).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var parameters = clientParams ?? JsonRpc.DefaultInitializeParams();
            BindInitializeProfile(parameters);
            if (client.UseLazyInitialization)
            {
                if (client.InitializeResult is { } advertised)
                    return McpDiscoveryCache.CreateProxyInitializeResult(advertised);

                if (_processGeneration == 0 && !IsDiscoveryTainted && _discoveryKey is { } key)
                {
                    while (true)
                    {
                        initializeCt.ThrowIfCancellationRequested();
                        var refreshRevision = _discoveryCache.RefreshRevision;
                        var revision = DiscoveryRevision;
                        var cached = _discoveryCache.Read(key, parameters);
                        if (cached is null)
                            break;
                        if (revision != DiscoveryRevision)
                            continue;
                        client.InitializeResult = cached.InitializeResult;
                        client.ToolsResult = cached.ToolsResult;
                        Volatile.Write(ref client.AdvertisedRevision, refreshRevision);
                        Trace.TraceInformation("MCP server '{0}' is using cached discovery; backend startup is deferred.", _definition.Name);
                        return McpDiscoveryCache.CreateProxyInitializeResult(cached.InitializeResult);
                    }
                }
            }

            var initialize = await EnsureInitializedUnderLockAsync(parameters, initializeCt).ConfigureAwait(false);
            if (client.UseLazyInitialization && !IsDiscoveryTainted
                && McpDiscoveryCache.IsCacheableInitialization(parameters, initialize))
            {
                client.InitializeResult = initialize;
                Volatile.Write(ref client.AdvertisedRevision, _discoveryCache.RefreshRevision);
                return McpDiscoveryCache.CreateProxyInitializeResult(initialize);
            }
            return initialize;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task<JsonElement> EnsureInitializedUnderLockAsync(
        JsonElement? clientParams,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        BindInitializeProfile(clientParams ?? _initializeParams ?? JsonRpc.DefaultInitializeParams());
        if (_initializeResult is { } current && IsProcessRunning(_process))
            return current;

        if (_process is not null && !IsProcessRunning(_process))
            ResetStoppedProcess();

        try
        {
            StartProcess();
            var initParams = _initializeParams!.Value;
            var initializedResult = await SendInitializeWithRetryAsync(initParams, cancellationToken).ConfigureAwait(false);
            await SendNotificationAsync("notifications/initialized", null, cancellationToken).ConfigureAwait(false);
            _initializeResult = initializedResult;
            return initializedResult;
        }
        catch
        {
            InvalidateDiscovery();
            RetireProcessAfterFailedInitialization();
            throw;
        }
    }

    private async Task<JsonElement> SendInitializeWithRetryAsync(
        JsonElement initParams,
        CancellationToken cancellationToken)
    {
        JsonElement initResponse;
        for (var attempt = 0; ; attempt++)
        {
            initResponse = await SendRequestAsync("initialize", initParams, cancellationToken).ConfigureAwait(false);
            if (initResponse.TryGetProperty("result", out _))
                break;

            if (attempt >= InitializeRetryLimit || !IsRetryableInitializeErrorResponse(initResponse))
                throw new InvalidOperationException(BuildInitializeErrorMessage(initResponse));

            Trace.TraceWarning(
                "MCP server '{0}' returned a transient server error during initialization; retrying once.",
                _definition.Name);
        }

        return initResponse.GetProperty("result").Clone();
    }

    internal static bool IsRetryableInitializeErrorResponse(JsonElement response)
        => response.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("code", out var code)
            && code.TryGetInt32(out var errorCode)
            && errorCode is <= -32_000 and >= -32_099;

    private string BuildInitializeErrorMessage(JsonElement response)
    {
        if (response.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(message.GetString()))
        {
            return $"MCP server '{_definition.Name}' failed to initialize: {message.GetString()!.Trim()}";
        }

        return $"MCP server '{_definition.Name}' did not return an initialize result.";
    }

    private async Task RecoverExpiredServerSessionAsync(int observedGeneration, CancellationToken cancellationToken)
    {
        using var initializeCts = CreateInitializeTimeoutSource(cancellationToken);
        var initializeCt = initializeCts.Token;
        await _lifecycleLock.WaitAsync(initializeCt).ConfigureAwait(false);
        try
        {
            await RecoverExpiredServerSessionUnderLockAsync(observedGeneration, initializeCt).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task RecoverExpiredServerSessionUnderLockAsync(int observedGeneration, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (_sessionRecoveryOutcomes.TryGetValue(observedGeneration, out var recoveryError))
        {
            if (recoveryError is not null)
                throw new IOException(recoveryError);
            return;
        }

        // Another caller may already have recovered the shared process while this request waited.
        if (observedGeneration != Volatile.Read(ref _processGeneration)
            && _initializeResult is not null
            && IsProcessRunning(_process))
            return;

        var attempts = Interlocked.Increment(ref _sessionRecoveryAttempts);
        try
        {
            InvalidateDiscovery();
            RestartProcessForExpiredSession();
            StartProcess();

            var initParams = _initializeParams ?? JsonRpc.DefaultInitializeParams();
            var initializedResult = await SendInitializeWithRetryAsync(initParams, cancellationToken).ConfigureAwait(false);
            await SendNotificationAsync("notifications/initialized", null, cancellationToken).ConfigureAwait(false);
            _initializeResult = initializedResult;
            _sessionRecoveryOutcomes[observedGeneration] = null;
            var successes = Interlocked.Increment(ref _sessionRecoverySuccesses);
            Trace.TraceInformation(
                "MCP server '{0}' session recovery succeeded. Attempts: {1}; successes: {2}; failures: {3}.",
                _definition.Name,
                attempts,
                successes,
                Volatile.Read(ref _sessionRecoveryFailures));
        }
        catch (Exception ex)
        {
            RestartProcessForExpiredSession();
            _sessionRecoveryOutcomes[observedGeneration] = ex.Message;
            var failures = Interlocked.Increment(ref _sessionRecoveryFailures);
            Trace.TraceWarning(
                "MCP server '{0}' session recovery failed. Attempts: {1}; successes: {2}; failures: {3}.",
                _definition.Name,
                attempts,
                Volatile.Read(ref _sessionRecoverySuccesses),
                failures);
            throw;
        }
    }

    internal static bool IsRecoverableSessionLossResponse(JsonElement response)
    {
        if (!response.TryGetProperty("error", out var error)
            || error.ValueKind != JsonValueKind.Object
            || !error.TryGetProperty("code", out var code)
            || !code.TryGetInt32(out var errorCode)
            || !error.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var errorMessage = message.GetString()?.Trim();
        return RecoverableSessionLossErrors.Any(signature =>
            errorCode == signature.Code
            && string.Equals(errorMessage, signature.Message, StringComparison.OrdinalIgnoreCase));
    }

    private static bool CanRetryAfterSessionInterruption(string? method)
        => method is "ping"
            or "tools/list"
            or "resources/list"
            or "resources/templates/list"
            or "resources/read"
            or "prompts/list"
            or "prompts/get"
            or "completion/complete";

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(_definition.Name);
    }

    private sealed class McpServerSessionRetiredException(string serverName, int processGeneration)
        : IOException($"MCP server '{serverName}' session expired.")
    {
        public int ProcessGeneration { get; } = processGeneration;
    }
}
