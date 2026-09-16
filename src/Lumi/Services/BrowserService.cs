using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
#if WINDOWS
using Microsoft.Web.WebView2.Core;
#endif

namespace Lumi.Services;

#if WINDOWS
/// <summary>
/// Manages an embedded WebView2 browser instance and exposes automation tool methods
/// that the LLM can invoke (navigate, click, type, screenshot, JS eval, etc.).
/// </summary>
public sealed partial class BrowserService : IAsyncDisposable
{
    private const int WebViewInvalidStateHResult = unchecked((int)0x8007139F);

    // Shared environment so all per-chat instances share cookies/sessions
    private static CoreWebView2Environment? _sharedEnvironment;
    private static readonly SemaphoreSlim _sharedEnvLock = new(1, 1);

    /// <summary>
    /// CSS selector for interactive elements. Used by LookAsync, ClickByNumber, TypeByNumber, etc.
    /// Expanded to include ARIA roles for custom UI components (radio buttons, checkboxes, comboboxes,
    /// calendar grid cells, switches) that frameworks like MUI/React use instead of native elements.
    /// </summary>
    private const string InteractiveElementSelector =
        "a[href],button,input,select,textarea," +
        "[role=\"button\"],[role=\"link\"],[role=\"tab\"],[role=\"menuitem\"]," +
        "[role=\"radio\"],[role=\"checkbox\"],[role=\"switch\"],[role=\"combobox\"]," +
        "[role=\"option\"],[role=\"gridcell\"],[role=\"spinbutton\"],[role=\"slider\"]," +
        "[onclick],[tabindex],[contenteditable],[data-tooltip]";

    /// <summary>The same selector escaped for embedding in JS single-quoted strings.</summary>
    private const string InteractiveElementSelectorJs =
        "a[href],button,input,select,textarea," +
        "[role=\\\"button\\\"],[role=\\\"link\\\"],[role=\\\"tab\\\"],[role=\\\"menuitem\\\"]," +
        "[role=\\\"radio\\\"],[role=\\\"checkbox\\\"],[role=\\\"switch\\\"],[role=\\\"combobox\\\"]," +
        "[role=\\\"option\\\"],[role=\\\"gridcell\\\"],[role=\\\"spinbutton\\\"],[role=\\\"slider\\\"]," +
        "[onclick],[tabindex],[contenteditable],[data-tooltip]";

    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _webView;
    private IntPtr _parentHwnd;
    private bool _initialized;
    private volatile bool _isDisposed;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _actionLock = new(1, 1);
    private readonly object _parentHwndSync = new();
    private IntPtr _webViewHwnd;
    private bool _isReparentQueued;

    // Navigation completion tracking — deterministic via WebView2 events
    private TaskCompletionSource<bool>? _navigationTcs;
    private TaskCompletionSource<string>? _sourceChangedTcs;

    // New-window tracking — detect when a click opens a link instead of downloading
    private string? _lastNewWindowUrl;

    // Download tracking — deterministic detection via WebView2 DownloadStarting event
    private readonly ConcurrentQueue<TrackedDownload> _recentDownloads = new();
    private TaskCompletionSource<string>? _downloadWaiter;

    /// <summary>Thread-safe download state. All fields updated only on the UI thread via WebView2 events.</summary>
    private class TrackedDownload
    {
        public string FilePath { get; }
        public DateTime StartedAt { get; }
        public volatile int State; // 0=InProgress, 1=Completed, 2=Interrupted
        public long BytesReceived;
        public long TotalBytesToReceive;
        public CoreWebView2DownloadOperation? Operation;
        public EventHandler<object>? StateChanged;
        public EventHandler<object>? BytesChanged;

        public void Unsubscribe()
        {
            if (Operation is null)
                return;
            Operation.StateChanged -= StateChanged;
            Operation.BytesReceivedChanged -= BytesChanged;
            Operation = null;
            StateChanged = null;
            BytesChanged = null;
        }

        public TrackedDownload(string filePath, DateTime startedAt, long bytesReceived, long totalBytes)
        {
            FilePath = filePath;
            StartedAt = startedAt;
            BytesReceived = bytesReceived;
            TotalBytesToReceive = totalBytes;
        }
    }

    private bool _isDark = true;

    /// <summary>Store the parent HWND for lazy initialization.</summary>
    private IntPtr _pendingParentHwnd;

    /// <summary>Raised when the browser is first initialized or needs to show.</summary>
    public event Action? BrowserReady;

    // ----- File upload policy (security limits enforced before any DOM.setFileInputFiles) -----

    /// <summary>Maximum number of files that may be attached in a single upload action.</summary>
    private const int MaxUploadFiles = 20;

    /// <summary>Maximum size of any single uploaded file (100 MB).</summary>
    private const long MaxUploadFileBytes = 100L * 1024 * 1024;

    /// <summary>Maximum combined size of all files in a single upload action (250 MB).</summary>
    private const long MaxTotalUploadBytes = 250L * 1024 * 1024;

    /// <summary>The current URL loaded in the browser.</summary>
    public string CurrentUrl => _tabOwner is null ? _activeTab?._tabUrl ?? "about:blank" : _tabUrl;

    /// <summary>The page title.</summary>
    public string CurrentTitle => _tabOwner is null ? _activeTab?._tabTitle ?? "" : _tabTitle;

    /// <summary>Whether the browser has been initialized.</summary>
    public bool IsInitialized => _tabOwner is null ? _activeTab?._initialized == true : _initialized;

    private static string GetUserDataFolder() => Path.Combine(DataStore.AppDirectory, "browser-data");

