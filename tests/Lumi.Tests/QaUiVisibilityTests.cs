using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Lumi.Qa;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
[Trait("Category", "CoreQA")]
public sealed class QaUiVisibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealizedControlsOutsideTheScrollViewportAreNotVisible(bool horizontal)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var near = new Border { Width = 20, Height = 20 };
            var far = new Border { Width = 20, Height = 20 };
            var canvas = new Canvas { Width = 500, Height = 500, Children = { near, far } };
            Canvas.SetLeft(near, 10);
            Canvas.SetTop(near, 10);
            Canvas.SetLeft(far, horizontal ? 300 : 10);
            Canvas.SetTop(far, horizontal ? 10 : 300);
            var scroller = new ScrollViewer
            {
                Content = canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var window = new Window { Width = 200, Height = 150, Content = scroller };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.True(far.IsEffectivelyVisible);
                Assert.True(far.Bounds.Height > 0);
                Assert.True(QaUiVisibility.IsVisibleWithin(near, window));
                Assert.False(QaUiVisibility.IsVisibleWithin(far, window));
                scroller.Offset = horizontal ? new Vector(280, 0) : new Vector(0, 280);
                window.UpdateLayout();
                Assert.True(QaUiVisibility.IsVisibleWithin(far, window));
                Assert.False(QaUiVisibility.IsVisibleWithin(near, window));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ClippedAndTransparentAncestorsAndUnrelatedTreesAreNotVisible()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var child = new Border { Width = 20, Height = 20 };
            var canvas = new Canvas
            {
                Width = 200, Height = 200, Children = { child },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
            };
            var clip = new Border { Width = 40, Height = 40, ClipToBounds = true, Child = canvas };
            var window = new Window { Width = 200, Height = 150, Content = clip };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.True(QaUiVisibility.IsVisibleWithin(child, window));
                Canvas.SetTop(child, 80);
                window.UpdateLayout();
                Assert.True(child.IsEffectivelyVisible);
                Assert.False(QaUiVisibility.IsVisibleWithin(child, window));
                Canvas.SetTop(child, 0);
                window.UpdateLayout();
                clip.Opacity = 0;
                Assert.False(QaUiVisibility.IsVisibleWithin(child, window));
                clip.Opacity = 1;
                Assert.True(QaUiVisibility.IsVisibleWithin(child, window));
                Assert.False(QaUiVisibility.IsVisibleWithin(child, new Border()));
                clip.IsVisible = false;
                Assert.False(QaUiVisibility.IsVisibleWithin(child, window));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }
}
