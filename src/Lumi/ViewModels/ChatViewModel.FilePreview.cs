using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    [ObservableProperty] private string? _previewFilePath;
    [ObservableProperty] private bool _isFilePreviewOpen;

    public event Action<string>? FilePreviewShowRequested;
    public event Action? FilePreviewHideRequested;

    [RelayCommand]
    public void OpenFilePreview(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        PreviewFilePath = filePath;
        IsFilePreviewOpen = true;
        FilePreviewShowRequested?.Invoke(filePath);
    }

    [RelayCommand]
    public void CloseFilePreview()
    {
        IsFilePreviewOpen = false;
        PreviewFilePath = null;
        FilePreviewHideRequested?.Invoke();
    }
}
