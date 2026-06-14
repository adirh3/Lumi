using System.Collections.Generic;
using System.Linq;
using Lumi.Models;

namespace Lumi.Services;

internal static class AppDataSnapshotFactory
{
    public static AppData CreateIndexSnapshot(AppData source)
    {
        var settings = source.Settings;

        return new AppData
        {
            Settings = new UserSettings
            {
                UserName = settings.UserName,
                UserSex = settings.UserSex,
                IsOnboarded = settings.IsOnboarded,
                DefaultsSeeded = settings.DefaultsSeeded,
                CodingLumiSeeded = settings.CodingLumiSeeded,
                Language = settings.Language,
                LaunchAtStartup = settings.LaunchAtStartup,
                StartMinimized = settings.StartMinimized,
                MinimizeToTray = settings.MinimizeToTray,
                GlobalHotkey = settings.GlobalHotkey,
                NotificationsEnabled = settings.NotificationsEnabled,
                DismissedUpdateBannerToken = settings.DismissedUpdateBannerToken,
                IsDarkTheme = settings.IsDarkTheme,
                IsCompactDensity = settings.IsCompactDensity,
                FontSize = settings.FontSize,
                ShowAnimations = settings.ShowAnimations,
                SendWithEnter = settings.SendWithEnter,
                ShowTimestamps = settings.ShowTimestamps,
                ShowToolCalls = settings.ShowToolCalls,
                ShowReasoning = settings.ShowReasoning,
                AutoGenerateTitles = settings.AutoGenerateTitles,
                PreferredModel = settings.PreferredModel,
                ReasoningEffort = settings.ReasoningEffort,
                UseMcpProxy = settings.UseMcpProxy,
                ContextWindowTier = settings.ContextWindowTier,
                EnableMemoryAutoSave = settings.EnableMemoryAutoSave,
                EnableMemoryAutoMaintenance = settings.EnableMemoryAutoMaintenance,
                AutoSaveChats = settings.AutoSaveChats,
                WindowWidth = settings.WindowWidth,
                WindowHeight = settings.WindowHeight,
                WindowLeft = settings.WindowLeft,
                WindowTop = settings.WindowTop,
                SidebarWidth = settings.SidebarWidth,
                SidebarCollapsed = settings.SidebarCollapsed,
                IsMaximized = settings.IsMaximized,
                HasImportedBrowserCookies = settings.HasImportedBrowserCookies,
            },
            Chats = source.Chats
                .Select(CloneChatIndex)
                .ToList(),
            Projects = source.Projects
                .Select(static p => new Project
                 {
                     Id = p.Id,
                     Name = p.Name,
                     Instructions = p.Instructions,
                     WorkingDirectory = p.WorkingDirectory,
                     AdditionalContextDirectories = [..p.AdditionalContextDirectories],
                     CreatedAt = p.CreatedAt
                 })
                 .ToList(),
            Skills = source.Skills
                .Select(static s => new Skill
                {
                    Id = s.Id,
                    Name = s.Name,
                    Description = s.Description,
                    Content = s.Content,
                    IconGlyph = s.IconGlyph,
                    IsBuiltIn = s.IsBuiltIn,
                    CreatedAt = s.CreatedAt
                })
                .ToList(),
            Agents = source.Agents
                .Select(static a => new LumiAgent
                {
                    Id = a.Id,
                    Name = a.Name,
                    Description = a.Description,
                    SystemPrompt = a.SystemPrompt,
                    IconGlyph = a.IconGlyph,
                    IsBuiltIn = a.IsBuiltIn,
                    IsLearningAgent = a.IsLearningAgent,
                    SkillIds = [..a.SkillIds],
                    ToolNames = [..a.ToolNames],
                    McpServerIds = [..a.McpServerIds],
                    CreatedAt = a.CreatedAt
                })
                .ToList(),
            McpServers = source.McpServers
                .Select(static s => new McpServer
                {
                    Id = s.Id,
                    Name = s.Name,
                    Description = s.Description,
                    ServerType = s.ServerType,
                    Command = s.Command,
                    Args = [..s.Args],
                    Env = new(s.Env),
                    Url = s.Url,
                    Headers = new(s.Headers),
                    Tools = [..s.Tools],
                    Timeout = s.Timeout,
                    IsEnabled = s.IsEnabled,
                    CreatedAt = s.CreatedAt
                })
                .ToList(),
            BackgroundJobs = source.BackgroundJobs
                .Select(CloneBackgroundJob)
                .ToList(),
            Memories = source.Memories
                .Select(static m => new Memory
                {
                    Id = m.Id,
                    Key = m.Key,
                    Content = m.Content,
                    Category = m.Category,
                    Scope = m.Scope,
                    ProjectId = m.ProjectId,
                    Status = m.Status,
                    Source = m.Source,
                    SourceChatId = m.SourceChatId,
                    CreatedAt = m.CreatedAt,
                    UpdatedAt = m.UpdatedAt,
                    LastReviewedAt = m.LastReviewedAt,
                    LastUsedAt = m.LastUsedAt,
                    Confidence = m.Confidence,
                    MaintenanceNote = m.MaintenanceNote
                })
                .ToList(),
            ReleaseServiceProfiles = source.ReleaseServiceProfiles
                .Select(CloneReleaseServiceProfile)
                .ToList(),
            ReleaseEvidencePackets = source.ReleaseEvidencePackets
                .Select(CloneReleaseEvidencePacket)
                .ToList(),
            ReleaseSafeFlyDrafts = source.ReleaseSafeFlyDrafts
                .Select(CloneReleaseSafeFlyDraft)
                .ToList(),
            ReleaseLeases = source.ReleaseLeases
                .Select(static lease => new ReleaseLease
                {
                    Id = lease.Id,
                    Service = lease.Service,
                    Scope = lease.Scope,
                    EligibilityState = lease.EligibilityState,
                    MonitoringState = lease.MonitoringState,
                    LifecycleState = lease.LifecycleState,
                    ExpiresAt = lease.ExpiresAt,
                    RevocationPath = lease.RevocationPath
                })
                .ToList(),
            ReleaseRiskSignals = source.ReleaseRiskSignals
                .Select(static signal => new ReleaseRiskSignal
                {
                    Id = signal.Id,
                    EvidencePacketId = signal.EvidencePacketId,
                    Service = signal.Service,
                    SignalType = signal.SignalType,
                    Scope = signal.Scope,
                    Value = signal.Value,
                    Source = signal.Source,
                    CapturedAt = signal.CapturedAt
                })
                .ToList(),
        };
    }

