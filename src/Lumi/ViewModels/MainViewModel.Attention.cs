using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Microsoft.Data.Sqlite;

namespace Lumi.ViewModels;

public partial class MainViewModel
{
    public AttentionInboxViewModel AttentionVM => _chatSessionStore.Attention;
    public ObservableCollection<AttentionRequestViewModel> NeedsYouRequests { get; } = [];
    public bool HasNeedsYouChats => NeedsYouRequests.Count > 0;
    public bool HasChatInbox => HasNeedsYouChats || HasUnreadChats;
    public string NeedsYouSummaryText
    {
        get
        {
            var count = NeedsYouRequests.Select(entry => entry.ChatId).Distinct().Count();
            return count == 1 ? Loc.Get("NeedsYou_CountOne") : Loc.Get("NeedsYou_Count", count);
        }
    }
    public string UnreadCapsuleText => Loc.Get("NeedsYou_Unread", UnreadChatCount);
    [ObservableProperty] private bool _isNeedsYouPanelOpen;

    private void RefreshNeedsYouState()
    {
        if (_isDisposed)
            return;
        var visibleChat = SelectedNavIndex == 0 ? ActiveChatId : null;
        var desired = AttentionVM.Requests.Where(entry => entry.ChatId != visibleChat).ToList();
        SynchronizeCollection(NeedsYouRequests, desired);
        OnPropertyChanged(nameof(HasNeedsYouChats));
        OnPropertyChanged(nameof(HasChatInbox));
        OnPropertyChanged(nameof(NeedsYouSummaryText));
        if (!HasNeedsYouChats)
            IsNeedsYouPanelOpen = false;
    }

    [RelayCommand]
    private void ToggleNeedsYouPanel()
    {
        AttentionVM.Refresh();
        IsNeedsYouPanelOpen = HasNeedsYouChats && !IsNeedsYouPanelOpen;
        if (IsNeedsYouPanelOpen)
            IsUnreadPanelOpen = false;
    }

    [RelayCommand]
    private void OpenNeedsYouFromRail()
    {
        SelectedNavIndex = 0;
        AttentionVM.Refresh();
        if (!HasNeedsYouChats)
            return;
        if (IsSidebarCollapsed)
            IsSidebarCollapsed = false;
        IsNeedsYouPanelOpen = HasNeedsYouChats;
        IsUnreadPanelOpen = false;
    }

    [RelayCommand]
    private async Task OpenPendingInput(AttentionRequestViewModel? request)
    {
        if (request is null || !AttentionVM.Requests.Contains(request))
            return;
        IsNeedsYouPanelOpen = false;
        IsUnreadPanelOpen = false;
        try
        {
            if (await RevealChatAsync(request.Chat) && AttentionVM.Requests.Contains(request))
                request.Source.Owner.RevealPendingInput(request.ItemId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        {
            Trace.TraceWarning("[NeedsYou] Could not open request in chat {0}: {1}", request.ChatId, error);
            ChatVM.StatusText = Loc.Get("NeedsYou_OpenFailed");
        }
    }
}
