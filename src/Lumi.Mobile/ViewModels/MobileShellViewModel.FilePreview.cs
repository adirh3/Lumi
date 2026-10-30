using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.Mobile.Services;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.ViewModels;

public sealed partial class MobileShellViewModel : IRemoteProducedFileSink
{
    // ── The file preview ──────────────────────────────────────────────────────────────────────
    //
    // A file Lumi made opens in a pane of its own, as in the desktop's Workspace. Where two panes fit,
    // the chat moves into the chat list's place and the preview takes the rest; on a physical hinge
    // or a half-folded crease the two meet at the fold. Otherwise the preview covers the chat.

    /// <summary>The chat beside a preview keeps at least a phone's reading width.</summary>
    public const double MinimumChatBesidePreview = 360;

    /// <summary>On a wide screen the chat stops growing here and the preview takes the rest.</summary>
    public const double MaximumChatBesidePreview = 520;

    /// <summary>The preview shares the screen with the chat, in the chat list's place.</summary>
    public bool IsFilePreviewBeside => Chat.IsFilePreviewOpen && Layout.CanShowTwoPanes;

    /// <summary>One pane: the preview is the whole screen, over the chat.</summary>
    public bool IsFilePreviewCoveringChat => Chat.IsFilePreviewOpen && !Layout.CanShowTwoPanes;

    /// <summary>The chat's column beside a preview: the fold where there is one, otherwise a reading column.</summary>
    public double FilePreviewChatWidth => HasHingeGap
        ? Layout.HingePosition
        : Layout.FoldPosition > 0
            ? Layout.FoldPosition
            : Math.Clamp(Math.Round(Layout.Width * 0.42), MinimumChatBesidePreview, MaximumChatBesidePreview);

    /// <summary>A physical hinge between the chat and the preview, never painted over.</summary>
    public double FilePreviewGapWidth => HasHingeGap ? Layout.HingeSize : 0;

    /// <summary>The preview's column when it sits beside the chat.</summary>
    public double FilePreviewPaneWidth =>
        Math.Max(0, Layout.Width - FilePreviewChatWidth - FilePreviewGapWidth);

    /// <summary>The navigation pane keeps the leading side of a hinge, except while a preview has the chat there.</summary>
    public bool ReservesHingeForNavigation => HasHingeGap && !IsFilePreviewBeside;

    /// <summary>Where the preview's floating card sits in its pane: clear of the bars and the screen's edge.</summary>
    public Thickness FilePreviewPaneMargin => new(8, SafeArea.Top + 8, SafeArea.Right + 8, SafeArea.Bottom + 8);

    /// <summary>Set by the view while the preview's pane grows or shrinks beside the chat.</summary>
    [ObservableProperty] private bool _isFilePreviewMoving;

    partial void OnIsFilePreviewMovingChanged(bool value) => OnPropertyChanged(nameof(IsPaneMoving));

    /// <summary>Panes are moving: the drawer is sliding or a preview is opening beside the chat.</summary>
    public bool IsPaneMoving => IsDrawerMoving || IsFilePreviewMoving;

    private void OnFilePreviewChanged()
    {
        NotifyFilePreviewLayoutChanged();
        ReconcileSidebarWithSidePanel();
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanDragDrawer));
        NotifyWebAppUpdateSafetyChanged();
        OnPropertyChanged(nameof(CanAutomaticallyRecoverGatewaySignIn));
        SyncChatHiddenByPreview();
    }

    private bool _chatHiddenByPreview;

    /// <summary>
    /// The chat under a full-screen preview is not being read, so it is not marked read; it catches up
    /// as it comes back into view, when the preview closes or the phone unfolds beside it.
    /// </summary>
    private void SyncChatHiddenByPreview()
    {
        var hidden = IsFilePreviewCoveringChat;
        if (hidden == _chatHiddenByPreview)
            return;

        _chatHiddenByPreview = hidden;
        if (!hidden && CanReadChatTranscript)
            _ = RefreshTranscriptAsync();
    }

    private void NotifyFilePreviewLayoutChanged()
    {
        OnPropertyChanged(nameof(IsFilePreviewBeside));
        OnPropertyChanged(nameof(IsFilePreviewCoveringChat));
        OnPropertyChanged(nameof(FilePreviewChatWidth));
        OnPropertyChanged(nameof(FilePreviewGapWidth));
        OnPropertyChanged(nameof(FilePreviewPaneWidth));
        OnPropertyChanged(nameof(FilePreviewPaneMargin));
        OnPropertyChanged(nameof(ReservesHingeForNavigation));
        OnPropertyChanged(nameof(NavigationLeadingWidth));
    }

#if DEBUG
    /// <summary>DEBUG only: the design preview serves its own files, as there is no PC behind it.</summary>
    internal Func<Guid, string, CancellationToken, Task<string?>>? DesignPreviewFiles { get; set; }
#endif

    public async Task<ProducedFileDownload> DownloadProducedFileAsync(
        Guid chatId,
        Guid messageId,
        string fileName,
        CancellationToken cancellationToken)
    {
#if DEBUG
        if (DesignPreviewFiles is { } designFiles)
            return new ProducedFileDownload(await designFiles(messageId, fileName, cancellationToken), null);
#endif
        // A download holds a web app's reload until it lands, as opening a file always has.
        BeginPendingRemoteAction();
        try
        {
            using var request = CreateConnectionRequest();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.Token, cancellationToken);
            var path = await Client.DownloadProducedFileAsync(
                chatId, messageId, fileName, linked.Token, preferLocalFile: true);
            return path is null
                ? new ProducedFileDownload(null, Client.StateMessage ?? Strings.Get("DownloadFileFailed"))
                : new ProducedFileDownload(path, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                 && !_lifetime.IsCancellationRequested)
        {
            return new ProducedFileDownload(null, Strings.Get("ConnectionChangedAction"));
        }
        finally
        {
            EndPendingRemoteAction();
        }
    }
}
