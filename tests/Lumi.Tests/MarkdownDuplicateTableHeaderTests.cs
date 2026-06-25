using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class MarkdownDuplicateTableHeaderTests
{
    [Fact]
    public async Task TwoTablesWithSameHeader_BothRender()
    {
        using var session = HeadlessTestSession.Start();

        int tableCount = -1;
        bool allTablesVisible = false;

        await session.Dispatch(() =>
        {
            var md = new StrataMarkdown
            {
                Markdown = "Intro\n\n---\n\n## Washing Machines\n\n| Model | Size |\n|-------|------|\n| Bosch | 8 kg |\n\nBest value\n\n---\n\n## Dryers\n\n| Model | Size |\n|-------|------|\n| LG | 7 kg |",
                IsInline = true,
            };

            var window = new Window
            {
                Width = 800,
                Height = 600,
                Content = md,
            };
            window.Show();

            Pump();

            // Both tables have the same header "| Model | Size |".
            // Before the fix, the second table stole the first's control,
            // leaving a gap at the Washing Machines position.
            var tables = md.GetVisualDescendants()
                .OfType<Border>()
                .Where(b => b.Classes.Contains("strata-md-table"))
                .ToList();

            tableCount = tables.Count;
            allTablesVisible = tables.All(static table => table.Bounds.Width > 0 && table.Bounds.Height > 0);

            window.Close();
        }, CancellationToken.None);

        Assert.Equal(2, tableCount);
        Assert.True(allTablesVisible, "Both tables should be visible with non-zero bounds.");
    }

    [Fact]
    public async Task TwoTablesWithDifferentHeaders_BothRender()
    {
        using var session = HeadlessTestSession.Start();

        int tableCount = -1;

        await session.Dispatch(() =>
        {
            var md = new StrataMarkdown
            {
                Markdown = "## Table A\n\n| Name | Age |\n|------|-----|\n| Alice | 30 |\n\n## Table B\n\n| City | Pop |\n|------|-----|\n| NYC | 8M |",
                IsInline = true,
            };

            var window = new Window
            {
                Width = 800,
                Height = 600,
                Content = md,
            };
            window.Show();

            Pump();

            var tables = md.GetVisualDescendants()
                .OfType<Border>()
                .Where(b => b.Classes.Contains("strata-md-table"))
                .ToList();

            tableCount = tables.Count;

            window.Close();
        }, CancellationToken.None);

        Assert.Equal(2, tableCount);
    }

    [Fact]
    public async Task TableCopyButton_OffersMarkdownAndHtmlChoices()
    {
        using var session = HeadlessTestSession.Start();

        int copyButtonCount = -1;
        double copyButtonOpacity = -1;
        bool hasContextMenu = false;
        string[] menuHeaders = [];
        bool hasClipboard = false;
        string markdownClipboardText = string.Empty;
        string htmlClipboardText = string.Empty;

        await session.Dispatch(() =>
        {
            var md = new StrataMarkdown
            {
                Markdown = "| Model | Size |\n|-------|------|\n| Bosch | 8 kg |\n| LG | 7 kg |",
                IsInline = true,
            };

            var window = new Window
            {
                Width = 800,
                Height = 600,
                Content = md,
            };
            window.Show();

            Pump();

            var copyButtons = md.GetVisualDescendants()
                .OfType<Button>()
                .Where(IsTableCopyButton)
                .ToArray();
            copyButtonCount = copyButtons.Length;

            var copyButton = copyButtonCount == 1 ? copyButtons[0] : null;
            if (copyButton is not null)
            {
                copyButtonOpacity = copyButton.Opacity;
                hasContextMenu = copyButton.ContextMenu is not null;
                var menuItems = copyButton.ContextMenu?.Items
                    .OfType<MenuItem>()
                    .ToArray() ?? [];
                menuHeaders = menuItems.Select(static item => item.Header?.ToString() ?? string.Empty).ToArray();

                var markdownItem = menuItems.SingleOrDefault(static item => string.Equals(item.Header?.ToString(), "Copy as Markdown", StringComparison.Ordinal));
                var htmlItem = menuItems.SingleOrDefault(static item => string.Equals(item.Header?.ToString(), "Copy as HTML", StringComparison.Ordinal));

                if (markdownItem is not null)
                {
                    markdownItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Pump();
                }

                var clipboard = TopLevel.GetTopLevel(copyButton)?.Clipboard;
                hasClipboard = clipboard is not null;
                markdownClipboardText = clipboard is null
                    ? string.Empty
                    : NormalizeLineEndings(clipboard.TryGetTextAsync().GetAwaiter().GetResult() ?? string.Empty);

                if (htmlItem is not null)
                {
                    htmlItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Pump();
                    htmlClipboardText = clipboard is null
                        ? string.Empty
                        : clipboard.TryGetTextAsync().GetAwaiter().GetResult() ?? string.Empty;
                }
            }

            window.Close();
        }, CancellationToken.None);

        Assert.Equal(1, copyButtonCount);
        Assert.True(copyButtonOpacity > 0, "The copy icon should be visible without requiring a perfect hover target.");
        Assert.True(hasContextMenu, "The table copy button should expose a copy format menu.");
        Assert.Contains("Copy as Markdown", menuHeaders);
        Assert.Contains("Copy as HTML", menuHeaders);
        Assert.True(hasClipboard, "The test window should expose a clipboard.");
        Assert.Equal("| Model | Size |\n| --- | --- |\n| Bosch | 8 kg |\n| LG | 7 kg |", markdownClipboardText);
        Assert.Equal("<table><thead><tr><th>Model</th><th>Size</th></tr></thead><tbody><tr><td>Bosch</td><td>8 kg</td></tr><tr><td>LG</td><td>7 kg</td></tr></tbody></table>", htmlClipboardText);
    }

    [Fact]
    public async Task CompactTable_StretchesToCardWidth()
    {
        using var session = HeadlessTestSession.Start();

        double tableWidth = 0;
        double headerWidthTotal = 0;
        double minHeaderWidth = 0;

        await session.Dispatch(() =>
        {
            var md = new StrataMarkdown
            {
                Markdown = "| Item | Expected |\n| --- | --- |\n| Markdown | rendered |\n| Sources | visible below |",
                IsInline = true,
            };

            var window = new Window
            {
                Width = 520,
                Height = 320,
                Content = md,
            };
            window.Show();

            Dispatcher.UIThread.RunJobs();
            md.Measure(new Size(500, 280));
            md.Arrange(new Rect(0, 0, 500, 280));
            Dispatcher.UIThread.RunJobs();

            var table = md.GetVisualDescendants()
                .OfType<Border>()
                .Single(b => b.Classes.Contains("strata-md-table"));

            for (var attempt = 0; attempt < 4 && table.Bounds.Width <= 0; attempt++)
            {
                md.Measure(new Size(500, 280));
                md.Arrange(new Rect(0, 0, 500, 280));
                Dispatcher.UIThread.RunJobs();
            }

            var headerCells = table.GetVisualDescendants()
                .OfType<Border>()
                .Where(b => b.Classes.Contains("strata-md-table-header-cell"))
                .ToArray();

            tableWidth = table.Bounds.Width;
            headerWidthTotal = headerCells.Sum(cell => cell.Bounds.Width);
            minHeaderWidth = headerCells.Min(cell => cell.Bounds.Width);

            window.Close();
        }, CancellationToken.None);

        Assert.True(tableWidth > 0, "The compact table should be measured.");
        Assert.True(headerWidthTotal >= tableWidth - 2, "Compact tables should stretch their columns across the card width.");
        Assert.True(minHeaderWidth >= 180, "A two-column compact table should not stay cramped inside a wide card.");
    }

    [Fact]
    public async Task WideTable_WrapsCellsAndHorizontallyScrollsWithoutHint()
    {
        using var session = HeadlessTestSession.Start();

        double extentWidth = 0;
        double viewportWidth = 0;
        double minHeaderWidth = 0;
        double maxHeaderWidth = 0;
        int hintCount = -1;

        await session.Dispatch(() =>
        {
            var md = new StrataMarkdown
            {
                Markdown = """
                    | Model | Store | Size | Panel | Brightness | Gaming | Warranty | Notes |
                    | --- | --- | --- | --- | --- | --- | --- | --- |
                    | LG C4 | Best Buy | 65 inch | OLED evo | Excellent HDR | 144Hz VRR | 1 year | Best mixed-use value |
                    | Samsung S90D | Amazon | 65 inch | QD-OLED | Very bright | 144Hz VRR | 1 year | Strong color volume |
                    """,
                IsInline = true,
            };

            var window = new Window
            {
                Width = 440,
                Height = 360,
                Content = md,
            };
            window.Show();

            Dispatcher.UIThread.RunJobs();
            md.Measure(new Size(420, 320));
            md.Arrange(new Rect(0, 0, 420, 320));
            Dispatcher.UIThread.RunJobs();

            var table = md.GetVisualDescendants()
                .OfType<Border>()
                .Single(b => b.Classes.Contains("strata-md-table"));

            for (var attempt = 0; attempt < 4 && table.Bounds.Width <= 0; attempt++)
            {
                md.Measure(new Size(420, 320));
                md.Arrange(new Rect(0, 0, 420, 320));
                Dispatcher.UIThread.RunJobs();
            }

            var headerCells = table.GetVisualDescendants()
                .OfType<Border>()
                .Where(b => b.Classes.Contains("strata-md-table-header-cell"))
                .ToArray();

            extentWidth = headerCells.Sum(cell => cell.Bounds.Width);
            viewportWidth = table.Bounds.Width;
            minHeaderWidth = headerCells.Min(cell => cell.Bounds.Width);
            maxHeaderWidth = headerCells.Max(cell => cell.Bounds.Width);
            hintCount = table.GetVisualDescendants()
                .OfType<Border>()
                .Count(b => b.Classes.Contains("strata-md-table-scroll-hint"));

            window.Close();
        }, CancellationToken.None);

        Assert.True(viewportWidth > 0, "The table should be measured.");
        Assert.True(extentWidth > viewportWidth + 1, "Wide tables should still allow horizontal scrolling when there are many columns.");
        Assert.True(minHeaderWidth >= 64, "Wide table columns should keep a readable minimum width.");
        Assert.True(maxHeaderWidth <= 128, "Wide table columns should favor wrapping instead of becoming huge.");
        Assert.Equal(0, hintCount);
    }

    [Fact]
    public async Task TwoTablesWithSameHeader_GetSeparateCopyButtons()
    {
        using var session = HeadlessTestSession.Start();

        int copyButtonCount = -1;

        await session.Dispatch(() =>
        {
            var md = new StrataMarkdown
            {
                Markdown = "| Model | Size |\n|-------|------|\n| Bosch | 8 kg |\n\n| Model | Size |\n|-------|------|\n| LG | 7 kg |",
                IsInline = true,
            };

            var window = new Window
            {
                Width = 800,
                Height = 600,
                Content = md,
            };
            window.Show();

            Pump();

            var copyButtons = md.GetVisualDescendants()
                .OfType<Button>()
                .Where(IsTableCopyButton)
                .ToList();

            copyButtonCount = copyButtons.Count;

            window.Close();
        }, CancellationToken.None);

        Assert.Equal(2, copyButtonCount);
    }

    private static bool IsTableCopyButton(Button button)
        => string.Equals(button.Name, "PART_CopyTableButton", StringComparison.Ordinal)
           && button.Classes.Contains("strata-md-table-copy-button");

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n');
}
