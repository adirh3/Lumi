using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Behaviors;
using Lumi.Mobile.ViewModels;

namespace Lumi.Mobile.Views;

/// <summary>
/// A message's menu, as a phone's own: the page dims around the pressed message, which stays lit,
/// and a compact card of actions floats just below it, or above it when there is no room below.
/// A message taller than the screen gets its card beside the finger that pressed it.
/// </summary>
public partial class ChatDetailView
{
    internal const double MessageMenuGap = 8;
    internal const double MessageMenuEdge = 12;
    private const double AnswerHighlightInsetX = 10;
    private const double AnswerHighlightInsetY = 6;
    private const double AnswerHighlightRadius = 14;

    private Control? _messageMenuAnchor;
    private bool _messageMenuLayoutQueued;

    /// <summary>Where the card went last, in the layer's (screen) coordinates; for tests.</summary>
    internal Rect MessageMenuBounds { get; private set; }

    /// <summary>The lit message, in the layer's (screen) coordinates; for tests.</summary>
    internal Rect MessageHighlightBounds { get; private set; }

    private void OnMessageMenuOpenChanged()
    {
        if (_shell?.Chat.IsMessageMenuOpen == true)
        {
            QueueMessageMenuLayout();
            return;
        }

        SetMessageMenuAnchor(null);
        this.FindControl<Avalonia.Controls.Shapes.Path>("MessageMenuScrim")?.Classes.Set("shown", false);
        this.FindControl<Panel>("MessageMenuSurface")?.Classes.Set("shown", false);
    }

