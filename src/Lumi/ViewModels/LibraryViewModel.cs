using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Services;
using StrataSearch;

namespace Lumi.ViewModels;

/// <summary>Sort orders offered by the Library toolbar.</summary>
public enum LibrarySort
{
    Newest,
    Oldest,
    Name,
    Largest
}

/// <summary>Relative time windows offered by the Library "when" filter.</summary>
public enum LibraryTimeRange
{
    All,
    Today,
    Week,
    Month
}

/// <summary>
/// A selectable filter chip (collection, origin, time window, or project). One shared shape keeps
/// the Library sidebar templates simple and lets every facet render and animate identically.
/// </summary>
public partial class LibraryFilterOption : ObservableObject
{
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isSelected;

    public required string Id { get; init; }
    public required string Label { get; init; }

    /// <summary>Path data for the collection-rail glyph. Null for facets rendered as plain chips.</summary>
    public string? IconPath { get; init; }

    /// <summary>Kind this facet selects, when it maps to one. Drives the rail's per-collection hue.</summary>
    public LibraryArtifactKind? Kind { get; init; }

    /// <summary>Collection hue, falling back to the app accent for facets that span every kind.</summary>
    public IBrush Accent => Kind is { } kind
        ? LibraryPalette.Accent(kind)
        : LibraryPalette.NeutralAccent;

    /// <summary>Invoked when the chip is activated; the owning ViewModel applies exclusive selection.</summary>
    public Action<LibraryFilterOption>? SelectAction { get; init; }

    /// <summary>Empty facets are dimmed rather than removed, so the filter rail never jumps around.</summary>
    public bool HasItems => Count > 0;

    public bool HasIcon => IconPath is not null;

    /// <summary>Parsed on demand so pure ViewModel tests never need a render backend.</summary>
    public Geometry? Icon => IconPath is null ? null : LibraryIcons.Parse(IconPath);

    [RelayCommand]
    private void Select() => SelectAction?.Invoke(this);

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(HasItems));
}

/// <summary>
/// One narrowing constraint currently applied to the Library, rendered as a removable chip above
/// the results. The rail selects filters silently; without a readout next to the list there is no
/// way to tell an empty result from a mis-set filter, and no way to undo one constraint at a time.
/// </summary>
public sealed class LibraryActiveFilter
{
    private readonly Action _remove;

    public LibraryActiveFilter(string category, string label, Action remove, string? iconPath = null, IBrush? accent = null)
    {
        Category = category;
        Label = label;
        IconPath = iconPath;
        Accent = accent ?? LibraryPalette.NeutralAccent;
        _remove = remove;
        RemoveCommand = new RelayCommand(remove);
    }

    /// <summary>Which facet this constraint came from, spelled out so no chip is ambiguous.</summary>
    public string Category { get; }

    public string Label { get; }

    public string? IconPath { get; }

    public bool HasIcon => IconPath is not null;

    /// <summary>Parsed on demand so pure ViewModel tests never need a render backend.</summary>
    public Geometry? Icon => IconPath is null ? null : LibraryIcons.Parse(IconPath);

    public IBrush Accent { get; }

    public IRelayCommand RemoveCommand { get; }

    /// <summary>Test seam; the command wrapper is the same call.</summary>
    public void Remove() => _remove();
}

/// <summary>Display wrapper around a scanned <see cref="LibraryArtifact"/>.</summary>
public partial class LibraryItemViewModel : ObservableObject
{
    private readonly Func<Guid, Task>? _openChatAsync;
    private readonly Action<LibraryItemViewModel>? _select;

    private Bitmap? _iconImage;
    private bool _previewRequested;
    private string? _groupHeader;

    [ObservableProperty] private bool _isSelected;

    public LibraryItemViewModel(
        LibraryArtifact artifact,
        Func<Guid, Task>? openChatAsync,
        Action<LibraryItemViewModel>? select = null)
    {
        Artifact = artifact;
        _openChatAsync = openChatAsync;
        _select = select;
    }

    public LibraryArtifact Artifact { get; private set; }

