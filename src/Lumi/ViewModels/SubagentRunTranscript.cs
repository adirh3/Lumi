using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Lumi.Models;
using Lumi.Services;
namespace Lumi.ViewModels;

/// <summary>
/// Renders one sub-agent run as a real chat transcript.
/// <para>
/// The run page does not draw its own conversation: it feeds the agent's work through the very same
/// <see cref="TranscriptBuilder"/> the main chat uses, so consecutive tool calls group, finished
/// stretches of activity fold into a "Reasoned · 12 actions" line between the agent's messages, and
/// every tool renders with the card Lumi uses for its own steps. The only things synthesized here are
/// the agent's assistant/reasoning messages, which live on the tool payload's run log rather than in
/// the chat. The instruction the agent was given is shown by the page's Task card, not as a message.
/// </para>
/// <para>
/// A running agent changes constantly: its text streams, it starts and finishes tools, and its
/// messages finalize. Rebuilding the whole transcript for each of those would re-create and re-parse
/// every item several times a second, so a sync extends the existing transcript in place through
/// the builder's live path — the same one the main chat streams through — and only rebuilds when the
/// run's history itself changed (a different run, a setting, something already shown going away).
/// </para>
/// </summary>
internal sealed class SubagentRunTranscript
{
    private readonly DataStore _dataStore;
    private readonly TranscriptBuilder _builder;

    public SubagentRunTranscript(DataStore dataStore, Action<FileChangeItem> showDiffAction)
    {
        _dataStore = dataStore;
        // A read-only view: editing, resending and answering belong to the chat that owns the run.
        // The page exists to inspect the agent's steps, so its messages stay unfolded between them.
        _builder = new TranscriptBuilder(
            dataStore,
            showDiffAction,
            submitQuestionAnswerAction: static (_, _) => { },
            beginEditMessageAction: static _ => { },
            resendFromMessageAction: static (_, _) => System.Threading.Tasks.Task.CompletedTask,
            getSelectedModel: static () => null,
            foldProgressMessages: false);
    }

    /// <summary>The run rendered as transcript turns, ready for the chat's own turn control.</summary>
    public ObservableCollection<TranscriptTurn> Turns { get; private set; } = [];

    /// <summary>
    /// Brings the rendered run up to date: new steps and messages are appended to the live
    /// transcript, a growing tail is pushed into the bubble already bound to it, and the transcript
    /// is rebuilt only when what is already on screen no longer matches the run.
    /// </summary>
    public void Sync(SubagentToolCallItem run, IReadOnlyList<ChatMessageViewModel> chatMessages)
    {
        if (!string.Equals(_runStableId, run.StableId, StringComparison.Ordinal))
            ResetForRun(run.StableId);

        var entries = run.RunEntries;
        SettleTail(ReasoningTailKey, run.ReasoningText, SubagentRunEntryKind.Reasoning, entries);
        SettleTail(AssistantTailKey, run.TranscriptText, SubagentRunEntryKind.Assistant, entries);

        var shape = BuildShape(run);
        var messages = BuildRunMessages(run, CollectRunToolMessages(chatMessages, run.ToolCallId));
        _syncedEntryCount = entries.Count;

        if (shape == _shape && TryExtend(messages))
            return;

        Rebuild(messages, shape);
    }

    /// <summary>Drops the built turns and releases the builder's listeners.</summary>
    public void Clear()
    {
        _builder.ResetState();
        EndAndClearSynthesizedMessages();
        ForgetFedMessages();
        _runStableId = null;
        _shape = null;
        _syncedEntryCount = 0;
        Turns = [];
    }

    private const string ReasoningTailKey = "live:reasoning";
    private const string AssistantTailKey = "live:assistant";

    private string? _runStableId;
    private RunShape? _shape;
    private int _syncedEntryCount;
    private int _tailGeneration;

    /// <summary>Every message handed to the builder since its last rebuild, in transcript order.</summary>
    private readonly List<ChatMessageViewModel> _fed = [];
    private readonly HashSet<ChatMessageViewModel> _fedSet = new(ReferenceEqualityComparer.Instance);

    /// <summary>The tool messages on screen, with the state the transcript last reflected.</summary>
    private readonly Dictionary<ChatMessageViewModel, FedTool> _fedTools = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// <see cref="IsLive"/> cards were appended through the builder's live path and follow their own
    /// status; cards built by a rebuild have no such link, so a later status change rebuilds.
    /// </summary>
    private sealed class FedTool(long revision, string? status, bool isLive)
    {
        public long Revision { get; set; } = revision;
        public string? Status { get; set; } = status;
        public bool IsLive { get; } = isLive;
    }

