using System.Text;

namespace Lumi.Remote.Protocol;

/// <summary>
/// The text of a file Lumi made, as the desktop and the phone both preview it: the first
/// <see cref="MaxCharacters"/> of it, refused when the file is not text.
/// </summary>
public sealed record FilePreviewText(string Text, bool IsTruncated)
{
    /// <summary>Enough for any report or source file, and bounded so a phone can show it.</summary>
    public const int MaxCharacters = 100_000;

    /// <summary>Reads a file's text. Returns null when the file is binary.</summary>
    public static async Task<FilePreviewText?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads up to <see cref="MaxCharacters"/> of a stream's text. Returns null when it is binary: a control
    /// character a text file would not hold, or bytes that are not valid UTF-8.
    /// </summary>
    public static async Task<FilePreviewText?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true, bufferSize: -1, leaveOpen: true);
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
}
