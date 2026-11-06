using System.Diagnostics;
using GitHub.Copilot;

namespace Lumi.Services;

/// <summary>What the runtime did with a model selection: applied it at once, or queued it.</summary>
/// <param name="QueueIds">The runtime's queue items holding a queued switch; empty when it applied.</param>
internal readonly record struct SessionModelSwitchResult(bool Deferred, IReadOnlyList<string> QueueIds);

/// <summary>
/// Switches a live session's model and reports what the runtime did with the switch. The runtime changes
/// models only between turns: one picked while a turn runs waits in its queue, as a cancellable item, until
/// the turn ends, and an abort drops it with the turn. A change of reasoning effort alone applies from the
/// running turn's next step.
/// </summary>
internal static class SessionModelSwitch
{
    public static async Task<SessionModelSwitchResult> SwitchAsync(
        CopilotSession session,
        string modelId,
        SetModelOptions options,
        CancellationToken cancellationToken = default)
    {
#pragma warning disable GHCP001
        if (options.ResetAutoTier)
        {
            // Only SetModelAsync sends the explicit null that returns Auto to its default routing, and it
            // drops the runtime's answer, so what it queued is read from the queue.
            var before = await QueuedCommandIdsAsync(session, cancellationToken);
            await session.SetModelAsync(modelId, options, cancellationToken);
            var queued = (await QueuedCommandIdsAsync(session, cancellationToken)).Except(before).ToList();
            return new SessionModelSwitchResult(queued.Count > 0, queued);
        }

        var result = await session.Rpc.Model.SwitchToAsync(
            modelId: modelId,
            autoTier: options.AutoTier,
            reasoningEffort: options.ReasoningEffort,
            reasoningSummary: options.ReasoningSummary,
            modelCapabilities: options.ModelCapabilities,
            contextTier: options.ContextTier,
            // A person's pick: it drains after (and so wins over) a switch already waiting.
            deferIfModelChangeQueued: true,
            cancellationToken: cancellationToken);
        return result.Deferred == true
            ? new SessionModelSwitchResult(true, result.QueueId is { Length: > 0 } id ? [id] : [])
            : new SessionModelSwitchResult(false, []);
#pragma warning restore GHCP001
    }

    /// <summary>Takes queued switches back out of the runtime's queue.</summary>
    public static async Task RemoveQueuedAsync(CopilotSession session, IEnumerable<string> queueIds)
    {
        foreach (var queueId in queueIds)
        {
            try
            {
#pragma warning disable GHCP001
                await session.Rpc.Queue.RemoveAtAsync(queueId);
#pragma warning restore GHCP001
            }
            catch (Exception ex)
            {
                // One that drained, or that an abort cleared, is already gone.
                Trace.TraceInformation("[Chat] Queued model switch {0} was not removed: {1}", queueId, ex.Message);
            }
        }
    }

    private static async Task<HashSet<string>> QueuedCommandIdsAsync(CopilotSession session, CancellationToken cancellationToken)
    {
#pragma warning disable GHCP001
        var pending = await session.Rpc.Queue.PendingItemsAsync(cancellationToken);
#pragma warning restore GHCP001
        // Queued messages carry a message id; commands and model switches don't.
        return pending.Items.Where(item => item.MessageId is null).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
    }
}
