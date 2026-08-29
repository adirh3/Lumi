using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Lumi.Converters;

public sealed class ChatTagBrushConverter : IValueConverter
{
    public static readonly ChatTagBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => CreateBrush(value as string, string.Equals(parameter as string, "subtle", StringComparison.OrdinalIgnoreCase));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    public static IBrush CreateBrush(string? hex, bool subtle = false)
    {
        var color = Color.TryParse(hex, out var parsed)
            ? parsed
            : Color.Parse(Lumi.Models.ChatTag.DefaultColor);
        if (subtle)
            color = Color.FromArgb(0x2E, color.R, color.G, color.B);
        return new SolidColorBrush(color).ToImmutable();
    }
}