    public static AppData MergeChatIndexChanges(
        AppData currentSnapshot,
        AppData persistedSnapshot,
        ISet<Guid> dirtyChatIds,
        ISet<Guid> deletedChatIds,
        bool backgroundJobsDirty)
    {
        if (currentSnapshot.Chats.Count == 0 && persistedSnapshot.Chats.Count == 0)
        {
            if (!backgroundJobsDirty)
                currentSnapshot.BackgroundJobs = persistedSnapshot.BackgroundJobs.Select(CloneBackgroundJob).ToList();
            return currentSnapshot;
        }

        var currentChatsById = currentSnapshot.Chats.ToDictionary(static c => c.Id);
        var persistedChatIds = new HashSet<Guid>();
        var mergedChats = new List<Chat>(Math.Max(currentSnapshot.Chats.Count, persistedSnapshot.Chats.Count));

        foreach (var persistedChat in persistedSnapshot.Chats)
        {
            persistedChatIds.Add(persistedChat.Id);

            if (deletedChatIds.Contains(persistedChat.Id))
                continue;

            if (dirtyChatIds.Contains(persistedChat.Id)
                && currentChatsById.TryGetValue(persistedChat.Id, out var currentChat)
                && currentChat.UpdatedAt >= persistedChat.UpdatedAt)
            {
                mergedChats.Add(CloneChatIndex(currentChat));
                continue;
            }

            mergedChats.Add(CloneChatIndex(persistedChat));
        }

        foreach (var currentChat in currentSnapshot.Chats)
        {
            if (deletedChatIds.Contains(currentChat.Id)
                || persistedChatIds.Contains(currentChat.Id)
                || !dirtyChatIds.Contains(currentChat.Id))
            {
                continue;
            }

            mergedChats.Add(CloneChatIndex(currentChat));
        }

        currentSnapshot.Chats = mergedChats;
        if (!backgroundJobsDirty)
            currentSnapshot.BackgroundJobs = persistedSnapshot.BackgroundJobs.Select(CloneBackgroundJob).ToList();

        return currentSnapshot;
    }

