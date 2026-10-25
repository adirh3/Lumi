using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Lumi.Services;
using Lumi.ViewModels;

namespace Lumi.Views;

/// <summary>
/// How Lumi's window takes over from the loading window when Lumi starts on a welcome screen: the welcome tour on
/// a first start, or a new chat. Lumi's window opens when it always has, but cloaked: out of sight while it lays
/// out and draws. Meanwhile the loading window's mark comes to rest, a card fades in over the loading window and
/// grows into the size and place of Lumi's window, and Lumi's window comes into sight beneath it. The card clears,
/// and the mark glides into the welcome logo as the window fills in. Every motion runs on the render thread. Where
/// the hand-off doesn't apply (another first screen, animations off, the user no longer watching the loading
/// window, the windows on screens of different scale, or not Windows), Lumi's window simply replaces the loading
/// window.
/// </summary>
internal static class StartupHandOff
{
    private static readonly TimeSpan GatherTime = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan CardFadeIn = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ExpandTime = TimeSpan.FromMilliseconds(440);
    // The card has all but finished growing by then: its growth slows to a stop.
    private static readonly TimeSpan ArriveAfter = TimeSpan.FromMilliseconds(290);
    private static readonly TimeSpan CardFadeTime = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SystemTransitionsBack = TimeSpan.FromMilliseconds(1600);
    // A frame or two, for what one window drew to reach the screen before another window changes.
    private static readonly TimeSpan OnScreen = TimeSpan.FromMilliseconds(34);
    // The longest a step waits for a window to draw.
    private static readonly TimeSpan FrameWait = TimeSpan.FromSeconds(2);
    // Whatever happens, Lumi's window is in sight by then.
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(6);
    private const double CardRadius = 8;
    private const int ShadowRoom = 64;

    /// <summary>Shows <paramref name="main"/> in place of <paramref name="loading"/>, through the hand-off when it applies.</summary>
    internal static void Show(StartupWindow loading, MainWindow main)
    {
        if (TryBegin(loading, main))
            return;

        main.Show();
        Dispatcher.UIThread.Post(loading.Close, DispatcherPriority.Background);
    }

    /// <summary>Whether Lumi's window takes over through the hand-off.</summary>
    internal static bool Applies(bool isWindows, bool animate, bool opensOnWelcome, WindowState state, double loadingScale, double windowScale)
        => isWindows && animate && opensOnWelcome && state != WindowState.Minimized && Math.Abs(loadingScale - windowScale) < 0.01;

    /// <summary>The card's growth at progress <paramref name="q"/>: quick to start, gentle to settle.</summary>
    internal static double Grow(double q) => 1 - Math.Pow(1 - Math.Clamp(q, 0, 1), 4);

    private static bool TryBegin(StartupWindow loading, MainWindow main)
    {
        // The welcome tour on a first start, or a new chat.
        var opensOnWelcome = main.DataContext is MainViewModel { IsOnboarded: false } or MainViewModel { ChatVM.IsWelcomeVisible: true };
        // Only while the user is watching the loading window, not once they moved on to another app or minimized
        // it: the card is drawn above everything.
        if (!OperatingSystem.IsWindows() || !loading.IsAnimated || !opensOnWelcome || !IsWatched(loading, main))
            return false;

        var window = main.PrepareStartupBounds(out var scale);
        if (!Applies(isWindows: true, animate: true, opensOnWelcome, main.WindowState, loading.RenderScaling, scale))
            return false;

        // Lumi's window opens now, as it always has, but out of sight until the card has grown into it.
        if (!WindowChromeInterop.SetCloaked(main, true))
            return false;

        new HandOff(loading, main, window, scale).Begin();
        return true;
    }

    /// <summary>
    /// Whether the user is watching the loading window: it isn't minimized and is still the active window, or Lumi's
    /// window is, which takes activation as it shows maximized.
    /// </summary>
    private static bool IsWatched(StartupWindow loading, MainWindow main)
        => loading is { IsVisible: true, WindowState: WindowState.Normal } && (loading.IsActive || main.IsActive);