    /// <summary>
    /// A direct agent-row click can switch A → B without passing through the index (and therefore
    /// without calling <see cref="Clear"/>). Incremental messages and disclosure state are only
    /// meaningful within one run, so drop them before B is rendered.
    /// </summary>
    private void ResetForRun(string stableId)
    {
        Clear();
        _runStableId = stableId;
    }

    private void ForgetFedMessages()
    {
        _fed.Clear();
        _fedSet.Clear();
        _fedTools.Clear();
    }

    /// <summary>
    /// What decides how the run's existing items render, beyond the items themselves. Anything here
    /// changing means the items on screen are stale, so the transcript is rebuilt.
    /// </summary>
    private RunShape BuildShape(SubagentToolCallItem run)
        => new(
            run.StableId,
            run.DisplayName,
            run.StartedAt,
            _dataStore.Data.Settings.ShowTimestamps,
            _dataStore.Data.Settings.ShowToolCalls,
            _dataStore.Data.Settings.ShowReasoning,
            _dataStore.Data.Settings.ExpandReasoningWhileStreaming);

    private readonly record struct RunShape(
        string StableId,
        string DisplayName,
        DateTimeOffset? StartedAt,
        bool ShowTimestamps,
        bool ShowToolCalls,
        bool ShowReasoning,
        bool ExpandReasoningWhileStreaming);

    /// <summary>
    /// Appends what is new since the last sync through the builder's live path. Returns false when
    /// the transcript can't simply be extended — something already shown is gone, or a card built by
    /// a rebuild changed — and must be rebuilt instead.
    /// </summary>
    private bool TryExtend(List<ChatMessageViewModel> messages)
    {
        if (messages.Count < _fed.Count)
            return false;

        var current = new HashSet<ChatMessageViewModel>(messages, ReferenceEqualityComparer.Instance);
        foreach (var message in _fed)
        {
            if (!current.Contains(message))
                return false;
        }

        List<ChatMessageViewModel>? changedTools = null;
        foreach (var (tool, fed) in _fedTools)
        {
            if (tool.PresentationRevision == fed.Revision)
                continue;

            var isShell = ToolDisplayHelper.IsShellCommandTool(tool.ToolName);
            if (!fed.IsLive)
            {
                // A rebuilt card doesn't follow its message: its outcome changing needs a rebuild.
                if (!string.Equals(tool.ToolStatus, fed.Status, StringComparison.Ordinal))
                    return false;

                // A nested agent's payload churns while it streams; its card shows who and how it
                // ended, which the status change above refreshes. Other detail changes do rebuild.
                if (!isShell && !ToolDisplayHelper.IsSubagentTool(tool.ToolName))
                    return false;
            }

            (changedTools ??= []).Add(tool);
        }

        if (changedTools is not null)
        {
            foreach (var tool in changedTools)
            {
                var fed = _fedTools[tool];
                fed.Revision = tool.PresentationRevision;
                fed.Status = tool.ToolStatus;

                // A live card follows its own status; a terminal's output is pushed in, like the main
                // chat does for its own shells.
                if (ToolDisplayHelper.IsShellCommandTool(tool.ToolName)
                    && tool.Message.ToolCallId is { } toolCallId
                    && tool.Message.ToolOutput is { Length: > 0 } output)
                {
                    _builder.UpdateTerminalOutput(toolCallId, output, replaceExistingOutput: true);
                }
            }
        }

        // New messages extend the transcript in the order they arrived, after everything on screen.
        foreach (var message in messages)
        {
            if (_fedSet.Contains(message))
                continue;

            _builder.ProcessMessageToTranscript(message);
            Remember(message, isLive: true);
        }

        return true;
    }

    private void Rebuild(List<ChatMessageViewModel> messages, RunShape shape)
    {
        _shape = shape;
        var expansion = CaptureExpansion();

        RestartStreamingMessages();
        ForgetFedMessages();
        Turns = _builder.Rebuild(messages);
        foreach (var message in messages)
            Remember(message, isLive: false);

        // The whole run is one continuous turn: live work extends it rather than opening another.
        _builder.ContinueLastTurn();
        RestoreExpansion(expansion);
    }

    private void Remember(ChatMessageViewModel message, bool isLive)
    {
        _fed.Add(message);
        _fedSet.Add(message);
        if (message.Role == "tool")
            _fedTools[message] = new FedTool(message.PresentationRevision, message.ToolStatus, isLive);
    }

