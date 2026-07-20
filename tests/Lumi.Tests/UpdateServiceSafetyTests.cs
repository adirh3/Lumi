using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Lumi.Services;
using NuGet.Versioning;
using Velopack;
using Velopack.Locators;
using Xunit;

namespace Lumi.Tests;

public sealed class UpdateServiceSafetyTests
{
    [Theory]
    [InlineData(Architecture.Arm64, "osx-arm64")]
    [InlineData(Architecture.X64, "osx-x64")]
    public void UpdateChannel_ForMacOS_MatchesProcessArchitecture(
        Architecture processArchitecture,
        string expectedChannel)
    {
        Assert.Equal(
            expectedChannel,
            UpdateService.ResolveUpdateChannel(isMacOS: true, processArchitecture));
    }

    [Theory]
    [InlineData(Architecture.Arm64)]
    [InlineData(Architecture.X64)]
    public void UpdateChannel_ForNonMacOS_UsesVelopackDefault(Architecture processArchitecture)
    {
        Assert.Null(UpdateService.ResolveUpdateChannel(isMacOS: false, processArchitecture));
    }

    [Fact]
    public void SafeWorkingDirectory_IsOutsideVelopackInstallTree()
    {
        var safeDirectory = InstalledAppWorkingDirectory.GetSafeWorkingDirectory();

        Assert.False(string.IsNullOrWhiteSpace(safeDirectory));
        Assert.False(InstalledAppWorkingDirectory.IsPathInsideRoot(
            @"C:\Users\Adir\AppData\Roaming\Lumi\copilot\workspace",
            @"C:\Users\Adir\AppData\Local\Lumi"));
        Assert.True(InstalledAppWorkingDirectory.IsPathInsideRoot(
            @"C:\Users\Adir\AppData\Local\Lumi\current",
            @"C:\Users\Adir\AppData\Local\Lumi"));
        Assert.False(InstalledAppWorkingDirectory.IsPathInsideRoot(
            @"C:\Users\Adir\AppData\Local\Lumi-old",
            @"C:\Users\Adir\AppData\Local\Lumi"));
    }

    [Fact]
    public async Task ApplyUpdate_WhenNoBlockers_LaunchesUpdaterThenRequestsGracefulShutdown()
    {
        var blockerService = new FakeUpdateBlockerService([[]]);
        var restartLauncher = new FakeUpdateRestartLauncher();
        var service = CreateReadyService(blockerService, restartLauncher);
        var shutdownRequested = false;
        service.RestartRequested += () => shutdownRequested = true;

        await service.ApplyUpdateAndRestartAsync();

        Assert.Equal(1, blockerService.FindCalls);
        Assert.Equal(1, restartLauncher.LaunchCalls);
        Assert.True(shutdownRequested);
        Assert.Equal(UpdateStatus.PreparingToRestart, service.CurrentStatus);
    }

    [Fact]
    public async Task ApplyUpdate_WhenBlocked_ClosesProcessesRechecksAndRestarts()
    {
        var blocker = new UpdateBlockingProcess(
            4242,
            null,
            "PowerShell",
            null,
            canClose: true,
            isSimulated: true);
        var blockerService = new FakeUpdateBlockerService([[blocker], []]);
        var restartLauncher = new FakeUpdateRestartLauncher();
        var service = CreateReadyService(blockerService, restartLauncher);
        var shutdownRequested = false;
        service.RestartRequested += () => shutdownRequested = true;

        await service.ApplyUpdateAndRestartAsync();

        Assert.Equal(UpdateStatus.BlockedByProcesses, service.CurrentStatus);
        Assert.Single(service.BlockingProcesses);
        Assert.Equal(0, restartLauncher.LaunchCalls);

        await service.CloseBlockingProcessesAndRestartAsync();

        Assert.Equal(1, blockerService.CloseCalls);
        Assert.Equal(2, blockerService.FindCalls);
        Assert.Empty(service.BlockingProcesses);
        Assert.Equal(1, restartLauncher.LaunchCalls);
        Assert.True(shutdownRequested);
    }

