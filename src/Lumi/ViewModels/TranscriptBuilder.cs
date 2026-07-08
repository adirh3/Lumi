using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

public class TranscriptBuilder
{
    private readonly DataStore _dataStore;
    private readonly Action<FileChangeItem> _showDiffAction;
    private readonly Action<string, string> _submitQuestionAnswerAction;
    private readonly Func<ChatMessage, bool, Task> _resendFromMessageAction;
    private readonly Action<SkillReference>? _openSkillAction;
    private readonly Func<string, SkillReference?>? _resolveSkill;
    private readonly Func<string?> _getSelectedModel;

    private ToolGroupItem? _currentToolGroup;
    private int _currentToolGroupCount;
    private TodoProgressItem? _currentTodoToolCall;
    private TodoProgressState? _currentTodoProgress;
    private int _todoUpdateCount;
    private string? _currentIntentText;
    private TypingIndicatorItem? _typingIndicator;
    private TranscriptTurn? _typingTurn;
    private TranscriptTurn? _currentTurn;
    private readonly Dictionary<string, TerminalPreviewItem> _terminalPreviewsByToolCallId = new(StringComparer.Ordinal);
    // Async shells still running in the background (root tool-call id → authoritative start time),
    // supplied by the ChatViewModel before each Rebuild so a rebuilt terminal card is recreated
    // already in its running state (visible, expanded, correct elapsed clock) instead of flashing
    // "completed"/folding into a summary until the background monitor rediscovers it.
    private readonly Dictionary<string, DateTimeOffset> _knownRunningBackgroundShells = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _toolParentById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubagentToolCallItem> _subagentsByToolCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _toolStartTimes = [];
    private readonly HashSet<string> _trackedFileEditToolCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _trackedFileEditFilesByToolCall = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deferredFileEditSubscriptions = new(StringComparer.Ordinal);
    private readonly List<(ChatMessageViewModel Vm, PropertyChangedEventHandler Handler)> _pendingToolHandlers = [];
    public List<FileAttachmentItem> PendingToolFileChips { get; } = [];
    public List<(string FilePath, string ToolName, string? OldText, string? NewText)> PendingFileEdits { get; } = [];
    private readonly Dictionary<string, string?> _pendingFileOriginalContents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _pendingWorkspaceFileChanges = new(StringComparer.OrdinalIgnoreCase);
    private IList<TranscriptTurn>? _rebuildTarget;
    public bool IsRebuildingTranscript { get; set; }

    public HashSet<string> ShownFileChips { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _shownSkillNames = new(StringComparer.OrdinalIgnoreCase);
    private PlanCardItem? _pendingPlanCard;
    private string? _pendingModelName;

    public void SetLiveTarget(ObservableCollection<TranscriptTurn> target) => _liveTarget = target;

    private ObservableCollection<TranscriptTurn>? _liveTarget;

    private sealed class TodoProgressState
    {
        public string ToolStatus { get; set; } = "InProgress";
        public int Total { get; set; }
        public int Completed { get; set; }
        public int Failed { get; set; }
    }

    public TranscriptBuilder(
        DataStore dataStore,
        Action<FileChangeItem> showDiffAction,
        Action<string, string> submitQuestionAnswerAction,
        Func<ChatMessage, bool, Task> resendFromMessageAction,
        Func<string?> getSelectedModel,
        Action<SkillReference>? openSkillAction = null,
        Func<string, SkillReference?>? resolveSkill = null)
    {
        _dataStore = dataStore;
        _showDiffAction = showDiffAction;
        _submitQuestionAnswerAction = submitQuestionAnswerAction;
        _resendFromMessageAction = resendFromMessageAction;
        _getSelectedModel = getSelectedModel;
        _openSkillAction = openSkillAction;
        _resolveSkill = resolveSkill;
    }

    /// <summary>
    /// Resolves a fetched skill name to a display reference. Internal skills are matched
    /// first (cheap); otherwise the optional external resolver is consulted. Returns null
    /// when the skill cannot be resolved, so failed/not-found fetch_skill calls render no chip.
    /// </summary>
    private SkillReference? ResolveFetchedSkill(string skillName)
    {
        var internalSkill = _dataStore.Data.Skills
            .FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));
        if (internalSkill is not null)
            return new SkillReference
            {
                Name = internalSkill.Name,
                Glyph = internalSkill.IconGlyph,
                Description = internalSkill.Description
            };

