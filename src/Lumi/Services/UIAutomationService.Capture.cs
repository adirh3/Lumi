using System.Diagnostics;
#if WINDOWS
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core.Input;
#endif

namespace Lumi.Services;

public sealed partial class UIAutomationService
{
#if WINDOWS
    private readonly Dictionary<string, UIAutomationCapture> _captures = new(StringComparer.Ordinal);
    private readonly Queue<string> _captureOrder = new();

    public UIAutomationWindowInfo? GetWindowInfo(string? title = null, int? elementId = null)
    {
        lock (DesktopLock)
        {
            ThrowIfDisposed();
            using var dpi = new CaptureDpiScope();
            var window = title is not null ? GetActiveWindow(FindWindowByTitle(title))
                : elementId.HasValue ? GetIndexedElement(elementId.Value).WindowHandle
                : _lastWindowHandle;
            return window == IntPtr.Zero || !IsWindow(window) ? null : DescribeWindow(window);
        }
    }

    public UIAutomationCapture CaptureWindow(string title, int maxWidth = 1600, bool retainForClick = true)
    {
        if (maxWidth is < 320 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(maxWidth), "maxWidth must be between 320 and 4096.");
        lock (DesktopLock)
        {
            ThrowIfDisposed();
            using var dpi = new CaptureDpiScope();
            var window = PrepareWindow(title);
            if (SendMessageTimeout(window, 0, IntPtr.Zero, IntPtr.Zero, 2, 500, out _) == IntPtr.Zero)
                throw new InvalidOperationException("The window is not responding. No screenshot was captured.");

            var before = DescribeWindow(window);
            if (before.Width <= 0 || before.Height <= 0 || (long)before.Width * before.Height > 32_000_000)
                throw new InvalidOperationException("The window dimensions are unavailable or too large to capture safely.");

            using var bitmap = RenderWindow(window);
            var after = DescribeWindow(window);
            if (!before.HasSameGeometry(after))
                throw new InvalidOperationException("The window changed while the screenshot was being captured. Retry with a fresh capture.");

            var width = Math.Min(bitmap.Width, maxWidth);
            var height = Math.Max(1, (int)Math.Round((double)bitmap.Height * width / bitmap.Width));
            using var stream = new MemoryStream();
            if (width == bitmap.Width)
                bitmap.Save(stream, ImageFormat.Png);
            else
            {
                using var scaled = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                using (var graphics = Graphics.FromImage(scaled))
                {
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(bitmap, 0, 0, width, height);
                }
                scaled.Save(stream, ImageFormat.Png);
            }

            var capture = new UIAutomationCapture(Guid.NewGuid().ToString("N"), after,
                width, height, DateTimeOffset.UtcNow, stream.ToArray());
            if (retainForClick)
            {
                InvalidateWindowCaptures(window);
                _captures.Add(capture.CaptureId, capture);
                _captureOrder.Enqueue(capture.CaptureId);
                while (_captureOrder.Count > 4)
                    _captures.Remove(_captureOrder.Dequeue());
                _lastWindowHandle = window;
            }
            return capture;
        }
    }

