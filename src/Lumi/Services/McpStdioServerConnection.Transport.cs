using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lumi.Services;

internal sealed partial class McpStdioServerConnection : IAsyncDisposable
{
    private async Task<(JsonElement Response, int ProcessGeneration)> ForwardRequestAsync(
        McpDiscoverySession client,
        JsonElement clientMessage,
        CancellationToken cancellationToken)
    {
        var internalId = Interlocked.Increment(ref _nextId);
        var request = JsonRpc.WithId(clientMessage, internalId);
        var key = internalId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processGeneration = 0;
        var discoveryRevision = -1L;
        var sourceRevision = -1L;
        var validateCatalog = false;
        var pendingRegistered = false;

        using var initializeCts = CreateInitializeTimeoutSource(cancellationToken);
        var initializeCt = initializeCts.Token;
        await _lifecycleLock.WaitAsync(initializeCt).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var method = clientMessage.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
            var plainList = method == "tools/list" && McpDiscoveryCache.IsPlainListRequest(clientMessage);
            if (client.UseLazyInitialization
                && (plainList && client.ToolsResult is not null
                    || method == "ping" && _processGeneration == 0 && client.InitializeResult is not null))
            {
                using var cachedResponse = JsonDocument.Parse(method == "ping"
                    ? $$$"""{"jsonrpc":"2.0","id":{{{internalId}}},"result":{}}"""
                    : JsonRpc.Response(clientMessage.GetProperty("id"), client.ToolsResult!.Value));
                return (cachedResponse.RootElement.Clone(), 0);
            }

            await EnsureInitializedUnderLockAsync(null, initializeCt).ConfigureAwait(false);
            if (client.UseLazyInitialization && !client.LiveOnly)
            {
                if (client.InitializeResult is null && !IsDiscoveryTainted
                    && McpDiscoveryCache.IsCacheableInitialization(_initializeParams!.Value, _initializeResult!.Value))
                {
                    client.InitializeResult = _initializeResult;
                    Volatile.Write(ref client.AdvertisedRevision, _discoveryCache.RefreshRevision);
                }
                if (client.InitializeResult is not null
                    && (method != "tools/list" || plainList || client.ToolsResult is not null))
                {
                    // Discovery belongs to the source; publish it before comparing this client's
                    // contract, so a mismatched old client cannot poison discovery for a new one.
                    using var listRequest = JsonDocument.Parse(JsonRpc.Request(0, "tools/list", null));
                    var discovery = await DiscoverToolsUnderLockAsync(
                        plainList ? clientMessage : listRequest.RootElement, initializeCt).ConfigureAwait(false);
                    ValidateClientInitialization(client);
                    var cacheable = discovery.TryGetProperty("result", out var list)
                        && McpDiscoveryCache.IsCacheable(_initializeParams!.Value, _initializeResult!.Value, list);
                    if (cacheable)
                    {
                        validateCatalog = true;
                        if (method == "tools/call"
                            && (!clientMessage.TryGetProperty("params", out var callParams)
                                || !callParams.TryGetProperty("name", out var name)
                                || name.ValueKind != JsonValueKind.String
                                || !McpDiscoveryCache.IsToolCompatible(client.ToolsResult ?? list, list, name.GetString()!)))
                            ThrowDiscoveryChanged(client);
                    }
                    else if (client.ToolsResult is not null)
                        ThrowDiscoveryChanged(client);
                    else if (!discovery.TryGetProperty("result", out _) && !plainList)
                        throw new IOException("MCP tool discovery failed. No tool was called.");
                    else
                        client.LiveOnly = true;

                    if (!IsCurrentDiscovery(_liveDiscoveryGeneration, _liveDiscoveryRevision) || IsDiscoveryTainted
                        || _liveSourceRevision != Volatile.Read(ref _sourceRevision))
                        throw new IOException("MCP discovery changed before dispatch. No tool was called; retry discovery.");
                    // Capture only after the discovery revision check, and never replace a
                    // previous contract. Direct calls may capture their first real catalog.
                    if (cacheable && (plainList || method == "tools/call"))
                        client.ToolsResult ??= list.Clone();
                    if (plainList)
                        return (discovery, Volatile.Read(ref _processGeneration));
                }
                else
                    ValidateClientInitialization(client);
            }
            else if (client.UseLazyInitialization)
                ValidateClientInitialization(client);

            processGeneration = Volatile.Read(ref _processGeneration);
            discoveryRevision = validateCatalog ? _liveDiscoveryRevision : DiscoveryRevision;
            sourceRevision = validateCatalog ? _liveSourceRevision : Volatile.Read(ref _sourceRevision);
            lock (_pending)
                _pending[key] = new PendingRequest(processGeneration, tcs);
            pendingRegistered = true;
            await SendRawAsync(request, cancellationToken,
                client.UseLazyInitialization && !client.LiveOnly && client.InitializeResult is not null
                    ? () => IsCurrentDiscovery(processGeneration, discoveryRevision) && !IsDiscoveryTainted
                        && sourceRevision == Volatile.Read(ref _sourceRevision)
                    : null).ConfigureAwait(false);
        }
        catch
        {
            if (pendingRegistered)
            {
                lock (_pending)
                    _pending.Remove(key);
                InvalidateDiscovery(generation: processGeneration);
            }

            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeoutMilliseconds);
            var response = await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            if (clientMessage.TryGetProperty("method", out var method) && method.GetString() == "tools/list")
            {
                await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    RecordDiscovery(clientMessage, response, processGeneration, discoveryRevision, sourceRevision);
                }
                finally
                {
                    _lifecycleLock.Release();
                }
            }
            return (response, processGeneration);
        }
        catch
        {
            InvalidateDiscovery(generation: processGeneration);
            throw;
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
        var processGeneration = Volatile.Read(ref _processGeneration);

        lock (_pending)
            _pending[key] = new PendingRequest(processGeneration, tcs);

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
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is not { HasExited: false })
                return;

            await SendRawAsync(clientMessage.GetRawText(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task SendNotificationAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
        => await SendRawAsync(JsonRpc.Notification(method, parameters), cancellationToken).ConfigureAwait(false);

    private async Task SendRawAsync(
        string json, CancellationToken cancellationToken, Func<bool>? admitDispatch = null)
    {
        if (_stdin is null)
            throw new InvalidOperationException($"MCP server '{_definition.Name}' is not running.");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (admitDispatch is not null && !admitDispatch())
                throw new IOException("MCP discovery changed before dispatch. No tool was called; retry discovery.");
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
                    HandleServerLine(line, generation);
                }
                catch (JsonException ex)
                {
                    // Package launchers can write diagnostics, but malformed JSON-RPC is still an error.
                    if (line.AsSpan().TrimStart().StartsWith("{", StringComparison.Ordinal))
                        throw CreateNonJsonStdoutException(line, ex);

                    Trace.TraceWarning("MCP server '{0}' wrote non-JSON stdout: {1}",
                        _definition.Name, FormatDiagnosticLine(line));
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
        PendingRequest[] pending;
        lock (_pending)
        {
            var matching = _pending
                .Where(pair => pair.Value.ProcessGeneration == generation)
                .ToArray();
            pending = matching.Select(pair => pair.Value).ToArray();
            foreach (var pair in matching)
                _pending.Remove(pair.Key);
        }

        foreach (var request in pending)
            request.Completion.TrySetException(error);
    }

    private void HandleServerLine(string line, int generation)
    {
        using var doc = JsonDocument.Parse(line);
        var message = doc.RootElement.Clone();
        if (generation != Volatile.Read(ref _processGeneration))
            return;
        if (message.TryGetProperty("method", out var serverMethod)
            && (message.TryGetProperty("id", out _) || serverMethod.GetString() == "notifications/tools/list_changed"))
        {
            // Never take the lifecycle lock here: initialize/discovery waits for this reader.
            InvalidateDiscovery(taint: message.TryGetProperty("id", out _), generation);
        }
        if (message.TryGetProperty("id", out var id) && !message.TryGetProperty("method", out _))
        {
            var key = JsonRpc.IdKey(id);
            PendingRequest? pending;
            lock (_pending)
                _pending.TryGetValue(key, out pending);
            pending?.Completion.TrySetResult(message);
            return;
        }

        if (message.TryGetProperty("id", out var requestId) && message.TryGetProperty("method", out _))
        {
            var rejection = SendRawAsync(
                JsonRpc.Error(requestId, -32601, "Server-to-client MCP requests are not supported by Lumi's local proxy."),
                CancellationToken.None,
                () => generation == Volatile.Read(ref _processGeneration));
            _ = rejection.ContinueWith(
                static task => Trace.TraceWarning("MCP callback rejection could not be delivered: {0}", task.Exception),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private void CompletePendingWithError(Exception error)
    {
        PendingRequest[] pending;
        lock (_pending)
        {
            pending = _pending.Values.ToArray();
            _pending.Clear();
        }

        foreach (var request in pending)
            request.Completion.TrySetException(error);
    }

    private sealed record PendingRequest(
        int ProcessGeneration,
        TaskCompletionSource<JsonElement> Completion);
}
