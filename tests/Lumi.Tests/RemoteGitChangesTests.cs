using System.Diagnostics;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.Services.Remote;
using Xunit;

namespace Lumi.Tests;

public sealed class RemoteGitChangesTests
{
    [Fact]
    public async Task ExplicitProjectAndWorktreeScope_NeverFallsBackToAnotherCheckout()
    {
        using var temp = new Repository();
        await temp.Git("init");
        var project = new Project { Name = "Project", WorkingDirectory = temp.Path };
        var chat = new Chat { ProjectId = project.Id };
        var store = new DataStore(new AppData { Projects = [project], Chats = [chat] });
        var local = RemoteGitChangesService.CaptureScope(store, chat);
        Assert.Equal(temp.Path, local.Repository);
        Assert.Contains("Project checkout", local.Label);
        Assert.Equal(chat.Id, local.ChatId);
        Assert.Equal(project.Id, local.ProjectId);

        var worktree = System.IO.Path.Combine(temp.Path, "other-worktree");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(System.IO.Path.Combine(worktree, ".git"), "gitdir: not-used-for-capture");
        chat.WorktreePath = worktree;
        var linked = RemoteGitChangesService.CaptureScope(store, chat);
        Assert.Equal(worktree, linked.Repository);
        Assert.True(linked.UsesWorktree);
        Assert.NotEqual(local.ScopeId, linked.ScopeId);

        chat.WorktreePath = worktree + "-missing";
        var missing = await RemoteGitChangesService.GetChangesAsync(
            RemoteGitChangesService.CaptureScope(store, chat), CancellationToken.None);
        Assert.False(missing.IsRepository);
        Assert.Empty(missing.Files);
        chat.WorktreePath = null;
        chat.ProjectId = Guid.NewGuid();
        Assert.Null(RemoteGitChangesService.CaptureScope(store, chat).Repository);
    }

    [Fact]
    public async Task Diff_ShowsStagedAndWorkingChanges_UntrackedDeletedRenamedAndBinary()
    {
        using var repo = new Repository();
        await repo.Git("init");
        File.WriteAllText(repo.File("tracked.txt"), "original\n");
        File.WriteAllText(repo.File("deleted.txt"), "delete me\n");
        File.WriteAllText(repo.File("old name.txt"), "rename me\n");
        await repo.Git("add", ".");
        await repo.Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "fixture");
        File.WriteAllText(repo.File("tracked.txt"), "staged\n");
        await repo.Git("add", "tracked.txt");
        File.WriteAllText(repo.File("tracked.txt"), "working\n");
        File.Delete(repo.File("deleted.txt"));
        await repo.Git("mv", "old name.txt", "new name.txt");
        File.WriteAllText(repo.File("新 file.txt"), "new contents\n");
        File.WriteAllBytes(repo.File("binary.dat"), [0, 1, 0, 255]);

