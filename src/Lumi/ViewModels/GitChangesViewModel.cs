using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Services;

namespace Lumi.ViewModels;

// ── Git changes (Workspace page) ─────────────────────
// Groups the working-tree changes by source repository (the project repo plus every submodule
// that has changes) and then into a folder tree, so a change set reads as "where did this come from"
// instead of a flat list of file names. The tree is shown as one flat list of rows (sources, folders
// and files, each with its depth), so the page virtualizes and folding a folder only edits its rows.

/// <summary>A row of the Git changes tree: a source repository, a folder or a file.</summary>
public abstract class GitChangeTreeRow : ObservableObject
{
    private int _depth;

    /// <summary>Nesting level; drives the row's indent and its indent guides.</summary>
    public int Depth
    {
        get => _depth;
        internal set => SetProperty(ref _depth, value);
    }

    internal GitChangeTreeNode? Parent { get; set; }
}

/// <summary>A row that holds other rows and can be folded: a source repository or a folder.</summary>
public abstract partial class GitChangeTreeNode : GitChangeTreeRow
{
    internal string Key { get; }
    internal Action<GitChangeTreeNode>? ToggleAction { get; set; }

    public List<GitChangeFolderNode> Folders { get; } = [];
    public List<GitFileChangeViewModel> Files { get; } = [];

    public int FileCount { get; private set; }
    internal int LinesAdded { get; private set; }
    internal int LinesRemoved { get; private set; }
    public string AddedCell { get; private set; } = "";
    public string RemovedCell { get; private set; } = "";
    public string FileCountCompact => FileCount.ToString(CultureInfo.InvariantCulture);
    public string FileCountLabel => GitChangesViewModel.FormatFileCount(FileCount);

    [ObservableProperty] private bool _isExpanded = true;

    protected GitChangeTreeNode(string key) => Key = key;

    [RelayCommand]
    private void Toggle()
    {
        if (ToggleAction is { } toggle)
            toggle(this);
        else
            IsExpanded = !IsExpanded;
    }

    /// <summary>Totals over everything beneath; called once children are in place.</summary>
    internal void Summarize()
    {
        var files = 0;
        var added = 0;
        var removed = 0;
        foreach (var folder in Folders)
        {
            files += folder.FileCount;
            added += folder.LinesAdded;
            removed += folder.LinesRemoved;
        }

        foreach (var file in Files)
        {
            files++;
            added += file.LinesAdded;
            removed += file.LinesRemoved;
        }

        FileCount = files;
        LinesAdded = added;
        LinesRemoved = removed;
        AddedCell = added > 0 ? $"+{added.ToString(CultureInfo.InvariantCulture)}" : "";
        RemovedCell = removed > 0 ? $"−{removed.ToString(CultureInfo.InvariantCulture)}" : "";
    }

    /// <summary>Folders then files, depth first — the order rows appear when everything is open.</summary>
    internal IEnumerable<GitFileChangeViewModel> EnumerateFiles()
    {
        foreach (var folder in Folders)
            foreach (var file in folder.EnumerateFiles())
                yield return file;
        foreach (var file in Files)
            yield return file;
    }

    internal IEnumerable<GitChangeTreeNode> EnumerateNodes()
    {
        yield return this;
        foreach (var folder in Folders)
            foreach (var node in folder.EnumerateNodes())
                yield return node;
    }
}

/// <summary>A single changed file row on the Workspace's Git changes page.</summary>
public partial class GitFileChangeViewModel : GitChangeTreeRow
{
    public GitFileChange Change { get; }
    private readonly Action<GitFileChangeViewModel>? _showDiffAction;

    public string FileName => Change.FileName;
    public string? Directory => Change.Directory;
    public string? RepoRelativeDirectory => Change.RepoRelativeDirectory;
    public string KindIcon => Change.KindIcon;
    public string KindLabel => Change.DisplayKindLabel;
    public GitChangeKind Kind => Change.Kind;
    public int LinesAdded => Change.LinesAdded;
    public int LinesRemoved => Change.LinesRemoved;
    public bool HasStats => LinesAdded > 0 || LinesRemoved > 0;
    public string FullPath => Change.FullPath;
    public string RelativePath => Change.RelativePath;

