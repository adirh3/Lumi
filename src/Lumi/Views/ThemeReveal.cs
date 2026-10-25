using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Lumi.Views.Management;
using SkiaSharp;

namespace Lumi.Views;

/// <summary>
/// How Lumi turns light or dark when the user picks a theme, in Settings → Appearance or in the welcome tour. A
/// picture of the window's old look covers it first, so nothing of the new look shows early (not even the material
/// Windows switches at once behind a see-through window); the theme changes beneath the picture, and then the new
/// look spreads from what the user picked it with: a widening circle with a soft edge, faintly lit in the accent.
/// Every frame is drawn on the render thread. Where the reveal doesn't apply (animations off, a theme the user didn't
/// pick in this window, or no picture of the window to be had), the theme simply changes.
/// </summary>
public static partial class ThemeReveal
{
    // A pick is followed at once by the theme changing, in the same click.
    private static readonly TimeSpan PickedFor = TimeSpan.FromSeconds(1);
    // The longest the theme waits for the picture to cover the window, and then for that frame to reach the screen:
    // a frame or two at most, unless the window draws only a few frames a second (a locked or remote session).
    private static readonly TimeSpan CoverWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShownWait = TimeSpan.FromMilliseconds(400);
    // Whatever happens on the render thread, the picture is gone well after the reveal would have ended.
    private const int DeadlineInDurations = 5;

    /// <summary>Marks a button that picks a theme: the new look spreads from it.</summary>
    public static readonly AttachedProperty<bool> OriginProperty =
        AvaloniaProperty.RegisterAttached<Button, bool>("Origin", typeof(ThemeReveal));

    private static readonly ConditionalWeakTable<TopLevel, WindowReveals> RevealsByWindow = new();
    private static Picked? s_pick;

    static ThemeReveal()
    {
        OriginProperty.Changed.AddClassHandler<Button>(static (button, e) =>
        {
            button.Click -= OnOriginClick;
            if (e.NewValue is true)
                button.Click += OnOriginClick;
        });
    }

    public static bool GetOrigin(Button button) => button.GetValue(OriginProperty);
    public static void SetOrigin(Button button, bool value) => button.SetValue(OriginProperty, value);

