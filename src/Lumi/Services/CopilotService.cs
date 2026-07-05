using System;
using System.Collections.Concurrent;
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

/// <summary>
/// The outcome of classifying a failed send: whether it is <see cref="Recoverable"/> (abandon the
/// poisoned session and rebuild from the transcript as text, offering Retry) and, when it is,
/// whether the recovery copy should name an <see cref="IsImageError"/> (an image the backend could
/// not process). Produced by <see cref="CopilotService.ClassifySendFailure"/> so BOTH failure entry
/// points — the exception path (<c>HandleSendError</c>) and the structured <c>session.error</c> path
/// (<c>SessionErrorEvent</c>) — reach an identical verdict from a single place.
/// </summary>
internal readonly record struct SendFailureClassification(bool Recoverable, bool IsImageError);

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

    // Tracks in-flight session releases keyed by SERVER SESSION ID, shared across every
    // ChatViewModel surface. DisposeAsync sends session.destroy scoped to the session id but leaves
    // it resumable, so a destroy started by one (e.g. disposed/evicted) surface can still be in
    // flight when a *different* surface resumes the same id — and a late destroy would tear down the
    // freshly resumed live session. ResumeSessionAsync awaits any matching pending release first,
    // sequencing destroy-before-resume across surfaces. Concurrent because releases and resumes run
    // from independent surfaces / continuations.
    private readonly ConcurrentDictionary<string, Task> _pendingReleasesBySessionId = new(StringComparer.Ordinal);

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

    /// <summary>Known fast, low-latency models preferred for lightweight background work
    /// (chat titles and follow-up suggestions), in priority order. The first one the
    /// signed-in account can actually access wins; if none are available we fall back to a
    /// cost-based heuristic. gpt-5.4-mini leads because it is fast and produces good
    /// lightweight output.</summary>
    internal static readonly string[] PreferredFastModelIds =
    {
        "gpt-5.4-mini",
        "gpt-5-mini",
        "gemini-3.5-flash",
        "claude-haiku-4.5",
    };

    /// <summary>Returns the model ID to use for fast/lightweight sessions (titles, suggestions).
    /// Prefers a known fast model (<see cref="PreferredFastModelIds"/>) when the account has
    /// access; otherwise uses the lowest billing multiplier as a proxy for speed, then a
    /// lightweight-sounding name (e.g. "mini", "flash"), then the first available model.</summary>
    public async Task<string?> GetFastestModelIdAsync(CancellationToken ct = default)
    {
        if (_fastestModelId is not null) return _fastestModelId;
        try
        {
            var models = await GetModelsAsync(ct);
            var candidates = models
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .Select(m => (m.Id, Multiplier: m.Billing is null ? (double?)null : Convert.ToDouble(m.Billing.Multiplier)))
                .ToList();
            _fastestModelId = SelectFastestModelId(candidates, PreferredFastModelIds);
        }
        catch { /* best effort — fall back to default model */ }
        return _fastestModelId;
    }

    /// <summary>Pure selection logic for <see cref="GetFastestModelIdAsync"/>, separated so it
    /// can be unit-tested without a live connection. <paramref name="models"/> is the available
    /// model list (id + optional billing multiplier); <paramref name="preferredIds"/> is the
    /// priority allowlist of known-fast models.</summary>
    internal static string? SelectFastestModelId(
        IReadOnlyList<(string Id, double? Multiplier)> models,
        IReadOnlyList<string> preferredIds)
    {
        if (models is null || models.Count == 0)
            return null;

        // Preferred: first known-fast model the account can access, in priority order.
        foreach (var preferred in preferredIds)
        {
            var match = models.FirstOrDefault(m =>
                string.Equals(m.Id, preferred, StringComparison.OrdinalIgnoreCase));
            if (match.Id is not null)
                return match.Id;
        }

        // Lowest billing multiplier — cheapest ≈ lightest/fastest.
        var cheapest = models
            .Where(m => m.Multiplier is not null)
            .OrderBy(m => m.Multiplier!.Value)
            .Select(m => m.Id)
            .FirstOrDefault();
        if (cheapest is not null)
            return cheapest;

        // Name hints at a lightweight tier.
        var lightweight = models.FirstOrDefault(m =>
            m.Id.Contains("mini", StringComparison.OrdinalIgnoreCase) ||
            m.Id.Contains("flash", StringComparison.OrdinalIgnoreCase) ||
            m.Id.Contains("haiku", StringComparison.OrdinalIgnoreCase)).Id;
        if (lightweight is not null)
            return lightweight;

        // Last resort: first available model.
        return models[0].Id;
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
    /// <remarks>
    /// THREADING INVARIANT: callers are expected to be UI-thread-serialized with
    /// <see cref="ReleaseSessionAsync"/> — Lumi drives all Copilot session lifecycle (create,
    /// resume, release) on the Avalonia UI thread, which is also why <c>ChatViewModel._sessionCache</c>
    /// and <c>_sessionReleaseTasks</c> can be plain (non-concurrent) dictionaries. That serialization
    /// is what lets the destroy-before-resume gate below stay lock-free: on a single thread a release
    /// publishes itself into <c>_pendingReleasesBySessionId</c> synchronously before returning, so a
    /// later resume of the SAME id always observes the in-flight destroy and waits for it here. If
    /// this is ever called concurrently with a release from a DIFFERENT thread, the registry alone
    /// does not make check-then-resume atomic against a racing release — add per-session-id
    /// serialization (e.g. a keyed SemaphoreSlim spanning both release and resume) at that point.
    /// </remarks>
    public async Task<CopilotSession> ResumeSessionAsync(
        string sessionId, ResumeSessionConfig config, CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        // Sequence destroy-before-resume across surfaces: if any surface is still releasing this
        // exact server session, wait for that destroy to complete before resuming, otherwise the
        // late destroy could reap the session we are about to hand back live.
        await AwaitPendingReleaseAsync(sessionId, ct).ConfigureAwait(false);
        return await _client.ResumeSessionAsync(sessionId, config, ct);
    }

    /// <summary>
    /// Releases a dropped session (sends <c>session.destroy</c>, reaping its host process + MCP
    /// subprocesses) while registering the in-flight release by server session id so a concurrent
    /// <see cref="ResumeSessionAsync"/> of the same id — potentially from a different ChatViewModel
    /// surface — waits for the destroy to finish first. Exceptions are swallowed so best-effort
    /// fire-and-forget releases can never fault their caller. When <paramref name="deleteServerSession"/>
    /// is true the on-disk session data is also deleted (not just destroyed/resumable).
    /// </summary>
    /// <remarks>
    /// THREADING INVARIANT: expected to run on the same (UI) thread that drives
    /// <see cref="ResumeSessionAsync"/>. Two properties depend on it and would otherwise require a
    /// per-session-id lock: (1) the release is published into <c>_pendingReleasesBySessionId</c>
    /// synchronously right after <c>session.destroy</c> is dispatched (no <c>await</c> in between),
    /// so a same-thread resume can never observe the destroy without also observing its registry
    /// entry; and (2) only one surface holds a given server session at a time, so releases of the
    /// same id never overlap (which is why awaiting the single tracked task per id is sufficient).
    /// A note on hung destroys: <see cref="AwaitPendingReleaseAsync"/> waits only as long as the
    /// caller's cancellation token allows. In session setup that token is the turn's cancellation
    /// plus a 30s bound ONLY when the chat has MCP servers — a chat without MCP servers has no
    /// automatic bound, so a destroy that never completes blocks resume of that same id until the
    /// turn is cancelled. On timeout/cancel the wait surfaces cancellation out of session setup (the
    /// turn fails) rather than auto-creating a fresh session, and the stale registry entry clears
    /// only when the hung destroy finally settles or at process exit. This needs a live-but-
    /// unresponsive CLI to occur (a dead transport faults fast and self-cleans), so it is a rare
    /// degradation of that one id, not an app deadlock.
    /// </remarks>
    public Task ReleaseSessionAsync(CopilotSession session, bool deleteServerSession)
    {
        ArgumentNullException.ThrowIfNull(session);

        var sessionId = session.SessionId;
        var releaseTask = ReleaseSessionCoreAsync(session, deleteServerSession);

        if (string.IsNullOrWhiteSpace(sessionId))
            return releaseTask;

        _pendingReleasesBySessionId[sessionId] = releaseTask;
        // Remove our own entry when the release settles. The KeyValuePair overload only removes when
        // the tracked task is still THIS task, so a newer release that overwrote the entry is kept.
        _ = releaseTask.ContinueWith(
            static (completed, state) =>
            {
                var (map, key) = ((ConcurrentDictionary<string, Task>, string))state!;
                map.TryRemove(new KeyValuePair<string, Task>(key, completed));
            },
            (_pendingReleasesBySessionId, sessionId),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return releaseTask;
    }

    private async Task ReleaseSessionCoreAsync(CopilotSession session, bool deleteServerSession)
    {
        try
        {
            if (deleteServerSession)
                await DisposeAndDeleteSessionAsync(session).ConfigureAwait(false);
            else
                await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Lumi] Failed to release Copilot session {session.SessionId}: {ex.Message}");
        }
    }

    private async Task AwaitPendingReleaseAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || !_pendingReleasesBySessionId.TryGetValue(sessionId, out var release))
            return;

        try
        {
            // ReleaseSessionCoreAsync swallows its own faults, so this only surfaces cancellation.
            await release.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Lumi] Failed awaiting pending release for session {sessionId}: {ex.Message}");
        }
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

    /// <summary>
    /// True when the backend refused to process an <b>image</b> that is part of the session history
    /// (e.g. <c>400 invalid_request_error "Could not process image"</c> or
    /// <c>"The image data you provided does not represent a valid image."</c>).
    /// <para>
    /// A tool-result / attached image can be stored perfectly on our side yet still be rejected by
    /// the upstream model. Because it lives in the server-side session history, it is re-sent on
    /// <i>every</i> turn and permanently bricks the chat — a plain resend can never recover. Callers
    /// use this to rebuild the session from the local transcript as text (dropping the image).
    /// </para>
    /// This is an ALLOW-LIST: it matches only when the message is unambiguously about an image the
    /// backend could not process/validate, so unrelated request errors keep their normal handling.
    /// </summary>
    internal static bool IsUnprocessableImageError(string? errorText)
    {
        if (string.IsNullOrWhiteSpace(errorText))
            return false;

        var text = errorText.ToLowerInvariant();

        // Must be about an image AND about a processing/validity failure of that image.
        if (!text.Contains("image"))
            return false;

        return text.Contains("could not process")
            || text.Contains("cannot process")
            || text.Contains("couldn't process")
            || text.Contains("unable to process")
            || text.Contains("failed to process")
            || text.Contains("could not be processed")
            || text.Contains("does not represent a valid")
            || text.Contains("not a valid image")
            || text.Contains("invalid image");
    }

    /// <summary>
    /// Structured variant for SDK <c>session.error</c> events. Quota / rate-limit / context-limit and
    /// auth categories have dedicated handling and are never treated as an unprocessable-image error,
    /// even in the unlikely event their message mentions an image.
    /// </summary>
    internal static bool IsUnprocessableImageError(int? statusCode, string? errorType, string? message)
    {
        var type = errorType?.ToLowerInvariant();
        if (type is "quota" or "rate_limit" or "context_limit" or "authentication" or "authorization")
            return false;

        return IsUnprocessableImageError(message);
    }

    /// <summary>
    /// True when retrying a failed turn cannot possibly help by itself, because the failure is a hard
    /// credential / capacity / policy limit rather than a recoverable session or transport glitch:
    /// a genuine logout, an exhausted quota or rate limit, an exceeded context window, or a content
    /// policy rejection. Callers use this as the single "should we even offer Retry?" gate — EVERY
    /// other terminal error is treated as recoverable by rebuilding the session from the transcript
    /// as text (which safely drops any poisoned history such as an unprocessable image).
    /// </summary>
    internal static bool IsFatalNonRetryableError(int? statusCode, string? errorType, string? message)
    {
        // A bare 401/403 with no transient backend marker is a genuine logout / permission denial —
        // the transient allow-list (IsTransientServerAuthError) has already had its chance upstream,
        // so retrying the same turn just fails again; the user must re-authenticate.
        if (statusCode is 401 or 403)
            return true;

        var type = errorType?.ToLowerInvariant();
        if (type is "authentication" or "authorization" or "permission"
            or "quota" or "insufficient_quota" or "rate_limit"
            or "context_limit" or "context_length_exceeded"
            or "content_policy" or "content_filter"
            or "model_not_supported")
            return true;

        return IsFatalNonRetryableError(message);
    }

    /// <summary>String overload of <see cref="IsFatalNonRetryableError(int?,string?,string?)"/> used
    /// when only a flattened / persisted error message is available (e.g. reclassifying a stored
    /// <c>error</c> message on chat reload to decide whether to show a Retry button).</summary>
    internal static bool IsFatalNonRetryableError(string? errorText)
    {
        if (string.IsNullOrWhiteSpace(errorText))
            return false;

        var text = errorText.ToLowerInvariant();

        return text.Contains("quota")
            || text.Contains("rate limit") || text.Contains("rate_limit")
            || text.Contains("context length") || text.Contains("context_length")
            || text.Contains("context window") || text.Contains("context_limit")
            || text.Contains("maximum context")
            || text.Contains("content policy") || text.Contains("content_policy")
            || text.Contains("content filter") || text.Contains("content_filter")
            || text.Contains("responsible ai")
            // A genuine logout / permission failure: retrying the same turn just fails again, so it
            // must NOT be offered a Retry. Only unambiguous auth-failure phrases are matched — bare
            // "401" / "403" are deliberately excluded because those digits collide with request IDs.
            || text.Contains("unauthorized") || text.Contains("unauthenticated")
            || text.Contains("forbidden")
            || text.Contains("bad credentials") || text.Contains("invalid credentials")
            || text.Contains("authentication failed") || text.Contains("authorization failed")
            || text.Contains("not authenticated")
            // Permission / model-access denials (often a 403 whose numeric code we deliberately don't
            // substring-match because bare "403" collides with request IDs). Retrying the identical
            // request can never grant access, so these must surface terminally, not as a Retry.
            || text.Contains("not have access") || text.Contains("access denied")
            || text.Contains("permission denied")
            || text.Contains("model_not_supported") || text.Contains("model not supported")
            || text.Contains("log in again") || text.Contains("sign in again")
            || text.Contains("logged out") || text.Contains("re-authenticate");
    }

    /// <summary>
    /// The message thrown by <c>ChatViewModel.EnsureSessionAsync</c> when session setup
    /// (create/resume + MCP initialization) exceeds its bound because the chat has MCP servers.
    /// Centralized so the throw sites and <see cref="IsMcpSetupTimeoutError"/> can never drift.
    /// </summary>
    internal const string McpSetupTimeoutMessage =
        "MCP server connection timed out. Check that your MCP servers are installed and responding.";

    /// <summary>
    /// True when an error is the client-side MCP session-SETUP timeout (a slow create/resume + MCP
    /// init), as opposed to a backend/session failure. Such a timeout means setup was slow — NOT that
    /// the session is poisoned — so the session must be kept resumable and simply retried, never
    /// deleted + cold-recreated (which is strictly slower and cascades into further timeouts).
    /// </summary>
    internal static bool IsMcpSetupTimeoutError(string? errorText)
        => !string.IsNullOrWhiteSpace(errorText)
           && errorText.Contains("MCP server connection timed out", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The single send-failure decision shared by both handlers. Given whatever the failure exposed
    /// — an HTTP <paramref name="statusCode"/> and <paramref name="errorType"/> from a structured
    /// <c>session.error</c>, or just a flattened <paramref name="message"/> from a thrown exception —
    /// it decides whether the turn is <see cref="SendFailureClassification.Recoverable"/> and, if so,
    /// whether to show the unprocessable-<see cref="SendFailureClassification.IsImageError"/> copy.
    /// <para>
    /// The exception path passes <c>(null, null, flattenedMessage, …)</c>; the structured classifiers
    /// degrade gracefully to the message heuristic when status/type are null, so that path gets
    /// exactly the string-only result it had before. Centralizing the logic here is what keeps the two
    /// handlers from drifting apart — a divergence that has recurred across reviews.
    /// </para>
    /// </summary>
    /// <param name="hasTerminalOverride">True when the caller supplied a synthetic TERMINAL message
    /// (e.g. "start a new chat" / "restart Lumi") that its call site already deemed unrecoverable. Such
    /// a state is never reclassified and never offered Retry, so this short-circuits to
    /// non-recoverable / non-image.</param>
    internal static SendFailureClassification ClassifySendFailure(
        int? statusCode, string? errorType, string? message, bool hasTerminalOverride)
    {
        if (hasTerminalOverride)
            return new SendFailureClassification(Recoverable: false, IsImageError: false);

        // Recoverable = anything that is NOT a hard auth / quota / context / policy limit; those are
        // rebuilt from the transcript as text (safely dropping any poisoned history). An image
        // rejection can ALSO be fatal (e.g. a content-policy image block), so the fatal verdict wins
        // and gates the image flag — the "click Retry" copy is only used when Retry is actually shown.
        var recoverable = !IsFatalNonRetryableError(statusCode, errorType, message);
        var isImageError = recoverable && IsUnprocessableImageError(statusCode, errorType, message);
        return new SendFailureClassification(recoverable, isImageError);
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
                    TimeSpan.FromSeconds(40), innerCt).ConfigureAwait(false);
                return result?.Data?.Content;
            },
            ct).ConfigureAwait(false);

        return rawTitle?.Trim().Trim('"', '\'', '.', '!');
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;

        var end = maxLength;
        // Avoid cutting through a surrogate pair, which would leave a lone surrogate
        // (malformed UTF-16) that can slow down or corrupt the title request.
        if (char.IsHighSurrogate(text[end - 1]))
            end--;
        return text[..end];
    }

    private const string SuggestionSystemPrompt =
        "You generate follow-up suggestions for a chat assistant. Propose exactly 3 short messages the user would naturally send next, grounded in the CURRENT conversation. " +
        "QUALITY RULES (most important): " +
        "1. Ground every suggestion in the specifics of THIS conversation — reference the actual topics, names, entities, files, or details discussed. Avoid vague filler like \"What should I do next?\", \"Run the app\", \"Show a summary\", \"Check for errors\", or \"Tell me more\". " +
        "2. Advance the conversation — never re-ask something the assistant already answered; build on it instead. " +
        "3. Follow the user's actual intent and task, not just the broad theme (e.g. in a lesson about learning kanji, suggest a sharper way to study kanji — not an unrelated ramen recipe that merely shares the 'Japanese' theme). " +
        "4. Make the 3 meaningfully different from each other (e.g. go deeper, take the next concrete action, explore a related angle) — not three rephrasings of one idea. " +
        "5. Each must be concise (under 60 characters), specific, and something the user would actually click. " +
        "FREQUENT-REQUESTS LIST: you may also receive a list of the user's frequently-typed requests. Treat it as low-priority reference, NOT a menu: include one only if it is a genuinely natural next step for THIS conversation. Usually none fit — then ignore the list entirely. Never suggest something merely because it is frequent, and ignore any list item that looks like a test, debug, setup, or system command, or is unrelated to the current topic. " +
        "Output ONLY a JSON array of exactly 3 strings, nothing else. Example: [\"Compare the LG G4 and Sony A95L\", \"Which handles glare better?\", \"Show cheaper alternatives under $1500\"]";

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
            contextBuilder.AppendLine("The user's frequently-typed requests from other chats (reference only — include one ONLY if it is a natural next step for THIS conversation; otherwise ignore this list entirely):");
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
