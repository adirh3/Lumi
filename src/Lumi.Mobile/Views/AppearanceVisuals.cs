using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Lumi.Mobile.Appearance;

namespace Lumi.Mobile.Views;

/// <summary>An accent palette's swatch, drawn in the palette's tones for the theme on screen.</summary>
public sealed class AccentSwatch : Control
{
    public static readonly StyledProperty<MobileAccentPalettes.Palette?> PaletteProperty =
        AvaloniaProperty.Register<AccentSwatch, MobileAccentPalettes.Palette?>(nameof(Palette));

    private IBrush? _brush;
    private bool _brushIsDark;

    static AccentSwatch() => AffectsRender<AccentSwatch>(PaletteProperty);

    public AccentSwatch() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    public MobileAccentPalettes.Palette? Palette
    {
        get => GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PaletteProperty)
            _brush = null;
    }

    public override void Render(DrawingContext context)
    {
        if (Palette is not { } palette || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var dark = ActualThemeVariant != ThemeVariant.Light;
        if (_brush is null || _brushIsDark != dark)
        {
            _brush = MobileAccentPalettes.CreateSwatch(palette, dark);
            _brushIsDark = dark;
        }

        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2;
        context.DrawEllipse(_brush, null, new Rect(Bounds.Size).Center, radius, radius);
    }
}

/// <summary>
/// A still drawing of Lumi's glow in the accent's colours, which follows the palette and the theme as the
/// chat's presence does. The shell keeps one presence, the chat's, so previews draw instead of a second field.
/// </summary>
public abstract class AccentGlowVisual : Control
{
    /// <summary>How much brighter or quieter than its tuned level the glow shines (the glow setting's multiplier).</summary>
    public static readonly StyledProperty<double> BrightnessProperty =
        AvaloniaProperty.Register<AccentGlowVisual, double>(nameof(Brightness), 1.0);

    private readonly List<IDisposable> _subscriptions = [];

    static AccentGlowVisual() => AffectsRender<AccentGlowVisual>(BrightnessProperty);

    protected AccentGlowVisual()
    {
        IsHitTestVisible = false;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public double Brightness
    {
        get => GetValue(BrightnessProperty);
        set => SetValue(BrightnessProperty, value);
    }

    protected Color Accent { get; private set; } = Colors.Transparent;

    protected Color Violet { get; private set; } = Colors.Transparent;

    protected Color Rose { get; private set; } = Colors.Transparent;

    /// <summary>As bright as the chat's own field: the same tuned level for the theme, times the chosen glow.</summary>
    protected double Strength =>
        MobilePresenceController.ThemeIntensity(ActualThemeVariant == ThemeVariant.Light) * Math.Max(0, Brightness);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Observe("Color.AccentDefault", color => Accent = color);
        Observe("Color.AccentViolet", color => Violet = color);
        Observe("Color.AccentRose", color => Rose = color);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private void Observe(string key, Action<Color> apply) =>
        _subscriptions.Add(this.GetResourceObservable(key).Subscribe(new ColorObserver(color =>
        {
            apply(color);
            InvalidateVisual();
        })));

    /// <summary>One hue fading to its own transparent, so the falloff never greys toward black or white.</summary>
    protected static void DrawPool(DrawingContext context, Color color, Point center, double radiusX, double radiusY, double alpha)
    {
        if (color.A == 0 || alpha <= 0 || radiusX <= 0 || radiusY <= 0)
            return;

        var peak = Math.Clamp(alpha, 0, 1) * 255;
        var brush = new RadialGradientBrush();
        foreach (var (offset, share) in new[] { (0.0, 1.0), (0.35, 0.62), (0.7, 0.2), (1.0, 0.0) })
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(peak * share), color.R, color.G, color.B), offset));
        context.DrawEllipse(brush, null, center, radiusX, radiusY);
    }

    private sealed class ColorObserver(Action<Color> onColor) : IObserver<object?>
    {
        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(object? value)
        {
            if (value is Color color)
                onColor(color);
        }
    }
}

/// <summary>
/// Lumi's glow in miniature, for the Appearance preview: the presence's resting pool around the composer,
/// in the accent's colours and as bright as the chosen glow.
/// </summary>
public sealed class GlowPreview : AccentGlowVisual
{
    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        var strength = Strength;
        if (width <= 0 || height <= 0 || strength <= 0)
            return;

        // A wide pool resting at the composer, as the idle presence lies along it: the accent at its
        // heart, its violet trailing beside it and a little of its rose beneath.
        DrawPool(context, Violet, new Point(0.66 * width, 0.86 * height), 0.48 * width, 0.42 * height, strength * 0.75);
        DrawPool(context, Accent, new Point(0.38 * width, 0.84 * height), 0.56 * width, 0.46 * height, strength);
        DrawPool(context, Rose, new Point(0.54 * width, height), 0.42 * width, 0.3 * height, strength * 0.5);
    }
}

/// <summary>
/// A glow brightness at a glance, for the glow's segments: a core in the accent inside a pool as bright as
/// the choice, and an empty ring for none.
/// </summary>
public sealed class GlowMark : AccentGlowVisual
{
    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
            return;

        var center = new Rect(Bounds.Size).Center;
        if (Brightness <= 0)
        {
            var ring = this.FindResource("Brush.TextTertiary") as IBrush ?? Brushes.Gray;
            context.DrawEllipse(null, new Pen(ring, 1.5), center, size * 0.22, size * 0.22);
            return;
        }

        // The pool grows and brightens with the choice; the core stays the accent itself.
        var level = Math.Min(Brightness, 1.6);
        var reach = Math.Min(size / 2, size * (0.2 + 0.2 * level));
        DrawPool(context, Accent, center, reach, reach, Math.Min(1, 0.35 + 0.45 * level));
        context.DrawEllipse(new SolidColorBrush(Accent), null, center, size * 0.14, size * 0.14);
    }
}
