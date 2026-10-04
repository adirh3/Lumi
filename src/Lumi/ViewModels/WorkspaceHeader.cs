using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lumi.ViewModels;

/// <summary>
/// What the Workspace header shows for the page on screen. Each Workspace view owns one (two windows
/// can show the same chat, each on its own page), so it lives beside the view rather than on the chat.
/// </summary>
public sealed partial class WorkspaceHeader : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverview), nameof(IsFilePreview), nameof(Icon))]
    private WorkspacePage _page = WorkspacePage.Overview;

    [ObservableProperty] private string _title = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubtitle))]
    private string? _subtitle;

    public bool IsOverview => Page == WorkspacePage.Overview;
    public bool IsFilePreview => Page == WorkspacePage.FilePreview;
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
    public Geometry Icon => WorkspaceIcons.For(Page);
}