        return _resolveSkill?.Invoke(skillName);
    }

    public ObservableCollection<TranscriptTurn> Rebuild(IEnumerable<ChatMessageViewModel> messages)
    {
        IsRebuildingTranscript = true;
        var tempTurns = new List<TranscriptTurn>();
        _rebuildTarget = tempTurns;
        ResetState();

        foreach (var msg in messages)
            ProcessMessageToTranscript(msg);

        CloseCurrentToolGroup();
        FlushPendingFileEdits();
        FlushPendingPlanCard();
        FlushPendingModelLabel();
        CollapseAllCompletedTurns();
        FinalizeCurrentTurn();

        _rebuildTarget = null;
        var result = new ObservableCollection<TranscriptTurn>(tempTurns.Where(static turn => turn.Items.Count > 0));
        _liveTarget = result;
        IsRebuildingTranscript = false;
        return result;
    }

    /// <summary>
    /// Refreshes the sources section of the live transcript's assistant message matching
    /// <paramref name="targetMessage"/> in place, without rebuilding the transcript. Returns true
    /// if a matching item was found and refreshed. This avoids re-parsing the entire mounted tail's
    /// markdown when web sources are attached on session idle.
    /// </summary>
    public bool RefreshAssistantSources(ChatMessage targetMessage)
    {
        if (_liveTarget is null)
            return false;

        for (var t = _liveTarget.Count - 1; t >= 0; t--)
        {
            var items = _liveTarget[t].Items;
            for (var i = items.Count - 1; i >= 0; i--)
            {
                if (items[i] is AssistantMessageItem assistant && assistant.MessageId == targetMessage.Id)
                {
                    assistant.RefreshSources();
                    return true;
                }
            }
        }

        return false;
    }

    public void ResetState()
    {
        // Unsubscribe all pending PropertyChanged handlers to prevent leaking
        // TranscriptItem references via closures on ChatMessageViewModel events.
        foreach (var (vm, handler) in _pendingToolHandlers)
            vm.PropertyChanged -= handler;
        _pendingToolHandlers.Clear();

        _currentToolGroup = null;
        _currentToolGroupCount = 0;
        _currentTodoToolCall = null;
        _currentTodoProgress = null;
        _todoUpdateCount = 0;
        _currentIntentText = null;
        _typingIndicator = null;
        _typingTurn = null;
        _currentTurn = null;
        _pendingPlanCard = null;
        _pendingModelName = null;
        _terminalPreviewsByToolCallId.Clear();
        _toolParentById.Clear();
        _subagentsByToolCallId.Clear();
        _toolStartTimes.Clear();
        _trackedFileEditToolCalls.Clear();
        _trackedFileEditFilesByToolCall.Clear();
        _deferredFileEditSubscriptions.Clear();
        PendingToolFileChips.Clear();
        PendingFileEdits.Clear();
        _pendingFileOriginalContents.Clear();
        _pendingWorkspaceFileChanges.Clear();
        ShownFileChips.Clear();
        _shownSkillNames.Clear();
    }

    private static StrataAiToolCallStatus MapToolStatus(string? status)
        => status switch
        {
            "Completed" => StrataAiToolCallStatus.Completed,
            "Failed" => StrataAiToolCallStatus.Failed,
            "Stopped" => StrataAiToolCallStatus.Stopped,
            _ => StrataAiToolCallStatus.InProgress
        };

    private static bool IsTerminalToolStatus(string? status)
        => status is "Completed" or "Failed" or "Stopped";

    public void ProcessMessageToTranscript(ChatMessageViewModel msgVm)
    {
        var showToolCalls = _dataStore.Data.Settings.ShowToolCalls;
        var showReasoning = _dataStore.Data.Settings.ShowReasoning;
        var showTimestamps = _dataStore.Data.Settings.ShowTimestamps;
        var expandReasoning = _dataStore.Data.Settings.ExpandReasoningWhileStreaming;

        if (msgVm.Role == "tool")
            ProcessToolMessage(msgVm, showToolCalls, showTimestamps);
        else if (msgVm.Role == "reasoning")
            ProcessReasoningMessage(msgVm, showReasoning, expandReasoning);
        else
            ProcessChatMessage(msgVm, showTimestamps);
    }

    /// <summary>Adds a connection-lost error item with an optional retry button.</summary>
    public void AddConnectionLostError(string message, System.Windows.Input.ICommand? retryCommand)
    {
        var item = new ErrorMessageItem(message, Loc.Author_Lumi)
        {
            ShowRetryButton = retryCommand is not null,
        };
        if (retryCommand is not null)
        {
            item.RetryCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() =>
            {
                item.ShowRetryButton = false;
                retryCommand.Execute(null);
            });
        }
        AppendToCurrentTurn(item, TranscriptIds.Create("error"));
    }

    private void ProcessToolMessage(ChatMessageViewModel msgVm, bool showToolCalls, bool showTimestamps)
    {
        var toolName = msgVm.ToolName ?? "";
        var toolStableIdSeed = msgVm.Message.ToolCallId ?? msgVm.Message.Id.ToString();
        var turnStableId = TurnStableIdFor($"tool:{toolStableIdSeed}");
        var initialStatus = MapToolStatus(msgVm.ToolStatus);

        if (!string.IsNullOrWhiteSpace(msgVm.Message.ToolCallId))
            _toolParentById[msgVm.Message.ToolCallId!] = msgVm.Message.ParentToolCallId;

        if (toolName is "stop_powershell" or "write_powershell" or "read_powershell"
            or "task_complete" or "read_agent" or "list_agents")
            return;

        if (toolName is "ask_question")
        {
            if (IsRebuildingTranscript)
            {
                // Prefer first-class fields on ChatMessage; fall back to JSON parsing for older data
                var msg = msgVm.Message;
                var question = msg.QuestionText
                    ?? ToolDisplayHelper.ExtractJsonField(msgVm.Content, "question") ?? "";
                var opts = msg.QuestionOptions
                    ?? ToolDisplayHelper.ExtractJsonField(msgVm.Content, "options") ?? "";
                var optionsList = ParseOptionsList(opts);
                var freeText = msg.QuestionAllowFreeText
                    ?? string.Equals(ToolDisplayHelper.ExtractJsonField(msgVm.Content, "allowFreeText"), "true", StringComparison.OrdinalIgnoreCase);
                var multiSelect = msg.QuestionAllowMultiSelect
                    ?? string.Equals(ToolDisplayHelper.ExtractJsonField(msgVm.Content, "allowMultiSelect"), "true", StringComparison.OrdinalIgnoreCase);

                var answer = msg.ToolOutput;
                if (!string.IsNullOrEmpty(answer) && answer.StartsWith("User answered: ", StringComparison.Ordinal))
                    answer = answer["User answered: ".Length..];

                var qid = msg.QuestionId ?? ("replay_" + msg.Id);

                CloseCurrentToolGroup();
                var isAnswered = !string.IsNullOrEmpty(answer);
                var isExpired = !isAnswered && IsTerminalToolStatus(msg.ToolStatus);
                var card = new QuestionItem(qid, question, optionsList, freeText && !isAnswered && !isExpired, _submitQuestionAnswerAction, multiSelect && !isAnswered && !isExpired);
                if (isAnswered)
                {
                    card.SelectedAnswer = answer;
                    card.IsAnswered = true;
                }
                else if (isExpired)
                {
                    card.IsExpired = true;
                }

                AppendToCurrentTurn(card, TurnStableIdFor($"question:{msg.Id}"));
            }

            return;
        }

        if (toolName == "announce_file")
        {
            var filePath = ToolDisplayHelper.ExtractJsonField(msgVm.Content, "filePath");
            if (filePath is not null && File.Exists(filePath) && ShownFileChips.Add(filePath))
                PendingToolFileChips.Add(new FileAttachmentItem(filePath));
            return;
        }

        if (toolName == "fetch_skill")
        {
            var skillName = ToolDisplayHelper.ExtractJsonField(msgVm.Content, "name");
            // Surface a runtime-loaded skill as an inline chip at the point it was fetched
            // (mid-turn), de-duplicated against skills already shown earlier in the transcript.
            // Only emit when the skill actually resolves — a not-found/failed fetch_skill call
            // must not leave behind a misleading "loaded skill" chip.
            if (!string.IsNullOrEmpty(skillName) && _shownSkillNames.Add(skillName)
                && ResolveFetchedSkill(skillName) is { } skillRef)
            {
                var chip = new SkillChipItem(skillRef, () => _openSkillAction?.Invoke(skillRef));
                CloseCurrentToolGroup();
                AppendToCurrentTurn(new SkillLoadedItem(chip), TurnStableIdFor($"skill-loaded:{skillName}"));
            }
            return;
        }

        if (toolName == ToolDisplayHelper.WorkspaceFileChangedToolName)
        {
            var filePath = ToolDisplayHelper.ExtractJsonField(msgVm.Content, "filePath")
                ?? ToolDisplayHelper.ExtractJsonField(msgVm.Content, "path");
            var operation = ToolDisplayHelper.ExtractJsonField(msgVm.Content, "operation");
            var shouldFlushLateChange = IsCurrentTurnAlreadyEnded();
            TrackWorkspaceFileChange(
                filePath,
                string.Equals(operation, "Create", StringComparison.OrdinalIgnoreCase));
            if (shouldFlushLateChange)
                FlushPendingFileEdits();
            return;
        }

        if (toolName == "report_intent")
        {
            var intentText = ToolDisplayHelper.ExtractJsonField(msgVm.Content, "intent");
            if (!string.IsNullOrEmpty(intentText))
            {
                var intentSubagent = FindOwningSubagent(msgVm.Message.ParentToolCallId);
                if (intentSubagent is not null)
                {
                    intentSubagent.CurrentIntent = intentText;
                    UpdateSubagentState(intentSubagent);
                }
                else
                {
                    _currentIntentText = intentText;
                    if (showToolCalls)
                    {
                        EnsureCurrentToolGroup(initialStatus, toolStableIdSeed, turnStableId);
                        UpdateToolGroupLabel();
                    }
                }
            }
            return;
        }

        if (toolName is "update_todo" or "manage_todo_list")
        {
            if (!showToolCalls)
                return;

            var steps = ToolDisplayHelper.ParseTodoSteps(msgVm.Content);
            if (steps.Count == 0)
                return;

            EnsureCurrentToolGroup(initialStatus, toolStableIdSeed, turnStableId);
            var todoSubagent = FindOwningSubagent(msgVm.Message.ParentToolCallId);
            if (todoSubagent is not null)
            {
                UpsertSubagentTodoProgressToolCall(todoSubagent, steps, msgVm.ToolStatus ?? "InProgress");
                if (initialStatus == StrataAiToolCallStatus.InProgress && !IsRebuildingTranscript && !todoSubagent.IsGrouped)
                    todoSubagent.IsExpanded = true;
                UpdateSubagentState(todoSubagent);
            }
            else
            {
                _todoUpdateCount++;
                UpsertTodoProgressToolCall(steps, msgVm.ToolStatus ?? "InProgress");
                UpdateToolGroupLabel();
            }

            if (!IsRebuildingTranscript)
            {
                var capturedGroup = todoSubagent is null ? _currentToolGroup : null;
                var capturedSubagent = todoSubagent;
                var capturedTodoProgress = todoSubagent is null ? _currentTodoProgress : null;
                var capturedTodoToolCall = todoSubagent is null ? _currentTodoToolCall : null;
                PropertyChangedEventHandler? handler = null;
                handler = (_, args) =>
                {
                    if (args.PropertyName == nameof(ChatMessageViewModel.ToolStatus))
                    {
                        if (capturedSubagent is not null && capturedSubagent.TodoItem is not null)
                        {
                            capturedSubagent.TodoToolStatus = msgVm.ToolStatus ?? "InProgress";
                            capturedSubagent.TodoItem.Status = MapToolStatus(msgVm.ToolStatus);
                            UpdateSubagentState(capturedSubagent);
                        }
                        else if (capturedTodoProgress is not null)
                        {
                            capturedTodoProgress.ToolStatus = msgVm.ToolStatus ?? "InProgress";
                            if (capturedTodoToolCall is not null)
                                capturedTodoToolCall.Status = MapToolStatus(msgVm.ToolStatus);

                            UpdateToolGroupState(capturedGroup);
                        }

                        if (IsTerminalToolStatus(msgVm.ToolStatus))
                        {
                            msgVm.PropertyChanged -= handler;
                            RemovePendingHandler(msgVm, handler);
                        }
                    }
                };
                msgVm.PropertyChanged += handler;
                _pendingToolHandlers.Add((msgVm, handler));
            }

            return;
        }

        var shouldFlushLateFileEdit = IsCurrentTurnAlreadyEnded();
        var captureLiveSnapshot = !IsRebuildingTranscript && initialStatus == StrataAiToolCallStatus.InProgress;
        var diffs = TrackFileEditToolDiffs(msgVm, toolName, initialStatus);
        if (diffs.Count == 0 || (!showToolCalls && initialStatus == StrataAiToolCallStatus.InProgress))
            SubscribeToDeferredFileEditDiffs(msgVm, toolName);

        if (!showToolCalls)
        {
            if (shouldFlushLateFileEdit && diffs.Count > 0)
                FlushPendingFileEdits();
            return;
        }

        if (toolName == "task" || toolName.StartsWith("agent:", StringComparison.Ordinal))
        {
            ProcessSubagentToolMessage(msgVm, initialStatus, toolStableIdSeed, turnStableId);
            return;
        }

        var (friendlyName, friendlyInfo) = ToolDisplayHelper.GetFriendlyToolDisplay(toolName, msgVm.Author, msgVm.Content);
        friendlyName = $"{ToolDisplayHelper.GetToolGlyph(toolName)} {friendlyName}";

        var toolCallId = msgVm.Message.ToolCallId;
        if (toolCallId is not null && initialStatus == StrataAiToolCallStatus.InProgress)
            _toolStartTimes[toolCallId] = Stopwatch.GetTimestamp();

        if (toolName == "powershell")
        {
            var command = ToolDisplayHelper.ExtractJsonField(msgVm.Content, "command") ?? "";
            var termPreview = new TerminalPreviewItem(friendlyName, command, initialStatus, $"terminal:{toolStableIdSeed}")
            {
                Output = msgVm.Message.ToolOutput ?? string.Empty,
                IsExpanded = !IsRebuildingTranscript,
            };
            // Rebuilt while this async shell is still running in the background: recreate the card
            // already running (kept visible + expanded, anchored to the shell's real start time) so a
            // chat switch doesn't fold it into a "finished" summary or reset its elapsed clock.
            if (toolCallId is not null && _knownRunningBackgroundShells.TryGetValue(toolCallId, out var knownStartedUtc))
            {
                termPreview.IsRunningInBackground = true;
                termPreview.BackgroundStartedUtc = knownStartedUtc;
                termPreview.IsExpanded = true;
            }
            if (toolCallId is not null)
                _terminalPreviewsByToolCallId[toolCallId] = termPreview;

            EnsureCurrentToolGroup(initialStatus, toolStableIdSeed, turnStableId);
            var capturedTermGroup = _currentToolGroup!;
            capturedTermGroup.Source ??= msgVm;
            var termParentSubagent = FindOwningSubagent(msgVm.Message.ParentToolCallId);

            if (!IsRebuildingTranscript)
            {
                PropertyChangedEventHandler? handler = null;
                handler = (_, args) =>
                {
                    if (args.PropertyName != nameof(ChatMessageViewModel.ToolStatus))
                        return;

                    termPreview.Status = MapToolStatus(msgVm.ToolStatus);
                    if (termPreview.Status == StrataAiToolCallStatus.Failed
                        && !string.IsNullOrWhiteSpace(msgVm.Message.ToolOutput))
                    {
                        termPreview.Output = msgVm.Message.ToolOutput;
                    }

                    if (toolCallId is not null && termPreview.Status is not StrataAiToolCallStatus.InProgress
                        && _toolStartTimes.TryGetValue(toolCallId, out var startTick))
                    {
                        termPreview.DurationMs = Stopwatch.GetElapsedTime(startTick).TotalMilliseconds;
                        _toolStartTimes.Remove(toolCallId);
                    }

                    if (termParentSubagent is not null)
                        UpdateSubagentState(termParentSubagent);

                    UpdateToolGroupState(capturedTermGroup);

                    if (IsTerminalToolStatus(msgVm.ToolStatus))
                    {
                        msgVm.PropertyChanged -= handler;
                        RemovePendingHandler(msgVm, handler);
                    }
                };
                msgVm.PropertyChanged += handler;
                _pendingToolHandlers.Add((msgVm, handler));
            }

            AddToolItemToCurrentContext(termPreview, msgVm.Message.ParentToolCallId);

            if (termParentSubagent is not null && initialStatus == StrataAiToolCallStatus.InProgress && !IsRebuildingTranscript && !termParentSubagent.IsGrouped)
                termParentSubagent.IsExpanded = true;

            UpdateToolGroupLabel();
            return;
        }

        var toolCall = new ToolCallItem(friendlyName, initialStatus, $"tool:{toolStableIdSeed}")
        {
            InputParameters = ToolDisplayHelper.FormatToolArgsFriendly(toolName, msgVm.Content),
            MoreInfo = BuildToolCallMoreInfo(friendlyInfo, msgVm, initialStatus),
            IsCompact = ToolDisplayHelper.IsCompactEligible(toolName)
                && initialStatus == StrataAiToolCallStatus.Completed,
        };

        if (diffs.Count > 0)
        {
            toolCall.HasDiff = true;
            toolCall.DiffFilePath = diffs[0].FilePath;
            toolCall.DiffToolName = toolName;
            toolCall.DiffEdits = diffs.Select(static diff => (diff.OldText, diff.NewText)).ToList();
            toolCall.ShowFileChangeAction = _showDiffAction;
            if (captureLiveSnapshot)
                toolCall.DiffOriginalContent = CapturePendingOriginalSnapshot(diffs[0].FilePath, IsCreateDiff(toolName, diffs[0].OldText));
        }

        EnsureCurrentToolGroup(initialStatus, toolStableIdSeed, turnStableId);
        var capturedToolGroup = _currentToolGroup!;
        capturedToolGroup.Source ??= msgVm;
        var toolParentSubagent = FindOwningSubagent(msgVm.Message.ParentToolCallId);

        if (!IsRebuildingTranscript)
        {
            PropertyChangedEventHandler? handler = null;
            handler = (_, args) =>
            {
                if (args.PropertyName != nameof(ChatMessageViewModel.ToolStatus))
                    return;

                toolCall.Status = MapToolStatus(msgVm.ToolStatus);
                if (toolCallId is not null && toolCall.Status is not StrataAiToolCallStatus.InProgress
                    && _toolStartTimes.TryGetValue(toolCallId, out var startTick))
                {
                    toolCall.DurationMs = Stopwatch.GetElapsedTime(startTick).TotalMilliseconds;
                    _toolStartTimes.Remove(toolCallId);
                }

                if (toolCall.Status == StrataAiToolCallStatus.Completed && toolCall.HasDiff && toolCall.DiffFilePath is not null && !IsRebuildingTranscript)
                    toolCall.DiffCurrentContent = ReadFileContentOrEmpty(toolCall.DiffFilePath);

                if (toolCall.Status == StrataAiToolCallStatus.Failed && ToolDisplayHelper.IsFileEditTool(toolName))
                    RemoveTrackedFileEditDiffs(msgVm);

                if (toolCall.Status == StrataAiToolCallStatus.Failed
                    && !string.IsNullOrWhiteSpace(msgVm.Message.ToolOutput))
                {
                    toolCall.MoreInfo = BuildFailedToolMoreInfo(toolCall.MoreInfo, msgVm.Message.ToolOutput);
                }

                if (toolParentSubagent is not null)
                    UpdateSubagentState(toolParentSubagent);

                UpdateToolGroupState(capturedToolGroup);

                if (IsTerminalToolStatus(msgVm.ToolStatus))
                {
                    msgVm.PropertyChanged -= handler;
                    RemovePendingHandler(msgVm, handler);
                }
            };
            msgVm.PropertyChanged += handler;
            _pendingToolHandlers.Add((msgVm, handler));
        }

        AddToolItemToCurrentContext(toolCall, msgVm.Message.ParentToolCallId);
        if (toolParentSubagent is not null && initialStatus == StrataAiToolCallStatus.InProgress && !IsRebuildingTranscript && !toolParentSubagent.IsGrouped)
            toolParentSubagent.IsExpanded = true;
        UpdateToolGroupLabel();
        if (shouldFlushLateFileEdit && diffs.Count > 0)
            FlushPendingFileEdits();
    }

    private static string? BuildToolCallMoreInfo(
        string? friendlyInfo,
        ChatMessageViewModel msgVm,
        StrataAiToolCallStatus status)
    {
        if (status == StrataAiToolCallStatus.Failed)
            return BuildFailedToolMoreInfo(friendlyInfo, msgVm.Message.ToolOutput);

        return friendlyInfo;
    }

    private static string? BuildFailedToolMoreInfo(string? friendlyInfo, string? toolOutput)
    {
        if (string.IsNullOrWhiteSpace(toolOutput))
            return friendlyInfo;

        if (string.IsNullOrWhiteSpace(friendlyInfo))
            return toolOutput;

        if (friendlyInfo.Contains(toolOutput, StringComparison.Ordinal))
            return friendlyInfo;

        return $"{friendlyInfo}{Environment.NewLine}{Environment.NewLine}{toolOutput}";
    }

    private List<(string FilePath, string? OldText, string? NewText)> TrackFileEditToolDiffs(
        ChatMessageViewModel msgVm,
        string toolName,
        StrataAiToolCallStatus status)
    {
        if (!ToolDisplayHelper.IsFileEditTool(toolName) || status == StrataAiToolCallStatus.Failed)
            return [];

        var trackingKey = FileEditToolTrackingKey(msgVm);
        if (!_trackedFileEditToolCalls.Add(trackingKey))
            return [];

        var diffs = ToolDisplayHelper.ExtractAllDiffs(toolName, msgVm.Content);
        if (diffs.Count == 0)
        {
            _trackedFileEditToolCalls.Remove(trackingKey);
            return diffs;
        }

        var captureLiveSnapshot = !IsRebuildingTranscript && status == StrataAiToolCallStatus.InProgress;
        foreach (var diff in diffs)
        {
            RemovePendingWorkspaceFileChange(diff.FilePath);
            PendingFileEdits.Add((diff.FilePath, toolName, diff.OldText, diff.NewText));
            if (captureLiveSnapshot)
                CapturePendingOriginalSnapshot(diff.FilePath, IsCreateDiff(toolName, diff.OldText));
        }
        _trackedFileEditFilesByToolCall[trackingKey] = diffs
            .Select(static diff => diff.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return diffs;
    }

    private void SubscribeToDeferredFileEditDiffs(ChatMessageViewModel msgVm, string toolName)
    {
        if (IsRebuildingTranscript || !ToolDisplayHelper.IsFileEditTool(toolName))
            return;

        var trackingKey = FileEditToolTrackingKey(msgVm);
        if (!_deferredFileEditSubscriptions.Add(trackingKey))
            return;

        var hasTrackedDiffs = _trackedFileEditFilesByToolCall.ContainsKey(trackingKey);
        PropertyChangedEventHandler? handler = null;
        handler = (_, args) =>
        {
            if (args.PropertyName is not (nameof(ChatMessageViewModel.Content) or nameof(ChatMessageViewModel.ToolStatus)))
                return;

            var status = MapToolStatus(msgVm.ToolStatus);
            if (status == StrataAiToolCallStatus.Failed)
            {
                RemoveTrackedFileEditDiffs(msgVm);
                msgVm.PropertyChanged -= handler;
                RemovePendingHandler(msgVm, handler);
                _deferredFileEditSubscriptions.Remove(trackingKey);
                return;
            }

            if (!hasTrackedDiffs)
            {
                var trackedDiffs = TrackFileEditToolDiffs(msgVm, toolName, status);
                hasTrackedDiffs = trackedDiffs.Count > 0;
            }

            if (!hasTrackedDiffs)
            {
                if (status == StrataAiToolCallStatus.Completed && !string.IsNullOrWhiteSpace(msgVm.Content))
                {
                    msgVm.PropertyChanged -= handler;
                    RemovePendingHandler(msgVm, handler);
                    _deferredFileEditSubscriptions.Remove(trackingKey);
                }

                return;
            }

            if (status != StrataAiToolCallStatus.Completed)
                return;

            if (IsCurrentTurnAlreadyEnded())
                FlushPendingFileEdits();

            msgVm.PropertyChanged -= handler;
            RemovePendingHandler(msgVm, handler);
            _deferredFileEditSubscriptions.Remove(trackingKey);
        };
        msgVm.PropertyChanged += handler;
        _pendingToolHandlers.Add((msgVm, handler));
    }

    private static string FileEditToolTrackingKey(ChatMessageViewModel msgVm)
        => msgVm.Message.ToolCallId ?? msgVm.Message.Id.ToString();

    private void RemoveTrackedFileEditDiffs(ChatMessageViewModel msgVm)
    {
        var trackingKey = FileEditToolTrackingKey(msgVm);
        if (_trackedFileEditFilesByToolCall.Remove(trackingKey, out var filePaths))
        {
            foreach (var filePath in filePaths)
                RemovePendingFileEdits(filePath);
        }

        _trackedFileEditToolCalls.Remove(trackingKey);
    }

    private void ProcessSubagentToolMessage(ChatMessageViewModel msgVm, StrataAiToolCallStatus initialStatus, string toolStableIdSeed, string turnStableId)
    {
        // Subagents are standalone turn-level items — close any open tool group first.
        CloseCurrentToolGroup();

        var toolCallId = msgVm.Message.ToolCallId;
        if (toolCallId is not null && initialStatus == StrataAiToolCallStatus.InProgress)
            _toolStartTimes[toolCallId] = Stopwatch.GetTimestamp();

        var displayName = ToolDisplayHelper.GetSubagentDisplayName(msgVm.ToolName ?? "", msgVm.Content, msgVm.Author);
        var subagent = new SubagentToolCallItem(displayName, initialStatus, $"subagent:{toolStableIdSeed}")
        {
            IsExpanded = initialStatus == StrataAiToolCallStatus.InProgress && !IsRebuildingTranscript,
        };
        UpdateSubagentFromMessage(subagent, msgVm.Message);

        AttachSubagentToTurn(subagent, turnStableId);
        if (toolCallId is not null)
            _subagentsByToolCallId[toolCallId] = subagent;

        if (!IsRebuildingTranscript)
        {
            PropertyChangedEventHandler? handler = null;
            handler = (_, args) =>
            {
                if (args.PropertyName == nameof(ChatMessageViewModel.Content))
                {
                    UpdateSubagentFromMessage(subagent, msgVm.Message);
                    UpdateSubagentState(subagent);
                    return;
                }

                if (args.PropertyName != nameof(ChatMessageViewModel.ToolStatus))
                    return;

                subagent.Status = MapToolStatus(msgVm.ToolStatus);
                if (toolCallId is not null && subagent.Status is not StrataAiToolCallStatus.InProgress
                    && _toolStartTimes.TryGetValue(toolCallId, out var startTick))
                {
                    subagent.DurationMs = Stopwatch.GetElapsedTime(startTick).TotalMilliseconds;
                    _toolStartTimes.Remove(toolCallId);
                }

                UpdateSubagentState(subagent);

                if (IsTerminalToolStatus(msgVm.ToolStatus))
                {
                    msgVm.PropertyChanged -= handler;
                    RemovePendingHandler(msgVm, handler);
                }
            };
            msgVm.PropertyChanged += handler;
            _pendingToolHandlers.Add((msgVm, handler));
        }

        UpdateSubagentState(subagent);
    }

    private void AddToolItemToCurrentContext(ToolCallItemBase item, string? parentToolCallId)
    {
        var owningSubagent = FindOwningSubagent(parentToolCallId);
        if (owningSubagent is not null)
        {
            owningSubagent.Activities.Add(item);
            UpdateSubagentState(owningSubagent);
            return;
        }

        _currentToolGroup!.ToolCalls.Add(item);
        _currentToolGroupCount++;
    }

    /// <summary>Directly updates the transcript text on a subagent card (called from live streaming flush).</summary>
    public void UpdateSubagentTranscriptText(string toolCallId, string? text)
    {
        if (_subagentsByToolCallId.TryGetValue(toolCallId, out var subagent))
            subagent.TranscriptText = text;
    }

    /// <summary>Directly updates the reasoning text on a subagent card (called from live streaming flush).</summary>
    public void UpdateSubagentReasoningText(string toolCallId, string? text)
    {
        if (_subagentsByToolCallId.TryGetValue(toolCallId, out var subagent))
            subagent.ReasoningText = text;
    }

    private SubagentToolCallItem? FindOwningSubagent(string? parentToolCallId)
    {
        var current = parentToolCallId;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (_subagentsByToolCallId.TryGetValue(current, out var subagent))
                return subagent;

            if (!_toolParentById.TryGetValue(current, out current))
                return null;
        }

        return null;
    }

    private void UpdateSubagentFromMessage(SubagentToolCallItem subagent, ChatMessage message)
    {
        var toolName = message.ToolName ?? "task";
        subagent.DisplayName = ToolDisplayHelper.GetSubagentDisplayName(toolName, message.Content, message.Author);
        subagent.TaskDescription = ToolDisplayHelper.GetSubagentTaskDescription(toolName, message.Content);
        subagent.AgentDescription = ToolDisplayHelper.GetSubagentDescription(message.Content);
        subagent.ModeLabel = ToolDisplayHelper.GetSubagentModeLabel(message.Content);

        var modelId = ToolDisplayHelper.GetSubagentModelName(message.Content)
            ?? message.Model
            ?? _getSelectedModel();
        subagent.ModelDisplayName = ChatViewModel.FormatModelDisplay(modelId);

        subagent.TranscriptText = ToolDisplayHelper.ExtractJsonField(message.Content, "transcript");
        subagent.ReasoningText = ToolDisplayHelper.ExtractJsonField(message.Content, "reasoning");
    }

    private void UpdateSubagentState(SubagentToolCallItem subagent)
    {
        if (subagent.TodoTotal > 0)
        {
            var todoDone = subagent.TodoCompleted + subagent.TodoFailed;
            subagent.Meta = subagent.TodoFailed > 0
                ? string.Format(Loc.ToolTodo_MetaWithFailed, subagent.TodoCompleted, subagent.TodoTotal, subagent.TodoFailed)
                : string.Format(Loc.ToolTodo_Meta, subagent.TodoCompleted, subagent.TodoTotal);

            if (subagent.TodoUpdateCount > 1)
                subagent.Meta += " · " + string.Format(Loc.ToolTodo_Updates, subagent.TodoUpdateCount);

            subagent.ProgressValue = IsRebuildingTranscript || subagent.TodoTotal == 0
                ? -1
                : Math.Clamp((todoDone * 100d) / subagent.TodoTotal, 0d, 100d);
        }
        else
        {
            CountToolStatuses(subagent.Activities, out var total, out var completed, out var failed);
            if (total > 0)
            {
                var running = Math.Max(0, total - completed - failed);
                subagent.Meta = failed > 0
                    ? string.Format(Loc.ToolGroup_MetaFailed, completed, total, failed)
                    : running > 0
                        ? string.Format(Loc.ToolGroup_MetaRunning, completed, total, running)
                        : string.Format(Loc.ToolGroup_MetaDone, completed, total);
                subagent.ProgressValue = IsRebuildingTranscript
                    ? -1
                    : Math.Clamp(((completed + failed) * 100d) / total, 0d, 100d);
            }
            else
            {
                subagent.Meta = null;
                subagent.ProgressValue = -1;
            }
        }

        if (!string.IsNullOrWhiteSpace(subagent.DurationText) && subagent.Status is not StrataAiToolCallStatus.InProgress)
            subagent.Meta = string.IsNullOrWhiteSpace(subagent.Meta)
                ? subagent.DurationText
                : $"{subagent.Meta} · {subagent.DurationText}";

        if (subagent.OwningGroup is null)
        {
            if (subagent.Status == StrataAiToolCallStatus.InProgress && !IsRebuildingTranscript)
                subagent.IsExpanded = true;
            else if (IsRebuildingTranscript)
                subagent.IsExpanded = false;
        }

        if (subagent.OwningGroup is { } owningGroup)
            UpdateSubagentGroupState(owningGroup);
    }

    /// <summary>
    /// Places a freshly built sub-agent into the current turn. A lone sub-agent renders as a
    /// standalone card; a second consecutive sub-agent promotes the pair into a
    /// <see cref="SubagentGroupItem"/> so a parallel fan-out reads as one batch.
    /// </summary>
    private void AttachSubagentToTurn(SubagentToolCallItem subagent, string turnStableId)
    {
        var items = _currentTurn?.Items;
        if (items is { Count: > 0 })
        {
            // Already grouped — append to the open group.
            if (items[^1] is SubagentGroupItem group)
            {
                subagent.OwningGroup = group;
                subagent.IsExpanded = false;
                group.Subagents.Add(subagent);
                UpdateSubagentGroupState(group);
                return;
            }

            // A second back-to-back sub-agent promotes the pair into a group.
            if (items[^1] is SubagentToolCallItem lone)
            {
                var newGroup = new SubagentGroupItem($"subagent-group:{lone.StableId}");
                var idx = items.IndexOf(lone);

                lone.OwningGroup = newGroup;
                lone.IsExpanded = false;
                subagent.OwningGroup = newGroup;
                subagent.IsExpanded = false;
                newGroup.Subagents.Add(lone);
                newGroup.Subagents.Add(subagent);

                if (idx >= 0)
                    items[idx] = newGroup;
                else
                    items.Add(newGroup);

                UpdateSubagentGroupState(newGroup);
                return;
            }
        }

        // First / standalone sub-agent.
        AppendToCurrentTurn(subagent, turnStableId);
    }

    /// <summary>
    /// Recomputes the aggregate header, meta line and progress for a sub-agent group from the
    /// status of its members (how many agents are done / running / failed).
    /// </summary>
    private void UpdateSubagentGroupState(SubagentGroupItem group)
    {
        var total = group.Subagents.Count;
        var completed = 0;
        var failed = 0;
        var running = 0;
        for (var i = 0; i < total; i++)
        {
            var agent = group.Subagents[i];
            agent.AccentIndex = (i % 6) + 1;
            switch (agent.Status)
            {
                case StrataAiToolCallStatus.Completed:
                    completed++;
                    break;
                case StrataAiToolCallStatus.Failed:
                    failed++;
                    break;
                default:
                    running++;
                    break;
            }
        }

        group.TotalCount = total;
        group.DoneCount = completed;
        group.FailedCount = failed;
        group.RunningCount = running;
        group.IsActive = running > 0;
        group.IsExpanded = running > 0 && !IsRebuildingTranscript;
        group.HeaderLabel = running > 0
            ? string.Format(Loc.SubagentGroup_Working, total)
            : string.Format(Loc.SubagentGroup_Finished, total);

        group.Meta = failed > 0
            ? string.Format(Loc.ToolGroup_MetaFailed, completed, total, failed)
            : running > 0
                ? string.Format(Loc.ToolGroup_MetaRunning, completed, total, running)
                : string.Format(Loc.ToolGroup_MetaDone, completed, total);

        var done = completed + failed;
        group.ProgressValue = IsRebuildingTranscript || total == 0
            ? -1
            : Math.Clamp((done * 100d) / total, 0d, 100d);
    }

    private static void CountToolStatuses(IEnumerable<ToolCallItemBase> calls, out int total, out int completed, out int failed)
    {
        total = 0;
        completed = 0;
        failed = 0;

        foreach (var call in calls)
        {
            total++;
            var status = GetStatus(call);
            if (status is StrataAiToolCallStatus.Completed or StrataAiToolCallStatus.Stopped)
                completed++;
            else if (status == StrataAiToolCallStatus.Failed)
                failed++;
        }
    }

    private static StrataAiToolCallStatus GetStatus(ToolCallItemBase call)
        => call switch
        {
            ToolCallItem toolCall => toolCall.Status,
            TerminalPreviewItem terminal => terminal.Status,
            TodoProgressItem todo => todo.Status,
            _ => StrataAiToolCallStatus.InProgress
        };

    private void ProcessReasoningMessage(ChatMessageViewModel msgVm, bool showReasoning, bool expandWhileStreaming)
    {
        CloseCurrentToolGroup();
        if (!showReasoning)
            return;

        AppendToCurrentTurn(new ReasoningItem(msgVm, expandWhileStreaming), TurnStableIdFor($"reasoning:{msgVm.Message.Id}"));
    }

    private void ProcessChatMessage(ChatMessageViewModel msgVm, bool showTimestamps)
    {
        CloseCurrentToolGroup();

        if (JobWakeItem.IsJobWakeMessage(msgVm))
        {
            FlushPendingFileEdits();
            FlushPendingPlanCard();
            FlushPendingModelLabel();
            FinalizeCurrentTurn();

            AppendToCurrentTurn(new JobWakeItem(msgVm, showTimestamps), TurnStableIdFor($"job-wake:{msgVm.Message.Id}"));
            FinalizeCurrentTurn();
            return;
        }

        if (msgVm.Role == "user")
        {
            FlushPendingFileEdits();
            FlushPendingPlanCard();
            FlushPendingModelLabel();
            FinalizeCurrentTurn();

            // Only show skills that haven't been displayed yet in this transcript
            var newSkills = msgVm.Message.ActiveSkills
                .Where(s => _shownSkillNames.Add(s.Name))
                .ToList();
            var userItem = new UserMessageItem(msgVm, showTimestamps, newSkills, (msg, edited) => _ = _resendFromMessageAction(msg, edited), _openSkillAction);
            AppendToCurrentTurn(userItem, TurnStableIdFor($"message:{msgVm.Message.Id}"));
            FinalizeCurrentTurn();
            return;
        }

        if (msgVm.Role == "error")
        {
            AppendToCurrentTurn(new ErrorMessageItem(msgVm, showTimestamps), TurnStableIdFor($"message:{msgVm.Message.Id}"));
            return;
        }

        var assistantItem = new AssistantMessageItem(msgVm, showTimestamps, _openSkillAction);
        _pendingModelName = ChatViewModel.FormatModelDisplay(msgVm.Message.Model);
        if (!msgVm.IsStreaming && (PendingToolFileChips.Count > 0 || msgVm.Message.Sources.Count > 0 || msgVm.Message.ActiveSkills.Count > 0))
        {
            assistantItem.ApplyExtras(PendingToolFileChips.Count > 0 ? PendingToolFileChips.ToList() : null, _shownSkillNames);
            PendingToolFileChips.Clear();
        }

        var turn = AppendAssistantMessageToCurrentTurn(assistantItem, TurnStableIdFor($"message:{msgVm.Message.Id}"));

        if (msgVm.IsStreaming)
        {
            var capturedTurn = turn;
            var capturedItem = assistantItem;
            PropertyChangedEventHandler? handler = null;
            handler = (_, args) =>
            {
                if (args.PropertyName == nameof(ChatMessageViewModel.IsStreaming) && !msgVm.IsStreaming)
                {
                    capturedItem.ApplyExtras(PendingToolFileChips.Count > 0 ? PendingToolFileChips.ToList() : null, _shownSkillNames);
                    PendingToolFileChips.Clear();

                    CollapseCompletedTurnBlocks(capturedTurn, capturedItem);
                    FlushPendingPlanCard();
                    msgVm.PropertyChanged -= handler;
                    RemovePendingHandler(msgVm, handler);
                }
            };
            msgVm.PropertyChanged += handler;
            _pendingToolHandlers.Add((msgVm, handler));
        }
        else
        {
            CollapseCompletedTurnBlocks(turn, assistantItem);
        }
    }

    public void FlushPendingFileEdits()
    {
        if (PendingFileEdits.Count == 0 && _pendingWorkspaceFileChanges.Count == 0)
            return;

        var fileChanges = GroupFileEdits();
        if (fileChanges.Count > 0)
        {
            var existingSummary = _currentTurn?.Items.OfType<FileChangesSummaryItem>().LastOrDefault();
            if (existingSummary is not null)
            {
                existingSummary.MergeChanges(fileChanges);
            }
            else
            {
                var stableId = $"file-changes:{fileChanges[0].FilePath}:{fileChanges.Count}";
                AppendToCurrentTurn(new FileChangesSummaryItem(fileChanges, stableId), TurnStableIdFor(stableId));
            }
        }

        PendingFileEdits.Clear();
        _pendingFileOriginalContents.Clear();
        _pendingWorkspaceFileChanges.Clear();
    }

    private bool IsCurrentTurnAlreadyEnded()
        => _currentTurn?.Items.Any(static item => item is TurnModelItem) == true;

    public void TrackWorkspaceFileChange(string? filePath, bool isCreate)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        var normalizedPath = filePath.Trim();
        if (_pendingWorkspaceFileChanges.TryGetValue(normalizedPath, out var existingIsCreate))
            _pendingWorkspaceFileChanges[normalizedPath] = existingIsCreate || isCreate;
        else
            _pendingWorkspaceFileChanges.Add(normalizedPath, isCreate);
    }

    public void SetPendingPlanCard(string statusText, Action openAction)
    {
        if (_pendingPlanCard is not null)
        {
            _pendingPlanCard.StatusText = statusText;
            return;
        }

        _pendingPlanCard = new PlanCardItem(statusText, openAction);
    }

    /// <summary>Appends a plan card directly to the last assistant turn (used when restoring a plan after transcript rebuild).</summary>
    public void AppendPlanCardToLastTurn(string statusText, Action openAction)
    {
        var turns = GetTurnTarget();
        if (turns is null || turns.Count == 0) return;

        var stableId = TranscriptIds.Create("plan-card");
        var card = new PlanCardItem(statusText, openAction, stableId);

        // Find the last assistant turn and append
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            var turn = turns[i];
            if (turn.Items.Any(item => item is AssistantMessageItem or ToolGroupItem))
            {
                turn.Items.Add(card);
                return;
            }
        }

        // Fallback: append to the very last turn
        turns[^1].Items.Add(card);
    }

    private void FlushPendingPlanCard()
    {
        if (_pendingPlanCard is null)
            return;

        AppendToCurrentTurn(_pendingPlanCard, TurnStableIdFor("plan"));
        _pendingPlanCard = null;
    }

    private void FlushPendingModelLabel()
    {
        if (string.IsNullOrWhiteSpace(_pendingModelName))
            return;

        if (_currentTurn is not null && _currentTurn.Items.Any(static i => i is TurnModelItem))
        {
            _pendingModelName = null;
            return;
        }

        AppendToCurrentTurn(new TurnModelItem(_pendingModelName), TurnStableIdFor("turn-model"));
        _pendingModelName = null;
    }

    /// <summary>Appends a model label at the end of the current turn (called at AssistantTurnEnd).</summary>
    public void AppendModelLabel(string? modelId)
    {
        var displayName = ChatViewModel.FormatModelDisplay(modelId);
        if (string.IsNullOrWhiteSpace(displayName))
            return;

        // Avoid duplicate model labels in the same turn (agentic multi-turn)
        if (_currentTurn is not null && _currentTurn.Items.Any(static i => i is TurnModelItem))
            return;

        AppendToCurrentTurn(new TurnModelItem(displayName), TurnStableIdFor("turn-model"));
    }

    private List<FileChangeItem> GroupFileEdits()
    {
        var grouped = new Dictionary<string, FileChangeItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var (filePath, toolName, oldText, newText) in PendingFileEdits)
        {
            if (!grouped.TryGetValue(filePath, out var item))
            {
                var isCreate = IsCreateDiff(toolName, oldText);
                item = new FileChangeItem(filePath, isCreate, _showDiffAction);
                grouped[filePath] = item;
            }

            item.AddEdit(oldText, newText);
        }

        foreach (var (filePath, isCreate) in _pendingWorkspaceFileChanges)
        {
            if (grouped.ContainsKey(filePath))
                continue;

            var item = new FileChangeItem(filePath, isCreate, _showDiffAction);
            if (isCreate)
            {
                var currentContent = ReadFileContentOrEmpty(filePath);
                item.AddEdit(null, currentContent);
                item.SetSnapshots(string.Empty, currentContent);
            }

            grouped[filePath] = item;
        }

        foreach (var item in grouped.Values)
        {
            item.EnsureStatsForCreatedFile();
            if (_pendingFileOriginalContents.TryGetValue(item.FilePath, out var originalContent))
                item.SetSnapshots(originalContent, ReadFileContentOrEmpty(item.FilePath));
        }

        return grouped.Values.ToList();
    }

    private string? CapturePendingOriginalSnapshot(string filePath, bool isCreate)
    {
        if (_pendingFileOriginalContents.TryGetValue(filePath, out var existing))
            return existing;

        var content = ReadFileContentOrEmpty(filePath, isCreate);
        _pendingFileOriginalContents[filePath] = content;
        return content;
    }

    private void RemovePendingFileEdits(string filePath)
    {
        PendingFileEdits.RemoveAll(fe => string.Equals(fe.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (!PendingFileEdits.Any(fe => string.Equals(fe.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
            _pendingFileOriginalContents.Remove(filePath);
    }

    private void RemovePendingWorkspaceFileChange(string filePath)
    {
        _pendingWorkspaceFileChanges.Remove(filePath);
    }

    private static string? ReadFileContentOrEmpty(string filePath, bool treatMissingAsEmpty = true)
    {
        try
        {
            if (File.Exists(filePath))
                return File.ReadAllText(filePath);
            return treatMissingAsEmpty ? string.Empty : null;
        }
        catch
        {
            return treatMissingAsEmpty ? string.Empty : null;
        }
    }

    private static bool IsCreateDiff(string toolName, string? oldText)
        => ToolDisplayHelper.IsFileCreateTool(toolName)
           || (toolName == "apply_patch" && oldText is null);

    private void EnsureCurrentToolGroup(StrataAiToolCallStatus initialStatus, string? stableIdSeed = null, string? turnStableId = null)
    {
        if (_currentToolGroup is not null)
            return;

        _currentToolGroupCount = 0;
        _currentTodoToolCall = null;
        _currentTodoProgress = null;

        _currentToolGroup = new ToolGroupItem(
            _currentIntentText is not null ? _currentIntentText + "…" : Loc.ToolGroup_Working,
            stableIdSeed is not null ? $"tool-group:{stableIdSeed}" : null)
        {
            IsActive = initialStatus == StrataAiToolCallStatus.InProgress,
            ProgressValue = -1,
        };

        AppendToCurrentTurn(_currentToolGroup, turnStableId ?? TurnStableIdFor(stableIdSeed ?? TranscriptIds.Create("tool-group")));
    }

    public void CloseCurrentToolGroup()
    {
        if (_currentToolGroup is null)
            return;

        var turn = FindTurnContaining(_currentToolGroup) ?? _currentTurn;
        var target = turn?.Items;

        if (_currentToolGroupCount == 0)
        {
            target?.Remove(_currentToolGroup);
            RemoveTurnIfEmpty(turn);
        }
        else
        {
            UpdateToolGroupLabel();

            var hasRunningTool = _currentToolGroup.IsActive;
            // A terminal whose async shell is still running in the background must NOT flatten to a
            // "finished" pill — keep it in the tool group so it renders as a live terminal card.
            var hasBackgroundShell = _currentToolGroup.ToolCalls.Any(static t => t is TerminalPreviewItem { IsRunningInBackground: true });
            var canFlattenSingleTool = _currentToolGroupCount == 1 && !hasRunningTool && !hasBackgroundShell
                && _currentToolGroup.ToolCalls.Count == 1;

            _currentToolGroup.StreamingSummary = null;
            // A background-shell group must stay expanded so its live terminal card remains visible;
            // collapsing here would hide the still-running process behind a static "Working…" pill and
            // nothing would re-expand it (the monitor only refreshes the group when the flag flips).
            _currentToolGroup.IsExpanded = hasBackgroundShell;

            if (canFlattenSingleTool && target is not null)
            {
                var idx = target.IndexOf(_currentToolGroup);
                if (idx >= 0)
                    target[idx] = new SingleToolItem(_currentToolGroup.ToolCalls[0], _currentToolGroup.Source);
            }
        }

        _currentToolGroup = null;
        _currentToolGroupCount = 0;
        _currentTodoToolCall = null;
        _currentTodoProgress = null;
        _todoUpdateCount = 0;
        _currentIntentText = null;
        // Deliberately DO NOT clear _terminalPreviewsByToolCallId here: an async shell can outlive its
        // tool call (and its group can flatten to a pill or be rebuilt), yet the background-shell monitor
        // must still resolve the live terminal card — by tool-call id or by command — after the group
        // closes and even after a full transcript rebuild. The map is bounded by the transcript's
        // terminal count and is reset in ResetState() at the start of each Rebuild().
        // Keep _toolParentById and _subagentsByToolCallId alive across tool groups
        // so late-arriving child tools can still be nested under their parent subagent.
        // These maps are only cleared in ResetState() at the start of Rebuild().
    }

    public void CollapseCompletedBlocksInCurrentTurn()
    {
        if (IsRebuildingTranscript || _currentTurn is null)
            return;

        var assistantItems = _currentTurn.Items.OfType<AssistantMessageItem>().ToList();
        if (assistantItems.Count == 0)
        {
            CollapseActivityOnlyBlocks(_currentTurn);
            return;
        }

        for (var i = assistantItems.Count - 1; i >= 0; i--)
            CollapseCompletedTurnBlocks(_currentTurn, assistantItems[i]);
    }

    private void UpdateToolGroupLabel()
    {
        if (_currentToolGroup is null)
            return;

        UpdateToolGroupState(_currentToolGroup);
    }

    private void UpdateToolGroupState(ToolGroupItem? group)
    {
        if (group is null)
            return;

        var isCurrent = ReferenceEquals(group, _currentToolGroup);

        if (isCurrent && _currentTodoProgress is not null && _currentTodoProgress.Total > 0)
        {
            var todoDone = _currentTodoProgress.Completed + _currentTodoProgress.Failed;
            var running = Math.Max(0, _currentTodoProgress.Total - todoDone);

            group.Label = Loc.ToolTodo_Title;
            group.Meta = _currentTodoProgress.Failed > 0
                ? string.Format(Loc.ToolTodo_MetaWithFailed, _currentTodoProgress.Completed, _currentTodoProgress.Total, _currentTodoProgress.Failed)
                : string.Format(Loc.ToolTodo_Meta, _currentTodoProgress.Completed, _currentTodoProgress.Total);

            if (_todoUpdateCount > 1)
                group.Meta += " · " + string.Format(Loc.ToolTodo_Updates, _todoUpdateCount);

            var progress = Math.Clamp((todoDone * 100d) / _currentTodoProgress.Total, 0d, 100d);
            group.ProgressValue = IsRebuildingTranscript ? -1 : progress;
            group.IsActive = running > 0 && !IsTerminalToolStatus(_currentTodoProgress.ToolStatus);
            group.StreamingSummary = !IsRebuildingTranscript && group.IsActive
                ? ToolDisplayHelper.BuildToolActivitySummary(group.ToolCalls.Select(GetToolGroupSummaryLabel))
                : null;

            if (!group.IsActive || IsRebuildingTranscript)
                group.IsExpanded = false;
            return;
        }

        var toolCount = isCurrent ? _currentToolGroupCount : group.ToolCalls.Count;
        CountToolStatuses(group.ToolCalls, out _, out var completedCount, out var failedCount);

        if (toolCount <= 0)
        {
            group.Meta = null;
            group.ProgressValue = -1;
            group.IsActive = true;
            group.Label = isCurrent && _currentIntentText is not null
                ? _currentIntentText + "…"
                : Loc.ToolGroup_Working;
            group.StreamingSummary = null;
            return;
        }

        var intentText = isCurrent ? _currentIntentText : null;
        // A terminal card whose async shell is still running in the background keeps the group
        // "active" even though the SDK tool call itself has completed — otherwise the group would
        // collapse and hide the live process behind a "Finished" pill.
        var hasBackgroundShell = group.ToolCalls.Any(static t => t is TerminalPreviewItem { IsRunningInBackground: true });
        var allDone = !hasBackgroundShell && completedCount + failedCount == toolCount && toolCount > 0;
        if (allDone)
        {
            group.Label = intentText is not null
                ? (failedCount > 0 ? string.Format(Loc.ToolGroup_FinishedWithFailed, intentText, failedCount) : intentText)
                : (failedCount > 0 ? string.Format(Loc.ToolGroup_FinishedFailed, failedCount)
                    : toolCount == 1 ? Loc.ToolGroup_Finished : string.Format(Loc.ToolGroup_FinishedCount, toolCount));
            group.IsActive = false;
            group.Meta = failedCount > 0
                ? string.Format(Loc.ToolGroup_MetaFailed, completedCount, toolCount, failedCount)
                : string.Format(Loc.ToolGroup_MetaDone, completedCount, toolCount);
            if (IsRebuildingTranscript)
                group.IsExpanded = false;

            if (!isCurrent && toolCount == 1 && group.ToolCalls.Count == 1 && !IsRebuildingTranscript)
            {
                var turn = FindTurnContaining(group);
                if (turn is not null)
                {
                    var idx = turn.IndexOf(group);
                    if (idx >= 0)
                        turn.Items[idx] = new SingleToolItem(group.ToolCalls[0], group.Source);
                }
            }
        }
        else
        {
            group.IsActive = true;
            group.Label = intentText is not null
                ? intentText + "…"
                : (toolCount == 1 ? Loc.ToolGroup_Working : string.Format(Loc.ToolGroup_WorkingCount, toolCount));
            var runningCount = Math.Max(0, toolCount - completedCount - failedCount);
            group.Meta = runningCount > 0
                ? string.Format(Loc.ToolGroup_MetaRunning, completedCount, toolCount, runningCount)
                : string.Format(Loc.ToolGroup_MetaDone, completedCount, toolCount);
            if (IsRebuildingTranscript)
                group.IsExpanded = false;
            else if (hasBackgroundShell)
                group.IsExpanded = true;
        }

        var genericProgress = toolCount > 0
            ? Math.Clamp(((completedCount + failedCount) * 100d) / toolCount, 0d, 100d)
            : -1;
        group.ProgressValue = IsRebuildingTranscript ? -1 : genericProgress;
        group.StreamingSummary = !IsRebuildingTranscript && group.IsActive
            ? ToolDisplayHelper.BuildToolActivitySummary(group.ToolCalls.Select(GetToolGroupSummaryLabel))
            : null;
    }

    private static string? GetToolGroupSummaryLabel(ToolCallItemBase item)
        => item switch
        {
            ToolCallItem toolCall => string.IsNullOrWhiteSpace(toolCall.MoreInfo)
                ? toolCall.ToolName
                : $"{toolCall.ToolName}: {toolCall.MoreInfo}",
            TerminalPreviewItem terminal => terminal.ToolName,
            TodoProgressItem todo => todo.ToolName,
            _ => null
        };

    private void UpsertTodoProgressToolCall(List<ToolDisplayHelper.TodoStepSnapshot> steps, string toolStatus)
    {
        var total = steps.Count;
        var completed = steps.Count(step => string.Equals(step.Status, "completed", StringComparison.OrdinalIgnoreCase));
        var failed = steps.Count(step => string.Equals(step.Status, "failed", StringComparison.OrdinalIgnoreCase));
        var detailsMarkdown = ToolDisplayHelper.BuildTodoDetailsMarkdown(steps);

        if (_currentTodoProgress is null)
        {
            _currentTodoProgress = new TodoProgressState
            {
                ToolStatus = toolStatus,
                Total = total,
                Completed = completed,
                Failed = failed,
            };
        }
        else
        {
            _currentTodoProgress.ToolStatus = toolStatus;
            _currentTodoProgress.Total = total;
            _currentTodoProgress.Completed = completed;
            _currentTodoProgress.Failed = failed;
        }

        if (_currentTodoToolCall is null)
        {
            _currentTodoToolCall = new TodoProgressItem(
                $"✅ {Loc.ToolTodo_Title}",
                StrataAiToolCallStatus.InProgress,
                $"todo:{_currentToolGroup?.StableId}")
            {
                InputParameters = detailsMarkdown,
            };
            _currentToolGroup?.ToolCalls.Add(_currentTodoToolCall);
        }
        else
        {
            _currentTodoToolCall.InputParameters = detailsMarkdown;
        }
    }

    private void UpsertSubagentTodoProgressToolCall(SubagentToolCallItem subagent, List<ToolDisplayHelper.TodoStepSnapshot> steps, string toolStatus)
    {
        subagent.TodoTotal = steps.Count;
        subagent.TodoCompleted = steps.Count(step => string.Equals(step.Status, "completed", StringComparison.OrdinalIgnoreCase));
        subagent.TodoFailed = steps.Count(step => string.Equals(step.Status, "failed", StringComparison.OrdinalIgnoreCase));
        subagent.TodoToolStatus = toolStatus;
        subagent.TodoUpdateCount++;

        var detailsMarkdown = ToolDisplayHelper.BuildTodoDetailsMarkdown(steps);
        if (subagent.TodoItem is null)
        {
            subagent.TodoItem = new TodoProgressItem(
                $"✅ {Loc.ToolTodo_Title}",
                MapToolStatus(toolStatus),
                $"todo:{subagent.StableId}")
            {
                InputParameters = detailsMarkdown,
            };
            subagent.Activities.Add(subagent.TodoItem);
        }
        else
        {
            subagent.TodoItem.Status = MapToolStatus(toolStatus);
            subagent.TodoItem.InputParameters = detailsMarkdown;
        }
    }

    private void CollapseCompletedTurnBlocks(TranscriptTurn turn, AssistantMessageItem assistantItem)
    {
        var items = turn.Items;
        var idx = items.IndexOf(assistantItem);
        if (idx < 0)
            return;

        CollapseTranscriptBlocks(turn, CollectAdjacentSummaryBlocks(items, idx - 1, -1), assistantItem, "before");

        idx = items.IndexOf(assistantItem);
        if (idx < 0)
            return;

        CollapseTranscriptBlocks(turn, CollectAdjacentSummaryBlocks(items, idx + 1, 1), assistantItem, "after");
    }

    private void CollapseActivityOnlyBlocks(TranscriptTurn turn)
    {
        var runs = new List<(int StartIndex, List<TranscriptItem> Blocks)>();
        var currentRun = new List<TranscriptItem>();
        var currentRunStart = -1;
        var items = turn.Items;

        for (var i = 0; i < items.Count; i++)
        {
            if (IsActivityOnlySummaryEligibleBlock(items[i]))
            {
                if (currentRun.Count == 0)
                    currentRunStart = i;

                currentRun.Add(items[i]);
                continue;
            }

            AddActivityRunIfCollapsible();
        }

        AddActivityRunIfCollapsible();

        for (var i = runs.Count - 1; i >= 0; i--)
        {
            var (startIndex, blocks) = runs[i];
            CollapseTranscriptBlocks(turn, blocks, $"turn-summary:{turn.StableId}:activity:{startIndex}");
        }

        void AddActivityRunIfCollapsible()
        {
            if (currentRun.Count >= 2)
                runs.Add((currentRunStart, currentRun.ToList()));

            currentRun.Clear();
            currentRunStart = -1;
        }
    }

    private static List<TranscriptItem> CollectAdjacentSummaryBlocks(IList<TranscriptItem> items, int startIndex, int step)
    {
        var blocks = new List<TranscriptItem>();
        for (var i = startIndex; i >= 0 && i < items.Count; i += step)
        {
            if (!IsSummaryEligibleBlock(items[i]))
                break;

            blocks.Add(items[i]);
        }

        if (step < 0)
            blocks.Reverse();

        return blocks;
    }

    private static bool IsSummaryEligibleBlock(TranscriptItem item)
        => item is ToolGroupItem or ReasoningItem or SingleToolItem or SubagentToolCallItem or TurnSummaryItem
           && !ContainsRunningBackgroundShell(item);

    private static bool IsActivityOnlySummaryEligibleBlock(TranscriptItem item)
        => item is ToolGroupItem or ReasoningItem or SingleToolItem or TurnSummaryItem
           && !ContainsRunningBackgroundShell(item);

    /// <summary>A tool block whose async shell is still running in the background must never be folded
    /// into a compact turn summary — it would vanish behind a "Reasoned · N actions" line while the
    /// process is still live. Excluding it here keeps the running terminal card visible on its own.</summary>
    private static bool ContainsRunningBackgroundShell(TranscriptItem item)
        => item switch
        {
            ToolGroupItem group => group.ToolCalls.Any(static t => t is TerminalPreviewItem { IsRunningInBackground: true }),
            SingleToolItem single => single.Inner is TerminalPreviewItem { IsRunningInBackground: true },
            _ => false,
        };

    private static IEnumerable<TranscriptItem> ExpandSummaryBlock(TranscriptItem block)
    {
        if (block is not TurnSummaryItem summary)
        {
            yield return block;
            yield break;
        }

        foreach (var inner in summary.InnerItems)
        {
            foreach (var expanded in ExpandSummaryBlock(inner))
                yield return expanded;
        }
    }

    private void CollapseTranscriptBlocks(
        TranscriptTurn turn,
        List<TranscriptItem> blocksToMerge,
        AssistantMessageItem assistantItem,
        string position)
        => CollapseTranscriptBlocks(turn, blocksToMerge, $"turn-summary:{assistantItem.StableId}:{position}");

    private void CollapseTranscriptBlocks(
        TranscriptTurn turn,
        List<TranscriptItem> blocksToMerge,
        string stableId)
    {
        if (blocksToMerge.Count < 2)
            return;

        var items = turn.Items;
        var totalToolCalls = 0;
        var failedCount = 0;
        var hasReasoning = false;
        var hasTodoProgress = false;
        string? todoMeta = null;
        string? lastIntentLabel = null;

        var flattenedBlocks = new List<TranscriptItem>();
        foreach (var sourceBlock in blocksToMerge.SelectMany(ExpandSummaryBlock))
        {
            flattenedBlocks.Add(sourceBlock);

            if (sourceBlock is ToolGroupItem toolGroup)
            {
                if (!string.IsNullOrWhiteSpace(toolGroup.Label)
                    && !toolGroup.Label.StartsWith(Loc.ToolGroup_Working.TrimEnd('…', '.'), StringComparison.CurrentCulture)
                    && !toolGroup.Label.StartsWith(Loc.ToolGroup_Finished, StringComparison.CurrentCulture))
                {
                    lastIntentLabel = toolGroup.Label;
                }

                foreach (var call in toolGroup.ToolCalls)
                {
                    switch (call)
                    {
                        case ToolCallItem toolCall:
                            totalToolCalls++;
                            if (toolCall.Status == StrataAiToolCallStatus.Failed) failedCount++;
                            if (!string.IsNullOrWhiteSpace(toolCall.ToolName) && toolCall.ToolName.Contains(Loc.ToolTodo_Title, StringComparison.CurrentCultureIgnoreCase))
                            {
                                hasTodoProgress = true;
                                todoMeta = toolGroup.Meta ?? toolCall.MoreInfo;
                            }
                            break;
                        case TerminalPreviewItem terminal:
                            totalToolCalls++;
                            if (terminal.Status == StrataAiToolCallStatus.Failed) failedCount++;
                            break;
                        case TodoProgressItem todo:
                            totalToolCalls++;
                            if (todo.Status == StrataAiToolCallStatus.Failed) failedCount++;
                            break;
                    }
                }
            }
            else if (sourceBlock is SingleToolItem singleTool)
            {
                totalToolCalls++;
                if (singleTool.Inner is ToolCallItem singleCall && singleCall.Status == StrataAiToolCallStatus.Failed)
                    failedCount++;
                else if (singleTool.Inner is TerminalPreviewItem singleTerminal && singleTerminal.Status == StrataAiToolCallStatus.Failed)
                    failedCount++;
                else if (singleTool.Inner is TodoProgressItem singleTodo && singleTodo.Status == StrataAiToolCallStatus.Failed)
                    failedCount++;
            }
            else if (sourceBlock is SubagentToolCallItem subagentItem)
            {
                totalToolCalls++;
                if (subagentItem.Status == StrataAiToolCallStatus.Failed) failedCount++;
                if (!string.IsNullOrWhiteSpace(subagentItem.Title))
                    lastIntentLabel = subagentItem.Title;
            }
            else
            {
                hasReasoning = true;
            }
        }

        string label;
        if (hasTodoProgress)
            label = !string.IsNullOrWhiteSpace(todoMeta) ? $"{Loc.ToolTodo_Title} · {todoMeta}" : Loc.ToolTodo_Title;
        else if (lastIntentLabel is not null)
        {
            label = hasReasoning ? $"{lastIntentLabel} · {Loc.TurnSummary_Reasoned.ToLowerInvariant()}" : lastIntentLabel;
            if (totalToolCalls > 1)
                label += $" · {string.Format(Loc.ToolGroup_FinishedCount, totalToolCalls)}";
        }
        else if (hasReasoning && totalToolCalls > 0)
            label = totalToolCalls == 1 ? Loc.TurnSummary_ReasonedAndOneAction : string.Format(Loc.TurnSummary_ReasonedAndActions, totalToolCalls);
        else if (totalToolCalls > 0)
            label = totalToolCalls == 1 ? Loc.ToolGroup_FinishedOne : string.Format(Loc.ToolGroup_FinishedCount, totalToolCalls);
        else if (hasReasoning)
            label = Loc.TurnSummary_Reasoned;
        else
            label = Loc.TurnSummary_ReasonedAndOneAction;

        if (failedCount > 0)
            label += " " + string.Format(Loc.ToolGroup_FinishedFailed, failedCount);

        var firstIdx = items.IndexOf(blocksToMerge[0]);
        foreach (var block in blocksToMerge)
            items.Remove(block);

        var summary = new TurnSummaryItem(label, stableId)
        {
            IsExpanded = hasTodoProgress && !IsRebuildingTranscript,
            HasFailures = failedCount > 0,
        };
        foreach (var block in flattenedBlocks)
            summary.InnerItems.Add(block);

        items.Insert(firstIdx, summary);
    }

    private void CollapseAllCompletedTurns()
    {
        var turns = GetTurnTarget();
        if (turns is null)
            return;

        foreach (var turn in turns)
        {
            var assistantItems = turn.Items.OfType<AssistantMessageItem>().ToList();
            if (assistantItems.Count == 0)
            {
                CollapseActivityOnlyBlocks(turn);
                continue;
            }

            for (var i = assistantItems.Count - 1; i >= 0; i--)
                CollapseCompletedTurnBlocks(turn, assistantItems[i]);
        }
    }

    public void ShowTypingIndicator(string? label)
    {
        if (_liveTarget is null)
            return;

        if (_typingIndicator is null)
        {
            _typingIndicator = new TypingIndicatorItem(label ?? Loc.Status_Thinking);
            _typingTurn = new TranscriptTurn("turn:typing");
            _typingTurn.Items.Add(_typingIndicator);
            _liveTarget.Add(_typingTurn);
            return;
        }

        _typingIndicator.Label = label ?? Loc.Status_Thinking;
        _typingIndicator.IsActive = true;
        if (_typingTurn is not null)
        {
            var lastIndex = _liveTarget.Count - 1;
            if (lastIndex < 0 || _liveTarget[lastIndex] != _typingTurn)
            {
                _liveTarget.Remove(_typingTurn);
                _liveTarget.Add(_typingTurn);
            }
        }
    }

    public void HideTypingIndicator()
    {
        if (_typingTurn is not null && _liveTarget is not null)
            _liveTarget.Remove(_typingTurn);

        _typingIndicator = null;
        _typingTurn = null;
    }

    public void UpdateTypingIndicatorLabel(string? label)
    {
        if (_typingIndicator is not null && !string.IsNullOrEmpty(label))
            _typingIndicator.Label = label;
    }

    public void UpdateTerminalOutput(string rootToolCallId, string output, bool replaceExistingOutput)
    {
        if (string.IsNullOrEmpty(output))
            return;

        if (!_terminalPreviewsByToolCallId.TryGetValue(rootToolCallId, out var target))
            return;

        if (replaceExistingOutput || string.IsNullOrEmpty(target.Output))
        {
            target.Output = output;
            return;
        }

        if (output.StartsWith(target.Output, StringComparison.Ordinal))
            target.Output = output;
        else if (!target.Output.EndsWith(output, StringComparison.Ordinal))
            target.Output = target.Output + "\n" + output;
    }

    /// <summary>
    /// Marks (or clears) the "still running in the background" state on a terminal card whose async
    /// shell outlives the tool call. While set, the card keeps its live running affordance and its
    /// enclosing tool group stays expanded/visible so the process never looks finished prematurely.
    /// When cleared, <paramref name="finalDurationMs"/> (if provided) becomes the card's real total
    /// duration instead of the misleadingly-short tool-call launch time.
    /// </summary>
    public void SetTerminalRunningInBackground(string rootToolCallId, bool running, double? finalDurationMs = null, DateTimeOffset? startedUtc = null)
    {
        if (!_terminalPreviewsByToolCallId.TryGetValue(rootToolCallId, out var card))
            return;

        var changed = card.IsRunningInBackground != running;
        card.IsRunningInBackground = running;

        if (running)
        {
            // Anchor the elapsed clock to the shell's real start time so it survives control
            // recreation (chat switch, virtualization) and manual collapse. Idempotent: the monitor
            // passes the same StartedAt every poll, so the setter no-ops after the first.
            if (startedUtc is { } s)
                card.BackgroundStartedUtc = s;

            // Auto-expand only on the transition into "running in background" — NOT on every poll —
            // so a manual collapse by the user is respected instead of springing back open each tick.
            if (changed)
                card.IsExpanded = true;
        }
        else if (finalDurationMs is > 0)
        {
            card.DurationMs = finalDurationMs.Value;
        }

        if (changed)
            RefreshOwningToolGroup(card);
    }

    /// <summary>Supplies the async shells still running in the background (root tool-call id →
    /// authoritative start time) so the next <see cref="Rebuild"/> recreates their terminal cards
    /// already in the running state. Called by the ChatViewModel from the chat's persisted runtime
    /// state immediately before a rebuild.</summary>
    public void SetKnownRunningBackgroundShells(IReadOnlyDictionary<string, DateTimeOffset> shells)
    {
        _knownRunningBackgroundShells.Clear();
        foreach (var kvp in shells)
            _knownRunningBackgroundShells[kvp.Key] = kvp.Value;
    }

    /// <summary>Finds the tool-call id of a live terminal card whose command matches, so the
    /// background-shell monitor can map an SDK shell task back to its transcript card. Ids in
    /// <paramref name="exclude"/> are skipped so several concurrent shells with identical command text
    /// each bind to a distinct card instead of all collapsing onto the newest match.</summary>
    public string? FindTerminalToolCallIdByCommand(string command, IReadOnlySet<string>? exclude = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var trimmed = command.Trim();
        string? match = null;
        // Prefer the most recently created terminal with this command: entries are inserted in
        // transcript order and never removed between rebuilds, so the last match is the newest shell.
        foreach (var (toolCallId, card) in _terminalPreviewsByToolCallId)
        {
            if (exclude is not null && exclude.Contains(toolCallId))
                continue;
            if (string.Equals(card.Command?.Trim(), trimmed, StringComparison.Ordinal))
                match = toolCallId;
        }

        return match;
    }


    private void RefreshOwningToolGroup(ToolCallItemBase card)
    {
        var turns = GetTurnTarget();
        if (turns is null)
            return;

        var running = card is TerminalPreviewItem { IsRunningInBackground: true };

        foreach (var turn in turns)
        {
            var items = turn.Items;
            for (var i = 0; i < items.Count; i++)
            {
                switch (items[i])
                {
                    case ToolGroupItem group when group.ToolCalls.Contains(card):
                        UpdateToolGroupState(group);
                        return;

                    // A single completed tool is flattened to a compact pill that renders its terminal
                    // output as static text — it can't host the live terminal card. While the shell is
                    // still running in the background, promote it back into an expanded tool group so it
                    // renders as a real StrataTerminalPreview (amber state + live clock + output tail).
                    case SingleToolItem single when ReferenceEquals(single.Inner, card):
                        if (running)
                        {
                            var promoted = new ToolGroupItem(Loc.ToolGroup_Working, $"tool-group:{card.StableId}")
                            {
                                IsActive = true,
                                IsExpanded = true,
                                Source = single.Source,
                            };
                            promoted.ToolCalls.Add(card);
                            items[i] = promoted;
                            UpdateToolGroupState(promoted);
                        }
                        return;
                }
            }
        }
    }

    public void AddQuestionToTranscript(string questionId, string question, IList<string> optionsList, bool allowFreeText, bool allowMultiSelect = false)
    {
        CloseCurrentToolGroup();
        var card = new QuestionItem(questionId, question, optionsList, allowFreeText, _submitQuestionAnswerAction, allowMultiSelect);
        AppendToCurrentTurn(card, TurnStableIdFor($"question:{questionId}"));
    }

    private void AppendToCurrentTurn(TranscriptItem item, string turnStableId)
        => AppendItemToCurrentTurn(item, turnStableId);

    private TranscriptTurn AppendAssistantMessageToCurrentTurn(AssistantMessageItem item, string turnStableId)
        => AppendItemToCurrentTurn(item, turnStableId);

    private TranscriptTurn AppendItemToCurrentTurn(TranscriptItem item, string turnStableId)
    {
        if (_currentTurn is not null)
        {
            _currentTurn.Items.Add(item);
            return _currentTurn;
        }

        // Insert the turn only after it has content so the paging controller
        // never observes a transient empty turn and skips mounting it.
        var turn = new TranscriptTurn(turnStableId);
        turn.Items.Add(item);
        _currentTurn = turn;
        InsertTurnBeforeTypingIndicator(turn);
        return turn;
    }

    private void FinalizeCurrentTurn()
    {
        RemoveTurnIfEmpty(_currentTurn);
        _currentTurn = null;
    }

    private void InsertTurnBeforeTypingIndicator(TranscriptTurn turn)
    {
        if (_rebuildTarget is not null)
        {
            _rebuildTarget.Add(turn);
            return;
        }

        if (_liveTarget is null)
            return;

        if (_typingTurn is not null)
        {
            var idx = _liveTarget.IndexOf(_typingTurn);
            if (idx >= 0)
            {
                _liveTarget.Insert(idx, turn);
                return;
            }
        }

        _liveTarget.Add(turn);
    }

    private TranscriptTurn? FindTurnContaining(TranscriptItem item)
    {
        var turns = GetTurnTarget();
        return turns?.FirstOrDefault(turn => turn.Items.Contains(item));
    }

    private IList<TranscriptTurn>? GetTurnTarget() => _rebuildTarget ?? _liveTarget;

    private void RemoveTurnIfEmpty(TranscriptTurn? turn)
    {
        if (turn is null || turn.Items.Count > 0)
            return;

        GetTurnTarget()?.Remove(turn);
        if (ReferenceEquals(turn, _currentTurn))
            _currentTurn = null;
    }

    private static string TurnStableIdFor(string seed) => $"turn:{seed}";

    /// <summary>
    /// Parses an options string into a list. Supports JSON array format (new) and
    /// comma-separated format (legacy) for backward compatibility.
    /// </summary>
    internal static IList<string> ParseOptionsList(string? options)
    {
        if (string.IsNullOrWhiteSpace(options))
            return Array.Empty<string>();

        var trimmed = options.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize(trimmed, Models.AppDataJsonContext.Default.ListString);
                if (parsed is not null)
                    return parsed;
            }
            catch (System.Text.Json.JsonException)
            {
                // Malformed JSON — fall through to comma split
            }
        }

        return trimmed.Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    private void RemovePendingHandler(ChatMessageViewModel vm, PropertyChangedEventHandler? handler)
    {
        for (var i = _pendingToolHandlers.Count - 1; i >= 0; i--)
        {
            var (v, h) = _pendingToolHandlers[i];
            if (ReferenceEquals(v, vm) && ReferenceEquals(h, handler))
            {
                _pendingToolHandlers.RemoveAt(i);
                return;
            }
        }
    }
}
