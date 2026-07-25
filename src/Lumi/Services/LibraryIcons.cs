using System.Collections.Generic;
using Avalonia.Media;

namespace Lumi.Services;

/// <summary>
/// Vector glyphs for the Library's data-driven surfaces (collection rail, artifact cards, detail
/// pane). The facets and cards are built from data, so the icons are resolved in the ViewModel
/// rather than looked up as XAML resources. All paths use the non-zero fill rule (<c>F1</c>) so
/// counter-wound sub-paths punch clean holes and overlapping details simply union.
/// </summary>
/// <remarks>
/// Path data is kept as strings and parsed on first use: <see cref="StreamGeometry.Parse"/> needs
/// Avalonia's render interface, which pure ViewModel unit tests do not initialise.
/// </remarks>
public static class LibraryIcons
{
    /// <summary>Sparkle-in-a-stack: the "Everything" collection.</summary>
    public const string EverythingPath =
        "F1 M12 2.4 15.2 8.8 21.6 12 15.2 15.2 12 21.6 8.8 15.2 2.4 12 8.8 8.8 12 2.4z " +
        "M4.6 3.2 5.6 5.2 7.6 6.2 5.6 7.2 4.6 9.2 3.6 7.2 1.6 6.2 3.6 5.2z";

    public const string PhotoPath =
        "F1 M5.5 4h13A1.5 1.5 0 0 1 20 5.5v13a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 18.5v-13A1.5 1.5 0 0 1 5.5 4z " +
        "M5.6 5.6v12.8h12.8V5.6H5.6z " +
        "M9 7.6a1.7 1.7 0 1 1 0 3.4 1.7 1.7 0 0 1 0-3.4z " +
        "M6.2 18.4 9.6 14.1 11.9 16.9 14.9 13.3 18.2 18.4z";

    public const string DocumentPath =
        "F1 M7.5 2h6.1a1 1 0 0 1 .75.32l4.4 4.6a1 1 0 0 1 .25.68v12.9A1.5 1.5 0 0 1 17.5 22h-10A1.5 1.5 0 0 1 6 20.5v-17A1.5 1.5 0 0 1 7.5 2z " +
        "M7.6 3.6v16.8h9.8V9.1h-3.7a1.4 1.4 0 0 1-1.4-1.4V3.6H7.6z " +
        "M9.4 11.5h6.2v1.5H9.4z M9.4 14.4h6.2v1.5H9.4z M9.4 17.3h4.1v1.5H9.4z";

    public const string SheetPath =
        "F1 M5.5 4h13A1.5 1.5 0 0 1 20 5.5v13a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 18.5v-13A1.5 1.5 0 0 1 5.5 4z " +
        "M5.6 5.6v12.8h12.8V5.6H5.6z " +
        "M5.6 5.6h12.8v2.5H5.6z " +
        "M6.9 9.5h4v3.1h-4z M12.1 9.5h4v3.1h-4z M6.9 13.8h4v3.1h-4z M12.1 13.8h4v3.1h-4z";

    public const string SlidesPath =
        "F1 M5.5 4h13A1.5 1.5 0 0 1 20 5.5v8.5a1.5 1.5 0 0 1-1.5 1.5h-5.7v2.1h3.1v1.6H8.1v-1.6h3.1v-2.1H5.5A1.5 1.5 0 0 1 4 14V5.5A1.5 1.5 0 0 1 5.5 4z " +
        "M5.6 5.6v8.3h12.8V5.6H5.6z " +
        "M8.8 7.9h1.6v5H8.8z M11.2 9.6h1.6v3.3h-1.6z M13.6 6.9h1.6v6h-1.6z";

    public const string CodePath =
        "F1 M8.7 5.7 10.1 7.1 5.2 12l4.9 4.9-1.4 1.4L2.4 12z " +
        "M15.3 5.7 21.6 12l-6.3 6.3-1.4-1.4 4.9-4.9-4.9-4.9z";

    public const string MediaPath =
        "F1 M5.5 4h13A1.5 1.5 0 0 1 20 5.5v13a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 18.5v-13A1.5 1.5 0 0 1 5.5 4z " +
        "M5.6 5.6v12.8h12.8V5.6H5.6z " +
        "M10 8.5 16.2 12 10 15.5z";

