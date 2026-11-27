using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

public class MainWindowPlacementTests
{
    [Fact]
    public void StartupDecodesASpanningWindowUsingTheScaleOfItsLargestOverlap()
    {
        var primary = new PixelRect(0, 0, 1920, 1080);
        var right = new PixelRect(1920, 0, 1920, 1080);
        var saved = new Point(1600, 50);
        var size = new Size(1000, 700);
        PixelRect Host(PixelRect frame)
        {
            var a = primary.Intersect(frame);
            var b = right.Intersect(frame);
            return a.Width * a.Height >= b.Width * b.Height ? primary : right;
        }

        Assert.Null(WindowPlacement.DecodeSavedPosition(saved, size, 1.75, frame => Host(frame) == primary));
        var position = WindowPlacement.DecodeSavedPosition(saved, size, 1, frame => Host(frame) == right);
        var expected = new PixelPoint(1600, 50);
        Assert.Equal(expected, position);
        Assert.Equal((size, expected), WindowPlacement.RestoreNormalBounds(size, new Size(720, 480), position,
            right, 1, default, [primary, right]));
    }

    [Fact]
    public void StartupKeepsTheFrameWhenOnlyItsInvisibleResizeBorderIsOffScreen()
    {
        var work = new PixelRect(0, 0, 1920, 1040);
        var origin = new PixelPoint(-8, 41);
        var size = new Size(947, 731);
        var offset = new Vector(8, 0);
        Assert.True(work.Contains(WindowPlacement.ClientOrigin(origin, offset, 1)));
        Assert.Equal((size, origin), WindowPlacement.RestoreNormalBounds(size, new Size(720, 480),
            origin, work, 1, new Size(16, 8), [work], offset));
    }

    [Theory]
    [InlineData(0, 0, 1500)]
    [InlineData(20, 0, 920)]
    [InlineData(0, 200, 920)]
    public void StartupPreservesFullyVisibleSpansButFitsWindowsThatCrossAGap(
        int horizontalGap, int verticalOffset, int expectedLeft)
    {
        var primary = new PixelRect(0, 0, 1920, 1080);
        var secondary = new PixelRect(1920 + horizontalGap, verticalOffset, 1920, 1080);
        var savedSize = new Size(1000, 700);
        var (size, position) = WindowPlacement.RestoreNormalBounds(
            savedSize, new Size(800, 560), new PixelPoint(1500, 100), primary, 1, default, [primary, secondary]);

        Assert.Equal(savedSize, size);
        Assert.Equal(new PixelPoint(expectedLeft, 100), position);
    }

    [Theory]
    [InlineData(false, false, 800, 560)]
    [InlineData(true, false, 800, 560)]
    [InlineData(false, true, 720, 480)]
    public async Task OpeningKeepsSavedNormalBoundsInWorkArea(
        bool prepareBeforeShowing, bool alreadyFits, int savedWidth, int savedHeight)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var window = new MainWindow();
            var screen = Assert.Single(window.Screens.All);
            var workArea = screen.WorkingArea;
            var savedPosition = alreadyFits
                ? new PixelPoint(workArea.X + 20, workArea.Y + 20)
                : new PixelPoint(workArea.Right - 100, workArea.Bottom - 100);
            var settings = new UserSettings
            {
                WindowLeft = savedPosition.X / screen.Scaling,
                WindowTop = savedPosition.Y / screen.Scaling,
                WindowWidth = savedWidth,
                WindowHeight = savedHeight,
                MinimizeToTray = false
            };
            using var vm = new MainViewModel(
                new DataStore(new AppData { Settings = settings }), TestCopilot.Shared, new UpdateService(),
                startBackgroundJobs: false);
            window.DataContext = vm;
            try
            {
                if (prepareBeforeShowing)
                    window.PrepareStartupBounds(out _);
                window.Show();

                var size = new Size(window.Width, window.Height);
                var bounds = new PixelRect(window.Position, PixelSize.FromSize(size, screen.Scaling));
                Assert.True(workArea.Contains(bounds), $"{bounds} falls outside {workArea}");
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.Equal(new Size(savedWidth, savedHeight), size);
                if (alreadyFits)
                    Assert.Equal(savedPosition, window.Position);
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        }, CancellationToken.None);
    }
}