        var changes = await GitService.GetReadOnlyChangesAsync(repo.Path, 200, CancellationToken.None);
        Assert.Contains(changes.Files, f => f.RelativePath == "deleted.txt" && f.Kind == GitChangeKind.Deleted);
        Assert.Contains(changes.Files, f => f.RelativePath == "new name.txt" && f.Kind == GitChangeKind.Renamed);
        Assert.Contains(changes.Files, f => f.RelativePath == "新 file.txt" && f.Kind == GitChangeKind.Untracked);
        var modified = await repo.Diff("tracked.txt");
        Assert.Contains("-original", modified.Text);
        Assert.Contains("+staged", modified.Text);
        Assert.Contains("+working", modified.Text);
        Assert.Contains("-delete me", (await repo.Diff("deleted.txt")).Text);
        Assert.Contains("+new contents", (await repo.Diff("新 file.txt")).Text);
        Assert.NotEmpty((await repo.Diff("new name.txt")).Text);
        Assert.Empty((await repo.Diff("binary.dat")).Text);
        // Read-only inspection did not refresh or change staged content.
        Assert.Contains("staged", await repo.Git("show", ":tracked.txt"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task RenameDiff_PreservesBothNamesAndCountsOnlyActualEdits(bool edit, bool stageEdit)
    {
        using var repo = new Repository();
        await repo.Git("init");
        const string oldPath = "old name [1].txt";
        const string newPath = "new name [2].txt";
        const string original = "old first line\nsecond line\nthird line\nfourth line\nfifth line\nsixth line\n";
        File.WriteAllText(repo.File(oldPath), original);
        await repo.Git("add", ".");
        await repo.Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "fixture");
        await repo.Git("mv", oldPath, newPath);
        if (edit)
        {
            File.WriteAllText(repo.File(newPath), original.Replace("old first line", "changed first line"));
            if (stageEdit)
                await repo.Git("add", newPath);
        }

        var changes = await GitService.GetReadOnlyChangesAsync(repo.Path, 200, CancellationToken.None);
        var file = Assert.Single(changes.Files);
        Assert.Equal(GitChangeKind.Renamed, file.Kind);
        Assert.Equal(oldPath, file.OriginalRepoRelativePath);
        Assert.Equal(newPath, file.RepoRelativePath);
        var scope = new RemoteGitScope(Guid.NewGuid(), null, "Test", "scope", false, repo.Path);
        var diff = await RemoteGitChangesService.GetDiffAsync(scope, newPath, CancellationToken.None);
        Assert.Contains($"rename from {oldPath}", diff.UnifiedDiff);
        Assert.Contains($"rename to {newPath}", diff.UnifiedDiff);
        Assert.DoesNotContain("new file mode", diff.UnifiedDiff);
        Assert.Equal(edit ? 1 : 0, diff.LinesAdded);
        Assert.Equal(edit ? 1 : 0, diff.LinesRemoved);
        if (edit)
        {
            Assert.Contains("-old first line", diff.UnifiedDiff);
            Assert.Contains("+changed first line", diff.UnifiedDiff);
        }
        else
        {
            Assert.Contains("similarity index 100%", diff.UnifiedDiff);
            Assert.DoesNotContain("@@", diff.UnifiedDiff);
        }
    }

    [Theory]
    [InlineData("module")]
    [InlineData("deps/module")]
    public async Task RenameInsideNestedSubmodule_UsesOwningRepoPathsAndPreservesOuterPrefix(string modulePath)
    {
        using var repo = new Repository();
        var module = Path.GetFullPath(repo.File(modulePath));
        var inner = Path.Combine(module, "inner");
        Directory.CreateDirectory(inner);
        await repo.Git("init");
        await repo.Git("-C", module, "init");
        await repo.Git("-C", inner, "init");
        File.WriteAllText(Path.Combine(inner, "old.txt"), "unchanged content\n");
        await repo.Git("-C", inner, "add", ".");
        await repo.Git("-C", inner, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "inner fixture");
        // Embedded repositories are recorded as gitlinks, giving the same nested status expansion
        // as initialized submodules without fetching or configuring a remote.
        await repo.Git("-C", module, "add", "inner");
        await repo.Git("-C", module, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "module fixture");
        await repo.Git("add", modulePath);
        await repo.Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "outer fixture");
        await repo.Git("-C", inner, "mv", "old.txt", "new.txt");

        var changes = await GitService.GetReadOnlyChangesAsync(repo.Path, 200, CancellationToken.None);
        var file = Assert.Single(changes.Files, change => change.Kind == GitChangeKind.Renamed);
        Assert.Equal($"{modulePath}/inner/new.txt", file.RelativePath);
        Assert.Equal($"{modulePath}/inner", file.SubmodulePath);
        Assert.Equal(inner, file.RepoRoot);
        Assert.Equal("new.txt", file.RepoRelativePath);
        Assert.Equal("old.txt", file.OriginalRepoRelativePath);
        var scope = new RemoteGitScope(Guid.NewGuid(), null, "Test", "scope", false, repo.Path);
        var diff = await RemoteGitChangesService.GetDiffAsync(scope, file.RelativePath, CancellationToken.None);
        Assert.Equal($"{modulePath}/inner/new.txt", diff.Path);
        Assert.Contains("rename from old.txt", diff.UnifiedDiff);
        Assert.Contains("rename to new.txt", diff.UnifiedDiff);
        Assert.Equal(0, diff.LinesAdded);
        Assert.Equal(0, diff.LinesRemoved);
    }

    [Fact]
    public async Task StagedDeletionAndRecreationAreOneEntryWithBothDiffs()
    {
        using var repo = new Repository();
        await repo.Git("init");
        File.WriteAllText(repo.File("same.txt"), "original\nold tail\n");
        await repo.Git("add", "same.txt");
        await repo.Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "fixture");
        await repo.Git("rm", "same.txt");
        File.WriteAllText(repo.File("same.txt"), "recreated\nnew tail\n");

        var files = await GitService.GetReadOnlyChangesAsync(repo.Path, 200, CancellationToken.None);
        var file = Assert.Single(files.Files);
        Assert.Equal("same.txt", file.RelativePath);
        Assert.Equal("D?", file.StatusCode);
        var limited = await GitService.GetReadOnlyChangesAsync(repo.Path, 1, CancellationToken.None);
        Assert.Equal("D?", Assert.Single(limited.Files).StatusCode);
        Assert.False(limited.Truncated);

