using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

[Collection("Git process handles")]
public sealed class GitServiceTests
{
    [Fact]
    public async Task GetCurrentBranchAsync_ReturnsRepositoryHeadLabel()
    {
        var branch = await GitService.GetCurrentBranchAsync(FindRepoRoot());

        Assert.False(string.IsNullOrWhiteSpace(branch));
        Assert.NotEqual("Git", branch);
    }

    [Fact]
    public async Task RepeatedGitCommands_DoNotRetainRedirectedPipeHandles()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var repositoryRoot = FindRepoRoot();
        for (var i = 0; i < 5; i++)
            await GitService.GetCurrentBranchAsync(repositoryRoot);

        ForceFullGc();
        using var currentProcess = Process.GetCurrentProcess();
        currentProcess.Refresh();
        var baselineHandles = currentProcess.HandleCount;

        for (var i = 0; i < 200; i++)
            await GitService.GetCurrentBranchAsync(repositoryRoot);

        ForceFullGc();
        currentProcess.Refresh();
        var retainedHandles = currentProcess.HandleCount - baselineHandles;

        Assert.True(
            retainedHandles < 100,
            $"Repeated redirected git commands retained {retainedHandles} handles.");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Lumi.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate Lumi repository root.");
    }

    private static void ForceFullGc()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}

[CollectionDefinition("Git process handles", DisableParallelization = true)]
public sealed class GitProcessHandleCollection;
