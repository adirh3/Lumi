using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Lumi.Services.Appearance;

namespace Lumi.Services;

/// <summary>
/// Shared access to Lumi's application-icon asset plus cross-platform icon integration.
/// Windows is intentionally left untouched: its taskbar/window icon comes from the embedded
/// <c>.ico</c> (<c>&lt;ApplicationIcon&gt;</c>) and must stay exactly as it was.
/// </summary>
internal static class AppIcon
{
    private static readonly Uri IconUri = new("avares://Lumi/Assets/lumi-icon.png");

    /// <summary>The resource key of Lumi's mark in the current accent, an <see cref="Avalonia.Media.IImage"/>
    /// that the welcome screen, onboarding and the loading window show.</summary>
    internal const string MarkResourceKey = "Image.LumiMark";

    // Sharp for the largest mark in the app (88 dip, onboarding's greeting) up to 290% display scale.
    private const int MarkSize = 256;

    // The notification area's icon at 100% to 300% display scale.
    private static readonly int[] TrayIconSizes = [16, 20, 24, 32, 40, 48];

    /// <summary>Lumi's mark in <paramref name="palette"/>.</summary>
    internal static Bitmap CreateMark(AccentPalettes.Palette palette)
    {
        using var art = AppIconArt.Render(AppIconArt.ColorsFor(palette), MarkSize);
        return new Bitmap(
            PixelFormat.Bgra8888, AlphaFormat.Premul, art.GetPixels(),
            new PixelSize(MarkSize, MarkSize), new Vector(96, 96), art.RowBytes);
    }

    /// <summary>
    /// The tray icon in <paramref name="palette"/>. Windows gets an .ico with a frame drawn for each tray
    /// size, because the shell shrinks a single large bitmap without filtering; elsewhere it is one PNG.
    /// </summary>
    internal static WindowIcon CreateTrayIcon(AccentPalettes.Palette palette, bool isWindows)
    {
        var colors = AppIconArt.ColorsFor(palette);
        byte[] data;
        if (isWindows)
        {
            data = AppIconArt.CreateIco(colors, TrayIconSizes);
        }
        else
        {
            using var art = AppIconArt.Render(colors, MarkSize);
            data = AppIconArt.EncodePng(art);
        }

        using var stream = new MemoryStream(data);
        return new WindowIcon(stream);
    }

    /// <summary>
    /// The PNG fallback is only for unbundled macOS launches; packaged apps keep their system-rendered icon.
    /// Inspect the executable path, not the single-file extraction directory.
    /// </summary>
    internal static bool ShouldUseRuntimeDockIcon(bool isMacOS, string? processPath)
    {
        if (!isMacOS || string.IsNullOrWhiteSpace(processPath))
            return false;

        var executableDirectory = Path.GetDirectoryName(processPath);
        var contentsDirectory = Path.GetDirectoryName(executableDirectory);
        var bundleDirectory = Path.GetDirectoryName(contentsDirectory);
        var isAppBundle =
            string.Equals(Path.GetFileName(executableDirectory), "MacOS", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(contentsDirectory), "Contents", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetExtension(bundleDirectory), ".app", StringComparison.OrdinalIgnoreCase);

        return !isAppBundle;
    }

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
    /// on macOS the window icon is cosmetic (the Dock uses the bundle icon or an unbundled fallback). Best-effort.
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
