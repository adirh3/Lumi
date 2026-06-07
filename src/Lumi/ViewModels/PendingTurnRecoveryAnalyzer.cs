using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Services;

namespace Lumi.ViewModels;

internal sealed record RecoveredAssistantMessage(string Content);

internal enum PendingTurnTerminalState
{
    None,
    Idle,
    Error,
    Abort,
    Shutdown,
}

internal sealed class PendingTurnRecoveryAnalysis
{
    public static PendingTurnRecoveryAnalysis UserMessageNotObserved { get; } = new();

    public bool UserMessageObserved { get; init; }

    public PendingTurnTerminalState TerminalState { get; init; }

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<RecoveredAssistantMessage> AssistantMessages { get; init; } = [];

    public IReadOnlyCollection<string> CompletedToolCallIds { get; init; } = [];

    public IReadOnlyCollection<string> FailedToolCallIds { get; init; } = [];

    public int ActiveToolCount { get; init; }
}

internal static class PendingTurnRecoveryAnalyzer
{
    public static PendingTurnRecoveryAnalysis Analyze(
        IReadOnlyList<SessionEvent> events,
        int expectedSessionUserMessageCount)
    {
        var userMessagesSeen = 0;
        var turnStartIndex = -1;

        for (var i = 0; i < events.Count; i++)
        {
            if (events[i] is not UserMessageEvent)
                continue;

            userMessagesSeen++;
            if (userMessagesSeen != expectedSessionUserMessageCount)
                continue;

            turnStartIndex = i;
            break;
        }

        if (turnStartIndex < 0)
            return PendingTurnRecoveryAnalysis.UserMessageNotObserved;

        var assistantMessages = new List<RecoveredAssistantMessage>();
        var completedToolCallIds = new HashSet<string>();
        var failedToolCallIds = new HashSet<string>();
        var activeToolCallIds = new HashSet<string>();
        var externalToolCallIdByRequestId = new Dictionary<string, string>();
        var terminalState = PendingTurnTerminalState.None;
        string? errorMessage = null;

        for (var i = turnStartIndex + 1; i < events.Count; i++)
        {
            switch (events[i])
            {
                case AssistantMessageEvent assistantMessage
                    when string.IsNullOrWhiteSpace(assistantMessage.Data.ParentToolCallId)
                         && !string.IsNullOrWhiteSpace(assistantMessage.Data.Content):
                    assistantMessages.Add(new RecoveredAssistantMessage(assistantMessage.Data.Content));
                    break;

                case ToolExecutionStartEvent toolStart
                    when !string.IsNullOrWhiteSpace(toolStart.Data.ToolCallId):
                    activeToolCallIds.Add(toolStart.Data.ToolCallId);
                    break;

                case ToolExecutionCompleteEvent toolComplete
                    when !string.IsNullOrWhiteSpace(toolComplete.Data.ToolCallId):
                    activeToolCallIds.Remove(toolComplete.Data.ToolCallId);
                    if (toolComplete.Data.Success == true)
                        completedToolCallIds.Add(toolComplete.Data.ToolCallId);
                    else
                        failedToolCallIds.Add(toolComplete.Data.ToolCallId);
                    break;

                case ExternalToolRequestedEvent externalToolRequested
                    when !string.IsNullOrWhiteSpace(externalToolRequested.Data.RequestId)
                         && !string.IsNullOrWhiteSpace(externalToolRequested.Data.ToolCallId):
                    externalToolCallIdByRequestId[externalToolRequested.Data.RequestId] = externalToolRequested.Data.ToolCallId;
                    activeToolCallIds.Add(externalToolRequested.Data.ToolCallId);
                    break;

                case ExternalToolCompletedEvent externalToolCompleted
                    when !string.IsNullOrWhiteSpace(externalToolCompleted.Data.RequestId)
                         && externalToolCallIdByRequestId.TryGetValue(externalToolCompleted.Data.RequestId, out var externalToolCallId):
                    activeToolCallIds.Remove(externalToolCallId);
                    completedToolCallIds.Add(externalToolCallId);
                    externalToolCallIdByRequestId.Remove(externalToolCompleted.Data.RequestId);
                    break;

                case SessionIdleEvent:
                    terminalState = PendingTurnTerminalState.Idle;
                    break;

                case SessionErrorEvent sessionError:
                    terminalState = PendingTurnTerminalState.Error;
                    errorMessage = sessionError.Data.Message;
                    break;

                case AbortEvent:
                    terminalState = PendingTurnTerminalState.Abort;
                    break;

                case SessionShutdownEvent:
                    terminalState = PendingTurnTerminalState.Shutdown;
                    break;
            }
        }

        return new PendingTurnRecoveryAnalysis
        {
            UserMessageObserved = true,
            TerminalState = terminalState,
            ErrorMessage = errorMessage,
            AssistantMessages = assistantMessages,
            CompletedToolCallIds = completedToolCallIds,
            FailedToolCallIds = failedToolCallIds,
            ActiveToolCount = activeToolCallIds.Count,
        };
    }

