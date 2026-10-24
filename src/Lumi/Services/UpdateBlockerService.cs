using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Localization;
using Velopack.Locators;

namespace Lumi.Services;

public sealed class UpdateBlockingProcess
{
    internal UpdateBlockingProcess(
        int processId,
        long? processStartTimeUtcTicks,
        string displayName,
        string? executablePath,
        bool canClose,
        bool isSimulated = false)
    {
        ProcessId = processId;
        ProcessStartTimeUtcTicks = processStartTimeUtcTicks;
        DisplayName = displayName;
        ExecutablePath = executablePath;
        CanClose = canClose;
        IsSimulated = isSimulated;
    }

    public int ProcessId { get; }
    public string DisplayName { get; }
    public string? ExecutablePath { get; }
    public bool CanClose { get; }
    public string ProcessDetails
    {
        get
        {
            var executableName = string.IsNullOrWhiteSpace(ExecutablePath)
                ? string.Empty
                : Path.GetFileName(ExecutablePath);
            return string.IsNullOrWhiteSpace(executableName)
                ? $"PID {ProcessId}"
                : $"{executableName} · PID {ProcessId}";
        }
    }

    internal long? ProcessStartTimeUtcTicks { get; }
    internal bool IsSimulated { get; }
}

internal sealed record UpdateBlockerCloseResult(IReadOnlyList<int> FailedProcessIds);

internal enum ProcessIdentityStatus
{
    Match,
    Mismatch,
    Unavailable
}

internal interface IUpdateBlockerService
{
    Task<IReadOnlyList<UpdateBlockingProcess>> FindBlockingProcessesAsync(
        IReadOnlyCollection<string> updateResourceRoots,
        CancellationToken cancellationToken = default);

    Task<UpdateBlockerCloseResult> CloseBlockingProcessesAsync(
        IReadOnlyCollection<UpdateBlockingProcess> processes,
        CancellationToken cancellationToken = default);
}

internal sealed class UpdateBlockerService : IUpdateBlockerService
{
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ForcedCloseTimeout = TimeSpan.FromSeconds(5);

    public Task<IReadOnlyList<UpdateBlockingProcess>> FindBlockingProcessesAsync(
        IReadOnlyCollection<string> updateResourceRoots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updateResourceRoots);

        if (!OperatingSystem.IsWindows())
            return Task.FromResult<IReadOnlyList<UpdateBlockingProcess>>([]);

