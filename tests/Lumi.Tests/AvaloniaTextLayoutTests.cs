using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class AvaloniaTextLayoutTests
{
    [Theory]
    [InlineData("abc", 0)]
    [InlineData("abc", 1)]
    [InlineData("abc", 3)]
    [InlineData("a\r\nb\r\n", 3)]
    public async Task DirectEmptyLineRange_ReturnsNoBounds(string text, int start)
    {
        using var session = HeadlessTestSession.Start(typeof(AvaloniaTextInputTestApp));
        await session.Dispatch(() =>
        {
            using var layout = new TextLayout(text, Typeface.Default, 14, Brushes.Black);

            Assert.Empty(layout.TextLines[0].GetTextBounds(start, 0));
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    public async Task EmptySelection_ReturnsNoGeometryAtAnyOffset(int start)
    {
        using var session = HeadlessTestSession.Start(typeof(AvaloniaTextInputTestApp));
        await session.Dispatch(() =>
        {
            using var layout = new TextLayout("first\nsecond", Typeface.Default, 14, Brushes.Black);

            Assert.Empty(layout.HitTestTextRange(start, 0));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SelectionAtLineBoundary_StopsAtTheSelectedLine()
    {
        using var session = HeadlessTestSession.Start(typeof(AvaloniaTextInputTestApp));
        await session.Dispatch(() =>
        {
            using var layout = new TextLayout("first\nsecond", Typeface.Default, 14, Brushes.Black);
            var firstLine = layout.TextLines[0];

            var selection = layout.HitTestTextRange(0, firstLine.Length).ToArray();

            Assert.NotEmpty(selection);
            Assert.All(selection, rectangle => Assert.Equal(0, rectangle.Y));
            Assert.Contains(layout.HitTestTextRange(0, 12), rectangle => rectangle.Y >= firstLine.Height);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CollapsedSelectionAtWindowsLineBreak_ReturnsNoGeometry()
    {
        using var session = HeadlessTestSession.Start(typeof(AvaloniaTextInputTestApp));
        await session.Dispatch(() =>
        {
            var text = new SelectableTextBlock { Text = "a\r\nb\r\n", FontSize = 14 };
            text.Measure(new Size(200, 100));
            text.Arrange(new Rect(text.DesiredSize));
            text.SelectionStart = 2;
            text.SelectionEnd = 3;

            Assert.Equal(3, text.SelectionStart);
            Assert.Equal(3, text.SelectionEnd);
            Assert.Empty(text.TextLayout.HitTestTextRange(text.SelectionStart, text.SelectionEnd - text.SelectionStart));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SelectedFormattedText_RendersWithoutDroppingTheSelection()
    {
        using var session = HeadlessTestSession.Start(typeof(AvaloniaTextInputTestApp));
        await session.Dispatch(() =>
        {
            var text = new SelectableTextBlock
            {
                Width = 100,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                SelectionBrush = Brushes.Blue,
                Inlines = new InlineCollection
                {
                    new Run("First ") { FontWeight = FontWeight.Bold },
                    new Run("line\u200b"),
                    new LineBreak(),
                    new Run("\u05e9\u05dc\u05d5\u05dd"),
                    new LineBreak(),
                    new Run("Last line")
                }
            };
            text.Measure(new Size(100, 300));
            text.Arrange(new Rect(text.DesiredSize));
            text.SelectAll();
            text.Measure(new Size(100, 300));
            text.Arrange(new Rect(text.DesiredSize));

            using var context = new DrawingGroup().Open();
            text.Render(context);

            Assert.Equal(text.Inlines.Text, text.SelectedText);
            Assert.NotEmpty(text.TextLayout.HitTestTextRange(text.SelectionStart, text.SelectionEnd - text.SelectionStart));
        }, CancellationToken.None);
    }
}
