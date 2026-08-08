using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Lumi.Mobile.Behaviors;

/// <summary>
/// Prevents a touch drag inside a wide button from becoming a click on release.
///
/// <para>Rows in a phone drawer remain under the finger during a vertical scroll, so the normal
/// pointer-over check is not enough: without a movement threshold Avalonia can scroll the list and
/// still invoke the row when the finger lifts. Mouse clicks stay unchanged.</para>
/// </summary>
public sealed class TouchScrollClickGuard
{
    private const double DragThreshold = 10;
    private static readonly ConditionalWeakTable<Button, State> States = new();

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TouchScrollClickGuard, Button, bool>("IsEnabled");

    static TouchScrollClickGuard()
    {
        IsEnabledProperty.Changed.AddClassHandler<Button>((button, change) =>
        {
            if (change.GetNewValue<bool>())
                Attach(button);
            else
                Detach(button);
        });
    }

    private TouchScrollClickGuard()
    {
    }

    public static bool GetIsEnabled(Button button) => button.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Button button, bool value) => button.SetValue(IsEnabledProperty, value);

    public static bool WasDragged(Control control) =>
        control is Button button
        && States.TryGetValue(button, out var state)
        && state.Dragged;

    private static void Attach(Button button)
    {
        States.GetValue(button, static _ => new State());
        button.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, true);
        button.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, true);
        button.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, true);
    }

    private static void Detach(Button button)
    {
        button.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        button.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
        button.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
        States.Remove(button);
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Button button || !States.TryGetValue(button, out var state))
            return;

        state.Pointer = e.Pointer;
        state.Start = e.GetPosition(button);
        state.Tracking = true;
        state.Dragged = false;
    }

    private static void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Button button
            || !States.TryGetValue(button, out var state)
            || !state.Tracking
            || !ReferenceEquals(state.Pointer, e.Pointer))
        {
            return;
        }

        state.Dragged |= ExceedsThreshold(state.Start, e.GetPosition(button));
    }

    private static void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Button button
            || !States.TryGetValue(button, out var state)
            || !state.Tracking
            || !ReferenceEquals(state.Pointer, e.Pointer))
        {
            return;
        }

        var dragged = state.Dragged || ExceedsThreshold(state.Start, e.GetPosition(button));
        state.Tracking = false;
        state.Pointer = null;

        // Tunnel runs before Button's release handler, so marking the drag handled prevents Click /
        // Command while the ancestor ScrollViewer has already seen the release on its way down.
        if (dragged)
            e.Handled = true;
    }

    private static bool ExceedsThreshold(Point start, Point current)
    {
        var dx = current.X - start.X;
        var dy = current.Y - start.Y;
        return (dx * dx) + (dy * dy) >= DragThreshold * DragThreshold;
    }

    private sealed class State
    {
        public IPointer? Pointer;
        public Point Start;
        public bool Tracking;
        public bool Dragged;
    }
}
