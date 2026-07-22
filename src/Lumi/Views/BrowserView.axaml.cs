using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Lumi.Services;

namespace Lumi.Views;

public partial class BrowserView : UserControl
{
    internal readonly record struct NativeWebViewLayout(
        int X,
        int Y,
        int Width,
        int Height,
        int CornerRadiusPx,
        double RasterizationScale);

    private Border? _loadingOverlay;
    private Border? _cookieOnboardingOverlay;
    private StackPanel? _profilePicker;
    private StackPanel? _importProgressPanel;
    private TextBlock? _importStatusText;
    private StackPanel? _onboardingActions;
    private TextBlock? _urlText;
    private Border? _urlBar;
    private BrowserService? _browserService;
    private DataStore? _dataStore;
    private bool _isInitialized;
    private bool _isImportInProgress;
    private BrowserCookieService.BrowserProfile? _selectedProfile;
    private Action? _urlChangedHandler;

    public BrowserView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _loadingOverlay = this.FindControl<Border>("LoadingOverlay");
        _cookieOnboardingOverlay = this.FindControl<Border>("CookieOnboardingOverlay");
        _profilePicker = this.FindControl<StackPanel>("ProfilePicker");
        _importProgressPanel = this.FindControl<StackPanel>("ImportProgressPanel");
        _importStatusText = this.FindControl<TextBlock>("ImportStatusText");
        _onboardingActions = this.FindControl<StackPanel>("OnboardingActions");
        _urlText = this.FindControl<TextBlock>("UrlText");
        _urlBar = this.FindControl<Border>("UrlBar");
    }

    /// <summary>Binds a BrowserService and DataStore to this view. Can be called multiple times to switch between per-chat services.</summary>
    public void SetBrowserService(BrowserService browserService, DataStore dataStore)
    {
        ClearBrowserService();
        _browserService = browserService;
        _dataStore = dataStore;
        _isInitialized = false;
        _browserService.BrowserReady += OnBrowserReady;

        // Pre-set the HWND so tools can trigger lazy initialization
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null)
        {
            var handle = topLevel.TryGetPlatformHandle();
            if (handle is not null)
                _browserService.SetParentHwnd(handle.Handle);
        }

        // If the new service is already initialized, update immediately
        if (_browserService.IsInitialized)
        {
            OnBrowserReady();
            UpdateUrl();
        }
        else
        {
            // Show loading overlay while initializing
            if (_loadingOverlay is not null)
                _loadingOverlay.IsVisible = true;
            if (_urlText is not null)
                _urlText.Text = "about:blank";
            TryInitialize();
        }
    }

    public void ClearBrowserService()
    {
        if (_browserService is not null)
        {
            _browserService.BrowserReady -= OnBrowserReady;
            _browserService.SetControllerVisible(false);
            if (_urlChangedHandler is not null)
                _browserService.UrlChanged -= _urlChangedHandler;
        }

        _urlChangedHandler = null;
        _browserService = null;
        _dataStore = null;
        _isInitialized = false;
    }

    /// <summary>Hides the current browser service's controller overlay.</summary>
    public void HideCurrentController()
    {
        _browserService?.SetControllerVisible(false);
    }

    /// <summary>Shows the current browser service's controller overlay.</summary>
    public void ShowCurrentController()
    {
        _browserService?.SetControllerVisible(true);
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
                _browserService?.SetControllerVisible(true);
            }
            else
            {
                _browserService?.SetControllerVisible(false);
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

            if (_browserService is not null)
            {
                // Unsub old handler if switching services
                if (_urlChangedHandler is not null)
                    _browserService.UrlChanged -= _urlChangedHandler;

                _urlChangedHandler = () => Dispatcher.UIThread.Post(UpdateUrl);
                _browserService.UrlChanged += _urlChangedHandler;
            }

            // Show cookie onboarding if user hasn't imported yet
            if (_dataStore is not null && !_dataStore.Data.Settings.HasImportedBrowserCookies)
                ShowCookieOnboarding();

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
        if (_browserService is null || !_browserService.HasController) return;
        if (!IsEffectivelyVisible || Bounds.Width < 1 || Bounds.Height < 1)
        {
            // Not on screen — hide the native overlay
            _browserService.SetControllerVisible(false);
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var platformHandle = topLevel.TryGetPlatformHandle();
        if (platformHandle is not null)
            _browserService.SetParentHwnd(platformHandle.Handle);

        var layout = CalculateNativeWebViewLayout(topLevel);
        if (layout is null) return;

        _browserService.SyncRasterizationScale(layout.Value.RasterizationScale);
        _browserService.SetBounds(
            layout.Value.X,
            layout.Value.Y,
            layout.Value.Width,
            layout.Value.Height,
            layout.Value.CornerRadiusPx);
    }

    internal NativeWebViewLayout? CalculateNativeWebViewLayout(TopLevel topLevel)
    {
        var urlBarHeight = _urlBar?.Bounds.Height ?? 36.0;
        if (urlBarHeight < 1)
            urlBarHeight = 36.0;

        var localWidth = Bounds.Width;
        var localContentHeight = Bounds.Height - urlBarHeight;
        if (localWidth < 1 || localContentHeight < 1)
            return null;

        var firstCorner = this.TranslatePoint(new Point(0, urlBarHeight), topLevel);
        var secondCorner = this.TranslatePoint(new Point(Bounds.Width, Bounds.Height), topLevel);
        if (firstCorner is null || secondCorner is null)
            return null;

        var left = Math.Min(firstCorner.Value.X, secondCorner.Value.X);
        var top = Math.Min(firstCorner.Value.Y, secondCorner.Value.Y);
        var right = Math.Max(firstCorner.Value.X, secondCorner.Value.X);
        var bottom = Math.Max(firstCorner.Value.Y, secondCorner.Value.Y);
        var transformedWidth = right - left;
        if (transformedWidth < 1)
            return null;

        var renderScaling = topLevel.RenderScaling;
        var uiScale = transformedWidth / localWidth;
        var rasterizationScale = renderScaling * uiScale;
        if (!double.IsFinite(rasterizationScale) || rasterizationScale <= 0)
            return null;

        var x = (int)Math.Floor(left * renderScaling);
        var y = (int)Math.Floor(top * renderScaling);
        var rightPx = (int)Math.Ceiling(right * renderScaling);
        var bottomPx = (int)Math.Ceiling(bottom * renderScaling);

        return new NativeWebViewLayout(
            x,
            y,
            Math.Max(1, rightPx - x),
            Math.Max(1, bottomPx - y),
            (int)Math.Round(13.0 * rasterizationScale),
            rasterizationScale);
    }

    // ── Cookie onboarding ──────────────────────────────────────────

    private void ShowCookieOnboarding()
    {
        if (_cookieOnboardingOverlay is null || _profilePicker is null) return;

        var browsers = BrowserCookieService.GetInstalledBrowsers();
        var profilesByBrowser = browsers
            .Select(b => (Browser: b, Profiles: BrowserCookieService.GetProfiles(b)))
            .Where(x => x.Profiles.Count > 0)
            .ToList();

        if (profilesByBrowser.Count == 0) return; // no browsers to import from

        // Mark as handled when shown the first time so it won't appear on every open.
        if (_dataStore is not null && !_dataStore.Data.Settings.HasImportedBrowserCookies)
        {
            _dataStore.Data.Settings.HasImportedBrowserCookies = true;
            _dataStore.Save();
        }

        // Hide WebView2 while onboarding is visible
        _browserService?.SetControllerVisible(false);

        _profilePicker.Children.Clear();
        _selectedProfile = null;
        _isImportInProgress = false;

        if (_onboardingActions is not null)
            _onboardingActions.IsVisible = true;
        if (_importProgressPanel is not null)
            _importProgressPanel.IsVisible = false;
        SetImportStatus("Preparing…");

        foreach (var (browser, profiles) in profilesByBrowser)
        {

            foreach (var profile in profiles)
            {
                var rb = new RadioButton
                {
                    Content = $"{browser.Name} — {profile.Name}",
                    GroupName = "BrowserProfile",
                    Padding = new Thickness(8, 8),
                    Tag = profile,
                };
                rb.IsCheckedChanged += (_, _) =>
                {
                    if (rb.IsChecked == true)
                        _selectedProfile = (BrowserCookieService.BrowserProfile)rb.Tag!;
                };

                if (_selectedProfile is null ||
                    (browser.Name.Contains("Edge", StringComparison.OrdinalIgnoreCase) && _selectedProfile.Browser.Name != browser.Name))
                {
                    rb.IsChecked = true;
                    _selectedProfile = profile;
                }

                _profilePicker.Children.Add(rb);
            }
        }

        _cookieOnboardingOverlay.IsVisible = true;
    }

    private void HideCookieOnboarding()
    {
        if (_cookieOnboardingOverlay is not null)
            _cookieOnboardingOverlay.IsVisible = false;

        // Restore WebView2 visibility
        _browserService?.SetControllerVisible(true);
        UpdateWebViewBounds();
    }

    private async void OnImportOnboardingClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null || _browserService is null || _isImportInProgress) return;

        _isImportInProgress = true;

        // Show progress, hide actions
        if (_onboardingActions is not null) _onboardingActions.IsVisible = false;
        if (_importProgressPanel is not null) _importProgressPanel.IsVisible = true;
        SetImportStatus("Importing cookies…");

        try
        {
            var count = await _browserService
                .ImportCookiesAsync(_selectedProfile)
                .WaitAsync(TimeSpan.FromSeconds(45));

            if (count <= 0)
            {
                SetImportStatus("No cookies imported. Close the selected browser and try again.");
                await System.Threading.Tasks.Task.Delay(2200);
                if (_onboardingActions is not null) _onboardingActions.IsVisible = true;
                if (_importProgressPanel is not null) _importProgressPanel.IsVisible = false;
                return;
            }

            SetImportStatus($"Imported {count:N0} cookies!");
            await System.Threading.Tasks.Task.Delay(1000);

            // Mark as imported
            if (_dataStore is not null)
            {
                _dataStore.Data.Settings.HasImportedBrowserCookies = true;
                _dataStore.Save();
            }

            if (_importProgressPanel is not null) _importProgressPanel.IsVisible = false;
            HideCookieOnboarding();
            _browserService.Reload();
        }
        catch (TimeoutException)
        {
            SetImportStatus("Cookie import timed out. Close the browser and try again.");
            await System.Threading.Tasks.Task.Delay(2200);

            if (_onboardingActions is not null) _onboardingActions.IsVisible = true;
            if (_importProgressPanel is not null) _importProgressPanel.IsVisible = false;
        }
        catch (Exception ex)
        {
            SetImportStatus($"Error: {ex.Message}");
            await System.Threading.Tasks.Task.Delay(2000);

            // Reset UI so user can try again
            if (_onboardingActions is not null) _onboardingActions.IsVisible = true;
            if (_importProgressPanel is not null) _importProgressPanel.IsVisible = false;
        }
        finally
        {
            _isImportInProgress = false;
        }
    }

    private void OnSkipOnboardingClick(object? sender, RoutedEventArgs e)
    {
        // Mark as imported so we don't show again
        if (_dataStore is not null)
        {
            _dataStore.Data.Settings.HasImportedBrowserCookies = true;
            _dataStore.Save();
        }

        HideCookieOnboarding();
    }

    private void SetImportStatus(string text)
    {
        if (_importStatusText is not null)
            _importStatusText.Text = text;
    }

    // ── Navigation ─────────────────────────────────────────────────

    private async void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (_browserService is not null)
            await _browserService.GoBackAsync();
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        _browserService?.Reload();
    }
}
