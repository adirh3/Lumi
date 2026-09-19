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
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;

namespace Lumi.Services;

[Flags]
public enum McpToolCallPreflightPolicy
{
    None = 0,
    ToolsListSessionHealth = 1,
    AgencyNotDispatchedSignal = 2
}

public sealed record McpProxyServerDefinition(
    string Key,
    string Name,
    McpStdioServerConfig Config,
    bool UseLazyInitialization = false,
    McpToolCallPreflightPolicy ToolCallPreflightPolicy = McpToolCallPreflightPolicy.None);

public sealed partial class McpProxyRuntime : IAsyncDisposable
{
    public static McpProxyRuntime Shared { get; } = new(Path.Combine(DataStore.AppDirectory, "mcp-discovery"));

    private readonly McpDiscoveryCache _discoveryCache;
    private readonly object _gate = new();
    private readonly Dictionary<string, McpProxyRegistration> _registrationsByIdentity = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _persistentIdentityByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, McpProxyRegistration> _registrationsByRoute = new(StringComparer.Ordinal);
    private readonly string _routeToken = Guid.NewGuid().ToString("N");

    private HttpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private int _port;
    private bool _disposed;

    public McpProxyRuntime(string? discoveryCacheDirectory = null)
        => _discoveryCache = new McpDiscoveryCache(discoveryCacheDirectory);

