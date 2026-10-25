using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using SkiaSharp;

namespace Lumi.Views;

public static partial class ThemeReveal
{
    // An 8K screen's worth, and then some.
    private const long MaxPicturePixels = 40_000_000;

    /// <summary>
    /// The window as it is on screen, in physical pixels: what it shows over its material, which no drawing of
    /// Lumi's own includes. Only while the window is in front, where nothing else stands over it on screen.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static SKImage? TakeScreenPicture(Window window)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero
            || GetForegroundWindow() != hwnd || IsIconic(hwnd) || !GetClientRect(hwnd, out var client))
        {
            return null;
        }

        var corner = new NativePoint();
        if (!ClientToScreen(hwnd, ref corner))
            return null;
        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        var expected = PixelSize.FromSize(window.ClientSize, window.RenderScaling);
        if (width <= 0 || height <= 0 || (long)width * height > MaxPicturePixels
            || Math.Abs(width - expected.Width) > 2 || Math.Abs(height - expected.Height) > 2)
        {
            return null;
        }

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
            return null;
        var memory = CreateCompatibleDC(screen);
        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height, // top-down rows
            Planes = 1,
            BitCount = 32,
        };
        var bits = IntPtr.Zero;
        var bitmap = memory == IntPtr.Zero ? IntPtr.Zero : CreateDIBSection(screen, ref header, 0, out bits, IntPtr.Zero, 0);
        var previous = bitmap == IntPtr.Zero ? IntPtr.Zero : SelectObject(memory, bitmap);
        try
        {
            if (previous == IntPtr.Zero || !BitBlt(memory, 0, 0, width, height, screen, corner.X, corner.Y, SourceCopy))
                return null;
            GdiFlush();
            // A screen with nothing on it (a session without a display) has no picture to give.
            if (IsBlank(bits, width, height))
                return null;
            return SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque), bits, width * 4);
        }
        finally
        {
            if (previous != IntPtr.Zero)
                SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero)
                DeleteObject(bitmap);
            if (memory != IntPtr.Zero)
                DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private const int SourceCopy = 0x00CC0020;

    private static bool IsBlank(IntPtr bits, int width, int height)
    {
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                var offset = ((long)(height * (2 * y + 1) / 32) * width + width * (2 * x + 1) / 32) * 4;
                if ((Marshal.ReadInt32(bits, (int)offset) & 0x00FFFFFF) != 0)
                    return false;
            }
        }
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int operation);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();
}
