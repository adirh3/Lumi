using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Lumi.ViewModels;
using StrataTheme.Animation;

namespace Lumi.Views.Management;

/// <summary>
/// How a management surface comes into view: when it appears, or moves from one item to another,
/// the sections of its scrolling content rise into place one after another. The motion runs on the
/// render thread, so it still plays while the UI thread builds the page, when a style transition
/// would already be over.
/// </summary>
public static class ManagementMotion
{
    private const double Rise = 14;
    private const int StaggerLimit = 5;
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(45);

    /// <summary>Plays the entrance whenever the surface becomes visible.</summary>
    public static readonly AttachedProperty<bool> EntranceProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Entrance", typeof(ManagementMotion));

    /// <summary>
    /// The item the surface shows. Moving from one item to another plays the entrance again; saving
    /// a new item, or starting one from an open item, keeps the page still.
    /// </summary>
    public static readonly AttachedProperty<object?> KeyProperty =
        AvaloniaProperty.RegisterAttached<Control, object?>("Key", typeof(ManagementMotion));

    static ManagementMotion()
    {
        EntranceProperty.Changed.AddClassHandler<Control>(static (surface, e) =>
        {
            surface.PropertyChanged -= OnSurfacePropertyChanged;
            if (e.NewValue is true)
                surface.PropertyChanged += OnSurfacePropertyChanged;
        });
    }

    public static bool GetEntrance(Control surface) => surface.GetValue(EntranceProperty);
    public static void SetEntrance(Control surface, bool value) => surface.SetValue(EntranceProperty, value);
    public static object? GetKey(Control surface) => surface.GetValue(KeyProperty);
    public static void SetKey(Control surface, object? value) => surface.SetValue(KeyProperty, value);

    /// <summary>Whether the user lets Lumi animate (Settings → Show animations).</summary>
    internal static bool IsEnabledFor(Visual visual)
        => TopLevel.GetTopLevel(visual)?.DataContext is not MainViewModel { SettingsVM.ShowAnimations: false };

    private static void OnSurfacePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        var plays = e.Property == Visual.IsVisibleProperty
                    || (e.Property == KeyProperty && e.OldValue is not null && e.NewValue is not null);
        if (plays && sender is Control { IsVisible: true } surface && IsEnabledFor(surface))
            Play(surface);
    }

    private static void Play(Control surface)
    {
        var index = 0;
        foreach (var section in Sections(surface))
        {
            var delay = Stagger * Math.Min(index++, StaggerLimit);
            if (section.IsAttachedToVisualTree())
                SlideFadeEntrance.PlayComposited(section, Rise, Duration, delay);
            else
                PlayOnceAttached(section, delay);
        }
    }

    /// <summary>The visible sections of the surface's scrolling content, top to bottom.</summary>
    private static IEnumerable<Control> Sections(Control surface)
        => surface.GetLogicalDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Content is Panel content
            ? content.Children.Where(static section => section.IsVisible)
            : [];

    /// <summary>
    /// A ScrollViewer builds its template, and with it puts its content on screen, only when first
    /// measured. A surface hidden until now therefore attaches its sections in the coming layout pass.
    /// </summary>
    private static void PlayOnceAttached(Control section, TimeSpan delay)
    {
        section.AttachedToVisualTree += OnAttached;

        void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            section.AttachedToVisualTree -= OnAttached;
            SlideFadeEntrance.PlayComposited(section, Rise, Duration, delay);
        }
    }
}