    public static PendingTurnRecoveryAnalysis AnalyzePersistedLog(
        IEnumerable<string> lines,
        int expectedSessionUserMessageCount)
    {
        var state = new PersistedLogAnalysisState(expectedSessionUserMessageCount);
        foreach (var line in lines)
            ApplyPersistedLogLine(state, line);

        return BuildPersistedLogAnalysis(state);
    }

    public static int CountUserMessages(IReadOnlyList<SessionEvent> events)
    {
        var count = 0;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i] is UserMessageEvent)
                count++;
        }

        return count;
    }

    public static int CountPersistedLogUserMessages(IEnumerable<string> lines)
    {
        var count = 0;
        foreach (var line in lines)
        {
            if (IsPersistedUserMessageLine(line))
                count++;
        }

        return count;
    }

    public static async Task<PendingTurnRecoveryAnalysis?> TryAnalyzeSessionLogAsync(
        string? sessionId,
        int expectedSessionUserMessageCount,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        var logPath = GetSessionLogPath(sessionId);
        if (!File.Exists(logPath))
            return null;

        var state = new PersistedLogAnalysisState(expectedSessionUserMessageCount);
        try
        {
            using var stream = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync();
                if (line is null)
                    break;

                ApplyPersistedLogLine(state, line);
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return BuildPersistedLogAnalysis(state);
    }

    public static async Task<int?> TryCountSessionUserMessagesAsync(
        string? sessionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        var logPath = GetSessionLogPath(sessionId);
        if (!File.Exists(logPath))
            return null;

        var count = 0;
        try
        {
            using var stream = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync();
                if (line is null)
                    break;

                if (IsPersistedUserMessageLine(line))
                    count++;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return count;
    }

    public static PendingTurnRecoveryAnalysis Merge(
        PendingTurnRecoveryAnalysis? liveAnalysis,
        PendingTurnRecoveryAnalysis? persistedAnalysis)
    {
        if (liveAnalysis is null)
            return persistedAnalysis ?? PendingTurnRecoveryAnalysis.UserMessageNotObserved;

        if (persistedAnalysis is null)
            return liveAnalysis;

        var terminalState = persistedAnalysis.TerminalState != PendingTurnTerminalState.None
            ? persistedAnalysis.TerminalState
            : liveAnalysis.TerminalState;
        var errorMessage = terminalState == persistedAnalysis.TerminalState
            ? persistedAnalysis.ErrorMessage ?? liveAnalysis.ErrorMessage
            : liveAnalysis.ErrorMessage ?? persistedAnalysis.ErrorMessage;

        var completedToolCallIds = new HashSet<string>(liveAnalysis.CompletedToolCallIds);
        completedToolCallIds.UnionWith(persistedAnalysis.CompletedToolCallIds);

        var failedToolCallIds = new HashSet<string>(liveAnalysis.FailedToolCallIds);
        failedToolCallIds.UnionWith(persistedAnalysis.FailedToolCallIds);

        var assistantMessages = persistedAnalysis.AssistantMessages.Count >= liveAnalysis.AssistantMessages.Count
            ? persistedAnalysis.AssistantMessages
            : liveAnalysis.AssistantMessages;

        var activeToolCount = terminalState == PendingTurnTerminalState.None
            ? persistedAnalysis.UserMessageObserved
                ? persistedAnalysis.ActiveToolCount
                : liveAnalysis.ActiveToolCount
            : 0;

        return new PendingTurnRecoveryAnalysis
        {
            UserMessageObserved = liveAnalysis.UserMessageObserved || persistedAnalysis.UserMessageObserved,
            TerminalState = terminalState,
            ErrorMessage = errorMessage,
            AssistantMessages = assistantMessages,
            CompletedToolCallIds = completedToolCallIds,
            FailedToolCallIds = failedToolCallIds,
            ActiveToolCount = activeToolCount,
        };
    }

    private static string GetSessionLogPath(string sessionId)
        => ResolveSessionLogPath(sessionId, DataStore.CopilotConfigDir, GetLegacyCopilotConfigDir());

    internal static string ResolveSessionLogPath(string sessionId, string configDir, string legacyConfigDir)
    {
        var currentPath = BuildSessionLogPath(configDir, sessionId);
        if (File.Exists(currentPath))
            return currentPath;

        var legacyPath = BuildSessionLogPath(legacyConfigDir, sessionId);
        if (!string.Equals(currentPath, legacyPath, StringComparison.OrdinalIgnoreCase) && File.Exists(legacyPath))
            return legacyPath;

        return currentPath;
    }

    private static string BuildSessionLogPath(string configDir, string sessionId)
        => Path.Combine(configDir, "session-state", sessionId, "events.jsonl");

    private static string GetLegacyCopilotConfigDir()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");

    private static bool IsPersistedUserMessageLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("type", out var typeProperty)
                   && typeProperty.ValueKind == JsonValueKind.String
                   && string.Equals(typeProperty.GetString(), "user.message", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static PendingTurnRecoveryAnalysis BuildPersistedLogAnalysis(PersistedLogAnalysisState state)
    {
        if (state.UserMessagesSeen < state.ExpectedSessionUserMessageCount)
            return PendingTurnRecoveryAnalysis.UserMessageNotObserved;

        return new PendingTurnRecoveryAnalysis
        {
            UserMessageObserved = true,
            TerminalState = state.TerminalState,
            ErrorMessage = state.ErrorMessage,
            AssistantMessages = state.AssistantMessages,
            CompletedToolCallIds = state.CompletedToolCallIds,
            FailedToolCallIds = state.FailedToolCallIds,
            ActiveToolCount = state.TerminalState == PendingTurnTerminalState.None
                ? state.ActiveToolCallIds.Count
                : 0,
        };
    }

    private static void ApplyPersistedLogLine(PersistedLogAnalysisState state, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeProperty) || typeProperty.ValueKind != JsonValueKind.String)
                return;

            var eventType = typeProperty.GetString();
            if (string.IsNullOrWhiteSpace(eventType))
                return;

            if (eventType == "user.message")
            {
                state.UserMessagesSeen++;
                return;
            }

            if (state.UserMessagesSeen < state.ExpectedSessionUserMessageCount)
                return;

            root.TryGetProperty("data", out var data);
            switch (eventType)
            {
                case "assistant.message":
                    var parentToolCallId = TryGetString(data, "parentToolCallId");
                    var content = TryGetString(data, "content");
                    if (string.IsNullOrWhiteSpace(parentToolCallId) && !string.IsNullOrWhiteSpace(content))
                        state.AssistantMessages.Add(new RecoveredAssistantMessage(content));
                    break;

                case "tool.execution_start":
                    var startedToolCallId = TryGetString(data, "toolCallId");
                    if (!string.IsNullOrWhiteSpace(startedToolCallId))
                        state.ActiveToolCallIds.Add(startedToolCallId);
                    break;

                case "tool.execution_complete":
                    var completedToolCallId = TryGetString(data, "toolCallId");
                    if (string.IsNullOrWhiteSpace(completedToolCallId))
                        break;

                    state.ActiveToolCallIds.Remove(completedToolCallId);
                    if (TryGetBoolean(data, "success") == true)
                        state.CompletedToolCallIds.Add(completedToolCallId);
                    else
                        state.FailedToolCallIds.Add(completedToolCallId);
                    break;

                case "external_tool.requested":
                    var requestId = TryGetString(data, "requestId");
                    var externalToolCallId = TryGetString(data, "toolCallId");
                    if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(externalToolCallId))
                        break;

                    state.ExternalToolCallIdByRequestId[requestId] = externalToolCallId;
                    state.ActiveToolCallIds.Add(externalToolCallId);
                    break;

                case "external_tool.completed":
                    var completedRequestId = TryGetString(data, "requestId");
                    if (string.IsNullOrWhiteSpace(completedRequestId)
                        || !state.ExternalToolCallIdByRequestId.TryGetValue(completedRequestId, out var completedExternalToolCallId))
                    {
                        break;
                    }

                    state.ExternalToolCallIdByRequestId.Remove(completedRequestId);
                    state.ActiveToolCallIds.Remove(completedExternalToolCallId);
                    state.CompletedToolCallIds.Add(completedExternalToolCallId);
                    break;

                case "session.idle":
                    state.TerminalState = PendingTurnTerminalState.Idle;
                    break;

                case "session.error":
                    state.TerminalState = PendingTurnTerminalState.Error;
                    state.ErrorMessage = TryGetString(data, "message");
                    break;

                case "abort":
                    state.TerminalState = PendingTurnTerminalState.Abort;
                    break;

                case "session.shutdown":
                    state.TerminalState = PendingTurnTerminalState.Shutdown;
                    break;
            }
        }
        catch (JsonException)
        {
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool? TryGetBoolean(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var property)
               && (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
            ? property.GetBoolean()
            : null;
    }

    private sealed class PersistedLogAnalysisState(int expectedSessionUserMessageCount)
    {
        public int ExpectedSessionUserMessageCount { get; } = expectedSessionUserMessageCount;

        public int UserMessagesSeen { get; set; }

        public List<RecoveredAssistantMessage> AssistantMessages { get; } = [];

        public HashSet<string> CompletedToolCallIds { get; } = [];

        public HashSet<string> FailedToolCallIds { get; } = [];

        public HashSet<string> ActiveToolCallIds { get; } = [];

        public Dictionary<string, string> ExternalToolCallIdByRequestId { get; } = [];

        public PendingTurnTerminalState TerminalState { get; set; }

        public string? ErrorMessage { get; set; }
    }
}
