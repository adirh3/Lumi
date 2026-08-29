using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
        Assert.True(result.BackgroundJobsChanged);
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
        Assert.True(result.BackgroundJobsChanged);
        var job = Assert.Single(data.BackgroundJobs);
        Assert.Equal(BackgroundJobTriggerTypes.Time, job.TriggerType);
        Assert.Equal(BackgroundJobScheduleTypes.Interval, job.ScheduleType);
        Assert.False(job.IsTemporary);
        Assert.True(job.IsEnabled);
        Assert.NotNull(job.NextRunAt);
    }

    [Fact]
    public void ManageJobs_CreateChatEventJob_DefaultsToIdleAndUsesSeparateSourceChat()
    {
        var leadChat = new Chat { Title = "Lead" };
        var workerChat = new Chat { Title = "Worker" };
        var data = new AppData { Chats = [leadChat, workerChat] };
        var manager = new LumiFeatureManager(new DataStore(data));

        var result = manager.ManageJobs(
            "create",
            name: "Worker finished",
            prompt: "Review the worker result.",
            triggerType: BackgroundJobTriggerTypes.ChatEvent,
            defaultChatId: leadChat.Id,
            sourceChatIdentifier: workerChat.Id.ToString());

        Assert.True(result.DataChanged);
        var job = Assert.Single(data.BackgroundJobs);
        Assert.Equal(leadChat.Id, job.ChatId);
        Assert.Equal(workerChat.Id, job.SourceChatId);
        Assert.Equal([ChatLifecycleEventTypes.Idle], job.ChatEventTypes);
        Assert.Null(job.NextRunAt);
        Assert.False(job.IsTemporary);
    }

    [Fact]
    public void ManageJobs_CreateChatEventJob_RejectsSelfSubscription()
    {
        var chat = new Chat { Title = "Lead" };
        var data = new AppData { Chats = [chat] };
        var manager = new LumiFeatureManager(new DataStore(data));

        var result = manager.ManageJobs(
            "create",
            name: "Loop",
            prompt: "Wake yourself.",
            triggerType: BackgroundJobTriggerTypes.ChatEvent,
            defaultChatId: chat.Id,
            sourceChatIdentifier: chat.Id.ToString());

        Assert.False(result.DataChanged);
        Assert.Contains("different chat", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(data.BackgroundJobs);
    }

    [Fact]
    public void ManageJobs_CreateChatEventJob_NormalizesMultipleFilters()
    {
        var leadChat = new Chat { Title = "Lead" };
        var workerChat = new Chat { Title = "Worker" };
        var data = new AppData { Chats = [leadChat, workerChat] };
        var manager = new LumiFeatureManager(new DataStore(data));

        var result = manager.ManageJobs(
            "create",
            name: "Worker activity",
            prompt: "Track worker progress.",
            triggerType: "event",
            defaultChatId: leadChat.Id,
            sourceChatIdentifier: workerChat.Title,
            chatEventTypes: ["turn_started", "turn-ended", "error"]);

        Assert.True(result.DataChanged);
        Assert.Equal(
            [ChatLifecycleEventTypes.TurnStart, ChatLifecycleEventTypes.TurnEnd, ChatLifecycleEventTypes.Error],
            Assert.Single(data.BackgroundJobs).ChatEventTypes);
    }

    [Fact]
    public void ManageJobs_CreateChatEventJob_RejectsReciprocalCycle()
    {
        var firstChat = new Chat { Title = "First" };
        var secondChat = new Chat { Title = "Second" };
        var data = new AppData { Chats = [firstChat, secondChat] };
        var manager = new LumiFeatureManager(new DataStore(data));

        var first = manager.ManageJobs(
            "create",
            name: "First wakes second",
            prompt: "Continue in second.",
            chatIdentifier: secondChat.Id.ToString(),
            triggerType: BackgroundJobTriggerTypes.ChatEvent,
            sourceChatIdentifier: firstChat.Id.ToString());
        var second = manager.ManageJobs(
            "create",
            name: "Second wakes first",
            prompt: "Continue in first.",
            chatIdentifier: firstChat.Id.ToString(),
            triggerType: BackgroundJobTriggerTypes.ChatEvent,
            sourceChatIdentifier: secondChat.Id.ToString());

        Assert.True(first.DataChanged);
        Assert.False(second.DataChanged);
        Assert.Contains("cycle", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(data.BackgroundJobs);
    }

    [Fact]
    public void ManageJobs_FailedUpdate_DoesNotMutateExistingChatEventJob()
    {
        var leadChat = new Chat { Title = "Lead" };
        var workerChat = new Chat { Title = "Worker" };
        var job = new BackgroundJob
        {
            Name = "Worker completion",
            Prompt = "Review the worker result.",
            ChatId = leadChat.Id,
            SourceChatId = workerChat.Id,
            TriggerType = BackgroundJobTriggerTypes.ChatEvent,
            ChatEventTypes = [ChatLifecycleEventTypes.Idle],
            IsEnabled = true
        };
        var data = new AppData
        {
            Chats = [leadChat, workerChat],
            BackgroundJobs = [job]
        };
        var manager = new LumiFeatureManager(new DataStore(data));

        var result = manager.ManageJobs(
            "update",
            identifier: job.Name,
            name: "Mutated name",
            triggerType: BackgroundJobTriggerTypes.Time,
            scheduleType: BackgroundJobScheduleTypes.Cron,
            cronExpression: "invalid cron");

        Assert.False(result.DataChanged);
        Assert.Equal("Worker completion", job.Name);
        Assert.Equal(BackgroundJobTriggerTypes.ChatEvent, job.TriggerType);
        Assert.Equal(workerChat.Id, job.SourceChatId);
        Assert.Equal([ChatLifecycleEventTypes.Idle], job.ChatEventTypes);
        Assert.True(job.IsEnabled);
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
        Assert.True(result.McpCatalogChanged);
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
        Assert.True(result.McpCatalogChanged);
        Assert.Equal("filesystem", result.DeletedMcpName);
        Assert.Empty(data.McpServers);
        Assert.Empty(agent.McpServerIds);
        Assert.Empty(chat.ActiveMcpServerNames);
    }

    // ---- Surgical skill editing ----

    private static string NewTempSkillsDir()
        => Path.Combine(Path.GetTempPath(), "LumiSkillTests", Guid.NewGuid().ToString("N"));

    private static (DataStore Store, LumiFeatureManager Manager, Skill Skill) NewSkillStore(
        string skillsDir, string content, bool builtIn = false, string name = "Test Skill")
    {
        var skill = new Skill { Id = Guid.NewGuid(), Name = name, Description = "desc", Content = content, IsBuiltIn = builtIn };
        var data = new AppData();
        data.Skills.Add(skill);
        var store = new DataStore(data, skillsDir);
        return (store, new LumiFeatureManager(store), skill);
    }

    private static void CleanupDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void ManageSkills_Patch_UniqueMatch_ReplacesOnlyThatOccurrence()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (_, manager, skill) = NewSkillStore(dir, "# Title\n\nalpha beta gamma\n\ndelta");
            var result = manager.ManageSkills("update", identifier: skill.Name, updateMode: "patch",
                editOldString: "beta", editNewString: "BETA");

            Assert.True(result.DataChanged);
            Assert.Equal("# Title\n\nalpha BETA gamma\n\ndelta", skill.Content);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_Patch_ZeroMatch_Fails()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (_, manager, skill) = NewSkillStore(dir, "alpha beta gamma");
            var result = manager.ManageSkills("update", identifier: skill.Name, updateMode: "patch",
                editOldString: "missing", editNewString: "x");

            Assert.False(result.DataChanged);
            Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("alpha beta gamma", skill.Content);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_Patch_MultiMatch_Fails()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (_, manager, skill) = NewSkillStore(dir, "aa aa aa");
            var result = manager.ManageSkills("update", identifier: skill.Name, updateMode: "patch",
                editOldString: "aa", editNewString: "bb");

            Assert.False(result.DataChanged);
            Assert.Contains("matched 3 times", result.Message);
            Assert.Contains("unique", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("aa aa aa", skill.Content);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_Append_AddsWithNewlineSeparator()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (_, manager, skill) = NewSkillStore(dir, "line one");
            var result = manager.ManageSkills("update", identifier: skill.Name, updateMode: "append",
                editNewString: "line two");

            Assert.True(result.DataChanged);
            Assert.Equal("line one\nline two", skill.Content);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_Prepend_AddsWithNewlineSeparator()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (_, manager, skill) = NewSkillStore(dir, "body");
            var result = manager.ManageSkills("update", identifier: skill.Name, updateMode: "prepend",
                editNewString: "header");

            Assert.True(result.DataChanged);
            Assert.Equal("header\nbody", skill.Content);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_ReplaceSection_ReplacesBlockAndLeavesSiblingsIntact()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var content = "# Skill\n\n## Alpha\nalpha body\n\n## Beta\nbeta body\n\n## Gamma\ngamma body";
            var (_, manager, skill) = NewSkillStore(dir, content);
            var result = manager.ManageSkills("update", identifier: skill.Name, updateMode: "replaceSection",
                editOldString: "## Beta", editNewString: "## Beta\nreplaced beta");

            Assert.True(result.DataChanged);
            Assert.Equal(
                "# Skill\n\n## Alpha\nalpha body\n\n## Beta\nreplaced beta\n## Gamma\ngamma body",
                skill.Content);
            Assert.Contains("## Alpha\nalpha body", skill.Content);
            Assert.Contains("## Gamma\ngamma body", skill.Content);
            Assert.DoesNotContain("beta body", skill.Content);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_ReplaceSection_MissingHeading_Fails()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (_, manager, skill) = NewSkillStore(dir, "# Skill\n\n## Alpha\nbody");
            var result = manager.ManageSkills("update", identifier: skill.Name, updateMode: "replaceSection",
                editOldString: "## Missing", editNewString: "## Missing\nnew");

            Assert.False(result.DataChanged);
            Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("# Skill\n\n## Alpha\nbody", skill.Content);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_ReplaceFullContent_StillWorks()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (_, manager, skill) = NewSkillStore(dir, "old body");
            var result = manager.ManageSkills("update", identifier: skill.Name, content: "brand new body");

            Assert.True(result.DataChanged);
            Assert.Equal("brand new body", skill.Content);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_ReplaceWithEmptyContent_Fails()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (_, manager, skill) = NewSkillStore(dir, "old body");
            var result = manager.ManageSkills("update", identifier: skill.Name, content: "   ");

            Assert.False(result.DataChanged);
            Assert.Contains("cannot be empty", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("old body", skill.Content);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_Create_Oversize_WarnsAbove12Kb()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var data = new AppData();
            var store = new DataStore(data, dir);
            var manager = new LumiFeatureManager(store);

            var result = manager.ManageSkills("create", name: "Big Skill", content: new string('a', 13_000));

            Assert.True(result.DataChanged);
            Assert.NotNull(result.SkillContentBytes);
            Assert.True(result.SkillContentBytes > 12_288);
            Assert.Contains("⚠", result.Message);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_Create_BelowThreshold_NoOversizeWarning()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var data = new AppData();
            var store = new DataStore(data, dir);
            var manager = new LumiFeatureManager(store);

            var result = manager.ManageSkills("create", name: "Small Skill", content: "a short body");

            Assert.True(result.DataChanged);
            Assert.True(result.SkillContentBytes < 12_288);
            Assert.DoesNotContain("⚠", result.Message);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_Update_CreatesBackupBeforeOverwrite()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (store, manager, skill) = NewSkillStore(dir, "original body");
            var result = manager.ManageSkills("update", identifier: skill.Name, updateMode: "append",
                editNewString: "added");

            Assert.True(result.DataChanged);
            Assert.Equal("original body\nadded", skill.Content);

            var backups = Directory.GetFiles(store.SkillBackupsDirectory, $"{skill.Id}-*.md");
            Assert.Single(backups);
            Assert.Equal("original body", File.ReadAllText(backups[0]));
            // Backup must be byte-clean (no UTF-8 BOM), else re-import/round-trip would break.
            Assert.Equal(Encoding.UTF8.GetBytes("original body"), File.ReadAllBytes(backups[0]));
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_Backup_PrunesToTen()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (store, manager, skill) = NewSkillStore(dir, "v0");
            for (var i = 1; i <= 12; i++)
            {
                var result = manager.ManageSkills("update", identifier: skill.Name, updateMode: "append",
                    editNewString: "line" + i);
                Assert.True(result.DataChanged);
            }

            var backups = Directory.GetFiles(store.SkillBackupsDirectory, $"{skill.Id}-*.md");
            Assert.Equal(10, backups.Length);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_Import_RoundTripsOnDiskMarkdown()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (store, manager, skill) = NewSkillStore(dir, "old body");
            store.SyncSkillFiles();

            var mdPath = store.GetSkillFilePath(skill.Name);
            File.WriteAllText(mdPath,
                "---\nname: Renamed Skill\ndescription: New desc\n---\n\n# Imported\n\nfresh body from disk");

            var result = manager.ManageSkills("import", identifier: skill.Id.ToString());

            Assert.True(result.DataChanged);
            Assert.Equal("Renamed Skill", skill.Name);
            Assert.Equal("New desc", skill.Description);
            Assert.Equal("# Imported\n\nfresh body from disk", skill.Content);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_Import_MalformedFrontmatter_Fails()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (store, manager, skill) = NewSkillStore(dir, "old body");
            store.SyncSkillFiles();

            File.WriteAllText(store.GetSkillFilePath(skill.Name), "no frontmatter here\njust text");
            var result = manager.ManageSkills("import", identifier: skill.Id.ToString());

            Assert.False(result.DataChanged);
            Assert.Contains("frontmatter", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("old body", skill.Content);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_Mutation_MirrorMatchesContentByteForByte()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (store, manager, skill) = NewSkillStore(dir, "initial");
            manager.ManageSkills("update", identifier: skill.Name, updateMode: "patch",
                editOldString: "initial", editNewString: "# Header\n\npatched body text");
            store.SyncSkillFiles();

            var mirror = File.ReadAllText(store.GetSkillFilePath(skill.Name));
            Assert.EndsWith(skill.Content, mirror, StringComparison.Ordinal);
            Assert.Equal(DataStore.BuildSkillMarkdown(skill), mirror);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_Update_VerificationPayloadMatchesSavedContent()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (_, manager, skill) = NewSkillStore(dir, "abc");
            var result = manager.ManageSkills("update", identifier: skill.Name, updateMode: "append",
                editNewString: "def");

            var expectedBytes = Encoding.UTF8.GetByteCount(skill.Content);
            var expectedHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(skill.Content)), 0, 4).ToLowerInvariant();

            Assert.Equal(expectedBytes, result.SkillContentBytes);
            Assert.Equal(expectedHash, result.SkillContentHash);
            Assert.Contains("sha256:" + expectedHash, result.Message);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_Patch_BuiltInSkill_Persists()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (_, manager, skill) = NewSkillStore(dir, "builtin body", builtIn: true);
            var result = manager.ManageSkills("update", identifier: skill.Name, updateMode: "patch",
                editOldString: "builtin", editNewString: "BUILTIN");

            Assert.True(result.DataChanged);
            Assert.Equal("BUILTIN body", skill.Content);
            Assert.True(skill.IsBuiltIn);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_Patch_PreservesCrlfLineEndings()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (_, manager, skill) = NewSkillStore(dir, "line1\r\nTARGET\r\nline3");
            var result = manager.ManageSkills("update", identifier: skill.Name, updateMode: "patch",
                editOldString: "TARGET", editNewString: "swapped");

            Assert.True(result.DataChanged);
            Assert.Equal("line1\r\nswapped\r\nline3", skill.Content);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_RenameAndPatch_ProducesSingleCorrectMirrorFile()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (store, manager, skill) = NewSkillStore(dir, "aaa TARGET bbb", name: "Old Name");
            store.SyncSkillFiles();

            var result = manager.ManageSkills("update", identifier: skill.Id.ToString(), name: "New Name",
                updateMode: "patch", editOldString: "TARGET", editNewString: "DONE");

            Assert.True(result.DataChanged);
            Assert.Equal("New Name", skill.Name);
            Assert.Equal("aaa DONE bbb", skill.Content);

            store.SyncSkillFiles();
            var mdFiles = Directory.GetFiles(dir, "*.md");
            Assert.Single(mdFiles);
            Assert.Equal("New Name.md", Path.GetFileName(mdFiles[0]));
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_Create_WithPatchMode_Fails()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var data = new AppData();
            var store = new DataStore(data, dir);
            var manager = new LumiFeatureManager(store);

            var result = manager.ManageSkills("create", name: "New", content: "body", updateMode: "patch",
                editOldString: "x", editNewString: "y");

            Assert.False(result.DataChanged);
            Assert.Contains("existing skill", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(data.Skills);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_RenameThenFailedPatch_LeavesSkillUnchanged()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (_, manager, skill) = NewSkillStore(dir, "original body", name: "Old Skill");

            var result = manager.ManageSkills("update", identifier: "Old Skill", name: "New Skill",
                updateMode: "patch", editOldString: "missing", editNewString: "replacement");

            Assert.False(result.DataChanged);
            Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);
            // The failed content edit must not leave a half-applied rename behind.
            Assert.Equal("Old Skill", skill.Name);
            Assert.Equal("original body", skill.Content);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_ReplaceSection_DuplicateHeading_Fails()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var content = "# Skill\n\n## Steps\nfirst\n\n## Steps\nsecond";
            var (_, manager, skill) = NewSkillStore(dir, content);

            var result = manager.ManageSkills("update", identifier: skill.Name, updateMode: "replaceSection",
                editOldString: "## Steps", editNewString: "## Steps\nreplaced");

            Assert.False(result.DataChanged);
            Assert.Contains("matched 2 times", result.Message);
            Assert.Equal(content, skill.Content);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_Update_CollidingSanitizedFileName_Fails()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var existing = new Skill { Id = Guid.NewGuid(), Name = "a_b", Description = "d", Content = "one" };
            var target = new Skill { Id = Guid.NewGuid(), Name = "Other", Description = "d", Content = "two" };
            var data = new AppData();
            data.Skills.Add(existing);
            data.Skills.Add(target);
            var store = new DataStore(data, dir);
            var manager = new LumiFeatureManager(store);

            // "a/b" is a different display name but sanitizes to the same "a_b.md" mirror file.
            var result = manager.ManageSkills("update", identifier: target.Id.ToString(), name: "a/b");

            Assert.False(result.DataChanged);
            Assert.Contains("mirror file", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Other", target.Name);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_Update_NameWithLineBreak_Fails()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (_, manager, skill) = NewSkillStore(dir, "body", name: "Clean Name");

            var result = manager.ManageSkills("update", identifier: skill.Id.ToString(), name: "Bad\nName");

            Assert.False(result.DataChanged);
            Assert.Contains("line breaks", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Clean Name", skill.Name);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_RenameThenBackupFailure_LeavesSkillUnchanged()
    {
        var dir = NewTempSkillsDir();
        try
        {
            Directory.CreateDirectory(dir);
            var (store, manager, skill) = NewSkillStore(dir, "original body", name: "Old Skill");

            // Force BackupSkillContent to throw: put a FILE where the .backups directory must be,
            // so Directory.CreateDirectory(backupsDir) fails with an IOException.
            File.WriteAllText(store.SkillBackupsDirectory, "not a directory");

            var result = manager.ManageSkills("update", identifier: "Old Skill", name: "New Skill",
                content: "replacement body");

            Assert.False(result.DataChanged);
            Assert.Contains("back up", result.Message, StringComparison.OrdinalIgnoreCase);
            // Backup ran before any mutation, so the failed backup must leave name AND content intact.
            Assert.Equal("Old Skill", skill.Name);
            Assert.Equal("original body", skill.Content);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_Patch_OverlappingMatch_Fails()
    {
        var dir = NewTempSkillsDir();
        try
        {
            // "aa" occurs at index 0 and 1 in "aaa" — overlapping, hence ambiguous, not unique.
            var (_, manager, skill) = NewSkillStore(dir, "aaa");
            var result = manager.ManageSkills("update", identifier: skill.Name, updateMode: "patch",
                editOldString: "aa", editNewString: "X");

            Assert.False(result.DataChanged);
            Assert.Contains("matched 2 times", result.Message);
            Assert.Equal("aaa", skill.Content);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_ReplaceSection_IgnoresHeadingInsideFencedCode()
    {
        var dir = NewTempSkillsDir();
        try
        {
            // The "## Beta" inside the fenced code block must not be treated as a section boundary,
            // so replacing "## Alpha" must consume the fenced sample and stop at the real "## Gamma".
            var content = "# Skill\n\n## Alpha\nalpha body\n\n```md\n## Beta\nfenced sample\n```\n\n## Gamma\ngamma body";
            var (_, manager, skill) = NewSkillStore(dir, content);
            var result = manager.ManageSkills("update", identifier: skill.Name, updateMode: "replaceSection",
                editOldString: "## Alpha", editNewString: "## Alpha\nreplaced alpha");

            Assert.True(result.DataChanged);
            Assert.Equal("# Skill\n\n## Alpha\nreplaced alpha\n## Gamma\ngamma body", skill.Content);
            Assert.DoesNotContain("alpha body", skill.Content);
            Assert.DoesNotContain("fenced sample", skill.Content);
            Assert.Contains("## Gamma\ngamma body", skill.Content);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_ReplaceSection_DoesNotTreatFourSpaceIndentedFenceAsFence()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var content = "# Skill\n\n## Alpha\nalpha body\n    ```\n## Beta\nbeta body\n```\n\n## Gamma\ngamma body";
            var (_, manager, skill) = NewSkillStore(dir, content);

            var result = manager.ManageSkills("update", identifier: skill.Name, updateMode: "replaceSection",
                editOldString: "## Alpha", editNewString: "## Alpha\nreplaced alpha");

            Assert.True(result.DataChanged);
            Assert.Contains("## Beta\nbeta body", skill.Content);
            Assert.Contains("## Gamma\ngamma body", skill.Content);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void ManageSkills_FrontmatterScalars_RoundTripThroughMirror()
    {
        var dir = NewTempSkillsDir();
        try
        {
            // Values that would corrupt unquoted YAML: '#' comment, ':' mapping, trailing space.
            var (store, manager, skill) = NewSkillStore(dir, "body text", name: "# Triage");
            manager.ManageSkills("update", identifier: skill.Id.ToString(), description: "Use: tools now ");
            store.SyncSkillFiles();

            var import = manager.ManageSkills("import", identifier: skill.Id.ToString());

            Assert.True(import.DataChanged);
            Assert.Equal("# Triage", skill.Name);
            Assert.Equal("Use: tools now", skill.Description);
            Assert.Equal("body text", skill.Content);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void DataStore_EncodeDecodeYamlScalar_RoundTrips()
    {
        foreach (var value in new[] { "Word Creator", "# Triage", "Use: tools", "  spaced  ", "has \"quote\"", "back\\slash", "true", "null", "123", "2026-07-12" })
        {
            Assert.StartsWith("\"", DataStore.EncodeYamlScalar(value));
            Assert.Equal(value, DataStore.DecodeYamlScalar(DataStore.EncodeYamlScalar(value)));
        }

        Assert.Equal("AB", DataStore.DecodeYamlScalar("\"A\\u0042\""));
    }

    [Fact]
    public void ManageSkills_Import_NormalizesQuotedNameAndDescription()
    {
        var dir = NewTempSkillsDir();
        try
        {
            var (store, manager, skill) = NewSkillStore(dir, "old body", name: "Old Name");
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                store.GetSkillFilePath(skill.Name),
                "---\nname: \"  Foo  \"\ndescription: \"  useful description  \"\n---\n\nnew body");

            var result = manager.ManageSkills("import", identifier: skill.Id.ToString());

            Assert.True(result.DataChanged);
            Assert.Equal("Foo", skill.Name);
            Assert.Equal("useful description", skill.Description);
            Assert.Equal("new body", skill.Content);
        }
        finally { CleanupDir(dir); }
    }
}
