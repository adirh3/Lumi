using System.Text.Json;
using Lumi.Models;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class BackgroundJobIconTests
{
    [Theory]
    [InlineData("""{"backgroundJobs":[{"name":"Legacy job"}]}""")]
    [InlineData("""{"backgroundJobs":[{"iconGlyph":null}]}""")]
    [InlineData("""{"backgroundJobs":[{"iconGlyph":" "}]}""")]
    public void OlderOrEmptyIconsUseClockWithoutOptingIntoTitles(string json)
    {
        var data = JsonSerializer.Deserialize(json, AppDataJsonContext.Default.AppData)!;
        var job = Assert.Single(data.BackgroundJobs);
        Assert.Equal(BackgroundJob.DefaultIconGlyph, job.DisplayIconGlyph);
        Assert.False(job.UseIconInChatTitles);
    }

    [Fact]
    public void TitleGuidanceIsOptInAndPreservesJobInstructions()
    {
        var job = new BackgroundJob
        {
            Name = "Inbox monitor",
            Prompt = "Only mark result chats after a qualifying finding.",
            IconGlyph = "\U0001F4E9"
        };
        var ordinaryPrompt = ChatViewModel.BuildBackgroundJobPrompt(job, "New messages");
        Assert.DoesNotContain("Job chat-title icon:", ordinaryPrompt);
        Assert.Contains(job.Prompt, ordinaryPrompt);

        job.UseIconInChatTitles = true;
        var prompt = ChatViewModel.BuildBackgroundJobPrompt(job, "New messages");
        Assert.Contains("Job chat-title icon: \U0001F4E9", prompt);
        Assert.Contains(job.Prompt, prompt);
        Assert.Contains("Trigger context:\nNew messages", prompt);
        Assert.Contains("Explicit user or job instructions take priority", prompt);
        Assert.Contains("do not add a fallback icon", prompt);
        Assert.Contains("exactly one category prefix", prompt);
        Assert.Contains("preserve an existing custom category on a shared chat", prompt);
        Assert.Contains("require evidence and explicit alert criteria", prompt);
        Assert.Contains("reuse existing chats and deduplication mappings", prompt);
        Assert.Contains("Do not wake existing chats just to rename them", prompt);
    }

    [Fact]
    public void TitleGuidanceUsesDefaultClockForEmptyIcon()
    {
        var job = new BackgroundJob { IconGlyph = "", UseIconInChatTitles = true };
        Assert.Contains(
            $"Job chat-title icon: {BackgroundJob.DefaultIconGlyph}",
            ChatViewModel.BuildBackgroundJobPrompt(job, ""));
    }
}
