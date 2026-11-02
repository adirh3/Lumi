using Android.App;
using Android.Content;
using Android.Content.PM;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Lumi.Mobile.Services;
using AndroidSettings = Android.Provider.Settings;

namespace Lumi.Mobile.Android.Notifications;

/// <summary>
/// Android's side of notifications for the shared app: the notification permission (Android 13+),
/// starting and stopping <see cref="LumiNotificationService"/>, and each channel's own settings.
/// </summary>
internal sealed class AndroidPhoneNotificationPlatform : IPhoneNotificationPlatform
{
    internal const int PermissionRequestCode = 47655;
    private const string PostNotificationsPermission = "android.permission.POST_NOTIFICATIONS";
    private const string Tag = "LumiNotifications";
    private static PhoneNotificationLedger? _ledger;

    // The application object is not attached yet when this is built, so Android is asked lazily.
    private readonly Context _context;
    private TaskCompletionSource<bool>? _permission;

    public AndroidPhoneNotificationPlatform(Context context)
    {
        _context = context;
        LumiNotificationService.StateChanged += () => LinkStateChanged?.Invoke();
    }

    /// <summary>What this phone has shown, shared by the service and the app in their one process.</summary>
    public static PhoneNotificationLedger Ledger =>
        LazyInitializer.EnsureInitialized(ref _ledger, static () => new PhoneNotificationLedger(PhoneNotificationLedger.DefaultDirectory));

    public bool IsAvailable => true;

    public bool AreNotificationsAllowed => NotificationManagerCompat.From(_context)!.AreNotificationsEnabled();

    public Task<bool> RequestPermissionAsync()
    {
        if (AreNotificationsAllowed)
            return Task.FromResult(true);

        // Before Android 13 the switch is the user's own, in the system settings.
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)
            || MainActivity.Current is not { } activity
            || activity.CheckSelfPermission(PostNotificationsPermission) == Permission.Granted)
        {
            return Task.FromResult(false);
        }

        var permission = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _permission, permission)?.TrySetResult(false);
        activity.RequestPermissions([PostNotificationsPermission], PermissionRequestCode);
        return permission.Task;
    }

    internal void OnPermissionResult() =>
        Interlocked.Exchange(ref _permission, null)?.TrySetResult(AreNotificationsAllowed);

    public void Apply(bool enabled)
    {
        if (enabled)
        {
            try
            {
                ContextCompat.StartForegroundService(_context, LumiNotificationService.StartIntent(_context));
            }
            catch (Exception ex)
            {
                // Android 12+ refuses while Lumi is in the background; the next resume tries again.
                global::Android.Util.Log.Warn(Tag, $"Could not start notifications: {ex.Message}");
            }

            return;
        }

        _context.StopService(new Intent(_context, typeof(LumiNotificationService)));
        Cancel(Ledger.TakeAll());
    }

    public PhoneNotificationLinkState LinkState => LumiNotificationService.State;

    public event Action? LinkStateChanged;

    public bool HasChannelSettings => true;

    public bool IsChannelEnabled(PhoneNotificationChannel channel)
    {
        var manager = NotificationManagerCompat.From(_context)!;
        var existing = manager.GetNotificationChannel(LumiNotificationChannels.For(channel));
        if (existing is null)
        {
            LumiNotificationChannels.Ensure(_context);
            existing = manager.GetNotificationChannel(LumiNotificationChannels.For(channel));
        }

        return existing is null || existing.Importance != NotificationImportance.None;
    }

    public void OpenSettings(PhoneNotificationChannel? channel)
    {
        LumiNotificationChannels.Ensure(_context);
        var intent = channel is { } kind
            ? new Intent(AndroidSettings.ActionChannelNotificationSettings)
                .PutExtra(AndroidSettings.ExtraAppPackage, _context.PackageName)!
                .PutExtra(AndroidSettings.ExtraChannelId, LumiNotificationChannels.For(kind))!
            : new Intent(AndroidSettings.ActionAppNotificationSettings)
                .PutExtra(AndroidSettings.ExtraAppPackage, _context.PackageName)!;
        intent.AddFlags(ActivityFlags.NewTask);
        (MainActivity.Current ?? _context).StartActivity(intent);
    }

    public void ClearChat(Guid chatId) => Cancel(Ledger.TakeChat(chatId));

    private void Cancel(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0)
            return;

        var manager = NotificationManagerCompat.From(_context)!;
        foreach (var id in ids)
            manager.Cancel(id, AndroidNotificationPresenter.ChatNotificationId);
    }
}

/// <summary>Brings notifications back after the phone restarts or Lumi is updated.</summary>
[BroadcastReceiver(Name = "com.lumi.mobile.NotificationRestartReceiver", Exported = true, Enabled = true)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced])]
public sealed class NotificationRestartReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null
            || intent?.Action is not (Intent.ActionBootCompleted or Intent.ActionMyPackageReplaced)
            || !LumiNotificationService.CanRun(context, new MobileSettingsStore().Load()))
        {
            return;
        }

        try
        {
            ContextCompat.StartForegroundService(context, LumiNotificationService.StartIntent(context));
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("LumiNotifications", $"Could not restart notifications: {ex.Message}");
        }
    }
}