    private sealed class HandOff(StartupWindow loading, MainWindow main, PixelRect window, double scale)
    {
        private HandOffWindow? _card;
        private PixelRect _mark;
        private bool _revealed;

        public void Begin()
        {
            WindowChromeInterop.SetSystemTransitionsDisabled(main, true);
            main.ShowActivated = false;
            try
            {
                main.Show();
            }
            catch
            {
                main.ShowActivated = true;
                WindowChromeInterop.SetCloaked(main, false);
                throw;
            }

            Later(Abandon, Deadline);
            // Every window draws on one thread, and a first frame is heavy: the hand-off moves once Lumi's window
            // has drawn its first frame out of sight, and once the loading window has put itself together.
            When(Rendered(main), FrameWait, () =>
            {
                var wait = StartupMotion.LoopStart - loading.SinceOpened;
                Later(Gather, wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
            });
        }

        private void Gather()
        {
            if (_revealed)
                return;
            // If the user moved on in the meantime, Lumi's window simply comes into sight.
            if (!IsWatched(loading, main))
            {
                Arrive();
                return;
            }
            _mark = loading.MarkOnScreen;
            loading.Settle(GatherTime);

            // The card comes into sight over the loading window, standing exactly where it stands, while the
            // mark comes to rest.
            var dark = loading.ActualThemeVariant == ThemeVariant.Dark;
            var start = loading.ContentOnScreen;
            var area = Union(start, window, ShadowRoom);
            var fill = loading.TryFindResource("Color.Background", loading.ActualThemeVariant, out var value) && value is Color color
                ? color
                : dark ? Color.FromRgb(0x16, 0x16, 0x16) : Color.FromRgb(0xF5, 0xF5, 0xF5);
            var card = new CardMessage(
                From: Local(start, area), To: Local(window, area),
                FromRadius: CardRadius, ToRadius: main.WindowState == WindowState.Maximized ? 0 : CardRadius,
                Duration: ExpandTime,
                Fill: new ImmutableSolidColorBrush(fill),
                Edge: new ImmutablePen(new ImmutableSolidColorBrush(dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1C, 0, 0, 0)), 1),
                Shadow: BoxShadows.Parse(dark ? "0 2 6 0 #33000000, 0 14 40 0 #66000000" : "0 2 6 0 #1A000000, 0 14 40 0 #33000000"),
                Bounds: new Rect(0, 0, area.Width / scale, area.Height / scale));

            var handOffWindow = new HandOffWindow(area, scale, card, Local(_mark, area), loading.FindResource(AppIcon.MarkResourceKey) as IImage);
            if (handOffWindow.ActualTransparencyLevel != WindowTransparencyLevel.Transparent)
            {
                // Without a see-through card, Lumi's window simply replaces the loading window.
                handOffWindow.Close();
                Arrive();
                return;
            }

            WindowChromeInterop.SetSystemTransitionsDisabled(handOffWindow, true);
            _card = handOffWindow;
            handOffWindow.Show();
            // It comes into sight once it has drawn and the mark beneath it has come to rest: its own mark takes
            // over unseen, and the card fades in around it.
            When(Task.WhenAll(Rendered(handOffWindow), Task.Delay(GatherTime + OnScreen)), FrameWait, () =>
            {
                handOffWindow.FadeIn(CardFadeIn);
                Later(Expand, CardFadeIn);
            });
        }

        private void Expand()
        {
            if (_revealed || _card is not { } card)
                return;
            // With the card in sight over it, the loading window goes out of sight, wherever the card grows.
            WindowChromeInterop.SetCloaked(loading, true);
            card.Grow();
            // Lumi's window, still out of sight, readies its arrival in good time to have drawn it.
            main.PrepareArrival(_mark);
            Later(Arrive, ArriveAfter);
        }