    private static Bitmap RenderWindow(IntPtr window)
    {
        // PrintWindow renders DPI-unaware/system-aware apps in their own coordinate space,
        // not the physical pixel bounds used to validate and place a later click.
        var context = GetWindowDpiAwarenessContext(window);
        if (context == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the target window's rendering DPI context.");
        using var dpi = new CaptureDpiScope(context);
        if (!GetCaptureWindowRect(window, out var rectangle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the target's rendering bounds.");
        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        if (width <= 0 || height <= 0 || (long)width * height > 32_000_000)
            throw new InvalidOperationException("The target's rendering dimensions are unavailable or too large.");
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            var dc = graphics.GetHdc();
            try
            {
                if (!PrintWindow(window, dc, 2))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The application could not render a background screenshot.");
            }
            finally { graphics.ReleaseHdc(dc); }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    public string ClickAt(string captureId, double x, double y, string button = "left",
        int clickCount = 1, bool allowForeground = false)
        => RunLocked(() =>
        {
            using var dpi = new CaptureDpiScope();
            if (!_captures.TryGetValue(captureId, out var capture))
                throw new InvalidOperationException("Unknown or superseded captureId. Take a fresh ui_screenshot.");
            if (button is not ("left" or "right") || clickCount is < 1 or > 2
                || (button == "right" && clickCount != 1))
                throw new ArgumentException("Use left with clickCount 1 or 2, or right with clickCount 1.");
            var window = new IntPtr(capture.Window.Handle);
            if (!IsWindow(window) || IsIconic(window) || !IsWindowEnabled(window)
                || GetActiveWindow(window) != window)
                throw new InvalidOperationException("The captured window is closed, minimized, or blocked by a dialog. Take a fresh screenshot.");
            var point = capture.MapPoint(x, y, DescribeWindow(window), DateTimeOffset.UtcNow);
            RequireForeground(allowForeground, "Screenshot-coordinate clicks use the physical mouse.");
            EnsureWindowFocused(window);
            // Activation may move a restored/repositioned window; never use old coordinates.
            point = capture.MapPoint(x, y, DescribeWindow(window), DateTimeOffset.UtcNow);
            var position = new Point(point.X, point.Y);
            var hit = WindowFromPoint(position);
            // Activation can finish before DWM hit testing reflects the new z-order.
            var settle = Stopwatch.StartNew();
            while (hit != window && !IsChild(window, hit) && settle.ElapsedMilliseconds < 300)
            {
                if (GetForegroundWindow() != window)
                    throw new InvalidOperationException("Foreground focus changed before the click. No click was sent.");
                Thread.Sleep(10);
                capture.MapPoint(x, y, DescribeWindow(window), DateTimeOffset.UtcNow);
                hit = WindowFromPoint(position);
            }
            if (hit != window && !IsChild(window, hit))
                throw new InvalidOperationException($"Another window (hwnd:0x{hit:X}) covers the captured point ({point.X}, {point.Y}). No click was sent.");
            EnsurePointerAvailable(window);
            _captures.Remove(captureId);
            _lastWindowHandle = window;
            if (clickCount == 2)
                Mouse.DoubleClick(position, MouseButton.Left);
            else
                Mouse.Click(position, button == "right" ? MouseButton.Right : MouseButton.Left);
            return $"Sent {clickCount} {button} click(s) at image coordinate ({x}, {y}) in {capture.Window.WindowKey}. Input delivery does not prove the app acted; verify with a fresh screenshot. This capture is consumed.";
        });

    private static UIAutomationWindowInfo DescribeWindow(IntPtr window)
    {
        if (!GetCaptureWindowRect(window, out var rectangle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the target window bounds.");
        GetWindowThreadProcessId(window, out var processId);
        string? processName = null;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception)
        {
            Trace.TraceInformation($"[UIAutomation] Process metadata unavailable for {processId}: {ex.Message}");
        }
        return new UIAutomationWindowInfo(window.ToInt64(), (int)processId, GetWindowTitle(window),
            processName, rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left,
            rectangle.Bottom - rectangle.Top, GetDpiForWindow(window));
    }

    private void ClearCaptures()
    {
        _captures.Clear();
        _captureOrder.Clear();
    }

    private void InvalidateWindowCaptures(IntPtr window)
    {
        foreach (var capture in _captures.Values.Where(item => item.Window.Handle == window.ToInt64()).ToArray())
            _captures.Remove(capture.CaptureId);
    }

    private readonly struct CaptureDpiScope : IDisposable
    {
        private readonly IntPtr _previous;

        public CaptureDpiScope() : this(new IntPtr(-4))
        {
        }

        public CaptureDpiScope(IntPtr context)
        {
            _previous = SetThreadDpiAwarenessContext(context);
            if (_previous == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not establish physical-pixel capture coordinates.");
        }

        public void Dispose()
        {
            if (SetThreadDpiAwarenessContext(_previous) == IntPtr.Zero)
                Trace.TraceWarning("[UIAutomation] Could not restore the previous thread DPI context.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CaptureRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCaptureWindowRect(IntPtr window, out CaptureRectangle rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
#else
    public UIAutomationWindowInfo? GetWindowInfo(string? title = null, int? elementId = null) => null;
    public UIAutomationCapture CaptureWindow(string title, int maxWidth = 1600, bool retainForClick = true)
        => throw new PlatformNotSupportedException(NotSupported);
    public string ClickAt(string captureId, double x, double y, string button = "left",
        int clickCount = 1, bool allowForeground = false) => NotSupported;
#endif
}
