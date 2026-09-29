using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lumi.Services.Sharing;

/// <summary>Text primitives shared by the pack writer and reader: quoting, fences and line handling.</summary>
internal static partial class PackText
{
    public static string NormalizeNewlines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>
    /// A JSON string literal that is also a valid YAML double-quoted scalar. Unlike the framework
    /// encoders it keeps non-ASCII text (names, emoji icons, Hebrew) readable instead of \u-escaping it.
    /// </summary>
    public static string Quote(string? value)
    {
        value ??= "";
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            switch (ch)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (ch < 0x20 || ch == 0x7F || ch is '\u2028' or '\u2029' or '\uFEFF'
                        || (char.IsSurrogate(ch) && !IsValidSurrogatePair(value, i)))
                    {
                        builder.Append("\\u").Append(((int)ch).ToString("X4"));
                    }
                    else if (char.IsHighSurrogate(ch))
                    {
                        builder.Append(ch).Append(value[++i]);
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static bool IsValidSurrogatePair(string value, int index)
        => char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]);

    /// <summary>A flow sequence of quoted strings (<c>["a", "b"]</c>): valid YAML and valid JSON.</summary>
    public static string QuoteList(IEnumerable<string> values)
        => "[" + string.Join(", ", values.Select(Quote)) + "]";

    /// <summary>Decodes the inside of a YAML double-quoted scalar, including YAML-only escapes.</summary>
    public static string UnescapeDoubleQuoted(string inner)
    {
        if (inner.IndexOf('\\') < 0)
            return inner;

        var builder = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            var ch = inner[i];
            if (ch != '\\' || i + 1 >= inner.Length)
            {
                builder.Append(ch);
                continue;
            }

            var escape = inner[++i];
            switch (escape)
            {
                case '0': builder.Append('\0'); break;
                case 'a': builder.Append('\a'); break;
                case 'b': builder.Append('\b'); break;
                case 't': case '\t': builder.Append('\t'); break;
                case 'n': builder.Append('\n'); break;
                case 'v': builder.Append('\v'); break;
                case 'f': builder.Append('\f'); break;
                case 'r': builder.Append('\r'); break;
                case 'e': builder.Append('\u001B'); break;
                case ' ': builder.Append(' '); break;
                case '"': builder.Append('"'); break;
                case '/': builder.Append('/'); break;
                case '\\': builder.Append('\\'); break;
                case 'N': builder.Append('\u0085'); break;
                case '_': builder.Append('\u00A0'); break;
                case 'L': builder.Append('\u2028'); break;
                case 'P': builder.Append('\u2029'); break;
                case 'x': i = AppendHex(inner, i, 2, builder); break;
                case 'u': i = AppendHex(inner, i, 4, builder); break;
                case 'U': i = AppendHex(inner, i, 8, builder); break;
                default: builder.Append('\\').Append(escape); break;
            }
        }

