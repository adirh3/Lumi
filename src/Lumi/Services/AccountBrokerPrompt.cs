using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Lumi.Services;

/// <summary>
/// The sign-in window of the Windows account broker, which Microsoft Entra connectors such as Work IQ sign
/// in through. The Copilot runtime runs without a window or console, so it asks the broker for sign-in with no
/// parent window, and Windows opens the prompt unowned, where it can land behind Lumi. While Lumi's own
/// sign-in runs, the prompt that opens for it is brought to the front.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AccountBrokerPrompt
{
    /// <summary>The Windows app that shows the broker's sign-in UI.</summary>
    internal const string BrokerAppUserModelId = "Microsoft.AAD.BrokerPlugin_cw5n1h2txyewy!App";

    private const string FrameClassName = "ApplicationFrameWindow";
    private const int DwmwaCloaked = 14;
    private const ushort VtLpwstr = 31;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WatchLimit = TimeSpan.FromMinutes(1);
    private static readonly Guid PropertyStoreId = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    /// <summary>
    /// Brings a broker sign-in window that opens after this call to the front and reports it once. Windows
    /// already open belong to other sign-ins and are left alone. Ends when <paramref name="cancellationToken"/>
    /// is cancelled, once a window was found, or after a minute, and never throws.
    /// </summary>
    public static Task BringToFrontAsync(Action? shown, CancellationToken cancellationToken)
    {
        HashSet<nint> existing;
        try
        {
            existing = FindPromptWindows();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MCP] Couldn't look for the Windows sign-in window: {ex.Message}");
            return Task.CompletedTask;
        }

        return WatchAsync(existing, shown, cancellationToken);
    }

    internal static bool IsBrokerApp(string? appUserModelId)
        => string.Equals(appUserModelId, BrokerAppUserModelId, StringComparison.OrdinalIgnoreCase);

    private static async Task WatchAsync(HashSet<nint> existing, Action? shown, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            while (Stopwatch.GetElapsedTime(started) < WatchLimit)
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                foreach (var window in FindPromptWindows())
                {
                    if (existing.Contains(window))
                        continue;

                    // Lumi is in front while the user works in it, which lets it hand the foreground over.
                    SetForegroundWindow(window);
                    shown?.Invoke();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MCP] Couldn't bring the Windows sign-in window forward: {ex.Message}");
        }
    }

    private static HashSet<nint> FindPromptWindows()
    {
        var windows = new HashSet<nint>();
        var className = new StringBuilder(64);
        EnumWindows((window, _) =>
        {
            className.Clear();
            if (IsWindowVisible(window)
                && !IsCloaked(window)
                && GetClassNameW(window, className, className.Capacity) > 0
                && className.ToString() == FrameClassName
                && IsBrokerApp(GetAppUserModelId(window)))
            {
                windows.Add(window);
            }

            return true;
        }, 0);
        return windows;
    }

    private static bool IsCloaked(nint window)
        => DwmGetWindowAttribute(window, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>The app a frame window hosts, from the window's shell properties.</summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IPropertyStore))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PropertyKey))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PropVariant))]
    [UnconditionalSuppressMessage("Trimming", "IL2050",
        Justification = "IPropertyStore's complete vtable and the ABI structs it passes are explicitly rooted with DynamicDependency.")]
    private static string? GetAppUserModelId(nint window)
    {
        var storeId = PropertyStoreId;
        if (SHGetPropertyStoreForWindow(window, ref storeId, out var store) != 0 || store is null)
            return null;

        try
        {
            var key = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
            if (store.GetValue(ref key, out var value) != 0)
                return null;

            try
            {
                return value.ValueType == VtLpwstr ? Marshal.PtrToStringUni(value.Value) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort ValueType;
        private ushort _reserved1;
        private ushort _reserved2;
        private ushort _reserved3;
        public nint Value;
        private nint _value2;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint window, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(nint window, ref Guid interfaceId, out IPropertyStore? store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
