using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lumi.Services.Sharing;

/// <summary>
/// Keeps credentials out of the parts of a capability that are shared verbatim. Environment variable
/// and header VALUES never reach this class (the pack model has no field for them); what it catches
/// are the secrets people inline elsewhere: connection-string passwords, <c>?api_key=</c> query
/// parameters, <c>--token=…</c> flags, <c>Authorization: Bearer …</c> header arguments and well-known
/// token formats. It also spots paths on this computer, which should not travel unnoticed.
/// (Invisible characters that can hide instructions are removed on import by <see cref="HiddenText"/>.)
/// </summary>
public static partial class SecretRedactor
{
    /// <summary>Stands in for every removed value, so a recipient (and the import receipt) can see what to fill in.</summary>
    public const string Placeholder = "<REDACTED>";

    private static readonly HashSet<string> SensitiveWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "apikey", "token", "accesstoken", "refreshtoken", "secret", "clientsecret", "password",
        "passwd", "pass", "pwd", "auth", "authorization", "bearer", "credential", "credentials", "sig",
        "signature", "jwt", "cookie", "session", "sessionid", "privatekey"
    };

    /// <summary>True when a flag, query parameter, header or variable name holds a credential.</summary>
    public static bool IsSensitiveName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var trimmed = name.Trim().TrimStart('-', '/');
        foreach (var word in SplitWords(trimmed))
        {
            if (SensitiveWords.Contains(word))
                return true;
        }

        return false;
    }

    /// <summary>Splits <c>--x-api_key</c>, <c>accessToken</c> and <c>GITHUB_TOKEN</c> into their words.</summary>
    private static IEnumerable<string> SplitWords(string name)
    {
        foreach (var part in name.Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            yield return part;

            var start = 0;
            for (var i = 1; i < part.Length; i++)
            {
                if (char.IsUpper(part[i]) && char.IsLower(part[i - 1]))
                {
                    yield return part[start..i];
                    start = i;
                }
            }

            if (start > 0)
                yield return part[start..];
        }
    }

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_-])(gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{22,}|glpat-[A-Za-z0-9_-]{20,}|sk-(?:ant-|proj-)?[A-Za-z0-9_-]{20,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{35}|(?:sk|rk)_(?:live|test)_[A-Za-z0-9]{10,}|npm_[A-Za-z0-9]{36}|eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,})(?![A-Za-z0-9_-])",
        RegexOptions.CultureInvariant)]
    private static partial Regex KnownTokenPattern();

    [GeneratedRegex(@"^(\$\{[A-Za-z_][A-Za-z0-9_]*\}|\$[A-Za-z_][A-Za-z0-9_]*|%[A-Za-z_][A-Za-z0-9_]*%|\$\{input:[^}]+\}|\$\{env:[^}]+\})$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentReferencePattern();

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9-]*)\s*:\s*(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderArgumentPattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex UuidPattern();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePathPattern();

    /// <summary>A whole value in a well-known token format (GitHub, OpenAI/Anthropic, Slack, AWS, Google, Stripe, npm, JWT).</summary>
    public static bool LooksLikeKnownToken(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 16)
            return false;

        var match = KnownTokenPattern().Match(value);
        return match.Success && match.Index == 0 && match.Length == value.Length && ContainsDigit(match.Value);
    }

    /// <summary>
    /// A long random-looking value (base62/base64 or hex) of the kind services put in "secret URLs".
    /// Lower-case-and-hyphen slugs and UUIDs are left alone: they are overwhelmingly identifiers.
    /// </summary>
    public static bool LooksRandomSecret(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 32 || UuidPattern().IsMatch(value))
            return false;

        bool hasDigit = false, hasLower = false, hasUpper = false, isHex = true;
        foreach (var ch in value)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '+' or '/' or '=' or '_' or '-'))
                return false;
            hasDigit |= char.IsAsciiDigit(ch);
            hasLower |= char.IsAsciiLetterLower(ch);
            hasUpper |= char.IsAsciiLetterUpper(ch);
            isHex &= char.IsAsciiHexDigit(ch);
        }

        return hasDigit && (isHex || (hasLower && hasUpper));
    }

    /// <summary>Environment/input references (<c>${TOKEN}</c>, <c>$TOKEN</c>, <c>%TOKEN%</c>) name a secret without containing it.</summary>
    public static bool IsEnvironmentReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        foreach (var scheme in new[] { "Bearer ", "Basic ", "Token " })
        {
            if (trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[scheme.Length..].Trim();
                break;
            }
        }

        return EnvironmentReferencePattern().IsMatch(trimmed);
    }

    /// <summary>
    /// Replaces credentials inside a URL while leaving every other character exactly as written:
    /// the password (or a token used as the user name), sensitive query parameter values, and
    /// random-looking path segments. Non-URLs are returned unchanged.
    /// </summary>
    public static string RedactUrl(string? url, out bool changed)
    {
        changed = false;
        if (string.IsNullOrEmpty(url))
            return url ?? "";

        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0 || !IsUrlScheme(url.AsSpan(0, schemeEnd)))
            return url;

        var authorityStart = schemeEnd + 3;

        // A password may contain an unencoded '#' (postgresql://admin:p#ss@host). Look for the
        // userinfo '@' before the first '/' or '?' so such a password is still recognized as one.
        var userInfoLimit = url.IndexOfAny(['/', '?'], authorityStart);
        if (userInfoLimit < 0)
            userInfoLimit = url.Length;
        var userInfoEnd = userInfoLimit > authorityStart
            ? url.LastIndexOf('@', userInfoLimit - 1, userInfoLimit - authorityStart)
            : -1;
        var authorityEnd = url.IndexOfAny(['/', '?', '#'], userInfoEnd >= 0 ? userInfoEnd + 1 : authorityStart);
        if (authorityEnd < 0)
            authorityEnd = url.Length;

        var builder = new StringBuilder(url.Length + 16);
        builder.Append(url, 0, authorityStart);

        var authority = url[authorityStart..authorityEnd];
        var at = userInfoEnd >= 0 ? userInfoEnd - authorityStart : -1;
        if (at >= 0)
        {
            var userInfo = authority[..at];
            var colon = userInfo.IndexOf(':');
            var user = colon >= 0 ? userInfo[..colon] : userInfo;
            var password = colon >= 0 ? userInfo[(colon + 1)..] : null;

            if (LooksLikeKnownToken(user) || LooksRandomSecret(user))
            {
                user = Placeholder;
                changed = true;
            }

            if (!string.IsNullOrEmpty(password) && password != Placeholder && !IsEnvironmentReference(password))
            {
                password = Placeholder;
                changed = true;
            }

            builder.Append(user);
            if (password is not null)
                builder.Append(':').Append(password);
            builder.Append('@').Append(authority, at + 1, authority.Length - at - 1);
        }
        else
        {
            builder.Append(authority);
        }

        var rest = url[authorityEnd..];
        var fragmentIndex = rest.IndexOf('#');
        var fragment = fragmentIndex >= 0 ? rest[fragmentIndex..] : "";
        if (fragmentIndex >= 0)
            rest = rest[..fragmentIndex];

        var queryIndex = rest.IndexOf('?');
        var path = queryIndex >= 0 ? rest[..queryIndex] : rest;
        var query = queryIndex >= 0 ? rest[(queryIndex + 1)..] : null;

        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0 || segment == Placeholder)
                continue;

            var decoded = SafeUnescape(segment);
            if (LooksLikeKnownToken(decoded) || LooksRandomSecret(decoded))
            {
                segments[i] = Placeholder;
                changed = true;
            }
        }

        builder.Append(string.Join('/', segments));

        if (query is not null)
        {
            var pairs = query.Split('&');
            for (var i = 0; i < pairs.Length; i++)
            {
                var pair = pairs[i];
                var eq = pair.IndexOf('=');
                if (eq <= 0 || eq == pair.Length - 1)
                    continue;

                var name = SafeUnescape(pair[..eq]);
                var value = SafeUnescape(pair[(eq + 1)..]);
                if (value == Placeholder || IsEnvironmentReference(value))
                    continue;

                if (IsSensitiveName(name) || LooksLikeKnownToken(value) || LooksRandomSecret(value))
                {
                    pairs[i] = pair[..eq] + "=" + Placeholder;
                    changed = true;
                }
            }

            builder.Append('?').Append(string.Join('&', pairs));
        }

        builder.Append(fragment);
        return changed ? builder.ToString() : url;
    }

    /// <summary>
    /// Redacts credentials in an MCP server's argument list. Returns the safe arguments and, in
    /// <paramref name="redacted"/>, a short label for each value that was removed (never the value).
    /// A value is checked the same way whether it is joined to its flag (<c>--header=…</c>) or
    /// follows it as the next argument, and whatever shape it has: a header, <c>KEY=value</c>, a
    /// <c>;</c>-separated connection string, a JSON config, a URL, or text with a token inside.
    /// </summary>
    public static List<string> RedactArguments(IReadOnlyList<string> args, out List<string> redacted)
    {
        var result = new List<string>(args.Count);
        redacted = [];

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i] ?? "";

            if (arg.Length > 1 && arg[0] == '-')
            {
                var eq = arg.IndexOf('=');
                if (eq > 0)
                {
                    var flag = arg[..eq];
                    var value = arg[(eq + 1)..];
                    if (ShouldRedactValue(flag, value))
                    {
                        result.Add(flag + "=" + Placeholder);
                        redacted.Add(flag);
                        continue;
                    }

                    var safeValue = RedactValue(value, redacted);
                    result.Add(ReferenceEquals(safeValue, value) ? arg : flag + "=" + safeValue);
                    continue;
                }

                result.Add(arg);
                if (IsSensitiveName(arg)
                    && i + 1 < args.Count
                    && !string.IsNullOrEmpty(args[i + 1])
                    && args[i + 1][0] != '-'
                    && ShouldRedactValue(arg, args[i + 1]))
                {
                    result.Add(Placeholder);
                    redacted.Add(arg);
                    i++;
                }

                continue;
            }

            result.Add(RedactValue(arg, redacted));
        }

        return result;
    }

    /// <summary>Returns <paramref name="value"/> itself when nothing had to be removed.</summary>
    private static string RedactValue(string value, List<string> redacted)
    {
        if (value.Length == 0)
            return value;

        // Only a value that starts with a scheme is a URL (jdbc:postgresql://… included). A connection
        // string can carry a URL in one of its pairs (Endpoint=sb://…;SharedAccessKey=…).
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        var isUrl = schemeEnd > 0 && IsUrlScheme(value.AsSpan(0, schemeEnd));

        // "Authorization: Bearer …" — the shape mcp-remote and friends take after --header / -H.
        var header = HeaderArgumentPattern().Match(value);
        if (header.Success && !isUrl && IsSensitiveName(header.Groups[1].Value))
        {
            if (ShouldRedactValue(header.Groups[1].Value, header.Groups[2].Value))
            {
                redacted.Add(header.Groups[1].Value);
                return header.Groups[1].Value + ": " + Placeholder;
            }

            return value;
        }

        // {"apiKey":"…"} passed as a config argument.
        var trimmed = value.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            var json = RedactJson(value, out var jsonChanged);
            if (json is not null)
            {
                if (jsonChanged)
                    redacted.Add("JSON");
                return jsonChanged ? json : value;
            }
        }

        // Server=…;User Id=…;Password=… connection strings, a single KEY=value pair, and the
        // ;-separated options some URLs carry (sqlserver://host;password=…). A pair whose name says
        // nothing can still hold a URL with credentials: DATABASE_URL=postgres://user:pw@db/app.
        var result = value;
        if (value.Contains('='))
        {
            var pairs = value.Split(';');
            var changed = false;
            for (var i = 0; i < pairs.Length; i++)
            {
                var eq = pairs[i].IndexOf('=');
                if (eq <= 0)
                    continue;

                var name = pairs[i][..eq].Trim();
                if (!IsIdentifier(name.Replace(" ", "", StringComparison.Ordinal).AsSpan()))
                    continue;

                var pairValue = pairs[i][(eq + 1)..];
                if (ShouldRedactValue(name, pairValue))
                {
                    pairs[i] = pairs[i][..(eq + 1)] + Placeholder;
                    redacted.Add(name);
                    changed = true;
                    continue;
                }

                var safePairValue = RedactUrl(pairValue, out var pairChanged);
                if (pairChanged)
                {
                    pairs[i] = pairs[i][..(eq + 1)] + safePairValue;
                    redacted.Add(DescribeUrl(pairValue));
                    changed = true;
                }
            }

            if (changed)
                result = string.Join(';', pairs);
        }

        var safeUrl = RedactUrl(result, out var urlChanged);
        if (urlChanged)
        {
            redacted.Add(DescribeUrl(result));
            result = safeUrl;
        }

        if (ReferenceEquals(result, value) && LooksLikeKnownToken(value))
        {
            redacted.Add(MaskSample(value));
            return Placeholder;
        }

        return RedactEmbeddedTokens(result, redacted);
    }

    /// <summary>Final safety net: a well-known token format anywhere inside a value is replaced.</summary>
    private static string RedactEmbeddedTokens(string value, List<string> redacted)
    {
        var changed = false;
        var result = KnownTokenPattern().Replace(value, match =>
        {
            if (!ContainsDigit(match.Value))
                return match.Value;

            changed = true;
            redacted.Add(MaskSample(match.Value));
            return Placeholder;
        });
        return changed ? result : value;
    }

    /// <summary>
    /// Rewrites a JSON value with the values of sensitive properties (and any token-shaped strings)
    /// replaced. Returns null when <paramref name="json"/> is not JSON.
    /// </summary>
    private static string? RedactJson(string json, out bool changed)
    {
        changed = false;
        try
        {
            using var document = JsonDocument.Parse(json, PackText.LenientJson);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                changed = WriteRedacted(document.RootElement, writer, sensitive: false);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static bool WriteRedacted(JsonElement element, Utf8JsonWriter writer, bool sensitive)
    {
        var changed = false;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    changed |= WriteRedacted(property.Value, writer, IsSensitiveName(property.Name));
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    changed |= WriteRedacted(item, writer, sensitive);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                var text = element.GetString() ?? "";
                if (sensitive && text.Length > 0 && text != Placeholder && !IsEnvironmentReference(text))
                {
                    writer.WriteStringValue(Placeholder);
                    return true;
                }

                // Any other string is checked like an argument: {"db":{"url":"postgres://user:pw@…"}}.
                // The caller reports the whole JSON value as redacted, so the labels are not needed.
                var safe = RedactValue(text, []);
                writer.WriteStringValue(safe);
                return !ReferenceEquals(safe, text);

            case JsonValueKind.Number when sensitive:
                writer.WriteStringValue(Placeholder);
                return true;

            default:
                element.WriteTo(writer);
                break;
        }

        return changed;
    }

    private static bool ShouldRedactValue(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == Placeholder || IsEnvironmentReference(value))
            return false;

        return IsSensitiveName(name) || LooksLikeKnownToken(value.Trim());
    }

    /// <summary>True for an absolute path on this computer (C:\…, \\server\…, /home/…, ~/…), including a --flag=/path value.</summary>
    public static bool LooksLikeLocalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim();
        if (candidate.Length > 1 && candidate[0] == '-')
        {
            var eq = candidate.IndexOf('=');
            if (eq < 0)
                return false;
            candidate = candidate[(eq + 1)..];
        }

        if (candidate.Length < 3 || candidate.Contains("://", StringComparison.Ordinal))
            return false;

        return DrivePathPattern().IsMatch(candidate)
               || candidate.StartsWith(@"\\", StringComparison.Ordinal)
               || candidate.StartsWith("~/", StringComparison.Ordinal)
               || candidate.StartsWith(@"~\", StringComparison.Ordinal)
               || (candidate[0] == '/' && candidate[1] != '/' && candidate.IndexOf('/', 1) > 1);
    }

    /// <summary>Masked samples ("ghp_12…9f") of token-shaped strings in free text such as skill instructions.</summary>
    public static List<string> FindTokensInText(string? text)
    {
        var samples = new List<string>();
        if (string.IsNullOrEmpty(text))
            return samples;

        foreach (Match match in KnownTokenPattern().Matches(text))
        {
            if (ContainsDigit(match.Value))
                samples.Add(MaskSample(match.Value));
        }

        return samples;
    }

    /// <summary>First few and last two characters with an ellipsis — enough to recognize, never enough to use.</summary>
    public static string MaskSample(string value)
        => value.Length <= 8 ? "…" : value[..Math.Min(6, value.Length / 3)] + "…" + value[^2..];

    private static string DescribeUrl(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
            return "URL";

        var start = schemeEnd + 3;
        var end = url.IndexOfAny(['/', '?', '#'], start);
        var authority = end < 0 ? url[start..] : url[start..end];
        var at = authority.LastIndexOf('@');
        return url[..schemeEnd] + "://" + (at >= 0 ? authority[(at + 1)..] : authority);
    }

    private static bool IsUrlScheme(ReadOnlySpan<char> scheme)
    {
        if (scheme.IsEmpty || !char.IsAsciiLetter(scheme[0]))
            return false;

        // ':' allows compound schemes such as jdbc:postgresql.
        foreach (var ch in scheme)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '+' or '-' or '.' or ':'))
                return false;
        }

        return true;
    }

    private static bool IsIdentifier(ReadOnlySpan<char> name)
    {
        foreach (var ch in name)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.'))
                return false;
        }

        return true;
    }

    private static bool ContainsDigit(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsAsciiDigit(ch))
                return true;
        }

        return false;
    }

    private static string SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}

