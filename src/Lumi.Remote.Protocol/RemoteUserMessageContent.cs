namespace Lumi.Remote.Protocol;

/// <summary>Separates the mobile attachment instructions from the visible user message.</summary>
public static class RemoteUserMessageContent
{
    public static (string Text, List<RemoteAttachment> Attachments) Parse(string? content, string? author)
    {
        var text = content ?? "";
        if (!string.Equals(author, "Lumi Mobile", StringComparison.Ordinal))
            return (text, []);

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var marker = Array.FindLastIndex(lines, line => line == "Attached files:");
        if (marker < 0 || marker == lines.Length - 1)
            return (text, []);

        var attachments = new List<RemoteAttachment>();
        for (var index = marker + 1; index < lines.Length; index++)
        {
            var path = lines[index].Trim();
            if (path.Length == 0)
                continue;

            string? name = null;
            if (path.StartsWith("Name: ", StringComparison.Ordinal))
            {
                name = path[6..].Trim();
                if (++index >= lines.Length)
                    return (text, []);
                path = lines[index].Trim();
            }

            // Windows paths must also be recognized on an Android/Linux client.
            if (!IsAbsolutePath(path))
                return (text, []);

            attachments.Add(new RemoteAttachment
            {
                Path = path,
                FileName = LeafName(string.IsNullOrWhiteSpace(name) ? path : name),
                Extension = Path.GetExtension(LeafName(path))
            });
        }

        return attachments.Count == 0
            ? (text, [])
            : (string.Join('\n', lines.Take(marker)).TrimEnd(), attachments);
    }

    public static string BuildPrompt(string text, IEnumerable<RemoteAttachment> attachments)
    {
        var files = attachments.ToArray();
        if (files.Length == 0)
            return text.Trim();

        var block = files.SelectMany(file => new[]
        {
            $"Name: {LeafName(file.FileName).Replace('\r', ' ').Replace('\n', ' ')}",
            file.Path
        });
        return string.Join('\n', new[] { text.Trim() }.Where(part => part.Length > 0)
            .Concat(["Attached files:"])
            .Concat(block));
    }

    private static bool IsAbsolutePath(string path) =>
        path.StartsWith('/')
        || path.StartsWith(@"\\", StringComparison.Ordinal)
        || path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    private static string LeafName(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized[(normalized.LastIndexOf('/') + 1)..];
    }
}
