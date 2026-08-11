using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Content;
using AndroidX.Core.Util;
using AndroidX.Window.Java.Layout;
using AndroidX.Window.Layout;
using Avalonia.Android;
using Avalonia.Controls.ApplicationLifetimes;
using Java.Interop;
using Java.Util.Concurrent;
using Lumi.Mobile.Layout;
using Lumi.Mobile.Views;
using Object = Java.Lang.Object;

namespace Lumi.Mobile.Android;

/// <summary>
/// The Android entry point. Everything visible lives in the shared <c>Lumi.Mobile</c> library; this
/// activity only wires up the platform bits a phone needs and that a desktop window gets for free:
/// edge-to-edge drawing so Lumi paints under the status and gesture bars, and a resizing soft
/// keyboard so the composer stays above the IME instead of being covered by it.
/// </summary>
[Activity(
    Label = "Lumi",
    Icon = "@mipmap/ic_launcher",
    RoundIcon = "@mipmap/ic_launcher_round",
    Theme = "@style/MyTheme.NoActionBar",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ScreenOrientation = ScreenOrientation.FullUser,
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.UiMode
        | ConfigChanges.Density
        | ConfigChanges.KeyboardHidden)]
public class MainActivity : AvaloniaMainActivity, IConsumer
{
    private const string FoldableTag = "LumiFoldable";
    private const string LocalNetworkPermission = "android.permission.ACCESS_LOCAL_NETWORK";
    private const int LocalNetworkPermissionRequestCode = 47653;

