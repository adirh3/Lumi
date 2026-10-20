using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Collection("Git process handles")]
public sealed class GitServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingSurfaceRefreshStopsBeforeTheGitQueueIsReleased(bool multipleRefreshes)
    {
        using var ui = HeadlessTestSession.Start();
        var root = Path.Combine(Path.GetTempPath(), "LumiGitRefreshLifetime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var gate = (SemaphoreSlim)typeof(GitService)
            .GetField("GitInvocationGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        await gate.WaitAsync();
        ChatViewModel? surface = null;
        Task? first = null;
        Task? second = null;
        var stoppedBeforeRelease = false;
        try
        {
            await ui.Dispatch(() =>
            {
                var project = new Project { WorkingDirectory = root };
                var chat = new Chat { ProjectId = project.Id };
                var data = new AppData();
                data.Projects.Add(project);
                data.Chats.Add(chat);
                surface = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
                surface.CurrentChat = chat;
                first = surface.RefreshCodingProjectState();
                if (multipleRefreshes)
                    second = surface.RefreshCodingProjectState();
                surface.Dispose();
            }, CancellationToken.None);

            stoppedBeforeRelease = await Task.WhenAny(first!, Task.Delay(1500)) == first;
            if (multipleRefreshes)
            {
                stoppedBeforeRelease &= await Task.WhenAny(second!, Task.Delay(1500)) == second;
            }
        }
        finally
        {
            await ui.Dispatch(() => surface?.Dispose(), CancellationToken.None);
            gate.Release();
            if (first is not null)
                await first.WaitAsync(TimeSpan.FromSeconds(20));
            if (second is not null)
                await second.WaitAsync(TimeSpan.FromSeconds(20));
            Directory.Delete(root, recursive: true);
        }

        Assert.True(stoppedBeforeRelease,
            "Disposed surfaces must not wait in the global Git queue and retain their transcript.");
    }

    [Fact]
    public async Task CachedSurfaceCanFinishAndRepeatItsGitRefresh()
    {
        using var ui = HeadlessTestSession.Start();
        var root = FindRepoRoot();
        var expectedBranch = await GitService.GetCurrentBranchAsync(root);
        Assert.False(string.IsNullOrWhiteSpace(expectedBranch));

        await ui.Dispatch(async () =>
        {
            var project = new Project { WorkingDirectory = root };
            var chat = new Chat { ProjectId = project.Id };
            var data = new AppData();
            data.Projects.Add(project);
            data.Chats.Add(chat);
            using var sessions = new ChatSessionStore(
                new DataStore(data),
                TestCopilot.Shared,
                new ChatSurfaceRegistry(),
                static (surface, loadedChat) =>
                {
                    surface.CurrentChat = loadedChat;
                    return Task.CompletedTask;
                },
                maxIdleCachedSurfaces: 1);
            var gate = (SemaphoreSlim)typeof(GitService)
                .GetField("GitInvocationGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            await gate.WaitAsync();
            ChatViewModel surface;
            Task refresh;
            try
            {
                surface = await sessions.AcquireChatAsync(chat);
                refresh = surface.RefreshCodingProjectState();
                sessions.Release(surface);

                Assert.Contains(surface, sessions.SnapshotSurfaces());
                Assert.False(refresh.IsCompleted);
            }
            finally
            {
                gate.Release();
            }

            await refresh.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(expectedBranch, surface.GitBranch);
            Assert.False(surface.IsRefreshingGitStatus);

            var reopened = await sessions.AcquireChatAsync(chat);
            Assert.Same(surface, reopened);
            await reopened.RefreshCodingProjectState().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(expectedBranch, reopened.GitBranch);
            Assert.False(reopened.IsRefreshingGitStatus);
            sessions.Release(reopened);
        }, CancellationToken.None);
    }

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
