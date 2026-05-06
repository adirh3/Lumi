using System;
using System.Collections.Generic;
using System.IO;
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

        return SingleAttachmentPreparationResult.Ok(new UserMessageAttachmentFile
        {
            Path = fullPath,
            DisplayName = Path.GetFileName(fullPath)
        });
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
