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
    private readonly Dictionary<string, string?> _toolParentById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubagentToolCallItem> _subagentsByToolCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _toolStartTimes = [];
    private readonly List<(ChatMessageViewModel Vm, PropertyChangedEventHandler Handler)> _pendingToolHandlers = [];
    public List<FileAttachmentItem> PendingToolFileChips { get; } = [];
    public List<(string FilePath, string ToolName, string? OldText, string? NewText)> PendingFileEdits { get; } = [];
    private IList<TranscriptTurn>? _rebuildTarget;
    public bool IsRebuildingTranscript { get; set; }

    public HashSet<string> ShownFileChips { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SkillReference> PendingFetchedSkillRefs { get; } = [];
    private PlanCardItem? _pendingPlanCard;

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
        Func<ChatMessage, bool, Task> resendFromMessageAction)
    {
        _dataStore = dataStore;
        _showDiffAction = showDiffAction;
        _submitQuestionAnswerAction = submitQuestionAnswerAction;
        _resendFromMessageAction = resendFromMessageAction;
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
        CollapseAllCompletedTurns();
        FinalizeCurrentTurn();

        _rebuildTarget = null;
        var result = new ObservableCollection<TranscriptTurn>(tempTurns.Where(static turn => turn.Items.Count > 0));
        _liveTarget = result;
        IsRebuildingTranscript = false;
        return result;
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
        _terminalPreviewsByToolCallId.Clear();
        _toolParentById.Clear();
        _subagentsByToolCallId.Clear();
        _toolStartTimes.Clear();
        PendingToolFileChips.Clear();
        PendingFileEdits.Clear();
        PendingFetchedSkillRefs.Clear();
        ShownFileChips.Clear();
    }

    private static StrataAiToolCallStatus MapToolStatus(string? status)
        => status switch
        {
            "Completed" => StrataAiToolCallStatus.Completed,
            "Failed" => StrataAiToolCallStatus.Failed,
            _ => StrataAiToolCallStatus.InProgress
        };

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
                var question = ToolDisplayHelper.ExtractJsonField(msgVm.Content, "question") ?? "";
                var opts = ToolDisplayHelper.ExtractJsonField(msgVm.Content, "options") ?? "";
                var answer = msgVm.Message.ToolOutput;
                if (!string.IsNullOrEmpty(answer) && answer.StartsWith("User answered: ", StringComparison.Ordinal))
                    answer = answer["User answered: ".Length..];

                var qid = msgVm.Message.QuestionId ?? ("replay_" + msgVm.Message.Id);
                var freeText = string.Equals(ToolDisplayHelper.ExtractJsonField(msgVm.Content, "allowFreeText"), "true", StringComparison.OrdinalIgnoreCase);
                var multiSelect = string.Equals(ToolDisplayHelper.ExtractJsonField(msgVm.Content, "allowMultiSelect"), "true", StringComparison.OrdinalIgnoreCase);

                CloseCurrentToolGroup();
                var isAnswered = !string.IsNullOrEmpty(answer);
                var card = new QuestionItem(qid, question, opts, freeText && !isAnswered, _submitQuestionAnswerAction, multiSelect && !isAnswered);
                if (isAnswered)
                {
                    card.SelectedAnswer = answer;
                    card.IsAnswered = true;
                }

                AppendToCurrentTurn(card, TurnStableIdFor($"question:{msgVm.Message.Id}"));
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
            if (!string.IsNullOrEmpty(skillName))
            {
                var skill = _dataStore.Data.Skills.FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));
                PendingFetchedSkillRefs.Add(new SkillReference
                {
                    Name = skillName,
                    Glyph = skill?.IconGlyph ?? "\u26A1",
                    Description = skill?.Description ?? string.Empty
                });
            }
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
                if (initialStatus == StrataAiToolCallStatus.InProgress && !IsRebuildingTranscript)
                    todoSubagent.IsExpanded = true;
                UpdateSubagentState(todoSubagent);
            }
            else
            {
                _todoUpdateCount++;
                UpsertTodoProgressToolCall(steps, msgVm.ToolStatus ?? "InProgress");

                if (_currentToolGroup is not null && initialStatus == StrataAiToolCallStatus.InProgress && !IsRebuildingTranscript)
                    _currentToolGroup.IsExpanded = true;

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

                        if (msgVm.ToolStatus is "Completed" or "Failed")
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

        if (!showToolCalls)
            return;

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
            if (toolCallId is not null)
                _terminalPreviewsByToolCallId[toolCallId] = termPreview;

            EnsureCurrentToolGroup(initialStatus, toolStableIdSeed, turnStableId);
            var capturedTermGroup = _currentToolGroup!;
            var termParentSubagent = FindOwningSubagent(msgVm.Message.ParentToolCallId);

            if (!IsRebuildingTranscript)
            {
                PropertyChangedEventHandler? handler = null;
                handler = (_, args) =>
                {
                    if (args.PropertyName != nameof(ChatMessageViewModel.ToolStatus))
                        return;

                    termPreview.Status = msgVm.ToolStatus switch
                    {
                        "Completed" => StrataAiToolCallStatus.Completed,
                        "Failed" => StrataAiToolCallStatus.Failed,
                        _ => StrataAiToolCallStatus.InProgress
                    };
                    if (toolCallId is not null && termPreview.Status is not StrataAiToolCallStatus.InProgress
                        && _toolStartTimes.TryGetValue(toolCallId, out var startTick))
                    {
                        termPreview.DurationMs = Stopwatch.GetElapsedTime(startTick).TotalMilliseconds;
                        _toolStartTimes.Remove(toolCallId);
                    }

                    if (termParentSubagent is not null)
                        UpdateSubagentState(termParentSubagent);

                    UpdateToolGroupState(capturedTermGroup);

                    if (msgVm.ToolStatus is "Completed" or "Failed")
                    {
                        msgVm.PropertyChanged -= handler;
                        RemovePendingHandler(msgVm, handler);
                    }
                };
                msgVm.PropertyChanged += handler;
                _pendingToolHandlers.Add((msgVm, handler));
            }

            AddToolItemToCurrentContext(termPreview, msgVm.Message.ParentToolCallId);

            if (termParentSubagent is not null && initialStatus == StrataAiToolCallStatus.InProgress && !IsRebuildingTranscript)
                termParentSubagent.IsExpanded = true;
            else if (_currentToolGroup is not null && initialStatus == StrataAiToolCallStatus.InProgress && !IsRebuildingTranscript)
                _currentToolGroup.IsExpanded = true;

            UpdateToolGroupLabel();
            return;
        }

        var toolCall = new ToolCallItem(friendlyName, initialStatus, $"tool:{toolStableIdSeed}")
        {
            InputParameters = ToolDisplayHelper.FormatToolArgsFriendly(toolName, msgVm.Content),
            MoreInfo = friendlyInfo,
            IsCompact = ToolDisplayHelper.IsCompactEligible(toolName)
                && initialStatus == StrataAiToolCallStatus.Completed,
        };

        if (ToolDisplayHelper.IsFileEditTool(toolName) && initialStatus != StrataAiToolCallStatus.Failed)
        {
            var diffs = ToolDisplayHelper.ExtractAllDiffs(toolName, msgVm.Content);
            foreach (var diff in diffs)
                PendingFileEdits.Add((diff.FilePath, toolName, diff.OldText, diff.NewText));

            if (diffs.Count > 0)
            {
                toolCall.HasDiff = true;
                toolCall.DiffFilePath = diffs[0].FilePath;
                toolCall.DiffToolName = toolName;
                toolCall.DiffEdits = diffs.Select(static diff => (diff.OldText, diff.NewText)).ToList();
                toolCall.ShowFileChangeAction = _showDiffAction;
            }
        }

        EnsureCurrentToolGroup(initialStatus, toolStableIdSeed, turnStableId);
        var capturedToolGroup = _currentToolGroup!;
        var toolParentSubagent = FindOwningSubagent(msgVm.Message.ParentToolCallId);

        if (!IsRebuildingTranscript)
        {
            PropertyChangedEventHandler? handler = null;
            handler = (_, args) =>
            {
                if (args.PropertyName != nameof(ChatMessageViewModel.ToolStatus))
                    return;

                toolCall.Status = msgVm.ToolStatus switch
                {
                    "Completed" => StrataAiToolCallStatus.Completed,
                    "Failed" => StrataAiToolCallStatus.Failed,
                    _ => StrataAiToolCallStatus.InProgress
                };
                if (toolCallId is not null && toolCall.Status is not StrataAiToolCallStatus.InProgress
                    && _toolStartTimes.TryGetValue(toolCallId, out var startTick))
                {
                    toolCall.DurationMs = Stopwatch.GetElapsedTime(startTick).TotalMilliseconds;
                    _toolStartTimes.Remove(toolCallId);
                }

                if (toolCall.Status == StrataAiToolCallStatus.Failed && toolCall.HasDiff && toolCall.DiffFilePath is not null)
                    PendingFileEdits.RemoveAll(fe => fe.FilePath == toolCall.DiffFilePath);

                if (toolParentSubagent is not null)
                    UpdateSubagentState(toolParentSubagent);

                UpdateToolGroupState(capturedToolGroup);

                if (msgVm.ToolStatus is "Completed" or "Failed")
                {
                    msgVm.PropertyChanged -= handler;
                    RemovePendingHandler(msgVm, handler);
                }
            };
            msgVm.PropertyChanged += handler;
            _pendingToolHandlers.Add((msgVm, handler));
        }

        AddToolItemToCurrentContext(toolCall, msgVm.Message.ParentToolCallId);
        if (toolParentSubagent is not null && initialStatus == StrataAiToolCallStatus.InProgress && !IsRebuildingTranscript)
            toolParentSubagent.IsExpanded = true;
        UpdateToolGroupLabel();
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

        AppendToCurrentTurn(subagent, turnStableId);
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

                if (msgVm.ToolStatus is "Completed" or "Failed")
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
        subagent.ModelDisplayName = ToolDisplayHelper.FormatModelDisplayName(
            ToolDisplayHelper.GetSubagentModelName(message.Content));
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

        if (subagent.Status == StrataAiToolCallStatus.InProgress && !IsRebuildingTranscript)
            subagent.IsExpanded = true;
        else if (IsRebuildingTranscript)
            subagent.IsExpanded = false;
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
            if (status == StrataAiToolCallStatus.Completed)
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

        if (msgVm.Role == "user")
        {
            FlushPendingFileEdits();
            FlushPendingPlanCard();
            FinalizeCurrentTurn();

            var userItem = new UserMessageItem(msgVm, showTimestamps, (msg, edited) => _ = _resendFromMessageAction(msg, edited));
            AppendToCurrentTurn(userItem, TurnStableIdFor($"message:{msgVm.Message.Id}"));
            FinalizeCurrentTurn();
            return;
        }

        if (msgVm.Role == "error")
        {
            AppendToCurrentTurn(new ErrorMessageItem(msgVm, showTimestamps), TurnStableIdFor($"message:{msgVm.Message.Id}"));
            return;
        }

        var assistantItem = new AssistantMessageItem(msgVm, showTimestamps);
        if (!msgVm.IsStreaming && (PendingToolFileChips.Count > 0 || msgVm.Message.Sources.Count > 0 || msgVm.Message.ActiveSkills.Count > 0))
        {
            assistantItem.ApplyExtras(PendingToolFileChips.Count > 0 ? PendingToolFileChips.ToList() : null);
            PendingToolFileChips.Clear();
            PendingFetchedSkillRefs.Clear();
        }

        var turn = AppendItemToCurrentTurn(assistantItem, TurnStableIdFor($"message:{msgVm.Message.Id}"));

        if (msgVm.IsStreaming)
        {
            var capturedTurn = turn;
            var capturedItem = assistantItem;
            PropertyChangedEventHandler? handler = null;
            handler = (_, args) =>
            {
                if (args.PropertyName == nameof(ChatMessageViewModel.IsStreaming) && !msgVm.IsStreaming)
                {
                    capturedItem.ApplyExtras(PendingToolFileChips.Count > 0 ? PendingToolFileChips.ToList() : null);
                    PendingToolFileChips.Clear();
                    PendingFetchedSkillRefs.Clear();

                    CollapseCompletedTurnBlocks(capturedTurn, capturedItem);
                    FlushPendingPlanCard();
                    msgVm.PropertyChanged -= handler;
                    RemovePendingHandler(msgVm, handler);
                }
            };
            msgVm.PropertyChanged += handler;
            _pendingToolHandlers.Add((msgVm, handler));
        }
    }

    public void FlushPendingFileEdits()
    {
        if (PendingFileEdits.Count == 0)
            return;

        var fileChanges = GroupFileEdits();
        var stableId = fileChanges.Count > 0
            ? $"file-changes:{fileChanges[0].FilePath}:{fileChanges.Count}"
            : null;
        AppendToCurrentTurn(new FileChangesSummaryItem(fileChanges, stableId), TurnStableIdFor(stableId ?? "file-changes"));
        PendingFileEdits.Clear();
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

    private List<FileChangeItem> GroupFileEdits()
    {
        var grouped = new Dictionary<string, FileChangeItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var (filePath, toolName, oldText, newText) in PendingFileEdits)
        {
            if (!grouped.TryGetValue(filePath, out var item))
            {
                var isCreate = ToolDisplayHelper.IsFileCreateTool(toolName);
                item = new FileChangeItem(filePath, isCreate, _showDiffAction);
                grouped[filePath] = item;
            }

            item.AddEdit(oldText, newText);
        }

        foreach (var item in grouped.Values)
            item.EnsureStatsForCreatedFile();

        return grouped.Values.ToList();
    }

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

            if (_currentToolGroupCount == 1 && !_currentToolGroup.IsActive
                && _currentToolGroup.ToolCalls.Count == 1
                && target is not null)
            {
                var idx = target.IndexOf(_currentToolGroup);
                if (idx >= 0)
                    target[idx] = new SingleToolItem(_currentToolGroup.ToolCalls[0]);
            }
        }

        _currentToolGroup = null;
        _currentToolGroupCount = 0;
        _currentTodoToolCall = null;
        _currentTodoProgress = null;
        _todoUpdateCount = 0;
        _currentIntentText = null;
        _terminalPreviewsByToolCallId.Clear();
        // Keep _toolParentById and _subagentsByToolCallId alive across tool groups
        // so late-arriving child tools can still be nested under their parent subagent.
        // These maps are only cleared in ResetState() at the start of Rebuild().
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
            group.IsActive = running > 0 && _currentTodoProgress.ToolStatus != "Failed";

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
            return;
        }

        var intentText = isCurrent ? _currentIntentText : null;
        var allDone = completedCount + failedCount == toolCount && toolCount > 0;
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

            if (toolCount == 1 && group.ToolCalls.Count == 1 && !IsRebuildingTranscript)
            {
                var turn = FindTurnContaining(group);
                if (turn is not null)
                {
                    var idx = turn.IndexOf(group);
                    if (idx >= 0)
                        turn.Items[idx] = new SingleToolItem(group.ToolCalls[0]);
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
        }

        var genericProgress = toolCount > 0
            ? Math.Clamp(((completedCount + failedCount) * 100d) / toolCount, 0d, 100d)
            : -1;
        group.ProgressValue = IsRebuildingTranscript ? -1 : genericProgress;
    }

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
        if (idx <= 0)
            return;

        var blocksToMerge = new List<TranscriptItem>();
        for (var i = idx - 1; i >= 0; i--)
        {
            if (items[i] is ToolGroupItem or ReasoningItem or SingleToolItem)
                blocksToMerge.Add(items[i]);
            else
                break;
        }

        if (blocksToMerge.Count < 2)
            return;

        blocksToMerge.Reverse();

        var totalToolCalls = 0;
        var failedCount = 0;
        var hasReasoning = false;
        var hasTodoProgress = false;
        string? todoMeta = null;
        string? lastIntentLabel = null;

        foreach (var block in blocksToMerge)
        {
            if (block is ToolGroupItem toolGroup)
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
            else if (block is SingleToolItem singleTool)
            {
                totalToolCalls++;
                if (singleTool.Inner is ToolCallItem singleCall && singleCall.Status == StrataAiToolCallStatus.Failed)
                    failedCount++;
                else if (singleTool.Inner is TerminalPreviewItem singleTerminal && singleTerminal.Status == StrataAiToolCallStatus.Failed)
                    failedCount++;
                else if (singleTool.Inner is TodoProgressItem singleTodo && singleTodo.Status == StrataAiToolCallStatus.Failed)
                    failedCount++;
            }
            else if (block is SubagentToolCallItem subagentItem)
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
        else
            label = Loc.TurnSummary_ReasonedAndOneAction;

        if (failedCount > 0)
            label += " " + string.Format(Loc.ToolGroup_FinishedFailed, failedCount);

        var firstIdx = items.IndexOf(blocksToMerge[0]);
        foreach (var block in blocksToMerge)
            items.Remove(block);

        var summary = new TurnSummaryItem(label, $"turn-summary:{assistantItem.StableId}")
        {
            IsExpanded = hasTodoProgress && !IsRebuildingTranscript,
            HasFailures = failedCount > 0,
        };
        foreach (var block in blocksToMerge)
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

    public void AddQuestionToTranscript(string questionId, string question, string options, bool allowFreeText, bool allowMultiSelect = false)
    {
        CloseCurrentToolGroup();
        var card = new QuestionItem(questionId, question, options, allowFreeText, _submitQuestionAnswerAction, allowMultiSelect);
        AppendToCurrentTurn(card, TurnStableIdFor($"question:{questionId}"));
    }

    private void AppendToCurrentTurn(TranscriptItem item, string turnStableId)
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
