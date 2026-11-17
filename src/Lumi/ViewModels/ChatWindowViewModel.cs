using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.ViewModels;

public sealed partial class ChatWindowViewModel : ObservableObject, IDisposable
{
    private bool _isDisposed;
    private Chat? _renamingChat;
    private Chat? _pendingDeleteChat;

    public ChatWindowViewModel(ChatViewModel chatVM, MainViewModel? chatActions = null)
    {
        ChatVM = chatVM;
        ChatActions = chatActions;
        ChatVM.PropertyChanged += OnChatViewModelPropertyChanged;
    }

    public ChatViewModel ChatVM { get; }
    internal MainViewModel? ChatActions { get; set; }
    internal Action? ShowMainWindow { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenDialog))]
    private bool _isRenaming;
    [ObservableProperty] private string _renamingTitle = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenDialog))]
    private bool _isWorktreeDeleteDialogOpen;

    public bool HasOpenDialog => IsRenaming || IsWorktreeDeleteDialogOpen;

    [RelayCommand]
    private void StartRenameChat(Chat? chat)
    {
        if (chat is null)
            return;
        _renamingChat = chat;
        RenamingTitle = chat.Title;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRenameChat()
    {
        if (_renamingChat is { } chat && ChatActions is { } actions)
            actions.RenameChat(chat, RenamingTitle);
        CancelRenameChat();
    }

    [RelayCommand]
    private void CancelRenameChat()
    {
        IsRenaming = false;
        _renamingChat = null;
        RenamingTitle = "";
    }

    [RelayCommand]
    private async Task DeleteChat(Chat? chat)
    {
        if (chat is null || ChatActions is not { } actions || actions.IsChatFirstTurnReserved(chat.Id))
            return;

        if (actions.NeedsWorktreeDeleteConfirmation(chat))
        {
            _pendingDeleteChat = chat;
            IsWorktreeDeleteDialogOpen = true;
        }
        else
            await actions.DeleteChatKeepingWorktreeAsync(chat);
    }

    [RelayCommand]
    private async Task ConfirmDeleteWithWorktree()
    {
        if (_pendingDeleteChat is not { } chat || ChatActions is not { } actions
            || actions.IsChatFirstTurnReserved(chat.Id))
            return;
        CancelDeleteWorktreeDialog();
        await actions.DeleteChatWithWorktreeAsync(chat);
    }

    [RelayCommand]
    private async Task ConfirmDeleteWithoutWorktree()
    {
        if (_pendingDeleteChat is not { } chat || ChatActions is not { } actions
            || actions.IsChatFirstTurnReserved(chat.Id))
            return;
        CancelDeleteWorktreeDialog();
        await actions.DeleteChatKeepingWorktreeAsync(chat);
    }

    [RelayCommand]
    private void CancelDeleteWorktreeDialog()
    {
        _pendingDeleteChat = null;
        IsWorktreeDeleteDialogOpen = false;
    }

    [RelayCommand]
    private async Task DuplicateChat(Chat? chat)
    {
        ShowMainWindow?.Invoke();
        if (ChatActions is { } actions)
            await actions.DuplicateChatCommand.ExecuteAsync(chat);
    }

    [RelayCommand]
    private void ManageTags()
    {
        ShowMainWindow?.Invoke();
        ChatActions?.ChatTagsVM.OpenManagerCommand.Execute(null);
    }

    /// <summary>Opens a "Fix with Lumi" chat; a detached window hands it to the main window.</summary>
    internal Func<McpRepairRequest, Task>? McpRepairHandler { get; set; }

    internal Task StartMcpRepairChatAsync(McpRepairRequest request)
        => McpRepairHandler?.Invoke(request)
           ?? Task.FromException(new InvalidOperationException(Loc.Connector_RepairStartFailed));

    public string WindowTitle
    {
        get
        {
            var title = ChatVM.CurrentChatTitle;
            return string.IsNullOrWhiteSpace(title) ? Loc.Sidebar_NewChat : title;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        McpRepairHandler = null;
        ShowMainWindow = null;
        ChatActions = null;
        ChatVM.PropertyChanged -= OnChatViewModelPropertyChanged;
    }

    private void OnChatViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatViewModel.CurrentChatTitle) or nameof(ChatViewModel.CurrentChat))
            OnPropertyChanged(nameof(WindowTitle));
    }
}
