using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

public class TranscriptBuilder
{
    private readonly DataStore _dataStore;
    private readonly Action<FileChangeItem> _showDiffAction;
    private readonly Action<Guid>? _openChatAction;
    private readonly Action<string, string> _submitQuestionAnswerAction;
    private readonly Action<ChatMessage> _beginEditMessageAction;
    private readonly Func<ChatMessage, bool, Task> _resendFromMessageAction;
    private readonly Action<SkillReference>? _openSkillAction;
    private readonly Func<string, SkillReference?>? _resolveSkill;
    private readonly Func<string?> _getSelectedModel;
    private readonly Func<ChatMessageViewModel, Task>? _sendSteeredNowAsync;
    private readonly Action<SubagentToolCallItem>? _openSubagentRunAction;
    private readonly Action? _subagentRunsChanged;
    private readonly Func<string, string>? _resolveFilePath;
    private readonly Action<MessageReply>? _openReplySourceAction;
    private readonly bool _foldProgressMessages;
    private readonly Func<ChatMessage, EmbeddedAppItem?>? _appItemFor;
    private readonly HashSet<ChatMessage> _shownApps = [];
    // When the message that started the turn being built was sent: the start of its "Worked for" time.
    private DateTimeOffset? _requestedAt;
    private static readonly StringComparer FilePathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private readonly Dictionary<string, int> _announcedFileTurns = new(FilePathComparer);
    private FileChipTurn _fileChipTurn = new(0, Guid.Empty);

    /// <summary>
    /// Every sub-agent run in the current transcript, in the order it started. Backs the chat's
    /// "agents" index so a user can see and inspect all of them without hunting through the
    /// transcript.
    /// </summary>
    public ObservableCollection<SubagentToolCallItem> SubagentRuns { get; } = [];

    /// <summary>
    /// Tools whose calls never show as a step card of their own — they manage shells or agents, ask,
    /// announce, load or report instead — plus plan updates, which share one plan card per run
    /// (see <see cref="ProcessToolMessage"/>). Sub-agent tools are runs of their own.
    /// </summary>
    internal static readonly string[] ToolsWithoutStepCards =
    [
        "stop_powershell", "write_powershell", "read_powershell", "task_complete", "read_agent", "list_agents",
        "ask_input", "ask_question", "announce_file", "fetch_skill", "report_intent",
        ToolDisplayHelper.WorkspaceFileChangedToolName, "update_todo", "manage_todo_list",
    ];

    /// <summary>
    /// Steps filed at the top level rather than inside a run, counted the way a run counts its own
    /// (<see cref="SubagentToolCallItem.StepCount"/>): on a run's page, whose top level is the run's
    /// work, how many steps the run took.
    /// </summary>
    public int RootStepCount { get; private set; }
    private bool _rootPlanCounted;

    private ToolGroupItem? _currentToolGroup;
    private ReasoningItem? _currentReasoning;
    private readonly List<ReasoningItem> _reasoningItems = [];
    private int _currentToolGroupCount;
    private TodoProgressItem? _currentTodoToolCall;
    private TodoProgressState? _currentTodoProgress;
    private string? _currentIntentText;
    private TypingIndicatorItem? _typingIndicator;
    private TranscriptTurn? _typingTurn;
    private string? _typingActionLabel;
    private System.Windows.Input.ICommand? _typingActionCommand;
    private TranscriptTurn? _currentTurn;
    private readonly Dictionary<string, TerminalPreviewItem> _terminalPreviewsByToolCallId = new(StringComparer.Ordinal);
    /// <summary>Running tool rows by tool call id, so their progress messages reach the row and its card.</summary>
    private readonly Dictionary<string, (ToolCallItem Item, ToolGroupItem? Group)> _runningToolCallsByToolCallId = new(StringComparer.Ordinal);
    /// <summary>Rows for file tools whose arguments the model is still writing, by tool call id (see <see cref="ShowToolDraft"/>).</summary>
    private readonly Dictionary<string, ToolCallItem> _toolDraftsByToolCallId = new(StringComparer.Ordinal);
    /// <summary><c>sql</c> calls that became plan updates in the live tool group (see <see cref="ApplyPlanSnapshot"/>).</summary>
    private readonly HashSet<string> _livePlanToolCallIds = new(StringComparer.Ordinal);
    // Async shells still running in the background (root tool-call id → authoritative start time),
    // supplied by the ChatViewModel before each Rebuild so a rebuilt terminal card is recreated
    // already in its running state (visible, expanded, correct elapsed clock) instead of flashing
    // "completed"/folding into a summary until the background monitor rediscovers it.
    private readonly Dictionary<string, DateTimeOffset> _knownRunningBackgroundShells = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _toolParentById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubagentToolCallItem> _subagentsByToolCallId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _trackedFileEditToolCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _trackedFileEditFilesByToolCall = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deferredFileEditSubscriptions = new(StringComparer.Ordinal);
    private readonly List<(ChatMessageViewModel Vm, PropertyChangedEventHandler Handler)> _pendingToolHandlers = [];
    public List<FileAttachmentItem> PendingToolFileChips => _fileChipTurn.Pending;
    public List<(string FilePath, string ToolName, string? OldText, string? NewText)> PendingFileEdits { get; } = [];
    private readonly Dictionary<string, string?> _pendingFileOriginalContents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WeakReference<string>> _lastFileSnapshots = new(FilePathComparer);
    private readonly Dictionary<string, bool> _pendingWorkspaceFileChanges = new(StringComparer.OrdinalIgnoreCase);
    private IList<TranscriptTurn>? _rebuildTarget;
    public bool IsRebuildingTranscript { get; set; }

    /// <summary>
    /// Set for builds of stored history: a run keeps what its card and index row show, and its page
    /// reads the rest — its task, its log and its steps, with their count — from storage when it opens
    /// (see <see cref="SubagentToolCallItem.ReleaseDetail"/>).
    /// </summary>
    internal bool KeepsRunDetailInStorage { get; set; }

    public HashSet<string> ShownFileChips { get; } = new(FilePathComparer);
    private readonly HashSet<Guid> _processedMessageIds = [];
    private readonly HashSet<string> _shownSkillNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SkillReference> _invokedSkills = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _shownLinkedChatKeys = new(StringComparer.Ordinal);
    private PlanCardItem? _pendingPlanCard;
    private string? _pendingModelName;

    public void SetLiveTarget(ObservableCollection<TranscriptTurn> target) => _liveTarget = target;

    /// <summary>
    /// Lets live messages extend the last rebuilt turn instead of opening a new one. A rebuild closes
    /// its final turn because the chat's next message usually starts one; a surface whose whole
    /// transcript is one continuous turn (a sub-agent's run) keeps extending it, so its activity keeps
    /// folding together.
    /// </summary>
    public void ContinueLastTurn()
    {
        if (_liveTarget is { Count: > 0 } turns
            && turns[^1].Items.Count > 0
            && turns[^1].Items[0] is not (UserMessageItem or JobWakeItem or InteractionReceiptItem))
        {
            _currentTurn = turns[^1];
        }
    }

    private ObservableCollection<TranscriptTurn>? _liveTarget;

    private sealed class TodoProgressState
    {
        public int Total { get; set; }
        public int Completed { get; set; }
        public int Failed { get; set; }
    }

    private sealed class FileChipTurn(int index, Guid messageId)
    {
        public int Index { get; } = index;
        public Guid MessageId { get; } = messageId;
        public Dictionary<string, FileAttachmentItem> Chips { get; } = new(FilePathComparer);
        public List<FileAttachmentItem> Pending { get; } = [];
        public AssistantMessageItem? LastAssistant { get; set; }
        public TranscriptTurn? TranscriptTurn { get; set; }
        public TranscriptTurn? UserTurn { get; set; }
        public bool HasEnded { get; set; }
    }

    public TranscriptBuilder(
        DataStore dataStore,
        Action<FileChangeItem> showDiffAction,
        Action<string, string> submitQuestionAnswerAction,
        Action<ChatMessage> beginEditMessageAction,
        Func<ChatMessage, bool, Task> resendFromMessageAction,
        Func<string?> getSelectedModel,
        Action<SkillReference>? openSkillAction = null,
        Func<string, SkillReference?>? resolveSkill = null,
        Action<Guid>? openChatAction = null,
        Func<ChatMessageViewModel, Task>? sendSteeredNowAsync = null,
        Action<SubagentToolCallItem>? openSubagentRunAction = null,
        Action? subagentRunsChanged = null,
        Func<string, string>? resolveFilePath = null,
        Action<MessageReply>? openReplySourceAction = null,
        bool foldProgressMessages = true,
        Func<ChatMessage, EmbeddedAppItem?>? appItemFor = null)
    {
        _dataStore = dataStore;
        _showDiffAction = showDiffAction;
        _submitQuestionAnswerAction = submitQuestionAnswerAction;
        _beginEditMessageAction = beginEditMessageAction;
        _resendFromMessageAction = resendFromMessageAction;
        _getSelectedModel = getSelectedModel;
        _openSkillAction = openSkillAction;
        _resolveSkill = resolveSkill;
        _openChatAction = openChatAction;
        _sendSteeredNowAsync = sendSteeredNowAsync;
        _openSubagentRunAction = openSubagentRunAction;
        _subagentRunsChanged = subagentRunsChanged;
        _resolveFilePath = resolveFilePath;
        _openReplySourceAction = openReplySourceAction;
        _foldProgressMessages = foldProgressMessages;
        _appItemFor = appItemFor;
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
                Description = internalSkill.DisplayDescription
            };

