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
/// The run island does not draw its own conversation: it feeds the agent's work through the very
/// same <see cref="TranscriptBuilder"/> the main chat uses, so consecutive tool calls collapse into
/// one group, reasoning folds into a think card, and every tool renders with the card Lumi uses for
/// its own steps. The only thing synthesized here are the messages the run never had as real
/// <see cref="ChatMessage"/>s — the instruction it was given and its assistant/reasoning text, which
/// live on the tool payload's run log rather than in the chat.
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
        _builder = new TranscriptBuilder(
            dataStore,
            showDiffAction,
            submitQuestionAnswerAction: static (_, _) => { },
            beginEditMessageAction: static _ => { },
            resendFromMessageAction: static (_, _) => System.Threading.Tasks.Task.CompletedTask,
            getSelectedModel: static () => null)
        {
            CollapseCompletedTurns = false,
        };
    }

    /// <summary>The run rendered as transcript turns, ready for the chat's own turn control.</summary>
    public ObservableCollection<TranscriptTurn> Turns { get; private set; } = [];

    /// <summary>
    /// Brings the rendered run up to date.
    /// <para>
    /// A streaming agent changes its tail text many times a second. Rebuilding for each of those
    /// would re-create every transcript item and defeat the incremental machinery, so a rebuild only
    /// happens when the run's <em>shape</em> changes — a new step, a finalized message, a status
    /// flip. A growing tail is pushed straight into the live items instead, through the very same
    /// streaming path the main chat uses.
    /// </para>
    /// </summary>
    public void Sync(SubagentToolCallItem run, IReadOnlyList<ChatMessageViewModel> chatMessages)
    {
        if (!string.Equals(_runStableId, run.StableId, StringComparison.Ordinal))
            ResetForRun(run.StableId);

        EvictMissingTail("live:reasoning", run.ReasoningText);
        EvictMissingTail("live:assistant", run.TranscriptText);

        var tools = CollectRunToolMessages(chatMessages, run.ToolCallId);
        var shape = BuildShape(run, tools);

        if (Turns.Count > 0 && shape == _shape)
        {
            UpdateStreamingTail(run);
            return;
        }

        _shape = shape;
        var expansion = CaptureExpansion();
        DetachStreamingItemsBeforeRebuild();
        Turns = _builder.Rebuild(BuildRunMessages(run, tools));
        RestoreExpansion(expansion);
    }

    /// <summary>Drops the built turns and releases the builder's listeners.</summary>
    public void Clear()
    {
        _builder.ResetState();
        EndAndClearSynthesizedMessages();
        _runStableId = null;
        _shape = null;
        Turns = [];
    }

    private string? _runStableId;
    private RunShape? _shape;

    /// <summary>
    /// A direct agent-row click can switch A → B without passing through the index (and therefore
    /// without calling <see cref="Clear"/>). Incremental messages and disclosure state are only
    /// meaningful within one run, so drop them before B is rendered.
    /// </summary>
    private void ResetForRun(string stableId)
    {
        EndAndClearSynthesizedMessages();
        _runStableId = stableId;
        _shape = null;
        Turns = [];
    }

    /// <summary>
    /// Everything that decides which items the run renders — deliberately excluding the streaming
    /// tail text, which is what the in-place path handles.
    /// </summary>
    private RunShape BuildShape(
        SubagentToolCallItem run,
        List<ChatMessageViewModel> tools)
    {
        var toolSignature = new StringBuilder();
        foreach (var tool in tools)
            AppendValue(toolSignature, tool.Message.ToolCallId);
        foreach (var tool in tools)
            toolSignature.Append('|').Append(tool.PresentationRevision);

        return new RunShape(
            run.StableId,
            run.DisplayName,
            run.Prompt,
            run.StartedAt,
            // SyncRunEntries replaces this immutable list only when the entries JSON changes, so
            // reference equality is a constant-time structural version check.
            run.RunEntries,
            run.IsInProgress,
            !string.IsNullOrWhiteSpace(run.ReasoningText),
            !string.IsNullOrWhiteSpace(run.TranscriptText),
            toolSignature.ToString(),
            _dataStore.Data.Settings.ShowTimestamps,
            _dataStore.Data.Settings.ShowToolCalls,
            _dataStore.Data.Settings.ShowReasoning,
            _dataStore.Data.Settings.ExpandReasoningWhileStreaming);

        static void AppendValue(StringBuilder target, string? value)
        {
            target.Append('|').Append(value?.Length ?? -1).Append(':');
            if (value is not null)
                target.Append(value);
        }
    }

    private readonly record struct RunShape(
        string StableId,
        string DisplayName,
        string? Prompt,
        DateTimeOffset? StartedAt,
        IReadOnlyList<SubagentRunEntry> Entries,
        bool IsInProgress,
        bool HasReasoningTail,
        bool HasAssistantTail,
        string ToolSignature,
        bool ShowTimestamps,
        bool ShowToolCalls,
        bool ShowReasoning,
        bool ExpandReasoningWhileStreaming);

    /// <summary>
    /// A finalized tail disappears from the payload before it reappears as a run-log entry. End its
    /// VM's streaming state first so detached transcript items unsubscribe, then evict the cache
    /// entry; otherwise every later update would keep notifying obsolete item trees.
    /// </summary>
    private void EvictMissingTail(string key, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)
            || !_synthesized.Remove(key, out var message))
        {
            return;
        }

        EndStreaming(message);
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
    /// Transcript items subscribe directly to a synthetic VM while it streams. Before replacing the
    /// tree, send the terminal edge so the old items detach; Synthesize restores the VM to streaming
    /// before the replacement items are built.
    /// </summary>
    private void DetachStreamingItemsBeforeRebuild()
    {
        foreach (var message in _synthesized.Values)
            EndStreaming(message);
    }

    /// <summary>
    /// Pushes the agent's still-growing text into the messages the live items are already bound to.
    /// Both <see cref="AssistantMessageItem"/> and <see cref="ReasoningItem"/> subscribe to their
    /// source while it streams, so this updates the rendered bubble without touching the tree.
    /// </summary>
    private void UpdateStreamingTail(SubagentToolCallItem run)
    {
        PushText("live:reasoning", run.ReasoningText);
        PushText("live:assistant", run.TranscriptText);

        void PushText(string key, string? text)
        {
            if (text is null
                || !_synthesized.TryGetValue(key, out var message)
                || string.Equals(message.Message.Content, text, StringComparison.Ordinal))
            {
                return;
            }

            message.Message.Content = text;
            message.NotifyContentChanged();
        }
    }

    private IEnumerable<ChatMessageViewModel> BuildRunMessages(
        SubagentToolCallItem run,
        List<ChatMessageViewModel> tools)
    {
        var ordered = new List<(DateTimeOffset At, ChatMessageViewModel Message)>();

        // The instruction always opens the run, whatever the surrounding clocks say.
        if (!string.IsNullOrWhiteSpace(run.Prompt))
            ordered.Add((DateTimeOffset.MinValue, Synthesize(run, "prompt", "user", run.Prompt!, run.StartedAt)));

        var previous = run.StartedAt ?? DateTimeOffset.MinValue;
        var entries = run.RunEntries;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var at = entry.Timestamp == default ? previous : entry.Timestamp;
            previous = at;
            var role = entry.Kind == SubagentRunEntryKind.Reasoning ? "reasoning" : "assistant";
            ordered.Add((at, Synthesize(run, $"entry:{i}", role, entry.Text, at)));
        }

        foreach (var tool in tools)
            ordered.Add((tool.Message.ToolStartedAt ?? tool.Message.Timestamp, tool));

        // Whatever is still streaming trails the run exactly like a live reply trails a chat.
        if (!string.IsNullOrWhiteSpace(run.ReasoningText))
            ordered.Add((DateTimeOffset.MaxValue, Synthesize(run, "live:reasoning", "reasoning", run.ReasoningText!, null, run.IsInProgress)));
        if (!string.IsNullOrWhiteSpace(run.TranscriptText))
            ordered.Add((DateTimeOffset.MaxValue, Synthesize(run, "live:assistant", "assistant", run.TranscriptText!, null, run.IsInProgress)));

        return ordered.OrderBy(static entry => entry.At).Select(static entry => entry.Message);
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
    /// place: the live items stay subscribed to the very message this returns. The id is derived
    /// from the run and the key so it is identical on every rebuild — transcript stable ids hang off
    /// it, and reusing them is what lets turns, scroll position and disclosure state survive.
    /// </summary>
    private ChatMessageViewModel Synthesize(
        SubagentToolCallItem run,
        string key,
        string role,
        string content,
        DateTimeOffset? timestamp,
        bool isStreaming = false)
    {
        var id = DeterministicId($"{run.StableId}|{key}");
        if (_synthesized.TryGetValue(key, out var existing))
        {
            var message = existing.Message;
            message.Id = id;
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

        var created = new ChatMessageViewModel(new ChatMessage
        {
            Id = id,
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
            foreach (var item in turn.Items)
                if (TryGetExpansion(item, out var isExpanded))
                    expansion[item.StableId] = isExpanded;

        return expansion;
    }

    private void RestoreExpansion(Dictionary<string, bool> expansion)
    {
        if (expansion.Count == 0)
            return;

        foreach (var turn in Turns)
            foreach (var item in turn.Items)
                if (expansion.TryGetValue(item.StableId, out var isExpanded))
                    SetExpansion(item, isExpanded);
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
        }
    }
}
