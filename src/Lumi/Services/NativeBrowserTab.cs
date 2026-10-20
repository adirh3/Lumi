#if !WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Lumi.Services;

internal sealed partial class NativeBrowserTab : IAsyncDisposable
{
    private const double ParkOffset = -100000;
    private readonly BrowserService _owner;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _actionLock = new(1, 1);
    private readonly BrowserOperationGate _operationGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private NativeWebView? _view;
    private TaskCompletionSource<bool>? _adapterReady;
    private TaskCompletionSource<bool>? _navigation;
    private Task? _disposeTask;
    private bool _doubleEncoded;
    private bool _visible;
    private bool _isNavigating;
    private double _pageZoom = double.NaN;
    private long _documentVersion;
    private string? _lastNewWindowUrl;
    private volatile bool _disposed;

    internal NativeBrowserTab(BrowserService owner) => _owner = owner;
    internal string Id { get; } = "tab-" + Guid.NewGuid().ToString("N");
    internal string Url { get; private set; } = "about:blank";
    internal string Title { get; private set; } = "";
    internal bool IsInitialized { get; private set; }
    internal bool HasController { get; private set; }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed || _owner.IsDisposed, this);

    internal Task EnsureInitializedAsync() => BrowserService.OnUiThreadAsync(async () =>
    {
        ThrowIfDisposed();
        if (IsInitialized && HasController)
            return;
        if (OperatingSystem.IsMacOS() && !OperatingSystem.IsMacOSVersionAtLeast(14))
            throw new PlatformNotSupportedException(
                "The embedded browser requires macOS 14 or later to keep Lumi browser profiles private. " +
                "Use your system browser on older macOS versions.");
        await _initLock.WaitAsync(_lifetime.Token);
        try
        {
            ThrowIfDisposed();
            if (IsInitialized && HasController)
                return;
            var layer = _owner.GetHostLayer();
            System.IO.Directory.CreateDirectory(_owner.UserDataFolder);
            if (_view is null)
            {
                _adapterReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var view = new NativeWebView { Width = 1024, Height = 768 };
                Canvas.SetLeft(view, ParkOffset);
                Canvas.SetTop(view, ParkOffset);
                view.EnvironmentRequested += OnEnvironmentRequested;
                view.AdapterCreated += OnAdapterCreated;
                view.AdapterDestroyed += OnAdapterDestroyed;
                view.NavigationStarted += OnNavigationStarted;
                view.NavigationCompleted += OnNavigationCompleted;
                view.NewWindowRequested += OnNewWindowRequested;
                _view = view;
                layer.Children.Add(view);
            }
            else
            {
                MoveToHost(layer);
            }

            await (_adapterReady?.Task ?? throw new InvalidOperationException("The native browser has no initialization task."))
                .WaitAsync(TimeSpan.FromSeconds(30), _lifetime.Token);
            ThrowIfDisposed();
            var current = _view ?? throw new InvalidOperationException("The native browser closed during initialization.");
            var probe = await current.InvokeScript(NativeBrowserLogic.WrapScript("'" + NativeBrowserLogic.EncodingProbe + "'"))
                .WaitAsync(TimeSpan.FromSeconds(10), _lifetime.Token);
            _doubleEncoded = NativeBrowserLogic.DetectDoubleEncoding(probe);
            IsInitialized = true;
            ApplyTheme();
            _owner.NotifyReady(this);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                "The system browser did not start. On Linux install WebKitGTK 4.1 " +
                "(for example, sudo apt install libwebkit2gtk-4.1-0).", ex);
        }
        finally
        {
            _initLock.Release();
        }
    });

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        e.EnableDevTools = false;
        switch (e)
        {
            case AppleWKWebViewEnvironmentRequestedEventArgs apple:
                apple.NonPersistentDataStore = false;
                apple.DataStoreIdentifier = NativeBrowserLogic.GetAppleDataStoreId(_owner.UserDataFolder);
                break;
            case GtkWebViewEnvironmentRequestedEventArgs gtk:
                gtk.EphemeralDataManager = false;
                gtk.BaseDataDirectory = _owner.UserDataFolder;
                gtk.BaseCacheDirectory = System.IO.Path.Combine(_owner.UserDataFolder, "cache");
                gtk.ForceX11GdkBackend = true;
                break;
            case LinuxWpeWebViewEnvironmentRequestedEventArgs wpe:
                wpe.PreferWebKitGtkInstead = true;
                wpe.DataDirectory = _owner.UserDataFolder;
                wpe.CacheDirectory = System.IO.Path.Combine(_owner.UserDataFolder, "cache");
                break;
        }
    }

    private void OnAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _view))
            return;
        HasController = true;
        _adapterReady?.TrySetResult(true);
    }

    private void OnAdapterDestroyed(object? sender, WebViewAdapterEventArgs e)
    {
        if (!ReferenceEquals(sender, _view))
            return;
        HasController = false;
        IsInitialized = false;
        _pageZoom = double.NaN;
        _documentVersion++;
        _navigation?.TrySetException(new InvalidOperationException("The native browser closed during navigation."));
        if (!_disposed)
            _adapterReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (!ReferenceEquals(sender, _view))
            return;
        _isNavigating = true;
        _documentVersion++;
        if (e.Request is not null)
            Url = e.Request.AbsoluteUri;
        _owner.NotifyChanged();
    }

    private async void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _view))
            return;
        var version = _documentVersion;
        var navigation = _navigation;
        try
        {
            if (e.IsSuccess)
            {
                await RefreshMetadataAsync();
                await InstallPagePolicyAsync();
                await ApplyPageThemeAsync();
            }
            if (version != _documentVersion || _disposed || !ReferenceEquals(sender, _view))
                return;
            _isNavigating = false;
            navigation?.TrySetResult(e.IsSuccess);
            _owner.NotifyChanged();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Native browser navigation processing failed ({ex.GetType().Name}).");
            if (version == _documentVersion && !_disposed)
            {
                _isNavigating = false;
                navigation?.TrySetException(ex);
                _owner.ReportError("Could not finish initializing the loaded browser page.");
            }
        }
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (_disposed || _owner.IsDisposed || e.Request is null)
            return;
        _lastNewWindowUrl = e.Request.AbsoluteUri;
        var url = _lastNewWindowUrl;
        // GTK's create signal waits synchronously for this handler. Creating another GTK view
        // inside it would wait for the same GLib thread and deadlock both dispatchers.
        Dispatcher.UIThread.Post(() => OpenPopupAsync(url));
    }

    private async void OpenPopupAsync(string url)
    {
        if (_disposed || _owner.IsDisposed)
            return;
        try
        {
            var popup = await _owner.CreateTabAsync(activate: true);
            var result = await popup.OpenAndSnapshotAsync(url);
            if (result.Contains("Error:", StringComparison.Ordinal))
                _owner.ReportError("The requested popup tab could not load. " + result);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Native browser popup failed ({ex.GetType().Name}).");
            _owner.ReportError("Could not open the requested popup tab.");
        }
    }

    internal void MoveToHost(Canvas? layer)
    {
        Dispatcher.UIThread.VerifyAccess();
        var view = _view;
        if (view is null || ReferenceEquals(view.Parent, layer))
            return;
        using (view.BeginReparenting())
        {
            (view.Parent as Canvas)?.Children.Remove(view);
            if (layer is not null)
                layer.Children.Add(view);
        }
        if (layer is null)
            _visible = false;
    }

    internal void UpdatePresentation(Canvas? layer, Rect? physicalBounds, double rasterizationScale, bool visible)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed || _view is not { } view)
            return;
        if (layer is not null)
            MoveToHost(layer);
        var bounds = layer is not null && physicalBounds is { } physical
            ? NativeBrowserLogic.GetOverlayBounds(layer, physical, rasterizationScale) : null;
        _visible = visible && bounds is not null && layer is not null;
        if (bounds is { } rectangle)
        {
            view.Width = Math.Max(1, rectangle.Width);
            view.Height = Math.Max(1, rectangle.Height);
        }
        if (HasController && layer is not null && TopLevel.GetTopLevel(layer) is { } topLevel)
        {
            var zoom = rasterizationScale / topLevel.RenderScaling;
            if (double.IsFinite(zoom) && zoom > 0 && zoom != _pageZoom)
            {
                NativeWebViewPlatform.SetPageZoom(view, zoom);
                _pageZoom = zoom;
            }
        }
        Canvas.SetLeft(view, _visible ? bounds!.Value.X : ParkOffset);
        Canvas.SetTop(view, _visible ? bounds!.Value.Y : ParkOffset);
    }

    internal void Hide()
    {
        _visible = false;
        if (_view is { } view)
        {
            Canvas.SetLeft(view, ParkOffset);
            Canvas.SetTop(view, ParkOffset);
        }
    }

    internal void ApplyTheme()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed || _view is null)
            return;
        _view.Background = _owner.IsDark ? new SolidColorBrush(Color.FromRgb(30, 30, 30)) : Brushes.White;
        if (IsInitialized)
            ApplyPageThemeWithoutThrowing();
    }

    private async void ApplyPageThemeWithoutThrowing()
    {
        try { await ApplyPageThemeAsync(); }
        catch (Exception ex)
        {
            Debug.WriteLine($"Native browser theme update failed ({ex.GetType().Name}).");
        }
    }

    private Task ApplyPageThemeAsync()
    {
        var scheme = _owner.IsDark ? "dark" : "light";
        return ExecuteScriptAsync(
            "(() => {let m=document.querySelector('meta[name=\"color-scheme\"]');" +
            "if(!m){m=document.createElement('meta');m.name='color-scheme';document.head.appendChild(m);}" +
            "m.content='" + scheme + "';document.documentElement.style.colorScheme='" + scheme + "';return true;})()");
    }

    private Task InstallPagePolicyAsync() => ExecuteScriptAsync(
        "(() => {window.alert=()=>{};window.confirm=()=>false;window.prompt=()=>null;" +
        "window.onbeforeunload=null;return true;})()");

    internal void Reload()
    {
        _ = BrowserService.OnUiThreadAsync(() =>
        {
            if (!_disposed && _view is { } view)
                view.Refresh();
        });
    }

    private async Task<string> ExecuteScriptAsync(string script)
    {
        Dispatcher.UIThread.VerifyAccess();
        ThrowIfDisposed();
        var view = _view ?? throw new InvalidOperationException("The browser is not initialized.");
        var result = await view.InvokeScript(NativeBrowserLogic.WrapScript(script))
            .WaitAsync(TimeSpan.FromSeconds(15), _lifetime.Token);
        ThrowIfDisposed();
        return NativeBrowserLogic.NormalizeResult(result, _doubleEncoded);
    }

    private async Task RefreshMetadataAsync()
    {
        var raw = await ExecuteScriptAsync("({url:location.href,title:document.title})");
        using var data = JsonDocument.Parse(raw);
        if (data.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The browser did not return page metadata.");
        var url = data.RootElement.GetProperty("url").GetString() ?? "about:blank";
        var title = data.RootElement.GetProperty("title").GetString() ?? "";
        if (url != Url || title != Title)
        {
            Url = url;
            Title = title;
            _owner.NotifyChanged();
        }
    }

    internal async Task<BrowserScreenshot> CaptureScreenshotAsync()
    {
        ThrowIfDisposed();
        if (!IsInitialized)
            throw new InvalidOperationException($"Tab {Id} is not initialized. Open a page before capturing.");
        if (!await _actionLock.WaitAsync(TimeSpan.FromSeconds(10), _lifetime.Token))
            throw new InvalidOperationException($"Tab {Id} is busy. Wait for its browser action to finish.");
        try
        {
            var screenshot = await BrowserService.OnUiThreadAsync(async () =>
            {
                ThrowIfDisposed();
                var view = _view ?? throw new InvalidOperationException($"Tab {Id} has no native view.");
                if (!_visible)
                    throw new InvalidOperationException($"Tab {Id} is hidden. Switch to it and show the browser panel.");
                if (_isNavigating)
                    throw new InvalidOperationException($"Tab {Id} is navigating. Wait for the page to load.");
                if (view.Bounds.Width < 1 || view.Bounds.Height < 1)
                    throw new InvalidOperationException($"Tab {Id} has no usable viewport.");
                var version = _documentVersion;
                await RefreshMetadataAsync();
                var url = Url;
                var bytes = await NativeWebViewPlatform.CapturePngAsync(view, _lifetime.Token)
                    .WaitAsync(TimeSpan.FromSeconds(10), _lifetime.Token);
                ThrowIfDisposed();
                await RefreshMetadataAsync();
                if (version != _documentVersion || Url != url || !_visible || !ReferenceEquals(view, _view))
                    throw new InvalidOperationException($"Tab {Id} changed during capture. Observe the tab before capturing again.");
                if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                    throw new InvalidOperationException("The system browser did not return a PNG screenshot.");
                var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
                var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
                if (width < 1 || height < 1)
                    throw new InvalidOperationException("The system browser returned an empty screenshot.");
                return new BrowserScreenshot(Id, url, width, height, bytes);
            });
            return await Task.Run(() => BrowserService.PrepareScreenshotForModel(screenshot), _lifetime.Token);
        }
        finally { _actionLock.Release(); }
    }

    internal async Task ClearCookiesAsync()
    {
        await _operationGate.RunAsync(async () =>
        {
            await EnsureInitializedAsync();
            await _actionLock.WaitAsync(_lifetime.Token);
            try
            {
                await BrowserService.OnUiThreadAsync(() => NativeWebViewCookies.ClearAsync(_view!));
                return "Cookies cleared.";
            }
            finally { _actionLock.Release(); }
        });
    }

    internal async Task SetCookiesAsync(IReadOnlyList<Cookie> cookies)
    {
        await _operationGate.RunAsync(async () =>
        {
            await EnsureInitializedAsync();
            await _actionLock.WaitAsync(_lifetime.Token);
            try
            {
                await BrowserService.OnUiThreadAsync(() => NativeWebViewCookies.SetAsync(_view!, cookies));
                return "Cookies imported.";
            }
            finally { _actionLock.Release(); }
        });
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetime)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);
            _disposed = true;
            _lifetime.Cancel();
            _adapterReady?.TrySetCanceled();
            _navigation?.TrySetCanceled();
            _disposeTask = DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _initLock.WaitAsync();
        await _actionLock.WaitAsync();
        try
        {
            if (_view is null)
            {
                IsInitialized = false;
                HasController = false;
                return;
            }
            await BrowserService.OnUiThreadAsync(() =>
            {
                var view = _view;
                _view = null;
                IsInitialized = false;
                HasController = false;
                _visible = false;
                if (view is null)
                    return;
                view.EnvironmentRequested -= OnEnvironmentRequested;
                view.AdapterCreated -= OnAdapterCreated;
                view.AdapterDestroyed -= OnAdapterDestroyed;
                view.NavigationStarted -= OnNavigationStarted;
                view.NavigationCompleted -= OnNavigationCompleted;
                view.NewWindowRequested -= OnNewWindowRequested;
                (view.Parent as Canvas)?.Children.Remove(view);
            });
        }
        finally
        {
            _actionLock.Release();
            _initLock.Release();
        }
    }
}
#endif
