using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lumi.Services;

internal enum FilePreviewKind { Text, Markdown, Image, Native }

internal sealed record FilePreviewContent(FilePreviewKind Kind, string? Text = null, bool IsTruncated = false)
{
    internal const int MaxCharacters = 100_000;

    internal static async Task<FilePreviewContent> LoadAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp")
            return new(FilePreviewKind.Image);
        if (extension is ".pdf" or ".doc" or ".docx" or ".docm" or ".xls" or ".xlsx" or ".xlsm"
            or ".ppt" or ".pptx" or ".pptm" or ".rtf" or ".odt" or ".ods" or ".odp"
            or ".zip" or ".7z" or ".rar" or ".exe" or ".dll"
            or ".mp4" or ".mp3" or ".wav" or ".mov" or ".avi")
            return new(FilePreviewKind.Native);

        // Content detection also covers extensionless files and uncommon source-code extensions.
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
                        return new(FilePreviewKind.Native);
                }
                length += read;
            }
        }
        catch (DecoderFallbackException)
        {
            return new(FilePreviewKind.Native);
        }

        var count = Math.Min(length, MaxCharacters);
        if (count > 0 && char.IsHighSurrogate(buffer[count - 1]))
            count--;
        return new(
            extension is ".md" or ".markdown" or ".mdown" ? FilePreviewKind.Markdown : FilePreviewKind.Text,
            new string(buffer, 0, count),
            length > MaxCharacters);
    }
}
