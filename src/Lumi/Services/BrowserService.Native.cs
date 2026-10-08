#if !WINDOWS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Lumi.Services;

/// <summary>
/// Linux/macOS browser coordinator. The Windows implementation has its own lifecycle and
/// automation; only the public contract and engine-independent DOM operations are shared.
/// </summary>
public sealed class BrowserService : IAsyncDisposable
{
    internal const int MaxScreenshotDimension = 2048;
    internal const int MaxScreenshotPngBytes = 3 * 1024 * 1024;

    private static readonly object HostSync = new();
    private static readonly List<(TopLevel Window, Canvas Layer)> Hosts = [];
    private static readonly List<WeakReference<BrowserService>> Browsers = [];

    private readonly object _tabsSync = new();
    private readonly List<NativeBrowserTab> _tabs = [];
    private NativeBrowserTab? _activeTab;
    private Canvas? _hostLayer;
    private Rect? _physicalBounds;
    private double _rasterizationScale = 1;
    private bool _panelVisible;
    private bool _isDark = true;
    private volatile bool _isDisposed;
    private Task? _disposeTask;

    public BrowserService() : this(null) { }

    internal BrowserService(string? userDataFolder)
    {
        UserDataFolder = Path.GetFullPath(userDataFolder ??
            Path.Combine(DataStore.AppDirectory, "browser-data"));
        _activeTab = new NativeBrowserTab(this);
        _tabs.Add(_activeTab);
        lock (HostSync)
        {
            Browsers.RemoveAll(reference => !reference.TryGetTarget(out _));
            Browsers.Add(new WeakReference<BrowserService>(this));
        }
    }

    internal string UserDataFolder { get; }
    internal bool IsDisposed => _isDisposed;
    internal bool IsDark => _isDark;

    public string TabId { get; } = "tab-" + Guid.NewGuid().ToString("N");
    public string ActiveTabId => CaptureActiveTab().Id;
    public string CurrentUrl => _activeTab?.Url ?? "about:blank";
    public string CurrentTitle => _activeTab?.Title ?? "";
    public bool IsInitialized => _activeTab?.IsInitialized == true;
    public bool HasController => _activeTab?.HasController == true;

    public event Action? BrowserReady;
    public event Action? UrlChanged;
    public event Action? TabsChanged;
    public event Action<string>? BrowserError;

    public IReadOnlyList<BrowserTabInfo> Tabs
    {
        get
        {
            lock (_tabsSync)
                return _tabs.Select(tab => new BrowserTabInfo(
                    tab.Id, tab.Title, tab.Url, ReferenceEquals(tab, _activeTab))).ToArray();
        }
    }

    internal static void RegisterHostLayer(TopLevel window, Canvas layer)
    {
        Dispatcher.UIThread.VerifyAccess();
        lock (HostSync)
        {
            Hosts.RemoveAll(host => ReferenceEquals(host.Window, window));
            Hosts.Add((window, layer));
        }
    }

    internal static void UnregisterHostLayer(TopLevel window)
    {
        Dispatcher.UIThread.VerifyAccess();
        Canvas[] removed;
        BrowserService[] browsers;
        lock (HostSync)
        {
            removed = Hosts.Where(host => ReferenceEquals(host.Window, window))
                .Select(host => host.Layer).ToArray();
            Hosts.RemoveAll(host => ReferenceEquals(host.Window, window));
            Browsers.RemoveAll(reference => !reference.TryGetTarget(out _));
            browsers = Browsers.Select(reference =>
                reference.TryGetTarget(out var browser) ? browser : null).OfType<BrowserService>().ToArray();
        }

        foreach (var browser in browsers)
        {
            if (browser._isDisposed || !removed.Any(layer => ReferenceEquals(layer, browser._hostLayer)))
                continue;
            browser._hostLayer = ResolveHostLayer(null);
            foreach (var tab in browser.GetTabs())
                tab.MoveToHost(browser._hostLayer);
            browser.ApplyPresentation();
        }
    }

    internal static Canvas? ResolveHostLayer(Visual? visual)
    {
        var window = visual is null ? null : TopLevel.GetTopLevel(visual);
        lock (HostSync)
        {
            if (window is not null)
            {
                foreach (var host in Hosts)
                    if (ReferenceEquals(host.Window, window))
                        return host.Layer;
            }
            return Hosts.Count > 0 ? Hosts[0].Layer : null;
        }
    }

