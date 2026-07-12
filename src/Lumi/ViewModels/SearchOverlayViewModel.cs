using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Services;

namespace Lumi.ViewModels;

public class SearchResultItem
{
    public GlobalSearchCategory CategoryKey { get; init; }
    public string Category { get; init; } = "";
    public string CategoryIcon { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public int NavIndex { get; init; }
    public object? Item { get; init; }
    public double Score { get; init; }
    public bool IsContentMatch { get; init; }
    public int SettingsPageIndex { get; init; } = -1;

    private Geometry? _categoryGeometry;
    public Geometry? CategoryGeometry => _categoryGeometry ??=
        !string.IsNullOrEmpty(CategoryIcon) ? StreamGeometry.Parse(CategoryIcon) : null;
}

public class SearchResultGroup
{
    public GlobalSearchCategory CategoryKey { get; init; }
    public string Category { get; init; } = "";
    public string CategoryIcon { get; init; } = "";
    public bool IsCurrentTab { get; init; }
    public bool ShowHeader { get; init; } = true;
    public int TotalCount { get; init; }
    public List<SearchResultItem> Items { get; init; } = [];
    public bool HasMore => TotalCount > Items.Count;
    public string CountText => TotalCount.ToString();
    public string ViewAllText => $"View all {TotalCount}";

    private Geometry? _categoryGeometry;
    public Geometry? CategoryGeometry => _categoryGeometry ??=
        !string.IsNullOrEmpty(CategoryIcon) ? StreamGeometry.Parse(CategoryIcon) : null;
}

public class SearchCategoryFilterItem
{
    public GlobalSearchCategory? CategoryKey { get; init; }
    public string Label { get; init; } = "";
    public string Icon { get; init; } = "";
    public int Count { get; init; }
    public bool IsSelected { get; init; }
    public string CountText => Count.ToString();

    private Geometry? _iconGeometry;
    public Geometry? IconGeometry => _iconGeometry ??=
        !string.IsNullOrEmpty(Icon) ? StreamGeometry.Parse(Icon) : null;
}

public partial class SearchOverlayViewModel : ObservableObject
{
    private readonly GlobalSearchService _searchService;
    private readonly Func<int> _getCurrentNavIndex;
    private CancellationTokenSource? _searchCts;
    private long _searchRequestId;
    private long _lastAppliedRequestId = -1;
    private bool _hasUserNavigatedSelection;

    private const int FastSearchDelayMs = 32;
    private const int InteractiveSearchDelayMs = 120;
    private const int FullSearchDelayMs = 400;
    private const int MinQueryLengthForFullSearch = 2;
    private const int MaxResultsPerGroup = 32;
    private const int MaxVisibleResults = 80;
    private const int AllVisibleResultLimit = 20;
    private const int AllChatsPreviewLimit = 6;
    private const int AllCategoryPreviewLimit = 3;

