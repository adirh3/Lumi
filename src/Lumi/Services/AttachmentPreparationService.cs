using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using GitHub.Copilot.SDK;

namespace Lumi.Services;

internal sealed record AttachmentPreparationResult(
    List<UserMessageAttachment> Attachments,
    string? ErrorMessage)
{
    public bool Success => ErrorMessage is null;
}

internal static class AttachmentPreparationService
{
    private const string DocxExtension = ".docx";
    private static readonly byte[] OleCompoundFileMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public static AttachmentPreparationResult PrepareForCopilot(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var attachments = new List<UserMessageAttachment>();
        foreach (var path in paths.Where(static p => !string.IsNullOrWhiteSpace(p)))
        {
            var result = PrepareOne(path);
            if (!result.Success)
                return new AttachmentPreparationResult([], result.ErrorMessage);

            attachments.Add(result.Attachment);
        }

        return new AttachmentPreparationResult(attachments, null);
    }

    public static string? ValidatePendingPath(string path)
        => PrepareOne(path).ErrorMessage;

    private static SingleAttachmentPreparationResult PrepareOne(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return SingleAttachmentPreparationResult.Fail($"Invalid attachment path \"{path}\": {ex.Message}");
        }

        if (Directory.Exists(fullPath))
        {
            return SingleAttachmentPreparationResult.Ok(new UserMessageAttachmentDirectory
            {
                Path = fullPath,
                DisplayName = new DirectoryInfo(fullPath).Name
            });
        }

        if (!File.Exists(fullPath))
            return SingleAttachmentPreparationResult.Fail($"Attachment not found: {path}");

        var docxValidationError = ValidateDocxIfNeeded(fullPath);
        if (docxValidationError is not null)
            return SingleAttachmentPreparationResult.Fail(docxValidationError);

        return SingleAttachmentPreparationResult.Ok(new UserMessageAttachmentFile
        {
            Path = fullPath,
            DisplayName = Path.GetFileName(fullPath)
        });
    }

    private static string? ValidateDocxIfNeeded(string path)
    {
        if (!string.Equals(Path.GetExtension(path), DocxExtension, StringComparison.OrdinalIgnoreCase))
            return null;

        byte[] header;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            header = new byte[Math.Min(8, (int)Math.Min(stream.Length, 8))];
            _ = stream.Read(header, 0, header.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not read attachment \"{Path.GetFileName(path)}\": {ex.Message}";
        }

        if (header.AsSpan().StartsWith(OleCompoundFileMagic))
        {
            return $"\"{Path.GetFileName(path)}\" has a .docx extension, but appears to be an older or encrypted Word/OLE document. Re-save it as a modern .docx file and attach it again.";
        }

        if (!LooksLikeZip(header))
        {
            return $"\"{Path.GetFileName(path)}\" has a .docx extension, but is not a valid modern Word document.";
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var hasContentTypes = archive.GetEntry("[Content_Types].xml") is not null;
            var hasMainDocument = archive.GetEntry("word/document.xml") is not null;
            if (!hasContentTypes || !hasMainDocument)
                return $"\"{Path.GetFileName(path)}\" has a .docx extension, but is missing required Word document parts.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return $"Could not read \"{Path.GetFileName(path)}\" as a modern Word document: {ex.Message}";
        }

        return null;
    }

    private static bool LooksLikeZip(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
            return false;

        return header[0] == 0x50
               && header[1] == 0x4B
               && ((header[2] == 0x03 && header[3] == 0x04)
                   || (header[2] == 0x05 && header[3] == 0x06)
                   || (header[2] == 0x07 && header[3] == 0x08));
    }

    private sealed record SingleAttachmentPreparationResult(
        UserMessageAttachment Attachment,
        string? ErrorMessage)
    {
        public bool Success => ErrorMessage is null;

        public static SingleAttachmentPreparationResult Ok(UserMessageAttachment attachment)
            => new(attachment, null);

        public static SingleAttachmentPreparationResult Fail(string errorMessage)
            => new(new UserMessageAttachment(), errorMessage);
    }
}