/// <summary>
/// Removes invisible characters that can make shared text say something other than what a reviewer
/// sees: zero-width spaces and word joiners, bidirectional embedding/override/isolate controls
/// ("Trojan Source"), and Unicode tag characters (used to smuggle hidden prompts). The only tag runs
/// kept are the three standard subdivision flags (England, Scotland, Wales), and joiners that build
/// emoji are kept too.
/// </summary>
public static class HiddenText
{
    private const int BlackFlag = 0x1F3F4;
    private const int CancelTag = 0xE007F;

    public static string Strip(string? text, out int removed)
    {
        removed = 0;
        if (string.IsNullOrEmpty(text))
            return text ?? "";

        var runes = text.EnumerateRunes().ToArray();
        var keep = new bool[runes.Length];
        for (var i = 0; i < runes.Length; i++)
        {
            var value = runes[i].Value;
            if (value == BlackFlag)
            {
                keep[i] = true;
                var flagLength = SubdivisionFlagTagCount(runes, i + 1);
                for (var j = 1; j <= flagLength; j++)
                    keep[i + j] = true;
                i += flagLength;
                continue;
            }

            keep[i] = !IsTag(value) && !IsInvisibleControl(value);
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < runes.Length; i++)
        {
            if (keep[i])
                builder.Append(runes[i].ToString());
            else
                removed++;
        }

        return removed == 0 ? text : builder.ToString();
    }

    /// <summary>Tags (including the cancel tag) forming a standard flag right after 🏴, or 0 when they do not.</summary>
    private static int SubdivisionFlagTagCount(System.Text.Rune[] runes, int start)
    {
        // Only England, Scotland and Wales are recommended for general interchange. Accepting any
        // other letters would let a run of 🏴s carry a hidden message a few characters at a time.
        foreach (var code in StandardSubdivisionFlags)
        {
            if (start + code.Length >= runes.Length)
                continue;

            var matches = true;
            for (var i = 0; i < code.Length && matches; i++)
                matches = runes[start + i].Value == 0xE0000 + code[i];

            if (matches && runes[start + code.Length].Value == CancelTag)
                return code.Length + 1;
        }

        return 0;
    }

    private static readonly string[] StandardSubdivisionFlags = ["gbeng", "gbsct", "gbwls"];

    private static bool IsTag(int value) => value is >= 0xE0000 and <= 0xE007F;

    private static bool IsInvisibleControl(int value)
        => value is 0x200B or 0x2060 or 0xFEFF
            or (>= 0x202A and <= 0x202E)
            or (>= 0x2066 and <= 0x2069);
}
