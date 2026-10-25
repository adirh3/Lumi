using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Services;
using Lumi.ViewModels;
using static Lumi.Views.StartupMotion;

namespace Lumi.Views;

/// <summary>Lumi's arrival when it starts on a welcome screen, taking over from the loading window.</summary>
public partial class MainWindow
{
    // The mark lifts off as the hand-off card clears, and the window fills in behind it.
    private static readonly TimeSpan ArrivalGlide = TimeSpan.FromMilliseconds(640);
    private static readonly TimeSpan ArrivalFillDelay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan LogoFadeIn = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan ArrivalDeadline = TimeSpan.FromSeconds(2);

    private bool _startupBoundsPrepared;
    private Action? _arrival;

    /// <summary>
    /// Places the window where it will open, before it shows, and returns that place on screen in physical
    /// pixels with the scale of its screen.
    /// </summary>
    internal PixelRect PrepareStartupBounds(out double scaling)
    {
        RestoreWindowBounds();
        _startupBoundsPrepared = true;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        scaling = screen is { Scaling: > 0 } ? screen.Scaling : 1;
        if (WindowState == WindowState.Maximized && screen is not null)
            return screen.WorkingArea;
        return new PixelRect(Position, PixelSize.FromSize(new Size(Width, Height), scaling));
    }

    /// <summary>
    /// Readies Lumi's arrival under the hand-off card: the welcome tour, or Lumi's panels, wait out of sight, the
    /// welcome logo too, and Lumi's mark stands where the loading window had it (<paramref name="markOnScreen"/>,
    /// physical pixels). <see cref="PlayArrival"/> plays it, and it plays by itself if nothing does. Call it once
    /// the window has been laid out.
    /// </summary>
    internal void PrepareArrival(PixelRect markOnScreen)
    {
        var onboarding = _onboardingPanel?.IsVisible == true;
        // The welcome tour fills in over its card, which stays put so Lumi's panels never show through.
        var content = onboarding ? (Visual?)_onboardingHost : _mainPanel;
        if (content is not null)
            Fade(content, _ => 0, Timing.Once(100));

        Canvas? layer = OverlayLayer.GetOverlayLayer(this);
        Image? ghost = null;
        Border? logo = null;
        var from = default(Rect);
        var restingOpacity = 1.0;
        if (layer is not null && this.FindResource(AppIcon.MarkResourceKey) is IImage mark)
        {
            // Placed in the window's own space: the overlay layer shares it once laid out, also right to left, while
            // mapping through the layer itself goes wrong there (it mirrors itself back to left to right).
            from = Normalized(this.PointToClient(markOnScreen.TopLeft), this.PointToClient(markOnScreen.BottomRight));
            ghost = new Image { Source = mark, Width = from.Width, Height = from.Height, IsHitTestVisible = false };
            RenderOptions.SetBitmapInterpolationMode(ghost, BitmapInterpolationMode.HighQuality);
            Canvas.SetLeft(ghost, from.X);
            Canvas.SetTop(ghost, from.Y);
            layer.Children.Add(ghost);

            // Its own opacity, which Avalonia keeps whenever it redraws the logo, as the welcome screen fills in.
            var logoName = onboarding ? "HelloMark" : "WelcomeOrb";
            logo = this.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(border => border.Name == logoName && border.IsEffectivelyVisible && border.Bounds.Width > 0);
            if (logo is not null)
            {
                restingOpacity = logo.Opacity;
                logo.Opacity = 0;
            }
        }

        _arrival = () =>
        {
            try
            {
                if (content is not null)
                {
                    var fill = Timing.Once(460, ArrivalFillDelay.TotalMilliseconds);
                    Fade(content, EaseOutCubic, fill);
                    Move(content, q => new Vector(0, 14 * (1 - EaseOutCubic(q))), fill);
                }
                if (layer is not null && ghost is not null)
                    GlideIntoLogo(layer, ghost, from, logo, restingOpacity);
            }
            catch (Exception ex)
            {
                // Whatever went wrong, everything comes into sight at once.
                Trace.TraceWarning($"[Startup] Lumi's arrival failed: {ex.Message}");
                if (content is not null && ElementComposition.GetElementVisual(content) is { } visual)
                {
                    visual.Opacity = (float)content.Opacity;
                    visual.Translation = default;
                }
                if (ghost is not null)
                    layer?.Children.Remove(ghost);
                if (logo is not null)
                    logo.Opacity = restingOpacity;
            }
            // Focus shows as the mark lands, when the user can see it come in.
            DispatcherTimer.RunOnce(FocusFirstScreen, ArrivalGlide, DispatcherPriority.Normal);
        };
        // Whatever happens to the hand-off, the window arrives.
        DispatcherTimer.RunOnce(() => PlayArrival(), ArrivalDeadline, DispatcherPriority.Normal);
    }