    // A button raises Click before it runs its command, so the pick is known by the time the theme changes.
    private static void OnOriginClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Visual origin)
            Pick(origin);
    }

    /// <summary>Notes that the user is picking a theme with <paramref name="origin"/>: the new look spreads from its centre.</summary>
    internal static void Pick(Visual origin)
    {
        if (TopLevel.GetTopLevel(origin) is not { } window)
            return;
        var place = MainWindow.InWindow(origin, window);
        if (place.Width > 0 && place.Height > 0)
            s_pick = new Picked(new WeakReference<TopLevel>(window), place.Center, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Changes <paramref name="window"/> to the look the user picked by calling <paramref name="apply"/>: through the
    /// reveal when they just picked it in this window, otherwise at once. <paramref name="dark"/> is the new look.
    /// </summary>
    internal static void Apply(Window window, bool dark, Action apply)
    {
        var reveals = RevealsByWindow.GetOrCreateValue(window);
        var change = ++reveals.Change;
        if (TakePick(window) is not { } origin || !ManagementMotion.IsEnabledFor(window)
            || !window.IsVisible || window.WindowState == WindowState.Minimized
            || OverlayLayer.GetOverlayLayer(window) is not { } layer)
        {
            reveals.Clear();
            apply();
            return;
        }

        var host = new RevealHost(window, layer);
        // On Windows, with Lumi in front, the screen holds exactly what the window shows, its material included.
        // Otherwise the cover's frame reads back what Lumi drew beneath it, and stands it on the old look's
        // background where the window is see-through.
        var screen = OperatingSystem.IsWindows() ? TakeScreenPicture(window) : null;
        var background = screen is null
            ? ToSkia(ResolveColor(window, "Color.Background", window.ActualThemeVariant, Colors.Black))
            : SKColors.Transparent;
        var theme = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var size = new Size(host.Width, host.Height);
        var endRadius = EndRadius(size, origin);
        var reveal = new Reveal(
            background, size, window.RenderScaling, host.WindowToHost, origin, endRadius, DurationFor(endRadius), dark,
            ToSkia(ResolveColor(window, "Color.AccentDefault", theme, Color.FromRgb(0x81, 0x8C, 0xF8))),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        RevealOnceCovered(window, reveals, change, host, host.Cover(reveal, screen), reveal, apply);
    }

    internal static Point? TakePick(TopLevel window)
    {
        if (s_pick is not { } pick || !pick.Window.TryGetTarget(out var picked))
        {
            s_pick = null;
            return null;
        }
        // Another window's pick waits for that window's own change.
        if (!ReferenceEquals(picked, window))
            return null;
        s_pick = null;
        return Stopwatch.GetElapsedTime(pick.At) < PickedFor ? pick.Origin : null;
    }

    private static async void RevealOnceCovered(
        Window window, WindowReveals reveals, int change, RevealHost host, Task<bool> covering, Reveal reveal, Action apply)
    {
        var covered = await Task.WhenAny(covering, Task.Delay(CoverWait)) == covering && covering.Result;
        if (covered)
        {
            // The frame that drew the cover reaches the screen before anything changes beneath it.
            var shown = host.NextFrameRendered();
            await Task.WhenAny(shown, Task.Delay(ShownWait));
        }

        // A later change of theme takes over from this one.
        if (reveals.Change != change)
        {
            host.Remove();
            return;
        }
        // The cover holds whatever an earlier reveal still showed, so that one can go.
        reveals.Clear();
        if (!covered || !window.IsVisible)
        {
            host.Remove();
            apply();
            return;
        }

        reveals.Active = host;
        apply();
        host.Go();
        await Task.WhenAny(reveal.Done.Task, Task.Delay(reveal.Duration * DeadlineInDurations));
        if (reveals.Active == host)
            reveals.Active = null;
        host.Remove();
    }

    private static Color ResolveColor(StyledElement element, string key, ThemeVariant theme, Color fallback)
        => element.TryFindResource(key, theme, out var value) && value is Color color ? color : fallback;

    private static SKColor ToSkia(Color color) => new(color.R, color.G, color.B, color.A);

    private sealed record Picked(WeakReference<TopLevel> Window, Point Origin, long At);

    /// <summary>The reveal a window is playing, and how many times its theme has changed.</summary>
    private sealed class WindowReveals
    {
        public RevealHost? Active;
        public int Change;

        public void Clear()
        {
            Active?.Remove();
            Active = null;
        }
    }

    /// <summary>
    /// Stands over the whole window, topmost in its overlay layer, holding the reveal's render-thread drawing: in place
    /// before the theme changes, so it can cover the window with the old look and knows its way from the window's space.
    /// </summary>
    private sealed class RevealHost : Control
    {
        private readonly Canvas _layer;
        private readonly CompositionCustomVisual? _visual;

        public RevealHost(Window window, Canvas layer)
        {
            _layer = layer;
            IsHitTestVisible = false;
            Width = window.ClientSize.Width;
            Height = window.ClientSize.Height;
            layer.Children.Add(this);
            UpdateLayout();
            WindowToHost = window.TransformToVisual(this) ?? Matrix.Identity;

            if (ElementComposition.GetElementVisual(this) is { } host)
            {
                _visual = host.Compositor.CreateCustomVisual(new RevealHandler());
                _visual.Size = new Vector(Width, Height);
                ElementComposition.SetElementChildVisual(this, _visual);
            }
        }

        /// <summary>From the window's space into the host's: a mirror, where the host is drawn right to left.</summary>
        public Matrix WindowToHost { get; }

        /// <summary>
        /// Covers the window with <paramref name="picture"/> of its old look from the next frame, or, without one,
        /// with the picture that frame takes of what it draws beneath the host. Completes once a frame has drawn the
        /// cover, with whether it could.
        /// </summary>
        public Task<bool> Cover(Reveal reveal, SKImage? picture)
        {
            if (_visual is null)
            {
                picture?.Dispose();
                return Task.FromResult(false);
            }
            var covered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _visual.SendHandlerMessage(new CoverRequest(reveal, picture, covered));
            return covered.Task;
        }

        /// <summary>Completes once the next frame of the window has been drawn.</summary>
        public Task NextFrameRendered()
            => ElementComposition.GetElementVisual(this)?.Compositor.RequestCompositionBatchCommitAsync().Rendered ?? Task.CompletedTask;

        /// <summary>Starts the reveal, from the frame that first shows the new look beneath the cover.</summary>
        public void Go() => _visual?.SendHandlerMessage(RevealHandler.Start);

        public void Remove()
        {
            _visual?.SendHandlerMessage(RevealHandler.Release);
            ElementComposition.SetElementChildVisual(this, null);
            _layer.Children.Remove(this);
        }
    }
}
