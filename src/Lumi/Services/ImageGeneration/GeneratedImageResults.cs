using System.Text;

namespace Lumi.Services.ImageGeneration;

/// <summary>
/// What an <c>image_generation</c> call reports, whichever route ran it: one <c>saved_to: &lt;path&gt;</c> line per
/// image it saved, as GitHub Copilot's native tool writes them and Lumi's own tool copies.
/// </summary>
public static class GeneratedImageResults
{
    public const string ToolName = "image_generation";
    private const string SavedToPrefix = "saved_to:";

    /// <summary>The absolute paths a result's <c>saved_to</c> lines name, in order and without duplicates.</summary>
    public static List<string> ParseSavedPaths(string? resultText)
    {
        var paths = new List<string>();
        if (string.IsNullOrWhiteSpace(resultText))
            return paths;

        foreach (var rawLine in resultText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(SavedToPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var path = line[SavedToPrefix.Length..].Trim().Trim('"', '`');
            if (path.Length == 0 || !Path.IsPathFullyQualified(path))
                continue;
            path = Path.GetFullPath(path);
            if (!paths.Contains(path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
                paths.Add(path);
        }

        return paths;
    }

    /// <summary>
    /// What the model reads after an image is created: Lumi keeps generated images out of the reply until the model
    /// shows them, so it can create images for the user and images that only feed other work (frames for a video,
    /// a texture) alike. The tool's own description, which says when to create one, comes from Copilot.
    /// </summary>
    public static string? BuildPresentationGuidance(IReadOnlyList<string> savedPaths)
    {
        if (savedPaths.Count == 0)
            return null;

        var text = new StringBuilder();
        text.Append("The user doesn't see this image until you show it. To show it, put it in your reply as a markdown image of its saved path, for example ");
        text.Append("![short description](").Append(MarkdownTarget(savedPaths[0])).Append(')');
        text.Append(". Leave it out when it is only material for other work. It is already saved, so don't also announce_file it.");
        return text.ToString();
    }

    /// <summary>A path as a markdown link target, in angle brackets when it has spaces.</summary>
    internal static string MarkdownTarget(string path)
        => path.Contains(' ') ? "<" + path + ">" : path;
}
