using System.Globalization;
using Lumi.Mobile.Localization;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class ChatSessionStringsTests
{
    [Theory]
    [InlineData("en", "Ready · Background activity", "Stop session")]
    [InlineData("he", "מוכן · פעילות ברקע", "עצירת ההפעלה")]
    public void SessionLabelsUseTheCurrentUiCulture(string culture, string ready, string stop)
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(ready, ChatSessionStrings.ReadyWithBackgroundActivity);
            Assert.Equal(stop, ChatSessionStrings.StopSession);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }
}
