using Avalonia;
using Avalonia.Platform;

namespace Lumi.Services.Appearance;

/// <summary>The operating system's light or dark preference, for "Theme: System".</summary>
public static class SystemTheme
{
    /// <summary>Whether the OS prefers a dark appearance; null when the platform doesn't say.</summary>
    public static bool? PrefersDark(Application? application = null)
    {
        var platformSettings = (application ?? Application.Current)?.PlatformSettings;
        if (platformSettings is null)
            return null;

        return platformSettings.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;
    }
}
