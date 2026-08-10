using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class GitServiceWorktreeTests
{
    [Fact]
    public void FindRepoRoot_ReturnsRoot_ForSubfolder()
    {
        using var temp = new TempDir();
        var repo = Path.Combine(temp.Path, "repo");
        var sub = Path.Combine(repo, "apps", "web");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(sub);

        Assert.Equal(repo, GitService.FindRepoRoot(sub));
        Assert.Equal(repo, GitService.FindRepoRoot(repo));
    }

    [Fact]
    public void FindRepoRoot_ReturnsNull_OutsideRepo()
    {
        using var temp = new TempDir();
        var dir = Path.Combine(temp.Path, "not-a-repo", "child");
        Directory.CreateDirectory(dir);

        Assert.Null(GitService.FindRepoRoot(dir));
        Assert.Null(GitService.FindRepoRoot(null));
        Assert.Null(GitService.FindRepoRoot("   "));
    }

    [Fact]
    public void ResolveWorktreeWorkingDirectory_MapsProjectSubpathIntoWorktree()
    {
        using var temp = new TempDir();
        var repo = Path.Combine(temp.Path, "monorepo");
        var projectDir = Path.Combine(repo, "apps", "web");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(projectDir);

        // A worktree mirrors the repo tree, so the mapped subpath must exist under the worktree root.
        var worktreeRoot = Path.Combine(temp.Path, "monorepo-wt-lumi-abc");
        var mapped = Path.Combine(worktreeRoot, "apps", "web");
        Directory.CreateDirectory(mapped);

        Assert.Equal(mapped, GitService.ResolveWorktreeWorkingDirectory(worktreeRoot, projectDir));
    }

    [Fact]
    public void ResolveWorktreeWorkingDirectory_ReturnsRoot_WhenProjectIsRepoRoot()
    {
        using var temp = new TempDir();
        var repo = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var worktreeRoot = Path.Combine(temp.Path, "repo-wt-lumi-abc");
        Directory.CreateDirectory(worktreeRoot);

        Assert.Equal(worktreeRoot, GitService.ResolveWorktreeWorkingDirectory(worktreeRoot, repo));
    }

    [Fact]
    public void ResolveWorktreeWorkingDirectory_FallsBackToRoot_WhenMappedSubpathMissing()
    {
        using var temp = new TempDir();
        var repo = Path.Combine(temp.Path, "monorepo");
        var projectDir = Path.Combine(repo, "apps", "web");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(projectDir);

        // Worktree root exists but the mapped subpath does not — fall back to the root.
        var worktreeRoot = Path.Combine(temp.Path, "monorepo-wt-lumi-abc");
        Directory.CreateDirectory(worktreeRoot);

        Assert.Equal(worktreeRoot, GitService.ResolveWorktreeWorkingDirectory(worktreeRoot, projectDir));
    }

    [Fact]
    public void ResolveWorktreeWorkingDirectory_ReturnsRoot_WhenProjectDirNullOrNotInRepo()
    {
        using var temp = new TempDir();
        var worktreeRoot = Path.Combine(temp.Path, "wt");
        Directory.CreateDirectory(worktreeRoot);

        Assert.Equal(worktreeRoot, GitService.ResolveWorktreeWorkingDirectory(worktreeRoot, null));
        Assert.Equal(worktreeRoot, GitService.ResolveWorktreeWorkingDirectory(worktreeRoot, Path.Combine(temp.Path, "elsewhere")));
    }

    [Fact]
    public async Task CreateWorktreeAsync_CreatesSiblingOfGitRoot_ForSubfolderProject()
    {
        using var temp = new TempDir();
        var repo = Path.Combine(temp.Path, "monorepo");
        var projectDir = Path.Combine(repo, "apps", "web");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(repo, "README.md"), "# root");
        File.WriteAllText(Path.Combine(projectDir, "index.ts"), "export const x = 1;");

        Git(repo, "init -q");
        Git(repo, "config user.email test@example.com");
        Git(repo, "config user.name Test");
        Git(repo, "add -A");
        Git(repo, "commit -q -m initial");

        var worktreeRoot = await GitService.CreateWorktreeAsync(projectDir, "lumi/test-subfolder");

        Assert.NotNull(worktreeRoot);
        try
        {
            // Sibling of the git root, never nested inside the repo.
            Assert.Equal(temp.Path, Path.GetDirectoryName(worktreeRoot!.TrimEnd(Path.DirectorySeparatorChar)));
            Assert.False(worktreeRoot.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Assert.True(Directory.Exists(worktreeRoot));

            // The project's subpath is reproduced inside the worktree.
            var mapped = GitService.ResolveWorktreeWorkingDirectory(worktreeRoot, projectDir);
            Assert.Equal(Path.Combine(worktreeRoot, "apps", "web"), mapped);
            Assert.True(Directory.Exists(mapped));
        }
        finally
        {
            await GitService.RemoveWorktreeAsync(repo, worktreeRoot!);
        }
    }

    [Fact]
    public async Task RemoveWorktreeIfCleanAsync_PreservesDetachedUnreferencedCommits()
    {
        using var temp = new TempDir();
        var repo = Path.Combine(temp.Path, "repo");
        var worktree = Path.Combine(temp.Path, "repo-detached");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "README.md"), "# root");
        Git(repo, "init -q");
        Git(repo, "config user.email test@example.com");
        Git(repo, "config user.name Test");
        Git(repo, "add -A");
        Git(repo, "commit -q -m initial");
        Git(repo, $"worktree add --detach \"{worktree}\"");
        try
        {
            File.WriteAllText(Path.Combine(worktree, "keep.txt"), "committed work");
            Git(worktree, "add -A");
            Git(worktree, "commit -q -m keep");

            var removed = await GitService.RemoveWorktreeIfCleanAsync(repo, worktree);

            Assert.False(removed);
            Assert.True(Directory.Exists(worktree));
        }
        finally
        {
            if (Directory.Exists(worktree))
                await GitService.RemoveWorktreeAsync(repo, worktree);
        }
    }

    [Fact]
    public async Task RunGit_DoesNotDeadlock_UnderConcurrentInvocations()
    {
        using var temp = new TempDir();
        var repo = Path.Combine(temp.Path, "monorepo");
        var projectDir = Path.Combine(repo, "apps", "web");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(repo, "README.md"), "# root");
        File.WriteAllText(Path.Combine(projectDir, "index.ts"), "export const x = 1;");

        Git(repo, "init -q");
        Git(repo, "config user.email test@example.com");
        Git(repo, "config user.name Test");
        Git(repo, "add -A");
        Git(repo, "commit -q -m initial");

        // Fire the exact refresh triad (branch + status + worktree-list) many times
        // concurrently. Before the serialization/bounded-drain fix this deadlocked: with
        // multiple redirected git processes starting at once, the Git-for-Windows launcher's
        // grandchild git could inherit a sibling's stdout pipe write-handle, so that sibling's
        // ReadToEndAsync never reached EOF and hung forever. All invocations must now complete
        // well within the timeout.
        var tasks = new System.Collections.Generic.List<Task>();
        for (int i = 0; i < 30; i++)
        {
            tasks.Add(GitService.GetCurrentBranchAsync(projectDir));
            tasks.Add(GitService.GetChangedFilesAsync(projectDir));
            tasks.Add(GitService.ListWorktreeInfoAsync(projectDir));
        }

        var all = Task.WhenAll(tasks);
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.True(ReferenceEquals(finished, all),
            "Concurrent git invocations deadlocked (did not complete within 60s).");
        await all; // surface any exceptions
    }

    private static void Git(string dir, string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lumi-gitwt-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
