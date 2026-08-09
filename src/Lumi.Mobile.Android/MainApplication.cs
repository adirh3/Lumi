using Android.App;
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
        CrashReporter.Install();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();
}
