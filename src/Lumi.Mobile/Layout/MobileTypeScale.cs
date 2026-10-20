using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace Lumi.Mobile.Layout;

/// <summary>
/// Lumi's text follows the phone's own font size, as every native app's text does. The platform head
/// converts each rung of the type scale the way it converts its own text (on Android, sp to dp, which
/// grows large text less than small text) and the rungs are restated at application level, where every
/// view and every Strata control that reads them picks the new sizes up.
/// </summary>
public static class MobileTypeScale
{
    /// <summary>Every resource that sizes text: the type scale and the titles sized apart from it.</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        "Font.SizeCaption",
        "Font.SizeMeta",
        "Font.SizeBody",
        "Font.SizeBodyStrong",
        "Font.SizeSubtitle",
        "Font.SizeTitle",
        "Font.SizeHeadline",
        "Font.SizeDisplay",
        "Mobile.Sheet.TitleSize",
        "Mobile.Place.TitleSize"
    ];

    /// <summary>
    /// Line heights that keep their proportion to the rung they space. Converting one as a size of its own
    /// would grow it less than its text at large font sizes (as large text grows less), crowding the lines.
    /// </summary>
    public static IReadOnlyList<(string LineHeight, string Size)> LineHeightKeys { get; } =
    [
        ("Font.LineHeightBody", "Font.SizeBody"),
        ("Font.LineHeightMeta", "Font.SizeMeta")
    ];

    /// <summary>
    /// Restates every rung through <paramref name="convert"/>, which maps a size designed for the default
    /// font size to the size to draw it at. Each call starts again from the designed sizes, so a new
    /// setting replaces the last one instead of compounding it, and the default removes the restatement.
    /// Call on the UI thread, before the views that should start at the new size are built.
    /// </summary>
    public static void Apply(Application application, Func<double, double> convert)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(convert);

        var resources = application.Resources;
        foreach (var key in Keys)
        {
            if (!TryGetDesignedSize(application, key, out var designed))
                continue;

            Restate(resources, key, designed, Converted(designed));
        }

        foreach (var (lineKey, sizeKey) in LineHeightKeys)
        {
            if (!TryGetDesignedSize(application, lineKey, out var designed)
                || !TryGetDesignedSize(application, sizeKey, out var designedSize) || designedSize <= 0)
                continue;

            Restate(resources, lineKey, designed, Math.Round(designed * Converted(designedSize) / designedSize, 2));
        }

        double Converted(double designed)
        {
            var size = convert(designed);
            return double.IsFinite(size) && size > 0 ? Math.Round(size, 2) : designed;
        }
    }

    private static void Restate(IResourceDictionary resources, string key, double designed, double value)
    {
        if (value == designed)
            resources.Remove(key);
        else if (!resources.TryGetValue(key, out var current) || current is not double previous || previous != value)
            resources[key] = value;
    }

    /// <summary>The size a rung was designed at, beneath any restatement made here.</summary>
    private static bool TryGetDesignedSize(Application application, string key, out double size)
    {
        // The restatement lives in the application's own dictionary; the designed scale is merged into it.
        foreach (var dictionary in application.Resources.MergedDictionaries.Reverse())
        {
            if (dictionary.TryGetResource(key, application.ActualThemeVariant, out var value) && value is double designed)
            {
                size = designed;
                return true;
            }
        }

        size = 0;
        return false;
    }
}
