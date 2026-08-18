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
    private readonly TranscriptBuilder _builder;

    public SubagentRunTranscript(DataStore dataStore, Action<FileChangeItem> showDiffAction)
    {
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
    /// Rebuilds the run from scratch. Runs are small (an instruction, a handful of steps and its
    /// replies), so a full rebuild is cheaper than incremental bookkeeping and is always consistent
    /// with the run log. Disclosure state is carried across by stable id so a rebuild triggered by
    /// the agent's next token never folds away a card the reader just opened.
    /// </summary>
    public void Rebuild(SubagentToolCallItem run, IReadOnlyList<ChatMessageViewModel> chatMessages)
    {
        var expansion = CaptureExpansion();
        Turns = _builder.Rebuild(BuildRunMessages(run, chatMessages));
        RestoreExpansion(expansion);
    }

    /// <summary>Drops the built turns and releases the builder's listeners.</summary>
    public void Clear()
    {
        _builder.ResetState();
        Turns = [];
    }

    private IEnumerable<ChatMessageViewModel> BuildRunMessages(
        SubagentToolCallItem run,
        IReadOnlyList<ChatMessageViewModel> chatMessages)
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

        foreach (var tool in CollectRunToolMessages(chatMessages, run.ToolCallId))
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
    /// Wraps run-log text in a message the transcript builder can consume. The id is derived from
    /// the run and the entry's key so it is identical on every rebuild — transcript stable ids hang
    /// off it, and reusing them is what lets turns, scroll position and disclosure state survive.
    /// </summary>
    private static ChatMessageViewModel Synthesize(
        SubagentToolCallItem run,
        string key,
        string role,
        string content,
        DateTimeOffset? timestamp,
        bool isStreaming = false)
        => new(new ChatMessage
        {
            Id = DeterministicId($"{run.StableId}|{key}"),
            Role = role,
            Content = content,
            Author = run.DisplayName,
            Timestamp = timestamp ?? DateTimeOffset.Now,
            IsStreaming = isStreaming,
        });

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
