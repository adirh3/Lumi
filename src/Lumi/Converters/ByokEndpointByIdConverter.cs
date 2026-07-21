using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Data.Converters;
using Lumi.Models;

namespace Lumi.Converters;

/// <summary>
/// Converts a <see cref="ByokEndpoint.Id"/> (string) into the matching <see cref="ByokEndpoint"/>
/// instance from the lookup collection provided via <see cref="EndpointSource"/>, and back.
/// Used by the BYOK model editor's endpoint ComboBox so the ComboBox can keep an
/// <c>ItemsSource</c> of full endpoint objects while the model only persists the id.
/// </summary>
public sealed class ByokEndpointByIdConverter : AvaloniaObject, IValueConverter
{
    public static readonly ByokEndpointByIdConverter Instance = new();

    /// <summary>The endpoint collection used as the lookup source.</summary>
    public static readonly StyledProperty<IEnumerable<ByokEndpoint>?> EndpointSourceProperty =
        AvaloniaProperty.Register<ByokEndpointByIdConverter, IEnumerable<ByokEndpoint>?>(nameof(EndpointSource));

    /// <summary>The endpoint collection used as the lookup source.</summary>
    public IEnumerable<ByokEndpoint>? EndpointSource
    {
        get => GetValue(EndpointSourceProperty);
        set => SetValue(EndpointSourceProperty, value);
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string id || string.IsNullOrWhiteSpace(id) || EndpointSource is null)
            return null;
        return EndpointSource.FirstOrDefault(e => e.Id == id);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ByokEndpoint ep ? ep.Id : null;
}