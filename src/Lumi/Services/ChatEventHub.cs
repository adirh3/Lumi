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

public enum ChatAttentionKind
{
    /// <summary>The main assistant finished a reply.</summary>
    Reply,

    /// <summary>ask_question waits for an answer.</summary>
    Question,

    /// <summary>ask_input waits for a form or a pick.</summary>
    Input,

    /// <summary>A tool call waits for approval.</summary>
    Approval
}

/// <summary>
/// Something in a chat that needs the user — exactly the moments the desktop notifies about. Phone
/// notifications are built from these.
/// </summary>
public sealed record ChatAttention(
    Guid ChatId,
    ChatAttentionKind Kind,
    string Text,
    string? RequestId = null);

public sealed class ChatEventHub
{
    public event Action<ChatLifecycleEvent>? EventPublished;

    /// <summary>A chat needs the user. Raised on the UI thread.</summary>
    public event Action<ChatAttention>? AttentionRaised;

    /// <summary>A pending request (question, input or approval) of a chat was settled. Raised on the UI thread.</summary>
    public event Action<Guid, string>? AttentionResolved;

    /// <summary>
    /// A chat's follow-up suggestions for its latest reply were written, or none are coming (empty).
    /// Raised on the UI thread.
    /// </summary>
    public event Action<Guid, IReadOnlyList<string>>? SuggestionsSettled;

    public void PublishAttention(ChatAttention attention)
    {
        ArgumentNullException.ThrowIfNull(attention);
        Raise(AttentionRaised, handler => handler(attention));
    }

    public void ResolveAttention(Guid chatId, string requestId) =>
        Raise(AttentionResolved, handler => handler(chatId, requestId));

    public void PublishSuggestions(Guid chatId, IReadOnlyList<string> suggestions) =>
        Raise(SuggestionsSettled, handler => handler(chatId, suggestions));

    private static void Raise<T>(T? handlers, Action<T> invoke) where T : Delegate
    {
        if (handlers is null)
            return;

        foreach (T handler in handlers.GetInvocationList())
        {
            try
            {
                invoke(handler);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"[ChatEvents] Subscriber failed: {ex}");
            }
        }
    }

    public void Publish(ChatLifecycleEvent chatEvent)
    {
        ArgumentNullException.ThrowIfNull(chatEvent);
        Raise(EventPublished, handler => handler(chatEvent));
    }
}
