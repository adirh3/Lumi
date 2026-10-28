using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class RemoteFeatureManagerLocalizationAuditTests : IDisposable
{
    public RemoteFeatureManagerLocalizationAuditTests() => Loc.Load("he");

    public void Dispose() => Loc.Load("en");

    [Fact]
    public void ModelFacingReportsAndValidationRemainCanonicalWhileUiReportsCanBeLocalized()
    {
        var target = new Chat { Title = "Original chat" };
        var source = new Chat { Title = "Source chat" };
        var skill = new Skill
        {
            Name = "Code Helper",
            Description = Loc.GetEnglish(nameof(Loc.BuiltIn_CodeHelper_Description)),
            Content = "Original instructions",
            IsBuiltIn = true
        };
        var agent = new LumiAgent
        {
            Name = "Coding Lumi",
            Description = Loc.GetEnglish(nameof(Loc.BuiltIn_CodingLumi_Description)),
            SystemPrompt = "Original agent instructions",
            IsBuiltIn = true
        };
        var data = new AppData
        {
            Chats = [target, source],
            Projects = [new Project { Name = "Original project" }],
            Skills = [skill],
            Agents = [agent],
            McpServers = [new McpServer { Name = "Original connector" }],
            Memories = [new Memory { Key = "Original memory", Content = "Original fact", Category = "General" }],
            BackgroundJobs =
            [
                new BackgroundJob
                {
                    Name = "Original job", ChatId = target.Id, ScheduleType = "weekly",
                    DaysOfWeek = "Mon,Tue,Wed,Thu,Fri", DailyTime = "08:15"
                }
            ]
        };
        var store = new DataStore(data);
        var model = new LumiFeatureManager(store);
        string[] Capture() =>
        [
            model.ManageProjects("list").Message,
            model.ManageSkills("list").Message,
            model.ManageLumis("list").Message,
            model.ManageMcps("list").Message,
            model.ManageMemories("list").Message,
            model.ManageJobs("list").Message,
            model.ManageSkills("create").Message,
            model.ManageMcps("create", name: "Invalid", serverType: "invalid").Message,
            model.ManageJobs("create", name: "Invalid time", prompt: "Original prompt",
                chatIdentifier: target.Id.ToString(), scheduleType: "daily", dailyTime: "invalid").Message,
            model.ManageJobs("create", name: "Invalid days", prompt: "Original prompt",
                chatIdentifier: target.Id.ToString(), scheduleType: "weekly", daysOfWeek: "invalid").Message,
            model.ManageJobs("create", name: "Invalid cron", prompt: "Original prompt",
                chatIdentifier: target.Id.ToString(), scheduleType: "cron", cronExpression: "*/0 * * * *").Message,
            model.ManageJobs("create", name: "Invalid event", prompt: "Original prompt",
                chatIdentifier: target.Id.ToString(), triggerType: "chat_event",
                sourceChatIdentifier: source.Id.ToString(), chatEventTypes: ["invalid"]).Message
        ];

        Loc.Load("en");
        var english = Capture();
        foreach (var (language, _) in Loc.AvailableLanguages)
        {
            Loc.Load(language);
            Assert.Equal(english, Capture());
        }
        Loc.Load("he");
        Assert.Equal(english, Capture());
        Assert.Contains(skill.Description, model.ManageSkills("list").Message);
        Assert.Contains("Every weekdays at 08:15", model.ManageJobs("list").Message);
        Assert.Contains("category: General", model.ManageMemories("list").Message);

        var display = new LumiFeatureManager(store, localizeOutput: true);
        Assert.Contains(skill.DisplayDescription, display.ManageSkills("list").Message);
        Assert.Contains("יום שני עד שישי", display.ManageJobs("list").Message);
        Assert.Contains("קטגוריה: כללי", display.ManageMemories("list").Message);
        Assert.StartsWith("כישורים:", display.ManageSkills("list").Message);
        Assert.Equal("Original instructions", skill.Content);
        Assert.Equal("General", data.Memories[0].Category);
        Assert.Single(data.BackgroundJobs);
    }

    [Fact]
    public void ProjectCreateAndUpdate_LocalizeReportsWithoutChangingIdentityOrAuthoredContent()
    {
        const string name = "User project";
        const string instructions = "Keep this authored text: replaceSection and [REDACTED].";
        var directory = Path.Combine(Path.GetTempPath(), "feature-manager-localization-project");
        var data = new AppData();
        var manager = new LumiFeatureManager(new DataStore(data), localizeOutput: true);

        var created = manager.ManageProjects("create", name: name, instructions: instructions);
        var project = Assert.Single(data.Projects);
        var id = project.Id;

        Assert.True(created.DataChanged);
        Assert.False(created.SyncSkillFiles);
        Assert.Equal($"הפרויקט נוצר: {name}.", created.Message);
        Assert.Equal(name, project.Name);
        Assert.Equal(instructions, project.Instructions);

        var updated = manager.ManageProjects(
            "update", identifier: id.ToString(), workingDirectory: directory);

        Assert.True(updated.DataChanged);
        Assert.False(updated.SyncSkillFiles);
        Assert.StartsWith("הפרויקט עודכן.\n", updated.Message);
        Assert.Contains(id.ToString(), updated.Message);
        Assert.Contains($"תיקיית עבודה: {directory}", updated.Message);
        Assert.Contains($"הוראות: {instructions}", updated.Message);
        Assert.Equal(id, project.Id);
        Assert.Equal(name, project.Name);
        Assert.Equal(directory, project.WorkingDirectory);
        Assert.Equal(instructions, project.Instructions);
    }

    [Fact]
    public void SkillCreateAndUpdate_LocalizeDetailsButPreserveIdsContentAndVerificationData()
    {
        const string name = "Raw skill name";
        const string description = "Authored description: updateMode and [REDACTED]";
        const string updatedDescription = "Keep my edited description: name and description";
        const string content = "# User heading\n\nKeep replaceSection, KEY=VALUE and שלום unchanged.";
        var data = new AppData();
        var manager = new LumiFeatureManager(new DataStore(data), localizeOutput: true);

        var created = manager.ManageSkills("create", name: name, description: description, content: content);
        var skill = Assert.Single(data.Skills);
        var id = skill.Id;
        var updated = manager.ManageSkills(
            "update", identifier: id.ToString(), description: updatedDescription);

        Assert.StartsWith("הכישור נוצר.\n", created.Message);
        Assert.StartsWith("הכישור עודכן.\n", updated.Message);
        Assert.Contains(description, created.Message);
        Assert.Contains(updatedDescription, updated.Message);

        var bytes = Encoding.UTF8.GetByteCount(content);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)), 0, 4).ToLowerInvariant();
        foreach (var result in new[] { created, updated })
        {
            Assert.True(result.DataChanged);
            Assert.True(result.SyncSkillFiles);
            Assert.Equal(id, Assert.Single(result.SkillIds!));
            Assert.Equal(bytes, result.SkillContentBytes);
            Assert.Equal(hash, result.SkillContentHash);
            Assert.Contains($"אומתה שמירת {bytes} בתים (sha256:{hash}).", result.Message);
            Assert.Contains(id.ToString(), result.Message);
            Assert.Contains(name, result.Message);
        }

        Assert.Equal(id, skill.Id);
        Assert.Equal(name, skill.Name);
        Assert.Equal(content, skill.Content);
        Assert.Equal(updatedDescription, skill.Description);

        var serialized = JsonSerializer.SerializeToElement(skill);
        Assert.Equal(id, serialized.GetProperty("Id").GetGuid());
        Assert.Equal(content, serialized.GetProperty("Content").GetString());
        Assert.Equal(updatedDescription, serialized.GetProperty("Description").GetString());
        Assert.False(serialized.TryGetProperty("DisplayDescription", out _));

        var listed = manager.ManageSkills("list");
        Assert.False(listed.DataChanged);
        Assert.StartsWith("כישורים:\n", listed.Message);
        Assert.Equal(id, Assert.Single(listed.SkillIds!));
    }

    [Fact]
    public void DuplicateProjectName_ReturnsHebrewErrorWithoutChangingData()
    {
        var project = new Project { Name = "Existing project", Instructions = "Keep my instructions" };
        var data = new AppData { Projects = [project] };
        var manager = new LumiFeatureManager(new DataStore(data), localizeOutput: true);

        var rejected = manager.ManageProjects(
            "create", name: project.Name, instructions: "Do not overwrite the existing project");

        Assert.False(rejected.DataChanged);
        Assert.False(rejected.SyncSkillFiles);
        Assert.Equal($"כבר קיים פרויקט בשם \"{project.Name}\".", rejected.Message);
        Assert.Same(project, Assert.Single(data.Projects));
        Assert.Equal("Keep my instructions", project.Instructions);
    }

    [Theory]
    [InlineData(false, "נדרש תוכן לכישור.")]
    [InlineData(true, "תוכן הכישור אינו יכול להיות ריק.")]
    public void EmptySkillContent_ReturnsHebrewErrorAndPreservesFlagsAndExistingContent(
        bool updating, string expectedMessage)
    {
        var skill = new Skill { Name = "Existing skill", Content = "Keep this existing content" };
        var data = new AppData { Skills = updating ? [skill] : [] };
        var manager = new LumiFeatureManager(new DataStore(data), localizeOutput: true);

        var rejected = manager.ManageSkills(
            updating ? "update" : "create",
            identifier: updating ? skill.Id.ToString() : null,
            name: updating ? null : "Empty skill",
            content: " \n ");

        Assert.False(rejected.DataChanged);
        Assert.False(rejected.SyncSkillFiles);
        Assert.Null(rejected.SkillIds);
        Assert.Null(rejected.SkillContentBytes);
        Assert.Null(rejected.SkillContentHash);
        Assert.Equal(expectedMessage, rejected.Message);
        Assert.Equal("Keep this existing content", skill.Content);
        if (updating)
            Assert.Same(skill, Assert.Single(data.Skills));
        else
            Assert.Empty(data.Skills);
    }

    [Fact]
    public void ConnectorReports_LocalizeWordingWithoutChangingProtocolOrRedactionSemantics()
    {
        var server = new McpServer
        {
            Name = "Raw connector name",
            ServerType = "remote",
            Url = "https://example.test/mcp",
            Headers = new() { ["Authorization"] = "Bearer test-fixture-value" },
            Tools = ["raw_tool_name"]
        };
        var id = server.Id;
        var data = new AppData { McpServers = [server] };
        var manager = new LumiFeatureManager(new DataStore(data), localizeOutput: true);

        var updated = manager.ManageMcps(
            "update", identifier: id.ToString(), headerEntries: ["authorization=[REDACTED]"]);

        Assert.True(updated.DataChanged);
        Assert.True(updated.McpCatalogChanged);
        Assert.StartsWith("המחבר עודכן.\n", updated.Message);
        Assert.Contains(" | remote | ", updated.Message);
        Assert.Contains("raw_tool_name", updated.Message);
        Assert.Contains(id.ToString(), updated.Message);
        Assert.Equal(id, server.Id);
        Assert.Equal("remote", server.ServerType);
        Assert.Equal("https://example.test/mcp", server.Url);
        Assert.Equal("Bearer test-fixture-value", server.Headers["Authorization"]);
        Assert.False(server.Headers.ContainsKey("authorization"));

        var rejected = manager.ManageMcps(
            "update", identifier: id.ToString(), headerEntries: ["bad-entry"]);
        Assert.False(rejected.DataChanged);
        Assert.False(rejected.McpCatalogChanged);
        Assert.Contains("רשומת מפתח/ערך אינה תקינה", rejected.Message);
        Assert.Contains("bad-entry", rejected.Message);
        Assert.Contains("KEY=VALUE", rejected.Message);
        Assert.Equal("Bearer test-fixture-value", server.Headers["Authorization"]);
    }

    [Fact]
    public void JobReport_LocalizesHeadingsWithoutChangingTriggerScheduleOrStatusTokens()
    {
        var chat = new Chat { Title = "Raw target chat" };
        var data = new AppData { Chats = [chat] };
        var manager = new LumiFeatureManager(new DataStore(data), localizeOutput: true);

        var created = manager.ManageJobs(
            "create", name: "Raw job name", prompt: "Keep these raw instructions",
            chatIdentifier: chat.Id.ToString(), triggerType: "time", scheduleType: "interval",
            intervalMinutes: 60);
        var job = Assert.Single(data.BackgroundJobs);

        Assert.True(created.DataChanged);
        Assert.True(created.BackgroundJobsChanged);
        Assert.StartsWith("משימת הרקע נוצרה.\n", created.Message);
        Assert.Contains($"שיחה: {chat.Title}", created.Message);
        Assert.Contains($"מצב: {BackgroundJobRunStatuses.Idle}", created.Message);
        Assert.Contains(job.Id.ToString(), created.Message);
        Assert.Equal(chat.Id, job.ChatId);
        Assert.Equal("time", job.TriggerType);
        Assert.Equal("interval", job.ScheduleType);
        Assert.Equal(BackgroundJobRunStatuses.Idle, job.LastRunStatus);
        Assert.Equal("Keep these raw instructions", job.Prompt);
    }

    [Theory]
    [InlineData("General", "כללי")]
    [InlineData("Work", "Work")]
    [InlineData("Personal", "Personal")]
    public void MemoryReport_LocalizesOnlyTheDefaultCategoryWithoutChangingStoredData(
        string category, string expectedDisplay)
    {
        var memory = new Memory { Key = "Raw memory key", Category = category, Content = "Original content" };
        var id = memory.Id;
        var data = new AppData { Memories = [memory] };
        var manager = new LumiFeatureManager(new DataStore(data), localizeOutput: true);

        var updated = manager.ManageMemories(
            "update", identifier: id.ToString(), content: "Keep this revised authored content");

        Assert.True(updated.DataChanged);
        Assert.StartsWith("הזיכרון עודכן.\n", updated.Message);
        Assert.Contains($"קטגוריה: {expectedDisplay}", updated.Message);
        Assert.Contains(id.ToString(), updated.Message);
        Assert.Equal(id, memory.Id);
        Assert.Equal(category, memory.Category);
        Assert.Equal("Keep this revised authored content", memory.Content);
        Assert.Equal(category, JsonSerializer.SerializeToElement(memory).GetProperty("Category").GetString());
    }

    [Fact]
    public void EnglishReports_RetainCompatibleSuccessSummaryAndValidationWording()
    {
        Loc.Load("en");
        var data = new AppData();
        var manager = new LumiFeatureManager(new DataStore(data), localizeOutput: true);
        var created = manager.ManageProjects("create", name: "Example", instructions: "Raw instructions");
        var project = Assert.Single(data.Projects);
        var updated = manager.ManageProjects("update", identifier: project.Id.ToString(), instructions: "Raw instructions");

        Assert.Equal("Project created: Example.", created.Message);
        Assert.Equal(
            $"Project updated.\n- {project.Id} | Example | chats: 0 | workdir: (none) | context folders: (none) | instructions: Raw instructions",
            updated.Message);
        Assert.Equal(
            "A project named \"Example\" already exists.",
            manager.ManageProjects("create", name: "Example").Message);
        Assert.Equal(
            "Skill content is required.",
            manager.ManageSkills("create", name: "Empty skill", content: "").Message);
    }
}
