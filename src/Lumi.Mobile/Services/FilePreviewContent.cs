using System.Globalization;
using System.Text;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.Services;

/// <summary>How a file Lumi made is shown in its preview.</summary>
public enum FilePreviewKind
{
    /// <summary>Text whose kind is only known once it is read: no extension, or one Lumi doesn't know.</summary>
    Unknown,
    Image,
    Markdown,
    Code,
    Text,
    Csv,
    Pdf,

    /// <summary>Nothing to draw here (Office documents, archives, media): it opens in another app.</summary>
    Unsupported
}

/// <summary>The desktop's file preview rules at phone limits: what a file is, and its text.</summary>
public static class FilePreviewContent
{
    /// <summary>As on the desktop: enough for any report or source file, bounded for a phone.</summary>
    public const int MaxCharacters = 100_000;

    /// <summary>Highlighting tokenizes on the UI thread, so very long code shows as plain text.</summary>
    public const int MaxHighlightedCharacters = 48_000;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"
    };

    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdown", ".mkd"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".log"
    };

    /// <summary>Formats a phone cannot draw. They are never downloaded just to say so.</summary>
    private static readonly HashSet<string> UnsupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".docm", ".dot", ".dotx", ".xls", ".xlsx", ".xlsm", ".xlsb", ".ppt", ".pptx", ".pptm",
        ".rtf", ".odt", ".ods", ".odp", ".pages", ".numbers", ".key", ".vsdx", ".one",
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".cab", ".iso", ".dmg",
        ".exe", ".dll", ".msi", ".msix", ".appx", ".apk", ".aab", ".ipa", ".bin", ".so", ".dylib", ".pdb",
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma",
        ".mp4", ".mov", ".avi", ".mkv", ".webm", ".m4v", ".wmv",
        ".heic", ".heif", ".tif", ".tiff", ".psd", ".ai", ".eps", ".raw", ".ico", ".icns",
        ".ttf", ".otf", ".woff", ".woff2", ".sqlite", ".db", ".mdb", ".accdb", ".parquet", ".pkl"
    };

    /// <summary>
    /// Source extensions and the highlighting grammar each one uses. Strata resolves a grammar by
    /// extension, so most map to themselves.
    /// </summary>
    private static readonly Dictionary<string, string> CodeLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "cs", [".csx"] = "cs", [".vb"] = "vb", [".fs"] = "fs", [".fsx"] = "fs",
        [".ts"] = "ts", [".tsx"] = "tsx", [".js"] = "js", [".jsx"] = "jsx", [".mjs"] = "js", [".cjs"] = "js",
        [".py"] = "py", [".pyw"] = "py", [".ipynb"] = "json",
        [".java"] = "java", [".kt"] = "kt", [".kts"] = "kt", [".scala"] = "scala", [".groovy"] = "groovy",
        [".gradle"] = "groovy",
        [".c"] = "c", [".h"] = "c", [".cpp"] = "cpp", [".cc"] = "cpp", [".cxx"] = "cpp", [".hpp"] = "cpp",
        [".hh"] = "cpp", [".m"] = "m", [".mm"] = "cpp",
        [".rs"] = "rs", [".go"] = "go", [".swift"] = "swift", [".dart"] = "dart", [".rb"] = "rb",
        [".php"] = "php", [".pl"] = "pl", [".lua"] = "lua", [".r"] = "r", [".jl"] = "jl",
        [".sh"] = "sh", [".bash"] = "sh", [".zsh"] = "sh", [".ps1"] = "ps1", [".psm1"] = "ps1",
        [".psd1"] = "ps1", [".bat"] = "bat", [".cmd"] = "bat",
        [".sql"] = "sql", [".graphql"] = "graphql", [".gql"] = "graphql", [".proto"] = "proto",
        [".json"] = "json", [".jsonc"] = "json", [".json5"] = "json",
        [".xml"] = "xml", [".xaml"] = "xml", [".axaml"] = "xml", [".csproj"] = "xml", [".fsproj"] = "xml",
        [".vbproj"] = "xml", [".props"] = "xml", [".targets"] = "xml", [".slnx"] = "xml", [".resx"] = "xml",
        [".config"] = "xml", [".plist"] = "xml", [".svg"] = "xml", [".manifest"] = "xml",
        [".yaml"] = "yaml", [".yml"] = "yaml", [".toml"] = "toml", [".ini"] = "ini", [".cfg"] = "ini",
        [".conf"] = "ini", [".properties"] = "ini", [".editorconfig"] = "ini", [".env"] = "ini",
        [".html"] = "html", [".htm"] = "html", [".css"] = "css", [".scss"] = "scss", [".less"] = "less",
        [".vue"] = "vue", [".svelte"] = "svelte",
        [".dockerfile"] = "dockerfile", [".cmake"] = "cmake", [".makefile"] = "makefile",
        [".diff"] = "diff", [".patch"] = "diff", [".tex"] = "tex"
    };

    public static FilePreviewKind Classify(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (ImageExtensions.Contains(extension))
            return FilePreviewKind.Image;
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return FilePreviewKind.Pdf;
        if (MarkdownExtensions.Contains(extension))
            return FilePreviewKind.Markdown;
        if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase))
            return FilePreviewKind.Csv;
        if (TextExtensions.Contains(extension) && !IsNamedCodeFile(fileName))
            return FilePreviewKind.Text;
        if (CodeLanguages.ContainsKey(extension) || IsNamedCodeFile(fileName))
            return FilePreviewKind.Code;
        if (UnsupportedExtensions.Contains(extension))
            return FilePreviewKind.Unsupported;
        return FilePreviewKind.Unknown;
    }

    /// <summary>The highlighting grammar for a source file, or null for plain text.</summary>
    public static string? CodeLanguage(string fileName) =>
        Path.GetFileName(fileName).ToLowerInvariant() switch
        {
            "dockerfile" => "dockerfile",
            "makefile" => "makefile",
            "cmakelists.txt" => "cmake",
            _ => CodeLanguages.TryGetValue(Path.GetExtension(fileName), out var language) ? language : null
        };

    private static bool IsNamedCodeFile(string fileName) =>
        Path.GetFileName(fileName).ToLowerInvariant() is "dockerfile" or "makefile" or "cmakelists.txt";

    /// <summary>
    /// Reads up to <see cref="MaxCharacters"/> of a text file. Returns null when the file is binary: a
    /// control character a text file would not hold, or bytes that are not valid UTF-8.
    /// </summary>
    public static async Task<FilePreviewText?> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxCharacters + 1];
        var length = 0;
        try
        {
            while (length < buffer.Length)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                for (var i = length; i < length + read; i++)
                {
                    if (char.IsControl(buffer[i]) && buffer[i] is not ('\r' or '\n' or '\t' or '\f'))
                        return null;
                }
                length += read;
            }
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        var count = Math.Min(length, MaxCharacters);
        if (count > 0 && char.IsHighSurrogate(buffer[count - 1]))
            count--;
        return new FilePreviewText(new string(buffer, 0, count), length > MaxCharacters);
    }

    /// <summary>A byte count the way the phone shows sizes: "940 B", "12 KB", "1.4 MB".</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => Strings.Get("FileSizeBytes", bytes),
        < 1024 * 1024 => Strings.Get("FileSizeKilobytes",
            Math.Max(1, Math.Round(bytes / 1024d)).ToString("0", CultureInfo.CurrentCulture)),
        _ => Strings.Get("FileSizeMegabytes",
            (bytes / (1024d * 1024d)).ToString(bytes < 10L * 1024 * 1024 ? "0.0" : "0", CultureInfo.CurrentCulture))
    };
}

public sealed record FilePreviewText(string Text, bool IsTruncated);