    /// <summary>Plays the arrival <see cref="PrepareArrival"/> readied, once; whether there was one to play.</summary>
    internal bool PlayArrival()
    {
        var arrival = _arrival;
        _arrival = null;
        arrival?.Invoke();
        return arrival is not null;
    }

    /// <summary>
    /// Focuses what the first screen focuses when the window opens. The hand-off opens the window out of sight and
    /// not yet in charge, where that focus doesn't hold, so it focuses again once the window is.
    /// </summary>
    internal void FocusFirstScreen()
    {
        if (_onboardingPanel?.IsVisible == true)
            (_onboardingHost?.Content as OnboardingView)?.FocusNameSoon();
        else if (DataContext is MainViewModel { IsOnboarded: true, SelectedNavIndex: 0 })
            _chatView?.FocusComposer();
    }

    private void GlideIntoLogo(Canvas layer, Image ghost, Rect from, Border? logo, double restingOpacity)
    {
        var to = logo is null ? default : InWindow(logo, this);
        if (logo is null || to.Width <= 0 || to.Height <= 0)
        {
            // No welcome logo to land on: the mark fades as the window fills in.
            if (logo is not null)
                logo.Opacity = restingOpacity;
            Fade(ghost, q => 1 - q, Timing.Once(220, ArrivalFillDelay.TotalMilliseconds));
            DispatcherTimer.RunOnce(() => layer.Children.Remove(ghost), ArrivalFillDelay + TimeSpan.FromMilliseconds(260));
            return;
        }

        ghost.Width = to.Width;
        ghost.Height = to.Height;
        Canvas.SetLeft(ghost, to.X);
        Canvas.SetTop(ghost, to.Y);

        Vector offset = from.Center - to.Center;
        var scale = from.Width / to.Width;
        var glide = new Timing(ArrivalGlide, TimeSpan.Zero);
        Move(ghost, q => offset * (1 - Spring(q, 0.74)), glide);
        Scale(ghost, new Point(to.Width / 2, to.Height / 2), q => scale + (1 - scale) * Spring(q, 0.74), glide);

        // The welcome logo comes in under the mark as it lands, and then the mark makes way.
        DispatcherTimer.RunOnce(() =>
        {
            var transitions = logo.Transitions;
            logo.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = LogoFadeIn }];
            logo.Opacity = restingOpacity;
            DispatcherTimer.RunOnce(() =>
            {
                logo.Transitions = transitions;
                layer.Children.Remove(ghost);
            }, LogoFadeIn + TimeSpan.FromMilliseconds(40), DispatcherPriority.Normal);
        }, ArrivalGlide, DispatcherPriority.Normal);
    }

    /// <summary>
    /// Where <paramref name="visual"/> is in <paramref name="window"/>'s own space, left to right as on screen, which
    /// the overlay layer shares once laid out. In a right-to-left window the visual's own left edge is on the right,
    /// so its corners are put back in order.
    /// </summary>
    internal static Rect InWindow(Visual visual, TopLevel window)
        => visual.TranslatePoint(default, window) is { } a
           && visual.TranslatePoint(new Point(visual.Bounds.Width, visual.Bounds.Height), window) is { } b
            ? Normalized(a, b)
            : default;

    private static Rect Normalized(Point a, Point b)
        => new(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
}
