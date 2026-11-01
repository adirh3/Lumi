using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lumi.MemoryDiagnostics;

/// <summary>Windows private residency, sampled without collecting GC or touching dump pages.</summary>
[SupportedOSPlatform("windows")]
internal static class MemoryProcessProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MachineMemory
    {
        public uint Size, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    public static MemoryMachineSample CaptureMachine()
    {
        var memory = new MachineMemory { Size = (uint)Marshal.SizeOf<MachineMemory>() };
        if (!GlobalMemoryStatusEx(ref memory) || !GetSystemTimes(out var idle, out var kernel, out var user))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot record memory-regression machine pressure.");
        return new(memory.Load, checked((long)memory.TotalPhysical), checked((long)memory.AvailablePhysical),
            idle, checked(kernel + user));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
        public nuint PrivateUsage, PrivateWorkingSetSize, SharedCommitUsage;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Name;
    }

    public static IReadOnlyList<MemoryProcessSample> Capture(int rootId)
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new nint(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var entries = new List<ProcessEntry>();
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32FirstW(snapshot, ref entry))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            do { entries.Add(entry); } while (Process32NextW(snapshot, ref entry));
            if (Marshal.GetLastWin32Error() != 18)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { CloseHandle(snapshot); }
        var root = entries.SingleOrDefault(entry => entry.ProcessId == rootId);
        if (root.ProcessId == 0)
            throw new InvalidOperationException("The memory check process is no longer in the process snapshot.");
        var pending = new Queue<(ProcessEntry Entry, long ParentStarted)>();
        pending.Enqueue((root, 0));
        var seen = new HashSet<uint>();
        var samples = new List<MemoryProcessSample>();
        while (pending.TryDequeue(out var candidate))
        {
            var entry = candidate.Entry;
            if (!seen.Add(entry.ProcessId))
                continue;
            var process = OpenProcess(0x410, false, (int)entry.ProcessId);
            if (process == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot measure owned process {entry.ProcessId}.");
            try
            {
                if (!GetProcessTimes(process, out var started, out _, out _, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot verify process lifetime for {entry.ProcessId}.");
                // A reused parent PID does not make an older orphan process this instance's child.
                if (started < candidate.ParentStarted)
                    continue;
                var counters = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
                if (!K32GetProcessMemoryInfo(process, ref counters, counters.Size))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot read memory for owned process {entry.ProcessId}.");
                samples.Add(new((int)entry.ProcessId, (int)entry.ParentProcessId, entry.Name,
                    checked((long)counters.PrivateWorkingSetSize), checked((long)counters.WorkingSetSize), checked((long)counters.PrivateUsage)));
                foreach (var child in entries.Where(child => child.ParentProcessId == entry.ProcessId))
                    pending.Enqueue((child, started));
            }
            finally { CloseHandle(process); }
        }
        if (!samples.Any(sample => sample.ProcessId == rootId))
            throw new InvalidOperationException("The memory check process is no longer in the process snapshot.");
        return samples;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(nint process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(nint process, ref MemoryCounters counters, uint size);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MachineMemory memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}
