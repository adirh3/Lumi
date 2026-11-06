using System;
using System.Globalization;
using System.Reflection;
using Avalonia.Data.Converters;
using Avalonia.Media;
using StrataTheme.Controls;

namespace Lumi.Converters;

/// <summary>
/// The line glyph for a What's new icon: the <see cref="StrataIcons"/> icon of that name, drawn with
/// <see cref="StrataLineIcon"/>. Each conversion parses a new geometry, as Strata's icons are never shared between
/// renderers.
/// </summary>
public sealed class WhatsNewIconConverter : IValueConverter
{
    public static readonly WhatsNewIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string name ? Find(name)?.GetValue(null) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>The <see cref="StrataIcons"/> icon a What's new item names, or null when Strata has none by that name.</summary>
    internal static PropertyInfo? Find(string name)
        => typeof(StrataIcons).GetProperty(name, BindingFlags.Public | BindingFlags.Static) is { } icon
           && icon.PropertyType == typeof(Geometry)
            ? icon
            : null;
}