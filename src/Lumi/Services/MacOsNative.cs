using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lumi.Services;

/// <summary>
/// macOS-only native integration via the Objective-C runtime. On macOS the Dock icon is taken from
/// the <c>.app</c> bundle's <c>Info.plist</c> (<c>CFBundleIconFile</c>); a plain <c>dotnet publish</c>
/// or otherwise unbundled launch has no bundle, so Lumi shows the generic Dock icon. This sets the
/// running application's Dock icon at runtime so Lumi is branded regardless of how it was launched
/// (and reinforces the bundled case). Everything here is best-effort and no-ops off macOS.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacOsNative
{
    // objc_getClass / sel_registerName / objc_msgSend all live in libobjc; the NSData/NSImage/
    // NSApplication classes are already registered because Avalonia's macOS backend loads AppKit.
    private const string ObjC = "/usr/lib/libobjc.dylib";

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass(string name);

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr GetSelector(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendData(IntPtr receiver, IntPtr selector, IntPtr bytes, UIntPtr length);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendRep(IntPtr receiver, IntPtr selector, UIntPtr arg1, IntPtr arg2);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern UIntPtr MsgSendReturnUInt(IntPtr receiver, IntPtr selector);

    // NSBitmapImageFileType.png — the value passed to -representationUsingType:properties:.
    private const int NSBitmapImageFileTypePng = 4;

    /// <summary>
    /// Sets the running app's Dock icon from raw image bytes (PNG/TIFF/etc.). Best-effort: any failure
    /// leaves the current icon untouched. Must be called on the UI (main) thread.
    /// </summary>
    public static void TrySetDockIcon(byte[]? imageBytes)
    {
        if (imageBytes is not { Length: > 0 } || !OperatingSystem.IsMacOS())
            return;

        var handle = GCHandle.Alloc(imageBytes, GCHandleType.Pinned);
        try
        {
            var bytesPtr = handle.AddrOfPinnedObject();

            // NSData* data = [NSData dataWithBytes:imageBytes length:imageBytes.Length];
            var data = MsgSendData(
                GetClass("NSData"), GetSelector("dataWithBytes:length:"), bytesPtr, (UIntPtr)imageBytes.Length);
            if (data == IntPtr.Zero)
                return;

            // NSImage* image = [[NSImage alloc] initWithData:data];
            var allocated = MsgSend(GetClass("NSImage"), GetSelector("alloc"));
            var image = MsgSend(allocated, GetSelector("initWithData:"), data);
            if (image == IntPtr.Zero)
                return;

            // [[NSApplication sharedApplication] setApplicationIconImage:image];
            var app = MsgSend(GetClass("NSApplication"), GetSelector("sharedApplication"));
            if (app != IntPtr.Zero)
                MsgSend(app, GetSelector("setApplicationIconImage:"), image);
        }
        catch
        {
            // Native interop is best-effort; never let a Dock-icon tweak crash startup.
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Converts arbitrary image bytes (notably macOS clipboard <c>public.tiff</c>) to PNG using AppKit's
    /// <c>NSBitmapImageRep</c>. Skia — the codec behind Avalonia's <c>Bitmap</c> — cannot decode TIFF, so
    /// macOS screenshots pasted from the clipboard must be transcoded to PNG first. Returns null off macOS
    /// or on any failure (best-effort). Safe to call from the UI thread.
    /// </summary>
    public static byte[]? TryConvertImageToPng(byte[]? imageBytes)
    {
        if (imageBytes is not { Length: > 0 } || !OperatingSystem.IsMacOS())
            return null;

        var handle = GCHandle.Alloc(imageBytes, GCHandleType.Pinned);
        try
        {
            var bytesPtr = handle.AddrOfPinnedObject();

            // NSData* src = [NSData dataWithBytes:imageBytes length:imageBytes.Length];
            var srcData = MsgSendData(
                GetClass("NSData"), GetSelector("dataWithBytes:length:"), bytesPtr, (UIntPtr)imageBytes.Length);
            if (srcData == IntPtr.Zero)
                return null;

            // NSBitmapImageRep* rep = [NSBitmapImageRep imageRepWithData:src];
            var rep = MsgSend(GetClass("NSBitmapImageRep"), GetSelector("imageRepWithData:"), srcData);
            if (rep == IntPtr.Zero)
                return null;

            // NSData* png = [rep representationUsingType:NSBitmapImageFileTypePNG properties:nil];
            var png = MsgSendRep(
                rep, GetSelector("representationUsingType:properties:"), (UIntPtr)NSBitmapImageFileTypePng, IntPtr.Zero);
            if (png == IntPtr.Zero)
                return null;

            // Copy [png bytes] / [png length] out while the autoreleased NSData is still alive.
            var pngPtr = MsgSend(png, GetSelector("bytes"));
            var pngLen = (ulong)MsgSendReturnUInt(png, GetSelector("length"));
            if (pngPtr == IntPtr.Zero || pngLen == 0 || pngLen > int.MaxValue)
                return null;

            var result = new byte[pngLen];
            Marshal.Copy(pngPtr, result, 0, (int)pngLen);
            return result;
        }
        catch
        {
            // Best-effort transcode; fall back to "no image" on any failure.
            return null;
        }
        finally
        {
            handle.Free();
        }
    }
}
