using System.Globalization;
using System.Resources;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.Localization;

/// <summary>The mobile UI catalog. Like desktop Lumi, a language change takes effect at startup.</summary>
public static class ChatSessionStrings
{
    private static readonly ResourceManager Resources = new(
        "Lumi.Mobile.Localization.ChatSessionStrings",
        typeof(ChatSessionStrings).Assembly);

    public static string Language => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "he" ? "he" : "en";

    public static Avalonia.Media.FlowDirection UiFlowDirection => Language == "he"
        ? Avalonia.Media.FlowDirection.RightToLeft
        : Avalonia.Media.FlowDirection.LeftToRight;

    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string Get(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    /// <summary>Only the protocol-generated suffix is presentation text; the original payload stays unchanged.</summary>
    public static string? LocalizeMobileTruncation(string? value) =>
        value?.EndsWith(RemoteProtocol.MobileTruncationMarker, StringComparison.Ordinal) == true
            ? value[..^RemoteProtocol.MobileTruncationMarker.Length] + "\n\n" + Get("MobileTruncationNotice")
            : value;

    public static readonly FuncValueConverter<string?, string?> MobileTextConverter =
        new(LocalizeMobileTruncation);

    /// <summary>Sets dispatcher and background-thread culture before any mobile views are built.</summary>
    public static void Load(string? language)
    {
        var requested = string.IsNullOrWhiteSpace(language) ? CultureInfo.InstalledUICulture.Name : language;
        var culture = CultureInfo.GetCultureInfo(
            requested.Equals("he", StringComparison.OrdinalIgnoreCase)
            || requested.StartsWith("he-", StringComparison.OrdinalIgnoreCase) ? "he" : "en");
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
    }

    public static string ReadyWithBackgroundActivity => Get(nameof(ReadyWithBackgroundActivity));

    public static string StopSession => Get(nameof(StopSession));
}

/// <summary>Reads the same resx catalog used by code, without a desktop application dependency.</summary>
public sealed class StrExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => ChatSessionStrings.Get(key);
}