    /// <summary>Blank when zero so the +/− columns stay aligned across rows.</summary>
    public string AddedCell => LinesAdded > 0 ? $"+{LinesAdded.ToString(CultureInfo.InvariantCulture)}" : "";
    public string RemovedCell => LinesRemoved > 0 ? $"−{LinesRemoved.ToString(CultureInfo.InvariantCulture)}" : "";

    public bool IsAdded => Kind is GitChangeKind.Added;
    public bool IsUntracked => Kind is GitChangeKind.Untracked;
    public bool IsDeleted => Kind is GitChangeKind.Deleted;
    public bool IsRenamed => Kind is GitChangeKind.Renamed;
    public bool IsSubmodulePointer => Kind is GitChangeKind.Submodule;

    /// <summary>Secondary text: the reason a submodule row exists, otherwise its status.</summary>
    public string SubtitleLabel => IsSubmodulePointer ? Loc.Git_SubmodulePointer : KindLabel;

    /// <summary>Shown after the name on the row itself, so every row keeps one line.</summary>
    public string InlineNote => IsSubmodulePointer ? $"  {Loc.Git_SubmodulePointer}" : "";

    public string PathTooltip => string.IsNullOrEmpty(Change.SubmodulePath)
        ? Change.FullPath
        : $"{Change.SubmodulePath} · {Change.FullPath}";

    /// <summary>Highlights the row the user drilled into so returning from a diff keeps context.</summary>
    [ObservableProperty] private bool _isSelected;

    public GitFileChangeViewModel(GitFileChange change, Action<GitFileChangeViewModel>? showDiffAction = null)
    {
        Change = change;
        _showDiffAction = showDiffAction;
    }

    [RelayCommand]
    private void ShowDiff() => _showDiffAction?.Invoke(this);
}

/// <summary>
/// A folder inside one source repository. Chains of folders that hold nothing but one folder are
/// compacted into a single row ("src/Lumi/Views"), as editors do, so deep paths don't waste rows.
/// </summary>
public sealed class GitChangeFolderNode : GitChangeTreeNode
{
    /// <summary>The row's text: one folder, or a compacted chain of them.</summary>
    public string Name { get; }

    /// <summary>Path from the repository root, e.g. "src/Lumi/Views".</summary>
    public string FullPath { get; }

    /// <summary>Leading segments of a compacted chain, dimmed so the folder the files live in reads first.</summary>
    public string ParentLabel { get; }
    public string LeafLabel { get; }

    public GitChangeFolderNode(string key, string name, string fullPath) : base(key)
    {
        Name = name;
        FullPath = fullPath;
        var lastSeparator = name.LastIndexOf('/');
        ParentLabel = lastSeparator > 0 ? name[..(lastSeparator + 1)] : "";
        LeafLabel = name[(lastSeparator + 1)..];
    }
}

/// <summary>One repository that contributed changes — the project repo or a submodule.</summary>
public sealed class GitChangeSourceGroup : GitChangeTreeNode
{
    public string Name { get; }
    public bool IsSubmodule { get; }
    public string? SourcePath { get; }
    public string? BadgeLabel { get; }
    public bool HasBadge => !string.IsNullOrEmpty(BadgeLabel);

    public GitChangeSourceGroup(string key, string name, bool isSubmodule, string? sourcePath, string? badgeLabel)
        : base(key)
    {
        Name = name;
        IsSubmodule = isSubmodule;
        SourcePath = sourcePath;
        BadgeLabel = badgeLabel;
    }
}

public enum GitChangeKindGroup
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Submodule,
}

/// <summary>A chip in the summary ("4 added") that narrows the list to that kind of change.</summary>
public sealed partial class GitChangeKindFilter : ObservableObject
{
    private readonly Action<GitChangeKindFilter> _toggle;

    public GitChangeKindGroup Group { get; }
    public int Count { get; }
    public string Label { get; }

    public bool IsAdded => Group is GitChangeKindGroup.Added;
    public bool IsDeleted => Group is GitChangeKindGroup.Deleted;
    public bool IsRenamed => Group is GitChangeKindGroup.Renamed;
    public bool IsSubmodule => Group is GitChangeKindGroup.Submodule;

