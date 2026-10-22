using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Services;
using Lumi.Services.EmbeddedApps;
using Lumi.ViewModels;

namespace Lumi.Views;

/// <summary>
/// The Workspace's app page: one MCP App or canvas in a browser kept apart from Lumi's own browsing
/// profile. Lumi's loading and error states stand in for the app until it reports that it is ready.
/// </summary>
public partial class EmbeddedAppView : UserControl, IDisposable
{
    private enum AppState { Idle, Loading, Ready, Failed }

    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(45);

    // Lumi's theme as the MCP Apps host style variables, so apps that use them look at home.
    private static readonly (string Variable, string Resource)[] ThemeColors =
    [
        ("--color-background-primary", "Color.Surface0"),
        ("--color-background-secondary", "Color.Surface1"),
        ("--color-background-tertiary", "Color.Surface2"),
        ("--color-background-inverse", "Color.TextPrimary"),
        ("--color-background-info", "Color.AccentSubtle"),
        ("--color-background-danger", "Color.DangerSubtle"),
        ("--color-background-disabled", "Color.ControlDisabled"),
        ("--color-text-primary", "Color.TextPrimary"),
        ("--color-text-secondary", "Color.TextSecondary"),
        ("--color-text-tertiary", "Color.TextTertiary"),
        ("--color-text-inverse", "Color.Surface0"),
        ("--color-text-info", "Color.AccentDefault"),
        ("--color-text-danger", "Color.DangerDefault"),
        ("--color-text-disabled", "Color.TextDisabled"),
        ("--color-border-primary", "Color.BorderDefault"),
        ("--color-border-secondary", "Color.BorderSubtle"),
        ("--color-border-tertiary", "Color.BorderSubtle"),
        ("--color-border-inverse", "Color.TextPrimary"),
        ("--color-border-info", "Color.AccentDefault"),
        ("--color-border-danger", "Color.DangerDefault"),
        ("--color-ring-primary", "Color.AccentDefault"),
        ("--color-ring-info", "Color.AccentDefault"),
        ("--color-ring-danger", "Color.DangerDefault"),
    ];

    private static readonly (string Variable, string Resource)[] ThemeRadii =
    [
        ("--border-radius-sm", "Radius.Base"),
        ("--border-radius-md", "Radius.Interactive"),
        ("--border-radius-lg", "Radius.Overlay"),
    ];

    private readonly ChatViewModel? _owner;
    private readonly DataStore? _dataStore;
    private readonly BrowserView _browserView;
    private readonly StackPanel _status;
    private readonly PathIcon _statusIcon;
    private readonly TextBlock _statusTitle;
    private readonly SelectableTextBlock _statusDetail;
    private readonly Border _loadingLine;
    private readonly Button _retryButton;
    private BrowserService? _browser;
    private EmbeddedAppHost? _host;
    private EmbeddedAppItem? _item;
    private AppState _state;
    private CancellationTokenSource? _loadCts;
    private bool _disposed;

    public EmbeddedAppView()
    {
        AvaloniaXamlLoader.Load(this);
        _browserView = this.FindControl<BrowserView>("AppBrowser")!;
        _status = this.FindControl<StackPanel>("AppStatus")!;
        _statusIcon = this.FindControl<PathIcon>("AppStatusIcon")!;
        _statusTitle = this.FindControl<TextBlock>("AppStatusTitle")!;
        _statusDetail = this.FindControl<SelectableTextBlock>("AppStatusDetail")!;
        _loadingLine = this.FindControl<Border>("AppLoadingLine")!;
        _retryButton = this.FindControl<Button>("AppRetryButton")!;
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
    }

    internal EmbeddedAppView(ChatViewModel owner, DataStore dataStore) : this()
    {
        _owner = owner;
        _dataStore = dataStore;
    }

    internal BrowserService? Browser => _browser;
    internal EmbeddedAppItem? App => _item;
    internal bool IsAppReady => _state == AppState.Ready;
    internal string? AppError => _state == AppState.Failed ? _statusDetail.Text : null;

    private bool IsDark => ActualThemeVariant == ThemeVariant.Dark;

