using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.ViewModels;
using StrataTheme.Animation;

namespace Lumi.Views;

/// <summary>
/// The view side of the new-chat launchpad: it keeps the launchpad observing only while it is on
/// screen, keeps it clear of the composer, and composes it in when the welcome state appears.
/// </summary>
public partial class ChatView
{
    /// <summary>Below this much room the launchpad goes compact (no mark, tighter margins).</summary>
    private const double LaunchpadCompactHeight = 600;

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
    private StackPanel? _welcomeContent;
    private StackPanel? _composerContainer;
    private Control? _launchpadHero;
    private Control? _launchpadStarters;
    private Control? _launchpadActivity;
    private Control? _launchpadSetupTiles;
    private LaunchpadViewModel? _activeLaunchpad;

    private void InitializeLaunchpad()
    {
        _welcomeScroller = this.FindControl<ScrollViewer>("WelcomeScroller");
        _welcomeContent = this.FindControl<StackPanel>("WelcomeGreeting");
        _composerContainer = this.FindControl<StackPanel>("ComposerContainer");
        _launchpadHero = this.FindControl<Control>("LaunchpadHero");
        _launchpadStarters = this.FindControl<Control>("WelcomeSuggestions");
        _launchpadActivity = this.FindControl<Control>("LaunchpadActivity");
        _launchpadSetupTiles = this.FindControl<Control>("LaunchpadSetupTiles");

        if (_composerContainer is not null)
            _composerContainer.SizeChanged += (_, _) => ReserveComposerSpaceForLaunchpad();

        if (_welcomeScroller is not null)
        {
            _welcomeScroller.SizeChanged += (_, _) => UpdateLaunchpadFit();
            _welcomeScroller.ScrollChanged += (_, _) => UpdateLaunchpadOverflowFade();
        }
    }

    /// <summary>Keeps the launchpad centered in the space above the composer, whatever its height.</summary>
    private void ReserveComposerSpaceForLaunchpad()
    {
        // An open chat never sees the launchpad, so its composer animating must not relayout it.
        if (_welcomeScroller is null || _composerContainer is null || _activeLaunchpad is null)
            return;

        var reserve = _composerContainer.Bounds.Height + _composerContainer.Margin.Bottom + 12;
        _welcomeScroller.Margin = new Thickness(0, 0, 0, Math.Max(reserve, 120));
    }

    private void UpdateLaunchpadFit()
    {
        if (_welcomeScroller is null || _welcomeContent is null || _activeLaunchpad is null)
            return;

        _welcomeContent.Classes.Set("compact", _welcomeScroller.Bounds.Height < LaunchpadCompactHeight);
        UpdateLaunchpadOverflowFade();
    }

    private void UpdateLaunchpadOverflowFade()
    {
        if (_welcomeScroller is not { } scroller)
            return;

        var moreBelow = scroller.Extent.Height - scroller.Viewport.Height - scroller.Offset.Y > 1;
        scroller.OpacityMask = moreBelow ? LaunchpadOverflowMask : null;
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

        _activeLaunchpad?.Deactivate();
        _activeLaunchpad = target;
        if (target is null)
            return;

        target.Activate();
        ReserveComposerSpaceForLaunchpad();
        UpdateLaunchpadFit();
        Dispatcher.UIThread.Post(PlayLaunchpadEntrance, DispatcherPriority.Loaded);
    }

    private void DeactivateLaunchpad()
    {
        _activeLaunchpad?.Deactivate();
        _activeLaunchpad = null;
    }

    /// <summary>Composes the launchpad in: hero, starters and activity rise in turn, the setups settle onto the composer.</summary>
    private void PlayLaunchpadEntrance()
    {
        if (_subscribedVm is not { IsWelcomeVisible: true, AreAnimationsEnabled: true })
            return;

        var delay = 0;
        foreach (var host in new[] { _launchpadHero, _launchpadStarters, _launchpadActivity })
        {
            if (host is null)
                continue;

            SlideFadeEntrance.Play(
                host,
                offsetY: 10,
                duration: TimeSpan.FromMilliseconds(340),
                delay: TimeSpan.FromMilliseconds(delay));
            delay += 70;
        }

        if (_launchpadSetupTiles is not null)
        {
            SlideFadeEntrance.Play(
                _launchpadSetupTiles,
                offsetY: 6,
                duration: TimeSpan.FromMilliseconds(300),
                delay: TimeSpan.FromMilliseconds(120));
        }
    }
}
