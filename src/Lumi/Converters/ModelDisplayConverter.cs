using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Lumi.ViewModels;

namespace Lumi.Converters;

/// <summary>
/// Converts a raw model ID string (e.g. "claude-opus-4.6-1m") into a
/// human-friendly display name (e.g. "Claude Opus 4.6 1M").
/// </summary>
public class ModelDisplayConverter : IValueConverter
{
    public static readonly ModelDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ChatViewModel.FormatModelDisplay(value as string) ?? value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
