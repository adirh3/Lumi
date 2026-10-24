using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lumi.Services;

internal static class WorkAccountRegistration
{
    private const int DsregWorkplaceJoin = 2;

    internal static bool IsPresent()
        => OperatingSystem.IsWindows() && ReadWindowsRegistration();

    internal static bool IsUserWorkplaceRegistration(int hresult, int? joinType)
    {
        if (hresult != 0)
        {
            Trace.TraceWarning(
                "[SetupSuggestions] Couldn't check work-account registration (HRESULT 0x{0:X8}).", hresult);
            return false;
        }

        // A device's Entra join does not establish that its current user has a work account.
        return joinType == DsregWorkplaceJoin;
    }

    [SupportedOSPlatform("windows")]
    private static bool ReadWindowsRegistration()
    {
        var info = IntPtr.Zero;
        try
        {
            var hresult = NetGetAadJoinInformation(null, out info);
            // DSREG_JOIN_INFO starts with DSREG_JOIN_TYPE; no account names or identifiers are read.
            int? joinType = hresult == 0 && info != IntPtr.Zero ? Marshal.ReadInt32(info) : null;
            return IsUserWorkplaceRegistration(hresult, joinType);
        }
        finally
        {
            if (info != IntPtr.Zero)
                NetFreeAadJoinInformation(info);
        }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NetGetAadJoinInformation(string? tenantId, out IntPtr joinInfo);

    [SupportedOSPlatform("windows")]
    [DllImport("netapi32.dll", ExactSpelling = true)]
    private static extern void NetFreeAadJoinInformation(IntPtr joinInfo);
}
