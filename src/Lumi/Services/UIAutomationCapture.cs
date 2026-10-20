using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Lumi.Services;

public sealed record UIAutomationWindowInfo(
    long Handle, int ProcessId, string Title, string? ProcessName,
    int Left, int Top, int Width, int Height, uint Dpi)
{
    public string WindowKey => $"hwnd:0x{Handle:X}";

    internal bool HasSameGeometry(UIAutomationWindowInfo other)
        => Handle == other.Handle && ProcessId == other.ProcessId
           && Left == other.Left && Top == other.Top
           && Width == other.Width && Height == other.Height && Dpi == other.Dpi;
}

public sealed record UIAutomationCapture(
    string CaptureId, UIAutomationWindowInfo Window, int PixelWidth, int PixelHeight,
    DateTimeOffset CapturedAt, [property: JsonIgnore] ReadOnlyMemory<byte> PngBytes)
{
    public string Describe() => new JsonObject
    {
        ["captureId"] = CaptureId,
        ["window"] = Window.WindowKey,
        ["windowTitle"] = Window.Title,
        ["processId"] = Window.ProcessId,
        ["width"] = PixelWidth,
        ["height"] = PixelHeight,
        ["capturedAt"] = CapturedAt.ToString("O"),
        ["coordinateSpace"] = "Pixels in this image, origin at its top-left. Use ui_click_at with this captureId.",
        ["captureMethod"] = "App-rendered window capture; other windows are not included.",
        ["note"] = "Some protected or GPU-rendered windows cannot supply a usable background image."
    }.ToJsonString();

    internal (int X, int Y) MapPoint(double x, double y, UIAutomationWindowInfo current, DateTimeOffset now)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)
            || x < 0 || y < 0 || x >= PixelWidth || y >= PixelHeight)
            throw new ArgumentOutOfRangeException(nameof(x), "Coordinates must be inside the captured image.");
        if (PixelWidth <= 0 || PixelHeight <= 0 || Window.Width <= 0 || Window.Height <= 0)
            throw new InvalidOperationException("The capture has invalid dimensions.");
        if (now - CapturedAt > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("This capture has expired. Take a fresh ui_screenshot before clicking.");
        if (!Window.HasSameGeometry(current))
            throw new InvalidOperationException("The captured window moved, resized, changed DPI, or was replaced. Take a fresh ui_screenshot.");
        return (
            checked(Window.Left + (int)Math.Floor(x * Window.Width / PixelWidth)),
            checked(Window.Top + (int)Math.Floor(y * Window.Height / PixelHeight)));
    }
}
