using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatHistoryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 14, 18, 0, 0, TimeSpan.Zero);

    private static ChatMessage Msg(string role, string content, string? author = null)
        => new() { Role = role, Content = content, Author = author, Timestamp = Now };

    private static Chat Chat(string title, DateTimeOffset updated, params ChatMessage[] messages)
        => new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            UpdatedAt = updated,
            Messages = messages.ToList()
        };

    private static ChatHistoryService CreateService(AppData data, bool withSearch)
    {
        var store = new DataStore(data);
        GlobalSearchService? search = withSearch
            ? new GlobalSearchService(() => store.Data, store.GetChatSearchSnapshot, () => Now)
            : null;
        return new ChatHistoryService(store, search, () => Now);
    }

    [Fact]
    public async Task SearchChats_ByTitle_ReturnsMatchingChatId()
    {
        var hotels = Chat("Honeymoon hotel shortlist", Now.AddDays(-1));
        var taxes = Chat("Quarterly taxes", Now.AddDays(-2));
        var service = CreateService(new AppData { Chats = [hotels, taxes] }, withSearch: false);

        var result = await service.SearchChatsAsync("honeymoon hotel");

        Assert.Contains(hotels.Id.ToString(), result);
        Assert.Contains("Honeymoon hotel shortlist", result);
        Assert.DoesNotContain(taxes.Id.ToString(), result);
    }

    [Fact]
    public async Task SearchChats_ByMessageContent_FindsChat()
    {
        var chat = Chat(
            "Weekly sync",
            Now.AddHours(-3),
            Msg("user", "Can you summarize the OLED television deal we found?"));
        var other = Chat("Grocery list", Now.AddHours(-5), Msg("user", "milk and eggs"));
        var service = CreateService(new AppData { Chats = [chat, other] }, withSearch: false);

        var result = await service.SearchChatsAsync("OLED television");

        Assert.Contains(chat.Id.ToString(), result);
        Assert.DoesNotContain(other.Id.ToString(), result);
    }

    [Fact]
    public async Task SearchChats_UsesGlobalSearchService_ForContentMatches()
    {
        var chat = Chat(
            "Planning notes",
            Now.AddHours(-2),
            Msg("assistant", "We discussed the application rollout plan in detail."));
        var service = CreateService(new AppData { Chats = [chat] }, withSearch: true);

        var result = await service.SearchChatsAsync("rollout");

        Assert.Contains(chat.Id.ToString(), result);
        Assert.Contains("Planning notes", result);
    }

    [Fact]
    public async Task SearchChats_EmptyQuery_ListsRecentChatsNewestFirst()
    {
        var oldest = Chat("Oldest", Now.AddDays(-9));
        var newest = Chat("Newest", Now.AddHours(-1));
        var middle = Chat("Middle", Now.AddDays(-3));
        var service = CreateService(new AppData { Chats = [oldest, newest, middle] }, withSearch: false);

        var result = await service.SearchChatsAsync("");

        var newestPos = result.IndexOf(newest.Id.ToString(), StringComparison.Ordinal);
        var middlePos = result.IndexOf(middle.Id.ToString(), StringComparison.Ordinal);
        var oldestPos = result.IndexOf(oldest.Id.ToString(), StringComparison.Ordinal);

        Assert.True(newestPos >= 0 && middlePos >= 0 && oldestPos >= 0);
        Assert.True(newestPos < middlePos);
        Assert.True(middlePos < oldestPos);
    }

    [Fact]
    public async Task SearchChats_NoMatch_ReturnsHelpfulMessage()
    {
        var chat = Chat("Cooking ideas", Now.AddDays(-1), Msg("user", "pasta recipes"));
        var service = CreateService(new AppData { Chats = [chat] }, withSearch: false);

        var result = await service.SearchChatsAsync("astrophysics");

        Assert.Contains("No chats matched", result);
        Assert.DoesNotContain(chat.Id.ToString(), result);
    }

    [Fact]
    public async Task ReadChat_ById_ReturnsRoleLabelledTranscript()
    {
        var chat = Chat(
            "Trip to Japan",
            Now.AddHours(-4),
            Msg("system", "internal system prompt"),
            Msg("user", "What anime locations should we visit?"),
            Msg("reasoning", "thinking about studios"),
            Msg("tool", "results", author: null),
            Msg("assistant", "Let's visit the Ghibli museum."));
        chat.Messages[3].ToolName = "web_search";
        chat.Messages[3].ToolOutput = "Found 3 results about Ghibli.";

        var service = CreateService(new AppData { Chats = [chat] }, withSearch: false);

        var result = await service.ReadChatAsync(chat.Id.ToString());

        Assert.Contains("Trip to Japan", result);
        Assert.Contains(chat.Id.ToString(), result);
        Assert.Contains("User: What anime locations should we visit?", result);
        Assert.Contains("Lumi: Let's visit the Ghibli museum.", result);
        Assert.Contains("[tool:", result);
        // system messages are internal; reasoning is hidden unless requested.
        Assert.DoesNotContain("internal system prompt", result);
        Assert.DoesNotContain("thinking about studios", result);
    }

    [Fact]
    public async Task ReadChat_IncludeReasoning_ShowsReasoning()
    {
        var chat = Chat(
            "Debugging",
            Now.AddHours(-1),
            Msg("user", "why is it slow?"),
            Msg("reasoning", "profiling the hot path"),
            Msg("assistant", "It was an N+1 query."));
        var service = CreateService(new AppData { Chats = [chat] }, withSearch: false);

        var result = await service.ReadChatAsync(chat.Id.ToString(), includeReasoning: true);

        Assert.Contains("[reasoning] profiling the hot path", result);
    }

    [Fact]
    public async Task ReadChat_ByExactTitle_ReadsChat()
    {
        var chat = Chat("Budget review", Now.AddHours(-2), Msg("user", "let's cut costs"));
        var service = CreateService(new AppData { Chats = [chat] }, withSearch: false);

        var result = await service.ReadChatAsync("budget review");

        Assert.Contains("Budget review", result);
        Assert.Contains("User: let's cut costs", result);
    }

    [Fact]
    public async Task ReadChat_UnknownId_ReturnsNotFound()
    {
        var chat = Chat("Anything", Now, Msg("user", "hi"));
        var service = CreateService(new AppData { Chats = [chat] }, withSearch: false);

        var missingId = Guid.NewGuid();
        var result = await service.ReadChatAsync(missingId.ToString());

        Assert.Contains("No chat has the id", result);
        Assert.Contains(missingId.ToString(), result);
    }

    [Fact]
    public async Task ReadChat_AmbiguousPhrase_ReturnsCandidates()
    {
        var rome = Chat("Trip planning Rome", Now.AddHours(-1), Msg("user", "colosseum"));
        var paris = Chat("Trip planning Paris", Now.AddHours(-2), Msg("user", "eiffel tower"));
        var service = CreateService(new AppData { Chats = [rome, paris] }, withSearch: false);

        var result = await service.ReadChatAsync("Trip planning");

        Assert.Contains("Several chats match", result);
        Assert.Contains(rome.Id.ToString(), result);
        Assert.Contains(paris.Id.ToString(), result);
    }

    [Fact]
    public async Task ReadChat_WindowsToMostRecentMessages()
    {
        var chat = Chat(
            "Long chat",
            Now,
            Msg("user", "FIRST_MESSAGE_MARKER"),
            Msg("assistant", "reply one"),
            Msg("user", "second"),
            Msg("assistant", "reply two"),
            Msg("user", "LAST_MESSAGE_MARKER"));
        var service = CreateService(new AppData { Chats = [chat] }, withSearch: false);

        var result = await service.ReadChatAsync(chat.Id.ToString(), maxMessages: 2);

        Assert.Contains("showing the latest 2 of 5", result);
        Assert.Contains("LAST_MESSAGE_MARKER", result);
        Assert.DoesNotContain("FIRST_MESSAGE_MARKER", result);
    }

    [Fact]
    public async Task ReadChat_EmptyIdentifier_AsksForOne()
    {
        var service = CreateService(new AppData { Chats = [] }, withSearch: false);

        var result = await service.ReadChatAsync("   ");

        Assert.Contains("Provide a chat id or title", result);
    }

    [Fact]
    public async Task ReadChat_SurfacesWorktreeWorkspace()
    {
        var workspace = Directory.CreateTempSubdirectory("lumi_ws_").FullName;
        try
        {
            var chat = Chat("Refactor auth", Now.AddHours(-1), Msg("user", "let's refactor"));
            chat.WorktreePath = workspace;
            var service = CreateService(new AppData { Chats = [chat] }, withSearch: false);

            var result = await service.ReadChatAsync(chat.Id.ToString());

            Assert.Contains($"workspace: {workspace} (git worktree)", result);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ReadChat_SurfacesProjectWorkspace_WhenNoWorktree()
    {
        var workspace = Directory.CreateTempSubdirectory("lumi_proj_").FullName;
        try
        {
            var project = new Project { Name = "Lumi", WorkingDirectory = workspace };
            var chat = Chat("Project chat", Now.AddHours(-1), Msg("user", "hi"));
            chat.ProjectId = project.Id;
            var service = CreateService(new AppData { Chats = [chat], Projects = [project] }, withSearch: false);

            var result = await service.ReadChatAsync(chat.Id.ToString());

            Assert.Contains($"workspace: {workspace} (project folder)", result);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ReadChat_FlagsWorkspaceMissingOnDisk()
    {
        var missing = Path.Combine(Path.GetTempPath(), "lumi_missing_" + Guid.NewGuid().ToString("N"));
        var chat = Chat("Gone", Now.AddHours(-1), Msg("user", "hi"));
        chat.WorktreePath = missing;
        var service = CreateService(new AppData { Chats = [chat] }, withSearch: false);

        var result = await service.ReadChatAsync(chat.Id.ToString());

        Assert.Contains("no longer on disk", result);
    }

    [Fact]
    public async Task ReadChat_FallsBackToProjectFolder_WhenWorktreeMissing()
    {
        var workspace = Directory.CreateTempSubdirectory("lumi_proj_").FullName;
        var missing = Path.Combine(Path.GetTempPath(), "lumi_missing_" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = new Project { Name = "Lumi", WorkingDirectory = workspace };
            var chat = Chat("Stale worktree", Now.AddHours(-1), Msg("user", "hi"));
            chat.ProjectId = project.Id;
            chat.WorktreePath = missing;
            var service = CreateService(new AppData { Chats = [chat], Projects = [project] }, withSearch: false);

            var result = await service.ReadChatAsync(chat.Id.ToString());

            Assert.Contains($"workspace: {workspace} (project folder", result);
            Assert.Contains("git worktree", result);
            Assert.Contains("no longer on disk", result);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ReadChat_SurfacesSavedPlan()
    {
        var chat = Chat("Planned work", Now.AddHours(-1), Msg("user", "hi"));
        chat.PlanContent = "## Plan\n- Step ALPHA\n- Step BETA";
        var service = CreateService(new AppData { Chats = [chat] }, withSearch: false);

        var result = await service.ReadChatAsync(chat.Id.ToString());

        Assert.Contains("plan:", result);
        Assert.Contains("Step ALPHA", result);
        Assert.Contains("Step BETA", result);
    }

    [Fact]
    public async Task ReadChat_SurfacesActiveSkillsAndMcpServers()
    {
        var skill = new Skill { Name = "Web Researcher" };
        var chat = Chat("Tooled chat", Now.AddHours(-1), Msg("user", "hi"));
        chat.ActiveSkillIds = [skill.Id];
        // Legacy file-skill selections should no longer be surfaced as active skills.
        chat.ActiveExternalSkillNames = ["Azure Image Generator"];
        chat.ActiveMcpServerNames = ["github", "playwright"];
        var service = CreateService(new AppData { Chats = [chat], Skills = [skill] }, withSearch: false);

        var result = await service.ReadChatAsync(chat.Id.ToString());

        Assert.Contains("skills: ", result);
        Assert.Contains("Web Researcher", result);
        Assert.DoesNotContain("Azure Image Generator", result);
        Assert.Contains("mcp servers: github, playwright", result);
    }

    [Fact]
    public async Task ReadChat_SurfacesModelAndTokenUsage()
    {
        var chat = Chat("Costly chat", Now.AddHours(-1), Msg("user", "hi"));
        chat.LastModelUsed = "gpt-5.5";
        chat.TotalInputTokens = 12_345;
        chat.TotalOutputTokens = 6_789;
        var service = CreateService(new AppData { Chats = [chat] }, withSearch: false);

        var result = await service.ReadChatAsync(chat.Id.ToString());

        Assert.Contains("model: gpt-5.5", result);
        Assert.Contains("tokens: 12.3k in / 6.8k out", result);
    }

    [Fact]
    public async Task ReadChat_OmitsWorkspace_WhenChatHasNoWorkspace()
    {
        var chat = Chat("No workspace", Now.AddHours(-1), Msg("user", "hi"));
        var service = CreateService(new AppData { Chats = [chat] }, withSearch: false);

        var result = await service.ReadChatAsync(chat.Id.ToString());

        Assert.DoesNotContain("workspace:", result);
    }
}