        return Task.Run(
            () => WindowsRestartManager.FindBlockingProcesses(updateResourceRoots, cancellationToken),
            cancellationToken);
    }

    public async Task<UpdateBlockerCloseResult> CloseBlockingProcessesAsync(
        IReadOnlyCollection<UpdateBlockingProcess> processes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processes);

        var failedProcessIds = new List<int>();
        foreach (var blocker in processes.Where(static process => process.CanClose))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (blocker.IsSimulated)
                continue;

            Process? process = null;
            try
            {
                process = Process.GetProcessById(blocker.ProcessId);
                var identityStatus = GetProcessIdentityStatus(process, blocker.ProcessStartTimeUtcTicks);
                if (identityStatus == ProcessIdentityStatus.Mismatch)
                    continue;
                if (identityStatus == ProcessIdentityStatus.Unavailable)
                {
                    Trace.TraceWarning(
                        $"[UpdateService] Could not verify process {blocker.ProcessId} ({blocker.DisplayName}) before closing it.");
                    failedProcessIds.Add(blocker.ProcessId);
                    continue;
                }

                if (!process.HasExited && process.CloseMainWindow())
                    await WaitForExitAsync(process, GracefulCloseTimeout, cancellationToken);

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: ShouldKillEntireProcessTree(
                        process.Id,
                        blocker.ProcessStartTimeUtcTicks!.Value));
                    await WaitForExitAsync(process, ForcedCloseTimeout, cancellationToken);
                }

                if (!process.HasExited)
                    failedProcessIds.Add(blocker.ProcessId);
            }
            catch (ArgumentException)
            {
                // The process exited between detection and the close request.
            }
            catch (InvalidOperationException)
            {
                // The process exited while its state was being inspected.
            }
            catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
            {
                Trace.TraceWarning(
                    $"[UpdateService] Could not close process {blocker.ProcessId} ({blocker.DisplayName}): {ex.Message}");
                failedProcessIds.Add(blocker.ProcessId);
            }
            finally
            {
                process?.Dispose();
            }
        }

        return new UpdateBlockerCloseResult(failedProcessIds);
    }

    private static bool ShouldKillEntireProcessTree(
        int blockerProcessId,
        long blockerStartTimeUtcTicks)
    {
        var ancestryComplete =
            WindowsRestartManager.WindowsProcessTree.TryCaptureCompleteDescendantTree(
                blockerProcessId,
                blockerStartTimeUtcTicks,
                out _,
                out var descendantProcessIds);
        return CanSafelyKillEntireProcessTree(
            ancestryComplete,
            descendantProcessIds,
            Environment.ProcessId);
    }

    internal static bool CanSafelyKillEntireProcessTree(
        bool ancestryComplete,
        IReadOnlySet<int> descendantProcessIds,
        int currentProcessId)
    {
        ArgumentNullException.ThrowIfNull(descendantProcessIds);
        return ancestryComplete && !descendantProcessIds.Contains(currentProcessId);
    }

    internal static IReadOnlyList<int> ForceCloseManagedChildProcessesForRestart(
        IReadOnlyCollection<string> updateResourceRoots,
        string? updaterExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(updateResourceRoots);

        if (!OperatingSystem.IsWindows())
            return [];

        var normalizedRoots = updateResourceRoots
            .Select(TryNormalizePath)
            .Where(static path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedRoots.Length == 0)
            return [];

        var normalizedUpdaterPath = TryNormalizePath(updaterExecutablePath);
        if (!WindowsRestartManager.WindowsProcessTree.TryCapture(out var initialProcessTree))
            return [];

        var initialTreeComplete = initialProcessTree.TryGetDescendantProcessIds(
            Environment.ProcessId,
            out var descendantProcessIds);
        if (!initialTreeComplete)
        {
            Trace.TraceInformation(
                "[UpdateService] Lumi's initial managed process tree was incomplete; forced cleanup will consider only verified descendants.");
        }

        var candidates = new List<(Process Process, string ExecutablePath, long StartTimeUtcTicks)>();

        foreach (var processId in descendantProcessIds)
        {
            Process? process = null;
            try
            {
                process = Process.GetProcessById(processId);
                var executablePath = TryNormalizePath(process.MainModule?.FileName);
                if (executablePath is null)
                    continue;
                var startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                if (!initialProcessTree.MatchesIdentity(processId, startTimeUtcTicks))
                    continue;

                if (!normalizedRoots.Any(root =>
                        InstalledAppWorkingDirectory.IsPathInsideRoot(executablePath, root))
                    && !string.Equals(
                        executablePath,
                        normalizedUpdaterPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidates.Add((process, executablePath, startTimeUtcTicks));
                process = null;
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or InvalidOperationException
                                       or Win32Exception
                                       or NotSupportedException)
            {
                Trace.TraceInformation(
                    $"[UpdateService] Could not inspect managed child process {processId}: {ex.Message}");
            }
            finally
            {
                process?.Dispose();
            }
        }

        var failedProcessIds = new List<int>();
        var terminationRequested = new List<Process>();
        try
        {
            if (!WindowsRestartManager.WindowsProcessTree.TryCapture(out var currentProcessTree))
                return GetUnstoppedCandidateProcessIds();

            var currentTreeComplete = currentProcessTree.TryGetDescendantProcessIds(
                Environment.ProcessId,
                out var currentDescendantProcessIds);
            if (!currentTreeComplete)
            {
                Trace.TraceInformation(
                    "[UpdateService] Lumi's current managed process tree was incomplete; forced cleanup will act only on verified descendants.");
            }

            var excludedProcessIds = new HashSet<int>();
            if (normalizedUpdaterPath is not null)
            {
                var updaterProcessVerified = false;
                foreach (var candidate in candidates.Where(candidate =>
                             string.Equals(
                                 candidate.ExecutablePath,
                                 normalizedUpdaterPath,
                                 StringComparison.OrdinalIgnoreCase)))
                {
                    if (!currentDescendantProcessIds.Contains(candidate.Process.Id)
                        || !currentProcessTree.MatchesIdentity(
                            candidate.Process.Id,
                            candidate.StartTimeUtcTicks))
                    {
                        continue;
                    }

                    updaterProcessVerified = true;
                    excludedProcessIds.Add(candidate.Process.Id);
                    if (!currentProcessTree.TryGetDescendantProcessIds(
                            candidate.Process.Id,
                            candidate.StartTimeUtcTicks,
                            out var updaterDescendantProcessIds)
                        && !WindowsRestartManager.WindowsProcessTree.TryCaptureCompleteDescendantTree(
                            candidate.Process.Id,
                            candidate.StartTimeUtcTicks,
                            out _,
                            out updaterDescendantProcessIds))
                    {
                        Trace.TraceWarning(
                            $"[UpdateService] Could not completely verify the Velopack updater process tree rooted at {candidate.Process.Id}; managed child cleanup was skipped.");
                        return GetUnstoppedCandidateProcessIds(excludedProcessIds);
                    }

                    excludedProcessIds.UnionWith(updaterDescendantProcessIds);
                }

                if (!updaterProcessVerified)
                {
                    Trace.TraceWarning(
                        "[UpdateService] Could not verify the Velopack updater process; managed child cleanup was skipped.");
                    return GetUnstoppedCandidateProcessIds();
                }
            }

            foreach (var candidate in candidates)
            {
                var process = candidate.Process;
                if (excludedProcessIds.Contains(process.Id))
                    continue;

                try
                {
                    if (process.HasExited)
                        continue;
                    var candidateTreeComplete =
                        currentProcessTree.TryGetDescendantProcessIds(
                            process.Id,
                            candidate.StartTimeUtcTicks,
                            out var candidateDescendantProcessIds);
                    if (!currentDescendantProcessIds.Contains(process.Id)
                        || !currentProcessTree.MatchesIdentity(
                            process.Id,
                            candidate.StartTimeUtcTicks)
                        || GetProcessIdentityStatus(process, candidate.StartTimeUtcTicks)
                        != ProcessIdentityStatus.Match)
                    {
                        failedProcessIds.Add(process.Id);
                        continue;
                    }
                    if (candidateDescendantProcessIds.Any(excludedProcessIds.Contains))
                    {
                        Trace.TraceWarning(
                            $"[UpdateService] Did not stop managed child process {process.Id} because it owns the Velopack updater process.");
                        failedProcessIds.Add(process.Id);
                        continue;
                    }

                    if (!candidateTreeComplete)
                    {
                        Trace.TraceWarning(
                            $"[UpdateService] Process ancestry for managed child {process.Id} was incomplete; stopping only that process.");
                    }

                    process.Kill(entireProcessTree: candidateTreeComplete);
                    terminationRequested.Add(process);
                }
                catch (InvalidOperationException)
                {
                    // The process exited between inspection and termination.
                }
                catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
                {
                    Trace.TraceWarning(
                        $"[UpdateService] Could not stop managed child process {process.Id}: {ex.Message}");
                    failedProcessIds.Add(process.Id);
                }
            }

            var deadline = DateTime.UtcNow + ForcedCloseTimeout;
            foreach (var process in terminationRequested)
            {
                try
                {
                    if (process.HasExited)
                        continue;

                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero
                        || !process.WaitForExit((int)Math.Min(remaining.TotalMilliseconds, int.MaxValue)))
                    {
                        failedProcessIds.Add(process.Id);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited while its state was being inspected.
                }
            }
        }
        finally
        {
            foreach (var candidate in candidates)
                candidate.Process.Dispose();
        }

        return failedProcessIds.Distinct().ToArray();

        IReadOnlyList<int> GetUnstoppedCandidateProcessIds(
            IReadOnlySet<int>? processIdsToExclude = null)
        {
            return candidates
                .Where(candidate =>
                    (processIdsToExclude is null
                        || !processIdsToExclude.Contains(candidate.Process.Id))
                    && !string.Equals(
                        candidate.ExecutablePath,
                        normalizedUpdaterPath,
                        StringComparison.OrdinalIgnoreCase))
                .Select(static candidate => candidate.Process.Id)
                .Distinct()
                .ToArray();
        }
    }

    private static ProcessIdentityStatus GetProcessIdentityStatus(
        Process process,
        long? expectedStartTimeUtcTicks)
    {
        if (!expectedStartTimeUtcTicks.HasValue)
            return ProcessIdentityStatus.Unavailable;

        try
        {
            var actualTicks = process.StartTime.ToUniversalTime().Ticks;
            return actualTicks == expectedStartTimeUtcTicks.Value
                ? ProcessIdentityStatus.Match
                : ProcessIdentityStatus.Mismatch;
        }
        catch (InvalidOperationException)
        {
            return ProcessIdentityStatus.Unavailable;
        }
        catch (Win32Exception)
        {
            return ProcessIdentityStatus.Unavailable;
        }
    }

    private static string? TryNormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return null;
        }
    }

    private static async Task WaitForExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (process.HasExited)
            return;

        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            // The caller decides whether to force-close or report the process as still running.
        }
    }
}