    private const string IconSearch = "M10 3a7 7 0 1 0 4.2 12.6l4.1 4.1a1 1 0 0 0 1.4-1.4l-4.1-4.1A7 7 0 0 0 10 3zm-5 7a5 5 0 1 1 10 0 5 5 0 0 1-10 0z";
    private const string IconChat = "M4 4h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H7l-4 3V6a2 2 0 0 1 2-2z";
    private const string IconClock = "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zm1 5h-2v6l5 3 .9-1.6-3.9-2.3V7z";
    private const string IconFolder = "M2 6a2 2 0 0 1 2-2h5l2 2h9a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6z";
    private const string IconBolt = "M21.64 3.64l-1.28-1.28a1.21 1.21 0 0 0-1.72 0L2.36 18.64a1.21 1.21 0 0 0 0 1.72l1.28 1.28a1.2 1.2 0 0 0 1.72 0L21.64 5.36a1.2 1.2 0 0 0 0-1.72z M14 7l3 3 M5 6v4 M19 14v4 M10 2v2 M7 8H3 M21 16h-4 M11 3H9";
    private const string IconSparkle = "M11.017 2.814a1 1 0 0 1 1.966 0l1.051 5.558a2 2 0 0 0 1.594 1.594l5.558 1.051a1 1 0 0 1 0 1.966l-5.558 1.051a2 2 0 0 0-1.594 1.594l-1.051 5.558a1 1 0 0 1-1.966 0l-1.051-5.558a2 2 0 0 0-1.594-1.594l-5.558-1.051a1 1 0 0 1 0-1.966l5.558-1.051a2 2 0 0 0 1.594-1.594z M20 2v4 M22 4h-4 M4 20a2 2 0 1 0 0-4 2 2 0 0 0 0 4z";
    private const string IconMemory = "M9.5 2A1.5 1.5 0 0 0 8 3.5V4H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2h-4v-.5A1.5 1.5 0 0 0 14.5 2h-5zM10 4V3.5a.5.5 0 0 1 .5-.5h3a.5.5 0 0 1 .5.5V4h-4zm-2 5a1 1 0 1 1 2 0 1 1 0 0 1-2 0zm5 0a1 1 0 1 1 2 0 1 1 0 0 1-2 0zm-5 4a1 1 0 1 1 2 0 1 1 0 0 1-2 0zm5 0a1 1 0 1 1 2 0 1 1 0 0 1-2 0z";
    private const string IconPlug = "M17 6.1h3V8h-3v2.1a5 5 0 0 1-4 4.9V18h2v2H9v-2h2v-3a5 5 0 0 1-4-4.9V8H4V6.1h3V4h2v2.1h4V4h2v2.1zM9 8v2.1a3 3 0 0 0 6 0V8H9z";
    private const string IconGear = "M9.671 4.136a2.34 2.34 0 0 1 4.659 0 2.34 2.34 0 0 0 3.319 1.915 2.34 2.34 0 0 1 2.33 4.033 2.34 2.34 0 0 0 0 3.831 2.34 2.34 0 0 1-2.33 4.033 2.34 2.34 0 0 0-3.319 1.915 2.34 2.34 0 0 1-4.659 0 2.34 2.34 0 0 0-3.32-1.915 2.34 2.34 0 0 1-2.33-4.033 2.34 2.34 0 0 0 0-3.831A2.34 2.34 0 0 1 6.35 6.051a2.34 2.34 0 0 0 3.319-1.915z M12 12a3 3 0 1 0 0-6 3 3 0 0 0 0 6z";

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private int _selectedIndex;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isDeepSearching;

    private IReadOnlyList<SearchResultGroup> _resultGroups = Array.Empty<SearchResultGroup>();
    public IReadOnlyList<SearchResultGroup> ResultGroups
    {
        get => _resultGroups;
        private set => SetProperty(ref _resultGroups, value);
    }

    private IReadOnlyList<SearchResultGroup> _availableGroups = Array.Empty<SearchResultGroup>();
    private IReadOnlyList<SearchCategoryFilterItem> _categoryFilters = Array.Empty<SearchCategoryFilterItem>();
    public IReadOnlyList<SearchCategoryFilterItem> CategoryFilters
    {
        get => _categoryFilters;
        private set
        {
            if (!SetProperty(ref _categoryFilters, value))
                return;

            OnPropertyChanged(nameof(HasCategoryFilters));
            OnPropertyChanged(nameof(SelectedCategoryFilterIndex));
        }
    }

    private GlobalSearchCategory? _selectedCategory;
    public GlobalSearchCategory? SelectedCategory
    {
        get => _selectedCategory;
        private set
        {
            if (!SetProperty(ref _selectedCategory, value))
                return;

            OnPropertyChanged(nameof(IsAllCategoriesSelected));
            OnPropertyChanged(nameof(HasSelectedCategory));
            OnPropertyChanged(nameof(SelectedCategorySummary));
            OnPropertyChanged(nameof(SelectedCategoryFilterIndex));
            OnPropertyChanged(nameof(ResultsHeading));
            OnPropertyChanged(nameof(EmptyStateTitle));
            OnPropertyChanged(nameof(EmptyStateHint));
            NotifySearchStatusChanged();
        }
    }

