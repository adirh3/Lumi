using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.ViewModels;

/// <summary>
/// One chat listed in the sidebar's unread inbox. Snapshotted at refresh time so the row can show
/// the chat's project and relative activity without the list template reaching back into the store.
/// </summary>
public sealed class UnreadChatEntry
{
    public required Chat Chat { get; init; }
    public required string Title { get; init; }

    /// <summary>Project name, or null for chats that live outside any project.</summary>
    public string? ProjectName { get; init; }

    public bool HasProject => ProjectName is not null;

    /// <summary>True when this chat is hidden by the sidebar's active project filter.</summary>
    public bool IsOutsideActiveFilter { get; init; }

    public required string TimeLabel { get; init; }

    /// <summary>
    /// Hover text. For a chat the active filter is hiding it spells out *why* the chat is not in the
    /// list below, which is the question this row exists to answer.
    /// </summary>
    public string TooltipText => IsOutsideActiveFilter
        ? $"{Title}\n{string.Format(Loc.Culture, Loc.Unread_RowHiddenByFilter, ProjectName ?? Loc.Unread_NoProject)}"
        : Title;
}

/// <summary>
/// Aggregates <see cref="Chat.HasUnreadMessages"/> across every chat into the sidebar's unread
/// inbox, the nav badge, and the project switcher's per-project unread counts.
///
/// Chats run concurrently across projects, so a reply can land in a project the sidebar is not
/// currently filtered to — or while the user is on another page entirely. The per-row dot alone
/// cannot surface that, so these aggregates give a filter-independent "you have unread" signal
/// plus a one-click way to jump to the chat (switching the project filter so it is revealed).
/// </summary>
public partial class MainViewModel
{
    /// <summary>Cap on rows rendered in the unread drawer; the rest are summarized by a "+N more" line.</summary>
    private const int UnreadListLimit = 8;

    public ObservableCollection<UnreadChatEntry> UnreadChats { get; } = [];

    [ObservableProperty] private int _unreadChatCount;
    [ObservableProperty] private bool _hasUnreadChats;

    /// <summary>Unread chats the active project filter is currently hiding from the sidebar list.</summary>
    [ObservableProperty] private int _unreadOutsideFilterCount;
    [ObservableProperty] private bool _hasUnreadOutsideFilter;

    /// <summary>Context-aware headline for the unread pill (e.g. "3 unread · 2 in other projects").</summary>
    [ObservableProperty] private string _unreadSummaryText = "";

    /// <summary>Hover text for the unread pill, spelling out what the headline is counting.</summary>
    [ObservableProperty] private string _unreadTooltipText = "";

    /// <summary>Compact count for the nav badge, clamped to "9+".</summary>
    [ObservableProperty] private string _unreadBadgeText = "";

    /// <summary>"+N more" footnote when more chats are unread than the drawer lists.</summary>
    [ObservableProperty] private string _unreadOverflowText = "";
    [ObservableProperty] private bool _hasUnreadOverflow;

    [ObservableProperty] private bool _isUnreadPanelOpen;

    /// <summary>
    /// Set while a chat is being revealed from the unread inbox. Switching
    /// <see cref="SelectedProjectFilter"/> normally auto-opens that project's most recent chat,
    /// which would race with (and win over) the chat the user actually clicked.
    /// </summary>
    private bool _isRevealingChat;

    partial void OnHasUnreadChatsChanged(bool value)
    {
        if (!value)
            IsUnreadPanelOpen = false;
    }

    /// <summary>Number of unread chats in a project (null = chats with no project).</summary>
    public int GetProjectUnreadCount(Guid? projectId)
        => _dataStore.Data.Chats.Count(chat => chat.ProjectId == projectId && chat.HasUnreadMessages);