    public Task RefreshDiscoveryAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => _discoveryCache.Clear(), cancellationToken);

    public McpHttpServerConfig Register(McpProxyServerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Key))
            throw new ArgumentException("MCP proxy definition key cannot be empty.", nameof(definition));

        McpProxyRegistration? staleRegistration = null;
        McpProxyRegistration activeRegistration;
        string? clientToken;
        int port;
        // Cache revision reads may synchronize with disk maintenance; never wait for that
        // while holding the runtime's registration/HTTP routing gate.
        var cacheRevision = definition.UseLazyInitialization ? _discoveryCache.RefreshRevision : 0;
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(McpProxyRuntime));

            EnsureListenerStartedLocked();

            var fingerprint = ComputeFingerprint(definition.Config);
            var identity = ComputeRegistrationIdentity(
                definition.Key,
                fingerprint,
                definition.ToolCallPreflightPolicy);
            if (_persistentIdentityByKey.TryGetValue(definition.Key, out var previousIdentity)
                && !string.Equals(previousIdentity, identity, StringComparison.Ordinal)
                && _registrationsByIdentity.TryGetValue(previousIdentity, out var previousRegistration))
            {
                previousRegistration.HasPersistentOwner = false;
                if (previousRegistration.SessionLeaseCount == 0)
                {
                    RemoveRegistrationLocked(previousRegistration);
                    staleRegistration = previousRegistration;
                }
            }

            var legacyRouteId = Hash(definition.Key)[..24];
            activeRegistration = GetOrCreateRegistrationLocked(
                definition,
                identity,
                fingerprint,
                preferredRouteId: _registrationsByRoute.ContainsKey(legacyRouteId)
                    ? null
                    : legacyRouteId);
            activeRegistration.HasPersistentOwner = true;
            clientToken = RequiresDedicatedClient(definition)
                ? activeRegistration.GetPersistentClientToken(
                    cacheRevision,
                    definition.UseLazyInitialization)
                : null;
            _persistentIdentityByKey[definition.Key] = identity;
            port = _port;
        }

        if (staleRegistration is not null)
            RetireRegistrationInBackground(staleRegistration);

        return BuildServerConfig(definition, activeRegistration, port, clientToken);
    }

    public SessionRegistrationLease AcquireSessionRegistration(McpProxyServerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Key))
            throw new ArgumentException("MCP proxy definition key cannot be empty.", nameof(definition));

        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(McpProxyRuntime));

            EnsureListenerStartedLocked();

            var fingerprint = ComputeFingerprint(definition.Config);
            var identity = ComputeRegistrationIdentity(
                definition.Key,
                fingerprint,
                definition.ToolCallPreflightPolicy);
            var registration = GetOrCreateRegistrationLocked(definition, identity, fingerprint);
            registration.SessionLeaseCount++;
            var clientToken = RequiresDedicatedClient(definition)
                ? registration.AddClient(definition.UseLazyInitialization)
                : null;
            return new SessionRegistrationLease(
                () => ReleaseSessionRegistrationAsync(registration, clientToken),
                BuildServerConfig(definition, registration, _port, clientToken));
        }
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

            foreach (var (key, identity) in _persistentIdentityByKey.ToArray())
            {
                if (!key.StartsWith("lumi:", StringComparison.Ordinal) || retainedKeys.Contains(key))
                    continue;

                _persistentIdentityByKey.Remove(key);
                if (!_registrationsByIdentity.TryGetValue(identity, out var registration))
                    continue;

                registration.HasPersistentOwner = false;
                if (registration.SessionLeaseCount == 0)
                {
                    RemoveRegistrationLocked(registration);
                    staleRegistrations.Add(registration);
                }
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
            registrations = _registrationsByRoute.Values.ToList();
            _registrationsByIdentity.Clear();
            _persistentIdentityByKey.Clear();
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

    private McpProxyRegistration GetOrCreateRegistrationLocked(
        McpProxyServerDefinition definition,
        string identity,
        string fingerprint,
        string? preferredRouteId = null)
    {
        if (_registrationsByIdentity.TryGetValue(identity, out var registration))
            return registration;

        var routeId = preferredRouteId ?? Hash(identity)[..24];
        if (_registrationsByRoute.ContainsKey(routeId))
            routeId = Guid.NewGuid().ToString("N");

        registration = new McpProxyRegistration(
            definition,
            Identity: identity,
            RouteId: routeId,
            Fingerprint: fingerprint,
            _discoveryCache);
        _registrationsByIdentity[identity] = registration;
        _registrationsByRoute[registration.RouteId] = registration;
        return registration;
    }

    private Task ReleaseSessionRegistrationAsync(McpProxyRegistration registration, string? clientToken)
    {
        McpProxyRegistration? staleRegistration = null;
        lock (_gate)
        {
            if (_disposed)
                return Task.CompletedTask;

            registration.RemoveClient(clientToken);
            registration.SessionLeaseCount--;
            if (registration.SessionLeaseCount < 0)
                throw new InvalidOperationException("MCP proxy session registration was released more than once.");

            if (registration.SessionLeaseCount == 0 && !registration.HasPersistentOwner)
            {
                RemoveRegistrationLocked(registration);
                staleRegistration = registration;
            }
        }

        return staleRegistration is null
            ? Task.CompletedTask
            : staleRegistration.RetireAsync().AsTask();
    }

    private void RemoveRegistrationLocked(McpProxyRegistration registration)
    {
        if (_registrationsByIdentity.TryGetValue(registration.Identity, out var current)
            && ReferenceEquals(current, registration))
            _registrationsByIdentity.Remove(registration.Identity);
        if (_registrationsByRoute.TryGetValue(registration.RouteId, out var routed)
            && ReferenceEquals(routed, registration))
        {
            _registrationsByRoute.Remove(registration.RouteId);
        }
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
            await using var registrationLease = ResolveRegistrationLease(
                context.Request.Url?.AbsolutePath, context.Request.QueryString["client"]);
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

            var responseJson = await registrationLease.Registration.Connection.HandleClientMessageAsync(
                registrationLease.Client, body, cancellationToken).ConfigureAwait(false);
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

    private McpProxyRegistrationLease? ResolveRegistrationLease(string? path, string? clientToken)
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
                ? registration.TryAcquireLease(clientToken)
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

    internal static string ComputeFingerprint(McpStdioServerConfig config, bool includeTimeout = true)
    {
        var builder = new StringBuilder();
        AppendFingerprintField(builder, "command", config.Command);
        AppendFingerprintField(builder, "working-directory", NormalizeWorkingDirectory(config.WorkingDirectory));
        if (includeTimeout)
            AppendFingerprintField(
                builder,
                "timeout",
                config.Timeout?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var arg in config.Args ?? [])
            AppendFingerprintField(builder, "arg", arg);
        foreach (var tool in config.Tools ?? [])
            AppendFingerprintField(builder, "tool", tool);
        foreach (var pair in (config.Env ?? new Dictionary<string, string>())
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            AppendFingerprintField(builder, "env-key", pair.Key);
            AppendFingerprintField(builder, "env-value", pair.Value);
        }

        return Hash(builder.ToString());
    }

    private static void AppendFingerprintField(StringBuilder builder, string name, string? value)
    {
        builder.Append(name).Append('=');
        if (value is null)
            builder.Append("-1:");
        else
            builder.Append(value.Length).Append(':').Append(value);
        builder.Append(';');
    }

    private static string ComputeRegistrationIdentity(
        string key,
        string fingerprint,
        McpToolCallPreflightPolicy toolCallPreflightPolicy)
        => key + "\n" + fingerprint + "\npreflight:" + (int)toolCallPreflightPolicy;

    private static bool RequiresDedicatedClient(McpProxyServerDefinition definition)
        => definition.UseLazyInitialization
            || definition.ToolCallPreflightPolicy != McpToolCallPreflightPolicy.None;

    private static string NormalizeWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return string.Empty;

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        return OperatingSystem.IsWindows() && Directory.Exists(fullPath)
            ? NormalizeExistingWindowsPathCasing(fullPath)
            : fullPath;
    }

    private static string NormalizeExistingWindowsPathCasing(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
            return fullPath;

        var current = root.ToUpperInvariant();
        foreach (var segment in fullPath[root.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var matches = Directory.EnumerateFileSystemEntries(current)
                    .Where(entry => string.Equals(
                        Path.GetFileName(entry),
                        segment,
                        StringComparison.OrdinalIgnoreCase))
                    .Take(2)
                    .ToArray();
                current = Path.Combine(
                    current,
                    matches.Length == 1 ? Path.GetFileName(matches[0]) : segment);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return fullPath;
            }
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    private McpHttpServerConfig BuildServerConfig(
        McpProxyServerDefinition definition,
        McpProxyRegistration registration,
        int port,
        string? clientToken = null)
        => new()
        {
            Url = $"http://127.0.0.1:{port}/mcp/{_routeToken}/{registration.RouteId}"
                + (clientToken is null ? "" : $"?client={clientToken}"),
            Tools = definition.Config.Tools?.ToList() ?? ["*"],
            Timeout = definition.Config.Timeout
        };

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
