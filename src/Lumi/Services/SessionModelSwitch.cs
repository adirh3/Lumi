using System.Diagnostics;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace Lumi.Services;

/// <summary>What the runtime did with a model selection: applied it at once, or queued it.</summary>
/// <param name="QueueIds">The runtime's queue items holding a queued switch; empty when it applied.</param>
internal readonly record struct SessionModelSwitchResult(bool Deferred, IReadOnlyList<string> QueueIds);

/// <summary>
/// Switches a live session's model and reports what the runtime did with the switch. The runtime changes
/// models only between turns: one picked while a turn runs waits in its queue, as a cancellable item, until
/// the turn ends, and an abort drops it with the turn. A change of reasoning effort alone applies from the
/// running turn's next step. A pick the session already runs is queued too while a turn runs; nothing waits
/// for it, so it is taken back out of the queue.
/// </summary>
internal static class SessionModelSwitch
{
    public static async Task<SessionModelSwitchResult> SwitchAsync(
        CopilotSession session,
        string modelId,
        SetModelOptions options,
        CancellationToken cancellationToken = default)
    {
        var result = await RequestSwitchAsync(session, modelId, options, cancellationToken);
#pragma warning disable GHCP001
        if (result.Deferred && IsRunning(await session.Rpc.Model.GetCurrentAsync(cancellationToken), modelId, options))
#pragma warning restore GHCP001
        {
            await RemoveQueuedAsync(session, result.QueueIds);
            return new SessionModelSwitchResult(false, []);
        }

        return result;
    }

    private static async Task<SessionModelSwitchResult> RequestSwitchAsync(
        CopilotSession session,
        string modelId,
        SetModelOptions options,
        CancellationToken cancellationToken)
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

    /// <summary>
    /// Whether the session already runs the pick: the model, and any effort and tier it names. An Auto pick
    /// always goes through the queue: a newer tier can wait behind the committed one, and a pick replaces it.
    /// </summary>
    private static bool IsRunning(CurrentModel live, string modelId, SetModelOptions options)
    {
#pragma warning disable GHCP001
        return !string.Equals(modelId, "auto", StringComparison.OrdinalIgnoreCase)
               && string.Equals(live.ModelId, modelId, StringComparison.OrdinalIgnoreCase)
               && (options.ReasoningEffort is null
                   || string.Equals(live.ReasoningEffort, options.ReasoningEffort, StringComparison.OrdinalIgnoreCase))
               && (options.ContextTier ?? ContextTier.Default) == (live.ContextTier ?? ContextTier.Default);
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