    // Loading, or showing, this app already: another request for it changes nothing.
    private bool IsShowing(EmbeddedAppItem item)
        => ReferenceEquals(item, _item) && (_loadCts is not null || _host is not null) && _state != AppState.Failed;

    /// <summary>Shows the app's loading state at once, while the page is still settling.</summary>
    internal void ShowLoading(EmbeddedAppItem item)
    {
        if (_disposed || IsShowing(item))
            return;
        CancelLoad();
        SetLoading(item);
    }

    /// <summary>Opens the app (or, with <paramref name="reload"/>, opens it again).</summary>
    internal async Task ShowAsync(EmbeddedAppItem item, bool reload = false)
    {
        if (_disposed || _owner is not { } owner || _dataStore is null || (!reload && IsShowing(item)))
            return;

        CancelLoad();
        var load = _loadCts = new CancellationTokenSource();
        SetLoading(item);
        try
        {
            var chat = owner.CurrentChat ?? throw new InvalidOperationException(Loc.Apps_Unavailable);
            var launch = await owner.PrepareAppAsync(chat, item.Message, load.Token);
            load.Token.ThrowIfCancellationRequested();
            var host = new EmbeddedAppHost(launch, BuildHostContext(),
                (method, parameters, token) => owner.DispatchAppRequestAsync(chat, item.Message, method, parameters, token));
            _host = host;
            host.Ready += () => Dispatcher.UIThread.Post(() => OnReady(host));
            host.Failed += message => Dispatcher.UIThread.Post(() => OnFailed(host, message));
            await EnsureBrowser().NavigateAsync(host.Url);
            DispatcherTimer.RunOnce(() => OnFailed(host, Loc.Apps_NotResponding, onlyWhileLoading: true), LoadTimeout);
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception error)
        {
            Trace.TraceWarning("[EmbeddedApp] Could not open '{0}': {1}", item.Title, error);
            if (!_disposed && ReferenceEquals(load, _loadCts))
                SetFailed(error.Message);
        }
        finally
        {
            if (ReferenceEquals(load, _loadCts))
                _loadCts = null;
            load.Dispose();
        }
    }

    // Stops opening the previous app and shuts its host down; the browser stays for the next one.
    private void CancelLoad()
    {
        _loadCts?.Cancel();
        _loadCts = null;
        _ = DisposeQuietlyAsync(host: DetachHost());
    }

    private BrowserService EnsureBrowser()
    {
        if (_browser is { } existing)
            return existing;
        // Every app shares one profile, apart from Lumi's browser; each runs on its own loopback origins.
        var browser = _browser = new BrowserService(Path.Combine(DataStore.AppDirectory, "app-browser-data"));
        browser.BrowserError += OnBrowserError;
        browser.SetTheme(IsDark);
        _browserView.SetBrowserService(browser, _dataStore!);
        return browser;
    }

