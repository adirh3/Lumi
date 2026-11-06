using System;
using System.Linq;
using System.Numerics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.ViewModels;
using StrataTheme;
using StrataTheme.Animation;

namespace Lumi.Views;

/// <summary>
/// The What's new overlay. It arrives in steps on the render thread (the card rises, the Strata line draws across, the
/// spotlight and the tiles follow, and a notification lands on the pictured phone) and fades away before the shell
/// lets it go. Narrow windows get one column of tiles and no phone.
/// </summary>
public partial class WhatsNewView : UserControl
{
    private static readonly Easing SettleEasing = new CubicEaseOut();
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(170);
    private const double CompactWidth = 620;
    private const int StaggerLimit = 6;

    private readonly Control? _root;
    private readonly Control? _scrim;
    private readonly Control? _cardHost;
    private readonly Control? _card;
    private readonly Control? _header;
    private readonly Control? _stratum;
    private readonly Control? _spotlight;
    private readonly Control? _highlightsSection;
    private readonly ItemsControl? _highlights;
    private readonly ScrollViewer? _scroll;
    private TopLevel? _window;

    public WhatsNewView()
    {
        AvaloniaXamlLoader.Load(this);
        _root = this.FindControl<Control>("WhatsNewRoot");
        _scrim = this.FindControl<Control>("WhatsNewScrim");
        _cardHost = this.FindControl<Control>("WhatsNewCardHost");
        _card = this.FindControl<Control>("WhatsNewCard");
        _header = this.FindControl<Control>("WhatsNewHeader");
        _stratum = this.FindControl<Control>("WhatsNewStratum");
        _spotlight = this.FindControl<Control>("WhatsNewSpotlight");
        _highlightsSection = this.FindControl<Control>("WhatsNewHighlightsSection");
        _highlights = this.FindControl<ItemsControl>("WhatsNewHighlights");
        _scroll = this.FindControl<ScrollViewer>("WhatsNewScroll");

        if (_cardHost is not null)
            _cardHost.SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
        if (_highlights is not null)
            _highlights.SizeChanged += (_, e) => LayoutTiles(e.NewSize.Width);
        if (_scroll is not null)
        {
            _scroll.ScrollChanged += (_, _) => UpdateEdgeFades();
            _scroll.SizeChanged += (_, _) => UpdateEdgeFades();
        }
    }