    [ObservableProperty] private bool _isActive;

    internal GitChangeKindFilter(GitChangeKindGroup group, int count, string label, Action<GitChangeKindFilter> toggle)
    {
        Group = group;
        Count = count;
        Label = $"{count.ToString(CultureInfo.InvariantCulture)} {label}";
        _toggle = toggle;
    }

    [RelayCommand]
    private void Toggle() => _toggle(this);

    internal static GitChangeKindGroup GroupOf(GitChangeKind kind) => kind switch
    {
        GitChangeKind.Added or GitChangeKind.Untracked => GitChangeKindGroup.Added,
        GitChangeKind.Deleted => GitChangeKindGroup.Deleted,
        GitChangeKind.Renamed => GitChangeKindGroup.Renamed,
        GitChangeKind.Submodule => GitChangeKindGroup.Submodule,
        _ => GitChangeKindGroup.Modified,
    };
}

/// <summary>Backing model for the Git changes page: totals, the folder tree, folding and filtering.</summary>
public sealed partial class GitChangesViewModel : ObservableObject
{
    /// <summary>Total pixel width of the added/removed proportion bar in the summary header.</summary>
    private const double StatBarWidth = 96;

    private readonly List<GitFileChangeViewModel> _allFiles;

    /// <summary>Folded nodes while no filter is applied; kept across filtering.</summary>
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folded nodes in filtered results; every new filter starts with its matches open.</summary>
    private readonly HashSet<string> _filterCollapsed = new(StringComparer.OrdinalIgnoreCase);

    private List<GitFileChangeViewModel> _orderedFiles = [];

    public ObservableCollection<GitChangeSourceGroup> Sources { get; } = [];

    /// <summary>The tree as the page shows it: the rows of every open node, in order.</summary>
    [ObservableProperty] private ObservableCollection<GitChangeTreeRow> _rows = [];

    public string RootName { get; }
    public string? RootPath { get; }
    public string? BranchLabel { get; }
    public bool HasBranch => !string.IsNullOrEmpty(BranchLabel);
    public bool IsWorktree { get; }

    public string TotalFilesLabel { get; }
    public string TotalAddedLabel { get; }
    public string TotalRemovedLabel { get; }
    public bool HasTotalStats { get; }
    public double AddedBarWidth { get; }
    public double RemovedBarWidth { get; }

    /// <summary>Per-kind rollup, e.g. "4 added · 7 modified · 1 deleted".</summary>
    public string KindBreakdownLabel { get; }
    public bool HasKindBreakdown => !string.IsNullOrEmpty(KindBreakdownLabel);

    /// <summary>The same rollup as chips that filter the list to one kind of change.</summary>
    public IReadOnlyList<GitChangeKindFilter> KindFilters { get; }

    public bool HasFiles => _allFiles.Count > 0;