    /// <summary>
    /// Date separator rendered above this row, or null when the row continues the previous bucket.
    /// Carried on the item instead of a group wrapper so the list stays flat and virtualizable.
    /// </summary>
    public string? GroupHeader
    {
        get => _groupHeader;
        internal set
        {
            if (_groupHeader == value)
                return;

            _groupHeader = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasGroupHeader));
        }
    }

    public bool HasGroupHeader => _groupHeader is not null;

    /// <summary>
    /// Resolved on first read, which in a virtualized list means the row actually reached the
    /// screen. Image bytes are decoded on a worker thread so only the finished bitmap is handed
    /// back to the UI thread.
    /// </summary>
    public Bitmap? IconImage
    {
        get
        {
            RequestPreview();
            return _iconImage;
        }
        private set
        {
            if (ReferenceEquals(_iconImage, value))
                return;

            _iconImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasIconImage));
            OnPropertyChanged(nameof(HasGlyph));
            OnPropertyChanged(nameof(IsCoverPreview));
            OnPropertyChanged(nameof(IsBadgePreview));
        }
    }

    private void RequestPreview()
    {
        if (_previewRequested)
            return;

        _previewRequested = true;

        if (Artifact.IsLink || !Artifact.Exists)
            return;

        // Worktrees are directories: there is nothing to preview and the branch glyph reads better
        // than a generic shell folder icon.
        if (Artifact.Kind == LibraryArtifactKind.Worktree)
            return;

        var path = Artifact.Location;

        // Routed by the extension the scan already resolved off-thread. Probing the file here instead
        // would put a synchronous stat on the UI thread for every row that scrolls into view, which
        // is exactly the kind of stall a virtualized list is supposed to avoid.
        if (HasDecodablePreview)
        {
            // Decoding dominates the cost, so it never runs on the UI thread.
            _ = Task.Run(() => FileIconHelper.GetFilePreview(path))
                .ContinueWith(
                    task => Dispatcher.UIThread.Post(() => IconImage = task.Result),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion,
                    TaskScheduler.Default);
            return;
        }

        // Shell type icons are resolved from the extension alone (SHGFI_USEFILEATTRIBUTES) and
        // cached, so they never touch the disk. SHGetFileInfo is only known-good on the UI thread,
        // so they stay there at idle priority.
        Dispatcher.UIThread.Post(
            () => IconImage = FileIconHelper.GetFileIcon(path),
            DispatcherPriority.Background);
    }

    /// <summary>
    /// Re-points the row at a newer scan of the same artifact (same <see cref="LibraryArtifact.Key"/>),
    /// keeping the resolved preview, selection and identity so a progressive scan never flickers.
    ///
    /// A scan republishes freshly merged artifacts many times, so nearly every rebind carries
    /// identical data. Raising the notification storm regardless cost tens of thousands of
    /// PropertyChanged allocations per republish on the UI thread, so unchanged rows stay silent.
    /// </summary>
    internal void Rebind(LibraryArtifact artifact)
    {
        if (ReferenceEquals(Artifact, artifact))
            return;

        var changed = !Artifact.HasSameDisplayData(artifact);
        Artifact = artifact;

        if (!changed)
            return;

        foreach (var property in RebindableProperties)
            OnPropertyChanged(property);
    }

    private static readonly string[] RebindableProperties =
    [
        nameof(Name), nameof(Location), nameof(ChatTitle), nameof(IsLink), nameof(IsMissing),
        nameof(IsCoverPreview), nameof(IsBadgePreview), nameof(Glyph), nameof(KindLabel),
        nameof(Accent), nameof(Tint),
        nameof(OriginLabel), nameof(TypeBadge), nameof(SizeLabel), nameof(SizeFieldLabel),
        nameof(CopyActionLabel), nameof(OpenActionLabel), nameof(TimeLabel), nameof(FullTimeLabel),
        nameof(Description), nameof(HasDescription), nameof(ProjectName), nameof(HasProject),
        nameof(IsSharedAcrossChats), nameof(SharedLabel), nameof(SearchText), nameof(MetaLine)
    ];

    public string Name => Artifact.Name;
    public string Location => Artifact.Location;
    public string ChatTitle => Artifact.ChatTitle;
    public bool IsLink => Artifact.IsLink;
    public bool IsMissing => !Artifact.IsLink && !Artifact.Exists;
    public bool HasIconImage => IconImage is not null;
    public bool HasGlyph => IconImage is null;

    /// <summary>
    /// True when the decoder can actually open this file. Deliberately narrower than
    /// <see cref="LibraryArtifactKind.Image"/>: .svg/.heic/.avif classify as images but decode to
    /// nothing, so they must neither take the decode path nor be stretched to fill a cover.
    /// </summary>
    private bool HasDecodablePreview => FileIconHelper.IsImageExtension(Artifact.Extension);

    /// <summary>Image artifacts fill the whole thumbnail; other types show a smaller inset icon.</summary>
    public bool IsCoverPreview => HasIconImage && HasDecodablePreview;
    public bool IsBadgePreview => HasIconImage && !HasDecodablePreview;

    /// <summary>Vector fallback shown until (or instead of) a real preview.</summary>
    public Geometry Glyph => LibraryIcons.ForKind(Artifact.Kind);

    /// <summary>Hue for this artifact's kind - what turns the list from a grey dump into a scannable surface.</summary>
    public IBrush Accent => LibraryPalette.Accent(Artifact.Kind);
    public IBrush Tint => LibraryPalette.Tint(Artifact.Kind);

    public string KindLabel => LibraryViewModel.DescribeKind(Artifact.Kind);
    public string OriginLabel => LibraryViewModel.DescribeOrigin(Artifact.Origin);

    /// <summary>Extension badge for files, host name for links.</summary>
    public string TypeBadge => Artifact.IsLink
        ? Artifact.Extension
        : Artifact.Extension.TrimStart('.').ToUpperInvariant();

    public string SizeLabel => Artifact.IsLink
        ? Artifact.Extension
        : Artifact.Kind == LibraryArtifactKind.Worktree
            ? Artifact.Exists ? Loc.Library_Worktree_Present : Loc.Library_Missing
            : Artifact.Exists ? ToolDisplayHelper.FormatFileSize(Artifact.SizeBytes) : Loc.Library_Missing;

    /// <summary>
    /// "Size" for files, "Site" for links, "Status" for worktrees - a worktree is a directory, so
    /// what matters is whether it is still taking up disk, not a byte count nobody can afford to compute.
    /// </summary>
    public string SizeFieldLabel => Artifact.IsLink
        ? Loc.Library_Field_Site
        : Artifact.Kind == LibraryArtifactKind.Worktree
            ? Loc.Library_Field_Status
            : Loc.Library_Field_Size;

    public string CopyActionLabel => Artifact.IsLink ? Loc.Library_Action_CopyLink : Loc.Library_Action_CopyPath;

    public string OpenActionLabel => Artifact.IsLink
        ? Loc.Library_Action_OpenLink
        : Artifact.Kind == LibraryArtifactKind.Worktree
            ? Loc.Library_Action_OpenFolder
            : Loc.Library_Action_Open;

    public string TimeLabel => LibraryViewModel.FormatRelativeTime(Artifact.LastSeen);

    public string FullTimeLabel => Artifact.LastSeen.ToString("f", CultureInfo.CurrentCulture);

    public string? Description => Artifact.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Artifact.Description);

    public string? ProjectName => Artifact.ProjectName;
    public bool HasProject => !string.IsNullOrWhiteSpace(Artifact.ProjectName);

    public bool IsSharedAcrossChats => Artifact.ChatCount > 1;
    public string SharedLabel => string.Format(CultureInfo.CurrentCulture, Loc.Library_UsedInChats, Artifact.ChatCount);

    public string SearchText => $"{Artifact.Name} {Artifact.ChatTitle} {Artifact.ProjectName} {Artifact.Location}";

    /// <summary>
    /// The row's single secondary line. Everything the old card stacked into separate chips
    /// collapses into one dot-separated string so a row reads at a glance.
    /// </summary>
    public string MetaLine
    {
        get
        {
            var parts = new List<string>(3);

            if (Artifact.IsLink)
                parts.Add(Artifact.Extension);
            else if (Artifact.Kind == LibraryArtifactKind.Worktree)
                parts.Add(Artifact.Exists ? Loc.Library_Worktree_Present : Loc.Library_Missing);
            else if (Artifact.Exists)
                parts.Add(ToolDisplayHelper.FormatFileSize(Artifact.SizeBytes));
            else
                parts.Add(Loc.Library_Missing);

            parts.Add(OriginLabel);

            if (!string.IsNullOrWhiteSpace(Artifact.ChatTitle))
                parts.Add(Artifact.ChatTitle);

            return string.Join(" · ", parts);
        }
    }

    [RelayCommand]
    private void Select() => _select?.Invoke(this);

    [RelayCommand]
    private void Open()
    {
        try
        {
            // Worktrees are directories, so existence is probed as one - File.Exists would reject them.
            if (!Artifact.IsLink && !File.Exists(Artifact.Location) && !Directory.Exists(Artifact.Location))
                return;

            Process.Start(new ProcessStartInfo(Artifact.Location) { UseShellExecute = true });
        }
        catch { /* opening is best-effort */ }
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        if (Artifact.IsLink)
            return;

        try
        {
            var path = Artifact.Location;
            try { path = Path.GetFullPath(path); } catch { /* keep original */ }

            var onDisk = File.Exists(path) || Directory.Exists(path);

            if (OperatingSystem.IsWindows() && onDisk)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }

            if (OperatingSystem.IsMacOS() && onDisk)
            {
                var reveal = new ProcessStartInfo("open") { UseShellExecute = false };
                reveal.ArgumentList.Add("-R");
                reveal.ArgumentList.Add(path);
                Process.Start(reveal);
                return;
            }

            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch { /* revealing is best-effort */ }
    }

    [RelayCommand]
    private Task CopyLocation() => ClipboardHelper.CopyTextAsync(Artifact.Location);

    [RelayCommand]
    private async Task OpenSourceChat()
    {
        if (_openChatAsync is null)
            return;

        await _openChatAsync(Artifact.ChatId);
    }
}

