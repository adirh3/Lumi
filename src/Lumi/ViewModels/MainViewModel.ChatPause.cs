using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.ViewModels;

public partial class MainViewModel
{
    public bool CanPauseAllChats => _dataStore.Data.Chats.Any(
        chat => (chat.IsSessionActive || chat.IsRunning) && !chat.IsPaused);

    public bool CanResumeAllChats => _dataStore.Data.Chats.Any(chat => chat.IsPaused);

    public bool HasChatActivity => CanPauseAllChats || CanResumeAllChats;

    public string ChatActivitySummary => Loc.Get(
        "Chat_ActivitySummary",
        _dataStore.Data.Chats.Count(chat => (chat.IsSessionActive || chat.IsRunning) && !chat.IsPaused),
        _dataStore.Data.Chats.Count(chat => chat.IsPaused));

    [RelayCommand]
    private async Task ToggleChatPause(Chat? chat)
    {
        if ((chat ?? ChatVM.CurrentChat) is { } target)
            await _chatSessionStore.SetChatPausedAsync(target, !target.IsPaused);
    }

    [RelayCommand(CanExecute = nameof(CanPauseAllChats))]
    private async Task PauseAllChats()
    {
        await Task.WhenAll(_dataStore.Data.Chats
                     .Where(chat => (chat.IsSessionActive || chat.IsRunning) && !chat.IsPaused)
                     .ToArray()
                     .Select(chat => _chatSessionStore.SetChatPausedAsync(chat, paused: true)));
    }

    [RelayCommand(CanExecute = nameof(CanResumeAllChats))]
    private async Task ResumeAllChats()
    {
        await Task.WhenAll(_dataStore.Data.Chats.Where(chat => chat.IsPaused).ToArray()
            .Select(chat => _chatSessionStore.SetChatPausedAsync(chat, paused: false)));
    }

    private void RefreshChatPauseCommands()
    {
        OnPropertyChanged(nameof(CanPauseAllChats));
        OnPropertyChanged(nameof(CanResumeAllChats));
        OnPropertyChanged(nameof(HasChatActivity));
        OnPropertyChanged(nameof(ChatActivitySummary));
        PauseAllChatsCommand.NotifyCanExecuteChanged();
        ResumeAllChatsCommand.NotifyCanExecuteChanged();
    }
}
