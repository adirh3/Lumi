using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Content;
using AndroidX.Core.Util;
using AndroidX.Core.View;
using AndroidX.Window.Java.Layout;
using AndroidX.Window.Layout;
using Avalonia.Android;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Java.Interop;
using Java.Util.Concurrent;
using Lumi.Mobile.Layout;
using Lumi.Mobile.Services;
using Lumi.Mobile.Views;
using Object = Java.Lang.Object;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.Android;

/// <summary>
/// The Android entry point. Everything visible lives in the shared <c>Lumi.Mobile</c> library; this
/// activity only wires up the platform bits a phone needs and that a desktop window gets for free:
/// edge-to-edge drawing so Lumi paints under the status and gesture bars, and a resizing soft
/// keyboard so the composer stays above the IME instead of being covered by it.
/// </summary>
[Activity(
    Label = LumiAppLabel.Text,
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
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "lumi",
    DataHost = "connect")]
public class MainActivity : AvaloniaMainActivity, IConsumer
{
    /// <summary>A notification's tap: open the chat it is about.</summary>
    internal const string OpenChatAction = "com.lumi.mobile.OPEN_CHAT";
    internal const string ChatIdExtra = "lumi.notification.chatId";
    internal const string ChatTitleExtra = "lumi.notification.chatTitle";

    private const string FoldableTag = "LumiFoldable";
    private const string LocalNetworkPermission = "android.permission.ACCESS_LOCAL_NETWORK";
    private const int LocalNetworkPermissionRequestCode = 47653;

    /// <summary>The activity on screen, for requests that need one (the notification permission).</summary>
    internal static MainActivity? Current { get; private set; }

    private WindowInfoTrackerCallbackAdapter? _windowInfoTracker;
    private IExecutor? _windowLayoutExecutor;
    private AndroidNativeComposerEditorFactory? _nativeComposerEditorFactory;
    private AndroidTextSelectionPresenter? _textSelectionPresenter;
    private GestureDetector? _textSelectionGestureDetector;
    private bool _windowLayoutListenerRegistered;
#if LUMI_MOBILE_SMOOTHNESS
    private Lumi.Mobile.Smoothness.SmoothnessSocketHost? _smoothnessHost;
#endif

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Avalonia draws in dp and never reads the phone's font size, so Lumi restates its type scale
        // the way Android sizes its own text. A change of the setting recreates the activity, which
        // comes back through here.
        if (Avalonia.Application.Current is { } application)
            MobileTypeScale.Apply(application, SpToDips);

        TryGetConnectionRequest(Intent, out var launchRequest);

        _nativeComposerEditorFactory =
            new AndroidNativeComposerEditorFactory(this);
        MobilePlatformServices.NativeComposerEditorFactory =
            _nativeComposerEditorFactory;

        // Draw behind the system bars. The shared shell already reserves the safe-area insets that
        // Avalonia reports, so the content stays clear of the notch and the gesture pill while the
        // background still bleeds to the edges — which is what makes it look like a real phone app.
        // Only needed on 30–34: below 30 the API does not exist, and from 35 edge-to-edge is the
        // platform default and this call is obsolete.
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && !OperatingSystem.IsAndroidVersionAtLeast(35))
            Window?.SetDecorFitsSystemWindows(false);

        base.OnCreate(savedInstanceState);
        Current = this;
#if LUMI_MOBILE_SMOOTHNESS
        var benchmarkAttempts = 0;
        DispatcherTimer.Run(() =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime
                { MainView: MobileShellView view } && Avalonia.Controls.TopLevel.GetTopLevel(view) is not null
                && _nativeComposerEditorFactory is { } editorFactory)
            {
                _smoothnessHost = new(view, 47692, () => SystemClock.ElapsedRealtimeNanos() / 1_000_000d,
                    softKeyboardExpected: true,
                    nativeEditorHitTest: editorFactory.CanReceiveSmoothnessTouch);
                return false;
            }
            if (++benchmarkAttempts < 75)
                return true;
            global::Android.Util.Log.Error("LumiSmoothness", "The benchmark view did not initialize.");
            return false;
        }, TimeSpan.FromMilliseconds(100));
#endif
        if (launchRequest is not null)
            DispatchConnectionRequest(launchRequest);
        // A recreated activity keeps its launching intent; only a fresh launch opens the chat.
        if (savedInstanceState is null)
            DispatchNotificationIntent(Intent);