    [Fact]
    public async Task ApplyUpdate_WhenBlockerScanFails_RemainsReadyAndCanRetry()
    {
        var blockerService = new FailOnceUpdateBlockerService();
        var restartLauncher = new FakeUpdateRestartLauncher();
        var service = CreateReadyService(blockerService, restartLauncher);
        var shutdownRequested = false;
        service.RestartRequested += () => shutdownRequested = true;

        await service.ApplyUpdateAndRestartAsync();

        Assert.Equal(UpdateStatus.ReadyToRestart, service.CurrentStatus);
        Assert.Contains("scan failed", service.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, restartLauncher.LaunchCalls);

        await service.ApplyUpdateAndRestartAsync();

        Assert.Equal(UpdateStatus.PreparingToRestart, service.CurrentStatus);
        Assert.Equal(string.Empty, service.ErrorMessage);
        Assert.Equal(1, restartLauncher.LaunchCalls);
        Assert.True(shutdownRequested);
    }

    [Fact]
    public async Task ApplyUpdate_WhenUpdaterLaunchFails_RemainsReadyAndCanRetry()
    {
        var blockerService = new FakeUpdateBlockerService([[], []]);
        var restartLauncher = new FakeUpdateRestartLauncher(failuresBeforeSuccess: 1);
        var service = CreateReadyService(blockerService, restartLauncher);
        var shutdownRequested = false;
        service.RestartRequested += () => shutdownRequested = true;

        await service.ApplyUpdateAndRestartAsync();

        Assert.Equal(UpdateStatus.ReadyToRestart, service.CurrentStatus);
        Assert.Contains("launch failed", service.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, restartLauncher.LaunchCalls);
        Assert.False(shutdownRequested);

        await service.ApplyUpdateAndRestartAsync();

        Assert.Equal(UpdateStatus.PreparingToRestart, service.CurrentStatus);
        Assert.Equal(string.Empty, service.ErrorMessage);
        Assert.Equal(2, restartLauncher.LaunchCalls);
        Assert.True(shutdownRequested);
    }

    [SkippableFact]
    public async Task RestartManager_FindsAndClosesProcessHoldingUpdateFile()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows Restart Manager is only available on Windows.");

        var tempDirectory = Path.Combine(Path.GetTempPath(), "Lumi-update-lock-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var lockedFile = Path.Combine(tempDirectory, "locked.dll");
        var readyFile = Path.Combine(tempDirectory, "ready.txt");
        await File.WriteAllTextAsync(lockedFile, "locked");

        var escapedLockedFile = lockedFile.Replace("'", "''", StringComparison.Ordinal);
        var escapedReadyFile = readyFile.Replace("'", "''", StringComparison.Ordinal);
        var script =
            $"$stream = [IO.File]::Open('{escapedLockedFile}', [IO.FileMode]::Open, "
            + "[IO.FileAccess]::ReadWrite, [IO.FileShare]::Read); "
            + $"[IO.File]::WriteAllText('{escapedReadyFile}', 'ready'); "
            + "Start-Sleep -Seconds 30";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        try
        {
            await WaitForFileAsync(readyFile, TimeSpan.FromSeconds(5));
            var service = new UpdateBlockerService();

            var blockers = await service.FindBlockingProcessesAsync([tempDirectory]);
            var blocker = Assert.Single(blockers, candidate => candidate.ProcessId == process!.Id);

            Assert.True(blocker.CanClose);

            var closeResult = await service.CloseBlockingProcessesAsync([blocker]);
            await process!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(closeResult.FailedProcessIds);
            Assert.True(process.HasExited);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process?.Dispose();
            await DeleteDirectoryWithRetryAsync(tempDirectory);
        }
    }

    [SkippableFact]
    public async Task WorkingDirectoryScanner_FindsAndClosesProcessInsideUpdateFolder()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Process working-directory inspection is only available on Windows.");

