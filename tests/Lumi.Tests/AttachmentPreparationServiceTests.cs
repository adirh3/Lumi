using System.IO.Compression;
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
        CreateMinimalDocx(path);

        var result = AttachmentPreparationService.PrepareForCopilot([path]);

        Assert.True(result.Success);
        var file = Assert.IsType<UserMessageAttachmentFile>(Assert.Single(result.Attachments));
        Assert.Equal(Path.GetFullPath(path), file.Path);
        Assert.Equal("contract.docx", file.DisplayName);
    }

    [Fact]
    public void PrepareForCopilot_DocxExtensionOnOleFile_ReturnsActionableError()
    {
        var path = Path.Combine(_tempDir, "legacy-renamed.docx");
        File.WriteAllBytes(path, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00]);

        var result = AttachmentPreparationService.PrepareForCopilot([path]);

        Assert.False(result.Success);
        Assert.Empty(result.Attachments);
        Assert.Contains("older or encrypted Word/OLE document", result.ErrorMessage);
        Assert.Contains("Re-save it as a modern .docx", result.ErrorMessage);
    }

    [Fact]
    public void PrepareForCopilot_InvalidDocxZip_ReturnsClearError()
    {
        var path = Path.Combine(_tempDir, "not-word.docx");
        File.WriteAllText(path, "not a zip file");

        var result = AttachmentPreparationService.PrepareForCopilot([path]);

        Assert.False(result.Success);
        Assert.Empty(result.Attachments);
        Assert.Contains("not a valid modern Word document", result.ErrorMessage);
    }

    [Fact]
    public void PrepareForCopilot_ZipMissingWordParts_ReturnsClearError()
    {
        var path = Path.Combine(_tempDir, "plain-zip.docx");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("readme.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("hello");
        }

        var result = AttachmentPreparationService.PrepareForCopilot([path]);

        Assert.False(result.Success);
        Assert.Empty(result.Attachments);
        Assert.Contains("missing required Word document parts", result.ErrorMessage);
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

    private static void CreateMinimalDocx(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
            </Types>
            """);
        WriteEntry(archive, "word/document.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body><w:p><w:r><w:t>Hello from DOCX</w:t></w:r></w:p></w:body>
            </w:document>
            """);
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