        private void Arrive()
        {
            if (_revealed)
                return;
            // Under the full-grown card Lumi's window comes into sight in its place. Then the card clears around
            // the mark, its mark makes way for Lumi's own beneath, and that one glides into the welcome logo.
            if (_card is { } card)
            {
                Later(() => card.FadeOut(CardFadeTime), OnScreen);
                Later(CloseCard, OnScreen + CardFadeTime + OnScreen);
                Later(() => main.PlayArrival(), OnScreen + CardFadeTime + OnScreen + OnScreen);
                Reveal();
                return;
            }
            Reveal();
            main.FocusFirstScreen();
        }

        /// <summary>Brings Lumi's window into sight and in charge, and closes the loading window behind it.</summary>
        private void Reveal()
        {
            if (_revealed)
                return;
            _revealed = true;
            WindowChromeInterop.SetCloaked(main, false);
            main.ShowActivated = true;
            main.Activate();
            Dispatcher.UIThread.Post(loading.Close, DispatcherPriority.Background);
            Later(() => WindowChromeInterop.SetSystemTransitionsDisabled(main, false), SystemTransitionsBack);
        }

        private void Abandon()
        {
            if (_revealed)
                return;
            Trace.TraceWarning("[Startup] Lumi's window took too long to take over from the loading window.");
            CloseCard();
            Reveal();
            if (!main.PlayArrival())
                main.FocusFirstScreen();
        }

        private void Fail(Exception ex)
        {
            Trace.TraceWarning($"[Startup] The hand-off to Lumi's window failed: {ex.Message}");
            CloseCard();
            Reveal();
            if (!main.PlayArrival())
                main.FocusFirstScreen();
        }

        private void CloseCard()
        {
            _card?.Close();
            _card = null;
        }

        private void Later(Action step, TimeSpan delay)
            => DispatcherTimer.RunOnce(() => Run(step), delay, DispatcherPriority.Normal);

        private void When(Task drawn, TimeSpan timeout, Action step)
            => Task.WhenAny(drawn, Task.Delay(timeout)).ContinueWith(
                _ => Dispatcher.UIThread.Post(() => Run(step), DispatcherPriority.Normal), TaskScheduler.Default);

        private void Run(Action step)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        /// <summary>Completes once the window of <paramref name="visual"/> has drawn what it shows now.</summary>
        private static Task Rendered(Visual visual)
            => ElementComposition.GetElementVisual(visual)?.Compositor.RequestCompositionBatchCommitAsync().Rendered ?? Task.CompletedTask;

        private Rect Local(PixelRect rect, PixelRect area)
            => new((rect.X - area.X) / scale, (rect.Y - area.Y) / scale, rect.Width / scale, rect.Height / scale);

        private static PixelRect Union(PixelRect a, PixelRect b, int room)
        {
            var left = Math.Min(a.X, b.X) - room;
            var top = Math.Min(a.Y, b.Y) - room;
            return new PixelRect(left, top, Math.Max(a.Right, b.Right) + room - left, Math.Max(a.Bottom, b.Bottom) + room - top);
        }
    }

    /// <summary>A see-through window above everything, for as long as the card grows.</summary>
    private sealed class HandOffWindow : Window
    {
        private readonly Panel _root = new() { IsHitTestVisible = false };
        private readonly Border _cardHost = new();
        private readonly CardMessage _card;
        private CompositionCustomVisual? _cardVisual;

        public HandOffWindow(PixelRect area, double scale, CardMessage card, Rect mark, IImage? markImage)
        {
            _card = card;
            WindowDecorations = WindowDecorations.None;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Background = Brushes.Transparent;
            TransparencyBackgroundFallback = Brushes.Transparent;
            CanResize = false;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Title = "Lumi";
            FlowDirection = FlowDirection.LeftToRight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = area.Position;
            Width = area.Width / scale;
            Height = area.Height / scale;

            _root.Children.Add(_cardHost);
            if (markImage is not null)
            {
                var image = new Image
                {
                    Source = markImage,
                    Width = mark.Width,
                    Height = mark.Height,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(mark.X, mark.Y, 0, 0),
                };
                RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
                _root.Children.Add(image);
            }
            Content = _root;
        }

