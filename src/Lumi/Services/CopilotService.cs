using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Models;
using Microsoft.Extensions.AI;

namespace Lumi.Services;

public enum CopilotSignInResult
{
    Success,
    CliNotFound,
    Failed,
}

public enum ConnectionState
{
    Disconnected,
    Connected,
    Error,
}

public readonly record struct ModelContextWindowLimits(long Default, long? LongContext);

public sealed record ModelContextWindowCatalog(
    IReadOnlySet<string> LongContextModelIds,
    IReadOnlyDictionary<string, ModelContextWindowLimits> Limits);

public class CopilotService : IAsyncDisposable
{
    private CopilotClient? _client;
    /// <summary>Exposes the underlying CopilotClient for advanced usage (e.g. test harness).</summary>
    public CopilotClient? Client => _client;
    private List<ModelInfo>? _models;
    private ModelContextWindowCatalog? _contextWindowCatalog;
    private string? _fastestModelId;
    private long _connectionGeneration;
    private ConnectionState _state = ConnectionState.Disconnected;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private Action? _cleanupProcessHandlers;
    private IDisposable? _lifecycleSub;
    private CopilotSession? _suggestionSession;
    private readonly SemaphoreSlim _suggestionGate = new(1, 1);

    /// <summary>Fires after the CopilotClient has been replaced (reconnection).
    /// Consumers should discard any cached CopilotSession objects.</summary>
    public event Action? Reconnected;

    /// <summary>Fires when the CLI process exits unexpectedly.
    /// Subscribers receive the connection generation at the time of the disconnect.</summary>
    public event Action<long>? CliProcessExited;

    /// <summary>Fires when a session is deleted on the server side (e.g. by another client).
    /// Subscribers receive the deleted session ID so they can detach cleanly.</summary>
    public event Action<string>? SessionDeletedRemotely;

    public bool IsConnected => _client is not null && State == ConnectionState.Connected;

    /// <summary>The current connection state of the underlying CopilotClient.
    /// Useful for UI indicators and fallback disconnect detection.</summary>
    public ConnectionState State => _client is null ? ConnectionState.Disconnected : _state;

    /// <summary>Monotonically increasing generation counter. Changes every time a
    /// new CopilotClient is created, allowing consumers to detect stale sessions.</summary>
    public long ConnectionGeneration => Interlocked.Read(ref _connectionGeneration);

    public async Task ConnectAsync(CancellationToken ct = default)
        => await ConnectCoreAsync(forceReconnect: false, allowCoalesce: true, ct);

    public async Task ForceReconnectAsync(CancellationToken ct = default)
        => await ConnectCoreAsync(forceReconnect: true, allowCoalesce: true, ct);

    /// <summary>
    /// Reconnects after the stored credential changed (sign-in / sign-out), guaranteeing a brand
    /// new client created strictly AFTER the credential write. Unlike <see cref="ForceReconnectAsync"/>
    /// this never coalesces with an in-flight reconnect: a reconnect that began before the credential
    /// changed may have read the PREVIOUS credential, and accepting it would strand the app on stale
    /// auth state — e.g. a fresh login still appearing logged out, or a logout still appearing signed in.
    /// </summary>
    public async Task ReconnectForCredentialChangeAsync(CancellationToken ct = default)
        => await ConnectCoreAsync(forceReconnect: true, allowCoalesce: false, ct);

    private async Task ConnectCoreAsync(bool forceReconnect, bool allowCoalesce, CancellationToken ct)
    {
        CopilotClient? oldClient = null;
        CopilotSession? oldSuggestionSession = null;

        // Capture generation before waiting — if another caller reconnects while
        // we're queued on the semaphore, we should skip instead of cascading.
        var generationBeforeWait = Interlocked.Read(ref _connectionGeneration);

        await _connectGate.WaitAsync(ct);
        try
        {
            if (!forceReconnect && IsConnected)
                return;

            // Another caller already reconnected while we were waiting on the gate. The client is
            // fresh and healthy — no need to create yet another one. This coalescing is SKIPPED for
            // credential-change reconnects (allowCoalesce == false): the winning reconnect may have
            // read the PREVIOUS credential and must not be accepted as our post-change client.
            if (forceReconnect && allowCoalesce
                && Interlocked.Read(ref _connectionGeneration) != generationBeforeWait
                && IsConnected)
                return;

            oldClient = _client;
            var cliPath = FindCliPath();
            var clientOptions = new CopilotClientOptions
            {
                Connection = RuntimeConnection.ForStdio(cliPath ?? "copilot"),
                LogLevel = CopilotLogLevel.Error,
            };

            ConfigureAuthentication(clientOptions);

            var newClient = new CopilotClient(clientOptions);
            await newClient.StartAsync(ct);

            _client = newClient;
            _state = ConnectionState.Connected;
            _models = null;
            _contextWindowCatalog = null;
            _fastestModelId = null;
            await _suggestionGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                oldSuggestionSession = _suggestionSession;
                _suggestionSession = null;
            }
            finally
            {
                _suggestionGate.Release();
            }
            Interlocked.Increment(ref _connectionGeneration);

            // Unsubscribe old process/RPC handlers before subscribing new ones
            _cleanupProcessHandlers?.Invoke();
            _cleanupProcessHandlers = null;
            _lifecycleSub?.Dispose();
            _lifecycleSub = null;

            // Watch the CLI process for unexpected exits
            SubscribeToCliProcessExit(newClient);

            // Subscribe to client-level session lifecycle events (e.g. remote deletion)
            _lifecycleSub = newClient.OnLifecycle<SessionDeletedEvent>(evt =>
            {
                if (!string.IsNullOrEmpty(evt.SessionId))
                    SessionDeletedRemotely?.Invoke(evt.SessionId);
            });
        }
        finally
        {
            _connectGate.Release();
        }

