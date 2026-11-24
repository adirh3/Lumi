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
            var count = _attentionInbox.Requests.Select(entry => entry.Chat.Id).Distinct().Count();
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

        var pendingChats = _attentionInbox.Requests.DistinctBy(entry => entry.Chat.Id).ToList();
        HashSet<string> ambiguousTitles = pendingChats.Count == 0 ? [] : _dataStore.Data.Chats
            .GroupBy(chat => chat.Title, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(chat => chat.ProjectId).Distinct().Skip(1).Any())
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desired = new List<UnreadChatEntry>();
        foreach (var request in pendingChats)
        {
            var title = string.IsNullOrWhiteSpace(request.Chat.Title) ? Loc.Library_UntitledChat : request.Chat.Title;
            var project = GetProjectName(request.Chat.ProjectId);
            var time = LibraryViewModel.FormatRelativeTime(request.RequestedAt);
            var isOutsideFilter = SelectedProjectFilter.HasValue && request.Chat.ProjectId != SelectedProjectFilter.Value;
            var ambiguous = ambiguousTitles.Contains(request.Chat.Title);
            var previous = NotificationChats.FirstOrDefault(entry => entry.PendingRequest == request
                && entry.Title == title && entry.ProjectName == project && entry.TimeLabel == time
                && entry.IsOutsideActiveFilter == isOutsideFilter && entry.HasAmbiguousTitle == ambiguous);
            desired.Add(previous ?? new UnreadChatEntry
            {
                Chat = request.Chat, Title = title, ProjectName = project, HasAmbiguousTitle = ambiguous,
                TimeLabel = time, PendingRequest = request, IsOutsideActiveFilter = isOutsideFilter
            });
        }
        desired.AddRange(UnreadChats);
        SynchronizeCollection(NotificationChats, desired);
        OnPropertyChanged(nameof(HasPendingNotifications));
        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(NotificationSummaryText));
        OnPropertyChanged(nameof(NotificationTooltipText));
        if (!HasNotifications)
            IsUnreadPanelOpen = false;
    }
}
