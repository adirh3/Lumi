using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Lumi.Views;

public abstract class DpiAwareWindow : Window
{
    protected DpiAwareWindow()
    {
        if (OperatingSystem.IsWindows() && TryGetPlatformHandle()?.HandleDescriptor == "HWND")
            ScalingChanged += OnScalingChanged;
    }

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(nint hwnd);

    [SupportedOSPlatform("windows")]
    private void OnScalingChanged(object? sender, EventArgs e)
    {
        // The suggested native frame is applied after ScalingChanged returns.
        if (IsVisible && WindowState == WindowState.Maximized)
            Dispatcher.UIThread.Post(RefreshMaximizedClient, DispatcherPriority.Send);
    }

    [SupportedOSPlatform("windows")]
    private void RefreshMaximizedClient()
    {
        if (!IsVisible || WindowState != WindowState.Maximized || TryGetPlatformHandle()?.Handle is not { } hwnd
            || hwnd == 0 || !IsZoomed(hwnd) || !WindowChromeInterop.TryGetBounds(hwnd, out var frame, out var client))
            return;

        var scale = RenderScaling;
        if (client.Width <= 0 || client.Height <= 0 || (Math.Abs(ClientSize.Width * scale - client.Width) <= 1
            && Math.Abs(ClientSize.Height * scale - client.Height) <= 1))
            return;

        // With no WM_SIZE after a DPI change the HWND already fits, but the old logical size
        // remains. Correct the layout, not the native window (WM_SIZE would also raise it).
        var size = KeepPhysicalPixels(client.Size.ToSize(scale), scale);
        Width = size.Width;
        Height = size.Height;
        FrameSize = frame.Size.ToSize(scale);
        ClientSize = size;
        UpdateLayout();
    }

    protected override void ArrangeCore(Rect finalRect)
    {
        // Layout rounds back to DIPs before Window arranges. Keep the same pixels here too.
        if (OperatingSystem.IsWindows() && WindowState == WindowState.Maximized)
            finalRect = new Rect(finalRect.Position, KeepPhysicalPixels(finalRect.Size, RenderScaling));
        base.ArrangeCore(finalRect);
    }

    private static Size KeepPhysicalPixels(Size size, double scale)
    {
        return new Size(Keep(size.Width), Keep(size.Height));

        // Avalonia.Win32 truncates on the way back to pixels. At fractional DPI a whole
        // pixel can come back one floating-point step below the integer (e.g. 1910 at 175%).
        // Losing that pixel would make Win32 overwrite the normal restore bounds.
        double Keep(double dips)
        {
            var pixels = dips * scale;
            return Math.BitIncrement(pixels) == Math.Ceiling(pixels) ? Math.BitIncrement(dips) : dips;
        }
    }
}
