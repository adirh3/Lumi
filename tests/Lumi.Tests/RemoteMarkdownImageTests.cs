using System.Text.Json;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services.Remote;
using Xunit;

namespace Lumi.Tests;

public sealed class RemoteMarkdownImageTests
{
    [Fact]
    public void ParserFindsAndRewritesOnlyTheRequestedImageTargets()
    {
        const string markdown =
            "Before ![one](https://example.com/a.png) and " +
            "![two](C:\\Images\\two.png) after.";
        var references = RemoteMarkdownImages.Find(markdown);

        Assert.Equal(2, references.Count);
        Assert.Equal("https://example.com/a.png", references[0].Target);
        Assert.Equal(@"C:\Images\two.png", references[1].Target);

        var rewritten = RemoteMarkdownImages.RewriteTargets(
            markdown,
            new Dictionary<int, string> { [1] = "/tmp/two.png" });
        Assert.Contains("![one](https://example.com/a.png)", rewritten);
        Assert.Contains("![two](/tmp/two.png)", rewritten);

        Assert.Equal(
            "Before one and two after.",
            RemoteMarkdownImages.ToSelectionText(markdown));
    }

    [Fact]
    public void LocalImageDescriptorsRequireAnAuthorizedArtifact()
    {
        var local = Path.Combine(Path.GetTempPath(), "lumi-inline-image.png");
        var authorizedPaths = RemoteMarkdownImageFiles.BuildAuthorizedPaths(
        [
            new ChatMessage
            {
                Role = "tool",
                ToolName = "announce_file",
                Content = $$"""{"filePath":{{JsonSerializer.Serialize(local)}}}"""
            }
        ]);
        var descriptors = RemoteMarkdownImageFiles.BuildDescriptors(
            $"![public](https://example.com/a.png)\n![local]({local})",
            authorizedPaths);

        Assert.Collection(
            descriptors!,
            image =>
            {
                Assert.Equal(0, image.Index);
                Assert.Equal("a.png", image.FileName);
            },
            image =>
            {
                Assert.Equal(1, image.Index);
                Assert.Equal(Path.GetFileName(local), image.FileName);
            });

        Assert.Null(RemoteMarkdownImageFiles.BuildDescriptors(
            $"![private]({Path.Combine(Path.GetTempPath(), "not-announced.png")})",
            authorizedPaths));
    }

