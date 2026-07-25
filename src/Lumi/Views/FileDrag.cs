using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Lumi.Views;

/// <summary>
/// Turns any control into a native file drag source so a file Lumi produced or touched can be
/// dragged straight out of the app and dropped on Explorer, an email compose window, or any other
/// application that accepts files.
/// </summary>
/// <remarks>
/// Attach with <c>views:FileDrag.FilePath="{Binding FilePath}"</c>. The drag only starts once the
/// pointer travels past a small threshold, so plain clicks (open the file, run a command) keep
/// working untouched.
/// </remarks>
public static class FileDrag
{
    /// <summary>Pointer travel, in DIPs, required before a press turns into a drag.</summary>
    private const double DragThreshold = 4;

    /// <summary>Absolute path of the file this control represents. Empty disables dragging.</summary>
    public static readonly AttachedProperty<string?> FilePathProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("FilePath", typeof(FileDrag));

    // Only one pointer can drag at a time, so the pending press is tracked process-wide instead of
    // allocating per-chip state for every row in the transcript and workspace lists.
    private static Control? _pressSource;
    private static TopLevel? _pressRoot;
    private static PointerPressedEventArgs? _pressArgs;
    private static Point _pressOrigin;
    private static string? _pressPath;
    private static bool _isDragging;

    static FileDrag()
    {
        FilePathProperty.Changed.AddClassHandler<Control>(OnFilePathChanged);
    }

    public static string? GetFilePath(Control control) => control.GetValue(FilePathProperty);

    public static void SetFilePath(Control control, string? value) => control.SetValue(FilePathProperty, value);

    private static void OnFilePathChanged(Control control, AvaloniaPropertyChangedEventArgs args)
    {
        var hadHandlers = !string.IsNullOrWhiteSpace(args.OldValue as string);
        var needsHandlers = !string.IsNullOrWhiteSpace(args.NewValue as string);
        if (hadHandlers == needsHandlers)
            return;

        if (needsHandlers)
        {
            // Tunnel: chips and rows swallow the press for their own click handling, so the drag
            // source has to see it on the way down.
            control.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
            control.DetachedFromVisualTree += OnDetached;
        }
        else
        {
            control.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            control.DetachedFromVisualTree -= OnDetached;
            if (ReferenceEquals(_pressSource, control))
                CancelPendingPress();
        }
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (ReferenceEquals(_pressSource, sender))
            CancelPendingPress();
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CancelPendingPress();

        if (sender is not Control control || _isDragging)
            return;
        // Mouse only: touch/pen presses belong to the scroll gesture recognizers that drive the
        // transcript and workspace lists.
        if (e.Pointer.Type != PointerType.Mouse)
            return;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;

        var path = GetFilePath(control);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        if (TopLevel.GetTopLevel(control) is not { } root)
            return;

        _pressSource = control;
        _pressRoot = root;
        _pressArgs = e;
        _pressPath = path;
        _pressOrigin = e.GetPosition(root);

        // Listening on the window guarantees the moves keep arriving even when the chip itself never
        // captures the pointer or the pointer leaves it immediately.
        root.AddHandler(InputElement.PointerMovedEvent, OnRootPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerReleasedEvent, OnRootPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static void OnRootPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressRoot is not { } root || _pressArgs is not { } trigger || _pressPath is not { } path)
            return;

        if (!e.GetCurrentPoint(root).Properties.IsLeftButtonPressed)
        {
            CancelPendingPress();
            return;
        }

        var delta = e.GetPosition(root) - _pressOrigin;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;

        CancelPendingPress();
        _ = StartDragAsync(root, trigger, path);
    }

    private static void OnRootPointerReleased(object? sender, PointerReleasedEventArgs e) => CancelPendingPress();

    private static void CancelPendingPress()
    {
        if (_pressRoot is { } root)
        {
            root.RemoveHandler(InputElement.PointerMovedEvent, OnRootPointerMoved);
            root.RemoveHandler(InputElement.PointerReleasedEvent, OnRootPointerReleased);
        }

        _pressSource = null;
        _pressRoot = null;
        _pressArgs = null;
        _pressPath = null;
    }

    private static async Task StartDragAsync(TopLevel root, PointerPressedEventArgs trigger, string path)
    {
        _isDragging = true;
        try
        {
            if (await root.StorageProvider.TryGetFileFromPathAsync(path) is not { } file)
                return;

            using var data = new DataTransfer();
            data.Add(DataTransferItem.CreateFile(file));
            await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Copy | DragDropEffects.Link);
        }
        catch
        {
            // A drag the platform refuses (missing file, no drag source) must never take the app down.
        }
        finally
        {
            _isDragging = false;
        }
    }
}
