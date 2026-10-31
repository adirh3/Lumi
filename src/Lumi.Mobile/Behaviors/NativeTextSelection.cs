using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Lumi.Mobile.Services;
using StrataTheme.Controls;

namespace Lumi.Mobile.Behaviors;

public sealed class NativeTextSelection
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<NativeTextSelection, StyledElement, string?>("Text");

    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<NativeTextSelection, StyledElement, ICommand?>("Command");

    public static readonly AttachedProperty<object?> CommandParameterProperty =
        AvaloniaProperty.RegisterAttached<NativeTextSelection, StyledElement, object?>("CommandParameter");

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<NativeTextSelection, StyledElement, bool>(
            "IsEnabled",
            false);

    /// <summary>Where the press that opened the actions landed, in the control's coordinates.</summary>
    public static readonly AttachedProperty<Point?> PressPositionProperty =
        AvaloniaProperty.RegisterAttached<NativeTextSelection, StyledElement, Point?>("PressPosition");

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

    public static ICommand? GetCommand(StyledElement element) => element.GetValue(CommandProperty);

    public static void SetCommand(StyledElement element, ICommand? value) => element.SetValue(CommandProperty, value);

    public static object? GetCommandParameter(StyledElement element) => element.GetValue(CommandParameterProperty);

    public static void SetCommandParameter(StyledElement element, object? value) =>
        element.SetValue(CommandParameterProperty, value);

    public static Point? GetPressPosition(StyledElement element) => element.GetValue(PressPositionProperty);

    public static void SetPressPosition(StyledElement element, Point? value) =>
        element.SetValue(PressPositionProperty, value);

    private static void Attach(Control control)
    {
        control.AddHandler(
            InputElement.ContextRequestedEvent,
            OnContextRequested,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        control.AddHandler(
            InputElement.PointerPressedEvent,
            OnPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        if (!OperatingSystem.IsAndroid())
        {
            control.SetValue(InputElement.IsHoldingEnabledProperty, true);
            control.Holding += OnHolding;
        }
    }

    private static void Detach(Control control)
    {
        control.RemoveHandler(InputElement.ContextRequestedEvent, OnContextRequested);
        control.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        control.ClearValue(PressPositionProperty);
        if (OperatingSystem.IsAndroid())
        {
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
        if (sender is not Control control)
            return;
        SetPressPosition(control, e.GetPosition(control));

        if (!OperatingSystem.IsAndroid()
            || e.Pointer.Type is not (PointerType.Touch or PointerType.Pen)
            || IsInteractiveTarget(control, e.Source)
            || !CanPresent(control))
        {
            return;
        }

        var context = control.DataContext;
        MobilePlatformServices.ArmTextSelectionGesture(GetText(control) ?? "", () =>
        {
            if (control.IsAttachedToVisualTree() && ReferenceEquals(control.DataContext, context))
                Present(control);
        });
    }

    private static void OnHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (OperatingSystem.IsAndroid()
            || e.HoldingState != HoldingState.Started
            || e.PointerType is not (PointerType.Touch or PointerType.Pen)
            || sender is not Control control
            || IsInteractiveTarget(control, e.Source)
            || !CanPresent(control))
        {
            return;
        }

        Present(control);
        e.Handled = true;
    }

    private static void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (OperatingSystem.IsAndroid()
            && MobilePlatformServices.IsTextSelectionGestureActive())
        {
            e.Handled = true;
            return;
        }

        if (sender is Control control && GetCommand(control) is { } command
            && !IsInteractiveTarget(control, e.Source)
            && command.CanExecute(GetCommandParameter(control)))
        {
            SetPressPosition(control, e.TryGetPosition(control, out var position) ? position : null);
            command.Execute(GetCommandParameter(control));
            e.Handled = true;
        }
    }

    private static bool CanPresent(Control control) => GetCommand(control) is { } command
        ? command.CanExecute(GetCommandParameter(control))
        : !string.IsNullOrWhiteSpace(GetText(control));

    private static void Present(Control control)
    {
        if (GetCommand(control) is { } command)
        {
            if (command.CanExecute(GetCommandParameter(control)))
                command.Execute(GetCommandParameter(control));
        }
        else if (GetText(control) is { Length: > 0 } text)
            MobilePlatformServices.TextSelectionPresenter.Show(text);
    }

    private static bool IsInteractiveTarget(Control owner, object? source) =>
        source is Visual visual && visual.GetSelfAndVisualAncestors()
            .TakeWhile(ancestor => !ReferenceEquals(ancestor, owner))
            .Any(ancestor => ancestor is Button or TextBox or StrataInteractiveBlock);
}