    [Fact]
    public void TableImagesWithEncodedSpacesKeepTheirIndexesAndRequireAuthorizedFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "wallpapers with spaces", "portrait.png");
        var other = Path.Combine(Path.GetTempPath(), "wallpapers with spaces", "private.png");
        var authorizedPaths = RemoteMarkdownImageFiles.BuildAuthorizedPaths(
        [
            new ChatMessage
            {
                Role = "tool",
                ToolName = "announce_file",
                Content = JsonSerializer.Serialize(new { filePath = path })
            }
        ]);
        var markdown = $"| First | Second |\n|---|---|\n| ![Wallpaper]({path.Replace(" ", "%20")}) | ![Private]({other.Replace(" ", "%20")}) |";

        var image = Assert.Single(RemoteMarkdownImageFiles.BuildDescriptors(markdown, authorizedPaths)!);
        Assert.Equal(0, image.Index);
        Assert.Equal("portrait.png", image.FileName);
        Assert.True(RemoteMarkdownImageFiles.TryResolveReferencedPath(markdown, 0, authorizedPaths, out var resolved));
        Assert.Equal(path, resolved);
        Assert.False(RemoteMarkdownImageFiles.TryResolveReferencedPath(markdown, 1, authorizedPaths, out _));
    }

    [Fact]
    public void FileUrisAreDecodedOnlyOnceAndLiteralAuthorizedPathsTakePrecedence()
    {
        var literal = Path.Combine(Path.GetTempPath(), "wallpaper%20portrait.png");
        var decoded = Path.Combine(Path.GetTempPath(), "wallpaper portrait.png");
        var authorizedPaths = new HashSet<string> { literal, decoded };

        foreach (var target in new[] { literal, new Uri(literal).AbsoluteUri })
        {
            Assert.True(RemoteMarkdownImageFiles.TryResolveReferencedPath(
                $"![Wallpaper]({target})", 0, authorizedPaths, out var resolved));
            Assert.Equal(literal, resolved);
        }

        Assert.False(RemoteMarkdownImageFiles.TryResolveReferencedPath(
            $"![Wallpaper]({new Uri(literal).AbsoluteUri})", 0, new HashSet<string> { decoded }, out _));
    }

    [Theory]
    [InlineData("Completed", true)]
    [InlineData("Failed", false)]
    [InlineData("InProgress", false)]
    [InlineData("Stopped", false)]
    [InlineData(null, false)]
    public void ViewedInlineImagesRequireASuccessfulToolRead(string? status, bool authorized)
    {
        var path = Path.Combine(Path.GetTempPath(), "lumi-viewed-image.png");
        var paths = RemoteMarkdownImageFiles.BuildAuthorizedPaths(
        [
            new ChatMessage
            {
                Role = "tool", ToolName = "view", ToolStatus = status,
                Content = JsonSerializer.Serialize(new { path })
            }
        ]);
        var descriptors = RemoteMarkdownImageFiles.BuildDescriptors($"![Screenshot]({path})", paths);
        if (authorized)
            Assert.Equal("lumi-viewed-image.png", Assert.Single(descriptors!).FileName);
        else
            Assert.Null(descriptors);
    }

    [Fact]
    public void ImageReadsDoNotAuthorizeUserClaimsToolOutputOrOtherPaths()
    {
        var path = Path.Combine(Path.GetTempPath(), "lumi-viewed-image.png");
        var other = Path.Combine(Path.GetTempPath(), "other-image.png");
        var paths = RemoteMarkdownImageFiles.BuildAuthorizedPaths(
        [
            new ChatMessage
            {
                Role = "user", ToolName = "view", ToolStatus = "Completed",
                Content = JsonSerializer.Serialize(new { path })
            },
            new ChatMessage
            {
                Role = "tool", ToolName = "powershell", ToolStatus = "Completed",
                Content = JsonSerializer.Serialize(new { path })
            },
            new ChatMessage
            {
                Role = "tool", ToolName = "view", ToolStatus = "Completed",
                Content = JsonSerializer.Serialize(new { path = Path.Combine(Path.GetTempPath(), "notes.txt") })
            }
        ]);
        Assert.Null(RemoteMarkdownImageFiles.BuildDescriptors($"![Image]({path})", paths));

        paths = RemoteMarkdownImageFiles.BuildAuthorizedPaths(
        [
            new ChatMessage
            {
                Role = "tool", ToolName = "view", ToolStatus = "Completed",
                Content = JsonSerializer.Serialize(new { path })
            }
        ]);
        Assert.Null(RemoteMarkdownImageFiles.BuildDescriptors($"![Unrelated]({other})", paths));
    }

    [Fact]
    public void ImageScannerLeavesCodeAndEscapedLiteralsUntouched()
    {
        const string markdown = """
            Visible ![yes](C:\Images\yes.png)
            Inline `![inline](C:\Images\inline.png)`
            Escaped \![escaped](C:\Images\escaped.png)

            ```md
            ![fenced](C:\Images\fenced.png)
            ```

                ```md
                ![indented-fenced](C:\Images\indented.png)
                ```
            """;

        var reference = Assert.Single(RemoteMarkdownImages.Find(markdown));
        Assert.Equal(@"C:\Images\yes.png", reference.Target);

        var rewritten = RemoteMarkdownImages.RewriteTargets(
            markdown,
            new Dictionary<int, string> { [0] = "/cache/yes.png" });
        Assert.Contains("Visible ![yes](/cache/yes.png)", rewritten);
        Assert.Contains("`![inline](C:\\Images\\inline.png)`", rewritten);
        Assert.Contains("\\![escaped](C:\\Images\\escaped.png)", rewritten);
        Assert.Contains("![fenced](C:\\Images\\fenced.png)", rewritten);
        Assert.Contains(
            "![indented-fenced](C:\\Images\\indented.png)",
            rewritten);

        var selection = RemoteMarkdownImages.ToSelectionText(markdown);
        Assert.Contains("Visible yes", selection);
        Assert.Contains("`![inline](C:\\Images\\inline.png)`", selection);
        Assert.Contains("\\![escaped](C:\\Images\\escaped.png)", selection);
        Assert.Contains("![fenced](C:\\Images\\fenced.png)", selection);
        Assert.Contains(
            "![indented-fenced](C:\\Images\\indented.png)",
            selection);
    }
}
