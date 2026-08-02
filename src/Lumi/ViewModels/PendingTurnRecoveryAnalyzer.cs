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

internal sealed record PendingTurnRecoveryLogSnapshot(
    PendingTurnRecoveryAnalysis Analysis,
    string LogPath,
    long ReadLength,
    long ObservedLength,
    DateTime LastWriteTimeUtc)
{
    public bool ReachedObservedEnd => ReadLength == ObservedLength;
}

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

    public bool AssistantTurnEnded { get; init; }

    public IReadOnlyList<RecoveredAssistantMessage> AssistantMessages { get; init; } = [];

    public IReadOnlyCollection<string> CompletedToolCallIds { get; init; } = [];

    public IReadOnlyCollection<string> FailedToolCallIds { get; init; } = [];

    public IReadOnlyCollection<string> StoppedToolCallIds { get; init; } = [];

    public int ActiveToolCount { get; init; }
}

/// <summary>
/// Result of mapping a "fork from here" cut point onto the server event log.
/// </summary>
/// <param name="Resolved">
/// False when the local and server turns could not be lined up. The caller must then fall back to
/// transcript replay rather than forking more history than the visible transcript shows.
/// </param>
/// <param name="Event">
/// The event to fork through, or null (when <paramref name="Resolved"/> is true) to fork the whole
/// conversation because the cut is at or past the last turn.
/// </param>
internal readonly record struct ForkCutSelection(bool Resolved, SessionEvent? Event)
{
    public static ForkCutSelection Unresolved { get; } = new(false, null);

    public static ForkCutSelection ForkEverything { get; } = new(true, null);
}

internal static class PendingTurnRecoveryAnalyzer
{
    private const string CompactEventTypePrefix = "{\"type\":\"";
    private const string CompactUserMessageEventPrefix = "{\"type\":\"user.message\"";

    public static PendingTurnRecoveryAnalysis Analyze(
        IReadOnlyList<SessionEvent> events,
        int expectedSessionUserMessageCount)
    {
        var state = new RecoveryAnalysisState(expectedSessionUserMessageCount);
        foreach (var sessionEvent in events)
        {
            if (sessionEvent is UserMessageEvent userMessage)
            {
                state.ObserveUserMessage(string.IsNullOrEmpty(userMessage.Data.Source));
                continue;
            }

            if (!state.CanProcessEvent)
                continue;

            switch (sessionEvent)
            {
                case AssistantTurnStartEvent:
                case AssistantMessageStartEvent:
                case AssistantMessageDeltaEvent:
                case AssistantStreamingDeltaEvent:
                case AssistantReasoningDeltaEvent:
                case AssistantReasoningEvent:
                    state.RecordContinuedActivity();
                    break;

                case AssistantMessageEvent assistantMessage:
#pragma warning disable CS0618 // ParentToolCallId is deprecated in GitHub.Copilot.SDK 1.0.1 with no replacement; still required to detect sub-agent assistant messages.
                    state.RecordAssistantMessage(
                        assistantMessage.Data.ParentToolCallId,
                        assistantMessage.Data.Content);
#pragma warning restore CS0618
                    break;

                case AssistantTurnEndEvent:
                    state.RecordAssistantTurnEnd();
                    break;

                case ToolExecutionStartEvent toolStart:
                    state.RecordToolActivity(toolStart.Data.ToolCallId);
                    break;

                case ToolExecutionCompleteEvent toolComplete:
                    state.RecordToolCompletion(
                        toolComplete.Data.ToolCallId,
                        toolComplete.Data.Success == true);
                    break;

                case ToolExecutionPartialResultEvent partialResult:
                    state.RecordToolActivity(partialResult.Data.ToolCallId);
                    break;

                case ToolExecutionProgressEvent progress:
                    state.RecordToolActivity(progress.Data.ToolCallId);
                    break;

                case ExternalToolRequestedEvent externalToolRequested:
                    state.RecordExternalToolRequest(
                        externalToolRequested.Data.RequestId,
                        externalToolRequested.Data.ToolCallId);
                    break;

                case ExternalToolCompletedEvent externalToolCompleted:
                    state.RecordExternalToolCompletion(externalToolCompleted.Data.RequestId);
                    break;

                case SessionBackgroundTasksChangedEvent:
                    state.InvalidateAssistantTurnEnd();
                    break;

                case SubagentStartedEvent subagentStarted:
                    state.RecordSubagentStarted(subagentStarted.Data.ToolCallId);
                    break;

                case SubagentCompletedEvent subagentCompleted:
                    state.RecordSubagentCompletion(subagentCompleted.Data.ToolCallId, success: true);
                    break;

                case SubagentFailedEvent subagentFailed:
                    state.RecordSubagentCompletion(subagentFailed.Data.ToolCallId, success: false);
                    break;

                case SubagentSelectedEvent:
                case SubagentDeselectedEvent:
                    state.RecordContinuedActivity();
                    break;

                case SessionIdleEvent:
                    state.RecordTerminalState(PendingTurnTerminalState.Idle);
                    break;

                case SessionErrorEvent sessionError:
                    state.RecordTerminalState(PendingTurnTerminalState.Error, sessionError.Data.Message);
                    break;

                case AbortEvent:
                    state.RecordTerminalState(PendingTurnTerminalState.Abort);
                    break;

                case SessionShutdownEvent:
                    state.RecordTerminalState(PendingTurnTerminalState.Shutdown);
                    break;
            }
        }

        return state.BuildAnalysis();
    }

