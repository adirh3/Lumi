using Android.App;
using Android.Content;
using Android.Graphics;
using AndroidX.Core.App;
using Lumi.Mobile.Services;
using Lumi.Remote.Protocol;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.Android.Notifications;

/// <summary>
/// The kinds of notification the user can silence on their own in Android's settings: what needs an
/// answer is loud, a finished reply makes a sound, and the connection that brings both is silent.
/// </summary>
internal static class LumiNotificationChannels
{
    public const string Questions = "lumi.questions";
    public const string Replies = "lumi.replies";
    public const string Connection = "lumi.connection";

    /// <summary>Creates the channels, or renames them in the current language. Cheap and idempotent.</summary>
    public static void Ensure(Context context)
    {
        if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager)
            return;

        var questions = new NotificationChannel(Questions, Strings.Get("NotificationChannelQuestions"), NotificationImportance.High)
        {
            Description = Strings.Get("NotificationChannelQuestionsDescription")
        };
        questions.EnableVibration(true);
        questions.SetShowBadge(true);

        var replies = new NotificationChannel(Replies, Strings.Get("NotificationChannelReplies"), NotificationImportance.Default)
        {
            Description = Strings.Get("NotificationChannelRepliesDescription")
        };
        replies.SetShowBadge(true);

        var connection = new NotificationChannel(Connection, Strings.Get("NotificationChannelConnection"), NotificationImportance.Min)
        {
            Description = Strings.Get("NotificationChannelConnectionDescription")
        };
        connection.SetShowBadge(false);
        connection.EnableVibration(false);
        connection.SetSound(null, null);

        manager.CreateNotificationChannels([questions, replies, connection]);
    }

    public static string For(PhoneNotificationChannel channel) => channel switch
    {
        PhoneNotificationChannel.Questions => Questions,
        PhoneNotificationChannel.Replies => Replies,
        _ => Connection
    };

    public static string For(RemoteNotification notification) =>
        notification.Kind == RemoteProtocol.NotificationKinds.Reply ? Replies : Questions;
}

/// <summary>
/// Shows the PC's notifications as the web app does: the chat's title, then what Lumi (or the chat's
/// agent) said. No avatars: Android shows Lumi's own icon beside it. A tap opens the chat; answering
/// happens there.
/// </summary>
internal sealed class AndroidNotificationPresenter(Context context, Func<string> hostName) : IPhoneNotificationPresenter
{
    /// <summary>Every chat notification shares one id; its tag is the PC's notification id.</summary>
    public const int ChatNotificationId = 1;

    /// <summary>The foreground service's own, silent notification.</summary>
    public const int ConnectionNotificationId = 4242;

    private static readonly Color Accent = Color.ParseColor("#8B5CF6");

    public event Action<PhoneNotificationLinkState>? LinkChanged;

    public void Show(RemoteNotification notification, bool alert)
    {
        try
        {
            NotificationManagerCompat.From(context)!.Notify(notification.Id, ChatNotificationId, Build(notification, alert));
        }
        catch (Exception ex) when (ex is Java.Lang.SecurityException or Java.Lang.IllegalArgumentException)
        {
            global::Android.Util.Log.Warn("LumiNotifications", $"Could not show a notification: {ex.Message}");
        }
    }

    public void Dismiss(IReadOnlyCollection<string> ids)
    {
        var manager = NotificationManagerCompat.From(context)!;
        foreach (var id in ids)
            manager.Cancel(id, ChatNotificationId);
    }

    public void LinkStateChanged(PhoneNotificationLinkState state) => LinkChanged?.Invoke(state);

