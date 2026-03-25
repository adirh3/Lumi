using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;

namespace Lumi.Services;

/// <summary>
/// Manages an embedded WebView2 browser instance and exposes automation tool methods
/// that the LLM can invoke (navigate, click, type, screenshot, JS eval, etc.).
/// </summary>
public sealed class BrowserService : IAsyncDisposable
{
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
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _actionLock = new(1, 1);
    private IntPtr _webViewHwnd;

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

    /// <summary>The current URL loaded in the browser.</summary>
    public string CurrentUrl => _webView?.Source ?? "about:blank";

    /// <summary>The page title.</summary>
    public string CurrentTitle => _webView?.DocumentTitle ?? "";

    /// <summary>Whether the browser has been initialized.</summary>
    public bool IsInitialized => _initialized;

    private static string GetUserDataFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lumi", "browser-data");

    /// <summary>
    /// Sets the browser color scheme to match the app theme.
    /// Safe to call before initialization — the value is stored and applied on init.
    /// </summary>
    public void SetTheme(bool isDark)
    {
        _isDark = isDark;
        if (_controller is not null)
            _controller.DefaultBackgroundColor = isDark
                ? System.Drawing.Color.FromArgb(255, 30, 30, 30)
                : System.Drawing.Color.FromArgb(255, 255, 255, 255);
        if (_webView is not null)
            _webView.Profile.PreferredColorScheme = isDark
                ? CoreWebView2PreferredColorScheme.Dark
                : CoreWebView2PreferredColorScheme.Light;
    }

    /// <summary>The underlying CoreWebView2 (for direct access if needed).</summary>
    public CoreWebView2? WebView => _webView;

    /// <summary>The underlying controller (for resize/bounds).</summary>
    public CoreWebView2Controller? Controller => _controller;

    /// <summary>
    /// Initializes the WebView2 environment and controller with a persistent user data folder.
    /// Uses a shared environment so all per-chat instances share cookies/sessions.
    /// Must be called from the UI thread with a valid HWND.
    /// </summary>
    public async Task InitializeAsync(IntPtr parentHwnd)
    {
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;
            _parentHwnd = parentHwnd;

            // Get or create the shared environment (all instances share cookies)
            await _sharedEnvLock.WaitAsync();
            try
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
            }
            finally
            {
                _sharedEnvLock.Release();
            }

            _controller = await _environment.CreateCoreWebView2ControllerAsync(_parentHwnd);
            _webView = _controller.CoreWebView2;

            // Sync theme with app
            SetTheme(_isDark);

            // Configure settings
            _webView.Settings.IsScriptEnabled = true;
            _webView.Settings.AreDefaultScriptDialogsEnabled = true;
            _webView.Settings.IsWebMessageEnabled = true;
            _webView.Settings.AreDevToolsEnabled = false;
            _webView.Settings.IsStatusBarEnabled = false;
            _webView.Settings.AreDefaultContextMenusEnabled = true;

            // Track navigation completion
            _webView.NavigationCompleted += OnNavigationCompleted;

            // Track URL changes (including SPA hash navigations that skip NavigationCompleted)
            _webView.SourceChanged += OnSourceChanged;

            // Intercept target="_blank" links — redirect to our single browser instance
            _webView.NewWindowRequested += OnNewWindowRequested;

            // Track downloads so we can detect click-triggered downloads
            _webView.DownloadStarting += OnDownloadStarting;

            _initialized = true;
            BrowserReady?.Invoke();
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _navigationTcs?.TrySetResult(e.IsSuccess);
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        var url = _webView?.Source ?? "about:blank";
        _sourceChangedTcs?.TrySetResult(url);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Redirect target="_blank" navigations to our single browser instance
        e.Handled = true;
        _lastNewWindowUrl = e.Uri;
        _webView?.Navigate(e.Uri);
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
        op.StateChanged += (_, _) =>
        {
            tracked.State = op.State switch
            {
                CoreWebView2DownloadState.Completed => 1,
                CoreWebView2DownloadState.Interrupted => 2,
                _ => 0
            };
            tracked.BytesReceived = op.BytesReceived;
            tracked.TotalBytesToReceive = (long)(op.TotalBytesToReceive ?? 0);
        };
        op.BytesReceivedChanged += (_, _) =>
        {
            tracked.BytesReceived = op.BytesReceived;
            tracked.TotalBytesToReceive = (long)(op.TotalBytesToReceive ?? 0);
        };

        _recentDownloads.Enqueue(tracked);
        // Trim old entries
        while (_recentDownloads.Count > 10)
            _recentDownloads.TryDequeue(out _);

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
        if (_controller is null) return;
        _controller.Bounds = new System.Drawing.Rectangle(x, y, width, height);

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

    // ═══════════════════════════════════════════════════════════════
    // Tool Methods — called by the LLM via AIFunction tools
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Navigate to a URL and wait for the page to load.</summary>
    private async Task<string> NavigateAsync(string url)
    {
        await EnsureInitializedAsync();
        await _actionLock.WaitAsync();
        try
        {
            TaskCompletionSource<bool> navTcs = new();
            var previousUrl = await InvokeOnUiThreadAsync(() => _webView!.Source ?? "about:blank");
            var startedAt = DateTime.UtcNow;
            await InvokeOnUiThreadAsync(() =>
            {
                _navigationTcs = navTcs;
                _webView!.Navigate(url);
            });

            // Wait for NavigationCompleted OR SourceChanged (for SPA hash navigations)
            var sourceChangeTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _sourceChangedTcs = sourceChangeTcs;
            var timeout = IsLikelySpaRoute(url)
                ? TimeSpan.FromSeconds(8)
                : TimeSpan.FromSeconds(18);
            var success = await WaitForNavigationEventsAsync(navTcs.Task, sourceChangeTcs.Task, timeout);
            _sourceChangedTcs = null;
            await Task.Delay(200); // brief settle time

            // Check if navigation actually triggered a file download (e.g. export URLs)
            var downloads = GetDownloadsSince(startedAt);
            if (downloads.Count > 0)
            {
                var status = await GetDownloadStatusAsync(downloads[^1]);
                return $"Navigation triggered a file download:\n{status}";
            }

            var page = await InvokeOnUiThreadAsync(() =>
                (_webView!.Source ?? "about:blank", _webView.DocumentTitle ?? ""));
            var elapsed = (DateTime.UtcNow - startedAt).TotalSeconds;

            return success
                ? $"Navigated to {page.Item1}. Page title: {page.Item2}. ({elapsed:F1}s)"
                : $"Navigation to {url} timed out after {elapsed:F1}s.";
        }
        finally
        {
            _actionLock.Release();
        }
    }

