using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lumi.Services;

internal sealed partial class McpStdioServerConnection : IAsyncDisposable
{
    private void BindInitializeProfile(JsonElement parameters)
    {
        if (_initializeParams is { } bound)
        {
            if (!JsonElement.DeepEquals(bound, parameters))
                throw new InvalidOperationException(
                    "This shared MCP route was initialized with a different client profile. Use a separate MCP configuration.");
            return;
        }

        // Bind before advertising cached initialization, even when there is no process yet.
        _initializeParams = parameters.Clone();
        if (!McpDiscoveryCache.IsCacheableClient(parameters))
            return;
        try
        {
            _discoveryKey = McpDiscoveryCache.CreateKey(
                _definition.Key + "\n" + McpProxyRuntime.ComputeFingerprint(_definition.Config, includeTimeout: false),
                parameters,
                CreateStartInfo());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning("MCP server '{0}' cannot fingerprint discovery; keeping initialization live: {1}", _definition.Name, ex.Message);
        }
    }

    private bool IsDiscoveryTainted
        => Volatile.Read(ref _taintedGeneration) == Volatile.Read(ref _processGeneration);

    private void InvalidateDiscovery(bool taint = false, int? generation = null)
    {
        lock (_discoverySignalGate)
        {
            if (generation is not null && generation != Volatile.Read(ref _processGeneration))
                return;
            Interlocked.Increment(ref _sourceRevision);
            if (taint)
                Interlocked.Exchange(ref _taintedGeneration, Volatile.Read(ref _processGeneration));
            if (_discoveryKey is { } key)
                _discoveryCache.Invalidate(key);
        }
    }

    private void ThrowDiscoveryChanged(McpDiscoverySession client)
    {
        client.MarkNeedsRediscovery();
        throw new InvalidOperationException(
            $"MCP server '{_definition.Name}' changed its advertised discovery. No tool was called. Reconnect the MCP client to rediscover its tools.");
    }

    private bool IsCurrentDiscovery(int generation, long revision)
        => generation == Volatile.Read(ref _processGeneration) && revision == DiscoveryRevision;

    private long DiscoveryRevision => _discoveryCache.GetRevision(_discoveryKey);

    private void RecordDiscovery(
        JsonElement request, JsonElement response, int generation, long revision, long sourceRevision)
    {
        if (!IsCurrentDiscovery(generation, revision) || IsDiscoveryTainted
            || sourceRevision != Volatile.Read(ref _sourceRevision)
            || !McpDiscoveryCache.IsPlainListRequest(request))
            return;

        _liveToolsResponse = response.Clone();
        _liveDiscoveryGeneration = generation;
        _liveDiscoveryRevision = revision;
        _liveSourceRevision = sourceRevision;
        if (response.TryGetProperty("result", out var list)
            && _initializeParams is { } parameters && _initializeResult is { } initialize
            && McpDiscoveryCache.IsCacheable(parameters, initialize, list))
        {
            if (_discoveryKey is { } key)
                _discoveryCache.Write(key, new McpDiscoveryCache.Snapshot(initialize, list.Clone()), revision);
        }
        else
        {
            InvalidateDiscovery();
            _liveToolsResponse = null;
            // Unsupported catalogs stay live, and a later successful list can recover this source.
            _liveDiscoveryRevision = DiscoveryRevision;
            _liveSourceRevision = Volatile.Read(ref _sourceRevision);
        }
    }

    private async Task<JsonElement> DiscoverToolsUnderLockAsync(
        JsonElement request, CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref _processGeneration);
        var revision = DiscoveryRevision;
        var sourceRevision = Volatile.Read(ref _sourceRevision);
        if (_liveToolsResponse is { } existing
            && _liveDiscoveryGeneration == generation && _liveDiscoveryRevision == revision
            && _liveSourceRevision == sourceRevision && !IsDiscoveryTainted)
            return existing;
        try
        {
            var parameters = request.TryGetProperty("params", out var p) ? p : (JsonElement?)null;
            JsonElement response;
            for (var attempt = 0; ; attempt++)
            {
                response = await SendRequestAsync("tools/list", parameters, cancellationToken).ConfigureAwait(false);
                if (!IsRecoverableSessionLossResponse(response) || attempt >= SessionRecoveryRetryLimit)
                    break;
                // Discovery is read-only and no client tool has been dispatched yet. Recover
                // under the lock already held here rather than recursively acquiring it.
                await RecoverExpiredServerSessionUnderLockAsync(generation, cancellationToken).ConfigureAwait(false);
                generation = Volatile.Read(ref _processGeneration);
                revision = DiscoveryRevision;
                sourceRevision = Volatile.Read(ref _sourceRevision);
            }
            if (!IsCurrentDiscovery(generation, revision) || IsDiscoveryTainted
                || sourceRevision != Volatile.Read(ref _sourceRevision))
                throw new IOException("MCP discovery changed while it was being read. Retry discovery before calling a tool.");
            RecordDiscovery(request, response, generation, revision, sourceRevision);
            return response;
        }
        catch
        {
            InvalidateDiscovery();
            throw;
        }
    }

    private void ValidateClientInitialization(McpDiscoverySession client)
    {
        if (client.InitializeResult is not { } expected)
            return;
        if (IsDiscoveryTainted || _initializeParams is not { } parameters || _initializeResult is not { } actual
            || !McpDiscoveryCache.IsCacheableInitialization(parameters, actual)
            || !McpDiscoveryCache.HasCompatibleInitialization(expected, actual))
            ThrowDiscoveryChanged(client);
    }
}
