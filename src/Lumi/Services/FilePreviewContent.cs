using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Remote.Protocol;

namespace Lumi.Services;

internal enum FilePreviewKind { Text, Markdown, Image, Native, Csv }

internal sealed record FilePreviewContent(FilePreviewKind Kind, string? Text = null, bool IsTruncated = false,
    CsvPreviewData? Csv = null)
{
    internal const int MaxCharacters = FilePreviewText.MaxCharacters;

    internal static bool IsHtmlFile(string filePath)
        => Path.GetExtension(filePath).ToLowerInvariant() is ".html" or ".htm";

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
        if (await FilePreviewText.ReadAsync(stream, cancellationToken).ConfigureAwait(false) is not { } content)
            return new(FilePreviewKind.Native);

        var text = content.Text;
        var isTruncated = content.IsTruncated;
        if (extension is ".csv" or ".tsv")
        {
            var csv = await Task.Run(() => CsvPreviewData.Parse(text, isTruncated,
                extension == ".tsv" ? '\t' : null, cancellationToken), cancellationToken).ConfigureAwait(false);
            return new(FilePreviewKind.Csv, IsTruncated: isTruncated, Csv: csv);
        }
        return new(
            extension is ".md" or ".markdown" or ".mdown" ? FilePreviewKind.Markdown : FilePreviewKind.Text,
            text, isTruncated);
    }
}