        if (oldSuggestionSession is not null)
            await DisposeAndDeleteSessionAsync(oldSuggestionSession).ConfigureAwait(false);

        // Dispose the old client (stops the old CLI process) after the new one is ready.
        if (oldClient is not null && !ReferenceEquals(oldClient, _client))
        {
            Reconnected?.Invoke();
            try { await oldClient.DisposeAsync(); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Tears down the current client and CLI process, leaving the service cleanly disconnected and
    /// still reusable (gates are not disposed). Used after a successful sign-out when the follow-up
    /// unauthenticated reconnect fails, to guarantee no authenticated session lingers in memory.
    /// </summary>
    public async Task DisconnectAsync()
    {
        CopilotClient? oldClient;
        CopilotSession? oldSuggestionSession;

        await _connectGate.WaitAsync();
        try
        {
            // Detach process/RPC watchers BEFORE dropping the client so the subsequent dispose can't
            // trip the CLI-process-exit handler into an auto-reconnect.
            _cleanupProcessHandlers?.Invoke();
            _cleanupProcessHandlers = null;
            _lifecycleSub?.Dispose();
            _lifecycleSub = null;

            oldClient = _client;
            _client = null;
            _state = ConnectionState.Disconnected;
            _models = null;
            _contextWindowCatalog = null;
            _fastestModelId = null;
            Interlocked.Increment(ref _connectionGeneration);
        }
        finally
        {
            _connectGate.Release();
        }

        await _suggestionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            oldSuggestionSession = _suggestionSession;
            _suggestionSession = null;
        }
        finally
        {
            _suggestionGate.Release();
        }

        if (oldSuggestionSession is not null)
        {
            // Best-effort: a failure tearing down the suggestion session must NOT prevent the
            // authenticated client below from being disposed (that would leave a signed-in CLI
            // process alive after a confirmed logout).
            try { await DisposeAndDeleteSessionAsync(oldSuggestionSession).ConfigureAwait(false); }
            catch { /* best-effort */ }
        }

        if (oldClient is not null)
        {
            try { await oldClient.DisposeAsync(); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Pings the CLI process with a timeout. Returns false if
    /// the client is missing, disconnected, or unresponsive.</summary>
    public async Task<bool> IsHealthyAsync(TimeSpan? timeout = null)
    {
        if (_client is null || State != ConnectionState.Connected)
            return false;
        try
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(8));
            await _client.PingAsync(cancellationToken: cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Uses reflection to reach the SDK's internal Process and JsonRpc
    /// objects and subscribes to their exit/disconnect events.
    /// When the CLI process dies, the service automatically reconnects and then
    /// fires <see cref="CliProcessExited"/> so consumers can update UI state.
    /// If reflection fails (SDK internals changed), falls back to periodic ping checks.</summary>
    private void SubscribeToCliProcessExit(CopilotClient client)
    {
        var gen = ConnectionGeneration;
        var fired = 0;
        void FireOnce()
        {
            if (Interlocked.CompareExchange(ref fired, 1, 0) != 0) return;
            if (gen == ConnectionGeneration)
                _state = ConnectionState.Disconnected;
            // Auto-reconnect at the service level, then notify consumers.
            // This keeps reconnect responsibility in CopilotService instead of
            // requiring every per-session handler to independently call ForceReconnectAsync.
            _ = AutoReconnectAndNotifyAsync(gen);
        }

        try
        {
            var bf = System.Reflection.BindingFlags.Instance
                   | System.Reflection.BindingFlags.NonPublic
                   | System.Reflection.BindingFlags.Public;

            // Path: CopilotClient._connectionTask.Result → Connection
            var connTaskField = client.GetType().GetField("_connectionTask", bf);
            if (connTaskField?.GetValue(client) is not Task connTask || !connTask.IsCompletedSuccessfully)
            {
                StartStatePollingFallback(client, gen, FireOnce);
                return;
            }

            var result = connTask.GetType().GetProperty("Result")?.GetValue(connTask);
            if (result is null)
            {
                StartStatePollingFallback(client, gen, FireOnce);
                return;
            }

            EventHandler? processHandler = null;
            Process? process = null;
            EventHandler<StreamJsonRpc.JsonRpcDisconnectedEventArgs>? rpcHandler = null;
            StreamJsonRpc.JsonRpc? jsonRpc = null;

            // Primary: Process.Exited — fires instantly when the OS process terminates
#pragma warning disable IL2075 // Reflection on non-annotated type — CliProcess/Rpc are internal SDK properties
            var processProp = result.GetType().GetProperty("CliProcess", bf);
            if (processProp?.GetValue(result) is Process cliProcess)
            {
                process = cliProcess;
                processHandler = (_, _) => FireOnce();
                cliProcess.EnableRaisingEvents = true;
                cliProcess.Exited += processHandler;
            }

            // Backup: JsonRpc.Disconnected — fires on RPC transport breaks
            var rpcProp = result.GetType().GetProperty("Rpc", bf);
#pragma warning restore IL2075
            if (rpcProp?.GetValue(result) is StreamJsonRpc.JsonRpc rpc)
            {
                jsonRpc = rpc;
                rpcHandler = (_, _) => FireOnce();
                rpc.Disconnected += rpcHandler;
            }

            // If we couldn't hook either signal, fall back to polling
            if (process is null && jsonRpc is null)
            {
                StartStatePollingFallback(client, gen, FireOnce);
                return;
            }

            _cleanupProcessHandlers = () =>
            {
                if (process is not null && processHandler is not null)
                    process.Exited -= processHandler;
                if (jsonRpc is not null && rpcHandler is not null)
                    jsonRpc.Disconnected -= rpcHandler;
            };
        }
        catch
        {
            // Reflection failed — SDK internals may have changed.
            // Fall back to polling the runtime as a last resort.
            StartStatePollingFallback(client, gen, FireOnce);
        }
    }

    /// <summary>Pings the runtime every 3 seconds as a fallback when reflection-based
    /// process exit detection is unavailable.</summary>
    private void StartStatePollingFallback(CopilotClient client, long gen, Action fireOnce)
    {
        var pollCts = new CancellationTokenSource();
        _cleanupProcessHandlers = () => pollCts.Cancel();

        _ = Task.Run(async () =>
        {
            try
            {
                while (!pollCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(3000, pollCts.Token);
                    if (gen != ConnectionGeneration) return;
                    using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(pollCts.Token);
                    pingCts.CancelAfter(TimeSpan.FromSeconds(5));

                    try
                    {
                        await client.PingAsync(cancellationToken: pingCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (pollCts.Token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch
                    {
                        fireOnce();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Auto-reconnects when the CLI process dies, then fires <see cref="CliProcessExited"/>
    /// so consumers can update UI state. The reconnect happens once at the service level
    /// instead of per-session, preventing cascade reconnections.</summary>
    private async Task AutoReconnectAndNotifyAsync(long exitedGeneration)
    {
        try
        {
            await ForceReconnectAsync();
        }
        catch
        {
            // Reconnect failed — consumers still need to know the CLI died.
            _state = ConnectionState.Error;
        }

        CliProcessExited?.Invoke(exitedGeneration);
    }

    public async Task<List<ModelInfo>> GetModelsAsync(CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        _models ??= (await _client.ListModelsAsync(ct)).ToList();
        return _models;
    }

    public async Task<IReadOnlySet<string>> GetLongContextModelIdsAsync(CancellationToken ct = default)
        => (await GetContextWindowCatalogAsync(ct).ConfigureAwait(false)).LongContextModelIds;

    public async Task<ModelContextWindowCatalog> GetContextWindowCatalogAsync(CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        if (_contextWindowCatalog is not null)
            return _contextWindowCatalog;

        try
        {
#pragma warning disable GHCP001
            var rawModels = await _client.Rpc.Models.ListAsync(null, ct).ConfigureAwait(false);
#pragma warning restore GHCP001
            var longContextModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var limits = new Dictionary<string, ModelContextWindowLimits>(StringComparer.OrdinalIgnoreCase);

            foreach (var model in rawModels.Models)
            {
                if (string.IsNullOrWhiteSpace(model.Id))
                    continue;

                var tokenPrices = model.Billing?.TokenPrices;
                var defaultContextMax = NormalizeContextMax(tokenPrices?.ContextMax);
                var longContextMax = NormalizeContextMax(tokenPrices?.LongContext?.ContextMax);
                if (longContextMax > 0)
                    longContextModelIds.Add(model.Id);

                if (defaultContextMax > 0 || longContextMax > 0)
                {
                    limits[model.Id] = new ModelContextWindowLimits(
                        defaultContextMax > 0 ? defaultContextMax : 0,
                        longContextMax > 0 ? longContextMax : null);
                }
            }

            _contextWindowCatalog = new ModelContextWindowCatalog(longContextModelIds, limits);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load context window model catalog: {ex}");
            _contextWindowCatalog = new ModelContextWindowCatalog(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, ModelContextWindowLimits>(StringComparer.OrdinalIgnoreCase));
        }

        return _contextWindowCatalog;
    }

    private static long NormalizeContextMax(double? tokens)
    {
        if (tokens is null || double.IsNaN(tokens.Value) || double.IsInfinity(tokens.Value) || tokens.Value <= 0)
            return 0;

        return (long)Math.Round(tokens.Value);
    }

    /// <summary>Returns the cheapest/fastest model ID from the cached model list.
    /// Uses billing multiplier as a proxy for speed — lower cost ≈ faster/lighter.
    /// Falls back to models whose name suggests a lightweight tier (e.g. "mini", "flash").</summary>
    public async Task<string?> GetFastestModelIdAsync(CancellationToken ct = default)
    {
        if (_fastestModelId is not null) return _fastestModelId;
        try
        {
            var models = await GetModelsAsync(ct);

            // Primary: lowest billing multiplier
            _fastestModelId = models
                .Where(m => m.Billing is not null)
                .OrderBy(m => m.Billing!.Multiplier)
                .FirstOrDefault()?.Id;

            // Fallback: pick a model whose name hints at a lightweight tier
            _fastestModelId ??= models
                .FirstOrDefault(m =>
                    m.Id.Contains("mini", StringComparison.OrdinalIgnoreCase) ||
                    m.Id.Contains("flash", StringComparison.OrdinalIgnoreCase) ||
                    m.Id.Contains("haiku", StringComparison.OrdinalIgnoreCase))?.Id;

            // Last resort: just use the first available model
            _fastestModelId ??= models.FirstOrDefault()?.Id;
        }
        catch { /* best effort — fall back to default model */ }
        return _fastestModelId;
    }

    /// <summary>Creates a new Copilot session with the given configuration.</summary>
    public async Task<CopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        return await _client.CreateSessionAsync(config, ct);
    }

    /// <summary>
    /// Runs a one-shot lightweight helper session and always deletes the session afterwards.
    /// The Copilot SDK does not expose a public transient-session API, so helper flows must
    /// explicitly create, use, dispose, and delete ordinary sessions to avoid polluting history.
    /// </summary>
    public async Task<TResult> UseLightweightSessionAsync<TResult>(
        LightweightSessionOptions options,
        Func<CopilotSession, CancellationToken, Task<TResult>> operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(operation);

        var session = await CreateSessionAsync(SessionConfigBuilder.BuildLightweight(options), ct).ConfigureAwait(false);
        try
        {
            return await operation(session, ct).ConfigureAwait(false);
        }
        finally
        {
            await DisposeAndDeleteSessionAsync(session).ConfigureAwait(false);
        }
    }

    public async Task UseLightweightSessionAsync(
        LightweightSessionOptions options,
        Func<CopilotSession, CancellationToken, Task> operation,
        CancellationToken ct = default)
    {
        await UseLightweightSessionAsync(
            options,
            async (session, innerCt) =>
            {
                await operation(session, innerCt).ConfigureAwait(false);
                return true;
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>Resumes an existing Copilot session by ID.</summary>
    public async Task<CopilotSession> ResumeSessionAsync(
        string sessionId, ResumeSessionConfig config, CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        return await _client.ResumeSessionAsync(sessionId, config, ct);
    }

    /// <summary>
    /// Initiates the interactive OAuth login flow for a remote MCP server and opens the returned
    /// authorization URL in the system browser.
    /// </summary>
    /// <remarks>
    /// The SDK only drives interactive MCP OAuth when a consumer asks for it — without this call the
    /// runtime installs a browserless fallback that can merely reuse an already-cached token. The
    /// returned URL is empty when a cached token already authenticated the server (no browser needed);
    /// when non-empty, the runtime starts the loopback callback listener before returning and finishes
    /// the flow in the background, signalling completion via the session's
    /// <c>mcp_server_status_changed</c> event.
    /// </remarks>
    /// <returns>The authorization URL that was opened, or an empty string when no browser step was required.</returns>
    public async Task<string> StartMcpOAuthLoginAsync(
        CopilotSession session,
        string serverName,
        bool forceReauth = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(serverName))
            return string.Empty;

        var result = await session.Rpc.Mcp.Oauth.LoginAsync(
            serverName,
            forceReauth,
            clientName: "Lumi",
            callbackSuccessMessage: $"Signed in to \"{serverName}\". You can return to Lumi — the MCP server will reconnect automatically.",
            ct).ConfigureAwait(false);

        var url = result?.AuthorizationUrl;
        if (!string.IsNullOrWhiteSpace(url))
            OpenBrowser(url);

        return url ?? string.Empty;
    }

    /// <summary>Lists all known sessions, optionally filtered.</summary>
    public async Task<List<SessionMetadata>> ListSessionsAsync(
        SessionListFilter? filter = null, CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        return (await _client.ListSessionsAsync(filter, ct)).ToList();
    }

    public async Task<GetAuthStatusResponse> GetAuthStatusAsync(CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        return await _client.GetAuthStatusAsync(ct);
    }

    public string? GetStoredLogin()
    {
        try
        {
            return GetStoredCopilotIdentity().Login;
        }
        catch
        {
            return null;
        }
    }

    // ── Plan API ──

    /// <summary>Reads the current plan content from the session.</summary>
    public async Task<(bool Exists, string? Content)> ReadSessionPlanAsync(
        CopilotSession session, CancellationToken ct = default)
    {
        var result = await session.Rpc.Plan.ReadAsync(ct);
        return (result.Exists == true, result.Content);
    }

    /// <summary>Updates the plan content for the session.</summary>
    public async Task UpdateSessionPlanAsync(
        CopilotSession session, string content, CancellationToken ct = default)
    {
        await session.Rpc.Plan.UpdateAsync(content, ct);
    }

    /// <summary>Deletes the current plan from the session.</summary>
    public async Task DeleteSessionPlanAsync(
        CopilotSession session, CancellationToken ct = default)
    {
        await session.Rpc.Plan.DeleteAsync(ct);
    }

    // ── Account API ──

    /// <summary>Gets the current account quota information.</summary>
    public async Task<GitHub.Copilot.Rpc.AccountGetQuotaResult?> GetAccountQuotaAsync(CancellationToken ct = default)
    {
        if (_client is null) return null;
        return await _client.Rpc.Account.GetQuotaAsync(cancellationToken: ct);
    }

    // ── Tools API ──

    /// <summary>Lists all available tools for the current model.</summary>
    public async Task<List<GitHub.Copilot.Rpc.Tool>> ListToolsAsync(string? model = null, CancellationToken ct = default)
    {
        if (_client is null) return [];
        var result = await _client.Rpc.Tools.ListAsync(model, ct);
        return result.Tools.ToList();
    }

    /// <summary>
    /// Launches the Copilot CLI login flow (OAuth device flow) and waits for completion.
    /// When <paramref name="onDeviceCode"/> is provided, stdout/stderr are captured to
    /// extract the one-time device code and verification URL, which are passed to the
    /// callback. The browser is opened automatically.
    /// When <paramref name="onDeviceCode"/> is null, falls back to UseShellExecute (legacy).
    /// </summary>
    public async Task<CopilotSignInResult> SignInAsync(
        Action<string, string>? onDeviceCode = null,
        CancellationToken ct = default)
    {
        var cliPath = FindCliPath();
        if (cliPath is null) return CopilotSignInResult.CliNotFound;

        if (onDeviceCode is null)
        {
            // Legacy path: let the CLI handle everything
            var legacyPsi = new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = "login",
                UseShellExecute = true,
            };
            using var legacyProcess = Process.Start(legacyPsi);
            if (legacyProcess is null) return CopilotSignInResult.Failed;
            await legacyProcess.WaitForExitAsync(ct);
            if (legacyProcess.ExitCode != 0) return CopilotSignInResult.Failed;
            // Build a fresh client that loads the just-written credential (see ReconnectAfterSignInAsync).
            await ReconnectAfterSignInAsync(ct);
            return CopilotSignInResult.Success;
        }

        // Capture stdout/stderr to extract device code
        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = "login",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null) return CopilotSignInResult.Failed;

        string? deviceCode = null;
        string? verificationUrl = null;
        int notified = 0; // 0 = not yet, 1 = done (atomic flag)
        var parseLock = new object();

        void ProcessLine(string line)
        {
            lock (parseLock)
            {
                ParseDeviceCodeLine(line, ref deviceCode, ref verificationUrl);

                if (deviceCode is not null && verificationUrl is not null
                    && Interlocked.CompareExchange(ref notified, 1, 0) == 0)
                {
                    var code = deviceCode;
                    var url = verificationUrl;
                    onDeviceCode(code, url);
                    OpenBrowser(url);
                }
            }
        }

        // Read both stdout and stderr — CLI may write to either
        var outputTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                ProcessLine(line);
            }
        }, ct);

        var errorTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(ct)) is not null)
            {
                ProcessLine(line);
            }
        }, ct);

        // Send Enter to proceed past "Press Enter to open..." prompt
        try
        {
            await Task.Delay(500, ct);
            await process.StandardInput.WriteLineAsync();
        }
        catch { /* best-effort */ }

        await Task.WhenAll(outputTask, errorTask);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            return CopilotSignInResult.Failed;

        // Build a fresh client that loads the just-written credential (see ReconnectAfterSignInAsync).
        await ReconnectAfterSignInAsync(ct);
        return CopilotSignInResult.Success;
    }

    /// <summary>
    /// Reconnects after a successful sign-in so the new client loads the freshly written credential.
    /// Uses a non-coalescing reconnect so an older in-flight reconnect that read the pre-login
    /// credential cannot satisfy it (which would leave a fresh login still looking logged out).
    /// Sign-in already succeeded once the credential is on disk, so a transient failure to spin up
    /// the new client is intentionally swallowed rather than reported as a sign-in failure — the
    /// previous no-op <c>ConnectAsync</c> never threw here. Cancellation still propagates, and the
    /// caller's auth-status refresh reconciles the real connection state (it never fabricates a
    /// logged-in state).
    /// </summary>
    private async Task ReconnectAfterSignInAsync(CancellationToken ct)
    {
        try
        {
            await ReconnectForCredentialChangeAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Credential is valid; connection state is reconciled by the follow-up refresh.
        }
    }

    private static void ParseDeviceCodeLine(string line, ref string? deviceCode, ref string? verificationUrl)
    {
        // The CLI outputs lines like:
        //   "First copy your one-time code: XXXX-XXXX"
        //   "Open https://github.com/login/device in your browser"
        // or sometimes a combined message. We look for common patterns.

        // Look for a code pattern like ABCD-ABCD (4+ alphanum, dash, 4+ alphanum)
        if (deviceCode is null)
        {
            var codeMatch = System.Text.RegularExpressions.Regex.Match(
                line, @"\b([A-Z0-9]{4,}-[A-Z0-9]{4,})\b");
            if (codeMatch.Success)
                deviceCode = codeMatch.Groups[1].Value;
        }

        // Look for a URL containing "login/device" or "github.com"
        if (verificationUrl is null)
        {
            var urlMatch = System.Text.RegularExpressions.Regex.Match(
                line, @"(https?://\S+)");
            if (urlMatch.Success)
                verificationUrl = urlMatch.Groups[1].Value;
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Launches the Copilot CLI logout flow and reconnects without credentials.
    /// </summary>
    public async Task<bool> SignOutAsync(CancellationToken ct = default)
    {
        var cliPath = FindCliPath();
        if (cliPath is null) return false;

        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = "logout",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null) return false;

        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            return false;

        // Logout succeeded: the stored credential is gone, so the user IS signed out regardless of
        // what happens next. Tear down the authenticated client immediately so a signed-in session
        // can never linger, then rebuild on the fresh unauthenticated state as a best-effort step. A
        // failed (or cancelled) reconnect must NOT report logout as failed — that would leave the UI
        // showing "signed in" over a wiped credential (the unsafe direction).
        await DisconnectAsync();
        try
        {
            await ReconnectForCredentialChangeAsync(ct);
        }
        catch
        {
            // Best-effort: already disconnected above, so any reconnect failure is safe to ignore.
        }
        return true;
    }

    private static string? FindCliPath()
    {
        var binary = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "copilot.exe" : "copilot";
        var appDir = AppContext.BaseDirectory;

        // Check runtimes/{rid}/native/ (standard SDK output location)
        var rid = RuntimeInformation.RuntimeIdentifier;
        var runtimePath = Path.Combine(appDir, "runtimes", rid, "native", binary);
        if (File.Exists(runtimePath)) return runtimePath;

        // Fallback: try portable rid
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
                 RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var portablePath = Path.Combine(appDir, "runtimes", $"{os}-{arch}", "native", binary);
        if (File.Exists(portablePath)) return portablePath;

        // Fallback: check app directory directly
        var directPath = Path.Combine(appDir, binary);
        if (File.Exists(directPath)) return directPath;

        return null;
    }

    private static void ConfigureAuthentication(CopilotClientOptions options)
    {
        var token = TryGetGitHubTokenForMcp();
        if (!string.IsNullOrWhiteSpace(token))
        {
            options.GitHubToken = token;
            options.Environment = BuildCliEnvironmentWithGitHubToken(token);
            options.UseLoggedInUser = false;
            return;
        }

        options.UseLoggedInUser = true;
    }

    /// <summary>
    /// Detects <em>provably</em> transient, server-side failures that are safe to retry with the
    /// <em>same</em> credential. GitHub's backend occasionally wraps an internal RPC failure in a
    /// <c>401</c> (e.g. <c>twirp error internal: ... failed to do request</c> against the
    /// <c>usersd</c> user-service) on long-running sessions; the stored token is still valid and a
    /// plain resend (what a manual "continue" does) succeeds once the backend recovers.
    /// <para>
    /// This is an ALLOW-LIST by design: it returns <c>true</c> ONLY when the text carries an
    /// unambiguous backend-internal / transient marker. Everything else — a bare
    /// <c>401 unauthorized</c>, a bare <c>403 forbidden</c>, an empty body, an expired/revoked/SSO
    /// message, or <c>AuthenticateToken authentication failed</c> on its own — returns <c>false</c>
    /// so a genuine logout SURFACES and routes the user to re-authenticate instead of being masked
    /// behind a silent retry loop. On the login path this asymmetric default is the only safe one:
    /// a misclassified transient error costs the user one manual retry, whereas a misclassified
    /// logout strands them.
    /// </para>
    /// </summary>
    internal static bool IsTransientServerAuthError(string? errorText)
    {
        if (string.IsNullOrWhiteSpace(errorText))
            return false;

        var text = errorText.ToLowerInvariant();

        // Unambiguous GitHub backend-internal RPC failures (observed verbatim in CLI logs as a 401
        // wrapping a twirp/usersd error) plus plain 5xx-style transient phrases. Auth status words
        // (401/403/unauthorized/forbidden) are deliberately NOT markers on their own, because a
        // genuine logout looks identical and must be allowed to surface.
        return text.Contains("twirp error internal")
            || text.Contains("failed to do request")
            || text.Contains("usersd")
            || text.Contains("service unavailable")
            || text.Contains("temporarily unavailable")
            || text.Contains("gateway timeout");
    }

    /// <summary>
    /// Structured variant for SDK <c>session.error</c> events, which expose the upstream HTTP
    /// <paramref name="statusCode"/> and error <paramref name="errorType"/> ("authentication",
    /// "authorization", "quota", "rate_limit", "context_limit", "query") alongside the message.
    /// <para>
    /// Like the string overload this is an ALLOW-LIST: a 401/403 status or an
    /// "authentication"/"authorization" category is NOT transient on its own — only a provable
    /// backend-internal marker in the message is. This guarantees a genuine logout (bare 401/403,
    /// empty body, expired/revoked/SSO) surfaces so the user can sign in again. Quota / rate-limit /
    /// context errors (which can legitimately contain phrases like "try again later") have dedicated
    /// handling and are never routed through the auth-retry path.
    /// </para>
    /// </summary>
    internal static bool IsTransientServerAuthError(int? statusCode, string? errorType, string? message)
    {
        var type = errorType?.ToLowerInvariant();

        // Quota / rate-limit / context errors have dedicated handling (e.g. auto model switch) and
        // can carry transient-sounding wording; never treat them as an auth blip.
        if (type is "quota" or "rate_limit" or "context_limit")
            return false;

        // The status code / category alone is never enough to call an auth failure "transient" —
        // require a provable backend-internal marker in the message.
        return IsTransientServerAuthError(message);
    }

    internal static string? TryGetGitHubTokenForMcp()
    {
        foreach (var name in new[]
        {
            "GITHUB_PERSONAL_ACCESS_TOKEN",
            "GITHUB_COPILOT_GITHUB_TOKEN",
        })
        {
            var token = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(token))
                return token;
        }

        return TryReadStoredGitHubToken();
    }

    private static IReadOnlyDictionary<string, string> BuildCliEnvironmentWithGitHubToken(string token)
    {
        var env = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(entry => entry.Key is string && entry.Value is string)
            .ToDictionary(
                entry => (string)entry.Key,
                entry => (string)entry.Value!,
                StringComparer.OrdinalIgnoreCase);

        env["GITHUB_PERSONAL_ACCESS_TOKEN"] = token;
        env["GITHUB_COPILOT_GITHUB_TOKEN"] = token;

        return env;
    }

    internal static string? TryReadStoredGitHubToken()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        try
        {
            var identity = GetStoredCopilotIdentity();
            if (string.IsNullOrWhiteSpace(identity.Login) || string.IsNullOrWhiteSpace(identity.Host))
                return null;

            var credentialBytes = ReadGenericCredential($"copilot-cli/{identity.Host}:{identity.Login}");
            return ExtractTokenFromCredential(credentialBytes);
        }
        catch
        {
            return null;
        }
    }

    private static (string? Login, string? Host) GetStoredCopilotIdentity()
    {
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot",
            "config.json");

        if (!File.Exists(configPath))
            return default;

        return ParseStoredCopilotIdentity(File.ReadAllText(configPath));
    }

    internal static (string? Login, string? Host) ParseStoredCopilotIdentity(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        if ((!document.RootElement.TryGetProperty("last_logged_in_user", out var lastUser)
                && !document.RootElement.TryGetProperty("lastLoggedInUser", out lastUser))
            || lastUser.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        return (
            lastUser.TryGetProperty("login", out var login) ? login.GetString() : null,
            lastUser.TryGetProperty("host", out var host) ? host.GetString() : null);
    }

    private static string? ExtractTokenFromCredential(byte[]? credentialBytes)
    {
        if (credentialBytes is not { Length: > 0 })
            return null;

        foreach (var decoded in new[]
        {
            Encoding.UTF8.GetString(credentialBytes).Trim('\0'),
            Encoding.Unicode.GetString(credentialBytes).Trim('\0'),
        })
        {
            if (string.IsNullOrWhiteSpace(decoded))
                continue;

            try
            {
                using var document = JsonDocument.Parse(decoded);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var propertyName in new[] { "access_token", "oauth_token", "token" })
                {
                    if (!document.RootElement.TryGetProperty(propertyName, out var property)
                        || property.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var token = property.GetString();
                    if (!string.IsNullOrWhiteSpace(token))
                        return token;
                }
            }
            catch
            {
                if (decoded.All(ch => ch >= ' ' && ch <= '~'))
                    return decoded;
            }
        }

        return null;
    }

    private static byte[]? ReadGenericCredential(string target)
    {
        if (!CredRead(target, 1, 0, out var credentialPtr))
            return null;

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
                return [];

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr cred);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    /// <summary>Generates a short chat title using a lightweight session with the fastest model.</summary>
    public async Task<string?> GenerateTitleAsync(string userMessage, CancellationToken ct = default)
    {
        if (_client is null) return null;

        var fastModel = await GetFastestModelIdAsync(ct).ConfigureAwait(false);
        var systemContent = $"""
            Generate a short title (3-6 words) for a chat that starts with this message. Output ONLY the title text, nothing else.

            User: {Truncate(userMessage, 500)}
            """;

        var rawTitle = await UseLightweightSessionAsync(
            new LightweightSessionOptions
            {
                SystemPrompt = systemContent,
                Model = fastModel,
                Streaming = false
            },
            async (session, innerCt) =>
            {
                var result = await session.SendAndWaitAsync(
                    new MessageOptions { Prompt = "title:" },
                    TimeSpan.FromSeconds(15), innerCt).ConfigureAwait(false);
                return result?.Data?.Content;
            },
            ct).ConfigureAwait(false);

        return rawTitle?.Trim().Trim('"', '\'', '.', '!');
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength];

    private const string SuggestionSystemPrompt =
        "You generate follow-up suggestions for a chat assistant. Use the latest conversation plus the user's historical request patterns across chats. Prefer recurring historical requests when they are plausible next actions. Produce exactly 3 short messages the user might want to send next. Each suggestion must be concise (under 60 characters) and actionable. Output ONLY a JSON array of 3 strings, nothing else. Example: [\"Run code review\", \"Push to main\", \"What are the alternatives?\"]";

    private async Task<CopilotSession> GetOrCreateSuggestionSessionAsync(CancellationToken ct)
    {
        if (_suggestionSession is not null)
            return _suggestionSession;

        var fastModel = await GetFastestModelIdAsync(ct).ConfigureAwait(false);
        _suggestionSession = await _client!.CreateSessionAsync(
            SessionConfigBuilder.BuildLightweight(new LightweightSessionOptions
            {
                SystemPrompt = SuggestionSystemPrompt,
                Model = fastModel,
                Streaming = false
            }),
            ct).ConfigureAwait(false);
        return _suggestionSession;
    }

    public Task<List<string>?> GenerateSuggestionsAsync(
        string assistantMessage,
        string? userMessage,
        CancellationToken ct = default)
        => GenerateSuggestionsAsync(assistantMessage, userMessage, userHistorySummary: null, ct);

    public async Task<List<string>?> GenerateSuggestionsAsync(
        string assistantMessage,
        string? userMessage,
        string? userHistorySummary,
        CancellationToken ct = default)
    {
        if (_client is null) return null;

        var contextBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(userMessage))
        {
            contextBuilder.AppendLine("Latest user message:");
            contextBuilder.AppendLine(Truncate(userMessage.Trim(), 800));
            contextBuilder.AppendLine();
        }

        contextBuilder.AppendLine("Latest assistant message:");
        contextBuilder.AppendLine(Truncate(assistantMessage.Trim(), 1400));

        if (!string.IsNullOrWhiteSpace(userHistorySummary))
        {
            contextBuilder.AppendLine();
            contextBuilder.AppendLine("Historical user requests across all chats:");
            contextBuilder.AppendLine(Truncate(userHistorySummary.Trim(), 1400));
        }

        var context = contextBuilder.ToString().Trim();

        // Truncate to keep the request lightweight
        if (context.Length > 3600)
            context = context[..3600];

        await _suggestionGate.WaitAsync(ct).ConfigureAwait(false);
        CopilotSession? session = null;
        try
        {
            session = await GetOrCreateSuggestionSessionAsync(ct).ConfigureAwait(false);
            var result = await session.SendAndWaitAsync(
                 new MessageOptions { Prompt = context },
                 TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            return ParseSuggestions(result?.Data?.Content);
        }
        catch
        {
            // Session may be stale — discard so next call creates a fresh one
            if (session is not null)
            {
                if (ReferenceEquals(_suggestionSession, session))
                    _suggestionSession = null;
                await DisposeAndDeleteSessionAsync(session).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            _suggestionGate.Release();
        }
    }

    private static List<string>? ParseSuggestions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Trim();

        // Strip markdown code fences if present (e.g., ```json ... ```)
        if (raw.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = raw.IndexOf('\n');
            if (firstNewline > 0)
                raw = raw[(firstNewline + 1)..];
            if (raw.EndsWith("```", StringComparison.Ordinal))
                raw = raw[..^3];
            raw = raw.Trim();
        }

        var suggestions = TryDeserializeSuggestions(raw);
        if (suggestions is null)
        {
            var arrayStart = raw.IndexOf('[');
            var arrayEnd = raw.LastIndexOf(']');
            if (arrayStart >= 0 && arrayEnd > arrayStart)
                suggestions = TryDeserializeSuggestions(raw[arrayStart..(arrayEnd + 1)]);
        }

        if (suggestions is null || suggestions.Count == 0)
            return null;

        var normalized = suggestions
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();

        return normalized.Count > 0 ? normalized : null;
    }

    private static List<string>? TryDeserializeSuggestions(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize(raw, Lumi.Models.AppDataJsonContext.Default.ListString);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id is required.", nameof(sessionId));

        ExceptionDispatchInfo? remoteDeleteError = null;

        if (_client is not null)
        {
            try
            {
                await _client.DeleteSessionAsync(sessionId, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (IsAlreadyDeletedSessionError(ex))
            {
                // SDK 1.0 non-persistent helper sessions can have no server-side session file.
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                remoteDeleteError = ExceptionDispatchInfo.Capture(ex);
            }
        }

        DeleteLocalSessionStateDirectory(sessionId);
        remoteDeleteError?.Throw();
    }

    private static bool IsAlreadyDeletedSessionError(InvalidOperationException ex)
        => ex.Message.Contains("Session file not found", StringComparison.OrdinalIgnoreCase);

    private static void DeleteLocalSessionStateDirectory(string sessionId)
    {
        var sessionDirectory = GetLocalSessionStateDirectory(sessionId);
        try
        {
            Directory.Delete(sessionDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already deleted by the SDK, a concurrent cleanup, or an external process.
        }
        catch (IOException)
        {
            // Best-effort cleanup: logs may still be held briefly by the runtime.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup: preserve delete idempotency if local files are locked down.
        }
    }

    private static string GetLocalSessionStateDirectory(string sessionId)
    {
        if (Path.IsPathFullyQualified(sessionId)
            || !string.Equals(Path.GetFileName(sessionId), sessionId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Session id must be a single path segment.", nameof(sessionId));
        }

        var baseDirectory = Path.GetFullPath(Path.Combine(DataStore.CopilotConfigDir, "session-state"));
        var sessionDirectory = Path.GetFullPath(Path.Combine(baseDirectory, sessionId));
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!sessionDirectory.StartsWith(baseDirectory + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Session id resolves outside the session-state directory.", nameof(sessionId));

        return sessionDirectory;
    }

    public async Task DisposeAndDeleteSessionAsync(CopilotSession? session)
    {
        if (session is null)
            return;

        var sessionId = session.SessionId;
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
                await DeleteSessionAsync(sessionId).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cleanupProcessHandlers?.Invoke();
        _cleanupProcessHandlers = null;
        _lifecycleSub?.Dispose();
        _lifecycleSub = null;

        CopilotSession? suggestionSession = null;
        await _suggestionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            suggestionSession = _suggestionSession;
            _suggestionSession = null;
        }
        finally
        {
            _suggestionGate.Release();
        }

        if (suggestionSession is not null)
            await DisposeAndDeleteSessionAsync(suggestionSession).ConfigureAwait(false);

        if (_client is not null)
        {
            try
            {
                // StopAsync gracefully closes sessions and the CLI process.
                await _client.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Graceful stop failed — force kill the CLI process.
                try { await _client.ForceStopAsync(); }
                catch { /* best-effort */ }
            }
            finally
            {
                _state = ConnectionState.Disconnected;
            }
        }
    }
}
