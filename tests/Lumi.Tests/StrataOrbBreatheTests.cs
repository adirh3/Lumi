using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Regression guard for the Active-state "breathe" composition animation on <see cref="StrataOrb"/>.
///
/// An Active orb runs a <c>Forever</c> opacity animation on its composition visual. If that animation is
/// left running when the orb detaches (e.g. onboarding teardown / view virtualization), it becomes an
/// orphan that keeps ticking on the render thread indefinitely — the same class of leak that starves the
/// compositor over a long session (animations break, the navigation menu stops compositing, the app slows).
/// This test pins the fix: the breathe animation stops on detach.
/// </summary>
[Collection("Headless UI")]
public sealed class StrataOrbBreatheTests
{
    [Fact]
    public async Task Breathe_StopsWhenActiveOrbDetaches()
    {
        using var session = HeadlessTestSession.Start();

        var activeWhileAttached = false;
        var activeAfterDetach = false;

        await session.Dispatch(async () =>
        {
            var orb = new StrataOrb { State = OrbState.Active };

            var panel = new StackPanel();
            panel.Children.Add(orb);
            var window = new Window { Width = 320, Height = 200, Content = panel };

            window.Show();
            await PumpAsync(); // attach + apply template + run the posted ApplyAnimation
            await PumpAsync();

            activeWhileAttached = orb.IsBreatheActiveForTest;

            // Detach the still-active orb (the onboarding-teardown / virtualization equivalent).
            panel.Children.Remove(orb);
            await PumpAsync();

            activeAfterDetach = orb.IsBreatheActiveForTest;

            window.Close();
        }, CancellationToken.None);

        Assert.True(activeWhileAttached, "breathe animation should be engaged while Active and attached");
        Assert.False(activeAfterDetach, "breathe animation must stop when the Active orb detaches");
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