#if DEBUG
        // Debug builds open the design preview on request (adb shell am start ... --ez lumi.design_preview
        // true [--es lumi.theme Light] [--es lumi.open_file report.pdf] [--ez lumi.preview_conversation true]),
        // so the design is judged on Android's own renderer without a PC.
        if (Intent?.GetBooleanExtra("lumi.design_preview", false) == true)
        {
            var theme = Intent.GetStringExtra("lumi.theme");
            var file = Intent.GetStringExtra("lumi.open_file");
            var previewAttempts = 0;
            Avalonia.Threading.DispatcherTimer.Run(() =>
            {
                if (Avalonia.Application.Current is not App { Shell: { } shell })
                {
                    if (++previewAttempts < 75)
                        return true;
                    System.Diagnostics.Trace.TraceWarning("[MobileDebug] The design preview shell did not initialize.");
                    return false;
                }
                if (Enum.TryParse<Lumi.Mobile.ViewModels.MobileShellViewModel.ThemePreference>(
                        theme, ignoreCase: true, out var preference))
                    shell.Theme = preference;
                DesignPreview.Apply(shell);
                if (file is { Length: > 0 })
                    DesignPreview.OpenFile(shell, file);
                if (Intent?.GetBooleanExtra("lumi.preview_conversation", false) == true)
                    shell.CloseDrawerCommand.Execute(null);
                return false;
            }, TimeSpan.FromMilliseconds(200));
        }
