using System;
using Lumi.Localization;
using Lumi.Models;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class DesktopViewModelLocalizationAuditTests : IDisposable
{
    public DesktopViewModelLocalizationAuditTests() => Loc.Load("en");

    public void Dispose() => Loc.Load("en");

    [Fact]
    public void ToolDisplayNamesFollowTheLanguageWithoutChangingToolIds()
    {
        Assert.Equal("Request Input", AgentsViewModel.GetToolDisplayName("ask_input"));

        Loc.Load("he");

        Assert.Equal("בקשת פרטים", AgentsViewModel.GetToolDisplayName("ask_input"));
        Assert.Equal("extension_action", AgentsViewModel.GetToolDisplayName("extension_action"));

        Loc.Load("en");

        Assert.Equal("Request Input", AgentsViewModel.GetToolDisplayName("ask_input"));
    }

    [Fact]
    public void WorkDurationUsesHebrewUnitsWithoutChangingItsRounding()
    {
        Assert.Equal("1m 5s", TranscriptBuilder.FormatWorkDuration(TimeSpan.FromSeconds(65)));

        Loc.Load("he");

        Assert.Equal("1 דק׳ 5 שנ׳", TranscriptBuilder.FormatWorkDuration(TimeSpan.FromSeconds(65)));
        Assert.Equal("1 שנ׳", TranscriptBuilder.FormatWorkDuration(TimeSpan.FromMilliseconds(400)));
        Assert.Equal("1 שע׳ 5 דק׳", TranscriptBuilder.FormatWorkDuration(TimeSpan.FromMinutes(65)));
    }

    [Fact]
    public void WorkspaceLocalizesFallbacksAndMetadataButNotUserText()
    {
        Loc.Load("he");
        var attachments = new WorkspaceUserMessageItem(1, "", "10:42", 2, 1, null, null);
        var content = new WorkspaceUserMessageItem(2, "  Keep this user text  ", "", 0, 0, null, null);

        Assert.Equal("2 קבצים מצורפים", attachments.Preview);
        Assert.Equal("10:42 · 2 קבצים · כישור אחד", attachments.MetaText);
        Assert.Equal("Keep this user text", content.Preview);
        Assert.Equal("הודעת משתמש", content.MetaText);
    }

    [Fact]
    public void SearchGroupUsesALocalizedWholeCountTemplate()
    {
        Loc.Load("he");

        Assert.Equal("הצג את כל 12", new SearchResultGroup { TotalCount = 12 }.ViewAllText);
    }

    [Fact]
    public void MemoryCategoryDisplayDoesNotChangeTheStoredCategory()
    {
        Loc.Load("he");
        var memory = new Memory { Key = "A fact", Category = MemoriesViewModel.DefaultCategory };
        var card = new MemoryCard(memory, Loc.Memories_Everywhere, _ => { });

        Assert.Equal("כללי", card.Category);
        Assert.Equal("General", memory.Category);
        Assert.Equal("כללי", Loc.GetMemoryCategoryDisplay(" general "));
        Assert.Equal("כללי", Loc.GetMemoryCategoryDisplay(""));
        Assert.Equal("My custom category", Loc.GetMemoryCategoryDisplay("My custom category"));
        Assert.Equal("  Work  ", Loc.GetMemoryCategoryDisplay("  Work  "));
    }

    [Fact]
    public void BuiltInSkillChipDescriptionDoesNotChangeTheTranscriptSnapshot()
    {
        var description = Loc.GetEnglish(nameof(Loc.BuiltIn_CodeHelper_Description));
        var reference = new SkillReference { Name = "Code Helper", Description = description };
        Loc.Load("he");

        Assert.Equal(Loc.BuiltIn_CodeHelper_Description, new SkillChipItem(reference, null).Description);
        Assert.Equal(description, reference.Description);

        reference.Description = "Keep my custom description";
        Assert.Equal("Keep my custom description", new SkillChipItem(reference, null).Description);
    }

    [Fact]
    public void JobWakeLocalizesOnlyFallbacksAndPreservesTheRawMessage()
    {
        Loc.Load("he");
        var fallback = new JobWakeItem(new ChatMessageViewModel(new ChatMessage { Role = "user" }), false);
        Assert.Equal(Loc.Jobs_WakeFallbackName, fallback.JobName);
        Assert.Equal(Loc.Jobs_WakeFallbackSignal, fallback.WakeSignal);
        Assert.NotEqual("Background job", fallback.JobName);
        Assert.NotEqual("Wake signal received.", fallback.WakeSignal);

        const string author = "Lumi Job - My job";
        const string content = "Background job triggered: My job\n\nJob instructions:\nKeep my instructions.\n\nTrigger context:\n\nRespond as Lumi";
        var message = new ChatMessage { Role = "user", Author = author, Content = content };
        var source = new ChatMessageViewModel(message);
        var item = new JobWakeItem(source, false);

        Assert.True(JobWakeItem.IsJobWakeMessage(source));
        Assert.Equal("My job", item.JobName);
        Assert.Equal("Keep my instructions.", item.Instructions);
        Assert.Equal(Loc.Jobs_WakeFallbackSignal, item.WakeSignal);
        Assert.Equal(author, message.Author);
        Assert.Equal(content, message.Content);
    }

    [Fact]
    public void HebrewInputRequestsKeepTheNativeStatusTokens()
    {
        Loc.Load("he");
        var message = new ChatMessage
        {
            Role = "tool",
            ToolName = "ask_input",
            ToolStatus = "InProgress",
            InputKind = "form",
            InputDefinition = """{"id":"details","title":"User-owned title","fields":[{"id":"note","label":"Your note"}]}"""
        };
        var item = new InputRequestItem(message);

        Assert.Equal("he", item.Language);
        Assert.Equal("Waiting", item.InputStatus);
        Assert.True(item.CanSubmit);
        Assert.Equal("User-owned title", item.Definition.Title);

        message.InputAnswer = "{}";
        Assert.Equal("Submitted", item.InputStatus);
        Assert.False(item.CanSubmit);

        message.InputCanceled = true;
        Assert.Equal("Canceled", item.InputStatus);
        Assert.False(item.CanSubmit);
    }
}
