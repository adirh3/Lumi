using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Lumi.Localization;

namespace Lumi.Views.Management;

/// <summary>Small behaviours every management page shares: the save shortcut and scroll reset.</summary>
internal static class ManagementPage
{
    /// <summary>Ctrl+S, or Cmd+S on macOS, to match the platform's own save gesture.</summary>
    public static bool IsSaveGesture(KeyEventArgs e)
    {
        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        return e.Key == Key.S && e.KeyModifiers == primary;
    }

    public static string SaveHint => Loc.AdaptKeyboardHint("Ctrl+S");

    /// <summary>
    /// Escape backs out one step: it dismisses a delete confirmation, or returns to the overview
    /// when there is nothing unsaved to lose.
    /// </summary>
    public static bool TryHandleEscape(KeyEventArgs e, bool isOpen, bool isConfirmingDelete, bool hasUnsavedWork, Action cancelDelete, Action close)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || !isOpen)
            return false;

        if (isConfirmingDelete)
            cancelDelete();
        else if (!hasUnsavedWork)
            close();
        else
            return false;

        e.Handled = true;
        return true;
    }

    /// <summary>
    /// Scrolls the detail back to the top whenever a different item is opened, so a new item never
    /// opens halfway down because the previous one was scrolled.
    /// </summary>
    public static void ResetScrollOn(Control view, string scrollViewerName, string selectionProperty)
    {
        INotifyPropertyChanged? observed = null;
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName != selectionProperty)
                return;

            if (view.FindControl<ScrollViewer>(scrollViewerName) is { } scroll)
                scroll.Offset = new Vector(scroll.Offset.X, 0);
        };

        view.DataContextChanged += (_, _) =>
        {
            if (observed is not null)
                observed.PropertyChanged -= handler;
            observed = view.DataContext as INotifyPropertyChanged;
            if (observed is not null)
                observed.PropertyChanged += handler;
        };
    }
}