    public const string ArchivePath =
        "F1 M3.6 4.4h16.8a1 1 0 0 1 1 1v3.2a1 1 0 0 1-1 1H20v8.9A1.5 1.5 0 0 1 18.5 20h-13A1.5 1.5 0 0 1 4 18.5V9.6h-.4a1 1 0 0 1-1-1V5.4a1 1 0 0 1 1-1z " +
        "M5.2 6v2h13.6V6H5.2z " +
        "M5.6 9.6v8.8h12.8V9.6H5.6z " +
        "M9.4 11.9h5.2v1.7H9.4z";

    public const string GlobePath =
        "F1 M12 2.6a9.4 9.4 0 1 1 0 18.8 9.4 9.4 0 0 1 0-18.8z " +
        "M12 4.4a7.6 7.6 0 1 0 0 15.2 7.6 7.6 0 0 0 0-15.2z " +
        "M4.7 11.2h14.6v1.6H4.7z " +
        "M12 4.4a4.2 7.6 0 1 1 0 15.2 4.2 7.6 0 0 1 0-15.2z " +
        "M12 6.1a2.6 5.9 0 1 0 0 11.8 2.6 5.9 0 0 0 0-11.8z";

    public const string FilePath =
        "F1 M7.5 2h6.1a1 1 0 0 1 .75.32l4.4 4.6a1 1 0 0 1 .25.68v12.9A1.5 1.5 0 0 1 17.5 22h-10A1.5 1.5 0 0 1 6 20.5v-17A1.5 1.5 0 0 1 7.5 2z " +
        "M7.6 3.6v16.8h9.8V9.1h-3.7a1.4 1.4 0 0 1-1.4-1.4V3.6H7.6z";

    public const string FolderPath =
        "F1 M4 4.6h5.1a1.5 1.5 0 0 1 1.1.47l1.6 1.73h8.2A1.5 1.5 0 0 1 21.5 8.3v9.6a1.5 1.5 0 0 1-1.5 1.5H4a1.5 1.5 0 0 1-1.5-1.5V6.1A1.5 1.5 0 0 1 4 4.6z";

    /// <summary>Tray with an up arrow: artifacts the user sent into a chat.</summary>
    public const string UploadPath =
        "F1 M12 2.6 17.4 8.5h-3.3v6.1H9.9V8.5H6.6z " +
        "M4.4 14.2h1.9v4.2h11.4v-4.2h1.9v4.6a1.5 1.5 0 0 1-1.5 1.5H5.9a1.5 1.5 0 0 1-1.5-1.5z";

    /// <summary>Four-point spark: artifacts Lumi produced.</summary>
    public const string SparkPath =
        "F1 M12 2.2 15.1 9.4 22.3 12.5 15.1 15.6 12 22.8 8.9 15.6 1.7 12.5 8.9 9.4z";

    /// <summary>Magnifier: marks the chip that mirrors the search box.</summary>
    public const string SearchPath =
        "F1 M10.6 3.2a7.4 7.4 0 1 1 0 14.8 7.4 7.4 0 0 1 0-14.8z " +
        "M10.6 5a5.6 5.6 0 1 0 0 11.2 5.6 5.6 0 0 0 0-11.2z " +
        "M15.9 14.6 21.3 20l-1.3 1.3-5.4-5.4z";

    /// <summary>Clock face: marks the chip that mirrors the time-window filter.</summary>
    public const string ClockPath =
        "F1 M12 2.6a9.4 9.4 0 1 1 0 18.8 9.4 9.4 0 0 1 0-18.8z " +
        "M12 4.4a7.6 7.6 0 1 0 0 15.2 7.6 7.6 0 0 0 0-15.2z " +
        "M11.2 6.9h1.7v5.5l4.1 2.4-.85 1.45-4.95-2.9z";

    /// <summary>Git branch: a worktree checked out by a chat.</summary>
    public const string BranchPath =
        "F1 M6.6 2.4a3.4 3.4 0 0 1 .85 6.69v5.82a3.4 3.4 0 1 1-1.7 0V9.09A3.4 3.4 0 0 1 6.6 2.4z " +
        "M6.6 4.1a1.7 1.7 0 1 0 0 3.4 1.7 1.7 0 0 0 0-3.4z " +
        "M6.6 16.5a1.7 1.7 0 1 0 0 3.4 1.7 1.7 0 0 0 0-3.4z " +
        "M17.4 2.4a3.4 3.4 0 0 1 .85 6.69V10a4.6 4.6 0 0 1-4.6 4.6h-2.5v-1.7h2.5A2.9 2.9 0 0 0 16.55 10V9.09A3.4 3.4 0 0 1 17.4 2.4z " +
        "M17.4 4.1a1.7 1.7 0 1 0 0 3.4 1.7 1.7 0 0 0 0-3.4z";

