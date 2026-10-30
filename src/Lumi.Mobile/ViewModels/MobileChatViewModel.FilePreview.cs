using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Mobile.Services;
using Lumi.Remote.Protocol;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.ViewModels;

public sealed partial class MobileChatViewModel
{
    /// <summary>
    /// The file Lumi made that is open in the preview pane: beside the chat when there is room, over it
    /// otherwise. It belongs to this chat and closes with it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilePreviewOpen))]
    private FilePreviewViewModel? _filePreview;

    public bool IsFilePreviewOpen => FilePreview is not null;

    /// <summary>Opens a file Lumi announced in the preview, replacing any file already in it.</summary>
    [RelayCommand]
    private void OpenFilePreview(RemoteAttachment? file)
    {
        if (file is not { MessageId: { } messageId, FileName.Length: > 0 } || ChatId == Guid.Empty)
            return;
        if (FilePreview is { } current && current.MessageId == messageId && current.ChatId == ChatId)
            return;

        var chatId = ChatId;
        var fileName = file.FileName;
        var preview = new FilePreviewViewModel(file, chatId, token =>
            _sink is IRemoteProducedFileSink sink
                ? sink.DownloadProducedFileAsync(chatId, messageId, fileName, token)
                : Task.FromResult(new ProducedFileDownload(null, Strings.Get("DownloadFileFailed"))));

        // Beside the chat there is room for one supporting surface, as in the desktop's Workspace: the
        // preview takes the place of a panel or of the chat's changes.
        if (UseSidePanels)
        {
            IsPlanOpen = false;
            IsSourcesSheetOpen = false;
            IsActivitySheetOpen = false;
        }
        IsGitChangesOpen = false;
        ReplaceFilePreview(preview);
        _ = preview.LoadAsync();
    }

    [RelayCommand]
    private void CloseFilePreview() => ReplaceFilePreview(null);

    private void ReplaceFilePreview(FilePreviewViewModel? preview)
    {
        var previous = FilePreview;
        if (ReferenceEquals(previous, preview))
            return;

        FilePreview = preview;
        previous?.Close();
    }
}
