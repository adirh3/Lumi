using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using StrataTheme.Controls;

namespace Lumi.Mobile.Views;

/// <summary>
/// Maps the desktop's transcript icon keys (Lumi's <c>TranscriptIcons</c>, sent on each step) onto
/// Strata's line icons, exactly as the desktop transcript draws them. An unknown or missing key is
/// drawn as the generic step. A geometry is parsed per conversion: Strata's icons are never shared
/// between renderers.
/// </summary>
public sealed class TranscriptIconConverter : IValueConverter
{
    public static readonly TranscriptIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Resolve(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    public static Geometry Resolve(string? key) => TryResolve(key) ?? StrataIcons.Cube;

    /// <summary>The key's own icon, or null when it has none and is drawn as the generic step.</summary>
    public static Geometry? TryResolve(string? key) => key switch
    {
        "read" => StrataIcons.Document,
        "create" => StrataIcons.DocumentPlus,
        "edit" => StrataIcons.Pencil,
        "search" => StrataIcons.Search,
        "web" => StrataIcons.Globe,
        "page" => StrataIcons.Book,
        "terminal" => StrataIcons.Terminal,
        "plan" => StrataIcons.Checklist,
        "screenshot" => StrataIcons.Camera,
        "desktop" => StrataIcons.Monitor,
        "memory" => StrataIcons.Bookmark,
        "skill" => StrataIcons.Wand,
        "project" => StrataIcons.Folder,
        "agent" => StrataIcons.Person,
        "connector" => StrataIcons.Plug,
        "chat" => StrataIcons.Chat,
        "chats" => StrataIcons.Chats,
        "test" => StrataIcons.Beaker,
        "data" => StrataIcons.Database,
        "question" => StrataIcons.Question,
        "mixed" => StrataIcons.Layers,
        "think" => StrataIcons.Lightbulb,
        "deleted" => StrataIcons.Cross,
        _ => null
    };
}

/// <summary>
/// A file's type as a line icon, in the muted hues the rest of the app uses for categories, where
/// the desktop shows the system's file icon. Emoji would read as a toy here.
/// </summary>
public sealed class FileTypeIconConverter : IValueConverter
{
    public static readonly FileTypeIconConverter Instance = new();

    // 24-unit line drawings in Strata's icon style.
    private const string ImagePath =
        "M5 5.5h14a1.5 1.5 0 0 1 1.5 1.5v10a1.5 1.5 0 0 1-1.5 1.5H5A1.5 1.5 0 0 1 3.5 17V7A1.5 1.5 0 0 1 5 5.5z M3.5 15.5l4.5-4.5 4 4 2.5-2.5 6 6 M15.5 8.25a1.25 1.25 0 1 0 0 2.5 1.25 1.25 0 1 0 0-2.5z";
    private const string SheetPath = "M5 4.5h14a.5.5 0 0 1 .5.5v14a.5.5 0 0 1-.5.5H5a.5.5 0 0 1-.5-.5V5a.5.5 0 0 1 .5-.5z M4.5 9.5h15 M4.5 14.5h15 M10 9.5v10";
    private const string CodePath = "M9 7.5 4.5 12 9 16.5 M15 7.5l4.5 4.5-4.5 4.5";
    private const string SlidesPath = "M3.5 5.5h17 M5 5.5v9a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-9 M12 15.5v4 M9 19.5h6";
    private const string AudioPath = "M9 17.5V6.5l10-2v11 M6.5 20a2.5 2.5 0 1 0 0-5 2.5 2.5 0 0 0 0 5z M16.5 18a2.5 2.5 0 1 0 0-5 2.5 2.5 0 0 0 0 5z";
    private const string VideoPath = "M4.5 6.5h10a1.5 1.5 0 0 1 1.5 1.5v8a1.5 1.5 0 0 1-1.5 1.5h-10A1.5 1.5 0 0 1 3 16V8a1.5 1.5 0 0 1 1.5-1.5z M16 10.5l5-3v9l-5-3";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string name ? Create(name) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    public static IImage Create(string fileName)
    {
        var (geometry, color) = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" or ".heic" =>
                (StreamGeometry.Parse(ImagePath), Color.Parse("#A59BDD")),
            ".xls" or ".xlsx" or ".csv" or ".tsv" => (StreamGeometry.Parse(SheetPath), Color.Parse("#7FBF9A")),
            ".pdf" => (StrataIcons.Document, Color.Parse("#D98C8C")),
            ".ppt" or ".pptx" or ".key" => (StreamGeometry.Parse(SlidesPath), Color.Parse("#E39A7B")),
            ".cs" or ".ts" or ".tsx" or ".js" or ".py" or ".java" or ".cpp" or ".c" or ".h" or ".rs" or ".go"
                or ".kt" or ".swift" or ".axaml" or ".xaml" or ".html" or ".css" or ".json" or ".xml" or ".yaml"
                or ".yml" or ".toml" or ".ps1" or ".sh" or ".sql" => (StreamGeometry.Parse(CodePath), Color.Parse("#CDAE70")),
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".apk" => (StrataIcons.Cube, Color.Parse("#B9B2A4")),
            ".mp3" or ".wav" or ".ogg" or ".flac" or ".m4a" => (StreamGeometry.Parse(AudioPath), Color.Parse("#CC9AB3")),
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm" => (StreamGeometry.Parse(VideoPath), Color.Parse("#84BDB2")),
            ".doc" or ".docx" or ".md" or ".txt" or ".rtf" or ".log" => (StrataIcons.Document, Color.Parse("#8FB4D4")),
            _ => (StrataIcons.Document, Color.Parse("#A3A3AE"))
        };

        var pen = new Pen(new SolidColorBrush(color), 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        return new DrawingImage(new DrawingGroup
        {
            Children =
            {
                // The frame keeps the drawing at its 24-unit size, so every icon scales alike.
                new GeometryDrawing { Geometry = new RectangleGeometry(new Rect(0, 0, 24, 24)), Brush = Brushes.Transparent },
                new GeometryDrawing { Geometry = geometry, Pen = pen }
            }
        });
    }
}

/// <summary>A file's type in a word for its chip ("PNG", "MD"), where the desktop shows its size.</summary>
public sealed class FileTypeLabelConverter : IValueConverter
{
    public static readonly FileTypeLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string extension && extension.TrimStart('.') is { Length: > 0 } bare
            ? bare.ToUpperInvariant()
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
