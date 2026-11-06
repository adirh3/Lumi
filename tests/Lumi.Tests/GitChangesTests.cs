using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Localization;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Covers the changes-island data model: submodule expansion (git reports a dirty submodule as one
/// opaque entry) and the source/folder grouping the island renders.
/// </summary>
public sealed class GitChangesTests
{
    [Fact]
    public async Task CancellableStatusRefreshDoesNotRewriteTheIndex()
    {
        using var temp = new TempDir();
        var trackedFile = Path.Combine(temp.Path, "tracked.txt");
        var modifiedFile = Path.Combine(temp.Path, "modified.txt");
        File.WriteAllText(trackedFile, "unchanged\n");
        File.WriteAllText(modifiedFile, "before\n");
        InitRepo(temp.Path);

        var indexPath = Path.Combine(temp.Path, ".git", "index");
        var originalIndex = File.ReadAllBytes(indexPath);
        File.SetLastWriteTimeUtc(trackedFile, File.GetLastWriteTimeUtc(trackedFile).AddMinutes(-5));
        File.WriteAllText(modifiedFile, "after\nchanged\n");
        using var cancellation = new CancellationTokenSource();

        var changes = await GitService.GetChangedFilesAsync(temp.Path, cancellation.Token);

        var change = Assert.Single(changes);
        Assert.Equal("modified.txt", change.RelativePath);
        Assert.Equal(2, change.LinesAdded);
        Assert.Equal(1, change.LinesRemoved);
        Assert.Equal(originalIndex, File.ReadAllBytes(indexPath));
        Assert.False(File.Exists(indexPath + ".lock"));
    }

    [Fact]
    public async Task CancellableDetachedBranchRefreshDoesNotRewriteTheIndex()
    {
        using var temp = new TempDir();
        var trackedFile = Path.Combine(temp.Path, "tracked.txt");
        File.WriteAllText(trackedFile, "unchanged\n");
        InitRepo(temp.Path);
        Git(temp.Path, "checkout -q --detach");

        var indexPath = Path.Combine(temp.Path, ".git", "index");
        var originalIndex = File.ReadAllBytes(indexPath);
        File.SetLastWriteTimeUtc(trackedFile, File.GetLastWriteTimeUtc(trackedFile).AddMinutes(-5));
        using var cancellation = new CancellationTokenSource();

        var branch = await GitService.GetCurrentBranchAsync(temp.Path, cancellation.Token);

        Assert.NotNull(branch);
        Assert.StartsWith("Detached ", branch);
        Assert.Equal(originalIndex, File.ReadAllBytes(indexPath));
        Assert.False(File.Exists(indexPath + ".lock"));
    }

    [Fact]
    public async Task GetChangedFilesAsync_ExpandsSubmoduleFiles()
    {
        using var temp = new TempDir();
        var (parent, submodulePath) = CreateRepoWithSubmodule(temp.Path);
        if (parent is null || submodulePath is null)
            return; // git refused file-protocol submodules on this host

        File.WriteAllText(Path.Combine(submodulePath, "lib.txt"), "one\ntwo\nthree\n");
        File.WriteAllText(Path.Combine(submodulePath, "added.txt"), "new\n");
        File.WriteAllText(Path.Combine(parent, "app.txt"), "changed\n");
        ConfigureAdverseSubmoduleStatusSettings(parent);

        var changes = await GitService.GetChangedFilesAsync(parent);

        // The parent's own file is still reported normally.
        Assert.Contains(changes, c => c.RelativePath == "app.txt" && !c.IsSubmoduleFile);

        // Files inside the submodule are surfaced individually, prefixed with the submodule path.
        var nested = changes.Single(c => c.RelativePath == "sub/lib.txt");
        Assert.True(nested.IsSubmoduleFile);
        Assert.Equal("sub", nested.SubmodulePath);
        Assert.Equal("sub", nested.SubmoduleName);
        Assert.Equal("lib.txt", nested.RepoRelativePath);
        Assert.Equal(Path.Combine(submodulePath, "lib.txt"), nested.FullPath);
        Assert.Equal(GitChangeKind.Modified, nested.Kind);

        Assert.Contains(changes, c => c.RelativePath == "sub/added.txt" && c.Kind == GitChangeKind.Untracked);

        // The opaque " M sub" entry must not survive as a file row.
        Assert.DoesNotContain(changes, c => c.RelativePath == "sub" && c.Kind == GitChangeKind.Modified);
    }

    [Fact]
    public async Task GetChangedFilesAsync_ReportsSubmodulePointerMove()
    {
        using var temp = new TempDir();
        var (parent, submodulePath) = CreateRepoWithSubmodule(temp.Path);
        if (parent is null || submodulePath is null)
            return;

        // Commit inside the submodule so the superproject's recorded commit no longer matches.
        File.WriteAllText(Path.Combine(submodulePath, "lib.txt"), "moved\n");
        Git(submodulePath, "add -A");
        Git(submodulePath, "commit -q -m second");
        ConfigureAdverseSubmoduleStatusSettings(parent);

        var changes = await GitService.GetChangedFilesAsync(parent);

        var pointer = Assert.Single(changes, c => c.Kind == GitChangeKind.Submodule);
        Assert.Equal("sub", pointer.RelativePath);
        Assert.Equal("sub", pointer.SubmodulePath);
        Assert.Equal("S", pointer.KindIcon);
    }

    [Fact]
    public async Task GetChangedFilesAsync_ResolvesFullPathsFromRepoRoot_WhenRunFromSubfolder()
    {
        using var temp = new TempDir();
        var repo = Path.Combine(temp.Path, "monorepo");
        var projectDir = Path.Combine(repo, "apps", "web");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(repo, "README.md"), "# root\n");
        InitRepo(repo);

        File.WriteAllText(Path.Combine(projectDir, "index.ts"), "export const x = 1;\n");

        // git status emits repo-root-relative paths even when invoked from a subfolder.
        var changes = await GitService.GetChangedFilesAsync(projectDir);

        var change = Assert.Single(changes);
        Assert.Equal("apps/web/index.ts", change.RelativePath);
        Assert.Equal(Path.Combine(projectDir, "index.ts"), change.FullPath);
        Assert.True(File.Exists(change.FullPath));
    }

    [Fact]
    public async Task RenamedFileIsListedAtItsNewPath()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "tools"));
        File.WriteAllText(Path.Combine(temp.Path, "tools", "old.cs"), "one\ntwo\n");
        InitRepo(temp.Path);
        Assert.True(TryGit(temp.Path, "mv tools/old.cs tools/new.cs", out _));

        var change = Assert.Single(await GitService.GetChangedFilesAsync(temp.Path));

        Assert.Equal(GitChangeKind.Renamed, change.Kind);
        Assert.Equal("tools/new.cs", change.RelativePath);
        Assert.Equal("tools/old.cs", change.OriginalRepoRelativePath);
        Assert.Equal("new.cs", change.FileName);
        Assert.Equal("tools", change.RepoRelativeDirectory);
    }

    [Fact]
    public void GitChangesViewModel_GroupsBySourceThenFolderTree()
    {
        var vm = new GitChangesViewModel(
            [
                Change("src/Lumi/Views/ChatView.axaml", GitChangeKind.Modified, added: 10, removed: 4),
                Change("src/Lumi/Views/DiffView.axaml", GitChangeKind.Modified, added: 2, removed: 1),
                Change("README.md", GitChangeKind.Added, added: 5),
                SubmoduleChange("Strata/src/Controls/Button.cs", "Strata", "src/Controls/Button.cs", added: 7, removed: 3),
            ],
            rootPath: @"C:\repos\Lumi",
            branch: "main",
            isWorktree: false);

        Assert.Equal(2, vm.Sources.Count);

        var main = vm.Sources[0];
        Assert.Equal("Lumi", main.Name);
        Assert.False(main.IsSubmodule);
        // A chain of folders that only hold one folder is one row, as editors compact it.
        var views = Assert.Single(main.Folders);
        Assert.Equal("src/Lumi/Views", views.Name);
        Assert.Equal("src/Lumi/", views.ParentLabel);
        Assert.Equal("Views", views.LeafLabel);
        Assert.Equal(2, views.Files.Count);
        Assert.Equal("ChatView.axaml", views.Files[0].FileName);
        Assert.Equal("+12", views.AddedCell);
        Assert.Equal("−5", views.RemovedCell);
        Assert.Equal("README.md", Assert.Single(main.Files).FileName);

        var submodule = vm.Sources[1];
        Assert.Equal("Strata", submodule.Name);
        Assert.True(submodule.IsSubmodule);
        // Folders inside a submodule are relative to the submodule, not the parent repo.
        Assert.Equal("src/Controls", Assert.Single(submodule.Folders).FullPath);

        Assert.Equal("3", main.FileCountCompact);
        Assert.Equal("3 files changed", main.FileCountLabel);
        Assert.Equal("1", submodule.FileCountCompact);

        Assert.True(vm.HasMultipleSources);
        Assert.Equal("+24", vm.TotalAddedLabel);
        Assert.Equal("−8", vm.TotalRemovedLabel);
        Assert.True(vm.HasTotalStats);
        Assert.True(vm.AddedBarWidth > vm.RemovedBarWidth);
        Assert.Equal("main", vm.BranchLabel);

        // Rows: each source's header, its folders before its files, children one level deeper.
        Assert.Equal(
            ["Lumi:0", "src/Lumi/Views:1", "ChatView.axaml:2", "DiffView.axaml:2", "README.md:1",
             "Strata:0", "src/Controls:1", "Button.cs:2"],
            vm.Rows.Select(Describe));
    }

    [Fact]
    public void GitChangesViewModel_OneSourceShowsNoHeaderRowAndSplitsFoldersWhereTheyBranch()
    {
        var vm = new GitChangesViewModel(
            [
                Change("src/app/a.cs", GitChangeKind.Modified, added: 1),
                Change("src/app/b.cs", GitChangeKind.Modified, added: 1),
                Change("src/lib/c.cs", GitChangeKind.Modified, added: 1),
            ],
            rootPath: @"C:\repos\Lumi",
            branch: null,
            isWorktree: false);

        Assert.False(vm.HasMultipleSources);
        Assert.Equal(
            ["src:0", "app:1", "a.cs:2", "b.cs:2", "lib:1", "c.cs:2"],
            vm.Rows.Select(Describe));
    }

    [Fact]
    public void GitChangesViewModel_FoldingAFolderRemovesOnlyItsRows()
    {
        var vm = new GitChangesViewModel(
            [
                Change("src/app/a.cs", GitChangeKind.Modified, added: 1),
                Change("src/lib/c.cs", GitChangeKind.Modified, added: 1),
                Change("README.md", GitChangeKind.Modified, added: 1),
            ],
            rootPath: @"C:\repos\Lumi",
            branch: null,
            isWorktree: false);
        var rows = vm.Rows;
        var app = vm.Sources[0].Folders[0].Folders[0];
        Assert.Equal("app", app.Name);

        app.ToggleCommand.Execute(null);

        Assert.False(app.IsExpanded);
        Assert.Same(rows, vm.Rows);
        Assert.Equal(["src:0", "app:1", "lib:1", "c.cs:2", "README.md:0"], vm.Rows.Select(Describe));
        Assert.True(vm.CanCollapseAll);

        app.ToggleCommand.Execute(null);
        Assert.Equal(["src:0", "app:1", "a.cs:2", "lib:1", "c.cs:2", "README.md:0"], vm.Rows.Select(Describe));

        vm.ToggleAllCommand.Execute(null);
        Assert.False(vm.CanCollapseAll);
        Assert.Equal(Loc.Git_ExpandAll, vm.ToggleAllLabel);
        Assert.Equal(["src:0", "README.md:0"], vm.Rows.Select(Describe));

        vm.ToggleAllCommand.Execute(null);
        Assert.True(vm.CanCollapseAll);
        Assert.Equal(6, vm.Rows.Count);
    }

    [Fact]
    public void GitChangesViewModel_FilterNarrowsTheTreeOpensMatchesAndKeepsFoldingOnClear()
    {
        var vm = new GitChangesViewModel(
            [
                Change("src/a.cs", GitChangeKind.Modified, added: 1),
                Change("docs/b.md", GitChangeKind.Modified, added: 1),
            ],
            rootPath: @"C:\repos\Lumi",
            branch: null,
            isWorktree: false);

        vm.ToggleAllCommand.Execute(null);
        Assert.Equal(["docs:0", "src:0"], vm.Rows.Select(Describe));

        vm.FilterText = "docs";
        Assert.Equal(["docs:0", "b.md:1"], vm.Rows.Select(Describe));
        Assert.False(vm.HasNoMatches);

        vm.FilterText = "nothing-here";
        Assert.Empty(vm.Sources);
        Assert.Empty(vm.Rows);
        Assert.True(vm.HasNoMatches);

        vm.ClearFilterCommand.Execute(null);
        Assert.Equal("", vm.FilterText);
        Assert.False(vm.HasNoMatches);
        Assert.Equal(["docs:0", "src:0"], vm.Rows.Select(Describe));
    }

    [Fact]
    public void GitChangesViewModel_KindChipsFilterToOneKind()
    {
        var vm = new GitChangesViewModel(
            [
                Change("src/a.cs", GitChangeKind.Modified, added: 1),
                Change("src/new.cs", GitChangeKind.Untracked, added: 3),
                Change("src/old.cs", GitChangeKind.Deleted, removed: 2),
            ],
            rootPath: @"C:\repos\Lumi",
            branch: null,
            isWorktree: false);

        Assert.Equal(["1 added", "1 modified", "1 deleted"], vm.KindFilters.Select(static k => k.Label));
        Assert.Equal("1 added · 1 modified · 1 deleted", vm.KindBreakdownLabel);

        var added = vm.KindFilters[0];
        added.ToggleCommand.Execute(null);
        Assert.True(added.IsActive);
        Assert.Equal(["src:0", "new.cs:1"], vm.Rows.Select(Describe));

        vm.KindFilters[2].ToggleCommand.Execute(null);
        Assert.False(added.IsActive);
        Assert.Equal(["src:0", "old.cs:1"], vm.Rows.Select(Describe));

        vm.KindFilters[2].ToggleCommand.Execute(null);
        Assert.Null(vm.ActiveKind);
        Assert.Equal(4, vm.Rows.Count);
    }

    [Fact]
    public void GitChangesViewModel_SelectionStepsThroughFilesAndUnfoldsTheirFolders()
    {
        var vm = new GitChangesViewModel(
            [
                Change("src/a.cs", GitChangeKind.Modified, added: 1),
                Change("src/b.cs", GitChangeKind.Modified, added: 1),
                Change("z.md", GitChangeKind.Modified, added: 1),
            ],
            rootPath: @"C:\repos\Lumi",
            branch: null,
            isWorktree: true);

        var a = vm.Sources[0].Folders[0].Files[0];
        var b = vm.AdjacentFile(a, 1)!;
        Assert.Equal("b.cs", b.FileName);
        Assert.Equal("z.md", vm.AdjacentFile(b, 1)!.FileName);
        Assert.Null(vm.AdjacentFile(a, -1));
        Assert.Equal((2, 3), vm.PositionOf(b));

        vm.ToggleAllCommand.Execute(null);
        Assert.Equal(-1, vm.IndexOfRow(b));

        vm.Select(b);
        Assert.True(b.IsSelected);
        Assert.False(a.IsSelected);
        Assert.Equal(2, vm.IndexOfRow(b));

        vm.ClearSelection();
        Assert.False(b.IsSelected);
    }

    [Fact]
    public void GitChangesViewModel_NestedSubmodulePointerSitsAtSubmoduleRoot()
    {
        // The pointer row keeps its parent-repo path so git can still resolve the commit log, so the
        // island must not fold it into a folder named after the submodule's parent directory.
        var vm = new GitChangesViewModel(
            [
                SubmodulePointerChange("vendor/lib"),
                SubmoduleChange("vendor/lib/src/Widget.cs", "vendor/lib", "src/Widget.cs", added: 3),
            ],
            rootPath: @"C:\repos\App",
            branch: "main",
            isWorktree: false);

        var submodule = Assert.Single(vm.Sources);
        Assert.True(submodule.IsSubmodule);
        Assert.Equal("lib", submodule.Name);

        Assert.Equal("lib", Assert.Single(submodule.Files).FileName);
        Assert.Equal("src", Assert.Single(submodule.Folders).FullPath);
        Assert.Equal(["src:0", "Widget.cs:1", "lib:0"], vm.Rows.Select(Describe));
    }

    private static string Describe(GitChangeTreeRow row) => row switch
    {
        GitChangeSourceGroup source => $"{source.Name}:{row.Depth}",
        GitChangeFolderNode folder => $"{folder.Name}:{row.Depth}",
        GitFileChangeViewModel file => $"{file.FileName}:{row.Depth}",
        _ => "?",
    };

    private static GitFileChange Change(string relativePath, GitChangeKind kind, int added = 0, int removed = 0)
        => new()
        {
            RelativePath = relativePath,
            FullPath = Path.Combine(@"C:\repos\Lumi", relativePath.Replace('/', Path.DirectorySeparatorChar)),
            RepoRoot = @"C:\repos\Lumi",
            RepoRelativePath = relativePath,
            Kind = kind,
            StatusCode = " M",
            LinesAdded = added,
            LinesRemoved = removed,
        };

    private static GitFileChange SubmoduleChange(
        string relativePath,
        string submodulePath,
        string repoRelativePath,
        int added = 0,
        int removed = 0)
        => new()
        {
            RelativePath = relativePath,
            FullPath = Path.Combine(@"C:\repos\Lumi", relativePath.Replace('/', Path.DirectorySeparatorChar)),
            RepoRoot = Path.Combine(@"C:\repos\Lumi", submodulePath),
            RepoRelativePath = repoRelativePath,
            SubmodulePath = submodulePath,
            Kind = GitChangeKind.Modified,
            StatusCode = " M",
            LinesAdded = added,
            LinesRemoved = removed,
        };

    /// <summary>Mirrors what <c>GitFileChange.AsSubmodulePointer()</c> produces for a dirty submodule.</summary>
    private static GitFileChange SubmodulePointerChange(string submodulePath)
        => new()
        {
            RelativePath = submodulePath,
            FullPath = Path.Combine(@"C:\repos\App", submodulePath.Replace('/', Path.DirectorySeparatorChar)),
            RepoRoot = @"C:\repos\App",
            RepoRelativePath = submodulePath,
            SubmodulePath = submodulePath,
            Kind = GitChangeKind.Submodule,
            StatusCode = " M",
        };

    /// <summary>Creates a parent repo with a real submodule at "sub", or (null, null) when the host
    /// git refuses local-file submodule transport.</summary>
    private static (string? Parent, string? Submodule) CreateRepoWithSubmodule(string root)
    {
        var origin = Path.Combine(root, "origin");
        Directory.CreateDirectory(origin);
        File.WriteAllText(Path.Combine(origin, "lib.txt"), "one\ntwo\n");
        InitRepo(origin);

        var parent = Path.Combine(root, "parent");
        Directory.CreateDirectory(parent);
        File.WriteAllText(Path.Combine(parent, "app.txt"), "app\n");
        InitRepo(parent);

        var originUrl = origin.Replace('\\', '/');
        if (!TryGit(parent, $"-c protocol.file.allow=always submodule add -q \"{originUrl}\" sub", out _))
            return (null, null);

        var submodule = Path.Combine(parent, "sub");
        if (!Directory.Exists(Path.Combine(submodule, ".git")) && !File.Exists(Path.Combine(submodule, ".git")))
            return (null, null);

        ConfigureIdentity(submodule);
        Git(parent, "add -A");
        Git(parent, "commit -q -m add-submodule");
        return (parent, submodule);
    }

    private static void InitRepo(string dir)
    {
        Git(dir, "init -q");
        ConfigureIdentity(dir);
        Git(dir, "add -A");
        Git(dir, "commit -q -m initial");
    }

    private static void ConfigureIdentity(string dir)
    {
        Git(dir, "config user.email test@example.com");
        Git(dir, "config user.name Test");
    }

    private static void ConfigureAdverseSubmoduleStatusSettings(string parent)
    {
        Git(parent, "config submodule.recurse true");
        Git(parent, "config submodule.sub.ignore all");
    }

    private static void Git(string dir, string args)
    {
        if (!TryGit(dir, args, out var error))
            throw new InvalidOperationException($"git {args} failed in {dir}: {error}");
    }

    private static bool TryGit(string dir, string args, out string error)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // The fixture must not depend on a developer or runner supplying identity or submodule settings.
        psi.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(dir, ".lumi-test-global-gitconfig");
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";

        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);
        error = stderrTask.Result.Trim();
        return p.ExitCode == 0;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lumi-gitchanges-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { ForceDelete(Path); }
            catch { /* best effort */ }
        }

        private static void ForceDelete(string path)
        {
            // Git marks objects read-only, which blocks a plain recursive delete on Windows.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
    }
}
