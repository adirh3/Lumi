#if !WINDOWS
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;

namespace Lumi.Services;

internal static class NativeWebViewPlatform
{
    internal const int MaxScreenshotDimension = 2048;
    internal const int MaxScreenshotPngBytes = 3 * 1024 * 1024;
    private const int MinScreenshotDimension = 1024;

    internal const string NativeDownloadUnsupportedReason =
        "Native browser downloads are currently unsupported on Linux and macOS. " +
        "Use curl with a verified direct HTTP(S) file URL instead; browser-session cookies " +
        "are not transferred automatically, so verify the response and saved file.";

    // Callers own tab selection, navigation guards and the action lock. View access stays on
    // the Avalonia UI thread; GTK work runs on the same default GLib context as the package.
    internal static async Task<byte[]> CapturePngAsync(
        NativeWebView view, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        Dispatcher.UIThread.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        var handle = view.TryGetPlatformHandle();
        byte[] bytes;
        try
        {
            if (OperatingSystem.IsLinux())
                bytes = await Gtk.CaptureAsync(RequireGtkWebView(handle), cancellationToken).ConfigureAwait(false);
            else if (OperatingSystem.IsMacOS() && handle is IAppleWKWebViewPlatformHandle apple)
                bytes = await Mac.CaptureAsync(apple, cancellationToken).ConfigureAwait(false);
            else
                throw UnsupportedHandle(handle);
        }
        catch (DllNotFoundException ex)
        {
            throw new PlatformNotSupportedException(
                "Native browser capture requires WebKitGTK 4.1 on Linux or WKWebView on macOS. " +
                $"A required native library is missing: {ex.Message}", ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new PlatformNotSupportedException(
                $"The native browser backend does not provide the required snapshot API: {ex.Message}", ex);
        }

        ReadPngDimensions(bytes);
        return bytes;
    }

    internal static void SetPageZoom(NativeWebView view, double zoom)
    {
        ArgumentNullException.ThrowIfNull(view);
        ValidatePageZoom(zoom);
        Dispatcher.UIThread.VerifyAccess();
        var handle = view.TryGetPlatformHandle();
        try
        {
            if (OperatingSystem.IsLinux())
                Gtk.SetPageZoom(RequireGtkWebView(handle), zoom);
            else if (OperatingSystem.IsMacOS() && handle is IAppleWKWebViewPlatformHandle apple)
                Mac.SetPageZoom(apple, zoom);
            else
                throw UnsupportedHandle(handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new PlatformNotSupportedException(
                "Native page zoom requires WebKitGTK 4.1 or WKWebView's public pageZoom API.", ex);
        }
    }

    internal static void ValidatePageZoom(double zoom)
    {
        if (!double.IsFinite(zoom) || zoom <= 0)
            throw new ArgumentOutOfRangeException(nameof(zoom), "Page zoom must be finite and greater than zero.");
    }

    internal static BrowserScreenshot PrepareScreenshotForModel(BrowserScreenshot screenshot)
    {
        ArgumentNullException.ThrowIfNull(screenshot);
        var (actualWidth, actualHeight) = ReadPngDimensions(screenshot.PngBytes);
        if (screenshot.Width != actualWidth || screenshot.Height != actualHeight)
            throw new InvalidOperationException("Screenshot metadata does not match the native PNG dimensions.");

        var originalLongestEdge = Math.Max(screenshot.Width, screenshot.Height);
        if (originalLongestEdge <= MaxScreenshotDimension && screenshot.PngBytes.Length <= MaxScreenshotPngBytes)
            return screenshot;

        using var original = SKBitmap.Decode(screenshot.PngBytes)
            ?? throw new InvalidOperationException("The native browser returned a PNG that could not be decoded.");
        var longestEdge = Math.Min(originalLongestEdge, MaxScreenshotDimension);
        var minimumEdge = Math.Min(originalLongestEdge, MinScreenshotDimension);
        while (true)
        {
            var scale = (double)longestEdge / originalLongestEdge;
            var width = Math.Max(1, (int)Math.Round(screenshot.Width * scale));
            var height = Math.Max(1, (int)Math.Round(screenshot.Height * scale));
            using var resized = original.Resize(
                new SKImageInfo(width, height, original.ColorType, original.AlphaType),
                new SKSamplingOptions(SKCubicResampler.Mitchell))
                ?? throw new InvalidOperationException("Could not resize the native browser screenshot.");
            using var image = SKImage.FromBitmap(resized);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("Could not encode the native browser screenshot as PNG.");
            if (png.Size <= MaxScreenshotPngBytes)
                return screenshot with { Width = width, Height = height, PngBytes = png.ToArray() };
            if (longestEdge <= minimumEdge)
                throw new InvalidOperationException(
                    "Screenshot exceeds the 3 MiB PNG budget at a readable size. " +
                    "Narrow the browser panel or use look/find to inspect text.");

            // Each attempt uses the original, with the same readability floor as WebView2.
            var reduction = Math.Min(0.9, Math.Sqrt((double)MaxScreenshotPngBytes / png.Size) * 0.95);
            longestEdge = Math.Max(minimumEdge, (int)(longestEdge * reduction));
        }
    }

    internal static nint RequireGtkWebView(IPlatformHandle? handle)
    {
        // GTK's X11 adapter exposes an XID through IPlatformHandle.Handle, NOT a GObject.
        // Only the package's public, typed WebKitWebView property is safe to pass to WebKit.
        if (handle is IGtkWebViewPlatformHandle { WebKitWebView: not 0 } gtk)
            return gtk.WebKitWebView;
        throw UnsupportedHandle(handle);
    }

    internal static (int Width, int Height) ReadPngDimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 33
            || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(8, 4)) != 13
            || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidOperationException("The native browser did not return a valid PNG screenshot.");
        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4));
        if (width < 1 || height < 1)
            throw new InvalidOperationException("The native browser returned an empty PNG screenshot.");
        return (width, height);
    }

    private static PlatformNotSupportedException UnsupportedHandle(IPlatformHandle? handle) => new(
        handle is null
            ? "The native browser is not initialized. Show the tab and wait for its native adapter."
            : $"Native capture/page zoom requires the public GTK WebKitWebView or Apple WKWebView handle. " +
              $"Backend '{handle.HandleDescriptor}' is not supported; XIDs, WPE and other native surfaces are not cast to web views.");

    private static void LogNativeFailure(string operation, Exception error)
    {
        // Trace listeners and subscriber code must never unwind through a native callback.
        try { Trace.TraceError("[NativeWebView] {0}: {1}", operation, error); }
        catch { }
    }

    private static class Gtk
    {
        private const string WebKit = "libwebkit2gtk-4.1.so.0";
        private const string GObject = "libgobject-2.0.so.0";
        private const string GLib = "libglib-2.0.so.0";
        private const string Gio = "libgio-2.0.so.0";
        private const string Cairo = "libcairo.so.2";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SourceCallback(nint data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DestroyCallback(nint data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SnapshotCallback(nint view, nint result, nint data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PngWriterCallback(nint data, nint bytes, uint length);

        private static readonly SourceCallback RunSource = OnSource;
        private static readonly DestroyCallback DestroySource = OnSourceDestroyed;
        private static readonly SnapshotCallback SnapshotReady = OnSnapshotReady;
        private static readonly PngWriterCallback PngWriter = OnWritePng;

        internal static Task<byte[]> CaptureAsync(nint view, CancellationToken token)
        {
            ValidateView(view);
            g_object_ref(view);
            var snapshot = new GtkSnapshot(view, token);
            try { Post(snapshot.Start); }
            catch (Exception ex) { snapshot.Abort(ex); }
            return snapshot.Completion.Task;
        }

        internal static void SetPageZoom(nint view, double zoom)
        {
            ValidateView(view);
            g_object_ref(view);
            try
            {
                Invoke(() =>
                {
                    webkit_web_view_set_zoom_level(view, zoom);
                    return true;
                });
            }
            finally
            {
                Invoke(() =>
                {
                    g_object_unref(view);
                    return true;
                });
            }
        }

        private static void ValidateView(nint view)
        {
            if (view == 0 || g_type_check_instance_is_a(view, webkit_web_view_get_type()) == 0)
                throw new PlatformNotSupportedException("The public GTK handle is not a WebKitGTK 4.1 WebKitWebView.");
        }

        private static T Invoke<T>(Func<T> action)
        {
            if (g_main_context_is_owner(g_main_context_default()) != 0)
                return action();
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try { completion.TrySetResult(action()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
            return completion.Task.GetAwaiter().GetResult();
        }

        private static void Post(Action action)
        {
            var data = GCHandle.Alloc(action);
            try
            {
                // Always enqueue. g_main_context_invoke can acquire the context and run GTK
                // code on the calling Avalonia thread instead of the package's GTK thread.
                if (g_idle_add_full(0, RunSource, GCHandle.ToIntPtr(data), DestroySource) == 0)
                    throw new InvalidOperationException("Could not schedule work on the native GTK context.");
            }
            catch
            {
                data.Free();
                throw;
            }
        }

        private static int OnSource(nint data)
        {
            try { ((Action)GCHandle.FromIntPtr(data).Target!)(); }
            catch (Exception ex) { LogNativeFailure("GTK dispatch callback", ex); }
            return 0;
        }

        private static void OnSourceDestroyed(nint data)
        {
            try { GCHandle.FromIntPtr(data).Free(); }
            catch (Exception ex) { LogNativeFailure("GTK source cleanup", ex); }
        }

        private static void OnSnapshotReady(nint view, nint result, nint data)
        {
            GtkSnapshot? snapshot = null;
            try
            {
                snapshot = (GtkSnapshot)GCHandle.FromIntPtr(data).Target!;
                snapshot.Finish(view, result);
            }
            catch (Exception ex)
            {
                LogNativeFailure("GTK snapshot callback", ex);
                try { snapshot?.Abort(ex); }
                catch (Exception cleanupError) { LogNativeFailure("GTK snapshot cleanup", cleanupError); }
            }
        }

        private sealed class GtkSnapshot
        {
            private nint _view;
            private nint _cancellable;
            private nint _callbackData;
            private readonly CancellationToken _token;
            private readonly CancellationTokenRegistration _cancellation;
            private bool _finished;
            internal TaskCompletionSource<byte[]> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal GtkSnapshot(nint retainedView, CancellationToken token)
            {
                _view = retainedView;
                _token = token;
                _cancellation = token.Register(static state => ((GtkSnapshot)state!).Cancel(), this);
            }

            private void Cancel()
            {
                Completion.TrySetCanceled(_token);
                try
                {
                    Post(() =>
                    {
                        if (!_finished && _cancellable != 0)
                            g_cancellable_cancel(_cancellable);
                    });
                }
                catch (Exception ex) { LogNativeFailure("Cancelling GTK snapshot", ex); }
            }

            internal void Start()
            {
                try
                {
                    if (_token.IsCancellationRequested)
                    {
                        Completion.TrySetCanceled(_token);
                        Release();
                        return;
                    }
                    _cancellable = g_cancellable_new();
                    if (_cancellable == 0)
                        throw new InvalidOperationException("Could not create a native snapshot cancellation object.");
                    _callbackData = GCHandle.ToIntPtr(GCHandle.Alloc(this));
                    // WEBKIT_SNAPSHOT_REGION_VISIBLE = 0; WEBKIT_SNAPSHOT_OPTIONS_NONE = 0.
                    webkit_web_view_get_snapshot(_view, 0, 0, _cancellable, SnapshotReady, _callbackData);
                }
                catch (Exception ex) { Abort(ex); }
            }

            internal void Finish(nint view, nint result)
            {
                nint surface = 0;
                nint error = 0;
                try
                {
                    // The finish call is required even after managed cancellation. Its surface
                    // and GError are transfer-full; userdata survives until this one callback.
                    surface = webkit_web_view_get_snapshot_finish(view, result, out error);
                    if (_token.IsCancellationRequested)
                        Completion.TrySetCanceled(_token);
                    else if (error != 0 || surface == 0)
                        throw new InvalidOperationException($"WebKitGTK snapshot failed: {ReadError(error)}");
                    else
                        Completion.TrySetResult(EncodeSurface(surface));
                }
                catch (Exception ex)
                {
                    LogNativeFailure("Capturing GTK snapshot", ex);
                    Completion.TrySetException(ex);
                }
                finally
                {
                    if (surface != 0)
                        cairo_surface_destroy(surface);
                    if (error != 0)
                        g_error_free(error);
                    Release();
                }
            }

            internal void Abort(Exception error)
            {
                LogNativeFailure("Starting GTK snapshot", error);
                Completion.TrySetException(error);
                Release();
            }

            private void Release()
            {
                if (_finished)
                    return;
                _finished = true;
                _cancellation.Dispose();
                if (_callbackData != 0)
                {
                    GCHandle.FromIntPtr(_callbackData).Free();
                    _callbackData = 0;
                }
                if (_cancellable != 0)
                {
                    g_object_unref(_cancellable);
                    _cancellable = 0;
                }
                if (_view != 0)
                {
                    g_object_unref(_view);
                    _view = 0;
                }
            }
        }

        private sealed class PngEncoding : IDisposable
        {
            internal MemoryStream Stream { get; } = new();
            internal Exception? Error { get; set; }
            public void Dispose() => Stream.Dispose();
        }

        private static byte[] EncodeSurface(nint surface)
        {
            using var encoding = new PngEncoding();
            var data = GCHandle.Alloc(encoding);
            try
            {
                var status = cairo_surface_write_to_png_stream(surface, PngWriter, GCHandle.ToIntPtr(data));
                if (status != 0)
                    throw new InvalidOperationException(
                        $"Could not encode the WebKitGTK snapshot: {Marshal.PtrToStringUTF8(cairo_status_to_string(status))}",
                        encoding.Error);
                return encoding.Stream.ToArray();
            }
            finally { data.Free(); }
        }

        private static int OnWritePng(nint data, nint bytes, uint length)
        {
            PngEncoding? encoding = null;
            try
            {
                encoding = (PngEncoding)GCHandle.FromIntPtr(data).Target!;
                var buffer = new byte[checked((int)length)];
                Marshal.Copy(bytes, buffer, 0, buffer.Length);
                encoding.Stream.Write(buffer);
                return 0;
            }
            catch (Exception ex)
            {
                if (encoding is not null)
                    encoding.Error = ex;
                LogNativeFailure("Cairo PNG callback", ex);
                return 11; // CAIRO_STATUS_WRITE_ERROR.
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GError
        {
            internal uint Domain;
            internal int Code;
            internal nint Message;
        }

        private static string ReadError(nint error) => error == 0
            ? "The native operation returned no result."
            : Marshal.PtrToStringUTF8(Marshal.PtrToStructure<GError>(error).Message) ?? "Unknown WebKitGTK error.";

        [DllImport(WebKit, CallingConvention = CallingConvention.Cdecl)]
        private static extern nuint webkit_web_view_get_type();
        [DllImport(WebKit, CallingConvention = CallingConvention.Cdecl)]
        private static extern void webkit_web_view_set_zoom_level(nint view, double zoom);
        [DllImport(GObject, CallingConvention = CallingConvention.Cdecl)]
        private static extern int g_type_check_instance_is_a(nint instance, nuint type);
        [DllImport(GObject, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint g_object_ref(nint instance);
        [DllImport(GObject, CallingConvention = CallingConvention.Cdecl)]
        private static extern void g_object_unref(nint instance);
        [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint g_main_context_default();
        [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int g_main_context_is_owner(nint context);
        [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint g_idle_add_full(int priority, SourceCallback callback, nint data, DestroyCallback destroy);
        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint g_cancellable_new();
        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
        private static extern void g_cancellable_cancel(nint cancellable);
        [DllImport(WebKit, CallingConvention = CallingConvention.Cdecl)]
        private static extern void webkit_web_view_get_snapshot(
            nint view, int region, int options, nint cancellable, SnapshotCallback callback, nint data);
        [DllImport(WebKit, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint webkit_web_view_get_snapshot_finish(nint view, nint result, out nint error);
        [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void g_error_free(nint error);
        [DllImport(Cairo, CallingConvention = CallingConvention.Cdecl)]
        private static extern int cairo_surface_write_to_png_stream(nint surface, PngWriterCallback callback, nint data);
        [DllImport(Cairo, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint cairo_status_to_string(int status);
        [DllImport(Cairo, CallingConvention = CallingConvention.Cdecl)]
        private static extern void cairo_surface_destroy(nint surface);
    }

    private static class Mac
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const string SystemLibrary = "/usr/lib/libSystem.B.dylib";
        private static readonly nint WebViewClass = objc_getClass("WKWebView");
        private static readonly nint BitmapClass = objc_getClass("NSBitmapImageRep");
        private static readonly nint DictionaryClass = objc_getClass("NSDictionary");
        private static readonly nint PoolClass = objc_getClass("NSAutoreleasePool");
        private static readonly nint IsKindOfClass = sel_registerName("isKindOfClass:");
        private static readonly nint RespondsToSelector = sel_registerName("respondsToSelector:");
        private static readonly nint Snapshot = sel_registerName("takeSnapshotWithConfiguration:completionHandler:");
        private static readonly nint PageZoom = sel_registerName("setPageZoom:");
        private static readonly nint Alloc = sel_registerName("alloc");
        private static readonly nint Init = sel_registerName("init");
        private static readonly nint InitWithData = sel_registerName("initWithData:");
        private static readonly nint TiffRepresentation = sel_registerName("TIFFRepresentation");
        private static readonly nint PngRepresentation = sel_registerName("representationUsingType:properties:");
        private static readonly nint Dictionary = sel_registerName("dictionary");
        private static readonly nint Length = sel_registerName("length");
        private static readonly nint Bytes = sel_registerName("bytes");
        private static readonly nint Release = sel_registerName("release");
        private static readonly nint Drain = sel_registerName("drain");
        private static readonly nint LocalizedDescription = sel_registerName("localizedDescription");
        private static readonly nint Utf8String = sel_registerName("UTF8String");

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SnapshotCallback(nint block, nint image, nint error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CopyBlockCallback(nint destination, nint source);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DisposeBlockCallback(nint block);

        private static readonly SnapshotCallback SnapshotReady = OnSnapshotReady;
        private static readonly CopyBlockCallback CopyBlock = OnCopyBlock;
        private static readonly DisposeBlockCallback DisposeBlock = OnDisposeBlock;
        private static readonly int StateOffset = Marshal.OffsetOf<SnapshotBlock>(nameof(SnapshotBlock.State)).ToInt32();
        private static readonly nint StackBlockClass = NativeLibrary.GetExport(
            NativeLibrary.Load(SystemLibrary), "_NSConcreteStackBlock");
        // One process-lifetime descriptor/signature, specific to void (^)(NSImage *, NSError *).
        private static readonly nint Descriptor = CreateDescriptor();

        internal static void SetPageZoom(IAppleWKWebViewPlatformHandle handle, double zoom)
        {
            var view = handle.GetWKWebViewRetained();
            if (view == 0)
                throw new InvalidOperationException("The native WKWebView has been destroyed.");
            try
            {
                if (WebViewClass == 0 || SendBool(view, IsKindOfClass, WebViewClass) == 0)
                    throw new PlatformNotSupportedException("The public Apple handle is not a WKWebView.");
                if (SendBool(view, RespondsToSelector, PageZoom) == 0)
                    throw new PlatformNotSupportedException("This WKWebView does not support page zoom (macOS 11.0+ required).");
                // CGFloat is double on both supported 64-bit macOS architectures.
                SendDouble(view, PageZoom, zoom);
            }
            finally { SendVoid(view, Release); }
        }

        internal static Task<byte[]> CaptureAsync(IAppleWKWebViewPlatformHandle handle, CancellationToken token)
        {
            var retainedView = handle.GetWKWebViewRetained();
            if (retainedView == 0)
                throw new InvalidOperationException("The native WKWebView has been destroyed.");
            var capture = new MacSnapshot(retainedView, token);
            nint block = 0;
            try
            {
                if (WebViewClass == 0 || SendBool(retainedView, IsKindOfClass, WebViewClass) == 0)
                    throw new PlatformNotSupportedException("The public Apple handle is not a WKWebView.");
                if (SendBool(retainedView, RespondsToSelector, Snapshot) == 0)
                    throw new PlatformNotSupportedException("This WKWebView does not support native viewport snapshots (macOS 10.13+ required).");
                if (token.IsCancellationRequested)
                    capture.Completion.TrySetCanceled(token);
                else
                {
                    block = CreateBlock(capture);
                    // A nil configuration captures the visible WKWebView bounds. No CGRect
                    // return/argument marshalling is needed on either arm64 or x64.
                    SendSnapshot(retainedView, Snapshot, 0, block);
                }
            }
            catch (Exception ex)
            {
                LogNativeFailure("Starting WKWebView snapshot", ex);
                capture.Completion.TrySetException(ex);
            }
            finally
            {
                if (block != 0)
                    _Block_release(block);
                capture.ReleaseIfUnused();
            }
            return capture.Completion.Task;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SnapshotBlock
        {
            internal nint Isa;
            internal int Flags;
            internal int Reserved;
            internal nint Invoke;
            internal nint Descriptor;
            internal nint State;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SnapshotBlockDescriptor
        {
            internal nuint Reserved;
            internal nuint Size;
            internal nint Copy;
            internal nint Dispose;
            internal nint Signature;
        }

        private static nint CreateDescriptor()
        {
            var descriptor = Marshal.AllocHGlobal(Marshal.SizeOf<SnapshotBlockDescriptor>());
            Marshal.StructureToPtr(new SnapshotBlockDescriptor
            {
                Size = (nuint)Marshal.SizeOf<SnapshotBlock>(),
                Copy = Marshal.GetFunctionPointerForDelegate(CopyBlock),
                Dispose = Marshal.GetFunctionPointerForDelegate(DisposeBlock),
                Signature = Marshal.StringToHGlobalAnsi("v@?@@")
            }, descriptor, false);
            return descriptor;
        }

        private static nint CreateBlock(MacSnapshot capture)
        {
            var state = GCHandle.Alloc(capture);
            var stack = Marshal.AllocHGlobal(Marshal.SizeOf<SnapshotBlock>());
            try
            {
                Marshal.StructureToPtr(new SnapshotBlock
                {
                    Isa = StackBlockClass,
                    Flags = (1 << 25) | (1 << 30), // BLOCK_HAS_COPY_DISPOSE | BLOCK_HAS_SIGNATURE.
                    Invoke = Marshal.GetFunctionPointerForDelegate(SnapshotReady),
                    Descriptor = Descriptor,
                    State = GCHandle.ToIntPtr(state)
                }, stack, false);
                var copied = _Block_copy(stack);
                if (copied == 0)
                    throw new InvalidOperationException("Could not allocate the WKWebView snapshot completion block.");
                if (Marshal.ReadIntPtr(copied, StateOffset) == 0)
                {
                    _Block_release(copied);
                    throw new InvalidOperationException("Could not retain WKWebView snapshot completion state.");
                }
                return copied;
            }
            finally
            {
                Marshal.FreeHGlobal(stack);
                state.Free();
            }
        }

        private static void OnCopyBlock(nint destination, nint source)
        {
            MacSnapshot? capture = null;
            try
            {
                capture = (MacSnapshot)GCHandle.FromIntPtr(Marshal.ReadIntPtr(source, StateOffset)).Target!;
                var state = GCHandle.Alloc(capture);
                Marshal.WriteIntPtr(destination, StateOffset, GCHandle.ToIntPtr(state));
                capture.AddBlock();
            }
            catch (Exception ex)
            {
                LogNativeFailure("Copying WKWebView snapshot block", ex);
                try
                {
                    Marshal.WriteIntPtr(destination, StateOffset, 0);
                    capture?.Completion.TrySetException(ex);
                }
                catch (Exception cleanupError) { LogNativeFailure("WKWebView block copy cleanup", cleanupError); }
            }
        }

        private static void OnDisposeBlock(nint block)
        {
            try
            {
                var data = Marshal.ReadIntPtr(block, StateOffset);
                if (data == 0)
                    return;
                var state = GCHandle.FromIntPtr(data);
                var capture = (MacSnapshot)state.Target!;
                Marshal.WriteIntPtr(block, StateOffset, 0);
                state.Free();
                capture.RemoveBlock();
            }
            catch (Exception ex) { LogNativeFailure("Releasing WKWebView snapshot block", ex); }
        }

        private static void OnSnapshotReady(nint block, nint image, nint error)
        {
            MacSnapshot? capture = null;
            try
            {
                var data = Marshal.ReadIntPtr(block, StateOffset);
                if (data == 0)
                    return;
                capture = (MacSnapshot)GCHandle.FromIntPtr(data).Target!;
                capture.Finish(image, error);
            }
            catch (Exception ex)
            {
                LogNativeFailure("WKWebView snapshot callback", ex);
                capture?.Completion.TrySetException(ex);
            }
        }

        private sealed class MacSnapshot
        {
            private nint _view;
            private readonly CancellationToken _token;
            private readonly CancellationTokenRegistration _cancellation;
            private int _blocks;
            private int _released;
            internal TaskCompletionSource<byte[]> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal MacSnapshot(nint retainedView, CancellationToken token)
            {
                _view = retainedView;
                _token = token;
                _cancellation = token.Register(static state =>
                {
                    var capture = (MacSnapshot)state!;
                    // WK snapshot has no cancellation API. Complete managed cancellation
                    // now, but retain the view and copied block until native completion/release.
                    capture.Completion.TrySetCanceled(capture._token);
                }, this);
            }

            internal void AddBlock() => Interlocked.Increment(ref _blocks);
            internal void RemoveBlock()
            {
                if (Interlocked.Decrement(ref _blocks) == 0)
                    ReleaseIfUnused();
            }

            internal void Finish(nint image, nint error)
            {
                if (_token.IsCancellationRequested)
                    Completion.TrySetCanceled(_token);
                else if (error != 0)
                    Completion.TrySetException(new InvalidOperationException(
                        $"WKWebView snapshot failed: {ReadNSError(error)}"));
                else if (image == 0)
                    Completion.TrySetException(new InvalidOperationException("WKWebView returned no snapshot image."));
                else
                {
                    Dispatcher.UIThread.VerifyAccess();
                    Completion.TrySetResult(EncodeImage(image));
                }
                // The block's final dispose, after this callback, owns view/userdata cleanup.
            }

            internal void ReleaseIfUnused()
            {
                if (Volatile.Read(ref _blocks) != 0 || Interlocked.Exchange(ref _released, 1) != 0)
                    return;
                if (!Completion.Task.IsCompleted)
                    Completion.TrySetException(new InvalidOperationException(
                        "WKWebView released its snapshot completion handler without returning an image."));
                _cancellation.Dispose();
                var view = Interlocked.Exchange(ref _view, 0);
                if (Dispatcher.UIThread.CheckAccess())
                    SendVoid(view, Release);
                else
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { SendVoid(view, Release); }
                        catch (Exception ex) { LogNativeFailure("Releasing retained WKWebView", ex); }
                    });
            }
        }

        private static string ReadNSError(nint error)
        {
            var description = SendPointer(error, LocalizedDescription);
            return Marshal.PtrToStringUTF8(SendPointer(description, Utf8String)) ?? "Unknown WebKit error.";
        }

        private static byte[] EncodeImage(nint image)
        {
            var pool = SendPointer(SendPointer(PoolClass, Alloc), Init);
            nint bitmap = 0;
            try
            {
                var tiff = SendPointer(image, TiffRepresentation);
                if (tiff == 0)
                    throw new InvalidOperationException("The WKWebView snapshot has no native image representation.");
                bitmap = SendPointerArgument(SendPointer(BitmapClass, Alloc), InitWithData, tiff);
                if (bitmap == 0)
                    throw new InvalidOperationException("Could not decode the native WKWebView snapshot image.");
                var png = SendIntegerAndPointer(bitmap, PngRepresentation, 4 /* NSBitmapImageFileTypePNG */,
                    SendPointer(DictionaryClass, Dictionary));
                var length = SendLength(png, Length);
                var data = SendPointer(png, Bytes);
                if (png == 0 || data == 0 || length == 0 || length > int.MaxValue)
                    throw new InvalidOperationException("WKWebView could not encode its snapshot as a usable PNG.");
                var bytes = new byte[(int)length];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                if (bitmap != 0)
                    SendVoid(bitmap, Release);
                SendVoid(pool, Drain);
            }
        }

        [DllImport(ObjC, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(ObjC, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
        private static extern nint SendPointer(nint receiver, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
        private static extern nint SendPointerArgument(nint receiver, nint selector, nint argument);
        [DllImport(ObjC, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
        private static extern nint SendIntegerAndPointer(nint receiver, nint selector, nuint type, nint properties);
        [DllImport(ObjC, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
        private static extern byte SendBool(nint receiver, nint selector, nint argument);
        [DllImport(ObjC, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
        private static extern nuint SendLength(nint receiver, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SendVoid(nint receiver, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SendDouble(nint receiver, nint selector, double argument);
        [DllImport(ObjC, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SendSnapshot(nint receiver, nint selector, nint configuration, nint block);
        [DllImport(SystemLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint _Block_copy(nint block);
        [DllImport(SystemLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void _Block_release(nint block);
    }
}
#endif
