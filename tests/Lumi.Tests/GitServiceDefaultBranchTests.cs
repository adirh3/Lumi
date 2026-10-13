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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncDefaultBranchAsync_UpdatesRecursiveSubmodulesToRecordedCommits(bool initializeSubmodules)
    {
        using var fixture = CreateRemoteFixture("main", withSubmodules: true, initializeSubmodules: initializeSubmodules);
        var (moduleCommit, nestedCommit) = fixture.AddSubmoduleCommits("recorded");
        var remoteCommit = fixture.RecordSubmoduleCommit(moduleCommit);
        fixture.AddSubmoduleCommits("unrecorded");
        Git(fixture.CloneDirectory, "config", "submodule.module.update", "merge");
        if (initializeSubmodules)
            Git(fixture.ModuleDirectory, "config", "submodule.nested.update", "rebase");

        var result = await GitService.SyncDefaultBranchAsync(fixture.CloneDirectory);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Updated);
        Assert.Equal(remoteCommit, Git(fixture.CloneDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal(moduleCommit, Git(fixture.ModuleDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal(nestedCommit, Git(fixture.NestedDirectory, "rev-parse", "HEAD").Trim());
        Assert.Empty(Git(fixture.CloneDirectory, "status", "--porcelain", "-uall", "--ignore-submodules=none"));
    }

    [Fact]
    public async Task SyncDefaultBranchAsync_RepairsSubmodulesWhenBranchIsAlreadyCurrent()
    {
        using var fixture = CreateRemoteFixture("main", withSubmodules: true);
        var (moduleCommit, nestedCommit) = fixture.AddSubmoduleCommits("recorded");
        var remoteCommit = fixture.RecordSubmoduleCommit(moduleCommit);
        Git(fixture.CloneDirectory, "fetch", "-q", "origin");
        Git(fixture.CloneDirectory, "merge", "-q", "--ff-only", "origin/main");

        var result = await GitService.SyncDefaultBranchAsync(fixture.CloneDirectory);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.Updated);
        Assert.Equal(remoteCommit, Git(fixture.CloneDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal(moduleCommit, Git(fixture.ModuleDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal(nestedCommit, Git(fixture.NestedDirectory, "rev-parse", "HEAD").Trim());
        Assert.Empty(Git(fixture.CloneDirectory, "status", "--porcelain", "-uall", "--ignore-submodules=none"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncDefaultBranchAsync_PreservesLocalSubmoduleCommits(bool nested)
    {
        using var fixture = CreateRemoteFixture("main", withSubmodules: true);
        var originalCommit = Git(fixture.CloneDirectory, "rev-parse", "HEAD").Trim();
        var localDirectory = nested ? fixture.NestedDirectory : fixture.ModuleDirectory;
        Git(localDirectory, "config", "user.email", "test@example.com");
        Git(localDirectory, "config", "user.name", "Test");
        var localFile = Path.Combine(localDirectory, "local-work.txt");
        File.WriteAllText(localFile, "local committed work");
        Git(localDirectory, "add", "local-work.txt");
        Git(localDirectory, "commit", "-q", "-m", "local detached commit");
        var localCommit = Git(localDirectory, "rev-parse", "HEAD").Trim();
        Assert.Empty(Git(localDirectory, "for-each-ref", "--contains", localCommit));
        fixture.AddRemoteCommit("upstream update");

        var result = await GitService.SyncDefaultBranchAsync(fixture.CloneDirectory);

        Assert.False(result.Succeeded);
        Assert.False(result.Updated);
        Assert.Contains("local submodule commits", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalCommit, Git(fixture.CloneDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal(localCommit, Git(localDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal("local committed work", File.ReadAllText(localFile));
    }

    [Fact]
    public async Task SyncDefaultBranchAsync_AllowsRecordedSubmoduleRewinds()
    {
        using var fixture = CreateRemoteFixture("main", withSubmodules: true);
        var originalModuleCommit = Git(fixture.ModuleDirectory, "rev-parse", "HEAD").Trim();
        var originalNestedCommit = Git(fixture.NestedDirectory, "rev-parse", "HEAD").Trim();
        var (moduleCommit, _) = fixture.AddSubmoduleCommits("recorded");
        fixture.RecordSubmoduleCommit(moduleCommit);
        var advanced = await GitService.SyncDefaultBranchAsync(fixture.CloneDirectory);
        Assert.True(advanced.Succeeded, advanced.Message);
        var remoteCommit = fixture.RecordSubmoduleCommit(originalModuleCommit);

        var result = await GitService.SyncDefaultBranchAsync(fixture.CloneDirectory);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Updated);
        Assert.Equal(remoteCommit, Git(fixture.CloneDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal(originalModuleCommit, Git(fixture.ModuleDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal(originalNestedCommit, Git(fixture.NestedDirectory, "rev-parse", "HEAD").Trim());
        Assert.Empty(Git(fixture.CloneDirectory, "status", "--porcelain", "-uall", "--ignore-submodules=none"));
    }

    [Fact]
    public async Task SyncDefaultBranchAsync_UsesNewNestedUrlsOnTheFirstUpdate()
    {
        using var fixture = CreateRemoteFixture("main", withSubmodules: true);
        var (moduleCommit, nestedCommit, nestedRemote) = fixture.ChangeNestedRemote();
        var remoteCommit = fixture.RecordSubmoduleCommit(moduleCommit);
        Git(fixture.ModuleDirectory, "config", "fetch.recurseSubmodules", "true");

        var result = await GitService.SyncDefaultBranchAsync(fixture.CloneDirectory);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Updated);
        Assert.Equal(remoteCommit, Git(fixture.CloneDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal(moduleCommit, Git(fixture.ModuleDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal(nestedCommit, Git(fixture.NestedDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal(nestedRemote, Git(fixture.NestedDirectory, "remote", "get-url", "origin").Trim());
        Assert.Empty(Git(fixture.CloneDirectory, "status", "--porcelain", "-uall", "--ignore-submodules=none"));
    }

    [Fact]
    public async Task SyncDefaultBranchAsync_ReportsNestedFailureAndRetries()
    {
        using var fixture = CreateRemoteFixture("main", withSubmodules: true);
        var (moduleCommit, nestedCommit, nestedRemote) = fixture.ChangeNestedRemote();
        var remoteCommit = fixture.RecordSubmoduleCommit(moduleCommit);
        var unavailableRemote = nestedRemote + "-unavailable";
        Directory.Move(nestedRemote, unavailableRemote);

        var failed = await GitService.SyncDefaultBranchAsync(fixture.CloneDirectory);

        Assert.False(failed.Succeeded);
        Assert.True(failed.Updated);
        Assert.Contains("submodule", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(remoteCommit, Git(fixture.CloneDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal(moduleCommit, Git(fixture.ModuleDirectory, "rev-parse", "HEAD").Trim());
        Assert.NotEqual(nestedCommit, Git(fixture.NestedDirectory, "rev-parse", "HEAD").Trim());
        Directory.Move(unavailableRemote, nestedRemote);

        var retried = await GitService.SyncDefaultBranchAsync(fixture.CloneDirectory);

        Assert.True(retried.Succeeded, retried.Message);
        Assert.False(retried.Updated);
        Assert.Equal(nestedCommit, Git(fixture.NestedDirectory, "rev-parse", "HEAD").Trim());
        Assert.Empty(Git(fixture.CloneDirectory, "status", "--porcelain", "-uall", "--ignore-submodules=none"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncDefaultBranchAsync_SkipsDirtySubmodules(bool nested)
    {
        using var fixture = CreateRemoteFixture("main", withSubmodules: true);
        var originalCommit = Git(fixture.CloneDirectory, "rev-parse", "HEAD").Trim();
        var dirtyDirectory = nested ? fixture.NestedDirectory : fixture.ModuleDirectory;
        var dirtyFile = Path.Combine(dirtyDirectory, "README.md");
        File.WriteAllText(dirtyFile, "uncommitted");
        Git(fixture.CloneDirectory, "config", "submodule.module.ignore", "all");
        Git(fixture.ModuleDirectory, "config", "submodule.nested.ignore", "all");
        var (moduleCommit, _) = fixture.AddSubmoduleCommits("recorded");
        fixture.RecordSubmoduleCommit(moduleCommit);

        var result = await GitService.SyncDefaultBranchAsync(fixture.CloneDirectory);

        Assert.False(result.Succeeded);
        Assert.False(result.Updated);
        Assert.Contains("uncommitted changes", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalCommit, Git(fixture.CloneDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal("uncommitted", File.ReadAllText(dirtyFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncDefaultBranchAsync_SkipsStagedSubmodulePointers(bool nested)
    {
        using var fixture = CreateRemoteFixture("main", withSubmodules: true);
        var originalCommit = Git(fixture.CloneDirectory, "rev-parse", "HEAD").Trim();
        var (moduleCommit, nestedCommit) = fixture.AddSubmoduleCommits("recorded");
        fixture.RecordSubmoduleCommit(moduleCommit);
        var checkoutDirectory = nested ? fixture.NestedDirectory : fixture.ModuleDirectory;
        var indexDirectory = nested ? fixture.ModuleDirectory : fixture.CloneDirectory;
        Git(checkoutDirectory, "fetch", "-q", "origin");
        Git(checkoutDirectory, "checkout", "-q", nested ? nestedCommit : moduleCommit);
        Git(indexDirectory, "add", nested ? "nested" : "module");
        var stagedDiff = Git(indexDirectory, "diff", "--cached", "--ignore-submodules=none");

        var result = await GitService.SyncDefaultBranchAsync(fixture.CloneDirectory);

        Assert.False(result.Succeeded);
        Assert.False(result.Updated);
        Assert.Contains("uncommitted changes", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalCommit, Git(fixture.CloneDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal(stagedDiff, Git(indexDirectory, "diff", "--cached", "--ignore-submodules=none"));
    }

    [Fact]
    public async Task SyncDefaultBranchAsync_ReportsSubmoduleFailureAndRetriesWithoutAnotherFastForward()
    {
        using var fixture = CreateRemoteFixture("main", withSubmodules: true);
        var (moduleCommit, nestedCommit) = fixture.AddSubmoduleCommits("recorded");
        var unavailableRemote = fixture.ModuleSourceDirectory + "-unavailable";
        Git(fixture.SeedDirectory, "config", "--file", ".gitmodules", "submodule.module.url", unavailableRemote);
        var remoteCommit = fixture.RecordSubmoduleCommit(moduleCommit);

        var failed = await GitService.SyncDefaultBranchAsync(fixture.CloneDirectory);

        Assert.False(failed.Succeeded);
        Assert.True(failed.Updated);
        Assert.Contains("submodule", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(remoteCommit, Git(fixture.CloneDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal(unavailableRemote, Git(fixture.ModuleDirectory, "remote", "get-url", "origin").Trim());

        Directory.Move(fixture.ModuleSourceDirectory, unavailableRemote);

        var retried = await GitService.SyncDefaultBranchAsync(fixture.CloneDirectory);

        Assert.True(retried.Succeeded, retried.Message);
        Assert.False(retried.Updated);
        Assert.Equal(moduleCommit, Git(fixture.ModuleDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal(nestedCommit, Git(fixture.NestedDirectory, "rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task SyncDefaultBranchAsync_UpdatesOnlyTheDefaultBranchWorktree()
    {
        using var fixture = CreateRemoteFixture("main", withSubmodules: true);
        var originalCommit = Git(fixture.CloneDirectory, "rev-parse", "HEAD").Trim();
        var originalModuleCommit = Git(fixture.ModuleDirectory, "rev-parse", "HEAD").Trim();
        var originalNestedCommit = Git(fixture.NestedDirectory, "rev-parse", "HEAD").Trim();
        Git(fixture.CloneDirectory, "checkout", "-q", "-b", "feature");
        Git(fixture.CloneDirectory, "worktree", "add", "-q", fixture.DefaultWorktreeDirectory, "main");
        Git(fixture.DefaultWorktreeDirectory, "submodule", "update", "--init", "--recursive");
        var (moduleCommit, nestedCommit) = fixture.AddSubmoduleCommits("recorded");
        var remoteCommit = fixture.RecordSubmoduleCommit(moduleCommit);

        var result = await GitService.SyncDefaultBranchAsync(fixture.CloneDirectory);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Updated);
        Assert.Equal(remoteCommit, Git(fixture.DefaultWorktreeDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal(moduleCommit, Git(Path.Combine(fixture.DefaultWorktreeDirectory, "module"), "rev-parse", "HEAD").Trim());
        Assert.Equal(nestedCommit, Git(Path.Combine(fixture.DefaultWorktreeDirectory, "module", "nested"), "rev-parse", "HEAD").Trim());
        Assert.Equal(originalCommit, Git(fixture.CloneDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal(originalModuleCommit, Git(fixture.ModuleDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal(originalNestedCommit, Git(fixture.NestedDirectory, "rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task SyncDefaultBranchAsync_PreservesOtherSubmodulesWhenDefaultBranchIsNotCheckedOut()
    {
        using var fixture = CreateRemoteFixture("main", withSubmodules: true);
        var originalCommit = Git(fixture.CloneDirectory, "rev-parse", "HEAD").Trim();
        var originalModuleCommit = Git(fixture.ModuleDirectory, "rev-parse", "HEAD").Trim();
        var originalNestedCommit = Git(fixture.NestedDirectory, "rev-parse", "HEAD").Trim();
        Git(fixture.CloneDirectory, "checkout", "-q", "-b", "feature");
        var (moduleCommit, _) = fixture.AddSubmoduleCommits("recorded");
        var remoteCommit = fixture.RecordSubmoduleCommit(moduleCommit);

        var result = await GitService.SyncDefaultBranchAsync(fixture.CloneDirectory);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Updated);
        Assert.Equal(remoteCommit, Git(fixture.CloneDirectory, "rev-parse", "main").Trim());
        Assert.Equal(originalCommit, Git(fixture.CloneDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal(originalModuleCommit, Git(fixture.ModuleDirectory, "rev-parse", "HEAD").Trim());
        Assert.Equal(originalNestedCommit, Git(fixture.NestedDirectory, "rev-parse", "HEAD").Trim());
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

    private static RemoteFixture CreateRemoteFixture(
        string branch,
        bool withSubmodules = false,
        bool initializeSubmodules = true)
    {
        var fixture = new RemoteFixture(branch, withSubmodules, initializeSubmodules);
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
        private readonly bool _withSubmodules;
        private readonly bool _initializeSubmodules;
        private readonly string? _originalAllowedProtocols = Environment.GetEnvironmentVariable("GIT_ALLOW_PROTOCOL");

        public RemoteFixture(string branch, bool withSubmodules, bool initializeSubmodules)
        {
            _branch = branch;
            _withSubmodules = withSubmodules;
            _initializeSubmodules = initializeSubmodules;
            RemoteDirectory = Path.Combine(_temp.Path, "remote.git");
            SeedDirectory = Path.Combine(_temp.Path, "seed");
            CloneDirectory = Path.Combine(_temp.Path, "clone");
            ModuleSourceDirectory = Path.Combine(_temp.Path, "module-source");
            NestedSourceDirectory = Path.Combine(_temp.Path, "nested-source");
            Environment.SetEnvironmentVariable("GIT_ALLOW_PROTOCOL", "file");
        }

        public string RemoteDirectory { get; }
        public string SeedDirectory { get; }
        public string CloneDirectory { get; }
        public string ModuleSourceDirectory { get; }
        public string NestedSourceDirectory { get; }
        public string DefaultWorktreeDirectory => Path.Combine(_temp.Path, "default-worktree");
        public string ModuleDirectory => Path.Combine(CloneDirectory, "module");
        public string NestedDirectory => Path.Combine(ModuleDirectory, "nested");

        public void Initialize()
        {
            Directory.CreateDirectory(RemoteDirectory);
            Git(RemoteDirectory, "init", "--bare", "-q");
            InitializeRepository(SeedDirectory, _branch);
            if (_withSubmodules)
            {
                InitializeRepository(NestedSourceDirectory, "main");
                InitializeRepository(ModuleSourceDirectory, "main");
                Git(ModuleSourceDirectory, "submodule", "add", "-q", NestedSourceDirectory, "nested");
                Git(ModuleSourceDirectory, "commit", "-q", "-am", "nested module");
                Git(SeedDirectory, "submodule", "add", "-q", ModuleSourceDirectory, "module");
                Git(SeedDirectory, "commit", "-q", "-am", "module");
            }

            Git(SeedDirectory, "remote", "add", "origin", RemoteDirectory);
            Git(SeedDirectory, "push", "-q", "-u", "origin", _branch);
            Git(_temp.Path, "--git-dir", RemoteDirectory, "symbolic-ref", "HEAD", $"refs/heads/{_branch}");
            Git(_temp.Path, "clone", "-q", RemoteDirectory, CloneDirectory);
            Git(CloneDirectory, "config", "fetch.recurseSubmodules", "false");
            Git(CloneDirectory, "config", "submodule.recurse", "false");
            if (_withSubmodules && _initializeSubmodules)
                Git(CloneDirectory, "submodule", "update", "--init", "--recursive");
        }

        public (string ModuleCommit, string NestedCommit) AddSubmoduleCommits(string content)
        {
            File.WriteAllText(Path.Combine(NestedSourceDirectory, "README.md"), content);
            Git(NestedSourceDirectory, "commit", "-q", "-am", content);
            var nestedCommit = Git(NestedSourceDirectory, "rev-parse", "HEAD").Trim();
            var nestedCheckout = Path.Combine(ModuleSourceDirectory, "nested");
            Git(nestedCheckout, "fetch", "-q", "origin");
            Git(nestedCheckout, "checkout", "-q", nestedCommit);
            File.WriteAllText(Path.Combine(ModuleSourceDirectory, "README.md"), content);
            Git(ModuleSourceDirectory, "commit", "-q", "-am", content);
            return (Git(ModuleSourceDirectory, "rev-parse", "HEAD").Trim(), nestedCommit);
        }

        public (string ModuleCommit, string NestedCommit, string NestedRemote) ChangeNestedRemote()
        {
            var nestedRemote = Path.Combine(_temp.Path, "replacement-nested");
            Git(_temp.Path, "clone", "-q", NestedSourceDirectory, nestedRemote);
            Git(nestedRemote, "config", "user.email", "test@example.com");
            Git(nestedRemote, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(nestedRemote, "README.md"), "new nested remote");
            Git(nestedRemote, "commit", "-q", "-am", "new nested remote");
            var nestedCommit = Git(nestedRemote, "rev-parse", "HEAD").Trim();
            Git(ModuleSourceDirectory, "config", "--file", ".gitmodules", "submodule.nested.url", nestedRemote);
            Git(ModuleSourceDirectory, "submodule", "sync");
            var nestedCheckout = Path.Combine(ModuleSourceDirectory, "nested");
            Git(nestedCheckout, "fetch", "-q", "origin");
            Git(nestedCheckout, "checkout", "-q", nestedCommit);
            Git(ModuleSourceDirectory, "commit", "-q", "-am", "record nested URL and commit");
            return (Git(ModuleSourceDirectory, "rev-parse", "HEAD").Trim(), nestedCommit, nestedRemote);
        }

        public string RecordSubmoduleCommit(string moduleCommit)
        {
            var seedModule = Path.Combine(SeedDirectory, "module");
            Git(seedModule, "fetch", "-q", "origin");
            Git(seedModule, "checkout", "-q", moduleCommit);
            return AddRemoteCommit("submodules");
        }

        public string AddRemoteCommit(string content)
        {
            File.WriteAllText(Path.Combine(SeedDirectory, "remote.txt"), content);
            Git(SeedDirectory, "add", ".");
            Git(SeedDirectory, "commit", "-q", "-m", content);
            Git(SeedDirectory, "push", "-q", "origin", _branch);
            return Git(SeedDirectory, "rev-parse", "HEAD").Trim();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("GIT_ALLOW_PROTOCOL", _originalAllowedProtocols);
            _temp.Dispose();
        }
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
