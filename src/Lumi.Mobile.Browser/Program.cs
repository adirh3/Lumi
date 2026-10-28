using Avalonia;
using Avalonia.Browser;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using System.Runtime.InteropServices.JavaScript;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.Browser;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        await JSHost.ImportAsync("./browserHost.js", "/app/browserHost.js");
        await JSHost.ImportAsync("./pwaHost.js", "/app/pwaHost.js");
        // The pre-runtime page and Strata/theme XAML must use the same startup language.
        Strings.Load(BrowserInterop.GetLanguage());
        var host = new BrowserHostEnvironment(BrowserInterop.GetOrigin());
        var nativeTextInputOverlayPresenter = new BrowserNativeTextInputOverlayPresenter();
        MobilePlatformServices.HostEnvironment = host;
        MobilePlatformServices.NetworkMonitor = BrowserInterop.NetworkMonitor;
        MobilePlatformServices.ProducedFileOpener = new BrowserProducedFileOpener();
        MobilePlatformServices.TextSelectionPresenter = new BrowserTextSelectionPresenter();
        MobilePlatformServices.TextInputOverlayPresenter = nativeTextInputOverlayPresenter;
        BrowserInterop.NativeTextInputOverlayPresenter = nativeTextInputOverlayPresenter;
        RemotePlatformServices.RouteVerifier = new BrowserSameOriginRouteVerifier();
        App.ShellFactory = () =>
        {
            var store = new BrowserMobileSettingsStore(host.FixedBaseUrl!);
            var settings = store.Load();
            var downloads = new BrowserRemoteDownloadStore();
            var client = new LumiRemoteClient(
                settings.DeviceId,
                settings.DeviceName,
                new BrowserSameOriginHandler(
                    new Uri(host.FixedBaseUrl!),
                    new HttpClientHandler { AllowAutoRedirect = false }),
                downloads);
            var shell = new MobileShellViewModel(
                client,
                new BrowserDiscoveryClient(),
                store);
            BrowserInterop.SetTheme(shell.Theme.ToString());
            shell.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MobileShellViewModel.Theme))
                    BrowserInterop.SetTheme(shell.Theme.ToString());
                else if (e.PropertyName is nameof(MobileShellViewModel.IsGatewaySignInRequired)
                    or nameof(MobileShellViewModel.CanAutomaticallyRecoverGatewaySignIn)
                    && shell.IsGatewaySignInRequired
                    && shell.CanAutomaticallyRecoverGatewaySignIn)
                {
                    BrowserInterop.RecoverGatewaySignIn();
                }
                else if (e.PropertyName == nameof(MobileShellViewModel.IsConnected) && shell.IsConnected)
                {
                    BrowserInterop.ConfirmGatewaySignIn();
                    BrowserInterop.CheckAppUpdate();
                }
                else if (e.PropertyName == nameof(MobileShellViewModel.CanApplyWebAppUpdate))
                {
                    BrowserInterop.TryApplyAppUpdate();
                }
            };
            return shell;
        };

        await AppBuilder.Configure<App>()
            .WithInterFont()
            .With(new SkiaOptions
            {
                // Keep high-DPI glass intermediates reusable instead of recreating GPU textures each frame.
                MaxGpuResourceSizeBytes = 64 * 1024 * 1024
            })
            .With(new FontManagerOptions
            {
                FontFallbacks = new[]
                {
                    "Noto Sans Hebrew", "Noto Sans Arabic", "Noto Color Emoji",
                    Strings.Language switch
                    {
                        "ja" => "Noto Sans JP",
                        "ko" => "Noto Sans KR",
                        _ => "Noto Sans SC"
                    },
                    "Noto Sans SC", "Noto Sans JP", "Noto Sans KR"
                }.Distinct(StringComparer.Ordinal).Select(family => new FontFallback
                {
                    FontFamily = new FontFamily(
                        (family is "Noto Sans Hebrew" or "Noto Color Emoji"
                            ? "avares://Lumi.Mobile/Assets/Fonts#"
                            : "avares://Lumi.Mobile.Browser/Assets/Fonts#") + family)
                }).ToArray()
            })
            .StartBrowserAppAsync(
                "out",
                new BrowserPlatformOptions
                {
                    RenderingMode =
                    [
                        BrowserRenderingMode.WebGL2,
                        BrowserRenderingMode.WebGL1,
                        BrowserRenderingMode.Software2D
                    ],
                    RegisterAvaloniaServiceWorker = false,
                    PreferFileDialogPolyfill = true
                });

        // Resolve Avalonia's lazy storage import before a tap; no folder is opened or permission requested.
        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime { MainView: { } view }
            && TopLevel.GetTopLevel(view)?.StorageProvider is { } storage)
        {
            try
            {
                using var folder = await storage.TryGetWellKnownFolderAsync(WellKnownFolder.Documents);
            }
            catch (JSException error)
            {
                Console.Error.WriteLine($"[Lumi PWA] The attachment picker could not be initialized: {error}");
            }
        }
        else
        {
            Console.Error.WriteLine("[Lumi PWA] The browser storage provider was unavailable during picker initialization.");
        }
    }
}
