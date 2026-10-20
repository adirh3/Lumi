using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.Localization;

namespace Lumi.ViewModels;

public sealed class ChatWindowViewModel : ObservableObject, IDisposable
{
    private bool _isDisposed;

    public ChatWindowViewModel(ChatViewModel chatVM)
    {
        ChatVM = chatVM;
        ChatVM.PropertyChanged += OnChatViewModelPropertyChanged;
    }

    public ChatViewModel ChatVM { get; }

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
        ChatVM.PropertyChanged -= OnChatViewModelPropertyChanged;
    }

    private void OnChatViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatViewModel.CurrentChatTitle) or nameof(ChatViewModel.CurrentChat))
            OnPropertyChanged(nameof(WindowTitle));
    }
}
