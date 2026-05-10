using GitHub.Copilot.SDK;
using Lumi.Models;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public class RebaseAttachmentPathsTests
{
    private static (List<UserMessageAttachment> attachments, ChatMessage msg) MakeAttachments(
        params string[] paths)
    {
        var attachments = paths.Select(p => (UserMessageAttachment)new UserMessageAttachmentFile
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

    private static UserMessageAttachmentFile FileAt(List<UserMessageAttachment> attachments, int index)
        => Assert.IsType<UserMessageAttachmentFile>(attachments[index]);

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

        Assert.Equal(@"E:\Git\Lumi-wt-abc123\src\Models\Models.cs", FileAt(attachments, 0).Path);
        Assert.Equal(@"E:\Git\Lumi-wt-abc123\src\ViewModels\ChatViewModel.cs", FileAt(attachments, 1).Path);
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

        Assert.Equal(@"E:\Git\Lumi-wt-abc123\src\file.cs", FileAt(attachments, 0).Path);
        Assert.Equal(externalPath, FileAt(attachments, 1).Path);
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

        Assert.Equal(original, FileAt(attachments, 0).Path);
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

        Assert.Equal(@"E:\Git\Lumi-wt-abc123\src\file.cs", FileAt(attachments, 0).Path);
    }

    [Fact]
    public void Case_insensitive_prefix_matching()
    {
        var (attachments, msg) = MakeAttachments(@"e:\git\lumi\src\file.cs");

        ChatViewModel.RebaseAttachmentPaths(
            attachments, msg,
            @"E:\Git\Lumi",
            @"E:\Git\Lumi-wt-abc123");

        Assert.Equal(@"E:\Git\Lumi-wt-abc123\src\file.cs", FileAt(attachments, 0).Path);
    }

    [Fact]
    public void Empty_attachments_is_noop()
    {
        var attachments = new List<UserMessageAttachment>();
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

        Assert.Equal("file.cs", FileAt(attachments, 0).DisplayName);
    }

    [Fact]
    public void Rebases_directory_attachments()
    {
        var attachments = new List<UserMessageAttachment>
        {
            new UserMessageAttachmentDirectory
            {
                Path = @"E:\Git\Lumi\docs",
                DisplayName = "docs"
            }
        };
        var msg = new ChatMessage
        {
            Role = "user",
            Content = "test",
            Attachments = [@"E:\Git\Lumi\docs"]
        };

        ChatViewModel.RebaseAttachmentPaths(
            attachments,
            msg,
            @"E:\Git\Lumi",
            @"E:\Git\Lumi-wt-abc123");

        var directory = Assert.IsType<UserMessageAttachmentDirectory>(attachments[0]);
        Assert.Equal(@"E:\Git\Lumi-wt-abc123\docs", directory.Path);
        Assert.Equal(@"E:\Git\Lumi-wt-abc123\docs", msg.Attachments[0]);
        Assert.Equal("docs", directory.DisplayName);
    }

    [Fact]
    public void Chat_message_attachment_paths_include_files_and_directories()
    {
        var attachments = new List<UserMessageAttachment>
        {
            new UserMessageAttachmentFile
            {
                Path = @"E:\Git\Lumi\notes.txt",
                DisplayName = "notes.txt"
            },
            new UserMessageAttachmentDirectory
            {
                Path = @"E:\Git\Lumi\docs",
                DisplayName = "docs"
            }
        };

        var paths = ChatViewModel.GetChatMessageAttachmentPaths(attachments);

        Assert.Equal([@"E:\Git\Lumi\notes.txt", @"E:\Git\Lumi\docs"], paths);
    }
}
