using System.Text;
using System.Text.RegularExpressions;
using Lumi.Remote.Protocol;

namespace Lumi.Services.Remote;

/// <summary>
/// Turns Lumi's markdown into the plain text a notification shows: code, diagrams and other fenced
/// blocks are left out, links and images keep their words, and markup characters go.
/// </summary>
internal static partial class PhoneNotificationText
{
    public static string FromMarkdown(string? markdown, int limit = RemoteProtocol.MobileNotificationTextLimit)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "";

        var lines = new List<string>();
        var inFence = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || TableRule().IsMatch(line))
                continue;

            line = Image().Replace(line, "$1");
            line = Link().Replace(line, "$1");
            line = Heading().Replace(line, "");
            line = Quote().Replace(line, "");
            line = Bullet().Replace(line, "• ");
            line = line.Replace("**", "").Replace("__", "").Replace("~~", "");
            line = Emphasis().Replace(line, "$1");
            line = line.Replace("`", "");
            if (line.StartsWith('|'))
                line = string.Join(" · ", line.Trim('|').Split('|').Select(static cell => cell.Trim()));

            line = Whitespace().Replace(line, " ").Trim();
            if (line.Length > 0)
                lines.Add(line);
        }

        var text = string.Join('\n', lines);
        return Truncate(text, limit);
    }

    /// <summary>One line of text, cut to <paramref name="limit"/> characters with an ellipsis.</summary>
    public static string Line(string? text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        return Truncate(Whitespace().Replace(text, " ").Trim(), limit);
    }

    /// <summary>Cuts <paramref name="text"/> to <paramref name="limit"/> characters, ending in an ellipsis.</summary>
    internal static string Truncate(string text, int limit)
    {
        if (text.Length <= limit)
            return text;
        if (limit <= 0)
            return "";

        var cut = limit - 1;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return new StringBuilder(cut + 1).Append(text, 0, cut).Append('…').ToString().TrimEnd();
    }

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^#{1,6}\s+")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(>\s?)+")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^[-*+]\s+")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"(?<![\w*])\*(?!\s)([^*\n]+?)(?<!\s)\*(?![\w*])")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableRule();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