        protected override void OnOpened(EventArgs e)
        {
            // Before the first frame: the card stands exactly over the loading window, and comes into sight there.
            if (ElementComposition.GetElementVisual(_cardHost) is { } host)
            {
                _cardVisual = host.Compositor.CreateCustomVisual(new CardHandler());
                _cardVisual.Size = new Vector(_card.Bounds.Width, _card.Bounds.Height);
                ElementComposition.SetElementChildVisual(_cardHost, _cardVisual);
                _cardVisual.SendHandlerMessage(_card);
            }
            // Out of sight until FadeIn.
            StartupMotion.Fade(_root, _ => 0, StartupMotion.Timing.Once(100));
            base.OnOpened(e);
        }

        // Avalonia fades a group by fading each thing drawn in it, so the mark is never faded with the card it
        // stands on: either would show through the other.

        /// <summary>Brings the card into sight: its mark at once, over the identical one beneath it, and the card
        /// fading in around it.</summary>
        public void FadeIn(TimeSpan duration)
        {
            StartupMotion.Fade(_cardHost, q => q, new StartupMotion.Timing(duration, TimeSpan.Zero));
            StartupMotion.Fade(_root, _ => 1, StartupMotion.Timing.Once(16));
        }

        /// <summary>Clears the card around its mark, which stays until the window closes.</summary>
        public void FadeOut(TimeSpan duration)
            => StartupMotion.Fade(_cardHost, q => 1 - q, new StartupMotion.Timing(duration, TimeSpan.Zero));

        /// <summary>Grows the card into Lumi's window, on the render thread from its next frame.</summary>
        public void Grow() => _cardVisual?.SendHandlerMessage(CardHandler.StartGrowing);
    }

    private sealed record CardMessage(
        Rect From, Rect To, double FromRadius, double ToRadius, TimeSpan Duration,
        IImmutableBrush Fill, ImmutablePen Edge, BoxShadows Shadow, Rect Bounds);

    /// <summary>Draws the card on the render thread, growing from where it starts to where it ends.</summary>
    private sealed class CardHandler : CompositionCustomVisualHandler
    {
        public static readonly object StartGrowing = new();
        private CardMessage? _card;
        private bool _growing;
        private long _startedAt;
        private double _progress;

        public override void OnMessage(object message)
        {
            if (message is CardMessage card)
            {
                _card = card;
                Invalidate();
            }
            else if (message == StartGrowing && !_growing)
            {
                _growing = true;
                RegisterForNextAnimationFrameUpdate();
            }
        }

        public override void OnAnimationFrameUpdate()
        {
            if (_card is null)
                return;
            var now = Stopwatch.GetTimestamp();
            if (_startedAt == 0)
                _startedAt = now;
            _progress = Math.Min(1, Stopwatch.GetElapsedTime(_startedAt, now) / _card.Duration);
            Invalidate();
            if (_progress < 1)
                RegisterForNextAnimationFrameUpdate();
        }

        public override void OnRender(ImmediateDrawingContext context)
        {
            if (_card is not { } card)
                return;
            var t = Grow(_progress);
            var rect = new Rect(
                card.From.X + (card.To.X - card.From.X) * t,
                card.From.Y + (card.To.Y - card.From.Y) * t,
                card.From.Width + (card.To.Width - card.From.Width) * t,
                card.From.Height + (card.To.Height - card.From.Height) * t);
            var radius = card.FromRadius + (card.ToRadius - card.FromRadius) * t;
            context.DrawRectangle(card.Fill, card.Edge, rect, radius, radius, card.Shadow);
        }

        public override Rect GetRenderBounds() => _card?.Bounds ?? default;
    }
}
