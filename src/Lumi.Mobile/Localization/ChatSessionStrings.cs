using System.Globalization;
using System.Resources;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.Localization;

/// <summary>The mobile UI catalog. Like desktop Lumi, a language change takes effect at startup.</summary>
public static class ChatSessionStrings
{
    public static readonly (string Code, string DisplayName)[] AvailableLanguages =
    [
        ("en", "English"), ("he", "עברית"), ("es", "Español"),
        ("pt-BR", "Português (Brasil)"), ("zh-Hans", "简体中文"),
        ("de", "Deutsch"), ("fr", "Français"), ("ja", "日本語"),
        ("ko", "한국어"), ("ar", "العربية")
    ];

    private static readonly ResourceManager Resources = new(
        "Lumi.Mobile.Localization.ChatSessionStrings",
        typeof(ChatSessionStrings).Assembly);

    public static string Language => NormalizeLanguage(CultureInfo.CurrentUICulture.Name);

    public static Avalonia.Media.FlowDirection UiFlowDirection => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft
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
        var culture = CultureInfo.GetCultureInfo(NormalizeLanguage(requested));
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
    }

    internal static string NormalizeLanguage(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "he" => "he",
        { } code when code.StartsWith("he-", StringComparison.Ordinal) => "he",
        "de" => "de",
        "fr" => "fr",
        "ja" => "ja",
        "ko" => "ko",
        "ar" => "ar",
        { } code when code.StartsWith("de-", StringComparison.Ordinal) => "de",
        { } code when code.StartsWith("fr-", StringComparison.Ordinal) => "fr",
        { } code when code.StartsWith("ja-", StringComparison.Ordinal) => "ja",
        { } code when code.StartsWith("ko-", StringComparison.Ordinal) => "ko",
        { } code when code.StartsWith("ar-", StringComparison.Ordinal) => "ar",
        "es" => "es",
        { } code when code.StartsWith("es-", StringComparison.Ordinal) => "es",
        "pt" => "pt-BR",
        { } code when code.StartsWith("pt-", StringComparison.Ordinal) => "pt-BR",
        "zh" or "zh-hans" or "zh-cn" or "zh-sg" => "zh-Hans",
        { } code when code.StartsWith("zh-hans-", StringComparison.Ordinal) => "zh-Hans",
        _ => "en"
    };

    public static string ReadyWithBackgroundActivity => Get(nameof(ReadyWithBackgroundActivity));

    public static string StopSession => Get(nameof(StopSession));
}

/// <summary>Reads the same resx catalog used by code, without a desktop application dependency.</summary>
public sealed class StrExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => ChatSessionStrings.Get(key);
}
