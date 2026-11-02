using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lumi.Services;

/// <summary>
/// What the operating system can tell about the user being at this computer. <see cref="IdleFor"/>
/// is the time since the last keyboard, mouse or touch input in this session; null where the
/// platform does not report it.
/// </summary>
internal readonly record struct DesktopPresenceState(TimeSpan? IdleFor, bool IsLocked);

internal interface IDesktopPresenceProbe
{
    DesktopPresenceState Read();
}

/// <summary>
/// Reads input idle time and whether the session is locked. Windows reports both (a disconnected
/// remote session counts as locked); macOS reports idle time. Elsewhere idle time is unknown and
/// callers fall back to whether one of Lumi's own windows is in use.
/// </summary>
internal sealed class SystemDesktopPresenceProbe : IDesktopPresenceProbe
{
    public static SystemDesktopPresenceProbe Instance { get; } = new();

    private bool _failed;

    public DesktopPresenceState Read()
    {
#if DEBUG
        // End-to-end runs cannot leave the developer's desk, so a Debug build can be told.
        switch (Environment.GetEnvironmentVariable("LUMI_DEBUG_PRESENCE")?.Trim().ToLowerInvariant())
        {
            case "away":
                return new DesktopPresenceState(TimeSpan.FromHours(1), false);
            case "locked":
                return new DesktopPresenceState(TimeSpan.FromHours(1), true);
            case "present":
                return new DesktopPresenceState(TimeSpan.Zero, false);
        }
#endif
        if (_failed)
            return new DesktopPresenceState(null, false);

        try
        {
            if (OperatingSystem.IsWindows())
                return new DesktopPresenceState(ReadWindowsIdle(), IsWindowsSessionLocked());
            if (OperatingSystem.IsMacOS())
                return new DesktopPresenceState(ReadMacIdle(), false);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                       or MarshalDirectiveException)
        {
            _failed = true;
            Trace.TraceWarning($"[Presence] Idle time is unavailable: {ex.Message}");
        }

        return new DesktopPresenceState(null, false);
    }

    private static TimeSpan? ReadWindowsIdle()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
            return null;

        // Both are GetTickCount values; unsigned subtraction stays right across the 49.7-day wrap.
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    private static bool IsWindowsSessionLocked()
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, CurrentSession, SessionInfoEx, out var buffer, out var bytes))
            return false;

        try
        {
            // WTSINFOEXW: DWORD Level, then the 8-byte aligned WTSINFOEX_LEVEL1_W
            // { ULONG SessionId; WTS_CONNECTSTATE_CLASS SessionState; LONG SessionFlags; ... }.
            if (bytes < 20 || Marshal.ReadInt32(buffer) != 1)
                return false;

            var state = Marshal.ReadInt32(buffer, 12);
            var flags = Marshal.ReadInt32(buffer, 16);
            return state == DisconnectedState || flags == SessionLockedFlag;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static TimeSpan? ReadMacIdle()
    {
        var seconds = CGEventSourceSecondsSinceLastEventType(CombinedSessionState, AnyInputEventType);
        return double.IsFinite(seconds) && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    private const int CurrentSession = -1;
    private const int SessionInfoEx = 25;
    private const int DisconnectedState = 4;
    private const int SessionLockedFlag = 0;
    private const int CombinedSessionState = 0;
    private const uint AnyInputEventType = uint.MaxValue;

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern double CGEventSourceSecondsSinceLastEventType(int stateId, uint eventType);
}
