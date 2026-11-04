using System.Runtime.Versioning;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Graphics;
using AndroidX.Core.App;
using AndroidX.Core.Graphics.Drawable;
using Lumi.Mobile.Services;
using Lumi.Remote.Protocol;
using Person = AndroidX.Core.App.Person;
using RemoteInput = AndroidX.Core.App.RemoteInput;
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
/// Shows the PC's notifications. Each chat is an Android conversation with Lumi
/// (<see cref="LumiConversations"/>): the chat's title and avatar, with Lumi's badge, and what Lumi (or the
/// chat's agent) said, in the Conversations section like a chat app's. A tap opens the chat. A finished
/// reply can be answered from the notification: Reply to write one, and each of Lumi's suggestions as a
/// chip, which a watch shows as a button.
/// </summary>
internal sealed class AndroidNotificationPresenter(Context context, Func<string> hostName) : IPhoneNotificationPresenter
{
    /// <summary>Every chat notification shares one id; its tag is the PC's notification id.</summary>
    public const int ChatNotificationId = 1;

    /// <summary>The foreground service's own, silent notification.</summary>
    public const int ConnectionNotificationId = 4242;

    /// <summary>Where a reply's Reply action puts what the user wrote.</summary>
    public const string ReplyInputKey = "lumi.reply";

    private static readonly Color Accent = Color.ParseColor("#8B5CF6");

    public event Action<PhoneNotificationLinkState>? LinkChanged;

    public void Show(RemoteNotification notification, bool alert)
    {
        // Android links a notification to its conversation only if the conversation exists first.
        if (IsChat(notification))
            LumiConversations.Publish(context, notification.ChatId, Title(notification), OpenChat(notification));
        Post(notification, Build(notification, alert));
    }

    /// <summary>
    /// Shows a reply's notification again once the user answered it there: what they sent follows Lumi's
    /// words and its Reply action goes, until the PC moves on and takes it down.
    /// </summary>
    public void ShowReplied(RemoteNotification notification, string sent) =>
        Post(notification, Build(notification, alert: false, sent: sent));

    /// <summary>Shows a reply's notification again, saying the answer did not go, with Reply to try again.</summary>
    public void ShowReplyFailed(RemoteNotification notification, string sent) =>
        Post(notification, Build(notification, alert: false, sent: sent, failed: true));

