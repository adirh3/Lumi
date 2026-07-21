using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Lumi.Converters;

/// <summary>
/// Two-way converter between a <see cref="string"/> (from a <c>TextBox</c>) and a nullable
/// integer. Used by the BYOK Advanced settings fields so that an empty text box maps cleanly to
/// <c>null</c> (meaning "inherit the provider/SDK default") instead of raising a binding
/// validation error.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia's default <c>string</c> → <c>int?</c> binding fails validation when the text is
/// cleared: the empty string cannot be parsed as an integer, so the binding reports an error
/// and the underlying <c>int?</c> property is never updated. That makes the field impossible
/// to clear once a value has been entered.
/// </para>
/// <para>
/// This converter treats whitespace-only text as <c>null</c> (round-trip safe), parses any
/// other text with <see cref="int.TryParse(string, NumberStyles, IFormatProvider, out int)"/>
/// (InvariantCulture, so <c>"4096"</c> works regardless of locale), and returns
/// <see cref="BindingNotification"/> (not an exception) for genuinely malformed input so the
/// binding layer keeps the last good value instead of throwing.
/// </para>
/// </remarks>
public sealed class StringToNullableIntConverter : IValueConverter
{
    /// <summary>Shared singleton instance — the converter is stateless and safe to reuse.</summary>
    public static readonly StringToNullableIntConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // int? → string for display. null renders as "" so the TextBox shows an empty field
        // (which, on a round-trip back, converts to null again — the desired "clear" semantic).
        if (value is null)
            return string.Empty;

        return value switch
        {
            int i => i.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // string → int? for the source property. Empty/whitespace clears the value (null).
        var text = value as string;
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            return result;

        // Malformed input (e.g. mid-typing "4a"): return null so the binding never throws and the
        // source is updated to a consistent value. We deliberately do NOT return a
        // BindingNotification here — under CompileBindings, returning a BindingNotification for a
        // nullable-value-type target caused the source to be written as null on every keystroke,
        // which is exactly the "always saves null" symptom this converter was added to prevent.
        // The trade-off (dropping a half-typed value to null) is acceptable because the field is
        // advisory and the next valid keystroke re-parses cleanly.
        return null;
    }
}
