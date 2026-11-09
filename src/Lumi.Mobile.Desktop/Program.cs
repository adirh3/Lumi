using Avalonia;
using Avalonia.Controls;
using Lumi.Mobile;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;
#if DEBUG
using AvaloniaMcp.Diagnostics;
#endif

namespace Lumi.Mobile.Desktop;

/// <summary>
/// Development / test head for the Lumi mobile app. Hosts the exact same shared UI the phone heads
/// host, inside a device simulator so every form factor is reachable without an emulator.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
#if DEBUG || LUMI_MOBILE_SMOOTHNESS
        var smoothness = args.Contains("--mobile-smoothness");
        if (smoothness)
            App.ShellFactory = Lumi.Mobile.Smoothness.MobileSmoothnessFixture.CreateShell;
#else
        if (args.Contains("--mobile-smoothness"))
        {
            Console.Error.WriteLine("Build with -p:LumiMobileSmoothness=true to run an optimized mobile benchmark.");
            return 1;
        }
#endif
        // An isolated data dir keeps simulator runs from clobbering a real phone pairing on this box.
        if (args.Contains("--isolated") || args.Contains("--popup-preview") || args.Contains("--design-preview")
            || args.Contains("--install-preview"))
        {
            var dir = Path.Combine(Path.GetTempPath(), "Lumi-mobile-sim", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("LUMI_MOBILE_DATA_DIR", dir);
        }

        // Set culture before the builder loads App/theme XAML, not only before the shell's views.
#if DEBUG || LUMI_MOBILE_SMOOTHNESS
        if (smoothness)
            Strings.Load("en");
        else
#endif
        {
            var settingsStore = new MobileSettingsStore();
            var settings = settingsStore.Load();
            if (args.Contains("--install-preview") && ReadOption(args, "--language") is { } previewLanguage)
            {
                settings.Language = previewLanguage;
                settingsStore.Save(settings);
            }
            Strings.Load(settings.Language);
        }
        var device = ReadOption(args, "--device");
        var host = ReadOption(args, "--host");
#if DEBUG
        var interactiveProfile = ReadOption(args, "--interactive-controls");
        var popupPreview = args.Contains("--popup-preview");
        var designPreview = args.Contains("--design-preview");
        var designFile = ReadOption(args, "--open-file");
        var installPreview = ReadOption(args, "--install-preview");
        if (installPreview is not null)
            MobilePlatformServices.HostEnvironment = new InstallPreviewHost(installPreview);
        if (args.Contains("--notifications") || args.Contains("--notifications-denied"))
            MobilePlatformServices.Notifications = new SimulatedNotificationPlatform(args.Contains("--notifications-denied"));
#endif

        App.DesktopWindowFactory = () =>
        {
            var window = new SimulatorWindow();
            if (device is { Length: > 0 })
                window.Opened += (_, _) => window.SelectDevice(device);
#if DEBUG || LUMI_MOBILE_SMOOTHNESS
            if (smoothness)
            {
                var port = int.Parse(ReadOption(args, "--smoothness-port") ?? "47692");
                window.Opened += (_, _) =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        var view = window.FindControl<Lumi.Mobile.Views.MobileShellView>("Shell")!;
                        var diagnostics = new Lumi.Mobile.Smoothness.SmoothnessSocketHost(view, port);
                        window.Closed += (_, _) => diagnostics.Dispose();
                    }, Avalonia.Threading.DispatcherPriority.Loaded);
            }
#endif
#if DEBUG
            if (interactiveProfile is { Length: > 0 })
                window.Opened += (_, _) => window.OpenInteractiveControls(interactiveProfile);
            if (popupPreview)
                window.Opened += (_, _) => window.OpenPopupPreview();
            if (designPreview)
                window.Opened += (_, _) => window.OpenDesignPreview(designFile);
            if (installPreview is not null)
                window.Opened += (_, _) =>
                {
                    window.OpenDesignPreview();
                    if (window.DataContext is MobileShellViewModel shell)
                        shell.OfferPhoneSetupIfUseful();
                };
#endif
            return window;
        };

        if (host is { Length: > 0 })
        {
#if DEBUG || LUMI_MOBILE_SMOOTHNESS
            if (smoothness)
                throw new ArgumentException("A benchmark cannot connect to a real PC.");
#endif
            App.ShellFactory = () =>
            {
                var shell = new MobileShellViewModel();
                shell.Connect.ManualAddress = host;
                return shell;
            };
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 64 * 1024 * 1024 })
            .LogToTrace();

#if DEBUG
        builder = builder.UseMcpDiagnostics();
#endif

        return builder;
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

#if DEBUG
    private sealed class InstallPreviewHost(string mode) : IMobileHostEnvironment
    {
        public bool HasFixedEndpoint => false;
        public string? FixedBaseUrl => null;
        public string FixedEndpointName => "";
        public WebAppInstallMode WebAppInstallMode { get; private set; } = mode switch
        {
            "apple" => WebAppInstallMode.Apple,
            "android" => WebAppInstallMode.Android,
            "prompt" => WebAppInstallMode.NativePrompt,
            "installed" => WebAppInstallMode.Unavailable,
            _ => throw new ArgumentException("Use apple, android, prompt or installed for --install-preview.")
        };

        public Task<WebAppInstallResult> InstallWebAppAsync()
        {
            WebAppInstallMode = WebAppInstallMode.Unavailable;
            return Task.FromResult(WebAppInstallResult.Accepted);
        }
    }
#endif
}
