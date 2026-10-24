using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Lumi.Models;
using StrataTheme.Controls;

namespace Lumi.Converters;

/// <summary>
/// The line glyph for an AI feature on Settings → AI Features, by <see cref="AiFeatureIds"/> id. A geometry is parsed
/// per conversion, as Strata's icons are never shared between renderers.
/// </summary>
public sealed class AiFeatureIconConverter : IValueConverter
{
    public static readonly AiFeatureIconConverter Instance = new();

    /// <summary>What a feature reads: an eye, beside the words for it.</summary>
    public static Geometry Eye => StreamGeometry.Parse(
        "M2.5 12s3.5-6.5 9.5-6.5 9.5 6.5 9.5 6.5-3.5 6.5-9.5 6.5S2.5 12 2.5 12z M12 9.25a2.75 2.75 0 1 1 0 5.5 2.75 2.75 0 0 1 0-5.5z");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        AiFeatureIds.ChatTitles => StreamGeometry.Parse(
            "M4 12.6V5.5A1.5 1.5 0 0 1 5.5 4h7.1a1.5 1.5 0 0 1 1.06.44l6.4 6.4a1.5 1.5 0 0 1 0 2.12l-7.1 7.1a1.5 1.5 0 0 1-2.12 0l-6.4-6.4A1.5 1.5 0 0 1 4 12.6z M8.5 8.5h.01"),
        AiFeatureIds.FollowUps => StrataIcons.Lightbulb,
        AiFeatureIds.Memories => StrataIcons.Bookmark,
        AiFeatureIds.DailyBriefs => StrataIcons.Document,
        AiFeatureIds.SharedPrompts => StreamGeometry.Parse(
            "M12 14.5v-11 M7.75 7.75 12 3.5l4.25 4.25 M5 11.5v7A1.5 1.5 0 0 0 6.5 20h11a1.5 1.5 0 0 0 1.5-1.5v-7"),
        AiFeatureIds.Welcome => StrataIcons.Hand,
        _ => null
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
