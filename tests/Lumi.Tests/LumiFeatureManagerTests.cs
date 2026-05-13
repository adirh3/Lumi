using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class LumiFeatureManagerTests
{
    [Fact]
    public void ManageJobs_CreateScriptJob_CreatesOneShotWakeJob()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Watch chat"
        };
        var data = new AppData();
        data.Chats.Add(chat);
        var store = new DataStore(data);
        var manager = new LumiFeatureManager(store);

        var result = manager.ManageJobs(
            "create",
            name: "Watch price",
            prompt: "Wake me when the price drops.",
            triggerType: BackgroundJobTriggerTypes.Script,
            scriptContent: "Write-Output price-dropped",
            defaultChatId: chat.Id);

        Assert.True(result.DataChanged);
        var job = Assert.Single(data.BackgroundJobs);
        Assert.Equal(chat.Id, job.ChatId);
        Assert.Equal(BackgroundJobTriggerTypes.Script, job.TriggerType);
        Assert.True(job.IsTemporary);
        Assert.True(job.IsEnabled);
        Assert.NotNull(job.NextRunAt);
    }

    [Fact]
    public void ManageJobs_CreateIntervalTimeJob_CanBeOngoing()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Daily plan"
        };
        var data = new AppData();
        data.Chats.Add(chat);
        var store = new DataStore(data);
        var manager = new LumiFeatureManager(store);

        var result = manager.ManageJobs(
            "create",
            name: "Daily plan",
            prompt: "Summarize my day.",
            triggerType: BackgroundJobTriggerTypes.Time,
            scheduleType: BackgroundJobScheduleTypes.Interval,
            intervalMinutes: 60,
            isTemporary: false,
            defaultChatId: chat.Id);

        Assert.True(result.DataChanged);
        var job = Assert.Single(data.BackgroundJobs);
        Assert.Equal(BackgroundJobTriggerTypes.Time, job.TriggerType);
        Assert.Equal(BackgroundJobScheduleTypes.Interval, job.ScheduleType);
        Assert.False(job.IsTemporary);
        Assert.True(job.IsEnabled);
        Assert.NotNull(job.NextRunAt);
    }

    [Fact]
    public void ManageSkills_Delete_RemovesLinkedReferences()
    {
        var skill = new Skill
        {
            Id = Guid.NewGuid(),
            Name = "Conversation Skill",
            Description = "Created from a chat",
            Content = "# Skill"
        };
        var agent = new LumiAgent
        {
            Id = Guid.NewGuid(),
            Name = "Skillful Lumi",
            SkillIds = [skill.Id]
        };
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Skill Chat",
            ActiveSkillIds = [skill.Id]
        };

        var data = new AppData();
        data.Skills.Add(skill);
        data.Agents.Add(agent);
        data.Chats.Add(chat);

        var store = new DataStore(data);
        var manager = new LumiFeatureManager(store);

        var result = manager.ManageSkills("delete", identifier: skill.Name);

        Assert.True(result.DataChanged);
        Assert.Empty(data.Skills);
        Assert.Empty(agent.SkillIds);
        Assert.Empty(chat.ActiveSkillIds);
    }

    [Fact]
    public void ManageLumis_Delete_ClearsChatAssignments()
    {
        var agent = new LumiAgent
        {
            Id = Guid.NewGuid(),
            Name = "Daily Planner"
        };
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Agent Chat",
            AgentId = agent.Id
        };

        var data = new AppData();
        data.Agents.Add(agent);
        data.Chats.Add(chat);

        var store = new DataStore(data);
        var manager = new LumiFeatureManager(store);

        var result = manager.ManageLumis("delete", identifier: agent.Name);

        Assert.True(result.DataChanged);
        Assert.Empty(data.Agents);
        Assert.Null(chat.AgentId);
    }

    [Fact]
    public void ManageProjects_Delete_ClearsChatAssignments()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Cleanup Project"
        };
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Project Chat",
            ProjectId = project.Id
        };

        var data = new AppData();
        data.Projects.Add(project);
        data.Chats.Add(chat);

        var store = new DataStore(data);
        var manager = new LumiFeatureManager(store);

        var result = manager.ManageProjects("delete", identifier: project.Name);

        Assert.True(result.DataChanged);
        Assert.Empty(data.Projects);
        Assert.Null(chat.ProjectId);
    }

    [Fact]
    public void ManageProjects_Create_SavesAdditionalContextDirectories()
    {
        var data = new AppData();
        var store = new DataStore(data);
        var manager = new LumiFeatureManager(store);

        var result = manager.ManageProjects(
            "create",
            name: "Multi folder project",
            workingDirectory: @"C:\Repo",
            additionalContextDirectories: [@"C:\SharedSkills", @"C:\SharedSkills", @"D:\McpConfigs"]);

        Assert.True(result.DataChanged);
        var project = Assert.Single(data.Projects);
        Assert.Equal([@"C:\SharedSkills", @"D:\McpConfigs"], project.AdditionalContextDirectories);
    }

    [Fact]
    public void ManageProjects_Update_CanClearAdditionalContextDirectories()
    {
        var project = new Project
        {
            Name = "Multi folder project",
            AdditionalContextDirectories = [@"C:\SharedSkills"]
        };
        var data = new AppData();
        data.Projects.Add(project);
        var store = new DataStore(data);
        var manager = new LumiFeatureManager(store);

        var result = manager.ManageProjects(
            "update",
            identifier: project.Name,
            clearAdditionalContextDirectories: true);

        Assert.True(result.DataChanged);
        Assert.Empty(project.AdditionalContextDirectories);
    }

    [Fact]
    public void ManageMcps_Update_RenamesChatSelections()
    {
        var server = new McpServer
        {
            Id = Guid.NewGuid(),
            Name = "filesystem",
            ServerType = "local",
            Command = "npx.cmd",
            Args = ["-y", "@modelcontextprotocol/server-filesystem"]
        };
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "MCP Chat",
            ActiveMcpServerNames = ["filesystem"]
        };

        var data = new AppData();
        data.McpServers.Add(server);
        data.Chats.Add(chat);

        var store = new DataStore(data);
        var manager = new LumiFeatureManager(store);

        var result = manager.ManageMcps("update", identifier: server.Name, name: "local-filesystem");

        Assert.True(result.DataChanged);
        Assert.Equal("filesystem", result.RenamedMcpOldName);
        Assert.Equal("local-filesystem", result.RenamedMcpNewName);
        Assert.Equal("local-filesystem", server.Name);
        Assert.Equal(["local-filesystem"], chat.ActiveMcpServerNames);
    }

    [Fact]
    public void ManageMcps_Delete_RemovesLinkedReferences()
    {
        var server = new McpServer
        {
            Id = Guid.NewGuid(),
            Name = "filesystem",
            ServerType = "local",
            Command = "npx.cmd",
            Args = ["-y", "@modelcontextprotocol/server-filesystem"]
        };
        var agent = new LumiAgent
        {
            Id = Guid.NewGuid(),
            Name = "Mcp Lumi",
            McpServerIds = [server.Id]
        };
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "MCP Chat",
            ActiveMcpServerNames = ["filesystem"]
        };

        var data = new AppData();
        data.McpServers.Add(server);
        data.Agents.Add(agent);
        data.Chats.Add(chat);

        var store = new DataStore(data);
        var manager = new LumiFeatureManager(store);

        var result = manager.ManageMcps("delete", identifier: server.Name);

        Assert.True(result.DataChanged);
        Assert.Equal("filesystem", result.DeletedMcpName);
        Assert.Empty(data.McpServers);
        Assert.Empty(agent.McpServerIds);
        Assert.Empty(chat.ActiveMcpServerNames);
    }
}
