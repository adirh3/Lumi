using System.Reflection;
using System.Collections.Generic;
using System.Text.Json;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class AppDataSnapshotFactoryTests
{
    [Fact]
    public void CreateIndexSnapshot_PreservesSettingsReasoningEffort()
    {
        var source = new AppData
        {
            Settings = new UserSettings
            {
                PreferredModel = "gpt-5.4",
                ReasoningEffort = "high"
            }
        };

        var snapshot = InvokeCreateIndexSnapshot(source);

        Assert.Equal("gpt-5.4", snapshot.Settings.PreferredModel);
        Assert.Equal("high", snapshot.Settings.ReasoningEffort);
    }

    [Fact]
    public void CreateIndexSnapshot_PreservesUseMcpProxy()
    {
        var source = new AppData { Settings = new UserSettings { UseMcpProxy = true } };

        var snapshot = InvokeCreateIndexSnapshot(source);

        Assert.True(snapshot.Settings.UseMcpProxy);
    }

    [Fact]
    public void AppDataJsonContext_SerializesUseMcpProxy()
    {
        var data = new AppData { Settings = new UserSettings { UseMcpProxy = true } };

        var json = JsonSerializer.Serialize(data, AppDataJsonContext.Default.AppData);
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement
            .GetProperty("settings")
            .GetProperty("useMcpProxy")
            .GetBoolean());
    }

    [Fact]
    public void AppDataJsonContext_SerializesSettingsReasoningEffort()
    {
        var data = new AppData
        {
            Settings = new UserSettings
            {
                PreferredModel = "gpt-5.4",
                ReasoningEffort = "medium"
            }
        };

        var json = JsonSerializer.Serialize(data, AppDataJsonContext.Default.AppData);
        using var document = JsonDocument.Parse(json);

        Assert.Equal("medium", document.RootElement
            .GetProperty("settings")
            .GetProperty("reasoningEffort")
            .GetString());
    }

    [Fact]
    public void CreateIndexSnapshot_PreservesSessionProviderSignature()
    {
        var source = new AppData
        {
            Chats =
            [
                new Chat
                {
                    CopilotSessionId = "session-1",
                    SessionProviderSignature = "provider-signature-1",
                }
            ]
        };

        var snapshot = InvokeCreateIndexSnapshot(source);

        Assert.Equal("provider-signature-1", Assert.Single(snapshot.Chats).SessionProviderSignature);
    }

    [Fact]
    public void AppDataJsonContext_RoundTripsSessionProviderSignature()
    {
        var source = new AppData
        {
            Chats = [new Chat { SessionProviderSignature = "provider-signature-1" }]
        };

        var json = JsonSerializer.Serialize(source, AppDataJsonContext.Default.AppData);
        var restored = JsonSerializer.Deserialize(json, AppDataJsonContext.Default.AppData);

        Assert.NotNull(restored);
        Assert.Equal("provider-signature-1", Assert.Single(restored.Chats).SessionProviderSignature);
    }

    [Fact]
    public void AppDataJsonContext_SerializesMemoryScopeAndMaintenanceFields()
    {
        var projectId = Guid.NewGuid();
        var data = new AppData
        {
            Settings = new UserSettings
            {
                EnableMemoryAutoMaintenance = false
            },
            Memories =
            [
                new Memory
                {
                    Key = "Lumi UI convention",
                    Content = "In Lumi, always use Strata controls for chat UI.",
                    Category = "Project",
                    Scope = MemoryScopes.Project,
                    ProjectId = projectId,
                    Status = MemoryStatuses.Active,
                    Confidence = 88,
                    MaintenanceNote = "Reviewed"
                }
            ]
        };

        var json = JsonSerializer.Serialize(data, AppDataJsonContext.Default.AppData);
        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.GetProperty("settings").GetProperty("enableMemoryAutoMaintenance").GetBoolean());
        var memory = document.RootElement.GetProperty("memories")[0];
        Assert.Equal(MemoryScopes.Project, memory.GetProperty("scope").GetString());
        Assert.Equal(projectId, memory.GetProperty("projectId").GetGuid());
        Assert.Equal(MemoryStatuses.Active, memory.GetProperty("status").GetString());
        Assert.Equal(88, memory.GetProperty("confidence").GetInt32());
        Assert.Equal("Reviewed", memory.GetProperty("maintenanceNote").GetString());
    }

    [Fact]
    public void AppDataJsonContext_SerializesProjectAdditionalContextDirectories()
    {
        var data = new AppData
        {
            Projects =
            [
                new Project
                {
                    Name = "Multi folder project",
                    WorkingDirectory = @"C:\Repo",
                    AdditionalContextDirectories = [@"C:\SharedSkills", @"D:\McpConfigs"]
                }
            ]
        };

        var json = JsonSerializer.Serialize(data, AppDataJsonContext.Default.AppData);
        using var document = JsonDocument.Parse(json);

        var project = document.RootElement.GetProperty("projects")[0];
        Assert.Equal(@"C:\SharedSkills", project.GetProperty("additionalContextDirectories")[0].GetString());
        Assert.Equal(@"D:\McpConfigs", project.GetProperty("additionalContextDirectories")[1].GetString());
    }

    [Fact]
    public void CreateIndexSnapshot_PreservesProjectAdditionalContextDirectories()
    {
        var source = new AppData
        {
            Projects =
            [
                new Project
                {
                    Name = "Multi folder project",
                    WorkingDirectory = @"C:\Repo",
                    AdditionalContextDirectories = [@"C:\SharedSkills", @"D:\McpConfigs"]
                }
            ]
        };

        var snapshot = InvokeCreateIndexSnapshot(source);

        var project = Assert.Single(snapshot.Projects);
        Assert.Equal([@"C:\SharedSkills", @"D:\McpConfigs"], project.AdditionalContextDirectories);
        Assert.NotSame(source.Projects[0].AdditionalContextDirectories, project.AdditionalContextDirectories);
    }

    [Fact]
    public void CreateIndexSnapshot_PreservesAgentToolSelectionState()
    {
        var source = new AppData
        {
            Agents =
            [
                new LumiAgent
                {
                    Name = "Prompt-only Lumi",
                    HasExplicitToolSelection = true
                }
            ]
        };

        var snapshot = InvokeCreateIndexSnapshot(source);

        var agent = Assert.Single(snapshot.Agents);
        Assert.True(agent.HasExplicitToolSelection);
        Assert.True(agent.HasToolRestrictions);
        Assert.Empty(agent.ToolNames);
    }

    [Fact]
    public void AppDataJsonContext_SerializesAgentToolSelectionState()
    {
        var data = new AppData
        {
            Agents =
            [
                new LumiAgent
                {
                    Name = "Prompt-only Lumi",
                    HasExplicitToolSelection = true
                }
            ]
        };

        var json = JsonSerializer.Serialize(data, AppDataJsonContext.Default.AppData);
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement
            .GetProperty("agents")[0]
            .GetProperty("hasExplicitToolSelection")
            .GetBoolean());
    }

    [Fact]
    public void CreateIndexSnapshot_PreservesByokModelAdvancedSettings()
    {
        // Regression: the BYOK Advanced fields (token limits + RPM) were omitted from the
        // ByokModel clone in CreateIndexSnapshot, so every save dropped them and data.json
        // always showed null even though the in-memory model held the value.
        var source = new AppData
        {
            Settings = new UserSettings
            {
                ByokModels =
                [
                    new ByokModel
                    {
                        ModelId = "gpt-test",
                        DisplayName = "Test",
                        MaxOutputTokens = 4096,
                        MaxPromptTokens = 128000,
                        MaxRequestsPerMinute = 60
                    }
                ]
            }
        };

        var snapshot = InvokeCreateIndexSnapshot(source);

        var model = Assert.Single(snapshot.Settings.ByokModels);
        Assert.Equal(4096, model.MaxOutputTokens);
        Assert.Equal(128000, model.MaxPromptTokens);
        Assert.Equal(60, model.MaxRequestsPerMinute);
    }

    [Fact]
    public void CreateIndexSnapshot_PreservesMemoryMaintenanceFields()
    {
        var projectId = Guid.NewGuid();
        var reviewedAt = new DateTimeOffset(2026, 4, 29, 10, 0, 0, TimeSpan.Zero);
        var source = new AppData
        {
            Settings = new UserSettings
            {
                EnableMemoryAutoMaintenance = false
            },
            Memories =
            [
                new Memory
                {
                    Key = "Lumi UI convention",
                    Content = "In Lumi, always use Strata controls for chat UI.",
                    Category = "Project",
                    Scope = MemoryScopes.Project,
                    ProjectId = projectId,
                    Status = MemoryStatuses.Archived,
                    LastReviewedAt = reviewedAt,
                    LastUsedAt = reviewedAt,
                    Confidence = 42,
                    MaintenanceNote = "Archived"
                }
            ]
        };

        var snapshot = InvokeCreateIndexSnapshot(source);

        Assert.False(snapshot.Settings.EnableMemoryAutoMaintenance);
        var memory = Assert.Single(snapshot.Memories);
        Assert.Equal(MemoryScopes.Project, memory.Scope);
        Assert.Equal(projectId, memory.ProjectId);
        Assert.Equal(MemoryStatuses.Archived, memory.Status);
        Assert.Equal(reviewedAt, memory.LastReviewedAt);
        Assert.Equal(reviewedAt, memory.LastUsedAt);
        Assert.Equal(42, memory.Confidence);
        Assert.Equal("Archived", memory.MaintenanceNote);
    }

    [Fact]
    public void AppDataJsonContext_SerializesChatReasoningEffort()
    {
        var data = new AppData
        {
            Chats =
            [
                CreateChat(
                    Guid.NewGuid(),
                    title: "Serializer check",
                    copilotSessionId: null,
                    updatedAt: new DateTimeOffset(2026, 3, 31, 18, 30, 0, TimeSpan.Zero),
                    lastModelUsed: "gpt-5.4",
                    lastReasoningEffortUsed: "medium")
            ]
        };

        var json = JsonSerializer.Serialize(data, AppDataJsonContext.Default.AppData);
        using var document = JsonDocument.Parse(json);

        var chat = document.RootElement.GetProperty("chats")[0];
        Assert.Equal("medium", chat.GetProperty("lastReasoningEffortUsed").GetString());
    }

    [Fact]
    public void CreateIndexSnapshot_AndJsonContext_PreserveChatPinState()
    {
        var source = new AppData
        {
            Chats =
            [
                CreateChat(
                    Guid.NewGuid(),
                    title: "Pinned chat",
                    copilotSessionId: null,
                    updatedAt: new DateTimeOffset(2026, 7, 19, 17, 0, 0, TimeSpan.Zero),
                    isPinned: true)
            ]
        };

        var snapshot = InvokeCreateIndexSnapshot(source);
        Assert.True(Assert.Single(snapshot.Chats).IsPinned);

        var json = JsonSerializer.Serialize(snapshot, AppDataJsonContext.Default.AppData);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("chats")[0].GetProperty("isPinned").GetBoolean());
    }

    [Fact]
    public void AppDataJsonContext_SerializesChatContextUsage()
    {
        var data = new AppData
        {
            Chats =
            [
                CreateChat(
                    Guid.NewGuid(),
                    title: "Context usage check",
                    copilotSessionId: null,
                    updatedAt: new DateTimeOffset(2026, 3, 31, 18, 30, 0, TimeSpan.Zero),
                    contextCurrentTokens: 42_000,
                    contextTokenLimit: 128_000)
            ]
        };

        var json = JsonSerializer.Serialize(data, AppDataJsonContext.Default.AppData);
        using var document = JsonDocument.Parse(json);

        var chat = document.RootElement.GetProperty("chats")[0];
        Assert.Equal(42_000, chat.GetProperty("contextCurrentTokens").GetInt64());
        Assert.Equal(128_000, chat.GetProperty("contextTokenLimit").GetInt64());
    }

    [Fact]
    public void AppDataJsonContext_SerializesChatFollowUpSuggestions()
    {
        var assistantMessageId = Guid.NewGuid();
        var data = new AppData
        {
            Chats =
            [
                CreateChat(
                    Guid.NewGuid(),
                    title: "Suggestion check",
                    copilotSessionId: null,
                    updatedAt: new DateTimeOffset(2026, 4, 27, 17, 0, 0, TimeSpan.Zero),
                    followUpSuggestions: ["Run code review", "Push changes"],
                    followUpSuggestionAssistantMessageId: assistantMessageId)
            ]
        };

        var json = JsonSerializer.Serialize(data, AppDataJsonContext.Default.AppData);
        using var document = JsonDocument.Parse(json);

        var chat = document.RootElement.GetProperty("chats")[0];
        Assert.Equal("Run code review", chat.GetProperty("followUpSuggestions")[0].GetString());
        Assert.Equal("Push changes", chat.GetProperty("followUpSuggestions")[1].GetString());
        Assert.Equal(assistantMessageId, chat.GetProperty("followUpSuggestionAssistantMessageId").GetGuid());
    }

    [Fact]
    public void AppDataJsonContext_DeserializesMissingExternalSkillNamesAsEmptyList()
    {
        var chatId = Guid.NewGuid();
        var json = $$"""
            {
              "settings": {},
              "chats": [
                {
                  "id": "{{chatId}}",
                  "title": "Legacy chat",
                  "activeSkillIds": [],
                  "activeMcpServerNames": []
                }
              ],
              "projects": [],
              "skills": [],
              "agents": [],
              "mcpServers": [],
              "memories": []
            }
            """;

        var data = JsonSerializer.Deserialize(json, AppDataJsonContext.Default.AppData);

        var chat = Assert.Single(data!.Chats);
        Assert.NotNull(chat.ActiveExternalSkillNames);
        Assert.Empty(chat.ActiveExternalSkillNames);
        Assert.NotNull(chat.FollowUpSuggestions);
        Assert.Empty(chat.FollowUpSuggestions);
    }

    [Fact]
    public void AppDataJsonContext_SerializesBackgroundJobs()
    {
        var chatId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var data = new AppData
        {
            BackgroundJobs =
            [
                new BackgroundJob
                {
                    Id = jobId,
                    ChatId = chatId,
                    Name = "Hotel monitor",
                    Prompt = "Watch London hotel prices.",
                    TriggerType = BackgroundJobTriggerTypes.Time,
                    ScheduleType = BackgroundJobScheduleTypes.Daily,
                    DailyTime = "08:00",
                    IsEnabled = true
                }
            ]
        };

        var json = JsonSerializer.Serialize(data, AppDataJsonContext.Default.AppData);
        using var document = JsonDocument.Parse(json);

        var job = document.RootElement.GetProperty("backgroundJobs")[0];
        Assert.Equal(jobId, job.GetProperty("id").GetGuid());
        Assert.Equal("Hotel monitor", job.GetProperty("name").GetString());
    }

    [Fact]
    public void CreateIndexSnapshot_PreservesBackgroundJobs()
    {
        var chatId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var source = new AppData
        {
            BackgroundJobs =
            [
                new BackgroundJob
                {
                    Id = jobId,
                    ChatId = chatId,
                    Name = "PR watcher",
                    Description = "Wait for a PR check.",
                    Prompt = "Wake when CI finishes.",
                    TriggerType = BackgroundJobTriggerTypes.Script,
                    ScriptContent = "Write-Output done",
                    ScriptLanguage = BackgroundJobScriptLanguages.PowerShell,
                    IsEnabled = true,
                    IsTemporary = true,
                    LastRunStatus = BackgroundJobRunStatuses.Watching,
                    LastRunSummary = "Watching...",
                    RunCount = 2
                }
            ]
        };

        var snapshot = InvokeCreateIndexSnapshot(source);

        var job = Assert.Single(snapshot.BackgroundJobs);
        Assert.Equal(jobId, job.Id);
        Assert.Equal(chatId, job.ChatId);
        Assert.Equal("PR watcher", job.Name);
        Assert.Equal(BackgroundJobTriggerTypes.Script, job.TriggerType);
        Assert.Equal("Write-Output done", job.ScriptContent);
        Assert.Equal(2, job.RunCount);
    }

    [Fact]
    public void MergeChatIndexChanges_PreservesPersistedChat_WhenLocalChatWasNotMarkedDirty()
    {
        var chatId = Guid.NewGuid();
        var currentSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(
                    chatId,
                    title: "Stale local",
                    copilotSessionId: "session-live",
                    updatedAt: new DateTimeOffset(2026, 3, 20, 11, 0, 0, TimeSpan.Zero))
            ]
        };
        var persistedSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(
                    chatId,
                    title: "Recovered persisted",
                    copilotSessionId: null,
                    updatedAt: new DateTimeOffset(2026, 3, 20, 11, 5, 0, TimeSpan.Zero))
            ]
        };

        var merged = InvokeMergeChatIndexChanges(currentSnapshot, persistedSnapshot, [], []);

        var chat = Assert.Single(merged.Chats);
        Assert.Equal("Recovered persisted", chat.Title);
        Assert.Null(chat.CopilotSessionId);
        Assert.Equal(persistedSnapshot.Chats[0].UpdatedAt, chat.UpdatedAt);
    }

    [Fact]
    public void MergeChatIndexChanges_PreservesDirtyLocalChat_WhenItIsNewerThanPersisted()
    {
        var chatId = Guid.NewGuid();
        var currentSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(
                    chatId,
                    title: "Recovered locally",
                    copilotSessionId: null,
                    updatedAt: new DateTimeOffset(2026, 3, 20, 11, 5, 0, TimeSpan.Zero),
                    skillIds: [Guid.NewGuid()],
                    mcpServers: ["new-mcp"],
                    lastModelUsed: "gpt-5.4",
                    lastReasoningEffortUsed: "high",
                    totalInputTokens: 100,
                    totalOutputTokens: 200,
                    contextCurrentTokens: 90,
                    contextTokenLimit: 1_000,
                    planContent: "updated plan",
                    followUpSuggestions: ["Run code review", "Push changes"],
                    followUpSuggestionAssistantMessageId: chatId)
            ]
        };
        var persistedSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(
                    chatId,
                    title: "Stale persisted",
                    copilotSessionId: "stale-session",
                    updatedAt: new DateTimeOffset(2026, 3, 20, 11, 0, 0, TimeSpan.Zero),
                    skillIds: [Guid.NewGuid()],
                    mcpServers: ["old-mcp"],
                    lastModelUsed: "claude-opus-4.6-1m",
                    lastReasoningEffortUsed: "low",
                    totalInputTokens: 10,
                    totalOutputTokens: 20,
                    planContent: "stale plan")
            ]
        };

        var merged = InvokeMergeChatIndexChanges(currentSnapshot, persistedSnapshot, [chatId], []);

        var chat = Assert.Single(merged.Chats);
        Assert.Equal("Recovered locally", chat.Title);
        Assert.Null(chat.CopilotSessionId);
        Assert.Equal(currentSnapshot.Chats[0].UpdatedAt, chat.UpdatedAt);
        Assert.Equal(currentSnapshot.Chats[0].ActiveSkillIds, chat.ActiveSkillIds);
        Assert.Equal(currentSnapshot.Chats[0].ActiveMcpServerNames, chat.ActiveMcpServerNames);
        Assert.Equal(currentSnapshot.Chats[0].HasExplicitMcpServerSelection, chat.HasExplicitMcpServerSelection);
        Assert.Equal("gpt-5.4", chat.LastModelUsed);
        Assert.Equal("high", chat.LastReasoningEffortUsed);
        Assert.Equal(100, chat.TotalInputTokens);
        Assert.Equal(200, chat.TotalOutputTokens);
        Assert.Equal(90, chat.ContextCurrentTokens);
        Assert.Equal(1_000, chat.ContextTokenLimit);
        Assert.Equal("updated plan", chat.PlanContent);
        Assert.Equal(["Run code review", "Push changes"], chat.FollowUpSuggestions);
        Assert.Equal(chatId, chat.FollowUpSuggestionAssistantMessageId);
    }

    [Fact]
    public void MergeChatIndexChanges_KeepsNewerPersistedChat_WhenDirtyLocalChatIsOlder()
    {
        var chatId = Guid.NewGuid();
        var currentSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(
                    chatId,
                    title: "Older local",
                    copilotSessionId: "stale-session",
                    updatedAt: new DateTimeOffset(2026, 3, 20, 11, 0, 0, TimeSpan.Zero),
                    totalInputTokens: 10,
                    totalOutputTokens: 20)
            ]
        };
        var persistedSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(
                    chatId,
                    title: "Recovered persisted",
                    copilotSessionId: null,
                    updatedAt: new DateTimeOffset(2026, 3, 20, 11, 5, 0, TimeSpan.Zero),
                    totalInputTokens: 100,
                    totalOutputTokens: 200,
                    contextCurrentTokens: 80,
                    contextTokenLimit: 1_000)
            ]
        };

        var merged = InvokeMergeChatIndexChanges(currentSnapshot, persistedSnapshot, [chatId], []);

        var chat = Assert.Single(merged.Chats);
        Assert.Equal("Recovered persisted", chat.Title);
        Assert.Null(chat.CopilotSessionId);
        Assert.Equal(persistedSnapshot.Chats[0].UpdatedAt, chat.UpdatedAt);
        Assert.Equal(100, chat.TotalInputTokens);
        Assert.Equal(200, chat.TotalOutputTokens);
        Assert.Equal(80, chat.ContextCurrentTokens);
        Assert.Equal(1_000, chat.ContextTokenLimit);
    }

    [Fact]
    public void MergeChatIndexChanges_RemovesDeletedChats()
    {
        var chatId = Guid.NewGuid();
        var currentSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(chatId, title: "Deleted locally", copilotSessionId: null, updatedAt: new DateTimeOffset(2026, 3, 20, 11, 5, 0, TimeSpan.Zero))
            ]
        };
        var persistedSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(chatId, title: "Still on disk", copilotSessionId: "session-live", updatedAt: new DateTimeOffset(2026, 3, 20, 11, 0, 0, TimeSpan.Zero))
            ]
        };

        var merged = InvokeMergeChatIndexChanges(currentSnapshot, persistedSnapshot, [], [chatId]);

        Assert.Empty(merged.Chats);
    }

    [Fact]
    public void MergeChatIndexChanges_PreservesNewDirtyLocalChats()
    {
        var newChatId = Guid.NewGuid();
        var currentSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(newChatId, title: "Brand new", copilotSessionId: "new-session", updatedAt: new DateTimeOffset(2026, 3, 20, 11, 5, 0, TimeSpan.Zero))
            ]
        };
        var persistedSnapshot = new AppData();

        var merged = InvokeMergeChatIndexChanges(currentSnapshot, persistedSnapshot, [newChatId], []);

        var chat = Assert.Single(merged.Chats);
        Assert.Equal(newChatId, chat.Id);
        Assert.Equal("Brand new", chat.Title);
    }

    [Fact]
    public void MergeChatIndexChanges_PreventsRecoveredShutdownFromBeingRevertedByStaleSnapshot()
    {
        var chatId = Guid.NewGuid();
        var staleCurrentSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(
                    chatId,
                    title: "Anniversary chat",
                    copilotSessionId: "old-session",
                    updatedAt: new DateTimeOffset(2026, 3, 20, 13, 49, 39, TimeSpan.Zero),
                    totalInputTokens: 10,
                    totalOutputTokens: 20)
            ]
        };
        var recoveredPersistedSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(
                    chatId,
                    title: "Anniversary chat",
                    copilotSessionId: null,
                    updatedAt: new DateTimeOffset(2026, 3, 20, 13, 49, 47, TimeSpan.Zero),
                    totalInputTokens: 100,
                    totalOutputTokens: 200)
            ]
        };

        var merged = InvokeMergeChatIndexChanges(staleCurrentSnapshot, recoveredPersistedSnapshot, [], []);

        var chat = Assert.Single(merged.Chats);
        Assert.Null(chat.CopilotSessionId);
        Assert.Equal(recoveredPersistedSnapshot.Chats[0].UpdatedAt, chat.UpdatedAt);
        Assert.Equal(100, chat.TotalInputTokens);
        Assert.Equal(200, chat.TotalOutputTokens);
    }

    [Fact]
    public void MergeChatIndexChanges_PreservesPersistedBackgroundJobs_WhenJobsWereNotChangedLocally()
    {
        var chatId = Guid.NewGuid();
        var persistedJob = CreateJob(chatId, "Persisted watcher");
        var currentSnapshot = new AppData
        {
            Chats = [CreateChat(chatId, "Chat", null, new DateTimeOffset(2026, 3, 20, 11, 0, 0, TimeSpan.Zero))]
        };
        var persistedSnapshot = new AppData
        {
            Chats = [CreateChat(chatId, "Chat", null, new DateTimeOffset(2026, 3, 20, 11, 0, 0, TimeSpan.Zero))],
            BackgroundJobs = [persistedJob]
        };

        var merged = InvokeMergeChatIndexChanges(currentSnapshot, persistedSnapshot, [], [], backgroundJobsDirty: false);

        var job = Assert.Single(merged.BackgroundJobs);
        Assert.Equal(persistedJob.Id, job.Id);
        Assert.Equal("Persisted watcher", job.Name);
    }

    [Fact]
    public void MergeChatIndexChanges_AllowsClearingBackgroundJobs_WhenJobsWereChangedLocally()
    {
        var chatId = Guid.NewGuid();
        var currentSnapshot = new AppData
        {
            Chats = [CreateChat(chatId, "Chat", null, new DateTimeOffset(2026, 3, 20, 11, 0, 0, TimeSpan.Zero))]
        };
        var persistedSnapshot = new AppData
        {
            Chats = [CreateChat(chatId, "Chat", null, new DateTimeOffset(2026, 3, 20, 11, 0, 0, TimeSpan.Zero))],
            BackgroundJobs = [CreateJob(chatId, "Deleted watcher")]
        };

        var merged = InvokeMergeChatIndexChanges(currentSnapshot, persistedSnapshot, [], [], backgroundJobsDirty: true);

        Assert.Empty(merged.BackgroundJobs);
    }

    private static Chat CreateChat(
        Guid id,
        string title,
        string? copilotSessionId,
        DateTimeOffset updatedAt,
        List<Guid>? skillIds = null,
        List<string>? mcpServers = null,
        string? lastModelUsed = null,
        string? lastReasoningEffortUsed = null,
        long totalInputTokens = 0,
        long totalOutputTokens = 0,
        long contextCurrentTokens = 0,
        long contextTokenLimit = 0,
        string? planContent = null,
        List<string>? followUpSuggestions = null,
        Guid? followUpSuggestionAssistantMessageId = null,
        bool isPinned = false)
    {
        return new Chat
        {
            Id = id,
            Title = title,
            CopilotSessionId = copilotSessionId,
            UpdatedAt = updatedAt,
            ActiveSkillIds = skillIds ?? [],
            ActiveMcpServerNames = mcpServers ?? [],
            HasExplicitMcpServerSelection = mcpServers is not null,
            LastModelUsed = lastModelUsed,
            LastReasoningEffortUsed = lastReasoningEffortUsed,
            TotalInputTokens = totalInputTokens,
            TotalOutputTokens = totalOutputTokens,
            ContextCurrentTokens = contextCurrentTokens,
            ContextTokenLimit = contextTokenLimit,
            PlanContent = planContent,
            FollowUpSuggestions = followUpSuggestions ?? [],
            FollowUpSuggestionAssistantMessageId = followUpSuggestionAssistantMessageId,
            IsPinned = isPinned
        };
    }

    private static BackgroundJob CreateJob(Guid chatId, string name)
    {
        return new BackgroundJob
        {
            Id = Guid.NewGuid(),
            ChatId = chatId,
            Name = name,
            Prompt = "Watch something.",
            TriggerType = BackgroundJobTriggerTypes.Time,
            ScheduleType = BackgroundJobScheduleTypes.Interval,
            IntervalMinutes = 60,
            IsEnabled = true,
            NextRunAt = new DateTimeOffset(2026, 3, 20, 12, 0, 0, TimeSpan.Zero)
        };
    }

    private static AppData InvokeCreateIndexSnapshot(AppData source)
    {
        var factoryType = typeof(DataStore).Assembly.GetType("Lumi.Services.AppDataSnapshotFactory")
            ?? throw new InvalidOperationException("AppDataSnapshotFactory type was not found.");
        var createMethod = factoryType.GetMethod(
            "CreateIndexSnapshot",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("CreateIndexSnapshot method was not found.");

        return (AppData)(createMethod.Invoke(null, [source])
            ?? throw new InvalidOperationException("CreateIndexSnapshot returned null."));
    }

    [Fact]
    public void CreateIndexSnapshot_DeepCopiesByokEndpointsAndModels()
    {
        var source = new AppData
        {
            Settings = new UserSettings
            {
                ByokEndpoints =
                [
                    new ByokEndpoint
                    {
                        Id = "ep1",
                        Name = "Endpoint 1",
                        ProviderType = "openai",
                        BaseUrl = "https://api.example.com/v1",
                        WireApi = "openai",
                        ApiKeyMode = ByokApiKeyMode.Stored,
                        ApiKeyEnvVar = "MY_KEY",
                        ApiKey = "secret",
                    }
                ],
                ByokModels =
                [
                    new ByokModel
                    {
                        Id = "mod1",
                        EndpointId = "ep1",
                        ModelId = "gpt-4o",
                        DisplayName = "My GPT",
                        IsEnabled = true,
                    }
                ],
            }
        };

        var snapshot = InvokeCreateIndexSnapshot(source);

        Assert.Single(snapshot.Settings.ByokEndpoints);
        Assert.Single(snapshot.Settings.ByokModels);
        var snapEp = snapshot.Settings.ByokEndpoints[0];
        var snapMod = snapshot.Settings.ByokModels[0];
        Assert.Equal("ep1", snapEp.Id);
        Assert.Equal("Endpoint 1", snapEp.Name);
        Assert.Equal("secret", snapEp.ApiKey);
        Assert.Equal(ByokApiKeyMode.Stored, snapEp.ApiKeyMode);
        Assert.Equal("mod1", snapMod.Id);
        Assert.Equal("ep1", snapMod.EndpointId);
        Assert.Equal("gpt-4o", snapMod.ModelId);
        Assert.Equal("My GPT", snapMod.DisplayName);

        // Mutating the snapshot must not affect the source.
        snapEp.Name = "Mutated";
        snapMod.DisplayName = "Mutated";
        Assert.Equal("Endpoint 1", source.Settings.ByokEndpoints[0].Name);
        Assert.Equal("My GPT", source.Settings.ByokModels[0].DisplayName);
    }

    [Fact]
    public void AppDataJsonContext_SerializesByokCollections()
    {
        var data = new AppData
        {
            Settings = new UserSettings
            {
                ByokEndpoints =
                [
                    new ByokEndpoint
                    {
                        Id = "ep1",
                        Name = "Endpoint 1",
                        ProviderType = "openai",
                        BaseUrl = "https://api.example.com/v1",
                        WireApi = "openai",
                    }
                ],
                ByokModels =
                [
                    new ByokModel
                    {
                        Id = "mod1",
                        EndpointId = "ep1",
                        ModelId = "gpt-4o",
                        DisplayName = "My GPT",
                    }
                ],
            }
        };

        var json = JsonSerializer.Serialize(data, AppDataJsonContext.Default.AppData);
        using var document = JsonDocument.Parse(json);

        var endpoints = document.RootElement
            .GetProperty("settings")
            .GetProperty("byokEndpoints");
        Assert.Equal(1, endpoints.GetArrayLength());
        Assert.Equal("ep1", endpoints[0].GetProperty("id").GetString());
        Assert.Equal("openai", endpoints[0].GetProperty("providerType").GetString());

        var models = document.RootElement
            .GetProperty("settings")
            .GetProperty("byokModels");
        Assert.Equal(1, models.GetArrayLength());
        Assert.Equal("mod1", models[0].GetProperty("id").GetString());
        Assert.Equal("gpt-4o", models[0].GetProperty("modelId").GetString());
    }

    private static AppData InvokeMergeChatIndexChanges(
        AppData currentSnapshot,
        AppData persistedSnapshot,
        IReadOnlyCollection<Guid> dirtyChatIds,
        IReadOnlyCollection<Guid> deletedChatIds,
        bool backgroundJobsDirty = false)
    {
        var factoryType = typeof(DataStore).Assembly.GetType("Lumi.Services.AppDataSnapshotFactory")
            ?? throw new InvalidOperationException("AppDataSnapshotFactory type was not found.");
        var mergeMethod = factoryType.GetMethod(
            "MergeChatIndexChanges",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("MergeChatIndexChanges method was not found.");
        return (AppData)(mergeMethod.Invoke(
            null,
            [currentSnapshot, persistedSnapshot, new HashSet<Guid>(dirtyChatIds), new HashSet<Guid>(deletedChatIds), backgroundJobsDirty])
            ?? throw new InvalidOperationException("MergeChatIndexChanges returned null."));
    }
}
