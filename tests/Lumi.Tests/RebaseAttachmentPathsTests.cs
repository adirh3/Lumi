using GitHub.Copilot;
using Lumi.Models;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public class RebaseAttachmentPathsTests
{
    private static (List<Attachment> attachments, ChatMessage msg) MakeAttachments(
        params string[] paths)
    {
        var attachments = paths.Select(p => (Attachment)new AttachmentFile
        {
            Path = p,
            DisplayName = Path.GetFileName(p)
        }).ToList();

        var msg = new ChatMessage
        {
            Role = "user",
            Content = "test",
            Attachments = paths.ToList()
        };

        return (attachments, msg);
    }

    private static AttachmentFile FileAt(List<Attachment> attachments, int index)
        => Assert.IsType<AttachmentFile>(attachments[index]);

    // The path literals below were written for Windows. Convert them to the host OS format so the
    // same assertions hold on Windows, macOS, and Linux. Windows: unchanged (byte-for-byte). Unix:
    // drop a leading drive letter and flip '\' to '/'. The rebase logic itself is OS-agnostic
    // (it keys off Path.DirectorySeparatorChar), so exercising it with native paths is the correct test.
    private static string P(string windowsPath)
    {
        if (OperatingSystem.IsWindows()) return windowsPath;
        var s = windowsPath;
        if (s.Length >= 2 && char.IsLetter(s[0]) && s[1] == ':') s = s[2..];
        return s.Replace('\\', '/');
    }

    [Fact]
    public void Rebases_paths_under_project_dir_to_worktree()
    {
        var (attachments, msg) = MakeAttachments(
            P(@"E:\Git\Lumi\src\Models\Models.cs"),
            P(@"E:\Git\Lumi\src\ViewModels\ChatViewModel.cs"));

        ChatViewModel.RebaseAttachmentPaths(
            attachments, msg,
            P(@"E:\Git\Lumi"),
            P(@"E:\Git\Lumi-wt-abc123"));

        Assert.Equal(P(@"E:\Git\Lumi-wt-abc123\src\Models\Models.cs"), FileAt(attachments, 0).Path);
        Assert.Equal(P(@"E:\Git\Lumi-wt-abc123\src\ViewModels\ChatViewModel.cs"), FileAt(attachments, 1).Path);
        Assert.Equal(P(@"E:\Git\Lumi-wt-abc123\src\Models\Models.cs"), msg.Attachments[0]);
        Assert.Equal(P(@"E:\Git\Lumi-wt-abc123\src\ViewModels\ChatViewModel.cs"), msg.Attachments[1]);
    }

    [Fact]
    public void Leaves_external_files_untouched()
    {
        var externalPath = P(@"C:\Users\adirh\Documents\notes.txt");
        var (attachments, msg) = MakeAttachments(
            P(@"E:\Git\Lumi\src\file.cs"),
            externalPath);

        ChatViewModel.RebaseAttachmentPaths(
            attachments, msg,
            P(@"E:\Git\Lumi"),
            P(@"E:\Git\Lumi-wt-abc123"));

        Assert.Equal(P(@"E:\Git\Lumi-wt-abc123\src\file.cs"), FileAt(attachments, 0).Path);
        Assert.Equal(externalPath, FileAt(attachments, 1).Path);
        Assert.Equal(externalPath, msg.Attachments[1]);
    }

    [Fact]
    public void Noop_when_project_dir_equals_worktree()
    {
        var original = P(@"E:\Git\Lumi\src\file.cs");
        var (attachments, msg) = MakeAttachments(original);

        ChatViewModel.RebaseAttachmentPaths(
            attachments, msg,
            P(@"E:\Git\Lumi"),
            P(@"E:\Git\Lumi"));

        Assert.Equal(original, FileAt(attachments, 0).Path);
        Assert.Equal(original, msg.Attachments[0]);
    }

    [Fact]
    public void Handles_trailing_separator_variations()
    {
        var (attachments, msg) = MakeAttachments(P(@"E:\Git\Lumi\src\file.cs"));

        ChatViewModel.RebaseAttachmentPaths(
            attachments, msg,
            P(@"E:\Git\Lumi\"),
            P(@"E:\Git\Lumi-wt-abc123"));

        Assert.Equal(P(@"E:\Git\Lumi-wt-abc123\src\file.cs"), FileAt(attachments, 0).Path);
    }

    [Fact]
    public void Case_insensitive_prefix_matching()
    {
        var (attachments, msg) = MakeAttachments(P(@"e:\git\lumi\src\file.cs"));

        ChatViewModel.RebaseAttachmentPaths(
            attachments, msg,
            P(@"E:\Git\Lumi"),
            P(@"E:\Git\Lumi-wt-abc123"));

        Assert.Equal(P(@"E:\Git\Lumi-wt-abc123\src\file.cs"), FileAt(attachments, 0).Path);
    }

    [Fact]
    public void Empty_attachments_is_noop()
    {
        var attachments = new List<Attachment>();
        var msg = new ChatMessage { Role = "user", Content = "test", Attachments = [] };

        ChatViewModel.RebaseAttachmentPaths(
            attachments, msg,
            @"E:\Git\Lumi",
            @"E:\Git\Lumi-wt-abc123");

        Assert.Empty(attachments);
    }

    [Fact]
    public void DisplayName_is_preserved()
    {
        var (attachments, msg) = MakeAttachments(P(@"E:\Git\Lumi\src\file.cs"));

        ChatViewModel.RebaseAttachmentPaths(
            attachments, msg,
            P(@"E:\Git\Lumi"),
            P(@"E:\Git\Lumi-wt-abc123"));

        Assert.Equal("file.cs", FileAt(attachments, 0).DisplayName);
    }
}
