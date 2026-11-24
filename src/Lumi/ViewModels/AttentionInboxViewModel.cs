using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

internal sealed record PendingChatInput(
    ChatViewModel Owner, Chat Chat, string RequestId, ChatMessage? Message, PermissionRequestViewModel? Approval)
{
    public string ItemId => Approval is { } approval
        ? $"approval:{approval.SessionId}:{RequestId}"
        : Message?.ToolName == "ask_input" ? InputRequestItem.StableIdFor(Message.Id)
        : QuestionItem.StableIdFor(RequestId);
    public DateTimeOffset RequestedAt => Approval?.RequestedAt ?? Message!.Timestamp;
}

/// <summary>A live request's navigation metadata. Its only answer surface is the owning chat.</summary>
public sealed class AttentionRequestViewModel : ObservableObject, IDisposable
{
    private readonly DataStore _store;
    internal PendingChatInput Source { get; }
    public Chat Chat => Source.Chat;
    public Guid ChatId => Chat.Id;
    public string RequestId => Source.RequestId;
    public string ItemId => Source.ItemId;
    public DateTimeOffset RequestedAt => Source.RequestedAt;
    public string ChatTitle => string.IsNullOrWhiteSpace(Chat.Title) ? Loc.Library_UntitledChat : Chat.Title;
    public string ProjectName => _store.Data.Projects.Find(project => project.Id == Chat.ProjectId)?.Name ?? Loc.Unread_NoProject;
    public string Kind => Loc.Get(Source.Approval is not null ? "NeedsYou_Approval"
        : Source.Message?.ToolName == "ask_input" ? "NeedsYou_Form" : "NeedsYou_Question");
    public string Ask => LaunchpadPlanner.CleanPreview(Source.Message?.QuestionText
        ?? (Source.Approval!.Title + (Source.Approval.Subject is { Length: > 0 } subject ? " · " + subject : "")));
    public string Meta => ProjectName + " · " + Kind;
    public string TimeLabel => LibraryViewModel.FormatRelativeTime(RequestedAt);

    internal AttentionRequestViewModel(PendingChatInput source, DataStore store)
    {
        Source = source;
        _store = store;
        Chat.PropertyChanged += OnChatChanged;
    }

    internal void RefreshLabels()
    {
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(Meta));
        OnPropertyChanged(nameof(TimeLabel));
    }

    private void OnChatChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Chat.Title))
            OnPropertyChanged(nameof(ChatTitle));
    }

    public void Dispose() => Chat.PropertyChanged -= OnChatChanged;
}

/// <summary>Shared, lightweight index of live requests, never another place to answer them.</summary>
public sealed class AttentionInboxViewModel : IDisposable
{
    private readonly DataStore _store;
    private readonly ChatSurfaceRegistry _surfaces;
    private readonly ChatEventHub _events;
    private bool _disposed;

    public ObservableCollection<AttentionRequestViewModel> Requests { get; } = [];
    public event Action? Changed;

    internal AttentionInboxViewModel(DataStore store, ChatSurfaceRegistry surfaces, ChatEventHub events)
    {
        _store = store;
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
        var pending = _surfaces.SnapshotSurfaces().SelectMany(surface => surface.GetPendingInputs())
            .OrderBy(request => request.RequestedAt).ThenBy(request => request.Chat.Id)
            .ThenBy(request => request.ItemId, StringComparer.Ordinal).ToList();
        foreach (var entry in Requests.ToArray())
            if (!pending.Any(request => request.Chat.Id == entry.ChatId && request.ItemId == entry.ItemId))
            {
                Requests.Remove(entry);
                entry.Dispose();
            }
        for (var index = 0; index < pending.Count; index++)
        {
            var request = pending[index];
            var entry = Requests.FirstOrDefault(candidate => candidate.ChatId == request.Chat.Id && candidate.ItemId == request.ItemId);
            if (entry is null)
                Requests.Insert(index, new AttentionRequestViewModel(request, _store));
            else
            {
                if (Requests.IndexOf(entry) != index)
                    Requests.Move(Requests.IndexOf(entry), index);
                entry.RefreshLabels();
            }
        }
        Changed?.Invoke();
    }

    internal List<(Chat Chat, LaunchpadChatState State)> SelectChats(int limit, out int total)
    {
        var chats = Requests.DistinctBy(entry => entry.ChatId).Select(entry => (entry.Chat, LaunchpadChatState.Waiting)).ToList();
        total = chats.Count;
        return chats.Take(limit).ToList();
    }

    private void OnAttentionRaised(ChatAttention attention)
    {
        if (attention.Kind != ChatAttentionKind.Reply)
            Refresh();
    }

    private void OnAttentionResolved(Guid chatId, string requestId)
    {
        // Approval completion publishes before it removes the native request, so remove by identity here.
        foreach (var entry in Requests.Where(entry => entry.ChatId == chatId && entry.RequestId == requestId).ToArray())
        {
            Requests.Remove(entry);
            entry.Dispose();
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _events.AttentionRaised -= OnAttentionRaised;
        _events.AttentionResolved -= OnAttentionResolved;
        foreach (var entry in Requests)
            entry.Dispose();
        Requests.Clear();
    }
}