    /// <summary>
    /// More than one repository has changes, so each gets a header row. With one, the summary already
    /// names it and its folders start at the left edge.
    /// </summary>
    public bool HasMultipleSources { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilter))]
    private string _filterText = "";

    public bool HasFilter => !string.IsNullOrWhiteSpace(FilterText);

    [ObservableProperty] private GitChangeKindGroup? _activeKind;

    [ObservableProperty] private bool _hasNoMatches;

    /// <summary>Some folder or source can be folded, so Collapse all / Expand all has work to do.</summary>
    [ObservableProperty] private bool _hasFoldableRows;

    /// <summary>Something is open: the toggle collapses everything, otherwise it expands everything.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleAllLabel))]
    private bool _canCollapseAll;

    public string ToggleAllLabel => CanCollapseAll ? Loc.Git_CollapseAll : Loc.Git_ExpandAll;

    /// <summary>Invoked when a row is activated; the host opens the diff for that file.</summary>
    public Action<GitFileChangeViewModel>? FileActivated { get; set; }

    /// <summary>A file to open straight away when the list is shown (e.g. picked in the Workspace overview).</summary>
    public string? InitialFilePath { get; init; }

    public GitFileChangeViewModel? FindFile(string? fullPath)
        => string.IsNullOrEmpty(fullPath)
            ? null
            : _allFiles.FirstOrDefault(file => string.Equals(file.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));

    public GitChangesViewModel(
        IEnumerable<GitFileChange> changes,
        string? rootPath,
        string? branch,
        bool isWorktree)
    {
        RootPath = rootPath;
        BranchLabel = string.IsNullOrWhiteSpace(branch) ? null : branch;
        IsWorktree = isWorktree;
        RootName = ResolveRootName(rootPath);

        _allFiles = changes
            .Select(change => new GitFileChangeViewModel(change, file => FileActivated?.Invoke(file)))
            .ToList();

        var added = _allFiles.Sum(static f => f.LinesAdded);
        var removed = _allFiles.Sum(static f => f.LinesRemoved);
        TotalFilesLabel = FormatFileCount(_allFiles.Count);
        TotalAddedLabel = $"+{added.ToString(CultureInfo.InvariantCulture)}";
        TotalRemovedLabel = $"−{removed.ToString(CultureInfo.InvariantCulture)}";
        HasTotalStats = added > 0 || removed > 0;
        (AddedBarWidth, RemovedBarWidth) = ComputeBar(added, removed);
        KindFilters = BuildKindFilters(_allFiles, OnKindFilterToggled);
        KindBreakdownLabel = string.Join(" · ", KindFilters.Select(static k => k.Label));
        HasMultipleSources = _allFiles
            .Select(static f => f.Change.SubmodulePath ?? "")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Skip(1)
            .Any();

        RebuildGroups();
    }

    private bool HasActiveFilter => HasFilter || ActiveKind is not null;

    private HashSet<string> CollapsedKeys => HasActiveFilter ? _filterCollapsed : _collapsed;

    partial void OnFilterTextChanged(string value)
    {
        _filterCollapsed.Clear();
        RebuildGroups();
    }

    partial void OnActiveKindChanged(GitChangeKindGroup? value)
    {
        foreach (var filter in KindFilters)
            filter.IsActive = filter.Group == value;
        _filterCollapsed.Clear();
        RebuildGroups();
    }

    private void OnKindFilterToggled(GitChangeKindFilter filter)
        => ActiveKind = ActiveKind == filter.Group ? null : filter.Group;

    [RelayCommand]
    private void ClearFilter()
    {
        FilterText = "";
        ActiveKind = null;
    }

    /// <summary>Folds every folder (and source header) when anything is open, otherwise opens them all.</summary>
    [RelayCommand]
    private void ToggleAll()
    {
        var collapse = CanCollapseAll;
        var keys = CollapsedKeys;
        foreach (var node in FoldableNodes())
        {
            node.IsExpanded = !collapse;
            if (collapse)
                keys.Add(node.Key);
            else
                keys.Remove(node.Key);
        }

        RebuildRows();
    }

    public void ClearSelection()
    {
        foreach (var file in _allFiles)
            file.IsSelected = false;
    }

    /// <summary>Marks the file the user opened and unfolds the folders above it, so the list shows it on return.</summary>
    public void Select(GitFileChangeViewModel file)
    {
        ClearSelection();
        file.IsSelected = true;

        var unfolded = false;
        for (var node = file.Parent; node is not null; node = node.Parent)
        {
            if (node.IsExpanded)
                continue;
            node.IsExpanded = true;
            CollapsedKeys.Remove(node.Key);
            unfolded = true;
        }

        if (unfolded)
            RebuildRows();
    }

    /// <summary>The file <paramref name="delta"/> places away in the list's order, among the files the filter shows.</summary>
    public GitFileChangeViewModel? AdjacentFile(GitFileChangeViewModel file, int delta)
    {
        var index = _orderedFiles.IndexOf(file);
        if (index < 0)
            return null;
        var target = index + delta;
        return target >= 0 && target < _orderedFiles.Count ? _orderedFiles[target] : null;
    }

    /// <summary>1-based position of a file among the files the filter shows, and how many there are.</summary>
    public (int Position, int Count) PositionOf(GitFileChangeViewModel file)
        => (_orderedFiles.IndexOf(file) + 1, _orderedFiles.Count);

    public int IndexOfRow(GitChangeTreeRow row) => Rows.IndexOf(row);

    private void RebuildGroups()
    {
        var filter = FilterText.Trim();
        var visible = HasActiveFilter
            ? _allFiles.Where(f => Matches(f, filter)).ToList()
            : _allFiles;

        Sources.Clear();
        foreach (var source in BuildSources(visible))
            Sources.Add(source);

        _orderedFiles = Sources.SelectMany(static s => s.EnumerateFiles()).ToList();
        HasNoMatches = HasFiles && visible.Count == 0;
        RebuildRows();
    }

    private bool Matches(GitFileChangeViewModel file, string filter)
        => (filter.Length == 0 || file.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase))
           && (ActiveKind is not { } kind || GitChangeKindFilter.GroupOf(file.Kind) == kind);

    private IEnumerable<GitChangeTreeNode> FoldableNodes()
        => Sources.SelectMany(source => HasMultipleSources
            ? source.EnumerateNodes()
            : source.EnumerateNodes().Skip(1));

    /// <summary>Lays the open part of the tree out as rows; replaces the collection in one step.</summary>
    private void RebuildRows()
    {
        var rows = new List<GitChangeTreeRow>(_orderedFiles.Count + 16);
        foreach (var source in Sources)
        {
            if (HasMultipleSources)
            {
                rows.Add(source);
                if (source.IsExpanded)
                    AppendChildren(source, rows);
            }
            else
                AppendChildren(source, rows);
        }

        Rows = new ObservableCollection<GitChangeTreeRow>(rows);
        UpdateFoldState();
    }

    private static void AppendChildren(GitChangeTreeNode node, List<GitChangeTreeRow> rows)
    {
        foreach (var folder in node.Folders)
        {
            rows.Add(folder);
            if (folder.IsExpanded)
                AppendChildren(folder, rows);
        }

        rows.AddRange(node.Files);
    }

    /// <summary>Folds or unfolds one node by removing or inserting only the rows beneath it.</summary>
    private void ToggleNode(GitChangeTreeNode node)
    {
        node.IsExpanded = !node.IsExpanded;
        if (node.IsExpanded)
            CollapsedKeys.Remove(node.Key);
        else
            CollapsedKeys.Add(node.Key);

        var index = Rows.IndexOf(node);
        if (index >= 0)
        {
            if (node.IsExpanded)
            {
                var children = new List<GitChangeTreeRow>();
                AppendChildren(node, children);
                for (var i = 0; i < children.Count; i++)
                    Rows.Insert(index + 1 + i, children[i]);
            }
            else
            {
                while (index + 1 < Rows.Count && Rows[index + 1].Depth > node.Depth)
                    Rows.RemoveAt(index + 1);
            }
        }

        UpdateFoldState();
    }

    private void UpdateFoldState()
    {
        var foldable = false;
        var anyOpen = false;
        foreach (var node in FoldableNodes())
        {
            foldable = true;
            if (node.IsExpanded)
            {
                anyOpen = true;
                break;
            }
        }

        HasFoldableRows = foldable;
        CanCollapseAll = anyOpen;
    }

    private IEnumerable<GitChangeSourceGroup> BuildSources(IReadOnlyCollection<GitFileChangeViewModel> files)
    {
        // Main repository first, then submodules by path so nesting reads top-down.
        var groups = files
            .GroupBy(f => f.Change.SubmodulePath ?? "", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key.Length == 0 ? 0 : 1)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        var keys = CollapsedKeys;
        foreach (var group in groups)
        {
            var isSubmodule = group.Key.Length > 0;
            var name = isSubmodule
                ? group.Key[(group.Key.LastIndexOf('/') + 1)..]
                : RootName;
            var badge = isSubmodule
                ? Loc.Git_Submodule
                : IsWorktree ? Loc.Git_Worktree : null;
            var sourcePath = isSubmodule
                ? group.Key
                : RootPath;

            var source = new GitChangeSourceGroup(group.Key + "\u0000", name, isSubmodule, sourcePath, badge)
            {
                Depth = 0,
                ToggleAction = ToggleNode,
            };
            source.IsExpanded = !keys.Contains(source.Key);
            BuildTree(source, group, HasMultipleSources ? 1 : 0, keys);
            yield return source;
        }
    }

    private void BuildTree(GitChangeSourceGroup source, IEnumerable<GitFileChangeViewModel> files, int depth, HashSet<string> collapsed)
    {
        var root = new FolderBuilder("");
        foreach (var file in files)
        {
            // A pointer row keeps its parent-repo path (git needs it for the commit log), so place it at
            // the submodule root instead — otherwise a nested submodule invents a folder that doesn't
            // exist inside it (e.g. "vendor" showing up inside "vendor/lib").
            var directory = file.IsSubmodulePointer ? "" : file.RepoRelativeDirectory ?? "";
            var builder = root;
            foreach (var segment in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
                builder = builder.Child(segment);
            builder.Files.Add(file);
        }

        Fill(source, root, "", depth);
        source.Summarize();

        void Fill(GitChangeTreeNode node, FolderBuilder builder, string path, int level)
        {
            foreach (var child in builder.Children.Values.OrderBy(static c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                var name = child.Name;
                var current = child;
                while (current.Files.Count == 0 && current.Children.Count == 1)
                {
                    current = current.Children.Values.First();
                    name += "/" + current.Name;
                }

                var fullPath = path.Length == 0 ? name : $"{path}/{name}";
                var folder = new GitChangeFolderNode(source.Key + fullPath, name, fullPath)
                {
                    Depth = level,
                    Parent = node,
                    ToggleAction = ToggleNode,
                };
                folder.IsExpanded = !collapsed.Contains(folder.Key);
                Fill(folder, current, fullPath, level + 1);
                folder.Summarize();
                node.Folders.Add(folder);
            }

            foreach (var file in builder.Files.OrderBy(static f => f.FileName, StringComparer.OrdinalIgnoreCase))
            {
                file.Depth = level;
                file.Parent = node;
                node.Files.Add(file);
            }
        }
    }

    private sealed class FolderBuilder(string name)
    {
        public string Name { get; } = name;
        public Dictionary<string, FolderBuilder> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<GitFileChangeViewModel> Files { get; } = [];

        public FolderBuilder Child(string segment)
        {
            if (!Children.TryGetValue(segment, out var child))
                Children[segment] = child = new FolderBuilder(segment);
            return child;
        }
    }

    private static (double Added, double Removed) ComputeBar(int added, int removed)
    {
        var total = added + removed;
        if (total <= 0)
            return (0, 0);
        if (added == 0)
            return (0, StatBarWidth);
        if (removed == 0)
            return (StatBarWidth, 0);

        // Keep both segments visible even when one side is a rounding error.
        var addedWidth = Math.Clamp(StatBarWidth * added / total, 4, StatBarWidth - 4);
        return (Math.Round(addedWidth), Math.Round(StatBarWidth - addedWidth));
    }

    private static IReadOnlyList<GitChangeKindFilter> BuildKindFilters(
        IReadOnlyCollection<GitFileChangeViewModel> files,
        Action<GitChangeKindFilter> toggle)
    {
        var counts = files
            .GroupBy(static f => GitChangeKindFilter.GroupOf(f.Kind))
            .ToDictionary(static g => g.Key, static g => g.Count());

        var filters = new List<GitChangeKindFilter>(5);
        void Add(GitChangeKindGroup group, string label)
        {
            if (counts.TryGetValue(group, out var count) && count > 0)
                filters.Add(new GitChangeKindFilter(group, count, label, toggle));
        }

        Add(GitChangeKindGroup.Added, Loc.Git_KindAdded);
        Add(GitChangeKindGroup.Modified, Loc.Git_KindModified);
        Add(GitChangeKindGroup.Deleted, Loc.Git_KindDeleted);
        Add(GitChangeKindGroup.Renamed, Loc.Git_KindRenamed);
        Add(GitChangeKindGroup.Submodule, Loc.Git_KindSubmodule);
        return filters;
    }

    internal static string FormatFileCount(int count)
        => count == 1 ? Loc.Git_OneFileChanged : string.Format(Loc.Culture, Loc.Git_NFilesChanged, count);

    private static string ResolveRootName(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return Loc.Git_Repository;

        try
        {
            var name = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? Loc.Git_Repository : name;
        }
        catch
        {
            return Loc.Git_Repository;
        }
    }
}