internal static class VelopackUpdatePathResolver
{
    public static IReadOnlyList<string> GetUpdateResourceRoots()
    {
        var locator = VelopackLocator.Current;
        var candidates = new[]
        {
            locator.RootAppDir,
            locator.AppContentDir,
            locator.PackagesDir,
            locator.AppTempDir,
            locator.UpdateExePath
        };

        var normalized = candidates
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Path.GetFullPath(path!))
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .OrderBy(static path => path.Length)
            .ToList();

        var roots = new List<string>();
        foreach (var candidate in normalized)
        {
            if (roots.Any(root => InstalledAppWorkingDirectory.IsPathInsideRoot(candidate, root)))
                continue;

            roots.Add(candidate);
        }

        return roots;
    }
}

internal static class WindowsRestartManager
{
    private const int ErrorSuccess = 0;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorMoreData = 234;
    private const int SessionKeyLength = 32;
    private const int RegistrationBatchSize = 128;

    private static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss",
        "dwm",
        "explorer",
        "lsass",
        "registry",
        "services",
        "smss",
        "svchost",
        "system",
        "wininit",
        "winlogon"
    };

    public static IReadOnlyList<UpdateBlockingProcess> FindBlockingProcesses(
        IReadOnlyCollection<string> updateResourceRoots,
        CancellationToken cancellationToken)
    {
        var fileBlockers = FindFileBlockers(updateResourceRoots, cancellationToken);
        var workingDirectoryBlockers =
            WindowsProcessWorkingDirectoryScanner.FindBlockingProcesses(
                updateResourceRoots,
                cancellationToken);
        var processTree = WindowsProcessTree.TryCapture(out var capturedProcessTree)
            ? capturedProcessTree
            : WindowsProcessTree.ProcessTreeSnapshot.Empty;
        var descendantProcessIds = processTree.GetDescendantProcessIds(Environment.ProcessId);

        return fileBlockers
            .Concat(workingDirectoryBlockers)
            .GroupBy(static process => process.ProcessId)
            .Select(static group => group.First())
            .Where(process => !IsManagedInstalledChild(
                process,
                descendantProcessIds,
                processTree,
                updateResourceRoots))
            .OrderBy(static process => process.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static process => process.ProcessId)
            .ToArray();
    }

    internal static bool IsProtectedProcessName(string processName)
        => ProtectedProcessNames.Contains(processName);

    private static bool IsManagedInstalledChild(
        UpdateBlockingProcess process,
        IReadOnlySet<int> descendantProcessIds,
        WindowsProcessTree.ProcessTreeSnapshot processTree,
        IReadOnlyCollection<string> updateResourceRoots)
    {
        if (!descendantProcessIds.Contains(process.ProcessId)
            || !process.ProcessStartTimeUtcTicks.HasValue
            || !processTree.MatchesIdentity(
                process.ProcessId,
                process.ProcessStartTimeUtcTicks.Value)
            || string.IsNullOrWhiteSpace(process.ExecutablePath))
        {
            return false;
        }

        return updateResourceRoots.Any(root =>
            InstalledAppWorkingDirectory.IsPathInsideRoot(process.ExecutablePath, root));
    }

    private static IReadOnlyList<UpdateBlockingProcess> FindFileBlockers(
        IReadOnlyCollection<string> updateResourceRoots,
        CancellationToken cancellationToken)
    {
        var files = CollectResourceFiles(updateResourceRoots, cancellationToken);
        if (files.Count == 0)
            return [];

        var sessionKey = new StringBuilder(SessionKeyLength + 1);
        var result = RmStartSession(out var sessionHandle, 0, sessionKey);
        if (result != ErrorSuccess)
            throw new Win32Exception(result, Loc.Update_BlockerScanStartFailed);

        try
        {
            var registeredCount = RegisterFiles(sessionHandle, files, cancellationToken);
            if (registeredCount == 0)
                return [];

            var processInfos = GetProcessList(sessionHandle);
            var currentProcessId = Environment.ProcessId;
            return processInfos
                .Where(info => info.Process.dwProcessId > 0 && info.Process.dwProcessId != currentProcessId)
                .Select(CreateBlockingProcess)
                .Where(static process => process is not null)
                .Cast<UpdateBlockingProcess>()
                .GroupBy(static process => process.ProcessId)
                .Select(static group => group.First())
                .ToArray();
        }
        finally
        {
            RmEndSession(sessionHandle);
        }
    }

    private static IReadOnlyList<string> CollectResourceFiles(
        IReadOnlyCollection<string> updateResourceRoots,
        CancellationToken cancellationToken)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var resourceRoot in updateResourceRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string normalizedRoot;
            try
            {
                normalizedRoot = Path.GetFullPath(resourceRoot);
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or NotSupportedException
                                       or PathTooLongException)
            {
                Trace.TraceWarning($"[UpdateService] Ignoring invalid update path '{resourceRoot}': {ex.Message}");
                continue;
            }

            if (File.Exists(normalizedRoot))
            {
                files.Add(normalizedRoot);
                continue;
            }

            if (!Directory.Exists(normalizedRoot))
                continue;

            try
            {
                foreach (var file in Directory.EnumerateFiles(normalizedRoot, "*", enumerationOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    files.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or DirectoryNotFoundException)
            {
                Trace.TraceWarning(
                    $"[UpdateService] Could not enumerate every update file under '{normalizedRoot}': {ex.Message}");
            }
        }

        return files.ToArray();
    }

    private static int RegisterFiles(
        uint sessionHandle,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        var registeredCount = 0;
        foreach (var batch in files.Chunk(RegistrationBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var existingFiles = batch.Where(File.Exists).ToArray();
            if (existingFiles.Length == 0)
                continue;

            var result = RmRegisterResources(
                sessionHandle,
                (uint)existingFiles.Length,
                existingFiles,
                0,
                null,
                0,
                null);

            if (result == ErrorSuccess)
            {
                registeredCount += existingFiles.Length;
                continue;
            }

            if (result is not (ErrorFileNotFound or ErrorPathNotFound or ErrorAccessDenied))
                throw new Win32Exception(result, Loc.Update_BlockerInspectFailed);

            foreach (var file in existingFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var singleResult = RmRegisterResources(sessionHandle, 1, [file], 0, null, 0, null);
                if (singleResult == ErrorSuccess)
                {
                    registeredCount++;
                    continue;
                }

                Trace.TraceWarning(
                    $"[UpdateService] Restart Manager skipped '{file}' (error {singleResult}).");
            }
        }

        return registeredCount;
    }

    private static IReadOnlyList<RM_PROCESS_INFO> GetProcessList(uint sessionHandle)
    {
        uint processInfoNeeded = 0;
        uint processInfoCount = 0;
        uint rebootReasons = 0;
        var result = RmGetList(
            sessionHandle,
            out processInfoNeeded,
            ref processInfoCount,
            null,
            ref rebootReasons);

        if (result == ErrorSuccess)
            return [];
        if (result != ErrorMoreData)
            throw new Win32Exception(result, Loc.Update_BlockerListFailed);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var processInfos = new RM_PROCESS_INFO[processInfoNeeded];
            processInfoCount = processInfoNeeded;
            result = RmGetList(
                sessionHandle,
                out processInfoNeeded,
                ref processInfoCount,
                processInfos,
                ref rebootReasons);

            if (result == ErrorSuccess)
                return processInfos.Take((int)processInfoCount).ToArray();
            if (result != ErrorMoreData)
                throw new Win32Exception(result, Loc.Update_BlockerListFailed);
        }

        throw new Win32Exception(ErrorMoreData, Loc.Update_BlockerListChanged);
    }

    private static UpdateBlockingProcess? CreateBlockingProcess(RM_PROCESS_INFO processInfo)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processInfo.Process.dwProcessId);
            var processName = process.ProcessName;
            var displayName = string.IsNullOrWhiteSpace(processInfo.strAppName)
                ? processName
                : processInfo.strAppName.Trim();

            string? executablePath = null;
            try
            {
                executablePath = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is Win32Exception
                                       or InvalidOperationException
                                       or NotSupportedException)
            {
                Trace.TraceInformation(
                    $"[UpdateService] Could not read the executable path for process {process.Id}: {ex.Message}");
            }

            var processStartTimeUtcTicks = ToUtcTicks(processInfo.Process.ProcessStartTime);
            var canClose = processStartTimeUtcTicks.HasValue
                && processInfo.ApplicationType is not (
                    RestartManagerApplicationType.Service
                    or RestartManagerApplicationType.Explorer
                    or RestartManagerApplicationType.Critical)
                && !ProtectedProcessNames.Contains(processName);

            return new UpdateBlockingProcess(
                process.Id,
                processStartTimeUtcTicks,
                displayName,
                executablePath,
                canClose);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception ex)
        {
            Trace.TraceInformation(
                $"[UpdateService] Could not inspect blocking process {processInfo.Process.dwProcessId}: {ex.Message}");
            return CreateUninspectableBlockingProcess(processInfo);
        }
        catch (NotSupportedException ex)
        {
            Trace.TraceInformation(
                $"[UpdateService] Could not inspect blocking process {processInfo.Process.dwProcessId}: {ex.Message}");
            return CreateUninspectableBlockingProcess(processInfo);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static UpdateBlockingProcess CreateUninspectableBlockingProcess(
        RM_PROCESS_INFO processInfo)
    {
        var processId = processInfo.Process.dwProcessId;
        var displayName = string.IsNullOrWhiteSpace(processInfo.strAppName)
            ? string.Format(Loc.Update_ProcessName, processId)
            : processInfo.strAppName.Trim();
        return new UpdateBlockingProcess(
            processId,
            ToUtcTicks(processInfo.Process.ProcessStartTime),
            displayName,
            executablePath: null,
            canClose: false);
    }

    internal static class WindowsProcessTree
    {
        private const uint SnapshotProcesses = 0x00000002;
        private const int ErrorNoMoreFiles = 18;
        private const int CompleteSnapshotAttempts = 2;
        private static readonly IntPtr InvalidHandleValue = new(-1);

        public static IReadOnlySet<int> GetDescendantProcessIds(int ancestorProcessId)
            => TryCapture(out var snapshot)
                ? snapshot.GetDescendantProcessIds(ancestorProcessId)
                : new HashSet<int>();

        internal static bool TryGetDescendantProcessIds(
            int ancestorProcessId,
            out IReadOnlySet<int> descendants)
        {
            if (TryCapture(out var snapshot))
                return snapshot.TryGetDescendantProcessIds(ancestorProcessId, out descendants);

            descendants = new HashSet<int>();
            return false;
        }

        internal static bool TryCaptureCompleteDescendantTree(
            int ancestorProcessId,
            out ProcessTreeSnapshot processTree,
            out IReadOnlySet<int> descendants)
            => TryCaptureCompleteDescendantTreeCore(
                ancestorProcessId,
                expectedStartTimeUtcTicks: null,
                out processTree,
                out descendants);

        internal static bool TryCaptureCompleteDescendantTree(
            int ancestorProcessId,
            long ancestorStartTimeUtcTicks,
            out ProcessTreeSnapshot processTree,
            out IReadOnlySet<int> descendants)
            => TryCaptureCompleteDescendantTreeCore(
                ancestorProcessId,
                ancestorStartTimeUtcTicks,
                out processTree,
                out descendants);

        private static bool TryCaptureCompleteDescendantTreeCore(
            int ancestorProcessId,
            long? expectedStartTimeUtcTicks,
            out ProcessTreeSnapshot processTree,
            out IReadOnlySet<int> descendants)
        {
            processTree = ProcessTreeSnapshot.Empty;
            descendants = new HashSet<int>();
            for (var attempt = 0; attempt < CompleteSnapshotAttempts; attempt++)
            {
                if (!TryCapture(out var capturedProcessTree))
                    continue;

                var traversalComplete = expectedStartTimeUtcTicks.HasValue
                    ? capturedProcessTree.TryGetDescendantProcessIds(
                        ancestorProcessId,
                        expectedStartTimeUtcTicks.Value,
                        out var capturedDescendants)
                    : capturedProcessTree.TryGetDescendantProcessIds(
                        ancestorProcessId,
                        out capturedDescendants);
                processTree = capturedProcessTree;
                descendants = capturedDescendants;
                if (traversalComplete)
                    return true;
            }

            return false;
        }

        internal static bool TryCapture(out ProcessTreeSnapshot processTree)
        {
            processTree = ProcessTreeSnapshot.Empty;
            if (!OperatingSystem.IsWindows())
                return true;

            var snapshotStartedAtUtcTicks = DateTime.UtcNow.Ticks;
            var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
            if (snapshot == InvalidHandleValue)
            {
                Trace.TraceWarning(
                    $"[UpdateService] Could not snapshot child processes (error {Marshal.GetLastWin32Error()}).");
                return false;
            }

            try
            {
                var childrenByParent = new Dictionary<int, List<int>>();
                var processIds = new HashSet<int>();
                var entry = new PROCESSENTRY32 { Size = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (!Process32First(snapshot, ref entry))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreFiles)
                        return true;

                    Trace.TraceWarning(
                        $"[UpdateService] Could not read the process snapshot (error {error}).");
                    return false;
                }

                while (true)
                {
                    var processId = unchecked((int)entry.ProcessId);
                    var parentProcessId = unchecked((int)entry.ParentProcessId);
                    processIds.Add(processId);
                    if (!childrenByParent.TryGetValue(parentProcessId, out var children))
                    {
                        children = [];
                        childrenByParent[parentProcessId] = children;
                    }

                    children.Add(processId);
                    entry.Size = (uint)Marshal.SizeOf<PROCESSENTRY32>();
                    if (Process32Next(snapshot, ref entry))
                        continue;

                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorNoMoreFiles)
                    {
                        Trace.TraceWarning(
                            $"[UpdateService] Process snapshot enumeration stopped early (error {error}).");
                        return false;
                    }

                    break;
                }

                var processStartTimesUtcTicks = new Dictionary<int, long>();
                foreach (var processId in processIds)
                {
                    if (TryGetProcessStartTimeUtcTicks(processId, out var startTimeUtcTicks)
                        && WasProcessPresentWhenSnapshotStarted(
                            startTimeUtcTicks,
                            snapshotStartedAtUtcTicks))
                    {
                        processStartTimesUtcTicks[processId] = startTimeUtcTicks;
                    }
                }

                processTree = new ProcessTreeSnapshot(
                    childrenByParent,
                    processStartTimesUtcTicks);
                return true;
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }

        internal static bool WasProcessPresentWhenSnapshotStarted(
            long processStartTimeUtcTicks,
            long snapshotStartedAtUtcTicks)
            => processStartTimeUtcTicks <= snapshotStartedAtUtcTicks;

        private static bool TryGetProcessStartTimeUtcTicks(
            int processId,
            out long startTimeUtcTicks)
        {
            startTimeUtcTicks = 0;
            try
            {
                using var process = Process.GetProcessById(processId);
                startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or InvalidOperationException
                                       or Win32Exception
                                       or NotSupportedException)
            {
                return false;
            }
        }

        internal sealed class ProcessTreeSnapshot(
            IReadOnlyDictionary<int, List<int>> childrenByParent,
            IReadOnlyDictionary<int, long> processStartTimesUtcTicks)
        {
            public static ProcessTreeSnapshot Empty { get; } =
                new(
                    new Dictionary<int, List<int>>(),
                    new Dictionary<int, long>());

            public IReadOnlySet<int> GetDescendantProcessIds(int ancestorProcessId)
            {
                TryGetDescendantProcessIds(ancestorProcessId, out var descendants);
                return descendants;
            }

            public IReadOnlySet<int> GetDescendantProcessIds(
                int ancestorProcessId,
                long ancestorStartTimeUtcTicks)
            {
                TryGetDescendantProcessIds(
                    ancestorProcessId,
                    ancestorStartTimeUtcTicks,
                    out var descendants);
                return descendants;
            }

            public bool TryGetDescendantProcessIds(
                int ancestorProcessId,
                out IReadOnlySet<int> descendants)
            {
                if (!processStartTimesUtcTicks.TryGetValue(
                        ancestorProcessId,
                        out var ancestorStartTimeUtcTicks))
                {
                    descendants = new HashSet<int>();
                    return false;
                }

                return TryGetDescendantProcessIds(
                    ancestorProcessId,
                    ancestorStartTimeUtcTicks,
                    out descendants);
            }

            public bool TryGetDescendantProcessIds(
                int ancestorProcessId,
                long ancestorStartTimeUtcTicks,
                out IReadOnlySet<int> descendants)
            {
                var descendantProcessIds = new HashSet<int>();
                descendants = descendantProcessIds;
                if (!MatchesIdentity(ancestorProcessId, ancestorStartTimeUtcTicks))
                    return false;

                var traversalComplete = true;
                var visited = new HashSet<int> { ancestorProcessId };
                var pending = new Queue<(int ProcessId, long StartTimeUtcTicks)>();
                pending.Enqueue((ancestorProcessId, ancestorStartTimeUtcTicks));
                while (pending.Count > 0)
                {
                    var parent = pending.Dequeue();
                    if (!childrenByParent.TryGetValue(parent.ProcessId, out var children))
                        continue;

                    foreach (var child in children)
                    {
                        if (!processStartTimesUtcTicks.TryGetValue(
                                child,
                                out var childStartTimeUtcTicks))
                        {
                            traversalComplete = false;
                            continue;
                        }

                        if (childStartTimeUtcTicks < parent.StartTimeUtcTicks)
                        {
                            continue;
                        }

                        if (visited.Add(child))
                        {
                            descendantProcessIds.Add(child);
                            pending.Enqueue((child, childStartTimeUtcTicks));
                        }
                    }
                }

                return traversalComplete;
            }

            public bool MatchesIdentity(int processId, long startTimeUtcTicks)
                => processStartTimesUtcTicks.TryGetValue(processId, out var capturedStartTimeUtcTicks)
                    && capturedStartTimeUtcTicks == startTimeUtcTicks;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint Size;
            public uint UsageCount;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint ThreadCount;
            public uint ParentProcessId;
            public int BasePriority;
            public uint Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }
    }

    internal static class WindowsProcessWorkingDirectoryScanner
    {
        private const uint ProcessQueryInformation = 0x0400;
        private const uint ProcessVmRead = 0x0010;
        private const int ProcessBasicInformation = 0;
        private const int ProcessWow64Information = 26;
        private const ushort ImageFileMachineI386 = 0x014c;
        private const ushort ImageFileMachineArmNt = 0x01c4;

        public static IReadOnlyList<UpdateBlockingProcess> FindBlockingProcesses(
            IReadOnlyCollection<string> updateResourceRoots,
            CancellationToken cancellationToken)
        {
            var directoryRoots = NormalizeDirectoryRoots(updateResourceRoots);
            if (directoryRoots.Count == 0)
                return [];

            var currentProcessId = Environment.ProcessId;
            var blockers = new List<UpdateBlockingProcess>();
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (process.Id == currentProcessId
                        || !TryGetCurrentDirectory(process.Id, out var currentDirectory)
                        || !directoryRoots.Any(root =>
                            InstalledAppWorkingDirectory.IsPathInsideRoot(currentDirectory, root)))
                    {
                        continue;
                    }

                    var processName = string.Format(Loc.Update_ProcessName, process.Id);
                    var processNameIsKnown = false;
                    try
                    {
                        processName = process.ProcessName;
                        processNameIsKnown = true;
                    }
                    catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
                    {
                        Trace.TraceInformation(
                            $"[UpdateService] Could not read the name for process {process.Id}: {ex.Message}");
                    }

                    string? executablePath = null;
                    try
                    {
                        executablePath = process.MainModule?.FileName;
                    }
                    catch (Exception ex) when (ex is Win32Exception
                                               or InvalidOperationException
                                               or NotSupportedException)
                    {
                        Trace.TraceInformation(
                            $"[UpdateService] Could not read the executable path for process {process.Id}: {ex.Message}");
                    }

                    long? startTimeUtcTicks = null;
                    try
                    {
                        startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                    {
                        Trace.TraceInformation(
                            $"[UpdateService] Could not read the start time for process {process.Id}: {ex.Message}");
                    }

                    blockers.Add(new UpdateBlockingProcess(
                        process.Id,
                        startTimeUtcTicks,
                        processName,
                        executablePath,
                        canClose: startTimeUtcTicks.HasValue
                            && processNameIsKnown
                            && !WindowsRestartManager.IsProtectedProcessName(processName)));
                }
                catch (Exception ex) when (ex is ArgumentException
                                           or InvalidOperationException
                                           or Win32Exception
                                           or NotSupportedException)
                {
                    Trace.TraceInformation(
                        $"[UpdateService] Could not inspect process {SafeGetProcessId(process)} working directory: {ex.Message}");
                }
                finally
                {
                    process.Dispose();
                }
            }

            return blockers;
        }

        private static IReadOnlyList<string> NormalizeDirectoryRoots(
            IReadOnlyCollection<string> updateResourceRoots)
        {
            var comparer = StringComparer.OrdinalIgnoreCase;
            return updateResourceRoots
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(static path =>
                {
                    var normalized = Path.GetFullPath(path);
                    return File.Exists(normalized)
                        ? Path.GetDirectoryName(normalized)
                        : normalized;
                })
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .Distinct(comparer)
                .ToArray();
        }

        private static int SafeGetProcessId(Process process)
        {
            try
            {
                return process.Id;
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }

        private static bool TryGetCurrentDirectory(int processId, out string currentDirectory)
        {
            currentDirectory = string.Empty;
            var processHandle = OpenProcess(
                ProcessQueryInformation | ProcessVmRead,
                inheritHandle: false,
                processId);
            if (processHandle == IntPtr.Zero)
                return false;

            try
            {
                if (!TryGetPebAddress(processHandle, out var pebAddress, out var is32BitProcess)
                    || !TryReadPointer(
                        processHandle,
                        Add(pebAddress, is32BitProcess ? 0x10 : 0x20),
                        is32BitProcess,
                        out var processParametersAddress)
                    || processParametersAddress == 0)
                {
                    return false;
                }

                var unicodeStringAddress = Add(
                    processParametersAddress,
                    is32BitProcess ? 0x24 : 0x38);
                var unicodeStringSize = is32BitProcess ? 8 : 16;
                var unicodeString = new byte[unicodeStringSize];
                if (!TryReadMemory(processHandle, unicodeStringAddress, unicodeString))
                    return false;

                var byteLength = BitConverter.ToUInt16(unicodeString, 0);
                var bufferAddress = is32BitProcess
                    ? BitConverter.ToUInt32(unicodeString, 4)
                    : BitConverter.ToUInt64(unicodeString, 8);
                if (byteLength == 0 || byteLength > 32768 || bufferAddress == 0)
                    return false;

                var buffer = new byte[byteLength];
                if (!TryReadMemory(processHandle, bufferAddress, buffer))
                    return false;

                currentDirectory = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
                return !string.IsNullOrWhiteSpace(currentDirectory);
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }

        private static bool TryGetPebAddress(
            IntPtr processHandle,
            out ulong pebAddress,
            out bool is32BitProcess)
        {
            pebAddress = 0;
            is32BitProcess = false;

            if (IsWow64Process2(processHandle, out var processMachine, out _)
                && processMachine is ImageFileMachineI386 or ImageFileMachineArmNt)
            {
                var status = NtQueryInformationProcessWow64(
                    processHandle,
                    ProcessWow64Information,
                    out var wow64PebAddress,
                    IntPtr.Size,
                    out _);
                if (status != 0 || wow64PebAddress == IntPtr.Zero)
                    return false;

                pebAddress = unchecked((ulong)wow64PebAddress.ToInt64());
                is32BitProcess = true;
                return true;
            }

            var basicStatus = NtQueryInformationProcessBasic(
                processHandle,
                ProcessBasicInformation,
                out var basicInformation,
                Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(),
                out _);
            if (basicStatus != 0 || basicInformation.PebBaseAddress == IntPtr.Zero)
                return false;

            pebAddress = unchecked((ulong)basicInformation.PebBaseAddress.ToInt64());
            return true;
        }

        private static bool TryReadPointer(
            IntPtr processHandle,
            ulong address,
            bool is32BitPointer,
            out ulong pointer)
        {
            var buffer = new byte[is32BitPointer ? 4 : 8];
            if (!TryReadMemory(processHandle, address, buffer))
            {
                pointer = 0;
                return false;
            }

            pointer = is32BitPointer
                ? BitConverter.ToUInt32(buffer, 0)
                : BitConverter.ToUInt64(buffer, 0);
            return true;
        }

        private static bool TryReadMemory(IntPtr processHandle, ulong address, byte[] buffer)
        {
            if (address == 0)
                return false;

            return ReadProcessMemory(
                processHandle,
                new IntPtr(unchecked((long)address)),
                buffer,
                buffer.Length,
                out var bytesRead)
                && bytesRead.ToInt64() == buffer.Length;
        }

        private static ulong Add(ulong address, int offset)
            => checked(address + (uint)offset);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(
            IntPtr processHandle,
            IntPtr baseAddress,
            [Out] byte[] buffer,
            int size,
            out IntPtr bytesRead);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process2(
            IntPtr processHandle,
            out ushort processMachine,
            out ushort nativeMachine);

        [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
        private static extern int NtQueryInformationProcessBasic(
            IntPtr processHandle,
            int processInformationClass,
            out PROCESS_BASIC_INFORMATION processInformation,
            int processInformationLength,
            out int returnLength);

        [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
        private static extern int NtQueryInformationProcessWow64(
            IntPtr processHandle,
            int processInformationClass,
            out IntPtr processInformation,
            int processInformationLength,
            out int returnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr Reserved3;
        }
    }

    private static long? ToUtcTicks(FILETIME fileTime)
    {
        var rawFileTime = ((long)(uint)fileTime.dwHighDateTime << 32) | (uint)fileTime.dwLowDateTime;
        if (rawFileTime <= 0)
            return null;

        try
        {
            return DateTime.FromFileTimeUtc(rawFileTime).Ticks;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(
        out uint sessionHandle,
        int sessionFlags,
        StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint sessionHandle,
        uint fileCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)]
        string[]? fileNames,
        uint applicationCount,
        [In] RM_UNIQUE_PROCESS[]? applications,
        uint serviceCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)]
        string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint sessionHandle,
        out uint processInfoNeeded,
        ref uint processInfoCount,
        [In, Out] RM_PROCESS_INFO[]? affectedApps,
        ref uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;

        public RestartManagerApplicationType ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    private enum RestartManagerApplicationType
    {
        Unknown = 0,
        MainWindow = 1,
        OtherWindow = 2,
        Service = 3,
        Explorer = 4,
        Console = 5,
        Critical = 1000
    }
}
