using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
#if WINDOWS
using Microsoft.Web.WebView2.Core;
#endif

namespace Lumi.Services;

public sealed record BrowserTabInfo(string Id, string Title, string Url, bool IsActive);

public sealed record BrowserScreenshot(string TabId, string Url, int Width, int Height, byte[] PngBytes);

#if WINDOWS
public sealed partial class BrowserService
{
    // The per-chat service coordinates tabs. Each leaf reuses the existing controller and automation code.
    private readonly BrowserService? _tabOwner;
    private readonly List<BrowserService> _tabs = [];
    private readonly object _tabsSync = new();
    private BrowserService? _activeTab;
    private readonly string? _userDataFolderOverride;
    private bool _panelVisible;
    private System.Drawing.Rectangle _tabBounds;
    private int _tabCornerRadius;
    private double _tabScale = 1;
    private string _tabUrl = "about:blank";
    private string _tabTitle = "";
    private bool _isNavigating;
    private long _documentVersion;
    private ulong _navigationId;

    public BrowserService() : this((string?)null) { }

    internal BrowserService(string? userDataFolder)
    {
        _userDataFolderOverride = userDataFolder;
        _activeTab = new BrowserService(this);
        _tabs.Add(_activeTab);
    }

    private BrowserService(BrowserService owner)
    {
        _tabOwner = owner;
    }

    public string TabId { get; } = "tab-" + Guid.NewGuid().ToString("N");
    public string ActiveTabId => CaptureActiveTab().TabId;
    public event Action? TabsChanged;

    public IReadOnlyList<BrowserTabInfo> Tabs
    {
        get
        {
            var owner = _tabOwner ?? this;
            lock (owner._tabsSync)
                return owner._tabs.Select(t => new BrowserTabInfo(
                    t.TabId, t._tabTitle, t._tabUrl, ReferenceEquals(t, owner._activeTab))).ToArray();
        }
    }

