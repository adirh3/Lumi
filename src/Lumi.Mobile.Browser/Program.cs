using Avalonia;
using Avalonia.Browser;
using Avalonia.Media;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using System.Runtime.InteropServices.JavaScript;

namespace Lumi.Mobile.Browser;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        await JSHost.ImportAsync("./browserHost.js", "/app/browserHost.js");
        var host = new BrowserHostEnvironment(BrowserInterop.GetOrigin());
        var nativeTextInputOverlayPresenter = new BrowserNativeTextInputOverlayPresenter();
        MobilePlatformServices.HostEnvironment = host;
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
            return new MobileShellViewModel(
                client,
                new BrowserDiscoveryClient(),
                store);
        };

        await AppBuilder.Configure<App>()
            .WithInterFont()
            .With(new FontManagerOptions
            {
                FontFallbacks =
                [
                    new FontFallback
                    {
                        FontFamily = new FontFamily(
                            "avares://Lumi.Mobile/Assets/Fonts#Noto Sans Hebrew"),
                        UnicodeRange = new UnicodeRange(0x0590, 0x05FF)
                    },
                    new FontFallback
                    {
                        FontFamily = new FontFamily(
                            "avares://Lumi.Mobile/Assets/Fonts#Noto Color Emoji")
                    }
                ]
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
    }
}