        var tempDirectory = Path.Combine(Path.GetTempPath(), "Lumi-update-cwd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var readyFile = Path.Combine(tempDirectory, "ready.txt");
        var escapedReadyFile = readyFile.Replace("'", "''", StringComparison.Ordinal);
        var script =
            $"[IO.File]::WriteAllText('{escapedReadyFile}', 'ready'); "
            + "Start-Sleep -Seconds 30";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = tempDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        try
        {
            await WaitForFileAsync(readyFile, TimeSpan.FromSeconds(5));
            var service = new UpdateBlockerService();

            var blockers = await service.FindBlockingProcessesAsync([tempDirectory]);
            var blocker = Assert.Single(blockers, candidate => candidate.ProcessId == process!.Id);

            Assert.True(blocker.CanClose);

            var closeResult = await service.CloseBlockingProcessesAsync([blocker]);
            await process!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(closeResult.FailedProcessIds);
            Assert.True(process.HasExited);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process?.Dispose();
            await DeleteDirectoryWithRetryAsync(tempDirectory);
        }
    }

    [SkippableFact]
    public async Task ClosingExternalBlocker_TerminatesItsChildProcessTree()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Process working-directory inspection is only available on Windows.");

        var tempDirectory = Path.Combine(Path.GetTempPath(), "Lumi-update-tree-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var childPidFile = Path.Combine(tempDirectory, "child-pid.txt");
        var readyFile = Path.Combine(tempDirectory, "ready.txt");
        var escapedChildPidFile = childPidFile.Replace("'", "''", StringComparison.Ordinal);
        var escapedReadyFile = readyFile.Replace("'", "''", StringComparison.Ordinal);
        var script =
            "$child = Start-Process -FilePath $env:ComSpec "
            + "-ArgumentList '/d', '/c', 'ping 127.0.0.1 -n 30 > nul' -PassThru; "
            + $"[IO.File]::WriteAllText('{escapedChildPidFile}', [string]$child.Id); "
            + $"[IO.File]::WriteAllText('{escapedReadyFile}', 'ready'); "
            + "Wait-Process -Id $child.Id";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = tempDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var parentProcess = Process.Start(startInfo);
        Assert.NotNull(parentProcess);
        Process? childProcess = null;

        try
        {
            await WaitForFileAsync(readyFile, TimeSpan.FromSeconds(5));
            var childProcessId = int.Parse(await File.ReadAllTextAsync(childPidFile));
            childProcess = Process.GetProcessById(childProcessId);
            var service = new UpdateBlockerService();
            var blockers = await service.FindBlockingProcessesAsync([tempDirectory]);
            var blocker = Assert.Single(
                blockers,
                candidate => candidate.ProcessId == parentProcess!.Id);

            var closeResult = await service.CloseBlockingProcessesAsync([blocker]);
            await parentProcess!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await childProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(closeResult.FailedProcessIds);
            Assert.True(parentProcess.HasExited);
            Assert.True(childProcess.HasExited);
        }
        finally
        {
            if (parentProcess is { HasExited: false })
            {
                parentProcess.Kill(entireProcessTree: false);
                await parentProcess.WaitForExitAsync();
            }

            if (childProcess is { HasExited: false })
            {
                childProcess.Kill(entireProcessTree: true);
                await childProcess.WaitForExitAsync();
            }

            childProcess?.Dispose();
            parentProcess?.Dispose();
            await DeleteDirectoryWithRetryAsync(tempDirectory);
        }
    }

    [Fact]
    public void ProcessTreeKill_WhenLumiIsDescendant_IsProhibited()
    {
        var descendants = new HashSet<int> { Environment.ProcessId };

        Assert.False(UpdateBlockerService.CanSafelyKillEntireProcessTree(
            ancestryComplete: true,
            descendants,
            Environment.ProcessId));
    }

    [Fact]
    public void ProcessTreeKill_WhenSnapshotFails_IsProhibited()
    {
        Assert.False(UpdateBlockerService.CanSafelyKillEntireProcessTree(
            ancestryComplete: false,
            new HashSet<int>(),
            Environment.ProcessId));
    }

    [Fact]
    public void ProcessTreeKill_WhenLumiIsNotDescendant_IsAllowed()
    {
        Assert.True(UpdateBlockerService.CanSafelyKillEntireProcessTree(
            ancestryComplete: true,
            new HashSet<int> { int.MaxValue },
            Environment.ProcessId));
    }

