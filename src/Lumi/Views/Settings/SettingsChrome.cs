using Avalonia;
using Avalonia.Media;

namespace Lumi.Views.Settings;

/// <summary>Decorations for Settings' cards and rows that Strata's setting controls don't carry.</summary>
public static class SettingsChrome
{
    /// <summary>The glyph in a settings card's header tile.</summary>
    public static readonly AttachedProperty<Geometry?> IconProperty =
        AvaloniaProperty.RegisterAttached<StyledElement, Geometry?>("Icon", typeof(SettingsChrome));

    public static Geometry? GetIcon(StyledElement element) => element.GetValue(IconProperty);

    public static void SetIcon(StyledElement element, Geometry? value) => element.SetValue(IconProperty, value);

    /// <summary>
    /// Other words that should find a setting row (an older name, a common synonym), or what finds a panel
    /// that isn't a setting (the update center, statistics) in Settings search.
    /// </summary>
    public static readonly AttachedProperty<string?> SearchTextProperty =
        AvaloniaProperty.RegisterAttached<StyledElement, string?>("SearchText", typeof(SettingsChrome));

    public static string? GetSearchText(StyledElement element) => element.GetValue(SearchTextProperty);

    public static void SetSearchText(StyledElement element, string? value) => element.SetValue(SearchTextProperty, value);
}