    /// <summary>Recomputes every unread aggregate. Safe to call from any thread.</summary>
    public void RefreshUnreadState()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshUnreadState);
            return;
        }

        if (_isDisposed)
            return;

        var unread = _dataStore.Data.Chats
            .Where(chat => chat.HasUnreadMessages)
            .OrderByDescending(chat => chat.UpdatedAt)
            .ToList();

        var filter = SelectedProjectFilter;
        var outsideCount = filter.HasValue
            ? unread.Count(chat => chat.ProjectId != filter.Value)
            : 0;

        UnreadChats.Clear();
        foreach (var chat in unread.Take(UnreadListLimit))
        {
            UnreadChats.Add(new UnreadChatEntry
            {
                Chat = chat,
                Title = string.IsNullOrWhiteSpace(chat.Title) ? Loc.Library_UntitledChat : chat.Title,
                ProjectName = GetProjectName(chat.ProjectId),
                IsOutsideActiveFilter = filter.HasValue && chat.ProjectId != filter.Value,
                TimeLabel = LibraryViewModel.FormatRelativeTime(chat.UpdatedAt),
            });
        }

        var overflow = unread.Count - UnreadChats.Count;
        HasUnreadOverflow = overflow > 0;
        UnreadOverflowText = overflow > 0
            ? string.Format(CultureInfo.CurrentCulture, Loc.Unread_MoreChats, overflow)
            : "";

        UnreadChatCount = unread.Count;
        UnreadOutsideFilterCount = outsideCount;
        HasUnreadOutsideFilter = outsideCount > 0;
        UnreadBadgeText = unread.Count > 9 ? Loc.Unread_BadgeOverflow : unread.Count.ToString(Loc.Culture);
        UnreadSummaryText = BuildUnreadSummary(unread.Count, outsideCount);
        UnreadTooltipText = outsideCount > 0
            ? string.Format(CultureInfo.CurrentCulture, Loc.Unread_OpenTooltipElsewhere, outsideCount)
            : Loc.Unread_OpenTooltip;
        HasUnreadChats = unread.Count > 0;

        UnreadStateChanged?.Invoke();
    }

    /// <summary>Fired after any unread aggregate changes, so views can refresh derived chrome.</summary>
    public event Action? UnreadStateChanged;

    /// <summary>
    /// Headline for the unread pill. It names where the unread chats are relative to the active
    /// filter, because "you have unread" is only actionable if you know they are somewhere you
    /// cannot currently see. "Other projects" is spelled out rather than abbreviated — a bare
    /// "elsewhere" reads as jargon and leaves the user guessing what it is counting.
    /// </summary>
    internal static string BuildUnreadSummary(int total, int outsideFilter)
    {
        if (total <= 0)
            return "";

        if (outsideFilter <= 0)
        {
            return total == 1
                ? Loc.Unread_SummaryOne
                : string.Format(CultureInfo.CurrentCulture, Loc.Unread_SummaryMany, total);
        }

        if (outsideFilter == total)
        {
            return total == 1
                ? Loc.Unread_SummaryElsewhereOne
                : string.Format(CultureInfo.CurrentCulture, Loc.Unread_SummaryElsewhereMany, total);
        }

        return string.Format(CultureInfo.CurrentCulture, Loc.Unread_SummaryMixed, total, outsideFilter);
    }

    [RelayCommand]
    private void ToggleUnreadPanel()
    {
        if (!HasUnreadChats)
        {
            IsUnreadPanelOpen = false;
            return;
        }

        if (!IsUnreadPanelOpen)
            RefreshUnreadState();

        IsUnreadPanelOpen = !IsUnreadPanelOpen;
    }

    [RelayCommand]
    private void CloseUnreadPanel() => IsUnreadPanelOpen = false;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenUnreadChat(UnreadChatEntry? entry)
    {
        if (entry is null)
            return;

        IsUnreadPanelOpen = false;
        await RevealChatAsync(entry.Chat);
    }

    /// <summary>Jumps to the most recently updated unread chat, if any.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenNextUnreadChat()
    {
        var next = _dataStore.Data.Chats
            .Where(chat => chat.HasUnreadMessages)
            .OrderByDescending(chat => chat.UpdatedAt)
            .FirstOrDefault();

        if (next is null)
            return;

        IsUnreadPanelOpen = false;
        await RevealChatAsync(next);
    }

    [RelayCommand]
    private void MarkAllChatsRead()
    {
        foreach (var chat in _dataStore.Data.Chats)
            chat.HasUnreadMessages = false;

        IsUnreadPanelOpen = false;
        RefreshUnreadState();
    }

    /// <summary>
    /// Opens <paramref name="chat"/> and makes it visible in the sidebar by moving the project
    /// filter to the chat's own project first. Without the filter move the chat would open while
    /// its sidebar row stayed filtered out, so the list would appear to have no selection.
    /// </summary>
    public async Task<bool> RevealChatAsync(Chat chat)
    {
        if (SelectedProjectFilter != chat.ProjectId)
        {
            _isRevealingChat = true;
            try
            {
                SelectedProjectFilter = chat.ProjectId;
            }
            finally
            {
                _isRevealingChat = false;
            }
        }

        try
        {
            return await LoadChatAndShowAsync(chat);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