        return builder.ToString();
    }

    private static int AppendHex(string text, int escapeIndex, int digits, StringBuilder builder)
    {
        if (escapeIndex + digits < text.Length
            && int.TryParse(text.AsSpan(escapeIndex + 1, digits), System.Globalization.NumberStyles.HexNumber, null, out var code)
            && code is >= 0 and <= 0x10FFFF)
        {
            if (code <= 0xFFFF)
                builder.Append((char)code);
            else
                builder.Append(char.ConvertFromUtf32(code));
            return escapeIndex + digits;
        }

        builder.Append('\\').Append(text[escapeIndex]);
        return escapeIndex;
    }

    /// <summary>
    /// The shortest backtick fence that cannot be closed by anything inside <paramref name="content"/>
    /// (CommonMark: a closing fence must be at least as long as the opening one).
    /// </summary>
    public static int FenceLength(string content)
    {
        var longest = 0;
        foreach (var line in NormalizeNewlines(content).Split('\n'))
        {
            var trimmed = line.TrimStart(' ');
            var run = 0;
            while (run < trimmed.Length && trimmed[run] == '`')
                run++;
            longest = Math.Max(longest, run);
        }

        return Math.Max(3, longest + 1);
    }

    public static void AppendFence(StringBuilder builder, string info, string content)
    {
        var fence = new string('`', FenceLength(content));
        builder.Append(fence).Append(info).Append('\n');
        builder.Append(content);
        if (content.Length > 0 && content[^1] != '\n')
            builder.Append('\n');
        builder.Append(fence).Append("\n\n");
    }

    /// <summary>Inline code that survives backticks in the text (CommonMark variable-length code spans).</summary>
    public static string InlineCode(string text)
    {
        var longest = 0;
        var run = 0;
        foreach (var ch in text)
        {
            run = ch == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var delimiter = new string('`', longest + 1);
        var padding = text.StartsWith('`') || text.EndsWith('`') ? " " : "";
        return delimiter + padding + text + padding + delimiter;
    }

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex FenceOpenPattern();

    public readonly record struct FencedBlock(string? Tag, string Content);

    /// <summary>
    /// Every fenced code block in a markdown document, in order. Untagged blocks are returned too so a
    /// caller can skip them as a unit; <see cref="FencedBlock.Tag"/> is the <c>lumi:*</c> token, if any.
    /// </summary>
    public static IEnumerable<FencedBlock> FindFencedBlocks(string markdown)
    {
        var lines = NormalizeNewlines(markdown).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var open = FenceOpenPattern().Match(lines[i]);
            if (!open.Success)
                continue;

            var fence = open.Groups[1].Value;
            var info = open.Groups[2].Value.Trim();
            if (fence[0] == '`' && info.Contains('`'))
                continue;

            var end = i + 1;
            while (end < lines.Length && !IsClosingFence(lines[end], fence[0], fence.Length))
                end++;

            var content = string.Join('\n', lines[(i + 1)..Math.Min(end, lines.Length)]);
            var tag = info.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(static token => token.StartsWith("lumi:", StringComparison.OrdinalIgnoreCase));
            yield return new FencedBlock(tag?.ToLowerInvariant(), content);
            i = end;
        }
    }

    private static bool IsClosingFence(string line, char fenceChar, int minimumLength)
    {
        var indent = 0;
        while (indent < line.Length && indent < 4 && line[indent] == ' ')
            indent++;
        if (indent > 3)
            return false;

        var run = 0;
        while (indent + run < line.Length && line[indent + run] == fenceChar)
            run++;

        return run >= minimumLength && line[(indent + run)..].Trim().Length == 0;
    }

    /// <summary>
    /// When a whole paste is a single fenced block (copied out of a README or a chat), returns its body.
    /// </summary>
    public static bool TryUnwrapSingleFence(string text, out string inner)
    {
        inner = text;
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal) && !trimmed.StartsWith("~~~", StringComparison.Ordinal))
            return false;

        var blocks = FindFencedBlocks(trimmed).ToList();
        if (blocks.Count != 1)
            return false;

        var lines = NormalizeNewlines(trimmed).Split('\n');
        var fenceChar = trimmed[0];
        if (lines.Length < 2 || !lines[^1].TrimStart().StartsWith(new string(fenceChar, 3), StringComparison.Ordinal))
            return false;

        inner = blocks[0].Content;
        return true;
    }

    /// <summary>Collapses any whitespace run (including line breaks) to one space, for single-line fields.</summary>
    public static string SingleLine(string? value, int maxLength = 0)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
                builder.Append(' ');
            pendingSpace = false;
            builder.Append(ch);
        }

        var result = builder.ToString();
        if (maxLength > 0 && result.Length > maxLength)
            result = result[..maxLength].TrimEnd();
        return result;
    }

    public static bool SameText(string? left, string? right)
        => string.Equals(
            NormalizeNewlines(left ?? "").Trim(),
            NormalizeNewlines(right ?? "").Trim(),
            StringComparison.Ordinal);

    /// <summary>"pdf-processing" → "Pdf Processing"; names that already read as a title are kept as written.</summary>
    public static string HumanizeName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Any(char.IsUpper) || trimmed.Any(char.IsWhiteSpace))
            return trimmed;

        var words = trimmed.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0
            ? trimmed
            : string.Join(' ', words.Select(static word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    public static string FormatSize(string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        return bytes < 1024
            ? bytes.ToString(System.Globalization.CultureInfo.CurrentCulture) + " B"
            : (bytes / 1024.0).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " KB";
    }

    public static JsonDocumentOptions LenientJson { get; } = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 32
    };
}