    private BrowserService CaptureActiveTab()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_tabOwner is not null)
            return this;
        lock (_tabsSync)
            return _activeTab ?? throw new InvalidOperationException("No browser tab is available.");
    }

    private BrowserService ResolveTab(string? tabId)
    {
        var owner = _tabOwner ?? this;
        ObjectDisposedException.ThrowIf(owner._isDisposed, owner);
        lock (owner._tabsSync)
            return string.IsNullOrWhiteSpace(tabId)
                ? owner._activeTab ?? throw new InvalidOperationException("No browser tab is available.")
                : owner._tabs.FirstOrDefault(t => t.TabId == tabId)
                    ?? throw new InvalidOperationException($"Browser tab '{tabId}' is closed or does not exist.");
    }

    private void NotifyTabChanged()
    {
        var owner = _tabOwner ?? this;
        if (owner._isDisposed)
            return;
        owner.TabsChanged?.Invoke();
        owner.UrlChanged?.Invoke();
    }

    private void ApplyTabPresentation()
    {
        BrowserService[] tabs;
        lock (_tabsSync)
            tabs = _tabs.ToArray();
        foreach (var tab in tabs)
        {
            tab.SetParentHwnd(_pendingParentHwnd);
            tab.SyncRasterizationScale(_tabScale);
            if (_tabBounds.Width > 0 && _tabBounds.Height > 0)
                tab.SetBounds(_tabBounds.X, _tabBounds.Y, _tabBounds.Width, _tabBounds.Height, _tabCornerRadius);
            tab.SetControllerVisible(_panelVisible && ReferenceEquals(tab, _activeTab));
        }
    }

    private async Task<BrowserService> CreateTabAsync(bool activate, bool initialize)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        var tab = new BrowserService(this) { _isDark = _isDark };
        tab.SetParentHwnd(_pendingParentHwnd);
        lock (_tabsSync)
        {
            _tabs.Add(tab);
            if (activate)
                _activeTab = tab;
        }
        ApplyTabPresentation();
        NotifyTabChanged();
        if (initialize)
        {
            try
            {
                await tab.EnsureInitializedAsync();
            }
            catch
            {
                bool stillOpen;
                lock (_tabsSync)
                    stillOpen = _tabs.Contains(tab);
                if (stillOpen)
                    await CloseTabAsync(tab);
                throw;
            }
        }
        return tab;
    }

    private async Task CloseTabAsync(BrowserService tab)
    {
        lock (_tabsSync)
        {
            if (!_tabs.Remove(tab))
                throw new InvalidOperationException($"Browser tab '{tab.TabId}' is already closed.");
            if (_tabs.Count == 0 && !_isDisposed)
            {
                var replacement = new BrowserService(this) { _isDark = _isDark };
                replacement.SetParentHwnd(_pendingParentHwnd);
                _tabs.Add(replacement);
            }
            if (ReferenceEquals(_activeTab, tab))
                _activeTab = _tabs.LastOrDefault();
        }
        tab.SetControllerVisible(false);
        ApplyTabPresentation();
        NotifyTabChanged();
        await tab.DisposeAsync();
    }

    public async Task<string> ManageTabsAsync(string action, string? tabId = null, string? url = null)
    {
        var owner = _tabOwner ?? this;
        var requestedTab = action.Trim().ToLowerInvariant() is "close" or "switch"
            ? owner.ResolveTab(tabId) : null;
        return await InvokeOnUiThreadAsync(async () =>
        {
            ObjectDisposedException.ThrowIf(owner._isDisposed, owner);
            switch (action.Trim().ToLowerInvariant())
            {
                case "list":
                    break;
                case "new":
                    var tab = await owner.CreateTabAsync(activate: true, initialize: true);
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        var result = await tab.OpenAndSnapshotAsync(url);
                        return owner.FormatTabs() + "\n\n" + result;
                    }
                    break;
                case "switch":
                    lock (owner._tabsSync)
                    {
                        if (!owner._tabs.Contains(requestedTab!))
                            throw new InvalidOperationException("The requested browser tab was closed.");
                        owner._activeTab = requestedTab;
                    }
                    owner.ApplyTabPresentation();
                    owner.NotifyTabChanged();
                    await requestedTab!.EnsureInitializedAsync();
                    break;
                case "close":
                    await owner.CloseTabAsync(requestedTab!);
                    await owner.CaptureActiveTab().EnsureInitializedAsync();
                    break;
                default:
                    return "Error: Unsupported tab action. Use list, new, switch, or close.";
            }
            return owner.FormatTabs();
        });
    }

    private string FormatTabs() => string.Join("\n", Tabs.Select(t =>
        $"{(t.IsActive ? "*" : " ")} {t.Id} | {(string.IsNullOrWhiteSpace(t.Title) ? "New tab" : t.Title)} | {t.Url}"));

    public async Task<BrowserScreenshot> CaptureScreenshotAsync(string? tabId = null)
    {
        var tab = ResolveTab(tabId);
        if (!tab._initialized)
            throw new InvalidOperationException($"Tab {tab.TabId} is not initialized. Open a page and show the browser before capturing.");
        if (!await tab._actionLock.WaitAsync(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException($"Tab {tab.TabId} is busy. Wait for its current browser action to finish before capturing.");
        try
        {
            return await InvokeOnUiThreadAsync(async () =>
            {
                ObjectDisposedException.ThrowIf(tab._isDisposed, tab);
                var controller = tab._controller
                    ?? throw new InvalidOperationException($"Tab {tab.TabId} has no initialized controller.");
                var webView = tab._webView
                    ?? throw new InvalidOperationException($"Tab {tab.TabId} has no initialized document.");
                if (!controller.IsVisible)
                    throw new InvalidOperationException($"Tab {tab.TabId} is hidden. Switch to this tab and show the browser panel before capturing.");
                if (controller.Bounds.Width < 1 || controller.Bounds.Height < 1)
                    throw new InvalidOperationException($"Tab {tab.TabId} has no usable viewport. Show and resize the browser panel.");
                if (tab._isNavigating)
                    throw new InvalidOperationException($"Tab {tab.TabId} is navigating. Wait for the page to load before capturing.");
                var version = tab._documentVersion;
                var url = webView.Source;
                var bytes = await CapturePngAsync(webView).WaitAsync(TimeSpan.FromSeconds(10));
                if (tab._isDisposed || tab._documentVersion != version || !controller.IsVisible)
                    throw new InvalidOperationException($"Tab {tab.TabId} closed, navigated, or became hidden during capture. Show and observe the tab before capturing again.");
                if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                    throw new InvalidOperationException("WebView2 did not return a valid PNG screenshot.");
                var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
                var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
                if (width < 1 || height < 1)
                    throw new InvalidOperationException("WebView2 returned an empty screenshot.");
                return new BrowserScreenshot(tab.TabId, url, width, height, bytes);
            });
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TimeoutException)
        {
            throw new InvalidOperationException(
                $"Could not capture tab {tab.TabId}: {ex.Message} Show the tab and wait for loading to finish before trying again.", ex);
        }
        finally
        {
            tab._actionLock.Release();
        }
    }

    private static async Task<byte[]> CapturePngAsync(CoreWebView2 webView)
    {
        using var stream = new MemoryStream();
        await webView.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        return stream.ToArray();
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _isNavigating = true;
        _navigationId = e.NavigationId;
        _documentVersion++;
    }

    private void OnDocumentTitleChanged(object? sender, object e)
    {
        _tabTitle = _webView?.DocumentTitle ?? "";
        NotifyTabChanged();
    }

    private async void OnWindowCloseRequested(object? sender, object e)
    {
        if (_isDisposed || _tabOwner is null || _tabOwner._isDisposed)
            return;
        try
        {
            await _tabOwner.CloseTabAsync(this);
            await _tabOwner.CaptureActiveTab().EnsureInitializedAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Debug.WriteLine($"Browser popup close failed: {ex}");
            _tabOwner.BrowserError?.Invoke($"Could not close popup: {ex.Message}");
        }
    }

    public event Action<string>? BrowserError;
}
#endif