    [Fact]
    public void ProcessTreeSnapshot_RejectsChildOlderThanReusedParentPid()
    {
        const int parentProcessId = 100;
        const int childProcessId = 200;
        var snapshot = new WindowsRestartManager.WindowsProcessTree.ProcessTreeSnapshot(
            new Dictionary<int, List<int>>
            {
                [parentProcessId] = [childProcessId]
            },
            new Dictionary<int, long>
            {
                [parentProcessId] = 20,
                [childProcessId] = 10
            });

        var descendants = snapshot.GetDescendantProcessIds(
            parentProcessId,
            ancestorStartTimeUtcTicks: 20);

        Assert.Empty(descendants);
    }

    [Fact]
    public void ProcessTreeSnapshot_AcceptsChildCreatedAfterParent()
    {
        const int parentProcessId = 100;
        const int childProcessId = 200;
        var snapshot = new WindowsRestartManager.WindowsProcessTree.ProcessTreeSnapshot(
            new Dictionary<int, List<int>>
            {
                [parentProcessId] = [childProcessId]
            },
            new Dictionary<int, long>
            {
                [parentProcessId] = 10,
                [childProcessId] = 20
            });

        var descendants = snapshot.GetDescendantProcessIds(
            parentProcessId,
            ancestorStartTimeUtcTicks: 10);

        Assert.Equal([childProcessId], descendants);
    }

    [Fact]
    public void ProcessTreeSnapshot_MissingReachableIdentity_IsIncomplete()
    {
        const int parentProcessId = 100;
        const int intermediateProcessId = 200;
        const int lumiProcessId = 300;
        var snapshot = new WindowsRestartManager.WindowsProcessTree.ProcessTreeSnapshot(
            new Dictionary<int, List<int>>
            {
                [parentProcessId] = [intermediateProcessId],
                [intermediateProcessId] = [lumiProcessId]
            },
            new Dictionary<int, long>
            {
                [parentProcessId] = 10,
                [lumiProcessId] = 30
            });

        var ancestryComplete = snapshot.TryGetDescendantProcessIds(
            parentProcessId,
            ancestorStartTimeUtcTicks: 10,
            out var descendants);

        Assert.False(ancestryComplete);
        Assert.Empty(descendants);
        Assert.False(UpdateBlockerService.CanSafelyKillEntireProcessTree(
            ancestryComplete,
            descendants,
            lumiProcessId));
    }

    [Fact]
    public void ProcessTreeSnapshot_MissingUnrelatedIdentity_DoesNotInvalidateTraversal()
    {
        const int parentProcessId = 100;
        const int childProcessId = 200;
        const int unrelatedParentProcessId = 300;
        const int unrelatedChildProcessId = 400;
        var snapshot = new WindowsRestartManager.WindowsProcessTree.ProcessTreeSnapshot(
            new Dictionary<int, List<int>>
            {
                [parentProcessId] = [childProcessId],
                [unrelatedParentProcessId] = [unrelatedChildProcessId]
            },
            new Dictionary<int, long>
            {
                [parentProcessId] = 10,
                [childProcessId] = 20,
                [unrelatedParentProcessId] = 30
            });

        var ancestryComplete = snapshot.TryGetDescendantProcessIds(
            parentProcessId,
            ancestorStartTimeUtcTicks: 10,
            out var descendants);

        Assert.True(ancestryComplete);
        Assert.Equal([childProcessId], descendants);
    }

    [Fact]
    public void ProcessTreeSnapshot_AncestorIdentityMismatch_IsIncomplete()
    {
        const int parentProcessId = 100;
        const int childProcessId = 200;
        var snapshot = new WindowsRestartManager.WindowsProcessTree.ProcessTreeSnapshot(
            new Dictionary<int, List<int>>
            {
                [parentProcessId] = [childProcessId]
            },
            new Dictionary<int, long>
            {
                [parentProcessId] = 10,
                [childProcessId] = 20
            });

        var ancestryComplete = snapshot.TryGetDescendantProcessIds(
            parentProcessId,
            ancestorStartTimeUtcTicks: 11,
            out var descendants);

        Assert.False(ancestryComplete);
        Assert.Empty(descendants);
    }