    /// <summary>Flat list of all results for keyboard navigation.</summary>
    private IReadOnlyList<SearchResultItem> _flatResults = Array.Empty<SearchResultItem>();
    public IReadOnlyList<SearchResultItem> FlatResults
    {
        get => _flatResults;
        internal set
        {
            if (!SetProperty(ref _flatResults, value))
                return;

            OnPropertyChanged(nameof(ResultCount));
            OnPropertyChanged(nameof(HasResults));
            NotifySearchStatusChanged();
        }
    }

    [ObservableProperty] private int _totalResultCount;

    public int ResultCount => FlatResults.Count;
    public bool HasResults => ResultCount > 0;
    public bool IsSearchPending => IsSearching || IsDeepSearching;
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);
    public bool HasCategoryFilters => CategoryFilters.Count > 1;
    public bool IsAllCategoriesSelected => SelectedCategory is null;
    public bool HasSelectedCategory => SelectedCategory is not null;
    public int SelectedCategoryFilterIndex => GetSelectedCategoryFilterIndex();
    public string SelectedCategorySummary => BuildSelectedCategorySummary();
    public string ResultsHeading => SelectedCategory is { } category
        ? GetCategoryLabel(category)
        : HasSearchQuery
            ? "Best matches"
            : "Recent";
    public string SearchStatusText => BuildSearchStatusText();
    public string EmptyStateTitle => SelectedCategory is { } category
        ? $"No {GetCategoryLabel(category).ToLowerInvariant()} found"
        : "No results found";
    public string EmptyStateHint => SelectedCategory is null
        ? "Try fewer words, a broader phrase, or another category."
        : "Try a broader phrase or switch back to All.";

    /// <summary>Raised when a result is selected (navigate to it).</summary>
    public event Action<SearchResultItem>? ResultSelected;

    /// <summary>Raises the ResultSelected event for the given item.</summary>
    public void RaiseResultSelected(SearchResultItem item) => ResultSelected?.Invoke(item);

    public SearchOverlayViewModel(GlobalSearchService searchService, Func<int> getCurrentNavIndex)
    {
        _searchService = searchService;
        _getCurrentNavIndex = getCurrentNavIndex;
    }

    public void Open()
    {
        SelectedCategory = null;
        SearchQuery = "";
        SelectedIndex = -1;
        _hasUserNavigatedSelection = false;
        IsOpen = true;
        QueueSearch(immediate: true);
    }

    [RelayCommand]
    public void Close()
    {
        CancelPendingSearch();
        IsOpen = false;
        SearchQuery = "";
        SelectedCategory = null;
        _availableGroups = Array.Empty<SearchResultGroup>();
        CategoryFilters = Array.Empty<SearchCategoryFilterItem>();
        ResultGroups = Array.Empty<SearchResultGroup>();
        FlatResults = Array.Empty<SearchResultItem>();
        TotalResultCount = 0;
        SelectedIndex = -1;
        _hasUserNavigatedSelection = false;
        IsSearching = false;
        IsDeepSearching = false;
        NotifySearchStatusChanged();
    }

    public void SelectCurrent()
    {
        if (SelectedIndex >= 0 && SelectedIndex < FlatResults.Count)
        {
            var result = FlatResults[SelectedIndex];
            Close();
            RaiseResultSelected(result);
        }
    }

    public void MoveSelection(int delta)
    {
        if (FlatResults.Count == 0) return;

        var newIndex = SelectedIndex + delta;
        if (newIndex < 0) newIndex = FlatResults.Count - 1;
        else if (newIndex >= FlatResults.Count) newIndex = 0;

        _hasUserNavigatedSelection = true;
        SelectedIndex = newIndex;
    }

