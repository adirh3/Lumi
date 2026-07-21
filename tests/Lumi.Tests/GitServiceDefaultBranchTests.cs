using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

[Collection("Git process handles")]
public sealed class GitServiceDefaultBranchTests
{
    [Fact]
    public async Task GetDefaultBranchInfoAsync_UsesRemoteHead()
    {
        using var fixture = CreateRemoteFixture("trunk");

        var result = await GitService.GetDefaultBranchInfoAsync(fixture.CloneDirectory);

        Assert.NotNull(result);
        Assert.Equal("trunk", result!.BranchName);
        Assert.Equal("origin", result.RemoteName);
    }

    [Fact]
    public async Task GetDefaultBranchInfoAsync_FallsBackToLocalMaster()
    {
        using var temp = new TempDir();
        var repo = Path.Combine(temp.Path, "repo");
        InitializeRepository(repo, "master");

        var result = await GitService.GetDefaultBranchInfoAsync(repo);

        Assert.NotNull(result);
        Assert.Equal("master", result!.BranchName);
        Assert.Null(result.RemoteName);
    }

    [Fact]
    public async Task SyncDefaultBranchAsync_FastForwardsCleanCheckedOutBranch()
    {
        using var fixture = CreateRemoteFixture("main");
        var remoteCommit = fixture.AddRemoteCommit("second");

        var result = await GitService.SyncDefaultBranchAsync(fixture.CloneDirectory);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Updated);
        Assert.Equal(remoteCommit, Git(fixture.CloneDirectory, "rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task SyncDefaultBranchAsync_SkipsDirtyCheckedOutBranch()
    {
        using var fixture = CreateRemoteFixture("main");
        var originalCommit = Git(fixture.CloneDirectory, "rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(fixture.CloneDirectory, "local.txt"), "uncommitted");
        fixture.AddRemoteCommit("second");

        var result = await GitService.SyncDefaultBranchAsync(fixture.CloneDirectory);

        Assert.False(result.Succeeded);
        Assert.False(result.Updated);
        Assert.Contains("uncommitted changes", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalCommit, Git(fixture.CloneDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal("uncommitted", File.ReadAllText(Path.Combine(fixture.CloneDirectory, "local.txt")));
    }

    [Fact]
    public async Task SyncDefaultBranchAsync_HonorsCancellation()
    {
        using var fixture = CreateRemoteFixture("main");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => GitService.SyncDefaultBranchAsync(fixture.CloneDirectory, cancellation.Token));
    }

    private static RemoteFixture CreateRemoteFixture(string branch)
    {
        var fixture = new RemoteFixture(branch);
        try
        {
            fixture.Initialize();
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private static void InitializeRepository(string repo, string branch)
    {
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", "-b", branch);
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "initial");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "initial");
    }

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}");
        return stdout;
    }

    private sealed class RemoteFixture : IDisposable
    {
        private readonly TempDir _temp = new();
        private readonly string _branch;

        public RemoteFixture(string branch)
        {
            _branch = branch;
            RemoteDirectory = Path.Combine(_temp.Path, "remote.git");
            SeedDirectory = Path.Combine(_temp.Path, "seed");
            CloneDirectory = Path.Combine(_temp.Path, "clone");
        }

        public string RemoteDirectory { get; }
        public string SeedDirectory { get; }
        public string CloneDirectory { get; }

        public void Initialize()
        {
            Directory.CreateDirectory(RemoteDirectory);
            Git(RemoteDirectory, "init", "--bare", "-q");
            InitializeRepository(SeedDirectory, _branch);
            Git(SeedDirectory, "remote", "add", "origin", RemoteDirectory);
            Git(SeedDirectory, "push", "-q", "-u", "origin", _branch);
            Git(_temp.Path, "--git-dir", RemoteDirectory, "symbolic-ref", "HEAD", $"refs/heads/{_branch}");
            Git(_temp.Path, "clone", "-q", RemoteDirectory, CloneDirectory);
        }

        public string AddRemoteCommit(string content)
        {
            File.WriteAllText(Path.Combine(SeedDirectory, "remote.txt"), content);
            Git(SeedDirectory, "add", ".");
            Git(SeedDirectory, "commit", "-q", "-m", content);
            Git(SeedDirectory, "push", "-q", "origin", _branch);
            return Git(SeedDirectory, "rev-parse", "HEAD").Trim();
        }

        public void Dispose() => _temp.Dispose();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lumi-git-sync-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
