using Android.App;
using Android.OS;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Avalonia.Media;
using Lumi.Mobile;
using Lumi.Mobile.Services;

namespace Lumi.Mobile.Android;

/// <summary>
/// The Android application object. Avalonia 12 builds the app here rather than in the activity, so
/// this is where the shared <see cref="App"/> is configured.
/// </summary>
[Application(
    Label = "Lumi",
    Icon = "@mipmap/ic_launcher",
    RoundIcon = "@mipmap/ic_launcher_round",
    AllowBackup = false,
    SupportsRtl = true,
    UsesCleartextTraffic = true)]
public class MainApplication : AvaloniaAndroidApplication<App>
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
        // Before anything else: a crash during Avalonia startup is exactly the one we cannot see.
        RemotePlatformServices.RouteVerifier = new AndroidRemoteRouteVerifier(this);
        MobilePlatformServices.NetworkMonitor = new AndroidNetworkMonitor(this);
        MobilePlatformServices.ProducedFileOpener = new AndroidProducedFileOpener(this);
        MobilePlatformServices.TextShare = new AndroidTextShareService(this);
        MobilePlatformServices.DeviceNameOverride = ResolveDeviceName();
        System.Diagnostics.Trace.Listeners.Add(new LogcatTraceListener());
        CrashReporter.Install();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();

    private static string ResolveDeviceName()
    {
        var model = Build.Model?.Trim();
        return string.IsNullOrWhiteSpace(model)
            || model.StartsWith("sdk_", StringComparison.OrdinalIgnoreCase)
            ? "Android phone"
            : model;
    }
}
