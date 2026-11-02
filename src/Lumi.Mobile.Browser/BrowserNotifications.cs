using System.Text;
using System.Text.Json;
using Lumi.Mobile.Services;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.Browser;

/// <summary>
/// Notifications in the browser: the PC pushes them through the browser's own push service (Web Push)
/// and the service worker shows them, even with Lumi closed. Nothing runs in the background here. The
/// page asks for permission, subscribes with the PC's key, and leaves the words notifications use, in
/// the app's language, where the service worker finds them.
/// </summary>
internal sealed class BrowserPhoneNotificationPlatform : IPhoneNotificationPlatform
{
    public bool IsAvailable => true;

    public bool AreNotificationsAllowed => BrowserInterop.GetNotificationPermission() == "granted";

    // "default": nothing has refused yet, so a tap can still bring up the browser's prompt.
    public bool CanRequestPermission => BrowserInterop.GetNotificationPermission() == "default";

    public bool IsInstalledApp => BrowserInterop.IsInstalledApp();

    public PhoneNotificationSupport Support => BrowserInterop.GetNotificationSupport() switch
    {
        "supported" => PhoneNotificationSupport.Supported,
        "insecure" => PhoneNotificationSupport.NeedsSecureAddress,
        "home-screen" => PhoneNotificationSupport.NeedsHomeScreen,
        _ => PhoneNotificationSupport.Unsupported
    };

    public bool UsesWebPush => true;

    // The browser asks only in answer to a tap, so the request starts before anything is awaited.
    public async Task<bool> RequestPermissionAsync() =>
        await BrowserInterop.RequestNotificationPermission() == "granted";

    public async Task<WebPushSubscriptionKeys> SubscribeAsync(string serverKey)
    {
        var json = await BrowserInterop.SubscribeToPush(serverKey, NotificationText());
        using var subscription = JsonDocument.Parse(json);
        var root = subscription.RootElement;
        return new WebPushSubscriptionKeys(
            root.GetProperty("endpoint").GetString() ?? "",
            root.GetProperty("p256dh").GetString() ?? "",
            root.GetProperty("auth").GetString() ?? "");
    }

    public Task UnsubscribeAsync() => BrowserInterop.UnsubscribeFromPush();

    public async Task<bool?> ExpectTestNotificationAsync(TimeSpan timeout) =>
        await BrowserInterop.ExpectTestPush((int)timeout.TotalMilliseconds);

    public void ClearChat(Guid chatId) => BrowserInterop.ClearChatNotifications(chatId.ToString("D"));

    // There is no connection to keep: the PC pushes.
    public void Apply(bool enabled)
    {
    }

    public PhoneNotificationLinkState LinkState => PhoneNotificationLinkState.Stopped;

    public event Action? LinkStateChanged
    {
        add { }
        remove { }
    }

    public bool HasChannelSettings => false;

    public bool IsChannelEnabled(PhoneNotificationChannel channel) => true;

    public bool CanOpenSettings => false;

    public void OpenSettings(PhoneNotificationChannel? channel)
    {
    }

    /// <summary>The words a notification uses, in the app's language, for the service worker.</summary>
    private static string NotificationText()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("lang", Strings.Language);
            writer.WriteString("testTitle", Strings.Get("NotificationTestTitle"));
            writer.WriteString("testText", Strings.Get("NotificationTestText"));
            writer.WriteString("inputText", Strings.Get("NotificationInputText"));
            writer.WriteString("approvalText", Strings.Get("NotificationApprovalText"));
            writer.WriteString("replyFallback", Strings.Get("NotificationReplyFallback"));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
