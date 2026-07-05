using System.Globalization;
using Avalonia.Data.Converters;
using Lumi.Converters;
using Xunit;

namespace Lumi.Tests;

public sealed class StringToNullableIntConverterTests
{
    private readonly IValueConverter _converter = StringToNullableIntConverter.Instance;

    [Fact]
    public void Convert_NullInt_ReturnsEmptyString()
    {
        // null displays as "" so the TextBox shows an empty (clearable) field.
        Assert.Equal(string.Empty, _converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Convert_Int_ReturnsInvariantString()
    {
        // 4096 renders as "4096" regardless of locale (no thousands separator).
        Assert.Equal("4096", _converter.Convert(4096, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ConvertBack_EmptyString_ReturnsNull()
    {
        // Clearing the field must map to null (inherit provider default) — this is the bug fix.
        Assert.Null(_converter.ConvertBack(string.Empty, typeof(int?), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ConvertBack_WhitespaceString_ReturnsNull()
    {
        // Whitespace-only is treated the same as empty.
        Assert.Null(_converter.ConvertBack("   ", typeof(int?), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ConvertBack_ValidInt_ReturnsInt()
    {
        Assert.Equal(4096, _converter.ConvertBack("4096", typeof(int?), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ConvertBack_NegativeInt_ReturnsInt()
    {
        // Negative values pass through; normalization in ByokConfigHelper coerces <=0 to null.
        Assert.Equal(-5, _converter.ConvertBack("-5", typeof(int?), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ConvertBack_Malformed_ReturnsNull()
    {
        // Non-numeric text returns null (not a BindingNotification) so the binding never throws
        // under CompileBindings and the source gets a consistent value. The trade-off (dropping
        // a half-typed value to null) is acceptable because the field is advisory and the next
        // valid keystroke re-parses cleanly.
        var result = _converter.ConvertBack("abc", typeof(int?), null, CultureInfo.InvariantCulture);
        Assert.Null(result);
    }

    [Fact]
    public void ConvertBack_ParsesInvariantCulture()
    {
        // Even in a comma-decimal locale, the converter uses InvariantCulture so "4096" parses.
        // We pass a comma-decimal culture to confirm the converter ignores it.
        var commaCulture = new CultureInfo("bg-BG");
        Assert.Equal(4096, _converter.ConvertBack("4096", typeof(int?), null, commaCulture));
    }
}
