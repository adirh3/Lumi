using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class StartupHandOffTests
{
    [Fact]
    public void HandOff_PlaysOnAWelcomeScreen_WithAnimationsOn()
    {
        Assert.True(StartupHandOff.Applies(isWindows: true, animate: true, opensOnWelcome: true, WindowState.Normal, 1.5, 1.5));
        Assert.True(StartupHandOff.Applies(isWindows: true, animate: true, opensOnWelcome: true, WindowState.Maximized, 1, 1));
    }

    [Theory]
    [InlineData(false, true, true, WindowState.Normal, 1, 1)] // Not Windows: no cloaking.
    [InlineData(true, false, true, WindowState.Normal, 1, 1)] // Animations off.
    [InlineData(true, true, false, WindowState.Normal, 1, 1)] // Another first screen.
    [InlineData(true, true, true, WindowState.Minimized, 1, 1)] // Opens minimized.
    [InlineData(true, true, true, WindowState.Normal, 1, 1.5)] // Screens of different scale.
    public void HandOff_FallsBackToReplacingTheLoadingWindow(
        bool isWindows, bool animate, bool opensOnWelcome, WindowState state, double loadingScale, double windowScale)
    {
        Assert.False(StartupHandOff.Applies(isWindows, animate, opensOnWelcome, state, loadingScale, windowScale));
    }

    [Fact]
    public void Card_GrowsFromTheLoadingWindowIntoLumisWindow_WithoutOvershoot()
    {
        Assert.Equal(0, StartupHandOff.Grow(0));
        Assert.Equal(1, StartupHandOff.Grow(1));
        Assert.Equal(1, StartupHandOff.Grow(1.5));

        var growth = Enumerable.Range(0, 101).Select(i => StartupHandOff.Grow(i / 100.0)).ToArray();
        Assert.All(growth.Zip(growth.Skip(1)), step => Assert.True(step.Second >= step.First));
        Assert.All(growth, t => Assert.InRange(t, 0, 1));
        // Quick to start, gentle to settle: most of the way there by two thirds of its time.
        Assert.True(StartupHandOff.Grow(2 / 3.0) > 0.98);
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public async Task Mark_LandsOnTheWelcomeLogo_InEitherDirection(FlowDirection direction)
    {
        using var session = HeadlessTestSession.Start();
        Rect place = default, logoDrawn = default, markDrawn = default;

        await session.Dispatch(() =>
        {
            // The welcome logo centred beside a sidebar, so it sits off the window's centre: on the left in a
            // right-to-left window, where the sidebar is on the right.
            var logo = new Border
            {
                Width = 56, Height = 56,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(logo, 1);
            var window = new Window
            {
                Width = 800, Height = 600, FlowDirection = direction,
                Content = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*"), Children = { logo } },
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                // As the arrival does: the mark goes into the overlay layer, which is created then and not yet laid out.
                place = MainWindow.InWindow(logo, window);
                var mark = new Border { Width = place.Width, Height = place.Height };
                Canvas.SetLeft(mark, place.X);
                Canvas.SetTop(mark, place.Y);
                OverlayLayer.GetOverlayLayer(window)!.Children.Add(mark);
                window.UpdateLayout();

                logoDrawn = Drawn(logo);
                markDrawn = Drawn(mark);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

        Assert.Equal(new Size(56, 56), place.Size);
        // The sidebar's 240 sit on the leading side; the logo is centred in the remaining 560.
        var expectedX = direction == FlowDirection.LeftToRight ? 240 + 252 : 252;
        Assert.Equal(expectedX, place.X, 0.5);
        Assert.Equal(expectedX, logoDrawn.X, 0.5);
        Assert.Equal(logoDrawn.X, markDrawn.X, 0.5);
        Assert.Equal(logoDrawn.Y, markDrawn.Y, 0.5);
        Assert.Equal(logoDrawn.Size, markDrawn.Size);
    }

    /// <summary>Where <paramref name="visual"/> is drawn in its window, through the transforms the renderer
    /// applies, right-to-left mirrors included.</summary>
    private static Rect Drawn(Visual visual)
    {
        var drawn = visual.GetTransformedBounds()!.Value;
        return drawn.Bounds.TransformToAABB(drawn.Transform);
    }
}