    private void QueueMessageMenuLayout()
    {
        if (_messageMenuLayoutQueued)
            return;
        _messageMenuLayoutQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _messageMenuLayoutQueued = false;
            LayoutMessageMenu();
        }, DispatcherPriority.Loaded);
    }

    internal void LayoutMessageMenu()
    {
        if (_shell is not { Chat: { IsMessageMenuOpen: true, SelectedMessage: { } message } chat } shell
            || this.FindControl<Panel>("MessageMenuLayer") is not { } layer
            || this.FindControl<Panel>("MessageMenuSurface") is not { } surface
            || this.FindControl<Avalonia.Controls.Shapes.Path>("MessageMenuScrim") is not { } scrim)
        {
            return;
        }

        var anchor = FindMessageAnchor(message);
        if (anchor is null)
        {
            // The message left the transcript (a refresh replaced it): its menu goes with it.
            chat.CloseMessageActionsCommand.Execute(null);
            return;
        }
        SetMessageMenuAnchor(anchor);

        var layerSize = layer.Bounds.Size;
        if (layerSize.Width <= 0 || layerSize.Height <= 0
            || GetMessageHighlight(anchor, layer) is not { } lit)
        {
            return;
        }
        var (highlight, radius) = lit;

        Point? press = null;
        if (NativeTextSelection.GetPressPosition(anchor) is { } position
            && anchor.TransformToVisual(layer) is { } toLayer)
        {
            press = position.Transform(toLayer);
        }

        var screen = new Rect(layerSize);
        scrim.Data = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(screen),
            new RectangleGeometry(highlight, radius, radius));

        surface.Measure(Size.Infinity);
        var area = screen.Deflate(shell.SafeArea).Deflate(MessageMenuEdge);
        var alignEnd = message is UserTurnItemViewModel;
        var placement = PlaceMessageMenu(area, highlight, surface.DesiredSize, alignEnd, press);
        Canvas.SetLeft(surface, placement.Bounds.X);
        Canvas.SetTop(surface, placement.Bounds.Y);
        surface.RenderTransformOrigin = new RelativePoint(
            alignEnd ? 1 : 0.5, placement.IsBelow ? 0 : 1, RelativeUnit.Relative);
        MessageMenuBounds = placement.Bounds;
        MessageHighlightBounds = highlight;

        scrim.Classes.Set("shown", true);
        surface.Classes.Set("shown", true);
    }

    /// <summary>
    /// Places a card of <paramref name="menu"/>'s size within <paramref name="area"/>: below the message,
    /// else above it, else beside the press; aligned to a user bubble's outer edge, or centred on the
    /// press over an answer.
    /// </summary>
    internal static (Rect Bounds, bool IsBelow) PlaceMessageMenu(
        Rect area, Rect message, Size menu, bool alignEnd, Point? press)
    {
        var x = alignEnd ? message.Right - menu.Width
            : press is { } point ? point.X - menu.Width / 2
            : message.Left;
        x = Math.Max(area.Left, Math.Min(x, area.Right - menu.Width));

        var visible = message.Intersect(area);
        double y;
        bool below;
        if (visible.Height > 0 && visible.Bottom + MessageMenuGap + menu.Height <= area.Bottom)
            (y, below) = (visible.Bottom + MessageMenuGap, true);
        else if (visible.Height > 0 && visible.Top - MessageMenuGap - menu.Height >= area.Top)
            (y, below) = (visible.Top - MessageMenuGap - menu.Height, false);
        else if (press is { } at && at.Y + MessageMenuGap + menu.Height <= area.Bottom)
            (y, below) = (at.Y + MessageMenuGap, true);
        else if (press is { } above)
            (y, below) = (above.Y - MessageMenuGap - menu.Height, false);
        else
            (y, below) = (area.Bottom - menu.Height, false);
        y = Math.Max(area.Top, Math.Min(y, area.Bottom - menu.Height));

        return (new Rect(new Point(x, y), menu), below);
    }

    private static (Rect Bounds, double Radius)? GetMessageHighlight(Control anchor, Visual layer)
    {
        if (anchor is StrataTheme.Controls.StrataChatMessage
            && anchor.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "PART_Bubble")
                is { } bubble
            && bubble.TransformToVisual(layer) is { } bubbleToLayer)
        {
            return (new Rect(bubble.Bounds.Size).TransformToAABB(bubbleToLayer), bubble.CornerRadius.TopLeft);
        }

        return anchor.TransformToVisual(layer) is { } toLayer
            ? (new Rect(anchor.Bounds.Size).TransformToAABB(toLayer)
                .Inflate(new Thickness(AnswerHighlightInsetX, AnswerHighlightInsetY)), AnswerHighlightRadius)
            : null;
    }

    private Control? FindMessageAnchor(TranscriptItemViewModel message)
    {
        if (_messageMenuAnchor is { } current && IsMessageAnchor(current, message))
            return current;
        return this.FindControl<ItemsControl>("Transcript")?.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => IsMessageAnchor(control, message));
    }

    private static bool IsMessageAnchor(Control control, TranscriptItemViewModel message) =>
        ReferenceEquals(control.DataContext, message)
        && NativeTextSelection.GetIsEnabled(control)
        && NativeTextSelection.GetCommand(control) is not null
        && control.IsAttachedToVisualTree()
        && control.IsEffectivelyVisible;

    private void SetMessageMenuAnchor(Control? anchor)
    {
        if (ReferenceEquals(anchor, _messageMenuAnchor))
            return;
        if (_messageMenuAnchor is { } previous)
        {
            previous.SizeChanged -= OnMessageMenuAnchorChanged;
            previous.EffectiveViewportChanged -= OnMessageMenuAnchorChanged;
            previous.DataContextChanged -= OnMessageMenuAnchorChanged;
            previous.DetachedFromVisualTree -= OnMessageMenuAnchorChanged;
        }
        _messageMenuAnchor = anchor;
        if (anchor is not null)
        {
            // A streaming answer can still scroll the transcript under the menu: it follows the message.
            anchor.SizeChanged += OnMessageMenuAnchorChanged;
            anchor.EffectiveViewportChanged += OnMessageMenuAnchorChanged;
            anchor.DataContextChanged += OnMessageMenuAnchorChanged;
            anchor.DetachedFromVisualTree += OnMessageMenuAnchorChanged;
        }
    }

    private void OnMessageMenuAnchorChanged(object? sender, EventArgs e) => QueueMessageMenuLayout();

    private void OnMessageMenuLayerSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_shell?.Chat.IsMessageMenuOpen == true)
            QueueMessageMenuLayout();
    }

    private void OnMessageMenuDismissPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        _shell?.Chat.CloseMessageActionsCommand.Execute(null);
    }
}
