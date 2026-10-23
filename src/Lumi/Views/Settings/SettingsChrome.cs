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

    /// <summary>
    /// Content shown under a row's description by rows styled to show it (an AI feature's facts and model), while
    /// the row's own content keeps its place at the right.
    /// </summary>
    public static readonly AttachedProperty<object?> DetailsProperty =
        AvaloniaProperty.RegisterAttached<StyledElement, object?>("Details", typeof(SettingsChrome));

    public static object? GetDetails(StyledElement element) => element.GetValue(DetailsProperty);

    public static void SetDetails(StyledElement element, object? value) => element.SetValue(DetailsProperty, value);
}
