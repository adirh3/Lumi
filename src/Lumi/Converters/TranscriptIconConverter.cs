using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Lumi.Services;
using StrataTheme.Controls;

namespace Lumi.Converters;

/// <summary>
/// Maps a transcript icon key (see <see cref="Lumi.Services.ToolDisplayHelper.GetToolIconKey"/>) onto
/// Strata's line icon for it, so each step in the work trail says what it is at a glance. A geometry is
/// parsed per conversion: Strata's icons are never shared between renderers.
/// </summary>
public sealed class TranscriptIconConverter : IValueConverter
{
    public static readonly TranscriptIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string key ? Resolve(key) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    internal static Geometry Resolve(string key) => TryResolve(key) ?? StrataIcons.Cube;

    /// <summary>The key's own icon, or null when it has none and is drawn as the generic tool.</summary>
    internal static Geometry? TryResolve(string key) => key switch
    {
        TranscriptIcons.Read => StrataIcons.Document,
        TranscriptIcons.Create => StrataIcons.DocumentPlus,
        TranscriptIcons.Edit => StrataIcons.Pencil,
        TranscriptIcons.Search => StrataIcons.Search,
        TranscriptIcons.Web => StrataIcons.Globe,
        TranscriptIcons.Page => StrataIcons.Book,
        TranscriptIcons.Terminal => StrataIcons.Terminal,
        TranscriptIcons.Plan => StrataIcons.Checklist,
        TranscriptIcons.Screenshot => StrataIcons.Camera,
        TranscriptIcons.Desktop => StrataIcons.Monitor,
        TranscriptIcons.Memory => StrataIcons.Bookmark,
        TranscriptIcons.Skill => StrataIcons.Wand,
        TranscriptIcons.Project => StrataIcons.Folder,
        TranscriptIcons.Agent => StrataIcons.Person,
        TranscriptIcons.Connector => StrataIcons.Plug,
        TranscriptIcons.Chat => StrataIcons.Chat,
        TranscriptIcons.Chats => StrataIcons.Chats,
        TranscriptIcons.Test => StrataIcons.Beaker,
        TranscriptIcons.Data => StrataIcons.Database,
        TranscriptIcons.Question => StrataIcons.Question,
        TranscriptIcons.Mixed => StrataIcons.Layers,
        _ => null,
    };
}
