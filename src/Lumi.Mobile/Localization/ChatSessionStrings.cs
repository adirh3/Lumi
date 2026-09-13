using System.Globalization;
using System.Resources;

namespace Lumi.Mobile.Localization;

/// <summary>Localized labels for the mobile chat's independent session activity.</summary>
public static class ChatSessionStrings
{
    private static readonly ResourceManager Resources = new(
        "Lumi.Mobile.Localization.ChatSessionStrings",
        typeof(ChatSessionStrings).Assembly);

    public static string ReadyWithBackgroundActivity =>
        Resources.GetString(nameof(ReadyWithBackgroundActivity), CultureInfo.CurrentUICulture)!;

    public static string StopSession =>
        Resources.GetString(nameof(StopSession), CultureInfo.CurrentUICulture)!;
}
