using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Media;
using Lumi.Models;

namespace Lumi.Services.Appearance;

/// <summary>
/// The material behind Lumi's windows. "auto" is the translucent blur each platform does best
/// (acrylic on Windows, vibrancy on macOS); Windows 11 can use Mica instead; "solid" turns the
/// see-through off for an opaque, calmer window.
/// </summary>
public static class WindowMaterials
{
    private static readonly ConditionalWeakTable<Window, IDisposable> SolidBackgrounds = new();

    /// <summary>The materials this platform can show, in picker order. Mica needs Windows 11.</summary>
    public static IReadOnlyList<string> Available { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
        ? [UserSettings.WindowMaterialAuto, UserSettings.WindowMaterialMica, UserSettings.WindowMaterialSolid]
        : [UserSettings.WindowMaterialAuto, UserSettings.WindowMaterialSolid];

    public static string Normalize(string? id)
        => Available.FirstOrDefault(material => string.Equals(material, id, StringComparison.OrdinalIgnoreCase))
           ?? UserSettings.WindowMaterialAuto;

    public static IReadOnlyList<WindowTransparencyLevel> HintFor(string? id) => Normalize(id) switch
    {
        UserSettings.WindowMaterialSolid => [WindowTransparencyLevel.None],
        UserSettings.WindowMaterialMica =>
            [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur],
        _ when OperatingSystem.IsMacOS() => [WindowTransparencyLevel.Blur],
        _ => [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Mica, WindowTransparencyLevel.Blur],
    };

    /// <summary>
    /// Shows <paramref name="id"/> on <paramref name="window"/>. A solid window paints the theme's
    /// background itself, so nothing shows through where its islands are translucent.
    /// </summary>
    public static void Apply(Window window, string? id)
    {
        ArgumentNullException.ThrowIfNull(window);

        var solid = Normalize(id) == UserSettings.WindowMaterialSolid;
        window.TransparencyLevelHint = HintFor(id);

        if (solid)
        {
            if (!SolidBackgrounds.TryGetValue(window, out _))
                SolidBackgrounds.Add(window, window.Bind(Window.BackgroundProperty, window.GetResourceObservable("Brush.Background")));
            return;
        }

        if (SolidBackgrounds.TryGetValue(window, out var binding))
        {
            binding.Dispose();
            SolidBackgrounds.Remove(window);
        }
        window.Background = Brushes.Transparent;
    }
}