    private static readonly Dictionary<string, Geometry> ParsedCache = new(StringComparer.Ordinal);

    /// <summary>Maps an artifact kind to the glyph used by both the collection rail and its cards.</summary>
    public static string PathForKind(LibraryArtifactKind kind) => kind switch
    {
        LibraryArtifactKind.Image => PhotoPath,
        LibraryArtifactKind.Document => DocumentPath,
        LibraryArtifactKind.Sheet => SheetPath,
        LibraryArtifactKind.Slides => SlidesPath,
        LibraryArtifactKind.Code => CodePath,
        LibraryArtifactKind.Media => MediaPath,
        LibraryArtifactKind.Archive => ArchivePath,
        LibraryArtifactKind.Link => GlobePath,
        LibraryArtifactKind.Worktree => BranchPath,
        _ => FilePath
    };

    /// <summary>Parses path data once and reuses the geometry for every card that needs it.</summary>
    public static Geometry Parse(string data)
    {
        lock (ParsedCache)
        {
            if (ParsedCache.TryGetValue(data, out var cached))
                return cached;

            var geometry = StreamGeometry.Parse(data);
            ParsedCache[data] = geometry;
            return geometry;
        }
    }

    public static Geometry ForKind(LibraryArtifactKind kind) => Parse(PathForKind(kind));

    /// <summary>Test hook: every declared glyph parses into a non-empty geometry.</summary>
    internal static IEnumerable<string> AllPaths()
    {
        yield return EverythingPath;
        yield return PhotoPath;
        yield return DocumentPath;
        yield return SheetPath;
        yield return SlidesPath;
        yield return CodePath;
        yield return MediaPath;
        yield return ArchivePath;
        yield return GlobePath;
        yield return FilePath;
        yield return FolderPath;
        yield return UploadPath;
        yield return SparkPath;
        yield return SearchPath;
        yield return ClockPath;
        yield return BranchPath;
    }
}

/// <summary>
/// Per-kind accent colours. A single grey list reads as a file dump; giving each collection its own
/// hue makes the Library scannable at a glance and is what carries most of its visual identity.
/// </summary>
/// <remarks>
/// Brushes are plain <see cref="SolidColorBrush"/> instances, so unlike geometry they resolve
/// without a platform render backend and stay safe to touch from ViewModel unit tests.
/// </remarks>
public static class LibraryPalette
{
    private static readonly Dictionary<LibraryArtifactKind, (IBrush Accent, IBrush Tint)> Palette = new()
    {
        [LibraryArtifactKind.Image] = Pair("#A78BFA"),
        [LibraryArtifactKind.Document] = Pair("#60A5FA"),
        [LibraryArtifactKind.Sheet] = Pair("#34D399"),
        [LibraryArtifactKind.Slides] = Pair("#FB923C"),
        [LibraryArtifactKind.Code] = Pair("#FBBF24"),
        [LibraryArtifactKind.Media] = Pair("#F472B6"),
        [LibraryArtifactKind.Archive] = Pair("#94A3B8"),
        [LibraryArtifactKind.Link] = Pair("#38BDF8"),
        [LibraryArtifactKind.Worktree] = Pair("#2DD4BF"),
        [LibraryArtifactKind.Other] = Pair("#A1A1AA")
    };

    /// <summary>Saturated hue used for the glyph itself.</summary>
    public static IBrush Accent(LibraryArtifactKind kind) => Palette[kind].Accent;

    /// <summary>App accent, for rail entries that span every collection rather than one kind.</summary>
    public static IBrush NeutralAccent { get; } = new SolidColorBrush(Color.Parse("#818CF8")).ToImmutable();

    /// <summary>The same hue at low alpha, used behind the glyph so the chip reads as a swatch.</summary>
    public static IBrush Tint(LibraryArtifactKind kind) => Palette[kind].Tint;

    private static (IBrush, IBrush) Pair(string hex)
    {
        var color = Color.Parse(hex);
        var accent = new SolidColorBrush(color).ToImmutable();
        var tint = new SolidColorBrush(Color.FromArgb(0x2E, color.R, color.G, color.B)).ToImmutable();
        return (accent, tint);
    }
}
