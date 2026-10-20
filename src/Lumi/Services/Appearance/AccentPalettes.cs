using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Lumi.Models;

namespace Lumi.Services.Appearance;

/// <summary>
/// Accent palettes. A palette recolours Strata's accent tokens: buttons, toggles, selection, links,
/// focus, the user's chat bubbles and the gradient marks. The ambient presence reads the same tokens,
/// so the glow follows too. "lumi" is Strata's own indigo and writes nothing.
/// </summary>
public static class AccentPalettes
{
    public const string DefaultId = UserSettings.DefaultAccentColor;

    /// <summary>The colours of one palette in one theme.</summary>
    public sealed record Tones(
        Color Accent,
        Color Hover,
        Color Pressed,
        Color Subtle,
        Color SubtleHover,
        Color Link,
        Color Selection,
        Color Bubble,
        Color BubbleText,
        Color BubbleTextSecondary,
        Color BubbleSelection,
        Color Secondary,
        Color Tertiary);

    public sealed record Palette(string Id, string NameKey, Tones Dark, Tones Light)
    {
        public bool IsDefault => Id == DefaultId;

        public Tones For(bool dark) => dark ? Dark : Light;
    }

    public static IReadOnlyList<Palette> All { get; } =
    [
        // Strata's own tokens, listed so the picker can draw them; never written.
        new("lumi", "Accent_Lumi",
            T("#818CF8", "#A5B4FC", "#6366F1", "#1E1B4B", "#312E81", "#A5B4FC", "#3730A3",
              "#312E81", "#F0EEFF", "#CBC5F0", "#4F46E5", "#A78BFA", "#F472B6"),
            T("#4F46E5", "#6366F1", "#4338CA", "#EEF2FF", "#E0E7FF", "#4F46E5", "#C7D2FE",
              "#DDD6FE", "#1E1B3A", "#3B3660", "#A5B4FC", "#7C3AED", "#EC4899")),
        new("ocean", "Accent_Ocean",
            T("#60A5FA", "#93C5FD", "#3B82F6", "#172554", "#1E3A8A", "#93C5FD", "#1E40AF",
              "#1E3A8A", "#EFF6FF", "#BFDBFE", "#2563EB", "#22D3EE", "#2DD4BF"),
            T("#2563EB", "#3B82F6", "#1D4ED8", "#EFF6FF", "#DBEAFE", "#2563EB", "#BFDBFE",
              "#BFDBFE", "#172554", "#1E3A8A", "#93C5FD", "#0891B2", "#0D9488")),
        new("lagoon", "Accent_Lagoon",
            T("#14B8A6", "#2DD4BF", "#0D9488", "#042F2E", "#134E4A", "#5EEAD4", "#115E59",
              "#134E4A", "#F0FDFA", "#99F6E4", "#0F766E", "#22D3EE", "#34D399"),
            T("#0D9488", "#14B8A6", "#0F766E", "#F0FDFA", "#CCFBF1", "#0F766E", "#99F6E4",
              "#CCFBF1", "#042F2E", "#115E59", "#5EEAD4", "#0891B2", "#059669")),
        new("forest", "Accent_Forest",
            T("#10B981", "#34D399", "#059669", "#022C22", "#064E3B", "#6EE7B7", "#065F46",
              "#064E3B", "#ECFDF5", "#A7F3D0", "#047857", "#A3E635", "#2DD4BF"),
            T("#059669", "#10B981", "#047857", "#ECFDF5", "#D1FAE5", "#047857", "#A7F3D0",
              "#D1FAE5", "#022C22", "#065F46", "#6EE7B7", "#65A30D", "#0D9488")),
        new("sunset", "Accent_Sunset",
            T("#F97316", "#FB923C", "#EA580C", "#431407", "#7C2D12", "#FDBA74", "#9A3412",
              "#7C2D12", "#FFF7ED", "#FED7AA", "#C2410C", "#FB7185", "#FBBF24"),
            T("#EA580C", "#F97316", "#C2410C", "#FFF7ED", "#FFEDD5", "#C2410C", "#FED7AA",
              "#FFEDD5", "#431407", "#9A3412", "#FDBA74", "#E11D48", "#D97706")),
        new("blossom", "Accent_Blossom",
            T("#FB7185", "#FDA4AF", "#F43F5E", "#4C0519", "#881337", "#FDA4AF", "#9F1239",
              "#881337", "#FFF1F2", "#FECDD3", "#E11D48", "#E879F9", "#FB923C"),
            T("#E11D48", "#F43F5E", "#BE123C", "#FFF1F2", "#FFE4E6", "#BE123C", "#FECDD3",
              "#FFE4E6", "#4C0519", "#9F1239", "#FDA4AF", "#C026D3", "#EA580C")),
        new("grape", "Accent_Grape",
            T("#C084FC", "#D8B4FE", "#A855F7", "#3B0764", "#581C87", "#D8B4FE", "#6B21A8",
              "#581C87", "#FAF5FF", "#E9D5FF", "#9333EA", "#F472B6", "#818CF8"),
            T("#9333EA", "#A855F7", "#7E22CE", "#FAF5FF", "#F3E8FF", "#7E22CE", "#E9D5FF",
              "#F3E8FF", "#3B0764", "#6B21A8", "#D8B4FE", "#DB2777", "#4F46E5")),
        new("graphite", "Accent_Graphite",
            T("#A1A1AA", "#D4D4D8", "#71717A", "#27272A", "#3F3F46", "#D4D4D8", "#52525B",
              "#3F3F46", "#FAFAFA", "#D4D4D8", "#71717A", "#94A3B8", "#A8A29E"),
            T("#52525B", "#71717A", "#3F3F46", "#F4F4F5", "#E4E4E7", "#3F3F46", "#D4D4D8",
              "#E4E4E7", "#18181B", "#3F3F46", "#A1A1AA", "#475569", "#78716C")),
    ];

