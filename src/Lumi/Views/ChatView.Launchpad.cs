using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.ViewModels;
using StrataTheme.Animation;

namespace Lumi.Views;

/// <summary>
/// The view side of the new-chat launchpad: it keeps the launchpad observing only while it is on
/// screen, fits it to short windows, and composes it in when the welcome state appears.
/// </summary>
public partial class ChatView
{
    /// <summary>
    /// Below this much room for the whole welcome state the launchpad goes compact: no mark, and four starters.
    /// It is read from the welcome panel, which the compact layout doesn't change, so it can't flip back and forth.
    /// </summary>
    private const double LaunchpadCompactHeight = 680;

    /// <summary>Fades the launchpad's lower edge while more of it lies below, so the cut reads as scrollable.</summary>
    private static readonly IImmutableBrush LaunchpadOverflowMask = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Colors.Black, 0),
            new GradientStop(Colors.Black, 0.9),
            new GradientStop(Colors.Transparent, 1),
        },
    }.ToImmutable();

    private ScrollViewer? _welcomeScroller;
    private Panel? _welcomePanel;
    private StackPanel? _welcomeContent;
    private StackPanel? _composerContainer;
    private Control? _launchpadHero;
    private Control? _launchpadIdeas;
    private Control? _launchpadStarters;
    private Control? _launchpadSetupRow;
    private Panel? _launchpadSetupRail;
    private ScrollViewer? _launchpadSetupScroller;
    private ItemsControl? _launchpadSetupTiles;
    private TextBlock? _launchpadIdeaReason;
    private Control? _launchpadNeedsYouCard;
    private Control? _launchpadOtherCard;
    private TextBox? _launchpadOtherSearch;
    private LaunchpadViewModel? _activeLaunchpad;

    /// <summary>Where the setups rail is gliding to, while <see cref="_launchpadRailGliding"/>.</summary>
    private double _launchpadRailTarget;
    private bool _launchpadRailGliding;
    private TimeSpan? _launchpadRailLastFrame;
    private (bool Back, bool Forward, double Width) _launchpadRailEdges;

    private void InitializeLaunchpad()
    {
        _welcomeScroller = this.FindControl<ScrollViewer>("WelcomeScroller");
        _welcomePanel = this.FindControl<Panel>("WelcomePanel");
        _welcomeContent = this.FindControl<StackPanel>("WelcomeGreeting");
        _composerContainer = this.FindControl<StackPanel>("ComposerContainer");
        _launchpadHero = this.FindControl<Control>("LaunchpadHero");
        _launchpadIdeas = this.FindControl<Control>("LaunchpadIdeas");
        _launchpadStarters = this.FindControl<Control>("WelcomeSuggestions");
        _launchpadSetupRow = this.FindControl<Control>("LaunchpadSetupRow");
        _launchpadSetupRail = this.FindControl<Panel>("LaunchpadSetupRail");
        _launchpadSetupScroller = this.FindControl<ScrollViewer>("LaunchpadSetupScroller");
        _launchpadSetupTiles = this.FindControl<ItemsControl>("LaunchpadSetupTiles");
        _launchpadIdeaReason = this.FindControl<TextBlock>("LaunchpadIdeaReason");
        _launchpadNeedsYouCard = this.FindControl<Control>("LaunchpadNeedsYouCard");
        _launchpadOtherCard = this.FindControl<Control>("LaunchpadOtherCard");
        _launchpadOtherSearch = this.FindControl<TextBox>("LaunchpadOtherSearch");

        if (_composerContainer is not null)
            _composerContainer.SizeChanged += (_, _) => ReserveComposerSpaceForLaunchpad();

        if (_welcomeScroller is not null)
        {
            _welcomeScroller.SizeChanged += (_, _) => UpdateLaunchpadOverflowFade();
            _welcomeScroller.ScrollChanged += (_, _) => UpdateLaunchpadOverflowFade();
        }

        if (_launchpadSetupScroller is not null)
        {
            _launchpadSetupScroller.ScrollChanged += (_, _) => UpdateLaunchpadRailEdges();
            _launchpadSetupScroller.SizeChanged += (_, _) => UpdateLaunchpadRailEdges();
        }

        // The wheel (or a touchpad's sideways swipe) over the setups moves them along, when more lie either way.
        _launchpadSetupRail?.AddHandler(PointerWheelChangedEvent, OnLaunchpadRailWheel, RoutingStrategies.Tunnel);

        // The panel's own size decides compact: a child's resize fires before the panel's new size is known.
        if (_welcomePanel is not null)
            _welcomePanel.SizeChanged += (_, e) => UpdateLaunchpadFit(e.NewSize.Height);

        if (this.FindControl<Button>("LaunchpadOtherButton")?.Flyout is PopupFlyoutBase otherFlyout)
        {
            otherFlyout.Opening += (_, _) => _activeLaunchpad?.OpenOtherCommand.Execute(null);
            otherFlyout.Opened += (_, _) => Dispatcher.UIThread.Post(() => _launchpadOtherSearch?.Focus(), DispatcherPriority.Loaded);
        }

        // Enter picks the first match, so "Other, type a few letters, Enter" starts there without the mouse.
        _launchpadOtherSearch?.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && _activeLaunchpad is { } launchpad)
            {
                e.Handled = true;
                launchpad.SelectFirstOther();
            }
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>A starter went into the composer: focus it and walk the user through its blanks.</summary>
    private void OnComposerFillInRequested(IReadOnlyList<string> blanks) => _composer?.BeginFillIn(blanks);

    /// <summary>A setup's ⋯ opens the menu its right-click opens, on the setup itself.</summary>
    private void OnLaunchpadSetupMoreClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Parent: Panel host }
            && host.Children.OfType<Button>().FirstOrDefault(static button => button.Classes.Contains("launchpad-setup"))
                is { ContextMenu: { } menu } setup)
        {
            menu.Open(setup);
        }
    }

    private void OnLaunchpadChatOpening() => CloseLaunchpadCard(_launchpadNeedsYouCard);

    private void OnLaunchpadOtherPicked() => CloseLaunchpadCard(_launchpadOtherCard);

    /// <summary>
    /// Closes a launchpad card through its popup, as Escape does, so it fades out as it looked. A flyout's
    /// Hide takes the card out of the window at once, and its exit would play without its styles.
    /// </summary>
    private static void CloseLaunchpadCard(Control? card)
    {
        if (card?.FindLogicalAncestorOfType<Popup>() is { IsOpen: true } popup)
            popup.IsOpen = false;
    }

    /// <summary>Another tab's starters rise in one after another, so switching tabs reads as the capsules changing rather than a blink.</summary>
    private void OnLaunchpadPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LaunchpadViewModel.ActiveSetup))
        {
            // The chat's setup may lie past the rail's edge (kept just now, or picked from Other): bring it into sight
            // once its capsule is laid out.
            Dispatcher.UIThread.Post(() => BringActiveSetupIntoView(animate: true), DispatcherPriority.Loaded);
            return;
        }

        if (e.PropertyName != nameof(LaunchpadViewModel.SelectedCategory)
            || _launchpadStarters is not ItemsControl starters
            || _subscribedVm is not { AreAnimationsEnabled: true })
        {
            return;
        }

        _launchpadIdeaReason?.Classes.Set("shown", false);
        // The capsules are made at the next layout pass; stagger them once they are.
        Dispatcher.UIThread.Post(() =>
        {
            var delay = 0;
            foreach (var capsule in starters.GetRealizedContainers())
            {
                SlideFadeEntrance.Play(capsule, offsetY: 6, duration: TimeSpan.FromMilliseconds(240),
                    delay: TimeSpan.FromMilliseconds(delay));
                delay += 24;
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Says why a For you starter is offered while the pointer rests on it, on one line under the starters: the
    /// capsules themselves carry no tooltips, since they change with the tab under the pointer.
    /// </summary>
    private void OnLaunchpadIdeaPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_launchpadIdeaReason is null)
            return;

        if (sender is Control { DataContext: LaunchpadStarterItem { HasReason: true } starter })
        {
            _launchpadIdeaReason.Text = starter.Reason;
            _launchpadIdeaReason.Classes.Set("shown", true);
        }
        else
        {
            _launchpadIdeaReason.Classes.Set("shown", false);
        }
    }

    private void OnLaunchpadIdeaPointerExited(object? sender, PointerEventArgs e)
        => _launchpadIdeaReason?.Classes.Set("shown", false);

    /// <summary>Keeps the launchpad in the space above the composer and the setups on it, whatever their height.</summary>
    private void ReserveComposerSpaceForLaunchpad()
    {
        // An open chat never sees the launchpad, so its composer animating must not relayout it.
        if (_welcomeScroller is null || _composerContainer is null || _activeLaunchpad is null)
            return;

        var reserve = _composerContainer.Bounds.Height + _composerContainer.Margin.Bottom + 12;
        _welcomeScroller.Margin = new Thickness(0, 0, 0, Math.Max(reserve, 120));
    }

    private void UpdateLaunchpadFit(double? panelHeight = null)
    {
        if (_welcomeScroller is null || _welcomeContent is null || _activeLaunchpad is null)
            return;

        var compact = (panelHeight ?? _welcomePanel?.Bounds.Height ?? _welcomeScroller.Bounds.Height) < LaunchpadCompactHeight;
        _welcomeContent.Classes.Set("compact", compact);
        _launchpadIdeas?.Classes.Set("compact", compact);
        UpdateLaunchpadOverflowFade();
    }

    private void UpdateLaunchpadOverflowFade()
    {
        if (_welcomeScroller is not { } scroller)
            return;

        var moreBelow = scroller.Extent.Height - scroller.Viewport.Height - scroller.Offset.Y > 1;
        scroller.OpacityMask = moreBelow ? LaunchpadOverflowMask : null;
    }

    // ───────────── the setups rail ─────────────

    /// <summary>
    /// Marks which ways more setups lie (the arrows show for those under the pointer) and fades the rail's edge there,
    /// so a capsule cut by the edge reads as more to come.
    /// </summary>
    private void UpdateLaunchpadRailEdges()
    {
        if (_launchpadSetupScroller is not { } scroller || _launchpadSetupRail is not { } rail)
            return;

        var offset = scroller.Offset.X;
        var viewport = scroller.Viewport.Width;
        var back = offset > 0.5;
        var forward = scroller.Extent.Width - viewport - offset > 0.5;
        rail.Classes.Set("can-back", back);
        rail.Classes.Set("can-forward", forward);

        var edges = (back, forward, Math.Round(viewport));
        if (edges == _launchpadRailEdges)
            return;

        _launchpadRailEdges = edges;
        scroller.OpacityMask = back || forward ? LaunchpadRailMask(back, forward, viewport) : null;
    }

    /// <summary>A mask that fades the rail over its last 40 pixels on each side where more setups lie.</summary>
    private static IImmutableBrush LaunchpadRailMask(bool back, bool forward, double width)
    {
        var fade = width > 0 ? Math.Min(0.3, 40 / width) : 0;
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(back ? Colors.Transparent : Colors.Black, 0),
                new GradientStop(Colors.Black, back ? fade : 0),
                new GradientStop(Colors.Black, forward ? 1 - fade : 1),
                new GradientStop(forward ? Colors.Transparent : Colors.Black, 1),
            },
        }.ToImmutable();
    }

    /// <summary>An arrow moves the rail by most of its width, keeping a capsule's worth of what was in sight.</summary>
    private void OnLaunchpadRailStepClick(object? sender, RoutedEventArgs e)
    {
        if (_launchpadSetupScroller is not { } scroller)
            return;

        var step = Math.Max(80, scroller.Viewport.Width - 96);
        var forward = sender is Control control && control.Classes.Contains("forward");
        GlideLaunchpadRailTo(LaunchpadRailDestination() + (forward ? step : -step), animate: true);
    }

    private void OnLaunchpadRailWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_launchpadSetupScroller is not { } scroller || scroller.Extent.Width - scroller.Viewport.Width <= 0.5)
            return;

        var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
        if (delta == 0)
            return;

        GlideLaunchpadRailTo(LaunchpadRailDestination() - delta * 72, animate: true);
        e.Handled = true;
    }

    /// <summary>Where the rail is headed: the end of a glide under way, or where it rests.</summary>
    private double LaunchpadRailDestination()
        => _launchpadRailGliding ? _launchpadRailTarget : _launchpadSetupScroller?.Offset.X ?? 0;

    /// <summary>Keeps the chat's setup in sight, clear of the rail's faded edges.</summary>
    private void BringActiveSetupIntoView(bool animate)
    {
        if (_launchpadSetupScroller is not { } scroller
            || _launchpadSetupTiles is not { } tiles
            || _activeLaunchpad?.ActiveSetup is not { } active
            || tiles.ContainerFromItem(active) is not Control container
            || container.TranslatePoint(default, tiles) is not { } origin)
        {
            return;
        }

        const double clearance = 36;
        var start = origin.X - clearance;
        var end = origin.X + container.Bounds.Width + clearance;
        var offset = LaunchpadRailDestination();
        if (start < offset)
            GlideLaunchpadRailTo(start, animate);
        else if (end > offset + scroller.Viewport.Width)
            GlideLaunchpadRailTo(end - scroller.Viewport.Width, animate);
    }

    private void GlideLaunchpadRailTo(double target, bool animate)
    {
        if (_launchpadSetupScroller is not { } scroller)
            return;

        _launchpadRailTarget = Math.Clamp(target, 0, Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width));
        if (!animate || _subscribedVm is not { AreAnimationsEnabled: true } || TopLevel.GetTopLevel(this) is not { } top)
        {
            _launchpadRailGliding = false;
            scroller.Offset = new Vector(_launchpadRailTarget, scroller.Offset.Y);
            return;
        }

        if (_launchpadRailGliding)
            return;

        _launchpadRailGliding = true;
        _launchpadRailLastFrame = null;
        top.RequestAnimationFrame(StepLaunchpadRail);
    }

    /// <summary>Eases the rail toward its target a frame at a time, so an arrow or a wheel notch glides rather than jumps.</summary>
    private void StepLaunchpadRail(TimeSpan now)
    {
        if (!_launchpadRailGliding || _launchpadSetupScroller is not { } scroller)
            return;

        var elapsed = _launchpadRailLastFrame is { } last ? (now - last).TotalMilliseconds : 16;
        _launchpadRailLastFrame = now;
        // The rail may have changed under the glide (a setup kept or removed): never aim past its end.
        _launchpadRailTarget = Math.Clamp(_launchpadRailTarget, 0, Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width));
        var current = scroller.Offset.X;
        var next = current + (_launchpadRailTarget - current) * (1 - Math.Exp(-Math.Clamp(elapsed, 1, 50) / 70));
        if (Math.Abs(_launchpadRailTarget - next) < 0.5)
        {
            next = _launchpadRailTarget;
            _launchpadRailGliding = false;
        }

        scroller.Offset = new Vector(next, scroller.Offset.Y);
        if (_launchpadRailGliding)
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(StepLaunchpadRail);
    }

    /// <summary>
    /// Points the launchpad subscription at the surface this view shows in its new-chat state, so a
    /// cached draft or an open chat never keeps watching chats and the clock.
    /// </summary>
    private void UpdateLaunchpadActivation()
    {
        var target = this.IsAttachedToVisualTree() && _subscribedVm is { IsWelcomeVisible: true } vm
            ? vm.Launchpad
            : null;
        if (ReferenceEquals(target, _activeLaunchpad))
            return;

        DeactivateLaunchpad();
        _activeLaunchpad = target;
        if (target is null)
            return;

        target.ChatOpening += OnLaunchpadChatOpening;
        target.OtherPicked += OnLaunchpadOtherPicked;
        target.PropertyChanged += OnLaunchpadPropertyChanged;
        target.Activate();
        ReserveComposerSpaceForLaunchpad();
        UpdateLaunchpadFit();
        Dispatcher.UIThread.Post(PlayLaunchpadEntrance, DispatcherPriority.Loaded);
        // A new chat's setups start from the beginning, with the chat's own in sight.
        Dispatcher.UIThread.Post(() =>
        {
            GlideLaunchpadRailTo(0, animate: false);
            BringActiveSetupIntoView(animate: false);
        }, DispatcherPriority.Loaded);
    }

    private void DeactivateLaunchpad()
    {
        if (_activeLaunchpad is { } launchpad)
        {
            launchpad.ChatOpening -= OnLaunchpadChatOpening;
            launchpad.OtherPicked -= OnLaunchpadOtherPicked;
            launchpad.PropertyChanged -= OnLaunchpadPropertyChanged;
            launchpad.Deactivate();
        }

        _activeLaunchpad = null;
        CloseLaunchpadCard(_launchpadNeedsYouCard);
        CloseLaunchpadCard(_launchpadOtherCard);
    }

    /// <summary>Composes the launchpad in: the greeting, then what to start, rise in turn; the setups settle onto the composer.</summary>
    private void PlayLaunchpadEntrance()
    {
        if (_subscribedVm is not { IsWelcomeVisible: true, AreAnimationsEnabled: true })
            return;

        var delay = 0;
        foreach (var host in new[] { _launchpadHero, _launchpadIdeas })
        {
            if (host is null)
                continue;

            SlideFadeEntrance.Play(
                host,
                offsetY: 10,
                duration: TimeSpan.FromMilliseconds(340),
                delay: TimeSpan.FromMilliseconds(delay));
            delay += 80;
        }

        if (_launchpadSetupRow is not null)
        {
            SlideFadeEntrance.Play(
                _launchpadSetupRow,
                offsetY: 6,
                duration: TimeSpan.FromMilliseconds(300),
                delay: TimeSpan.FromMilliseconds(120));
        }
    }
}
