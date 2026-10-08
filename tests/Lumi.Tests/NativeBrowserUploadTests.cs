using System;
using System.IO;
using System.Linq;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class NativeBrowserUploadTests
{
    [Fact]
    public void CommasRemainPartOfFileNamesAndNewlinesSeparatePlainPaths()
    {
        Assert.Equal(new[] { "/tmp/name,with,commas.txt" },
            NativeBrowserUpload.ParsePaths("/tmp/name,with,commas.txt"));
        Assert.Equal(new[] { "/tmp/a", "/tmp/b" }, NativeBrowserUpload.ParsePaths("/tmp/a\r\n/tmp/b"));
        Assert.Equal(new[] { "/tmp/a\nb", "/tmp/c" },
            NativeBrowserUpload.ParsePaths("[\"/tmp/a\\nb\",\"/tmp/c\"]"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[true]")]
    [InlineData("[null]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"\\ud800\"]")]
    [InlineData("[not-json]")]
    public void InvalidInputsFailExplicitly(string value) =>
        Assert.Throws<ArgumentException>(() => NativeBrowserUpload.ParsePaths(value));

    [Fact]
    public void PolicyRejectsRelativeMissingAndTooManyFilesBeforeReadingContent()
    {
        Assert.Throws<ArgumentException>(() => NativeBrowserUpload.ValidatePaths(["relative.txt"]));
        Assert.Throws<FileNotFoundException>(() => NativeBrowserUpload.ValidatePaths(
            [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".missing")]));
        Assert.Throws<ArgumentException>(() => NativeBrowserUpload.ValidatePaths([]));
        Assert.Throws<ArgumentException>(() => NativeBrowserUpload.ValidatePaths(
            Enumerable.Repeat(Path.GetTempPath(), NativeBrowserUpload.MaxFiles + 1).ToArray()));
    }

    [Fact]
    public void SizeLimitsMatchWindowsAtTheirExactBoundaries()
    {
        NativeBrowserUpload.ValidateSizes([NativeBrowserUpload.MaxFileBytes]);
        NativeBrowserUpload.ValidateSizes([100L * 1024 * 1024, 100L * 1024 * 1024, 50L * 1024 * 1024]);
        Assert.Throws<ArgumentException>(() =>
            NativeBrowserUpload.ValidateSizes([NativeBrowserUpload.MaxFileBytes + 1]));
        Assert.Throws<ArgumentException>(() =>
            NativeBrowserUpload.ValidateSizes([100L * 1024 * 1024, 100L * 1024 * 1024, 50L * 1024 * 1024 + 1]));
    }

    [Fact]
    public void FileMetadataIncludesEmptyFilesAndCanonicalNames()
    {
        var path = Path.Combine(Path.GetTempPath(), "lumi-upload-" + Guid.NewGuid().ToString("N") + ",empty.txt");
        try
        {
            File.WriteAllBytes(path, []);
            var file = Assert.Single(NativeBrowserUpload.ValidatePaths([path]));
            Assert.Equal(Path.GetFullPath(path), file.Path);
            Assert.Equal(Path.GetFileName(path), file.Name);
            Assert.Equal(0, file.Length);
            Assert.Equal("text/plain", file.MimeType);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StreamingScriptsPinTheOriginalInputAndVerifyItsFiles()
    {
        var begin = NativeBrowserUpload.Begin("fixture", "123000001", 2);
        var commit = NativeBrowserUpload.Commit("fixture");
        Assert.Contains("lumi.browser.elements.v1", begin);
        Assert.Contains("Stale or unknown", begin);
        Assert.Contains("input,entries", begin);
        Assert.Contains("s.input.isConnected", commit);
        Assert.Contains("f.size!==s.entries[i].buffer.length", commit);
        Assert.DoesNotContain("querySelector", commit);
    }
}
