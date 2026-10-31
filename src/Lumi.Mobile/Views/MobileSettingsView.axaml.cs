using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Lumi.Mobile.Behaviors;
using Lumi.Mobile.ViewModels;

namespace Lumi.Mobile.Views;

public partial class MobileSettingsView : UserControl
{
    /// <summary>The width eight swatches need in one row with full touch targets; narrower, they make two rows of four.</summary>
    private const double OneRowOfSwatches = 8 * 56;

    private MobileShellViewModel? _shell;
    private SettingsSection _shownSection;
    private MobileEntrance? _pageEntrance;

    public MobileSettingsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Observe(DataContext as MobileShellViewModel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.IsAttachedToVisualTree())
            Observe(DataContext as MobileShellViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Observe(null);
        _pageEntrance?.Dispose();
        _pageEntrance = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void Observe(MobileShellViewModel? shell)
    {
        if (ReferenceEquals(shell, _shell))
            return;

        if (_shell is not null)
            _shell.PropertyChanged -= OnShellPropertyChanged;
        _shell = shell;
        _shownSection = shell?.VisibleSettingsSection ?? SettingsSection.None;
        if (_shell is not null)
            _shell.PropertyChanged += OnShellPropertyChanged;
    }

    /// <summary>A page that opens rises into place, beside the list or over it, as Library's pages do.</summary>
    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MobileShellViewModel.VisibleSettingsSection) || _shell is null)
            return;

        // Layout changes re-announce the page without changing it; only a new page moves.
        var section = _shell.VisibleSettingsSection;
        if (section == _shownSection)
            return;

        _shownSection = section;
        if (section == SettingsSection.None || !IsEffectivelyVisible
            || this.FindControl<StackPanel>("SettingsDetailContent") is not { } content)
            return;

        _pageEntrance ??= new MobileEntrance(content, offsetY: 10);
        _pageEntrance.Play();
    }

    private void OnAccentSwatchesSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not ItemsControl { ItemsPanelRoot: UniformGrid grid })
            return;

        var columns = e.NewSize.Width >= OneRowOfSwatches ? 8 : 4;
        if (grid.Columns != columns)
            grid.Columns = columns;
    }
}
