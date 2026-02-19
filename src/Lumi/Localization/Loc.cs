using System;
using System.Globalization;
using Avalonia.Markup.Xaml;

namespace Lumi.Localization;

/// <summary>
/// Localization service backed by compile-time generated string arrays and FrozenDictionary.
/// The source generator reads JSON files and produces Loc.g.cs with typed properties,
/// language arrays, and frozen lookup maps. No JSON parsing at runtime.
/// Culture is set at startup based on UserSettings.Language; changing requires restart.
/// </summary>
public static partial class Loc
{
    /// <summary>The active culture for the app.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.CurrentUICulture;

    /// <summary>Whether the current language is right-to-left.</summary>
    public static bool IsRightToLeft => Culture.TextInfo.IsRightToLeft;

    /// <summary>
    /// Activates the given language. English is used as fallback for missing keys.
    /// </summary>
    public static void Load(string language)
    {
        SelectLanguage(language);

        try
        {
            Culture = new CultureInfo(language);
            CultureInfo.CurrentUICulture = Culture;
            CultureInfo.CurrentCulture = Culture;
        }
        catch
        {
            Culture = CultureInfo.InvariantCulture;
        }
    }

    /// <summary>Gets a localized string by key via FrozenDictionary lookup.</summary>
    public static string Get(string key) => GetByKey(key);

    /// <summary>Gets a localized string with format arguments.</summary>
    public static string Get(string key, params object[] args)
        => string.Format(GetByKey(key), args);

    /// <summary>Available language codes and their display names.</summary>
    public static readonly (string Code, string DisplayName)[] AvailableLanguages =
    [
        ("en", "English"),
        ("he", "עברית"),
    ];
}

/// <summary>
/// XAML markup extension for localized strings.
/// Usage: {loc:Str Nav_Chats}
/// </summary>
public class StrExtension : MarkupExtension
{
    public string Key { get; }

    public StrExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => Loc.Get(Key);
}
