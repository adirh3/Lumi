using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Lumi.Services;

namespace Lumi.Views;

public partial class BrowserView : UserControl
{
    private Border? _loadingOverlay;
    private TextBlock? _urlText;
    private Border? _urlBar;
    private BrowserService? _browserService;
    private bool _isInitialized;

    public BrowserView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _loadingOverlay = this.FindControl<Border>("LoadingOverlay");
        _urlText = this.FindControl<TextBlock>("UrlText");
        _urlBar = this.FindControl<Border>("UrlBar");
    }

    /// <summary>Binds the BrowserService to this view and initializes WebView2 when attached.</summary>
    public void SetBrowserService(BrowserService browserService)
    {
        _browserService = browserService;
        _browserService.BrowserReady += OnBrowserReady;

        // Pre-set the HWND so tools can trigger lazy initialization
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null)
        {
            var handle = topLevel.TryGetPlatformHandle();
            if (handle is not null)
                _browserService.SetParentHwnd(handle.Handle);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Ensure HWND is set for lazy init
        if (_browserService is not null)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is not null)
            {
                var handle = topLevel.TryGetPlatformHandle();
                if (handle is not null)
                    _browserService.SetParentHwnd(handle.Handle);
            }
        }

        // Keep WebView2 overlay in sync whenever Avalonia re-layouts
        LayoutUpdated += OnLayoutUpdated;

        TryInitialize();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdated;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => UpdateWebViewBounds();

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateWebViewBounds();
    }

    /// <summary>Called when visibility changes so we can update bounds or hide WebView2.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            if (IsVisible)
            {
                TryInitialize();
                UpdateWebViewBounds();
                if (_browserService?.Controller is not null)
                    _browserService.Controller.IsVisible = true;
            }
            else
            {
                if (_browserService?.Controller is not null)
                    _browserService.Controller.IsVisible = false;
            }
        }
    }

    private async void TryInitialize()
    {
        if (_isInitialized || _browserService is null || !IsVisible) return;
        if (_browserService.IsInitialized)
        {
            OnBrowserReady();
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var platformHandle = topLevel.TryGetPlatformHandle();
        if (platformHandle is null) return;
        var hwnd = platformHandle.Handle;

        try
        {
            await _browserService.InitializeAsync(hwnd);
            _isInitialized = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebView2 init failed: {ex.Message}");
        }
    }

    private void OnBrowserReady()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_loadingOverlay is not null)
                _loadingOverlay.IsVisible = false;
            _isInitialized = true;

            if (_browserService?.WebView is not null)
            {
                _browserService.WebView.SourceChanged += (_, _) =>
                    Dispatcher.UIThread.Post(UpdateUrl);
            }

            UpdateWebViewBounds();
        });
    }

    private void UpdateUrl()
    {
        if (_urlText is not null && _browserService is not null)
            _urlText.Text = _browserService.CurrentUrl;
    }

    /// <summary>Public method to force a bounds refresh from outside (e.g. after layout changes).</summary>
    public void RefreshBounds() => UpdateWebViewBounds();

    private void UpdateWebViewBounds()
    {
        if (_browserService?.Controller is null) return;
        if (!IsEffectivelyVisible || Bounds.Width < 1 || Bounds.Height < 1)
        {
            // Not on screen — hide the native overlay
            _browserService.Controller.IsVisible = false;
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var scaling = topLevel.RenderScaling;

        // Get position of this control relative to the top-level window
        var point = this.TranslatePoint(new Point(0, 0), topLevel);
        if (point is null) return;

        // Measure URL bar height dynamically from the actual control
        var urlBarHeight = _urlBar?.Bounds.Height ?? 36.0;
        if (urlBarHeight < 1) urlBarHeight = 36.0; // Guard against unmeasured layout

        // Sync rasterization scale with Avalonia's render scaling for sharp text
        if (Math.Abs(_browserService.Controller.RasterizationScale - scaling) > 0.01)
            _browserService.Controller.RasterizationScale = scaling;

        // Small bottom inset so the rectangular WebView2 overlay stays inside
        // the BrowserIsland's rounded bottom corners (CornerRadius=14).
        var cornerInset = 2.0;

        // Convert logical coordinates to physical pixels for the WebView2 overlay.
        // No horizontal inset — the border content area already defines exact bounds.
        var x = (int)Math.Round(point.Value.X * scaling);
        var y = (int)Math.Round((point.Value.Y + urlBarHeight) * scaling);
        var w = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        var h = Math.Max(1, (int)Math.Round((Bounds.Height - urlBarHeight - cornerInset) * scaling));

        _browserService.SetBounds(x, y, w, h);
    }

    private async void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (_browserService is not null)
            await _browserService.GoBackAsync();
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        _browserService?.WebView?.Reload();
    }
}
