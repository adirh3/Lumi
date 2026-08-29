using System;
using System.Diagnostics;
using Lumi.Models;

namespace Lumi.Services;

public sealed record ChatLifecycleEvent(
    Guid ChatId,
    string ChatTitle,
    string EventType,
    DateTimeOffset OccurredAt,
    string? Detail = null);

public sealed class ChatEventHub
{
    public event Action<ChatLifecycleEvent>? EventPublished;

    public void Publish(ChatLifecycleEvent chatEvent)
    {
        ArgumentNullException.ThrowIfNull(chatEvent);

        var handlers = EventPublished;
        if (handlers is null)
            return;

        foreach (Action<ChatLifecycleEvent> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(chatEvent);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"[ChatEvents] Subscriber failed: {ex}");
            }
        }
    }
}
