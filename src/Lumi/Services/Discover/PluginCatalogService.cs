using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Copilot;
using Lumi.Localization;

namespace Lumi.Services.Discover;

/// <summary>A plugin source registered with the Copilot runtime.</summary>
public sealed record PluginMarketplace(
    string Name,
    string Source,
    GitHubRepoRef? Repository,
    bool IsDefault,
    bool IsManaged);

/// <summary>A plugin a marketplace offers, with what its catalog says about it.</summary>
public sealed record PluginListing(
    string Name,
    string Marketplace,
    string? Description,
    string? Version,
    string? Author,
    string? AuthorUrl,
    string? Homepage,
    string? Repository,
    string? License,
    IReadOnlyList<string> Keywords,
    GitHubRepoRef? SourceFolder)
{
    /// <summary>The runtime's install spec.</summary>
    public string Spec => $"{Name}@{Marketplace}";
}

/// <summary>An installed plugin as the runtime reports it.</summary>
public sealed record PluginInstallation(
    string Name,
    string Marketplace,
    string? Version,
    bool Enabled,
    string? DirectSourceId,
    bool IsManaged)
{
    public string Spec => string.IsNullOrEmpty(Marketplace) ? Name : $"{Name}@{Marketplace}";
}

public sealed record PluginInstallOutcome(PluginInstallation? Plugin, string? PostInstallMessage, string? Warning);

public sealed record PluginUpdateOutcome(string? PreviousVersion, string? NewVersion);

public sealed record MarketplaceRemoveOutcome(bool Removed, IReadOnlyList<string> DependentPlugins);

/// <summary>Adding a marketplace was stopped: by whoever added it, or because its download stalled.</summary>
public sealed class MarketplaceAddStoppedException(string message, bool stalled, bool stillRunning, CancellationToken cancellationToken)
    : OperationCanceledException(message, cancellationToken)
{
    /// <summary>The download made no progress for <see cref="PluginCatalogService.StallTimeout"/>.</summary>
    public bool Stalled { get; } = stalled;

    /// <summary>No download was found to stop, so the runtime may still add the marketplace.</summary>
    public bool StillRunning { get; } = stillRunning;
}

/// <summary>
/// Plugins through the GitHub Copilot runtime: its marketplaces, installs, updates and removals.
/// Lumi shares the runtime's home with Copilot CLI, so a plugin added in either appears in both.
/// </summary>
/// <remarks>
/// The runtime's browse call names each plugin but says little else, so a marketplace hosted on
/// GitHub also has its <c>marketplace.json</c> read for authors, versions and where each plugin
/// lives. This optional metadata may be unavailable for private Git sources authenticated as
/// another identity; it must not prevent an explicit runtime update.
/// </remarks>
public sealed class PluginCatalogService
{
    private readonly Func<CopilotClient?> _client;
    private readonly Action _pluginsChanged;
    private readonly GitHubRepoReader _reader;
    private readonly PluginInspector _inspector;
    private readonly MarketplaceDownloads? _downloads;
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<MarketplaceListing>>> _manifests = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedRefreshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<PluginContents>> _contents = new(StringComparer.OrdinalIgnoreCase);

    public PluginCatalogService(CopilotService copilotService, GitHubRepoReader? reader = null)
        : this(
            () => copilotService.IsConnected ? copilotService.Client : null,
            copilotService.NotifyPluginsChanged,
            reader,
            new MarketplaceDownloads(CopilotService.RuntimeCacheDirectory, () => copilotService.RuntimeProcessId))
    {
    }

    /// <param name="downloads">The runtime's marketplace downloads; without them, an add can't be measured or stopped.</param>
    internal PluginCatalogService(
        Func<CopilotClient?> client,
        Action pluginsChanged,
        GitHubRepoReader? reader = null,
        MarketplaceDownloads? downloads = null)
    {
        _client = client;
        _pluginsChanged = pluginsChanged;
        _reader = reader ?? new GitHubRepoReader();
        _inspector = new PluginInspector(_reader);
        _downloads = downloads;
    }

    /// <summary>How often an add checks the download's progress.</summary>
    internal TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a download may make no progress before Lumi stops it.</summary>
    internal TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long the runtime gets to report a stopped download and remove its staging folder.</summary>
    internal TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>False until the Copilot runtime is connected.</summary>
    public bool IsAvailable => _client() is not null;

