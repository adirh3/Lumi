using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    public void GitChangesViewModel_GroupsBySourceThenFolder()
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
        // Root-level files sort before nested folders.
        Assert.Equal(2, main.Folders.Count);
        Assert.Equal("src/Lumi/Views", main.Folders[1].FolderLabel);
        Assert.Equal(2, main.Folders[1].Files.Count);
        Assert.Equal("ChatView.axaml", main.Folders[1].Files[0].FileName);

        var submodule = vm.Sources[1];
        Assert.Equal("Strata", submodule.Name);
        Assert.True(submodule.IsSubmodule);
        // Folder labels inside a submodule are relative to the submodule, not the parent repo.
        Assert.Equal("src/Controls", submodule.Folders[0].FolderLabel);

        // The header shows a compact count so a narrow island still has room for the name.
        Assert.Equal("3", main.FileCountCompact);
        Assert.Equal("3 files changed", main.FileCountLabel);
        Assert.Equal("1", submodule.FileCountCompact);

        Assert.True(vm.HasMultipleSources);
        Assert.Equal("+24", vm.TotalAddedLabel);
        Assert.Equal("−8", vm.TotalRemovedLabel);
        Assert.True(vm.HasTotalStats);
        Assert.True(vm.AddedBarWidth > vm.RemovedBarWidth);
        Assert.Equal("main", vm.BranchLabel);
    }

    [Fact]
    public void GitChangesViewModel_FilterNarrowsGroupsAndReportsNoMatches()
    {
        var vm = new GitChangesViewModel(
            [
                Change("src/a.cs", GitChangeKind.Modified, added: 1),
                Change("docs/b.md", GitChangeKind.Modified, added: 1),
            ],
            rootPath: @"C:\repos\Lumi",
            branch: null,
            isWorktree: false);

        vm.FilterText = "docs";
        var source = Assert.Single(vm.Sources);
        var folder = Assert.Single(source.Folders);
        Assert.Equal("b.md", Assert.Single(folder.Files).FileName);
        Assert.False(vm.HasNoMatches);

        vm.FilterText = "nothing-here";
        Assert.Empty(vm.Sources);
        Assert.True(vm.HasNoMatches);

        vm.ClearFilterCommand.Execute(null);
        Assert.Equal("", vm.FilterText);
        Assert.False(vm.HasNoMatches);
        Assert.Equal(2, vm.Sources.Single().Folders.Count);
    }

    [Fact]
    public void GitChangesViewModel_SelectionAndToggleAllTrackIslandNavigation()
    {
        var vm = new GitChangesViewModel(
            [Change("src/a.cs", GitChangeKind.Modified, added: 1)],
            rootPath: @"C:\repos\Lumi",
            branch: null,
            isWorktree: true);

        var file = vm.Sources[0].Folders[0].Files[0];
        vm.Select(file);
        Assert.True(file.IsSelected);

        vm.ClearSelection();
        Assert.False(file.IsSelected);

        Assert.True(vm.Sources[0].IsExpanded);
        vm.ToggleAllSourcesCommand.Execute(null);
        Assert.False(vm.Sources[0].IsExpanded);
        vm.ToggleAllSourcesCommand.Execute(null);
        Assert.True(vm.Sources[0].IsExpanded);
    }

    [Fact]
    public void GitChangesViewModel_NestedSubmodulePointerGroupsAtSubmoduleRoot()
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

        Assert.Equal(2, submodule.Folders.Count);
        Assert.Equal(Loc.Git_RepositoryRoot, submodule.Folders[0].FolderLabel);
        Assert.Equal("lib", Assert.Single(submodule.Folders[0].Files).FileName);
        Assert.Equal("src", submodule.Folders[1].FolderLabel);
    }

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
