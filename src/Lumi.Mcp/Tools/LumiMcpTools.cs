using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace Lumi.Mcp;

[McpServerToolType]
public sealed class LumiMcpTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly LumiMcpToolHandlers _handlers = new();

    [McpServerTool(Name = "lumi_status", ReadOnly = true)]
    [Description("Check whether a Debug Lumi instance with the Lumi debug bridge is running and return app/chat state.")]
    public Task<string> Status(
        [Description("Optional debug bridge instance id to target.")] string? targetInstanceId = null,
        [Description("Optional Lumi app/debug bridge process id to target.")] int? targetProcessId = null,
        [Description("Optional app-data root to target. Useful for isolated debug instances.")] string? targetAppDataDir = null)
        => InvokeAsync("lumi_status", Args(
            ("targetInstanceId", targetInstanceId),
            ("targetProcessId", targetProcessId),
            ("targetAppDataDir", targetAppDataDir)));

    [McpServerTool(Name = "lumi_list_instances", ReadOnly = true)]
    [Description("List discovered Debug Lumi bridge instances so agents can choose a target process or instance id.")]
    public Task<string> ListInstances(
        [Description("Include stale/offline discovery files.")] bool includeOffline = false,
        [Description("Optional app-data root filter.")] string? appDataDir = null)
        => InvokeAsync("lumi_list_instances", Args(
            ("includeOffline", includeOffline),
            ("appDataDir", appDataDir)));

    [McpServerTool(Name = "lumi_launch")]
    [Description("Launch a Debug Lumi instance without onboarding by default, optionally into the transcript fixture harness, then wait for the Lumi debug bridge.")]
    public Task<string> Launch(
        [Description("Launch mode: normal or fixture.")] string? harness = "fixture",
        [Description("When true, return the existing debug bridge if one is already running.")] bool reuseExisting = true,
        [Description("How long to wait for the bridge to appear.")] int timeoutMs = 90000,
        [Description("Optional app-data root for isolated smoke tests. Passed to Lumi as LUMI_APPDATA_DIR.")] string? appDataDir = null,
        [Description("When true, force Lumi's onboarding flow instead of skipping it for debug automation.")] bool showOnboarding = false,
        [Description("Optional existing debug bridge instance id to target when reuseExisting is true.")] string? targetInstanceId = null,
        [Description("Optional existing Lumi app/debug bridge process id to target when reuseExisting is true.")] int? targetProcessId = null,
        [Description("Optional existing app-data root to target when reuseExisting is true. Defaults to appDataDir when provided.")] string? targetAppDataDir = null)
        => InvokeAsync("lumi_launch", Args(
            ("harness", harness),
            ("reuseExisting", reuseExisting),
            ("timeoutMs", timeoutMs),
            ("appDataDir", appDataDir),
            ("showOnboarding", showOnboarding),
            ("targetInstanceId", targetInstanceId),
            ("targetProcessId", targetProcessId),
            ("targetAppDataDir", targetAppDataDir ?? appDataDir)));

    [McpServerTool(Name = "lumi_navigate")]
    [Description("Navigate Lumi to a top-level page such as chat, jobs, projects, skills, lumis, memories, mcp, or settings.")]
    public Task<string> Navigate(
        [Description("Page name to navigate to.")] string? page = null,
        [Description("Optional numeric page index from the debug map.")] int? index = null,
        [Description("Optional settings subpage: profile, general, appearance, chat, models, privacy, about.")] string? settingsPage = null,
        [Description("Optional debug bridge instance id to target.")] string? targetInstanceId = null,
        [Description("Optional Lumi app/debug bridge process id to target.")] int? targetProcessId = null)
        => InvokeAsync("lumi_navigate", Args(
            ("page", page),
            ("index", index),
            ("settingsPage", settingsPage),
            ("targetInstanceId", targetInstanceId),
            ("targetProcessId", targetProcessId)));

    [McpServerTool(Name = "lumi_list_chats", ReadOnly = true)]
    [Description("List persisted Lumi chats, optionally filtered by title/id.")]
    public Task<string> ListChats(
        [Description("Optional title/id filter.")] string? query = null,
        [Description("Exact or partial chat title filter.")] string? title = null,
        [Description("Require exact title match when title is provided.")] bool exactTitle = false,
        [Description("Optional project id.")] string? projectId = null,
        [Description("Optional project name.")] string? projectName = null,
        [Description("Optional Lumi agent id.")] string? agentId = null,
        [Description("Optional Lumi agent name.")] string? agentName = null,
        [Description("Include chats with no loaded messages/session metadata.")] bool includeEmpty = true,
        [Description("Filter running chats. Null means either.")] bool? isRunning = null,
        [Description("Filter unread chats. Null means either.")] bool? hasUnread = null,
        [Description("ISO timestamp lower bound for CreatedAt.")] string? createdAfter = null,
        [Description("ISO timestamp upper bound for CreatedAt.")] string? createdBefore = null,
        [Description("ISO timestamp lower bound for UpdatedAt.")] string? updatedAfter = null,
        [Description("ISO timestamp upper bound for UpdatedAt.")] string? updatedBefore = null,
        [Description("Sort by updatedAt, createdAt, title, or messageCount.")] string? sortBy = "updatedAt",
        [Description("Sort direction: asc or desc.")] string? sortDirection = "desc",
        [Description("Number of matching chats to skip.")] int offset = 0,
        [Description("Maximum chats to return.")] int limit = 25,
        [Description("Optional debug bridge instance id to target.")] string? targetInstanceId = null,
        [Description("Optional Lumi app/debug bridge process id to target.")] int? targetProcessId = null)
        => InvokeAsync("lumi_list_chats", Args(
            ("query", query),
            ("title", title),
            ("exactTitle", exactTitle),
            ("projectId", projectId),
            ("projectName", projectName),
            ("agentId", agentId),
            ("agentName", agentName),
            ("includeEmpty", includeEmpty),
            ("isRunning", isRunning),
            ("hasUnread", hasUnread),
            ("createdAfter", createdAfter),
            ("createdBefore", createdBefore),
            ("updatedAfter", updatedAfter),
            ("updatedBefore", updatedBefore),
            ("sortBy", sortBy),
            ("sortDirection", sortDirection),
            ("offset", offset),
            ("limit", limit),
            ("targetInstanceId", targetInstanceId),
            ("targetProcessId", targetProcessId)));

    [McpServerTool(Name = "lumi_create_chat")]
    [Description("Create a persisted chat and optionally open it. Use this before testing sends so existing user chats are not touched accidentally.")]
    public Task<string> CreateChat(
        [Description("Chat title. Defaults to Debug chat.")] string? title = null,
        [Description("Optional project id.")] string? projectId = null,
        [Description("Optional project name.")] string? projectName = null,
        [Description("Optional Lumi agent id.")] string? agentId = null,
        [Description("Optional Lumi agent name.")] string? agentName = null,
        [Description("Optional skill names to activate.")] string[]? skillNames = null,
        [Description("Optional MCP server names to activate.")] string[]? mcpServerNames = null,
        [Description("Optional model id.")] string? model = null,
        [Description("Optional reasoning effort.")] string? reasoningEffort = null,
        [Description("Whether to open the chat after creating it.")] bool open = true,
        [Description("Optional debug bridge instance id to target.")] string? targetInstanceId = null,
        [Description("Optional Lumi app/debug bridge process id to target.")] int? targetProcessId = null)
        => InvokeAsync("lumi_create_chat", Args(
            ("title", title),
            ("projectId", projectId),
            ("projectName", projectName),
            ("agentId", agentId),
            ("agentName", agentName),
            ("skillNames", skillNames),
            ("mcpServerNames", mcpServerNames),
            ("model", model),
            ("reasoningEffort", reasoningEffort),
            ("open", open),
            ("targetInstanceId", targetInstanceId),
            ("targetProcessId", targetProcessId)));

    [McpServerTool(Name = "lumi_open_chat")]
    [Description("Open a chat by id or title.")]
    public Task<string> OpenChat(
        [Description("Chat id.")] string? chatId = null,
        [Description("Exact or partial chat title.")] string? title = null,
        [Description("Require exact title matching.")] bool exactTitle = false,
        [Description("Optional debug bridge instance id to target.")] string? targetInstanceId = null,
        [Description("Optional Lumi app/debug bridge process id to target.")] int? targetProcessId = null)
        => InvokeAsync("lumi_open_chat", Args(
            ("chatId", chatId),
            ("title", title),
            ("exactTitle", exactTitle),
            ("targetInstanceId", targetInstanceId),
            ("targetProcessId", targetProcessId)));

    [McpServerTool(Name = "lumi_send_message")]
    [Description("Send a message through Lumi's real chat pipeline, optionally opening/creating a target chat first and waiting for idle.")]
    public Task<string> SendMessage(
        [Description("Message to send.")] string message,
        [Description("Optional chat id to open before sending.")] string? chatId = null,
        [Description("Optional chat title to open before sending.")] string? title = null,
        [Description("Start from a fresh draft chat before sending.")] bool newChat = false,
        [Description("Wait for streaming to finish before returning.")] bool waitForIdle = true,
        [Description("Wait timeout in milliseconds.")] int timeoutMs = 180000,
        [Description("Polling interval while waiting for idle.")] int pollIntervalMs = 250,
        [Description("Return filtered activity after sending.")] bool returnActivity = false,
        [Description("Return filtered transcript after sending.")] bool returnTranscript = false,
        [Description("Maximum transcript/activity messages to return.")] int maxMessages = 20,
        [Description("Include tool output previews in returned transcript/activity.")] bool includeToolOutput = false,
        [Description("Maximum message content characters in returned transcript/activity.")] int maxContentChars = 4000,
        [Description("Maximum tool output preview characters in returned transcript/activity.")] int maxToolOutputChars = 1000,
        [Description("Optional debug bridge instance id to target.")] string? targetInstanceId = null,
        [Description("Optional Lumi app/debug bridge process id to target.")] int? targetProcessId = null)
        => InvokeAsync("lumi_send_message", Args(
            ("message", message),
            ("chatId", chatId),
            ("title", title),
            ("newChat", newChat),
            ("waitForIdle", waitForIdle),
            ("timeoutMs", timeoutMs),
            ("pollIntervalMs", pollIntervalMs),
            ("returnActivity", returnActivity),
            ("returnTranscript", returnTranscript),
            ("maxMessages", maxMessages),
            ("includeToolOutput", includeToolOutput),
            ("maxContentChars", maxContentChars),
            ("maxToolOutputChars", maxToolOutputChars),
            ("targetInstanceId", targetInstanceId),
            ("targetProcessId", targetProcessId)));

    [McpServerTool(Name = "lumi_wait_for_idle", ReadOnly = true)]
    [Description("Wait until a chat is no longer busy/streaming.")]
    public Task<string> WaitForIdle(
        [Description("Optional chat id. Defaults to the active chat.")] string? chatId = null,
        [Description("Wait timeout in milliseconds.")] int timeoutMs = 180000,
        [Description("Polling interval in milliseconds.")] int pollIntervalMs = 250,
        [Description("Optional debug bridge instance id to target.")] string? targetInstanceId = null,
        [Description("Optional Lumi app/debug bridge process id to target.")] int? targetProcessId = null)
        => InvokeAsync("lumi_wait_for_idle", Args(
            ("chatId", chatId),
            ("timeoutMs", timeoutMs),
            ("pollIntervalMs", pollIntervalMs),
            ("targetInstanceId", targetInstanceId),
            ("targetProcessId", targetProcessId)));

    [McpServerTool(Name = "lumi_read_transcript", ReadOnly = true)]
    [Description("Read a chat transcript as structured message JSON.")]
    public Task<string> ReadTranscript(
        [Description("Optional chat id. Defaults to active chat.")] string? chatId = null,
        [Description("Optional exact or partial title.")] string? title = null,
        [Description("Require exact title matching.")] bool exactTitle = false,
        [Description("Roles to include: user, assistant, tool, reasoning, error.")] string[]? roles = null,
        [Description("Tool names to include when role/tool messages are queried.")] string[]? toolNames = null,
        [Description("Tool statuses to include, e.g. InProgress, Completed, Failed, Stopped.")] string[]? toolStatuses = null,
        [Description("Full-text query over message content, tool output, sources, and files.")] string? query = null,
        [Description("ISO timestamp lower bound.")] string? afterTimestamp = null,
        [Description("ISO timestamp upper bound.")] string? beforeTimestamp = null,
        [Description("Return messages after this message id.")] string? afterMessageId = null,
        [Description("Return messages before this message id.")] string? beforeMessageId = null,
        [Description("Skip this many matched messages after sorting.")] int offset = 0,
        [Description("Maximum messages to return.")] int maxMessages = 200,
        [Description("Sort direction before paging: asc or desc.")] string? sortDirection = "desc",
        [Description("Return page in chronological order after descending paging.")] bool returnChronological = true,
        [Description("Compact output: less metadata and shorter defaults.")] bool compact = false,
        [Description("Include message content.")] bool includeContent = true,
        [Description("Include metadata fields such as author/model/tool ids.")] bool includeMetadata = true,
        [Description("Include source chips.")] bool includeSources = true,
        [Description("Include file attachments.")] bool includeFiles = true,
        [Description("Include raw tool output previews.")] bool includeToolOutput = false,
        [Description("Maximum message content characters.")] int maxContentChars = 20000,
        [Description("Maximum tool output preview characters.")] int maxToolOutputChars = 2000,
        [Description("Optional debug bridge instance id to target.")] string? targetInstanceId = null,
        [Description("Optional Lumi app/debug bridge process id to target.")] int? targetProcessId = null)
        => InvokeAsync("lumi_read_transcript", Args(
            ("chatId", chatId),
            ("title", title),
            ("exactTitle", exactTitle),
            ("roles", roles),
            ("toolNames", toolNames),
            ("toolStatuses", toolStatuses),
            ("query", query),
            ("afterTimestamp", afterTimestamp),
            ("beforeTimestamp", beforeTimestamp),
            ("afterMessageId", afterMessageId),
            ("beforeMessageId", beforeMessageId),
            ("offset", offset),
            ("maxMessages", maxMessages),
            ("sortDirection", sortDirection),
            ("returnChronological", returnChronological),
            ("compact", compact),
            ("includeContent", includeContent),
            ("includeMetadata", includeMetadata),
            ("includeSources", includeSources),
            ("includeFiles", includeFiles),
            ("includeToolOutput", includeToolOutput),
            ("maxContentChars", maxContentChars),
            ("maxToolOutputChars", maxToolOutputChars),
            ("targetInstanceId", targetInstanceId),
            ("targetProcessId", targetProcessId)));

    [McpServerTool(Name = "lumi_read_activity", ReadOnly = true)]
    [Description("Read what Lumi did in a chat: app state, transcript, tool calls, sources, files, questions, and errors.")]
    public Task<string> ReadActivity(
        [Description("Optional chat id. Defaults to active chat.")] string? chatId = null,
        [Description("Optional exact or partial title.")] string? title = null,
        [Description("Require exact title matching.")] bool exactTitle = false,
        [Description("Sections to return: status, chat, summary, messages, toolCalls, sources, files, questions, errors, all.")] string[]? sections = null,
        [Description("Roles to include: user, assistant, tool, reasoning, error.")] string[]? roles = null,
        [Description("Tool names to include.")] string[]? toolNames = null,
        [Description("Tool statuses to include, e.g. InProgress, Completed, Failed, Stopped.")] string[]? toolStatuses = null,
        [Description("Full-text query over message content, tool output, sources, and files.")] string? query = null,
        [Description("ISO timestamp lower bound.")] string? afterTimestamp = null,
        [Description("ISO timestamp upper bound.")] string? beforeTimestamp = null,
        [Description("Return messages after this message id.")] string? afterMessageId = null,
        [Description("Return messages before this message id.")] string? beforeMessageId = null,
        [Description("Only include messages with source chips.")] bool onlyWithSources = false,
        [Description("Only include messages with file attachments or announced files.")] bool onlyWithFiles = false,
        [Description("Include streaming messages.")] bool includeStreaming = true,
        [Description("Skip this many matched messages after sorting.")] int offset = 0,
        [Description("Maximum messages to include.")] int maxMessages = 200,
        [Description("Sort direction before paging: asc or desc.")] string? sortDirection = "desc",
        [Description("Return page in chronological order after descending paging.")] bool returnChronological = true,
        [Description("Compact output: less metadata and shorter defaults.")] bool compact = false,
        [Description("Include message content.")] bool includeContent = true,
        [Description("Include metadata fields such as author/model/tool ids.")] bool includeMetadata = true,
        [Description("Include source chips in message payloads.")] bool includeSources = true,
        [Description("Include files in message payloads.")] bool includeFiles = true,
        [Description("Include raw tool output text/previews.")] bool includeToolOutput = false,
        [Description("Maximum message content characters.")] int maxContentChars = 20000,
        [Description("Maximum tool output preview characters when includeToolOutput is true.")] int maxToolOutputChars = 2000,
        [Description("Optional debug bridge instance id to target.")] string? targetInstanceId = null,
        [Description("Optional Lumi app/debug bridge process id to target.")] int? targetProcessId = null)
        => InvokeAsync("lumi_read_activity", Args(
            ("chatId", chatId),
            ("title", title),
            ("exactTitle", exactTitle),
            ("sections", sections),
            ("roles", roles),
            ("toolNames", toolNames),
            ("toolStatuses", toolStatuses),
            ("query", query),
            ("afterTimestamp", afterTimestamp),
            ("beforeTimestamp", beforeTimestamp),
            ("afterMessageId", afterMessageId),
            ("beforeMessageId", beforeMessageId),
            ("onlyWithSources", onlyWithSources),
            ("onlyWithFiles", onlyWithFiles),
            ("includeStreaming", includeStreaming),
            ("offset", offset),
            ("maxMessages", maxMessages),
            ("sortDirection", sortDirection),
            ("returnChronological", returnChronological),
            ("compact", compact),
            ("includeContent", includeContent),
            ("includeMetadata", includeMetadata),
            ("includeSources", includeSources),
            ("includeFiles", includeFiles),
            ("includeToolOutput", includeToolOutput),
            ("maxContentChars", maxContentChars),
            ("maxToolOutputChars", maxToolOutputChars),
            ("targetInstanceId", targetInstanceId),
            ("targetProcessId", targetProcessId)));

    [McpServerTool(Name = "lumi_load_fixture")]
    [Description("Open the Debug-only synthetic transcript fixture chat, useful for validating transcript UI changes without sending a real message.")]
    public Task<string> LoadFixture(
        [Description("Optional debug bridge instance id to target.")] string? targetInstanceId = null,
        [Description("Optional Lumi app/debug bridge process id to target.")] int? targetProcessId = null)
        => InvokeAsync("lumi_load_fixture", Args(
            ("targetInstanceId", targetInstanceId),
            ("targetProcessId", targetProcessId)));

    [McpServerTool(Name = "lumi_list_features", ReadOnly = true)]
    [Description("List Lumi features/configuration: projects, skills, lumis, MCP servers, memories, jobs, and settings.")]
    public Task<string> ListFeatures(
        [Description("Optional resource filter: projects, skills, lumis, mcps, memories, jobs, settings.")] string? resource = null,
        [Description("Text query over names/descriptions/content for the selected resource.")] string? query = null,
        [Description("Number of matched items to skip.")] int offset = 0,
        [Description("Maximum items to return per resource.")] int limit = 100,
        [Description("Optional debug bridge instance id to target.")] string? targetInstanceId = null,
        [Description("Optional Lumi app/debug bridge process id to target.")] int? targetProcessId = null)
        => InvokeAsync("lumi_list_features", Args(
            ("resource", resource),
            ("query", query),
            ("offset", offset),
            ("limit", limit),
            ("targetInstanceId", targetInstanceId),
            ("targetProcessId", targetProcessId)));

    [McpServerTool(Name = "lumi_configure_feature")]
    [Description("Create/update/delete Lumi projects, skills, Lumis, MCP servers, memories, jobs, or update settings via Lumi's feature manager.")]
    public Task<string> ConfigureFeature(
        [Description("Resource: projects, skills, lumis, mcps, memories, jobs, or settings.")] string resource,
        [Description("Action for managed resources: list, create, update, delete, pause, resume, run_now.")] string? action = null,
        [Description("Id/name/key for update/delete/list filtering.")] string? identifier = null,
        [Description("Name for create/update.")] string? name = null,
        [Description("Description for create/update.")] string? description = null,
        [Description("Skill or memory content.")] string? content = null,
        [Description("Project instructions.")] string? instructions = null,
        [Description("Project working directory.")] string? workingDirectory = null,
        [Description("Clear a project working directory.")] bool? clearWorkingDirectory = null,
        [Description("Project additional context directories.")] string[]? additionalContextDirectories = null,
        [Description("Clear project additional context directories.")] bool? clearAdditionalContextDirectories = null,
        [Description("Lumi agent system prompt.")] string? systemPrompt = null,
        [Description("Icon glyph for skills/Lumis.")] string? iconGlyph = null,
        [Description("Skill ids/names for Lumi agents.")] string[]? skillIdentifiers = null,
        [Description("Allowed tool names for Lumi agents or MCP server tool filters.")] string[]? toolNames = null,
        [Description("MCP server ids/names for Lumi agents.")] string[]? mcpServerIdentifiers = null,
        [Description("MCP server type: local or remote.")] string? serverType = null,
        [Description("Local MCP command.")] string? command = null,
        [Description("Local MCP command arguments.")] string[]? args = null,
        [Description("Remote MCP URL.")] string? url = null,
        [Description("Local MCP environment entries in KEY=VALUE form.")] string[]? envEntries = null,
        [Description("Remote MCP header entries in KEY=VALUE form.")] string[]? headerEntries = null,
        [Description("MCP timeout in milliseconds.")] int? timeout = null,
        [Description("Clear an MCP timeout.")] bool? clearTimeout = null,
        [Description("Enable/disable MCP servers or jobs.")] bool? isEnabled = null,
        [Description("Memory key.")] string? key = null,
        [Description("Memory category.")] string? category = null,
        [Description("Background job prompt/instructions.")] string? prompt = null,
        [Description("Background job chat id/title.")] string? chatIdentifier = null,
        [Description("Background job trigger type: time or script.")] string? triggerType = null,
        [Description("Background job schedule type.")] string? scheduleType = null,
        [Description("Interval minutes for interval jobs.")] int? intervalMinutes = null,
        [Description("Daily HH:mm time for daily jobs.")] string? dailyTime = null,
        [Description("Days of week for weekly jobs.")] string? daysOfWeek = null,
        [Description("Monthly day for monthly jobs.")] int? monthlyDay = null,
        [Description("Cron expression for cron jobs.")] string? cronExpression = null,
        [Description("One-shot run date/time.")] string? runAt = null,
        [Description("Script job content.")] string? scriptContent = null,
        [Description("Script language: powershell, python, node, or command.")] string? scriptLanguage = null,
        [Description("Whether a time job should pause after a successful invocation.")] bool? isTemporary = null,
        [Description("Queue a job immediately.")] bool? runNow = null,
        [Description("Optional list/search query.")] string? query = null,
        [Description("Settings: user name.")] string? userName = null,
        [Description("Settings: UI language code.")] string? language = null,
        [Description("Settings: dark theme.")] bool? isDarkTheme = null,
        [Description("Settings: compact density.")] bool? isCompactDensity = null,
        [Description("Settings: send with Enter.")] bool? sendWithEnter = null,
        [Description("Settings: show timestamps.")] bool? showTimestamps = null,
        [Description("Settings: show tool calls.")] bool? showToolCalls = null,
        [Description("Settings: show reasoning.")] bool? showReasoning = null,
        [Description("Settings: expand reasoning while streaming.")] bool? expandReasoningWhileStreaming = null,
        [Description("Settings: auto-generate chat titles.")] bool? autoGenerateTitles = null,
        [Description("Settings: preferred model id.")] string? preferredModel = null,
        [Description("Settings: reasoning effort.")] string? reasoningEffort = null,
        [Description("Settings: enable automatic memory saving.")] bool? enableMemoryAutoSave = null,
        [Description("Settings: enable automatic memory maintenance.")] bool? enableMemoryAutoMaintenance = null,
        [Description("Settings: auto-save chats.")] bool? autoSaveChats = null,
        [Description("Preview the intended configure operation without mutating Lumi data.")] bool dryRun = false,
        [Description("Optional debug bridge instance id to target.")] string? targetInstanceId = null,
        [Description("Optional Lumi app/debug bridge process id to target.")] int? targetProcessId = null)
    {
        var arguments = Args(
            ("resource", resource),
            ("action", action),
            ("identifier", identifier),
            ("name", name),
            ("description", description),
            ("content", content),
            ("instructions", instructions),
            ("workingDirectory", workingDirectory),
            ("clearWorkingDirectory", clearWorkingDirectory),
            ("additionalContextDirectories", additionalContextDirectories),
            ("clearAdditionalContextDirectories", clearAdditionalContextDirectories),
            ("systemPrompt", systemPrompt),
            ("iconGlyph", iconGlyph),
            ("skillIdentifiers", skillIdentifiers),
            ("toolNames", toolNames),
            ("mcpServerIdentifiers", mcpServerIdentifiers),
            ("serverType", serverType),
            ("command", command),
            ("args", args),
            ("url", url),
            ("envEntries", envEntries),
            ("headerEntries", headerEntries),
            ("timeout", timeout),
            ("clearTimeout", clearTimeout),
            ("isEnabled", isEnabled),
            ("key", key),
            ("category", category),
            ("prompt", prompt),
            ("chatIdentifier", chatIdentifier),
            ("triggerType", triggerType),
            ("scheduleType", scheduleType),
            ("intervalMinutes", intervalMinutes),
            ("dailyTime", dailyTime),
            ("daysOfWeek", daysOfWeek),
            ("monthlyDay", monthlyDay),
            ("cronExpression", cronExpression),
            ("runAt", runAt),
            ("scriptContent", scriptContent),
            ("scriptLanguage", scriptLanguage),
            ("isTemporary", isTemporary),
            ("runNow", runNow),
            ("query", query),
            ("dryRun", dryRun),
            ("targetInstanceId", targetInstanceId),
            ("targetProcessId", targetProcessId));

        var values = Args(
            ("userName", userName),
            ("language", language),
            ("isDarkTheme", isDarkTheme),
            ("isCompactDensity", isCompactDensity),
            ("sendWithEnter", sendWithEnter),
            ("showTimestamps", showTimestamps),
            ("showToolCalls", showToolCalls),
            ("showReasoning", showReasoning),
            ("expandReasoningWhileStreaming", expandReasoningWhileStreaming),
            ("autoGenerateTitles", autoGenerateTitles),
            ("preferredModel", preferredModel),
            ("reasoningEffort", reasoningEffort),
            ("enableMemoryAutoSave", enableMemoryAutoSave),
            ("enableMemoryAutoMaintenance", enableMemoryAutoMaintenance),
            ("autoSaveChats", autoSaveChats));
        if (values.Count > 0)
            arguments["values"] = values;

        return InvokeAsync("lumi_configure_feature", arguments);
    }

    [McpServerTool(Name = "lumi_run_harness")]
    [Description("Run one of Lumi's CLI harnesses: chat, mcp-native, or mcp-proxy.")]
    public Task<string> RunHarness(
        [Description("Harness kind: chat, mcp-native, or mcp-proxy.")] string kind,
        [Description("Timeout in milliseconds.")] int timeoutMs = 180000,
        [Description("Include stdout in the returned result.")] bool includeStdout = true,
        [Description("Include stderr in the returned result.")] bool includeStderr = true,
        [Description("Maximum characters per output stream.")] int maxOutputChars = 12000,
        [Description("Optional marker expected in stdout or stderr.")] string? expectedOutput = null)
        => InvokeAsync("lumi_run_harness", Args(
            ("kind", kind),
            ("timeoutMs", timeoutMs),
            ("includeStdout", includeStdout),
            ("includeStderr", includeStderr),
            ("maxOutputChars", maxOutputChars),
            ("expectedOutput", expectedOutput)));

    private async Task<string> InvokeAsync(string toolName, IReadOnlyDictionary<string, object?> arguments)
    {
        using var document = JsonSerializer.SerializeToDocument(arguments, JsonOptions);
        var result = await _handlers.HandleAsync(toolName, document.RootElement, CancellationToken.None).ConfigureAwait(false);
        return ToJson(result);
    }

    private static Dictionary<string, object?> Args(params (string Name, object? Value)[] values)
    {
        return values
            .Where(static pair => pair.Value is not null)
            .ToDictionary(static pair => pair.Name, static pair => pair.Value);
    }

    private static string ToJson(object? value)
    {
        return value is JsonElement element
            ? JsonSerializer.Serialize(element, JsonOptions)
            : JsonSerializer.Serialize(value, JsonOptions);
    }
}