    public static PendingTurnRecoveryAnalysis AnalyzePersistedLog(
        IEnumerable<string> lines,
        int expectedSessionUserMessageCount)
    {
        var state = new RecoveryAnalysisState(expectedSessionUserMessageCount);
        foreach (var line in lines)
            ApplyPersistedLogLine(state, line);

        return state.BuildAnalysis();
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

    /// <summary>
    /// Selects the server <c>user.message</c> event to truncate at when an edited turn is
    /// resent via History.Truncate. Only <em>genuine</em> user turns (typed by the user, with
    /// an empty <see cref="UserMessageData.Source"/>) correspond to a local user message; the
    /// SDK/CLI also emits <em>injected</em> user messages — e.g. a system-sourced priming
    /// message with empty content (<c>Source == "system"</c>) — that have no local counterpart.
    /// Counting those would shift the ordinal and truncate an earlier turn than the edited one,
    /// silently dropping a message the user still expects. This skips injected messages so the
    /// Nth genuine server user turn lines up with the Nth local user message.
    /// </summary>
    /// <param name="events">The ordered server event log.</param>
    /// <param name="retainedUserCount">The number of local user turns kept before the edited
    /// turn — i.e. the zero-based ordinal of the genuine user turn to truncate at.</param>
    /// <returns>The event whose id should be passed to History.Truncate, or <c>null</c> if the
    /// local and server user turns don't line up (caller should fall back to replay).</returns>
    public static UserMessageEvent? SelectEditTruncationTarget(IReadOnlyList<SessionEvent> events, int retainedUserCount)
    {
        if (events is null || retainedUserCount < 0)
            return null;

        var genuineSeen = 0;
        foreach (var sessionEvent in events)
        {
            if (sessionEvent is not UserMessageEvent userEvent
                || !string.IsNullOrEmpty(userEvent.Data?.Source))
                continue;

            if (genuineSeen == retainedUserCount)
                return userEvent;

            genuineSeen++;
        }

        return null;
    }

    /// <summary>
    /// Selects the server event a forked session should be cut at ("fork from here"), so the fork's
    /// server-side history ends exactly where the copied transcript does.
    /// </summary>
    /// <param name="events">The ordered server event log of the chat being forked.</param>
    /// <param name="retainedUserCount">The number of local user turns the fork keeps.</param>
    /// <remarks>
    /// The first turn the fork must NOT contain is the (<paramref name="retainedUserCount"/>)-th
    /// genuine user turn, so the cut is the event immediately before it — that keeps the retained
    /// turns and their answers while dropping everything after. Injected user messages are skipped
    /// the same way <see cref="SelectEditTruncationTarget"/> skips them, so the ordinals line up.
    ///
    /// <para>Forking everything is only correct when the retained turns account for the WHOLE server
    /// conversation. If the log holds fewer genuine user turns than the transcript does they cannot
    /// be lined up at all — which happens permanently to any chat whose session was recovered, since
    /// recovery replays the entire retained transcript as a single prompt. Forking everything there
    /// would give the fork more history than the transcript shows, including the very answers the
    /// user branched away from, so that case is unresolved and falls back to replay.</para>
    /// </remarks>
    public static ForkCutSelection SelectForkCutEvent(
        IReadOnlyList<SessionEvent> events, int retainedUserCount)
    {
        if (events is null || retainedUserCount < 0)
            return ForkCutSelection.Unresolved;

        var excludedIndex = -1;
        var genuineSeen = 0;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i] is not UserMessageEvent userEvent
                || !string.IsNullOrEmpty(userEvent.Data?.Source))
                continue;