    /// <summary>
    /// Settles a streaming tail. A message finalizes in the same update that clears its live field
    /// and appends it to the run log — and the agent's next message of the same kind may already be
    /// streaming by the time the page syncs. Either way the first new entry of this kind is the
    /// message the tail was showing, so the tail's message (and the bubble already bound to it)
    /// becomes that entry and keeps its place; newer streaming text starts a tail of its own. A tail
    /// that vanished without becoming an entry ends its streaming state so detached items
    /// unsubscribe, and is dropped (which rebuilds).
    /// </summary>
    private void SettleTail(string key, string? text, SubagentRunEntryKind kind, IReadOnlyList<SubagentRunEntry> entries)
    {
        if (!_synthesized.TryGetValue(key, out var tail))
            return;

        for (var i = _syncedEntryCount; i < entries.Count; i++)
        {
            var entryKey = EntryKey(i);
            if (entries[i].Kind == kind && !_synthesized.ContainsKey(entryKey))
            {
                _synthesized.Remove(key);
                _synthesized[entryKey] = tail;
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _synthesized.Remove(key);
            EndStreaming(tail);
        }
    }

    private void EndAndClearSynthesizedMessages()
    {
        foreach (var message in _synthesized.Values)
            EndStreaming(message);

        _synthesized.Clear();
    }

    private static void EndStreaming(ChatMessageViewModel message)
    {
        if (!message.Message.IsStreaming && !message.IsStreaming)
            return;

        message.Message.IsStreaming = false;
        message.IsStreaming = false;
    }

    /// <summary>
    /// Items built from a streaming message follow it until it ends. Before the tree is replaced,
    /// each streaming message sends that end so the old items let go, then streams again so the
    /// replacement items follow it.
    /// </summary>
    private void RestartStreamingMessages()
    {
        foreach (var message in _synthesized.Values)
        {
            if (!message.IsStreaming)
                continue;

            EndStreaming(message);
            message.Message.IsStreaming = true;
            message.IsStreaming = true;
        }
    }

    private static string EntryKey(int index) => $"entry:{index}";

    private List<ChatMessageViewModel> BuildRunMessages(
        SubagentToolCallItem run,
        List<ChatMessageViewModel> tools)
    {
        var ordered = new List<(DateTimeOffset At, ChatMessageViewModel Message)>(run.RunEntries.Count + tools.Count + 2);

        var previous = run.StartedAt ?? DateTimeOffset.MinValue;
        var entries = run.RunEntries;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var at = entry.Timestamp == default ? previous : entry.Timestamp;
            previous = at;
            var role = entry.Kind == SubagentRunEntryKind.Reasoning ? "reasoning" : "assistant";
            ordered.Add((at, Synthesize(run, EntryKey(i), role, entry.Text, at)));
        }

        foreach (var tool in tools)
            ordered.Add((tool.Message.ToolStartedAt ?? tool.Message.Timestamp, tool));

        // Whatever is still streaming trails the run exactly like a live reply trails a chat.
        if (!string.IsNullOrWhiteSpace(run.ReasoningText))
            ordered.Add((DateTimeOffset.MaxValue, Synthesize(run, ReasoningTailKey, "reasoning", run.ReasoningText!, null, run.IsInProgress)));
        if (!string.IsNullOrWhiteSpace(run.TranscriptText))
            ordered.Add((DateTimeOffset.MaxValue, Synthesize(run, AssistantTailKey, "assistant", run.TranscriptText!, null, run.IsInProgress)));

        // A stable sort: entries and tools recorded at the same instant keep their log order.
        return ordered.OrderBy(static entry => entry.At).Select(static entry => entry.Message).ToList();
    }

    /// <summary>
    /// Walks the chat for every tool message this run produced, following the parent chain so a
    /// tool a nested agent ran is collected too. Messages are chronological, so a parent is always
    /// seen before the tools it owns.
    /// </summary>
    private static List<ChatMessageViewModel> CollectRunToolMessages(
        IReadOnlyList<ChatMessageViewModel> chatMessages,
        string? rootToolCallId)
    {
        var collected = new List<ChatMessageViewModel>();
        if (string.IsNullOrWhiteSpace(rootToolCallId))
            return collected;

        var owned = new HashSet<string>(StringComparer.Ordinal) { rootToolCallId! };
        foreach (var msgVm in chatMessages)
        {
            var parent = msgVm.Message.ParentToolCallId;
            if (string.IsNullOrWhiteSpace(parent) || !owned.Contains(parent))
                continue;

            if (!string.IsNullOrWhiteSpace(msgVm.Message.ToolCallId))
                owned.Add(msgVm.Message.ToolCallId!);

            collected.Add(msgVm);
        }

        return collected;
    }