        return _resolveSkill?.Invoke(skillName);
    }

    /// <summary>Supplies the content that can arrive after a native skill's row was built.</summary>
    public void RecordInvokedSkill(SkillReference skill)
    {
        if (!_invokedSkills.TryGetValue(skill.Name, out var reference))
        {
            _invokedSkills[skill.Name] = new SkillReference
            {
                Name = skill.Name,
                Glyph = skill.Glyph,
                Description = skill.Description,
                Content = skill.Content
            };
            return;
        }

        if (!string.IsNullOrWhiteSpace(skill.Content))
            reference.Content = skill.Content;
        if (!string.IsNullOrWhiteSpace(skill.Description))
            reference.Description = skill.Description;
    }

    public ObservableCollection<TranscriptTurn> Rebuild(IEnumerable<ChatMessageViewModel> messages)
        => Rebuild(messages, forkOrigin: null);

    /// <summary>
    /// Rebuilds the transcript, optionally leading with a "Forked from …" breadcrumb chip that
    /// opens the chat this one was branched from.
    /// </summary>
    /// <param name="isWorking">Lumi is still working on the chat's latest turn.</param>
    /// <param name="seed">What earlier exchanges that are not part of this build already showed.</param>
    public ObservableCollection<TranscriptTurn> Rebuild(
        IEnumerable<ChatMessageViewModel> messages,
        ForkOrigin? forkOrigin,
        bool isWorking = false,
        TranscriptSeed? seed = null)
    {
        BeginRebuild(forkOrigin, seed);
        foreach (var msg in messages)
            ProcessMessageToTranscript(msg);
        return EndRebuild(isWorking);
    }

    /// <summary>
    /// Starts a rebuild that is fed one message at a time with <see cref="ProcessMessageToTranscript"/>
    /// and finished by <see cref="EndRebuild"/>, so a long build can be spread over several frames.
    /// </summary>
    public void BeginRebuild(ForkOrigin? forkOrigin, TranscriptSeed? seed = null)
    {
        IsRebuildingTranscript = true;
        _rebuildTarget = new List<TranscriptTurn>();
        ResetState();
        ApplySeed(seed);

        EmitForkOriginChip(forkOrigin);
    }

    /// <summary>Finishes a rebuild started by <see cref="BeginRebuild"/> and returns its turns.</summary>
    public ObservableCollection<TranscriptTurn> EndRebuild(bool isWorking = false)
    {
        var tempTurns = _rebuildTarget ?? [];
        CloseCurrentToolGroup();
        // A turn still in progress stays open, so live messages extend it and its updates stay in view until
        // its answer or its end folds them. Its end-of-turn files, plan and model label wait for that end too.
        var workingTurn = isWorking
            && _currentTurn is { Items: [not (UserMessageItem or JobWakeItem or InteractionReceiptItem), ..] } last
            ? last
            : null;
        if (workingTurn is null)
        {
            FlushPendingFileEdits();
            FlushPendingPlanCard();
            FlushPendingModelLabel();
            FlushPendingFileChips(_fileChipTurn);
            _fileChipTurn.HasEnded = true;
        }
        CollapseAllCompletedTurns(workingTurn);
        if (workingTurn is null)
            FinalizeCurrentTurn();

        _rebuildTarget = null;
        var result = new ObservableCollection<TranscriptTurn>(tempTurns.Where(static turn => turn.Items.Count > 0));
        _liveTarget = result;
        IsRebuildingTranscript = false;
        // Reconcile the run index once the rebuilt runs are all present. The per-run notifications
        // during a rebuild fire against a half-built collection (ResetState clears it first), so an
        // open run could never be matched to its rebuilt twin from inside the loop.
        _subagentRunsChanged?.Invoke();
        return result;
    }

    /// <summary>
    /// Starts a build in the middle of a chat whose earlier exchanges are not part of it: their
    /// count, the skills they already showed and the files they announced. With it, an exchange
    /// built on its own shows the same chips as when the whole chat is built in one pass.
    /// </summary>
    private void ApplySeed(TranscriptSeed? seed)
    {
        if (seed is null)
            return;

        _fileChipTurn = new FileChipTurn(seed.ExchangeIndex, Guid.Empty);
        foreach (var skill in seed.ShownSkillNames)
            _shownSkillNames.Add(skill);
        foreach (var (path, exchange) in seed.AnnouncedFiles)
        {
            _announcedFileTurns[path] = exchange;
            ShownFileChips.Add(path);
        }

        // A run of an earlier exchange owns the steps it took after it, as in a full build, where they
        // join its card there. A stand-in takes them here; what else they show stays in this build.
        var runsShown = _dataStore.Data.Settings.ShowToolCalls;
        foreach (var call in seed.EarlierToolCalls)
        {
            _toolParentById[call.ToolCallId] = call.ParentToolCallId;
            if (call.IsRun && runsShown)
            {
                _subagentsByToolCallId[call.ToolCallId] = new SubagentToolCallItem(
                    call.ToolCallId,
                    StrataAiToolCallStatus.Completed,
                    $"subagent:{call.ToolCallId}")
                {
                    ToolCallId = call.ToolCallId,
                };
            }
        }
    }

    /// <summary>The runs this build created, or stands in for, by tool call id.</summary>
    internal IReadOnlyDictionary<string, SubagentToolCallItem> RunsByToolCallId => _subagentsByToolCallId;

    /// <summary>
    /// Lets runs of another build take the steps this build is fed: a step of one of them joins that
    /// run's card, while anything else the step shows stays in this build's own turns.
    /// </summary>
    internal void ContinueRuns(IReadOnlyDictionary<string, SubagentToolCallItem> runs)
    {
        foreach (var (toolCallId, run) in runs)
            _subagentsByToolCallId[toolCallId] = run;
    }

    /// <summary>
    /// Emits the breadcrumb at the very top of a duplicated or forked chat's transcript. Reuses the
    /// linked-chat chip so branches are navigable back to their source with one click, and names the
    /// action that created the chat so the wording matches the menu the user actually used.
    /// </summary>
    private void EmitForkOriginChip(ForkOrigin? forkOrigin)
    {
        if (forkOrigin is not { } origin)
            return;

        var label = Loc.Get(
            origin.FromMessage ? "ForkedFrom" : "DuplicatedFrom",
            string.IsNullOrWhiteSpace(origin.Title) ? Loc.OpenChat : origin.Title);

        var chip = new LinkedChatChipItem(origin.ChatId, label, () => _openChatAction?.Invoke(origin.ChatId));
        AppendToCurrentTurn(new LinkedChatItem(chip), TurnStableIdFor($"fork-origin:{origin.ChatId}"));
    }

    /// <summary>
    /// Refreshes the sources section of the live transcript's assistant message matching
    /// <paramref name="targetMessage"/> in place, without rebuilding the transcript. Returns true
    /// if a matching item was found and refreshed. This avoids re-parsing the entire mounted tail's
    /// markdown when web sources are attached on session idle.
    /// </summary>
    public bool RefreshAssistantSources(ChatMessage targetMessage)
    {
        if (FindLiveAssistant(targetMessage.Id) is not { } assistant)
            return false;

        assistant.RefreshSources();
        return true;
    }

    /// <summary>Restyles messages whose recorded role changed after they were shown.</summary>
    public void RefreshAssistantProgress(IEnumerable<ChatMessage> messages)
    {
        if (!_foldProgressMessages)
            return;

        foreach (var message in messages)
            FindLiveAssistant(message.Id)?.RefreshProgress();
    }

    private AssistantMessageItem? FindLiveAssistant(Guid messageId)
    {
        if (_liveTarget is null)
            return null;

        for (var t = _liveTarget.Count - 1; t >= 0; t--)
        {
            foreach (var item in _liveTarget[t].EnumerateItems())
            {
                if (item is AssistantMessageItem assistant && assistant.MessageId == messageId)
                    return assistant;
            }
        }

        return null;
    }

    /// <summary>
    /// Lets go of the work of messages that moved to the chat's stored history while their turns stay
    /// on screen, owned by the history (see <see cref="TranscriptHistory.AdoptRolledTail"/>), so this
    /// builder no longer keeps their items alive. What they showed — skills, announced files — stays
    /// known, so later messages read as before. Called when the chat is idle: nothing of theirs runs,
    /// and the runs the remaining messages continue are carried by the history's seed instead.
    /// </summary>
    internal void ForgetMessages(IReadOnlyCollection<ChatMessage> messages, IReadOnlyCollection<TranscriptTurn> turns)
    {
        var messageIds = new HashSet<Guid>();
        var toolCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            messageIds.Add(message.Id);
            foreach (var skill in message.ActiveSkills)
                _invokedSkills.Remove(skill.Name);
            if (message.ToolName == "skill")
            {
                var skillName = message.ToolSkillName
                    ?? ToolDisplayHelper.ExtractJsonField(message.Content, "skill");
                if (skillName is not null)
                    _invokedSkills.Remove(skillName);
            }
            if (message.ToolCallId is { Length: > 0 } toolCallId)
                toolCallIds.Add(toolCallId);
        }

        for (var i = _pendingToolHandlers.Count - 1; i >= 0; i--)
        {
            var (vm, handler) = _pendingToolHandlers[i];
            if (!messageIds.Contains(vm.Message.Id))
                continue;
            vm.PropertyChanged -= handler;
            _pendingToolHandlers.RemoveAt(i);
        }

        for (var i = _reasoningItems.Count - 1; i >= 0; i--)
        {
            if (!messageIds.Contains(_reasoningItems[i].MessageId))
                continue;
            _reasoningItems[i].DetachSources();
            _reasoningItems.RemoveAt(i);
        }

        foreach (var toolCallId in toolCallIds)
        {
            _terminalPreviewsByToolCallId.Remove(toolCallId);
            _runningToolCallsByToolCallId.Remove(toolCallId);
            _toolDraftsByToolCallId.Remove(toolCallId);
            _subagentsByToolCallId.Remove(toolCallId);
            _toolParentById.Remove(toolCallId);
            _livePlanToolCallIds.Remove(toolCallId);
            _trackedFileEditToolCalls.Remove(toolCallId);
            _trackedFileEditFilesByToolCall.Remove(toolCallId);
            _deferredFileEditSubscriptions.Remove(toolCallId);
        }

        _processedMessageIds.ExceptWith(messageIds);
        _shownApps.ExceptWith(messages);
        var runsChanged = false;
        for (var i = SubagentRuns.Count - 1; i >= 0; i--)
        {
            if (SubagentRuns[i].ToolCallId is { } runId && toolCallIds.Contains(runId))
            {
                SubagentRuns.RemoveAt(i);
                runsChanged = true;
            }
        }

        // The kept exchange starts with a user message processed after every rolled one, so the
        // builder's per-turn state (its current turn, group, pending plan card...) is the kept
        // exchange's. Only a turn reference could still point back, and is dropped if it does.
        var rolledTurns = turns.ToHashSet();
        if (_currentTurn is not null && rolledTurns.Contains(_currentTurn))
            _currentTurn = null;
        if (_typingTurn is not null && rolledTurns.Contains(_typingTurn))
        {
            _typingTurn = null;
            _typingIndicator = null;
        }

        if (runsChanged)
            _subagentRunsChanged?.Invoke();
        PublishLiveActivity();
    }

    public void ResetState()
    {
        // Unsubscribe all pending PropertyChanged handlers to prevent leaking
        // TranscriptItem references via closures on ChatMessageViewModel events.
        foreach (var (vm, handler) in _pendingToolHandlers)
            vm.PropertyChanged -= handler;
        _pendingToolHandlers.Clear();
        foreach (var reasoning in _reasoningItems)
            reasoning.DetachSources();
        _reasoningItems.Clear();
        _currentReasoning = null;

        _currentToolGroup = null;
        _currentToolGroupCount = 0;
        _currentTodoToolCall = null;
        _currentTodoProgress = null;
        _currentIntentText = null;
        _typingIndicator = null;
        _typingTurn = null;
        _currentTurn = null;
        _requestedAt = null;
        _pendingPlanCard = null;
        _pendingModelName = null;
        _terminalPreviewsByToolCallId.Clear();
        _runningToolCallsByToolCallId.Clear();
        _toolDraftsByToolCallId.Clear();
        _livePlanToolCallIds.Clear();
        _toolParentById.Clear();
        _subagentsByToolCallId.Clear();
        SubagentRuns.Clear();
        RootStepCount = 0;
        _rootPlanCounted = false;
        _subagentRunsChanged?.Invoke();
        _trackedFileEditToolCalls.Clear();
        _trackedFileEditFilesByToolCall.Clear();
        _deferredFileEditSubscriptions.Clear();
        PendingToolFileChips.Clear();
        PendingFileEdits.Clear();
        _pendingFileOriginalContents.Clear();
        _lastFileSnapshots.Clear();
        _pendingWorkspaceFileChanges.Clear();
        ShownFileChips.Clear();
        _announcedFileTurns.Clear();
        _fileChipTurn = new FileChipTurn(0, Guid.Empty);
        _processedMessageIds.Clear();
        _shownApps.Clear();
        _shownSkillNames.Clear();
        _invokedSkills.Clear();
        _shownLinkedChatKeys.Clear();
        PublishLiveActivity();
    }

    /// <summary>
    /// What the assistant is doing right now, for chrome that stays visible while the live step is
    /// scrolled out of view: the running tool group's intent or first running step, else the typing
    /// indicator's label. Null when nothing is live.
    /// </summary>
    public string? LiveActivityLabel { get; private set; }

    /// <summary>Raised when <see cref="LiveActivityLabel"/> changes.</summary>
    public event Action? LiveActivityChanged;

    private void PublishLiveActivity()
    {
        var label = ResolveLiveActivityLabel();
        if (string.Equals(label, LiveActivityLabel, StringComparison.Ordinal))
            return;

        LiveActivityLabel = label;
        LiveActivityChanged?.Invoke();
    }

    private string? ResolveLiveActivityLabel()
    {
        if (_currentToolGroup is { IsActive: true } group)
        {
            // An intent ("Building the charts…") says more than the step that serves it, and the step at work
            // says more than the plan it serves.
            if (_currentIntentText is null && group.ActivityPreview.Count > 0)
            {
                var step = group.ActivityPreview.FirstOrDefault(static item => !item.HasSteps) ?? group.ActivityPreview[0];
                var detail = step.Detail?.Split('\n', 2)[0].Trim();
                return string.IsNullOrEmpty(detail) ? step.Label : $"{step.Label} · {detail}";
            }

            return group.Label;
        }

        return _typingIndicator?.Label;
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
        if (!_processedMessageIds.Add(msgVm.Message.Id))
            return;

        foreach (var skill in msgVm.Message.ActiveSkills)
            RecordInvokedSkill(skill);

        if (msgVm.Role != "reasoning")
            _currentReasoning = null;

        var showToolCalls = _dataStore.Data.Settings.ShowToolCalls;
        var showReasoning = _dataStore.Data.Settings.ShowReasoning;
        var showTimestamps = _dataStore.Data.Settings.ShowTimestamps;
        var expandReasoning = _dataStore.Data.Settings.ExpandReasoningWhileStreaming;

        if (msgVm.Role == "tool")
        {
            ProcessToolMessage(msgVm, showToolCalls, showTimestamps);
            if (msgVm.Message.EmbeddedApp is not null)
                ShowApp(msgVm.Message);
        }
        else if (msgVm.Role == "reasoning")
            ProcessReasoningMessage(msgVm, showReasoning, expandReasoning);
        else
            ProcessChatMessage(msgVm, showTimestamps);
    }

    /// <summary>Shows the card of the app a tool message opened, once, after that tool's steps.</summary>
    public void ShowApp(ChatMessage message)
    {
        if (_appItemFor?.Invoke(message) is not { } item)
            return;
        if (!_shownApps.Add(message))
        {
            item.Refresh();
            return;
        }
        CloseCurrentToolGroup();
        AppendToCurrentTurn(item, TurnStableIdFor(item.StableId));
    }

    /// <summary>
    /// Notes a message this build leaves out (a run's step, which only the run's page shows). Like any
    /// message but reasoning, it ends the reasoning being shown, so reasoning after it starts its own.
    /// </summary>
    internal void NoteOmittedMessage() => _currentReasoning = null;

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

        if (toolName == "ask_input")
        {
            if (IsRebuildingTranscript && msgVm.Message.InputDefinition is not null)
                AddInputRequest(msgVm.Message);
            return;
        }

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
                var isAnswered = !string.IsNullOrEmpty(answer) && msg.ToolStatus is not ("Failed" or "Stopped");
                var isExpired = !isAnswered && IsTerminalToolStatus(msg.ToolStatus);
                var card = new QuestionItem(qid, question, optionsList, freeText && !isAnswered && !isExpired, _submitQuestionAnswerAction, multiSelect && !isAnswered && !isExpired);
                if (isAnswered && answer is not null)
                {
                    card.ApplyAnswer(answer);
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
            var chipTurn = _fileChipTurn;
            ObserveSuccessfulFileTool(msgVm, () => AnnounceFile(
                ToolDisplayHelper.ExtractJsonField(msgVm.Content, "filePath"), chipTurn));
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
            if (msgVm.ToolStatus is "Failed" or "Stopped")
                return;
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

        // A plan update: update_todo's own checklist, or a query whose call changed the agent's todos
        // (the plan read back afterwards). A query whose plan could not be read stays a query.
        if (toolName is "update_todo" or "manage_todo_list"
            || (toolName == "sql" && msgVm.Message.PlanSnapshot is not null))
        {
            if (!showToolCalls)
                return;

            var steps = ToolDisplayHelper.ParseTodoSteps(toolName == "sql" ? msgVm.Message.PlanSnapshot : msgVm.Content);
            if (steps.Count > 0)
            {
                ProcessPlanUpdate(msgVm, steps, initialStatus, toolStableIdSeed, turnStableId);
                return;
            }

            if (toolName != "sql")
                return;
        }

        var shouldFlushLateFileEdit = IsCurrentTurnAlreadyEnded();
        if (ToolDisplayHelper.IsFileEditTool(toolName))
        {
            var chipTurn = _fileChipTurn;
            ObserveSuccessfulFileTool(msgVm, () =>
            {
                foreach (var diff in ToolDisplayHelper.ExtractAllDiffs(toolName, msgVm.Content))
                    QueueEditedFileChip(diff.FilePath, chipTurn);
            });
        }
        var captureLiveSnapshot = !IsRebuildingTranscript && initialStatus == StrataAiToolCallStatus.InProgress;
        var diffs = TrackFileEditToolDiffs(msgVm, toolName, initialStatus);
        if (diffs.Count == 0 || (!showToolCalls && initialStatus == StrataAiToolCallStatus.InProgress))
            SubscribeToDeferredFileEditDiffs(msgVm, toolName);

        if (!showToolCalls)
        {
            // Tool cards are hidden, but the open-chat chip is a first-class transcript affordance
            // (like a loaded-skill chip), so it must still surface. Emit any link already known at
            // process time (a rebuild replaying history, or a stamp that landed before the card) and
            // subscribe for one that arrives asynchronously after the chat tool completes.
            TryEmitLinkedChatChip(msgVm);
            if (!IsRebuildingTranscript
                && msgVm.Message.LinkedChatId is null
                && toolName is "manage_chats" or "read_chat")
                SubscribeLinkedChatChip(msgVm);
            if (shouldFlushLateFileEdit && diffs.Count > 0)
                FlushPendingFileEdits();
            return;
        }

        if (ToolDisplayHelper.IsSubagentTool(toolName))
        {
            ProcessSubagentToolMessage(msgVm, initialStatus, toolStableIdSeed, turnStableId);
            return;
        }

        var displaySkillName = msgVm.Message.ToolSkillName;
        if (displaySkillName is null && toolName == "skill"
            && ToolDisplayHelper.ExtractJsonField(msgVm.Content, "skill") is { Length: > 0 } runtimeSkillName)
            displaySkillName = ResolveFetchedSkill(runtimeSkillName)?.Name;
        var (friendlyName, friendlyInfo) = ToolDisplayHelper.GetFriendlyToolDisplay(
            toolName, msgVm.Author, msgVm.Content, displaySkillName);
        var iconKey = ToolDisplayHelper.GetToolIconKey(toolName);

        var toolCallId = msgVm.Message.ToolCallId;

        if (ToolDisplayHelper.IsShellCommandTool(toolName))
        {
            var termParentSubagent = FindOwningSubagent(msgVm.Message.ParentToolCallId);
            var command = ToolDisplayHelper.ExtractJsonField(msgVm.Content, "command") ?? "";
            var termPreview = new TerminalPreviewItem(friendlyName, command, initialStatus, $"terminal:{toolStableIdSeed}",
                ToolDisplayHelper.GetCommandTitle(msgVm.Content))
            {
                Output = msgVm.Message.ToolOutput ?? string.Empty,
                DurationMs = msgVm.Message.ToolDurationMs ?? 0,
                RunningSince = msgVm.Message.ToolStartedAt,
                IsExpanded = !IsRebuildingTranscript && termParentSubagent is not null,
                Timestamp = msgVm.Message.ToolStartedAt ?? msgVm.Message.Timestamp,
            };
            // Rebuilt while this async shell is still running in the background: recreate the card
            // already running (kept visible + expanded, anchored to the shell's real start time) so a
            // chat switch doesn't fold it into a "finished" summary or reset its elapsed clock.
            if (toolCallId is not null && _knownRunningBackgroundShells.TryGetValue(toolCallId, out var knownStartedUtc))
            {
                termPreview.IsRunningInBackground = true;
                termPreview.RunningSince = knownStartedUtc;
                termPreview.IsExpanded = true;
            }
            if (toolCallId is not null)
                _terminalPreviewsByToolCallId[toolCallId] = termPreview;

            // A tool the sub-agent ran belongs to its run, not to Lumi's own tool group. Creating a
            // group here anyway would leave an empty "Working…" card stranded under the agent row
            // for as long as the run lasts.
            ToolGroupItem? capturedTermGroup = null;
            if (termParentSubagent is null)
            {
                EnsureCurrentToolGroup(initialStatus, toolStableIdSeed, turnStableId);
                capturedTermGroup = _currentToolGroup!;
                capturedTermGroup.Source ??= msgVm;
            }

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

                    if (termPreview.Status is not StrataAiToolCallStatus.InProgress)
                        termPreview.DurationMs = msgVm.Message.ToolDurationMs ?? 0;

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

            AddToolItemToCurrentContext(termPreview, termParentSubagent);

            UpdateToolGroupLabel();
            return;
        }

        var toolCall = new ToolCallItem(friendlyName, initialStatus, $"tool:{toolStableIdSeed}")
        {
            IconKey = iconKey,
            Detail = friendlyInfo,
            InputParameters = ToolDisplayHelper.FormatToolArgsFriendly(toolName, msgVm.Content, displaySkillName),
            MoreInfo = BuildToolCallMoreInfo(friendlyInfo, msgVm, initialStatus),
            DurationMs = msgVm.Message.ToolDurationMs ?? 0,
            RunningSince = msgVm.Message.ToolStartedAt,
            Timestamp = msgVm.Message.ToolStartedAt ?? msgVm.Message.Timestamp,
            IsCompact = ToolDisplayHelper.IsCompactEligible(toolName)
                && initialStatus == StrataAiToolCallStatus.Completed,
        };

        if (toolName == "skill" && _openSkillAction is not null
            && ToolDisplayHelper.ExtractJsonField(msgVm.Content, "skill") is { Length: > 0 } invokedSkillName)
        {
            var resolved = ResolveFetchedSkill(invokedSkillName);
            var skillName = displaySkillName ?? resolved?.Name ?? invokedSkillName;
            if (!_invokedSkills.TryGetValue(skillName, out var reference))
            {
                reference = resolved ?? new SkillReference { Name = skillName };
                _invokedSkills[skillName] = reference;
            }
            toolCall.HeaderCommand = new RelayCommand(() => _openSkillAction(reference));
        }

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

        var toolParentSubagent = FindOwningSubagent(msgVm.Message.ParentToolCallId);
        // See the terminal branch above: a sub-agent's tool must not open one of Lumi's tool groups.
        ToolGroupItem? capturedToolGroup = null;
        if (toolParentSubagent is null)
        {
            EnsureCurrentToolGroup(initialStatus, toolStableIdSeed, turnStableId);
            capturedToolGroup = _currentToolGroup!;
            capturedToolGroup.Source ??= msgVm;
        }

        if (!IsRebuildingTranscript)
        {
            PropertyChangedEventHandler? handler = null;
            handler = (_, args) =>
            {
                if (args.PropertyName == nameof(ChatMessageViewModel.LinkedChatId))
                {
                    TryEmitLinkedChatChip(msgVm);
                    return;
                }
                if (args.PropertyName != nameof(ChatMessageViewModel.ToolStatus))
                    return;

                toolCall.Status = MapToolStatus(msgVm.ToolStatus);
                if (toolCall.Status is not StrataAiToolCallStatus.InProgress)
                    toolCall.DurationMs = msgVm.Message.ToolDurationMs ?? 0;

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
                    if (toolCallId is not null)
                        _runningToolCallsByToolCallId.Remove(toolCallId);
                }
            };
            msgVm.PropertyChanged += handler;
            _pendingToolHandlers.Add((msgVm, handler));
        }

        if (toolCallId is not null && initialStatus == StrataAiToolCallStatus.InProgress)
            _runningToolCallsByToolCallId[toolCallId] = (toolCall, capturedToolGroup);

        AddToolItemToCurrentContext(toolCall, toolParentSubagent);
        UpdateToolGroupLabel();
        if (shouldFlushLateFileEdit && diffs.Count > 0)
            FlushPendingFileEdits();

        // Surface the linked-chat chip for any message that already carries a LinkedChatId at
        // process time — rebuilds replaying persisted history, or a stamp that landed before the
        // card was built. The live PropertyChanged hook above covers the async-arrival case; the
        // per-tool-call de-dup guarantees exactly one chip regardless of which path fires first.
        TryEmitLinkedChatChip(msgVm);
    }

    /// <summary>
    /// Surfaces a chat that Lumi created, messaged, or read via a chat tool as a first-class
    /// transcript chip (like a loaded-skill chip), placed after the current tool group so the
    /// "open chat" affordance is always visible instead of buried inside a collapsed tool card.
    /// De-duplicated per tool call so it renders exactly once across the async live stamp and any
    /// later transcript rebuild.
    /// </summary>
    private void TryEmitLinkedChatChip(ChatMessageViewModel msgVm)
    {
        if (msgVm.Message.LinkedChatId is not Guid chatId)
            return;

        var key = msgVm.Message.ToolCallId ?? msgVm.Message.Id.ToString();
        if (!_shownLinkedChatKeys.Add(key))
            return;

        var chip = new LinkedChatChipItem(chatId, msgVm.Message.LinkedChatTitle ?? string.Empty,
            () => _openChatAction?.Invoke(chatId));
        CloseCurrentToolGroup();
        AppendToCurrentTurn(new LinkedChatItem(chip), TurnStableIdFor($"linked-chat:{key}"));
    }

    /// <summary>
    /// Subscribes for an asynchronous <see cref="ChatMessageViewModel.LinkedChatId"/> stamp so the
    /// open-chat chip surfaces even when tool cards are hidden (the live tool-card handler that
    /// normally carries this is never attached in that mode). The de-dup in
    /// <see cref="TryEmitLinkedChatChip"/> guarantees a single chip if a rebuild later replays it.
    /// </summary>
    private void SubscribeLinkedChatChip(ChatMessageViewModel msgVm)
    {
        PropertyChangedEventHandler? handler = null;
        handler = (_, args) =>
        {
            if (args.PropertyName == nameof(ChatMessageViewModel.LinkedChatId))
                TryEmitLinkedChatChip(msgVm);
        };
        msgVm.PropertyChanged += handler;
        _pendingToolHandlers.Add((msgVm, handler));
    }

    // Tool arguments may arrive after the start event. Only successful completion may
    // announce a file or re-show an edited deliverable; a started/failed edit is not a change.
    private void ObserveSuccessfulFileTool(ChatMessageViewModel message, Action onSuccess)
    {
        if (message.ToolStatus == "Completed" && !string.IsNullOrWhiteSpace(message.Content))
        {
            onSuccess();
            return;
        }
        if (message.ToolStatus is "Failed" or "Stopped")
            return;

        PropertyChangedEventHandler? handler = null;
        handler = (_, args) =>
        {
            if (args.PropertyName is not (nameof(ChatMessageViewModel.ToolStatus) or nameof(ChatMessageViewModel.Content)))
                return;
            var succeeded = message.ToolStatus == "Completed" && !string.IsNullOrWhiteSpace(message.Content);
            if (!succeeded && message.ToolStatus is not ("Failed" or "Stopped"))
                return;

            message.PropertyChanged -= handler;
            RemovePendingHandler(message, handler);
            if (succeeded)
                onSuccess();
        };
        message.PropertyChanged += handler;
        _pendingToolHandlers.Add((message, handler));
    }

    private string? NormalizeFileChipPath(string? filePath, bool requireAbsolute = false)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;
        try
        {
            if (requireAbsolute && !Path.IsPathFullyQualified(filePath))
                return null;
            if (!Path.IsPathFullyQualified(filePath))
                filePath = _resolveFilePath?.Invoke(filePath) ?? filePath;
            return Path.IsPathFullyQualified(filePath) ? Path.GetFullPath(filePath) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private void AnnounceFile(string? filePath, FileChipTurn turn)
    {
        var path = NormalizeFileChipPath(filePath, requireAbsolute: true);
        if (path is null)
            return;

        // Retain the announcement even if an old deliverable is currently missing.
        // A subsequent successful edit/recreation can make it available again.
        if (!_announcedFileTurns.TryGetValue(path, out var firstTurn) || turn.Index < firstTurn)
            _announcedFileTurns[path] = turn.Index;
        ShownFileChips.Add(path);
        QueueFileChip(path, turn, isPreviewable: true, isEdited: false);
    }

    private void QueueEditedFileChip(string? filePath, FileChipTurn turn)
    {
        var path = NormalizeFileChipPath(filePath);
        if (path is null
            || !_announcedFileTurns.TryGetValue(path, out var announcedTurn)
            || announcedTurn >= turn.Index
            || turn.Chips.ContainsKey(path))
        {
            return;
        }

        QueueFileChip(path, turn, isPreviewable: true, isEdited: true);
    }

    /// <summary>Preserves the existing once-per-chat, user-facing new-file discovery behavior.</summary>
    public void AddDetectedFileChip(string? filePath)
    {
        var path = NormalizeFileChipPath(filePath);
        if (path is null || !File.Exists(path) || !ToolDisplayHelper.IsUserFacingFile(path)
            || _announcedFileTurns.ContainsKey(path) || !ShownFileChips.Add(path))
        {
            return;
        }

        QueueFileChip(path, _fileChipTurn, isPreviewable: false, isEdited: false);
    }

    private void QueueFileChip(string path, FileChipTurn turn, bool isPreviewable, bool isEdited)
    {
        if (turn.Chips.TryGetValue(path, out var existing))
        {
            // An explicit announcement upgrades the already mounted auto chip in place.
            existing.IsPreviewable |= isPreviewable;
            if (!isEdited)
                existing.IsEdited = false;
            return;
        }
        if (!File.Exists(path))
            return;

        var chip = new FileAttachmentItem(path, isPreviewable: isPreviewable, isEdited: isEdited);
        turn.Chips.Add(path, chip);
        turn.Pending.Add(chip);
        if (turn.HasEnded || (ReferenceEquals(turn, _fileChipTurn) && IsCurrentTurnAlreadyEnded()))
            FlushPendingFileChips(turn);
    }

    private void FlushPendingFileChips(FileChipTurn turn)
    {
        if (turn.Pending.Count == 0)
            return;

        if (turn.LastAssistant is null)
        {
            // Tool-only/aborted turns still deliver their files. Reuse the normal assistant
            // attachment surface rather than leaving pending chips to leak into the next turn.
            turn.LastAssistant = new AssistantMessageItem(new ChatMessageViewModel(new ChatMessage
            {
                Id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"file-deliverables:{turn.MessageId}"))),
                Role = "assistant",
                Author = Loc.Author_Lumi,
            }), showTimestamps: false);
            if (turn.TranscriptTurn is { } transcriptTurn)
                transcriptTurn.Items.Add(turn.LastAssistant);
            else if (!ReferenceEquals(turn, _fileChipTurn) && GetTurnTarget() is { } turns)
            {
                var fileTurn = new TranscriptTurn(TurnStableIdFor($"file-deliverables:{turn.MessageId}"));
                fileTurn.Items.Add(turn.LastAssistant);
                var userIndex = turn.UserTurn is null ? -1 : turns.IndexOf(turn.UserTurn);
                turns.Insert(userIndex + 1, fileTurn);
                turn.TranscriptTurn = fileTurn;
            }
            else
                turn.TranscriptTurn = AppendAssistantMessageToCurrentTurn(
                    turn.LastAssistant, TurnStableIdFor($"file-deliverables:{turn.MessageId}"));
        }
        turn.LastAssistant.AddFileAttachments(turn.Pending);
        turn.Pending.Clear();
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

        var displayName = ToolDisplayHelper.GetSubagentDisplayName(msgVm.ToolName ?? "", msgVm.Content, msgVm.Author);
        var subagent = new SubagentToolCallItem(displayName, initialStatus, $"subagent:{toolStableIdSeed}")
        {
            OpenRunAction = _openSubagentRunAction,
            ToolCallId = toolCallId,
        };
        // Its page counts its steps when it reads them.
        if (KeepsRunDetailInStorage)
            subagent.KnownStepCount = 0;
        UpdateSubagentFromMessage(subagent, msgVm.Message);

        AttachSubagentToTurn(subagent, turnStableId);
        if (toolCallId is not null)
            _subagentsByToolCallId[toolCallId] = subagent;
        SubagentRuns.Add(subagent);
        _subagentRunsChanged?.Invoke();

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
                subagent.ErrorText = FailureReason(msgVm.Message);
                if (subagent.Status is not StrataAiToolCallStatus.InProgress)
                    subagent.DurationMs = msgVm.Message.ToolDurationMs ?? 0;

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

    /// <summary>
    /// Files a tool card where it belongs: inside the sub-agent run that produced it, or otherwise in
    /// Lumi's own tool group. The owning sub-agent is resolved by the caller, which must skip creating
    /// a tool group entirely when the tool belongs to a run.
    /// </summary>
    private void AddToolItemToCurrentContext(ToolCallItemBase item, SubagentToolCallItem? owningSubagent)
    {
        if (owningSubagent is not null)
        {
            owningSubagent.Activities.Add(item);
            owningSubagent.AddTimelineTool(item);
            UpdateSubagentState(owningSubagent);
            return;
        }

        RootStepCount++;
        _currentToolGroup!.ToolCalls.Add(item);
        _currentToolGroupCount++;
    }

    /// <summary>Directly updates the transcript text on a subagent card (called from live streaming flush).</summary>
    public void UpdateSubagentTranscriptText(string toolCallId, string? text)
    {
        if (!_subagentsByToolCallId.TryGetValue(toolCallId, out var subagent))
            return;

        subagent.TranscriptText = text;
    }

    /// <summary>The run launched by <paramref name="toolCallId"/>, when it is in this transcript.</summary>
    public SubagentToolCallItem? FindSubagentRun(string? toolCallId)
        => !string.IsNullOrEmpty(toolCallId) && _subagentsByToolCallId.TryGetValue(toolCallId, out var run)
            ? run
            : null;

    /// <summary>Directly updates the reasoning text on a subagent card (called from live streaming flush).</summary>
    public void UpdateSubagentReasoningText(string toolCallId, string? text)
    {
        if (!_subagentsByToolCallId.TryGetValue(toolCallId, out var subagent))
            return;

        subagent.ReasoningText = text;
    }

    /// <summary>Applies an authoritative sub-agent status even when the wrapping task tool already
    /// reported the same terminal status and its property-change listener has been released.</summary>
    public void UpdateSubagentToolStatus(string toolCallId, string? status, double? durationMs = null, string? error = null)
    {
        if (!_subagentsByToolCallId.TryGetValue(toolCallId, out var subagent))
            return;

        subagent.Status = MapToolStatus(status);
        if (durationMs is > 0 && subagent.Status is not StrataAiToolCallStatus.InProgress)
            subagent.DurationMs = durationMs.Value;
        if (subagent.Status == StrataAiToolCallStatus.Failed && !string.IsNullOrWhiteSpace(error))
            subagent.ErrorText = error;

        UpdateSubagentState(subagent);
    }

    /// <summary>The error a failed sub-agent reported, kept on its message's tool output.</summary>
    private static string? FailureReason(ChatMessage message)
        => message.ToolStatus == "Failed" && !string.IsNullOrWhiteSpace(message.ToolOutput)
            ? message.ToolOutput.Trim()
            : null;

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
        // One parse for the whole payload: a streaming sub-agent re-enters here on every flush.
        var payload = SubagentPayload.Parse(message.Content, withDetail: !KeepsRunDetailInStorage);
        var taskDescription = ToolDisplayHelper.GetSubagentTaskDescription(toolName, payload);

        subagent.DisplayName = ToolDisplayHelper.GetSubagentDisplayName(toolName, payload, message.Author);
        subagent.TaskDescription = taskDescription;
        subagent.AgentDescription = payload.AgentDescription;
        subagent.ModeLabel = ToolDisplayHelper.GetSubagentModeLabel(payload);

        var modelId = payload.Model
            ?? message.Model
            ?? _getSelectedModel();
        subagent.ModelDisplayName = ChatViewModel.FormatModelDisplay(modelId);

        subagent.StartedAt = message.ToolStartedAt ?? message.Timestamp;
        subagent.DurationMs = message.ToolDurationMs ?? 0;
        subagent.ErrorText = FailureReason(message);
        if (KeepsRunDetailInStorage)
            subagent.ReleaseDetail();
        else
            subagent.ApplyDetail(payload);
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

        if (subagent.OwningGroup is { } owningGroup)
            UpdateSubagentGroupState(owningGroup);

        subagent.NotifyRunContentChanged();
        _subagentRunsChanged?.Invoke();
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
                subagent.OwningGroup = newGroup;
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
            switch (agent.Status)
            {
                case StrataAiToolCallStatus.Completed:
                case StrataAiToolCallStatus.Stopped:
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
        group.ApplyAutoExpansion(running > 0 && !IsRebuildingTranscript);
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

        if (_currentReasoning is not null)
        {
            _currentTurn ??= GetTurnTarget()?.LastOrDefault(turn => turn.Items.Any(item =>
                ReferenceEquals(item, _currentReasoning)
                || item is TurnSummaryItem summary && summary.InnerItems.Contains(_currentReasoning)));
            if (_currentTurn is not null)
                UnfoldHistoryContaining(_currentTurn, item => ReferenceEquals(item, _currentReasoning));
            _currentReasoning.AppendSource(msgVm);
            CompactEarlierActivity();
            return;
        }

        _currentReasoning = new ReasoningItem(msgVm, expandWhileStreaming);
        _reasoningItems.Add(_currentReasoning);
        AppendToCurrentTurn(_currentReasoning, TurnStableIdFor($"reasoning:{msgVm.Message.Id}"));
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
            _requestedAt = msgVm.Message.Timestamp;

            AppendToCurrentTurn(new JobWakeItem(msgVm, showTimestamps), TurnStableIdFor($"job-wake:{msgVm.Message.Id}"));
            BeginFileChipTurn(msgVm.Message.Id);
            FinalizeCurrentTurn();
            return;
        }

        if (msgVm.Role == "user")
        {
            FlushPendingFileEdits();
            FlushPendingPlanCard();
            FlushPendingModelLabel();
            FinalizeCurrentTurn();
            _requestedAt = msgVm.Message.Timestamp;

            if (msgVm.Message.InteractionReceipt is not null)
            {
                var receipt = new InteractionReceiptItem(msgVm.Message);
                if (!IsRebuildingTranscript && _dataStore.Data.Settings.ShowAnimations)
                    receipt.RequestEntranceAnimation();
                AppendToCurrentTurn(receipt, TurnStableIdFor($"message:{msgVm.Message.Id}"));
                BeginFileChipTurn(msgVm.Message.Id);
                FinalizeCurrentTurn();
                return;
            }

            // Only show skills that haven't been displayed yet in this transcript
            var newSkills = msgVm.Message.ActiveSkills
                .Where(s => _shownSkillNames.Add(s.Name))
                .ToList();
            var userItem = new UserMessageItem(
                msgVm,
                showTimestamps,
                newSkills,
                msg => _beginEditMessageAction(msg),
                (msg, edited) => _ = _resendFromMessageAction(msg, edited),
                _openSkillAction,
                _sendSteeredNowAsync,
                _openReplySourceAction,
                _openChatAction);
            if (!IsRebuildingTranscript && _dataStore.Data.Settings.ShowAnimations)
                userItem.RequestEntranceAnimation();
            AppendToCurrentTurn(userItem, TurnStableIdFor($"message:{msgVm.Message.Id}"));
            BeginFileChipTurn(msgVm.Message.Id);
            FinalizeCurrentTurn();
            return;
        }

        if (msgVm.Role == "error")
        {
            AppendToCurrentTurn(new ErrorMessageItem(msgVm, showTimestamps), TurnStableIdFor($"message:{msgVm.Message.Id}"));
            return;
        }

        var assistantItem = new AssistantMessageItem(msgVm, showTimestamps, _openSkillAction);
        if (!IsRebuildingTranscript && _dataStore.Data.Settings.ShowAnimations)
            assistantItem.RequestEntranceAnimation();
        var chipTurn = _fileChipTurn;
        chipTurn.LastAssistant = assistantItem;
        _pendingModelName = ChatViewModel.FormatModelDisplay(msgVm.Message.Model) ?? _pendingModelName;
        // A progress update folds away with the work, so the files that work produced wait for the answer.
        if (!msgVm.IsStreaming && (PendingToolFileChips.Count > 0 || msgVm.Message.Sources.Count > 0 || msgVm.Message.ActiveSkills.Count > 0))
        {
            var takesFiles = !IsRecordedProgress(msgVm);
            assistantItem.ApplyExtras(takesFiles && PendingToolFileChips.Count > 0 ? PendingToolFileChips.ToList() : null, _shownSkillNames);
            if (takesFiles)
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
                    foreach (var skill in msgVm.Message.ActiveSkills)
                        RecordInvokedSkill(skill);
                    var takesFiles = !IsRecordedProgress(msgVm);
                    capturedItem.ApplyExtras(takesFiles && chipTurn.Pending.Count > 0 ? chipTurn.Pending.ToList() : null, _shownSkillNames);
                    if (takesFiles)
                        chipTurn.Pending.Clear();

                    CollapseCompletedTurnBlocks(capturedTurn, capturedItem);
                    FlushPendingPlanCard();
                    msgVm.PropertyChanged -= handler;
                    RemovePendingHandler(msgVm, handler);
                }
            };
            msgVm.PropertyChanged += handler;
            _pendingToolHandlers.Add((msgVm, handler));
        }
        else if (!IsRebuildingTranscript)
        {
            // A rebuild folds each turn once it is complete, when every message's role is known.
            CollapseCompletedTurnBlocks(turn, assistantItem);
        }
    }

    private bool IsRecordedProgress(ChatMessageViewModel message)
        => _foldProgressMessages && message.Message.IsProgress == true;

    public void FlushPendingFileEdits()
    {
        if (PendingFileEdits.Count == 0 && _pendingWorkspaceFileChanges.Count == 0)
        {
            FlushPendingFileChips(_fileChipTurn);
            return;
        }

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
        FlushPendingFileChips(_fileChipTurn);
    }

    private void BeginFileChipTurn(Guid userMessageId)
    {
        _fileChipTurn.HasEnded = true;
        _fileChipTurn = new FileChipTurn(_fileChipTurn.Index + 1, userMessageId)
        {
            UserTurn = _currentTurn,
        };
    }

    private bool IsCurrentTurnAlreadyEnded()
        => _currentTurn?.Items.Any(static item => item is TurnModelItem) == true;

    public void TrackWorkspaceFileChange(string? filePath, bool isCreate)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        var normalizedPath = filePath.Trim();
        QueueEditedFileChip(normalizedPath, _fileChipTurn);
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

        var card = new PlanCardItem(statusText, openAction, TranscriptIds.Create("plan-card"));
        if (!TryAppendAfterLastAnswer(turns, card))
            turns[^1].Items.Add(card);
    }

    /// <summary>Whether a turn shows an answer or tool steps: what a restored plan card follows.</summary>
    internal static bool ShowsAnswer(TranscriptTurn turn)
        => turn.Items.Any(static item => item is AssistantMessageItem or ToolGroupItem);

    /// <summary>Adds <paramref name="item"/> to the last of <paramref name="turns"/> that shows an answer; false when none does.</summary>
    internal static bool TryAppendAfterLastAnswer(IList<TranscriptTurn> turns, TranscriptItem item)
    {
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            if (ShowsAnswer(turns[i]))
            {
                turns[i].Items.Add(item);
                return true;
            }
        }

        return false;
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

        AppendToCurrentTurn(CreateTurnModelItem(_pendingModelName), TurnStableIdFor("turn-model"));
        _fileChipTurn.HasEnded = true;
        FlushPendingFileChips(_fileChipTurn);
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

        AppendToCurrentTurn(CreateTurnModelItem(displayName), TurnStableIdFor("turn-model"));
        _fileChipTurn.HasEnded = true;
        FlushPendingFileChips(_fileChipTurn);
    }

    private TurnModelItem CreateTurnModelItem(string displayName)
    {
        var hasModelCaption = false;
        if (_currentTurn is not null)
        {
            ApplyAssistantRoles(_currentTurn);
            foreach (var assistant in _currentTurn.Items.OfType<AssistantMessageItem>())
            {
                // The completed turn's actual model fills missing snippet metadata, never the current picker.
                if (string.IsNullOrWhiteSpace(assistant.ModelName))
                    assistant.ModelName = displayName;
                // A progress update shows no caption, so only an answer can name the model for the turn.
                hasModelCaption |= assistant.IsItemVisible && !IsFoldableProgress(assistant) && assistant.ModelName == displayName;
            }
        }

        return new TurnModelItem(displayName) { IsItemVisible = !hasModelCaption };
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

    private string? ReadFileContentOrEmpty(string filePath, bool treatMissingAsEmpty = true)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var content = File.ReadAllText(filePath);
                if (_lastFileSnapshots.TryGetValue(filePath, out var snapshot))
                {
                    // Always reread the file; share only text that is still held by a transcript item.
                    if (snapshot.TryGetTarget(out var existing)
                        && string.Equals(existing, content, StringComparison.Ordinal))
                        return existing;

                    snapshot.SetTarget(content);
                }
                else
                {
                    _lastFileSnapshots[filePath] = new WeakReference<string>(content);
                }
                return content;
            }
            return treatMissingAsEmpty ? string.Empty : null;
        }
        catch
        {
            return treatMissingAsEmpty ? string.Empty : null;
        }
    }

    internal static bool IsCreateDiff(string toolName, string? oldText)
        => ToolDisplayHelper.IsFileCreateTool(toolName)
           || (toolName == "apply_patch" && oldText is null);

    /// <summary>
    /// Whether a message shows as one of the user's own (a <see cref="UserMessageItem"/>): a user
    /// message that is not a job's wake or a form's receipt. Jumps between the user's messages stop
    /// at these; <paramref name="text"/> may be just how the message starts.
    /// </summary>
    internal static bool ShowsAsUserMessage(string role, string? author, string? text, bool isReceipt)
        => role == "user" && !isReceipt && !Lumi.Remote.Protocol.RemoteJobWake.IsJobWake(author, text);

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
        PublishLiveActivity();
    }

    public void CloseCurrentToolGroup()
    {
        if (_currentToolGroup is null)
            return;

        // A call still being written belongs to the next group, where it will run, not to history.
        ClearToolDrafts();
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

            // A background-shell group must stay expanded so its live terminal card remains visible;
            // collapsing here would hide the still-running process behind a static "Working…" pill and
            // nothing would re-expand it (the monitor only refreshes the group when the flag flips).
            _currentToolGroup.IsExpanded |= hasBackgroundShell;

            if (canFlattenSingleTool && target is not null)
            {
                var idx = target.IndexOf(_currentToolGroup);
                if (idx >= 0)
                    target[idx] = new SingleToolItem(_currentToolGroup.ToolCalls[0], _currentToolGroup.Source)
                    {
                        IsExpanded = _currentToolGroup.HasExpandedContent,
                    };
            }
        }

        _currentToolGroup = null;
        _currentToolGroupCount = 0;
        _currentTodoToolCall = null;
        _currentTodoProgress = null;
        _currentIntentText = null;
        // Deliberately DO NOT clear _terminalPreviewsByToolCallId here: an async shell can outlive its
        // tool call (and its group can flatten to a pill or be rebuilt), yet the background-shell monitor
        // must still resolve the live terminal card — by tool-call id or by command — after the group
        // closes and even after a full transcript rebuild. The map is bounded by the transcript's
        // terminal count and is reset in ResetState() at the start of each Rebuild().
        // Keep _toolParentById and _subagentsByToolCallId alive across tool groups
        // so late-arriving child tools can still be nested under their parent subagent.
        // These maps are only cleared in ResetState() at the start of Rebuild().
        PublishLiveActivity();
    }

    /// <summary>
    /// Folds the work of the run that just ended. A message sent while Lumi worked splits that work across
    /// turns, so every turn since the last settled one is folded.
    /// </summary>
    public void CollapseCompletedBlocksInCurrentTurn()
    {
        if (IsRebuildingTranscript || _liveTarget is null)
            return;

        for (var i = _liveTarget.Count - 1; i >= 0 && !_liveTarget[i].IsSettled; i--)
        {
            if (!ReferenceEquals(_liveTarget[i], _typingTurn))
                CollapseTurnBlocks(_liveTarget[i]);
        }
    }

    /// <summary>
    /// Folds a turn's finished work. The work before each answer becomes one history row; a turn
    /// without an answer folds its activity around the latest update, which stays readable. Work kept
    /// from any answer by something the reader may still use, such as a plan card, folds where it is.
    /// </summary>
    private void CollapseTurnBlocks(TranscriptTurn turn)
    {
        ApplyAssistantRoles(turn);
        var answers = turn.Items.OfType<AssistantMessageItem>().Where(item => !IsFoldableProgress(item)).ToList();
        for (var i = answers.Count - 1; i >= 0; i--)
            CollapseCompletedTurnBlocks(turn, answers[i], turnFinished: true);

        CollapseActivityOnlyBlocks(turn, settled: true);
        turn.IsSettled = true;
    }

    /// <summary>
    /// Folds a turn Lumi is still working on the way it folded while live: each update stays in view as a
    /// boundary that its finished steps fold up to, and only an answer settles the work before it. A message
    /// still streaming has no role yet, so the end of its stream folds around it, as it does live.
    /// </summary>
    private void CollapseWorkingTurnBlocks(TranscriptTurn turn)
    {
        ApplyAssistantRoles(turn);
        var messages = turn.Items.OfType<AssistantMessageItem>().Where(static item => !item.IsStreaming).ToList();
        if (messages.Count == 0)
        {
            CollapseActivityOnlyBlocks(turn);
            return;
        }

        for (var i = messages.Count - 1; i >= 0; i--)
            CollapseCompletedTurnBlocks(turn, messages[i]);
    }

    private void UpdateToolGroupLabel()
    {
        if (_currentToolGroup is null)
            return;

        UpdateToolGroupState(_currentToolGroup);
    }

    private void UpdateToolGroupState(ToolGroupItem? group)
    {
        UpdateToolGroupStateCore(group);
        if (group is not null && ReferenceEquals(group, _currentToolGroup))
            PublishLiveActivity();
    }

    private void UpdateToolGroupStateCore(ToolGroupItem? group)
    {
        if (group is null)
            return;

        var isCurrent = ReferenceEquals(group, _currentToolGroup);

        if (isCurrent && _currentTodoProgress is not null && _currentTodoProgress.Total > 0)
        {
            var todoDone = _currentTodoProgress.Completed + _currentTodoProgress.Failed;

            // The plan says how far the work has come; the group is at work while any of its steps runs, and
            // only then does its intent trail off.
            group.IsActive = group.ToolCalls.Any(static call => GetStatus(call) == StrataAiToolCallStatus.InProgress
                || call is TerminalPreviewItem { IsRunningInBackground: true });
            group.Label = _currentIntentText is null ? Loc.ToolTodo_Title
                : group.IsActive ? _currentIntentText + "…" : _currentIntentText;
            group.Meta = _currentTodoProgress.Failed > 0
                ? string.Format(Loc.ToolTodo_MetaWithFailed, _currentTodoProgress.Completed, _currentTodoProgress.Total, _currentTodoProgress.Failed)
                : string.Format(Loc.ToolTodo_Meta, _currentTodoProgress.Completed, _currentTodoProgress.Total);

            var progress = Math.Clamp((todoDone * 100d) / _currentTodoProgress.Total, 0d, 100d);
            group.ProgressValue = IsRebuildingTranscript && !group.IsActive ? -1 : progress;
            UpdateToolGroupActivityPreview(group);
            if (!group.IsActive || IsRebuildingTranscript)
                group.IsExpanded = false;
            CompactEarlierActivity();
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
            UpdateToolGroupActivityPreview(group);
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
                        turn.Items[idx] = new SingleToolItem(group.ToolCalls[0], group.Source)
                        {
                            IsExpanded = group.HasExpandedContent,
                        };
                }
            }
        }
        else
        {
            group.IsActive = true;
            group.Label = intentText is not null
                ? intentText + "…"
                : Loc.ToolGroup_Working;
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
        group.ProgressValue = IsRebuildingTranscript && !group.IsActive ? -1 : genericProgress;
        UpdateToolGroupActivityPreview(group);
        CompactEarlierActivity();
    }

    private static void UpdateToolGroupActivityPreview(ToolGroupItem group)
    {
        var preview = new List<ToolActivityPreviewItem>(4);
        var runningCount = 0;
        TodoProgressItem? plan = null;
        if (group.IsActive)
        {
            foreach (var item in group.ToolCalls)
            {
                if (item is TodoProgressItem todo)
                {
                    plan = todo;
                    continue;
                }

                var activity = item switch
                {
                    ToolCallItem { Status: StrataAiToolCallStatus.InProgress } tool
                        => new ToolActivityPreviewItem(tool.ToolName, tool.LiveDetail ?? tool.MoreInfo, tool.IconKey,
                            RunningSince: tool.RunningSince),
                    TerminalPreviewItem terminal when terminal.Status == StrataAiToolCallStatus.InProgress || terminal.IsRunningInBackground
                        => new ToolActivityPreviewItem(terminal.ToolName, terminal.Command, terminal.IconKey,
                            RunningSince: terminal.RunningSince),
                    _ => null
                };
                if (activity is null)
                    continue;

                runningCount++;
                if (preview.Count < 3)
                    preview.Add(activity);
            }
        }

        var runningShown = preview.Count;
        // A plan leads its card, under the header that counts its steps, for as long as the work it lays out
        // goes on.
        if (plan is not null && plan.Steps.Any(static step => !step.IsDone))
            preview.Insert(0, new ToolActivityPreviewItem(plan.CurrentStepTitle ?? plan.ToolName, null, plan.IconKey, plan.GlanceSteps));

        if (!group.ActivityPreview.SequenceEqual(preview))
            group.ActivityPreview = preview;
        group.AdditionalActivityCount = runningCount - runningShown;
        group.IconKey = ResolveToolGroupIconKey(group, preview);
        group.HasFailures = group.ToolCalls.Any(static call => GetStatus(call) == StrataAiToolCallStatus.Failed);
    }

    /// <summary>
    /// The group's glyph says what it is doing: the plan it is following, the step running now, the one kind
    /// of work all its steps share, or mixed work.
    /// </summary>
    private static string ResolveToolGroupIconKey(ToolGroupItem group, IReadOnlyList<ToolActivityPreviewItem> preview)
    {
        if (preview.Count > 0)
            return preview[0].IconKey;

        string? shared = null;
        foreach (var call in group.ToolCalls)
        {
            if (call is TodoProgressItem)
                return TranscriptIcons.Plan;

            if (shared is null)
                shared = call.IconKey;
            else if (!string.Equals(shared, call.IconKey, StringComparison.Ordinal))
                return TranscriptIcons.Mixed;
        }

        return shared ?? TranscriptIcons.Mixed;
    }

    /// <summary>
    /// Files a plan update (an <c>update_todo</c> call, or a <c>sql</c> call that changed the agent's
    /// todos) as the plan card of its tool group, or of the sub-agent run that made it, and keeps the card
    /// in step with the call's status.
    /// </summary>
    private void ProcessPlanUpdate(
        ChatMessageViewModel msgVm,
        List<ToolDisplayHelper.TodoStepSnapshot> steps,
        StrataAiToolCallStatus initialStatus,
        string toolStableIdSeed,
        string turnStableId)
    {
        var todoSubagent = FindOwningSubagent(msgVm.Message.ParentToolCallId);
        if (todoSubagent is not null)
        {
            UpsertSubagentTodoProgressToolCall(todoSubagent, msgVm.Message, steps, msgVm.ToolStatus ?? "InProgress");
            UpdateSubagentState(todoSubagent);
        }
        else
        {
            // A run counts its plan as one step, however often it changes (see the sub-agent branch).
            if (!_rootPlanCounted)
            {
                _rootPlanCounted = true;
                RootStepCount++;
            }

            EnsureCurrentToolGroup(initialStatus, toolStableIdSeed, turnStableId);
            UpsertTodoProgressToolCall(steps, msgVm.ToolStatus ?? "InProgress");
            UpdateToolGroupLabel();
        }

        // A finished call (a rebuild, or a plan read back after its query ended) has nothing more to report.
        if (IsRebuildingTranscript || IsTerminalToolStatus(msgVm.ToolStatus))
            return;

        var capturedGroup = todoSubagent is null ? _currentToolGroup : null;
        var capturedSubagent = todoSubagent;
        var capturedTodoToolCall = todoSubagent is null ? _currentTodoToolCall : null;
        PropertyChangedEventHandler? handler = null;
        handler = (_, args) =>
        {
            if (args.PropertyName != nameof(ChatMessageViewModel.ToolStatus))
                return;

            if (capturedSubagent is not null && capturedSubagent.TodoItem is not null)
            {
                capturedSubagent.TodoToolStatus = msgVm.ToolStatus ?? "InProgress";
                capturedSubagent.TodoItem.Status = MapToolStatus(msgVm.ToolStatus);
                UpdateSubagentState(capturedSubagent);
            }
            else if (capturedTodoToolCall is not null)
            {
                capturedTodoToolCall.Status = MapToolStatus(msgVm.ToolStatus);
                UpdateToolGroupState(capturedGroup);
            }

            if (IsTerminalToolStatus(msgVm.ToolStatus))
            {
                msgVm.PropertyChanged -= handler;
                RemovePendingHandler(msgVm, handler);
            }
        };
        msgVm.PropertyChanged += handler;
        _pendingToolHandlers.Add((msgVm, handler));
    }

    private void UpsertTodoProgressToolCall(List<ToolDisplayHelper.TodoStepSnapshot> steps, string toolStatus)
    {
        var total = steps.Count;
        var completed = steps.Count(static step => IsPlanStatus(step.Status, "completed", "done"));
        var failed = steps.Count(static step => IsPlanStatus(step.Status, "failed"));

        if (_currentTodoProgress is null)
        {
            _currentTodoProgress = new TodoProgressState
            {
                Total = total,
                Completed = completed,
                Failed = failed,
            };
        }
        else
        {
            _currentTodoProgress.Total = total;
            _currentTodoProgress.Completed = completed;
            _currentTodoProgress.Failed = failed;
        }

        if (_currentTodoToolCall is null)
        {
            _currentTodoToolCall = new TodoProgressItem(
                Loc.ToolTodo_Title,
                MapToolStatus(toolStatus),
                $"todo:{_currentToolGroup?.StableId}")
            {
                Steps = BuildPlanSteps(steps),
            };
            if (_currentToolGroup is not null)
            {
                // The plan is a step of its group, so a group holding only the plan keeps it when it closes.
                _currentToolGroup.ToolCalls.Add(_currentTodoToolCall);
                _currentToolGroupCount++;
            }
        }
        else
        {
            // Each update is a call of its own; the latest one is the plan's state.
            _currentTodoToolCall.Status = MapToolStatus(toolStatus);
            _currentTodoToolCall.Steps = BuildPlanSteps(steps);
        }
    }

    /// <summary>
    /// A plan's steps as its card lists them: done, being worked on, failed, blocked, or still to come.
    /// Only the first step in progress is the one at work: a checklist marks every unfinished line in
    /// progress.
    /// </summary>
    internal static IReadOnlyList<PlanStepItem> BuildPlanSteps(IEnumerable<ToolDisplayHelper.TodoStepSnapshot> steps)
    {
        var result = new List<PlanStepItem>();
        var hasCurrent = false;
        foreach (var step in steps)
        {
            var isCurrent = !hasCurrent && IsPlanStatus(step.Status, "in-progress", "in_progress", "running", "active");
            hasCurrent |= isCurrent;
            result.Add(new PlanStepItem(
                step.Title,
                IsDone: IsPlanStatus(step.Status, "completed", "done"),
                IsCurrent: isCurrent,
                IsFailed: IsPlanStatus(step.Status, "failed"),
                IsBlocked: IsPlanStatus(step.Status, "blocked")));
        }

        return result;
    }

    private static bool IsPlanStatus(string status, params string[] names)
        => names.Any(name => string.Equals(status, name, StringComparison.OrdinalIgnoreCase));

    private void UpsertSubagentTodoProgressToolCall(
        SubagentToolCallItem subagent,
        ChatMessage message,
        List<ToolDisplayHelper.TodoStepSnapshot> steps,
        string toolStatus)
    {
        subagent.TodoTotal = steps.Count;
        subagent.TodoCompleted = steps.Count(step => string.Equals(step.Status, "completed", StringComparison.OrdinalIgnoreCase));
        subagent.TodoFailed = steps.Count(step => string.Equals(step.Status, "failed", StringComparison.OrdinalIgnoreCase));
        subagent.TodoToolStatus = toolStatus;
        subagent.TodoUpdateCount++;

        if (subagent.TodoItem is null)
        {
            subagent.TodoItem = new TodoProgressItem(
                Loc.ToolTodo_Title,
                MapToolStatus(toolStatus),
                $"todo:{subagent.StableId}")
            {
                Steps = BuildPlanSteps(steps),
                // The message's own instant, like every other tool item — using "now" would sort a
                // rebuilt plan card after the agent's final answer instead of where it happened.
                Timestamp = message.ToolStartedAt ?? message.Timestamp,
            };
            subagent.Activities.Add(subagent.TodoItem);
            subagent.AddTimelineTool(subagent.TodoItem);
        }
        else
        {
            subagent.TodoItem.Status = MapToolStatus(toolStatus);
            subagent.TodoItem.Steps = BuildPlanSteps(steps);
        }
    }

    /// <param name="turnFinished">The turn has ended, so the work after its answer is settled too.</param>
    private void CollapseCompletedTurnBlocks(TranscriptTurn turn, AssistantMessageItem assistantItem, bool turnFinished = false)
    {
        ApplyAssistantRoles(turn);
        var items = turn.Items;
        var latest = FindLatestAssistant(items);
        var idx = items.IndexOf(assistantItem);
        if (idx < 0)
            return;

        // While Lumi works, every update stays in view as a boundary its finished steps fold up to. The answer
        // settles the work before it, and the end of the turn all of it; only then do the updates fold too.
        var settlesWorkBefore = !IsFoldableProgress(assistantItem);
        CollapseTranscriptBlocks(turn, CollectAdjacentSummaryBlocks(items, idx - 1, -1, latest, settlesWorkBefore), assistantItem, "before");

        idx = items.IndexOf(assistantItem);
        if (idx < 0)
            return;

        CollapseTranscriptBlocks(turn, CollectAdjacentSummaryBlocks(items, idx + 1, 1, latest, turnFinished), assistantItem, "after");
    }

    private void CompactEarlierActivity()
    {
        if (!IsRebuildingTranscript && _currentTurn is not null)
            CollapseActivityOnlyBlocks(_currentTurn, keepLatest: true);
    }

    /// <param name="settled">The work is settled, so what only narrated it folds with it.</param>
    private void CollapseActivityOnlyBlocks(TranscriptTurn turn, bool keepLatest = false, bool settled = false)
    {
        ApplyAssistantRoles(turn);
        var runs = new List<(int StartIndex, List<TranscriptItem> Blocks)>();
        var currentRun = new List<TranscriptItem>();
        var currentRunStart = -1;
        var items = turn.Items;
        var latest = FindLatestAssistant(items);

        var limit = keepLatest ? items.Count - 1 : items.Count;
        for (var i = 0; i < limit; i++)
        {
            if (IsActivityOnlySummaryEligibleBlock(items[i], latest, settled)
                && (!keepLatest || items[i] is TurnSummaryItem || !IsExpandedActivity(items[i])))
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
            CollapseTranscriptBlocks(turn, blocks, $"turn-summary:{turn.StableId}:activity:{startIndex}", keepLatest);
        }

        void AddActivityRunIfCollapsible()
        {
            if (currentRun.Count >= 2)
                runs.Add((currentRunStart, currentRun.ToList()));

            currentRun.Clear();
            currentRunStart = -1;
        }
    }

    private List<TranscriptItem> CollectAdjacentSummaryBlocks(
        IList<TranscriptItem> items,
        int startIndex,
        int step,
        AssistantMessageItem? latestAssistant,
        bool settled)
    {
        var blocks = new List<TranscriptItem>();
        for (var i = startIndex; i >= 0 && i < items.Count; i += step)
        {
            if (!IsSummaryEligibleBlock(items[i], latestAssistant, settled))
                break;

            blocks.Add(items[i]);
        }

        if (step < 0)
            blocks.Reverse();

        return blocks;
    }

    private bool IsSummaryEligibleBlock(TranscriptItem item, AssistantMessageItem? latestAssistant, bool settled)
    {
        // Settled work also takes what only narrated it: its updates (except the latest one and one that
        // delivered files), the skills it loaded, the questions it asked once answered, and its finished agent
        // groups. What the reader may still use, such as a linked chat or a plan card, stays in view.
        var foldsNarration = settled && _foldProgressMessages;
        return item switch
        {
            ToolGroupItem group => !ReferenceEquals(group, _currentToolGroup)
                && !group.IsActive && !ContainsRunningBackgroundShell(group),
            ReasoningItem reasoning => !reasoning.IsActive,
            SingleToolItem single => !single.IsActive && !ContainsRunningBackgroundShell(single),
            SubagentToolCallItem agent => agent.Status != StrataAiToolCallStatus.InProgress,
            AssistantMessageItem assistant => foldsNarration
                && assistant.IsProgress
                && !assistant.IsStreaming
                && !assistant.HasFileAttachments
                && !ReferenceEquals(assistant, latestAssistant),
            SkillLoadedItem => foldsNarration,
            QuestionItem question => foldsNarration && (question.IsAnswered || question.IsExpired),
            ApprovalItem approval => foldsNarration && approval.Request.IsDecided,
            SubagentGroupItem agents => foldsNarration && !agents.IsActive,
            TurnSummaryItem summary => summary.InnerItems.All(inner => IsSummaryEligibleBlock(inner, latestAssistant, settled)),
            _ => false,
        };
    }

    private bool IsActivityOnlySummaryEligibleBlock(TranscriptItem item, AssistantMessageItem? latestAssistant, bool settled)
        => item is not (SubagentToolCallItem or SubagentGroupItem) && IsSummaryEligibleBlock(item, latestAssistant, settled);

    private bool IsFoldableProgress(AssistantMessageItem item) => _foldProgressMessages && item.IsProgress;

    /// <summary>
    /// Gives each assistant message its role in the turn. Recorded messages keep the SDK's verdict;
    /// older history infers it from what followed: a message followed by a later one, or by tool calls
    /// it requested, was progress. The latter is the last update of a turn that was stopped, steered
    /// or cut off mid-work, which must not read as its answer.
    /// </summary>
    private void ApplyAssistantRoles(TranscriptTurn turn)
    {
        if (!_foldProgressMessages)
            return;

        var items = turn.EnumerateItems().ToList();
        var hasLater = false;
        var toolsFollow = false;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            switch (items[i])
            {
                case AssistantMessageItem assistant:
                    assistant.IsProgress = assistant.RecordedProgress ?? (hasLater || toolsFollow);
                    if (assistant.IsItemVisible)
                    {
                        hasLater = true;
                        toolsFollow = false;
                    }
                    break;
                case ToolGroupItem or SingleToolItem or SubagentToolCallItem or SubagentGroupItem or QuestionItem or InputRequestItem:
                    toolsFollow = true;
                    break;
            }
        }
    }

    /// <summary>
    /// The turn's newest message stays readable: while Lumi works it is the latest update, and once
    /// the work ends it is the answer, or the last word before Lumi stopped.
    /// </summary>
    private static AssistantMessageItem? FindLatestAssistant(IList<TranscriptItem> items)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is AssistantMessageItem { IsItemVisible: true } assistant)
                return assistant;
        }

        return null;
    }

    private static bool IsExpandedActivity(TranscriptItem item)
        => item switch
        {
            ToolGroupItem group => group.HasExpandedContent,
            ReasoningItem reasoning => reasoning.IsExpanded,
            SingleToolItem single => single.IsExpanded,
            TurnSummaryItem summary => summary.IsExpanded && !summary.IsAutoExpanded,
            _ => false,
        };

    private static void UnfoldHistoryContaining(TranscriptTurn turn, Func<TranscriptItem, bool> matches)
    {
        for (var i = 0; i < turn.Items.Count; i++)
        {
            if (turn.Items[i] is not TurnSummaryItem summary || !summary.InnerItems.Any(matches))
                continue;

            turn.Items.RemoveAt(i);
            for (var j = 0; j < summary.InnerItems.Count; j++)
                turn.Items.Insert(i + j, summary.InnerItems[j]);
            return;
        }
    }

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
        => CollapseTranscriptBlocks(
            turn,
            blocksToMerge,
            $"turn-summary:{assistantItem.StableId}:{position}",
            // Only a written answer says how long Lumi worked; a bubble that just delivers files does not.
            answer: _foldProgressMessages && position == "before" && !IsFoldableProgress(assistantItem)
                && !string.IsNullOrWhiteSpace(assistantItem.Content) ? assistantItem : null);

    /// <param name="answer">The answer this work led to; its history row says how long Lumi worked.</param>
    private void CollapseTranscriptBlocks(
        TranscriptTurn turn,
        List<TranscriptItem> blocksToMerge,
        string stableId,
        bool earlierActivity = false,
        AssistantMessageItem? answer = null)
    {
        // A finished answer folds even a lone update or history row before it, so it takes the
        // "Worked for" row; other lone blocks already read as one line on their own.
        if (blocksToMerge.Count < 2
            && !(answer is not null && blocksToMerge is [TurnSummaryItem or AssistantMessageItem]))
        {
            return;
        }

        var items = turn.Items;
        var totalToolCalls = 0;
        var failedCount = 0;
        var hasReasoning = false;
        var hasNarration = false;
        var hasTodoProgress = false;
        var approvalCount = 0;
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
            else if (sourceBlock is AssistantMessageItem)
            {
                hasNarration = true;
            }
            else if (sourceBlock is ApprovalItem)
            {
                // A receipt answers for a tool row beside it; it is neither reasoning nor another action.
                approvalCount++;
            }
            else
            {
                hasReasoning = true;
            }
        }

        string label;
        if (answer is not null && GetWorkDuration(turn, answer, flattenedBlocks) is { } worked)
        {
            label = Loc.Get(
                totalToolCalls > 0 || hasNarration ? "TurnSummary_WorkedFor" : "TurnSummary_ThoughtFor",
                FormatWorkDuration(worked));
        }
        else if (hasTodoProgress)
            label = !string.IsNullOrWhiteSpace(todoMeta) ? $"{Loc.ToolTodo_Title} · {todoMeta}" : Loc.ToolTodo_Title;
        else if (lastIntentLabel is not null)
        {
            label = hasReasoning ? $"{lastIntentLabel} · {Loc.TurnSummary_Reasoned.ToLowerInvariant()}" : lastIntentLabel;
            if (totalToolCalls > 1)
                label += $" · {Loc.Get("TurnSummary_Actions", totalToolCalls)}";
        }
        else if (hasReasoning && totalToolCalls > 0)
            label = totalToolCalls == 1 ? Loc.TurnSummary_ReasonedAndOneAction : string.Format(Loc.TurnSummary_ReasonedAndActions, totalToolCalls);
        else if (totalToolCalls > 0)
            label = totalToolCalls == 1 ? Loc.ToolGroup_FinishedOne : string.Format(Loc.ToolGroup_FinishedCount, totalToolCalls);
        else if (hasReasoning)
            label = Loc.TurnSummary_Reasoned;
        else if (hasNarration)
            label = Loc.TurnSummary_Worked;
        else if (approvalCount > 0)
            label = approvalCount == 1 ? Loc.Get("TurnSummary_OneApproval") : Loc.Get("TurnSummary_Approvals", approvalCount);
        else
            label = Loc.TurnSummary_ReasonedAndOneAction;

        if (failedCount > 0)
            label += " · " + Loc.Get("TurnSummary_Failed", failedCount);

        if (earlierActivity)
            label = Loc.Get("TurnSummary_EarlierWork", LowercaseFirst(label));

        var firstIdx = items.IndexOf(blocksToMerge[0]);
        // Keep the history row and its children mounted as more work arrives. Replacing it would
        // reset a user's expansion and the scroll position while they inspect earlier steps.
        var summary = blocksToMerge[0] as TurnSummaryItem;
        // Lumi holds a row open to keep a live plan in view; the finished answer folds it again.
        var isLiveWork = !IsRebuildingTranscript && answer is null;
        if (summary is not null)
        {
            summary.Label = label;
            summary.HasFailures = failedCount > 0;
            if (answer is not null && summary.IsAutoExpanded)
                summary.SetAutoExpanded(false);
            summary.IsExpanded |= blocksToMerge.Skip(1).Any(IsExpandedActivity);
            if (!summary.IsExpanded && isLiveWork && blocksToMerge.Skip(1).Any(IsHeldOpen))
                summary.SetAutoExpanded(true);
            foreach (var block in flattenedBlocks.Skip(summary.InnerItems.Count))
                summary.InnerItems.Add(block);
            foreach (var block in blocksToMerge.Skip(1))
                items.Remove(block);
        }
        else
        {
            summary = new TurnSummaryItem(label, stableId)
            {
                IsExpanded = !IsRebuildingTranscript && blocksToMerge.Any(IsExpandedActivity),
                HasFailures = failedCount > 0,
            };
            if (!summary.IsExpanded && isLiveWork && (hasTodoProgress || blocksToMerge.Any(IsHeldOpen)))
                summary.SetAutoExpanded(true);
            foreach (var block in flattenedBlocks)
                summary.InnerItems.Add(block);
            foreach (var block in blocksToMerge)
                items.Remove(block);
            items.Insert(firstIdx, summary);
        }

        static bool IsHeldOpen(TranscriptItem block) => block is TurnSummaryItem { IsAutoExpanded: true };
    }

    /// <summary>
    /// How long Lumi worked before <paramref name="answer"/>: from the request, or from the turn's
    /// previous answer, until the answer began. Null when the history holds no usable times.
    /// </summary>
    private static TimeSpan? GetWorkDuration(
        TranscriptTurn turn,
        AssistantMessageItem answer,
        IReadOnlyList<TranscriptItem> blocks)
    {
        var start = turn.RequestedAt;
        for (var i = turn.Items.IndexOf(answer) - 1; i >= 0; i--)
        {
            if (turn.Items[i] is AssistantMessageItem { IsProgress: false, IsItemVisible: true } previous)
            {
                start = previous.Timestamp;
                break;
            }
        }

        start ??= blocks.OfType<AssistantMessageItem>()
            .Select(static item => (DateTimeOffset?)item.Timestamp)
            .Min();
        if (start is not { } startedAt)
            return null;

        var duration = answer.Timestamp - startedAt;
        return duration >= TimeSpan.Zero && duration < TimeSpan.FromDays(1) ? duration : null;
    }

    /// <summary>"8s", "1m 42s", "2m", "1h 5m".</summary>
    internal static string FormatWorkDuration(TimeSpan duration)
    {
        var totalSeconds = Math.Max(1, (long)Math.Round(duration.TotalSeconds));
        if (totalSeconds < 60)
            return Loc.Get(nameof(Loc.Chat_DurationSeconds), totalSeconds);

        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        if (minutes < 60)
            return seconds == 0
                ? Loc.Get(nameof(Loc.Chat_DurationMinutes), minutes)
                : Loc.Get(nameof(Loc.Chat_DurationMinutesSeconds), minutes, seconds);

        var hours = minutes / 60;
        minutes %= 60;
        return minutes == 0
            ? Loc.Get(nameof(Loc.Chat_DurationHours), hours)
            : Loc.Get(nameof(Loc.Chat_DurationHoursMinutes), hours, minutes);
    }

    /// <summary>
    /// Lowercases the first letter, for a label that continues another ("Earlier work · reasoned"), unless
    /// its first word is an acronym or a name with capitals inside it ("PR review", "GitHub triage").
    /// </summary>
    internal static string LowercaseFirst(string text)
    {
        if (text.Length == 0 || !char.IsUpper(text[0]))
            return text;

        for (var index = 1; index < text.Length && !char.IsWhiteSpace(text[index]); index++)
        {
            if (char.IsUpper(text[index]))
                return text;
        }

        return char.ToLower(text[0], System.Globalization.CultureInfo.CurrentCulture) + text[1..];
    }

    private void CollapseAllCompletedTurns(TranscriptTurn? workingTurn)
    {
        var turns = GetTurnTarget();
        if (turns is null)
            return;

        foreach (var turn in turns)
        {
            if (ReferenceEquals(turn, workingTurn))
                CollapseWorkingTurnBlocks(turn);
            else
                CollapseTurnBlocks(turn);
        }
    }

    public void ShowTypingIndicator(string? label)
    {
        if (_liveTarget is null)
            return;

        if (_typingIndicator is null)
        {
            _typingIndicator = new TypingIndicatorItem(label ?? Loc.Status_Thinking)
            {
                ActionLabel = _typingActionLabel,
                ActionCommand = _typingActionCommand,
            };
            _typingTurn = new TranscriptTurn("turn:typing");
            _typingTurn.Items.Add(_typingIndicator);
            _liveTarget.Add(_typingTurn);
            PublishLiveActivity();
            return;
        }

        _typingIndicator.Label = label ?? Loc.Status_Thinking;
        _typingIndicator.IsActive = true;
        PublishLiveActivity();
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
        PublishLiveActivity();
    }

    public void UpdateTypingIndicatorLabel(string? label)
    {
        if (_typingIndicator is not null && !string.IsNullOrEmpty(label))
        {
            _typingIndicator.Label = label;
            PublishLiveActivity();
        }
    }

    /// <summary>
    /// Offers an action beside the typing indicator, or removes it when <paramref name="command"/> is null.
    /// It is kept for an indicator shown later, so it survives the indicator being recreated.
    /// </summary>
    public void SetTypingIndicatorAction(string? label, System.Windows.Input.ICommand? command)
    {
        _typingActionLabel = command is null ? null : label;
        _typingActionCommand = command;
        if (_typingIndicator is null)
            return;

        _typingIndicator.ActionLabel = _typingActionLabel;
        _typingIndicator.ActionCommand = _typingActionCommand;
    }

    public void UpdateTerminalOutput(string rootToolCallId, string output, bool replaceExistingOutput)
    {
        if (string.IsNullOrEmpty(output))
            return;

        if (!_terminalPreviewsByToolCallId.TryGetValue(rootToolCallId, out var target))
            return;

        var previousOutput = target.Output;
        if (replaceExistingOutput || string.IsNullOrEmpty(target.Output))
        {
            target.Output = output;
        }
        else if (output.StartsWith(target.Output, StringComparison.Ordinal))
            target.Output = output;
        else if (!target.Output.EndsWith(output, StringComparison.Ordinal))
            target.Output = target.Output + "\n" + output;

        if (!string.Equals(previousOutput, target.Output, StringComparison.Ordinal))
            FindOwningSubagent(rootToolCallId)?.NotifyRunContentChanged();
    }

    /// <summary>
    /// Shows a running tool's latest progress message (the SDK's <c>tool.execution_progress</c>, e.g. an
    /// MCP server's status). Keeps its full markdown for the opened card; the row and live preview show
    /// a plain first-line summary and return to their usual detail when the step ends.
    /// </summary>
    public void UpdateToolProgress(string toolCallId, string? message)
    {
        if (!_runningToolCallsByToolCallId.TryGetValue(toolCallId, out var running)
            || running.Item.Status != StrataAiToolCallStatus.InProgress)
            return;

        var text = message?.Trim();
        if (string.IsNullOrEmpty(text) || string.Equals(text, running.Item.ProgressText, StringComparison.Ordinal))
            return;

        running.Item.ProgressText = text;
        if (running.Group is { } group)
        {
            UpdateToolGroupActivityPreview(group);
            PublishLiveActivity();
        }
    }

    /// <summary>
    /// Shows a <c>sql</c> call that changed the agent's todos as the plan update it is, once the plan has
    /// been read back into <see cref="ChatMessage.PlanSnapshot"/>: the query's row leaves the live tool
    /// group and the group's plan card takes the new steps. A call outside the live group keeps its
    /// snapshot for the next rebuild.
    /// </summary>
    public void ApplyPlanSnapshot(ChatMessageViewModel msgVm)
    {
        var toolCallId = msgVm.Message.ToolCallId;
        var steps = ToolDisplayHelper.ParseTodoSteps(msgVm.Message.PlanSnapshot);
        if (toolCallId is null || steps.Count == 0 || _currentToolGroup is null)
            return;

        // A call's plan can be read back more than once; after the first time it only refreshes the card.
        if (_livePlanToolCallIds.Contains(toolCallId))
        {
            UpsertTodoProgressToolCall(steps, msgVm.ToolStatus ?? "InProgress");
            UpdateToolGroupLabel();
            return;
        }

        var row = _currentToolGroup.ToolCalls.FirstOrDefault(item => item.StableId == $"tool:{toolCallId}");
        if (row is null)
            return;

        _currentToolGroup.ToolCalls.Remove(row);
        _currentToolGroupCount--;
        _runningToolCallsByToolCallId.Remove(toolCallId);
        _livePlanToolCallIds.Add(toolCallId);
        ProcessPlanUpdate(msgVm, steps, MapToolStatus(msgVm.ToolStatus), toolCallId, TurnStableIdFor($"tool:{toolCallId}"));
    }

    /// <summary>
    /// Shows a file tool whose arguments the model is still writing ("Writing notes.md", with how much it
    /// has written and for how long) as a running step of the live tool group, until the call itself
    /// starts (<see cref="EndToolDraft"/>). Nothing of it is kept.
    /// </summary>
    public void ShowToolDraft(string toolCallId, string toolName, string? filePath, long writtenLength, DateTimeOffset startedAt)
    {
        // Like the call it stands for, a draft shows only while tool calls are shown.
        if (!_dataStore.Data.Settings.ShowToolCalls)
            return;

        if (!_toolDraftsByToolCallId.TryGetValue(toolCallId, out var draft))
        {
            EnsureCurrentToolGroup(StrataAiToolCallStatus.InProgress, toolCallId, TurnStableIdFor($"tool:{toolCallId}"));
            draft = new ToolCallItem(ToolDisplayHelper.FormatStreamingToolName(filePath), StrataAiToolCallStatus.InProgress, $"draft:{toolCallId}")
            {
                IconKey = ToolDisplayHelper.GetToolIconKey(toolName),
                RunningSince = startedAt,
            };
            _currentToolGroup!.ToolCalls.Add(draft);
            _currentToolGroupCount++;
            _toolDraftsByToolCallId[toolCallId] = draft;
        }

        draft.ToolName = ToolDisplayHelper.FormatStreamingToolName(filePath);
        draft.Detail = ToolDisplayHelper.FormatFileSize(writtenLength);
        UpdateToolGroupLabel();
    }

    /// <summary>Removes a call's draft row (<see cref="ShowToolDraft"/>) once the call starts.</summary>
    public void EndToolDraft(string toolCallId)
    {
        if (!_toolDraftsByToolCallId.Remove(toolCallId, out var draft) || _currentToolGroup is null)
            return;

        if (_currentToolGroup.ToolCalls.Remove(draft))
            _currentToolGroupCount--;
        UpdateToolGroupLabel();
    }

    /// <summary>Removes every draft row, as when the group closes or the turn ends without the calls starting.</summary>
    public void ClearToolDrafts()
    {
        if (_toolDraftsByToolCallId.Count == 0)
            return;

        foreach (var draft in _toolDraftsByToolCallId.Values)
        {
            if (_currentToolGroup?.ToolCalls.Remove(draft) == true)
                _currentToolGroupCount--;
        }

        _toolDraftsByToolCallId.Clear();
        UpdateToolGroupLabel();
    }

    /// <summary>
    /// Marks (or clears) the "still running in the background" state on a terminal card whose async
    /// shell outlives the tool call. While set, the card keeps its live running affordance and its
    /// enclosing tool group stays expanded/visible so the process never looks finished prematurely.
    /// When cleared, <paramref name="finalDurationMs"/> (if provided) becomes the card's real total
    /// duration instead of the misleadingly-short tool-call launch time.
    /// </summary>
    public void SetTerminalRunningInBackground(string rootToolCallId, bool running, double? finalDurationMs = null,
        DateTimeOffset? startedUtc = null, string? finalStatus = null)
    {
        if (!_terminalPreviewsByToolCallId.TryGetValue(rootToolCallId, out var card))
            return;

        var changed = card.IsRunningInBackground != running;
        var presentationChanged = changed;
        card.IsRunningInBackground = running;
        if (finalStatus is not null)
        {
            var status = MapToolStatus(finalStatus);
            presentationChanged |= card.Status != status;
            card.Status = status;
        }

        if (running)
        {
            // Anchor the elapsed clock to the shell's real start time so it survives control
            // recreation (chat switch, virtualization) and manual collapse. Idempotent: the monitor
            // passes the same StartedAt every poll, so the setter no-ops after the first.
            if (startedUtc is { } s)
            {
                presentationChanged |= card.RunningSince != s;
                card.RunningSince = s;
            }

            // Auto-expand only on the transition into "running in background" — NOT on every poll —
            // so a manual collapse by the user is respected instead of springing back open each tick.
            if (changed)
                card.IsExpanded = true;
        }
        else if (finalDurationMs is > 0)
        {
            presentationChanged |= card.DurationMs != finalDurationMs.Value;
            card.DurationMs = finalDurationMs.Value;
        }

        if (presentationChanged)
        {
            RefreshOwningToolGroup(card);
            FindOwningSubagent(rootToolCallId)?.NotifyRunContentChanged();
        }
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
            if (running)
            {
                UnfoldHistoryContaining(turn, item => item switch
                {
                    ToolGroupItem group => group.ToolCalls.Contains(card),
                    SingleToolItem single => ReferenceEquals(single.Inner, card),
                    _ => false,
                });
            }

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
                        else
                            single.NotifyTerminalPresentationChanged();
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

    /// <summary>Shows an approval card where the work is waiting, once per request.</summary>
    public void AddApprovalToTranscript(PermissionRequestViewModel request)
    {
        if (_liveTarget?.SelectMany(turn => turn.Items).OfType<ApprovalItem>()
                .Any(item => ReferenceEquals(item.Request, request)
                             || (item.Request.RequestId == request.RequestId && item.Request.SessionId == request.SessionId)) == true)
            return;
        CloseCurrentToolGroup();
        AppendToCurrentTurn(new ApprovalItem(request), TurnStableIdFor($"approval:{request.RequestId}"));
    }

    public void AddInputRequest(ChatMessage message)
    {
        CloseCurrentToolGroup();
        if (_liveTarget?.SelectMany(turn => turn.Items).OfType<InputRequestItem>()
                .FirstOrDefault(item => item.Message.Id == message.Id) is { } existing)
        {
            existing.Refresh();
            return;
        }
        AppendToCurrentTurn(new InputRequestItem(message), TurnStableIdFor($"input:{message.Id}"));
    }

    private void AppendToCurrentTurn(TranscriptItem item, string turnStableId)
        => AppendItemToCurrentTurn(item, turnStableId);

    private TranscriptTurn AppendAssistantMessageToCurrentTurn(AssistantMessageItem item, string turnStableId)
        => AppendItemToCurrentTurn(item, turnStableId);

    private TranscriptTurn AppendItemToCurrentTurn(TranscriptItem item, string turnStableId)
    {
        if (item is not ReasoningItem)
            _currentReasoning = null;

        if (_currentTurn is not null)
        {
            _currentTurn.Items.Add(item);
            // New work reopens a turn an earlier run settled, so the end of this run folds it again.
            _currentTurn.IsSettled = false;
            if (item is not (UserMessageItem or JobWakeItem))
                _fileChipTurn.TranscriptTurn = _currentTurn;
            CompactEarlierActivity();
            return _currentTurn;
        }

        // Insert the turn only after it has content so the paging controller
        // never observes a transient empty turn and skips mounting it.
        var turn = new TranscriptTurn(turnStableId);
        turn.Items.Add(item);
        if (item is not (UserMessageItem or JobWakeItem or InteractionReceiptItem))
            turn.RequestedAt = _requestedAt;
        _currentTurn = turn;
        if (item is not (UserMessageItem or JobWakeItem))
            _fileChipTurn.TranscriptTurn = turn;
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