    public void ClearSearch() => SearchQuery = "";

    public void SelectCategory(GlobalSearchCategory? category)
    {
        if (SelectedCategory == category)
            return;

        SelectedCategory = category;
        _hasUserNavigatedSelection = false;
        RefreshCategoryFilters();
        RebuildVisibleResults(preserveSelection: false);
    }

    public void CycleCategory(int delta)
    {
        if (CategoryFilters.Count <= 1)
            return;

        var currentIndex = CategoryFilters
            .Select(static (filter, index) => new { filter, index })
            .FirstOrDefault(entry => entry.filter.CategoryKey == SelectedCategory)?.index ?? 0;
        var nextIndex = (currentIndex + delta + CategoryFilters.Count) % CategoryFilters.Count;
        SelectCategory(CategoryFilters[nextIndex].CategoryKey);
    }

    partial void OnSearchQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchQuery));
        OnPropertyChanged(nameof(ResultsHeading));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateHint));
        NotifySearchStatusChanged();
        _hasUserNavigatedSelection = false;

        if (!IsOpen)
            return;

        QueueSearch();
    }

    partial void OnIsSearchingChanged(bool value) => NotifySearchStatusChanged();
    partial void OnIsDeepSearchingChanged(bool value) => NotifySearchStatusChanged();
    partial void OnTotalResultCountChanged(int value) => NotifySearchStatusChanged();

    private void QueueSearch(bool immediate = false)
    {
        CancelPendingSearch(disposeOnly: true);

        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        var requestId = Interlocked.Increment(ref _searchRequestId);
        var query = SearchQuery?.Trim() ?? "";
        _lastAppliedRequestId = -1;

        GlobalSearchService.PreparedSearch preparedSearch;
        try
        {
            preparedSearch = _searchService.PrepareSearch(query);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to prepare search for query '{query}': {ex}");
            IsSearching = false;
            IsDeepSearching = false;
            return;
        }

        IsSearching = true;
        IsDeepSearching = ShouldRunFullSearch(query);
        NotifySearchStatusChanged();

        _ = RunSearchPipelineAsync(query, preparedSearch, requestId, token, immediate);
    }

    private async Task RunSearchPipelineAsync(
        string query,
        GlobalSearchService.PreparedSearch preparedSearch,
        long requestId,
        CancellationToken cancellationToken,
        bool immediate)
    {
        var elapsed = Stopwatch.StartNew();

        try
        {
            if (string.IsNullOrEmpty(query))
            {
                await RunSearchPhaseAsync(
                    query,
                    preparedSearch,
                    requestId,
                    cancellationToken,
                    GlobalSearchExecutionMode.Full,
                    DispatcherPriority.Input);
                return;
            }

            if (!await RunSearchPhaseAsync(
                    query,
                    preparedSearch,
                    requestId,
                    cancellationToken,
                    GlobalSearchExecutionMode.Preview,
                    DispatcherPriority.Input))
            {
                return;
            }

            await DelayUntilAsync(elapsed, immediate ? 0 : FastSearchDelayMs, cancellationToken);
            if (!await RunSearchPhaseAsync(
                    query,
                    preparedSearch,
                    requestId,
                    cancellationToken,
                    GlobalSearchExecutionMode.Fast,
                    DispatcherPriority.Input))
            {
                return;
            }

            await DelayUntilAsync(elapsed, InteractiveSearchDelayMs, cancellationToken);
            if (!await RunSearchPhaseAsync(
                    query,
                    preparedSearch,
                    requestId,
                    cancellationToken,
                    GlobalSearchExecutionMode.Interactive,
                    DispatcherPriority.Background))
            {
                return;
            }

            await UpdateSearchActivityAsync(query, requestId, visibleSearchComplete: true);

            if (!ShouldRunFullSearch(query))
                return;

            await DelayUntilAsync(elapsed, FullSearchDelayMs, cancellationToken);
            await RunSearchPhaseAsync(
                query,
                preparedSearch,
                requestId,
                cancellationToken,
                GlobalSearchExecutionMode.Full,
                DispatcherPriority.Background);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer query superseded this one.
        }
        finally
        {
            await UpdateSearchActivityAsync(query, requestId, allSearchComplete: true);
        }
    }

    private async Task<bool> RunSearchPhaseAsync(
        string query,
        GlobalSearchService.PreparedSearch preparedSearch,
        long requestId,
        CancellationToken cancellationToken,
        GlobalSearchExecutionMode executionMode,
        DispatcherPriority applyPriority)
    {
        try
        {
            var matches = await _searchService
                .SearchAsync(preparedSearch, executionMode, cancellationToken)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(
                () => ApplyResults(query, requestId, matches),
                applyPriority);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Search phase '{executionMode}' failed for query '{query}': {ex}");
            return true;
        }
    }

    private static async Task DelayUntilAsync(
        Stopwatch elapsed,
        int targetMilliseconds,
        CancellationToken cancellationToken)
    {
        var remaining = targetMilliseconds - elapsed.ElapsedMilliseconds;
        if (remaining > 0)
            await Task.Delay(TimeSpan.FromMilliseconds(remaining), cancellationToken);
    }

    private async Task UpdateSearchActivityAsync(
        string query,
        long requestId,
        bool visibleSearchComplete = false,
        bool allSearchComplete = false)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!IsCurrentRequest(query, requestId))
                return;

            if (visibleSearchComplete || allSearchComplete)
                IsSearching = false;

            if (allSearchComplete)
                IsDeepSearching = false;

            NotifySearchStatusChanged();
        }, DispatcherPriority.Background);
    }

    private void ApplyResults(string query, long requestId, IReadOnlyList<GlobalSearchMatch> matches)
    {
        if (!IsCurrentRequest(query, requestId))
            return;

        var currentNavIndex = _getCurrentNavIndex();
        var candidates = matches
            .GroupBy(static match => match.Category)
            .Select(group =>
            {
                var items = group
                    .Take(MaxResultsPerGroup)
                    .Select(ToResultItem)
                    .ToList();

                return new GroupCandidate(
                    new SearchResultGroup
                    {
                        CategoryKey = group.Key,
                        Category = GetCategoryLabel(group.Key),
                        CategoryIcon = GetCategoryIcon(group.Key),
                        IsCurrentTab = items.Count > 0 && items[0].NavIndex == currentNavIndex,
                        TotalCount = group.Count(),
                        Items = items
                    },
                    items.Count > 0 ? items[0].Score : 0);
            })
            .ToList();

        _availableGroups = OrderGroupCandidates(candidates)
            .Select(static candidate => candidate.Group)
            .ToArray();
        TotalResultCount = matches.Count;
        OnPropertyChanged(nameof(SelectedCategorySummary));
        RefreshCategoryFilters();

        var groups = BuildVisibleGroups(_availableGroups);
        // A changed query starts at its top result; later phases for the same query preserve identity.
        var isRefinement = _lastAppliedRequestId == requestId;
        var preserveUserSelection = isRefinement && _hasUserNavigatedSelection;
        var previousSelectedItem = preserveUserSelection && SelectedIndex >= 0 && SelectedIndex < FlatResults.Count
            ? FlatResults[SelectedIndex]
            : null;
        var flatResults = FlattenGroups(groups);
        var selectedIndex = ResolveSelectedIndex(
            previousSelectedItem,
            flatResults,
            preserveUserSelection ? SelectedIndex : -1);

        if (!HasEquivalentResults(groups, flatResults))
        {
            FlatResults = flatResults;
            ResultGroups = groups;
        }

        SelectedIndex = selectedIndex;
        _lastAppliedRequestId = requestId;
        NotifySearchStatusChanged();
    }

    private static IEnumerable<GroupCandidate> OrderGroupCandidates(
        IReadOnlyList<GroupCandidate> candidates)
    {
        return candidates
            .OrderBy(static candidate => GetCategoryPriority(candidate.Group.CategoryKey))
            .ThenByDescending(static candidate => candidate.TopScore)
            .ThenBy(static candidate => candidate.Group.Category);
    }

    internal static int GetCategoryPriority(GlobalSearchCategory category) => category switch
    {
        GlobalSearchCategory.Chats => 0,
        GlobalSearchCategory.BackgroundJobs => 1,
        GlobalSearchCategory.Projects => 2,
        GlobalSearchCategory.Skills => 3,
        GlobalSearchCategory.Lumis => 4,
        GlobalSearchCategory.Memories => 5,
        GlobalSearchCategory.McpServers => 6,
        GlobalSearchCategory.Settings => 7,
        _ => int.MaxValue
    };

    private IReadOnlyList<SearchResultGroup> BuildVisibleGroups(
        IEnumerable<SearchResultGroup> availableGroups)
        => BuildVisibleGroupsForCategory(availableGroups, SelectedCategory);

    internal static IReadOnlyList<SearchResultGroup> BuildVisibleGroupsForCategory(
        IEnumerable<SearchResultGroup> availableGroups,
        GlobalSearchCategory? selectedCategory)
    {
        var groups = new List<SearchResultGroup>();
        var remaining = selectedCategory is null
            ? AllVisibleResultLimit
            : MaxVisibleResults;

        foreach (var availableGroup in availableGroups)
        {
            if (remaining <= 0)
                break;

            if (selectedCategory is { } category &&
                availableGroup.CategoryKey != category)
            {
                continue;
            }

            var categoryLimit = selectedCategory is not null
                ? MaxResultsPerGroup
                : availableGroup.CategoryKey == GlobalSearchCategory.Chats
                    ? AllChatsPreviewLimit
                    : AllCategoryPreviewLimit;
            var items = availableGroup.Items
                .Take(Math.Min(remaining, categoryLimit))
                .ToList();
            if (items.Count == 0)
                continue;

            groups.Add(new SearchResultGroup
            {
                CategoryKey = availableGroup.CategoryKey,
                Category = availableGroup.Category,
                CategoryIcon = availableGroup.CategoryIcon,
                IsCurrentTab = availableGroup.IsCurrentTab,
                ShowHeader = selectedCategory is null,
                TotalCount = availableGroup.TotalCount,
                Items = items
            });
            remaining -= items.Count;
        }

        return groups;
    }

    private void RebuildVisibleResults(bool preserveSelection)
    {
        var previousSelectedItem = preserveSelection && SelectedIndex >= 0 && SelectedIndex < FlatResults.Count
            ? FlatResults[SelectedIndex]
            : null;
        var groups = BuildVisibleGroups(_availableGroups);
        var flatResults = FlattenGroups(groups);
        var selectedIndex = ResolveSelectedIndex(
            previousSelectedItem,
            flatResults,
            preserveSelection ? SelectedIndex : -1);

        if (!HasEquivalentResults(groups, flatResults))
        {
            FlatResults = flatResults;
            ResultGroups = groups;
        }

        SelectedIndex = selectedIndex;
        NotifySearchStatusChanged();
    }

    private void RefreshCategoryFilters()
    {
        var categories = _availableGroups
            .Select(group => new SearchCategoryFilterItem
            {
                CategoryKey = group.CategoryKey,
                Label = group.Category,
                Icon = group.CategoryIcon,
                Count = group.TotalCount,
                IsSelected = group.CategoryKey == SelectedCategory
            })
            .ToList();

        if (SelectedCategory is { } selectedCategory &&
            categories.All(filter => filter.CategoryKey != selectedCategory))
        {
            categories.Add(new SearchCategoryFilterItem
            {
                CategoryKey = selectedCategory,
                Label = GetCategoryLabel(selectedCategory),
                Icon = GetCategoryIcon(selectedCategory),
                Count = 0,
                IsSelected = true
            });
            categories.Sort(static (left, right) =>
                GetCategoryPriority(left.CategoryKey!.Value)
                    .CompareTo(GetCategoryPriority(right.CategoryKey!.Value)));
        }

        categories.Insert(0, new SearchCategoryFilterItem
        {
            Label = "All",
            Icon = IconSearch,
            Count = TotalResultCount,
            IsSelected = SelectedCategory is null
        });

        CategoryFilters = categories;
    }

    private bool IsCurrentRequest(string query, long requestId)
    {
        return IsOpen
               && requestId == _searchRequestId
               && string.Equals(query, SearchQuery?.Trim() ?? "", StringComparison.Ordinal);
    }

    private void CancelPendingSearch(bool disposeOnly = false)
    {
        if (_searchCts is not null)
        {
            _searchCts.Cancel();
            _searchCts.Dispose();
            _searchCts = null;
        }

        _lastAppliedRequestId = -1;

        if (!disposeOnly)
            Interlocked.Increment(ref _searchRequestId);
    }

    internal static bool ShouldRunFullSearch(string? query)
        => (query?.Trim().Length ?? 0) >= MinQueryLengthForFullSearch;

    internal static int ResolveSelectedIndex(
        SearchResultItem? previousSelectedItem,
        IReadOnlyList<SearchResultItem> flatResults,
        int currentSelectedIndex)
    {
        if (flatResults.Count == 0)
            return -1;

        if (previousSelectedItem is not null)
        {
            for (var index = 0; index < flatResults.Count; index++)
            {
                if (AreEquivalent(previousSelectedItem, flatResults[index]))
                    return index;
            }
        }

        return currentSelectedIndex >= 0
            ? Math.Min(currentSelectedIndex, flatResults.Count - 1)
            : 0;
    }

    private bool HasEquivalentResults(
        IReadOnlyList<SearchResultGroup> groups,
        IReadOnlyList<SearchResultItem> flatResults)
    {
        if (FlatResults.Count != flatResults.Count || ResultGroups.Count != groups.Count)
            return false;

        for (var index = 0; index < flatResults.Count; index++)
        {
            var current = FlatResults[index];
            var next = flatResults[index];
            if (!AreEquivalent(current, next))
                return false;
        }

        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var currentGroup = ResultGroups[groupIndex];
            var nextGroup = groups[groupIndex];

            if (currentGroup.CategoryKey != nextGroup.CategoryKey
                || !string.Equals(currentGroup.Category, nextGroup.Category, StringComparison.Ordinal)
                || !string.Equals(currentGroup.CategoryIcon, nextGroup.CategoryIcon, StringComparison.Ordinal)
                || currentGroup.IsCurrentTab != nextGroup.IsCurrentTab
                || currentGroup.ShowHeader != nextGroup.ShowHeader
                || currentGroup.TotalCount != nextGroup.TotalCount
                || currentGroup.Items.Count != nextGroup.Items.Count)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool AreEquivalent(SearchResultItem current, SearchResultItem next)
    {
        return current.CategoryKey == next.CategoryKey
               && string.Equals(current.Category, next.Category, StringComparison.Ordinal)
               && string.Equals(current.CategoryIcon, next.CategoryIcon, StringComparison.Ordinal)
               && string.Equals(current.Title, next.Title, StringComparison.Ordinal)
               && string.Equals(current.Subtitle, next.Subtitle, StringComparison.Ordinal)
               && current.NavIndex == next.NavIndex
               && Equals(current.Item, next.Item)
               && current.IsContentMatch == next.IsContentMatch
               && current.SettingsPageIndex == next.SettingsPageIndex;
    }

    private string BuildSearchStatusText()
    {
        if (!IsOpen)
            return "";

        var hasQuery = !string.IsNullOrWhiteSpace(SearchQuery);
        if (!hasQuery)
            return IsSearching ? "Loading recent items..." : "Recent items";

        if (IsSearching)
            return ResultCount == 0 ? "Searching..." : $"{FormatResultCount()} · refining...";

        if (IsDeepSearching)
            return ResultCount == 0
                ? "Searching all history..."
                : $"{FormatResultCount()} · checking older chats...";

        return ResultCount == 0 ? "No results" : FormatResultCount();
    }

    private string FormatResultCount()
    {
        var activeTotal = SelectedCategory is { } selectedCategory
            ? _availableGroups.FirstOrDefault(group => group.CategoryKey == selectedCategory)?.TotalCount ?? 0
            : TotalResultCount;

        if (activeTotal > ResultCount)
            return $"{ResultCount} of {activeTotal} results";

        return activeTotal == 1 ? "1 result" : $"{activeTotal} results";
    }

    private string BuildSelectedCategorySummary()
    {
        if (SelectedCategory is not { } selectedCategory)
            return "";

        var count = _availableGroups
            .FirstOrDefault(group => group.CategoryKey == selectedCategory)?.TotalCount ?? 0;
        var noun = count == 1 ? "result" : "results";
        return $"{GetCategoryLabel(selectedCategory)} · {count} {noun}";
    }

    private int GetSelectedCategoryFilterIndex()
    {
        for (var index = 0; index < CategoryFilters.Count; index++)
        {
            if (CategoryFilters[index].CategoryKey == SelectedCategory)
                return index;
        }

        return CategoryFilters.Count > 0 ? 0 : -1;
    }

    private void NotifySearchStatusChanged()
    {
        OnPropertyChanged(nameof(IsSearchPending));
        OnPropertyChanged(nameof(SearchStatusText));
    }

    private static SearchResultItem ToResultItem(GlobalSearchMatch match)
    {
        return new SearchResultItem
        {
            CategoryKey = match.Category,
            Category = GetCategoryLabel(match.Category),
            CategoryIcon = GetCategoryIcon(match.Category),
            Title = match.Title,
            Subtitle = match.Subtitle,
            NavIndex = match.NavIndex,
            Item = match.Item,
            Score = match.Score,
            IsContentMatch = match.IsContentMatch,
            SettingsPageIndex = match.SettingsPageIndex
        };
    }

    private static List<SearchResultItem> FlattenGroups(
        IEnumerable<SearchResultGroup> groups)
    {
        return groups
            .SelectMany(static group => group.Items)
            .OrderBy(static item => GetCategoryPriority(item.CategoryKey))
            .ThenByDescending(static item => item.Score)
            .ToList();
    }

    private static string GetCategoryLabel(GlobalSearchCategory category) => category switch
    {
        GlobalSearchCategory.Chats => "Chats",
        GlobalSearchCategory.BackgroundJobs => "Jobs",
        GlobalSearchCategory.Projects => "Projects",
        GlobalSearchCategory.Skills => "Skills",
        GlobalSearchCategory.Lumis => "Lumis",
        GlobalSearchCategory.Memories => "Memories",
        GlobalSearchCategory.McpServers => "MCP Servers",
        GlobalSearchCategory.Settings => "Settings",
        _ => "Results"
    };

    private static string GetCategoryIcon(GlobalSearchCategory category) => category switch
    {
        GlobalSearchCategory.Chats => IconChat,
        GlobalSearchCategory.BackgroundJobs => IconClock,
        GlobalSearchCategory.Projects => IconFolder,
        GlobalSearchCategory.Skills => IconBolt,
        GlobalSearchCategory.Lumis => IconSparkle,
        GlobalSearchCategory.Memories => IconMemory,
        GlobalSearchCategory.McpServers => IconPlug,
        GlobalSearchCategory.Settings => IconGear,
        _ => IconChat
    };

    private sealed record GroupCandidate(SearchResultGroup Group, double TopScore);
}
