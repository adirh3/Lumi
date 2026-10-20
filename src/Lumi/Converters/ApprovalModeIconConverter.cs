using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Lumi.Models;
using StrataTheme.Controls;

namespace Lumi.Converters;

/// <summary>The line icon for an approval mode: a bolt acts freely, a checked shield reviews, a hand asks.</summary>
public sealed class ApprovalModeIconConverter : IValueConverter
{
    public static readonly ApprovalModeIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ApprovalMode.AskFirst => StrataIcons.Hand,
        ApprovalMode.AutoReview => StrataIcons.ShieldCheck,
        ApprovalMode.FullAccess => StrataIcons.Bolt,
        _ => null
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