    /// <summary>
    /// Sets the browser color scheme to match the app theme.
    /// Safe to call before initialization — the value is stored and applied on init.
    /// </summary>
    public void SetTheme(bool isDark)
    {
        _isDark = isDark;
        if (_tabOwner is null)
        {
            lock (_tabsSync)
                foreach (var tab in _tabs)
                    tab.SetTheme(isDark);
            return;
        }

        if (_isDisposed || (_controller is null && _webView is null))
            return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ApplyStoredTheme);
            return;
        }

        ApplyStoredTheme();
    }

    private void ApplyStoredTheme()
    {
        if (_isDisposed)
            return;

        var controller = _controller;
        var webView = _webView;

        InvokeOnLiveController(
            "theme update",
            () =>
            {
                if (controller is not null)
                    controller.DefaultBackgroundColor = _isDark
                        ? System.Drawing.Color.FromArgb(255, 30, 30, 30)
                        : System.Drawing.Color.FromArgb(255, 255, 255, 255);
                if (webView is not null)
                    webView.Profile.PreferredColorScheme = _isDark
                        ? CoreWebView2PreferredColorScheme.Dark
                        : CoreWebView2PreferredColorScheme.Light;
            });
    }

    /// <summary>
    /// Runs an operation that touches the native WebView2 controller, tolerating the controller having
    /// been closed underneath us.
    /// </summary>
    /// <remarks>
    /// WebView2 ties a controller to its parent HWND, so the browser can be torn down by something we
    /// do not observe — the owning window being destroyed, or a concurrent disposal. Every subsequent
    /// member access then throws <see cref="InvalidOperationException"/> wrapping
    /// <c>COMException 0x8007139F</c>. These calls run from window/panel plumbing (theme sync, layout,
    /// visibility, reparenting), which is not exception-tolerant, so an unguarded call takes the whole
    /// app down. When that happens, drop the stale wrappers so the next browser use creates a fresh
    /// controller instead of repeatedly failing against the dead one.
    /// </remarks>
    private bool InvokeOnLiveController(string operation, Action action)
    {
        if (_isDisposed || _controller is null)
            return false;

        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (IsWebViewInvalidState(ex))
        {
            DropClosedController(operation, ex);
            return false;
        }
    }

    private void DropClosedController(string operation, Exception ex)
    {
        var controller = _controller;

        _controller = null;
        _webView = null;
        _initialized = false;
        _webViewHwnd = IntPtr.Zero;

        System.Diagnostics.Debug.WriteLine(
            $"WebView2 {operation} skipped because the controller is already closed: {ex.Message}");

        if (controller is not null)
            CloseControllerIgnoringInvalidState(controller, $"after a failed {operation}");
    }

    internal static bool IsWebViewInvalidState(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is COMException { HResult: WebViewInvalidStateHResult })
                return true;
        }

        return false;
    }

    private static void CloseControllerIgnoringInvalidState(
        CoreWebView2Controller controller,
        string operation)
    {
        try
        {
            controller.Close();
        }
        catch (Exception ex) when (IsWebViewInvalidState(ex))
        {
            System.Diagnostics.Debug.WriteLine(
                $"WebView2 controller was already closed {operation}: {ex.Message}");
        }
    }

    private void DisposeNativeBrowserOnUiThread()
    {
        System.Diagnostics.Debug.Assert(Dispatcher.UIThread.CheckAccess());

        var webView = _webView;
        var controller = _controller;

        _controller = null;
        _webView = null;
        _environment = null;
        _initialized = false;
        _webViewHwnd = IntPtr.Zero;

        try
        {
            while (_recentDownloads.TryDequeue(out var download))
                download.Unsubscribe();
            if (controller is not null)
                controller.AcceleratorKeyPressed -= OnAcceleratorKeyPressed;

            if (webView is not null)
            {
                webView.NavigationStarting -= OnNavigationStarting;
                webView.NavigationCompleted -= OnNavigationCompleted;
                webView.DocumentTitleChanged -= OnDocumentTitleChanged;
                webView.WindowCloseRequested -= OnWindowCloseRequested;
                webView.SourceChanged -= OnSourceChanged;
                webView.NewWindowRequested -= OnNewWindowRequested;
                webView.DownloadStarting -= OnDownloadStarting;
                webView.ScriptDialogOpening -= OnScriptDialogOpening;
            }
        }
        catch (Exception ex) when (IsWebViewInvalidState(ex))
        {
            System.Diagnostics.Debug.WriteLine(
                $"WebView2 event cleanup found an already closed controller: {ex.Message}");
        }
        finally
        {
            if (controller is not null)
                CloseControllerIgnoringInvalidState(controller, "during disposal");
        }
    }

    /// <summary>The underlying CoreWebView2 (for direct access if needed).</summary>
    public CoreWebView2? WebView => _tabOwner is null ? _activeTab?._webView : _webView;

    /// <summary>The underlying controller (for resize/bounds).</summary>
    public CoreWebView2Controller? Controller => _tabOwner is null ? _activeTab?._controller : _controller;

    // ── Cross-platform view surface ───────────────────────────────────────
    // These wrappers let the shared BrowserView / preview-panel code drive the
    // native overlay WITHOUT referencing WebView2 types, so the same view code
    // compiles on Linux/macOS (where BrowserService is a stub).

    /// <summary>Raised (on the WebView2 UI thread) when the page URL changes.</summary>
    public event Action? UrlChanged;

    /// <summary>True once the native controller has been created.</summary>
    public bool HasController => Controller is not null;

    /// <summary>Shows or hides the native browser overlay, if present.</summary>
    public void SetControllerVisible(bool visible)
    {
        if (_tabOwner is null)
        {
            _panelVisible = visible;
            if (!_isDisposed)
                ApplyTabPresentation();
            return;
        }
        var controller = _controller;
        if (controller is not null)
            InvokeOnLiveController("visibility update", () => controller.IsVisible = visible);
    }

    /// <summary>Syncs the native overlay's rasterization scale with Avalonia's effective UI scale.</summary>
    public void SyncRasterizationScale(double scale)
    {
        if (_tabOwner is null)
        {
            _tabScale = scale;
            if (!_isDisposed)
                ApplyTabPresentation();
            return;
        }
        var controller = _controller;
        if (controller is null)
            return;

        InvokeOnLiveController(
            "rasterization scale update",
            () =>
            {
                if (Math.Abs(controller.RasterizationScale - scale) > 0.01)
                    controller.RasterizationScale = scale;
            });
    }

    /// <summary>Reloads the current page, if initialized.</summary>
    public void Reload()
    {
        if (_tabOwner is null)
        {
            if (!_isDisposed)
                CaptureActiveTab().Reload();
            return;
        }
        var webView = _webView;
        if (webView is not null)
            InvokeOnLiveController("reload", webView.Reload);
    }

    /// <summary>
    /// Initializes the WebView2 environment and controller with a persistent user data folder.
    /// Uses a shared environment so all per-chat instances share cookies/sessions.
    /// Must be called from the UI thread with a valid HWND.
    /// </summary>
    public async Task InitializeAsync(IntPtr parentHwnd)
    {
        if (_tabOwner is null)
        {
            SetParentHwnd(parentHwnd);
            await CaptureActiveTab().InitializeAsync(parentHwnd);
            return;
        }
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_initialized) return;
            _parentHwnd = parentHwnd;

            // Get or create the shared environment (all instances share cookies)
            await _sharedEnvLock.WaitAsync();
            try
            {
                if (_tabOwner._environment is not null)
                {
                    _environment = _tabOwner._environment;
                }
                else if (_tabOwner._userDataFolderOverride is { } isolatedFolder)
                {
                    _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: isolatedFolder);
                    _tabOwner._environment = _environment;
                }
                else
                {
                    if (_sharedEnvironment is null)
                    {
                        var userDataFolder = GetUserDataFolder();
                        Directory.CreateDirectory(userDataFolder);
                        var options = new CoreWebView2EnvironmentOptions
                        {
                            AllowSingleSignOnUsingOSPrimaryAccount = true
                        };
                        _sharedEnvironment = await CoreWebView2Environment.CreateAsync(
                            browserExecutableFolder: null,
                            userDataFolder: userDataFolder,
                            options: options);
                    }
                    _environment = _sharedEnvironment;
                    _tabOwner._environment = _environment;
                }
            }
            finally
            {
                _sharedEnvLock.Release();
            }

            var controller = await _environment.CreateCoreWebView2ControllerAsync(_parentHwnd);
            if (_isDisposed)
            {
                CloseControllerIgnoringInvalidState(controller, "after initialization was cancelled");
                throw new ObjectDisposedException(GetType().FullName);
            }

            var webView = controller.CoreWebView2;
            if (_tabOwner._pendingParentHwnd == IntPtr.Zero)
                _tabOwner._pendingParentHwnd = parentHwnd;
            _controller = controller;
            _webView = webView;
            controller.ShouldDetectMonitorScaleChanges = false;
            controller.IsVisible = false;
            controller.AcceleratorKeyPressed += OnAcceleratorKeyPressed;

            // Sync theme with app
            SetTheme(_isDark);
            if (!ReferenceEquals(_controller, controller) || !ReferenceEquals(_webView, webView))
                throw new InvalidOperationException("WebView2 controller closed during initialization.");

            // Configure settings
            webView.Settings.IsScriptEnabled = true;
            // Native WebView2 dialogs disable the owner HWND. If the browser is hidden while one
            // is open, WebView2 hides the dialog but can leave Lumi disabled indefinitely.
            webView.Settings.AreDefaultScriptDialogsEnabled = false;
            webView.Settings.IsWebMessageEnabled = true;
            webView.Settings.AreDevToolsEnabled = false;
            webView.Settings.IsStatusBarEnabled = false;
            webView.Settings.AreDefaultContextMenusEnabled = true;

            webView.ScriptDialogOpening += OnScriptDialogOpening;

            // Track navigation completion
            webView.NavigationCompleted += OnNavigationCompleted;
            webView.NavigationStarting += OnNavigationStarting;
            webView.DocumentTitleChanged += OnDocumentTitleChanged;
            webView.WindowCloseRequested += OnWindowCloseRequested;

            // Track URL changes (including SPA hash navigations that skip NavigationCompleted)
            webView.SourceChanged += OnSourceChanged;

            // Supply a related WebView2 for popups so window.opener and authentication flows survive.
            webView.NewWindowRequested += OnNewWindowRequested;

            // Track downloads so we can detect click-triggered downloads
            webView.DownloadStarting += OnDownloadStarting;

            _initialized = true;
            _tabOwner.ApplyTabPresentation();
            _tabOwner.BrowserReady?.Invoke();
            NotifyTabChanged();
            BrowserReady?.Invoke();
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.NavigationId != _navigationId)
            return;
        _isNavigating = false;
        _navigationTcs?.TrySetResult(e.IsSuccess);
        NotifyTabChanged();
    }

    private void OnAcceleratorKeyPressed(
        object? sender,
        CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        var isKeyDown = e.KeyEventKind is
            CoreWebView2KeyEventKind.KeyDown or
            CoreWebView2KeyEventKind.SystemKeyDown;
        var delta = BrowserAcceleratorShortcut.GetUiScaleDelta(
            (int)e.VirtualKey,
            isKeyDown,
            IsVirtualKeyDown(BrowserAcceleratorShortcut.ControlKey),
            IsVirtualKeyDown(BrowserAcceleratorShortcut.AltKey),
            IsVirtualKeyDown(BrowserAcceleratorShortcut.LeftWindowsKey)
                || IsVirtualKeyDown(BrowserAcceleratorShortcut.RightWindowsKey));

        if (delta == 0 || Application.Current is not App)
            return;

        // This event is synchronous in windowed WebView2. Defer UI work until after
        // returning so layout updates do not make blocked cross-process COM calls.
        e.Handled = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (Application.Current is App app)
                    app.TryAdjustUiScale(delta);
            },
            DispatcherPriority.Input);
    }

    internal static bool ShouldAcceptScriptDialog(CoreWebView2ScriptDialogKind kind)
        => kind == CoreWebView2ScriptDialogKind.Beforeunload;

    private static void OnScriptDialogOpening(
        object? sender,
        CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        // Alerts dismiss when the handler returns; confirm and prompt safely default to cancel.
        // An initiated navigation must accept beforeunload so it cannot strand a hidden modal.
        if (ShouldAcceptScriptDialog(e.Kind))
            e.Accept();
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        var url = _webView?.Source ?? "about:blank";
        _tabUrl = url;
        _sourceChangedTcs?.TrySetResult(url);
        UrlChanged?.Invoke();
        NotifyTabChanged();
    }

    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        _lastNewWindowUrl = e.Uri;
        using var deferral = e.GetDeferral();
        try
        {
            if (_tabOwner is null || _isDisposed)
                throw new InvalidOperationException("The popup's opener has closed.");
            var popup = await _tabOwner.CreateTabAsync(activate: true, initialize: true);
            if (_isDisposed || popup._isDisposed)
                throw new InvalidOperationException("The popup or its opener closed during initialization.");
            e.NewWindow = popup._webView;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            System.Diagnostics.Debug.WriteLine($"Browser popup creation failed: {ex}");
            _tabOwner?.BrowserError?.Invoke($"Could not open popup: {ex.Message}");
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var path = e.ResultFilePath;
        if (string.IsNullOrEmpty(path)) return;

        var op = e.DownloadOperation;
        var tracked = new TrackedDownload(
            path,
            DateTime.UtcNow,
            op.BytesReceived,
            (long)(op.TotalBytesToReceive ?? 0));

        // Subscribe to state changes on the UI thread — updates plain fields
        tracked.Operation = op;
        tracked.StateChanged = (_, _) =>
        {
            tracked.State = op.State switch
            {
                CoreWebView2DownloadState.Completed => 1,
                CoreWebView2DownloadState.Interrupted => 2,
                _ => 0
            };
            tracked.BytesReceived = op.BytesReceived;
            tracked.TotalBytesToReceive = (long)(op.TotalBytesToReceive ?? 0);
            if (tracked.State != 0)
                tracked.Unsubscribe();
        };
        tracked.BytesChanged = (_, _) =>
        {
            tracked.BytesReceived = op.BytesReceived;
            tracked.TotalBytesToReceive = (long)(op.TotalBytesToReceive ?? 0);
        };
        op.StateChanged += tracked.StateChanged;
        op.BytesReceivedChanged += tracked.BytesChanged;

        _recentDownloads.Enqueue(tracked);
        // Trim old entries
        while (_recentDownloads.Count > 10)
            if (_recentDownloads.TryDequeue(out var oldDownload))
                oldDownload.Unsubscribe();

        // Signal any pending download waiter
        _downloadWaiter?.TrySetResult(path);
    }

    /// <summary>Get downloads that started since the given time, optionally matching a glob pattern.</summary>
    private List<TrackedDownload> GetDownloadsSince(DateTime since, string? pattern = null)
    {
        return _recentDownloads.ToArray()
            .Where(d => d.StartedAt >= since)
            .Where(d => pattern == null || MatchesGlob(d.FilePath, pattern))
            .ToList();
    }

    /// <summary>Get download status, waiting briefly for in-progress downloads to complete.</summary>
    private static async Task<string> GetDownloadStatusAsync(TrackedDownload dl, int maxWaitMs = 5000)
    {
        // If in progress, wait up to maxWaitMs for completion
        if (dl.State == 0)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(maxWaitMs);
            while (DateTime.UtcNow < deadline && dl.State == 0)
                await Task.Delay(250);
        }

        return GetDownloadStatus(dl);
    }

    private static string GetDownloadStatus(TrackedDownload dl)
    {
        var fileName = Path.GetFileName(dl.FilePath);
        var state = dl.State;
        var received = dl.BytesReceived;
        var total = dl.TotalBytesToReceive;

        if (state == 1) // Completed
        {
            var size = total > 0 ? total : received;
            return $"Downloaded: {dl.FilePath} ({size:N0} bytes)";
        }

        if (state == 2) // Interrupted
        {
            return $"Download interrupted: {fileName} ({received:N0} of {total:N0} bytes received)";
        }

        // Still in progress — check file on disk as fallback
        // (small files may complete before StateChanged fires)
        try
        {
            var info = new FileInfo(dl.FilePath);
            if (info.Exists && info.Length > 0)
                return $"Downloaded: {info.FullName} ({info.Length:N0} bytes)";
        }
        catch { }

        // In progress
        if (total > 0)
        {
            var pct = (double)received / total * 100;
            var elapsed = (DateTime.UtcNow - dl.StartedAt).TotalSeconds;
            var etaStr = "";
            if (elapsed > 1 && received > 0)
            {
                var bytesPerSec = received / elapsed;
                var remaining = (total - received) / bytesPerSec;
                etaStr = remaining < 60
                    ? $", ~{remaining:F0}s remaining"
                    : $", ~{remaining / 60:F1}min remaining";
            }
            return $"Downloading: {fileName} ({pct:F0}% — {received:N0}/{total:N0} bytes{etaStr})";
        }

        return $"Downloading: {fileName} ({received:N0} bytes so far)";
    }

    /// <summary>Updates the bounds of the WebView2 controller to fill the given area.</summary>
    public void SetBounds(int x, int y, int width, int height, int cornerRadiusPx = 0)
    {
        if (_isDisposed)
            return;
        if (_tabOwner is null)
        {
            _tabBounds = new System.Drawing.Rectangle(x, y, width, height);
            _tabCornerRadius = cornerRadiusPx;
            ApplyTabPresentation();
            return;
        }

        var controller = _controller;
        if (controller is null) return;

        if (!InvokeOnLiveController(
                "bounds update",
                () => controller.Bounds = new System.Drawing.Rectangle(x, y, width, height)))
        {
            return;
        }

        if (cornerRadiusPx > 0)
            ApplyRoundedRegion(width, height, cornerRadiusPx);
    }

    private void ApplyRoundedRegion(int width, int height, int cornerRadiusPx)
    {
        if (_webViewHwnd == IntPtr.Zero)
        {
            _webViewHwnd = FindWindowEx(_parentHwnd, IntPtr.Zero, "Chrome_WidgetWin_0", null);
            if (_webViewHwnd == IntPtr.Zero)
                _webViewHwnd = FindWindowEx(_parentHwnd, IntPtr.Zero, "Chrome_WidgetWin_1", null);
        }
        if (_webViewHwnd == IntPtr.Zero) return;

        // Shift the region upward so only the bottom corners are rounded.
        var rgn = CreateRoundRectRgn(0, -cornerRadiusPx, width + 1, height + 1, cornerRadiusPx, cornerRadiusPx);
        if (rgn != IntPtr.Zero)
        {
            if (SetWindowRgn(_webViewHwnd, rgn, true) == 0)
                DeleteObject(rgn);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    private static bool IsVirtualKeyDown(int virtualKey) =>
        (GetKeyState(virtualKey) & 0x8000) != 0;

    private async Task WaitForActionLockAsync()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        await _actionLock.WaitAsync().ConfigureAwait(false);
        if (!_isDisposed)
            return;

        _actionLock.Release();
        throw new ObjectDisposedException(GetType().FullName);
    }

    // ═══════════════════════════════════════════════════════════════
    // Tool Methods — called by the LLM via AIFunction tools
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Navigate to a URL and wait for the page to load.</summary>
    public async Task<string> NavigateAsync(string url)
    {
        if (_tabOwner is null)
            return await CaptureActiveTab().NavigateAsync(url);
        return await RunOperationAsync(async () => (await NavigateCoreAsync(url)).Result.ToDisplayText());
    }

    private async Task<(BrowserActionResult Result, bool IsDownload)> NavigateCoreAsync(string url)
    {
        await EnsureInitializedAsync();
        await WaitForActionLockAsync();
        try
        {
            TaskCompletionSource<bool> navTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var sourceChangeTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var startedAt = DateTime.UtcNow;
            await InvokeOnUiThreadAsync(() =>
            {
                _navigationTcs = navTcs;
                _sourceChangedTcs = sourceChangeTcs;
                _webView!.Navigate(url);
            });

            // Wait for NavigationCompleted OR SourceChanged (for SPA hash navigations)
            var timeout = IsLikelySpaRoute(url)
                ? TimeSpan.FromSeconds(8)
                : TimeSpan.FromSeconds(18);
            var success = await WaitForNavigationEventsAsync(navTcs.Task, sourceChangeTcs.Task, timeout);
            _sourceChangedTcs = null;

            // Check if navigation actually triggered a file download (e.g. export URLs)
            var downloads = GetDownloadsSince(startedAt);
            if (downloads.Count > 0)
            {
                var status = await GetDownloadStatusAsync(downloads[^1]);
                return (BrowserActionResult.FromLegacy(status), true);
            }

            var page = await InvokeOnUiThreadAsync(() =>
                (_webView!.Source ?? "about:blank", _webView.DocumentTitle ?? ""));
            var elapsed = (DateTime.UtcNow - startedAt).TotalSeconds;

            return (success
                ? BrowserActionResult.Success($"Navigated to {page.Item1}. Page title: {page.Item2}. ({elapsed:F1}s)")
                : BrowserActionResult.Failure($"Navigation failed or timed out after {elapsed:F1}s."), false);
        }
        finally
        {
            _sourceChangedTcs = null;
            _actionLock.Release();
        }
    }

    /// <summary>Press a keyboard key on the active element or an optional selector target.</summary>
    public async Task<string> PressKeyAsync(string key, string? selector = null)
    {
        if (_tabOwner is null)
            return await CaptureActiveTab().PressKeyAsync(key, selector);
        return await RunOperationAsync(async () => (await RunDomActionAsync("press", key, selector)).ToDisplayText());
    }

    /// <summary>Execute arbitrary JavaScript and return the result. Wraps in try/catch for better error reporting.</summary>
    public async Task<string> EvaluateAsync(string javascript)
    {
        if (_tabOwner is null)
            return await CaptureActiveTab().EvaluateAsync(javascript);
        return await RunOperationAsync(() => EvaluateCoreAsync(javascript));
    }

    private async Task<string> EvaluateCoreAsync(string javascript)
    {
        await EnsureInitializedAsync();
        await WaitForActionLockAsync();
        try
        {
            // Wrap the user script in a synchronous try/catch so errors are returned as text instead of null.
            // NOTE: We intentionally do NOT use async/await here because WebView2's ExecuteScriptAsync
            // may not auto-await Promises, which would cause all results to come back as empty "{}".
            var wrappedScript =
                "(function(){try{" +
                "var __result__=(function(){" + javascript + "})();" +
                "if(__result__===undefined)return '(undefined)';" +
                "if(__result__===null)return '(null)';" +
                "if(typeof __result__==='object'){" +
                "if(typeof __result__.then==='function')return '(Promise returned — use .then() or callback pattern instead of await)';" +
                "try{return JSON.stringify(__result__,null,2)}catch(e){return String(__result__);}}" +
                "return String(__result__);" +
                "}catch(e){return 'JS Error: '+e.message+(e.stack?'\\n'+e.stack.split('\\n').slice(0,3).join('\\n'):'');}})()";

            var beforeEval = DateTime.UtcNow;
            var result = await InvokeOnUiThreadAsync(() => _webView!.ExecuteScriptAsync(wrappedScript));
            await Task.Delay(300); // brief settle for any download to register

            // Check if the script triggered a download
            var downloads = GetDownloadsSince(beforeEval);
            if (downloads.Count > 0)
            {
                var status = await GetDownloadStatusAsync(downloads[^1]);
                return CleanJsResult(result) + $"\n\nDownload triggered:\n{status}";
            }

            return CleanJsResult(result);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    /// <summary>Wait for an element to appear in the DOM.</summary>
    public async Task<string> WaitForAsync(string selector, int timeoutMs = 10000)
    {
        if (_tabOwner is null)
            return await CaptureActiveTab().WaitForAsync(selector, timeoutMs);
        return await RunOperationAsync(async () => (await WaitForDomElementAsync(selector, timeoutMs)).ToDisplayText());
    }

    /// <summary>Go back in browser history.</summary>
    public async Task<string> GoBackAsync()
    {
        if (_tabOwner is null)
            return await CaptureActiveTab().GoBackAsync();
        return await RunOperationAsync(GoBackCoreAsync);
    }

    private async Task<string> GoBackCoreAsync()
    {
        await EnsureInitializedAsync();
        var canGoBack = await InvokeOnUiThreadAsync(() => _webView!.CanGoBack);
        if (canGoBack)
        {
            TaskCompletionSource<bool> navTcs = new();
            await InvokeOnUiThreadAsync(() =>
            {
                _navigationTcs = navTcs;
                _webView!.GoBack();
            });
            if (!await WaitWithTimeout(navTcs.Task, TimeSpan.FromSeconds(10)))
                return "Error: navigation back failed or timed out.";
            var source = await InvokeOnUiThreadAsync(() => _webView!.Source ?? "about:blank");
            return $"Navigated back to: {source}";
        }
        return "Cannot go back — no previous page.";
    }

    /// <summary>Scroll the page up or down.</summary>
    public async Task<string> ScrollAsync(string direction, int pixels = 500)
    {
        if (_tabOwner is null)
            return await CaptureActiveTab().ScrollAsync(direction, pixels);
        return await RunOperationAsync(() => ScrollCoreAsync(direction, pixels));
    }

    private async Task<string> ScrollCoreAsync(string direction, int pixels)
    {
        await EnsureInitializedAsync();
        var dy = direction.Equals("up", StringComparison.OrdinalIgnoreCase) ? -pixels : pixels;
        await InvokeOnUiThreadAsync(() => _webView!.ExecuteScriptAsync($"window.scrollBy(0, {dy})"));
        return $"Scrolled {direction} by {pixels}px";
    }

    // ═══════════════════════════════════════════════════════════════
    // Composite Tool Methods — clean 4-tool surface for the LLM
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Navigate to a URL, wait for dynamic content to settle, and return a numbered snapshot.</summary>
    public async Task<string> OpenAndSnapshotAsync(string url)
    {
        if (_tabOwner is null)
            return await CaptureActiveTab().OpenAndSnapshotAsync(url);
        return await RunOperationAsync(() => OpenAndSnapshotCoreAsync(url));
    }

    private async Task<string> OpenAndSnapshotCoreAsync(string url)
    {
        try
        {
            var (result, isDownload) = await NavigateCoreAsync(url);
            if (!result.Succeeded || isDownload)
                return $"Tab: {TabId}\n" + result.ToDisplayText();
            var readiness = await WaitForContentSettleAsync();
            var snapshot = await LookCoreAsync();
            return readiness.Succeeded ? snapshot : readiness.ToDisplayText() + "\n\n" + snapshot;
        }
        catch (Exception ex) { return $"Tab: {TabId}\n" + BrowserActionResult.FromException(ex).ToDisplayText(); }
    }

    /// <summary>Get current page state with numbered interactive elements, optionally filtered.</summary>
    public async Task<string> LookAsync(string? filter = null)
    {
        if (_tabOwner is null)
            return await CaptureActiveTab().LookAsync(filter);
        return await RunOperationAsync(() => LookCoreAsync(filter));
    }

    private async Task<string> LookCoreAsync(string? filter = null) =>
        $"Tab: {TabId}\n" + (await RunDomActionAsync("look", filter)).ToDisplayText();

    /// <summary>
    /// Find and rank interactive elements by query across text/aria/tooltip/title/href.
    /// Returns stable element indices that can be used with lumi_browser_do(click, target).
    /// </summary>
    public async Task<string> FindElementsAsync(string query, int limit = 12, bool preferDialog = true)
    {
        if (_tabOwner is null)
            return await CaptureActiveTab().FindElementsAsync(query, limit, preferDialog);
        return await RunOperationAsync(async () =>
            $"Tab: {TabId}\n" + (await RunDomActionAsync("find", query, limit: limit, preferDialog: preferDialog)).ToDisplayText());
    }

    /// <summary>Perform a browser action. Dispatches to the appropriate internal method.</summary>
    public async Task<string> DoAsync(string action, string? target = null, string? value = null)
    {
        if (_tabOwner is null)
            return await CaptureActiveTab().DoAsync(action, target, value);
        return await RunOperationAsync(() => DoCoreAsync(action, target, value));
    }

    private async Task<string> DoCoreAsync(string action, string? target, string? value)
    {
        var act = (action ?? "").Trim().ToLowerInvariant();

        // "steps" is a meta-action that runs multiple sub-actions with one snapshot at the end.
        if (act == "steps")
            return $"Tab: {TabId}\n" + await ExecuteStepsAsync(value);

        // Check for quiet flag: value="quiet" or target ends with " quiet" suppresses the auto-snapshot.
        var quiet = false;
        if (act is "click" or "press" or "scroll" or "clear" or "back")
        {
            if (string.Equals(value, "quiet", StringComparison.OrdinalIgnoreCase))
            {
                quiet = true;
                value = null;
            }
            else if (target is not null && target.EndsWith(" quiet", StringComparison.OrdinalIgnoreCase))
            {
                quiet = true;
                target = target[..^6].TrimEnd();
            }
        }

        // Actions that change page state — auto-append a snapshot so the LLM sees the result.
        var autoLook = !quiet && act is "click" or "type" or "press" or "select" or "back" or "clear" or "upload";

        // Record time before the action so we can detect click-triggered downloads.
        var beforeAction = DateTime.UtcNow;
        _lastNewWindowUrl = null; // Reset new-window tracker

        var outcome = await ExecuteActionAsync(new(act, target, value));
        var result = $"Tab: {TabId}\n" + outcome.ToDisplayText();
        if (!outcome.Succeeded)
            return result + (quiet ? "" : "\n\n" + await LookCoreAsync());

        if (autoLook)
        {
            // Check if the action triggered a download
            var downloads = GetDownloadsSince(beforeAction);
            if (downloads.Count > 0)
            {
                var status = await GetDownloadStatusAsync(downloads[^1]);
                return result + $"\n\nDownload detected:\n{status}";
            }

            // Check if the click opened a new page (target="_blank" link)
            var newWindowUrl = _lastNewWindowUrl;
            if (newWindowUrl is not null)
            {
                result += $"\n\nNew tab requested: {newWindowUrl}. Observation below remains on the original tab.";
            }

            var snapshot = await LookCoreAsync();
            return result + "\n\n" + snapshot;
        }

        if (quiet)
            return result + " (quiet — no snapshot)";

        return result;
    }

    /// <summary>
    /// Execute a sequence of browser actions, only returning a snapshot after the last one.
    /// This drastically reduces round-trips and token waste for multi-step flows like
    /// calendar navigation or multi-field form interactions.
    /// The value parameter is a JSON array of action objects, each with action/target/value fields.
    /// Example: [{"action":"click","target":"Next month"},{"action":"click","target":"Next month"},{"action":"click","target":"25"}]
    /// </summary>
    private Task<string> ExecuteStepsAsync(string? stepsJson) =>
        BrowserAutomationBatch.ExecuteAsync(stepsJson, ExecuteActionAsync, () => LookCoreAsync());


    /// <summary>
    /// Attach local file(s) to a file input on the page WITHOUT opening the native OS file picker.
    /// Uses the Chrome DevTools Protocol (Runtime.evaluate to locate the input, then
    /// DOM.setFileInputFiles to set the files) — the same technique Playwright/Puppeteer use.
    /// This is the only reliable way to upload because the native file dialog is an OS window that
    /// JavaScript cannot drive.
    ///
    /// Security: because this is reachable from LLM tool calls it is a potential local-file
    /// exfiltration vector, so it enforces a policy before attaching anything — paths must be
    /// fully-qualified and are canonicalized, and the file count and per-file/total sizes are capped.
    ///
    /// <paramref name="target"/> is an optional locator for the file input: a CSS selector, or the
    /// visible text/aria-label of the upload button or label. If it is omitted and the page has more
    /// than one file input, an ambiguity error listing the candidates is returned instead of guessing.
    /// <paramref name="filesValue"/> is one or more absolute file paths: a JSON array (preferred for
    /// multiple files), or — for a single file — a plain path. Non-JSON input is split only on
    /// newlines so paths containing commas remain intact.
    /// </summary>
    public async Task<string> UploadFileAsync(string? target, string? filesValue)
    {
        if (_tabOwner is null)
            return await CaptureActiveTab().UploadFileAsync(target, filesValue);
        return await RunOperationAsync(() => UploadFileCoreAsync(target, filesValue));
    }

    private async Task<string> UploadFileCoreAsync(string? target, string? filesValue)
    {
        var rawPaths = ParseFilePaths(filesValue);
        if (rawPaths.Count == 0)
            return "Error: upload needs file path(s) in the value parameter (absolute paths; a JSON array for multiple files, or a single path)";

        var paths = ValidateUploadPaths(rawPaths, out var policyError);
        if (policyError is not null)
            return policyError;

        await EnsureInitializedAsync();
        await WaitForActionLockAsync();
        string? objectId = null;
        try
        {
            // 1. Resolve the file input by value first so we can detect ambiguity / capability issues
            //    BEFORE obtaining a remote handle or attaching anything.
            var metaExpr = BuildFileInputMetaExpression(target);
            var metaParams = "{\"expression\":" + JsonQuote(metaExpr) + ",\"returnByValue\":true}";
            string metaJson;
            try
            {
                metaJson = await InvokeOnUiThreadAsync(() =>
                    _webView!.CallDevToolsProtocolMethodAsync("Runtime.evaluate", metaParams));
            }
            catch (Exception ex)
            {
                return $"Error: could not evaluate page to find the file input — {ex.Message}";
            }

            string status;
            bool inputMultiple;
            JsonElement candidates = default;
            var hasCandidates = false;
            try
            {
                using var doc = JsonDocument.Parse(metaJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("exceptionDetails", out _))
                    return "Error: page script error while locating the file input";
                var meta = root.GetProperty("result").GetProperty("value");
                status = meta.TryGetProperty("status", out var st) ? st.GetString() ?? "none" : "none";
                inputMultiple = meta.TryGetProperty("multiple", out var mu) && mu.ValueKind == JsonValueKind.True;
                if (meta.TryGetProperty("candidates", out var cand) && cand.ValueKind == JsonValueKind.Array)
                {
                    candidates = cand.Clone();
                    hasCandidates = true;
                }
            }
            catch (Exception ex)
            {
                return $"Error: could not interpret file-input lookup result — {ex.Message}";
            }

            if (status == "none")
            {
                return target is null
                    ? "Error: no file input (<input type=file>) found on the page. Click the page's upload button first so the input appears, then retry — or pass a CSS selector / button text as the target."
                    : $"Error: no file input found for target '{target}'. Pass a CSS selector for the <input type=file>, the upload button's visible text, or omit the target if the page has exactly one file input.";
            }

            if (status == "ambiguous")
                return BuildAmbiguityError(candidates, hasCandidates);

            // 2. Enforce the input's own capability: a single-file input must not receive many files.
            if (paths.Count > 1 && !inputMultiple)
                return $"Error: the selected file input does not accept multiple files, but {paths.Count} were provided. Upload a single file, or target an input with the 'multiple' attribute.";

            // 3. Obtain a CDP remote handle for the resolved input, then set the files on it. No native
            //    dialog is shown; CDP dispatches input/change events so framework components react.
            var elemExpr = BuildFileInputElementExpression(target);
            var elemParams = "{\"expression\":" + JsonQuote(elemExpr) + ",\"returnByValue\":false}";
            string elemJson;
            try
            {
                elemJson = await InvokeOnUiThreadAsync(() =>
                    _webView!.CallDevToolsProtocolMethodAsync("Runtime.evaluate", elemParams));
            }
            catch (Exception ex)
            {
                return $"Error: could not obtain a handle to the file input — {ex.Message}";
            }

            try
            {
                using var doc = JsonDocument.Parse(elemJson);
                if (doc.RootElement.TryGetProperty("result", out var resObj) &&
                    resObj.TryGetProperty("objectId", out var oid))
                    objectId = oid.GetString();
            }
            catch
            {
                // handled by the null check below
            }

            if (string.IsNullOrEmpty(objectId))
                return "Error: the file input could not be resolved to a live element (the page may have changed). Re-open the page and retry.";

            var filesJson = string.Join(",", paths.Select(JsonQuote));
            var setParams = "{\"objectId\":" + JsonQuote(objectId) + ",\"files\":[" + filesJson + "]}";
            try
            {
                await InvokeOnUiThreadAsync(() =>
                    _webView!.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", setParams));
            }
            catch (Exception ex)
            {
                return $"Error: failed to set files on the input — {ex.Message}";
            }

            // 4. Read back what actually got attached for a trustworthy confirmation.
            var attached = string.Join(", ", paths.Select(Path.GetFileName));
            try
            {
                var fnParams = "{\"objectId\":" + JsonQuote(objectId) +
                    ",\"functionDeclaration\":" + JsonQuote("function(){return Array.from(this.files||[]).map(function(f){return f.name;}).join(', ');}") +
                    ",\"returnByValue\":true}";
                var fnJson = await InvokeOnUiThreadAsync(() =>
                    _webView!.CallDevToolsProtocolMethodAsync("Runtime.callFunctionOn", fnParams));
                using var doc = JsonDocument.Parse(fnJson);
                if (doc.RootElement.TryGetProperty("result", out var r) &&
                    r.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var names = v.GetString();
                    if (!string.IsNullOrWhiteSpace(names))
                        attached = names!;
                }
            }
            catch
            {
                // confirmation read-back is best-effort
            }

            return $"Uploaded {paths.Count} file(s) to the file input: {attached}";
        }
        finally
        {
            // Always release the DevTools remote object so repeated uploads don't leak handles.
            if (!string.IsNullOrEmpty(objectId))
            {
                try
                {
                    var releaseParams = "{\"objectId\":" + JsonQuote(objectId) + "}";
                    await InvokeOnUiThreadAsync(() =>
                        _webView!.CallDevToolsProtocolMethodAsync("Runtime.releaseObject", releaseParams));
                }
                catch
                {
                    // best-effort cleanup
                }
            }
            _actionLock.Release();
        }
    }

    /// <summary>Formats the multi-file-input ambiguity error with enough metadata to choose a target.</summary>
    private static string BuildAmbiguityError(JsonElement candidates, bool hasCandidates)
    {
        var sb = new StringBuilder();
        sb.Append("Error: the page has multiple file inputs — specify a target to choose one. Candidates:");
        if (hasCandidates)
        {
            foreach (var c in candidates.EnumerateArray())
            {
                var id = c.TryGetProperty("id", out var idv) ? idv.GetString() : "";
                var name = c.TryGetProperty("name", out var nv) ? nv.GetString() : "";
                var accept = c.TryGetProperty("accept", out var av) ? av.GetString() : "";
                var mult = c.TryGetProperty("multiple", out var mv) && mv.ValueKind == JsonValueKind.True;
                var visible = c.TryGetProperty("visible", out var vv) && vv.ValueKind == JsonValueKind.True;
                var label = c.TryGetProperty("label", out var lv) ? lv.GetString() : "";
                var sel = !string.IsNullOrEmpty(id) ? $"#{id}" :
                          !string.IsNullOrEmpty(name) ? $"input[name=\"{name}\"]" : "(no id/name)";
                sb.Append("\n - ").Append(sel);
                if (!string.IsNullOrEmpty(label)) sb.Append($" label=\"{label}\"");
                if (!string.IsNullOrEmpty(accept)) sb.Append($" accept=\"{accept}\"");
                sb.Append(mult ? " [multiple]" : "");
                sb.Append(visible ? " [visible]" : " [hidden]");
            }
            sb.Append("\nPass one of the selectors above (or its label text) as the target.");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Validates and canonicalizes the requested upload paths against the upload policy. Returns the
    /// canonical paths to hand to CDP, or sets <paramref name="error"/> (and returns an empty list).
    /// </summary>
    private static List<string> ValidateUploadPaths(List<string> rawPaths, out string? error)
    {
        error = null;
        var canonical = new List<string>(rawPaths.Count);

        if (rawPaths.Count > MaxUploadFiles)
        {
            error = $"Error: too many files ({rawPaths.Count}); the maximum per upload is {MaxUploadFiles}.";
            return canonical;
        }

        long total = 0;
        foreach (var raw in rawPaths)
        {
            var p = raw.Trim();
            if (p.Length == 0)
                continue;

            if (!Path.IsPathFullyQualified(p))
            {
                error = $"Error: upload paths must be absolute (fully-qualified). '{p}' is not — pass a full path like C:\\Users\\you\\file.pdf.";
                return [];
            }

            string full;
            try
            {
                full = Path.GetFullPath(p);
            }
            catch (Exception ex)
            {
                error = $"Error: invalid file path '{p}' — {ex.Message}";
                return [];
            }

            if (!File.Exists(full))
            {
                error = $"Error: file not found: {full}";
                return [];
            }

            long len;
            try
            {
                len = new FileInfo(full).Length;
            }
            catch (Exception ex)
            {
                error = $"Error: could not read file '{full}' — {ex.Message}";
                return [];
            }

            if (len > MaxUploadFileBytes)
            {
                error = $"Error: '{Path.GetFileName(full)}' is {len / (1024 * 1024)} MB, over the {MaxUploadFileBytes / (1024 * 1024)} MB per-file limit.";
                return [];
            }

            total += len;
            canonical.Add(full);
        }

        if (canonical.Count == 0)
        {
            error = "Error: no valid file path was provided.";
            return [];
        }

        if (total > MaxTotalUploadBytes)
        {
            error = $"Error: total upload size {total / (1024 * 1024)} MB exceeds the {MaxTotalUploadBytes / (1024 * 1024)} MB limit.";
            return [];
        }

        return canonical;
    }

    /// <summary>JS that defines the shared file-input resolution logic and leaves the chosen element in `resolved`.</summary>
    private static string FileInputResolutionPreambleJs(string? target)
    {
        var targetLiteral = string.IsNullOrWhiteSpace(target) ? "null" : JsonQuote(target.Trim());
        return
            " var target=" + targetLiteral + ";" +
            " function vis(el){if(!el)return false;var r=el.getBoundingClientRect();if(r.width<=0||r.height<=0)return false;var cs=getComputedStyle(el);return cs.display!=='none'&&cs.visibility!=='hidden'&&cs.opacity!=='0';}" +
            " function asFileInput(el){" +
            "  if(!el) return null;" +
            "  if(el.tagName==='INPUT' && (el.type||'').toLowerCase()==='file') return el;" +
            "  if(el.tagName==='LABEL'){ if(el.htmlFor){var lf=document.getElementById(el.htmlFor); if(lf&&(lf.type||'').toLowerCase()==='file')return lf;} var li=el.querySelector('input[type=file]'); if(li)return li; }" +
            "  if(el.querySelector){var qi=el.querySelector('input[type=file]'); if(qi)return qi;}" +
            "  if(el.closest){var lab=el.closest('label'); if(lab){var lq=lab.querySelector('input[type=file]'); if(lq)return lq; if(lab.htmlFor){var lg=document.getElementById(lab.htmlFor); if(lg&&(lg.type||'').toLowerCase()==='file')return lg;}}}" +
            "  return null;" +
            " }" +
            " function lbl(el){ try{ if(el.labels&&el.labels.length){return (el.labels[0].textContent||'').replace(/\\s+/g,' ').trim().slice(0,60);} }catch(e){} return ((el.getAttribute&&el.getAttribute('aria-label'))||el.name||el.id||'').slice(0,60); }" +
            " var allInputs=Array.from(document.querySelectorAll('input[type=file]'));" +
            " var resolved=null; var status='none'; var cands=[];" +
            " if(target){" +
            "  try{ resolved=asFileInput(document.querySelector(target)); }catch(e){}" +
            "  if(!resolved){" +
            "   var c2=Array.from(document.querySelectorAll('button,label,a,[role=\"button\"],div,span,input'));" +
            "   var tl=target.toLowerCase();" +
            "   for(var i=0;i<c2.length;i++){ var c=c2[i]; var t=((c.textContent||'')+' '+((c.getAttribute&&c.getAttribute('aria-label'))||'')).replace(/\\s+/g,' ').trim().toLowerCase(); if(t && t.indexOf(tl)>=0){ var f=asFileInput(c); if(f){resolved=f;break;} } }" +
            "  }" +
            "  status=resolved?'ok':'none';" +
            " } else {" +
            "  if(allInputs.length===0){ status='none'; }" +
            "  else if(allInputs.length===1){ resolved=allInputs[0]; status='ok'; }" +
            "  else { status='ambiguous'; cands=allInputs.map(function(el,i){ return {index:i+1,id:el.id||'',name:el.name||'',accept:(el.getAttribute('accept')||''),multiple:!!el.multiple,visible:vis(el),label:lbl(el)}; }); }" +
            " }";
    }

    /// <summary>JS expression returning a by-value description of the file-input resolution (status, capability, candidates).</summary>
    private static string BuildFileInputMetaExpression(string? target) =>
        "(function(){" + FileInputResolutionPreambleJs(target) +
        " return {status:status, multiple: resolved?!!resolved.multiple:false," +
        " candidates:cands};" +
        "})()";

    /// <summary>JS expression returning the resolved file-input element itself (for a CDP remote objectId).</summary>
    private static string BuildFileInputElementExpression(string? target) =>
        "(function(){" + FileInputResolutionPreambleJs(target) + " return resolved;})()";

    /// <summary>Parses upload paths: a JSON array (preferred for multiple files), otherwise a single
    /// path or newline-separated paths. Commas/semicolons are NOT treated as separators so paths
    /// containing them (e.g. "report, final.pdf") stay intact.</summary>
    private static List<string> ParseFilePaths(string? value)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
            return result;
        value = value.Trim();

        if (value.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(value);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        var s = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
                        if (!string.IsNullOrWhiteSpace(s))
                            result.Add(s!.Trim());
                    }
                    return result;
                }
            }
            catch
            {
                // not valid JSON — fall through to newline parsing
            }
        }

        foreach (var part in value.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var s = part.Trim().Trim('"');
            if (s.Length > 0)
                result.Add(s);
        }
        return result;
    }

    /// <summary>Produces a JSON-encoded, double-quoted string literal (trim-safe, no reflection).</summary>
    private static string JsonQuote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }


    /// <summary>
    /// Check download status. Uses the WebView2 DownloadStarting event —
    /// reports progress/completion immediately, never blocks.
    /// If a matching download was already detected (e.g. from a prior click), reports its status.
    /// Otherwise waits briefly for a new DownloadStarting event.
    /// </summary>
    private async Task<string> WaitForDownloadAsync(string? filePattern, int timeoutMs = 5000)
    {
        var pattern = string.IsNullOrWhiteSpace(filePattern) ? "*" : filePattern;

        // 1. Check if a matching download was already detected by OnDownloadStarting
        var existing = GetDownloadsSince(DateTime.UtcNow.AddSeconds(-60), pattern);
        if (existing.Count > 0)
            return GetDownloadStatus(existing[^1]);

        // 2. Wait for a new DownloadStarting event
        _downloadWaiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var completed = await Task.WhenAny(_downloadWaiter.Task, Task.Delay(timeoutMs));
            if (completed == _downloadWaiter.Task)
            {
                var path = await _downloadWaiter.Task;
                // Find the tracked download to get its operation
                var tracked = _recentDownloads.ToArray()
                    .LastOrDefault(d => d.FilePath == path);
                if (tracked is not null && MatchesGlob(path, pattern))
                    return GetDownloadStatus(tracked);
                return $"Download detected but doesn't match pattern '{pattern}': {path}";
            }
            var hints = await GetDownloadHintsAsync();
            return string.IsNullOrWhiteSpace(hints)
                ? "No download detected."
                : "No download detected.\n\n" + hints;
        }
        finally
        {
            _downloadWaiter = null;
        }
    }

    private async Task<string> GetDownloadHintsAsync(int limit = 6)
        => (await RunDomActionAsync("find", "download export save attachment file csv xlsx", limit: limit)).ToDisplayText();

    private static bool MatchesGlob(string filePath, string pattern)
    {
        if (pattern == "*") return true;
        var fileName = Path.GetFileName(filePath);
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
            return fileName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        return fileName.Contains(pattern.Replace("*", ""), StringComparison.OrdinalIgnoreCase);
    }


    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Store the HWND for lazy initialization (called by BrowserView or MainWindow).</summary>
    public void SetParentHwnd(IntPtr hwnd)
    {
        if (_isDisposed || hwnd == IntPtr.Zero)
            return;
        if (_tabOwner is null)
        {
            _pendingParentHwnd = hwnd;
            lock (_tabsSync)
                foreach (var tab in _tabs)
                    tab.SetParentHwnd(hwnd);
            return;
        }

        lock (_parentHwndSync)
        {
            _pendingParentHwnd = hwnd;
            if (!_initialized || _controller is null || _parentHwnd == hwnd)
                return;
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            ReparentController(hwnd);
        }
        else
        {
            lock (_parentHwndSync)
            {
                if (_isReparentQueued)
                    return;
                _isReparentQueued = true;
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(
                ReparentToPendingParent,
                Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    private void ReparentToPendingParent()
    {
        IntPtr hwnd;
        lock (_parentHwndSync)
        {
            _isReparentQueued = false;
            hwnd = _pendingParentHwnd;
        }

        ReparentController(hwnd);
    }

    private void ReparentController(IntPtr hwnd)
    {
        if (_isDisposed || hwnd == IntPtr.Zero || _parentHwnd == hwnd)
            return;

        var controller = _controller;
        if (controller is null)
            return;

        InvokeOnLiveController(
            "reparent",
            () =>
            {
                var wasVisible = controller.IsVisible;
                controller.IsVisible = false;
                controller.ParentWindow = hwnd;
                _parentHwnd = hwnd;
                _webViewHwnd = IntPtr.Zero;
                controller.IsVisible = wasVisible;
                controller.NotifyParentWindowPositionChanged();
            });
    }

    /// <summary>Ensures the browser is initialized, using the stored HWND if needed.
    /// Marshals to the UI thread if called from a background thread.</summary>
    private async Task EnsureInitializedAsync()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_initialized && _webView is not null) return;

        if (_pendingParentHwnd == IntPtr.Zero)
        {
            // Fallback: resolve from current Avalonia main window at runtime
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow is not null)
            {
                var handle = desktop.MainWindow.TryGetPlatformHandle();
                if (handle is not null)
                    _pendingParentHwnd = handle.Handle;
            }
        }

        if (_pendingParentHwnd == IntPtr.Zero)
            throw new InvalidOperationException(
                "Browser not initialized and no parent HWND available. " +
                "The browser panel must be attached to a window first.");

        // WebView2 must be created on the UI thread
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            await InitializeAsync(_pendingParentHwnd);
        }
        else
        {
            var tcs = new TaskCompletionSource<bool>();
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await InitializeAsync(_pendingParentHwnd);
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            await tcs.Task;
        }
    }

    private static async Task<bool> WaitWithTimeout(Task<bool> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        return completed == task && await task;
    }

    /// <summary>
    /// Wait for either NavigationCompleted or SourceChanged to fire.
    /// NavigationCompleted covers full page loads; SourceChanged covers SPA hash navigations.
    /// </summary>
    private static async Task<bool> WaitForNavigationEventsAsync(
        Task<bool> navigationTask,
        Task<string> sourceChangedTask,
        TimeSpan timeout)
    {
        var timeoutTask = Task.Delay(timeout);
        var completed = await Task.WhenAny(navigationTask, sourceChangedTask, timeoutTask);

        if (completed == navigationTask)
        {
            try { return await navigationTask; }
            catch { return false; }
        }

        if (completed == sourceChangedTask)
            return true; // URL changed — SPA navigation completed

        return false; // timeout
    }

    private static bool IsLikelySpaRoute(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.Contains("#", StringComparison.Ordinal)
            || url.Contains("mail.google.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("contacts.google.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeCssSelector(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        return input.Any(ch => ch is '#' or '.' or '[' or ']' or '>' or ':' or '*' or '+' or '~');
    }

    /// <summary>Clears all cookies used by Lumi's embedded browser profile.</summary>
    public async Task ClearCookiesAsync()
    {
        if (_tabOwner is null)
        {
            await CaptureActiveTab().ClearCookiesAsync();
            return;
        }
        if (_initialized && _webView is not null)
        {
            await InvokeOnUiThreadAsync(() => _webView!.CookieManager.DeleteAllCookies());
            return;
        }

        var userDataFolder = GetUserDataFolder();
        DeleteCookieFiles(userDataFolder);
    }

    private static void DeleteCookieFiles(string userDataFolder)
    {
        if (!Directory.Exists(userDataFolder))
            return;

        static void DeleteFromProfile(string profileDir)
        {
            var files = new[]
            {
                Path.Combine(profileDir, "Network", "Cookies"),
                Path.Combine(profileDir, "Network", "Cookies-journal"),
                Path.Combine(profileDir, "Cookies"),
                Path.Combine(profileDir, "Cookies-journal"),
            };

            foreach (var file in files)
            {
                try
                {
                    if (File.Exists(file))
                        File.Delete(file);
                }
                catch
                {
                }
            }
        }

        try
        {
            DeleteFromProfile(Path.Combine(userDataFolder, "Default"));
            foreach (var profileDir in Directory.GetDirectories(userDataFolder, "Profile *"))
                DeleteFromProfile(profileDir);
        }
        catch
        {
        }
    }

    private static Task InvokeOnUiThreadAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource<bool>();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private static Task<T> InvokeOnUiThreadAsync<T>(Func<T> func)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return Task.FromResult(func());

        var tcs = new TaskCompletionSource<T>();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private static Task<T> InvokeOnUiThreadAsync<T>(Func<Task<T>> func)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return func();

        var tcs = new TaskCompletionSource<T>();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var value = await func();
                tcs.TrySetResult(value);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private static string EscapeJs(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
    }

    /// <summary>
    /// Returns a JS snippet that sets the value on an element variable using the native setter trick
    /// (used by Playwright/React Testing Library) so React/Vue/Angular controlled components pick up the change.
    /// The caller must define the element variable (e.g. "el" or "t") and the text must already be JS-escaped.
    /// </summary>
    private static string FrameworkAwareSetterJs(string elementVar, string escapedText)
    {
        // Strategy: use the native HTMLInputElement/HTMLTextAreaElement prototype setter to bypass
        // React/Vue/Angular value tracking, then dispatch proper InputEvent + change + blur.
        return
            $" {elementVar}.focus();" +
            $" var _tag = {elementVar}.tagName;" +
            $" var _proto = _tag === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;" +
            $" var _nativeSetter = Object.getOwnPropertyDescriptor(_proto, 'value');" +
            $" if (_nativeSetter && _nativeSetter.set) {{ _nativeSetter.set.call({elementVar}, '{escapedText}'); }}" +
            $" else {{ {elementVar}.value = '{escapedText}'; }}" +
            $" if ({elementVar}._valueTracker) {{ {elementVar}._valueTracker.setValue(''); }}" +
            $" {elementVar}.dispatchEvent(new InputEvent('input', {{ bubbles: true, data: '{escapedText}', inputType: 'insertText' }}));" +
            $" {elementVar}.dispatchEvent(new Event('change', {{ bubbles: true }}));" +
            $" {elementVar}.dispatchEvent(new Event('blur', {{ bubbles: true }}));";
    }

    /// <summary>
    /// Returns a JS snippet that clears a field using the native setter trick, then dispatches events.
    /// </summary>
    private static string FrameworkAwareClearJs(string elementVar)
    {
        return
            $" {elementVar}.focus();" +
            $" var _tag = {elementVar}.tagName;" +
            $" var _proto = _tag === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;" +
            $" var _nativeSetter = Object.getOwnPropertyDescriptor(_proto, 'value');" +
            $" if (_nativeSetter && _nativeSetter.set) {{ _nativeSetter.set.call({elementVar}, ''); }}" +
            $" else {{ {elementVar}.value = ''; }}" +
            $" if ({elementVar}._valueTracker) {{ {elementVar}._valueTracker.setValue('x'); }}" +
            $" {elementVar}.dispatchEvent(new InputEvent('input', {{ bubbles: true, data: null, inputType: 'deleteContentBackward' }}));" +
            $" {elementVar}.dispatchEvent(new Event('change', {{ bubbles: true }}));" +
            $" {elementVar}.dispatchEvent(new Event('blur', {{ bubbles: true }}));";
    }

    private static string CleanJsResult(string result)
    {
        if (result.StartsWith('"') && result.EndsWith('"'))
        {
            result = result[1..^1];
            result = result
                .Replace("\\\"", "\"")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t")
                .Replace("\\\\", "\\");
        }
        return result;
    }

    /// <summary>
    /// Import cookies from a browser profile into this WebView2 instance.
    /// Returns the number of cookies imported.
    /// </summary>
    public async Task<int> ImportCookiesAsync(BrowserCookieService.BrowserProfile profile)
    {
        if (_tabOwner is null)
            return await CaptureActiveTab().ImportCookiesAsync(profile);
        await EnsureInitializedAsync();
        await WaitForActionLockAsync();
        try
        {
            return await InvokeOnUiThreadAsync(() => BrowserCookieService.ImportCookiesAsync(profile, _webView!));
        }
        finally
        {
            _actionLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        if (_tabOwner is null)
        {
            BrowserService[] tabs;
            lock (_tabsSync)
            {
                tabs = _tabs.ToArray();
                _tabs.Clear();
                _activeTab = null;
            }
            foreach (var tab in tabs)
                await tab.DisposeAsync();
            _environment = null;
            return;
        }

        await _initLock.WaitAsync().ConfigureAwait(false);
        await _actionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_parentHwndSync)
            {
                _isReparentQueued = false;
                _pendingParentHwnd = IntPtr.Zero;
            }

            if (_controller is not null || _webView is not null)
                await InvokeOnUiThreadAsync(DisposeNativeBrowserOnUiThread).ConfigureAwait(false);
            else
            {
                _environment = null;
                _initialized = false;
                _webViewHwnd = IntPtr.Zero;
            }
        }
        finally
        {
            _actionLock.Release();
            _initLock.Release();
            // Do not dispose these semaphores: late browser/tool calls may already be
            // waiting and need to observe _isDisposed cleanly instead of racing disposal.
        }
    }
}

