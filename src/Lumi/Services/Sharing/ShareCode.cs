using System.Buffers;
using System.Collections.Generic;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Lumi.Services.Sharing;

public enum ShareCodeStatus
{
    /// <summary>The text holds no Lumi code.</summary>
    NotFound,

    /// <summary>A complete code was found and decoded.</summary>
    Decoded,

    /// <summary>A code was found but it is cut short or altered (the checksum does not match).</summary>
    Damaged,

    /// <summary>A code from a newer Lumi, in a format this version does not know.</summary>
    NewerVersion
}

public readonly record struct ShareCodeResult(ShareCodeStatus Status, string? Text = null);

/// <summary>
/// The chat-proof form of a capability: <c>lumi1.</c> followed by base64url of a 4-byte SHA-256
/// check and the Brotli-compressed pack text (a SKILL.md or a compact Lumi pack). One unbroken line
/// of URL-safe characters survives chat apps that reformat markdown, is about half the size of the
/// text, and cannot be half-imported: a code that was cut short or changed fails its check and is
/// reported as damaged. Decoding tolerates what chats do to long lines — wrapping, surrounding
/// words, code fences, and invisible characters inserted to allow line breaks.
/// </summary>
public static partial class ShareCode
{
    public const int FormatVersion = 1;
    public const string Prefix = "lumi1.";

    /// <summary>Beyond this many characters some chat apps (Teams among them) may refuse the message.</summary>
    public const int LongCodeThreshold = 12_000;

    private const int CheckLength = 4;

    /// <summary>The shortest body a real code can have; "lumi1." in a sentence is not a code.</summary>
    private const int MinBodyLength = 16;

    /// <summary>How many codes in one text are tried; a message carries one.</summary>
    private const int MaxAttempts = 3;
    private const int BrotliQuality = 11;
    private const int BrotliWindow = 22;

    /// <summary>A decoded pack is plain text a person wrote; anything bigger is refused before it is inflated.</summary>
    private const int MaxDecodedBytes = CapabilityPackReader.MaxTextLength * 3;

