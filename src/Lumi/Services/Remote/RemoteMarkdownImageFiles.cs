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

            string pathField;
            if (string.Equals(
                    message.ToolName,
                    "announce_file",
                    StringComparison.OrdinalIgnoreCase))
            {
                pathField = "filePath";
            }
            else if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(message.ToolName, "view", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(message.ToolStatus, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                pathField = "path";
            }
            else
            {
                continue;
            }

            var announcedPath = ToolDisplayHelper.ExtractJsonField(
                message.Content,
                pathField);
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
            if (TryResolvePublicUri(reference.Target, out var remoteUri))
            {
                (images ??= []).Add(new RemoteInlineImage
                {
                    Index = reference.Index,
                    FileName = RemoteFileName(remoteUri, reference.Index)
                });
                continue;
            }

            if (TryResolveAuthorizedPath(
                    reference.Target,
                    authorizedPaths,
                    out var path))
            {
                (images ??= []).Add(new RemoteInlineImage
                {
                    Index = reference.Index,
                    FileName = Path.GetFileName(path)
                });
            }
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

    public static bool TryResolveReferencedRemoteUri(
        string? markdown,
        int imageIndex,
        out Uri uri)
    {
        uri = null!;
        var reference = RemoteMarkdownImages.Find(markdown)
            .FirstOrDefault(candidate => candidate.Index == imageIndex);
        return reference.Target is { Length: > 0 }
               && TryResolvePublicUri(reference.Target, out uri);
    }

    private static bool TryResolveAuthorizedPath(
        string target,
        IReadOnlySet<string> authorizedPaths,
        out string path)
    {
        return TryResolveLocalPath(target, out path)
               && authorizedPaths.Contains(path);
    }

    private static bool TryResolvePublicUri(string target, out Uri uri)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out uri!)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && string.IsNullOrEmpty(uri.UserInfo)
            && !uri.IsLoopback
            && !string.Equals(uri.DnsSafeHost, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        uri = null!;
        return false;
    }

    private static string RemoteFileName(Uri uri, int imageIndex)
    {
        var name = Path.GetFileName(uri.AbsolutePath);
        return string.IsNullOrWhiteSpace(name)
            ? $"image-{imageIndex}.png"
            : name;
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