#else
/// <summary>
/// Non-Windows stub. The embedded browser is built on WebView2 (Windows-only), so the
/// lumi_browser_* tools are not registered on Linux/macOS and the browser panel is never
/// shown. This stub preserves the public API so shared ViewModels/Views compile unchanged;
/// every operation is an inert no-op or returns an "unsupported" message.
/// </summary>
public sealed class BrowserService : IAsyncDisposable
{
    private const string NotSupported = "The embedded browser is only available on Windows.";
    private const int WebViewInvalidStateHResult = unchecked((int)0x8007139F);

#pragma warning disable CS0067 // Part of the shared API surface; never raised in the stub.
    public event Action? BrowserReady;
    public event Action? UrlChanged;
    public event Action? TabsChanged;
    public event Action<string>? BrowserError;
#pragma warning restore CS0067

    public string CurrentUrl => "about:blank";
    public string CurrentTitle => "";
    public bool IsInitialized => false;
    public bool HasController => false;
    public string TabId => "";
    public string ActiveTabId => "";
    public IReadOnlyList<BrowserTabInfo> Tabs => [];
    public Task<string> ManageTabsAsync(string action, string? tabId = null, string? url = null) => Task.FromResult(NotSupported);
    public Task<BrowserScreenshot> CaptureScreenshotAsync(string? tabId = null) => Task.FromException<BrowserScreenshot>(new PlatformNotSupportedException(NotSupported));

