using Avalonia.Controls;

namespace Lumi.Views;

/// <summary>
/// Restores native Windows minimize / maximize / restore animations for windows that use
/// <see cref="Window.ExtendClientAreaToDecorationsHint"/> for custom chrome.
/// <para>
/// When the client area is extended for custom chrome, Avalonia removes the <c>WS_CAPTION</c>
/// window style so the OS does not paint its own title bar. Windows, however, ties the
/// minimize / maximize / restore animations to the presence of <c>WS_CAPTION</c>, so dropping it
/// silently disables those animations. Re-adding the style through the supported Win32 styles
/// callback brings the animations back without drawing a native title bar — the frame is still
/// removed by Avalonia's <c>WM_NCCALCSIZE</c> handling, so the custom chrome is unaffected.
/// </para>
/// <para>
/// This is only safe on Windows 11. On Windows 10, DWM unconditionally paints a native caption
/// when <c>WS_CAPTION</c> is present on an extended-client-area window (Avalonia keeps non-client
/// rendering enabled there for borders/shadows), which is precisely why Avalonia drops the style.
/// See https://github.com/AvaloniaUI/Avalonia/issues/21328. We therefore only re-add it on
/// Windows 11+ and leave Avalonia's frameless behavior untouched on Windows 10.
/// </para>
/// </summary>
internal static class WindowChromeInterop
{
    private const uint WS_CAPTION = 0x00C00000;

    /// <summary>
    /// Ensures <paramref name="window"/> keeps the <c>WS_CAPTION</c> style so Windows animates
    /// minimize / maximize / restore. No-op on non-Windows, Windows 10 and headless platforms.
    /// Call this before setting <see cref="Window.ExtendClientAreaToDecorationsHint"/> so the
    /// caption style is present the first time Avalonia computes the window styles.
    /// </summary>
    public static void EnableNativeMinMaxAnimations(Window window)
    {
        // Windows 10 draws a native caption when WS_CAPTION is present on an extended-client-area
        // window, so only re-add the style on Windows 11+ (build 22000) where the OS honors the
        // extended client area. IsWindowsVersionAtLeast also covers the non-Windows no-op.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;

        try
        {
            // The callback runs every time Avalonia (re)computes the native window styles,
            // so WS_CAPTION is re-applied across maximize / restore / state changes.
            Win32Properties.AddWindowStylesCallback(window, static (style, exStyle) => (style | WS_CAPTION, exStyle));
        }
        catch
        {
            // PlatformImpl is not a Win32 window (e.g. the headless test host) — nothing to do.
        }
    }
}
