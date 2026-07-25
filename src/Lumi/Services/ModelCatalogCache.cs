using System.Diagnostics;
using System.Text;
using GitHub.Copilot;

namespace Lumi.Services;

/// <summary>The full model catalog as returned by a single fetch: the SDK model list plus the
/// context-window metadata that is only exposed through the raw models RPC.</summary>
public sealed record ModelCatalogSnapshot(
    IReadOnlyList<ModelInfo> Models,
    ModelContextWindowCatalog ContextWindows);

/// <summary>
/// Caches the Copilot model catalog and refreshes it on demand so a model released while Lumi is
/// running shows up without a restart. Deliberately knows nothing about the CLI connection: it takes
/// fetch delegates, and treats any <see cref="Invalidate"/> (which <see cref="CopilotService"/> calls
/// on every connect and disconnect) as "the connection changed", which is what makes a refresh that
/// raced a reconnect safe to discard.
/// </summary>
public sealed class ModelCatalogCache
{
    /// <summary>How long a fetched catalog is trusted before an on-demand refresh is allowed to hit
    /// the CLI again. Keeps repeated picker opens from issuing an RPC every click.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);

    private readonly Func<CancellationToken, Task<List<ModelInfo>>> _fetchModels;
    private readonly Func<CancellationToken, Task<ModelContextWindowCatalog?>> _fetchContextWindows;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private List<ModelInfo>? _models;
    private ModelContextWindowCatalog? _contextWindows;
    private long _loadedAtTicks;
    private long _generation;

    /// <param name="fetchModels">Reads the SDK model list. Throws when not connected.</param>
    /// <param name="fetchContextWindows">Reads context-window metadata, returning null when the
    /// fetch failed so a known-good catalog is never replaced by an empty one.</param>
    public ModelCatalogCache(
        Func<CancellationToken, Task<List<ModelInfo>>> fetchModels,
        Func<CancellationToken, Task<ModelContextWindowCatalog?>> fetchContextWindows)
    {
        _fetchModels = fetchModels;
        _fetchContextWindows = fetchContextWindows;
    }

    /// <summary>
    /// Fires when a refresh found a catalog that differs from the cached one. Never fires for an
    /// unchanged catalog, so subscribers can rebind unconditionally. Raised off the UI thread.
    /// </summary>
    public event Action<ModelCatalogSnapshot>? Changed;

    public static ModelContextWindowCatalog Empty => new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, ModelContextWindowLimits>(StringComparer.OrdinalIgnoreCase));

    public async Task<List<ModelInfo>> GetModelsAsync(CancellationToken ct = default)
    {
        if (_models is null)
        {
            _models = await _fetchModels(ct).ConfigureAwait(false);
            Stamp();
        }

        return _models;
    }

    public async Task<ModelContextWindowCatalog> GetContextWindowsAsync(CancellationToken ct = default)
    {
        if (_contextWindows is not null)
            return _contextWindows;

        _contextWindows = await _fetchContextWindows(ct).ConfigureAwait(false) ?? Empty;
        Stamp();
        return _contextWindows;
    }

    /// <summary>
    /// Refetches when the cache is older than <see cref="Freshness"/> and raises <see cref="Changed"/>
    /// only when the result actually differs. Concurrent callers collapse onto one fetch, and a failed
    /// fetch leaves the cached catalog untouched.
    /// </summary>
    /// <param name="force">Bypass the freshness window (used by explicit user-driven refreshes).</param>
    /// <returns>True when the catalog changed and subscribers were notified.</returns>
    public async Task<bool> RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && !IsStale)
            return false;

        // Non-blocking: a caller that finds a refresh already running simply skips.
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return false;

        try
        {
            // A caller that queued behind the gate would otherwise refetch what just landed.
            if (!force && !IsStale)
                return false;

            var generation = Interlocked.Read(ref _generation);
            var models = await _fetchModels(ct).ConfigureAwait(false);

            // A failed context-window fetch must not wipe the cached one: publishing an empty catalog
            // would strip long context from every model and persist that loss per chat. Keeping the
            // previous one still lets a newly released model surface.
            var contextWindows = await _fetchContextWindows(ct).ConfigureAwait(false) ?? _contextWindows;
            if (contextWindows is null)
                return false;

            // A reconnect or explicit invalidation during the fetch already discarded the catalog;
            // committing this now-stale read would resurrect it.
            if (Interlocked.Read(ref _generation) != generation)
                return false;

            var previousSignature = CurrentSignature;
            _models = models;
            _contextWindows = contextWindows;
            Stamp();

            var signature = BuildSignature(models, contextWindows);
            if (string.Equals(previousSignature, signature, StringComparison.Ordinal))
                return false;

            Changed?.Invoke(new ModelCatalogSnapshot(models, contextWindows));
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Lumi] Model catalog refresh failed: {ex.Message}");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops the cached catalog and marks any refresh already in flight as stale.</summary>
    public void Invalidate()
    {
        _models = null;
        _contextWindows = null;
        Interlocked.Exchange(ref _loadedAtTicks, 0);
        Interlocked.Increment(ref _generation);
    }

    private bool IsStale
    {
        get
        {
            if (_models is null || _contextWindows is null)
                return true;

            var loadedAt = Interlocked.Read(ref _loadedAtTicks);
            return loadedAt == 0 || Stopwatch.GetElapsedTime(loadedAt) >= Freshness;
        }
    }

    /// <summary>Fingerprint of the currently cached catalog, or empty when nothing is cached yet.</summary>
    private string CurrentSignature
        => _models is not null && _contextWindows is not null
            ? BuildSignature(_models, _contextWindows)
            : string.Empty;

    private void Stamp() => Interlocked.Exchange(ref _loadedAtTicks, Stopwatch.GetTimestamp());

    /// <summary>
    /// Builds a stable, order-independent fingerprint of everything Lumi's UI derives from the model
    /// catalog (ids, reasoning efforts, context limits). Two catalogs with the same fingerprint
    /// produce an identical picker, so a refresh can skip rebinding entirely.
    /// </summary>
    internal static string BuildSignature(
        IEnumerable<ModelInfo> models,
        ModelContextWindowCatalog contextWindows)
    {
        var builder = new StringBuilder();
        foreach (var model in models
                     .Where(static model => !string.IsNullOrWhiteSpace(model.Id))
                     .OrderBy(static model => model.Id, StringComparer.Ordinal))
        {
            builder.Append(model.Id).Append('|');
            builder.Append(model.DefaultReasoningEffort).Append('|');
            if (model.SupportedReasoningEfforts is { Count: > 0 } efforts)
                builder.AppendJoin(',', efforts.OrderBy(static effort => effort, StringComparer.Ordinal));
            builder.Append('|');
            builder.Append(model.Capabilities?.Limits?.MaxContextWindowTokens ?? 0).Append('|');

            if (contextWindows.Limits.TryGetValue(model.Id, out var limits))
                builder.Append(limits.Default).Append(':').Append(limits.LongContext ?? 0);
            builder.Append('|');
            builder.Append(contextWindows.LongContextModelIds.Contains(model.Id) ? '1' : '0');
            builder.Append('\n');
        }

        return builder.ToString();
    }
}
