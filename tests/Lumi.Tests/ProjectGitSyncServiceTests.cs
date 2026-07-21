using System;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class ProjectGitSyncServiceTests
{
    [Fact]
    public async Task RunDueSyncsAsync_RunsOncePerLocalDay()
    {
        var now = DateTimeOffset.Now;
        var project = new Project
        {
            Name = "Code",
            WorkingDirectory = @"C:\Code",
            AutoSyncMainBranchDaily = true
        };
        var store = new DataStore(new AppData { Projects = [project] });
        var calls = 0;
        await using var service = new ProjectGitSyncService(
            store,
            (_, _) =>
            {
                calls++;
                return Task.FromResult(new GitBranchSyncResult(true, "main", true, "updated"));
            },
            () => now,
            TimeSpan.FromHours(1));

        await service.RunDueSyncsAsync();
        await service.RunDueSyncsAsync();

        Assert.Equal(1, calls);
        Assert.Equal(now, project.LastMainBranchSyncAttemptAt);
        Assert.Equal(now, project.LastMainBranchSyncAt);
        Assert.Null(project.LastMainBranchSyncError);

        now = now.AddDays(1);
        await service.RunDueSyncsAsync();

        Assert.Equal(2, calls);
        Assert.Equal(now, project.LastMainBranchSyncAttemptAt);
        Assert.Equal(now, project.LastMainBranchSyncAt);
    }

    [Fact]
    public async Task RunDueSyncsAsync_RecordsFailureWithoutMarkingSuccess()
    {
        var now = DateTimeOffset.Now;
        var project = new Project
        {
            Name = "Code",
            WorkingDirectory = @"C:\Code",
            AutoSyncMainBranchDaily = true
        };
        var store = new DataStore(new AppData { Projects = [project] });
        await using var service = new ProjectGitSyncService(
            store,
            (_, _) => Task.FromResult(new GitBranchSyncResult(false, "main", false, "dirty branch")),
            () => now,
            TimeSpan.FromHours(1));

        await service.RunDueSyncsAsync();

        Assert.Equal(now, project.LastMainBranchSyncAttemptAt);
        Assert.Null(project.LastMainBranchSyncAt);
        Assert.Equal("dirty branch", project.LastMainBranchSyncError);
    }

    [Fact]
    public async Task DisposeAsync_CancelsInFlightSyncAndKeepsProjectDue()
    {
        var project = new Project
        {
            Name = "Code",
            WorkingDirectory = @"C:\Code",
            AutoSyncMainBranchDaily = true
        };
        var store = new DataStore(new AppData { Projects = [project] });
        var enteredSync = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ProjectGitSyncService(
            store,
            async (_, cancellationToken) =>
            {
                enteredSync.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    observedCancellation.TrySetResult(true);
                    throw;
                }

                return new GitBranchSyncResult(true, "main", true, "updated");
            },
            () => DateTimeOffset.Now,
            TimeSpan.FromHours(1));

        try
        {
            service.Start();
            await enteredSync.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Null(project.LastMainBranchSyncAttemptAt);
        }
        finally
        {
            await service.DisposeAsync();
        }
    }
}
