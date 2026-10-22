using System;
using System.Collections.Generic;
using System.Linq;
using Lumi.Services;
using Lumi.Services.Storage;

namespace Lumi.ViewModels;

/// <summary>
/// What an older exchange's build reads of it. A run's card in the chat shows who ran, what it was
/// asked and how it ended; its steps show on its page, which reads them from storage when it opens.
/// So an exchange is built without the steps runs took — most of a long exchange — except those that
/// show outside the run too: files it changed or announced, skills, questions and links it brought
/// up, its intent and plan; the step its card names as its latest; and any step a kept one is
/// nested under. Everything else (the conversation, Lumi's own work) is read whole.
/// </summary>
internal static class HistoryExchangeRows
{
    // A step without a facet that still shows outside its run: a plan.
    private static readonly HashSet<string> SharedTools = new(StringComparer.Ordinal)
    {
        "update_todo", "manage_todo_list",
    };

    private static readonly HashSet<string> ToolsWithoutCards = new(TranscriptBuilder.ToolsWithoutStepCards, StringComparer.Ordinal);

    // Deeper than any real nesting; ends a walk through damaged links.
    private const int MaxDepth = 64;

    /// <summary>The positions of the messages, and of the later steps of its runs, the build reads.</summary>
    public static IReadOnlySet<long> Pick(ExchangeOutline outline)
    {
        var calls = new Dictionary<string, StoredMessageHeader>(StringComparer.Ordinal);
        foreach (var header in outline.Messages.Concat(outline.LaterRunSteps))
        {
            if (header.ToolCallId is { Length: > 0 } toolCallId)
                calls.TryAdd(toolCallId, header);
        }

        var earlier = new Dictionary<string, StoredToolCall>(StringComparer.Ordinal);
        foreach (var call in outline.EarlierToolCalls)
            earlier.TryAdd(call.ToolCallId, call);

        var picked = new HashSet<long>();
        var latestCards = new Dictionary<string, long>(StringComparer.Ordinal);
        void Consider(StoredMessageHeader header, string? run)
        {
            if (run is null)
            {
                picked.Add(header.Seq);
                return;
            }

            if (header.Kind is not null || header.ToolName is { } tool && SharedTools.Contains(tool))
                picked.Add(header.Seq);
            // Only a run of this exchange has its card here; one of an earlier exchange has it there.
            if (IsStepCard(header) && calls.ContainsKey(run) && (!latestCards.TryGetValue(run, out var latest) || header.Seq > latest))
                latestCards[run] = header.Seq;
        }

        foreach (var header in outline.Messages)
            Consider(header, header.Role == "tool" ? OwningRun(header) : null);
        // A later step's parent is a run of this exchange: its build continues that run's card with it.
        foreach (var header in outline.LaterRunSteps)
            Consider(header, header.Role == "tool" ? header.ParentToolCallId : null);
        picked.UnionWith(latestCards.Values);

        // A kept step nested under a left-out one keeps that one too, so it is filed where it belongs.
        foreach (var header in outline.Messages)
        {
            if (!picked.Contains(header.Seq))
                continue;

            var parent = header.ParentToolCallId;
            for (var depth = 0; parent is { Length: > 0 } && depth < MaxDepth && calls.TryGetValue(parent, out var ancestor); depth++)
            {
                picked.Add(ancestor.Seq);
                parent = ancestor.ParentToolCallId;
            }
        }

        return picked;

        // The run a step of this exchange works for, as a build finds it: through the calls built
        // before the step, or those of earlier exchanges it continues.
        string? OwningRun(StoredMessageHeader step)
        {
            var parent = step.ParentToolCallId;
            for (var depth = 0; parent is { Length: > 0 } && depth < MaxDepth; depth++)
            {
                if (calls.TryGetValue(parent, out var call) && call.Seq < step.Seq)
                {
                    if (ToolDisplayHelper.IsSubagentTool(call.ToolName))
                        return parent;
                    parent = call.ParentToolCallId;
                }
                else if (earlier.TryGetValue(parent, out var stored))
                {
                    if (ToolDisplayHelper.IsSubagentTool(stored.ToolName))
                        return parent;
                    parent = stored.ParentToolCallId;
                }
                else
                {
                    return null;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// The read messages that come right after messages the build left out (see
    /// <see cref="TranscriptBuilder.NoteOmittedMessage"/>).
    /// </summary>
    public static HashSet<long> OmittedBefore(ExchangeOutline outline, IReadOnlyList<Models.ChatMessage> read)
    {
        var picked = read.Select(static message => message.StorageSeq).ToHashSet();
        var result = new HashSet<long>();
        var omitted = false;
        foreach (var header in outline.Messages)
        {
            if (!picked.Contains(header.Seq))
            {
                omitted = true;
            }
            else if (omitted)
            {
                result.Add(header.Seq);
                omitted = false;
            }
        }

        return result;
    }

    // A step that shows as a step card of its run, so the run's card can name it as its latest.
    private static bool IsStepCard(StoredMessageHeader header)
        => header.ToolName is not { Length: > 0 } tool
           || !ToolsWithoutCards.Contains(tool) && !ToolDisplayHelper.IsSubagentTool(tool);
}