        var scope = new RemoteGitScope(Guid.NewGuid(), null, "Test", "scope", false, repo.Path);
        var summary = await RemoteGitChangesService.GetChangesAsync(scope, CancellationToken.None);
        Assert.Equal("Recreated", Assert.Single(summary.Files).Kind);
        var diff = await RemoteGitChangesService.GetDiffAsync(scope, "same.txt", CancellationToken.None);
        Assert.Contains("Staged changes", diff.UnifiedDiff);
        Assert.Contains("Working tree changes", diff.UnifiedDiff);
        Assert.Contains("-original", diff.UnifiedDiff);
        Assert.Contains("-old tail", diff.UnifiedDiff);
        Assert.Contains("+recreated", diff.UnifiedDiff);
        Assert.Contains("+new tail", diff.UnifiedDiff);
        Assert.Equal(2, diff.LinesAdded);
        Assert.Equal(2, diff.LinesRemoved);
        var status = await repo.Git("status", "--porcelain=v1", "-uall");
        Assert.Contains("D  same.txt", status);
        Assert.Contains("?? same.txt", status);
        File.WriteAllText(repo.File("same.txt"), new string('x', 2048));
        var bounded = await GitService.GetReadOnlyDiffAsync(
            repo.Path, "same.txt", 1, 256, CancellationToken.None);
        Assert.True(bounded.Truncated);
        Assert.InRange(bounded.Text.Length, 1, 256);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData(".git/config")]
    [InlineData("folder/../../outside.txt")]
    [InlineData(":(glob)*")]
    [InlineData("bad\nname.txt")]
    public async Task Diff_RejectsUnsafeClientPaths(string path)
    {
        using var repo = new Repository();
        Assert.False(GitService.IsReadOnlyPathSafe(repo.Path, path));
        await Assert.ThrowsAsync<ArgumentException>(() => repo.Diff(path));
    }

    [Fact]
    public async Task PathsThroughDirectoryLinks_AreNeverReadable()
    {
        using var repo = new Repository();
        using var outside = new Repository();
        File.WriteAllText(outside.File("private.txt"), "not repository content");
        var link = repo.File("escape");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var info = new ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, outside.Path })
                    info.ArgumentList.Add(arg);
                using var process = Process.Start(info)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, await error);
                await output;
            }
            else
            {
                Directory.CreateSymbolicLink(link, outside.Path);
            }
            Assert.False(GitService.IsReadOnlyPathSafe(repo.Path, "escape/private.txt"));
            await Assert.ThrowsAsync<ArgumentException>(() => repo.Diff("escape/private.txt"));
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
        }
    }

    [Fact]
    public async Task Diff_RequiresCurrentChangedFile_AndBoundsOutput()
    {
        using var repo = new Repository();
        await repo.Git("init");
        File.WriteAllText(repo.File("large.txt"), new string('x', RemoteProtocol.GitDiffCharacterLimit * 2));
        File.WriteAllText(repo.File("second.txt"), "second");
        var limited = await GitService.GetReadOnlyChangesAsync(repo.Path, 1, CancellationToken.None);
        Assert.Single(limited.Files);
        Assert.True(limited.Truncated);
        var diff = await repo.Diff("large.txt");
        Assert.True(diff.Truncated);
        Assert.True(diff.Text.Length <= RemoteProtocol.GitDiffCharacterLimit);
        await Assert.ThrowsAsync<FileNotFoundException>(() => repo.Diff("not-listed.txt"));
        File.Delete(repo.File("large.txt"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => repo.Diff("large.txt"));
    }

    [Fact]
    public async Task EmptyNonrepoErrorsAndCancellation_AreDistinct()
    {
        using var repo = new Repository();
        var project = new Project { Name = "Plain folder", WorkingDirectory = repo.Path };
        var chat = new Chat { ProjectId = project.Id };
        var store = new DataStore(new AppData { Projects = [project], Chats = [chat] });
        var nonrepo = await RemoteGitChangesService.GetChangesAsync(
            RemoteGitChangesService.CaptureScope(store, chat), CancellationToken.None);
        Assert.False(nonrepo.IsRepository);
        Assert.NotNull(nonrepo.Message);

        await repo.Git("init");
        var clean = await RemoteGitChangesService.GetChangesAsync(
            RemoteGitChangesService.CaptureScope(store, chat), CancellationToken.None);
        Assert.True(clean.IsRepository);
        Assert.Empty(clean.Files);
        Assert.Contains("No uncommitted changes", clean.Message);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GitService.GetReadOnlyChangesAsync(repo.Path, 200, new CancellationToken(true)));
        Directory.Delete(repo.File(".git"), recursive: true);
        Directory.CreateDirectory(repo.File(".git"));
        await Assert.ThrowsAsync<IOException>(() =>
            GitService.GetReadOnlyChangesAsync(repo.Path, 200, CancellationToken.None));
    }

    internal sealed class Repository : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "lumi-git-read-tests", Guid.NewGuid().ToString("N"));

        public Repository() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public Task<(string Text, bool Truncated)> Diff(string path) =>
            GitService.GetReadOnlyDiffAsync(Path, path, RemoteProtocol.GitFileLimit,
                RemoteProtocol.GitDiffCharacterLimit, CancellationToken.None);

        public async Task<string> Git(params string[] arguments)
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = Path, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await error);
            return await output;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
