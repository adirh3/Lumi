using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Lumi.Services;

namespace Lumi.Views;

public partial class BrowserView : UserControl
{
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
    private BrowserCookieService.BrowserProfile? _selectedProfile;

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

    /// <summary>Binds the BrowserService and DataStore to this view.</summary>
    public void SetBrowserService(BrowserService browserService, DataStore dataStore)
    {
        _browserService = browserService;
        _dataStore = dataStore;
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

        // Corner radius in physical pixels matching BrowserIsland's inner CornerRadius
        // (Radius.Overlay=14 minus BorderThickness=1 = 13 logical pixels)
        var cornerRadiusPx = (int)Math.Round(13.0 * scaling);

        // Convert logical coordinates to physical pixels for the WebView2 overlay.
        var x = (int)Math.Round(point.Value.X * scaling);
        var y = (int)Math.Round((point.Value.Y + urlBarHeight) * scaling);
        var w = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        var h = Math.Max(1, (int)Math.Round((Bounds.Height - urlBarHeight) * scaling));

        _browserService.SetBounds(x, y, w, h, cornerRadiusPx);
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

        // Hide WebView2 while onboarding is visible
        if (_browserService?.Controller is not null)
            _browserService.Controller.IsVisible = false;

        _profilePicker.Children.Clear();
        _selectedProfile = null;

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

                // Auto-select Edge if available, otherwise first profile
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
        if (_browserService?.Controller is not null)
            _browserService.Controller.IsVisible = true;
        UpdateWebViewBounds();
    }

    private async void OnImportOnboardingClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null || _browserService is null) return;

        // Show progress, hide actions
        if (_onboardingActions is not null) _onboardingActions.IsVisible = false;
        if (_importProgressPanel is not null) _importProgressPanel.IsVisible = true;
        SetImportStatus("Importing cookies…");

        try
        {
            var count = await _browserService.ImportCookiesAsync(_selectedProfile);

            SetImportStatus($"Imported {count:N0} cookies!");
            await System.Threading.Tasks.Task.Delay(1000);

            // Mark as imported
            if (_dataStore is not null)
            {
                _dataStore.Data.Settings.HasImportedBrowserCookies = true;
                _dataStore.Save();
            }

            HideCookieOnboarding();
            _browserService.WebView?.Reload();
        }
        catch (Exception ex)
        {
            SetImportStatus($"Error: {ex.Message}");
            await System.Threading.Tasks.Task.Delay(2000);

            // Reset UI so user can try again
            if (_onboardingActions is not null) _onboardingActions.IsVisible = true;
            if (_importProgressPanel is not null) _importProgressPanel.IsVisible = false;
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
        _browserService?.WebView?.Reload();
    }

    private async void OnCookieImportClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        var browsers = BrowserCookieService.GetInstalledBrowsers();
        if (browsers.Count == 0)
        {
            ShowCookieFlyout(button, "No supported browsers found.");
            return;
        }

        var allProfiles = browsers.SelectMany(BrowserCookieService.GetProfiles).ToList();

        // Quick-import: if only one profile available, skip the flyout
        if (allProfiles.Count == 1)
        {
            await ImportFromProfileAsync(allProfiles[0]);
            return;
        }

        var flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            ShowMode = FlyoutShowMode.Transient
        };

        var panel = new StackPanel { Spacing = 2, MinWidth = 240 };

        var header = new TextBlock
        {
            Text = "Import cookies from:",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(8, 6, 8, 8)
        };
        panel.Children.Add(header);

        foreach (var browser in browsers)
        {
            var profiles = BrowserCookieService.GetProfiles(browser);
            if (profiles.Count == 0) continue;

            var browserHeader = new TextBlock
            {
                Text = browser.Name,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(8, 8, 8, 2)
            };
            if (this.TryFindResource("Brush.TextSecondary", this.ActualThemeVariant, out var brush) && brush is IBrush b)
                browserHeader.Foreground = b;
            panel.Children.Add(browserHeader);

            foreach (var profile in profiles)
            {
                var profileBtn = new Button
                {
                    Content = profile.Name,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(12, 6),
                    MinHeight = 0,
                    Classes = { "subtle" }
                };
                var capturedProfile = profile;
                var capturedFlyout = flyout;
                profileBtn.Click += async (_, _) =>
                {
                    capturedFlyout.Hide();
                    await ImportFromProfileAsync(capturedProfile);
                };
                panel.Children.Add(profileBtn);
            }
        }

        flyout.Content = new ScrollViewer
        {
            MaxHeight = 400,
            Content = panel
        };
        flyout.ShowAt(button);
    }

    private async Task ImportFromProfileAsync(BrowserCookieService.BrowserProfile profile)
    {
        if (_browserService is null) return;

        try
        {
            var count = await _browserService.ImportCookiesAsync(profile);
            ShowCookieNotification($"Imported {count:N0} cookies from {profile.Browser.Name} ({profile.Name})");
            // Refresh the current page to apply cookies
            _browserService.WebView?.Reload();
        }
        catch (Exception ex)
        {
            ShowCookieNotification(ex.Message);
        }
    }

    private void ShowCookieNotification(string message)
    {
        if (_urlText is not null)
        {
            var originalText = _urlText.Text;
            _urlText.Text = message;
            DispatcherTimer.RunOnce(() =>
            {
                if (_urlText is not null)
                    _urlText.Text = _browserService?.CurrentUrl ?? originalText;
            }, TimeSpan.FromSeconds(3));
        }
    }

    private void ShowCookieFlyout(Button button, string message)
    {
        var flyout = new Flyout
        {
            Content = new TextBlock
            {
                Text = message,
                FontSize = 13,
                Margin = new Thickness(8)
            },
            Placement = PlacementMode.BottomEdgeAlignedRight
        };
        flyout.ShowAt(button);
    }
}
