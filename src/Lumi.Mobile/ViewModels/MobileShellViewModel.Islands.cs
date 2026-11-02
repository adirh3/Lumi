using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lumi.Mobile.ViewModels;

public sealed partial class MobileShellViewModel
{
    // ── Islands ───────────────────────────────────────────────────────────────────────────────
    //
    // As on the desktop, lists stay on the canvas and what is open rests on it as an island beside them:
    // the chat, a place's open page, a file. The canvas still runs under the status bar, the cutout and
    // the gesture area; the island keeps clear of them, and of the keyboard, by one gap. A side panel or a
    // file preview beside it floats with the same gap, so islands side by side read as one family.
    //
    // An island only makes sense beside something. Alone (a phone, a folded Fold, or the list hidden)
    // what is open is the whole pane in the island's colour, its content clear of the bars, and while the
    // docked list slides away or back the island settles into the pane, or lifts out of it, with it.

    /// <summary>The canvas between an island and the screen's edge, a list, or another island.</summary>
    public const double IslandGap = 8;

    /// <summary>
    /// The docked list's live position while it slides (0 away, 1 beside the chat), which the view
    /// follows from the drawer; at rest the island follows the docked state itself.
    /// </summary>
    [ObservableProperty] private double _navigationProgress;

    /// <summary>The chat stays an island while its list comes back in place of a panel or a file.</summary>
    private bool _holdChatIsland;

    /// <summary>
    /// What an island's foot keeps clear of: the gesture bar, or the keyboard while it is open. A
    /// browser's home indicator may pass over the foot, as it passed over the composer's own gap.
    /// </summary>
    private double IslandFootClearance => ComposerSafeAreaBottom.Bottom;

    /// <summary>
    /// How much of the chat's leading edge the chat list holds: all of it across a hinge or docked, none
    /// when it is away, and its live share while it slides.
    /// </summary>
    private double ListPresence => ReservesHingeForNavigation ? 1
        : !CanDockDrawer ? 0
        : IsDrawerMoving ? Math.Clamp(NavigationProgress, 0, 1)
        : IsDrawerDocked ? 1 : 0;

    /// <summary>
    /// How much of an island the chat is: 1 beside the list, a hinge, a side panel or a file; 0 alone;
    /// between while the docked list slides.
    /// </summary>
    public double ChatIslandFrame =>
        IsFilePreviewBeside || Chat.HasSidePanelOpen || _holdChatIsland && IsDrawerDocked ? 1 : ListPresence;

    /// <summary>The part of a leading cutout the list does not cover.</summary>
    private double ChatLeadingSafeArea => SafeArea.Left * (1 - ListPresence);

    /// <summary>The conversation's island within its pane; nothing while the chat is the whole pane.</summary>
    public Thickness ChatIslandMargin
    {
        get
        {
            var frame = ChatIslandFrame;
            return frame <= 0 ? default : new Thickness(
                frame * (IslandGap + ChatLeadingSafeArea),
                frame * (SafeArea.Top + IslandGap),
                // A preview beside the chat begins one gap after it; a side panel keeps its room on the canvas.
                frame * (IsFilePreviewBeside ? IslandGap : SafeArea.Right + IslandGap + ChatSidePanelInset.Right),
                frame * (IslandFootClearance + IslandGap));
        }
    }

    /// <summary>What keeps the chat's content clear of the bars while it is the whole pane.</summary>
    public Thickness ChatIslandPadding
    {
        get
        {
            var alone = 1 - ChatIslandFrame;
            return alone <= 0 ? default : new Thickness(
                alone * ChatLeadingSafeArea,
                alone * SafeArea.Top,
                alone * SafeArea.Right,
                alone * IslandFootClearance);
        }
    }

    /// <summary>An open page of Jobs, Customize or Settings is an island beside its list, the pane alone.</summary>
    public double PageIslandFrame => IsTwoPane || HasListBeside ? 1 : 0;

    public Thickness PageIslandMargin => PageIslandFrame <= 0 ? default : new Thickness(
        IsTwoPane || HasListBeside ? IslandGap : SafeArea.Left + IslandGap,
        SafeArea.Top + IslandGap,
        SafeArea.Right + IslandGap,
        IslandFootClearance + IslandGap);

    public Thickness PageIslandPadding => PageIslandFrame > 0 ? default : new Thickness(
        SafeArea.Left, SafeArea.Top, SafeArea.Right, IslandFootClearance);

    /// <summary>
    /// A list's header on the canvas, level with an island's header beside it: where islands rest beside
    /// lists, both start one gap below the status bar; on a phone, neither does.
    /// </summary>
    public Thickness CanvasHeaderInset => new(0, SafeArea.Top + (Layout.CanShowTwoPanes ? IslandGap : 0), 0, 0);

    /// <summary>The chat list, or the navigation pane across a hinge, sits on the chat's leading side.</summary>
    private bool HasListBeside => IsDrawerDocked || ReservesHingeForNavigation;

    /// <summary>The list returns in place of a side panel or a file: the chat stays an island meanwhile.</summary>
    private void HoldChatIslandWhileListReturns()
    {
        _holdChatIsland = true;
        NotifyIslandsChanged();
    }

    private void NotifyIslandsChanged()
    {
        NotifyChatIslandChanged(force: true);
        OnPropertyChanged(nameof(PageIslandFrame));
        OnPropertyChanged(nameof(PageIslandMargin));
        OnPropertyChanged(nameof(PageIslandPadding));
        OnPropertyChanged(nameof(CanvasHeaderInset));
    }

    private (double Frame, Thickness Margin, Thickness Padding) _chatIslandShown = (double.NaN, default, default);

    /// <summary>While the list slides, the chat's shape is published only on the frames it changes.</summary>
    private void NotifyChatIslandChanged(bool force)
    {
        var shape = (ChatIslandFrame, ChatIslandMargin, ChatIslandPadding);
        if (!force && shape == _chatIslandShown)
            return;

        _chatIslandShown = shape;
        OnPropertyChanged(nameof(ChatIslandFrame));
        OnPropertyChanged(nameof(ChatIslandMargin));
        OnPropertyChanged(nameof(ChatIslandPadding));
    }

    partial void OnNavigationProgressChanged(double value)
    {
        if (IsDrawerMoving && CanDockDrawer)
            NotifyChatIslandChanged(force: false);
    }

    partial void OnIsDrawerMovingChanged(bool value)
    {
        if (!value)
            _holdChatIsland = false;
        NotifyIslandsChanged();
    }

    partial void OnComposerSafeAreaBottomChanged(Thickness value)
    {
        NotifyIslandsChanged();
        OnPropertyChanged(nameof(SidePanelSheetMargin));
        OnPropertyChanged(nameof(FilePreviewPaneMargin));
    }
}
