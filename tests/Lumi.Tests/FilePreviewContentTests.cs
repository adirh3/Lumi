using System.Text;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class FilePreviewContentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumi-preview-tests-" + Guid.NewGuid().ToString("N"));

    public FilePreviewContentTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("notes.md", "# Notes", "Markdown")]
    [InlineData("code.cs", "class Example {}", "Text")]
    [InlineData("page.html", "<script>example()</script>", "Text")]
    [InlineData("Dockerfile", "FROM scratch", "Text")]
    [InlineData("unknown.source", "some readable text", "Text")]
    [InlineData("empty.txt", "", "Text")]
    public async Task TextFilesAreRenderedNatively(string name, string text, string expected)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, text);
        var content = await FilePreviewContent.LoadAsync(path, CancellationToken.None);
        Assert.Equal(expected, content.Kind.ToString());
        Assert.Equal(text, content.Text);
        Assert.False(content.IsTruncated);
    }

    [Fact]
    public async Task UnicodeBomIsRespected()
    {
        var path = Path.Combine(_root, "unicode.txt");
        await File.WriteAllTextAsync(path, "Hello \u05e9\u05dc\u05d5\u05dd", Encoding.Unicode);
        var content = await FilePreviewContent.LoadAsync(path, CancellationToken.None);
        Assert.Equal(FilePreviewKind.Text, content.Kind);
        Assert.Equal("Hello \u05e9\u05dc\u05d5\u05dd", content.Text);
    }

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("slides.pptx")]
    [InlineData("document.docx")]
    [InlineData("sheet.xlsx")]
    public async Task DocumentsUseNativeHandler(string name)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, "native document fixture");
        Assert.Equal(FilePreviewKind.Native, (await FilePreviewContent.LoadAsync(path, CancellationToken.None)).Kind);
    }

    [Fact]
    public async Task BinaryUnknownFileIsNotDisplayedAsText()
    {
        var path = Path.Combine(_root, "unknown");
        await File.WriteAllBytesAsync(path, [0, 1, 2, 255]);
        Assert.Equal(FilePreviewKind.Native, (await FilePreviewContent.LoadAsync(path, CancellationToken.None)).Kind);
    }

    [Fact]
    public async Task LargeFileIsBoundedAndHandleReleased()
    {
        var path = Path.Combine(_root, "large.txt");
        await File.WriteAllTextAsync(path, new string('a', FilePreviewContent.MaxCharacters + 20));
        var content = await FilePreviewContent.LoadAsync(path, CancellationToken.None);
        Assert.True(content.IsTruncated);
        Assert.Equal(FilePreviewContent.MaxCharacters, content.Text!.Length);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public async Task MissingFileAndCancellationAreNotSuccessShaped()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            FilePreviewContent.LoadAsync(Path.Combine(_root, "missing.txt"), CancellationToken.None));
        var path = Path.Combine(_root, "cancel.txt");
        await File.WriteAllTextAsync(path, "text");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FilePreviewContent.LoadAsync(path, cts.Token));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
