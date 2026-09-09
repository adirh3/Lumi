using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
#if WINDOWS
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Text;
#endif

namespace Lumi.Views;

/// <summary>
/// Hosts a registered Windows Shell preview handler. Construct, attach and dispose on the UI thread.
/// Failures are posted to the UI thread, so subscribers can safely replace this control.
/// </summary>
public sealed class NativeFilePreviewHost : NativeControlHost, IDisposable
{
    private readonly string _filePath;
    private bool _attached;
    private bool _disposed;
    private bool _failureReported;
    private int _attachmentVersion;

    public NativeFilePreviewHost(string filePath)
    {
        _filePath = filePath;
    }

    public event Action<string>? PreviewFailed;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = true;
        _failureReported = false;
        var version = ++_attachmentVersion;
        if (_disposed)
            return;

#if WINDOWS
        try
        {
            // Avalonia creates its composition attachment in a queued UpdateHost call. Probe
            // first: without Windows 8+ manifest compatibility, layered child creation throws
            // there, outside our preview initialization, and would terminate the application.
            var parent = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle();
            if (parent is not { HandleDescriptor: "HWND", Handle: not 0 })
                throw new PlatformNotSupportedException("A native Windows preview window is not available.");
            VerifyNativeWindowSupport(parent.Handle);
            base.OnAttachedToVisualTree(e);

            _topLevel = TopLevel.GetTopLevel(this);
            if (_topLevel is not null)
                _topLevel.ScalingChanged += OnScalingChanged;
            LayoutUpdated += OnLayoutUpdated;
        }
        catch (Exception ex) when (IsPreviewException(ex))
        {
            ReportFailure(ex.Message);
            return;
        }
#else
        base.OnAttachedToVisualTree(e);
#endif

        // NativeControlHost must finish creating its attachment before a failure can replace us.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_attached || _disposed || version != _attachmentVersion)
                return;
#if WINDOWS
            StartPreview();
#else
            ReportFailure("Native document previews are only supported on Windows.");
#endif
        }, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        ++_attachmentVersion;
#if WINDOWS
        StopObservingLayout();
        var error = ReleasePreview();
#endif
        base.OnDetachedFromVisualTree(e);
#if WINDOWS
        if (error is not null)
            ReportFailure(error);
#endif
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;

        _disposed = true;
        ++_attachmentVersion;
#if WINDOWS
        StopObservingLayout();
        var error = ReleasePreview();
        if (error is not null)
            ReportFailure(error);
#endif
        // Avalonia owns the child HWND and destroys it when the visual is detached.
        GC.SuppressFinalize(this);
    }

    private void ReportFailure(string message)
    {
        if (_failureReported)
            return;

        _failureReported = true;
        var version = _attachmentVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (version == _attachmentVersion)
                PreviewFailed?.Invoke(message);
        });
    }