/// <summary>
/// Backing ViewModel for the Library page: scans every chat for artifacts, exposes faceted filters
/// (search, collection, origin, time window, project), sorting, and time-bucketed grouping.
/// </summary>
public partial class LibraryViewModel : ObservableObject
{
    private const string AllFilterId = "all";

    /// <summary>Tiles in the gallery band. Small enough to render eagerly without virtualization.</summary>
    private const int HighlightCount = 7;
    private const string NoProjectFilterId = "none";

    private readonly DataStore _dataStore;
    private readonly LibraryService _libraryService;
    private readonly Func<Guid, Task>? _openChatAsync;
    private readonly Action? _closeLibrary;

    private IReadOnlyList<LibraryArtifact> _artifacts = [];
    private readonly Dictionary<string, LibraryItemViewModel> _itemCache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _scanCts;
    private bool _isApplyingFilters;
    private bool _isDirty = true;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasScanned;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private LibraryItemViewModel? _selectedItem;
    [ObservableProperty] private int _selectedSortIndex;
    [ObservableProperty] private string _summaryLine = "";
    [ObservableProperty] private string _resultCountLabel = "";
    [ObservableProperty] private int _totalFiles;
    [ObservableProperty] private int _totalLinks;

    /// <summary>Git worktrees checked out by chats. Counted apart from files - they are directories.</summary>
    [ObservableProperty] private int _totalWorktrees;
    [ObservableProperty] private string _totalSizeLabel = "";
    [ObservableProperty] private int _sourceChatCount;
    [ObservableProperty] private int _scannedChats;
    [ObservableProperty] private int _totalChatsToScan;

    /// <summary>Flat, newest-first result list. Kept flat so the view can virtualize it.</summary>
    public BulkObservableCollection<LibraryItemViewModel> Items { get; } = [];

    /// <summary>Gallery tiles shown above the list in the unfiltered view.</summary>
    public BulkObservableCollection<LibraryItemViewModel> Highlights { get; } = [];

    public bool HasHighlights => Highlights.Count > 0;
    public ObservableCollection<LibraryFilterOption> Kinds { get; } = [];
    public ObservableCollection<LibraryFilterOption> Origins { get; } = [];
    public ObservableCollection<LibraryFilterOption> TimeRanges { get; } = [];
    public ObservableCollection<LibraryFilterOption> Projects { get; } = [];

    /// <summary>Every constraint narrowing the current view, one removable chip each.</summary>
    public ObservableCollection<LibraryActiveFilter> ActiveFilters { get; } = [];

    /// <summary>"Clear all" only earns its place once undoing chips one by one is tedious.</summary>
    public bool HasMultipleActiveFilters => ActiveFilters.Count > 1;

    /// <summary>Placeholder rows shown while the first scan runs.</summary>
    public IReadOnlyList<int> SkeletonSlots { get; } = Enumerable.Range(0, 9).ToArray();

    public string[] SortOptions { get; } =
    [
        Loc.Library_Sort_Newest,
        Loc.Library_Sort_Oldest,
        Loc.Library_Sort_Name,
        Loc.Library_Sort_Largest
    ];

