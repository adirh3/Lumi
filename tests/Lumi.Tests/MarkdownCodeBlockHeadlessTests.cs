using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class MarkdownCodeBlockHeadlessTests
{
    private const string LongLine =
        "var message = \"This source line contains enough words to exceed a narrow code block several times without changing the original source text when it wraps.\";";

    [Theory]
    [InlineData("", LongLine)]
    [InlineData("text", LongLine)]
    [InlineData("txt", LongLine)]
    [InlineData("plain", LongLine)]
    [InlineData("plaintext", LongLine)]
    [InlineData("plain-text", LongLine)]
    [InlineData("markdown", LongLine)]
    [InlineData("md", LongLine)]
    [InlineData("  TeXt  ", LongLine)]
    [InlineData("text", "abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz0123456789")]
    public async Task CodeBlock_WrapsAtAvailableWidthAndReflowsAfterResize(string language, string source)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var markdown = new StrataMarkdown
            {
                Markdown = $"```{language}\n{source}\n```",
                IsInline = true,
                StreamingRebuildThrottleMs = 0,
            };
            var window = new Window { Width = 320, Height = 600, Content = markdown };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var block = markdown.GetVisualDescendants().OfType<StrataCodeBlock>().Single();
                var presenter = block.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
                var scroll = markdown.GetVisualDescendants().OfType<ScrollViewer>().Single();
                var narrowLines = presenter.TextLayout.TextLines.Count;

                Assert.Equal(TextWrapping.Wrap, presenter.TextWrapping);
                Assert.Empty(markdown.GetVisualDescendants().OfType<ToggleButton>());
                Assert.True(narrowLines > 1);
                Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
                Assert.True(block.Bounds.Height >= narrowLines * StrataCodeBlock.CodeLineHeight);
                presenter.SelectionStart = 0;
                presenter.SelectionEnd = source.Length;
                Assert.Equal(source, presenter.SelectedText);

                window.Width = 800;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.True(presenter.TextLayout.TextLines.Count < narrowLines);
                Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("csharp")]
    [InlineData("json")]
    [InlineData("bash")]
    [InlineData("mdx")]
    [InlineData("unknown-language")]
    public async Task OtherLanguages_KeepSourceLinesAndScrollHorizontally(string language)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var markdown = new StrataMarkdown
            {
                Markdown = $"```{language}\n{LongLine}\n```",
                IsInline = true,
                StreamingRebuildThrottleMs = 0,
            };
            var window = new Window { Width = 320, Height = 300, Content = markdown };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var block = markdown.GetVisualDescendants().OfType<StrataCodeBlock>().Single();
                var presenter = block.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
                var scroll = markdown.GetVisualDescendants().OfType<ScrollViewer>().Single();

                Assert.Equal(TextWrapping.NoWrap, presenter.TextWrapping);
                Assert.Single(presenter.TextLayout.TextLines);
                Assert.Equal(ScrollBarVisibility.Auto, scroll.HorizontalScrollBarVisibility);
                Assert.True(scroll.Extent.Width > scroll.Viewport.Width);
                Assert.Empty(markdown.GetVisualDescendants().OfType<ToggleButton>());
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task StandaloneCodeBlock_DefaultRemainsUnwrappedInNarrowScrollHost()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var block = new StrataCodeBlock { Language = "csharp", Text = LongLine };
            var scroll = new ScrollViewer
            {
                Content = block,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            var window = new Window { Width = 320, Height = 180, Content = scroll };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var presenter = block.GetVisualDescendants().OfType<SelectableTextBlock>().Single();

                Assert.Equal(TextWrapping.NoWrap, block.TextWrapping);
                Assert.Equal(TextWrapping.NoWrap, presenter.TextWrapping);
                Assert.Single(presenter.TextLayout.TextLines);
                Assert.True(scroll.Extent.Width > scroll.Viewport.Width);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task HorizontalScrollbar_TrackClickAndThumbDragMoveTheContent()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var markdown = new StrataMarkdown
            {
                Markdown = $"```csharp\n{string.Join(" ", Enumerable.Repeat(LongLine, 6))}\n```",
                IsInline = true,
                StreamingRebuildThrottleMs = 0,
            };
            var window = new Window { Width = 400, Height = 200, Content = markdown };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var scroll = markdown.GetVisualDescendants().OfType<ScrollViewer>().Single();
                var bar = scroll.GetVisualDescendants().OfType<ScrollBar>()
                    .Single(control => control.Name == "PART_HorizontalScrollBar");
                var track = bar.GetVisualDescendants().OfType<Track>().Single();
                var thumb = track.Thumb!;
                Assert.True(scroll.Extent.Width > scroll.Viewport.Width);
                Assert.True(bar.IsVisible);

                var point = track.TranslatePoint(new Point(track.Bounds.Width - 10, track.Bounds.Height / 2), window)!.Value;
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                Assert.True(scroll.Offset.X >= scroll.Viewport.Width * 0.8,
                    $"A track click moved only {scroll.Offset.X}px for a {scroll.Viewport.Width}px viewport.");

                var beforeDrag = scroll.Offset.X;
                var start = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(start + new Vector(40, 0), RawInputModifiers.LeftMouseButton);
                window.MouseUp(start + new Vector(40, 0), MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                Assert.True(scroll.Offset.X > beforeDrag, "Dragging the horizontal thumb must move the code.");
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("csharp", TextWrapping.NoWrap)]
    [InlineData("text", TextWrapping.Wrap)]
    [InlineData("md", TextWrapping.Wrap)]
    public async Task StreamingCode_PreservesLanguageWrappingAndCopiesOriginalSource(string language, TextWrapping wrapping)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var markdown = new StrataMarkdown
            {
                Markdown = $"```{language}\n{LongLine}\n```",
                IsInline = true,
                StreamingRebuildThrottleMs = 0,
            };
            var window = new Window { Width = 320, Height = 500, Content = markdown };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var block = markdown.GetVisualDescendants().OfType<StrataCodeBlock>().Single();
                var source = LongLine + "\n    Console.WriteLine(message);";
                markdown.Markdown = $"```{language}\n{source}\n```";
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                Assert.Same(block, markdown.GetVisualDescendants().OfType<StrataCodeBlock>().Single());
                Assert.Equal(source, block.Text);
                Assert.Equal(wrapping, block.TextWrapping);
                Assert.Empty(markdown.GetVisualDescendants().OfType<ToggleButton>());

                var copy = markdown.GetVisualDescendants().OfType<Button>()
                    .Single(control => control.Classes.Contains("subtle"));
                Click(window, copy);
                Assert.Equal(source, await window.Clipboard!.TryGetTextAsync());
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