#if WINDOWS
    private const string PreviewHandlerInterfaceId = "{8895B1C6-B41F-4C1C-A562-0D564250836F}";
    private const uint StgmRead = 0;
    private IPlatformHandle? _nativeControl;
    private TopLevel? _topLevel;
    private IPreviewHandler? _handler;
    private FileStream? _file;
    private PreviewHandlerSite? _site;
    private bool _siteAssigned;
    private bool _resizeQueued;
    private bool _handlerCallActive;
    private bool _releasePending;
    private int _previewWidth = -1;
    private int _previewHeight = -1;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _nativeControl = base.CreateNativeControlCore(parent);
        return _nativeControl;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        var error = ReleasePreview();
        _nativeControl = null;
        base.DestroyNativeControlCore(control);
        if (error is not null)
            ReportFailure(error);
    }

    private void StartPreview()
    {
        if (_handlerCallActive)
            return;

        var version = _attachmentVersion;
        _handlerCallActive = true;
        Exception? failure = null;
        try
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Windows preview handlers require an STA UI thread.");
            if (_nativeControl is not { HandleDescriptor: "HWND", Handle: not 0 })
                throw new PlatformNotSupportedException("A native Windows preview window is not available.");

            ArgumentException.ThrowIfNullOrWhiteSpace(_filePath);
            var path = Path.GetFullPath(_filePath);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new NotSupportedException("A document preview requires a file, not a directory.");
            var clsid = FindPreviewHandler(Path.GetExtension(path));
            var handler = CreatePreviewHandler(clsid);
            _handler = handler;
            if (_releasePending)
                return;

            if (handler is IObjectWithSite objectWithSite)
            {
                _site = new PreviewHandlerSite();
                // Also clear the site if SetSite partially succeeds and then returns an error.
                _siteAssigned = true;
                objectWithSite.SetSite(_site);
                if (_releasePending)
                    return;
            }

            if (handler is IInitializeWithStream withStream)
            {
                // Office and many editors save by replacing the original file. Deny-none Shell
                // streams still deny deletion, so supply an IStream over a share-delete handle.
                _file = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                withStream.Initialize(new ReadOnlyPreviewStream(_file), StgmRead);
            }
            else if (handler is IInitializeWithFile withFile)
            {
                withFile.Initialize(path, StgmRead);
            }
            else if (handler is IInitializeWithItem withItem)
            {
                var item = CreateShellItem(path);
                try
                {
                    if (!_releasePending)
                        withItem.Initialize(item, StgmRead);
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
            else
            {
                throw new NotSupportedException("The registered preview handler has no supported initialization interface.");
            }

            if (_releasePending)
                return;
            TryUpdateNativeControlPosition();
            var rect = GetPreviewRect();
            handler.SetWindow(_nativeControl.Handle, ref rect);
            if (_releasePending)
                return;
            handler.DoPreview();
            if (_releasePending)
                return;
            handler.SetRect(ref rect);
            _previewWidth = rect.Right;
            _previewHeight = rect.Bottom;
        }
        catch (Exception ex) when (IsPreviewException(ex))
        {
            failure = ex;
        }
        finally
        {
            FinishHandlerCall(version, failure);
        }
    }

    private void OnScalingChanged(object? sender, EventArgs e) => QueueResize();

    private void OnLayoutUpdated(object? sender, EventArgs e) => QueueResize();

    private void QueueResize()
    {
        if (_resizeQueued || _handler is null)
            return;

        _resizeQueued = true;
        var version = _attachmentVersion;
        Dispatcher.UIThread.Post(() =>
        {
            _resizeQueued = false;
            if (!_attached || _disposed || version != _attachmentVersion || _handler is null || _handlerCallActive)
                return;

            _handlerCallActive = true;
            Exception? failure = null;
            try
            {
                // NativeControlHost applies the layout transform and render scaling. Always read
                // the resulting client pixels, rather than independently rounding Avalonia DIPs.
                TryUpdateNativeControlPosition();
                var rect = GetPreviewRect();
                if (_previewWidth == rect.Right && _previewHeight == rect.Bottom)
                    return;

                _handler.SetRect(ref rect);
                _previewWidth = rect.Right;
                _previewHeight = rect.Bottom;
            }
            catch (Exception ex) when (IsPreviewException(ex))
            {
                failure = ex;
            }
            finally
            {
                FinishHandlerCall(version, failure);
            }
        }, DispatcherPriority.Background);
    }

    private NativeRect GetPreviewRect()
    {
        if (!GetClientRect(_nativeControl!.Handle, out var rect))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return rect;
    }

    private void StopObservingLayout()
    {
        LayoutUpdated -= OnLayoutUpdated;
        if (_topLevel is not null)
            _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;
    }

    private void FinishHandlerCall(int version, Exception? failure)
    {
        _handlerCallActive = false;
        if (_releasePending || failure is not null)
        {
            var cleanupError = ReleasePreview();
            var message = failure is null ? cleanupError
                : cleanupError is null ? failure.Message : $"{failure.Message} {cleanupError}";
            if (message is not null)
            {
                if (version == _attachmentVersion)
                    ReportFailure(message);
                else
                    System.Diagnostics.Trace.TraceWarning("Document preview teardown: {0}", message);
            }
        }

        RestartAfterReattach(version);
    }

    private void RestartAfterReattach(int version)
    {
        // COM calls can pump messages, including detach/reattach. Wait until the old handler's
        // call and cleanup have completed before trying to start a preview on the new attachment.
        if (version != _attachmentVersion && _attached && !_disposed)
        {
            var currentVersion = _attachmentVersion;
            Dispatcher.UIThread.Post(() =>
            {
                if (_attached && !_disposed && currentVersion == _attachmentVersion && _handler is null)
                    StartPreview();
            }, DispatcherPriority.Loaded);
        }
    }

    private string? ReleasePreview()
    {
        if (_handlerCallActive)
        {
            _releasePending = true;
            return null;
        }

        _releasePending = false;
        // Interface casts share the handler's RCW: release that owning reference only once.
        var handler = _handler;
        var file = _file;
        var clearSite = _siteAssigned;
        _handler = null;
        _file = null;
        _siteAssigned = false;
        _previewWidth = _previewHeight = -1;
        var version = _attachmentVersion;
        _handlerCallActive = true;
        string? error = null;

        try
        {
            if (handler is not null)
            {
                try
                {
                    handler.Unload();
                }
                catch (Exception ex) when (IsPreviewException(ex))
                {
                    error = ex.Message;
                }
                finally
                {
                    try
                    {
                        if (clearSite && handler is IObjectWithSite objectWithSite)
                            objectWithSite.SetSite(null);
                    }
                    catch (Exception ex) when (IsPreviewException(ex))
                    {
                        error = error is null ? ex.Message : $"{error} {ex.Message}";
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(handler);
                    }
                }
            }
        }
        finally
        {
            try
            {
                _site = null;
                file?.Dispose();
            }
            finally
            {
                _handlerCallActive = false;
                _releasePending = false;
                RestartAfterReattach(version);
            }
        }

        return error;
    }

    private static bool IsPreviewException(Exception ex) =>
        ex is COMException or Win32Exception or IOException or UnauthorizedAccessException
            or SecurityException or NotSupportedException or ArgumentException
            or InvalidOperationException or InvalidCastException or NotImplementedException;

    internal static void VerifyNativeWindowSupport(nint parent)
    {
        // Match Avalonia's Win32NativeControlHost: composition windows have no redirection
        // bitmap and need a layered child attachment; software-rendered windows do not.
        const int noRedirectionBitmap = 0x00200000;
        const uint layered = 0x00080000;
        var extendedStyle = (GetWindowLong(parent, -20 /* GWL_EXSTYLE */) & noRedirectionBitmap) != 0
            ? layered : 0u;
        var probe = CreateWindowEx(extendedStyle, "STATIC", null, 0x40000000 /* WS_CHILD */,
            0, 0, 1, 1, parent, 0, 0, 0);
        if (probe == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, extendedStyle == layered
                ? "Windows could not create the document preview window. Lumi's executable must declare Windows 8 or later support in its application manifest. "
                    + new Win32Exception(error).Message
                : "Windows could not create the document preview window. " + new Win32Exception(error).Message);
        }

        if (!DestroyWindow(probe))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    // Native code calls members that managed reachability analysis cannot see. Preserve complete
    // interface vtables (including unused slots), both CCW implementations, and their ABI structs.
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IPreviewHandler))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IObjectWithSite))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IInitializeWithStream))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IInitializeWithFile))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IInitializeWithItem))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IPreviewHandlerFrame))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PreviewHandlerSite))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PreviewHandlerFrameInfo))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(NativeRect))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IStream))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ReadOnlyPreviewStream))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(STATSTG))]
    [UnconditionalSuppressMessage("Trimming", "IL2050",
        Justification = "The handler's queried COM interfaces, complete vtables, managed callbacks, and ABI structs are explicitly rooted with DynamicDependency.")]
    private static IPreviewHandler CreatePreviewHandler(Guid clsid)
    {
        // LOCAL_SERVER activates the registered surrogate (normally prevhost.exe), not a DLL
        // in Lumi. With no architecture override COM selects an available 32/64-bit server.
        const uint context = 0x4 /* LOCAL_SERVER */ | 0x400 /* NO_CODE_DOWNLOAD */;
        var iid = typeof(IPreviewHandler).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, 0, context, ref iid, out var handler));
        return handler;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2050",
        Justification = "IShellItem is directly referenced by typeof and both native signatures. It has no managed-callable members; only its native interface pointer is passed to IInitializeWithItem.")]
    private static IShellItem CreateShellItem(string path)
    {
        var iid = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref iid, out var item));
        return item;
    }

    internal static Guid FindPreviewHandler(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            throw new NotSupportedException("The file has no extension with a registered preview handler.");

        // AssocQueryString wraps IQueryAssociations, including extension/ProgID and user defaults.
        // If the default application has no handler, also consult the extension's system ProgID.
        var value = QueryPreviewAssociation(extension, 0)
            ?? QueryPreviewAssociation(extension, 0x800 /* INIT_FIXED_PROGID */);
        if (value is null)
            throw new NotSupportedException($"No Windows preview handler is registered for '{extension}'.");
        if (!Guid.TryParse(value, out var clsid) || clsid == Guid.Empty)
            throw new NotSupportedException($"The Windows preview handler registration for '{extension}' is invalid.");
        return clsid;
    }

    private static string? QueryPreviewAssociation(string extension, uint flags)
    {
        // A CLSID, including braces and its null terminator, fits in 39 UTF-16 characters.
        var value = new StringBuilder(39);
        uint length = (uint)value.Capacity;
        var result = AssocQueryString(flags, 16 /* ASSOCSTR_SHELLEXTENSION */, extension,
            PreviewHandlerInterfaceId, value, ref length);
        if (result == 0)
            return value.ToString();

        // Missing associations are expected; access errors and damaged registrations are not.
        if (result is unchecked((int)0x80070483) /* ERROR_NO_ASSOCIATION */
            or unchecked((int)0x80070002) /* ERROR_FILE_NOT_FOUND */
            or unchecked((int)0x80070003) /* ERROR_PATH_NOT_FOUND */)
            return null;
        Marshal.ThrowExceptionForHR(result);
        throw new NotSupportedException($"The Windows preview handler registration for '{extension}' is invalid.");
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
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public struct PreviewHandlerFrameInfo
    {
        public nint AcceleratorTable;
        public uint AcceleratorCount;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class PreviewHandlerSite : IPreviewHandlerFrame
    {
        public int GetWindowContext(out PreviewHandlerFrameInfo info)
        {
            info = default;
            return 0;
        }

        public int TranslateAccelerator(nint message) => 1; // S_FALSE: no host accelerators.
    }

    [ComVisible(true), Guid("FEC87AAF-35F9-447A-ADB7-20234491401A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    // The CCW cannot expose a non-public interface, even with ComVisible(true).
    public interface IPreviewHandlerFrame
    {
        [PreserveSig] int GetWindowContext(out PreviewHandlerFrameInfo info);
        [PreserveSig] int TranslateAccelerator(nint message);
    }

    [ComImport, Guid("8895B1C6-B41F-4C1C-A562-0D564250836F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        void SetWindow(nint parent, ref NativeRect rect);
        void SetRect(ref NativeRect rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out nint window);
        [PreserveSig] int TranslateAccelerator(nint message);
    }

    [ComImport, Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectWithSite
    {
        void SetSite([MarshalAs(UnmanagedType.IUnknown)] object? site);
        void GetSite(ref Guid iid, out nint site);
    }

    [ComImport, Guid("B824B49D-22AC-4161-AC8A-9916E8FA3F7F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream
    {
        void Initialize(IStream stream, uint mode);
    }

    [ComImport, Guid("B7D14566-0509-4CCE-A71F-0A554233BD9B"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string path, uint mode);
    }

    [ComImport, Guid("7F73BE3F-FB79-493C-A6C7-7EE14E245841"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithItem
    {
        void Initialize(IShellItem item, uint mode);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem;

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context,
        ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPreviewHandler handler);

    [DllImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(uint flags, uint association,
        string extension, string extra, StringBuilder value, ref uint length);

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext,
        ref Guid iid, out IShellItem item);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", ExactSpelling = true)]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string? title,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
#endif
}

#if WINDOWS
/// <summary>
/// Read-only COM adapter. The host owns the file; Unload is followed by disposing it, invalidating
/// all clones even if a handler retains an IStream reference. Clones have independent seek positions.
/// </summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class ReadOnlyPreviewStream(FileStream file) : IStream
{
    private long _position;

    public void Read(byte[] pv, int cb, nint pcbRead)
    {
        var read = ReadBytes(pv, cb);
        if (pcbRead != 0)
            Marshal.WriteInt32(pcbRead, read);
    }

    private int ReadBytes(byte[] buffer, int count)
    {
        lock (file)
        {
            file.Position = _position;
            var read = file.ReadAtLeast(buffer.AsSpan(0, count), count, throwOnEndOfStream: false);
            _position += read;
            return read;
        }
    }

    public void Seek(long offset, int origin, nint newPosition)
    {
        lock (file)
        {
            var start = origin switch
            {
                0 => 0,
                1 => _position,
                2 => file.Length,
                _ => throw new COMException("Invalid stream seek origin.", unchecked((int)0x80030001))
            };
            var position = checked(start + offset);
            if (position < 0)
                throw new COMException("Cannot seek before the start of the preview stream.", unchecked((int)0x80030001));
            _position = position;
            if (newPosition != 0)
                Marshal.WriteInt64(newPosition, position);
        }
    }

    public void Stat(out STATSTG stat, int flags)
    {
        lock (file)
        {
            stat = new STATSTG
            {
                type = 2, // STGTY_STREAM
                cbSize = file.Length,
                grfMode = 0, // STGM_READ
                pwcsName = (flags & 1 /* STATFLAG_NONAME */) == 0 ? file.Name : null!
            };
        }
    }

    public void Clone(out IStream stream)
    {
        lock (file)
            stream = new ReadOnlyPreviewStream(file) { _position = _position };
    }

    public void CopyTo(IStream target, long count, nint pcbRead, nint pcbWritten)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var buffer = new byte[64 * 1024];
        var writtenPointer = Marshal.AllocCoTaskMem(sizeof(int));
        long totalRead = 0;
        long totalWritten = 0;
        try
        {
            while (totalRead < count)
            {
                var read = ReadBytes(buffer, (int)Math.Min(buffer.Length, count - totalRead));
                if (read == 0)
                    break;

                totalRead += read;
                Marshal.WriteInt32(writtenPointer, 0);
                target.Write(buffer, read, writtenPointer);
                var written = Marshal.ReadInt32(writtenPointer);
                totalWritten += written;
                if (written != read)
                    throw new COMException("The destination did not accept all preview stream data.", unchecked((int)0x80030070));
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(writtenPointer);
            if (pcbRead != 0)
                Marshal.WriteInt64(pcbRead, totalRead);
            if (pcbWritten != 0)
                Marshal.WriteInt64(pcbWritten, totalWritten);
        }
    }

    public void Write(byte[] pv, int cb, nint pcbWritten) => throw ReadOnly();
    public void SetSize(long size) => throw ReadOnly();
    public void Commit(int flags) { }
    public void Revert() => throw Unsupported();
    public void LockRegion(long offset, long count, int type) => throw Unsupported();
    public void UnlockRegion(long offset, long count, int type) => throw Unsupported();

    private static COMException ReadOnly() =>
        new("The preview stream is read-only.", unchecked((int)0x80030005)); // STG_E_ACCESSDENIED

    private static COMException Unsupported() =>
        new("The preview stream does not support transactions or region locking.", unchecked((int)0x80030001));
}
#endif
