using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Lumi.Services;
using Lumi.Services.Appearance;
using StrataTheme.Animation;
using static Lumi.Views.StartupMotion;
using Part = Lumi.Services.AppIconArt.Part;

namespace Lumi.Views;

/// <summary>
/// What Lumi shows while it starts: its mark putting itself together over a slowly turning glow, and the
/// Strata line. The mark's parts are drawn in the user's accent; with animations off it all stands still.
/// </summary>
public partial class StartupWindow : Window
{
    /// <summary>The side of the mark, in DIPs.</summary>
    private const double MarkSide = 104;

    private const double Tau = Math.PI * 2;

    private readonly bool _animate;
    private readonly List<Bitmap> _art = [];
    private long _openedAt;

    public StartupWindow() : this(AccentPalettes.DefaultId, animate: true)
    {
    }

    public StartupWindow(string? accent, bool animate)
    {
        _animate = animate;
        InitializeComponent();
        // Without a title bar, Windows still rounds the window and casts its shadow when the client area
        // extends into the frame.
        if (OperatingSystem.IsWindows())
            ExtendClientAreaToDecorationsHint = true;
        AppIcon.ApplyWindowIcon(this);
        DrawMark(AccentPalettes.Find(accent));

        var model = At(Part.Model);
        Ripple.Margin = new Thickness(model.X - Ripple.Width / 2, model.Y - Ripple.Height / 2, 0, 0);
        // The light runs along the Strata line only while it moves; standing still it would rest at one end.
        StartupLineSweep.IsVisible = animate;
        LifecycleOffsetSweep.SetIsActive(StartupLineSweep, animate);
    }

    /// <summary>Whether the mark moves; it stands still when the user turned animations off.</summary>
    internal bool IsAnimated => _animate;