    private Notification Build(RemoteNotification notification, bool alert)
    {
        var host = hostName();
        var isTest = notification.Kind == RemoteProtocol.NotificationKinds.Test;
        var title = isTest
            ? Strings.Get("NotificationTestTitle")
            : string.IsNullOrWhiteSpace(notification.ChatTitle) ? "Lumi" : notification.ChatTitle;
        var words = MessageText(notification, host);
        var text = !isTest && !string.IsNullOrWhiteSpace(notification.AgentName)
            ? $"{notification.AgentName}: {words}"
            : words;
        var when = notification.CreatedAt == default
            ? Java.Lang.JavaSystem.CurrentTimeMillis()
            : notification.CreatedAt.ToUnixTimeMilliseconds();

        var isRequest = notification.Kind is RemoteProtocol.NotificationKinds.Question
            or RemoteProtocol.NotificationKinds.Input
            or RemoteProtocol.NotificationKinds.Approval;
        var channel = LumiNotificationChannels.For(notification);
        var publicVersion = new NotificationCompat.Builder(context, channel)
            .SetSmallIcon(Resource.Drawable.ic_stat_lumi)!
            .SetColor(Accent)!
            .SetContentTitle("Lumi")!
            .SetContentText(Strings.Get(isRequest ? "NotificationPublicRequest" : "NotificationPublicReply"))!
            .Build();

        var builder = new NotificationCompat.Builder(context, channel)
            .SetSmallIcon(Resource.Drawable.ic_stat_lumi)!
            .SetColor(Accent)!
            .SetContentTitle(title)!
            .SetContentText(text)!
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(text))!
            .SetCategory(NotificationCompat.CategoryMessage)!
            .SetPriority(isRequest || isTest ? NotificationCompat.PriorityHigh : NotificationCompat.PriorityDefault)!
            .SetWhen(when)!
            .SetShowWhen(true)!
            .SetAutoCancel(true)!
            .SetSilent(!alert)!
            .SetVisibility(NotificationCompat.VisibilityPrivate)!
            .SetPublicVersion(publicVersion)!
            .SetTimeoutAfter((long)TimeSpan.FromHours(24).TotalMilliseconds)!
            .SetContentIntent(!isTest && notification.ChatId != Guid.Empty
                ? OpenChatIntent(notification)
                : OpenAppIntent())!;

        return builder.Build()!;
    }

    /// <summary>What the notification says: the words, or what Lumi waits for.</summary>
    private static string MessageText(RemoteNotification notification, string host) => notification.Kind switch
    {
        RemoteProtocol.NotificationKinds.Test => Strings.Get("NotificationTestText", host),
        RemoteProtocol.NotificationKinds.Input => Strings.Get("NotificationInputText", notification.Text),
        RemoteProtocol.NotificationKinds.Approval => Strings.Get("NotificationApprovalText", host, notification.Text),
        RemoteProtocol.NotificationKinds.Reply when string.IsNullOrWhiteSpace(notification.Text) =>
            Strings.Get("NotificationReplyFallback"),
        _ => notification.Text
    };

    private PendingIntent OpenChatIntent(RemoteNotification notification)
    {
        var intent = new Intent(context, typeof(MainActivity))
            .SetAction(MainActivity.OpenChatAction)!
            .PutExtra(MainActivity.ChatIdExtra, notification.ChatId.ToString("D"))!
            .PutExtra(MainActivity.ChatTitleExtra, notification.ChatTitle)!
            .AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop)!;
        return PendingIntent.GetActivity(
            context,
            RequestCode(notification.Id),
            intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    private PendingIntent OpenAppIntent() => PendingIntent.GetActivity(
        context,
        0,
        new Intent(context, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop),
        PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;

    /// <summary>A stable request code per notification, so each one's tap opens its own chat.</summary>
    private static int RequestCode(string id)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var character in id)
                hash = (hash ^ character) * 16777619u;
            return (int)(hash & 0x7FFFFFFF);
        }
    }

    /// <summary>The foreground service's silent notice: whether Lumi is listening to the PC now.</summary>
    public Notification BuildConnection(PhoneNotificationLinkState state)
    {
        var host = hostName();
        var text = state switch
        {
            PhoneNotificationLinkState.Connected => Strings.Get("NotificationConnected", host),
            PhoneNotificationLinkState.Connecting => Strings.Get("NotificationConnecting", host),
            PhoneNotificationLinkState.Unsupported => Strings.Get("NotificationsPcUpdateNeeded", host),
            _ => Strings.Get("NotificationWaiting", host)
        };
        return new NotificationCompat.Builder(context, LumiNotificationChannels.Connection)
            .SetSmallIcon(Resource.Drawable.ic_stat_lumi)!
            .SetColor(Accent)!
            .SetContentTitle(text)!
            .SetOngoing(true)!
            .SetSilent(true)!
            .SetShowWhen(false)!
            .SetCategory(NotificationCompat.CategoryService)!
            .SetPriority(NotificationCompat.PriorityMin)!
            .SetVisibility(NotificationCompat.VisibilityPublic)!
            .SetContentIntent(OpenAppIntent())!
            .Build()!;
    }
}
