using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Lumi.ViewModels;

namespace Lumi.Views;

/// <summary>
/// One titled list in the Workspace overview: an icon, title and count header, the section's rows
/// (rendered with <see cref="ItemTemplate"/>) and a "Show all" link that narrows the overview to the
/// section. Its look lives in the overview's control theme, so every section reads the same.
/// </summary>
public sealed class WorkspaceSectionView : TemplatedControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<WorkspaceSectionView, string?>(nameof(Title));

    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<WorkspaceSectionView, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<IWorkspaceSection?> SectionProperty =
        AvaloniaProperty.Register<WorkspaceSectionView, IWorkspaceSection?>(nameof(Section));

    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<WorkspaceSectionView, IDataTemplate?>(nameof(ItemTemplate));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public IWorkspaceSection? Section
    {
        get => GetValue(SectionProperty);
        set => SetValue(SectionProperty, value);
    }

    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }
}