    [Theory]
    [InlineData(10, 10, true)]
    [InlineData(10, 20, true)]
    [InlineData(20, 10, false)]
    public void ProcessTreeSnapshot_RejectsIdentityCreatedAfterSnapshotStarted(
        long processStartTimeUtcTicks,
        long snapshotStartedAtUtcTicks,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsRestartManager.WindowsProcessTree.WasProcessPresentWhenSnapshotStarted(
                processStartTimeUtcTicks,
                snapshotStartedAtUtcTicks));
    }

    [Fact]
    public async Task CloseBlockingProcesses_WhenIdentityIsUnavailable_ReportsFailure()
    {
        using var currentProcess = Process.GetCurrentProcess();
        var blocker = new UpdateBlockingProcess(
            currentProcess.Id,
            processStartTimeUtcTicks: null,
            currentProcess.ProcessName,
            currentProcess.MainModule?.FileName,
            canClose: true);
        var service = new UpdateBlockerService();

        var result = await service.CloseBlockingProcessesAsync([blocker]);

        Assert.Equal([currentProcess.Id], result.FailedProcessIds);
        Assert.False(currentProcess.HasExited);
    }

    [Fact]
    public async Task CloseBlockingProcesses_WhenPidWasReused_DoesNotCloseNewProcess()
    {
        using var currentProcess = Process.GetCurrentProcess();
        var blocker = new UpdateBlockingProcess(
            currentProcess.Id,
            currentProcess.StartTime.ToUniversalTime().Ticks + 1,
            currentProcess.ProcessName,
            currentProcess.MainModule?.FileName,
            canClose: true);
        var service = new UpdateBlockerService();

        var result = await service.CloseBlockingProcessesAsync([blocker]);

        Assert.Empty(result.FailedProcessIds);
        Assert.False(currentProcess.HasExited);
    }

    [SkippableFact]
    public async Task LockScan_IgnoresBundledChildProcessThatGracefulShutdownOwns()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows process-tree inspection is only available on Windows.");

        var tempDirectory = Path.Combine(Path.GetTempPath(), "Lumi-update-child-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var childExecutable = Path.Combine(tempDirectory, "lumi-child.exe");
        File.Copy(Environment.GetEnvironmentVariable("ComSpec")!, childExecutable);
        var startInfo = new ProcessStartInfo
        {
            FileName = childExecutable,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("ping 127.0.0.1 -n 30 > nul");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        try
        {
            await Task.Delay(300);
            Assert.Contains(
                process!.Id,
                WindowsRestartManager.WindowsProcessTree.GetDescendantProcessIds(Environment.ProcessId));

            var service = new UpdateBlockerService();
            var blockers = await service.FindBlockingProcessesAsync([tempDirectory]);

            Assert.DoesNotContain(blockers, candidate => candidate.ProcessId == process.Id);

            var failedProcessIds =
                UpdateBlockerService.ForceCloseManagedChildProcessesForRestart(
                    [tempDirectory],
                    updaterExecutablePath: null);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(failedProcessIds);
            Assert.True(process.HasExited);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process?.Dispose();
            await DeleteDirectoryWithRetryAsync(tempDirectory);
        }
    }

    [SkippableFact]
    public async Task ForcedUpdateShutdown_PreservesVelopackUpdaterProcess()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows process-tree inspection is only available on Windows.");

        var tempDirectory = Path.Combine(Path.GetTempPath(), "Lumi-update-fallback-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var updaterExecutable = Path.Combine(tempDirectory, "Update.exe");
        var managedChildExecutable = Path.Combine(tempDirectory, "lumi-child.exe");
        File.Copy(Environment.GetEnvironmentVariable("ComSpec")!, updaterExecutable);
        File.Copy(Environment.GetEnvironmentVariable("ComSpec")!, managedChildExecutable);

        using var updaterProcess = StartSleepingCommandProcess(updaterExecutable);
        using var managedChildProcess = StartSleepingCommandProcess(managedChildExecutable);
        try
        {
            await Task.Delay(300);
            var failedProcessIds =
                UpdateBlockerService.ForceCloseManagedChildProcessesForRestart(
                    [tempDirectory],
                    updaterExecutable);
            await managedChildProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            updaterProcess.Refresh();

            Assert.Empty(failedProcessIds);
            Assert.True(managedChildProcess.HasExited);
            Assert.False(updaterProcess.HasExited);
        }
        finally
        {
            if (updaterProcess is { HasExited: false })
            {
                updaterProcess.Kill(entireProcessTree: true);
                await updaterProcess.WaitForExitAsync();
            }

            if (managedChildProcess is { HasExited: false })
            {
                managedChildProcess.Kill(entireProcessTree: true);
                await managedChildProcess.WaitForExitAsync();
            }

            updaterProcess.Dispose();
            managedChildProcess.Dispose();
            await DeleteDirectoryWithRetryAsync(tempDirectory);
        }
    }

    [Fact]
    public void UpdateShutdownDeadline_LeavesMarginBeforeVelopackStopsWaiting()
    {
        Assert.True(
            UpdateShutdownCoordinator.DefaultGracefulCleanupTimeout
            < UpdateShutdownCoordinator.VelopackExitTimeout);
        Assert.True(
            UpdateShutdownCoordinator.VelopackExitTimeout
            - UpdateShutdownCoordinator.DefaultGracefulCleanupTimeout
            >= TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void UpdateShutdown_WhenCleanupCompletes_DoesNotForceTerminate()
    {
        var terminator = new FakeUpdateShutdownTerminator();
        var coordinator = new UpdateShutdownCoordinator(
            TimeSpan.FromSeconds(1),
            terminator);
        var synchronousCleanupCalled = false;
        var asynchronousCleanupCalled = false;

        var completedGracefully = coordinator.Run(
            () => synchronousCleanupCalled = true,
            () =>
            {
                asynchronousCleanupCalled = true;
                return Task.CompletedTask;
            });

        Assert.True(completedGracefully);
        Assert.True(synchronousCleanupCalled);
        Assert.True(asynchronousCleanupCalled);
        Assert.Equal(0, terminator.TerminateCalls);
    }

    [Fact]
    public void UpdateShutdown_WhenAsynchronousCleanupHangs_ForceTerminates()
    {
        var cleanupCompletion =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminator = new FakeUpdateShutdownTerminator();
        var coordinator = new UpdateShutdownCoordinator(
            TimeSpan.FromMilliseconds(50),
            terminator);

        var completedGracefully = coordinator.Run(
            static () => { },
            () => cleanupCompletion.Task);
        cleanupCompletion.TrySetResult();

        Assert.False(completedGracefully);
        Assert.Equal(1, terminator.TerminateCalls);
    }

    [Fact]
    public async Task UpdateShutdown_WatchdogTerminatesWhileSynchronousCleanupIsBlocked()
    {
        var terminator = new FakeUpdateShutdownTerminator();
        var coordinator = new UpdateShutdownCoordinator(
            TimeSpan.FromMilliseconds(50),
            terminator);
        using var cleanupStarted = new ManualResetEventSlim();
        using var releaseCleanup = new ManualResetEventSlim();

        var runTask = Task.Run(() => coordinator.Run(
            () =>
            {
                cleanupStarted.Set();
                releaseCleanup.Wait();
            },
            static () => Task.CompletedTask));

        Assert.True(cleanupStarted.Wait(TimeSpan.FromSeconds(1)));
        await terminator.Terminated.Task.WaitAsync(TimeSpan.FromSeconds(1));
        releaseCleanup.Set();

        Assert.False(await runTask.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, terminator.TerminateCalls);
    }

    [Fact]
    public void UpdateShutdown_RejectsDeadlineAtVelopackLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new UpdateShutdownCoordinator(UpdateShutdownCoordinator.VelopackExitTimeout));
    }

    private static UpdateService CreateReadyService(
        IUpdateBlockerService blockerService,
        IUpdateRestartLauncher restartLauncher)
    {
        var service = new UpdateService(blockerService, restartLauncher, () => [@"C:\Lumi"]);
        var locator = new TestVelopackLocator(
            "Lumi",
            "1.0.0",
            Path.Combine(Path.GetTempPath(), "Lumi-update-tests"));
        var manager = new UpdateManager(UpdateService.RepoUrl, locator: locator);
        SetPrivateField(service, "_manager", manager);

        var asset = new VelopackAsset
        {
            PackageId = "Lumi",
            Version = SemanticVersion.Parse("1.2.3"),
            Type = VelopackAssetType.Full,
            FileName = "Lumi-1.2.3-full.nupkg",
            SHA1 = "sha1",
            SHA256 = "sha256",
            Size = 1,
            NotesMarkdown = string.Empty,
            NotesHTML = string.Empty
        };
        SetPrivateField(service, "_pendingUpdate", new UpdateInfo(asset, isDowngrade: false));
        SetPrivateProperty(service, nameof(UpdateService.CurrentStatus), UpdateStatus.ReadyToRestart);
        SetPrivateProperty(service, nameof(UpdateService.AvailableVersion), "1.2.3");
        return service;
    }

    private static void SetPrivateField(object instance, string fieldName, object? value)
    {
        var field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private static void SetPrivateProperty<T>(object instance, string propertyName, T value)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        property!.SetValue(instance, value);
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(File.Exists(path), $"Timed out waiting for '{path}'.");
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (
                attempt < 20
                && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(50);
            }
        }
    }

    private static Process StartSleepingCommandProcess(string executablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("ping 127.0.0.1 -n 30 > nul");
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{executablePath}'.");
    }

    private sealed class FakeUpdateBlockerService(
        IEnumerable<IReadOnlyList<UpdateBlockingProcess>> findResults) : IUpdateBlockerService
    {
        private readonly Queue<IReadOnlyList<UpdateBlockingProcess>> _findResults = new(findResults);

        public int FindCalls { get; private set; }
        public int CloseCalls { get; private set; }

        public Task<IReadOnlyList<UpdateBlockingProcess>> FindBlockingProcessesAsync(
            IReadOnlyCollection<string> updateResourceRoots,
            CancellationToken cancellationToken = default)
        {
            FindCalls++;
            return Task.FromResult(
                _findResults.Count > 0
                    ? _findResults.Dequeue()
                    : (IReadOnlyList<UpdateBlockingProcess>)[]);
        }

        public Task<UpdateBlockerCloseResult> CloseBlockingProcessesAsync(
            IReadOnlyCollection<UpdateBlockingProcess> processes,
            CancellationToken cancellationToken = default)
        {
            CloseCalls++;
            return Task.FromResult(new UpdateBlockerCloseResult([]));
        }
    }

    private sealed class FailOnceUpdateBlockerService : IUpdateBlockerService
    {
        private int _findCalls;

        public Task<IReadOnlyList<UpdateBlockingProcess>> FindBlockingProcessesAsync(
            IReadOnlyCollection<string> updateResourceRoots,
            CancellationToken cancellationToken = default)
        {
            _findCalls++;
            return _findCalls == 1
                ? Task.FromException<IReadOnlyList<UpdateBlockingProcess>>(
                    new IOException("Blocker scan failed."))
                : Task.FromResult<IReadOnlyList<UpdateBlockingProcess>>([]);
        }

        public Task<UpdateBlockerCloseResult> CloseBlockingProcessesAsync(
            IReadOnlyCollection<UpdateBlockingProcess> processes,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateBlockerCloseResult([]));
    }

    private sealed class FakeUpdateRestartLauncher(int failuresBeforeSuccess = 0) : IUpdateRestartLauncher
    {
        private int _failuresRemaining = failuresBeforeSuccess;

        public int LaunchCalls { get; private set; }

        public void Launch(UpdateManager manager, VelopackAsset release)
        {
            LaunchCalls++;
            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                throw new InvalidOperationException("Updater launch failed.");
            }
        }
    }

    private sealed class FakeUpdateShutdownTerminator : IUpdateShutdownTerminator
    {
        public TaskCompletionSource Terminated { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int TerminateCalls { get; private set; }

        public void Terminate()
        {
            TerminateCalls++;
            Terminated.TrySetResult();
        }
    }
}