/// <summary>
/// The YAML subset that real <c>SKILL.md</c> front matter uses: plain, single- and double-quoted
/// scalars (single or multi-line), <c>|</c>/<c>&gt;</c> block scalars, one-level maps (for
/// <c>metadata</c>), block and flow sequences, and comments. Anything fancier is ignored rather than
/// rejected, so a foreign skill with exotic front matter still imports.
/// </summary>
internal sealed class Frontmatter
{
    private readonly Dictionary<string, object> _values;

    private Frontmatter(Dictionary<string, object> values) => _values = values;

    public bool Has(string key) => _values.ContainsKey(key);

    public string? GetString(string key)
        => _values.TryGetValue(key, out var value) && value is string text ? text : null;

    public IReadOnlyDictionary<string, string>? GetMap(string key)
        => _values.TryGetValue(key, out var value) ? value as IReadOnlyDictionary<string, string> : null;

    /// <summary>A sequence value; null when the key is absent or is not a sequence.</summary>
    public IReadOnlyList<string>? GetList(string key)
        => _values.TryGetValue(key, out var value) ? value as IReadOnlyList<string> : null;

    /// <summary>
    /// Splits <paramref name="text"/> into front matter and body. The body starts after the closing
    /// <c>---</c>, skipping the single blank separator line the writers emit.
    /// </summary>
    public static bool TryParse(string text, out Frontmatter frontmatter, out string body, out string? error)
    {
        frontmatter = new Frontmatter(new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase));
        body = "";
        error = null;

        var lines = PackText.NormalizeNewlines(text).TrimStart('\uFEFF').Split('\n');
        var start = 0;
        while (start < lines.Length && lines[start].Trim().Length == 0)
            start++;

        if (start >= lines.Length || lines[start].TrimEnd() != "---")
        {
            error = "the file must start with a --- line";
            return false;
        }

        var end = -1;
        for (var i = start + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd();
            if (trimmed is "---" or "...")
            {
                end = i;
                break;
            }
        }

        if (end < 0)
        {
            error = "the closing --- line is missing";
            return false;
        }

        frontmatter = new Frontmatter(ParseEntries(lines[(start + 1)..end]));

