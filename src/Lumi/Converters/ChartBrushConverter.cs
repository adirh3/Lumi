using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Lumi.Converters;

/// <summary>
/// Maps a 1-based accent index onto the theme's cycling chart palette (Brush.Chart1..6),
/// used to give each kind of sub-agent a stable color.
/// </summary>
public sealed class ChartBrushConverter : IValueConverter
{
    public static readonly ChartBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var index = value is int i ? i : 1;
        var slot = ((index - 1) % 6 + 6) % 6 + 1;
        var theme = Application.Current?.ActualThemeVariant;
        if (Application.Current?.TryGetResource($"Brush.Chart{slot}", theme, out var brush) == true && brush is IBrush b)
            return b;
        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
