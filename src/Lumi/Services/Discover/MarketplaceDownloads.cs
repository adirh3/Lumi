using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Lumi.Services.Discover;

public enum MarketplaceDownloadPhase
{
    /// <summary>git is receiving the repository.</summary>
    Downloading,

    /// <summary>The repository has arrived and git is writing out its files.</summary>
    Unpacking,
}

/// <summary>How far the runtime's download of a marketplace has got, measured on disk.</summary>
public sealed record MarketplaceDownloadProgress(MarketplaceDownloadPhase Phase, long Bytes);

/// <summary>A running git process, and whether the Copilot runtime started it.</summary>
internal sealed record GitProcess(int Id, string CommandLine, bool StartedByRuntime);

/// <summary>
/// The Copilot runtime adds a Git marketplace by cloning its whole repository with git into a staging
/// folder of its cache's <c>marketplaces</c> folder. It reports no progress, ignores cancellation,
/// leaves git running when it exits and keeps the staging folder of a clone that never finished. The
/// git command line names that folder, so Lumi uses it to show what has arrived, to stop the clone and
/// to remove what an attempt left behind.
/// </summary>
internal sealed class MarketplaceDownloads
{
    /// <summary>The runtime's staging folders; everything else in the cache is the runtime's own.</summary>
    internal const string StagingPrefix = ".copilot-clone-";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Func<int?> _runtimeProcessId;
    private readonly Func<int?, IReadOnlyList<GitProcess>> _gitProcesses;
    private readonly Action<int> _stopProcessTree;

    public MarketplaceDownloads(string cacheDirectory, Func<int?> runtimeProcessId)
        : this(cacheDirectory, runtimeProcessId, GitProcesses.List, GitProcesses.StopTree)
    {
    }

    internal MarketplaceDownloads(
        string cacheDirectory,
        Func<int?> runtimeProcessId,
        Func<int?, IReadOnlyList<GitProcess>> gitProcesses,
        Action<int> stopProcessTree)
    {
        Directory = Path.Combine(cacheDirectory, "marketplaces");
        _runtimeProcessId = runtimeProcessId;
        _gitProcesses = gitProcesses;
        _stopProcessTree = stopProcessTree;
    }

    /// <summary>The runtime's marketplace cache: a folder and a lock file per marketplace.</summary>
    public string Directory { get; }

