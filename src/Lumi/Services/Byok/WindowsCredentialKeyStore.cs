using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Lumi.Services.Byok;

/// <summary>
/// Windows Credential Manager backed <see cref="ISecureKeyStore"/>. Keys are stored as
/// <c>CRED_TYPE_GENERIC</c> credentials with <see cref="CRED_PERSIST_LOCAL_MACHINE"/>, named
/// <c>Lumi.Byok.{key}</c>. They are encrypted by DPAPI tied to the current Windows user, so
/// they are only readable by this account on this machine — they never appear in
/// <c>data.json</c> or any other file Lumi writes.
/// </summary>
/// <remarks>
/// <see cref="IsSupported"/> is <c>false</c> off-Windows (the type still constructs so the
/// factory can return a single instance unconditionally). The P/Invokes are Windows-only and
/// are platform-guarded; calls are infallible on non-Windows because they never execute.
/// Uses classic <c>[DllImport]</c> marshalling (rather than <c>[LibraryImport]</c> source-gen)
/// because the source generator emits unsafe code and the Lumi project does not enable
/// <c>AllowUnsafeBlocks</c> globally.
/// </remarks>
internal sealed class WindowsCredentialKeyStore : ISecureKeyStore
{
    internal const string KeyPrefix = "Lumi.Byok.";

    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public Task<string?> GetAsync(string key)
    {
        if (!IsSupported) return Task.FromResult<string?>(null);
        var target = MakeTarget(key);

        try
        {
            if (CredReadW(target, CRED_TYPE_GENERIC, 0, out var handle) == 0)
            {
                // ERROR_NOT_FOUND (1168) or any other failure → treat as absent, do not throw.
                return Task.FromResult<string?>(null);
            }

            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
                // CredentialBlob is UTF-16; CredentialBlobSize is in BYTES.
                if (cred.CredentialBlobSize <= 0 || cred.CredentialBlob == IntPtr.Zero)
                    return Task.FromResult<string?>(null);

                var charCount = (int)(cred.CredentialBlobSize / 2);
                return Task.FromResult<string?>(Marshal.PtrToStringUni(cred.CredentialBlob, charCount));
            }
            finally
            {
                CredFree(handle);
            }
        }
        catch
        {
            // Never leak OS read failures to the chat path — degrade to "no key".
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetAsync(string key, string? secret)
    {
        if (!IsSupported) return Task.CompletedTask;

        // Empty/null is a delete — the UI contract treats a cleared password box as removal.
        if (string.IsNullOrEmpty(secret))
            return DeleteAsync(key);

        var target = MakeTarget(key);
        var secretBytes = System.Text.Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(secretBytes.Length);

        try
        {
            Marshal.Copy(secretBytes, 0, blob, secretBytes.Length);

            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = blob,
                UserName = null,
            };

            if (CredWriteW(ref cred, 0) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CredWrite failed");

            return Task.CompletedTask;
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public Task DeleteAsync(string key)
    {
        if (!IsSupported) return Task.CompletedTask;
        var target = MakeTarget(key);

        try
        {
            // ERROR_NOT_FOUND is expected when deleting an absent entry — ignore it.
            CredDeleteW(target, CRED_TYPE_GENERIC, 0);
            return Task.CompletedTask;
        }
        catch
        {
            return Task.CompletedTask;
        }
    }

    private static string MakeTarget(string key)
        => KeyPrefix + (key ?? throw new ArgumentNullException(nameof(key)));

    // ── Win32 ──

    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const uint ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CredWriteW(ref CREDENTIAL cred, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CredReadW(string target, uint type, uint flags, out IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr handle);
}
