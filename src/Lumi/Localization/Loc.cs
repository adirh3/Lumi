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

    /// <summary>Localizes the built-in default category for display without changing stored or custom category names.</summary>
    public static string GetMemoryCategoryDisplay(string category)
        => string.IsNullOrWhiteSpace(category) || string.Equals(category.Trim(), "General", StringComparison.OrdinalIgnoreCase)
            ? Memories_DefaultCategory
            : category;

    internal static string GetEnglish(string key) => _lang_en[_keyIndex[key]];

    internal static string GetBuiltInDescription(string name, string description)
    {
        var key = name switch
        {
            "Document Creator" => nameof(BuiltIn_DocumentCreator_Description),
            "Web Researcher" => nameof(BuiltIn_WebResearcher_Description),
            "File Organizer" => nameof(BuiltIn_FileOrganizer_Description),
            "Code Helper" => nameof(BuiltIn_CodeHelper_Description),
            "Email Drafter" => nameof(BuiltIn_EmailDrafter_Description),
            "Website Creator" => description == GetEnglish(nameof(BuiltIn_WebsiteCreatorPortable_Description))
                ? nameof(BuiltIn_WebsiteCreatorPortable_Description) : nameof(BuiltIn_WebsiteCreator_Description),
            "Daily Planner" => nameof(BuiltIn_DailyPlanner_Description),
            "Creative Writer" => nameof(BuiltIn_CreativeWriter_Description),
            "Learning Lumi" => nameof(BuiltIn_LearningLumi_Description),
            "Architecture Advisor" => nameof(BuiltIn_ArchitectureAdvisor_Description),
            "Debug Expert" => nameof(BuiltIn_DebugExpert_Description),
            "Code Reviewer Pro" => nameof(BuiltIn_CodeReviewer_Description),
            "Coding Lumi" => nameof(BuiltIn_CodingLumi_Description),
            _ => null
        };
        // Stored defaults stay canonical for agents and sharing; a user's edits stay their own text.
        return key is not null && description == GetEnglish(key) ? Get(key) : description;
    }

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
#if WINDOWS
    public static string Get(string key) => GetByKey(key);
#else
    public static string Get(string key) => AdaptKeyboardHint(GetByKey(key));
#endif

    /// <summary>Gets a localized string with format arguments.</summary>
#if WINDOWS
    public static string Get(string key, params object[] args)
        => string.Format(GetByKey(key), args);
#else
    public static string Get(string key, params object[] args)
        => AdaptKeyboardHint(string.Format(GetByKey(key), args));
#endif

#if !WINDOWS
    private static readonly bool IsMacOs = OperatingSystem.IsMacOS();
#endif

    /// <summary>
    /// On macOS, renders keyboard-shortcut hints embedded in UI strings with native modifier
    /// symbols (⌘/⌥/⇧) instead of the Windows/Linux "Ctrl/Alt/Shift" wording — e.g. "Search
    /// (Ctrl+K)" becomes "Search (⌘K)". No-op on Windows and Linux, so their strings are
    /// unchanged. Apply this to any shortcut hint that is built outside the localization table
    /// (e.g. typed <c>Loc.*</c> properties or literal XAML text).
    /// </summary>
#if WINDOWS
    public static string AdaptKeyboardHint(string value) => value;
#else
    public static string AdaptKeyboardHint(string value) => AdaptKeyboardHint(value, IsMacOs);
#endif

    /// <summary>Testable core of <see cref="AdaptKeyboardHint(string)"/>; <paramref name="isMac"/>
    /// is the only platform input so the transform can be unit-tested on any host.</summary>
    internal static string AdaptKeyboardHint(string value, bool isMac)
    {
        if (!isMac || string.IsNullOrEmpty(value) || value.IndexOf('+') < 0)
            return value;
        return value
            .Replace("Ctrl+", "⌘")
            .Replace("Shift+", "⇧")
            .Replace("Alt+", "⌥")
            .Replace("Win+", "⌘");
    }

    /// <summary>Available language codes and their display names.</summary>
    public static readonly (string Code, string DisplayName)[] AvailableLanguages =
    [
        ("en", "English"),
        ("he", "עברית"),
    ];
}

/// <summary>
/// XAML markup extension for localized strings.
/// Usage: {loc:Str NavTip_Chats}
/// </summary>
public class StrExtension : MarkupExtension
{
    public string Key { get; }

    public StrExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => Loc.Get(Key);
}
