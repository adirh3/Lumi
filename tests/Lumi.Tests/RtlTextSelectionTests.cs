using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
[Trait(HeadlessTestSession.HostTrait, HeadlessTestSession.OwnHost)]
public sealed class RtlTextSelectionTests
{
    [Theory]
    [InlineData(FlowDirection.LeftToRight, false, "אחד שני שלישי רביעי", "שני")]
    [InlineData(FlowDirection.LeftToRight, true, "אחד שני שלישי רביעי", "שני")]
    [InlineData(FlowDirection.RightToLeft, false, "אחד שני שלישי רביעי", "שני")]
    [InlineData(FlowDirection.RightToLeft, true, "אחד שני שלישי רביעי", "שני")]
    [InlineData(FlowDirection.RightToLeft, false, "Alpha bravo charlie", "bravo")]
    [InlineData(FlowDirection.LeftToRight, true, "Alpha bravo charlie", "bravo")]
    public async Task MouseDrag_SelectsTheVisibleWordWithoutNeighboringText(
        FlowDirection hostDirection, bool reverse, string source, string word)
    {
        using var session = HeadlessTestSession.Start(typeof(AvaloniaTextInputTestApp));
        await session.Dispatch(async () =>
        {
            var markdown = new StrataMarkdown
            {
                Markdown = source, IsInline = true, StreamingRebuildThrottleMs = 0
            };
            var below = new SelectableTextBlock { Text = "The paragraph below must not be selected." };
            var root = new StackPanel { FlowDirection = hostDirection, Children = { markdown, below } };
            StrataTextSelection.SetIsSelectionScope(root, true);
            var window = Show(root);
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                var text = Assert.Single(markdown.GetVisualDescendants().OfType<SelectableTextBlock>());
                var index = source.IndexOf(word, StringComparison.Ordinal);
                var rtl = text.FlowDirection == FlowDirection.RightToLeft;
                var start = GlyphPointAt(window, text, index, rtl, trailing: false);
                var end = GlyphPointAt(window, text, index + word.Length - 1, rtl, trailing: true);
                await WaitForGlyphsAsync(window, text, start, end);

                Drag(window, reverse ? end : start, reverse ? start : end);

                Assert.Equal(word, text.SelectedText);
                Assert.True(string.IsNullOrEmpty(below.SelectedText), below.SelectedText);
                Assert.Equal(word, StrataTextSelection.GetSelectedText(root));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight, false)]
    [InlineData(FlowDirection.RightToLeft, true)]
    public async Task MouseDrag_AcrossWrappedHebrewLines_TracksTheRenderedLines(
        FlowDirection hostDirection, bool reverse)
    {
        using var session = HeadlessTestSession.Start(typeof(AvaloniaTextInputTestApp));
        await session.Dispatch(async () =>
        {
            const string source = "אחד שני שלישי רביעי חמישי שישי שביעי שמיני תשיעי עשירי";
            var markdown = new StrataMarkdown
            {
                Markdown = source, IsInline = true, StreamingRebuildThrottleMs = 0, Width = 220
            };
            var below = new SelectableTextBlock { Text = "Unselected text below." };
            var root = new StackPanel { FlowDirection = hostDirection, Children = { markdown, below } };
            StrataTextSelection.SetIsSelectionScope(root, true);
            var window = Show(root);
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                var text = Assert.Single(markdown.GetVisualDescendants().OfType<SelectableTextBlock>());
                Assert.True(text.TextLayout.TextLines.Count > 1);
                var endIndex = source.IndexOf("שמיני", StringComparison.Ordinal) + 3;
                var start = GlyphPointAt(window, text, 4, rtl: true, trailing: false);
                var end = GlyphPointAt(window, text, endIndex - 1, rtl: true, trailing: true);
                Assert.True(end.Y > start.Y);
                await WaitForGlyphsAsync(window, text, start, end);
                Drag(window, reverse ? end : start, reverse ? start : end);

                Assert.Equal(source[4..endIndex], text.SelectedText);
                Assert.True(string.IsNullOrEmpty(below.SelectedText), below.SelectedText);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public async Task MouseDrag_InlineCodeInHebrewProse_SelectsOnlyTheCode(FlowDirection hostDirection)
    {
        using var session = HeadlessTestSession.Start(typeof(AvaloniaTextInputTestApp));
        await session.Dispatch(async () =>
        {
            const string code = "Call(value)";
            var markdown = new StrataMarkdown
            {
                Markdown = $"לפני `{code}` ואחרי **מילים מודגשות** עם [קישור](https://example.com)",
                IsInline = true, StreamingRebuildThrottleMs = 0, FlowDirection = hostDirection
            };
            var window = Show(markdown);
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                var text = Assert.Single(markdown.GetVisualDescendants().OfType<SelectableTextBlock>());
                Assert.Equal(FlowDirection.RightToLeft, text.FlowDirection);
                var index = text.Inlines!.Text.IndexOf(code, StringComparison.Ordinal);
                Assert.True(index >= 0);
                var start = GlyphPointAt(window, text, index, rtl: false, trailing: false);
                var end = GlyphPointAt(window, text, index + code.Length - 1, rtl: false, trailing: true);
                await WaitForGlyphsAsync(window, text, start, end);
                Drag(window, start, end);

                Assert.Equal(code, text.SelectedText);
                Assert.Equal(code, StrataTextSelection.GetSelectedText(markdown));
                Assert.Equal($"`{code}`", ChatClipboardData.SelectionMarkdown(StrataTextSelection.GetSelection(markdown)));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    private static Window Show(Control content)
    {
        var window = new Window { Width = 640, Height = 240, Content = content };
        window.Styles.Add(new StyleInclude(new Uri("avares://StrataTheme/"))
        {
            Source = new Uri("avares://StrataTheme/StrataTheme.axaml")
        });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    private static void Drag(Window window, Point start, Point end)
    {
        window.MouseMove(start);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        window.MouseUp(end, MouseButton.Left);
    }

    private static Point GlyphPointAt(Window window, SelectableTextBlock text, int index, bool rtl, bool trailing)
    {
        var glyph = Assert.Single(text.TextLayout.HitTestTextRange(index, 1));
        var x = glyph.Left + glyph.Width * (rtl != trailing ? 0.75 : 0.25);
        return text.TranslatePoint(new Point(x + text.Padding.Left, glyph.Center.Y + text.Padding.Top), window)!.Value;
    }

    private static async Task WaitForGlyphsAsync(Window window, SelectableTextBlock text, Point start, Point end)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (ReferenceEquals(text, window.InputHitTest(start)) && ReferenceEquals(text, window.InputHitTest(end)))
                return;
            await Task.Delay(16);
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(2));

        Assert.Same(text, window.InputHitTest(start));
        Assert.Same(text, window.InputHitTest(end));
    }
}
