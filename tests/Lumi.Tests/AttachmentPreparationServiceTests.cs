using GitHub.Copilot.SDK;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class AttachmentPreparationServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"lumi-attachments-{Guid.NewGuid():N}");

    public AttachmentPreparationServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void PrepareForCopilot_ValidDocx_UsesSdkFileAttachment()
    {
        var path = Path.Combine(_tempDir, "contract.docx");
        File.WriteAllText(path, "docx bytes are interpreted by the SDK");

        var result = AttachmentPreparationService.PrepareForCopilot([path]);

        Assert.True(result.Success);
        var file = Assert.IsType<UserMessageAttachmentFile>(Assert.Single(result.Attachments));
        Assert.Equal(Path.GetFullPath(path), file.Path);
        Assert.Equal("contract.docx", file.DisplayName);
    }

    [Fact]
    public void PrepareForCopilot_DocxExtensionOnOleFile_UsesSdkFileAttachment()
    {
        var path = Path.Combine(_tempDir, "legacy-renamed.docx");
        File.WriteAllBytes(path, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00]);

        var result = AttachmentPreparationService.PrepareForCopilot([path]);

        Assert.True(result.Success);
        var file = Assert.IsType<UserMessageAttachmentFile>(Assert.Single(result.Attachments));
        Assert.Equal(Path.GetFullPath(path), file.Path);
        Assert.Equal("legacy-renamed.docx", file.DisplayName);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void PrepareForCopilot_InvalidDocxContent_UsesSdkFileAttachment()
    {
        var path = Path.Combine(_tempDir, "not-word.docx");
        File.WriteAllText(path, "not a zip file");

        var result = AttachmentPreparationService.PrepareForCopilot([path]);

        Assert.True(result.Success);
        var file = Assert.IsType<UserMessageAttachmentFile>(Assert.Single(result.Attachments));
        Assert.Equal(Path.GetFullPath(path), file.Path);
        Assert.Equal("not-word.docx", file.DisplayName);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void PrepareForCopilot_Directory_UsesSdkDirectoryAttachment()
    {
        var path = Path.Combine(_tempDir, "folder");
        Directory.CreateDirectory(path);

        var result = AttachmentPreparationService.PrepareForCopilot([path]);

        Assert.True(result.Success);
        var directory = Assert.IsType<UserMessageAttachmentDirectory>(Assert.Single(result.Attachments));
        Assert.Equal(Path.GetFullPath(path), directory.Path);
        Assert.Equal("folder", directory.DisplayName);
    }

    [Fact]
    public void PrepareForCopilot_MissingPath_ReturnsError()
    {
        var path = Path.Combine(_tempDir, "missing.docx");

        var result = AttachmentPreparationService.PrepareForCopilot([path]);

        Assert.False(result.Success);
        Assert.Empty(result.Attachments);
        Assert.Contains("Attachment not found", result.ErrorMessage);
    }
}
