using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Lumi.Converters;

/// <summary>
/// Converts a <see cref="bool"/> to an opacity value: <c>true</c> → 1.0, <c>false</c> → 0.0.
/// Used to drive a fade (via a DoubleTransition) on an element that stays present in the
/// visual tree, so it can cross-fade out instead of being hard-cut by an IsVisible toggle.
/// </summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 1d : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
