#if WINDOWS
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection(nameof(NativeWindowTestSession))]
public sealed class NativeWindowPlacementTests(NativeWindowTestSession session)
{
    [SkippableFact]
    public async Task FractionalScalingKeepsAFullyVisibleWindowSpanningRemainingDisplays()
    {
        Skip.IfNot(Environment.UserInteractive, "Requires an interactive Windows desktop.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await session.RunAsync(async () =>
        {
            var window = new Window { ShowActivated = false, ShowInTaskbar = false, Width = 640, Height = 480 };
            window.Show();
            try
            {
                var hwnd = window.TryGetPlatformHandle()!.Handle;
                const int physicalHeight = 900;
                ChangeDpiWithoutMoving(hwnd, 168);
                await Settle();
                Assert.True(WindowChromeInterop.TryGetBounds(hwnd, out var frame, out var client));
                Assert.True(SetWindowPos(hwnd, 0, 0, 0, frame.Width, physicalHeight + frame.Height - client.Height, 0x16));
                await Settle();
                Assert.True(WindowChromeInterop.TryGetBounds(hwnd, out frame, out client));
                Assert.Equal(physicalHeight, client.Height);
                Assert.Equal(physicalHeight + 1, PixelSize.FromSize(window.ClientSize, window.DesktopScaling).Height);
                var seam = client.X + client.Width / 2;
                var left = new WindowDisplayLossRecovery.Display(1,
                    new PixelRect(client.X - 200, client.Y - 100, seam - client.X + 200, client.Height + 100), true);
                var right = new WindowDisplayLossRecovery.Display(2,
                    new PixelRect(seam, client.Y - 100, client.Right - seam + 200, client.Height + 100), false);
                var dock = right with { Handle = 3 };
                bool docked = true;
                using var recovery = new WindowDisplayLossRecovery(window,
                    () => docked ? [left, right, dock] : [left, right], () => docked ? dock.Handle : right.Handle);
                docked = false;
                recovery.DisplayConfigurationChanged();
                await Task.Delay(500, timeout.Token);
                await Settle();
                Assert.True(WindowChromeInterop.TryGetBounds(hwnd, out var actual, out _));
                Assert.Equal(frame, actual);
            }
            finally
            {
                window.Close();
            }
        }, timeout.Token);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyAnInteractiveMoveCancelsPendingDisplayLossRecovery(bool userDragged)
    {
        Skip.IfNot(Environment.UserInteractive, "Requires an interactive Windows desktop.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await session.RunAsync(async () =>
        {
            var window = new Window { ShowActivated = false, ShowInTaskbar = false, Width = 480, Height = 320 };
            window.Show();
            try
            {
                var hwnd = window.TryGetPlatformHandle()!.Handle;
                Assert.True(GetWindowRect(hwnd, out var frame));
                var position = window.Position;
                var workArea = new PixelRect(frame.Left - 240, frame.Top - 100,
                    frame.Right - frame.Left + 120, frame.Bottom - frame.Top + 200);
                var remaining = new WindowDisplayLossRecovery.Display(1, workArea, true);
                var dock = remaining with { Handle = 2, IsPrimary = false };
                bool docked = true;
                using var recovery = new WindowDisplayLossRecovery(window,
                    () => docked ? [remaining, dock] : [remaining], () => docked ? dock.Handle : remaining.Handle);
                docked = false;
                recovery.DisplayConfigurationChanged();
                if (userDragged)
                    SendMessageWithRect(hwnd, 0x0216, 0, ref frame); // WM_MOVING

                await Task.Delay(500, timeout.Token);
                await Settle();
                if (userDragged)
                    Assert.Equal(position, window.Position);
                else
                    Assert.True(window.Position.X < position.X);
            }
            finally
            {
                window.Close();
            }
        }, timeout.Token);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownscalingWithoutResizingTheMaximizedFrameKeepsContentFillingTheClient(bool fractional)
    {
        Skip.IfNot(Environment.UserInteractive, "Requires an interactive Windows desktop.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await session.RunAsync(async () =>
        {
            var window = new TestWindow
            {
                ShowActivated = false, ShowInTaskbar = false,
                Width = 800, Height = 560, Content = new Border(),
                Template = new FuncControlTemplate<Window>((owner, _) => new ContentPresenter { Content = owner.Content })
            };
            WindowChromeInterop.EnableNativeMinMaxAnimations(window);
            window.ExtendClientAreaToDecorationsHint = true;
            window.Show();
            var other = new Window { ShowActivated = false, ShowInTaskbar = false, Width = 120, Height = 80 };
            try
            {
                window.WindowState = WindowState.Maximized;
                await Settle();
                var hwnd = window.TryGetPlatformHandle()!.Handle;
                var dpi = fractional ? 168u : GetDpiForWindow(hwnd);
                Assert.NotEqual(0u, dpi);

                ChangeDpiWithoutMoving(hwnd, fractional ? 384u : dpi * 2);
                await Settle();
                // Establish the last WM_SIZE at the old DPI. Windows can subsequently deliver
                // WM_DPICHANGED with no further resize if the native frame already fits.
                Assert.True(GetClientRect(hwnd, out var previous));
                SendMessageW(hwnd, WM_SIZE, SIZE_MAXIMIZED, (previous.Bottom << 16) | previous.Right);
                await Settle();
                AssertFillsClient(window, hwnd);

                if (fractional)
                {
                    // Pick a client width for which Win32's (int)(DIPs * 1.75) loses a pixel.
                    Assert.True(GetClientRect(hwnd, out var client));
                    var sensitiveWidth = Enumerable.Range(256, client.Right - 256)
                        .Last(width => (int)(width / 1.75 * 1.75) != width);
                    Assert.True(GetWindowRect(hwnd, out var raw));
                    Assert.True(SetWindowPos(hwnd, 0, 0, 0, sensitiveWidth + raw.Right - raw.Left - client.Right,
                        raw.Bottom - raw.Top, 0x16)); // Keep position, activation and z-order.
                    await Settle();
                    Assert.True(GetClientRect(hwnd, out client));
                    Assert.Equal(sensitiveWidth, client.Right);
                }

                Assert.True(GetWindowRect(hwnd, out var frame));
                other.Show();
                var otherHwnd = other.TryGetPlatformHandle()!.Handle;
                Assert.True(SetWindowPos(otherHwnd, 0, 0, 0, 0, 0, 0x13)); // Top, without activation, movement or resize.
                Assert.True(IsAbove(otherHwnd, hwnd));
                Assert.NotEqual(hwnd, GetForegroundWindow());
                var restoreBounds = NormalPlacement(hwnd);
                ChangeDpiWithoutMoving(hwnd, dpi);
                var restoreAfterNativeDpi = NormalPlacement(hwnd);
                await Settle();
                Assert.Equal(dpi / 96d, window.RenderScaling);
                Assert.Equal(WindowState.Maximized, window.WindowState);
                Assert.True(GetWindowRect(hwnd, out var after));
                Assert.Equal(frame, after);
                AssertFillsClient(window, hwnd);
                Assert.True(restoreBounds.Equals(NormalPlacement(hwnd)),
                    $"Restore bounds before DPI: {restoreBounds}, after native DPI processing: {restoreAfterNativeDpi}, after layout: {NormalPlacement(hwnd)}.");
                Assert.True(IsAbove(otherHwnd, hwnd), "Refreshing the client raised a background window.");
                Assert.NotEqual(hwnd, GetForegroundWindow());
            }
            finally
            {
                other.Close();
                window.Close();
            }
        }, timeout.Token);
    }

    private sealed class TestWindow : DpiAwareWindow;

    private const uint WM_SIZE = 0x0005;
    private const int SIZE_MAXIMIZED = 2;

    private static async Task Settle() =>
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

    private static void AssertFillsClient(Window window, nint hwnd)
    {
        Assert.True(GetClientRect(hwnd, out var native));
        foreach (var size in new[] { window.ClientSize, ((Control)window.Content!).Bounds.Size })
        {
            Assert.Equal(native.Right, (int)Math.Round(size.Width * window.RenderScaling));
            Assert.Equal(native.Bottom, (int)Math.Round(size.Height * window.RenderScaling));
        }
    }

    private static void ChangeDpiWithoutMoving(nint hwnd, uint dpi)
    {
        Assert.True(GetWindowRect(hwnd, out var current));
        SendMessageWithRect(hwnd, 0x02E0, (nint)(dpi << 16 | dpi), ref current);
    }

    private static bool IsAbove(nint other, nint hwnd)
    {
        for (var next = other; next != 0; next = GetWindow(next, 2)) // GW_HWNDNEXT
            if (next == hwnd)
                return true;
        return false;
    }

    private static Rect NormalPlacement(nint hwnd)
    {
        var placement = new Placement { Length = (uint)Marshal.SizeOf<Placement>() };
        Assert.True(GetWindowPlacement(hwnd, ref placement));
        return placement.Normal;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
        public override readonly string ToString() => $"{Left},{Top}..{Right},{Bottom}";
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        public uint Length, Flags, ShowCommand;
        public int MinX, MinY, MaxX, MaxY;
        public Rect Normal;
    }

    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(nint hwnd, ref Placement placement);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessageWithRect(nint hwnd, uint msg, nint wParam, ref Rect rect);
}
#endif
