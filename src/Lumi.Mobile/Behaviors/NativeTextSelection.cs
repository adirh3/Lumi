using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Lumi.Mobile.Services;

namespace Lumi.Mobile.Behaviors;

public sealed class NativeTextSelection
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<NativeTextSelection, StyledElement, string?>("Text");

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<NativeTextSelection, StyledElement, bool>(
            "IsEnabled",
            false);

    static NativeTextSelection()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>(static (control, change) =>
        {
            if (change.NewValue is true)
                Attach(control);
            else
                Detach(control);
        });
    }

    private NativeTextSelection()
    {
    }

    public static bool GetIsEnabled(StyledElement element) =>
        element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(StyledElement element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static string? GetText(StyledElement element) => element.GetValue(TextProperty);

    public static void SetText(StyledElement element, string? value) => element.SetValue(TextProperty, value);

    private static void Attach(Control control)
    {
        if (OperatingSystem.IsAndroid())
        {
            control.AddHandler(
                InputElement.PointerPressedEvent,
                OnPointerPressed,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            control.AddHandler(
                InputElement.ContextRequestedEvent,
                OnContextRequested,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
        }
        else
        {
            control.SetValue(InputElement.IsHoldingEnabledProperty, true);
            control.Holding += OnHolding;
        }
    }

    private static void Detach(Control control)
    {
        if (OperatingSystem.IsAndroid())
        {
            control.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            control.RemoveHandler(InputElement.ContextRequestedEvent, OnContextRequested);
            MobilePlatformServices.ClearTextSelectionGesture();
        }
        else
        {
            control.Holding -= OnHolding;
            control.SetValue(InputElement.IsHoldingEnabledProperty, false);
        }
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!OperatingSystem.IsAndroid()
            || sender is not Control control
            || e.Pointer.Type is not (PointerType.Touch or PointerType.Pen)
            || string.IsNullOrWhiteSpace(GetText(control)))
        {
            return;
        }

        MobilePlatformServices.ArmTextSelectionGesture(GetText(control)!);
    }

    private static void OnHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (OperatingSystem.IsAndroid()
            || e.HoldingState != HoldingState.Started
            || e.PointerType is not (PointerType.Touch or PointerType.Pen)
            || sender is not Control control
            || string.IsNullOrWhiteSpace(GetText(control)))
        {
            return;
        }

        MobilePlatformServices.TextSelectionPresenter.Show(GetText(control)!);
        e.Handled = true;
    }

    private static void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (OperatingSystem.IsAndroid()
            && MobilePlatformServices.IsTextSelectionGestureActive())
        {
            e.Handled = true;
        }
    }
}