    private void OnReady(EmbeddedAppHost host)
    {
        if (_disposed || !ReferenceEquals(host, _host) || _state == AppState.Failed)
            return;
        _state = AppState.Ready;
        _status.IsVisible = false;
        _browserView.IsVisible = true;
        // The native view takes its place once layout has measured it.
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !ReferenceEquals(host, _host) || _state != AppState.Ready)
                return;
            _browserView.RefreshBounds();
            _browserView.ShowCurrentController();
        }, DispatcherPriority.Loaded);
    }

    private void OnFailed(EmbeddedAppHost host, string message, bool onlyWhileLoading = false)
    {
        if (_disposed || !ReferenceEquals(host, _host) || (onlyWhileLoading && _state != AppState.Loading))
            return;
        Trace.TraceWarning("[EmbeddedApp] '{0}' failed: {1}", _item?.Title, message);
        SetFailed(message);
    }

    // Browser errors only matter while the app is opening; once it shows, the app reports its own.
    private void OnBrowserError(string message) => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed && _state == AppState.Loading && _host is not null)
            SetFailed(message);
    });

    private void SetLoading(EmbeddedAppItem item)
    {
        _item = item;
        _state = AppState.Loading;
        Classes.Set("failed", false);
        _browserView.IsVisible = false;
        _status.IsVisible = true;
        _statusIcon.Data = WorkspaceIcons.Apps;
        _statusTitle.Text = item.Title;
        _statusDetail.Text = Loc.Apps_Opening;
        _loadingLine.IsVisible = true;
        _retryButton.IsVisible = false;
    }

    private void SetFailed(string message)
    {
        _state = AppState.Failed;
        Classes.Set("failed", true);
        _browserView.IsVisible = false;
        _status.IsVisible = true;
        _statusIcon.Data = WorkspaceIcons.Error;
        _statusTitle.Text = string.Format(Loc.Apps_CouldNotOpen, _item?.Title);
        _statusDetail.Text = message;
        _loadingLine.IsVisible = false;
        _retryButton.IsVisible = true;
    }

    private void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        if (_item is { } item)
            _ = ShowAsync(item, reload: true);
    }

    /// <summary>The MCP Apps host context: Lumi's theme as the standard style variables, and where it runs.</summary>
    private JsonObject BuildHostContext()
    {
        var variables = new JsonObject();
        foreach (var (variable, resource) in ThemeColors)
        {
            if (this.TryFindResource(resource, ActualThemeVariant, out var value) && value is Color color)
                variables[variable] = color.A == 255
                    ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                    : string.Create(CultureInfo.InvariantCulture, $"rgba({color.R}, {color.G}, {color.B}, {color.A / 255d:0.###})");
        }
        foreach (var (variable, resource) in ThemeRadii)
        {
            if (this.TryFindResource(resource, ActualThemeVariant, out var value) && value is CornerRadius radius)
                variables[variable] = string.Create(CultureInfo.InvariantCulture, $"{radius.TopLeft}px");
        }
        variables["--border-radius-full"] = "9999px";
        if (this.TryFindResource("Font.Family", ActualThemeVariant, out var sans) && sans is FontFamily sansFamily)
            variables["--font-sans"] = CssFontFamily(sansFamily);
        if (this.TryFindResource("Font.FamilyMono", ActualThemeVariant, out var mono) && mono is FontFamily monoFamily)
            variables["--font-mono"] = CssFontFamily(monoFamily);

        return new JsonObject
        {
            ["theme"] = IsDark ? "dark" : "light",
            ["styles"] = new JsonObject { ["variables"] = variables },
            ["locale"] = Loc.Culture.Name,
            ["platform"] = "desktop",
            ["displayMode"] = "inline",
            ["availableDisplayModes"] = new JsonArray("inline"),
        };
    }

    private static string CssFontFamily(FontFamily family)
        => string.Join(", ", family.FamilyNames.Select(static name => name.Contains(' ') ? "\"" + name + "\"" : name));

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        if (_browser is not { } browser)
            return;
        browser.SetTheme(IsDark);
        if (_state == AppState.Ready && _item?.IsCanvas == false)
            _ = UpdateHostContextAsync(browser);
    }

    private async Task UpdateHostContextAsync(BrowserService browser)
    {
        try
        {
            await browser.EvaluateAsync("window.lumiHostContext?.(" + BuildHostContext().ToJsonString() + ")");
        }
        catch (Exception error)
        {
            Trace.TraceWarning("[EmbeddedApp] Could not update the app's theme: {0}", error.Message);
        }
    }

    private EmbeddedAppHost? DetachHost()
    {
        var host = _host;
        _host = null;
        return host;
    }

    private static async Task DisposeQuietlyAsync(BrowserService? browser = null, EmbeddedAppHost? host = null)
    {
        try
        {
            if (browser is not null)
                await browser.DisposeAsync();
        }
        catch (Exception error)
        {
            Trace.TraceWarning("[EmbeddedApp] Browser cleanup failed: {0}", error.Message);
        }
        try
        {
            if (host is not null)
                await host.DisposeAsync();
        }
        catch (Exception error)
        {
            Trace.TraceWarning("[EmbeddedApp] Host cleanup failed: {0}", error.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        _loadCts?.Cancel();
        var browser = _browser;
        _browser = null;
        _browserView.ClearBrowserService();
        if (browser is not null)
            browser.BrowserError -= OnBrowserError;
        _ = DisposeQuietlyAsync(browser, DetachHost());
    }
}