    /// <summary>Click an element matching a CSS selector or containing text.</summary>
    private async Task<string> ClickAsync(string selector)
    {
        await EnsureInitializedAsync();
        await _actionLock.WaitAsync();
        try
        {
            var escaped = EscapeJs(selector);
            var mode = LooksLikeCssSelector(selector) ? "css" : "text";
            var script =
                "(function() {" +
                "  const query = '" + escaped + "';" +
                "  const mode = '" + mode + "';" +
                "  const clickableSel = 'button,a,[role=\"button\"],[role=\"menuitem\"],[role=\"link\"],input[type=\"button\"],input[type=\"submit\"],summary,[tabindex],[data-tooltip]';" +
                "  const norm = (s) => (s || '').replace(/\\s+/g, ' ').trim();" +
                "  const isVisible = (el) => { if (!el) return false; const r = el.getBoundingClientRect(); if (r.width <= 0 || r.height <= 0) return false; const cs = getComputedStyle(el); return cs.display !== 'none' && cs.visibility !== 'hidden' && cs.opacity !== '0'; };" +
                "  const pickClickable = (el) => { if (!el) return null; if (el.matches && el.matches(clickableSel)) return el; if (el.closest) return el.closest(clickableSel); return null; };" +
                "  const rootDialogs = [...document.querySelectorAll('[role=\"dialog\"],[aria-modal=\"true\"]')].filter(isVisible);" +
                "  const roots = rootDialogs.length > 0 ? [...rootDialogs.reverse(), document] : [document];" +
                "  const findByText = (root, exact) => { const q = norm(query).toLowerCase(); const candidates = [...root.querySelectorAll(clickableSel)].filter(isVisible); for (const el of candidates) { const aria = norm(el.getAttribute('aria-label')).toLowerCase(); const txt = norm(el.textContent).toLowerCase(); const tip = norm(el.getAttribute('data-tooltip')||el.getAttribute('title')).toLowerCase(); if (exact) { if ((aria && aria === q) || (txt && txt === q) || (tip && tip === q)) return el; } else { if ((aria && aria.includes(q)) || (txt && txt.includes(q)) || (tip && tip.includes(q))) return el; } } return null; };" +
                "  let target = null;" +
                "  if (mode === 'css') { for (const root of roots) { target = root.querySelector(query); if (target) break; } }" +
                "  else { for (const root of roots) { target = findByText(root, true) || findByText(root, false); if (target) break; } }" +
                "  target = pickClickable(target) || target;" +
                "  if (!target || !isVisible(target)) return 'error: no clickable element found for ' + query;" +
                "  if (target.focus) target.focus();" +
                "  target.click();" +
                "  const info = norm(target.textContent || target.getAttribute('aria-label') || '');" +
                "  const tip = norm(target.getAttribute('data-tooltip') || target.getAttribute('title') || '');" +
                "  const link = (target.href || (target.closest && target.closest('a[href]') ? target.closest('a[href]').href : '') || '').substring(0,200);" +
                "  let out = 'clicked: ' + (target.tagName || '') + ' ' + info.substring(0, 80);" +
                "  if (tip && tip !== info) out += ' tooltip=\"' + tip.substring(0,80) + '\"';" +
                "  if (link) out += ' -> ' + link;" +
                "  return out;" +
                "})()";

            var result = await InvokeOnUiThreadAsync(() => _webView!.ExecuteScriptAsync(script));
            await Task.Delay(300);
            return CleanJsResult(result);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    /// <summary>Click a visible, clickable element by text. Can prioritize dialog content.</summary>
    private async Task<string> ClickTextAsync(string text, bool exact = true, bool preferDialog = true)
    {
        await EnsureInitializedAsync();
        await _actionLock.WaitAsync();
        try
        {
            var escapedText = EscapeJs(text);
            var exactJs = exact ? "true" : "false";
            var preferDialogJs = preferDialog ? "true" : "false";
            var script =
                "(function() {" +
                "  const text = '" + escapedText + "';" +
                "  const exact = " + exactJs + ";" +
                "  const preferDialog = " + preferDialogJs + ";" +
                "  const clickableSel = 'button,a,[role=\"button\"],[role=\"menuitem\"],[role=\"link\"],input[type=\"button\"],input[type=\"submit\"],summary,[tabindex],[data-tooltip]';" +
                "  const norm = (s) => (s || '').replace(/\\s+/g, ' ').trim();" +
                "  const isVisible = (el) => { if (!el) return false; const r = el.getBoundingClientRect(); if (r.width <= 0 || r.height <= 0) return false; const cs = getComputedStyle(el); return cs.display !== 'none' && cs.visibility !== 'hidden' && cs.opacity !== '0'; };" +
                "  const find = (root) => { const q = norm(text).toLowerCase(); const candidates = [...root.querySelectorAll(clickableSel)].filter(isVisible); for (const el of candidates) { const aria = norm(el.getAttribute('aria-label')).toLowerCase(); const txt = norm(el.textContent).toLowerCase(); const tip = norm(el.getAttribute('data-tooltip')||el.getAttribute('title')).toLowerCase(); if (exact) { if ((aria && aria === q) || (txt && txt === q) || (tip && tip === q)) return el; } else { if ((aria && aria.includes(q)) || (txt && txt.includes(q)) || (tip && tip.includes(q))) return el; } } return null; };" +
                "  const dialogs = [...document.querySelectorAll('[role=\"dialog\"],[aria-modal=\"true\"]')].filter(isVisible);" +
                "  let target = null;" +
                "  if (preferDialog && dialogs.length > 0) { for (const d of dialogs.reverse()) { target = find(d); if (target) break; } }" +
                "  if (!target) target = find(document);" +
                "  if (!target || !isVisible(target)) return 'error: no clickable element found for text: ' + text;" +
                "  if (target.focus) target.focus();" +
                "  target.click();" +
                "  const info = norm(target.textContent || target.getAttribute('aria-label') || '').substring(0,80);" +
                "  const tip = norm(target.getAttribute('data-tooltip') || target.getAttribute('title') || '');" +
                "  const link = (target.href || (target.closest && target.closest('a[href]') ? target.closest('a[href]').href : '') || '').substring(0,200);" +
                "  let out = 'clicked by text: ' + info;" +
                "  if (tip && tip !== info) out += ' tooltip=\"' + tip.substring(0,80) + '\"';" +
                "  if (link) out += ' -> ' + link;" +
                "  return out;" +
                "})()";

            var result = await InvokeOnUiThreadAsync(() => _webView!.ExecuteScriptAsync(script));
            await Task.Delay(250);
            return CleanJsResult(result);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    /// <summary>Press a keyboard key on the active element or an optional selector target.</summary>
    public async Task<string> PressKeyAsync(string key, string? selector = null)
    {
        await EnsureInitializedAsync();
        await _actionLock.WaitAsync();
        try
        {
            var escapedKey = EscapeJs(key);
            var escapedSel = EscapeJs(selector ?? "");
            var script =
                "(function(){" +
                " const key='" + escapedKey + "';" +
                " const sel='" + escapedSel + "';" +
                " let el=null;" +
                " if(sel) el=document.querySelector(sel);" +
                " if(!el) el=document.activeElement || document.body;" +
                " if(el && el.focus) el.focus();" +
                " const opts={ key:key, code:key, bubbles:true, cancelable:true };" +
                " el.dispatchEvent(new KeyboardEvent('keydown', opts));" +
                " el.dispatchEvent(new KeyboardEvent('keypress', opts));" +
                " el.dispatchEvent(new KeyboardEvent('keyup', opts));" +
                " if(key.toLowerCase()==='enter' && el && typeof el.form !== 'undefined' && el.form){ try { el.form.requestSubmit ? el.form.requestSubmit() : el.form.submit(); } catch {} }" +
                " return 'pressed key '+key;" +
                "})()";

            var result = await InvokeOnUiThreadAsync(() => _webView!.ExecuteScriptAsync(script));
            await Task.Delay(150);
            return CleanJsResult(result);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    /// <summary>Type text into an element matching a CSS selector.</summary>
    private async Task<string> TypeAsync(string selector, string text)
    {
        await EnsureInitializedAsync();
        await _actionLock.WaitAsync();
        try
        {
            var escapedSel = EscapeJs(selector);
            var escapedText = EscapeJs(text);
            var script =
                "(function() {" +
                "  let el = document.querySelector('" + escapedSel + "');" +
                "  if (!el) { el = document.querySelector('input[placeholder*=\"" + escapedSel + "\"], textarea[placeholder*=\"" + escapedSel + "\"]'); }" +
                "  if (!el) {" +
                "    const labels = document.querySelectorAll('label');" +
                "    for (const label of labels) {" +
                "      if (label.textContent.includes('" + escapedSel + "') && label.htmlFor) { el = document.getElementById(label.htmlFor); break; }" +
                "    }" +
                "  }" +
                "  if (!el) return 'error: no input found for: " + escapedSel + "';" +
                FrameworkAwareSetterJs("el", escapedText) +
                "  return 'typed into: ' + (el.tagName || '') + ' [' + (el.name || el.id || '') + ']';" +
                "})()";

            var result = await InvokeOnUiThreadAsync(() => _webView!.ExecuteScriptAsync(script));
            return CleanJsResult(result);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    /// <summary>Execute arbitrary JavaScript and return the result. Wraps in try/catch for better error reporting.</summary>
    public async Task<string> EvaluateAsync(string javascript)
    {
        await EnsureInitializedAsync();
        await _actionLock.WaitAsync();
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
        await EnsureInitializedAsync();
        await _actionLock.WaitAsync();
        try
        {
            var start = Environment.TickCount64;
            while (Environment.TickCount64 - start < timeoutMs)
            {
                var script = $"document.querySelector('{EscapeJs(selector)}') !== null";
                var result = await InvokeOnUiThreadAsync(() => _webView!.ExecuteScriptAsync(script));
                if (result == "true")
                    return $"Element found: {selector}";
                await Task.Delay(250);
            }
            return $"Timeout waiting for: {selector}";
        }
        finally
        {
            _actionLock.Release();
        }
    }

    /// <summary>
    /// Select an option from a dropdown/select element. Works with native &lt;select&gt; elements
    /// and custom dropdown components (react-select, MUI, downshift, etc.) by clicking to open
    /// and then clicking the matching option.
    /// </summary>
    private async Task<string> SelectAsync(string selector, string value)
    {
        await EnsureInitializedAsync();
        await _actionLock.WaitAsync();
        try
        {
            var escapedSel = EscapeJs(selector);
            var escapedVal = EscapeJs(value);
            var script =
                "(async function() {" +
                // Try element by number first
                "  var isNum=/^\\d+$/.test('" + escapedSel + "');" +
                "  var el=null;" +
                "  if(isNum){" +
                "    var vis=function(el){if(!el)return false;var r=el.getBoundingClientRect();if(r.width<=0||r.height<=0)return false;var cs=getComputedStyle(el);return cs.display!=='none'&&cs.visibility!=='hidden'&&cs.opacity!=='0';};" +
                "    var sel='a[href],button,input,select,textarea,[role=\"button\"],[role=\"link\"],[role=\"tab\"],[role=\"menuitem\"],[role=\"radio\"],[role=\"checkbox\"],[role=\"switch\"],[role=\"combobox\"],[role=\"option\"],[role=\"gridcell\"],[role=\"spinbutton\"],[role=\"slider\"],[onclick],[tabindex],[contenteditable],[data-tooltip]';" +
                "    var dialogs=Array.from(document.querySelectorAll('[role=\"dialog\"],[aria-modal=\"true\"]')).filter(vis);" +
                "    var roots=dialogs.length>0?dialogs.reverse().concat([document]):[document];" +
                "    var all=[];var seen=new Set();" +
                "    for(var ri=0;ri<roots.length;ri++){var els=roots[ri].querySelectorAll(sel);for(var ei=0;ei<els.length;ei++){var e=els[ei];if(!vis(e)||seen.has(e))continue;seen.add(e);all.push(e);}}" +
                "    var idx=parseInt('" + escapedSel + "');" +
                "    if(idx>=1&&idx<=all.length)el=all[idx-1];" +
                "  } else {" +
                "    el=document.querySelector('" + escapedSel + "');" +
                "  }" +
                "  if(!el) return 'error: element not found for: " + escapedSel + "';" +

                // Case 1: Native <select>
                "  if(el.tagName==='SELECT'){" +
                "    var opts=Array.from(el.options);" +
                "    var opt=opts.find(function(o){return o.value==='" + escapedVal + "'||o.text.toLowerCase().indexOf('" + escapedVal + "'.toLowerCase())>=0;});" +
                "    if(!opt)return 'error: option not found: " + escapedVal + "';" +
                "    var ns=Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype,'value');" +
                "    if(ns&&ns.set){ns.set.call(el,opt.value);}else{el.value=opt.value;}" +
                "    el.dispatchEvent(new Event('change',{bubbles:true}));" +
                "    el.dispatchEvent(new Event('input',{bubbles:true}));" +
                "    return 'selected: '+opt.text;" +
                "  }" +

                // Case 2: Custom dropdown — click to open, find option, click it
                "  el.click(); el.focus();" +
                "  var val='" + escapedVal + "'.toLowerCase();" +

                // Wait a tick for the dropdown to open, then search for the option
                "  return await new Promise(function(resolve){" +
                "    setTimeout(function(){" +
                "      var optionSels='[role=\"option\"],[role=\"menuitem\"],[role=\"listitem\"],li[data-value],li[class*=\"option\"],div[class*=\"option\"],div[class*=\"Option\"],span[class*=\"option\"]';" +
                "      var options=document.querySelectorAll(optionSels);" +
                "      var best=null;" +
                "      for(var oi=0;oi<options.length;oi++){" +
                "        var o=options[oi];" +
                "        var r=o.getBoundingClientRect();" +
                "        if(r.width<=0||r.height<=0)continue;" +
                "        var txt=(o.textContent||'').replace(/\\s+/g,' ').trim().toLowerCase();" +
                "        var dval=(o.getAttribute('data-value')||'').toLowerCase();" +
                "        if(txt===val||dval===val){best=o;break;}" +
                "        if(txt.indexOf(val)>=0||dval.indexOf(val)>=0){if(!best)best=o;}" +
                "      }" +
                "      if(best){best.click();resolve('selected: '+(best.textContent||'').replace(/\\s+/g,' ').trim().substring(0,60));}" +
                "      else{resolve('error: option not found in dropdown: " + escapedVal + "');}" +
                "    },150);" +
                "  });" +
                "})()";

            var result = await InvokeOnUiThreadAsync(() => _webView!.ExecuteScriptAsync(script));
            return CleanJsResult(result);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    /// <summary>Go back in browser history.</summary>
    public async Task<string> GoBackAsync()
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
            await WaitWithTimeout(navTcs.Task, TimeSpan.FromSeconds(10));
            var source = await InvokeOnUiThreadAsync(() => _webView!.Source ?? "about:blank");
            return $"Navigated back to: {source}";
        }
        return "Cannot go back — no previous page.";
    }

    /// <summary>Scroll the page up or down.</summary>
    public async Task<string> ScrollAsync(string direction, int pixels = 500)
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
        var navResult = await NavigateAsync(url);
        if (navResult.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return navResult;
        // If navigation triggered a download, return that immediately — don't snapshot the blank page
        if (navResult.Contains("Download", StringComparison.Ordinal))
            return navResult;
        await WaitForContentSettleAsync();
        return await LookAsync();
    }

    /// <summary>Polls until page text length AND element count stabilize (dynamic content finished rendering).</summary>
    private async Task WaitForContentSettleAsync(int maxWaitMs = 4000, int pollMs = 300)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(maxWaitMs);
        var lastTextLen = -1;
        var lastElemCount = -1;
        var stableCount = 0;
        while (DateTime.UtcNow < deadline)
        {
            var (textLen, elemCount) = await GetPageMetricsAsync();
            if (textLen == lastTextLen && elemCount == lastElemCount && textLen > 200)
            {
                stableCount++;
                if (stableCount >= 2) return; // unchanged for 2 consecutive polls — settled
            }
            else
            {
                stableCount = 0;
            }
            lastTextLen = textLen;
            lastElemCount = elemCount;
            await Task.Delay(pollMs);
        }
    }

    private async Task<(int TextLength, int ElementCount)> GetPageMetricsAsync()
    {
        try
        {
            var result = await InvokeOnUiThreadAsync(
                () => _webView!.ExecuteScriptAsync(
                    "((document.body.innerText||'').length+','+" +
                    "document.querySelectorAll('a[href],button,input,select,textarea,[role]').length)"));
            var clean = result.Trim('"');
            var parts = clean.Split(',');
            var textLen = parts.Length > 0 && int.TryParse(parts[0], out var t) ? t : 0;
            var elemCount = parts.Length > 1 && int.TryParse(parts[1], out var e) ? e : 0;
            return (textLen, elemCount);
        }
        catch { return (0, 0); }
    }

    /// <summary>Get current page state with numbered interactive elements, optionally filtered.</summary>
    public async Task<string> LookAsync(string? filter = null)
    {
        await EnsureInitializedAsync();
        await _actionLock.WaitAsync();
        try
        {
            var escapedFilter = EscapeJs(filter ?? "");
            var script =
                "(function(){" +
                " var filter='" + escapedFilter + "'.toLowerCase();" +
                " var norm=function(s){return (s||'').replace(/\\s+/g,' ').trim();};" +
                " var vis=function(el){if(!el)return false;var r=el.getBoundingClientRect();if(r.width<=0||r.height<=0)return false;var cs=getComputedStyle(el);return cs.display!=='none'&&cs.visibility!=='hidden'&&cs.opacity!=='0';};" +
                " var sel='a[href],button,input,select,textarea,[role=\"button\"],[role=\"link\"],[role=\"tab\"],[role=\"menuitem\"],[role=\"radio\"],[role=\"checkbox\"],[role=\"switch\"],[role=\"combobox\"],[role=\"option\"],[role=\"gridcell\"],[role=\"spinbutton\"],[role=\"slider\"],[onclick],[tabindex],[contenteditable],[data-tooltip]';" +
                " var dialogs=Array.from(document.querySelectorAll('[role=\"dialog\"],[aria-modal=\"true\"]')).filter(vis);" +
                " var roots=dialogs.length>0?dialogs.reverse().concat([document]):[document];" +
                " var items=[];var seen=new Set();" +
                " for(var ri=0;ri<roots.length;ri++){var els=roots[ri].querySelectorAll(sel);for(var ei=0;ei<els.length;ei++){var el=els[ei];if(!vis(el)||seen.has(el))continue;seen.add(el);" +
                "   var tag=el.tagName.toLowerCase();var type=el.type||'';var text=norm(el.textContent).substring(0,60);" +
                "   var aria=norm(el.getAttribute('aria-label'));var ph=el.placeholder||'';" +
                "   var href=el.href?el.href.substring(0,200):'';var role=el.getAttribute('role')||'';" +
                "   var name=el.name||el.id||'';var inDlg=!!el.closest('[role=\"dialog\"],[aria-modal=\"true\"]');" +
                "   var tooltip=norm(el.getAttribute('data-tooltip')||el.getAttribute('title'));" +
                "   if(filter){var s=(tag+' '+type+' '+text+' '+aria+' '+ph+' '+role+' '+name+' '+tooltip).toLowerCase();if(s.indexOf(filter)<0)continue;}" +
                "   var label=tag==='a'?'link':tag==='button'||role==='button'?'button':tag==='input'?'input'+(type?'['+type+']':''):tag==='select'?'select':tag==='textarea'?'textarea':role||tag;" +
                "   var info='';if(text)info+=' \"'+text+'\"';if(aria&&aria!==text)info+=' aria=\"'+aria+'\"';if(tooltip&&tooltip!==text&&tooltip!==aria)info+=' tooltip=\"'+tooltip+'\"';if(ph)info+=' placeholder=\"'+ph+'\"';if(href)info+=' -> '+href;if(name)info+=' name=\"'+name+'\"';if(inDlg)info+=' [dialog]';" +
                "   items.push('['+(items.length+1)+'] '+label+info);" +
                "   if(items.length>=50)break;" +
                " }if(items.length>=50)break;}" +
                " var pageText=norm(document.body.innerText||'').substring(0,1500);" +
                " return 'Page: '+document.title+'\\nURL: '+location.href+'\\n\\n--- Elements'+(filter?' (filter: '+filter+')':'')+' ---\\n'+(items.length>0?items.join('\\n'):'(no matching elements)')+'\\n('+items.length+' shown)\\n\\n--- Text Preview ---\\n'+pageText;" +
                "})()";

            var result = await InvokeOnUiThreadAsync(() => _webView!.ExecuteScriptAsync(script));
            return CleanJsResult(result);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    /// <summary>
    /// Find and rank interactive elements by query across text/aria/tooltip/title/href.
    /// Returns stable element indices that can be used with browser_do(click, target).
    /// </summary>
    public async Task<string> FindElementsAsync(string query, int limit = 12, bool preferDialog = true)
    {
        await EnsureInitializedAsync();
        await _actionLock.WaitAsync();
        try
        {
            var escapedQuery = EscapeJs(query ?? string.Empty);
            var jsLimit = Math.Clamp(limit, 1, 50);
            var preferDialogJs = preferDialog ? "true" : "false";

            var script =
                "(function(){" +
                " const query='" + escapedQuery + "';" +
                " const limit=" + jsLimit + ";" +
                " const preferDialog=" + preferDialogJs + ";" +
                " const tokens=query.toLowerCase().split(/\\s+/).filter(Boolean);" +
                " const queryLower=query.toLowerCase();" +
                " const wantsDownload=/download|save|export|attachment|file|xlsx|csv|\\u05d4\\u05d5\\u05e8\\u05d3|\\u05e7\\u05d5\\u05d1\\u05e5/.test(queryLower);" +
                " const norm=(s)=> (s||'').replace(/\\s+/g,' ').trim();" +
                " const vis=(el)=>{ if(!el) return false; const r=el.getBoundingClientRect(); if(r.width<=0||r.height<=0) return false; const cs=getComputedStyle(el); return cs.display!=='none'&&cs.visibility!=='hidden'&&cs.opacity!=='0'; };" +
                " const sel='" + InteractiveElementSelectorJs + "';" +
                " const dialogs=[...document.querySelectorAll('[role=\\\"dialog\\\"],[aria-modal=\\\"true\\\"]')].filter(vis);" +
                " const roots=(preferDialog && dialogs.length>0) ? [...dialogs.reverse(),document] : [document];" +
                " const all=[]; const seen=new Set();" +
                " for(const root of roots){ for(const el of root.querySelectorAll(sel)){ if(!vis(el)) continue; if(seen.has(el)) continue; seen.add(el);" +
                "   const tag=(el.tagName||'').toLowerCase(); const type=norm(el.type||''); const text=norm(el.textContent).substring(0,120);" +
                "   const aria=norm(el.getAttribute('aria-label')); const tooltip=norm(el.getAttribute('data-tooltip')||el.getAttribute('title'));" +
                "   const role=norm(el.getAttribute('role')); const name=norm(el.getAttribute('name')||el.id); const href=norm(el.href||'').substring(0,200);" +
                "   const inDialog=!!el.closest('[role=\\\"dialog\\\"],[aria-modal=\\\"true\\\"]');" +
                "   const label=tag==='a'?'link':tag==='button'||role==='button'?'button':tag==='input'?'input'+(type?'['+type+']':''):tag==='select'?'select':tag==='textarea'?'textarea':role||tag;" +
                "   all.push({ index: all.length+1, label, tag, type, text, aria, tooltip, role, name, href, inDialog });" +
                " }}" +
                " const score=(it)=>{" +
                "   if(tokens.length===0) return 1;" +
                "   let s=0;" +
                "   const textL=it.text.toLowerCase(); const ariaL=it.aria.toLowerCase(); const tipL=it.tooltip.toLowerCase(); const titleL=tipL;" +
                "   const hay=(it.text+' '+it.aria+' '+it.tooltip+' '+it.role+' '+it.name+' '+it.href+' '+it.type+' '+it.label).toLowerCase();" +
                "   for(const t of tokens){ if(!t) continue;" +
                "     if(textL===t || ariaL===t || tipL===t || titleL===t) s+=50;" +
                "     if(hay.includes(t)) s+=12;" +
                "   }" +
                "   if(wantsDownload){" +
                "     if(/download|save|export|attachment|file|xlsx|csv|\\u05d4\\u05d5\\u05e8\\u05d3|\\u05e7\\u05d5\\u05d1\\u05e5/.test(hay)) s+=18;" +
                "     if(/attid=|view=att|disp=safe|realattid=|download|export/.test((it.href||'').toLowerCase())) s+=30;" +
                "   }" +
                "   if(it.label==='button') s+=3;" +
                "   if(it.inDialog) s+=2;" +
                "   return s;" +
                " };" +
                " const ranked=all.map(it=>({ ...it, score: score(it) }))" +
                "   .filter(it=>tokens.length===0 ? true : it.score>0)" +
                "   .sort((a,b)=> b.score-a.score || a.index-b.index)" +
                "   .slice(0, limit);" +
                " const lines=[];" +
                " lines.push('Page: '+document.title);" +
                " lines.push('URL: '+location.href);" +
                " lines.push('');" +
                " lines.push('Matches for \"'+query+'\": '+ranked.length);" +
                " for(const it of ranked){" +
                "   let info='['+it.index+'] '+it.label;" +
                "   if(it.text) info+=' text=\"'+it.text.substring(0,80)+'\"';" +
                "   if(it.aria && it.aria!==it.text) info+=' aria=\"'+it.aria.substring(0,80)+'\"';" +
                "   if(it.tooltip && it.tooltip!==it.text && it.tooltip!==it.aria) info+=' tooltip=\"'+it.tooltip.substring(0,80)+'\"';" +
                "   if(it.name) info+=' name=\"'+it.name.substring(0,60)+'\"';" +
                "   if(it.href) info+=' href='+it.href;" +
                "   lines.push(info);" +
                " }" +
                " if(ranked.length===0){ lines.push('No matching interactive elements found.'); }" +
                " return lines.join('\\n');" +
                "})()";

            var result = await InvokeOnUiThreadAsync(() => _webView!.ExecuteScriptAsync(script));
            return CleanJsResult(result);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    /// <summary>Perform a browser action. Dispatches to the appropriate internal method.</summary>
    public async Task<string> DoAsync(string action, string? target = null, string? value = null)
    {
        var act = (action ?? "").Trim().ToLowerInvariant();

        // "steps" is a meta-action that runs multiple sub-actions with one snapshot at the end.
        if (act == "steps")
            return await ExecuteStepsAsync(value);

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
        var autoLook = !quiet && act is "click" or "type" or "press" or "select" or "back" or "clear";

        // Record time before the action so we can detect click-triggered downloads.
        var beforeAction = DateTime.UtcNow;
        _lastNewWindowUrl = null; // Reset new-window tracker

        var result = act switch
        {
            "click" => await DoClickAsync(target),
            "type" => await DoTypeAsync(target, value),
            "press" => await PressKeyAsync(target ?? "Enter"),
            "select" => string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(value)
                ? "Error: select needs target and value"
                : await SelectAsync(target, value),
            "scroll" => await ScrollAsync(target ?? "down", int.TryParse(value, out var px) ? px : 500),
            "back" => await GoBackAsync(),
            "wait" => await WaitForAsync(target ?? "body", int.TryParse(value, out var ms) ? ms : 10000),
            "download" => await WaitForDownloadAsync(target),
            "clear" => await ClearFieldAsync(target),
            "fill" => await FillFormAsync(value),
            "read_form" => await ReadFormAsync(),
            _ => $"Unknown action '{act}'. Valid: click, type, press, select, scroll, back, wait, download, clear, fill, read_form, steps"
        };

        if (result.StartsWith("Error", StringComparison.Ordinal))
            return result;

        if (autoLook)
        {
            await WaitForContentSettleAsync(maxWaitMs: 2000, pollMs: 250);

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
                result += $"\n\nNavigated to: {newWindowUrl}";
            }

            var snapshot = await LookAsync();
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
    private async Task<string> ExecuteStepsAsync(string? stepsJson)
    {
        if (string.IsNullOrWhiteSpace(stepsJson))
            return "Error: steps needs a value parameter — JSON array of actions, e.g. [{\"action\":\"click\",\"target\":\"Next month\"},{\"action\":\"click\",\"target\":\"25\"}]";

        List<StepDef>? steps;
        try
        {
            steps = System.Text.Json.JsonSerializer.Deserialize<List<StepDef>>(stepsJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            return $"Error: invalid JSON for steps — {ex.Message}";
        }

        if (steps is null || steps.Count == 0)
            return "Error: steps array is empty";

        if (steps.Count > 20)
            return "Error: max 20 steps per call";

        var results = new List<string>();
        foreach (var step in steps)
        {
            var act = (step.Action ?? "").Trim().ToLowerInvariant();

            // Don't allow nested steps or long-running actions
            if (act is "steps" or "download" or "wait")
            {
                results.Add($"{act}: skipped (not allowed in steps)");
                continue;
            }

            var result = act switch
            {
                "click" => await DoClickAsync(step.Target),
                "type" => await DoTypeAsync(step.Target, step.Value),
                "press" => await PressKeyAsync(step.Target ?? "Enter"),
                "select" => string.IsNullOrWhiteSpace(step.Target) || string.IsNullOrWhiteSpace(step.Value)
                    ? "Error: select needs target and value"
                    : await SelectAsync(step.Target, step.Value),
                "scroll" => await ScrollAsync(step.Target ?? "down", int.TryParse(step.Value, out var px) ? px : 500),
                "back" => await GoBackAsync(),
                "clear" => await ClearFieldAsync(step.Target),
                "fill" => await FillFormAsync(step.Value),
                "read_form" => await ReadFormAsync(),
                _ => $"Unknown action: {act}"
            };

            results.Add($"{act}({step.Target ?? ""}): {(result.Length > 120 ? result[..120] + "..." : result)}");

            // Brief pause between steps so the page can react
            await Task.Delay(150);
        }

        // One final settle + snapshot
        await WaitForContentSettleAsync(maxWaitMs: 2000, pollMs: 250);
        var snapshot = await LookAsync();

        return $"Executed {steps.Count} steps:\n" + string.Join("\n", results) + "\n\n" + snapshot;
    }

    private sealed class StepDef
    {
        public string? Action { get; set; }
        public string? Target { get; set; }
        public string? Value { get; set; }
    }

    private async Task<string> DoClickAsync(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return "Error: click needs a target — element number, button text, or CSS selector";
        target = target.Trim();
        if (int.TryParse(target.TrimStart('#'), out var idx))
            return await ClickByNumberAsync(idx);
        if (LooksLikeCssSelector(target))
            return await ClickAsync(target);
        return await ClickTextAsync(target, exact: false, preferDialog: true);
    }

    private async Task<string> DoTypeAsync(string? target, string? value)
    {
        if (string.IsNullOrWhiteSpace(target) || value is null)
            return "Error: type needs target (element number or selector) and value (text)";
        target = target.Trim();
        if (int.TryParse(target.TrimStart('#'), out var idx))
            return await TypeByNumberAsync(idx, value);
        return await TypeAsync(target, value);
    }

    private async Task<string> ClickByNumberAsync(int index)
    {
        await EnsureInitializedAsync();
        await _actionLock.WaitAsync();
        try
        {
            var script =
                "(function(){" +
                " var idx=" + Math.Max(1, index) + ";" +
                " var vis=function(el){if(!el)return false;var r=el.getBoundingClientRect();if(r.width<=0||r.height<=0)return false;var cs=getComputedStyle(el);return cs.display!=='none'&&cs.visibility!=='hidden'&&cs.opacity!=='0';};" +
                " var sel='a[href],button,input,select,textarea,[role=\"button\"],[role=\"link\"],[role=\"tab\"],[role=\"menuitem\"],[role=\"radio\"],[role=\"checkbox\"],[role=\"switch\"],[role=\"combobox\"],[role=\"option\"],[role=\"gridcell\"],[role=\"spinbutton\"],[role=\"slider\"],[onclick],[tabindex],[contenteditable],[data-tooltip]';" +
                " var dialogs=Array.from(document.querySelectorAll('[role=\"dialog\"],[aria-modal=\"true\"]')).filter(vis);" +
                " var roots=dialogs.length>0?dialogs.reverse().concat([document]):[document];" +
                " var all=[];var seen=new Set();" +
                " for(var ri=0;ri<roots.length;ri++){var els=roots[ri].querySelectorAll(sel);for(var ei=0;ei<els.length;ei++){var el=els[ei];if(!vis(el)||seen.has(el))continue;seen.add(el);all.push(el);}}" +
                " if(idx<1||idx>all.length)return 'Error: element '+idx+' not found (page has '+all.length+' elements)';" +
                " var t=all[idx-1];if(t.focus)t.focus();t.click();" +
                " var txt=(t.textContent||t.getAttribute('aria-label')||'').replace(/\\s+/g,' ').trim().substring(0,60);" +
                " var tip=(t.getAttribute('data-tooltip')||t.getAttribute('title')||'').replace(/\\s+/g,' ').trim().substring(0,80);" +
                " var link=((t.href)||((t.closest&&t.closest('a[href]'))?t.closest('a[href]').href:'' )||'').substring(0,200);" +
                " var out='Clicked ['+idx+'] '+t.tagName.toLowerCase()+' \"'+txt+'\"';" +
                " if(tip && tip!==txt) out+=' tooltip=\"'+tip+'\"';" +
                " if(link) out+=' -> '+link;" +
                " return out;" +
                "})()";

            var result = await InvokeOnUiThreadAsync(() => _webView!.ExecuteScriptAsync(script));
            await Task.Delay(200);
            return CleanJsResult(result);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    private async Task<string> TypeByNumberAsync(int index, string text)
    {
        await EnsureInitializedAsync();
        await _actionLock.WaitAsync();
        try
        {
            var escapedText = EscapeJs(text);
            var script =
                "(function(){" +
                " var idx=" + Math.Max(1, index) + ";" +
                " var vis=function(el){if(!el)return false;var r=el.getBoundingClientRect();if(r.width<=0||r.height<=0)return false;var cs=getComputedStyle(el);return cs.display!=='none'&&cs.visibility!=='hidden'&&cs.opacity!=='0';};" +
                " var sel='a[href],button,input,select,textarea,[role=\"button\"],[role=\"link\"],[role=\"tab\"],[role=\"menuitem\"],[role=\"radio\"],[role=\"checkbox\"],[role=\"switch\"],[role=\"combobox\"],[role=\"option\"],[role=\"gridcell\"],[role=\"spinbutton\"],[role=\"slider\"],[onclick],[tabindex],[contenteditable],[data-tooltip]';" +
                " var dialogs=Array.from(document.querySelectorAll('[role=\"dialog\"],[aria-modal=\"true\"]')).filter(vis);" +
                " var roots=dialogs.length>0?dialogs.reverse().concat([document]):[document];" +
                " var all=[];var seen=new Set();" +
                " for(var ri=0;ri<roots.length;ri++){var els=roots[ri].querySelectorAll(sel);for(var ei=0;ei<els.length;ei++){var el=els[ei];if(!vis(el)||seen.has(el))continue;seen.add(el);all.push(el);}}" +
                " if(idx<1||idx>all.length)return 'Error: element '+idx+' not found (page has '+all.length+' elements)';" +
                " var t=all[idx-1];" +
                FrameworkAwareSetterJs("t", escapedText) +
                " return 'Typed into ['+idx+'] '+t.tagName.toLowerCase();" +
                "})()";

            var result = await InvokeOnUiThreadAsync(() => _webView!.ExecuteScriptAsync(script));
            return CleanJsResult(result);
        }
        finally
        {
            _actionLock.Release();
        }
    }


    /// <summary>Clear a field's value using the framework-aware approach.</summary>
    private async Task<string> ClearFieldAsync(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return "Error: clear needs a target — element number or CSS selector";
        target = target.Trim();

        await EnsureInitializedAsync();
        await _actionLock.WaitAsync();
        try
        {
            string script;
            if (int.TryParse(target.TrimStart('#'), out var idx))
            {
                script =
                    "(function(){" +
                    " var idx=" + Math.Max(1, idx) + ";" +
                    " var vis=function(el){if(!el)return false;var r=el.getBoundingClientRect();if(r.width<=0||r.height<=0)return false;var cs=getComputedStyle(el);return cs.display!=='none'&&cs.visibility!=='hidden'&&cs.opacity!=='0';};" +
                    " var sel='a[href],button,input,select,textarea,[role=\"button\"],[role=\"link\"],[role=\"tab\"],[role=\"menuitem\"],[role=\"radio\"],[role=\"checkbox\"],[role=\"switch\"],[role=\"combobox\"],[role=\"option\"],[role=\"gridcell\"],[role=\"spinbutton\"],[role=\"slider\"],[onclick],[tabindex],[contenteditable],[data-tooltip]';" +
                    " var dialogs=Array.from(document.querySelectorAll('[role=\"dialog\"],[aria-modal=\"true\"]')).filter(vis);" +
                    " var roots=dialogs.length>0?dialogs.reverse().concat([document]):[document];" +
                    " var all=[];var seen=new Set();" +
                    " for(var ri=0;ri<roots.length;ri++){var els=roots[ri].querySelectorAll(sel);for(var ei=0;ei<els.length;ei++){var el=els[ei];if(!vis(el)||seen.has(el))continue;seen.add(el);all.push(el);}}" +
                    " if(idx<1||idx>all.length)return 'Error: element '+idx+' not found (page has '+all.length+' elements)';" +
                    " var t=all[idx-1];" +
                    FrameworkAwareClearJs("t") +
                    " return 'Cleared ['+idx+'] '+t.tagName.toLowerCase();" +
                    "})()";
            }
            else
            {
                var escapedSel = EscapeJs(target);
                script =
                    "(function() {" +
                    "  let el = document.querySelector('" + escapedSel + "');" +
                    "  if (!el) { el = document.querySelector('input[placeholder*=\"" + escapedSel + "\"], textarea[placeholder*=\"" + escapedSel + "\"]'); }" +
                    "  if (!el) return 'error: no input found for: " + escapedSel + "';" +
                    FrameworkAwareClearJs("el") +
                    "  return 'cleared: ' + (el.tagName || '') + ' [' + (el.name || el.id || '') + ']';" +
                    "})()";
            }

            var result = await InvokeOnUiThreadAsync(() => _webView!.ExecuteScriptAsync(script));
            return CleanJsResult(result);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    /// <summary>
    /// Fill multiple form fields at once. The value parameter is a JSON object mapping field
    /// identifiers (element number, name, id, placeholder, or label text) to values.
    /// Handles inputs, textareas, checkboxes (true/false), and native selects.
    /// </summary>
    private async Task<string> FillFormAsync(string? fieldsJson)
    {
        if (string.IsNullOrWhiteSpace(fieldsJson))
            return "Error: fill needs a value parameter — JSON object mapping field identifiers to values, e.g. {\"3\": \"John\", \"4\": \"john@email.com\", \"5\": true}";

        await EnsureInitializedAsync();
        await _actionLock.WaitAsync();
        try
        {
            var escapedJson = EscapeJs(fieldsJson);
            // The JS fills all fields in one execution, avoiding intermediate snapshots.
            // It uses the native setter trick for each field to work with React/Vue/Angular.
            var script =
                "(function(){" +
                " try { var fields = JSON.parse('" + escapedJson + "'); } catch(e) { return 'Error: invalid JSON — ' + e.message; }" +
                " var vis=function(el){if(!el)return false;var r=el.getBoundingClientRect();if(r.width<=0||r.height<=0)return false;var cs=getComputedStyle(el);return cs.display!=='none'&&cs.visibility!=='hidden'&&cs.opacity!=='0';};" +
                " var sel='a[href],button,input,select,textarea,[role=\"button\"],[role=\"link\"],[role=\"tab\"],[role=\"menuitem\"],[role=\"radio\"],[role=\"checkbox\"],[role=\"switch\"],[role=\"combobox\"],[role=\"option\"],[role=\"gridcell\"],[role=\"spinbutton\"],[role=\"slider\"],[onclick],[tabindex],[contenteditable],[data-tooltip]';" +
                " var dialogs=Array.from(document.querySelectorAll('[role=\"dialog\"],[aria-modal=\"true\"]')).filter(vis);" +
                " var roots=dialogs.length>0?dialogs.reverse().concat([document]):[document];" +
                " var all=[];var seen=new Set();" +
                " for(var ri=0;ri<roots.length;ri++){var els=roots[ri].querySelectorAll(sel);for(var ei=0;ei<els.length;ei++){var el=els[ei];if(!vis(el)||seen.has(el))continue;seen.add(el);all.push(el);}}" +

                // Helper: find element by key (number, name, id, placeholder, label)
                " var find=function(key){" +
                "   var n=parseInt(key);" +
                "   if(!isNaN(n)&&n>=1&&n<=all.length)return all[n-1];" +
                "   var q=key.toLowerCase();" +
                "   for(var i=0;i<all.length;i++){var el=all[i];" +
                "     if((el.name||'').toLowerCase()===q||(el.id||'').toLowerCase()===q)return el;" +
                "     if((el.placeholder||'').toLowerCase().indexOf(q)>=0)return el;" +
                "     var ariaLabel=(el.getAttribute('aria-label')||'').toLowerCase();" +
                "     if(ariaLabel&&ariaLabel.indexOf(q)>=0)return el;" +
                "   }" +
                "   var labels=document.querySelectorAll('label');" +
                "   for(var li=0;li<labels.length;li++){" +
                "     if(labels[li].textContent.toLowerCase().indexOf(q)>=0&&labels[li].htmlFor){" +
                "       var el=document.getElementById(labels[li].htmlFor);if(el)return el;" +
                "     }" +
                "   }" +
                "   var el=document.querySelector(key);if(el)return el;" +
                "   return null;" +
                " };" +

                // Helper: set value using native setter trick
                " var setVal=function(el,val){" +
                "   var tag=el.tagName;var type=(el.type||'').toLowerCase();" +
                "   if(type==='checkbox'||type==='radio'){" +
                "     var want=(val===true||val==='true'||val==='on');" +
                "     if(el.checked!==want){el.click();}" +
                "     return 'toggled';" +
                "   }" +
                "   if(tag==='SELECT'){" +
                "     var opts=Array.from(el.options);" +
                "     var opt=opts.find(function(o){return o.value===String(val)||o.text.toLowerCase().indexOf(String(val).toLowerCase())>=0;});" +
                "     if(!opt)return 'option not found';" +
                "     el.value=opt.value;" +
                "     el.dispatchEvent(new Event('change',{bubbles:true}));" +
                "     return 'selected: '+opt.text;" +
                "   }" +
                "   el.focus();" +
                "   var proto=tag==='TEXTAREA'?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;" +
                "   var ns=Object.getOwnPropertyDescriptor(proto,'value');" +
                "   if(ns&&ns.set){ns.set.call(el,String(val));}else{el.value=String(val);}" +
                "   if(el._valueTracker){el._valueTracker.setValue('');}" +
                "   el.dispatchEvent(new InputEvent('input',{bubbles:true,data:String(val),inputType:'insertText'}));" +
                "   el.dispatchEvent(new Event('change',{bubbles:true}));" +
                "   el.dispatchEvent(new Event('blur',{bubbles:true}));" +
                "   return 'filled';" +
                " };" +

                // Process all fields
                " var results=[];" +
                " var keys=Object.keys(fields);" +
                " for(var ki=0;ki<keys.length;ki++){" +
                "   var key=keys[ki];var val=fields[key];" +
                "   var el=find(key);" +
                "   if(!el){results.push(key+': NOT FOUND');continue;}" +
                "   var r=setVal(el,val);" +
                "   var label=el.name||el.id||el.placeholder||key;" +
                "   results.push(label+': '+r);" +
                " }" +

                // Read validation state after filling
                " var errors=[];" +
                " var inputs=document.querySelectorAll('input,select,textarea');" +
                " for(var ii=0;ii<inputs.length;ii++){" +
                "   var inp=inputs[ii];" +
                "   if(inp.validationMessage&&vis(inp)){" +
                "     errors.push((inp.name||inp.id||inp.placeholder||'field')+': '+inp.validationMessage);" +
                "   }" +
                "   if(inp.getAttribute('aria-invalid')==='true'&&vis(inp)){" +
                "     var errId=inp.getAttribute('aria-describedby');" +
                "     var errEl=errId?document.getElementById(errId):null;" +
                "     var errMsg=errEl?(errEl.textContent||'').trim():'';" +
                "     if(errMsg)errors.push((inp.name||inp.id||inp.placeholder||'field')+': '+errMsg);" +
                "   }" +
                " }" +
                " var out='Fill results:\\n'+results.join('\\n');" +
                " if(errors.length>0)out+='\\n\\nValidation errors:\\n'+errors.join('\\n');" +
                " return out;" +
                "})()";

            var result = await InvokeOnUiThreadAsync(() => _webView!.ExecuteScriptAsync(script));
            return CleanJsResult(result);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    /// <summary>
    /// Read all form fields on the page — names, values, types, required/validation state.
    /// Returns a structured summary that eliminates the need for manual browser_js inspection.
    /// </summary>
    private async Task<string> ReadFormAsync()
    {
        await EnsureInitializedAsync();
        await _actionLock.WaitAsync();
        try
        {
            var script =
                "(function(){" +
                " var vis=function(el){if(!el)return false;var r=el.getBoundingClientRect();if(r.width<=0||r.height<=0)return false;var cs=getComputedStyle(el);return cs.display!=='none'&&cs.visibility!=='hidden'&&cs.opacity!=='0';};" +

                // Find all visible form fields
                " var inputs=document.querySelectorAll('input,select,textarea');" +
                " var fields=[];" +
                " var elSel='a[href],button,input,select,textarea,[role=\"button\"],[role=\"link\"],[role=\"tab\"],[role=\"menuitem\"],[role=\"radio\"],[role=\"checkbox\"],[role=\"switch\"],[role=\"combobox\"],[role=\"option\"],[role=\"gridcell\"],[role=\"spinbutton\"],[role=\"slider\"],[onclick],[tabindex],[contenteditable],[data-tooltip]';" +
                " var dialogs=Array.from(document.querySelectorAll('[role=\"dialog\"],[aria-modal=\"true\"]')).filter(vis);" +
                " var roots=dialogs.length>0?dialogs.reverse().concat([document]):[document];" +
                " var all=[];var seen=new Set();" +
                " for(var ri=0;ri<roots.length;ri++){var els=roots[ri].querySelectorAll(elSel);for(var ei=0;ei<els.length;ei++){var el=els[ei];if(!vis(el)||seen.has(el))continue;seen.add(el);all.push(el);}}" +

                // Map elements to their indices
                " var idxMap=new Map();" +
                " for(var ai=0;ai<all.length;ai++){idxMap.set(all[ai],ai+1);}" +

                " for(var i=0;i<inputs.length;i++){" +
                "   var el=inputs[i];" +
                "   if(!vis(el))continue;" +
                "   var tag=el.tagName;var type=(el.type||'').toLowerCase();" +
                "   var name=el.name||el.id||'';" +
                "   var ph=el.placeholder||'';" +
                "   var label='';" +
                "   if(el.id){var lbl=document.querySelector('label[for=\"'+el.id+'\"]');if(lbl)label=(lbl.textContent||'').replace(/\\s+/g,' ').trim();}" +
                "   if(!label){var closest=el.closest('label');if(closest)label=(closest.textContent||'').replace(/\\s+/g,' ').trim();}" +
                "   var ariaLabel=el.getAttribute('aria-label')||'';" +

                // Get value based on type
                "   var val='';" +
                "   if(type==='checkbox'||type==='radio'){val=el.checked?'checked':'unchecked';}" +
                "   else if(tag==='SELECT'){var opt=el.options[el.selectedIndex];val=opt?(opt.text||opt.value):'';}" +
                "   else{val=el.value||'';}" +

                // Required and validation
                "   var req=el.required||el.getAttribute('aria-required')==='true';" +
                "   var invalid=el.getAttribute('aria-invalid')==='true'||(!el.checkValidity());" +
                "   var errMsg=el.validationMessage||'';" +
                "   if(!errMsg&&el.getAttribute('aria-describedby')){" +
                "     var errEl=document.getElementById(el.getAttribute('aria-describedby'));" +
                "     if(errEl)errMsg=(errEl.textContent||'').trim();" +
                "   }" +

                "   var idx=idxMap.get(el)||'?';" +
                "   var identifier=name||ph||ariaLabel||label||('field_'+i);" +
                "   var line='['+idx+'] '+(type||tag.toLowerCase());" +
                "   if(identifier)line+=' name=\"'+identifier.substring(0,40)+'\"';" +
                "   if(label&&label!==identifier)line+=' label=\"'+label.substring(0,40)+'\"';" +
                "   if(ph&&ph!==identifier)line+=' placeholder=\"'+ph.substring(0,40)+'\"';" +
                "   line+=' value=\"'+(val||'').substring(0,60)+'\"';" +
                "   if(req)line+=' [required]';" +
                "   if(invalid&&(val||req))line+=' [invalid]';" +
                "   if(errMsg)line+=' error=\"'+errMsg.substring(0,60)+'\"';" +
                "   fields.push(line);" +
                " }" +

                // Also check for custom error messages in the DOM
                " var errEls=document.querySelectorAll('[role=\"alert\"],[class*=\"error\"],[class*=\"Error\"]');" +
                " var pageErrors=[];" +
                " for(var ei=0;ei<errEls.length;ei++){" +
                "   var txt=(errEls[ei].textContent||'').replace(/\\s+/g,' ').trim();" +
                "   if(txt&&vis(errEls[ei])&&txt.length<200)pageErrors.push(txt);" +
                " }" +

                " var out='Form fields ('+fields.length+'):\\n'+fields.join('\\n');" +
                " if(pageErrors.length>0)out+='\\n\\nPage errors:\\n'+pageErrors.join('\\n');" +
                " return out;" +
                "})()";

            var result = await InvokeOnUiThreadAsync(() => _webView!.ExecuteScriptAsync(script));
            return CleanJsResult(result);
        }
        finally
        {
            _actionLock.Release();
        }
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
    {
        try
        {
            var jsLimit = Math.Clamp(limit, 1, 20);
            var script =
                "(function(){" +
                " const limit=" + jsLimit + ";" +
                " const norm=(s)=> (s||'').replace(/\\s+/g,' ').trim();" +
                " const vis=(el)=>{ if(!el) return false; const r=el.getBoundingClientRect(); if(r.width<=0||r.height<=0) return false; const cs=getComputedStyle(el); return cs.display!=='none'&&cs.visibility!=='hidden'&&cs.opacity!=='0'; };" +
                " const sel='" + InteractiveElementSelectorJs + "';" +
                " const dialogs=[...document.querySelectorAll('[role=\\\"dialog\\\"],[aria-modal=\\\"true\\\"]')].filter(vis);" +
                " const roots=dialogs.length>0 ? [...dialogs.reverse(), document] : [document];" +
                " const all=[]; const seen=new Set();" +
                " for(const root of roots){ for(const el of root.querySelectorAll(sel)){ if(!vis(el)) continue; if(seen.has(el)) continue; seen.add(el); all.push(el);} }" +
                " const rank=[];" +
                " for(let i=0;i<all.length;i++){ const el=all[i];" +
                "   const text=norm(el.textContent); const aria=norm(el.getAttribute('aria-label')); const tip=norm(el.getAttribute('data-tooltip')||el.getAttribute('title')); const href=(el.href || (el.closest&&el.closest('a[href]')?el.closest('a[href]').href:'') || '').substring(0,200); const role=norm(el.getAttribute('role'));" +
                "   const hay=(text+' '+aria+' '+tip+' '+href+' '+role).toLowerCase();" +
                "   let score=0;" +
                "   if(/download|save|export|attachment|file|xlsx|csv|\\u05d4\\u05d5\\u05e8\\u05d3|\\u05e7\\u05d5\\u05d1\\u05e5/.test(hay)) score+=10;" +
                "   if(/attid=|view=att|disp=safe|realattid=|download|export/.test(href.toLowerCase())) score+=25;" +
                "   if((el.tagName||'').toLowerCase()==='a' && href) score+=3;" +
                "   if(score>0) rank.push({ index:i+1, score, tag:(el.tagName||'').toLowerCase(), text:text.substring(0,80), aria:aria.substring(0,80), tooltip:tip.substring(0,80), href });" +
                " }" +
                " rank.sort((a,b)=> b.score-a.score || a.index-b.index);" +
                " const top=rank.slice(0, limit);" +
                " if(top.length===0) return '';" +
                " const lines=['Download-related elements on page:'];" +
                " for(const it of top){ let line='['+it.index+'] '+it.tag; if(it.text) line+=' text=\\\"'+it.text+'\\\"'; if(it.aria && it.aria!==it.text) line+=' aria=\\\"'+it.aria+'\\\"'; if(it.tooltip && it.tooltip!==it.text && it.tooltip!==it.aria) line+=' tooltip=\\\"'+it.tooltip+'\\\"'; if(it.href) line+=' -> '+it.href; lines.push(line); }" +
                " return lines.join('\\n');" +
                "})()";

            var result = await InvokeOnUiThreadAsync(() => _webView!.ExecuteScriptAsync(script));
            return CleanJsResult(result);
        }
        catch
        {
            return string.Empty;
        }
    }

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
        if (hwnd != IntPtr.Zero)
            _pendingParentHwnd = hwnd;
    }

    /// <summary>Ensures the browser is initialized, using the stored HWND if needed.
    /// Marshals to the UI thread if called from a background thread.</summary>
    private async Task EnsureInitializedAsync()
    {
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
        await EnsureInitializedAsync();
        return await InvokeOnUiThreadAsync(() => BrowserCookieService.ImportCookiesAsync(profile, _webView!));
    }

    public async ValueTask DisposeAsync()
    {
        if (_webView is not null)
        {
            _webView.NavigationCompleted -= OnNavigationCompleted;
            _webView.SourceChanged -= OnSourceChanged;
            _webView.NewWindowRequested -= OnNewWindowRequested;
            _webView.DownloadStarting -= OnDownloadStarting;
        }
        if (_controller is not null)
        {
            _controller.Close();
            _controller = null;
        }
        _webView = null;
        _environment = null;
        _initialized = false;
        _initLock.Dispose();
        _actionLock.Dispose();
    }
}
