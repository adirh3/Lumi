using System.Globalization;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using AndroidX.Core.Content;
using AndroidX.Core.Content.PM;
using AndroidX.Core.Graphics.Drawable;
using Person = AndroidX.Core.App.Person;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.Android.Notifications;

/// <summary>
/// Makes each chat an Android conversation with Lumi, so its notifications look and act like a chat
/// app's: the chat's own avatar with Lumi's badge, the Conversations section, and Android's
/// per-conversation choices (priority, silence). A conversation is a long-lived shortcut, published just
/// before a notification about the chat is shown, which also offers the chat when Lumi's icon is
/// long-pressed. They go once the phone stops hearing from its PC: notifications turned off, or the PC
/// forgotten or changed.
/// </summary>
internal static class LumiConversations
{
    private const string Tag = "LumiNotifications";
    private const string IdPrefix = "chat:";

    // What a chat's initial stands on: told apart at a glance, and dark enough for white text.
    private static readonly Color[] AvatarColors =
    [
        Color.ParseColor("#7C3AED"),
        Color.ParseColor("#4F46E5"),
        Color.ParseColor("#2563EB"),
        Color.ParseColor("#0891B2"),
        Color.ParseColor("#0D9488"),
        Color.ParseColor("#16A34A"),
        Color.ParseColor("#D97706"),
        Color.ParseColor("#E11D48")
    ];

    public static string ShortcutId(Guid chatId) => IdPrefix + chatId.ToString("N");

    public static LocusIdCompat LocusId(Guid chatId) => new(ShortcutId(chatId));

    /// <summary>The user, as Android names their own answers.</summary>
    public static Person You() => new Person.Builder()
        .SetName(Strings.Get("You"))!
        .SetKey("you")!
        .Build()!;

    /// <summary>
    /// The other side of a chat: Lumi, named after the chat, as the conversation is. Android then shows
    /// the title once instead of adding the sender's name under it.
    /// </summary>
    public static Person Chat(Context context, Guid chatId, string title) => new Person.Builder()
        .SetName(title)!
        .SetKey(ShortcutId(chatId))!
        .SetIcon(Avatar(context, chatId, title))!
        .Build()!;

    /// <summary>Lumi itself, for what it says about the chat rather than in it.</summary>
    public static Person Lumi(Context context) => new Person.Builder()
        .SetName("Lumi")!
        .SetKey("lumi")!
        .SetIcon(LumiIcon(context))!
        .Build()!;

    // The adaptive launcher icon, so Android shapes it without clipping Lumi's mark.
    private static IconCompat LumiIcon(Context context) =>
        IconCompat.CreateWithResource(context, Resource.Mipmap.ic_launcher)!;

    /// <summary>
    /// A chat's avatar, as a contact without a photo gets one: the first letter of its title on a colour
    /// of its own, so chats are told apart at a glance while Lumi's badge says whose they are. Adaptive, so
    /// each surface shapes it its own way; its inner two thirds show. A title without a letter gets Lumi's
    /// icon.
    /// </summary>
    private static IconCompat Avatar(Context context, Guid chatId, string title)
    {
        if (Initial(title) is not { } initial)
            return LumiIcon(context);

        // The visible part is the shortcut icon's size; an adaptive bitmap adds Android's bleed around it.
        var size = (int)(ShortcutManagerCompat.GetIconMaxWidth(context) * (1 + 2 * AdaptiveIconDrawable.ExtraInsetFraction));
        var bitmap = Bitmap.CreateBitmap(size, size, Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(bitmap);
        canvas.DrawColor(AvatarColors[chatId.ToByteArray()[0] % AvatarColors.Length]);
        using var paint = new Paint(PaintFlags.AntiAlias) { Color = Color.White, TextAlign = Paint.Align.Center, TextSize = size / 3f };
        paint.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
        canvas.DrawText(initial, size / 2f, (size - paint.Descent() - paint.Ascent()) / 2f, paint);
        return IconCompat.CreateWithAdaptiveBitmap(bitmap)!;
    }

    /// <summary>The title's first letter or digit, as an avatar shows it; null when it has none.</summary>
    private static string? Initial(string title)
    {
        var elements = StringInfo.GetTextElementEnumerator(title);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (char.IsLetterOrDigit(element, 0))
                return element.ToUpper(CultureInfo.CurrentCulture);
        }

        return null;
    }

    /// <summary>Publishes the chat's conversation, or brings its title up to date.</summary>
    public static void Publish(Context context, Guid chatId, string title, Intent openChat)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
            return;

        try
        {
            var chat = Chat(context, chatId, title);
            var shortcut = new ShortcutInfoCompat.Builder(context, ShortcutId(chatId))
                .SetShortLabel(title)!
                .SetLongLabel(title)!
                .SetIcon(chat.Icon)!
                .SetIntent(openChat)!
                .SetLongLived(true)!
                .SetPerson(chat)!
                .SetLocusId(LocusId(chatId))!
                .SetCategories(new HashSet<string> { ShortcutInfo.ShortcutCategoryConversation })!
                .Build()!;
            ShortcutManagerCompat.PushDynamicShortcut(context, shortcut);
        }
        catch (Java.Lang.RuntimeException ex)
        {
            // The notification still shows, as a plain chat message.
            global::Android.Util.Log.Warn(Tag, $"Could not publish the chat's conversation: {ex.Message}");
        }
    }

    /// <summary>Removes every chat's conversation, with what the user set for it.</summary>
    public static void ForgetAll(Context context)
    {
        try
        {
            var ids = ShortcutManagerCompat
                .GetShortcuts(context, ShortcutManagerCompat.FlagMatchDynamic | ShortcutManagerCompat.FlagMatchCached)?
                .Select(static shortcut => shortcut.Id)
                .OfType<string>()
                .Where(static id => id.StartsWith(IdPrefix, StringComparison.Ordinal))
                .ToList();
            if (ids is { Count: > 0 })
                ShortcutManagerCompat.RemoveLongLivedShortcuts(context, ids);
        }
        catch (Java.Lang.RuntimeException ex)
        {
            global::Android.Util.Log.Warn(Tag, $"Could not remove the chats' conversations: {ex.Message}");
        }
    }
}