    internal static bool IsWebViewInvalidState(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is COMException { HResult: WebViewInvalidStateHResult })
                return true;
        }

        return false;
    }

    public void SetTheme(bool isDark) { }
    public void SetControllerVisible(bool visible) { }
    public void SyncRasterizationScale(double scale) { }
    public void Reload() { }
    public void SetParentHwnd(IntPtr hwnd) { }
    public void SetBounds(int x, int y, int width, int height, int cornerRadiusPx = 0) { }

    public Task InitializeAsync(IntPtr parentHwnd) => Task.CompletedTask;
    public Task<string> NavigateAsync(string url) => Task.FromResult(NotSupported);
    public Task<string> OpenAndSnapshotAsync(string url) => Task.FromResult(NotSupported);
    public Task<string> LookAsync(string? filter = null) => Task.FromResult(NotSupported);
    public Task<string> FindElementsAsync(string query, int limit = 12, bool preferDialog = true) => Task.FromResult(NotSupported);
    public Task<string> DoAsync(string action, string? target = null, string? value = null) => Task.FromResult(NotSupported);
    public Task<string> EvaluateAsync(string javascript) => Task.FromResult(NotSupported);
    public Task<string> PressKeyAsync(string key, string? selector = null) => Task.FromResult(NotSupported);
    public Task<string> WaitForAsync(string selector, int timeoutMs = 10000) => Task.FromResult(NotSupported);
    public Task<string> GoBackAsync() => Task.FromResult(NotSupported);
    public Task<string> ScrollAsync(string direction, int pixels = 500) => Task.FromResult(NotSupported);
    public Task<string> UploadFileAsync(string? target, string? filesValue) => Task.FromResult(NotSupported);
    public Task ClearCookiesAsync() => Task.CompletedTask;
    public Task<int> ImportCookiesAsync(BrowserCookieService.BrowserProfile profile) => Task.FromResult(0);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
#endif

internal static class BrowserAcceleratorShortcut
{
    internal const int ControlKey = 0x11;
    internal const int AltKey = 0x12;
    internal const int LeftWindowsKey = 0x5B;
    internal const int RightWindowsKey = 0x5C;

    private const int AddKey = 0x6B;
    private const int SubtractKey = 0x6D;
    private const int OemPlusKey = 0xBB;
    private const int OemMinusKey = 0xBD;

    internal static int GetUiScaleDelta(
        int virtualKey,
        bool isKeyDown,
        bool controlDown,
        bool altDown,
        bool windowsDown)
    {
        if (!isKeyDown || !controlDown || altDown || windowsDown)
            return 0;

        return virtualKey switch
        {
            AddKey or OemPlusKey => 1,
            SubtractKey or OemMinusKey => -1,
            _ => 0,
        };
    }
}