            if (genuineSeen == retainedUserCount)
            {
                excludedIndex = i;
                break;
            }

            genuineSeen++;
        }

        // No turn to exclude: the cut is the end of the conversation, but only if every genuine
        // server turn is actually represented in the transcript. Fewer means they never lined up.
        if (excludedIndex < 0)
            return genuineSeen == retainedUserCount
                ? ForkCutSelection.ForkEverything
                : ForkCutSelection.Unresolved;

        // Index 0 would leave the fork with no history at all, which is not a fork.
        return excludedIndex == 0
            ? ForkCutSelection.Unresolved
            : new ForkCutSelection(true, events[excludedIndex - 1]);
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

    public static Task<PendingTurnRecoveryLogSnapshot?> TryAnalyzeSessionLogSnapshotAsync(
        string? sessionId,
        int expectedSessionUserMessageCount,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Task.FromResult<PendingTurnRecoveryLogSnapshot?>(null);

        var logPath = GetSessionLogPath(sessionId);
        if (!File.Exists(logPath))
            return Task.FromResult<PendingTurnRecoveryLogSnapshot?>(null);

        return TryAnalyzeLogFileSnapshotAsync(logPath, expectedSessionUserMessageCount, ct);
    }

    internal static async Task<PendingTurnRecoveryLogSnapshot?> TryAnalyzeLogFileSnapshotAsync(
        string logPath,
        int expectedSessionUserMessageCount,
        CancellationToken ct = default)
    {
        var state = new RecoveryAnalysisState(expectedSessionUserMessageCount);
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
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                    break;

                ApplyPersistedLogLine(state, line);
            }

            var readLength = stream.Position;
            var observedLength = stream.Length;
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(logPath);
            return new PendingTurnRecoveryLogSnapshot(
                state.BuildAnalysis(),
                logPath,
                readLength,
                observedLength,
                lastWriteTimeUtc);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static async Task<bool> IsLogSnapshotStableAsync(
        PendingTurnRecoveryLogSnapshot snapshot,
        TimeSpan quietPeriod,
        CancellationToken ct = default)
    {
        if (!snapshot.ReachedObservedEnd)
            return false;

        if (quietPeriod > TimeSpan.Zero)
            await Task.Delay(quietPeriod, ct).ConfigureAwait(false);

        try
        {
            var file = new FileInfo(snapshot.LogPath);
            file.Refresh();
            return file.Exists
                   && file.Length == snapshot.ObservedLength
                   && file.LastWriteTimeUtc == snapshot.LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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

        var stoppedToolCallIds = new HashSet<string>(liveAnalysis.StoppedToolCallIds);
        stoppedToolCallIds.UnionWith(persistedAnalysis.StoppedToolCallIds);
        stoppedToolCallIds.ExceptWith(completedToolCallIds);
        stoppedToolCallIds.ExceptWith(failedToolCallIds);

        var assistantMessages = persistedAnalysis.AssistantMessages.Count >= liveAnalysis.AssistantMessages.Count
            ? persistedAnalysis.AssistantMessages
            : liveAnalysis.AssistantMessages;
        var assistantTurnEnded = terminalState == PendingTurnTerminalState.None
            && (liveAnalysis.AssistantTurnEnded || persistedAnalysis.AssistantTurnEnded);

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
            AssistantTurnEnded = assistantTurnEnded,
            AssistantMessages = assistantMessages,
            CompletedToolCallIds = completedToolCallIds,
            FailedToolCallIds = failedToolCallIds,
            StoppedToolCallIds = stoppedToolCallIds,
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

        var trimmed = line.AsSpan().TrimStart();
        if (trimmed.StartsWith(CompactEventTypePrefix, StringComparison.Ordinal)
            && !trimmed.StartsWith(CompactUserMessageEventPrefix, StringComparison.Ordinal))
        {
            return false;
        }

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

    private static void ApplyPersistedLogLine(RecoveryAnalysisState state, string line)
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

            root.TryGetProperty("data", out var data);
            if (eventType == "user.message")
            {
                state.ObserveUserMessage(string.IsNullOrEmpty(TryGetString(data, "source")));
                return;
            }

            if (!state.CanProcessEvent)
                return;

            switch (eventType)
            {
                case "assistant.turn_start":
                case "assistant.message_start":
                case "assistant.message_delta":
                case "assistant.streaming_delta":
                case "assistant.reasoning_delta":
                case "assistant.reasoning":
                    state.RecordContinuedActivity();
                    break;

                case "assistant.message":
                    state.RecordAssistantMessage(
                        TryGetString(data, "parentToolCallId"),
                        TryGetString(data, "content"));
                    break;

                case "assistant.turn_end":
                    state.RecordAssistantTurnEnd();
                    break;

                case "tool.execution_start":
                    state.RecordToolActivity(TryGetString(data, "toolCallId"));
                    break;

                case "tool.execution_complete":
                    state.RecordToolCompletion(
                        TryGetString(data, "toolCallId"),
                        TryGetBoolean(data, "success") == true);
                    break;

                case "tool.execution_partial_result":
                case "tool.execution_progress":
                    state.RecordToolActivity(TryGetString(data, "toolCallId"));
                    break;

                case "external_tool.requested":
                    state.RecordExternalToolRequest(
                        TryGetString(data, "requestId"),
                        TryGetString(data, "toolCallId"));
                    break;

                case "external_tool.completed":
                    state.RecordExternalToolCompletion(TryGetString(data, "requestId"));
                    break;

                case "session.background_tasks_changed":
                case "session.background_tasks.changed":
                    state.InvalidateAssistantTurnEnd();
                    break;

                case "subagent.started":
                    state.RecordSubagentStarted(TryGetString(data, "toolCallId"));
                    break;

                case "subagent.completed":
                    state.RecordSubagentCompletion(TryGetString(data, "toolCallId"), success: true);
                    break;

                case "subagent.failed":
                    state.RecordSubagentCompletion(TryGetString(data, "toolCallId"), success: false);
                    break;

                case "subagent.selected":
                case "subagent.deselected":
                    state.RecordContinuedActivity();
                    break;

                case "session.idle":
                    state.RecordTerminalState(PendingTurnTerminalState.Idle);
                    break;

                case "session.error":
                    state.RecordTerminalState(
                        PendingTurnTerminalState.Error,
                        TryGetString(data, "message"));
                    break;

                case "abort":
                    state.RecordTerminalState(PendingTurnTerminalState.Abort);
                    break;

                case "session.shutdown":
                    state.RecordTerminalState(PendingTurnTerminalState.Shutdown);
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

    private sealed class RecoveryAnalysisState(int expectedSessionUserMessageCount)
    {
        private readonly List<RecoveredAssistantMessage> _assistantMessages = [];
        private readonly HashSet<string> _completedToolCallIds = [];
        private readonly HashSet<string> _failedToolCallIds = [];
        private readonly HashSet<string> _stoppedToolCallIds = [];
        private readonly HashSet<string> _activeToolCallIds = [];
        private readonly Dictionary<string, string> _externalToolCallIdByRequestId = [];
        private int _userMessagesSeen;
        private bool _turnSuperseded;
        private PendingTurnTerminalState _terminalState;
        private string? _errorMessage;
        private bool _assistantTurnEnded;

        public bool CanProcessEvent
            => _userMessagesSeen >= expectedSessionUserMessageCount && !_turnSuperseded;

        public void ObserveUserMessage(bool isGenuineUserMessage)
        {
            _userMessagesSeen++;
            if (_userMessagesSeen <= expectedSessionUserMessageCount)
                return;

            if (isGenuineUserMessage)
                _turnSuperseded = true;
            else
                RecordContinuedActivity();
        }

        public void InvalidateAssistantTurnEnd() => _assistantTurnEnded = false;

        public void RecordContinuedActivity()
        {
            if (_terminalState != PendingTurnTerminalState.None)
            {
                _stoppedToolCallIds.UnionWith(_activeToolCallIds);
                _activeToolCallIds.Clear();
            }

            _assistantTurnEnded = false;
            _terminalState = PendingTurnTerminalState.None;
            _errorMessage = null;
        }

        public void RecordAssistantMessage(string? parentToolCallId, string? content)
        {
            RecordContinuedActivity();
            if (string.IsNullOrWhiteSpace(parentToolCallId) && !string.IsNullOrWhiteSpace(content))
                _assistantMessages.Add(new RecoveredAssistantMessage(content));
        }

        public void RecordAssistantTurnEnd()
        {
            RecordContinuedActivity();
            _assistantTurnEnded = true;
        }

        public void RecordToolActivity(string? toolCallId)
        {
            RecordContinuedActivity();
            if (string.IsNullOrWhiteSpace(toolCallId))
                return;

            _stoppedToolCallIds.Remove(toolCallId);
            _activeToolCallIds.Add(toolCallId);
        }

        public void RecordToolCompletion(string? toolCallId, bool success)
        {
            RecordContinuedActivity();
            if (string.IsNullOrWhiteSpace(toolCallId))
                return;

            _activeToolCallIds.Remove(toolCallId);
            _stoppedToolCallIds.Remove(toolCallId);
            if (success)
            {
                _failedToolCallIds.Remove(toolCallId);
                _completedToolCallIds.Add(toolCallId);
            }
            else
            {
                _completedToolCallIds.Remove(toolCallId);
                _failedToolCallIds.Add(toolCallId);
            }
        }

        public void RecordExternalToolRequest(string? requestId, string? toolCallId)
        {
            RecordToolActivity(toolCallId);
            if (!string.IsNullOrWhiteSpace(requestId) && !string.IsNullOrWhiteSpace(toolCallId))
                _externalToolCallIdByRequestId[requestId] = toolCallId;
        }

        public void RecordExternalToolCompletion(string? requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId)
                || !_externalToolCallIdByRequestId.Remove(requestId, out var toolCallId))
            {
                RecordContinuedActivity();
                return;
            }

            RecordToolCompletion(toolCallId, success: true);
        }

        public void RecordSubagentStarted(string? toolCallId)
        {
            RecordToolActivity(toolCallId);
            if (string.IsNullOrWhiteSpace(toolCallId))
                return;

            _completedToolCallIds.Remove(toolCallId);
            _failedToolCallIds.Remove(toolCallId);
        }

        public void RecordSubagentCompletion(string? toolCallId, bool success)
            => RecordToolCompletion(toolCallId, success);

        public void RecordTerminalState(PendingTurnTerminalState state, string? message = null)
        {
            _stoppedToolCallIds.UnionWith(_activeToolCallIds);
            _activeToolCallIds.Clear();
            _assistantTurnEnded = false;
            _terminalState = state;
            _errorMessage = message;
        }

        public PendingTurnRecoveryAnalysis BuildAnalysis()
        {
            if (_userMessagesSeen < expectedSessionUserMessageCount)
                return PendingTurnRecoveryAnalysis.UserMessageNotObserved;

            if (_turnSuperseded)
                return new PendingTurnRecoveryAnalysis { UserMessageObserved = true };

            return new PendingTurnRecoveryAnalysis
            {
                UserMessageObserved = true,
                TerminalState = _terminalState,
                ErrorMessage = _errorMessage,
                AssistantTurnEnded = _assistantTurnEnded,
                AssistantMessages = _assistantMessages,
                CompletedToolCallIds = _completedToolCallIds,
                FailedToolCallIds = _failedToolCallIds,
                StoppedToolCallIds = _stoppedToolCallIds,
                ActiveToolCount = _terminalState == PendingTurnTerminalState.None
                    ? _activeToolCallIds.Count
                    : 0,
            };
        }
    }
}