    /// <summary>How long the window has been open.</summary>
    internal TimeSpan SinceOpened => _openedAt == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_openedAt);

    /// <summary>Where the window's content is on screen, in physical pixels.</summary>
    internal PixelRect ContentOnScreen => new(this.PointToScreen(default), PixelSize.FromSize(ClientSize, RenderScaling));

    /// <summary>Where Lumi's mark is on screen, in physical pixels.</summary>
    internal PixelRect MarkOnScreen => new(Mark.PointToScreen(default), Mark.PointToScreen(new Point(MarkSide, MarkSide)));

    /// <summary>
    /// Brings the mark's parts to rest from wherever their motion has them, so the mark can be handed over
    /// whole. The rest of the window stays as it is: the hand-off's card comes into sight over it.
    /// </summary>
    internal void Settle(TimeSpan duration)
    {
        if (!_animate)
            return;

        // Only transforms and full opacity: Avalonia restores a control's own opacity whenever it redraws it, so
        // a fade-out here could flash back.
        var whole = new Vector3D(1, 1, 1);
        foreach (var part in new Visual[] { TileHost, ModelHost, LumiHost, YouHost, ModelArt, LumiArt, YouArt })
        {
            StartupMotion.Settle(part, nameof(CompositionVisual.Scale), whole, duration);
            StartupMotion.Settle(part, nameof(CompositionVisual.Translation), default(Vector3D), duration);
            StartupMotion.Settle(part, nameof(CompositionVisual.Opacity), 1f, duration);
        }
        StartupMotion.Settle(LumiHost, nameof(CompositionVisual.RotationAngle), 0f, duration);
    }

    /// <summary>Says what Lumi is doing instead of starting, such as recovering chats.</summary>
    public void ShowStatus(string text) => StartupStatus.Text = text;

    protected override void OnOpened(EventArgs e)
    {
        _openedAt = Stopwatch.GetTimestamp();
        // Before the first frame, so the mark never shows whole before it puts itself together.
        PaintGlow();
        if (_animate)
            Play();
        base.OnOpened(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        foreach (var bitmap in _art)
            bitmap.Dispose();
        _art.Clear();
    }

    /// <summary>Draws each part of the mark on its own layer, sharp at the largest display scale.</summary>
    private void DrawMark(AccentPalettes.Palette palette)
    {
        var scaling = Screens?.All.Count > 0 ? Screens.All.Max(screen => screen.Scaling) : 2;
        // Headroom for the parts' swells, so they stay sharp at their largest.
        var pixels = (int)Math.Ceiling(MarkSide * Math.Clamp(scaling, 1, 4) * 1.15);
        foreach (var (image, part) in new[] { (TileArt, Part.Tile), (ModelArt, Part.Model), (LumiArt, Part.Lumi), (YouArt, Part.You) })
        {
            var bitmap = AppIcon.CreateMarkPart(palette, part, pixels);
            _art.Add(bitmap);
            image.Source = bitmap;
        }
    }

    /// <summary>The glow behind the mark, in the accent's three hues, softer on a light window.</summary>
    private void PaintGlow()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var strength = dark ? 1.0 : 0.7;
        GlowAccent.Background = GlowBrush("Color.AccentDefault", 0.7 * strength);
        GlowViolet.Background = GlowBrush("Color.AccentViolet", 0.6 * strength);
        GlowRose.Background = GlowBrush("Color.AccentRose", 0.5 * strength);
        Flare.Background = GlowBrush("Color.AccentViolet", dark ? 0.85 : 0.6);
    }

    private IBrush? GlowBrush(string colorKey, double strength)
    {
        if (!this.TryFindResource(colorKey, ActualThemeVariant, out var value) || value is not Color color)
            return null;

        Color Tone(double alpha) => Color.FromArgb((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1)), color.R, color.G, color.B);
        return new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Tone(strength), 0),
                new GradientStop(Tone(strength * 0.5), 0.5),
                new GradientStop(Tone(strength * 0.12), 0.78),
                new GradientStop(Tone(0), 1),
            },
        };
    }

    private void Play()
    {
        var tile = new Point(MarkSide / 2, MarkSide / 2);
        var lumi = At(Part.Lumi);

        // The glow gathers behind the mark and keeps turning, each hue at its own pace.
        Fade(Glow, EaseOutCubic, Timing.Once(900));
        Swirl(GlowAccent, turnMs: 13000, direction: 1, breathMs: 5200);
        Swirl(GlowViolet, turnMs: 17000, direction: -1, breathMs: 6600);
        Swirl(GlowRose, turnMs: 21000, direction: 1, breathMs: 7700);

        // The tile springs in, then Lumi pops in with a twirl.
        Scale(TileHost, tile, q => 0.55 + 0.45 * Spring(q, 0.6), Timing.Once(640));
        Fade(TileHost, q => EaseOutCubic(q / 0.3), Timing.Once(640));

        var lumiIn = Timing.Once(720, 90);
        Scale(LumiHost, lumi, q => 0.05 + 0.95 * Spring(q, 0.6), lumiIn);
        Turn(LumiHost, lumi, q => -0.45 * Math.PI * (1 - Spring(q, 0.66)), lumiIn);
        Fade(LumiHost, q => q / 0.2, lumiIn);

        // You and the model spin out of Lumi, to either side of it.
        foreach (var (host, part, delayMs) in new[] { (YouHost, Part.You, 220.0), (ModelHost, Part.Model, 290.0) })
        {
            var spin = Timing.Once(920, delayMs);
            Move(host, q => SpiralOffset(part, q) * MarkSide, spin);
            Scale(host, At(part), q => 0.35 + 0.65 * Spring(q, 0.55), spin);
            Fade(host, q => q / 0.15, spin);
        }

        // Then, for as long as Lumi takes: you float, Lumi breathes, and now and then you and the model
        // circle Lumi and land with a ping and a flare of light.
        var loop = Timing.Looping(LoopPeriod, LoopStart);
        foreach (var (art, part) in new[] { (YouArt, Part.You), (ModelArt, Part.Model) })
        {
            Move(art, p => LoopOffset(part, p) * MarkSide, loop);
            Scale(art, At(part), p => LoopScale(part, p), loop);
        }
        Scale(LumiArt, lumi, p => LoopScale(Part.Lumi, p), loop);

        Scale(Ripple, new Point(Ripple.Width / 2, Ripple.Height / 2), p => StartupMotion.Ripple(p).Scale, loop);
        Fade(Ripple, p => StartupMotion.Ripple(p).Opacity, loop);
        Fade(Flare, StartupMotion.Flare, loop);

        // The words rise in under the mark, and the Strata line draws out from its middle.
        SlideFadeEntrance.PlayComposited(StartupTitle, 10, TimeSpan.FromMilliseconds(460), TimeSpan.FromMilliseconds(260));
        SlideFadeEntrance.PlayComposited(StartupStatus, 10, TimeSpan.FromMilliseconds(460), TimeSpan.FromMilliseconds(340));
        Scale(StartupLine, new Point(StartupLine.Width / 2, StartupLine.Height / 2), q => new Vector(EaseOutCubic(q), 1), Timing.Once(640, 420));
    }

    /// <summary>Turns a glow about the middle of the stage, swelling and easing back as it goes.</summary>
    private static void Swirl(Border glow, double turnMs, int direction, double breathMs)
    {
        // The glow sits centred on the stage, so its own centre (before its offset) is the stage's.
        var centre = new Point(glow.Width / 2, glow.Height / 2);
        Turn(glow, centre, p => direction * Tau * p, Timing.Looping(TimeSpan.FromMilliseconds(turnMs), TimeSpan.Zero));
        Scale(glow, centre, p => 1 + 0.12 * Math.Sin(Tau * p), Timing.Looping(TimeSpan.FromMilliseconds(breathMs), TimeSpan.Zero));
    }

    /// <summary>The centre of a part of the mark, in the mark's DIPs.</summary>
    private static Point At(Part part)
    {
        var centre = AppIconArt.CentreOf(part);
        return new Point(centre.X * MarkSide, centre.Y * MarkSide);
    }
}
