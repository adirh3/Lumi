#if !WINDOWS
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Lumi.Services;

internal static class NativeWebViewCookies
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromMilliseconds(100);

    internal static async Task<IReadOnlyList<Cookie>> GetAllAsync(NativeWebView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        Dispatcher.UIThread.VerifyAccess();
        if (view.TryGetCookieManager() is { } manager)
            return await ReadOfficialAsync(manager, OperationTimeout);
        using var retained = Gtk.RetainView(RequireGtkView(view));
        return await Gtk.ExecuteAsync(retained.View, Gtk.Operation.Read, null);
    }

    internal static async Task ClearAsync(NativeWebView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        Dispatcher.UIThread.VerifyAccess();
        if (view.TryGetCookieManager() is { } manager)
        {
            var deadline = Stopwatch.StartNew();
            var existing = await ReadOfficialAsync(manager, Remaining(deadline));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var cookie in existing)
                    QueueOfficialMutation(view, manager, cookie, delete: true);
            });
            await ObserveOfficialAsync(manager, static cookies => cookies.Count == 0, deadline);
            return;
        }
        using var retained = Gtk.RetainView(RequireGtkView(view));
        var nativeView = retained.View;
        var gtkDeadline = Stopwatch.StartNew();
        var nativeCookies = await Gtk.ExecuteAsync(nativeView, Gtk.Operation.Read, null, Remaining(gtkDeadline));
        // WebKit 2.42+ replace_cookies rejects a NULL/empty GList before creating its async
        // operation. Delete the actual jar entries with real finish callbacks instead.
        await Task.WhenAll(nativeCookies.Select(cookie =>
            Gtk.ExecuteAsync(nativeView, Gtk.Operation.Delete, cookie, Remaining(gtkDeadline))));
        while ((await Gtk.ExecuteAsync(nativeView, Gtk.Operation.Read, null, Remaining(gtkDeadline))).Count != 0)
            await Task.Delay(TimeSpan.FromMilliseconds(
                Math.Min(ObservationInterval.TotalMilliseconds, Remaining(gtkDeadline).TotalMilliseconds)));
    }

    // Set adds/replaces the supplied identities. Clear is the explicit whole-store operation;
    // neither backend merges jars from other tabs nor changes the engine's storage profile.
    internal static async Task SetAsync(NativeWebView view, IReadOnlyList<Cookie> cookies)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(cookies);
        Dispatcher.UIThread.VerifyAccess();
        var requested = SnapshotRequest(cookies);
        if (requested.Count == 0)
            return;
        if (view.TryGetCookieManager() is { } manager)
        {
            var deadline = Stopwatch.StartNew();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var cookie in requested)
                    QueueOfficialMutation(view, manager, cookie, delete: false);
            });
            await ObserveOfficialAsync(manager, actual => RequestedStateMatches(requested, actual, DateTime.UtcNow), deadline);
            return;
        }
        using var retained = Gtk.RetainView(RequireGtkView(view));
        await Task.WhenAll(requested.Select(cookie => Gtk.ExecuteAsync(retained.View, Gtk.Operation.Set, cookie)));
    }

    private static nint RequireGtkView(NativeWebView view)
    {
        if (OperatingSystem.IsLinux()
            && view.TryGetPlatformHandle() is IGtkWebViewPlatformHandle { WebKitWebView: not 0 } gtk)
            return gtk.WebKitWebView;
        throw new PlatformNotSupportedException(
            "This native browser has no public cookie manager or supported GTK WebKitWebView handle.");
    }

    private static async Task<IReadOnlyList<Cookie>> ReadOfficialAsync(
        NativeWebViewCookieManager manager, TimeSpan timeout)
    {
        try
        {
            var read = Dispatcher.UIThread.InvokeAsync(manager.GetCookiesAsync);
            // A timeout does not dispose/cancel the package's original Task or callback state.
            // That public API remains its own owner until its actual native completion.
            return await read.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("The native cookie manager did not complete its cookie read within 10 seconds.");
        }
        catch (Exception ex)
        {
            throw SafeFailure("Reading native cookies", ex);
        }
    }

    private static async Task ObserveOfficialAsync(
        NativeWebViewCookieManager manager, Func<IReadOnlyList<Cookie>, bool> matches, Stopwatch deadline)
    {
        while (true)
        {
            var actual = await ReadOfficialAsync(manager, Remaining(deadline));
            if (matches(actual))
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(
                Math.Min(ObservationInterval.TotalMilliseconds, Remaining(deadline).TotalMilliseconds)));
        }
    }

    private static TimeSpan Remaining(Stopwatch deadline)
    {
        var remaining = OperationTimeout - deadline.Elapsed;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException(
                "The native cookie store did not reach the requested state within 10 seconds. " +
                "The mutation is not reported as complete.");
        return remaining;
    }

    private static void QueueOfficialMutation(
        NativeWebView view, NativeWebViewCookieManager manager, Cookie cookie, bool delete)
    {
        try
        {
            // The 12.1.0 WK wrapper rejects empty values, even for DeleteCookie. Empty
            // cookies are valid, so queue this one case through the same public WK store.
            if (OperatingSystem.IsMacOS() && cookie.Value.Length == 0)
                QueueEmptyWkCookie(view, cookie, delete);
            else if (delete)
                manager.DeleteCookie(cookie);
            else
                manager.AddOrUpdateCookie(cookie);
        }
        catch (Exception ex)
        {
            throw SafeFailure("Queuing a native cookie mutation", ex);
        }
    }

    private static IReadOnlyList<Cookie> SnapshotRequest(IReadOnlyList<Cookie> cookies)
    {
        // Repeated identities within this request follow ordinary last-write-wins setter
        // semantics. This is not a merge with another view's or profile's cookie jar.
        var requested = new Dictionary<(string Name, string Domain, string Path), Cookie>();
        try
        {
            foreach (var cookie in cookies)
            {
                if (cookie is null || string.IsNullOrEmpty(cookie.Name) || string.IsNullOrEmpty(cookie.Domain)
                    || cookie.Name.Contains('\0') || cookie.Value.Contains('\0')
                    || cookie.Domain.Contains('\0') || cookie.Path.Contains('\0'))
                    throw new InvalidOperationException("Invalid native cookie input.");
                var snapshot = new Cookie(cookie.Name, cookie.Value,
                    string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path, cookie.Domain)
                {
                    Secure = cookie.Secure,
                    HttpOnly = cookie.HttpOnly,
                    Expires = cookie.Expires,
                    Version = cookie.Version
                };
                requested[(snapshot.Name, snapshot.Domain.ToLowerInvariant(), snapshot.Path)] = snapshot;
            }
        }
        catch (Exception ex)
        {
            throw SafeFailure("Preparing native cookie input", ex);
        }
        return requested.Values.ToArray();
    }

    internal static bool RequestedStateMatches(
        IReadOnlyList<Cookie> requested, IReadOnlyList<Cookie> actual, DateTime utcNow)
    {
        foreach (var expected in requested)
        {
            var found = actual.FirstOrDefault(cookie => cookie.Name == expected.Name
                && string.Equals(cookie.Domain, expected.Domain, StringComparison.OrdinalIgnoreCase)
                && cookie.Path == expected.Path);
            if (expected.Expires != DateTime.MinValue && expected.Expires.ToUniversalTime() <= utcNow)
            {
                if (found is not null)
                    return false;
                continue;
            }
            if (found is null || found.Value != expected.Value || found.Secure != expected.Secure
                || found.HttpOnly != expected.HttpOnly
                || (found.Expires == DateTime.MinValue) != (expected.Expires == DateTime.MinValue))
                return false;
            // Cookie-store expiry resolution is at most one second. Do not turn a session
            // cookie into a persistent cookie, or ignore a real expiry/attribute mismatch.
            if (expected.Expires != DateTime.MinValue
                && Math.Abs((found.Expires.ToUniversalTime() - expected.Expires.ToUniversalTime()).TotalSeconds) > 1)
                return false;
        }
        return true;
    }

    private static InvalidOperationException SafeFailure(string operation, Exception error) =>
        // CookieException/native messages can contain names or values. Never include their
        // text/inner exceptions in an error that callers might display or log.
        new($"{operation} failed ({error.GetType().Name}); cookie contents were not included.");

    private static void LogCallbackFailure(Exception error)
    {
        try { Trace.TraceError("[NativeCookies] Native callback failed ({0}).", error.GetType().Name); }
        catch { }
    }

    private static class Gtk
    {
        private const string WebKit = "libwebkit2gtk-4.1.so.0";
        private const string Soup = "libsoup-3.0.so.0";
        private const string GLib = "libglib-2.0.so.0";
        private const string GObject = "libgobject-2.0.so.0";
        private const string Gio = "libgio-2.0.so.0";
        internal enum Operation { Read, Delete, Set }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SourceCallback(nint data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DestroyCallback(nint data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ReadyCallback(nint manager, nint result, nint data);
        private static readonly SourceCallback Dispatch = OnDispatch;
        private static readonly DestroyCallback Destroy = OnDestroy;
        private static readonly ReadyCallback Ready = OnReady;

        // A multi-phase clear must not reuse an unretained pointer between async reads and
        // deletions. Each pending Request additionally keeps its own native references.
        internal static ViewLease RetainView(nint view)
        {
            try
            {
                if (view == 0 || g_type_check_instance_is_a(view, webkit_web_view_get_type()) == 0)
                    throw new PlatformNotSupportedException("The GTK cookie handle is not a WebKitGTK 4.1 WebKitWebView.");
                return new ViewLease(g_object_ref(view));
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                throw new PlatformNotSupportedException(
                    "Native GTK cookie transport requires WebKitGTK 4.1 (2.42+) and libsoup 3.");
            }
        }

        internal sealed class ViewLease(nint view) : IDisposable
        {
            private nint _view = view;
            internal nint View => _view;
            public void Dispose()
            {
                var retained = Interlocked.Exchange(ref _view, 0);
                if (retained != 0)
                    Post(() => g_object_unref(retained));
            }
        }

        internal static async Task<IReadOnlyList<Cookie>> ExecuteAsync(
            nint view, Operation operation, Cookie? cookie, TimeSpan? timeout = null)
        {
            Request request;
            try
            {
                if (view == 0 || g_type_check_instance_is_a(view, webkit_web_view_get_type()) == 0)
                    throw new PlatformNotSupportedException("The GTK cookie handle is not a WebKitGTK 4.1 WebKitWebView.");
                request = new Request(g_object_ref(view), operation, cookie);
                try { Post(request.Start); }
                catch (Exception ex) { request.FailBeforeCallback(ex); }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                throw new PlatformNotSupportedException(
                    "Native GTK cookie transport requires WebKitGTK 4.1 (2.42+) and libsoup 3.");
            }
            try { return await request.Completion.Task.WaitAsync(timeout ?? OperationTimeout); }
            catch (TimeoutException)
            {
                request.CancelAfterTimeout();
                throw new TimeoutException("The GTK cookie operation did not provide its async finish within 10 seconds.");
            }
        }

        private static void Post(Action action)
        {
            var data = GCHandle.Alloc(action);
            try
            {
                // The official GTK adapter uses this default GLib context on its GTK thread.
                // Do not invoke GTK directly on Avalonia's UI thread.
                if (g_idle_add_full(0, Dispatch, GCHandle.ToIntPtr(data), Destroy) == 0)
                    throw new InvalidOperationException("Could not schedule native cookie work.");
            }
            catch
            {
                data.Free();
                throw;
            }
        }

        private static int OnDispatch(nint data)
        {
            try { ((Action)GCHandle.FromIntPtr(data).Target!)(); }
            catch (Exception ex) { LogCallbackFailure(ex); }
            return 0;
        }

        private static void OnDestroy(nint data)
        {
            try { GCHandle.FromIntPtr(data).Free(); }
            catch (Exception ex) { LogCallbackFailure(ex); }
        }

        private static void OnReady(nint manager, nint result, nint data)
        {
            Request? request = null;
            try
            {
                request = (Request)GCHandle.FromIntPtr(data).Target!;
                request.Finish(manager, result);
            }
            catch (Exception ex)
            {
                LogCallbackFailure(ex);
                request?.Completion.TrySetException(SafeFailure("Completing native GTK cookies", ex));
            }
            finally
            {
                try { request?.Release(); }
                catch (Exception ex) { LogCallbackFailure(ex); }
            }
        }

        private sealed class Request(nint retainedView, Operation operation, Cookie? cookie)
        {
            private nint _view = retainedView;
            private nint _dataManager;
            private nint _manager;
            private nint _cancellable;
            private nint _cookie;
            private nint _list;
            private nint _error;
            private nint _callbackData;
            private bool _released;
            private int _timedOut;
            internal TaskCompletionSource<IReadOnlyList<Cookie>> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal void Start()
            {
                try
                {
                    if (Volatile.Read(ref _timedOut) != 0)
                    {
                        Completion.TrySetCanceled();
                        Release();
                        return;
                    }
                    var dataManager = webkit_web_view_get_website_data_manager(_view);
                    if (dataManager == 0)
                        throw new InvalidOperationException("The view has no native website data manager.");
                    _dataManager = g_object_ref(dataManager);
                    var manager = webkit_website_data_manager_get_cookie_manager(dataManager);
                    if (manager == 0)
                        throw new InvalidOperationException("The view has no native cookie manager.");
                    _manager = g_object_ref(manager);
                    _cancellable = g_cancellable_new();
                    _callbackData = GCHandle.ToIntPtr(GCHandle.Alloc(this));
                    switch (operation)
                    {
                        case Operation.Read:
                            webkit_cookie_manager_get_all_cookies(_manager, _cancellable, Ready, _callbackData);
                            break;
                        case Operation.Delete:
                            _cookie = CreateCookie(cookie!);
                            webkit_cookie_manager_delete_cookie(_manager, _cookie, _cancellable, Ready, _callbackData);
                            break;
                        case Operation.Set:
                            _cookie = CreateCookie(cookie!);
                            webkit_cookie_manager_add_cookie(_manager, _cookie, _cancellable, Ready, _callbackData);
                            break;
                    }
                }
                catch (Exception ex) { FailBeforeCallback(ex); }
            }

            internal void Finish(nint manager, nint result)
            {
                var success = true;
                switch (operation)
                {
                    case Operation.Read:
                        _list = webkit_cookie_manager_get_all_cookies_finish(manager, result, out _error);
                        break;
                    case Operation.Delete:
                        success = webkit_cookie_manager_delete_cookie_finish(manager, result, out _error) != 0;
                        break;
                    case Operation.Set:
                        success = webkit_cookie_manager_add_cookie_finish(manager, result, out _error) != 0;
                        break;
                }
                // Always finish and release, including after timeout/cancellation.
                if (Volatile.Read(ref _timedOut) != 0)
                    Completion.TrySetCanceled();
                else if (_error != 0 || !success)
                {
                    var code = _error == 0 ? 0 : Marshal.PtrToStructure<GError>(_error).Code;
                    Completion.TrySetException(new InvalidOperationException(
                        $"The native GTK cookie {operation} operation failed (error code {code})."));
                }
                else
                    Completion.TrySetResult(operation == Operation.Read ? ReadCookies(_list) : []);
            }

            internal void CancelAfterTimeout()
            {
                Interlocked.Exchange(ref _timedOut, 1);
                try
                {
                    Post(() =>
                    {
                        if (!_released && _cancellable != 0)
                            g_cancellable_cancel(_cancellable);
                    });
                }
                catch (Exception ex) { LogCallbackFailure(ex); }
            }

            internal void FailBeforeCallback(Exception error)
            {
                LogCallbackFailure(error);
                Completion.TrySetException(error is DllNotFoundException or EntryPointNotFoundException
                    ? new PlatformNotSupportedException(
                        "Native GTK cookie transport requires WebKitGTK 4.1 (2.42+) and libsoup 3.")
                    : SafeFailure("Starting native GTK cookies", error));
                Release();
            }

            internal void Release()
            {
                if (_released)
                    return;
                _released = true;
                // get_all_cookies_finish transfers ownership of the GList AND SoupCookies.
                for (var node = _list; node != 0; node = Marshal.ReadIntPtr(node, IntPtr.Size))
                    soup_cookie_free(Marshal.ReadIntPtr(node));
                if (_list != 0)
                    g_list_free(_list);
                if (_error != 0)
                    g_error_free(_error);
                if (_cookie != 0)
                    soup_cookie_free(_cookie);
                if (_cancellable != 0)
                    g_object_unref(_cancellable);
                if (_manager != 0)
                    g_object_unref(_manager);
                if (_dataManager != 0)
                    g_object_unref(_dataManager);
                if (_view != 0)
                    g_object_unref(_view);
                if (_callbackData != 0)
                    GCHandle.FromIntPtr(_callbackData).Free();
            }
        }

        private static nint CreateCookie(Cookie cookie)
        {
            var native = soup_cookie_new(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, -1);
            if (native == 0)
                throw new InvalidOperationException("Could not allocate a native cookie.");
            try
            {
                soup_cookie_set_secure(native, cookie.Secure ? 1 : 0);
                soup_cookie_set_http_only(native, cookie.HttpOnly ? 1 : 0);
                if (cookie.Expires != DateTime.MinValue)
                {
                    // Set the absolute expiry rather than clamping it to a 32-bit max-age.
                    var expiry = g_date_time_new_from_unix_utc(new DateTimeOffset(
                        cookie.Expires.ToUniversalTime()).ToUnixTimeSeconds());
                    if (expiry == 0)
                        throw new InvalidOperationException("The cookie expiry cannot be represented by the native store.");
                    try { soup_cookie_set_expires(native, expiry); }
                    finally { g_date_time_unref(expiry); }
                }
                return native;
            }
            catch
            {
                soup_cookie_free(native);
                throw;
            }
        }

        private static IReadOnlyList<Cookie> ReadCookies(nint list)
        {
            var cookies = new List<Cookie>();
            for (var node = list; node != 0; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                var cookie = Marshal.ReadIntPtr(node);
                var expiry = soup_cookie_get_expires(cookie); // Borrowed GDateTime; do not unref.
                cookies.Add(new Cookie(
                    Marshal.PtrToStringUTF8(soup_cookie_get_name(cookie))!,
                    Marshal.PtrToStringUTF8(soup_cookie_get_value(cookie)) ?? "",
                    Marshal.PtrToStringUTF8(soup_cookie_get_path(cookie)) ?? "/",
                    Marshal.PtrToStringUTF8(soup_cookie_get_domain(cookie))!)
                {
                    Secure = soup_cookie_get_secure(cookie) != 0,
                    HttpOnly = soup_cookie_get_http_only(cookie) != 0,
                    Expires = expiry == 0 ? DateTime.MinValue
                        : DateTimeOffset.FromUnixTimeSeconds(g_date_time_to_unix(expiry)).UtcDateTime
                });
            }
            return cookies;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GError
        {
            internal uint Domain;
            internal int Code;
            internal nint Message; // Intentionally never read: native messages may contain cookie data.
        }

        [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint g_idle_add_full(int priority, SourceCallback callback, nint data, DestroyCallback destroy);
        [DllImport(GObject, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint g_object_ref(nint instance);
        [DllImport(GObject, CallingConvention = CallingConvention.Cdecl)]
        private static extern void g_object_unref(nint instance);
        [DllImport(GObject, CallingConvention = CallingConvention.Cdecl)]
        private static extern int g_type_check_instance_is_a(nint instance, nuint type);
        [DllImport(WebKit, CallingConvention = CallingConvention.Cdecl)]
        private static extern nuint webkit_web_view_get_type();
        [DllImport(WebKit, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint webkit_web_view_get_website_data_manager(nint view);
        [DllImport(WebKit, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint webkit_website_data_manager_get_cookie_manager(nint dataManager);
        [DllImport(WebKit, CallingConvention = CallingConvention.Cdecl)]
        private static extern void webkit_cookie_manager_get_all_cookies(nint manager, nint cancellable, ReadyCallback callback, nint data);
        [DllImport(WebKit, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint webkit_cookie_manager_get_all_cookies_finish(nint manager, nint result, out nint error);
        [DllImport(WebKit, CallingConvention = CallingConvention.Cdecl)]
        private static extern void webkit_cookie_manager_delete_cookie(nint manager, nint cookie, nint cancellable, ReadyCallback callback, nint data);
        [DllImport(WebKit, CallingConvention = CallingConvention.Cdecl)]
        private static extern int webkit_cookie_manager_delete_cookie_finish(nint manager, nint result, out nint error);
        [DllImport(WebKit, CallingConvention = CallingConvention.Cdecl)]
        private static extern void webkit_cookie_manager_add_cookie(nint manager, nint cookie, nint cancellable, ReadyCallback callback, nint data);
        [DllImport(WebKit, CallingConvention = CallingConvention.Cdecl)]
        private static extern int webkit_cookie_manager_add_cookie_finish(nint manager, nint result, out nint error);
        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint g_cancellable_new();
        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
        private static extern void g_cancellable_cancel(nint cancellable);
        [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void g_list_free(nint list);
        [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void g_error_free(nint error);
        [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint g_date_time_new_from_unix_utc(long seconds);
        [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern long g_date_time_to_unix(nint dateTime);
        [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void g_date_time_unref(nint dateTime);
        [DllImport(Soup, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint soup_cookie_new(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string domain, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int maxAge);
        [DllImport(Soup, CallingConvention = CallingConvention.Cdecl)]
        private static extern void soup_cookie_free(nint cookie);
        [DllImport(Soup, CallingConvention = CallingConvention.Cdecl)]
        private static extern void soup_cookie_set_secure(nint cookie, int secure);
        [DllImport(Soup, CallingConvention = CallingConvention.Cdecl)]
        private static extern void soup_cookie_set_http_only(nint cookie, int httpOnly);
        [DllImport(Soup, CallingConvention = CallingConvention.Cdecl)]
        private static extern void soup_cookie_set_expires(nint cookie, nint expiry);
        [DllImport(Soup, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint soup_cookie_get_name(nint cookie);
        [DllImport(Soup, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint soup_cookie_get_value(nint cookie);
        [DllImport(Soup, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint soup_cookie_get_domain(nint cookie);
        [DllImport(Soup, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint soup_cookie_get_path(nint cookie);
        [DllImport(Soup, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint soup_cookie_get_expires(nint cookie);
        [DllImport(Soup, CallingConvention = CallingConvention.Cdecl)]
        private static extern int soup_cookie_get_secure(nint cookie);
        [DllImport(Soup, CallingConvention = CallingConvention.Cdecl)]
        private static extern int soup_cookie_get_http_only(nint cookie);
    }

    // Only needed for empty values rejected by the official WK wrapper. The queued native
    // mutation still has to pass the same authoritative GetCookiesAsync observation above.
    private static void QueueEmptyWkCookie(NativeWebView view, Cookie cookie, bool delete)
    {
        if (view.TryGetPlatformHandle() is not IAppleWKWebViewPlatformHandle apple)
            throw new PlatformNotSupportedException("The empty-cookie fallback requires a public WKWebView handle.");
        var nativeView = apple.GetWKWebViewRetained();
        nint pool = 0;
        try
        {
            if (nativeView == 0 || ObjC.Bool(nativeView, ObjC.Sel("isKindOfClass:"), ObjC.Class("WKWebView")) == 0)
                throw new PlatformNotSupportedException("The public Apple cookie handle is not a WKWebView.");
            pool = ObjC.Pointer(ObjC.Pointer(ObjC.Class("NSAutoreleasePool"), ObjC.Sel("alloc")), ObjC.Sel("init"));
            var properties = ObjC.Pointer(ObjC.Class("NSMutableDictionary"), ObjC.Sel("dictionary"));
            void Add(string key, nint value) => ObjC.TwoPointersVoid(
                properties, ObjC.Sel("setObject:forKey:"), value, ObjC.String(key));
            Add("Name", ObjC.String(cookie.Name));
            Add("Value", ObjC.String(""));
            Add("Domain", ObjC.String(cookie.Domain));
            Add("Path", ObjC.String(cookie.Path));
            if (cookie.Secure) Add("Secure", ObjC.String("TRUE"));
            if (cookie.HttpOnly) Add("HttpOnly", ObjC.String("TRUE"));
            if (cookie.Expires != DateTime.MinValue)
                Add("Expires", ObjC.DoubleArgument(ObjC.Class("NSDate"), ObjC.Sel("dateWithTimeIntervalSince1970:"),
                    new DateTimeOffset(cookie.Expires.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000d));
            var nativeCookie = ObjC.PointerArgument(
                ObjC.Class("NSHTTPCookie"), ObjC.Sel("cookieWithProperties:"), properties);
            if (nativeCookie == 0)
                throw new InvalidOperationException("WKWebView could not represent an empty native cookie.");
            var configuration = ObjC.Pointer(nativeView, ObjC.Sel("configuration"));
            var dataStore = ObjC.Pointer(configuration, ObjC.Sel("websiteDataStore"));
            var cookieStore = ObjC.Pointer(dataStore, ObjC.Sel("httpCookieStore"));
            if (cookieStore == 0)
                throw new InvalidOperationException("WKWebView has no native cookie store.");
            // These public WK methods copy/retain the cookie for the async operation.
            // A nil completion is permitted; success is proved by read-back, never this call.
            ObjC.TwoPointersVoid(cookieStore,
                ObjC.Sel(delete ? "deleteCookie:completionHandler:" : "setCookie:completionHandler:"), nativeCookie, 0);
        }
        finally
        {
            if (pool != 0) ObjC.Void(pool, ObjC.Sel("drain"));
            if (nativeView != 0) ObjC.Void(nativeView, ObjC.Sel("release"));
        }
    }

    private static class ObjC
    {
        private const string Library = "/usr/lib/libobjc.A.dylib";
        internal static nint String(string value) =>
            StringArgument(Class("NSString"), Sel("stringWithUTF8String:"), value);
        [DllImport(Library, EntryPoint = "objc_getClass", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint Class([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Library, EntryPoint = "sel_registerName", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint Sel([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Library, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint Pointer(nint receiver, nint selector);
        [DllImport(Library, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint PointerArgument(nint receiver, nint selector, nint argument);
        [DllImport(Library, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
        private static extern nint StringArgument(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport(Library, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint DoubleArgument(nint receiver, nint selector, double argument);
        [DllImport(Library, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
        internal static extern byte Bool(nint receiver, nint selector, nint argument);
        [DllImport(Library, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Void(nint receiver, nint selector);
        [DllImport(Library, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void TwoPointersVoid(nint receiver, nint selector, nint first, nint second);
    }
}
#endif