    /// <summary>Whether the view is fading away; reopening What's new then builds a new one.</summary>
    public bool IsClosing { get; private set; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this);
        _window?.AddHandler(GotFocusEvent, OnWindowGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _window?.RemoveHandler(GotFocusEvent, OnWindowGotFocus);
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// What's new is modal: focus that lands behind it (the window focusing its composer as Lumi arrives, say), or on
    /// the overlay outside its card, comes back to the card, so nothing typed reaches a chat the user can't see.
    /// </summary>
    private void OnWindowGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (IsClosing || !IsEffectivelyVisible || e.Source is not Visual target
            || ReferenceEquals(target, _card) || _card?.IsVisualAncestorOf(target) == true)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!IsClosing && IsEffectivelyVisible)
                _card?.Focus();
        }, DispatcherPriority.Input);
    }

    /// <summary>
    /// Brings What's new in and focuses the card itself rather than a button: Escape closes it and Tab reaches its
    /// actions, while a Space or Enter typed as it arrives (the user was writing a message) presses nothing. With
    /// animations off it simply appears.
    /// </summary>
    public void PlayEntrance(bool animate)
    {
        if (animate)
        {
            // Hidden until the first animated frame, so the finished card never flashes before it rises.
            SetOpacity(_scrim, 0);
            SetOpacity(_cardHost, 0);
        }

        Dispatcher.UIThread.Post(() =>
        {
            _scroll?.ScrollToHome();
            _card?.Focus();
            SetOpacity(_scrim, 1);
            SetOpacity(_cardHost, 1);
            if (animate)
                Animate();
        }, DispatcherPriority.Render);
    }

    /// <summary>Fades What's new away, then calls <paramref name="done"/>; at once with animations off.</summary>
    public void PlayExit(bool animate, Action done)
    {
        if (IsClosing)
            return;
        IsClosing = true;

        if (!animate || _root is null || ElementComposition.GetElementVisual(_root) is not { Compositor: { } compositor } root)
        {
            done();
            return;
        }

        var fade = compositor.CreateStableScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 1f);
        fade.InsertKeyFrame(1f, 0f, SettleEasing);
        fade.Duration = ExitDuration;
        root.StartAnimation(nameof(CompositionVisual.Opacity), fade);

        if (_cardHost is not null && ElementComposition.GetElementVisual(_cardHost) is { } card)
        {
            card.CenterPoint = new Vector3((float)_cardHost.Bounds.Width / 2f, (float)_cardHost.Bounds.Height / 2f, 0f);
            var shrink = compositor.CreateStableVector3KeyFrameAnimation();
            shrink.InsertKeyFrame(0f, Vector3.One);
            shrink.InsertKeyFrame(1f, new Vector3(0.975f, 0.975f, 1f), SettleEasing);
            shrink.Duration = ExitDuration;
            card.StartAnimation(nameof(CompositionVisual.Scale), shrink);
        }

        DispatcherTimer.RunOnce(done, ExitDuration + TimeSpan.FromMilliseconds(20), DispatcherPriority.Normal);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            Dismiss();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void OnScrimPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Dismiss();
        e.Handled = true;
    }

    // Keeps clicks on the card from reaching the window behind it.
    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void Dismiss()
    {
        if (DataContext is WhatsNewViewModel { DismissCommand: { } dismiss } && dismiss.CanExecute(null))
            dismiss.Execute(null);
    }

    private void ApplyWidth(double width)
    {
        var compact = width > 0 && width < CompactWidth;
        if (_root is null || _root.Classes.Contains("compact") == compact)
            return;

        _root.Classes.Set("compact", compact);
        if (_highlights is not null)
            LayoutTiles(_highlights.Bounds.Width);
    }

    /// <summary>Two tiles a row (one in a narrow window); a row is as tall as its taller tile.</summary>
    private void LayoutTiles(double width)
    {
        if (width <= 0 || _highlights?.ItemsPanelRoot is not WrapPanel panel)
            return;

        var columns = _root?.Classes.Contains("compact") == true ? 1 : 2;
        panel.ItemWidth = Math.Floor((width - panel.ItemSpacing * (columns - 1)) / columns);
    }

    private void UpdateEdgeFades()
    {
        if (_scroll is null)
            return;

        var hidden = _scroll.Extent.Height - _scroll.Viewport.Height;
        _scroll.Classes.Set("fade-above", hidden > 1 && _scroll.Offset.Y > 1);
        _scroll.Classes.Set("fade-below", hidden > 1 && _scroll.Offset.Y < hidden - 1);
    }

    private void Animate()
    {
        FadeIn(_scrim, TimeSpan.FromMilliseconds(220), TimeSpan.Zero);
        Rise(_cardHost);
        DrawLine(_stratum);

        var index = 0;
        foreach (var section in Sections())
        {
            var delay = TimeSpan.FromMilliseconds(70 + 55 * Math.Min(index++, StaggerLimit));
            SlideFadeEntrance.PlayComposited(section, 14, TimeSpan.FromMilliseconds(380), delay);
        }

        // An earlier notification is already on the lock screen; the new one drops in above it.
        if (FindNamed("WhatsNewPhoneNotificationEarlier") is { } earlier)
            SlideFadeEntrance.PlayComposited(earlier, 10, TimeSpan.FromMilliseconds(360), TimeSpan.FromMilliseconds(320));
        if (FindNamed("WhatsNewPhoneNotification") is { } notification)
            SlideFadeEntrance.PlayComposited(notification, -26, TimeSpan.FromMilliseconds(520), TimeSpan.FromMilliseconds(620));
    }

    /// <summary>The parts that cascade in after the card: the header, the spotlight, then each tile.</summary>
    private System.Collections.Generic.IEnumerable<Control> Sections()
    {
        if (_header is not null)
            yield return _header;
        if (_spotlight is { IsVisible: true })
            yield return _spotlight;
        if (_highlightsSection is not { IsVisible: true } || _highlights is null)
            yield break;

        foreach (var tile in _highlights.GetRealizedContainers())
            yield return tile;
    }

    private Control? FindNamed(string name)
        => this.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == name && control.IsEffectivelyVisible);

    private static void SetOpacity(Control? control, double opacity)
    {
        if (control is not null)
            control.Opacity = opacity;
    }

    private static void FadeIn(Control? target, TimeSpan duration, TimeSpan delay)
    {
        if (target is null || ElementComposition.GetElementVisual(target) is not { Compositor: { } compositor } visual)
            return;

        var fade = compositor.CreateStableScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, SettleEasing);
        fade.Duration = duration;
        fade.DelayTime = delay;
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation(nameof(CompositionVisual.Opacity), fade);
    }

    private static void Rise(Control? card)
    {
        if (card is null || ElementComposition.GetElementVisual(card) is not { Compositor: { } compositor } visual)
            return;

        visual.CenterPoint = new Vector3((float)card.Bounds.Width / 2f, (float)card.Bounds.Height / 2f, 0f);
        var duration = TimeSpan.FromMilliseconds(440);

        var scale = compositor.CreateStableVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3(0.96f, 0.96f, 1f));
        scale.InsertKeyFrame(1f, Vector3.One, SettleEasing);
        scale.Duration = duration;
        visual.StartAnimation(nameof(CompositionVisual.Scale), scale);

        var rise = compositor.CreateStableVector3DKeyFrameAnimation();
        rise.InsertKeyFrame(0f, new Vector3D(0, 22, 0));
        rise.InsertKeyFrame(1f, default, SettleEasing);
        rise.Duration = duration;
        visual.StartAnimation(nameof(CompositionVisual.Translation), rise);

        FadeIn(card, TimeSpan.FromMilliseconds(240), TimeSpan.Zero);
    }

    private static void DrawLine(Control? line)
    {
        if (line is null || ElementComposition.GetElementVisual(line) is not { Compositor: { } compositor } visual)
            return;

        visual.CenterPoint = new Vector3(0f, (float)line.Bounds.Height / 2f, 0f);
        var draw = compositor.CreateStableVector3KeyFrameAnimation();
        draw.InsertKeyFrame(0f, new Vector3(0f, 1f, 1f));
        draw.InsertKeyFrame(1f, Vector3.One, SettleEasing);
        draw.Duration = TimeSpan.FromMilliseconds(680);
        draw.DelayTime = TimeSpan.FromMilliseconds(140);
        draw.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation(nameof(CompositionVisual.Scale), draw);
    }
}