    /// <summary>The cache's entries now, to tell later what an add created.</summary>
    public IReadOnlySet<string> Snapshot()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory))
                return System.IO.Directory.EnumerateFileSystemEntries(Directory).Select(Path.GetFileName).OfType<string>().ToHashSet(PathComparer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Plugins] Could not read '{Directory}': {ex.Message}");
        }

        return new HashSet<string>(PathComparer);
    }

    /// <summary>The download of the runtime's clones begun after <paramref name="before"/>, or null when none runs.</summary>
    public MarketplaceDownloadProgress? Measure(IReadOnlySet<string> before)
    {
        MarketplaceDownloadProgress? total = null;
        foreach (var target in RuntimeClones(before).Select(static clone => clone.Target).Distinct(PathComparer))
        {
            if (MeasureClone(target) is not { } clone)
                continue;

            total = total is null
                ? clone
                : new MarketplaceDownloadProgress(
                    total.Phase == MarketplaceDownloadPhase.Downloading ? total.Phase : clone.Phase,
                    total.Bytes + clone.Bytes);
        }

        return total;
    }

    /// <summary>
    /// Stops the runtime's clones into its marketplace cache (only those begun after
    /// <paramref name="before"/>, when given). True when there was one to stop.
    /// </summary>
    public bool Stop(IReadOnlySet<string>? before)
    {
        var clones = RuntimeClones(before);
        foreach (var clone in clones)
        {
            try
            {
                _stopProcessTree(clone.ProcessId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ArgumentException or AggregateException or NotSupportedException)
            {
                // It ended on its own meanwhile, or with the tree of a process stopped just before.
                Debug.WriteLine($"[Plugins] Could not stop git {clone.ProcessId}: {ex.Message}");
            }
        }

        return clones.Count > 0;
    }

    /// <summary>
    /// Removes what a failed or stopped add created: its clone's staging folder and the lock file of a
    /// marketplace that was never added. Entries in <paramref name="before"/>, and folders git still
    /// writes, stay.
    /// </summary>
    public void RemoveLeftovers(IReadOnlySet<string> before)
    {
        var inUse = StagingInUse();
        foreach (var name in Snapshot().Where(name => !before.Contains(name)))
        {
            var path = Path.Combine(Directory, name);
            if (name.StartsWith(StagingPrefix, StringComparison.Ordinal))
            {
                if (!inUse.Contains(path))
                    TryDelete(path);
            }
            else if (name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            {
                // The runtime keeps a lock beside each marketplace and its other files; a lock with
                // nothing beside it belongs to the attempt that failed.
                var subject = Path.Combine(Directory, name[..^".lock".Length]);
                if (!System.IO.Directory.Exists(subject) && !File.Exists(subject))
                    TryDelete(path);
            }
        }
    }

    /// <summary>
    /// Removes what adds interrupted when Lumi or the runtime closed left behind, which the runtime
    /// never removes: staging folders older than <paramref name="age"/> that no git process writes and,
    /// while no clone runs into this cache at all, lock files as old with no marketplace beside them.
    /// </summary>
    public void RemoveAbandoned(TimeSpan age)
    {
        string[] entries;
        try
        {
            entries = System.IO.Directory.Exists(Directory)
                ? System.IO.Directory.GetFileSystemEntries(Directory)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var stagings = entries.Where(static entry => Path.GetFileName(entry).StartsWith(StagingPrefix, StringComparison.Ordinal)).ToArray();
        var locks = entries.Where(static entry => entry.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (stagings.Length == 0 && locks.Length == 0)
            return;

        bool IsOld(string path) => DateTime.UtcNow - File.GetCreationTimeUtc(path) > age;
        var inUse = StagingInUse();
        foreach (var staging in stagings)
        {
            if (!inUse.Contains(staging) && IsOld(staging))
                TryDelete(staging);
        }

        // An add holds its lock while it clones, so locks are only safe to remove while none runs.
        if (inUse.Count > 0)
            return;

        foreach (var lockFile in locks)
        {
            var subject = lockFile[..^".lock".Length];
            if (!System.IO.Directory.Exists(subject) && !File.Exists(subject) && IsOld(lockFile))
                TryDelete(lockFile);
        }
    }

    /// <summary>
    /// Where a git command line clones to, when it clones into this cache's staging: the staging
    /// folder, and the target folder, the last argument, inside it.
    /// </summary>
    internal static (string Staging, string Target)? CloneInto(string commandLine, string marketplacesDirectory)
    {
        if (!commandLine.Split([' ', '\t', '"'], StringSplitOptions.RemoveEmptyEntries).Contains("clone", StringComparer.Ordinal)
            || StagingIn(commandLine, marketplacesDirectory) is not { } found)
            return null;

        // The target runs to its closing quote or, unquoted (ps on Linux and macOS never quotes), to the end.
        var quoted = found.Start > 0 && commandLine[found.Start - 1] == '"';
        var end = quoted ? commandLine.IndexOf('"', found.Start) : -1;
        var target = commandLine[found.Start..(end < 0 ? commandLine.Length : end)].TrimEnd();
        return (found.Staging, target);
    }

    /// <summary>The staging folder of this cache a command line names, and where its path starts.</summary>
    private static (string Staging, int Start)? StagingIn(string commandLine, string marketplacesDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(marketplacesDirectory) + Path.DirectorySeparatorChar;
        var start = commandLine.IndexOf(root + StagingPrefix, PathComparison);
        if (start < 0)
            return null;

        var end = start + root.Length + StagingPrefix.Length;
        while (end < commandLine.Length && commandLine[end] is not ('\\' or '/' or '"' or ' ' or '\t'))
            end++;

        var name = commandLine[(start + root.Length)..end];
        return name.Length > StagingPrefix.Length ? (Path.Combine(marketplacesDirectory, name), start) : null;
    }

    private IReadOnlyList<(int ProcessId, string Staging, string Target)> RuntimeClones(IReadOnlySet<string>? before)
    {
        if (_runtimeProcessId() is not { } runtime)
            return [];

        var clones = new List<(int, string, string)>();
        foreach (var process in _gitProcesses(runtime))
        {
            if (process.StartedByRuntime
                && CloneInto(process.CommandLine, Directory) is { } clone
                && before?.Contains(Path.GetFileName(clone.Staging)) != true)
            {
                clones.Add((process.Id, clone.Staging, clone.Target));
            }
        }

        return clones;
    }

    /// <summary>Staging folders some git process names, whoever started it.</summary>
    private HashSet<string> StagingInUse()
    {
        var inUse = new HashSet<string>(PathComparer);
        foreach (var process in _gitProcesses(_runtimeProcessId()))
        {
            // git's own children write some paths with forward slashes.
            var commandLine = OperatingSystem.IsWindows() ? process.CommandLine.Replace('/', '\\') : process.CommandLine;
            if (StagingIn(commandLine, Directory) is { } found)
                inUse.Add(found.Staging);
        }

        return inUse;
    }

    /// <summary>
    /// git receives a repository into <c>.git/objects/pack/tmp_pack_*</c>, renames it to
    /// <c>pack-*.pack</c> once complete and then writes out the files. A fresh <see cref="FileInfo"/>
    /// reads how much git has written so far; a Windows directory listing can still report the size
    /// the file had when git opened it.
    /// </summary>
    private static MarketplaceDownloadProgress? MeasureClone(string target)
    {
        var packs = Path.Combine(target, ".git", "objects", "pack");
        try
        {
            if (!System.IO.Directory.Exists(packs))
                return new MarketplaceDownloadProgress(MarketplaceDownloadPhase.Downloading, 0);

            long bytes = 0;
            var receiving = false;
            foreach (var file in System.IO.Directory.EnumerateFiles(packs))
            {
                var name = Path.GetFileName(file);
                var partial = name.StartsWith("tmp_pack_", StringComparison.Ordinal);
                if (!partial && !name.EndsWith(".pack", StringComparison.Ordinal))
                    continue;

                receiving |= partial;
                bytes += new FileInfo(file).Length;
            }

            return new MarketplaceDownloadProgress(
                receiving || bytes == 0 ? MarketplaceDownloadPhase.Downloading : MarketplaceDownloadPhase.Unpacking,
                bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // git renamed or removed a file during the measurement; the next one catches up.
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!System.IO.Directory.Exists(path))
            {
                File.Delete(path);
                return;
            }

            try
            {
                System.IO.Directory.Delete(path, recursive: true);
            }
            catch (UnauthorizedAccessException)
            {
                // git makes its object files read-only, which Windows refuses to delete.
                foreach (var file in System.IO.Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                    File.SetAttributes(file, FileAttributes.Normal);
                System.IO.Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Plugins] Could not remove '{path}': {ex.Message}");
        }
    }
}

/// <summary>Running git processes with their command lines, on Windows, Linux and macOS.</summary>
internal static partial class GitProcesses
{
    public static IReadOnlyList<GitProcess> List(int? runtimeProcessId)
    {
        try
        {
            return OperatingSystem.IsWindows() ? ListWindows(runtimeProcessId) : ListUnix(runtimeProcessId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Plugins] Could not list git processes: {ex.Message}");
            return [];
        }
    }

    public static void StopTree(int processId)
    {
        using var process = Process.GetProcessById(processId);
        process.Kill(entireProcessTree: true);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<GitProcess> ListWindows(int? runtimeProcessId)
    {
        var running = Process.GetProcessesByName("git");
        var gitIds = running.Select(static process => process.Id).ToHashSet();
        var processes = new List<GitProcess>();
        foreach (var process in running)
        {
            using (process)
            {
                if (ReadWindowsCommandLine(process.Id) is { } commandLine)
                {
                    var startedByRuntime = runtimeProcessId is { } runtime && IsStartedBy(process.Id, runtime, gitIds);
                    processes.Add(new GitProcess(process.Id, commandLine, startedByRuntime));
                }
            }
        }

        return processes;
    }

    /// <summary>Follows parents through git's own processes: the runtime starts git, which starts its helpers.</summary>
    [SupportedOSPlatform("windows")]
    private static bool IsStartedBy(int processId, int runtimeProcessId, IReadOnlySet<int> gitIds)
    {
        for (var hops = 0; hops < 8 && ReadWindowsParent(processId) is { } parent; hops++)
        {
            if (parent == runtimeProcessId)
                return true;
            if (!gitIds.Contains(parent))
                return false;
            processId = parent;
        }

        return false;
    }

    /// <summary><c>ps</c> on Linux and macOS, with one <c>-o</c> per column: BSD ps reads the rest of one as its header.</summary>
    private static IReadOnlyList<GitProcess> ListUnix(int? runtimeProcessId)
    {
        var start = new ProcessStartInfo("ps")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-A", "-o", "pid=", "-o", "ppid=", "-o", "args=" })
            start.ArgumentList.Add(argument);

        using var ps = Process.Start(start) ?? throw new InvalidOperationException("ps did not start.");
        var output = ps.StandardOutput.ReadToEndAsync();
        if (!ps.WaitForExit(5000))
        {
            ps.Kill(entireProcessTree: true);
            return [];
        }

        var rows = new List<(int Id, int Parent, string Args)>();
        foreach (var line in output.Result.Split('\n'))
        {
            if (PsRow().Match(line) is { Success: true } row)
                rows.Add((int.Parse(row.Groups[1].Value), int.Parse(row.Groups[2].Value), row.Groups[3].Value.Trim()));
        }

        var startedByRuntime = new HashSet<int>();
        if (runtimeProcessId is { } runtime)
        {
            var parents = new Queue<int>([runtime]);
            while (parents.TryDequeue(out var parent))
            {
                foreach (var child in rows.Where(row => row.Parent == parent && startedByRuntime.Add(row.Id)))
                    parents.Enqueue(child.Id);
            }
        }

        return rows
            .Where(static row => Path.GetFileName(row.Args.Split(' ', 2)[0]) == "git")
            .Select(row => new GitProcess(row.Id, row.Args, startedByRuntime.Contains(row.Id)))
            .ToArray();
    }

    [GeneratedRegex(@"^\s*(\d+)\s+(\d+)\s+(.+)$")]
    private static partial Regex PsRow();

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessBasicInformationClass = 0;
    private const int ProcessCommandLineInformation = 60;

    /// <summary>The process that started a process, or null when it can't be read.</summary>
    [SupportedOSPlatform("windows")]
    private static int? ReadWindowsParent(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var information = new ProcessBasicInformation();
            return NtQueryInformationProcess(handle, ProcessBasicInformationClass, ref information, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
                ? (int)information.InheritedFromUniqueProcessId
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>A process's command line (Windows 8.1 and later), or null when it can't be read.</summary>
    [SupportedOSPlatform("windows")]
    internal static string? ReadWindowsCommandLine(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var length);
            if (length <= 0)
                return null;

            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out _) != 0)
                    return null;

                // A UNICODE_STRING: the text's length in bytes, then a pointer-aligned pointer to it.
                var bytes = unchecked((ushort)Marshal.ReadInt16(buffer));
                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return bytes == 0 || text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, bytes / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    /// <summary>PROCESS_BASIC_INFORMATION, each field pointer-sized as aligned on 32- and 64-bit Windows.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }
}
