using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

public class WindowDisplayLossRecoveryTests
{
    private static readonly PixelRect LaptopArea = new(0, 0, 1000, 700);
    private static readonly PixelRect DockArea = new(1000, 0, 1600, 1000);
    private static readonly PixelRect ProjectorArea = new(-1000, 0, 1000, 700);
    private static readonly PixelPoint ClippedPosition = new(600, 250);
    private static readonly Size NormalSize = new(800, 600);

    [Theory]
    [InlineData(800, 600, 200, 100)]
    [InlineData(1100, 800, 0, 0)]
    public async Task UndockFitsClippedWindowOnceWithoutChangingItsState(
        int width, int height, int left, int top)
    {
        await WithWindowAsync(onDock: true, async (window, displays) =>
        {
            // Windows can move and resize the HWND before Avalonia announces the new screen list.
            window.Position = ClippedPosition;
            window.Width = width;
            window.Height = height;
            displays.SetDocked(false);

            var fittedSize = new Size(Math.Min(width, LaptopArea.Width), Math.Min(height, LaptopArea.Height));
            await WaitForAsync(() => window.Position == new PixelPoint(left, top) && window.ClientSize == fittedSize);
            Assert.Equal(WindowState.Normal, window.WindowState);

            window.Position = ClippedPosition;
            displays.SetDocked(false);
            await LetPlacementSettleAsync();
            Assert.Equal(ClippedPosition, window.Position);
        });
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task LeavesUnrelatedDisplayRemovalModeChangesAndValidPlacementAlone(bool onDock, bool removeDock)
    {
        await WithWindowAsync(onDock, async (window, displays) =>
        {
            var position = onDock && removeDock ? new PixelPoint(100, 50)
                : onDock ? new PixelPoint(2100, 500)
                : ClippedPosition;
            window.Position = position;
            if (removeDock)
                displays.SetDocked(false);
            else
                displays.ChangeDockWorkArea(new PixelRect(1000, 0, 1300, 800));
            await LetPlacementSettleAsync();

            Assert.Equal(position, window.Position);
            Assert.Equal(NormalSize, window.ClientSize);
        });
    }

    [Fact]
    public async Task DoesNotRecoverAfterTheUserMovedToTheRemainingDisplay()
    {
        await WithWindowAsync(onDock: true, async (window, displays) =>
        {
            window.Position = ClippedPosition;
            await LetPlacementSettleAsync();
            displays.SetDocked(false);
            await LetPlacementSettleAsync();

            Assert.Equal(ClippedPosition, window.Position);
            Assert.Equal(NormalSize, window.ClientSize);
        });
    }

    [Fact]
    public async Task TracksTheNewHostWhenMinimizedImmediatelyAfterMoving()
    {
        await WithWindowAsync(onDock: false, async (window, displays) =>
        {
            window.Position = new PixelPoint(1300, 100);
            window.WindowState = WindowState.Minimized;
            await LetPlacementSettleAsync();

            displays.SetDocked(false);
            window.Position = ClippedPosition;
            window.WindowState = WindowState.Normal;

            await WaitForAsync(() => window.Position == new PixelPoint(200, 100));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacingTheLostDisplayDoesNotSkipRecovery(bool sameNotification)
    {
        await WithWindowAsync(onDock: true, async (window, displays) =>
        {
            window.WindowState = WindowState.Minimized;
            if (!sameNotification)
            {
                displays.SetDocked(false);
                await LetPlacementSettleAsync();
            }
            displays.SetDocked(false, projectorConnected: true);
            window.Position = ClippedPosition;
            window.WindowState = WindowState.Normal;

            await WaitForAsync(() => window.Position == new PixelPoint(200, 100));
        });
    }

    [Fact]
    public async Task LeavesAWindowFullyVisibleAcrossRemainingDisplaysInPlace()
    {
        await WithWindowAsync(onDock: true, async (window, displays) =>
        {
            var spanningPosition = new PixelPoint(-350, 50);
            displays.SetDocked(true, projectorConnected: true);
            window.Position = spanningPosition;
            displays.SetDocked(false);
            await LetPlacementSettleAsync();

            Assert.Equal(spanningPosition, window.Position);
            Assert.Equal(NormalSize, window.ClientSize);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DoesNotUnminimizeAndChecksTheFirstRestoreUnlessTheDockReturned(bool redock)
    {
        await WithWindowAsync(onDock: true, async (window, displays) =>
        {
            window.WindowState = WindowState.Minimized;
            window.Position = ClippedPosition;
            displays.SetDocked(false);
            await LetPlacementSettleAsync();
            Assert.Equal(WindowState.Minimized, window.WindowState);
            Assert.Equal(ClippedPosition, window.Position);

            if (redock)
                displays.SetDocked(true);
            window.WindowState = WindowState.Normal;
            if (redock)
            {
                await LetPlacementSettleAsync();
                Assert.Equal(ClippedPosition, window.Position);
            }
            else
            {
                await WaitForAsync(() => window.Position == new PixelPoint(200, 100));
            }
            Assert.Equal(NormalSize, window.ClientSize);
            Assert.Equal(WindowState.Normal, window.WindowState);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeavesMaximizedWindowAloneUntilItRestoresToNormal(bool minimizedDuringUndock)
    {
        await WithWindowAsync(onDock: true, async (window, displays) =>
        {
            window.WindowState = WindowState.Maximized;
            if (minimizedDuringUndock)
                window.WindowState = WindowState.Minimized;
            window.Position = ClippedPosition;
            displays.SetDocked(false);
            await LetPlacementSettleAsync();

            if (minimizedDuringUndock)
            {
                Assert.Equal(WindowState.Minimized, window.WindowState);
                window.WindowState = WindowState.Maximized;
                await LetPlacementSettleAsync();
            }
            Assert.Equal(WindowState.Maximized, window.WindowState);
            Assert.Equal(ClippedPosition, window.Position);

            window.WindowState = WindowState.Normal;
            await WaitForAsync(() => window.Position == new PixelPoint(200, 100));
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(NormalSize, window.ClientSize);
        });
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task HiddenWindowOnlyFitsOnItsFirstNormalPlacementIfTheHostIsStillMissing(bool hideFirst, bool redock)
    {
        await WithWindowAsync(onDock: true, async (window, displays) =>
        {
            window.WindowState = WindowState.Maximized;
            if (hideFirst)
                window.Hide();
            displays.SetDocked(false);
            if (!hideFirst)
                window.Hide();
            window.Position = ClippedPosition;
            if (redock)
                displays.SetDocked(true);
            await LetPlacementSettleAsync();
            Assert.False(window.IsVisible);
            Assert.Equal(ClippedPosition, window.Position);

            window.Show();
            window.WindowState = WindowState.Normal;
            await LetPlacementSettleAsync();
            Assert.Equal(redock ? ClippedPosition : new PixelPoint(200, 100), window.Position);
        });
    }

    private static async Task WithWindowAsync(bool onDock, Func<Window, Displays, Task> test)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var window = new Window
            {
                Position = onDock ? new PixelPoint(1300, 100) : new PixelPoint(100, 50),
                Width = NormalSize.Width,
                Height = NormalSize.Height
            };
            window.Show();
            using var displays = new Displays(window);
            try
            {
                await test(window, displays);
            }
            finally
            {
                if (window.IsVisible)
                    window.Close();
            }
        }, CancellationToken.None);
    }

    private static Task LetPlacementSettleAsync() => Task.Delay(500);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    private sealed class Displays : IDisposable
    {
        private readonly WindowDisplayLossRecovery.Display _laptop = new(1, LaptopArea, true);
        private WindowDisplayLossRecovery.Display _dock = new(2, DockArea, false);
        private readonly WindowDisplayLossRecovery.Display _projector = new(3, ProjectorArea, false);
        private readonly WindowDisplayLossRecovery _recovery;
        private bool _docked = true;
        private bool _projectorConnected;

        public Displays(Window window) => _recovery = new WindowDisplayLossRecovery(window,
            () => AllScreens, () => AllScreens.FirstOrDefault(display => display.WorkArea.Contains(window.Position)).Handle);

        private IReadOnlyList<WindowDisplayLossRecovery.Display> AllScreens => (_docked, _projectorConnected) switch
        {
            (true, true) => [_laptop, _dock, _projector],
            (true, false) => [_laptop, _dock],
            (false, true) => [_laptop, _projector],
            _ => [_laptop]
        };

        public void SetDocked(bool docked, bool? projectorConnected = null)
        {
            _docked = docked;
            _projectorConnected = projectorConnected ?? _projectorConnected;
            _recovery.DisplayConfigurationChanged();
        }

        public void ChangeDockWorkArea(PixelRect workArea)
        {
            _dock = _dock with { WorkArea = workArea };
            _recovery.DisplayConfigurationChanged();
        }

        public void Dispose() => _recovery.Dispose();
    }
}
