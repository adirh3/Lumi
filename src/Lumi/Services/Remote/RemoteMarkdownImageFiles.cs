using Lumi.Models;
using Lumi.Remote.Protocol;

namespace Lumi.Services.Remote;

internal static class RemoteMarkdownImageFiles
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"
        };

    public static IReadOnlySet<string> BuildAuthorizedPaths(
        IReadOnlyList<ChatMessage> messages)
    {
        var paths = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var attachment in message.Attachments)
            {
                if (TryResolveLocalPath(attachment, out var path))
                    paths.Add(path);
            }

            if (!string.Equals(
                    message.ToolName,
                    "announce_file",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var announcedPath = ToolDisplayHelper.ExtractJsonField(
                message.Content,
                "filePath");
            if (announcedPath is not null
                && TryResolveLocalPath(announcedPath, out var announced))
            {
                paths.Add(announced);
            }
        }

        return paths;
    }

    public static List<RemoteInlineImage>? BuildDescriptors(
        string? markdown,
        IReadOnlySet<string> authorizedPaths)
    {
        List<RemoteInlineImage>? images = null;
        foreach (var reference in RemoteMarkdownImages.Find(markdown))
        {
            if (!TryResolveAuthorizedPath(
                    reference.Target,
                    authorizedPaths,
                    out var path))
            {
                continue;
            }

            (images ??= []).Add(new RemoteInlineImage
            {
                Index = reference.Index,
                FileName = Path.GetFileName(path)
            });
        }

        return images;
    }

    public static bool TryResolveReferencedPath(
        string? markdown,
        int imageIndex,
        IReadOnlySet<string> authorizedPaths,
        out string path)
    {
        path = "";
        var reference = RemoteMarkdownImages.Find(markdown)
            .FirstOrDefault(candidate => candidate.Index == imageIndex);
        return reference.Target is { Length: > 0 }
               && TryResolveAuthorizedPath(
                   reference.Target,
                   authorizedPaths,
                   out path);
    }

    private static bool TryResolveAuthorizedPath(
        string target,
        IReadOnlySet<string> authorizedPaths,
        out string path)
    {
        return TryResolveLocalPath(target, out path)
               && authorizedPaths.Contains(path);
    }

    private static bool TryResolveLocalPath(string target, out string path)
    {
        path = "";
        try
        {
            string candidate;
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
            {
                if (!uri.IsFile || uri.IsUnc || !string.IsNullOrEmpty(uri.Host))
                    return false;
                candidate = uri.LocalPath;
            }
            else
            {
                candidate = target;
            }

            if (!Path.IsPathFullyQualified(candidate))
                return false;
            var fullPath = Path.GetFullPath(candidate);
            if (OperatingSystem.IsWindows()
                    ? fullPath.StartsWith(@"\\", StringComparison.Ordinal)
                      || fullPath.StartsWith("//", StringComparison.Ordinal)
                      || fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
                      || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal)
                    : fullPath.StartsWith("//", StringComparison.Ordinal))
            {
                return false;
            }

            if (!SupportedExtensions.Contains(Path.GetExtension(fullPath)))
                return false;

            path = fullPath;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