    public bool HasAnyArtifacts => _artifacts.Count > 0;
    public bool HasResults => Items.Count > 0;
    public bool HasSelection => SelectedItem is not null;
    /// <summary>The project rail is only meaningful once artifacts span more than the implicit "all" bucket.</summary>
    public bool HasProjectFilters => Projects.Count > 1;
    public bool IsEmptyLibrary => HasScanned && !IsLoading && !HasAnyArtifacts;
    /// <summary>
    /// Only ever shown when a filter is actually responsible for the empty list. Without the
    /// <see cref="HasActiveFilters"/> gate the "clear filters" card also surfaced during the brief
    /// window where a rescan republishes and rebuilds the list, telling the user to clear filters
    /// they never set.
    /// </summary>
    public bool IsEmptyResult => HasAnyArtifacts && !HasResults && HasActiveFilters;
    /// <summary>
    /// Blocking state: nothing on screen yet. Keyed off the visible list rather than the raw
    /// artifact count so any transient empty list mid-scan reads as loading instead of blank.
    /// </summary>
    public bool IsInitialScan => IsLoading && !HasResults;

    /// <summary>Live readout of scan progress, so a long scan never looks frozen.</summary>
    public string ScanProgressLabel => TotalChatsToScan > 0
        ? string.Format(CultureInfo.CurrentCulture, Loc.Library_ScanProgress, ScannedChats, TotalChatsToScan)
        : Loc.Library_Scanning;

    /// <summary>0-1 completion of the current scan; 0 until the chat count is known.</summary>
    public double ScanFraction => TotalChatsToScan > 0
        ? Math.Clamp((double)ScannedChats / TotalChatsToScan, 0, 1)
        : 0;