    public async Task<IReadOnlyList<PluginMarketplace>> ListMarketplacesAsync(CancellationToken cancellationToken)
    {
        var result = await Client.Rpc.Plugins.Marketplaces.ListAsync(cancellationToken).ConfigureAwait(false);
        return (result?.Marketplaces ?? [])
            .Where(static marketplace => marketplace.Available != false)
            .Select(static marketplace => new PluginMarketplace(
                marketplace.Name,
                marketplace.Source ?? "",
                GitHubRepoRef.TryParse(marketplace.Source),
                marketplace.IsDefault == true,
                marketplace.Managed == true))
            .ToArray();
    }

    /// <summary>Every plugin a marketplace offers.</summary>
    public async Task<IReadOnlyList<PluginListing>> BrowseAsync(PluginMarketplace marketplace, CancellationToken cancellationToken)
    {
        var browse = Client.Rpc.Plugins.Marketplaces.BrowseAsync(marketplace.Name, cancellationToken);
        var catalog = marketplace.Repository is { } repository
            ? ReadMarketplaceManifestAsync(repository, cancellationToken)
            : Task.FromResult<IReadOnlyList<MarketplaceListing>>([]);

        var result = await browse.ConfigureAwait(false);
        IReadOnlyList<MarketplaceListing> listings;
        try
        {
            listings = await catalog.ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Metadata access uses the Copilot identity, not necessarily the Git identity.
            // Neither failure nor missing metadata means the installed plugin is current.
            System.Diagnostics.Debug.WriteLine($"[Plugins] Catalog metadata unavailable: {ex.GetType().Name}");
            listings = [];
        }

        var details = listings
            .GroupBy(static listing => listing.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        return (result?.Plugins ?? [])
            .Where(static plugin => !string.IsNullOrWhiteSpace(plugin.Name))
            .Select(plugin =>
            {
                details.TryGetValue(plugin.Name, out var detail);
                return new PluginListing(
                    plugin.Name,
                    marketplace.Name,
                    plugin.Description ?? detail?.Description,
                    _failedRefreshes.Contains(marketplace.Name) ? null : detail?.Version,
                    detail?.Author,
                    detail?.AuthorUrl,
                    detail?.Homepage,
                    detail?.Repository,
                    detail?.License,
                    detail?.Keywords ?? [],
                    detail?.Source?.Resolve(marketplace.Repository));
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<PluginInstallation>> ListInstalledAsync(CancellationToken cancellationToken)
    {
        var result = await Client.Rpc.Plugins.ListAsync(cancellationToken).ConfigureAwait(false);
        return (result?.Plugins ?? [])
            .Where(static plugin => plugin.Installed != false && !string.IsNullOrWhiteSpace(plugin.Name))
            .Select(static plugin => new PluginInstallation(
                plugin.Name,
                plugin.Marketplace ?? "",
                plugin.Version,
                plugin.Enabled,
                plugin.DirectSourceId,
                plugin.Managed == true))
            .ToArray();
    }

    public async Task<PluginInstallOutcome> InstallAsync(PluginListing listing, CancellationToken cancellationToken)
    {
        var result = await Client.Rpc.Plugins.InstallAsync(listing.Spec, null, cancellationToken).ConfigureAwait(false);
        _pluginsChanged();

        var installed = (await ListInstalledAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(plugin => plugin.Name.Equals(listing.Name, StringComparison.OrdinalIgnoreCase)
                                      && plugin.Marketplace.Equals(listing.Marketplace, StringComparison.OrdinalIgnoreCase));
        return new PluginInstallOutcome(installed, result?.PostInstallMessage, result?.DeprecationWarning);
    }

    public async Task UninstallAsync(PluginInstallation plugin, CancellationToken cancellationToken)
    {
        await Client.Rpc.Plugins.UninstallAsync(plugin.Spec, plugin.DirectSourceId, cancellationToken).ConfigureAwait(false);
        _pluginsChanged();
    }

    public async Task<PluginUpdateOutcome> UpdateAsync(PluginInstallation plugin, CancellationToken cancellationToken)
    {
        var result = await Client.Rpc.Plugins.UpdateAsync(plugin.Spec, cancellationToken).ConfigureAwait(false);
        _pluginsChanged();
        return new PluginUpdateOutcome(result?.PreviousVersion, result?.NewVersion);
    }

    /// <summary>
    /// Registers a marketplace (<c>owner/repo</c>, a Git URL or a local folder). The runtime first
    /// downloads a Git marketplace's whole repository, which takes long for a large one, so
    /// <paramref name="progress"/> reports what has reached disk every <see cref="ProgressInterval"/>.
    /// The runtime ignores cancellation: cancelling, or a download that makes no progress for
    /// <see cref="StallTimeout"/>, stops the download itself, after which the runtime fails the add and
    /// removes its clone.
    /// </summary>
    /// <exception cref="MarketplaceAddStoppedException">The add was cancelled or its download stalled.</exception>
    public async Task<string> AddMarketplaceAsync(
        string source,
        CancellationToken cancellationToken,
        IProgress<MarketplaceDownloadProgress?>? progress = null)
    {
        source = source.Trim();
        var client = Client;
        var before = _downloads?.Snapshot() ?? new HashSet<string>();
        var add = client.Rpc.Plugins.Marketplaces.AddAsync(source, null, CancellationToken.None);
        MarketplaceDownloadProgress? last = null;
        var sinceChange = Stopwatch.StartNew();
        var stalled = false;
        try
        {
            while (!add.IsCompleted)
            {
                await Task.WhenAny(add, Task.Delay(ProgressInterval, cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (add.IsCompleted)
                    break;

                var current = _downloads?.Measure(before, source);
                if (current != last)
                {
                    last = current;
                    sinceChange.Restart();
                }
                else if (current is { Phase: MarketplaceDownloadPhase.Downloading } && sinceChange.Elapsed >= StallTimeout)
                {
                    // Only a download Lumi can see is judged: writing out the files is local work, and
                    // a clone Lumi can't find may still be making progress.
                    stalled = true;
                    break;
                }

                progress?.Report(current);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        if ((stalled || cancellationToken.IsCancellationRequested) && !add.IsCompleted)
        {
            var stillRunning = await StopAsync(add, before, source).ConfigureAwait(false);
            if (!add.IsCompletedSuccessfully)
            {
                var message = stillRunning ? string.Format(CultureInfo.CurrentCulture, Loc.Discover_AddStillRunning, source)
                    : stalled ? string.Format(CultureInfo.CurrentCulture, Loc.Discover_AddStalled, source, (int)Math.Ceiling(StallTimeout.TotalMinutes))
                    : string.Format(CultureInfo.CurrentCulture, Loc.Discover_AddStopped, source);
                throw new MarketplaceAddStoppedException(message, stalled, stillRunning, stalled ? CancellationToken.None : cancellationToken);
            }
        }

        var result = await add.ConfigureAwait(false);
        return result?.Name ?? source;
    }

    /// <summary>
    /// Stops an add's download and gives the runtime time to report the failure and remove its staging
    /// folder. True when no download was found to stop and the runtime is still adding.
    /// </summary>
    private async Task<bool> StopAsync(Task<GitHub.Copilot.Rpc.MarketplaceAddResult> add, IReadOnlySet<string> before, string source)
    {
        // A clone may not have started yet: look for it a few more times.
        var stopped = false;
        for (var attempt = 0; attempt < 5 && !add.IsCompleted && !stopped; attempt++)
        {
            stopped = _downloads?.Stop(before, source) == true;
            if (!stopped)
                await Task.WhenAny(add, Task.Delay(ProgressInterval)).ConfigureAwait(false);
        }

        if (stopped)
            await Task.WhenAny(add, Task.Delay(StopTimeout)).ConfigureAwait(false);

        // Once Lumi gives up on the add, nothing awaits it: observe its failure whenever it comes.
        _ = add.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return !add.IsCompleted;
    }

    /// <summary>
    /// Removes the clones of adds interrupted when Lumi or the runtime closed, which the runtime never
    /// removes. Anything git still writes, or written in the last ten minutes, stays, and so do locks.
    /// An isolated Lumi profile on the user's own runtime (a Release test run) leaves that cache alone.
    /// </summary>
    public Task RemoveAbandonedDownloadsAsync()
        => _downloads is { } downloads && !(DataStore.UsesIsolatedProfile && CopilotService.IsolatedRuntimeHome is null)
            ? Task.Run(() => downloads.RemoveAbandoned(TimeSpan.FromMinutes(10)))
            : Task.CompletedTask;

    public async Task<MarketplaceRemoveOutcome> RemoveMarketplaceAsync(string name, CancellationToken cancellationToken)
    {
        var result = await Client.Rpc.Plugins.Marketplaces.RemoveAsync(name, false, cancellationToken).ConfigureAwait(false);
        return new MarketplaceRemoveOutcome(result?.Removed == true, result?.DependentPlugins?.ToArray() ?? []);
    }

    /// <summary>Fetches every marketplace again and forgets cached catalogs and previews.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        _manifests.Clear();
        _contents.Clear();
        var result = await Client.Rpc.Plugins.Marketplaces.RefreshAsync(new(), cancellationToken).ConfigureAwait(false);
        _failedRefreshes.Clear();
        foreach (var entry in result?.Results ?? [])
        {
            if (!entry.Success)
                _failedRefreshes.Add(entry.Name);
        }
        if (_failedRefreshes.Count > 0)
            throw new InvalidOperationException(Loc.Get(nameof(Loc.Discover_RefreshFailed), string.Join(", ", _failedRefreshes)));
    }

    /// <summary>What a plugin adds, read from its repository before it is installed.</summary>
    public Task<PluginContents> InspectAsync(PluginListing listing, CancellationToken cancellationToken)
    {
        if (listing.SourceFolder is not { } folder)
            return Task.FromResult(PluginContents.Empty);

        var key = $"{folder.FullName}#{folder.Ref}:{folder.Path}";
        var task = _contents.GetOrAdd(key, _ => _inspector.InspectAsync(folder, CancellationToken.None));
        if (task.IsFaulted || task.IsCanceled)
        {
            _contents.TryRemove(key, out _);
            task = _contents.GetOrAdd(key, _ => _inspector.InspectAsync(folder, CancellationToken.None));
        }

        return task.WaitAsync(cancellationToken);
    }

    private Task<IReadOnlyList<MarketplaceListing>> ReadMarketplaceManifestAsync(GitHubRepoRef repository, CancellationToken cancellationToken)
    {
        var task = _manifests.GetOrAdd(repository.FullName + "#" + repository.Ref, _ => LoadManifestAsync(repository));
        if (task.IsFaulted || task.IsCanceled)
        {
            _manifests.TryRemove(repository.FullName + "#" + repository.Ref, out _);
            task = _manifests.GetOrAdd(repository.FullName + "#" + repository.Ref, _ => LoadManifestAsync(repository));
        }

        return task.WaitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<MarketplaceListing>> LoadManifestAsync(GitHubRepoRef repository)
    {
        foreach (var location in MarketplaceManifest.Locations)
        {
            if (await _reader.TryGetTextAsync(repository, location, CancellationToken.None).ConfigureAwait(false) is { } json)
                return MarketplaceManifest.Parse(json);
        }

        return [];
    }

    private CopilotClient Client
        => _client() ?? throw new InvalidOperationException(Loc.Discover_CopilotNotConnected);
}

/// <summary>
/// Where the runtime keeps each installed plugin. Its plugin list does not say, but the runtime
/// records the folder in its own config; installs without a record use the runtime's layout.
/// </summary>
public static class PluginInstallLocator
{
    public static IReadOnlyDictionary<string, string> Read(string runtimeHome)
    {
        var locations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(runtimeHome, "config.json");
        try
        {
            if (!File.Exists(path))
                return locations;

            using var document = JsonDocument.Parse(File.ReadAllText(path), JsonOptions.Lenient);
            if (!document.RootElement.TryGetProperty("installedPlugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array)
                return locations;

            foreach (var plugin in plugins.EnumerateArray())
            {
                if (JsonText.String(plugin, "name") is { } name && JsonText.String(plugin, "cache_path") is { } cachePath)
                    locations[Key(name, JsonText.String(plugin, "marketplace"))] = cachePath;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"[Plugins] Could not read '{path}': {ex.Message}");
        }

        return locations;
    }

    public static string? Resolve(IReadOnlyDictionary<string, string> locations, string runtimeHome, string name, string? marketplace)
    {
        if (locations.TryGetValue(Key(name, marketplace), out var recorded) && Directory.Exists(recorded))
            return recorded;

        if (string.IsNullOrEmpty(marketplace))
            return null;

        var conventional = Path.Combine(runtimeHome, "installed-plugins", marketplace, name);
        return Directory.Exists(conventional) ? conventional : null;
    }

    private static string Key(string name, string? marketplace) => $"{name}@{marketplace}";
}
