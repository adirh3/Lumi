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

// ── Git changes island ───────────────────────────────
// Groups the working-tree changes by source repository (the project repo plus every submodule
// that has changes) and then by folder, so a change set reads as "where did this come from"
// instead of a flat list of file names.

/// <summary>A single changed file row in the changes island.</summary>
public partial class GitFileChangeViewModel : ObservableObject
{
    public GitFileChange Change { get; }
    private readonly Action<GitFileChangeViewModel>? _showDiffAction;

    public string FileName => Change.FileName;
    public string? Directory => Change.Directory;
    public string? RepoRelativeDirectory => Change.RepoRelativeDirectory;
    public string KindIcon => Change.KindIcon;
    public string KindLabel => Change.KindLabel;
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

    /// <summary>Secondary line: the reason a submodule row exists, otherwise its status.</summary>
    public string SubtitleLabel => IsSubmodulePointer ? Loc.Git_SubmodulePointer : KindLabel;

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

/// <summary>Files that share a folder inside one source repository.</summary>
public sealed class GitChangeFolderGroup
{
    public string FolderLabel { get; }

    /// <summary>Leading path segments, dimmed so the folder the files actually live in reads first.</summary>
    public string FolderParentLabel { get; }
    public string FolderLeafLabel { get; }
    public bool HasFolderParent => FolderParentLabel.Length > 0;

    public string CountLabel { get; }
    public ObservableCollection<GitFileChangeViewModel> Files { get; } = [];

    public GitChangeFolderGroup(string? folder, IEnumerable<GitFileChangeViewModel> files)
    {
        FolderLabel = string.IsNullOrEmpty(folder) ? Loc.Git_RepositoryRoot : folder;
        var lastSeparator = FolderLabel.LastIndexOf('/');
        FolderParentLabel = lastSeparator > 0 ? FolderLabel[..(lastSeparator + 1)] : "";
        FolderLeafLabel = FolderLabel[(lastSeparator + 1)..];

        foreach (var file in files)
            Files.Add(file);
        CountLabel = Files.Count.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>One repository that contributed changes — the project repo or a submodule.</summary>
public sealed partial class GitChangeSourceGroup : ObservableObject
{
    public string Name { get; }
    public bool IsSubmodule { get; }
    public string? SourcePath { get; }
    public string? BadgeLabel { get; }
    public bool HasBadge => !string.IsNullOrEmpty(BadgeLabel);
    public string FileCountLabel { get; }
    public string AddedCell { get; }
    public string RemovedCell { get; }
    public ObservableCollection<GitChangeFolderGroup> Folders { get; } = [];

    [ObservableProperty] private bool _isExpanded = true;

    public GitChangeSourceGroup(
        string name,
        bool isSubmodule,
        string? sourcePath,
        string? badgeLabel,
        IEnumerable<GitChangeFolderGroup> folders)
    {
        Name = name;
        IsSubmodule = isSubmodule;
        SourcePath = sourcePath;
        BadgeLabel = badgeLabel;
        foreach (var folder in folders)
            Folders.Add(folder);

        var files = Folders.SelectMany(static f => f.Files).ToList();
        FileCountLabel = GitChangesViewModel.FormatFileCount(files.Count);
        var added = files.Sum(static f => f.LinesAdded);
        var removed = files.Sum(static f => f.LinesRemoved);
        AddedCell = added > 0 ? $"+{added.ToString(CultureInfo.InvariantCulture)}" : "";
        RemovedCell = removed > 0 ? $"−{removed.ToString(CultureInfo.InvariantCulture)}" : "";
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>Backing model for the changes island: totals, grouping and live filtering.</summary>
public sealed partial class GitChangesViewModel : ObservableObject
{
    /// <summary>Total pixel width of the added/removed proportion bar in the summary header.</summary>
    private const double StatBarWidth = 96;

    private readonly List<GitFileChangeViewModel> _allFiles;

    public ObservableCollection<GitChangeSourceGroup> Sources { get; } = [];

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

    public bool HasFiles => _allFiles.Count > 0;
    public bool HasMultipleSources { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilter))]
    private string _filterText = "";

    public bool HasFilter => !string.IsNullOrWhiteSpace(FilterText);

    [ObservableProperty] private bool _hasNoMatches;

    /// <summary>Invoked when a row is activated; the host opens the diff for that file.</summary>
    public Action<GitFileChangeViewModel>? FileActivated { get; set; }

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
        KindBreakdownLabel = BuildKindBreakdown(_allFiles);

        RebuildGroups();
        HasMultipleSources = Sources.Count > 1;
    }

    partial void OnFilterTextChanged(string value) => RebuildGroups();

    [RelayCommand]
    private void ClearFilter() => FilterText = "";

    /// <summary>Collapses every source when any is expanded, otherwise expands them all.</summary>
    [RelayCommand]
    private void ToggleAllSources()
    {
        var collapse = Sources.Any(static s => s.IsExpanded);
        foreach (var source in Sources)
            source.IsExpanded = !collapse;
    }

    public void ClearSelection()
    {
        foreach (var file in _allFiles)
            file.IsSelected = false;
    }

    public void Select(GitFileChangeViewModel file)
    {
        ClearSelection();
        file.IsSelected = true;
    }

    private void RebuildGroups()
    {
        var filter = FilterText.Trim();
        var visible = string.IsNullOrEmpty(filter)
            ? _allFiles
            : _allFiles.Where(f => Matches(f, filter)).ToList();

        Sources.Clear();
        foreach (var source in BuildSources(visible))
            Sources.Add(source);

        HasNoMatches = HasFiles && visible.Count == 0;
    }

    private static bool Matches(GitFileChangeViewModel file, string filter)
        => file.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private IEnumerable<GitChangeSourceGroup> BuildSources(IReadOnlyCollection<GitFileChangeViewModel> files)
    {
        // Main repository first, then submodules by path so nesting reads top-down.
        var groups = files
            .GroupBy(f => f.Change.SubmodulePath ?? "", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key.Length == 0 ? 0 : 1)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

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

            yield return new GitChangeSourceGroup(name, isSubmodule, sourcePath, badge, BuildFolders(group));
        }
    }

