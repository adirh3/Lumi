using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;

namespace Lumi.Services;

public sealed partial class McpProxyRuntime : IAsyncDisposable
{
    private sealed class McpProxyRegistration(
        McpProxyServerDefinition definition,
        string Identity,
        string RouteId,
        string Fingerprint,
        McpDiscoveryCache discoveryCache) : IAsyncDisposable
    {
        private readonly object _leaseGate = new();
        private int _activeLeases;
        private bool _retired;
        private Task? _disposeTask;
        private TaskCompletionSource<object?>? _disposeCompletion;
        private readonly Dictionary<string, McpDiscoverySession> _clients = new(StringComparer.Ordinal);
        private readonly McpDiscoverySession _eagerClient = new(false);
        private string? _persistentClientToken;

        public string Identity { get; } = Identity;

        public string RouteId { get; } = RouteId;

        public string Fingerprint { get; } = Fingerprint;

        public int SessionLeaseCount { get; set; }

        public bool HasPersistentOwner { get; set; }

        public McpStdioServerConnection Connection { get; } = new(definition, discoveryCache);

        // These methods and route lookup run under the runtime gate.
        public string AddClient(bool useLazyInitialization)
        {
            var token = Guid.NewGuid().ToString("N");
            _clients.Add(token, new McpDiscoverySession(useLazyInitialization));
            return token;
        }

        public string GetPersistentClientToken(long revision, bool useLazyInitialization)
        {
            if (_persistentClientToken is null || _clients[_persistentClientToken].NeedsRediscovery(revision))
                _persistentClientToken = AddClient(useLazyInitialization);
            return _persistentClientToken;
        }

        public void RemoveClient(string? token)
        {
            if (token is not null)
                _clients.Remove(token);
        }

        public McpProxyRegistrationLease? TryAcquireLease(string? clientToken)
        {
            var client = _eagerClient;
            if (clientToken is not null && !_clients.TryGetValue(clientToken, out client))
                return null;
            lock (_leaseGate)
            {
                if (_retired)
                    return null;

                _activeLeases++;
                return new McpProxyRegistrationLease(this, client);
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

    private sealed class McpProxyRegistrationLease(
        McpProxyRegistration registration, McpDiscoverySession client) : IAsyncDisposable
    {
        private int _disposed;

        public McpProxyRegistration Registration { get; } = registration;
        public McpDiscoverySession Client { get; } = client;

        public ValueTask DisposeAsync()
            => Interlocked.Exchange(ref _disposed, 1) == 0
                ? Registration.ReleaseLeaseAsync()
                : ValueTask.CompletedTask;
    }

    public sealed class SessionRegistrationLease : IDisposable
    {
        private readonly object _releaseGate = new();
        private Func<Task>? _releaseAsync;
        private Task? _releaseTask;

        internal SessionRegistrationLease(
            Action release,
            McpHttpServerConfig serverConfig)
            : this(
                () =>
                {
                    release();
                    return Task.CompletedTask;
                },
                serverConfig)
        {
        }

        internal SessionRegistrationLease(
            Func<Task> releaseAsync,
            McpHttpServerConfig serverConfig)
        {
            _releaseAsync = releaseAsync;
            ServerConfig = serverConfig;
        }

        public McpHttpServerConfig ServerConfig { get; }

        public Task ReleaseAsync()
        {
            lock (_releaseGate)
            {
                if (_releaseTask is not null)
                    return _releaseTask;

                var releaseAsync = _releaseAsync;
                _releaseAsync = null;
                _releaseTask = releaseAsync?.Invoke() ?? Task.CompletedTask;
                return _releaseTask;
            }
        }

        public void Dispose()
        {
            var releaseTask = ReleaseAsync();
            if (!releaseTask.IsCompletedSuccessfully)
            {
                _ = releaseTask.ContinueWith(
                    static task => Trace.TraceWarning(
                        "MCP proxy session registration cleanup failed: {0}",
                        task.Exception),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
        }
    }
}
