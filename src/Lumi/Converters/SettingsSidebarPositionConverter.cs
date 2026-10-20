using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Lumi.ViewModels;

namespace Lumi.Converters;

/// <summary>
/// Maps the Settings sidebar's row to the page it shows and back. Memories sits under Profile in the
/// sidebar but has the page index after About, so the rows and the page indices differ.
/// </summary>
public sealed class SettingsSidebarPositionConverter : IValueConverter
{
    public static readonly SettingsSidebarPositionConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int page ? SettingsViewModel.SidebarPositionForPage(page) : -1;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int position ? SettingsViewModel.PageForSidebarPosition(position) : -1;
}