    private static IEnumerable<GitChangeFolderGroup> BuildFolders(IEnumerable<GitFileChangeViewModel> files)
        => files
            // A pointer row keeps its parent-repo path (git needs it for the commit log), so key it at
            // the submodule root instead — otherwise a nested submodule invents a folder that
            // doesn't exist inside it (e.g. "vendor" showing up inside "vendor/lib").
            .GroupBy(
                static f => f.IsSubmodulePointer ? "" : f.RepoRelativeDirectory ?? "",
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(static g => g.Key.Length == 0 ? 0 : 1)
            .ThenBy(static g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static g => new GitChangeFolderGroup(
                g.Key,
                g.OrderBy(static f => f.FileName, StringComparer.OrdinalIgnoreCase)));

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

    private static string BuildKindBreakdown(IReadOnlyCollection<GitFileChangeViewModel> files)
    {
        var parts = new List<string>(4);
        void Add(int count, string label)
        {
            if (count > 0)
                parts.Add($"{count.ToString(CultureInfo.InvariantCulture)} {label}");
        }

        Add(files.Count(static f => f.Kind is GitChangeKind.Added or GitChangeKind.Untracked), Loc.Git_KindAdded);
        Add(files.Count(static f => f.Kind is GitChangeKind.Modified), Loc.Git_KindModified);
        Add(files.Count(static f => f.Kind is GitChangeKind.Deleted), Loc.Git_KindDeleted);
        Add(files.Count(static f => f.Kind is GitChangeKind.Renamed), Loc.Git_KindRenamed);
        Add(files.Count(static f => f.Kind is GitChangeKind.Submodule), Loc.Git_KindSubmodule);

        return string.Join(" · ", parts);
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