    private static BackgroundJob CloneBackgroundJob(BackgroundJob source)
    {
        return new BackgroundJob
        {
            Id = source.Id,
            ChatId = source.ChatId,
            Name = source.Name,
            Description = source.Description,
            Prompt = source.Prompt,
            TriggerType = source.TriggerType,
            ScheduleType = source.ScheduleType,
            IntervalMinutes = source.IntervalMinutes,
            DailyTime = source.DailyTime,
            DaysOfWeek = source.DaysOfWeek,
            MonthlyDay = source.MonthlyDay,
            CronExpression = source.CronExpression,
            RunAt = source.RunAt,
            ScriptContent = source.ScriptContent,
            ScriptLanguage = source.ScriptLanguage,
            IsEnabled = source.IsEnabled,
            IsTemporary = source.IsTemporary,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            LastRunStartedAt = source.LastRunStartedAt,
            LastRunAt = source.LastRunAt,
            NextRunAt = source.NextRunAt,
            LastRunStatus = source.LastRunStatus,
            LastRunSummary = source.LastRunSummary,
            LastScriptOutput = source.LastScriptOutput,
            LastScriptExitCode = source.LastScriptExitCode,
            RunCount = source.RunCount
        };
    }

    private static Chat CloneChatIndex(Chat source)
    {
        return new Chat
        {
            Id = source.Id,
            Title = source.Title,
            ProjectId = source.ProjectId,
            AgentId = source.AgentId,
            CopilotSessionId = source.CopilotSessionId,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            ActiveSkillIds = [..source.ActiveSkillIds],
            ActiveExternalSkillNames = [..source.ActiveExternalSkillNames],
            ActiveMcpServerNames = [..source.ActiveMcpServerNames],
            HasExplicitMcpServerSelection = source.HasExplicitMcpServerSelection,
            SessionMode = source.SessionMode,
            SdkAgentName = source.SdkAgentName,
            WorktreePath = source.WorktreePath,
            LastModelUsed = source.LastModelUsed,
            LastReasoningEffortUsed = source.LastReasoningEffortUsed,
            LastContextWindowTierUsed = source.LastContextWindowTierUsed,
            TotalInputTokens = source.TotalInputTokens,
            TotalOutputTokens = source.TotalOutputTokens,
            ContextCurrentTokens = source.ContextCurrentTokens,
            ContextTokenLimit = source.ContextTokenLimit,
            PlanContent = source.PlanContent,
            FollowUpSuggestions = [..source.FollowUpSuggestions],
            FollowUpSuggestionAssistantMessageId = source.FollowUpSuggestionAssistantMessageId,
        };
    }

    private static ReleaseServiceProfile CloneReleaseServiceProfile(ReleaseServiceProfile source)
    {
        return new ReleaseServiceProfile
        {
            Id = source.Id,
            ServiceName = source.ServiceName,
            RepoPath = source.RepoPath,
            AdoOrgUrl = source.AdoOrgUrl,
            AdoProjectName = source.AdoProjectName,
            Pipelines = [..source.Pipelines.Select(static pipeline => new ReleasePipelineConfig
            {
                Name = pipeline.Name,
                PipelineId = pipeline.PipelineId,
                PipelineType = pipeline.PipelineType,
                Stage = pipeline.Stage,
                Ring = pipeline.Ring,
                Environment = pipeline.Environment,
                Region = pipeline.Region,
                Stamp = pipeline.Stamp
            })],
            HealthQueries = [..source.HealthQueries.Select(static query => new ReleaseHealthQuery
            {
                Name = query.Name,
                Query = query.Query,
                Scope = query.Scope,
                DashboardUrl = query.DashboardUrl
            })],
            Dependencies = [..source.Dependencies.Select(static dependency => new ReleasePipelineDependency
            {
                Source = dependency.Source,
                Target = dependency.Target,
                Reason = dependency.Reason
            })],
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt
        };
    }

