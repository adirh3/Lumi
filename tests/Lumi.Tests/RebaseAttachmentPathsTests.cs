using GitHub.Copilot.SDK;
using Lumi.Models;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public class RebaseAttachmentPathsTests
{
    private static (List<UserMessageDataAttachmentsItemFile> attachments, ChatMessage msg) MakeAttachments(
        params string[] paths)
    {
        var attachments = paths.Select(p => new UserMessageDataAttachmentsItemFile
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

    [Fact]
    public void Rebases_paths_under_project_dir_to_worktree()
    {
        var (attachments, msg) = MakeAttachments(
            @"E:\Git\Lumi\src\Models\Models.cs",
            @"E:\Git\Lumi\src\ViewModels\ChatViewModel.cs");

        ChatViewModel.RebaseAttachmentPaths(
            attachments, msg,
            @"E:\Git\Lumi",
            @"E:\Git\Lumi-wt-abc123");

        Assert.Equal(@"E:\Git\Lumi-wt-abc123\src\Models\Models.cs", attachments[0].Path);
        Assert.Equal(@"E:\Git\Lumi-wt-abc123\src\ViewModels\ChatViewModel.cs", attachments[1].Path);
        Assert.Equal(@"E:\Git\Lumi-wt-abc123\src\Models\Models.cs", msg.Attachments[0]);
        Assert.Equal(@"E:\Git\Lumi-wt-abc123\src\ViewModels\ChatViewModel.cs", msg.Attachments[1]);
    }

    [Fact]
    public void Leaves_external_files_untouched()
    {
        var externalPath = @"C:\Users\adirh\Documents\notes.txt";
        var (attachments, msg) = MakeAttachments(
            @"E:\Git\Lumi\src\file.cs",
            externalPath);

        ChatViewModel.RebaseAttachmentPaths(
            attachments, msg,
            @"E:\Git\Lumi",
            @"E:\Git\Lumi-wt-abc123");

        Assert.Equal(@"E:\Git\Lumi-wt-abc123\src\file.cs", attachments[0].Path);
        Assert.Equal(externalPath, attachments[1].Path);
        Assert.Equal(externalPath, msg.Attachments[1]);
    }

    [Fact]
    public void Noop_when_project_dir_equals_worktree()
    {
        var original = @"E:\Git\Lumi\src\file.cs";
        var (attachments, msg) = MakeAttachments(original);

        ChatViewModel.RebaseAttachmentPaths(
            attachments, msg,
            @"E:\Git\Lumi",
            @"E:\Git\Lumi");

        Assert.Equal(original, attachments[0].Path);
        Assert.Equal(original, msg.Attachments[0]);
    }

    [Fact]
    public void Handles_trailing_separator_variations()
    {
        var (attachments, msg) = MakeAttachments(@"E:\Git\Lumi\src\file.cs");

        ChatViewModel.RebaseAttachmentPaths(
            attachments, msg,
            @"E:\Git\Lumi\",
            @"E:\Git\Lumi-wt-abc123");

        Assert.Equal(@"E:\Git\Lumi-wt-abc123\src\file.cs", attachments[0].Path);
    }

    [Fact]
    public void Case_insensitive_prefix_matching()
    {
        var (attachments, msg) = MakeAttachments(@"e:\git\lumi\src\file.cs");

        ChatViewModel.RebaseAttachmentPaths(
            attachments, msg,
            @"E:\Git\Lumi",
            @"E:\Git\Lumi-wt-abc123");

        Assert.Equal(@"E:\Git\Lumi-wt-abc123\src\file.cs", attachments[0].Path);
    }

    [Fact]
    public void Empty_attachments_is_noop()
    {
        var attachments = new List<UserMessageDataAttachmentsItemFile>();
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
        var (attachments, msg) = MakeAttachments(@"E:\Git\Lumi\src\file.cs");

        ChatViewModel.RebaseAttachmentPaths(
            attachments, msg,
            @"E:\Git\Lumi",
            @"E:\Git\Lumi-wt-abc123");

        Assert.Equal("file.cs", attachments[0].DisplayName);
    }
}
