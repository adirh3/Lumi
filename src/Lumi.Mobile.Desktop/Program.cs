using Avalonia;
using Avalonia.Controls;
using Lumi.Mobile;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
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
        // An isolated data dir keeps simulator runs from clobbering a real phone pairing on this box.
        if (args.Contains("--isolated"))
        {
            var dir = Path.Combine(Path.GetTempPath(), "Lumi-mobile-sim", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("LUMI_MOBILE_DATA_DIR", dir);
        }

        var device = ReadOption(args, "--device");
        var host = ReadOption(args, "--host");

        App.DesktopWindowFactory = () =>
        {
            var window = new SimulatorWindow();
            if (device is { Length: > 0 })
                window.Opened += (_, _) => window.SelectDevice(device);
            return window;
        };

        if (host is { Length: > 0 })
        {
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
}
