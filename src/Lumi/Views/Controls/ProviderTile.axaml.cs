using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Lumi.ViewModels;

namespace Lumi.Views.Controls;

/// <summary>A provider's identity tile, scaled to <see cref="TileSize"/>.</summary>
public partial class ProviderTile : UserControl
{
    public static readonly StyledProperty<double> TileSizeProperty =
        AvaloniaProperty.Register<ProviderTile, double>(nameof(TileSize), 40);

    private readonly Border _fill;
    private readonly Border _outline;
    private readonly TextBlock _monogram;
    private readonly PathIcon _gitHub;
    private readonly PathIcon _custom;

    public ProviderTile()
    {
        AvaloniaXamlLoader.Load(this);
        _fill = this.FindControl<Border>("TileFill")!;
        _outline = this.FindControl<Border>("TileOutline")!;
        _monogram = this.FindControl<TextBlock>("TileMonogram")!;
        _gitHub = this.FindControl<PathIcon>("TileGitHub")!;
        _custom = this.FindControl<PathIcon>("TileCustom")!;
        ApplySize();
    }

    public double TileSize
    {
        get => GetValue(TileSizeProperty);
        set => SetValue(TileSizeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TileSizeProperty && _fill is not null)
            ApplySize();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ApplySize();
    }

    private void ApplySize()
    {
        var size = TileSize;
        Width = size;
        Height = size;
        var radius = new CornerRadius(Math.Round(size * 0.28));
        _fill.CornerRadius = radius;
        _outline.CornerRadius = radius;
        var letters = (DataContext as ProviderBadge)?.Monogram.Length ?? 1;
        _monogram.FontSize = Math.Round(size * (letters > 1 ? 0.35 : 0.42));
        _gitHub.Width = _gitHub.Height = Math.Round(size * 0.52);
        _custom.Width = _custom.Height = Math.Round(size * 0.44);
    }
}