    /// <summary>
    /// Wraps run-log text in a message the transcript builder can consume, reusing the instance
    /// created for the same key on an earlier pass. Reuse is what lets the streaming tail update in
    /// place: the live items stay subscribed to the very message this returns. A message keeps the id
    /// it was created with — transcript stable ids hang off it, and keeping them is what lets turns,
    /// scroll position and disclosure state survive rebuilds. Run-log entries get an id derived from
    /// the run and their position; each streaming tail gets its own, since a finalized tail carries
    /// its id on into the run log while a later tail streams under the same key.
    /// </summary>
    private ChatMessageViewModel Synthesize(
        SubagentToolCallItem run,
        string key,
        string role,
        string content,
        DateTimeOffset? timestamp,
        bool isStreaming = false)
    {
        if (_synthesized.TryGetValue(key, out var existing))
        {
            var message = existing.Message;
            message.Role = role;
            message.Author = run.DisplayName;
            message.Timestamp = timestamp ?? DateTimeOffset.Now;

            if (!string.Equals(message.Content, content, StringComparison.Ordinal))
            {
                message.Content = content;
                existing.NotifyContentChanged();
            }

            if (message.IsStreaming != isStreaming || existing.IsStreaming != isStreaming)
            {
                message.IsStreaming = isStreaming;
                existing.IsStreaming = isStreaming;
            }

            return existing;
        }

        var idKey = key.StartsWith("live:", StringComparison.Ordinal)
            ? $"{run.StableId}|{key}|{++_tailGeneration}"
            : $"{run.StableId}|{key}";
        var created = new ChatMessageViewModel(new ChatMessage
        {
            Id = DeterministicId(idKey),
            Role = role,
            Content = content,
            Author = run.DisplayName,
            Timestamp = timestamp ?? DateTimeOffset.Now,
            IsStreaming = isStreaming,
        });
        _synthesized[key] = created;
        return created;
    }

    private readonly Dictionary<string, ChatMessageViewModel> _synthesized = new(StringComparer.Ordinal);

    private static Guid DeterministicId(string key)
        => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));

    private Dictionary<string, bool> CaptureExpansion()
    {
        var expansion = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var turn in Turns)
            Capture(turn.Items);

        return expansion;

        void Capture(IEnumerable<TranscriptItem> items)
        {
            foreach (var item in items)
            {
                if (TryGetExpansion(item, out var isExpanded))
                    expansion[item.StableId] = isExpanded;
                if (item is TurnSummaryItem summary)
                    Capture(summary.InnerItems);
            }
        }
    }

    private void RestoreExpansion(Dictionary<string, bool> expansion)
    {
        if (expansion.Count == 0)
            return;

        foreach (var turn in Turns)
            Restore(turn.Items);

        void Restore(IEnumerable<TranscriptItem> items)
        {
            foreach (var item in items)
            {
                if (expansion.TryGetValue(item.StableId, out var isExpanded))
                    SetExpansion(item, isExpanded);
                if (item is TurnSummaryItem summary)
                    Restore(summary.InnerItems);
            }
        }
    }

    private static bool TryGetExpansion(TranscriptItem item, out bool isExpanded)
    {
        switch (item)
        {
            case ToolGroupItem group:
                isExpanded = group.IsExpanded;
                return true;
            case ReasoningItem reasoning:
                isExpanded = reasoning.IsExpanded;
                return true;
            case TurnSummaryItem summary:
                isExpanded = summary.IsExpanded;
                return true;
            case SingleToolItem single:
                isExpanded = single.IsExpanded;
                return true;
            default:
                isExpanded = false;
                return false;
        }
    }

    private static void SetExpansion(TranscriptItem item, bool isExpanded)
    {
        switch (item)
        {
            case ToolGroupItem group:
                group.IsExpanded = isExpanded;
                break;
            case ReasoningItem reasoning:
                reasoning.IsExpanded = isExpanded;
                break;
            case TurnSummaryItem summary:
                summary.IsExpanded = isExpanded;
                break;
            case SingleToolItem single:
                single.IsExpanded = isExpanded;
                break;
        }
    }
}