    public static Palette Find(string? id)
        => All.FirstOrDefault(palette => string.Equals(palette.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static string Normalize(string? id) => Find(id).Id;

    /// <summary>
    /// Shows <paramref name="id"/> across the app. The palette is one merged dictionary of
    /// <paramref name="resources"/>, swapped whole, so a change refreshes the UI once.
    /// </summary>
    public static void Apply(IResourceDictionary resources, string? id)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var palette = Find(id);
        var merged = resources.MergedDictionaries;
        var current = merged.OfType<PaletteDictionary>().ToList();
        if (palette.IsDefault ? current.Count == 0 : current.Count == 1 && current[0].PaletteId == palette.Id)
            return;

        if (palette.IsDefault)
        {
            foreach (var dictionary in current)
                merged.Remove(dictionary);
            return;
        }

        var replacement = new PaletteDictionary(palette.Id);
        replacement.ThemeDictionaries[ThemeVariant.Dark] = Build(palette.Dark, dark: true);
        replacement.ThemeDictionaries[ThemeVariant.Light] = Build(palette.Light, dark: false);

        if (current.Count == 0)
        {
            merged.Add(replacement);
            return;
        }

        merged[merged.IndexOf(current[0])] = replacement;
        foreach (var stale in current.Skip(1))
            merged.Remove(stale);
    }

    /// <summary>The id of the palette applied to <paramref name="resources"/>.</summary>
    public static string AppliedId(IResourceDictionary resources)
        => resources.MergedDictionaries.OfType<PaletteDictionary>().LastOrDefault()?.PaletteId ?? DefaultId;

    /// <summary>A round swatch for the picker: the accent leaning into its glow colour.</summary>
    public static IBrush CreateSwatch(Palette palette, bool dark)
    {
        var tones = palette.For(dark);
        return Diagonal(tones.Accent, tones.Secondary, startOffset: 0.35);
    }

    internal static ResourceDictionary Build(Tones tones, bool dark)
    {
        var dictionary = new ResourceDictionary();

        void SetColor(string name, Color color)
        {
            dictionary["Color." + name] = color;
            dictionary["Brush." + name] = new SolidColorBrush(color);
        }

        SetColor("AccentDefault", tones.Accent);
        SetColor("AccentHover", tones.Hover);
        SetColor("AccentPressed", tones.Pressed);
        SetColor("AccentSubtle", tones.Subtle);
        SetColor("AccentSubtleHover", tones.SubtleHover);
        SetColor("BorderFocus", tones.Accent);
        SetColor("TextLink", tones.Link);
        SetColor("SelectionBackground", tones.Selection);
        SetColor("ChatUserBubble", tones.Bubble);
        SetColor("ChatUserBubbleText", tones.BubbleText);
        SetColor("ChatUserBubbleTextSecondary", tones.BubbleTextSecondary);
        SetColor("ChatUserBubbleSelection", tones.BubbleSelection);
        dictionary["Color.AccentViolet"] = tones.Secondary;
        dictionary["Color.AccentRose"] = tones.Tertiary;

        dictionary["Brush.AccentGradient"] = Diagonal(dark ? tones.Accent : tones.Pressed, tones.Secondary);
        dictionary["Brush.AccentGradientHover"] = Diagonal(
            dark ? tones.Hover : tones.Accent,
            Mix(tones.Secondary, Colors.White, dark ? 0.25 : 0.12));
        dictionary["Brush.AccentGradientPressed"] = Diagonal(
            dark ? tones.Pressed : Mix(tones.Pressed, Colors.Black, 0.15),
            Mix(tones.Secondary, Colors.Black, 0.2));
        dictionary["Brush.AccentElevationBorder"] = Gradient(
            new(0, 0, RelativeUnit), new(0, 1, RelativeUnit),
            (0.9, tones.Hover), (1.0, dark ? tones.Accent : tones.Pressed));
        dictionary["Brush.StratumLine"] = Gradient(
            new(0, 0.5, RelativeUnit), new(1, 0.5, RelativeUnit),
            (0, tones.Accent), (0.5, tones.Secondary), (1, tones.Tertiary));
        dictionary["Brush.SelectionGradient"] = Gradient(
            new(0, 0.5, RelativeUnit), new(1, 0.5, RelativeUnit),
            (0, WithAlpha(tones.Accent, dark ? (byte)0x20 : (byte)0x18)),
            (0.6, WithAlpha(dark ? tones.Secondary : tones.Hover, dark ? (byte)0x10 : (byte)0x0C)),
            (1, WithAlpha(tones.Tertiary, dark ? (byte)0x06 : (byte)0x04)));
        dictionary["Brush.AccentGlowSoft"] = new SolidColorBrush(WithAlpha(tones.Accent, dark ? (byte)0x28 : (byte)0x20));
        dictionary["Brush.AccentGlowStrong"] = new SolidColorBrush(WithAlpha(tones.Accent, dark ? (byte)0x44 : (byte)0x38));
        return dictionary;
    }

    private const Avalonia.RelativeUnit RelativeUnit = Avalonia.RelativeUnit.Relative;

    private static Tones T(
        string accent, string hover, string pressed, string subtle, string subtleHover, string link,
        string selection, string bubble, string bubbleText, string bubbleTextSecondary,
        string bubbleSelection, string secondary, string tertiary)
        => new(
            Color.Parse(accent), Color.Parse(hover), Color.Parse(pressed), Color.Parse(subtle),
            Color.Parse(subtleHover), Color.Parse(link), Color.Parse(selection), Color.Parse(bubble),
            Color.Parse(bubbleText), Color.Parse(bubbleTextSecondary), Color.Parse(bubbleSelection),
            Color.Parse(secondary), Color.Parse(tertiary));

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Mix(Color color, Color other, double amount)
    {
        static byte Channel(byte from, byte to, double t) => (byte)Math.Round(from + (to - from) * t);
        return Color.FromRgb(Channel(color.R, other.R, amount), Channel(color.G, other.G, amount), Channel(color.B, other.B, amount));
    }

    private static LinearGradientBrush Diagonal(Color from, Color to, double startOffset = 0)
        => Gradient(new(0, 0, RelativeUnit), new(1, 1, RelativeUnit), (startOffset, from), (1, to));

    private static LinearGradientBrush Gradient(
        Avalonia.RelativePoint start, Avalonia.RelativePoint end, params (double Offset, Color Color)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var (offset, color) in stops)
            brush.GradientStops.Add(new GradientStop(color, offset));
        return brush;
    }

    /// <summary>Marks the merged dictionary that holds the applied palette.</summary>
    private sealed class PaletteDictionary(string paletteId) : ResourceDictionary
    {
        public string PaletteId { get; } = paletteId;
    }
}
