using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Services;

namespace Lumi.Services.Capabilities;

/// <summary>
/// Merges Lumi's live capabilities with capabilities loaded from external providers.
/// </summary>
public sealed class CapabilityCatalog : IDisposable
{
    private readonly LumiCapabilityProvider _lumiProvider;
    private readonly IReadOnlyList<ICapabilityProvider> _externalProviders;
    private readonly object _sync = new();
    private readonly Dictionary<string, CapabilityProviderResult> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingLoad> _loads = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private int _generation;
    private bool _isDisposed;

    public CapabilityCatalog(
        LumiCapabilityProvider lumiProvider,
        params ICapabilityProvider[] externalProviders)
    {
        ArgumentNullException.ThrowIfNull(lumiProvider);
        ArgumentNullException.ThrowIfNull(externalProviders);
        if (externalProviders.Any(static provider => provider is null))
            throw new ArgumentException("Capability providers cannot contain null.", nameof(externalProviders));

        _lumiProvider = lumiProvider;
        _externalProviders = [.. externalProviders];
    }

    public static CapabilityCatalog CreateDefault(DataStore dataStore, CopilotService copilotService)
    {
        ArgumentNullException.ThrowIfNull(dataStore);
        ArgumentNullException.ThrowIfNull(copilotService);

        return new CapabilityCatalog(
            new LumiCapabilityProvider(dataStore),
            new CopilotSdkCapabilityProvider(
                () => copilotService.IsConnected ? copilotService.Client : null),
            new WorkspaceMcpCapabilityProvider());
    }

    /// <summary>
    /// Returns the current snapshot without starting work. Lumi capabilities are read live; external
    /// capabilities come from the latest result for this query.
    /// </summary>
    public CapabilitySnapshot GetSnapshot(CapabilityQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        CapabilityProviderResult? external;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _cache.TryGetValue(query.CacheKey, out external);
        }

        return Merge(query, external);
    }

    /// <summary>
    /// Loads external providers when no complete result exists, or reloads them when requested.
    /// Concurrent callers for the same query join one load.
    /// </summary>
    public async Task LoadAsync(
        CapabilityQuery query,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        PendingLoad? pending = null;
        CapabilityProviderResult? cachedResult = null;
        var startLoad = false;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_externalProviders.Count == 0)
            {
                cachedResult = CapabilityProviderResult.Empty;
            }
            else if (_loads.TryGetValue(query.CacheKey, out var running)
                && running.Generation == _generation)
            {
                pending = running;
            }
            else if (!forceRefresh
                     && _cache.TryGetValue(query.CacheKey, out var cached)
                     && cached.IsAvailable)
            {
                cachedResult = cached;
            }
            else
            {
                pending = new PendingLoad(_generation);
                _loads[query.CacheKey] = pending;
                startLoad = true;
            }
        }

        if (cachedResult is not null)
            return;

        if (startLoad)
            _ = CompleteLoadAsync(query, pending!);

        await pending!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a new external-capability generation. Results still running from the previous runtime
    /// may finish, but cannot populate this generation's cache.
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            if (_isDisposed)
                return;

            _generation++;
            _cache.Clear();
            _loads.Clear();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _generation++;
            _cache.Clear();
            _loads.Clear();
        }

        _lifetime.Cancel();
    }

    private async Task CompleteLoadAsync(CapabilityQuery query, PendingLoad pending)
    {
        try
        {
            var result = await LoadExternalProvidersAsync(query).ConfigureAwait(false);

            lock (_sync)
            {
                if (!_isDisposed && pending.Generation == _generation)
                {
                    // A failed refresh must not replace a previously complete snapshot. After Reset
                    // there is no previous snapshot, so keeping the partial result still lets the UI
                    // show capabilities from providers that did answer.
                    if (result.IsAvailable
                        || !_cache.TryGetValue(query.CacheKey, out var existing)
                        || !existing.IsAvailable)
                    {
                        _cache[query.CacheKey] = result;
                    }
                }

                RemovePendingLoad(query.CacheKey, pending);
            }

            pending.SetResult(result);
        }
        catch (Exception ex)
        {
            lock (_sync)
                RemovePendingLoad(query.CacheKey, pending);
            pending.SetException(ex);
        }
    }

    private void RemovePendingLoad(string cacheKey, PendingLoad pending)
    {
        if (_loads.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, pending))
            _loads.Remove(cacheKey);
    }

    private async Task<CapabilityProviderResult> LoadExternalProvidersAsync(CapabilityQuery query)
    {
        var results = await Task.WhenAll(_externalProviders.Select(
            provider => LoadProviderAsync(provider, query, _lifetime.Token))).ConfigureAwait(false);

        return new CapabilityProviderResult(
            results.SelectMany(static result => result.Capabilities).ToArray(),
            IsAvailable: results.All(static result => result.IsAvailable),
            SessionSkillRoots: results
                .SelectMany(static result => result.SessionSkillRoots ?? [])
                .Distinct(CapabilityQuery.DirectoryComparer)
                .ToArray());
    }

    private static async Task<CapabilityProviderResult> LoadProviderAsync(
        ICapabilityProvider provider,
        CapabilityQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.LoadAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CapabilityProviderResult.Unavailable;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Capabilities] Provider '{provider.Id}' failed: {ex.Message}");
            return CapabilityProviderResult.Unavailable;
        }
    }

    private CapabilitySnapshot Merge(CapabilityQuery query, CapabilityProviderResult? external)
    {
        var merged = new Dictionary<(CapabilityKind, string), CapabilityDescriptor>(
            CapabilityKeyComparer.Instance);

        foreach (var capability in _lumiProvider.Load().Concat(external?.Capabilities ?? []))
        {
            if (string.IsNullOrWhiteSpace(capability.Name))
                continue;

            var key = (capability.Kind, capability.Name);
            if (!merged.TryGetValue(key, out var existing)
                || capability.Origin.Rank < existing.Origin.Rank)
            {
                merged[key] = capability;
            }
        }

        return new CapabilitySnapshot(
            query,
            merged.Values.ToArray(),
            isComplete: external?.IsAvailable ?? _externalProviders.Count == 0,
            sessionSkillRoots: external?.SessionSkillRoots);
    }

    private sealed class PendingLoad(int generation)
    {
        private readonly TaskCompletionSource<CapabilityProviderResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Generation { get; } = generation;
        public Task<CapabilityProviderResult> Task => _completion.Task;
        public void SetResult(CapabilityProviderResult result) => _completion.TrySetResult(result);
        public void SetException(Exception exception) => _completion.TrySetException(exception);
    }

    private sealed class CapabilityKeyComparer : IEqualityComparer<(CapabilityKind Kind, string Name)>
    {
        public static readonly CapabilityKeyComparer Instance = new();

        public bool Equals((CapabilityKind Kind, string Name) x, (CapabilityKind Kind, string Name) y)
            => x.Kind == y.Kind && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);

        public int GetHashCode((CapabilityKind Kind, string Name) obj)
            => HashCode.Combine(obj.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }
}
