using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Services.Remote;

internal sealed record TranscriptMessageWindow(
    IReadOnlyList<ChatMessage> Messages,
    int StartMessageIndex,
    int EndMessageIndex,
    int TotalMessageCount)
{
    public bool HasEarlierMessages => StartMessageIndex > 0;
    public bool HasLaterMessages => EndMessageIndex < TotalMessageCount;
    public bool IsLatestWindow => EndMessageIndex == TotalMessageCount;
}

/// <summary>
/// Maps Lumi's in-process domain and view-model state onto the wire contract in
/// <c>Lumi.Remote.Protocol</c>. Everything here is a pure read-side projection: it never mutates
/// Lumi state, so hosting a phone client can't change how the desktop behaves.
/// </summary>
internal static class RemoteProjector
{
    private const string ExternalAgentGlyph = "🤖";

    /// <summary>Tools whose output reads better as a terminal panel than as a tool card.</summary>
    private static readonly HashSet<string> TerminalTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "shell", "powershell", "run_command", "execute_command", "terminal"
    };

    public static RemoteSnapshot BuildSnapshot(
        DataStore dataStore,
        MainViewModel main,
        IReadOnlyList<string> models,
        bool includeChatList = true,
        bool includeLibrary = true,
        bool isPartial = false)
    {
        var activeChat = main.ChatVM.CurrentChat;
        return new RemoteSnapshot
        {
            Capabilities = [.. RemoteProtocol.Capabilities.Server],
            IsPartial = isPartial,
            HostName = Environment.MachineName,
            IsConnected = main.IsConnected,
            ConnectionStatus = main.ConnectionStatus,
            ActiveChatId = activeChat?.Id,
            ActiveChat = activeChat is null ? null : BuildChat(dataStore, activeChat, main),
            Chats = includeChatList
                ? BuildChatPage(
                    dataStore,
                    main,
                    offset: 0,
                    limit: RemoteProtocol.ChatPageSize,
                    query: null,
                    projectId: null)
                : new RemoteChatPage(),
            Library = includeLibrary ? BuildLibrary(dataStore) : new RemoteLibrary(),
            Settings = BuildSettings(dataStore, models, main.ChatVM)
        };
    }

    public static RemoteChatPage BuildChatPage(
        DataStore dataStore,
        MainViewModel main,
        int offset,
        int limit,
        string? query,
        Guid? projectId)
    {
        var context = ChatProjectionContext.Create(dataStore, main);
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, RemoteProtocol.MaxChatPageSize);
        query = string.IsNullOrWhiteSpace(query) ? null : query.Trim();

        var matching = dataStore.Data.Chats
            .OrderByDescending(chat => chat.IsPinned)
            .ThenByDescending(chat => chat.UpdatedAt)
            .Where(chat => projectId is null || chat.ProjectId == projectId)
            .Where(chat => query is null || MatchesQuery(chat, query, context))
            .ToList();
        var pageChats = matching.Skip(offset).Take(limit).ToList();

        var today = DateTimeOffset.Now.Date;
        var yesterday = today.AddDays(-1);
        var weekAgo = today.AddDays(-7);
        var pinned = pageChats.Where(chat => chat.IsPinned).ToList();
        var unpinned = pageChats.Where(chat => !chat.IsPinned).ToList();

        var groups = new List<RemoteChatGroup>();
        AddGroup(Loc.ChatGroup_Pinned, pinned);
        AddGroup(Loc.ChatGroup_Today, unpinned.Where(chat => chat.UpdatedAt.Date == today));
        AddGroup(Loc.ChatGroup_Yesterday, unpinned.Where(chat => chat.UpdatedAt.Date == yesterday));
        AddGroup(
            Loc.ChatGroup_Previous7Days,
            unpinned.Where(chat => chat.UpdatedAt.Date < yesterday && chat.UpdatedAt.Date >= weekAgo));
        AddGroup(Loc.ChatGroup_Older, unpinned.Where(chat => chat.UpdatedAt.Date < weekAgo));
        return new RemoteChatPage
        {
            Offset = offset,
            TotalCount = matching.Count,
            HasMore = offset + pageChats.Count < matching.Count,
            Query = query,
            ProjectId = projectId,
            Groups = groups
        };

        void AddGroup(string label, IEnumerable<Chat> chats)
        {
            var projected = chats.Select(chat => BuildChat(chat, context)).ToList();
            if (projected.Count > 0)
                groups.Add(new RemoteChatGroup { Label = label, Chats = projected });
        }
    }

    private static bool MatchesQuery(Chat chat, string query, ChatProjectionContext context)
    {
        if (chat.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (chat.Preview?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return true;
        }

        return chat.ProjectId is { } projectId
               && context.Projects.TryGetValue(projectId, out var project)
               && project.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public static RemoteChat BuildChat(DataStore dataStore, Chat chat, MainViewModel main)
        => BuildChat(chat, ChatProjectionContext.Create(dataStore, main));

    private static RemoteChat BuildChat(Chat chat, ChatProjectionContext context)
    {
        context.Owners.TryGetValue(chat.Id, out var owner);
        var project = chat.ProjectId is { } projectId
            ? context.Projects.GetValueOrDefault(projectId)
            : null;
        var agent = chat.AgentId is { } agentId
            ? context.Agents.GetValueOrDefault(agentId)
            : null;

        return new RemoteChat
        {
            Id = chat.Id,
            Title = chat.Title,
            ProjectId = chat.ProjectId,
            ProjectName = project?.Name,
            AgentId = chat.AgentId,
            AgentName = agent?.Name,
            AgentGlyph = agent?.IconGlyph,
            MessageCount = GetEffectiveMessageCount(chat),
            UpdatedAt = chat.UpdatedAt,
            IsPinned = chat.IsPinned,
            IsRunning = owner?.IsChatBusy(chat.Id) ?? chat.IsRunning,
            HasUnreadMessages = chat.HasUnreadMessages,
            LastModelUsed = chat.LastModelUsed,
            Preview = GetChatPreview(chat, owner)
        };
    }

    internal static int GetEffectiveMessageCount(Chat chat) =>
        Math.Max(chat.MessageCount, chat.Messages.Count);

    private sealed record ChatProjectionContext(
        Dictionary<Guid, Project> Projects,
        Dictionary<Guid, LumiAgent> Agents,
        Dictionary<Guid, ChatViewModel> Owners)
    {
        public static ChatProjectionContext Create(DataStore dataStore, MainViewModel main)
        {
            var owners = new Dictionary<Guid, ChatViewModel>();
            foreach (var surface in main.ChatSurfaceRegistry.SnapshotSurfaces())
            {
                if (surface.CurrentChat is { } chat)
                    owners.TryAdd(chat.Id, surface);
            }

            if (main.ChatVM.CurrentChat is { } current)
                owners[current.Id] = main.ChatVM;

            return new ChatProjectionContext(
                dataStore.Data.Projects.ToDictionary(static project => project.Id),
                dataStore.Data.Agents.ToDictionary(static agent => agent.Id),
                owners);
        }
    }

    internal static ChatViewModel? ResolveChatOwner(MainViewModel main, Guid chatId)
    {
        var displayingLiveOwner = main.ChatSurfaceRegistry
            .SnapshotSurfaces()
            .FirstOrDefault(surface =>
                surface.CurrentChat?.Id == chatId && surface.OwnsLiveChat(chatId));
        if (displayingLiveOwner is not null)
            return displayingLiveOwner;

        if (main.ChatSurfaceRegistry.TryGetOwner(chatId, out var owner))
            return owner;
        if (main.ChatSurfaceRegistry.TryGetLiveOwner(chatId, out var liveOwner))
            return liveOwner;
        return main.ChatVM.CurrentChat?.Id == chatId ? main.ChatVM : null;
    }

    private static string? GetChatPreview(Chat chat, ChatViewModel? owner)
    {
        if (owner?.CurrentChat?.Id == chat.Id && owner.Messages.Count > 0)
        {
            for (var index = owner.Messages.Count - 1; index >= 0; index--)
            {
                var message = owner.Messages[index].Message;
                if (message.Role is not ("user" or "assistant"))
                    continue;

                var preview = ChatPreviewHelper.FromContent(message.Content);
                if (preview is not null)
                    return preview;
            }
        }

        return chat.Messages.Count > 0
            ? ChatPreviewHelper.FromMessages(chat.Messages)
            : chat.Preview;
    }

    internal static string? BuildChatPreview(IReadOnlyList<ChatMessage> messages)
        => ChatPreviewHelper.FromMessages(messages);

    public static RemoteChatStatus BuildStatus(DataStore dataStore, ChatViewModel chatVm, Chat chat)
    {
        var chatId = chat.Id;
        var isActive = chatVm.CurrentChat?.Id == chatId;
        var model = isActive ? chatVm.SelectedModel ?? chat.LastModelUsed : chat.LastModelUsed;
        var agent = chat.AgentId is { } agentId
            ? dataStore.Data.Agents.FirstOrDefault(candidate => candidate.Id == agentId)
            : null;
        var project = chat.ProjectId is { } projectId
            ? dataStore.Data.Projects.FirstOrDefault(candidate => candidate.Id == projectId)
            : null;

        return BoundStatus(new RemoteChatStatus
        {
            ChatId = chatId,
            IsBusy = isActive ? chatVm.IsBusy : chatVm.IsChatBusy(chatId),
            IsStreaming = isActive && chatVm.IsStreaming,
            StatusText = isActive ? chatVm.StatusText : null,
            Model = model,
            ContextCurrentTokens = isActive ? chatVm.ContextCurrentTokens : 0,
            ContextTokenLimit = isActive ? chatVm.ContextTokenLimit : 0,
            PlanContent = RemoteProtocol.TruncateForMobile(
                isActive ? chatVm.PlanContent : chat.PlanContent,
                RemoteProtocol.MobilePlanTextLimit),
            Quality = isActive ? chatVm.SelectedQuality : chat.LastReasoningEffortUsed,
            QualityLevels = BoundStrings(
                chatVm.GetQualityLevelsFor(model) ?? [],
                RemoteProtocol.MobileStatusValueLimit),
            ContextWindowTier = isActive ? chatVm.SelectedContextWindowTier : chat.LastContextWindowTierUsed,
            ContextWindowTiers = isActive
                ? BoundStrings(
                    chatVm.ContextWindowTiers ?? [],
                    RemoteProtocol.MobileStatusValueLimit)
                : [],
            AgentName = isActive ? chatVm.SelectedAgentName : agent?.Name ?? chat.SdkAgentName,
            AgentId = chat.AgentId,
            AgentGlyph = isActive
                ? chatVm.SelectedAgentGlyph
                : agent?.IconGlyph ?? (chat.SdkAgentName is null ? null : ExternalAgentGlyph),
            ProjectName = isActive ? chatVm.SelectedProjectName : project?.Name,
            ProjectId = chat.ProjectId,
            UsesWorktree = chat.WorktreePath is { Length: > 0 } worktreePath
                           && Directory.Exists(worktreePath),
            SkillNames = isActive
                ? ChipNames(chatVm.ActiveSkillChips)
                :
                [
                    .. chat.ActiveSkillIds
                        .Select(skillId => dataStore.Data.Skills.FirstOrDefault(skill => skill.Id == skillId)?.Name)
                        .Where(static name => !string.IsNullOrWhiteSpace(name))
                        .Select(static name => name!),
                    .. chat.ActiveExternalSkillNames
                ],
            McpNames = isActive ? ChipNames(chatVm.ActiveMcpChips) : [.. chat.ActiveMcpServerNames],
            AvailableAgents = isActive ? BuildChips(chatVm.AvailableAgentChips) : [],
            AvailableSkills = isActive ? BuildChips(chatVm.AvailableSkillChips) : [],
            AvailableMcps = isActive ? BuildChips(chatVm.AvailableMcpChips) : [],
            AvailableProjects = isActive ? BuildChips(chatVm.AvailableProjectChips) : [],
            HasComposerCatalogs = isActive,
            Suggestions = isActive
                ? new[] { chatVm.SuggestionA, chatVm.SuggestionB, chatVm.SuggestionC }
                    .Where(suggestion => !string.IsNullOrWhiteSpace(suggestion))
                    .ToList()
                : []
        });
    }

    /// <summary>
    /// The active skill/MCP collections are <c>ObservableCollection&lt;object&gt;</c> on the desktop
    /// (the composer accepts loose items), so read the name defensively rather than casting.
    /// </summary>
    private static List<string> ChipNames(IEnumerable<object> chips)
    {
        var names = new List<string>(RemoteProtocol.MobileStatusCollectionCountLimit);
        foreach (var chip in chips)
        {
            var name = chip switch
            {
                StrataComposerChip typed => typed.Name,
                string text => text,
                _ => chip?.ToString()
            };
            if (string.IsNullOrWhiteSpace(name))
                continue;

            names.Add(BoundRequired(name, RemoteProtocol.MobileMetadataTextLimit));
            if (names.Count == RemoteProtocol.MobileStatusCollectionCountLimit)
                break;
        }

        return names;
    }

    private static List<RemoteChip> BuildChips(IEnumerable<StrataComposerChip> chips) =>
        chips.Select(chip => new RemoteChip
             {
                 Name = BoundRequired(chip.Name, RemoteProtocol.MobileMetadataTextLimit),
                 Glyph = BoundOptional(chip.Glyph, RemoteProtocol.MobileMetadataTextLimit),
                 Description = BoundOptional(chip.SecondaryText, RemoteProtocol.MobileMetadataTextLimit),
                 Value = BoundOptional(chip.Value, RemoteProtocol.MobileIdentifierLimit)
             })
             .ToList();

    public static RemoteLibrary BuildLibrary(DataStore dataStore)
    {
        var data = dataStore.Data;
        return new RemoteLibrary
        {
            Projects = data.Projects.Select(project => new RemoteProject
            {
                Id = project.Id,
                Name = BoundRequired(project.Name, RemoteProtocol.MobileMetadataTextLimit),
                Instructions = BoundOptional(project.Instructions, RemoteProtocol.MobileLibraryPreviewLimit),
                ChatCount = data.Chats.Count(chat => chat.ProjectId == project.Id),
                IsCodingProject = GitService.IsGitRepo(project.WorkingDirectory ?? ""),
                DefaultNewChatsUseWorktree = project.DefaultNewChatsUseWorktree
            }).ToList(),
            Skills = data.Skills.Select(skill => new RemoteSkill
            {
                Id = skill.Id,
                Name = BoundRequired(skill.Name, RemoteProtocol.MobileMetadataTextLimit),
                Description = BoundOptional(skill.Description, RemoteProtocol.MobileLibraryPreviewLimit),
                IconGlyph = BoundRequired(skill.IconGlyph, RemoteProtocol.MobileMetadataTextLimit),
                IsBuiltIn = skill.IsBuiltIn
            }).ToList(),
            Lumis = data.Agents.Select(agent => new RemoteLumi
            {
                Id = agent.Id,
                Name = BoundRequired(agent.Name, RemoteProtocol.MobileMetadataTextLimit),
                Description = BoundOptional(agent.Description, RemoteProtocol.MobileLibraryPreviewLimit),
                IconGlyph = BoundRequired(agent.IconGlyph, RemoteProtocol.MobileMetadataTextLimit),
                IsBuiltIn = agent.IsBuiltIn,
                SkillCount = agent.SkillIds.Count,
                ToolCount = agent.ToolNames.Count
            }).ToList(),
            Memories = data.Memories.Select(memory => new RemoteMemory
            {
                Id = memory.Id,
                Key = BoundRequired(memory.Key, RemoteProtocol.MobileMetadataTextLimit),
                Content = BoundRequired(memory.Content, RemoteProtocol.MobileLibraryPreviewLimit),
                Category = BoundRequired(memory.Category, RemoteProtocol.MobileMetadataTextLimit),
                Scope = BoundRequired(memory.Scope, RemoteProtocol.MobileMetadataTextLimit),
                UpdatedAt = memory.UpdatedAt
            }).ToList(),
            McpServers = data.McpServers.Select(server => new RemoteMcpServer
            {
                Id = server.Id,
                Name = BoundRequired(server.Name, RemoteProtocol.MobileMetadataTextLimit),
                Description = BoundOptional(server.Description, RemoteProtocol.MobileMetadataTextLimit),
                ServerType = BoundRequired(server.ServerType, RemoteProtocol.MobileMetadataTextLimit),
                IsEnabled = server.IsEnabled,
                ToolCount = server.Tools.Count
            }).ToList(),
            Jobs = dataStore.SnapshotBackgroundJobs().Select(job => new RemoteJob
            {
                Id = job.Id,
                Name = BoundRequired(job.Name, RemoteProtocol.MobileMetadataTextLimit),
                Description = BoundOptional(job.Description, RemoteProtocol.MobileMetadataTextLimit),
                ScheduleSummary = BoundOptional(job.UpcomingRunDisplay, RemoteProtocol.MobileMetadataTextLimit),
                IsEnabled = job.IsEnabled,
                LastRunStatus = BoundRequired(job.LifecycleDisplay, RemoteProtocol.MobileMetadataTextLimit),
                NextRunAt = job.NextRunAt
            }).ToList()
        };
    }

    public static RemoteSettings BuildSettings(
        DataStore dataStore,
        IReadOnlyList<string> models,
        ChatViewModel? chatVm = null)
    {
        var settings = dataStore.Data.Settings;
        return new RemoteSettings
        {
            UserName = settings.UserName ?? "",
            IsDarkTheme = settings.IsDarkTheme,
            PreferredModel = settings.PreferredModel,
            ReasoningEffort = settings.ReasoningEffort,
            SendWithEnter = settings.SendWithEnter,
            ShowToolCalls = settings.ShowToolCalls,
            ShowReasoning = settings.ShowReasoning,
            ShowTimestamps = settings.ShowTimestamps,
            AvailableModels = models.ToList(),
            ModelDisplayNames = models
                .Select(model => $"{model}={ChatViewModel.FormatModelDisplay(model) ?? model}")
                .ToList(),
            ModelReasoningEfforts = BuildReasoningEffortMap(models, chatVm),
            ModelContextWindowTiers = BuildContextWindowTierMap(models, chatVm)
        };
    }

    /// <summary>
    /// Which reasoning efforts each model supports, as <c>model=low,medium,high</c> lines.
    ///
    /// <para>The phone needs this to offer an effort control on a chat that does not exist yet — it
    /// defers creation until the first message, so there is no chat status to read levels from.
    /// Models that support no effort are omitted rather than listed empty, so the phone can treat
    /// "absent" and "none" identically.</para>
    /// </summary>
    private static List<string> BuildReasoningEffortMap(IReadOnlyList<string> models, ChatViewModel? chatVm)
    {
        if (chatVm is null)
            return [];

        var map = new List<string>();
        foreach (var model in models)
        {
            if (chatVm.GetQualityLevelsFor(model) is not { Length: > 0 } levels)
                continue;

            map.Add($"{model}={string.Join(',', levels)}");
        }

        return map;
    }

    private static List<string> BuildContextWindowTierMap(
        IReadOnlyList<string> models,
        ChatViewModel? chatVm)
    {
        if (chatVm is null)
            return [];

        var map = new List<string>();
        foreach (var model in models)
        {
            if (chatVm.GetContextWindowTiersFor(model) is not { Length: > 1 } tiers)
                continue;

            map.Add($"{model}={string.Join(',', tiers)}");
        }

        return map;
    }

    /// <summary>
    /// Projects a flat message list into the turn/item shape the phone renders. Grouping is done
    /// here rather than on the device so the client stays thin and a protocol bump can change the
    /// presentation without shipping a new app.
    /// </summary>
    public static RemoteTranscript BuildTranscript(
        Chat chat,
        IReadOnlyList<ChatMessage> messages,
        RemoteChatStatus status,
        bool showReasoning,
        bool showToolCalls,
        long revision,
        string? revisionEpoch = null,
        bool compact = false,
        string? workingDirectory = null,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null,
        IReadOnlySet<string>? runningBackgroundToolCallIds = null)
        => BuildTranscript(
            chat,
            new TranscriptMessageWindow(messages, 0, messages.Count, messages.Count),
            status,
            showReasoning,
            showToolCalls,
            revision,
            revisionEpoch,
            compact,
            workingDirectory,
            activitySourceMessages ?? messages,
            runningBackgroundToolCallIds);

    public static RemoteTranscript BuildTranscript(
        Chat chat,
        TranscriptMessageWindow window,
        RemoteChatStatus status,
        bool showReasoning,
        bool showToolCalls,
        long revision,
        string? revisionEpoch = null,
        bool compact = false,
        string? workingDirectory = null,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null,
        IReadOnlySet<string>? runningBackgroundToolCallIds = null)
    {
        var transcript = new RemoteTranscript
        {
            ChatId = chat.Id,
            Title = chat.Title,
            RevisionEpoch = revisionEpoch,
            Revision = revision,
            WindowStartMessageIndex = window.StartMessageIndex,
            WindowEndMessageIndex = window.EndMessageIndex,
            TotalRawMessageCount = window.TotalMessageCount,
            HasEarlierMessages = window.HasEarlierMessages,
            HasLaterMessages = window.HasLaterMessages,
            IsLatestWindow = window.IsLatestWindow,
            Status = status
        };

        RemoteTranscriptTurn? turn = null;
        RemoteTranscriptItem? toolGroup = null;
        RemoteTranscriptItem? compactActivity = null;
        var compactSource = activitySourceMessages ?? window.Messages;

        RemoteTranscriptTurn EnsureTurn()
        {
            if (turn is not null)
                return turn;

            turn = new RemoteTranscriptTurn { Id = $"turn-{transcript.Turns.Count}" };
            transcript.Turns.Add(turn);
            return turn;
        }

        void EnsureCompactActivity(ChatMessage message)
        {
            if (!compact || compactActivity is not null)
                return;

            compactActivity = BuildCompactActivityForMessage(
                compactSource,
                message.Id,
                workingDirectory,
                runningBackgroundToolCallIds);
            if (compactActivity is null)
                return;

            var target = EnsureTurn();
            var insertionIndex = target.Items.FindIndex(
                static item => item.Kind != RemoteProtocol.ItemKinds.User);
            target.Items.Insert(
                insertionIndex < 0 ? target.Items.Count : insertionIndex,
                compactActivity);
        }

        foreach (var message in window.Messages)
        {
            switch (message.Role)
            {
                case "user":
                    // A user message always opens a new turn.
                    turn = new RemoteTranscriptTurn { Id = $"turn-{transcript.Turns.Count}" };
                    transcript.Turns.Add(turn);
                    toolGroup = null;
                    compactActivity = null;
                    turn.Items.Add(BuildUserItem(message));
                    break;

                case "assistant":
                    toolGroup = null;
                    EnsureCompactActivity(message);
                    EnsureTurn().Items.Add(BuildAssistantItem(message));
                    break;

                case "reasoning":
                    toolGroup = null;
                    if (compact)
                        EnsureCompactActivity(message);
                    else if (showReasoning)
                        EnsureTurn().Items.Add(BuildReasoningItem(message));
                    break;

                case "error":
                    toolGroup = null;
                    EnsureTurn().Items.Add(new RemoteTranscriptItem
                    {
                        Id = message.Id.ToString("N"),
                        Kind = RemoteProtocol.ItemKinds.Error,
                        Text = RemoteProtocol.TruncateForMobile(
                            message.Content,
                            RemoteProtocol.MobileAssistantTextLimit),
                        Timestamp = message.Timestamp
                    });
                    break;

                case "tool":
                    if (!compact
                        && !showToolCalls
                        && !IsQuestion(message)
                        && !IsAnnouncedFile(message))
                    {
                        toolGroup = null;
                        break;
                    }

                    if (compact && !IsQuestion(message) && !IsAnnouncedFile(message))
                    {
                        EnsureCompactActivity(message);
                        toolGroup = null;
                    }
                    else
                    {
                        AppendToolLike(
                            EnsureTurn(),
                            message,
                            ref toolGroup,
                            runningBackgroundToolCallIds);
                    }
                    break;

                default:
                    toolGroup = null;
                    if (!string.IsNullOrWhiteSpace(message.Content))
                    {
                        EnsureCompactActivity(message);
                        EnsureTurn().Items.Add(new RemoteTranscriptItem
                        {
                            Id = message.Id.ToString("N"),
                            Kind = RemoteProtocol.ItemKinds.Assistant,
                            Text = RemoteProtocol.TruncateForMobile(
                                message.Content,
                                RemoteProtocol.MobileAssistantTextLimit),
                            Author = message.Author,
                            Timestamp = message.Timestamp
                        });
                    }

                    break;
            }
        }

        // The plan is chat-level state, not a transcript row. Appending it to the last turn pinned a
        // full-size card to the bottom of every refresh, which pushed the actual conversation up and
        // re-rendered the whole plan on every token. It travels on RemoteChatStatus.PlanContent
        // instead, where the phone can show it on demand.

        return EnforceTranscriptWireLimit(BoundTranscript(transcript));
    }

    public static RemoteActivityDetails? BuildActivityDetails(
        Chat chat,
        IReadOnlyList<ChatMessage> messages,
        string activityId,
        string? workingDirectory = null,
        IReadOnlySet<string>? runningBackgroundToolCallIds = null)
    {
        if (string.IsNullOrWhiteSpace(activityId))
            return null;

        if (!Guid.TryParseExact(activityId, "N", out var activityMessageId)
            || !TryFindLogicalTurn(messages, activityMessageId, out var start, out var end))
        {
            return null;
        }

        var anchor = messages
            .Skip(start)
            .Take(end - start)
            .FirstOrDefault(static message =>
                message.Role == "reasoning"
                || message.Role == "tool"
                   && !IsQuestion(message)
                   && !IsAnnouncedFile(message));
        if (anchor?.Id != activityMessageId)
            return null;

        var technicalMessages = new List<ChatMessage>();
        for (var index = start; index < end; index++)
        {
            var message = messages[index];
            if (message.Role == "tool"
                && !IsQuestion(message)
                && !IsAnnouncedFile(message))
            {
                technicalMessages.Add(message);
            }
        }

        var toolMessages = technicalMessages
            .Where(static message =>
                !string.Equals(
                    message.ToolName,
                    ToolDisplayHelper.WorkspaceFileChangedToolName,
                    StringComparison.Ordinal))
            .ToList();
        var retainedMessages = SelectActivityToolMessages(
            toolMessages,
            runningBackgroundToolCallIds,
            out var omittedToolCount,
            out var omittedToolStatus);
        var tools = retainedMessages
            .Select(message => BuildActivityToolCall(message, runningBackgroundToolCallIds))
            .Select(BoundActivityTool)
            .ToList();
        if (omittedToolCount > 0)
        {
            tools.Add(new RemoteToolCall
            {
                Id = "omitted-activity-tools",
                Name = "omitted",
                DisplayName = $"{omittedToolCount} more actions omitted",
                Category = "other",
                Status = omittedToolStatus
            });
        }

        var fileChanges = BuildFileChanges(technicalMessages, workingDirectory);
        return EnforceActivityWireLimit(new RemoteActivityDetails
        {
            ChatId = chat.Id,
            ActivityId = BoundRequired(activityId, RemoteProtocol.MobileIdentifierLimit),
            Tools = tools,
            TotalFileChangeCount = fileChanges.Count,
            FileChanges = BoundFileChanges(fileChanges)
        });
    }

    private static List<ChatMessage> SelectActivityToolMessages(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlySet<string>? runningBackgroundToolCallIds,
        out int omittedCount,
        out string omittedStatus)
    {
        var retainedLimit = RemoteProtocol.MobileActivityToolCountLimit - 1;
        if (messages.Count <= RemoteProtocol.MobileActivityToolCountLimit)
        {
            omittedCount = 0;
            omittedStatus = "Completed";
            return [.. messages];
        }

        var selectedIndexes = new HashSet<int>();
        for (var index = messages.Count - 1;
             index >= 0 && selectedIndexes.Count < retainedLimit;
             index--)
        {
            if (!string.Equals(
                    ProjectedToolStatus(messages[index], runningBackgroundToolCallIds),
                    "Completed",
                    StringComparison.Ordinal))
            {
                selectedIndexes.Add(index);
            }
        }

        for (var index = messages.Count - 1;
             index >= 0 && selectedIndexes.Count < retainedLimit;
             index--)
        {
            selectedIndexes.Add(index);
        }

        var retained = selectedIndexes
            .Order()
            .Select(index => messages[index])
            .ToList();
        var omittedStatuses = messages
            .Select((message, index) => (message, index))
            .Where(item => !selectedIndexes.Contains(item.index))
            .Select(item => ProjectedToolStatus(item.message, runningBackgroundToolCallIds));
        omittedCount = messages.Count - retained.Count;
        omittedStatus = AggregateActivityStatus(omittedStatuses);
        return retained;
    }

    private static string ProjectedToolStatus(
        ChatMessage message,
        IReadOnlySet<string>? runningBackgroundToolCallIds) =>
        message.ToolCallId is { Length: > 0 } toolCallId
        && runningBackgroundToolCallIds?.Contains(toolCallId) == true
            ? "InProgress"
            : message.ToolStatus ?? "Completed";

    private static string AggregateActivityStatus(IEnumerable<string> statuses)
    {
        var result = "Completed";
        foreach (var status in statuses)
            result = MergeActivityStatus(result, status);
        return result;
    }

    private static bool TryFindLogicalTurn(
        IReadOnlyList<ChatMessage> messages,
        Guid messageId,
        out int start,
        out int end)
    {
        var messageIndex = -1;
        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index].Id == messageId)
            {
                messageIndex = index;
                break;
            }
        }

        if (messageIndex < 0)
        {
            start = 0;
            end = 0;
            return false;
        }

        start = messageIndex;
        while (start > 0 && messages[start - 1].Role != "user")
            start--;
        end = messageIndex + 1;
        while (end < messages.Count && messages[end].Role != "user")
            end++;
        return true;
    }

    /// <summary>
    /// Selects a contiguous raw-message page before any turn/tool projection occurs. The cursor is an
    /// exclusive end index: requesting <c>beforeMessageIndex=N</c> returns messages strictly before
    /// N. Passing the returned start index to the next request reaches every earlier message without
    /// overlap or gaps.
    /// </summary>
    internal static TranscriptMessageWindow SelectTranscriptWindow(
        IReadOnlyList<ChatMessage> messages,
        int? beforeMessageIndex,
        int maxMessages = RemoteProtocol.TranscriptWindowRawMessageLimit,
        int textBudgetCharacters = RemoteProtocol.TranscriptWindowTextBudgetCharacters)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(textBudgetCharacters, 1);

        var total = messages.Count;
        if (total == 0)
            return new TranscriptMessageWindow([], 0, 0, 0);

        // The HTTP boundary rejects zero/negative cursors. Clamping here keeps this pure helper
        // progress-safe for internal callers and focused tests as well.
        var end = Math.Clamp(beforeMessageIndex ?? total, 1, total);
        var start = end;
        long textCharacters = 0;

        while (start > 0 && end - start < maxMessages)
        {
            var candidateIndex = start - 1;
            var remainingBudget = Math.Max(0, textBudgetCharacters - textCharacters);
            var candidateCost = EstimateTranscriptTextCharacters(
                messages[candidateIndex],
                remainingBudget);

            // Always include the first message, even when it alone exceeds the page budget. The
            // projection truncates it, and the cursor still moves instead of getting stuck forever.
            if (start < end && textCharacters + candidateCost > textBudgetCharacters)
                break;

            start = candidateIndex;
            textCharacters += candidateCost;
        }

        var selected = new List<ChatMessage>(end - start);
        for (var index = start; index < end; index++)
            selected.Add(messages[index]);

        return new TranscriptMessageWindow(selected, start, end, total);
    }

    private static long EstimateTranscriptTextCharacters(ChatMessage message, long stopAfter)
    {
        long total = 0;
        if (Add(message.Content)
            || Add(message.ToolOutput)
            || Add(message.QuestionText)
            || Add(message.QuestionOptions)
            || Add(message.LinkedChatTitle))
        {
            return stopAfter + 1;
        }

        foreach (var attachment in message.Attachments)
        {
            if (Add(attachment))
                return stopAfter + 1;
        }

        foreach (var source in message.Sources)
        {
            if (Add(source.Title) || Add(source.Snippet) || Add(source.Url))
                return stopAfter + 1;
        }

        return total;

        bool Add(string? value)
        {
            total += TextLength(value);
            return total > stopAfter;
        }
    }

    private static int TextLength(string? value) => value?.Length ?? 0;

    private static bool IsQuestion(ChatMessage message) =>
        !string.IsNullOrWhiteSpace(message.QuestionId) || message.ToolName == "ask_question";

    private static bool IsAnnouncedFile(ChatMessage message) =>
        string.Equals(message.ToolName, "announce_file", StringComparison.OrdinalIgnoreCase);

    private static void AppendToolLike(
        RemoteTranscriptTurn turn,
        ChatMessage message,
        ref RemoteTranscriptItem? toolGroup,
        IReadOnlySet<string>? runningBackgroundToolCallIds)
    {
        if (IsQuestion(message))
        {
            toolGroup = null;
            turn.Items.Add(new RemoteTranscriptItem
            {
                Id = message.Id.ToString("N"),
                Kind = RemoteProtocol.ItemKinds.Question,
                Timestamp = message.Timestamp,
                Question = new RemoteQuestion
                {
                    QuestionId = message.QuestionId ?? message.Id.ToString("N"),
                    Text = RemoteProtocol.TruncateForMobile(
                               message.QuestionText ?? message.Content,
                               RemoteProtocol.MobileQuestionTextLimit) ?? "",
                    Options = ParseOptions(message.QuestionOptions),
                    AllowFreeText = message.QuestionAllowFreeText ?? true,
                    AllowMultiSelect = message.QuestionAllowMultiSelect ?? false,
                    IsAnswered = !string.IsNullOrWhiteSpace(message.ToolOutput),
                    Answer = RemoteProtocol.TruncateForMobile(
                        message.ToolOutput,
                        RemoteProtocol.MobileQuestionAnswerLimit)
                }
            });
            return;
        }

        var call = BuildToolCall(message, runningBackgroundToolCallIds);

        // announce_file is how Lumi hands the user a produced file. The desktop turns it into an
        // attachment chip; rendering it as a raw tool card (which is what falling through to the
        // group below did) buries the deliverable inside collapsed JSON.
        if (string.Equals(message.ToolName, "announce_file", StringComparison.OrdinalIgnoreCase))
        {
            var path = ExtractJsonField(message.Content, "filePath");
            if (!string.IsNullOrWhiteSpace(path))
            {
                toolGroup = null;
                turn.Items.Add(new RemoteTranscriptItem
                {
                    Id = message.Id.ToString("N"),
                    Kind = RemoteProtocol.ItemKinds.File,
                    Timestamp = message.Timestamp,
                    Attachments =
                    [
                        new RemoteAttachment
                        {
                            // The phone downloads through the authenticated chat/message route; the
                            // desktop's absolute filesystem path is neither useful nor disclosed.
                            Path = "",
                            FileName = SafeFileName(path!),
                            Extension = SafeExtension(path!),
                            MessageId = message.Id
                        }
                    ]
                });
                return;
            }
        }

        if (message.ToolName is { Length: > 0 } toolName && TerminalTools.Contains(toolName))
        {
            toolGroup = null;
            turn.Items.Add(new RemoteTranscriptItem
            {
                Id = message.Id.ToString("N"),
                Kind = RemoteProtocol.ItemKinds.Terminal,
                Label = call.DisplayName,
                Text = RemoteProtocol.TruncateForMobile(
                    message.Content,
                    RemoteProtocol.MobileTerminalTextLimit),
                Status = call.Status,
                DurationMs = call.DurationMs,
                Timestamp = message.Timestamp,
                Tools = [call]
            });
            return;
        }

        // Consecutive tool calls collapse into one expandable group, matching the desktop transcript.
        if (toolGroup is null)
        {
            toolGroup = new RemoteTranscriptItem
            {
                Id = message.Id.ToString("N"),
                Kind = RemoteProtocol.ItemKinds.ToolGroup,
                Timestamp = message.Timestamp,
                Tools = []
            };
            turn.Items.Add(toolGroup);
        }

        toolGroup.Tools!.Add(call);
        toolGroup.Label = toolGroup.Tools.Count == 1
            ? call.DisplayName
            : string.Format(CultureInfo.CurrentCulture, "{0} steps", toolGroup.Tools.Count);
        toolGroup.Status = toolGroup.Tools.Any(tool => tool.Status == "InProgress")
            ? "InProgress"
            : toolGroup.Tools.Any(tool => tool.Status == "Failed") ? "Failed" : "Completed";
        toolGroup.DurationMs = toolGroup.Tools.Sum(tool => tool.DurationMs ?? 0);
    }

    private static RemoteTranscriptItem? BuildCompactActivityForMessage(
        IReadOnlyList<ChatMessage> messages,
        Guid messageId,
        string? workingDirectory,
        IReadOnlySet<string>? runningBackgroundToolCallIds)
    {
        if (!TryFindLogicalTurn(messages, messageId, out var start, out var end))
            return null;

        var technicalMessages = new List<ChatMessage>();
        for (var index = start; index < end; index++)
        {
            var candidate = messages[index];
            if (candidate.Role == "reasoning"
                || candidate.Role == "tool"
                   && !IsQuestion(candidate)
                   && !IsAnnouncedFile(candidate))
            {
                technicalMessages.Add(candidate);
            }
        }

        if (technicalMessages.Count == 0)
            return null;

        var anchor = technicalMessages[0];
        var fileChanges = BuildFileChanges(technicalMessages, workingDirectory);
        var activity = new RemoteTranscriptItem
        {
            Id = $"activity-{anchor.Id:N}",
            Kind = RemoteProtocol.ItemKinds.Activity,
            ActivityId = anchor.Id.ToString("N"),
            Status = "Completed",
            ActionCount = 0,
            DetailVersion = ComputeActivityDetailVersion(technicalMessages),
            FileChangeCount = fileChanges.Count,
            FileChanges = fileChanges
        };

        RemoteToolCall? latestRunningTool = null;
        var hasReasoning = false;
        foreach (var message in technicalMessages)
        {
            if (message.Role == "reasoning")
            {
                hasReasoning = true;
                if (message.IsStreaming)
                    activity.Status = MergeActivityStatus(activity.Status, "InProgress");
                continue;
            }

            if (string.Equals(
                    message.ToolName,
                    ToolDisplayHelper.WorkspaceFileChangedToolName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var call = BuildToolCall(message, runningBackgroundToolCallIds);
            activity.ActionCount = (activity.ActionCount ?? 0) + 1;
            activity.DurationMs = (activity.DurationMs ?? 0) + (call.DurationMs ?? 0);
            activity.Status = MergeActivityStatus(activity.Status, call.Status);
            if (string.Equals(call.Status, "InProgress", StringComparison.Ordinal))
                latestRunningTool = call;
        }

        if (string.Equals(activity.Status, "InProgress", StringComparison.Ordinal)
            && latestRunningTool is null
            && hasReasoning)
        {
            activity.Label = "Thinking...";
        }
        else if ((activity.ActionCount ?? 0) == 0
                 && activity.FileChanges.Count == 0
                 && hasReasoning)
        {
            activity.Label = "Thought through the response";
        }
        else
        {
            RefreshActivityLabel(activity, latestRunningTool);
        }

        return activity;
    }

    private static long ComputeActivityDetailVersion(IReadOnlyList<ChatMessage> messages)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;

        foreach (var message in messages)
        {
            Add(message.Id.ToString("N"));
            Add(message.Role);
            Add(message.ToolName);
            Add(message.ToolStatus);
            Add(message.Content);
            Add(message.ToolOutput);
            Add(message.ToolDurationMs?.ToString(CultureInfo.InvariantCulture));
            Add(message.IsStreaming ? "1" : "0");
        }

        return unchecked((long)hash);

        void Add(string? value)
        {
            foreach (var character in value ?? "")
            {
                hash ^= character;
                hash *= prime;
            }
            hash ^= 0xff;
            hash *= prime;
        }
    }

    private static void RefreshActivityLabel(
        RemoteTranscriptItem activity,
        RemoteToolCall? latestRunningTool)
    {
        if (string.Equals(activity.Status, "InProgress", StringComparison.Ordinal))
        {
            activity.Label = latestRunningTool?.Category switch
            {
                "research" => "Researching...",
                "verify" => "Verifying...",
                "work" => "Making changes...",
                _ => latestRunningTool?.DisplayName is { Length: > 0 } label
                    ? $"{label}..."
                    : "Working..."
            };
            return;
        }

        activity.Label = activity.FileChanges is { Count: > 0 }
            ? activity.FileChanges.Count == 1 ? "1 file changed" : $"{activity.FileChanges.Count} files changed"
            : "Activity";
    }

    private static string MergeActivityStatus(string? current, string incoming)
    {
        if (string.Equals(current, "InProgress", StringComparison.Ordinal)
            || string.Equals(incoming, "InProgress", StringComparison.Ordinal))
        {
            return "InProgress";
        }

        if (string.Equals(current, "Failed", StringComparison.Ordinal)
            || string.Equals(incoming, "Failed", StringComparison.Ordinal))
        {
            return "Failed";
        }

        if (string.Equals(current, "Stopped", StringComparison.Ordinal)
            || string.Equals(incoming, "Stopped", StringComparison.Ordinal))
        {
            return "Stopped";
        }

        return "Completed";
    }

    private static RemoteToolCall BuildToolCall(
        ChatMessage message,
        IReadOnlySet<string>? runningBackgroundToolCallIds = null)
    {
        var toolName = message.ToolName ?? "tool";
        var (friendly, info) = ToolDisplayHelper.GetFriendlyToolDisplay(toolName, message.Author, message.Content);
        return new RemoteToolCall
        {
            Id = message.ToolCallId ?? message.Id.ToString("N"),
            Name = toolName,
            DisplayName = friendly,
            Input = RemoteProtocol.TruncateForMobile(
                info ?? message.Content,
                RemoteProtocol.MobileToolInputLimit),
            Output = RemoteProtocol.TruncateForMobile(
                message.ToolOutput,
                RemoteProtocol.MobileToolOutputLimit),
            Category = ClassifyActivityCategory(toolName, message.Content),
            Status = message.ToolCallId is { Length: > 0 } toolCallId
                     && runningBackgroundToolCallIds?.Contains(toolCallId) == true
                ? "InProgress"
                : message.ToolStatus ?? "Completed",
            DurationMs = message.ToolDurationMs
        };
    }

    private static RemoteToolCall BuildActivityToolCall(
        ChatMessage message,
        IReadOnlySet<string>? runningBackgroundToolCallIds)
    {
        var call = BuildToolCall(message, runningBackgroundToolCallIds);
        call.Input = RemoteProtocol.TruncateForMobile(
            SanitizeActivityToolInput(message),
            RemoteProtocol.MobileActivityToolInputLimit);
        call.Output = RemoteProtocol.TruncateForMobile(
            message.ToolOutput,
            RemoteProtocol.MobileActivityToolOutputLimit);
        return call;
    }

    private static string? SanitizeActivityToolInput(ChatMessage message)
    {
        if (!ToolDisplayHelper.IsSubagentTool(message.ToolName)
            || string.IsNullOrWhiteSpace(message.Content))
        {
            return message.Content;
        }

        try
        {
            using var document = JsonDocument.Parse(message.Content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return BuildSafeSubagentActivityInput(message.Content);

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (string.Equals(
                            property.Name,
                            "reasoning",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    property.WriteTo(writer);
                }
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return BuildSafeSubagentActivityInput(message.Content);
        }
    }

    private static string BuildSafeSubagentActivityInput(string content)
    {
        var description = ToolDisplayHelper.GetSubagentDescription(content)
                          ?? ToolDisplayHelper.ExtractJsonField(content, "description")
                          ?? "Subagent activity";
        var name = ToolDisplayHelper.ExtractJsonField(content, "agentDisplayName")
                   ?? ToolDisplayHelper.ExtractJsonField(content, "agentName");
        return string.IsNullOrWhiteSpace(name)
            ? description
            : $"{name}: {description}";
    }

    private static string ClassifyActivityCategory(string toolName, string? input)
    {
        var normalized = toolName.Trim().ToLowerInvariant();
        if (normalized.Contains("search", StringComparison.Ordinal)
            || normalized.Contains("fetch", StringComparison.Ordinal)
            || normalized.Contains("find", StringComparison.Ordinal)
            || normalized.Contains("read", StringComparison.Ordinal)
            || normalized is "view" or "glob" or "rg" or "grep"
            || normalized.StartsWith("browser_", StringComparison.Ordinal)
            || normalized.StartsWith("lumi_browser_", StringComparison.Ordinal))
        {
            return "research";
        }

        if (normalized.Contains("test", StringComparison.Ordinal)
            || normalized.Contains("review", StringComparison.Ordinal)
            || normalized.Contains("lint", StringComparison.Ordinal)
            || ((normalized is "powershell" or "bash" or "shell" or "run_in_terminal")
                && LooksLikeVerificationCommand(input)))
        {
            return "verify";
        }

        if (ToolDisplayHelper.IsFileEditTool(normalized)
            || normalized.Contains("create", StringComparison.Ordinal)
            || normalized.Contains("edit", StringComparison.Ordinal)
            || normalized.Contains("write", StringComparison.Ordinal)
            || normalized.Contains("apply", StringComparison.Ordinal)
            || normalized.Contains("upload", StringComparison.Ordinal)
            || normalized.StartsWith("manage_", StringComparison.Ordinal)
            || normalized.StartsWith("configure_", StringComparison.Ordinal))
        {
            return "work";
        }

        return "other";
    }

    private static bool LooksLikeVerificationCommand(string? input)
    {
        var command = ToolDisplayHelper.ExtractJsonField(input, "command") ?? input ?? "";
        return command.Contains(" test", StringComparison.OrdinalIgnoreCase)
               || command.StartsWith("test", StringComparison.OrdinalIgnoreCase)
               || command.Contains(" build", StringComparison.OrdinalIgnoreCase)
               || command.StartsWith("build", StringComparison.OrdinalIgnoreCase)
               || command.Contains(" lint", StringComparison.OrdinalIgnoreCase)
               || command.Contains(" check", StringComparison.OrdinalIgnoreCase)
               || command.Contains(" verify", StringComparison.OrdinalIgnoreCase);
    }

    private static List<RemoteFileChange> BuildFileChanges(
        IReadOnlyList<ChatMessage> messages,
        string? workingDirectory)
    {
        var changes = new List<RemoteFileChange>();
        var indexes = new Dictionary<string, int>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        foreach (var message in messages)
        {
            if (string.Equals(message.ToolStatus, "Failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message.ToolStatus, "Stopped", StringComparison.OrdinalIgnoreCase))
                continue;

            var toolName = message.ToolName ?? "";
            if (string.Equals(
                    toolName,
                    ToolDisplayHelper.WorkspaceFileChangedToolName,
                    StringComparison.Ordinal))
            {
                var path = ToolDisplayHelper.ExtractJsonField(message.Content, "filePath")
                           ?? ToolDisplayHelper.ExtractJsonField(message.Content, "path");
                var operation = NormalizeFileOperation(
                    ToolDisplayHelper.ExtractJsonField(message.Content, "operation"));
                AddFileChange(path, operation, 0, 0, authoritative: true);
                continue;
            }

            if (toolName is "delete_file" or "delete" or "rm")
            {
                AddFileChange(
                    ToolDisplayHelper.ExtractJsonField(message.Content, "filePath")
                    ?? ToolDisplayHelper.ExtractJsonField(message.Content, "path"),
                    "Deleted",
                    0,
                    0);
                continue;
            }

            if (!ToolDisplayHelper.IsFileEditTool(toolName))
                continue;

            foreach (var diff in ToolDisplayHelper.ExtractAllDiffs(toolName, message.Content))
            {
                var operation = toolName == "apply_patch" && diff.NewText is null
                    ? "Deleted"
                    : ToolDisplayHelper.IsFileCreateTool(toolName)
                      || toolName == "apply_patch" && diff.OldText is null
                        ? "Created"
                        : "Modified";
                AddFileChange(
                    diff.FilePath,
                    operation,
                    CountLines(diff.NewText),
                    CountLines(diff.OldText));
            }
        }

        return changes;

        void AddFileChange(
            string? rawPath,
            string operation,
            int linesAdded,
            int linesRemoved,
            bool authoritative = false)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return;

            var canonicalPath = BuildCanonicalFileChangePath(rawPath, workingDirectory);
            var displayPath = BuildDisplayFileChangePath(rawPath, workingDirectory);
            if (canonicalPath.Length == 0 || displayPath.Length == 0)
                return;

            if (indexes.TryGetValue(canonicalPath, out var existingIndex))
            {
                var existing = changes[existingIndex];
                if (authoritative)
                {
                    existing.Operation = operation;
                }
                else
                {
                    existing.Operation = MergeFileOperation(existing.Operation, operation);
                }
                existing.LinesAdded += linesAdded;
                existing.LinesRemoved += linesRemoved;
                return;
            }

            indexes[canonicalPath] = changes.Count;
            changes.Add(new RemoteFileChange
            {
                Path = displayPath,
                FileName = SafeFileName(displayPath),
                Operation = operation,
                LinesAdded = linesAdded,
                LinesRemoved = linesRemoved
            });
        }
    }

    private static string MergeFileOperation(string current, string incoming)
    {
        if (string.Equals(incoming, "Deleted", StringComparison.Ordinal))
            return "Deleted";
        if (string.Equals(current, "Created", StringComparison.Ordinal)
            || string.Equals(incoming, "Created", StringComparison.Ordinal))
        {
            return "Created";
        }

        return "Modified";
    }

    private static string NormalizeFileOperation(string? operation) =>
        operation?.Trim().ToLowerInvariant() switch
        {
            "create" or "created" => "Created",
            "delete" or "deleted" or "remove" or "removed" => "Deleted",
            _ => "Modified"
        };

    private static string BuildDisplayFileChangePath(string rawPath, string? workingDirectory)
    {
        var path = rawPath.Trim();
        try
        {
            if (!string.IsNullOrWhiteSpace(workingDirectory)
                && Path.IsPathFullyQualified(path))
            {
                var relative = Path.GetRelativePath(workingDirectory, path);
                if (!relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !string.Equals(relative, "..", StringComparison.Ordinal)
                    && !Path.IsPathFullyQualified(relative))
                {
                    return relative.Replace('\\', '/');
                }
            }
        }
        catch
        {
            // A display path must never make transcript projection fail.
        }

        if (!Path.IsPathFullyQualified(path))
            return path.Replace('\\', '/');
        return SafeFileName(path);
    }

    private static string BuildCanonicalFileChangePath(
        string rawPath,
        string? workingDirectory)
    {
        try
        {
            var path = rawPath.Trim();
            var fullPath = Path.IsPathFullyQualified(path)
                ? Path.GetFullPath(path)
                : !string.IsNullOrWhiteSpace(workingDirectory)
                    ? Path.GetFullPath(Path.Combine(workingDirectory, path))
                    : Path.GetFullPath(path);
            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return rawPath.Trim().Replace('\\', '/');
        }
    }

    private static int CountLines(string? value) =>
        string.IsNullOrEmpty(value) ? 0 : value.Count(static character => character == '\n') + 1;

    private static RemoteTranscriptItem BuildUserItem(ChatMessage message) => new()
    {
        Id = message.Id.ToString("N"),
        Kind = RemoteProtocol.ItemKinds.User,
        Text = RemoteProtocol.TruncateForMobile(
            message.Content,
            RemoteProtocol.MobileUserTextLimit),
        Author = message.Author,
        RequestId = message.RemoteRequestId,
        Timestamp = message.Timestamp,
        SteerState = message.SteerDelivery == MessageSteerState.None
            ? null
            : message.SteerDelivery.ToString(),
        Attachments = BuildAttachments(message.Attachments)
    };

    private static RemoteTranscriptItem BuildAssistantItem(ChatMessage message) => new()
    {
        Id = message.Id.ToString("N"),
        Kind = RemoteProtocol.ItemKinds.Assistant,
        Text = RemoteProtocol.TruncateForMobile(
            message.Content,
            RemoteProtocol.MobileAssistantTextLimit),
        Author = message.Author,
        Timestamp = message.Timestamp,
        IsStreaming = message.IsStreaming,
        Model = message.Model,
        LinkedChatId = message.LinkedChatId,
        Label = message.LinkedChatTitle,
        Sources = BuildSources(message.Sources)
    };

    private static RemoteTranscriptItem BuildReasoningItem(ChatMessage message) => new()
    {
        Id = message.Id.ToString("N"),
        Kind = RemoteProtocol.ItemKinds.Reasoning,
        Label = "Thinking",
        Text = RemoteProtocol.TruncateForMobile(
            message.Content,
            RemoteProtocol.MobileReasoningTextLimit),
        Timestamp = message.Timestamp,
        IsStreaming = message.IsStreaming
    };

    private static RemoteTranscript BoundTranscript(RemoteTranscript transcript)
    {
        transcript.Title = BoundRequired(
            transcript.Title,
            RemoteProtocol.MobileTranscriptTitleLimit);
        transcript.RevisionEpoch = BoundOptional(
            transcript.RevisionEpoch,
            RemoteProtocol.MobileIdentifierLimit);
        transcript.Status = BoundStatus(transcript.Status);

        foreach (var turn in transcript.Turns)
        {
            turn.Id = BoundRequired(turn.Id, RemoteProtocol.MobileIdentifierLimit);
            foreach (var item in turn.Items)
            {
                item.Id = BoundRequired(item.Id, RemoteProtocol.MobileIdentifierLimit);
                item.Kind = BoundRequired(item.Kind, RemoteProtocol.MobileIdentifierLimit);
                item.Text = BoundOptional(item.Text, ItemTextLimit(item.Kind));
                item.Author = BoundOptional(item.Author, RemoteProtocol.MobileMetadataTextLimit);
                item.SteerState = BoundOptional(
                    item.SteerState,
                    RemoteProtocol.MobileStatusValueLimit);
                item.Label = BoundOptional(item.Label, RemoteProtocol.MobileMetadataTextLimit);
                item.Status = BoundOptional(item.Status, RemoteProtocol.MobileStatusValueLimit);
                item.Model = BoundOptional(item.Model, RemoteProtocol.MobileStatusValueLimit);
                item.ActivityId = BoundOptional(
                    item.ActivityId,
                    RemoteProtocol.MobileIdentifierLimit);
                item.Tools = BoundTools(item.Id, item.Tools);
                item.FileChanges = BoundFileChanges(item.FileChanges);
                item.Attachments = BoundAttachments(item.Attachments);
                item.Sources = BoundSources(item.Sources);

                if (item.Question is { } question)
                {
                    question.QuestionId = BoundRequired(
                        question.QuestionId,
                        RemoteProtocol.MobileIdentifierLimit);
                    question.Text = BoundRequired(
                        question.Text,
                        RemoteProtocol.MobileQuestionTextLimit);
                    question.Options = BoundOptions(question.Options);
                    question.Answer = BoundOptional(
                        question.Answer,
                        RemoteProtocol.MobileQuestionAnswerLimit);
                }
            }
        }

        return transcript;
    }

    private static RemoteTranscript EnforceTranscriptWireLimit(RemoteTranscript transcript)
    {
        if (SerializedTranscriptFits(transcript))
            return transcript;

        var minimumLength = RemoteProtocol.MobileTruncationMarker.Length + 1;
        for (var pass = 0; pass < 12; pass++)
        {
            var changed = CompactTranscriptStrings(transcript, minimumLength);
            if (SerializedTranscriptFits(transcript))
                return transcript;
            if (!changed)
                break;
        }

        // This is only reachable for an adversarial wire shape whose bounded collections and
        // strings still overflow the hard response ceiling. Keep the cursor/status envelope and make
        // the omitted activity explicit rather than sending an oversized response or failing the GET.
        var omittedItems = transcript.Turns.Sum(turn => turn.Items.Count);
        transcript.Status = new RemoteChatStatus
        {
            ChatId = transcript.Status.ChatId,
            IsBusy = transcript.Status.IsBusy,
            IsStreaming = transcript.Status.IsStreaming,
            ContextCurrentTokens = transcript.Status.ContextCurrentTokens,
            ContextTokenLimit = transcript.Status.ContextTokenLimit,
            UsesWorktree = transcript.Status.UsesWorktree
        };
        transcript.Turns =
        [
            new RemoteTranscriptTurn
            {
                Id = "mobile-wire-limit",
                Items =
                [
                    new RemoteTranscriptItem
                    {
                        Id = "mobile-wire-limit",
                        Kind = RemoteProtocol.ItemKinds.Error,
                        Text = FormattableString.Invariant(
                            $"{omittedItems} activity items omitted on mobile because their metadata exceeded the response limit. Open desktop for full activity.")
                    }
                ]
            }
        ];

        return transcript;
    }

    private static bool SerializedTranscriptFits(RemoteTranscript transcript)
    {
        using var stream = new CountingWriteStream();
        JsonSerializer.Serialize(
            stream,
            transcript,
            RemoteJsonContext.Default.RemoteTranscript);
        return stream.Length <= RemoteProtocol.MobileTranscriptJsonByteLimit;
    }

    private static RemoteActivityDetails EnforceActivityWireLimit(RemoteActivityDetails details)
    {
        if (SerializedActivityFits(details))
            return details;

        var minimumLength = RemoteProtocol.MobileTruncationMarker.Length + 1;
        for (var pass = 0; pass < 12; pass++)
        {
            var changed = false;
            foreach (var tool in details.Tools)
            {
                tool.Input = CompactOptional(tool.Input, minimumLength, ref changed);
                tool.Output = CompactOptional(tool.Output, minimumLength, ref changed);
            }
            foreach (var fileChange in details.FileChanges)
            {
                fileChange.Path = CompactRequired(fileChange.Path, minimumLength, ref changed);
                fileChange.FileName = CompactRequired(
                    fileChange.FileName,
                    minimumLength,
                    ref changed);
            }

            if (SerializedActivityFits(details))
                return details;
            if (!changed)
                break;
        }

        var omittedTools = 0;
        var omittedToolStatuses = new List<string>();
        while (details.Tools.Count > 1 && !SerializedActivityFits(details))
        {
            var removeIndex = details.Tools.FindLastIndex(tool =>
                string.Equals(tool.Status, "Completed", StringComparison.Ordinal));
            if (removeIndex < 0)
                removeIndex = details.Tools.Count - 1;
            omittedToolStatuses.Add(details.Tools[removeIndex].Status);
            details.Tools.RemoveAt(removeIndex);
            omittedTools++;
        }

        var omittedFiles = 0;
        while (details.FileChanges.Count > 1 && !SerializedActivityFits(details))
        {
            details.FileChanges.RemoveAt(details.FileChanges.Count - 1);
            omittedFiles++;
        }

        if (omittedTools > 0)
        {
            details.Tools.Add(new RemoteToolCall
            {
                Id = "omitted-activity-tools-wire-limit",
                Name = "omitted",
                DisplayName = $"{omittedTools} actions omitted to fit the mobile response limit",
                Category = "other",
                Status = AggregateActivityStatus(omittedToolStatuses)
            });
        }
        if (omittedFiles > 0)
        {
            details.FileChanges.Add(new RemoteFileChange
            {
                Path = $"{omittedFiles} more files omitted",
                FileName = "More files",
                Operation = "Omitted"
            });
        }

        if (!SerializedActivityFits(details))
        {
            var totalOmitted = details.Tools.Count + details.FileChanges.Count;
            details.Tools =
            [
                new RemoteToolCall
                {
                    Id = "activity-details-wire-limit",
                    Name = "omitted",
                    DisplayName = $"{totalOmitted} activity details omitted on mobile",
                    Category = "other",
                    Status = "Completed"
                }
            ];
            details.FileChanges = [];
        }

        return details;
    }

    private static bool SerializedActivityFits(RemoteActivityDetails details)
    {
        using var stream = new CountingWriteStream();
        JsonSerializer.Serialize(
            stream,
            details,
            RemoteJsonContext.Default.RemoteActivityDetails);
        return stream.Length <= RemoteProtocol.MaxActivityJsonBytes;
    }

    private static bool CompactTranscriptStrings(RemoteTranscript transcript, int minimumLength)
    {
        var changed = false;
        transcript.Title = CompactRequired(transcript.Title, minimumLength, ref changed);
        transcript.Status = CompactStatus(transcript.Status, minimumLength, ref changed);

        foreach (var turn in transcript.Turns)
        {
            turn.Id = CompactRequired(turn.Id, minimumLength, ref changed);
            foreach (var item in turn.Items)
            {
                item.Id = CompactRequired(item.Id, minimumLength, ref changed);
                item.Text = CompactOptional(item.Text, minimumLength, ref changed);
                item.Author = CompactOptional(item.Author, minimumLength, ref changed);
                item.SteerState = CompactOptional(item.SteerState, minimumLength, ref changed);
                item.Label = CompactOptional(item.Label, minimumLength, ref changed);
                item.Status = CompactOptional(item.Status, minimumLength, ref changed);
                item.Model = CompactOptional(item.Model, minimumLength, ref changed);
                item.ActivityId = CompactOptional(item.ActivityId, minimumLength, ref changed);

                if (item.Tools is { } tools)
                {
                    foreach (var tool in tools)
                    {
                        tool.Id = CompactRequired(tool.Id, minimumLength, ref changed);
                        tool.Name = CompactRequired(tool.Name, minimumLength, ref changed);
                        tool.DisplayName = CompactOptional(
                            tool.DisplayName,
                            minimumLength,
                            ref changed);
                        tool.Input = CompactOptional(tool.Input, minimumLength, ref changed);
                        tool.Output = CompactOptional(tool.Output, minimumLength, ref changed);
                        tool.Category = CompactRequired(tool.Category, minimumLength, ref changed);
                        tool.Status = CompactRequired(tool.Status, minimumLength, ref changed);
                    }
                }

                if (item.FileChanges is { } fileChanges)
                {
                    foreach (var fileChange in fileChanges)
                    {
                        fileChange.Path = CompactRequired(
                            fileChange.Path,
                            minimumLength,
                            ref changed);
                        fileChange.FileName = CompactRequired(
                            fileChange.FileName,
                            minimumLength,
                            ref changed);
                        fileChange.Operation = CompactRequired(
                            fileChange.Operation,
                            minimumLength,
                            ref changed);
                    }
                }

                if (item.Attachments is { } attachments)
                {
                    foreach (var attachment in attachments)
                    {
                        attachment.Path = CompactRequired(
                            attachment.Path,
                            minimumLength,
                            ref changed);
                        attachment.FileName = CompactRequired(
                            attachment.FileName,
                            minimumLength,
                            ref changed);
                        attachment.Extension = CompactOptional(
                            attachment.Extension,
                            minimumLength,
                            ref changed);
                    }
                }

                if (item.Sources is { } sources)
                {
                    foreach (var source in sources)
                    {
                        source.Title = CompactRequired(
                            source.Title,
                            minimumLength,
                            ref changed);
                        source.Snippet = CompactOptional(
                            source.Snippet,
                            minimumLength,
                            ref changed);
                        source.Url = CompactOptional(source.Url, minimumLength, ref changed);
                    }
                }

                if (item.Question is { } question)
                {
                    question.QuestionId = CompactRequired(
                        question.QuestionId,
                        minimumLength,
                        ref changed);
                    question.Text = CompactRequired(question.Text, minimumLength, ref changed);
                    for (var index = 0; index < question.Options.Count; index++)
                    {
                        question.Options[index] = CompactRequired(
                            question.Options[index],
                            minimumLength,
                            ref changed);
                    }

                    question.Answer = CompactOptional(
                        question.Answer,
                        minimumLength,
                        ref changed);
                }
            }
        }

        return changed;
    }

    private static RemoteChatStatus CompactStatus(
        RemoteChatStatus status,
        int minimumLength,
        ref bool changed)
    {
        status.StatusText = CompactOptional(status.StatusText, minimumLength, ref changed);
        status.Model = CompactOptional(status.Model, minimumLength, ref changed);
        status.PlanContent = CompactOptional(status.PlanContent, minimumLength, ref changed);
        CompactStrings(status.Suggestions, minimumLength, ref changed);
        status.Quality = CompactOptional(status.Quality, minimumLength, ref changed);
        CompactStrings(status.QualityLevels, minimumLength, ref changed);
        status.ContextWindowTier = CompactOptional(
            status.ContextWindowTier,
            minimumLength,
            ref changed);
        CompactStrings(status.ContextWindowTiers, minimumLength, ref changed);
        status.AgentName = CompactOptional(status.AgentName, minimumLength, ref changed);
        status.AgentGlyph = CompactOptional(status.AgentGlyph, minimumLength, ref changed);
        status.ProjectName = CompactOptional(status.ProjectName, minimumLength, ref changed);
        CompactStrings(status.SkillNames, minimumLength, ref changed);
        CompactStrings(status.McpNames, minimumLength, ref changed);
        return status;
    }

    private static void CompactStrings(
        IList<string> values,
        int minimumLength,
        ref bool changed)
    {
        for (var index = 0; index < values.Count; index++)
            values[index] = CompactRequired(values[index], minimumLength, ref changed);
    }

    private static string CompactRequired(
        string value,
        int minimumLength,
        ref bool changed) =>
        CompactOptional(value, minimumLength, ref changed) ?? "";

    private static string? CompactOptional(
        string? value,
        int minimumLength,
        ref bool changed)
    {
        if (value is null || value.Length <= minimumLength)
            return value;

        changed = true;
        var compactedLength = Math.Max(minimumLength, value.Length / 2);
        return RemoteProtocol.TruncateForMobile(value, compactedLength);
    }

    private static RemoteChatStatus BoundStatus(RemoteChatStatus status) =>
        new()
        {
            ChatId = status.ChatId,
            IsBusy = status.IsBusy,
            IsStreaming = status.IsStreaming,
            StatusText = BoundOptional(status.StatusText, RemoteProtocol.MobileStatusTextLimit),
            Model = BoundOptional(status.Model, RemoteProtocol.MobileStatusValueLimit),
            ContextCurrentTokens = status.ContextCurrentTokens,
            ContextTokenLimit = status.ContextTokenLimit,
            PlanContent = BoundOptional(status.PlanContent, RemoteProtocol.MobilePlanTextLimit),
            Suggestions = BoundStrings(
                status.Suggestions,
                RemoteProtocol.MobileMetadataTextLimit),
            Quality = BoundOptional(status.Quality, RemoteProtocol.MobileStatusValueLimit),
            QualityLevels = BoundStrings(
                status.QualityLevels,
                RemoteProtocol.MobileStatusValueLimit),
            ContextWindowTier = BoundOptional(
                status.ContextWindowTier,
                RemoteProtocol.MobileStatusValueLimit),
            ContextWindowTiers = BoundStrings(
                status.ContextWindowTiers,
                RemoteProtocol.MobileStatusValueLimit),
            AgentName = BoundOptional(status.AgentName, RemoteProtocol.MobileMetadataTextLimit),
            AgentId = status.AgentId,
            AgentGlyph = BoundOptional(status.AgentGlyph, RemoteProtocol.MobileStatusValueLimit),
            ProjectName = BoundOptional(status.ProjectName, RemoteProtocol.MobileMetadataTextLimit),
            ProjectId = status.ProjectId,
            UsesWorktree = status.UsesWorktree,
            SkillNames = BoundStrings(
                status.SkillNames,
                RemoteProtocol.MobileMetadataTextLimit),
            McpNames = BoundStrings(
                status.McpNames,
                RemoteProtocol.MobileMetadataTextLimit),
            AvailableAgents = BoundChips(status.AvailableAgents),
            AvailableSkills = BoundChips(status.AvailableSkills),
            AvailableMcps = BoundChips(status.AvailableMcps),
            AvailableProjects = BoundChips(status.AvailableProjects),
            HasComposerCatalogs = status.HasComposerCatalogs
        };

    private static List<RemoteChip> BoundChips(IEnumerable<RemoteChip> source) =>
        source.Take(RemoteProtocol.MobileStatusCollectionCountLimit)
            .Select(chip => new RemoteChip
            {
                Name = BoundRequired(chip.Name, RemoteProtocol.MobileMetadataTextLimit),
                Glyph = BoundOptional(chip.Glyph, RemoteProtocol.MobileStatusValueLimit),
                Description = BoundOptional(chip.Description, RemoteProtocol.MobileMetadataTextLimit),
                Value = BoundOptional(chip.Value, RemoteProtocol.MobileIdentifierLimit)
            })
            .ToList();

    private static List<string> BoundStrings(
        IEnumerable<string?> source,
        int stringLimit)
    {
        var bounded = new List<string>(RemoteProtocol.MobileStatusCollectionCountLimit);
        foreach (var value in source)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            bounded.Add(BoundRequired(value, stringLimit));
            if (bounded.Count == RemoteProtocol.MobileStatusCollectionCountLimit)
                break;
        }

        return bounded;
    }

    private static List<RemoteToolCall>? BoundTools(
        string itemId,
        IReadOnlyList<RemoteToolCall>? tools)
    {
        if (tools is not { Count: > 0 })
            return null;

        var hasOmitted = tools.Count > RemoteProtocol.MobileToolCallCountLimit;
        var retainedCount = hasOmitted
            ? RemoteProtocol.MobileToolCallCountLimit - 1
            : tools.Count;
        var bounded = new List<RemoteToolCall>(
            retainedCount + (hasOmitted ? 1 : 0));

        for (var index = 0; index < retainedCount; index++)
            bounded.Add(BoundTool(tools[index]));

        if (hasOmitted)
        {
            var omitted = tools.Count - retainedCount;
            bounded.Add(new RemoteToolCall
            {
                Id = BoundRequired(
                    $"{itemId}-omitted-tools",
                    RemoteProtocol.MobileIdentifierLimit),
                Name = "omitted",
                DisplayName = FormattableString.Invariant(
                    $"{omitted} more tool calls omitted on mobile"),
                Status = "Completed"
            });
        }

        return bounded;
    }

    private static RemoteToolCall BoundTool(RemoteToolCall tool) =>
        new()
        {
            Id = BoundRequired(tool.Id, RemoteProtocol.MobileIdentifierLimit),
            Name = BoundRequired(tool.Name, RemoteProtocol.MobileMetadataTextLimit),
            DisplayName = BoundOptional(
                tool.DisplayName,
                RemoteProtocol.MobileMetadataTextLimit),
            Input = BoundOptional(tool.Input, RemoteProtocol.MobileToolInputLimit),
            Output = BoundOptional(tool.Output, RemoteProtocol.MobileToolOutputLimit),
            Category = BoundRequired(tool.Category, RemoteProtocol.MobileStatusValueLimit),
            Status = BoundRequired(tool.Status, RemoteProtocol.MobileStatusValueLimit),
            DurationMs = tool.DurationMs
        };

    private static RemoteToolCall BoundActivityTool(RemoteToolCall tool) =>
        new()
        {
            Id = BoundRequired(tool.Id, RemoteProtocol.MobileIdentifierLimit),
            Name = BoundRequired(tool.Name, RemoteProtocol.MobileMetadataTextLimit),
            DisplayName = BoundOptional(
                tool.DisplayName,
                RemoteProtocol.MobileMetadataTextLimit),
            Input = BoundOptional(tool.Input, RemoteProtocol.MobileActivityToolInputLimit),
            Output = BoundOptional(tool.Output, RemoteProtocol.MobileActivityToolOutputLimit),
            Category = BoundRequired(tool.Category, RemoteProtocol.MobileStatusValueLimit),
            Status = BoundRequired(tool.Status, RemoteProtocol.MobileStatusValueLimit),
            DurationMs = tool.DurationMs
        };

    private static List<RemoteFileChange> BoundFileChanges(
        IReadOnlyList<RemoteFileChange>? fileChanges)
    {
        if (fileChanges is not { Count: > 0 })
            return [];

        var hasOmitted = fileChanges.Count > RemoteProtocol.MobileFileChangeCountLimit;
        var retainedCount = hasOmitted
            ? RemoteProtocol.MobileFileChangeCountLimit - 1
            : fileChanges.Count;
        var bounded = fileChanges
            .Take(retainedCount)
            .Select(static change => new RemoteFileChange
            {
                Path = BoundRequired(change.Path, RemoteProtocol.MobilePathLimit),
                FileName = BoundRequired(change.FileName, RemoteProtocol.MobileFileNameLimit),
                Operation = BoundRequired(
                    change.Operation,
                    RemoteProtocol.MobileStatusValueLimit),
                LinesAdded = Math.Max(0, change.LinesAdded),
                LinesRemoved = Math.Max(0, change.LinesRemoved)
            })
            .ToList();
        if (hasOmitted)
        {
            var omitted = fileChanges.Count - retainedCount;
            bounded.Add(new RemoteFileChange
            {
                Path = $"{omitted} more files",
                FileName = $"+{omitted} more files",
                Operation = "Omitted"
            });
        }

        return bounded;
    }

    private static List<RemoteAttachment>? BuildAttachments(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return null;

        var hasOmitted = paths.Count > RemoteProtocol.MobileAttachmentCountLimit;
        var retainedCount = hasOmitted
            ? RemoteProtocol.MobileAttachmentCountLimit - 1
            : paths.Count;
        var bounded = new List<RemoteAttachment>(
            retainedCount + (hasOmitted ? 1 : 0));

        for (var index = 0; index < retainedCount; index++)
        {
            var path = paths[index] ?? "";
            bounded.Add(new RemoteAttachment
            {
                Path = BoundRequired(path, RemoteProtocol.MobilePathLimit),
                FileName = SafeFileName(path),
                Extension = SafeExtension(path)
            });
        }

        if (hasOmitted)
            bounded.Add(OmittedAttachment(paths.Count - retainedCount));

        return bounded;
    }

    private static List<RemoteAttachment>? BoundAttachments(
        IReadOnlyList<RemoteAttachment>? attachments)
    {
        if (attachments is not { Count: > 0 })
            return null;

        var hasOmitted = attachments.Count > RemoteProtocol.MobileAttachmentCountLimit;
        var retainedCount = hasOmitted
            ? RemoteProtocol.MobileAttachmentCountLimit - 1
            : attachments.Count;
        var bounded = new List<RemoteAttachment>(
            retainedCount + (hasOmitted ? 1 : 0));

        for (var index = 0; index < retainedCount; index++)
        {
            var attachment = attachments[index];
            bounded.Add(new RemoteAttachment
            {
                Path = BoundRequired(attachment.Path, RemoteProtocol.MobilePathLimit),
                FileName = BoundRequired(
                    attachment.FileName,
                    RemoteProtocol.MobileFileNameLimit),
                Extension = BoundOptional(
                    attachment.Extension,
                    RemoteProtocol.MobileFileExtensionLimit),
                MessageId = attachment.MessageId
            });
        }

        if (hasOmitted)
            bounded.Add(OmittedAttachment(attachments.Count - retainedCount));

        return bounded;
    }

    private static RemoteAttachment OmittedAttachment(int omitted) =>
        new()
        {
            Path = "",
            FileName = FormattableString.Invariant(
                $"{omitted} more attachments omitted on mobile")
        };

    private static List<RemoteSource>? BuildSources(IReadOnlyList<SearchSource> sources)
    {
        if (sources.Count == 0)
            return null;

        var hasOmitted = sources.Count > RemoteProtocol.MobileSourceCountLimit;
        var retainedCount = hasOmitted
            ? RemoteProtocol.MobileSourceCountLimit - 1
            : sources.Count;
        var bounded = new List<RemoteSource>(
            retainedCount + (hasOmitted ? 1 : 0));

        for (var index = 0; index < retainedCount; index++)
        {
            var source = sources[index];
            bounded.Add(new RemoteSource
            {
                Title = BoundRequired(
                    source.Title,
                    RemoteProtocol.MobileSourceTitleLimit),
                Snippet = BoundOptional(
                    source.Snippet,
                    RemoteProtocol.MobileSourceSnippetLimit),
                Url = BoundOptional(source.Url, RemoteProtocol.MobileUrlLimit)
            });
        }

        if (hasOmitted)
            bounded.Add(OmittedSource(sources.Count - retainedCount));

        return bounded;
    }

    private static List<RemoteSource>? BoundSources(IReadOnlyList<RemoteSource>? sources)
    {
        if (sources is not { Count: > 0 })
            return null;

        var hasOmitted = sources.Count > RemoteProtocol.MobileSourceCountLimit;
        var retainedCount = hasOmitted
            ? RemoteProtocol.MobileSourceCountLimit - 1
            : sources.Count;
        var bounded = new List<RemoteSource>(
            retainedCount + (hasOmitted ? 1 : 0));

        for (var index = 0; index < retainedCount; index++)
        {
            var source = sources[index];
            bounded.Add(new RemoteSource
            {
                Title = BoundRequired(
                    source.Title,
                    RemoteProtocol.MobileSourceTitleLimit),
                Snippet = BoundOptional(
                    source.Snippet,
                    RemoteProtocol.MobileSourceSnippetLimit),
                Url = BoundOptional(source.Url, RemoteProtocol.MobileUrlLimit)
            });
        }

        if (hasOmitted)
            bounded.Add(OmittedSource(sources.Count - retainedCount));

        return bounded;
    }

    private static RemoteSource OmittedSource(int omitted) =>
        new()
        {
            Title = FormattableString.Invariant(
                $"{omitted} more sources omitted on mobile")
        };

    private static List<string> ParseOptions(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
            return [];

        var source = optionsJson.AsSpan();
        var index = 0;
        SkipWhitespace(source, ref index);
        if (index >= source.Length || source[index++] != '[')
            return [];

        SkipWhitespace(source, ref index);
        if (index < source.Length && source[index] == ']')
        {
            index++;
            SkipWhitespace(source, ref index);
            return [];
        }

        var retainedLimit = RemoteProtocol.MobileQuestionOptionCountLimit - 1;
        var options = new List<string>(RemoteProtocol.MobileQuestionOptionCountLimit);
        var meaningfulCount = 0;

        while (index < source.Length)
        {
            var capture = options.Count < retainedLimit;
            string? value;
            bool meaningful;

            if (source[index] == '"')
            {
                if (!TryReadJsonString(
                        source,
                        ref index,
                        capture,
                        RemoteProtocol.MobileQuestionOptionLimit,
                        out value,
                        out meaningful))
                {
                    return [];
                }
            }
            else
            {
                var valueStart = index;
                if (!TrySkipJsonValue(source, ref index))
                    return [];

                var raw = source[valueStart..index].Trim();
                meaningful = !raw.IsEmpty
                             && !raw.Equals("null".AsSpan(), StringComparison.Ordinal);
                value = capture && meaningful
                    ? BoundRequired(raw, RemoteProtocol.MobileQuestionOptionLimit)
                    : null;
            }

            if (meaningful)
            {
                meaningfulCount++;
                if (capture && value is not null)
                    options.Add(value);
            }

            SkipWhitespace(source, ref index);
            if (index >= source.Length)
                return [];

            if (source[index] == ']')
            {
                index++;
                break;
            }

            if (source[index++] != ',')
                return [];

            SkipWhitespace(source, ref index);
            if (index >= source.Length || source[index] == ']')
                return [];
        }

        SkipWhitespace(source, ref index);
        if (index != source.Length)
            return [];

        var omitted = meaningfulCount - options.Count;
        if (omitted > 0)
        {
            options.Add(FormattableString.Invariant(
                $"[{omitted} more options omitted on mobile]"));
        }

        return options;
    }

    private static List<string> BoundOptions(IReadOnlyList<string> options)
    {
        var hasOmitted = options.Count > RemoteProtocol.MobileQuestionOptionCountLimit;
        var retainedCount = hasOmitted
            ? RemoteProtocol.MobileQuestionOptionCountLimit - 1
            : options.Count;
        var bounded = new List<string>(retainedCount + (hasOmitted ? 1 : 0));

        for (var index = 0; index < retainedCount; index++)
        {
            bounded.Add(BoundRequired(
                options[index],
                RemoteProtocol.MobileQuestionOptionLimit));
        }

        if (hasOmitted)
        {
            bounded.Add(FormattableString.Invariant(
                $"[{options.Count - retainedCount} more options omitted on mobile]"));
        }

        return bounded;
    }

    private static bool TryReadJsonString(
        ReadOnlySpan<char> source,
        ref int index,
        bool capture,
        int maxCharacters,
        out string? value,
        out bool meaningful)
    {
        value = null;
        meaningful = false;
        if (index >= source.Length || source[index++] != '"')
            return false;

        StringBuilder? builder = capture ? new StringBuilder(Math.Min(maxCharacters, 256)) : null;
        var decodedLength = 0;
        var hasMeaningfulCharacter = false;

        while (index < source.Length)
        {
            var character = source[index++];
            if (character == '"')
            {
                if (capture)
                    value = FinishBoundedString(builder!, decodedLength, maxCharacters);
                meaningful = hasMeaningfulCharacter;
                return true;
            }

            if (character == '\\')
            {
                if (index >= source.Length)
                    return false;

                var escape = source[index++];
                switch (escape)
                {
                    case '"':
                    case '\\':
                    case '/':
                        Append(escape);
                        break;
                    case 'b':
                        Append('\b');
                        break;
                    case 'f':
                        Append('\f');
                        break;
                    case 'n':
                        Append('\n');
                        break;
                    case 'r':
                        Append('\r');
                        break;
                    case 't':
                        Append('\t');
                        break;
                    case 'u':
                        if (!TryReadHexCharacter(source, ref index, out var decoded))
                            return false;
                        Append(decoded);
                        break;
                    default:
                        return false;
                }

                continue;
            }

            if (character < ' ')
                return false;

            Append(character);
        }

        return false;

        void Append(char character)
        {
            decodedLength++;
            hasMeaningfulCharacter |= !char.IsWhiteSpace(character);
            if (capture && builder!.Length < maxCharacters)
                builder.Append(character);
        }
    }

    private static bool TryReadHexCharacter(
        ReadOnlySpan<char> source,
        ref int index,
        out char value)
    {
        value = default;
        if (index + 4 > source.Length)
            return false;

        var code = 0;
        for (var offset = 0; offset < 4; offset++)
        {
            var digit = source[index + offset];
            var valueOfDigit = digit switch
            {
                >= '0' and <= '9' => digit - '0',
                >= 'a' and <= 'f' => digit - 'a' + 10,
                >= 'A' and <= 'F' => digit - 'A' + 10,
                _ => -1
            };
            if (valueOfDigit < 0)
                return false;

            code = (code << 4) + valueOfDigit;
        }

        index += 4;
        value = (char)code;
        return true;
    }

    private static bool TrySkipJsonValue(ReadOnlySpan<char> source, ref int index)
    {
        var start = index;
        var depth = 0;
        var inString = false;
        var escaped = false;

        while (index < source.Length)
        {
            var character = source[index];
            if (inString)
            {
                index++;
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '\\')
                    escaped = true;
                else if (character == '"')
                    inString = false;
                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    index++;
                    break;
                case '{':
                case '[':
                    depth++;
                    index++;
                    break;
                case '}':
                    if (depth == 0)
                        return false;
                    depth--;
                    index++;
                    break;
                case ']':
                    if (depth == 0)
                        return index > start;
                    depth--;
                    index++;
                    break;
                case ',':
                    if (depth == 0)
                        return index > start;
                    index++;
                    break;
                default:
                    index++;
                    break;
            }
        }

        return index > start && depth == 0 && !inString;
    }

    private static void SkipWhitespace(ReadOnlySpan<char> source, ref int index)
    {
        while (index < source.Length && char.IsWhiteSpace(source[index]))
            index++;
    }

    private static string FinishBoundedString(
        StringBuilder builder,
        int decodedLength,
        int maxCharacters)
    {
        if (decodedLength <= maxCharacters)
            return builder.ToString();

        var prefixLength = maxCharacters - RemoteProtocol.MobileTruncationMarker.Length;
        if (prefixLength > 0 && char.IsHighSurrogate(builder[prefixLength - 1]))
            prefixLength--;

        return string.Concat(
            builder.ToString(0, prefixLength),
            RemoteProtocol.MobileTruncationMarker);
    }

    private static int ItemTextLimit(string kind) => kind switch
    {
        RemoteProtocol.ItemKinds.User => RemoteProtocol.MobileUserTextLimit,
        RemoteProtocol.ItemKinds.Reasoning => RemoteProtocol.MobileReasoningTextLimit,
        RemoteProtocol.ItemKinds.Terminal => RemoteProtocol.MobileTerminalTextLimit,
        RemoteProtocol.ItemKinds.Question => RemoteProtocol.MobileQuestionTextLimit,
        _ => RemoteProtocol.MobileAssistantTextLimit
    };

    private static string BoundRequired(string? value, int maxCharacters) =>
        RemoteProtocol.TruncateForMobile(value ?? "", maxCharacters) ?? "";

    private static string BoundRequired(ReadOnlySpan<char> value, int maxCharacters)
    {
        if (value.Length <= maxCharacters)
            return value.ToString();

        var prefixLength = maxCharacters - RemoteProtocol.MobileTruncationMarker.Length;
        if (prefixLength > 0 && char.IsHighSurrogate(value[prefixLength - 1]))
            prefixLength--;

        return string.Concat(
            value[..prefixLength],
            RemoteProtocol.MobileTruncationMarker.AsSpan());
    }

    private static string? BoundOptional(string? value, int maxCharacters) =>
        RemoteProtocol.TruncateForMobile(value, maxCharacters);

    private static string SafeFileName(string path)
    {
        try
        {
            return BoundRequired(
                Path.GetFileName(path.AsSpan()),
                RemoteProtocol.MobileFileNameLimit);
        }
        catch (ArgumentException)
        {
            return BoundRequired(path, RemoteProtocol.MobileFileNameLimit);
        }
    }

    /// <summary>
    /// Pulls one string field out of a tool's JSON arguments. Tool payloads are whatever the model
    /// emitted, so anything unparseable simply yields null rather than failing the projection.
    /// </summary>
    internal static string? ExtractJsonField(string? json, string field)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && document.RootElement.TryGetProperty(field, out var value)
                   && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? SafeExtension(string path)
    {
        try
        {
            var extension = Path.GetExtension(path.AsSpan());
            if (!extension.IsEmpty && extension[0] == '.')
                extension = extension[1..];

            return extension.IsEmpty
                ? null
                : BoundRequired(extension, RemoteProtocol.MobileFileExtensionLimit);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private sealed class CountingWriteStream : Stream
    {
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            _length += count;

        public override void Write(ReadOnlySpan<byte> buffer) =>
            _length += buffer.Length;

        public override void WriteByte(byte value) =>
            _length++;
    }
}
