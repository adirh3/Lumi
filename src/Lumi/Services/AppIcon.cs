using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Lumi.Services;

/// <summary>
/// Shared access to Lumi's application-icon asset plus cross-platform icon integration.
/// Windows is intentionally left untouched: its taskbar/window icon comes from the embedded
/// <c>.ico</c> (<c>&lt;ApplicationIcon&gt;</c>) and must stay exactly as it was.
/// </summary>
internal static class AppIcon
{
    private static readonly Uri IconUri = new("avares://Lumi/Assets/lumi-icon.png");

    /// <summary>Raw PNG bytes of the app icon, or null if the asset can't be read.</summary>
    public static byte[]? TryReadPngBytes()
    {
        try
        {
            using var stream = AssetLoader.Open(IconUri);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Applies the app icon to a window on platforms that use a window/taskbar icon and don't already
    /// get one from the executable. No-op on Windows (its embedded .ico is authoritative). On Linux this
    /// provides the taskbar/window icon (Linux is newly supported, so this is additive, not a regression);
    /// on macOS the window icon is cosmetic (the Dock icon is set separately) and harmless. Best-effort.
    /// </summary>
    public static void ApplyWindowIcon(Window window)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            using var stream = AssetLoader.Open(IconUri);
            window.Icon = new WindowIcon(stream);
        }
        catch
        {
            // Best-effort: a missing window icon is cosmetic, never fatal.
        }
    }
}