    [GeneratedRegex(@"(?<![A-Za-z0-9])lumi(\d{1,3})\.", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();

    public static string Encode(string packText)
    {
        ArgumentNullException.ThrowIfNull(packText);

        var bytes = Encoding.UTF8.GetBytes(PackText.NormalizeNewlines(packText));
        var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(bytes.Length)];
        if (!BrotliEncoder.TryCompress(bytes, compressed, out var written, BrotliQuality, BrotliWindow))
            throw new InvalidOperationException("Could not compress the capability.");

        var payload = new byte[CheckLength + written];
        SHA256.HashData(bytes).AsSpan(0, CheckLength).CopyTo(payload);
        compressed.AsSpan(0, written).CopyTo(payload.AsSpan(CheckLength));
        return Prefix + Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>True when <paramref name="text"/> holds something shaped like a Lumi code (valid or not).</summary>
    public static bool Contains(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        var cleaned = StripInvisible(text);
        if (!cleaned.Contains("lumi", StringComparison.Ordinal))
            return false;

        foreach (Match match in PrefixPattern().Matches(cleaned))
        {
            if (RunLength(cleaned, match.Index + match.Length) >= MinBodyLength)
                return true;
        }

        return false;
    }

    public static ShareCodeResult Decode(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return new ShareCodeResult(ShareCodeStatus.NotFound);

        var cleaned = StripInvisible(text);
        if (!cleaned.Contains("lumi", StringComparison.Ordinal))
            return new ShareCodeResult(ShareCodeStatus.NotFound);

        // The message around a code can mention "lumi2.0" or quote an older code; a complete code of
        // this version wins. Only the first few candidates are inflated, each at most once, so
        // clipboard text cannot make decoding expensive.
        var sawDamaged = false;
        var sawNewer = false;
        var attempts = 0;
        foreach (Match match in PrefixPattern().Matches(cleaned))
        {
            var start = match.Index + match.Length;
            if (RunLength(cleaned, start) < MinBodyLength || !int.TryParse(match.Groups[1].ValueSpan, out var version))
                continue;

            if (version != FormatVersion)
            {
                sawNewer |= version > FormatVersion;
                continue;
            }

            if (++attempts > MaxAttempts)
                break;

            var result = DecodeBody(ReadCode(cleaned, start));
            if (result.Status == ShareCodeStatus.Decoded)
                return result;

            sawDamaged = true;
        }

        return new ShareCodeResult(
            sawDamaged ? ShareCodeStatus.Damaged
            : sawNewer ? ShareCodeStatus.NewerVersion
            : ShareCodeStatus.NotFound);
    }

    /// <summary>
    /// Inflates the body once. The compressed stream marks its own end, so characters a chat joined
    /// onto the code (a wrapped word on the next line, say) are simply never read, and a stream that
    /// is cut short, corrupt or larger than a pack can be is damaged.
    /// </summary>
    private static ShareCodeResult DecodeBody(string body)
    {
        if (!TryFromBase64Url(body, out var payload) || payload.Length <= CheckLength)
            return new ShareCodeResult(ShareCodeStatus.Damaged);

        var decoder = new BrotliDecoder();
        try
        {
            ReadOnlySpan<byte> source = payload.AsSpan(CheckLength);
            var output = new byte[(int)Math.Min(MaxDecodedBytes, Math.Max(4096L, source.Length * 8L))];
            var written = 0;
            while (true)
            {
                var status = decoder.Decompress(source, output.AsSpan(written), out var consumed, out var produced);
                source = source[consumed..];
                written += produced;
                if (status == OperationStatus.Done)
                    break;
                if (status != OperationStatus.DestinationTooSmall || output.Length >= MaxDecodedBytes)
                    return new ShareCodeResult(ShareCodeStatus.Damaged);

                Array.Resize(ref output, (int)Math.Min(MaxDecodedBytes, output.Length * 2L));
            }

            var decompressed = output.AsSpan(0, written);
            if (!SHA256.HashData(decompressed).AsSpan(0, CheckLength).SequenceEqual(payload.AsSpan(0, CheckLength)))
                return new ShareCodeResult(ShareCodeStatus.Damaged);

            return new ShareCodeResult(
                ShareCodeStatus.Decoded,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(decompressed));
        }
        catch (DecoderFallbackException)
        {
            return new ShareCodeResult(ShareCodeStatus.Damaged);
        }
        finally
        {
            decoder.Dispose();
        }
    }

    /// <summary>The code's first line, then every following line that holds nothing but code characters.</summary>
    private static string ReadCode(string text, int start)
    {
        var code = new StringBuilder();
        var index = start;
        while (index < text.Length && IsCodeChar(text[index]))
            code.Append(text[index++]);

        while (index < text.Length)
        {
            var lineEnd = text.IndexOf('\n', index);
            if (lineEnd < 0 || !IsBlank(text.AsSpan(index, lineEnd - index)))
                break;

            var nextStart = lineEnd + 1;
            var nextEnd = text.IndexOf('\n', nextStart);
            var nextLine = text.AsSpan(nextStart, (nextEnd < 0 ? text.Length : nextEnd) - nextStart).Trim();
            if (nextLine.IsEmpty || !IsCodeRun(nextLine))
                break;

            code.Append(nextLine);
            index = nextEnd < 0 ? text.Length : nextEnd;
        }

        return code.ToString();
    }

    private static bool IsBlank(ReadOnlySpan<char> text) => text.Trim().IsEmpty;

    private static int RunLength(string text, int index)
    {
        var start = index;
        while (index < text.Length && IsCodeChar(text[index]))
            index++;
        return index - start;
    }

    private static bool IsCodeRun(ReadOnlySpan<char> line)
    {
        foreach (var ch in line)
        {
            if (!IsCodeChar(ch))
                return false;
        }

        return true;
    }

    private static bool IsCodeChar(char ch) => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_';

    /// <summary>
    /// Base64url without padding, decoded leniently on purpose: the last group may be followed by
    /// characters a chat joined on, so its unused bits need not be zero, and a lone trailing
    /// character (which cannot carry a whole byte) is ignored.
    /// </summary>
    private static bool TryFromBase64Url(string body, out byte[] bytes)
    {
        var usable = body.Length % 4 == 1 ? body.Length - 1 : body.Length;
        bytes = new byte[usable * 6 / 8];
        var bits = 0;
        var bitCount = 0;
        var written = 0;
        for (var i = 0; i < usable; i++)
        {
            var value = Base64UrlValue(body[i]);
            if (value < 0)
            {
                bytes = [];
                return false;
            }

            bits = (bits << 6) | value;
            bitCount += 6;
            if (bitCount >= 8)
            {
                bitCount -= 8;
                bytes[written++] = (byte)(bits >> bitCount);
                bits &= (1 << bitCount) - 1;
            }
        }

        return true;
    }

    private static int Base64UrlValue(char ch) => ch switch
    {
        >= 'A' and <= 'Z' => ch - 'A',
        >= 'a' and <= 'z' => ch - 'a' + 26,
        >= '0' and <= '9' => ch - '0' + 52,
        '-' => 62,
        '_' => 63,
        _ => -1
    };

    /// <summary>Chats sometimes insert zero-width spaces or soft hyphens so long lines can wrap.</summary>
    private static string StripInvisible(string text)
    {
        if (text.IndexOfAny(['\u200B', '\u200C', '\u200D', '\u2060', '\uFEFF', '\u00AD']) < 0)
            return PackText.NormalizeNewlines(text);

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is not ('\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF' or '\u00AD'))
                builder.Append(ch);
        }

        return PackText.NormalizeNewlines(builder.ToString());
    }
}