    internal void UseHostLayerFor(Visual visual)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_isDisposed)
            return;
        var layer = ResolveHostLayer(visual);
        if (layer is null || ReferenceEquals(layer, _hostLayer))
            return;
        _hostLayer = layer;
        foreach (var tab in GetTabs())
            tab.MoveToHost(layer);
        ApplyPresentation();
    }

    internal Canvas GetHostLayer()
    {
        Dispatcher.UIThread.VerifyAccess();
        return _hostLayer ??= ResolveHostLayer(null) ??
            throw new InvalidOperationException(
                "The browser panel must be attached to a Lumi window before opening a page.");
    }

    private NativeBrowserTab[] GetTabs()
    {
        lock (_tabsSync)
            return _tabs.ToArray();
    }

    private NativeBrowserTab CaptureActiveTab()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        lock (_tabsSync)
            return _activeTab ?? throw new InvalidOperationException("No browser tab is available.");
    }

    private NativeBrowserTab ResolveTab(string? tabId)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        lock (_tabsSync)
            return string.IsNullOrWhiteSpace(tabId)
                ? _activeTab ?? throw new InvalidOperationException("No browser tab is available.")
                : _tabs.FirstOrDefault(tab => tab.Id == tabId) ??
                    throw new InvalidOperationException($"Browser tab '{tabId}' is closed or does not exist.");
    }

    internal void NotifyChanged()
    {
        if (_isDisposed)
            return;
        TabsChanged?.Invoke();
        UrlChanged?.Invoke();
    }

    internal void NotifyReady(NativeBrowserTab tab)
    {
        if (_isDisposed)
            return;
        ApplyPresentation();
        if (ReferenceEquals(tab, _activeTab))
            BrowserReady?.Invoke();
        NotifyChanged();
    }

    internal void ReportError(string message)
    {
        if (!_isDisposed)
            BrowserError?.Invoke(message);
    }

    private void ApplyPresentation()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_isDisposed)
            return;
        foreach (var tab in GetTabs())
            tab.UpdatePresentation(_hostLayer, _physicalBounds, _rasterizationScale,
                _panelVisible && ReferenceEquals(tab, _activeTab));
    }

    private void UpdatePresentation(Action update)
    {
        if (_isDisposed)
            return;
        if (Dispatcher.UIThread.CheckAccess())
        {
            update();
            ApplyPresentation();
        }
        else
        {
            Dispatcher.UIThread.Post(() => UpdatePresentation(update));
        }
    }

    public void SetParentHwnd(IntPtr hwnd) => _ = hwnd;

    public void SetControllerVisible(bool visible) =>
        UpdatePresentation(() => _panelVisible = visible);

    public void SyncRasterizationScale(double scale) =>
        UpdatePresentation(() => _rasterizationScale = scale);

    public void SetBounds(int x, int y, int width, int height, int cornerRadiusPx = 0) =>
        UpdatePresentation(() => _physicalBounds = width > 0 && height > 0
            ? new Rect(x, y, width, height) : null);

    public void SetTheme(bool isDark)
    {
        UpdatePresentation(() =>
        {
            _isDark = isDark;
            foreach (var tab in GetTabs())
                tab.ApplyTheme();
        });
    }

    public void Reload()
    {
        if (!_isDisposed)
            CaptureActiveTab().Reload();
    }

    public Task InitializeAsync(IntPtr parentHwnd) => CaptureActiveTab().EnsureInitializedAsync();
    public Task<string> NavigateAsync(string url) => CaptureActiveTab().NavigateAsync(url);
    public Task<string> OpenAndSnapshotAsync(string url) => CaptureActiveTab().OpenAndSnapshotAsync(url);
    public Task<string> LookAsync(string? filter = null) => CaptureActiveTab().LookAsync(filter);
    public Task<string> FindElementsAsync(string query, int limit = 12, bool preferDialog = true) =>
        CaptureActiveTab().FindElementsAsync(query, limit, preferDialog);
    public Task<string> DoAsync(string action, string? target = null, string? value = null) =>
        CaptureActiveTab().DoAsync(action, target, value);
    public Task<string> EvaluateAsync(string javascript) => CaptureActiveTab().EvaluateAsync(javascript);
    public Task<string> PressKeyAsync(string key, string? selector = null) =>
        CaptureActiveTab().PressKeyAsync(key, selector);
    public Task<string> WaitForAsync(string selector, int timeoutMs = 10000) =>
        CaptureActiveTab().WaitForAsync(selector, timeoutMs);
    public Task<string> GoBackAsync() => CaptureActiveTab().GoBackAsync();
    public Task<string> ScrollAsync(string direction, int pixels = 500) =>
        CaptureActiveTab().ScrollAsync(direction, pixels);
    public Task<string> UploadFileAsync(string? target, string? filesValue) =>
        CaptureActiveTab().UploadFileAsync(target, filesValue);

    internal async Task<NativeBrowserTab> CreateTabAsync(bool activate)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        var tab = new NativeBrowserTab(this);
        NativeBrowserTab? previous;
        lock (_tabsSync)
        {
            previous = _activeTab;
            _tabs.Add(tab);
            if (activate)
                _activeTab = tab;
        }
        ApplyPresentation();
        NotifyChanged();
        try
        {
            await tab.EnsureInitializedAsync();
            return tab;
        }
        catch
        {
            lock (_tabsSync)
            {
                _tabs.Remove(tab);
                if (ReferenceEquals(_activeTab, tab))
                    _activeTab = _tabs.Contains(previous!) ? previous : _tabs.LastOrDefault();
            }
            await tab.DisposeAsync();
            ApplyPresentation();
            NotifyChanged();
            throw;
        }
    }

    internal async Task CloseTabAsync(NativeBrowserTab tab)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        lock (_tabsSync)
        {
            if (!_tabs.Remove(tab))
                throw new InvalidOperationException($"Browser tab '{tab.Id}' is already closed.");
            if (_tabs.Count == 0)
                _tabs.Add(new NativeBrowserTab(this));
            if (ReferenceEquals(_activeTab, tab))
                _activeTab = _tabs[^1];
        }
        tab.Hide();
        ApplyPresentation();
        NotifyChanged();
        await tab.DisposeAsync();
    }

    public Task<string> ManageTabsAsync(string action, string? tabId = null, string? url = null)
    {
        var requestedAction = action.Trim().ToLowerInvariant();
        var requested = requestedAction is "close" or "switch" ? ResolveTab(tabId) : null;
        return OnUiThreadAsync(async () =>
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            switch (requestedAction)
            {
                case "list":
                    break;
                case "new":
                    var created = await CreateTabAsync(activate: true);
                    if (!string.IsNullOrWhiteSpace(url))
                        return FormatTabs() + "\n\n" + await created.OpenAndSnapshotAsync(url);
                    break;
                case "switch":
                    lock (_tabsSync)
                    {
                        if (!_tabs.Contains(requested!))
                            throw new InvalidOperationException("The requested browser tab was closed.");
                        _activeTab = requested;
                    }
                    ApplyPresentation();
                    NotifyChanged();
                    await requested!.EnsureInitializedAsync();
                    break;
                case "close":
                    await CloseTabAsync(requested!);
                    await CaptureActiveTab().EnsureInitializedAsync();
                    break;
                default:
                    return "Error: Unsupported tab action. Use list, new, switch, or close.";
            }
            return FormatTabs();
        });
    }

    private string FormatTabs() => string.Join("\n", Tabs.Select(tab =>
        $"{(tab.IsActive ? "*" : " ")} {tab.Id} | {(string.IsNullOrWhiteSpace(tab.Title) ? "New tab" : tab.Title)} | {tab.Url}"));

    public Task<BrowserScreenshot> CaptureScreenshotAsync(string? tabId = null) =>
        ResolveTab(tabId).CaptureScreenshotAsync();

    internal static BrowserScreenshot PrepareScreenshotForModel(BrowserScreenshot screenshot) =>
        NativeWebViewPlatform.PrepareScreenshotForModel(screenshot);

    public async Task ClearCookiesAsync()
    {
        var tab = CaptureActiveTab();
        await tab.EnsureInitializedAsync();
        foreach (var initialized in GetTabs().Where(candidate => candidate.IsInitialized))
            await initialized.ClearCookiesAsync();
    }

    public async Task<int> ImportCookiesAsync(BrowserCookieService.BrowserProfile profile)
    {
        var tab = CaptureActiveTab();
        await tab.EnsureInitializedAsync();
        var cookies = await BrowserCookieService.ReadCookiesAsync(profile);
        foreach (var initialized in GetTabs().Where(candidate => candidate.IsInitialized))
            await initialized.SetCookiesAsync(cookies);
        return cookies.Count;
    }

    internal static bool IsWebViewInvalidState(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x8007139F) })
                return true;
        }
        return false;
    }

    internal static Task<T> OnUiThreadAsync<T>(Func<Task<T>> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return action();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { completion.TrySetResult(await action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
    }

    internal static Task OnUiThreadAsync(Func<Task> action) =>
        OnUiThreadAsync(async () => { await action(); return true; });

    internal static Task OnUiThreadAsync(Action action) =>
        OnUiThreadAsync(() => { action(); return Task.CompletedTask; });

    public ValueTask DisposeAsync()
    {
        lock (_tabsSync)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);
            _isDisposed = true;
            var tabs = _tabs.ToArray();
            _tabs.Clear();
            _activeTab = null;
            lock (HostSync)
                Browsers.RemoveAll(reference =>
                    !reference.TryGetTarget(out var browser) || ReferenceEquals(browser, this));
            _disposeTask = DisposeTabsAsync(tabs);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeTabsAsync(NativeBrowserTab[] tabs)
    {
        foreach (var tab in tabs)
            await tab.DisposeAsync();
        _hostLayer = null;
    }
}
#endif
