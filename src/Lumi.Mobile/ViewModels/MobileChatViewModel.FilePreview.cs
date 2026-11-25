using Avalonia.Media;
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
    private readonly Dictionary<int, FilePreviewViewModel> _imageGalleryPreviews = [];

    public bool IsFilePreviewOpen => FilePreview is not null;

    /// <summary>Opens a file Lumi announced in the preview, replacing any file already in it.</summary>
    [RelayCommand]
    private void OpenFilePreview(RemoteAttachment? file) => OpenAttachmentPreview(file);

    internal void OpenAttachmentPreview(RemoteAttachment? file, IEnumerable<RemoteAttachment>? siblings = null)
    {
        List<RemoteAttachment>? images = null;
        var index = 0;
        if (file is not null && FilePreviewContent.Classify(file.FileName) == FilePreviewKind.Image && siblings is not null)
        {
            images = siblings.Where(attachment => attachment.MessageId.HasValue
                && FilePreviewContent.Classify(attachment.FileName) == FilePreviewKind.Image).ToList();
            index = images.IndexOf(file);
        }
        if (images is not { Count: > 1 } || index < 0)
            ShowFilePreview(file, [], 0);
        else
            ShowFilePreview(file, images, index);
    }

    internal void StepImagePreview(FilePreviewViewModel from, int step)
    {
        if (!ReferenceEquals(from, FilePreview) || from.IsClosed)
            return;

        var index = from.ImageGalleryIndex + step;
        if (_imageGalleryPreviews.TryGetValue(index, out var next))
        {
            FilePreview = next;
            KeepAdjacentImagePreviews(next);
        }
    }

    internal IImage? GetAdjacentPreviewImage(FilePreviewViewModel from, int step) =>
        ReferenceEquals(from, FilePreview)
        && _imageGalleryPreviews.TryGetValue(from.ImageGalleryIndex + step, out var adjacent)
            ? adjacent.Image
            : null;

    private void ShowFilePreview(RemoteAttachment? file, IReadOnlyList<RemoteAttachment> images, int index)
    {
        if (file is not { MessageId: { } messageId, FileName.Length: > 0 } || ChatId == Guid.Empty)
            return;
        if (FilePreview is { } current && current.MessageId == messageId && current.ChatId == ChatId
            && current.File.AttachmentIndex == file.AttachmentIndex)
            return;

        var preview = CreateFilePreview(file, ChatId, images, index);

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
        if (preview.HasImageGallery)
            KeepAdjacentImagePreviews(preview);
    }

    private FilePreviewViewModel CreateFilePreview(
        RemoteAttachment file, Guid chatId, IReadOnlyList<RemoteAttachment> images, int index) =>
        new(file, chatId, token => _sink is IRemoteFilePreviewSink sink
            ? sink.DownloadPreviewFileAsync(chatId, file, token)
            : Task.FromResult(new ProducedFileDownload(null, Strings.Get("DownloadFileFailed"))))
        {
            ImageGallery = images,
            ImageGalleryIndex = index
        };

    private void KeepAdjacentImagePreviews(FilePreviewViewModel current)
    {
        var index = current.ImageGalleryIndex;
        _imageGalleryPreviews[index] = current;
        foreach (var stale in _imageGalleryPreviews.Keys.Where(key => Math.Abs(key - index) > 1).ToArray())
        {
            _imageGalleryPreviews[stale].Close();
            _imageGalleryPreviews.Remove(stale);
        }
        Warm(index - 1);
        Warm(index + 1);

        void Warm(int neighbor)
        {
            if ((uint)neighbor >= (uint)current.ImageGallery.Count || _imageGalleryPreviews.ContainsKey(neighbor))
                return;
            var preview = CreateFilePreview(current.ImageGallery[neighbor], current.ChatId, current.ImageGallery, neighbor);
            _imageGalleryPreviews.Add(neighbor, preview);
            _ = preview.LoadAsync();
        }
    }

    [RelayCommand]
    private void CloseFilePreview() => ReplaceFilePreview(null);

    private void ReplaceFilePreview(FilePreviewViewModel? preview)
    {
        var previous = FilePreview;
        if (ReferenceEquals(previous, preview))
            return;

        FilePreview = preview;
        foreach (var cached in _imageGalleryPreviews.Values)
        {
            if (!ReferenceEquals(cached, previous))
                cached.Close();
        }
        _imageGalleryPreviews.Clear();
        previous?.Close();
    }
}