    private void Post(RemoteNotification notification, Notification built)
    {
        try
        {
            NotificationManagerCompat.From(context)!.Notify(notification.Id, ChatNotificationId, built);
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

    private Notification Build(RemoteNotification notification, bool alert, string? sent = null, bool failed = false)
    {
        var host = hostName();
        var isTest = notification.Kind == RemoteProtocol.NotificationKinds.Test;
        var isChat = IsChat(notification);
        var title = isTest ? Strings.Get("NotificationTestTitle") : Title(notification);
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
            .SetCategory(NotificationCompat.CategoryMessage)!
            .SetPriority(isRequest || isTest ? NotificationCompat.PriorityHigh : NotificationCompat.PriorityDefault)!
            .SetWhen(when)!
            .SetShowWhen(true)!
            .SetAutoCancel(true)!
            .SetSilent(!alert)!
            .SetVisibility(NotificationCompat.VisibilityPrivate)!
            .SetPublicVersion(publicVersion)!
            .SetTimeoutAfter((long)TimeSpan.FromHours(24).TotalMilliseconds)!
            .SetContentIntent(isChat ? OpenChatIntent(notification) : OpenAppIntent())!;

        if (isChat)
        {
            builder.SetStyle(Conversation(notification.ChatId, title, text, when, sent, failed))!
                .SetShortcutId(LumiConversations.ShortcutId(notification.ChatId))!
                .SetLocusId(LumiConversations.LocusId(notification.ChatId));
        }
        else
        {
            builder.SetStyle(new NotificationCompat.BigTextStyle().BigText(text));
        }

        // Reply and Lumi's suggestions stay until an answer is on its way, and come back if one did not go.
        if ((sent is null || failed)
            && notification.Kind == RemoteProtocol.NotificationKinds.Reply
            && isChat
            && OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            builder.AddAction(ReplyAction(notification))!.SetAllowSystemGeneratedContextualActions(false);
            for (var index = 0; index < notification.Suggestions.Count; index++)
                builder.AddAction(SuggestionAction(notification, index));
        }

        return builder.Build()!;
    }

    /// <summary>
    /// The chat as a conversation: what Lumi said in it, then what the user answered from the
    /// notification, if anything, and whether it went. The title comes from the chat's sender (and from
    /// its conversation, where Android has one), so it shows once.
    /// </summary>
    private NotificationCompat.MessagingStyle Conversation(
        Guid chatId, string title, string text, long when, string? sent, bool failed)
    {
        var style = new NotificationCompat.MessagingStyle(LumiConversations.You())
            .SetGroupConversation(false)!
            .AddMessage(new NotificationCompat.MessagingStyle.Message(
                text, when, LumiConversations.Chat(context, chatId, title)))!;
        if (sent is not null)
        {
            var now = Java.Lang.JavaSystem.CurrentTimeMillis();
            // No sender: Android shows it as the user's own message.
            style.AddMessage(new NotificationCompat.MessagingStyle.Message(sent, now, (Person?)null));
            if (failed)
            {
                style.AddMessage(new NotificationCompat.MessagingStyle.Message(
                    Strings.Get("NotificationSendFailed"), now, LumiConversations.Lumi(context)));
            }
        }

        return style;
    }

    private static bool IsChat(RemoteNotification notification) =>
        notification.Kind != RemoteProtocol.NotificationKinds.Test && notification.ChatId != Guid.Empty;

    private static string Title(RemoteNotification notification) =>
        string.IsNullOrWhiteSpace(notification.ChatTitle) ? "Lumi" : notification.ChatTitle;

    /// <summary>
    /// Reply, to write an answer. What the user writes goes to the chat in the background
    /// (<see cref="LumiNotificationService"/>). From the phone it goes only once the phone is unlocked, which
    /// Android enforces from version 12; earlier versions get no Reply, since it would send from the lock
    /// screen. Lumi brings its own suggestions, so the system's generated replies are kept out.
    /// </summary>
    [SupportedOSPlatform("android31.0")]
    private NotificationCompat.Action ReplyAction(RemoteNotification notification)
    {
        var label = Strings.Get("NotificationActionReply");
        var send = PendingIntent.GetForegroundService(
            context,
            RequestCode(notification.Id),
            AnswerIntent(notification),
            PendingIntentFlags.Mutable | PendingIntentFlags.UpdateCurrent)!;
        return new NotificationCompat.Action.Builder(Resource.Drawable.ic_stat_lumi, label, send)
            .AddRemoteInput(new RemoteInput.Builder(ReplyInputKey).SetLabel(label)!.Build())!
            .SetAllowGeneratedReplies(false)!
            .SetSemanticAction(NotificationCompat.Action.SemanticActionReply)!
            .SetShowsUserInterface(false)!
            .SetAuthenticationRequired(true)!
            .Build()!;
    }

    /// <summary>
    /// One of Lumi's suggestions, sent as it is. Not a choice of Reply, which a Galaxy Watch never shows, but
    /// a contextual action: the phone draws it as a chip beside Reply, and a watch as a button. A locked phone
    /// asks to be unlocked before a chip's action goes, as for every chip; a watch, which has its own lock,
    /// sends at once, so it isn't told to unlock the phone.
    /// </summary>
    [SupportedOSPlatform("android31.0")]
    private NotificationCompat.Action SuggestionAction(RemoteNotification notification, int index)
    {
        var suggestion = notification.Suggestions[index];
        var send = PendingIntent.GetForegroundService(
            context,
            RequestCode($"{notification.Id}\n{index}"),
            AnswerIntent(notification).PutExtra(LumiNotificationService.SuggestionExtra, suggestion)!,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        // A chip shows its action's icon as it is: Lumi's mark in its accent, not the status bar's white.
        var icon = IconCompat.CreateWithResource(context, Resource.Drawable.ic_stat_lumi)!.SetTint(Accent.ToArgb())!;
        return new NotificationCompat.Action.Builder(icon, suggestion, send)
            .SetContextual(true)!
            .SetShowsUserInterface(false)!
            .Build()!;
    }

    /// <summary>
    /// Answers the chat from the notification. The service shows the notification again after sending, so
    /// the intent carries it along.
    /// </summary>
    private Intent AnswerIntent(RemoteNotification notification) =>
        new Intent(context, typeof(LumiNotificationService))
            .SetAction(LumiNotificationService.ActionReply)!
            .PutExtra(
                LumiNotificationService.NotificationExtra,
                JsonSerializer.Serialize(notification, RemoteJsonContext.Default.RemoteNotification))!;

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

    /// <summary>Opens the chat: a notification's tap, and its conversation in the launcher.</summary>
    private Intent OpenChat(RemoteNotification notification) =>
        new Intent(context, typeof(MainActivity))
            .SetAction(MainActivity.OpenChatAction)!
            .PutExtra(MainActivity.ChatIdExtra, notification.ChatId.ToString("D"))!
            .PutExtra(MainActivity.ChatTitleExtra, notification.ChatTitle)!
            .AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop)!;

    private PendingIntent OpenChatIntent(RemoteNotification notification) => PendingIntent.GetActivity(
        context,
        RequestCode(notification.Id),
        OpenChat(notification),
        PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;

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