    private WindowInfoTrackerCallbackAdapter? _windowInfoTracker;
    private IExecutor? _windowLayoutExecutor;
    private bool _windowLayoutListenerRegistered;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Draw behind the system bars. The shared shell already reserves the safe-area insets that
        // Avalonia reports, so the content stays clear of the notch and the gesture pill while the
        // background still bleeds to the edges — which is what makes it look like a real phone app.
        // Only needed on 30–34: below 30 the API does not exist, and from 35 edge-to-edge is the
        // platform default and this call is obsolete.
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && !OperatingSystem.IsAndroidVersionAtLeast(35))
            Window?.SetDecorFitsSystemWindows(false);

        base.OnCreate(savedInstanceState);

        if (OperatingSystem.IsAndroidVersionAtLeast(37)
            && CheckSelfPermission(LocalNetworkPermission) != Permission.Granted)
        {
            RequestPermissions([LocalNetworkPermission], LocalNetworkPermissionRequestCode);
        }

        try
        {
            // Avalonia.Android already brings in AndroidX WindowManager itself. The WindowJava
            // companion exposes its lifecycle-friendly callback adapter without making us collect a
            // Kotlin Flow from C#.
            _windowInfoTracker = new WindowInfoTrackerCallbackAdapter(WindowInfoTracker.GetOrCreate(this));
            _windowLayoutExecutor = ContextCompat.GetMainExecutor(this);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(FoldableTag, $"WindowManager initialization failed: {ex}");
            DisposeWindowLayoutTracking();
            PublishFoldLayout(FoldPosture.Flat, 0, 0);
        }
    }

    protected override void OnStart()
    {
        base.OnStart();

        if (_windowLayoutListenerRegistered
            || _windowInfoTracker is null
            || _windowLayoutExecutor is null)
        {
            return;
        }

        try
        {
            _windowInfoTracker.AddWindowLayoutInfoListener(this, _windowLayoutExecutor, this);
            _windowLayoutListenerRegistered = true;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(FoldableTag, $"Could not observe window layout: {ex}");
            PublishFoldLayout(FoldPosture.Flat, 0, 0);
        }
    }

    protected override void OnPause()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime
            {
                MainView: MobileShellView shell
            })
        {
            shell.NotifyApplicationDeactivated();
        }

        base.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (Avalonia.Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime
            {
                MainView: MobileShellView shell
            })
        {
            shell.NotifyApplicationActivated();
        }
    }

    public override void OnRequestPermissionsResult(
        int requestCode,
        string[] permissions,
        Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == LocalNetworkPermissionRequestCode
            && (grantResults.Length == 0 || grantResults[0] != Permission.Granted))
        {
            Toast.MakeText(
                    this,
                    "Local network access is required to connect to your Lumi PC.",
                    ToastLength.Long)
                ?.Show();
        }
    }

    protected override void OnStop()
    {
        RemoveWindowLayoutListener();
        base.OnStop();
    }

    protected override void OnDestroy()
    {
        DisposeWindowLayoutTracking();
        base.OnDestroy();
    }

    public void Accept(Object? value)
    {
        if (value is not WindowLayoutInfo layoutInfo)
        {
            PublishFoldLayout(FoldPosture.Flat, 0, 0);
            return;
        }

        try
        {
            ApplyWindowLayout(layoutInfo);
        }
        catch (Exception ex)
        {
            // A vendor WindowManager implementation must never make an ordinary phone fail to start.
            global::Android.Util.Log.Warn(FoldableTag, $"Could not read folding feature: {ex}");
            PublishFoldLayout(FoldPosture.Flat, 0, 0);
        }
    }

    private void ApplyWindowLayout(WindowLayoutInfo layoutInfo)
    {
        IFoldingFeature? foldingFeature = null;

        foreach (var displayFeature in layoutInfo.DisplayFeatures)
        {
            IFoldingFeature? candidate;
            try
            {
                // IFoldingFeature is a Java interface, and the generated binding does not reliably
                // support a normal C# type test. JavaCast is required, but a future non-folding
                // DisplayFeature must not prevent us from examining the rest of the list.
                candidate = displayFeature.JavaCast<IFoldingFeature>();
            }
            catch (InvalidCastException)
            {
                continue;
            }

            if (candidate is null)
                continue;

            foldingFeature = candidate;

            // Prefer the feature that actually divides the window if a vendor reports more than one.
            if (candidate.IsSeparating || candidate.State.Equals(FoldingFeatureState.HalfOpened))
                break;
        }

        if (foldingFeature is null)
        {
            PublishFoldLayout(FoldPosture.Flat, 0, 0);
            return;
        }

        var isVertical = foldingFeature.Orientation.Equals(FoldingFeatureOrientation.Vertical);
        var isHorizontal = foldingFeature.Orientation.Equals(FoldingFeatureOrientation.Horizontal);
        var isHalfOpened = foldingFeature.State.Equals(FoldingFeatureState.HalfOpened);
        var separatesContent = foldingFeature.IsSeparating || isHalfOpened;

        var posture = separatesContent switch
        {
            true when isVertical => FoldPosture.BookVerticalHinge,
            true when isHorizontal => FoldPosture.TabletopHorizontalHinge,
            _ => FoldPosture.Flat
        };

        if (posture == FoldPosture.Flat)
        {
            PublishFoldLayout(posture, 0, 0);
            return;
        }

        // WindowManager reports physical pixels; MobileShellView and MobileLayoutState use DIPs.
        var density = Resources?.DisplayMetrics?.Density ?? 1f;
        if (!float.IsFinite(density) || density <= 0)
            density = 1;

        var bounds = foldingFeature.Bounds;
        var hingeSizePixels = isVertical
            ? Math.Max(0, bounds.Right - bounds.Left)
            : Math.Max(0, bounds.Bottom - bounds.Top);
        var hingePositionPixels = isVertical ? bounds.Left : bounds.Top;

        PublishFoldLayout(
            posture,
            hingeSizePixels / density,
            Math.Max(0, hingePositionPixels) / density);
    }

    private void RemoveWindowLayoutListener()
    {
        if (!_windowLayoutListenerRegistered || _windowInfoTracker is null)
            return;

        try
        {
            _windowInfoTracker.RemoveWindowLayoutInfoListener(this);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(FoldableTag, $"Could not stop observing window layout: {ex}");
        }
        finally
        {
            _windowLayoutListenerRegistered = false;
        }
    }

    private void DisposeWindowLayoutTracking()
    {
        RemoveWindowLayoutListener();

        _windowInfoTracker?.Dispose();
        _windowInfoTracker = null;

        _windowLayoutExecutor?.Dispose();
        _windowLayoutExecutor = null;
    }

    private static void PublishFoldLayout(FoldPosture posture, double hingeSize, double hingePosition)
    {
        if (global::Avalonia.Application.Current?.ApplicationLifetime
                is not ISingleViewApplicationLifetime { MainView: MobileShellView shell })
        {
            return;
        }

        // Set the geometry first and posture last. All three changes run on Android's main executor,
        // so Avalonia sees one final coherent state before the next render.
        shell.HingeSize = hingeSize;
        shell.HingePosition = hingePosition;
        shell.Posture = posture;
    }
}
