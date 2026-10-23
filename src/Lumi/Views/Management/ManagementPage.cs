using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;

namespace Lumi.Views.Management;

/// <summary>Shared management-page shortcuts, scrolling and view release.</summary>
internal static class ManagementPage
{
    private static readonly ControlTheme ReleasedTheme = new() { TargetType = typeof(Control) };

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
    /// <param name="observe">The object that raises <paramref name="selectionProperty"/>, when it isn't the view's
    /// DataContext itself.</param>
    public static void ResetScrollOn(Control view, string scrollViewerName, string selectionProperty,
        Func<object?, INotifyPropertyChanged?>? observe = null)
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
            observed = observe is null ? view.DataContext as INotifyPropertyChanged : observe(view.DataContext);
            if (observed is not null)
                observed.PropertyChanged += handler;
        };
    }

    public static IReadOnlyDictionary<string, Vector> CaptureScroll(Control view)
        => ScrollViewers(view).ToDictionary(static item => item.Key, static item => item.Scroll.Offset);

    public static void RestoreScroll(Control view, IReadOnlyDictionary<string, Vector> offsets, Func<bool> isCurrent)
    {
        if (offsets.Count == 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!view.CheckAccess() || !view.IsAttachedToVisualTree() || !isCurrent())
                return;
            foreach (var (key, scroll) in ScrollViewers(view))
            {
                if (offsets.TryGetValue(key, out var offset))
                    scroll.Offset = offset;
            }
        }, DispatcherPriority.Loaded);
    }

    public static void Release(Control view)
    {
        var controls = OwnedControls(view);
        // Discarded controls must not animate binding defaults during teardown.
        foreach (var control in controls)
        {
            control.Transitions = null;
            if (ToolTip.GetIsOpen(control))
                ToolTip.SetIsOpen(control, false);
            control.ContextMenu?.Close();
            if (control is Button { Flyout: { } flyout })
                flyout.Hide();
            FlyoutBase.GetAttachedFlyout(control)?.Hide();
        }

        // Unbinding must not reveal hidden panels and start animations just before they are detached.
        view.IsVisible = false;
        view.DataContext = null;
        // Hidden templates may never have attached, so their theme animations receive no detach.
        for (var index = controls.Count - 1; index >= 0; index--)
            controls[index].Theme = ReleasedTheme;
        if (view is ContentControl content)
            content.Content = null;
    }

    private static List<Control> OwnedControls(Control view)
    {
        var result = new List<Control>();
        var seen = new HashSet<Control>();
        var pending = new Stack<Control>();
        pending.Push(view);
        while (pending.TryPop(out var control))
        {
            if (!seen.Add(control))
                continue;
            result.Add(control);
            foreach (var child in control.GetLogicalChildren().OfType<Control>()
                         .Concat(control.GetVisualChildren().OfType<Control>()))
                pending.Push(child);
        }
        return result;
    }

    private static IEnumerable<(string Key, ScrollViewer Scroll)> ScrollViewers(Control view)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var scroll in view.GetVisualDescendants().OfType<ScrollViewer>())
        {
            var names = scroll.GetVisualAncestors()
                .TakeWhile(ancestor => !ReferenceEquals(ancestor, view))
                .OfType<Control>()
                .Select(static control => control.Name)
                .Where(static name => !string.IsNullOrEmpty(name))
                .Reverse();
            var path = string.Join("/", names.Append(scroll.Name ?? "scroll"));
            var occurrence = occurrences.GetValueOrDefault(path);
            occurrences[path] = occurrence + 1;
            yield return ($"{path}:{occurrence}", scroll);
        }
    }
}