        var bodyStart = end + 1;
        if (bodyStart < lines.Length && lines[bodyStart].Trim().Length == 0)
            bodyStart++;
        body = bodyStart < lines.Length ? string.Join('\n', lines[bodyStart..]) : "";
        return true;
    }

    private static Dictionary<string, object> ParseEntries(string[] lines)
    {
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (IsBlankOrComment(line) || Indent(line) > 0 || !TrySplitKey(line, out var key, out var rest))
            {
                i++;
                continue;
            }

            i++;
            var childStart = i;
            while (i < lines.Length && (lines[i].Trim().Length == 0 || Indent(lines[i]) > 0))
                i++;

            values[key] = ParseValue(rest, lines[childStart..i]);
        }

        return values;
    }

    private static object ParseValue(string rest, string[] children)
    {
        if (rest.StartsWith('|') || rest.StartsWith('>'))
            return ParseBlockScalar(rest[0] == '|', children);

        if (rest.Length == 0)
        {
            var firstChild = children.FirstOrDefault(static child => !IsBlankOrComment(child));
            if (firstChild is null)
                return "";

            var trimmedChild = firstChild.TrimStart();
            return trimmedChild == "-" || trimmedChild.StartsWith("- ", StringComparison.Ordinal)
                ? ParseBlockSequence(children)
                : ParseMap(children);
        }

        if (rest[0] is '"' or '\'' or '[' or '{')
        {
            var combined = rest + (children.Length == 0
                ? ""
                : " " + string.Join(' ', children.Select(static child => child.Trim()).Where(static child => child.Length > 0)));
            return rest[0] switch
            {
                '"' => ReadDoubleQuoted(combined),
                '\'' => ReadSingleQuoted(combined),
                '[' => ParseFlowSequence(combined),
                _ => ParseFlowMap(combined)
            };
        }

        var plain = StripComment(rest);
        var continuation = children
            .Select(static child => child.Trim())
            .Where(static child => child.Length > 0 && !child.StartsWith('#'));
        return string.Join(' ', new[] { plain }.Concat(continuation)).Trim();
    }

    private static string ParseBlockScalar(bool literal, string[] children)
    {
        var indent = children.Where(static child => child.Trim().Length > 0).Select(Indent).DefaultIfEmpty(0).Min();
        var content = children
            .Select(child => child.Trim().Length == 0 ? "" : child[Math.Min(indent, child.Length)..])
            .ToList();
        while (content.Count > 0 && content[^1].Length == 0)
            content.RemoveAt(content.Count - 1);

        if (literal)
            return string.Join('\n', content);

        var builder = new StringBuilder();
        foreach (var line in content)
        {
            if (line.Length == 0)
            {
                builder.Append('\n');
                continue;
            }

            if (builder.Length > 0 && builder[^1] != '\n')
                builder.Append(' ');
            builder.Append(line.Trim());
        }

        return builder.ToString();
    }

    private static List<string> ParseBlockSequence(string[] children)
    {
        var items = new List<string>();
        foreach (var child in children)
        {
            var trimmed = child.Trim();
            if (trimmed == "-")
                items.Add("");
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                items.Add(ReadScalar(trimmed[2..].Trim()));
        }

        return items;
    }

    private static Dictionary<string, string> ParseMap(string[] children)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nonBlank = children.Where(static child => !IsBlankOrComment(child)).ToList();
        if (nonBlank.Count == 0)
            return map;

        var level = nonBlank.Min(Indent);
        foreach (var child in nonBlank)
        {
            if (Indent(child) != level || !TrySplitKey(child.Trim(), out var key, out var value))
                continue;
            map[key] = ReadScalar(value);
        }

        return map;
    }

    private static List<string> ParseFlowSequence(string text)
    {
        var end = text.LastIndexOf(']');
        var inner = end > 0 ? text[..(end + 1)] : text + "]";
        try
        {
            using var document = JsonDocument.Parse(inner, PackText.LenientJson);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                return document.RootElement.EnumerateArray()
                    .Select(static item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.GetRawText())
                    .ToList();
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Unquoted YAML flow items ([Read, Grep]) are not JSON; split them by hand below.
        }

        var body = inner.Trim().TrimStart('[').TrimEnd(']');
        return body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ReadScalar)
            .Where(static item => item.Length > 0)
            .ToList();
    }

    private static object ParseFlowMap(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text[..(text.LastIndexOf('}') + 1)], PackText.LenientJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                // Indexer, not ToDictionary: a repeated key keeps the last value instead of throwing.
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    map[property.Name] = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? ""
                        : property.Value.GetRawText();
                }

                return map;
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            // Not JSON-compatible; treat as an opaque value.
        }

        return text;
    }

    private static string ReadScalar(string text)
    {
        if (text.StartsWith('"'))
            return ReadDoubleQuoted(text);
        if (text.StartsWith('\''))
            return ReadSingleQuoted(text);
        return StripComment(text);
    }

    private static string ReadDoubleQuoted(string text)
    {
        var builder = new StringBuilder();
        for (var i = 1; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\\' && i + 1 < text.Length)
            {
                builder.Append(ch).Append(text[++i]);
                continue;
            }

            if (ch == '"')
                break;
            builder.Append(ch);
        }

        return PackText.UnescapeDoubleQuoted(builder.ToString());
    }

    private static string ReadSingleQuoted(string text)
    {
        var builder = new StringBuilder();
        for (var i = 1; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    builder.Append('\'');
                    i++;
                    continue;
                }

                break;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string StripComment(string text)
    {
        var index = text.IndexOf(" #", StringComparison.Ordinal);
        return (index >= 0 ? text[..index] : text).Trim();
    }

    private static bool TrySplitKey(string line, out string key, out string rest)
    {
        key = "";
        rest = "";
        var colon = line.IndexOf(':');
        while (colon > 0 && colon + 1 < line.Length && line[colon + 1] != ' ' && line[colon + 1] != '\t')
            colon = line.IndexOf(':', colon + 1);
        if (colon <= 0)
            return false;

        key = line[..colon].Trim().Trim('"', '\'');
        rest = line[(colon + 1)..].Trim();
        return key.Length > 0;
    }

    private static bool IsBlankOrComment(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length == 0 || trimmed.StartsWith('#');
    }

    private static int Indent(string line)
    {
        var count = 0;
        while (count < line.Length && (line[count] == ' ' || line[count] == '\t'))
            count++;
        return count;
    }
}
