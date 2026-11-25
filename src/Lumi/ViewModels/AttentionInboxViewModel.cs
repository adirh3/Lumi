using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

/// <summary>A live request's navigation metadata. Its only answer surface is the owning chat.</summary>
public sealed record PendingChatRequest(
    Chat Chat, string RequestId, string ItemId, DateTimeOffset RequestedAt, string Ask, ChatAttentionKind Kind);

/// <summary>Shared, lightweight index of live requests, never another place to answer them.</summary>
public sealed class AttentionInboxViewModel : IDisposable
{
    private readonly ChatSurfaceRegistry _surfaces;
    private readonly ChatEventHub _events;
    private bool _disposed;

    public IReadOnlyList<PendingChatRequest> Requests { get; private set; } = [];
    public event Action? Changed;

    internal AttentionInboxViewModel(ChatSurfaceRegistry surfaces, ChatEventHub events)
    {
        _surfaces = surfaces;
        _events = events;
        events.AttentionRaised += OnAttentionRaised;
        events.AttentionResolved += OnAttentionResolved;
        Refresh();
    }

    internal void Refresh()
    {
        if (_disposed)
            return;
        Requests = _surfaces.SnapshotSurfaces().SelectMany(surface => surface.GetPendingInputs())
            .OrderBy(request => request.RequestedAt).ThenBy(request => request.Chat.Id)
            .ThenBy(request => request.ItemId, StringComparer.Ordinal).ToArray();
        Changed?.Invoke();
    }

    private void OnAttentionRaised(ChatAttention attention)
    {
        if (attention.Kind != ChatAttentionKind.Reply)
            Refresh();
    }

    private void OnAttentionResolved(Guid chatId, string requestId)
    {
        if (_disposed)
            return;
        // Approval completion publishes before it removes the native request, so remove by identity here.
        Requests = Requests.Where(entry => entry.Chat.Id != chatId || entry.RequestId != requestId).ToArray();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _events.AttentionRaised -= OnAttentionRaised;
        _events.AttentionResolved -= OnAttentionResolved;
        Requests = [];
    }
}