#endif

        _textSelectionPresenter = new AndroidTextSelectionPresenter(this);
        MobilePlatformServices.TextSelectionPresenter = _textSelectionPresenter;
        _textSelectionGestureDetector = new GestureDetector(
            this,
            new TextSelectionGestureListener(
                () => Window?.DecorView?.PerformHapticFeedback(FeedbackConstants.LongPress)));
        AndroidImeAutocorrect.Install(this);

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

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (DispatchNotificationIntent(intent))
            return;
        if (!TryGetConnectionRequest(intent, out var request))
            return;

        DispatchConnectionRequest(request!);
    }

    /// <summary>Opens the chat a tapped notification was about. False for any other intent.</summary>
    private static bool DispatchNotificationIntent(Intent? intent)
    {
        if (intent?.Action != OpenChatAction
            || !Guid.TryParse(intent.GetStringExtra(ChatIdExtra), out var chatId))
        {
            return false;
        }

        var title = intent.GetStringExtra(ChatTitleExtra);
        Dispatcher.UIThread.Post(() =>
        {
            if (Avalonia.Application.Current is App { Shell: { } shell })
                shell.OpenChatFromNotification(chatId, title);
        });
        return true;
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
        AndroidImeAutocorrect.Refresh(this);
        Window?.DecorView?.Post(RefreshKeyboardInsets);
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus)
            Window?.DecorView?.Post(RefreshKeyboardInsets);
    }

    private void RefreshKeyboardInsets()
    {
        if (Window?.DecorView is not { HasWindowFocus: true } decor
            || Avalonia.Application.Current?.ApplicationLifetime is not ISingleViewApplicationLifetime
            {
                MainView: MobileShellView shell
            })
        {
            return;
        }

        ViewCompat.RequestApplyInsets(decor);
        if (ViewCompat.GetRootWindowInsets(decor) is not { } insets)
            return;

        var density = Resources?.DisplayMetrics?.Density ?? 1f;
        var keyboardInset = insets.IsVisible(WindowInsetsCompat.Type.Ime())
                            && insets.GetInsets(WindowInsetsCompat.Type.Ime()) is { } ime
            ? ime.Bottom / density
            : 0;
        shell.ApplyPlatformKeyboardInset(keyboardInset);
    }

    public override void OnRequestPermissionsResult(
        int requestCode,
        string[] permissions,
        Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == Notifications.AndroidPhoneNotificationPlatform.PermissionRequestCode
            && MobilePlatformServices.Notifications is Notifications.AndroidPhoneNotificationPlatform notifications)
        {
            notifications.OnPermissionResult();
            return;
        }

        if (requestCode == LocalNetworkPermissionRequestCode
            && (grantResults.Length == 0 || grantResults[0] != Permission.Granted))
        {
            Toast.MakeText(
                    this,
                    Strings.Get("LocalNetworkPermissionRequired"),
                    ToastLength.Long)
                ?.Show();
        }
    }

    private static bool TryGetConnectionRequest(
        Intent? intent,
        out MobileConnectionLaunchRequest? request) =>
        MobileConnectionLaunchRequest.TryParse(intent?.DataString, out request);

    /// <summary>
    /// A size in sp as dp, exactly as Android converts its own text: by the font size, and from Android
    /// 14 growing large text less than small text.
    /// </summary>
    private double SpToDips(double sp)
    {
        var metrics = Resources?.DisplayMetrics;
        if (metrics is null || !(metrics.Density > 0))
            return sp;

        return global::Android.Util.TypedValue.ApplyDimension(
                   global::Android.Util.ComplexUnitType.Sp, (float)sp, metrics)
               / metrics.Density;
    }

    private static void DispatchConnectionRequest(
        MobileConnectionLaunchRequest request) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (Avalonia.Application.Current is App { Shell: { } shell })
                _ = shell.HandleConnectionLaunchAsync(request);
        });

    protected override void OnStop()
    {
        RemoveWindowLayoutListener();
        base.OnStop();
    }

    protected override void OnDestroy()
    {
#if LUMI_MOBILE_SMOOTHNESS
        _smoothnessHost?.Dispose();
        _smoothnessHost = null;
#endif
        if (ReferenceEquals(Current, this))
            Current = null;
        MobilePlatformServices.ClearTextSelectionGesture();
        _textSelectionGestureDetector?.Dispose();
        _textSelectionGestureDetector = null;
        DisposeWindowLayoutTracking();
        if (_textSelectionPresenter is { } presenter)
        {
            MobilePlatformServices.ResetTextSelectionPresenter(presenter);
            presenter.Dismiss();
            _textSelectionPresenter = null;
        }
        var composerEditorFactory = _nativeComposerEditorFactory;
        _nativeComposerEditorFactory = null;
        try
        {
            base.OnDestroy();
        }
        finally
        {
            MobilePlatformServices.ResetNativeComposerEditorFactory(
                composerEditorFactory);
        }
    }

    public override bool DispatchTouchEvent(MotionEvent? motionEvent)
    {
#if LUMI_MOBILE_SMOOTHNESS
        if (motionEvent?.ActionMasked is MotionEventActions.Down or MotionEventActions.Up)
        {
            var density = Resources?.DisplayMetrics?.Density ?? 1f;
            _smoothnessHost?.ObserveHostTouch(motionEvent.GetX() / density, motionEvent.GetY() / density,
                motionEvent.ActionMasked == MotionEventActions.Down);
        }
#endif
        if (motionEvent?.ActionMasked == MotionEventActions.Down)
            MobilePlatformServices.ClearTextSelectionGesture();
        var handled = base.DispatchTouchEvent(motionEvent);
        if (motionEvent is not null)
            _textSelectionGestureDetector?.OnTouchEvent(motionEvent);
        if (motionEvent?.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel)
            MobilePlatformServices.ClearTextSelectionGesture();
        if (motionEvent?.ActionMasked == MotionEventActions.Up)
            AndroidImeAutocorrect.Refresh(this);
        return handled;
    }

#if LUMI_MOBILE_SMOOTHNESS
    internal void ObserveSmoothnessKeyboardRequest() => _smoothnessHost?.ObserveKeyboardRequest();
#endif

    public override bool DispatchKeyEvent(KeyEvent? keyEvent)
    {
        if (_nativeComposerEditorFactory?.TryDispatchKeyEvent(keyEvent) == true)
            return true;

        return base.DispatchKeyEvent(keyEvent);
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

    // Packaged builds need a public registered Java peer; a private nested listener is trimmed away.
    [global::Android.Runtime.Register("com/lumi/mobile/TextSelectionGestureListener")]
    public sealed class TextSelectionGestureListener
        : GestureDetector.SimpleOnGestureListener
    {
        private readonly Action _presented;

        public TextSelectionGestureListener(Action presented) => _presented = presented;

        public override bool OnDown(MotionEvent e) => true;

        public override void OnLongPress(MotionEvent e)
        {
            // A message's menu answers the press with the same tick as the platform's own menus.
            if (MobilePlatformServices.PresentTextSelectionGesture())
                _presented();
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
