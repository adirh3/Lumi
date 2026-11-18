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

/// <summary>A git clone into the runtime's staging, as its command line describes it.</summary>
/// <param name="Repository">The repository argument, as the runtime passed it.</param>
/// <param name="Branch">Its <c>--branch</c>, which the runtime passes for any ref but a commit.</param>
/// <param name="Pinned">Whether it stages in <c>_pinned</c>, as the runtime does for an add at a ref.</param>
internal sealed record MarketplaceClone(string Staging, string Target, string Repository, string? Branch, bool Pinned);

/// <summary>
/// The Copilot runtime adds a Git marketplace by cloning its whole repository with git into a staging
/// folder of its cache's <c>marketplaces</c> folder (of <c>marketplaces/_pinned</c> for an add at a
/// ref). It reports no progress, ignores cancellation, leaves git running when it exits and keeps the
/// staging folder of a clone that never finished. The git command line names that folder, the
/// repository and the ref, so Lumi uses it to show what has arrived, to stop the clone and to remove
/// staging folders that interrupted adds left behind.
/// </summary>
internal sealed partial class MarketplaceDownloads
{
    /// <summary>The runtime's staging folders; everything else in the cache is the runtime's own.</summary>
    internal const string StagingPrefix = ".copilot-clone-";

    /// <summary>Where the runtime keeps marketplaces added at a ref, and stages their clones.</summary>
    internal const string PinnedFolder = "_pinned";

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
        // COPILOT_CACHE_HOME may use either separator on Windows; git's command lines use the runtime's.
        Directory = Path.GetFullPath(Path.Combine(cacheDirectory, "marketplaces"));
        _runtimeProcessId = runtimeProcessId;
        _gitProcesses = gitProcesses;
        _stopProcessTree = stopProcessTree;
    }

    /// <summary>The runtime's marketplace cache: a folder and a lock file per marketplace.</summary>
    public string Directory { get; }

    /// <summary>The staging folders now, to tell later which clones an add began.</summary>
    public IReadOnlySet<string> Snapshot() => StagingFolders().ToHashSet(PathComparer);

    /// <summary>
    /// The download of the runtime's clones of <paramref name="source"/> begun after
    /// <paramref name="before"/>, or null when none runs (or none can be seen).
    /// </summary>
    public MarketplaceDownloadProgress? Measure(IReadOnlySet<string> before, string? source = null)
    {
        MarketplaceDownloadProgress? total = null;
        foreach (var target in RuntimeClones(before, source).Select(static clone => clone.Target).Distinct(PathComparer))
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
    /// Stops the runtime's clones into its marketplace cache: of <paramref name="source"/> and begun
    /// after <paramref name="before"/> when given, else all of them. True when there was one to stop.
    /// </summary>
    public bool Stop(IReadOnlySet<string>? before, string? source = null)
    {
        var clones = RuntimeClones(before, source);
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
    /// Removes the staging folders of adds interrupted when Lumi or the runtime closed, which the runtime
    /// never removes: those no git process names and nothing has written to for <paramref name="age"/>.
    /// Lock files stay. The runtime locks them natively, so removing one would let another add take a
    /// lock that a running add still holds.
    /// </summary>
    public void RemoveAbandoned(TimeSpan age)
    {
        var stagings = StagingFolders().ToArray();
        if (stagings.Length == 0)
            return;

        var inUse = StagingInUse();
        foreach (var staging in stagings)
        {
            if (!inUse.Contains(staging) && !WrittenWithin(staging, age))
                TryDelete(staging);
        }
    }

    /// <summary>The runtime's staging folders, for adds without a ref and in <c>_pinned</c> for adds at one.</summary>
    private IEnumerable<string> StagingFolders()
    {
        foreach (var folder in new[] { Directory, Path.Combine(Directory, PinnedFolder) })
        {
            string[] stagings;
            try
            {
                stagings = System.IO.Directory.Exists(folder) ? System.IO.Directory.GetDirectories(folder, StagingPrefix + "*") : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[Plugins] Could not read '{folder}': {ex.Message}");
                continue;
            }

            foreach (var staging in stagings)
                yield return staging;
        }
    }

    /// <summary>
    /// Whether anything in a staging folder was written within <paramref name="age"/>. Between git
    /// commands, and after the clone while the runtime moves it into place, no git process names it.
    /// </summary>
    private static bool WrittenWithin(string folder, TimeSpan age)
    {
        var since = DateTime.UtcNow - age;
        try
        {
            var root = new DirectoryInfo(folder);
            return root.LastWriteTimeUtc > since
                || root.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                    .Any(entry => entry.LastWriteTimeUtc > since);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't tell; a later sweep can.
            return true;
        }
    }

    /// <summary>
    /// What a git command line clones into this cache's staging: the staging folder, the target folder
    /// inside it (the last argument), the repository (the argument before it) and any <c>--branch</c>.
    /// </summary>
    internal static MarketplaceClone? CloneInto(string commandLine, string marketplacesDirectory)
    {
        var line = WithPlatformSeparators(commandLine);
        if (!line.Split([' ', '\t', '"'], StringSplitOptions.RemoveEmptyEntries).Contains("clone", StringComparer.Ordinal)
            || StagingIn(line, marketplacesDirectory) is not { } found)
            return null;

        // The target runs to its closing quote or, unquoted (ps on Linux and macOS never quotes), to the end.
        var quoted = found.Start > 0 && line[found.Start - 1] == '"';
        var end = quoted ? line.IndexOf('"', found.Start) : -1;
        var target = line[found.Start..(end < 0 ? line.Length : end)].TrimEnd();

        // The arguments before it as the runtime wrote them, so a URL keeps its slashes.
        var arguments = commandLine[..found.Start].Split([' ', '\t', '"'], StringSplitOptions.RemoveEmptyEntries);
        var branch = Array.LastIndexOf(arguments, "--branch") is var at and >= 0 && at < arguments.Length - 2 ? arguments[at + 1] : null;
        return arguments.Length > 0 ? new MarketplaceClone(found.Staging, target, arguments[^1], branch, found.Pinned) : null;
    }

    /// <summary>
    /// The repository a marketplace source or a clone's repository argument names, as one host and path:
    /// <c>github.com/owner/repo</c> for GitHub, else the URL's host and path without its scheme, user,
    /// ref or <c>.git</c>. Null for a local folder, which the runtime doesn't clone.
    /// </summary>
    internal static string? CloneAddress(string source)
    {
        source = source.Trim();
        if (source.IndexOf('#') is var hash and >= 0)
            source = source[..hash];

        if (GitHubRepoRef.TryParse(source) is { } github)
            return $"github.com/{github.Owner}/{github.Repo}";

        string address;
        if (source.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.IsFile || uri.Host.Length == 0)
                return null;
            address = uri.Authority + uri.AbsolutePath;
        }
        else if (ScpAddress().Match(source) is { Success: true } scp)
        {
            address = scp.Groups["host"].Value + "/" + scp.Groups["path"].Value;
        }
        else
        {
            return null;
        }

        address = address.TrimEnd('/');
        return address.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? address[..^".git".Length] : address;
    }

    /// <summary>
    /// Whether a clone is the runtime's clone of this marketplace source: the same repository at the same
    /// ref. The runtime stages an add at a ref in <c>_pinned</c> and clones it with <c>--branch</c>
    /// (without <c>refs/heads/</c> or <c>refs/tags/</c>), unless the ref is a commit, which it fetches after
    /// cloning.
    /// </summary>
    internal static bool IsCloneOf(MarketplaceClone clone, string source)
    {
        var hash = source.IndexOf('#');
        var reference = hash >= 0 && source[(hash + 1)..].Trim() is { Length: > 0 } named ? named : null;
        var branch = reference is null || CommitId().IsMatch(reference) ? null
            : reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..]
            : reference.StartsWith("refs/tags/", StringComparison.Ordinal) ? reference["refs/tags/".Length..]
            : reference;
        return clone.Pinned == (reference is not null)
            && string.Equals(clone.Branch, branch, StringComparison.Ordinal)
            && CloneAddress(source) is { } address
            && string.Equals(CloneAddress(clone.Repository), address, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^[^@/\s]+@(?<host>[^:/\s]+):(?<path>\S+)$")]
    private static partial Regex ScpAddress();

    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex CommitId();

    /// <summary>On Windows git and its children write paths with either separator; compare one.</summary>
    private static string WithPlatformSeparators(string path)
        => OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;

    /// <summary>The staging folder of this cache a command line names, where its path starts and whether it is pinned.</summary>
    private static (string Staging, int Start, bool Pinned)? StagingIn(string commandLine, string marketplacesDirectory)
    {
        commandLine = WithPlatformSeparators(commandLine);
        marketplacesDirectory = Path.TrimEndingDirectorySeparator(WithPlatformSeparators(marketplacesDirectory));
        foreach (var pinned in new[] { false, true })
        {
            var folder = pinned ? Path.Combine(marketplacesDirectory, PinnedFolder) : marketplacesDirectory;
            var root = folder + Path.DirectorySeparatorChar;
            var start = commandLine.IndexOf(root + StagingPrefix, PathComparison);
            if (start < 0)
                continue;

            var end = start + root.Length + StagingPrefix.Length;
            while (end < commandLine.Length && commandLine[end] is not ('\\' or '/' or '"' or ' ' or '\t'))
                end++;

            var name = commandLine[(start + root.Length)..end];
            if (name.Length > StagingPrefix.Length)
                return (Path.Combine(folder, name), start, pinned);
        }

        return null;
    }

    private IReadOnlyList<(int ProcessId, string Staging, string Target)> RuntimeClones(IReadOnlySet<string>? before, string? source)
    {
        if (_runtimeProcessId() is not { } runtime)
            return [];

        var clones = new List<(int, string, string)>();
        foreach (var process in _gitProcesses(runtime))
        {
            // Other adds and refreshes run their own clones in the same runtime: only this source's count.
            if (process.StartedByRuntime
                && CloneInto(process.CommandLine, Directory) is { } clone
                && before?.Contains(clone.Staging) != true
                && (source is null || IsCloneOf(clone, source)))
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
            if (StagingIn(process.CommandLine, Directory) is { } found)
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
