using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Lumi.Services.Discover;
using StrataSearch;

namespace Lumi.ViewModels;

public enum DiscoverFilter
{
    All,
    Plugins,
    McpServers,
    Installed,
    Sources,
}

/// <summary>
/// Customize → Discover: plugins from plugin marketplaces (through the GitHub Copilot runtime) and
/// MCP servers from MCP registries, in one place to search, preview and add.
/// </summary>
public sealed partial class DiscoverViewModel : ObservableObject
{
    /// <summary>Cards per kind on the "All" front page.</summary>
    public const int PreviewCount = 8;

    private const int RegistryPageSize = 40;
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(350);

    /// <summary>
    /// How long typing pauses before the plugins are ranked again. Each ranking rebuilds the cards,
    /// so steady typing should rank once rather than per key.
    /// </summary>
    private static readonly TimeSpan RankingDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Cards the grid adds per page as it scrolls: whole rows at two, three or four columns.</summary>
    private const int GridPageSize = 24;

    /// <summary>Cards built per frame while a page fills, so a click never waits for a whole page.</summary>
    private const int GridBatchSize = 4;

    private readonly DataStore _dataStore;
    private readonly PluginCatalogService _plugins;
    private readonly McpRegistryClient _registry;
    private readonly Func<CapabilitySnapshot?> _capabilities;
    private readonly Dictionary<string, RegistryResults> _registryResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _registryTotals = new(StringComparer.OrdinalIgnoreCase);
    private List<PluginMarketplace> _marketplaces = [];
    private List<DiscoverCard> _pluginCards = [];
    private List<PluginInstallation> _installed = [];

    /// <summary>Installed plugins and added servers no loaded listing stands for, by card key.</summary>
    private readonly Dictionary<string, DiscoverCard> _unlistedCards = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The plugins in search order for a query, and the card list they were ranked from.</summary>
    private (string Query, List<DiscoverCard> Cards, List<DiscoverCard> Ranked)? _pluginRanking;

    /// <summary>The ranking running in the background, so a refresh does not start it again.</summary>
    private (string Query, List<DiscoverCard> Cards)? _pendingRanking;
    private CancellationTokenSource? _rankingCts;

    /// <summary>What the last refresh listed, for the counts and "See all" links.</summary>
    private List<DiscoverCard> _shownPlugins = [];
    private List<DiscoverCard> _shownServers = [];
    private CancellationTokenSource? _serverSearchCts;
    private Task? _loading;
    private bool _pluginsNeedConnection;
    private List<DiscoverCard> _gridItems = [];
    private int _gridRequested = GridPageSize;
    private bool _isGridFillScheduled;

    public DiscoverViewModel(
        DataStore dataStore,
        PluginCatalogService plugins,
        McpRegistryClient registry,
        Func<CapabilitySnapshot?> capabilities)
    {
        _dataStore = dataStore;
        _plugins = plugins;
        _registry = registry;
        _capabilities = capabilities;
        RebuildRegistryRows();
    }

    /// <summary>An MCP server was added or removed; chat sessions pick up the change.</summary>
    public event Action? McpServersChanged;

    /// <summary>Open a Lumi MCP server in its editor (true when values must still be filled in).</summary>
    public event Action<Guid, bool>? OpenMcpServerRequested;

    /// <summary>Show a plugin's skills, Lumis or MCP servers in their Customize section.</summary>
    public event Action<string, CapabilityKind>? ShowPluginPartsRequested;

    /// <summary>Well-known setups, listed at the top of Everything whether or not Lumi suggested them.</summary>
    public SetupSuggestionsViewModel? Setups
    {
        get => _setups;
        init
        {
            _setups = value;
            RefreshSetups();
        }
    }

    private readonly SetupSuggestionsViewModel? _setups;
    private IReadOnlyList<SetupSuggestionItem> _setupsInView = [];

    public IReadOnlyList<SetupSuggestionItem> SetupsInView => _setupsInView;

    public bool HasSetupsInView => _setupsInView.Count > 0;

    private void RefreshSetups()
    {
        IReadOnlyList<SetupSuggestionItem> setups = _setups is not null && IsAll && SourceKey is null
            ? _setups.Items.Where(item => item.Matches(SearchQuery)).ToList()
            : [];
        if (setups.SequenceEqual(_setupsInView))
            return;

        _setupsInView = setups;
        OnPropertyChanged(nameof(SetupsInView));
        OnPropertyChanged(nameof(HasSetupsInView));
    }

    [ObservableProperty] private DiscoverFilter _filter;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string? _sourceKey;

    [ObservableProperty] private bool _isLoadingPlugins;
    [ObservableProperty] private bool _isLoadingServers;
    [ObservableProperty] private bool _isLoadingMoreServers;
    [ObservableProperty] private string? _pluginError;
    [ObservableProperty] private string? _serverError;

    /// <summary>A brief confirmation at the bottom of the page ("Added Azure").</summary>
    [ObservableProperty] private string? _toast;

    /// <summary>The Sources page's add boxes.</summary>
    [ObservableProperty] private string _newMarketplace = "";
    [ObservableProperty] private string _newRegistry = "";
    [ObservableProperty] private bool _isAddingSource;
    [ObservableProperty] private string? _sourceError;

    /// <summary>The "All" page's plugin cards.</summary>
    public ObservableCollection<DiscoverCard> PluginPreview { get; } = [];

