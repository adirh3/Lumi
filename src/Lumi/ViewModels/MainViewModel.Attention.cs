using System.Collections.ObjectModel;
using Lumi.Localization;

namespace Lumi.ViewModels;

public partial class MainViewModel
{
    private readonly AttentionInboxViewModel _attentionInbox;
    internal AttentionInboxViewModel AttentionInbox => _attentionInbox;
    public ObservableCollection<UnreadChatEntry> NotificationChats { get; } = [];
    public bool HasPendingNotifications => _attentionInbox.Requests.Count > 0;
    public bool HasNotifications => HasPendingNotifications || HasUnreadChats;
    public string NotificationSummaryText
    {
        get
        {
            var count = _attentionInbox.Requests.Select(entry => entry.ChatId).Distinct().Count();
            if (count == 0)
                return UnreadSummaryText;
            var pending = count == 1 ? Loc.Get("NeedsYou_CountOne") : Loc.Get("NeedsYou_Count", count);
            return HasUnreadChats ? pending + " · " + Loc.Get("NeedsYou_Unread", UnreadChatCount) : pending;
        }
    }
    public string NotificationTooltipText => HasPendingNotifications
        ? $"{Loc.Get("NeedsYou_Description")} ({Loc.AdaptKeyboardHint("Ctrl+Shift+I")})"
        : UnreadTooltipText;

    private void RefreshNotifications()
    {
        if (_isDisposed)
            return;

        var pendingChats = _attentionInbox.Requests.DistinctBy(entry => entry.ChatId).ToList();
        var desired = new List<UnreadChatEntry>();
        foreach (var request in pendingChats)
        {
            var project = GetProjectName(request.Chat.ProjectId);
            var previous = NotificationChats.FirstOrDefault(entry => ReferenceEquals(entry.PendingRequest, request)
                && entry.Title == request.ChatTitle && entry.ProjectName == project && entry.TimeLabel == request.TimeLabel);
            desired.Add(previous ?? new UnreadChatEntry
            {
                Chat = request.Chat, Title = request.ChatTitle, ProjectName = project,
                TimeLabel = request.TimeLabel, PendingRequest = request
            });
        }
        var pendingIds = pendingChats.Select(entry => entry.ChatId).ToHashSet();
        desired.AddRange(UnreadChats.Where(entry => !pendingIds.Contains(entry.Chat.Id)));
        SynchronizeCollection(NotificationChats, desired);
        OnPropertyChanged(nameof(HasPendingNotifications));
        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(NotificationSummaryText));
        OnPropertyChanged(nameof(NotificationTooltipText));
        if (!HasNotifications)
            IsUnreadPanelOpen = false;
    }
}