    private static ReleaseEvidencePacket CloneReleaseEvidencePacket(ReleaseEvidencePacket source)
    {
        return new ReleaseEvidencePacket
        {
            Id = source.Id,
            ChatId = source.ChatId,
            Service = source.Service,
            Goal = source.Goal,
            TargetScope = source.TargetScope,
            Candidate = source.Candidate is null ? null : new ReleaseCandidateEvidence
            {
                Version = source.Candidate.Version,
                BuildId = source.Candidate.BuildId,
                Branch = source.Candidate.Branch,
                CommitSha = source.Candidate.CommitSha,
                PullRequests = [..source.Candidate.PullRequests],
                WorkItems = [..source.Candidate.WorkItems],
                Rationale = source.Candidate.Rationale
            },
            ProofChain = [..source.ProofChain.Select(static link => new ReleaseProofLink
            {
                LinkType = link.LinkType,
                Label = link.Label,
                Evidence = link.Evidence,
                Status = link.Status
            })],
            Risk = new ReleaseRiskAssessment
            {
                Summary = source.Risk.Summary,
                ChangeType = source.Risk.ChangeType,
                ReviewPathRecommendation = source.Risk.ReviewPathRecommendation,
                Confidence = source.Risk.Confidence,
                Signals = [..source.Risk.Signals]
            },
            State = CloneReleaseState(source.State),
            MissingProof = [..source.MissingProof],
            RecommendedNextAction = source.RecommendedNextAction,
            RequiresUserApproval = source.RequiresUserApproval,
            SafeFlyDraftId = source.SafeFlyDraftId,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt
        };
    }

    private static ReleaseSafeFlyDraft CloneReleaseSafeFlyDraft(ReleaseSafeFlyDraft source)
    {
        return new ReleaseSafeFlyDraft
        {
            Id = source.Id,
            ChatId = source.ChatId,
            EvidencePacketId = source.EvidencePacketId,
            Service = source.Service,
            CandidateVersion = source.CandidateVersion,
            TargetScope = source.TargetScope,
            ChangeType = source.ChangeType,
            AutoCreateCapability = source.AutoCreateCapability,
            RiskSummary = source.RiskSummary,
            ValidationSummary = source.ValidationSummary,
            HealthSummary = source.HealthSummary,
            BakeTimeSummary = source.BakeTimeSummary,
            RollbackPlan = source.RollbackPlan,
            CommunicationsPlan = source.CommunicationsPlan,
            ReviewPathRecommendation = source.ReviewPathRecommendation,
            Status = source.Status,
            ExternalRequestId = source.ExternalRequestId,
            RequestStatus = source.RequestStatus,
            StatusUrl = source.StatusUrl,
            LastStatusSummary = source.LastStatusSummary,
            LastStatusCheckedAt = source.LastStatusCheckedAt,
            MissingFields = [..source.MissingFields],
            Links = [..source.Links],
            RequiresUserApprovalForCreate = source.RequiresUserApprovalForCreate,
            RequiresUserApprovalForSubmit = source.RequiresUserApprovalForSubmit,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt
        };
    }

    private static ReleaseStateSnapshot CloneReleaseState(ReleaseStateSnapshot source)
    {
        return new ReleaseStateSnapshot
        {
            CandidateState = source.CandidateState,
            PipelineState = source.PipelineState,
            DeploymentState = source.DeploymentState,
            HealthState = source.HealthState,
            ChangeValidationState = source.ChangeValidationState,
            DependencyState = source.DependencyState,
            R2DState = source.R2DState,
            CommunicationsState = source.CommunicationsState,
            ApprovalState = source.ApprovalState
        };
    }
}