    /// <summary>The "All" page's MCP server cards.</summary>
    public ObservableCollection<DiscoverCard> ServerPreview { get; } = [];

    /// <summary>The grid for the Plugins, MCP servers and Installed filters.</summary>
    public ObservableCollection<DiscoverCard> Cards { get; } = [];

    public ObservableCollection<DiscoverSourceRow> SourceRows { get; } = [];
    public ObservableCollection<MarketplaceRow> MarketplaceRows { get; } = [];
    public ObservableCollection<RegistryRow> RegistryRows { get; } = [];
    public ObservableCollection<SourceSuggestion> SourceSuggestions { get; } = [];

    public bool IsAll => Filter == DiscoverFilter.All;
    public bool IsPlugins => Filter == DiscoverFilter.Plugins;
    public bool IsServers => Filter == DiscoverFilter.McpServers;
    public bool IsInstalled => Filter == DiscoverFilter.Installed;
    public bool IsSources => Filter == DiscoverFilter.Sources;
    public bool IsGrid => Filter is DiscoverFilter.Plugins or DiscoverFilter.McpServers or DiscoverFilter.Installed;

    /// <summary>The sidebar's kind rows are selected only while no single source narrows the list.</summary>
    public bool IsPluginsFacet => IsPlugins && SourceKey is null;
    public bool IsServersFacet => IsServers && SourceKey is null;
    public bool ShowGallery => !IsViewingDetail && !IsSources;
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchQuery);

    public string PluginCountText => _pluginCards.Count.ToString(CultureInfo.CurrentCulture);
    public string ServerCountText
    {
        get
        {
            var total = RegistryUrls().Sum(url => _registryTotals.TryGetValue(url, out var known) ? known : 0);
            return total == 0 ? "" : total.ToString(CultureInfo.CurrentCulture);
        }
    }

    /// <summary>Everything added from Discover; like the other sidebar counts, it ignores the search.</summary>
    public string InstalledCountText => (_installed.Count + _dataStore.Data.McpServers.Count(static server => !string.IsNullOrWhiteSpace(server.CatalogId)))
        .ToString(CultureInfo.CurrentCulture);

    public bool IsAnyLoading => IsLoadingPlugins || IsLoadingServers;
    public bool HasPluginPreview => PluginPreview.Count > 0;
    public bool HasServerPreview => ServerPreview.Count > 0;
    public bool HasCards => Cards.Count > 0;

    /// <summary>The grid has cards beyond the pages shown so far.</summary>
    public bool HasMoreCards => _gridRequested < _gridItems.Count;

    /// <summary>The grid's cards are on their way: placeholders hold their place.</summary>
    public bool IsPreparingGrid => IsGrid && Cards.Count == 0 && _gridItems.Count > 0;
    public bool HasPluginError => !string.IsNullOrWhiteSpace(PluginError);
    public bool HasServerError => !string.IsNullOrWhiteSpace(ServerError);
    public bool HasToast => !string.IsNullOrWhiteSpace(Toast);
    public bool HasSourceError => !string.IsNullOrWhiteSpace(SourceError);
    public bool HasSourceSuggestions => SourceSuggestions.Count > 0;
    // Only the registries this view fetches from count: another source's cursor cannot add cards here.
    public bool CanLoadMoreServers => IsServers && !HasMoreCards && !IsLoadingMoreServers
        && RegistriesInView().Any(url => _registryResults.TryGetValue(url, out var results) && results.NextCursor is not null);

    /// <summary>Plugins come from the runtime, which is still starting.</summary>
    public bool IsWaitingForCopilot => _pluginsNeedConnection && !_plugins.IsAvailable;

    public bool ShowPluginEmpty => !IsLoadingPlugins && !HasPluginError && !IsWaitingForCopilot && PluginPreview.Count == 0;
    public bool ShowServerEmpty => !IsLoadingServers && !HasServerError && ServerPreview.Count == 0;
    public bool ShowGridEmpty => IsGrid && _gridItems.Count == 0 && !IsGridLoading;
    public bool IsGridLoading => (IsPlugins && IsLoadingPlugins) || (IsServers && IsLoadingServers) || (IsInstalled && IsLoadingPlugins) || IsPreparingGrid;

    public string PluginSeeAllText => string.Format(CultureInfo.CurrentCulture, Loc.Discover_SeeAll, _shownPlugins.Count);
    public string ServerSeeAllText => Loc.Discover_SeeAllServers;
    public bool HasMorePlugins => _shownPlugins.Count > PreviewCount;
    public bool HasMoreServers => _shownServers.Count > PreviewCount || _registryResults.Values.Any(static results => results.NextCursor is not null);

    /// <summary>The header of the grid: what is listed and how many.</summary>
    public string GridTitle => Filter switch
    {
        DiscoverFilter.Plugins => SelectedSourceRow?.Title ?? Loc.Discover_FacetPlugins,
        DiscoverFilter.McpServers => SelectedSourceRow?.Title ?? Loc.Discover_FacetServers,
        DiscoverFilter.Installed => Loc.Discover_FacetInstalled,
        _ => Loc.Discover_Title,
    };

    public string GridCaption => Filter switch
    {
        DiscoverFilter.Plugins => Loc.Discover_PluginsCaption,
        DiscoverFilter.McpServers => Loc.Discover_ServersCaption,
        DiscoverFilter.Installed => Loc.Discover_InstalledCaption,
        _ => "",
    };

    public string EmptyGridText => Filter switch
    {
        DiscoverFilter.Installed when !IsSearching => Loc.Discover_InstalledEmpty,
        _ when IsSearching => string.Format(CultureInfo.CurrentCulture, Loc.Mg_NoResults, SearchQuery.Trim()),
        _ => Loc.Discover_NothingHere,
    };

    private DiscoverSourceRow? SelectedSourceRow => SourceRows.FirstOrDefault(row => row.Key == SourceKey);

    /// <summary>Loads marketplaces, installed plugins and the first page of each registry once.</summary>
    public Task EnsureLoadedAsync() => _loading ??= LoadAllAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            if (_plugins.IsAvailable)
                await _plugins.RefreshAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Discover] Marketplace refresh failed: {ex.Message}");
        }

        _loading = LoadAllAsync();
        await _loading;
    }

    /// <summary>The Copilot runtime connected (or reconnected): plugins can load now.</summary>
    public void NotifyConnectionChanged(bool isConnected)
    {
        OnPropertyChanged(nameof(IsWaitingForCopilot));
        if (isConnected && _pluginsNeedConnection && _loading is not null)
            _ = LoadPluginsAsync();
    }

    /// <summary>The capability catalog changed: refresh what installed plugins add.</summary>
    public void NotifyCapabilitiesChanged()
    {
        if (OpenCard is { IsPlugin: true, Installation: not null } card)
            ShowInstalledParts(card);
    }

    /// <summary>MCP servers changed elsewhere (edited, deleted): keep "Added" accurate.</summary>
    public void RefreshAddedServers()
    {
        foreach (var card in _registryResults.Values.SelectMany(static results => results.Cards))
            card.AddedServer = FindAddedServer(card.Server?.Name);

        if (OpenCard is { IsServer: true } open)
        {
            open.AddedServer = FindAddedServer(open.Server?.Name ?? open.AddedServer?.CatalogId);
            NotifyDetail();
        }

        RefreshView();
    }

    [RelayCommand]
    private void ShowAll() => SetFilter(DiscoverFilter.All, null);

    [RelayCommand]
    private void ShowPlugins() => SetFilter(DiscoverFilter.Plugins, null);

    [RelayCommand]
    private void ShowServers() => SetFilter(DiscoverFilter.McpServers, null);

    [RelayCommand]
    private void ShowInstalled() => SetFilter(DiscoverFilter.Installed, null);

    [RelayCommand]
    private void ShowSources() => SetFilter(DiscoverFilter.Sources, null);

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    [RelayCommand]
    private Task LoadMoreServersAsync() => LoadServersAsync(append: true);

    [RelayCommand]
    private Task RetryAsync()
    {
        _loading = LoadAllAsync();
        return _loading;
    }

    private void SetFilter(DiscoverFilter filter, string? sourceKey)
    {
        CloseDetail();
        Filter = filter;
        SourceKey = sourceKey;
        foreach (var row in SourceRows)
            row.IsSelected = row.Key == sourceKey;

        RefreshView(resetGrid: true);
    }

    private void SelectSource(DiscoverSourceRow row)
        => SetFilter(row.Kind == DiscoverKind.Plugin ? DiscoverFilter.Plugins : DiscoverFilter.McpServers, row.Key);

    partial void OnSourceKeyChanged(string? value)
    {
        OnPropertyChanged(nameof(IsPluginsFacet));
        OnPropertyChanged(nameof(IsServersFacet));
    }

    partial void OnFilterChanged(DiscoverFilter value)
    {
        OnPropertyChanged(nameof(IsPluginsFacet));
        OnPropertyChanged(nameof(IsServersFacet));
        OnPropertyChanged(nameof(IsAll));
        OnPropertyChanged(nameof(IsPlugins));
        OnPropertyChanged(nameof(IsServers));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsSources));
        OnPropertyChanged(nameof(IsGrid));
        OnPropertyChanged(nameof(ShowGallery));
        OnPropertyChanged(nameof(GridTitle));
        OnPropertyChanged(nameof(GridCaption));
        OnPropertyChanged(nameof(CanLoadMoreServers));
        if (value == DiscoverFilter.Sources)
            _ = LoadSourcesAsync();
    }

    partial void OnSearchQueryChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearching));
        if (IsViewingDetail)
            CloseDetail();

        // A ranking still running is for an earlier query. The new query's plugins come from the cache
        // or a fresh background ranking, and until that lands the list keeps what it shows.
        CancelRanking();
        RefreshView(resetGrid: true);

        _serverSearchCts?.Cancel();
        _serverSearchCts = new CancellationTokenSource();
        var token = _serverSearchCts.Token;
        _ = DebouncedServerSearchAsync(token);
    }

    private async Task DebouncedServerSearchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SearchDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!cancellationToken.IsCancellationRequested)
            await LoadServersAsync(append: false, cancellationToken);
    }

    partial void OnIsLoadingPluginsChanged(bool value) => NotifyLoadState();
    partial void OnIsLoadingServersChanged(bool value) => NotifyLoadState();
    partial void OnIsLoadingMoreServersChanged(bool value) => OnPropertyChanged(nameof(CanLoadMoreServers));
    partial void OnPluginErrorChanged(string? value) => NotifyLoadState();
    partial void OnServerErrorChanged(string? value) => NotifyLoadState();
    private int _toastVersion;

    partial void OnToastChanged(string? value)
    {
        OnPropertyChanged(nameof(HasToast));
        if (value is not null)
            _ = HideToastAsync(++_toastVersion);
    }

    private async Task HideToastAsync(int version)
    {
        await Task.Delay(2600);
        if (version == _toastVersion)
            Toast = null;
    }
    partial void OnSourceErrorChanged(string? value) => OnPropertyChanged(nameof(HasSourceError));

    private void NotifyLoadState()
    {
        OnPropertyChanged(nameof(IsAnyLoading));
        OnPropertyChanged(nameof(HasPluginError));
        OnPropertyChanged(nameof(HasServerError));
        OnPropertyChanged(nameof(ShowPluginEmpty));
        OnPropertyChanged(nameof(ShowServerEmpty));
        OnPropertyChanged(nameof(ShowGridEmpty));
        OnPropertyChanged(nameof(IsGridLoading));
        OnPropertyChanged(nameof(IsWaitingForCopilot));
    }

    private async Task LoadAllAsync()
    {
        await Task.WhenAll(LoadPluginsAsync(), LoadServersAsync(append: false));
    }

    private async Task LoadPluginsAsync()
    {
        if (!_plugins.IsAvailable)
        {
            _pluginsNeedConnection = true;
            NotifyLoadState();
            return;
        }

        _pluginsNeedConnection = false;
        IsLoadingPlugins = true;
        PluginError = null;
        try
        {
            var marketplaces = await _plugins.ListMarketplacesAsync(CancellationToken.None);
            var installedTask = _plugins.ListInstalledAsync(CancellationToken.None);
            var browseTasks = marketplaces
                .Select(marketplace => BrowseSafelyAsync(marketplace))
                .ToArray();
            var browsed = await Task.WhenAll(browseTasks);
            var installed = await installedTask;

            _marketplaces = marketplaces.ToList();
            ApplyPlugins(browsed.SelectMany(static listings => listings), installed);
            RebuildSourceRows();
            if (IsSources)
                RebuildMarketplaceRows();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Discover] Plugins failed to load: {ex}");
            PluginError = string.Format(CultureInfo.CurrentCulture, Loc.Discover_PluginsError, Describe(ex));
        }
        finally
        {
            IsLoadingPlugins = false;
            RefreshView();
        }
    }

    private async Task<IReadOnlyList<PluginListing>> BrowseSafelyAsync(PluginMarketplace marketplace)
    {
        try
        {
            return await _plugins.BrowseAsync(marketplace, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // One unreachable marketplace must not hide the others.
            Debug.WriteLine($"[Discover] Marketplace '{marketplace.Name}' failed: {ex.Message}");
            return [];
        }
    }

    private async Task LoadServersAsync(bool append, CancellationToken cancellationToken = default)
    {
        // A new query goes to every registry, so each view of the results answers the same query;
        // loading more only asks the registries the current view lists.
        var registries = append ? RegistriesInView() : RegistryUrls();

        if (append)
            IsLoadingMoreServers = true;
        else
            IsLoadingServers = true;
        ServerError = null;

        var query = SearchQuery;
        var errors = new List<string>();
        try
        {
            foreach (var url in registries)
            {
                var state = _registryResults.TryGetValue(url, out var existing) ? existing : null;
                if (append && state?.NextCursor is null)
                    continue;

                try
                {
                    var page = await _registry.ListAsync(url, RegistrySearchTerm(query), append ? state?.NextCursor : null, RegistryPageSize, cancellationToken);
                    if (string.IsNullOrWhiteSpace(query) && page.Total is { } registryTotal)
                        _registryTotals[url] = registryTotal;
                    if (cancellationToken.IsCancellationRequested || !string.Equals(query, SearchQuery, StringComparison.Ordinal))
                        return;

                    state = append && state is not null ? state : new RegistryResults();
                    var known = state.Cards.Select(static card => card.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var server in page.Servers)
                    {
                        var card = DiscoverCard.ForServer(server, RegistryLabel(url), OpenCardFromGallery);
                        if (!known.Add(card.Key))
                            continue;

                        card.AddedServer = FindAddedServer(server.Name);
                        state.Cards.Add(card);
                    }

                    state.NextCursor = page.Servers.Count == 0 ? null : page.NextCursor;
                    _registryResults[url] = state;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // Only a newer search cancels this one; HttpClient reports a timeout as a cancellation too.
                    Debug.WriteLine($"[Discover] Registry '{url}' failed: {ex.Message}");
                    errors.Add(RegistryLabel(url));
                    if (!append)
                        _registryResults.Remove(url);
                }
            }

            if (errors.Count > 0)
                ServerError = string.Format(CultureInfo.CurrentCulture, Loc.Discover_ServersError, string.Join(", ", errors));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsLoadingServers = false;
            IsLoadingMoreServers = false;
            RebuildSourceRows();
            RefreshView();
        }
    }

    /// <summary>Rebuilds the visible collections from the loaded data, the filter and the search.</summary>
    /// <param name="resetGrid">The filter, source or search changed: the grid starts again at its first page.</param>
    private void RefreshView(bool resetGrid = false)
    {
        RefreshSetups();
        var plugins = FilteredPlugins();
        var servers = ServerCardsInView().ToList();
        _shownPlugins = plugins;
        _shownServers = servers;

        Replace(PluginPreview, plugins.Take(PreviewCount));
        Replace(ServerPreview, servers.Take(PreviewCount));
        SyncGrid(Filter switch
        {
            DiscoverFilter.Plugins => plugins,
            DiscoverFilter.McpServers => servers,
            DiscoverFilter.Installed => InstalledCards().ToList(),
            _ => [],
        }, resetGrid);

        foreach (var card in PluginPreview.Concat(ServerPreview))
            card.LoadAvatar();

        OnPropertyChanged(nameof(PluginCountText));
        OnPropertyChanged(nameof(ServerCountText));
        OnPropertyChanged(nameof(InstalledCountText));
        OnPropertyChanged(nameof(HasPluginPreview));
        OnPropertyChanged(nameof(HasServerPreview));
        OnPropertyChanged(nameof(ShowPluginEmpty));
        OnPropertyChanged(nameof(ShowServerEmpty));
        OnPropertyChanged(nameof(PluginSeeAllText));
        OnPropertyChanged(nameof(HasMorePlugins));
        OnPropertyChanged(nameof(HasMoreServers));
        OnPropertyChanged(nameof(GridTitle));
        OnPropertyChanged(nameof(EmptyGridText));
        OnPropertyChanged(nameof(IsWaitingForCopilot));

        UpdateSourceCounts();
    }

    /// <summary>
    /// Shows the grid a page at a time and builds its cards a few per frame. A marketplace lists
    /// hundreds of plugins; building every card at once froze the window for seconds.
    /// </summary>
    private void SyncGrid(List<DiscoverCard> items, bool reset)
    {
        _gridItems = items;
        if (reset)
            _gridRequested = GridPageSize;

        // Keep the cards already on screen that still lead the list, so a refresh does not rebuild them.
        var target = Math.Min(_gridRequested, items.Count);
        var keep = 0;
        while (keep < Cards.Count && keep < target && ReferenceEquals(Cards[keep], items[keep]))
            keep++;

        if (keep == 0)
        {
            if (Cards.Count > 0)
                Cards.Clear();
        }
        else
        {
            while (Cards.Count > keep)
                Cards.RemoveAt(Cards.Count - 1);
        }

        if (target - Cards.Count <= GridBatchSize)
            AppendCards(target);
        else
            ScheduleGridFill();

        NotifyGridState();
    }

    private void ScheduleGridFill()
    {
        if (_isGridFillScheduled)
            return;

        _isGridFillScheduled = true;
        // A short timer lets input and rendering run between batches. Background priority would
        // wait for a moment without pending input, which never comes while the user scrolls.
        DispatcherTimer.RunOnce(FillGridStep, TimeSpan.FromMilliseconds(1), DispatcherPriority.Normal);
    }

    private void FillGridStep()
    {
        _isGridFillScheduled = false;
        var target = Math.Min(_gridRequested, _gridItems.Count);
        AppendCards(Math.Min(Cards.Count + GridBatchSize, target));
        if (Cards.Count < target)
            ScheduleGridFill();
        NotifyGridState();
    }

    private void AppendCards(int count)
    {
        for (var i = Cards.Count; i < count && i < _gridItems.Count; i++)
        {
            var card = _gridItems[i];
            Cards.Add(card);
            card.LoadAvatar();
        }
    }

    /// <summary>The grid scrolled near its end: show the next page, or fetch one from the registries.</summary>
    public void ShowMoreCards()
    {
        // Still building the last page: its cards will extend the scroll and ask again.
        if (!IsGrid || Cards.Count < Math.Min(_gridRequested, _gridItems.Count))
            return;

        if (_gridRequested < _gridItems.Count)
        {
            _gridRequested += GridPageSize;
            ScheduleGridFill();
            NotifyGridState();
            return;
        }

        // A failed fetch is retried from its banner or the Load more button, never by scrolling.
        if (CanLoadMoreServers && !HasServerError && LoadMoreServersCommand.CanExecute(null))
        {
            // The fetched page shows as it arrives, without waiting for another scroll.
            _gridRequested += GridPageSize;
            LoadMoreServersCommand.Execute(null);
        }
    }

    private void NotifyGridState()
    {
        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(HasMoreCards));
        OnPropertyChanged(nameof(IsPreparingGrid));
        OnPropertyChanged(nameof(IsGridLoading));
        OnPropertyChanged(nameof(ShowGridEmpty));
        OnPropertyChanged(nameof(CanLoadMoreServers));
    }

    private void UpdateSourceCounts()
    {
        foreach (var row in SourceRows)
        {
            row.CountText = row.Kind == DiscoverKind.Plugin
                ? _pluginCards.Count(card => "mp:" + card.Listing?.Marketplace == row.Key).ToString(CultureInfo.CurrentCulture)
                : _registryTotals.TryGetValue(row.Key[4..], out var total)
                    ? total.ToString(CultureInfo.CurrentCulture)
                    : "";
        }
    }

    private List<DiscoverCard> FilteredPlugins()
    {
        // No popularity data exists for plugins, so the front page favours well-described ones.
        IEnumerable<DiscoverCard> cards = IsSearching
            ? SearchedPlugins()
            : _pluginCards
                .OrderByDescending(static card => card.IsAdded)
                .ThenByDescending(static card => Richness(card))
                .ThenBy(static card => card.Title, StringComparer.CurrentCultureIgnoreCase);

        return SourceKey is { } key && key.StartsWith("mp:", StringComparison.Ordinal)
            ? cards.Where(card => "mp:" + card.Listing?.Marketplace == key).ToList()
            : cards.ToList();
    }

    /// <summary>
    /// The plugins in the order the search ranks them. Ranking a few hundred plugins takes long
    /// enough to stall the window, and installs, registry results and every keystroke refresh the
    /// view, so the ranking is computed once per query in the background and reused. Until the
    /// query's ranking lands, the view keeps what it already shows.
    /// </summary>
    private IReadOnlyList<DiscoverCard> SearchedPlugins()
    {
        var query = SearchQuery.Trim();
        if (_pluginRanking is { } ranking && ranking.Query == query && ReferenceEquals(ranking.Cards, _pluginCards))
            return ranking.Ranked;

        RankPluginsInBackground(query);
        return _shownPlugins;
    }

    private void RankPluginsInBackground(string query)
    {
        var cards = _pluginCards;
        if (_pendingRanking is { } pending && pending.Query == query && ReferenceEquals(pending.Cards, cards))
            return;

        CancelRanking();
        var cancellation = _rankingCts = new CancellationTokenSource();
        _pendingRanking = (query, cards);

        // Cards change only on the UI thread, so their text is read here and ranked elsewhere.
        var documents = cards
            .Select(static card => (Card: card, Fields: PluginSearchFields(card), card.Title))
            .ToArray();
        _ = RankAsync();

        async Task RankAsync()
        {
            try
            {
                await Task.Delay(RankingDelay, cancellation.Token);
                var ranked = await Task.Run(
                    () => SearchPipeline.Rank(documents, query, static document => document.Fields, static document => new SearchSortMetadata(Text: document.Title))
                        .Select(static document => document.Card)
                        .ToList(),
                    cancellation.Token);
                if (cancellation.IsCancellationRequested)
                    return;

                _pendingRanking = null;
                _pluginRanking = (query, cards, ranked);
                RefreshView();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static SearchField[] PluginSearchFields(DiscoverCard card) =>
    [
        SearchField.Primary(card.Title, 3.2),
        SearchField.Primary(card.Listing?.Name ?? "", 2.6),
        new SearchField(card.Description, 1.6),
        new SearchField(string.Join(' ', card.Listing?.Keywords ?? []), 1.4),
        new SearchField(card.Publisher ?? "", 1.1),
    ];

    private void CancelRanking()
    {
        _rankingCts?.Cancel();
        _rankingCts = null;
        _pendingRanking = null;
    }

    private static int Richness(DiscoverCard card)
        => (card.HasPublisher ? 2 : 0)
           + (card.Listing?.Keywords.Count > 0 ? 1 : 0)
           + (card.Listing?.Homepage is not null ? 1 : 0)
           + (card.Description.Length > 80 ? 1 : 0);

    private IEnumerable<DiscoverCard> ServerCardsInView()
    {
        var urls = RegistriesInView();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cards = new List<DiscoverCard>();
        foreach (var url in urls)
        {
            if (!_registryResults.TryGetValue(url, out var results))
                continue;

            cards.AddRange(results.Cards.Where(card => seen.Add(card.Key)));
        }

        // The registry matches one phrase; a query of several words was sent as its most telling
        // word, so the results are ranked here against everything the user typed.
        if (!IsSearching || !SearchQuery.Trim().Contains(' '))
            return cards;

        return SearchPipeline.Rank(
            cards,
            SearchQuery,
            static card =>
            [
                SearchField.Primary(card.Title, 3.2),
                new SearchField(card.Server?.Name ?? "", 2.0),
                new SearchField(card.Description, 1.6),
                new SearchField(card.Publisher ?? "", 1.2),
            ],
            static card => new SearchSortMetadata(Text: card.Title));
    }

    /// <summary>
    /// Why a request failed, in words people understand. Discover never cancels the requests it
    /// waits for, so a cancellation reaching a handler is HttpClient's timeout, not the user.
    /// </summary>
    internal static string Describe(Exception ex)
        => ex is OperationCanceledException or TimeoutException ? Loc.Discover_TimedOut : ex.GetBaseException().Message;

    /// <summary>The registry API searches for one phrase; the longest word of a longer query finds the most.</summary>
    internal static string? RegistrySearchTerm(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var words = query.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= 1 ? query.Trim() : words.OrderByDescending(static word => word.Length).First();
    }

    private IEnumerable<DiscoverCard> InstalledCards()
    {
        var plugins = _installed.Select(InstalledPluginCard);
        var serverCards = ServerCardsInView().ToDictionary(static card => card.Key, StringComparer.OrdinalIgnoreCase);
        var servers = _dataStore.Data.McpServers
            .Where(static server => !string.IsNullOrWhiteSpace(server.CatalogId))
            .Select(server => serverCards.TryGetValue(DiscoverCard.ServerKey(server.CatalogId!), out var card)
                ? card
                : AddedServerCard(server));

        var all = plugins.Concat(servers);
        if (!IsSearching)
            return all.OrderBy(static card => card.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

        return SearchPipeline.Rank(
            all.ToList(),
            SearchQuery,
            static card =>
            [
                SearchField.Primary(card.Title, 3.2),
                new SearchField(card.Description, 1.6),
            ],
            static card => new SearchSortMetadata(Text: card.Title)).ToList();
    }

    private void ApplyInstallState()
    {
        foreach (var card in _pluginCards)
        {
            card.Installation = _installed.FirstOrDefault(installation =>
                DiscoverCard.PluginKey(installation.Name, installation.Marketplace) == card.Key);
        }
    }

    /// <summary>
    /// Turns the marketplaces' listings and the installed plugins into cards. Cards built before are
    /// kept and updated, so their avatars and state survive a reload.
    /// </summary>
    internal void ApplyPlugins(IEnumerable<PluginListing> listings, IReadOnlyList<PluginInstallation> installed)
    {
        _installed = installed.ToList();
        var existing = _pluginCards.ToDictionary(static card => card.Key, StringComparer.OrdinalIgnoreCase);
        _pluginCards = listings
            .GroupBy(static listing => DiscoverCard.PluginKey(listing.Name, listing.Marketplace), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var listing = group.First();
                if (existing.TryGetValue(group.Key, out var card))
                {
                    card.ApplyListing(listing);
                    return card;
                }

                return DiscoverCard.ForPlugin(listing, OpenCardFromGallery);
            })
            .ToList();
        ApplyInstallState();
    }

    /// <summary>
    /// The card for an installed plugin: its marketplace listing's, else one of its own. Those are
    /// kept, like the listings' cards, so a refresh does not rebuild the Installed grid.
    /// </summary>
    private DiscoverCard InstalledPluginCard(PluginInstallation installation)
    {
        var key = DiscoverCard.PluginKey(installation.Name, installation.Marketplace);
        if (_pluginCards.FirstOrDefault(card => card.Key == key) is { } listed)
            return listed;

        if (!_unlistedCards.TryGetValue(key, out var card))
            _unlistedCards[key] = card = DiscoverCard.ForInstalledPlugin(installation, OpenCardFromGallery);
        card.Installation = installation;
        return card;
    }

    /// <summary>The card for an added server whose registry entry is not among the loaded results.</summary>
    private DiscoverCard AddedServerCard(McpServer server)
    {
        var key = DiscoverCard.ServerKey(server.CatalogId!);
        if (!_unlistedCards.TryGetValue(key, out var card)
            || !ReferenceEquals(card.AddedServer, server)
            || card.Title != server.Name
            || card.Description != server.Description)
        {
            _unlistedCards[key] = card = DiscoverCard.ForAddedServer(server, OpenCardFromGallery);
        }

        return card;
    }

    private McpServer? FindAddedServer(string? registryName)
        => string.IsNullOrWhiteSpace(registryName)
            ? null
            : _dataStore.Data.McpServers.FirstOrDefault(server =>
                string.Equals(server.CatalogId, registryName, StringComparison.OrdinalIgnoreCase));

    private List<string> RegistryUrls()
        => (_dataStore.Data.Settings.McpRegistryUrls ?? [])
            .Where(static url => Uri.TryCreate(url, UriKind.Absolute, out _))
            .Select(static url => url.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The registries the current view lists: all of them, or the one source picked in the sidebar.</summary>
    private List<string> RegistriesInView()
    {
        var urls = RegistryUrls();
        return SourceKey is { } key && key.StartsWith("reg:", StringComparison.Ordinal)
            ? urls.Where(url => "reg:" + url == key).ToList()
            : urls;
    }

    private void RebuildSourceRows()
    {
        var rows = _marketplaces
            .Select(marketplace => new DiscoverSourceRow("mp:" + marketplace.Name, DiscoverKind.Plugin, marketplace.Name, SelectSource))
            .Concat(RegistryUrls().Select(url => new DiscoverSourceRow("reg:" + url, DiscoverKind.McpServer, RegistryLabel(url), SelectSource)))
            .ToList();

        // Loads finish often; recreating the same rows would only tear down what the pointer is on.
        if (!SourceRows.Select(static row => (row.Key, row.Title))
                .SequenceEqual(rows.Select(static row => (row.Key, row.Title))))
        {
            Replace(SourceRows, rows);
        }

        foreach (var row in SourceRows)
            row.IsSelected = row.Key == SourceKey;
        UpdateSourceCounts();
    }

    private void RebuildRegistryRows()
    {
        Replace(RegistryRows, RegistryUrls().Select(url => new RegistryRow(url, RemoveRegistry)));
        RebuildSuggestions();
    }

    private void RebuildSuggestions()
    {
        var suggestions = new List<SourceSuggestion>();
        if (!_marketplaces.Any(static marketplace => marketplace.Repository?.FullName.Equals(KnownMarketplace, StringComparison.OrdinalIgnoreCase) == true))
            suggestions.Add(new SourceSuggestion(KnownMarketplace, Loc.Discover_SuggestGitHubPluginsTitle, Loc.Discover_SuggestGitHubPlugins, AddSuggestionAsync));
        if (!RegistryUrls().Any(static url => url.Equals(UserSettings.DefaultMcpRegistryUrl, StringComparison.OrdinalIgnoreCase)))
            suggestions.Add(new SourceSuggestion(UserSettings.DefaultMcpRegistryUrl, Loc.Discover_GitHubRegistry, Loc.Discover_SuggestGitHubRegistry, AddSuggestionAsync));
        if (!RegistryUrls().Any(static url => url.Equals(CommunityRegistry, StringComparison.OrdinalIgnoreCase)))
            suggestions.Add(new SourceSuggestion(CommunityRegistry, Loc.Discover_CommunityRegistry, Loc.Discover_SuggestCommunityRegistry, AddSuggestionAsync));

        Replace(SourceSuggestions, suggestions);
        OnPropertyChanged(nameof(HasSourceSuggestions));
    }

    private const string KnownMarketplace = "github/copilot-plugins";
    private const string CommunityRegistry = "https://registry.modelcontextprotocol.io";

    /// <summary>Friendly names for registries people are likely to use; anything else shows its host.</summary>
    public static string RegistryLabel(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        if (trimmed.Equals(UserSettings.DefaultMcpRegistryUrl, StringComparison.OrdinalIgnoreCase))
            return Loc.Discover_GitHubRegistry;
        if (trimmed.Equals(CommunityRegistry, StringComparison.OrdinalIgnoreCase))
            return Loc.Discover_CommunityRegistry;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ? uri.Host : trimmed;
    }

    private async Task LoadSourcesAsync()
    {
        if (_plugins.IsAvailable)
        {
            try
            {
                _marketplaces = (await _plugins.ListMarketplacesAsync(CancellationToken.None)).ToList();
            }
            catch (Exception ex)
            {
                SourceError = Describe(ex);
            }
        }

        RebuildMarketplaceRows();
        RebuildRegistryRows();
        RebuildSourceRows();
    }

    private void RebuildMarketplaceRows()
        => Replace(MarketplaceRows, _marketplaces.Select(marketplace => new MarketplaceRow(
            marketplace,
            _pluginCards.Count(card => card.Listing?.Marketplace == marketplace.Name),
            row => _ = RemoveMarketplaceAsync(row))));

    [RelayCommand]
    private Task AddMarketplaceAsync() => AddMarketplaceAsync(NewMarketplace);

    private async Task AddMarketplaceAsync(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || IsAddingSource)
            return;

        if (!_plugins.IsAvailable)
        {
            SourceError = Loc.Discover_WaitingForCopilot;
            return;
        }

        IsAddingSource = true;
        SourceError = null;
        try
        {
            var name = await _plugins.AddMarketplaceAsync(source, CancellationToken.None);
            NewMarketplace = "";
            Toast = string.Format(CultureInfo.CurrentCulture, Loc.Discover_SourceAdded, name);
            await LoadPluginsAsync();
            await LoadSourcesAsync();
        }
        catch (Exception ex)
        {
            SourceError = string.Format(CultureInfo.CurrentCulture, Loc.Discover_SourceAddFailed, Describe(ex));
        }
        finally
        {
            IsAddingSource = false;
        }
    }

    private async Task RemoveMarketplaceAsync(MarketplaceRow row)
    {
        if (!row.CanRemove)
            return;

        SourceError = null;
        try
        {
            var outcome = await _plugins.RemoveMarketplaceAsync(row.Name, CancellationToken.None);
            if (!outcome.Removed)
            {
                SourceError = string.Format(CultureInfo.CurrentCulture, Loc.Discover_SourceInUse, row.Name, string.Join(", ", outcome.DependentPlugins));
                return;
            }

            await LoadPluginsAsync();
            await LoadSourcesAsync();
        }
        catch (Exception ex)
        {
            SourceError = Describe(ex);
        }
    }

    [RelayCommand]
    private Task AddRegistryAsync() => AddRegistryAsync(NewRegistry);

    private async Task AddRegistryAsync(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (url.EndsWith("/v0.1/servers", StringComparison.OrdinalIgnoreCase))
            url = url[..^"/v0.1/servers".Length];
        if (url.Length == 0 || IsAddingSource)
            return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            SourceError = Loc.Discover_RegistryInvalid;
            return;
        }

        if (RegistryUrls().Contains(url, StringComparer.OrdinalIgnoreCase))
        {
            NewRegistry = "";
            return;
        }

        IsAddingSource = true;
        SourceError = null;
        try
        {
            // A registry is only useful if it answers the registry API.
            await _registry.ListAsync(url, null, null, 1, CancellationToken.None);
            _dataStore.Data.Settings.McpRegistryUrls ??= [];
            _dataStore.Data.Settings.McpRegistryUrls.Add(url);
            _ = _dataStore.SaveAsync();
            NewRegistry = "";
            Toast = string.Format(CultureInfo.CurrentCulture, Loc.Discover_SourceAdded, RegistryLabel(url));
            RebuildRegistryRows();
            RebuildSourceRows();
            await LoadServersAsync(append: false);
        }
        catch (Exception ex)
        {
            SourceError = string.Format(CultureInfo.CurrentCulture, Loc.Discover_RegistryUnreachable, Describe(ex));
        }
        finally
        {
            IsAddingSource = false;
        }
    }

    private void RemoveRegistry(RegistryRow row)
    {
        _dataStore.Data.Settings.McpRegistryUrls?.RemoveAll(url => url.Trim().TrimEnd('/').Equals(row.Url, StringComparison.OrdinalIgnoreCase));
        _ = _dataStore.SaveAsync();
        _registryResults.Remove(row.Url);
        _registryTotals.Remove(row.Url);
        RebuildRegistryRows();
        RebuildSourceRows();
        RefreshView();
    }

    private Task AddSuggestionAsync(SourceSuggestion suggestion)
        => suggestion.Value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? AddRegistryAsync(suggestion.Value)
            : AddMarketplaceAsync(suggestion.Value);

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var list = items as IList<T> ?? items.ToList();
        if (target.Count == list.Count && target.SequenceEqual(list))
            return;

        target.Clear();
        foreach (var item in list)
            target.Add(item);
    }

    private sealed class RegistryResults
    {
        public List<DiscoverCard> Cards { get; } = [];
        public string? NextCursor { get; set; }
    }
}
