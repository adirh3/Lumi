using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public void InstanceNames_AreCrossPlatformSafe()
    {
        using var scope = new SingleInstanceTestScope();
        var names = SingleInstanceCoordinator.CreateNamesForScope(scope.Path);

        Assert.StartsWith(@"Global\Lumi.SingleInstance.", names.MutexName);
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith("Lumi.SingleInstance.", names.PipeName);
        }
        else
        {
            Assert.StartsWith("Lumi.SI.", names.PipeName);
            Assert.InRange(names.PipeName.Length, 1, 28);
            Assert.Equal(Path.Combine(scope.Path, "instance.lock"), names.UnixLockFilePath);
        }
    }

    [SkippableFact]
    public void UnixLock_IgnoresStaleFileContents()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix uses a file lock; Windows retains its global mutex.");
        using var scope = new SingleInstanceTestScope();
        var names = SingleInstanceCoordinator.CreateNamesForScope(scope.Path);
        File.WriteAllText(names.UnixLockFilePath, "stale state from a previous process");

        using var coordinator = SingleInstanceCoordinator.CreateForScope(scope.Path);

        Assert.True(coordinator.IsPrimaryInstance);
    }

    [Fact]
    public void QueuedActivation_IsDeliveredWhenHandlerRegisters()
    {
        using var scope = new SingleInstanceTestScope();
        using var coordinator = SingleInstanceCoordinator.CreateForScope(
            scope.Path);
        var request = new AppActivationRequest(Guid.NewGuid());
        AppActivationRequest? received = null;

        Assert.True(coordinator.IsPrimaryInstance);
        coordinator.PublishActivation(request);
        coordinator.SetActivationHandler(value => received = value);

        Assert.Equal(request, received);
    }

    [Fact]
    public async Task SecondaryActivation_IsRedirectedToPrimaryInstance()
    {
        using var scope = new SingleInstanceTestScope();
        var primaryReady = new TaskCompletionSource<SingleInstanceCoordinator>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePrimary = new ManualResetEventSlim();
        Exception? primaryError = null;

        var primaryThread = new Thread(() =>
        {
            try
            {
                using var primary = SingleInstanceCoordinator.CreateForScope(scope.Path);
                primaryReady.SetResult(primary);
                releasePrimary.Wait(TimeSpan.FromSeconds(15));
            }
            catch (Exception ex)
            {
                primaryError = ex;
                primaryReady.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstanceCoordinatorTests.Primary"
        };
        primaryThread.Start();

        var primary = await primaryReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var received = new TaskCompletionSource<AppActivationRequest>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            primary.SetActivationHandler(request => received.TrySetResult(request));

            using var secondary = SingleInstanceCoordinator.CreateForScope(scope.Path);
            var expected = new AppActivationRequest(Guid.NewGuid());

            Assert.True(primary.IsPrimaryInstance);
            Assert.False(secondary.IsPrimaryInstance);
            Assert.Equal(
                ActivationRedirectResult.Accepted,
                await secondary.RedirectActivationAsync(
                expected,
                TimeSpan.FromSeconds(5)));
            Assert.Equal(
                expected,
                await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releasePrimary.Set();
            Assert.True(primaryThread.Join(TimeSpan.FromSeconds(5)));
        }

        Assert.Null(primaryError);
    }

    [Fact]
    public async Task StoppingPrimary_RejectsActivationAndAllowsTakeover()
    {
        using var scope = new SingleInstanceTestScope();
        var primaryReady = new TaskCompletionSource<SingleInstanceCoordinator>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePrimary = new ManualResetEventSlim();

        var primaryThread = new Thread(() =>
        {
            using var primary = SingleInstanceCoordinator.CreateForScope(scope.Path);
            primaryReady.SetResult(primary);
            releasePrimary.Wait(TimeSpan.FromSeconds(15));
        })
        {
            IsBackground = true,
            Name = "SingleInstanceCoordinatorTests.ShutdownPrimary"
        };
        primaryThread.Start();

        var primary = await primaryReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var secondary = SingleInstanceCoordinator.CreateForScope(scope.Path);

        try
        {
            primary.StopAcceptingActivations();

            Assert.Equal(
                ActivationRedirectResult.PrimaryShuttingDown,
                await secondary.RedirectActivationAsync(
                    new AppActivationRequest(null),
                    TimeSpan.FromSeconds(5)));

            releasePrimary.Set();
            Assert.True(secondary.TryBecomePrimary(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releasePrimary.Set();
            Assert.True(primaryThread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task RestartingPrimary_RejectsActivationWithoutOfferingTakeover()
    {
        using var scope = new SingleInstanceTestScope();
        var primaryReady = new TaskCompletionSource<SingleInstanceCoordinator>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePrimary = new ManualResetEventSlim();

        var primaryThread = new Thread(() =>
        {
            using var primary = SingleInstanceCoordinator.CreateForScope(scope.Path);
            primaryReady.SetResult(primary);
            releasePrimary.Wait(TimeSpan.FromSeconds(15));
        })
        {
            IsBackground = true,
            Name = "SingleInstanceCoordinatorTests.RestartingPrimary"
        };
        primaryThread.Start();

        var primary = await primaryReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var secondary = SingleInstanceCoordinator.CreateForScope(scope.Path);

        try
        {
            primary.StopAcceptingActivations(restartExpected: true);

            Assert.Equal(
                ActivationRedirectResult.PrimaryRestarting,
                await secondary.RedirectActivationAsync(
                    new AppActivationRequest(null),
                    TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releasePrimary.Set();
            Assert.True(primaryThread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task SecondaryActivation_IsRedirectedAcrossProcessesAndUnixSessions()
    {
        using var scope = new SingleInstanceTestScope();
        var readyFile = Path.Combine(scope.Path, "ready");
        var activationFile = Path.Combine(scope.Path, "activation");
        var releaseFile = Path.Combine(scope.Path, "release");
        using var process = StartProcessHost(scope.Path, readyFile, activationFile, releaseFile);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await WaitForFileAsync(process, readyFile, TimeSpan.FromSeconds(20));
            using var secondary = SingleInstanceCoordinator.CreateForScope(scope.Path);
            var expectedChatId = Guid.NewGuid();

            Assert.False(secondary.IsPrimaryInstance);
            Assert.Equal(
                ActivationRedirectResult.Accepted,
                await secondary.RedirectActivationAsync(
                    new AppActivationRequest(expectedChatId),
                    TimeSpan.FromSeconds(5)));
            await WaitForFileAsync(process, activationFile, TimeSpan.FromSeconds(5));
            Assert.Equal(expectedChatId.ToString("D"), await File.ReadAllTextAsync(activationFile));

            await File.WriteAllTextAsync(releaseFile, "release");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(
                process.ExitCode == 0,
                $"Single-instance helper exited with {process.ExitCode}.{Environment.NewLine}"
                + $"stdout:{Environment.NewLine}{await stdoutTask}{Environment.NewLine}"
                + $"stderr:{Environment.NewLine}{await stderrTask}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static Process StartProcessHost(
        string scope,
        string readyFile,
        string activationFile,
        string releaseFile)
    {
        var repositoryRoot = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new DirectoryNotFoundException("Could not resolve the current test configuration.");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(Path.Combine(
            repositoryRoot,
            "tests",
            "Lumi.Tests",
            "Lumi.Tests.csproj"));
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--no-restore");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add(
            "FullyQualifiedName=Lumi.Tests.SingleInstanceCoordinatorProcessHost.RunAsPrimary");
        startInfo.Environment[SingleInstanceProcessTestEnvironment.Scope] = scope;
        startInfo.Environment[SingleInstanceProcessTestEnvironment.ReadyFile] = readyFile;
        startInfo.Environment[SingleInstanceProcessTestEnvironment.ActivationFile] = activationFile;
        startInfo.Environment[SingleInstanceProcessTestEnvironment.ReleaseFile] = releaseFile;

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the single-instance test host.");
    }

    private static async Task WaitForFileAsync(
        Process process,
        string path,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (File.Exists(path))
                return;
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"Single-instance test host exited with code {process.ExitCode} before creating '{path}'.");

            await Task.Delay(25);
        }

        throw new TimeoutException($"Timed out waiting for the single-instance test host to create '{path}'.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Lumi.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Lumi repository root.");
    }
}

public sealed class SingleInstanceCoordinatorProcessHost
{
    [SkippableFact]
    public async Task RunAsPrimary()
    {
        var scope = Environment.GetEnvironmentVariable(SingleInstanceProcessTestEnvironment.Scope);
        Skip.If(string.IsNullOrWhiteSpace(scope), "Only the cross-process parent test starts this host.");

        StartNewUnixSession();
        var readyFile = GetRequiredEnvironmentVariable(SingleInstanceProcessTestEnvironment.ReadyFile);
        var activationFile = GetRequiredEnvironmentVariable(
            SingleInstanceProcessTestEnvironment.ActivationFile);
        var releaseFile = GetRequiredEnvironmentVariable(SingleInstanceProcessTestEnvironment.ReleaseFile);
        using var coordinator = SingleInstanceCoordinator.CreateForScope(scope!);
        Assert.True(coordinator.IsPrimaryInstance);
        coordinator.SetActivationHandler(request =>
        {
            File.WriteAllText(activationFile, request.ChatId?.ToString("D") ?? "");
        });
        File.WriteAllText(readyFile, Environment.ProcessId.ToString());

        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(releaseFile) && stopwatch.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(25);

        Assert.True(File.Exists(releaseFile), "The cross-process parent did not release the helper.");
    }

    private static void StartNewUnixSession()
    {
        if (OperatingSystem.IsWindows())
            return;

        if (SetSessionId() < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static string GetRequiredEnvironmentVariable(string name)
        => Environment.GetEnvironmentVariable(name)
           ?? throw new InvalidOperationException($"Missing required environment variable '{name}'.");

    [DllImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static extern int SetSessionId();
}

internal static class SingleInstanceProcessTestEnvironment
{
    internal const string Scope = "LUMI_TEST_SINGLE_INSTANCE_SCOPE";
    internal const string ReadyFile = "LUMI_TEST_SINGLE_INSTANCE_READY";
    internal const string ActivationFile = "LUMI_TEST_SINGLE_INSTANCE_ACTIVATION";
    internal const string ReleaseFile = "LUMI_TEST_SINGLE_INSTANCE_RELEASE";
}

internal sealed class SingleInstanceTestScope : IDisposable
{
    internal SingleInstanceTestScope()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Lumi-single-instance-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
