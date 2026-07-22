using System;
using Avalonia;
using Avalonia.Input;

namespace Lumi.Services;

public static class UiScaleService
{
    public const string ResourceKey = "Lumi.UiScale";
    public const int DefaultScalePercent = 100;
    public const int MinimumScalePercent = 25;
    public const int MaximumScalePercent = 500;
    public const int LegacyDefaultFontSize = 14;

    private static readonly int[] BrowserScaleLevels =
    [
        25, 33, 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400, 500
    ];

    public static int LevelCount => BrowserScaleLevels.Length;
    public static int MaximumLevelIndex => BrowserScaleLevels.Length - 1;

    public static int NormalizeScalePercent(int scalePercent)
    {
        var closestLevel = BrowserScaleLevels[0];
        var closestDistance = Math.Abs((long)scalePercent - closestLevel);

        for (var index = 1; index < BrowserScaleLevels.Length; index++)
        {
            var level = BrowserScaleLevels[index];
            var distance = Math.Abs((long)scalePercent - level);
            if (distance >= closestDistance)
                continue;

            closestLevel = level;
            closestDistance = distance;
        }

        return closestLevel;
    }

    public static int MigrateLegacyFontSize(int fontSize)
    {
        if (fontSize <= 0)
            return DefaultScalePercent;

        var legacyScalePercent = (int)Math.Round(
            fontSize / (double)LegacyDefaultFontSize * 100,
            MidpointRounding.AwayFromZero);
        return NormalizeScalePercent(legacyScalePercent);
    }

    public static int GetLevelIndex(int scalePercent)
        => Array.IndexOf(BrowserScaleLevels, NormalizeScalePercent(scalePercent));

    public static int GetScalePercentAtLevelIndex(int levelIndex)
        => BrowserScaleLevels[Math.Clamp(levelIndex, 0, MaximumLevelIndex)];

    public static int AdjustScalePercent(int scalePercent, int delta)
    {
        var currentLevelIndex = GetLevelIndex(scalePercent);
        return GetScalePercentAtLevelIndex(currentLevelIndex + delta);
    }

    public static double GetScale(int scalePercent)
        => NormalizeScalePercent(scalePercent) / 100d;

    public static int GetShortcutDelta(Key key, KeyModifiers modifiers)
    {
        var primaryModifier = OperatingSystem.IsMacOS()
            ? KeyModifiers.Meta
            : KeyModifiers.Control;
        var allowedModifiers = primaryModifier | KeyModifiers.Shift;

        if ((modifiers & primaryModifier) == 0 || (modifiers & ~allowedModifiers) != 0)
            return 0;

        return key switch
        {
            Key.OemPlus or Key.Add => 1,
            Key.OemMinus or Key.Subtract => -1,
            _ => 0
        };
    }

    public static void Apply(int scalePercent)
    {
        if (Application.Current is { } app)
            app.Resources[ResourceKey] = GetScale(scalePercent);
    }
}
