using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ScrollbarInteractionTests
{
    [Theory]
    [InlineData(Orientation.Vertical, 20)]
    [InlineData(Orientation.Horizontal, 20)]
    [InlineData(Orientation.Vertical, 1)]
    [InlineData(Orientation.Horizontal, 1)]
    public async Task ThumbDrag_KeepsCaptureAcrossRepeatedMovesAndOutsideTheTrack(Orientation orientation, double step)
    {
        using var session = HeadlessTestSession.Start(typeof(SkiaHeadlessTestApp));
        await session.Dispatch(async () =>
        {
            var vertical = orientation == Orientation.Vertical;
            var scroll = new ScrollViewer
            {
                Content = new Border { Width = 2000, Height = 2000, Background = Brushes.Gray },
                HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            };
            var window = new Window { Width = 400, Height = 400, Content = scroll };
            IPointer? pointer = null;
            window.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer,
                Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                using (window.CaptureRenderedFrame()) { }
                var bar = scroll.GetVisualDescendants().OfType<ScrollBar>()
                    .Single(control => control.Orientation == orientation);
                var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
                var start = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
                window.MouseMove(start);
                window.MouseDown(start, MouseButton.Left);
                Assert.NotNull(pointer);
                var previous = 0d;
                for (var index = 1; index <= 6; index++)
                {
                    var delta = vertical ? new Vector(-40, index * step) : new Vector(index * step, -40);
                    window.MouseMove(start + delta, RawInputModifiers.LeftMouseButton);
                    window.UpdateLayout();
                    await Task.Delay(35);
                    using (window.CaptureRenderedFrame()) { }
                    var offset = vertical ? scroll.Offset.Y : scroll.Offset.X;
                    Assert.True(ReferenceEquals(pointer.Captured, thumb)
                        || pointer.Captured is Visual captured && thumb.IsVisualAncestorOf(captured),
                        $"Move {index} lost the scrollbar thumb's pointer capture.");
                    Assert.True(offset > previous, $"Move {index} stopped dragging at {offset}px (previous {previous}px).");
                    previous = offset;
                }
                window.MouseUp(start + (vertical ? new Vector(-40, 6 * step) : new Vector(6 * step, -40)), MouseButton.Left);
                Assert.Null(pointer.Captured);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }
}