    public double ScanPercent => ScanFraction * 100;
    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchQuery)
        || SelectedKindId != AllFilterId
        || SelectedOriginId != AllFilterId
        || SelectedTimeRangeId != AllFilterId
        || SelectedProjectId != AllFilterId;

    /// <summary>
    /// Drives the "clear" affordance inside the search field. A magnifier field's ✕ universally means
    /// "clear what I typed"; wiring it to every filter made rail selections vanish unexpectedly.
    /// </summary>
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);

    private string SelectedKindId => Kinds.FirstOrDefault(option => option.IsSelected)?.Id ?? AllFilterId;
    private string SelectedOriginId => Origins.FirstOrDefault(option => option.IsSelected)?.Id ?? AllFilterId;
    private string SelectedTimeRangeId => TimeRanges.FirstOrDefault(option => option.IsSelected)?.Id ?? AllFilterId;
    private string SelectedProjectId => Projects.FirstOrDefault(option => option.IsSelected)?.Id ?? AllFilterId;

    public LibraryViewModel(
        DataStore dataStore,
        Func<Guid, Task>? openChatAsync = null,
        Action? closeLibrary = null)
    {
        _dataStore = dataStore;
        _libraryService = new LibraryService(dataStore);
        _openChatAsync = openChatAsync;
        _closeLibrary = closeLibrary;

        BuildStaticFilters();
    }

    [RelayCommand]
    private void BackToChats() => _closeLibrary?.Invoke();

    /// <summary>Marks the cached scan stale so the next page visit refreshes it.</summary>
    public void MarkDirty() => _isDirty = true;

    /// <summary>Scans on first visit, and again whenever chat content changed since the last scan.</summary>
    public Task EnsureLoadedAsync()
    {
        if (IsLoading || (!_isDirty && HasScanned))
            return Task.CompletedTask;

        return RefreshAsync();
    }

    [RelayCommand]
    private async Task Refresh() => await RefreshAsync();

    private async Task RefreshAsync()
    {
        var cts = new CancellationTokenSource();
        // Captured once: Interlocked.Exchange hands each source to exactly one successor, which cancels
        // and disposes it. A superseded scan is still in flight and keeps checking for cancellation, and
        // CancellationTokenSource.Token throws ObjectDisposedException once disposed - so the stale scan
        // must read this captured token (which stays valid) rather than touch cts.Token again.
        var token = cts.Token;
        var previous = Interlocked.Exchange(ref _scanCts, cts);
        previous?.Cancel();
        previous?.Dispose();

        IsLoading = true;
        ScannedChats = 0;
        // Captured on the UI thread (List<Chat> is not thread-safe) so the background scan can never
        // fault on a chat added mid-scan by a background job. Also seeds the readout exactly from the
        // first frame; progress reports only advance the counter as chats are read.
        var chats = _dataStore.Data.Chats.ToArray();
        var projects = _dataStore.Data.Projects.ToArray();
        TotalChatsToScan = chats.Length;

        // Progress<T> hands its callbacks to the SynchronizationContext captured when it was built. Under
        // the app that is the UI dispatcher, which serialises them against this method's continuation; in
        // any other context (unit tests, a headless host) there is none, so reports are delivered straight
        // on the thread pool and can run *concurrently with* - or *after* - the completing scan. Both
        // publishers mutate the same collections, so a stray report could interleave with, or overwrite,
        // the final results. The gate serialises them and makes the final publish the last word.
        var publishGate = new object();
        var scanFinished = false;
        try
        {
            var progress = new Progress<LibraryScanProgress>(update =>
            {
                if (token.IsCancellationRequested || !ReferenceEquals(Volatile.Read(ref _scanCts), cts))
                    return;

                lock (publishGate)
                {
                    if (scanFinished)
                        return;

                    ScannedChats = update.ChatsScanned;
                    TotalChatsToScan = update.ChatsTotal;
                    Publish(update.Artifacts);
                    HasScanned = true;
                    RefreshEmptyStates();
                }
            });

            var artifacts = await Task.Run(() => _libraryService.ScanAsync(chats, projects, progress, token), token);
            if (token.IsCancellationRequested)
                return;

            lock (publishGate)
            {
                scanFinished = true;

                _isDirty = false;
                HasScanned = true;
                ScannedChats = TotalChatsToScan;
                Publish(artifacts);
                PruneItemCache(artifacts);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer scan.
        }
        finally
        {
            // Close the gate on the cancelled and faulted paths too, so nothing can publish once this
            // scan has stopped owning the collections.
            lock (publishGate)
                scanFinished = true;

            if (ReferenceEquals(Volatile.Read(ref _scanCts), cts))
            {
                IsLoading = false;
                RefreshEmptyStates();
            }
        }
    }

    private void Publish(IReadOnlyList<LibraryArtifact> artifacts)
    {
        _artifacts = artifacts;
        RebuildFacetCounts();
        ApplyFilters();
        OnPropertyChanged(nameof(HasAnyArtifacts));
        RefreshEmptyStates();
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _isApplyingFilters = true;
        try
        {
            SearchQuery = "";
            ResetToAll(Kinds);
            ResetToAll(Origins);
            ResetToAll(TimeRanges);
            ResetToAll(Projects);
        }
        finally
        {
            _isApplyingFilters = false;
        }

        OnPropertyChanged(nameof(SelectedTimeRangeOption));
        ApplyFilters();
    }

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    partial void OnSearchQueryChanged(string value) => ApplyFilters();

    partial void OnSelectedSortIndexChanged(int value) => ApplyFilters();

    partial void OnIsLoadingChanged(bool value) => RefreshEmptyStates();

    private void SelectKind(LibraryFilterOption option) => SelectExclusive(Kinds, option);

    private void SelectOrigin(LibraryFilterOption option) => SelectExclusive(Origins, option);

    private void SelectTimeRange(LibraryFilterOption option) => SelectExclusive(TimeRanges, option);

    private void SelectProject(LibraryFilterOption option) => SelectExclusive(Projects, option);

    private void SelectExclusive(ObservableCollection<LibraryFilterOption> options, LibraryFilterOption? selected)
    {
        if (selected is null)
            return;

        foreach (var option in options)
            option.IsSelected = ReferenceEquals(option, selected);

        // Keeps the toolbar's bound "when" dropdown in step when a chip removes that constraint.
        OnPropertyChanged(nameof(SelectedTimeRangeOption));

        ApplyFilters();
    }

    /// <summary>
    /// Two-way binding target for the toolbar's time-window dropdown. Reading and writing through
    /// the facet collection keeps a single source of truth, so removing the matching chip snaps the
    /// dropdown back to "Any time" without a second piece of state to sync.
    /// </summary>
    public LibraryFilterOption? SelectedTimeRangeOption
    {
        get => TimeRanges.FirstOrDefault(option => option.IsSelected);
        set
        {
            if (value is not null && !value.IsSelected)
                SelectExclusive(TimeRanges, value);
        }
    }

    private static void ResetToAll(ObservableCollection<LibraryFilterOption> options)
    {
        foreach (var option in options)
            option.IsSelected = option.Id == AllFilterId;
    }

    private void BuildStaticFilters()
    {
        Kinds.Add(new LibraryFilterOption
        {
            Id = AllFilterId,
            Label = Loc.Library_Kind_All,
            IconPath = LibraryIcons.EverythingPath,
            IsSelected = true,
            SelectAction = SelectKind
        });
        foreach (var kind in Enum.GetValues<LibraryArtifactKind>())
        {
            Kinds.Add(new LibraryFilterOption
            {
                Id = kind.ToString(),
                Label = DescribeKind(kind),
                IconPath = LibraryIcons.PathForKind(kind),
                Kind = kind,
                SelectAction = SelectKind
            });
        }

        Origins.Add(new LibraryFilterOption { Id = AllFilterId, Label = Loc.Library_Origin_All, IconPath = LibraryIcons.EverythingPath, IsSelected = true, SelectAction = SelectOrigin });
        Origins.Add(new LibraryFilterOption { Id = nameof(LibraryArtifactOrigin.Sent), Label = Loc.Library_Origin_Sent, IconPath = LibraryIcons.UploadPath, SelectAction = SelectOrigin });
        Origins.Add(new LibraryFilterOption { Id = nameof(LibraryArtifactOrigin.Created), Label = Loc.Library_Origin_Created, IconPath = LibraryIcons.SparkPath, SelectAction = SelectOrigin });
        Origins.Add(new LibraryFilterOption { Id = nameof(LibraryArtifactOrigin.Referenced), Label = Loc.Library_Origin_Referenced, IconPath = LibraryIcons.GlobePath, SelectAction = SelectOrigin });

        TimeRanges.Add(new LibraryFilterOption { Id = AllFilterId, Label = Loc.Library_Time_All, IconPath = LibraryIcons.ClockPath, IsSelected = true, SelectAction = SelectTimeRange });
        TimeRanges.Add(new LibraryFilterOption { Id = nameof(LibraryTimeRange.Today), Label = Loc.Library_Time_Today, IconPath = LibraryIcons.ClockPath, SelectAction = SelectTimeRange });
        TimeRanges.Add(new LibraryFilterOption { Id = nameof(LibraryTimeRange.Week), Label = Loc.Library_Time_Week, IconPath = LibraryIcons.ClockPath, SelectAction = SelectTimeRange });
        TimeRanges.Add(new LibraryFilterOption { Id = nameof(LibraryTimeRange.Month), Label = Loc.Library_Time_Month, IconPath = LibraryIcons.ClockPath, SelectAction = SelectTimeRange });

    }

    /// <summary>
    /// Counts every facet in a single pass. A progressive scan republishes many times over a large
    /// corpus, so per-facet LINQ passes would multiply the UI-thread cost by the facet count.
    /// </summary>
    private void RebuildFacetCounts()
    {
        var now = DateTimeOffset.Now;
        var total = _artifacts.Count;

        var kindCounts = new Dictionary<LibraryArtifactKind, int>();
        var originCounts = new Dictionary<LibraryArtifactOrigin, int>();
        var projectCounts = new Dictionary<Guid, int>();
        var chatIds = new HashSet<Guid>();
        var today = 0;
        var week = 0;
        var month = 0;
        var unassigned = 0;
        var files = 0;
        var links = 0;
        var worktrees = 0;
        long totalBytes = 0;

        foreach (var artifact in _artifacts)
        {
            kindCounts[artifact.Kind] = kindCounts.GetValueOrDefault(artifact.Kind) + 1;
            originCounts[artifact.Origin] = originCounts.GetValueOrDefault(artifact.Origin) + 1;
            chatIds.Add(artifact.ChatId);

            if (artifact.ProjectId is { } projectId)
                projectCounts[projectId] = projectCounts.GetValueOrDefault(projectId) + 1;
            else
                unassigned++;

            if (MatchesTimeRange(artifact, LibraryTimeRange.Today, now)) today++;
            if (MatchesTimeRange(artifact, LibraryTimeRange.Week, now)) week++;
            if (MatchesTimeRange(artifact, LibraryTimeRange.Month, now)) month++;

            if (artifact.IsLink)
            {
                links++;
            }
            else if (artifact.Kind == LibraryArtifactKind.Worktree)
            {
                // Counted on its own: a worktree is a directory, so it is neither a file nor sized.
                worktrees++;
            }
            else
            {
                files++;
                if (artifact.Exists)
                    totalBytes += artifact.SizeBytes;
            }
        }

        foreach (var option in Kinds)
        {
            option.Count = option.Id == AllFilterId
                ? total
                : Enum.TryParse<LibraryArtifactKind>(option.Id, out var kind) ? kindCounts.GetValueOrDefault(kind) : 0;
        }

        foreach (var option in Origins)
        {
            option.Count = option.Id == AllFilterId
                ? total
                : Enum.TryParse<LibraryArtifactOrigin>(option.Id, out var origin) ? originCounts.GetValueOrDefault(origin) : 0;
        }

        foreach (var option in TimeRanges)
        {
            option.Count = ParseTimeRange(option.Id) switch
            {
                LibraryTimeRange.Today => today,
                LibraryTimeRange.Week => week,
                LibraryTimeRange.Month => month,
                _ => total
            };
        }

        RebuildProjectFilters(projectCounts, unassigned, total);

        TotalFiles = files;
        TotalLinks = links;
        TotalWorktrees = worktrees;
        SourceChatCount = chatIds.Count;
        TotalSizeLabel = ToolDisplayHelper.FormatFileSize(totalBytes);
        SummaryLine = worktrees > 0
            ? string.Format(
                CultureInfo.CurrentCulture,
                Loc.Library_Summary_WithWorktrees,
                TotalFiles,
                TotalLinks,
                TotalWorktrees,
                SourceChatCount)
            : string.Format(
                CultureInfo.CurrentCulture,
                Loc.Library_Summary,
                TotalFiles,
                TotalLinks,
                SourceChatCount);
    }

    private void RebuildProjectFilters(IReadOnlyDictionary<Guid, int> projectCounts, int unassigned, int total)
    {
        var previousSelection = SelectedProjectId;

        Projects.Clear();
        Projects.Add(new LibraryFilterOption
        {
            Id = AllFilterId,
            Label = Loc.Library_AllProjects,
            Count = total,
            SelectAction = SelectProject
        });

        foreach (var project in _dataStore.Data.Projects.OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var count = projectCounts.GetValueOrDefault(project.Id);
            if (count == 0)
                continue;

            Projects.Add(new LibraryFilterOption
            {
                Id = project.Id.ToString(),
                Label = project.Name,
                Count = count,
                SelectAction = SelectProject
            });
        }

        if (unassigned > 0 && Projects.Count > 1)
        {
            Projects.Add(new LibraryFilterOption
            {
                Id = NoProjectFilterId,
                Label = Loc.Library_NoProject,
                Count = unassigned,
                SelectAction = SelectProject
            });
        }

        var restored = Projects.FirstOrDefault(option => option.Id == previousSelection) ?? Projects[0];
        foreach (var option in Projects)
            option.IsSelected = ReferenceEquals(option, restored);

        OnPropertyChanged(nameof(HasProjectFilters));
    }

    private void ApplyFilters()
    {
        if (_isApplyingFilters)
            return;

        var kindId = SelectedKindId;
        var originId = SelectedOriginId;
        var timeRange = ParseTimeRange(SelectedTimeRangeId);
        var projectId = SelectedProjectId;
        var now = DateTimeOffset.Now;

        IEnumerable<LibraryArtifact> filtered = _artifacts;

        // Facet ids are parsed once: comparing enums beats allocating a string per artifact, and this
        // runs over the whole corpus on every keystroke and every scan republish.
        if (kindId != AllFilterId && Enum.TryParse<LibraryArtifactKind>(kindId, out var kind))
            filtered = filtered.Where(artifact => artifact.Kind == kind);

        if (originId != AllFilterId && Enum.TryParse<LibraryArtifactOrigin>(originId, out var origin))
            filtered = filtered.Where(artifact => artifact.Origin == origin);

        if (timeRange != LibraryTimeRange.All)
            filtered = filtered.Where(artifact => MatchesTimeRange(artifact, timeRange, now));

        if (projectId == NoProjectFilterId)
            filtered = filtered.Where(artifact => artifact.ProjectId is null);
        else if (projectId != AllFilterId && Guid.TryParse(projectId, out var parsedProjectId))
            filtered = filtered.Where(artifact => artifact.ProjectId == parsedProjectId);

        var matches = filtered.ToList();

        var query = SearchQuery?.Trim() ?? "";
        var isSearching = query.Length > 0;
        if (isSearching)
        {
            matches = SearchPipeline.Rank(
                matches,
                query,
                static artifact =>
                [
                    SearchField.Primary(artifact.Name, 3.2),
                    new SearchField(artifact.ChatTitle, 1.6),
                    new SearchField(artifact.ProjectName, 1.2),
                    SearchField.Content(artifact.Location, 0.9)
                ],
                static artifact => new SearchSortMetadata(Text: artifact.Name)).ToList();
        }
        else
        {
            matches = SortArtifacts(matches);
        }

        var selectedKey = SelectedItem?.Artifact.Key;

        // The list is virtualized, so every match is bound at once and only on-screen rows are
        // realized. Date buckets ride along on the items themselves rather than nesting the list.
        var items = BuildItems(matches);
        AssignGroupHeaders(items, isSearching, now);

        // One Reset instead of one notification per row: a long scan republishes this list on every
        // progress report, and the list runs to thousands of rows.
        Items.Reset(items);

        ResultCountLabel = string.Format(CultureInfo.CurrentCulture, Loc.Library_ResultCount, items.Count);

        RebuildActiveFilters();
        RebuildHighlights();

        SelectedItem = items.FirstOrDefault(item => item.Artifact.Key == selectedKey);

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(HasSearchQuery));
        RefreshEmptyStates();
    }

    /// <summary>
    /// Mirrors the rail selections and the search box into an explicit, removable chip per
    /// constraint. Each chip names the facet it came from ("Collection", "Origin", …) so a filter
    /// always reads as a filter rather than as a mysteriously short result list.
    /// </summary>
    private void RebuildActiveFilters()
    {
        ActiveFilters.Clear();

        var query = SearchQuery?.Trim() ?? "";
        if (query.Length > 0)
        {
            ActiveFilters.Add(new LibraryActiveFilter(
                Loc.Library_Chip_Search,
                '"' + query + '"',
                ClearSearch,
                LibraryIcons.SearchPath));
        }

        AddFacetChip(Kinds, Loc.Library_Chip_Collection);
        AddFacetChip(Origins, Loc.Library_Chip_Origin);
        AddFacetChip(TimeRanges, Loc.Library_Chip_When);
        AddFacetChip(Projects, Loc.Library_Chip_Project);

        OnPropertyChanged(nameof(HasMultipleActiveFilters));

        void AddFacetChip(ObservableCollection<LibraryFilterOption> options, string category)
        {
            var selected = options.FirstOrDefault(option => option.IsSelected);
            if (selected is null || selected.Id == AllFilterId)
                return;

            ActiveFilters.Add(new LibraryActiveFilter(
                category,
                selected.Label,
                () => SelectExclusive(options, options.FirstOrDefault(option => option.Id == AllFilterId)),
                selected.IconPath,
                selected.Accent));
        }
    }

    /// <summary>
    /// The gallery band at the top of the unfiltered view. It surfaces the most recent real files -
    /// never web links, which dominate by count but all look identical - so the Library opens on
    /// something worth looking at instead of a wall of rows.
    ///
    /// Runs on every republish and every keystroke, so it picks the top few with two early-exiting
    /// linear passes rather than sorting the whole corpus to take eight rows off the front.
    /// <see cref="_artifacts"/> already arrives newest-first from the scan.
    /// </summary>
    private void RebuildHighlights()
    {
        if (HasActiveFilters)
        {
            if (Highlights.Count > 0)
                Highlights.Reset([]);

            OnPropertyChanged(nameof(HasHighlights));
            return;
        }

        var picked = new List<LibraryArtifact>(HighlightCount);

        foreach (var artifact in _artifacts)
        {
            if (picked.Count == HighlightCount)
                break;
            if (IsPreviewableHighlight(artifact))
                picked.Add(artifact);
        }

        if (picked.Count < HighlightCount)
        {
            foreach (var artifact in _artifacts)
            {
                if (picked.Count == HighlightCount)
                    break;
                if (IsGalleryCandidate(artifact) && !IsPreviewableHighlight(artifact))
                    picked.Add(artifact);
            }
        }

        Highlights.Reset(BuildItems(picked));
        OnPropertyChanged(nameof(HasHighlights));
    }

    /// <summary>
    /// The gallery shows real files. Links all look identical and dominate by count, and a worktree
    /// is a directory with nothing to show.
    /// </summary>
    private static bool IsGalleryCandidate(LibraryArtifact artifact)
        => !artifact.IsLink && artifact.Kind != LibraryArtifactKind.Worktree;

    /// <summary>Images still on disk lead the gallery, since they are the only tiles that show art.</summary>
    private static bool IsPreviewableHighlight(LibraryArtifact artifact)
        => artifact.Kind == LibraryArtifactKind.Image && artifact.Exists;

    /// <summary>
    /// Stamps the first row of each time bucket with its label. Search results and the name/size
    /// orders are a single ranked list, so they carry no separators at all.
    /// </summary>
    private void AssignGroupHeaders(List<LibraryItemViewModel> items, bool isSearching, DateTimeOffset now)
    {
        if (isSearching || (LibrarySort)SelectedSortIndex is LibrarySort.Name or LibrarySort.Largest)
        {
            foreach (var item in items)
                item.GroupHeader = null;
            return;
        }

        string? previous = null;
        foreach (var item in items)
        {
            var label = DescribeBucket(item.Artifact.LastSeen, now);
            item.GroupHeader = label == previous ? null : label;
            previous = label;
        }
    }

    private static string DescribeBucket(DateTimeOffset timestamp, DateTimeOffset now)
    {
        var day = timestamp.Date;
        var today = now.Date;

        if (day == today)
            return Loc.ChatGroup_Today;
        if (day == today.AddDays(-1))
            return Loc.ChatGroup_Yesterday;
        if (day >= today.AddDays(-7))
            return Loc.ChatGroup_Previous7Days;

        return Loc.ChatGroup_Older;
    }

    /// <summary>
    /// Orders the filtered matches. The scan already hands over artifacts newest-first, so the
    /// default order is a no-op rather than a full re-sort of the corpus on every republish.
    /// </summary>
    private List<LibraryArtifact> SortArtifacts(List<LibraryArtifact> artifacts) =>
        (LibrarySort)SelectedSortIndex switch
        {
            LibrarySort.Oldest => [.. artifacts.OrderBy(artifact => artifact.LastSeen)],
            LibrarySort.Name => [.. artifacts.OrderBy(artifact => artifact.Name, StringComparer.CurrentCultureIgnoreCase)],
            LibrarySort.Largest => [.. artifacts.OrderByDescending(artifact => artifact.SizeBytes)
                .ThenByDescending(artifact => artifact.LastSeen)],
            _ => artifacts
        };

    /// <summary>
    /// Item ViewModels are reused across publishes, keyed by artifact identity. A progressive scan
    /// republishes the list many times, and rebuilding every row would re-decode previews and
    /// throw away scroll/selection state each time.
    /// </summary>
    private List<LibraryItemViewModel> BuildItems(IEnumerable<LibraryArtifact> artifacts)
    {
        var items = new List<LibraryItemViewModel>();
        foreach (var artifact in artifacts)
        {
            if (_itemCache.TryGetValue(artifact.Key, out var item))
            {
                item.Rebind(artifact);
            }
            else
            {
                item = new LibraryItemViewModel(artifact, _openChatAsync, SelectItem);
                _itemCache[artifact.Key] = item;
            }

            items.Add(item);
        }

        return items;
    }

    private void SelectItem(LibraryItemViewModel item) => SelectedItem = item;

    /// <summary>
    /// Drops rows whose artifact the latest scan no longer sees - deleted chats, removed files. The
    /// ViewModel lives for the whole app session, so without this the cache only ever grows. Only
    /// safe after a completed scan: a partial one is a subset and would evict rows still to come.
    /// </summary>
    private void PruneItemCache(IReadOnlyList<LibraryArtifact> artifacts)
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in artifacts)
            live.Add(artifact.Key);

        List<string>? stale = null;
        foreach (var key in _itemCache.Keys)
        {
            if (!live.Contains(key))
                (stale ??= []).Add(key);
        }

        if (stale is null)
            return;

        foreach (var key in stale)
            _itemCache.Remove(key);
    }

    partial void OnSelectedItemChanged(LibraryItemViewModel? oldValue, LibraryItemViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;
        if (newValue is not null)
            newValue.IsSelected = true;

        OnPropertyChanged(nameof(HasSelection));
    }

    private void RefreshEmptyStates()
    {
        OnPropertyChanged(nameof(IsEmptyLibrary));
        OnPropertyChanged(nameof(IsEmptyResult));
        OnPropertyChanged(nameof(IsInitialScan));
    }

    partial void OnScannedChatsChanged(int value) => RefreshScanProgress();

    partial void OnTotalChatsToScanChanged(int value) => RefreshScanProgress();

    private void RefreshScanProgress()
    {
        OnPropertyChanged(nameof(ScanProgressLabel));
        OnPropertyChanged(nameof(ScanFraction));
        OnPropertyChanged(nameof(ScanPercent));
    }

    private static LibraryTimeRange ParseTimeRange(string id) =>
        Enum.TryParse<LibraryTimeRange>(id, ignoreCase: true, out var range) ? range : LibraryTimeRange.All;

    internal static bool MatchesTimeRange(LibraryArtifact artifact, LibraryTimeRange range, DateTimeOffset now) => range switch
    {
        LibraryTimeRange.Today => artifact.LastSeen.Date == now.Date,
        LibraryTimeRange.Week => artifact.LastSeen >= now.AddDays(-7),
        LibraryTimeRange.Month => artifact.LastSeen >= now.AddDays(-30),
        _ => true
    };

    internal static string DescribeKind(LibraryArtifactKind kind) => kind switch
    {
        LibraryArtifactKind.Image => Loc.Library_Kind_Image,
        LibraryArtifactKind.Document => Loc.Library_Kind_Document,
        LibraryArtifactKind.Sheet => Loc.Library_Kind_Sheet,
        LibraryArtifactKind.Slides => Loc.Library_Kind_Slides,
        LibraryArtifactKind.Code => Loc.Library_Kind_Code,
        LibraryArtifactKind.Media => Loc.Library_Kind_Media,
        LibraryArtifactKind.Archive => Loc.Library_Kind_Archive,
        LibraryArtifactKind.Link => Loc.Library_Kind_Link,
        LibraryArtifactKind.Worktree => Loc.Library_Kind_Worktree,
        _ => Loc.Library_Kind_Other
    };

    internal static string DescribeOrigin(LibraryArtifactOrigin origin) => origin switch
    {
        LibraryArtifactOrigin.Sent => Loc.Library_Origin_Sent,
        LibraryArtifactOrigin.Created => Loc.Library_Origin_Created,
        _ => Loc.Library_Origin_Referenced
    };

    internal static string FormatRelativeTime(DateTimeOffset timestamp)
    {
        var age = DateTimeOffset.Now - timestamp;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        if (age < TimeSpan.FromMinutes(1))
            return Loc.Library_Time_JustNow;
        if (age < TimeSpan.FromHours(1))
            return string.Format(CultureInfo.CurrentCulture, Loc.Library_Time_MinutesAgo, Math.Max(1, (int)age.TotalMinutes));
        if (age < TimeSpan.FromDays(1))
            return string.Format(CultureInfo.CurrentCulture, Loc.Library_Time_HoursAgo, Math.Max(1, (int)age.TotalHours));
        if (age < TimeSpan.FromDays(7))
            return string.Format(CultureInfo.CurrentCulture, Loc.Library_Time_DaysAgo, Math.Max(1, (int)age.TotalDays));
        if (age < TimeSpan.FromDays(365))
            return timestamp.ToString("MMM d", CultureInfo.CurrentCulture);

        return timestamp.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }
}
