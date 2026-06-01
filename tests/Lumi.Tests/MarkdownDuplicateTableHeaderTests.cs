using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

        await session.Dispatch(async () =>
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

            await Task.Delay(150);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // Both tables have the same header "| Model | Size |".
            // Before the fix, the second table stole the first's control,
            // leaving a gap at the Washing Machines position.
            var tables = md.GetVisualDescendants()
                .OfType<Border>()
                .Where(b => b.Classes.Contains("strata-md-table"))
                .ToList();

            Assert.Equal(2, tables.Count);

            // Verify both tables are actually visible (have non-zero bounds)
            foreach (var table in tables)
            {
                Assert.True(table.Bounds.Width > 0, "Table should have non-zero width");
                Assert.True(table.Bounds.Height > 0, "Table should have non-zero height");
            }

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TwoTablesWithDifferentHeaders_BothRender()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
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

            await Task.Delay(150);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var tables = md.GetVisualDescendants()
                .OfType<Border>()
                .Where(b => b.Classes.Contains("strata-md-table"))
                .ToList();

            Assert.Equal(2, tables.Count);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TableCopyButton_OffersMarkdownAndHtmlChoices()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
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

            await Task.Delay(150);
            await PumpAsync();

            var copyButton = md.GetVisualDescendants()
                .OfType<Button>()
                .Single(IsTableCopyButton);

            Assert.True(copyButton.Opacity > 0, "The copy icon should be visible without requiring a perfect hover target.");

            Assert.NotNull(copyButton.ContextMenu);
            var menuItems = copyButton.ContextMenu!.Items
                .OfType<MenuItem>()
                .ToArray();
            var markdownItem = Assert.Single(menuItems, static item => string.Equals(item.Header?.ToString(), "Copy as Markdown", StringComparison.Ordinal));
            var htmlItem = Assert.Single(menuItems, static item => string.Equals(item.Header?.ToString(), "Copy as HTML", StringComparison.Ordinal));

            markdownItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Task.Delay(50);
            await PumpAsync();

            var clipboard = TopLevel.GetTopLevel(copyButton)?.Clipboard;
            Assert.NotNull(clipboard);
            Assert.Equal("| Model | Size |\n| --- | --- |\n| Bosch | 8 kg |\n| LG | 7 kg |", NormalizeLineEndings(await clipboard.TryGetTextAsync() ?? string.Empty));

            htmlItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Task.Delay(50);
            await PumpAsync();

            Assert.Equal("<table><thead><tr><th>Model</th><th>Size</th></tr></thead><tbody><tr><td>Bosch</td><td>8 kg</td></tr><tr><td>LG</td><td>7 kg</td></tr></tbody></table>", await clipboard.TryGetTextAsync());

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TwoTablesWithSameHeader_GetSeparateCopyButtons()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
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

            await Task.Delay(150);
            await PumpAsync();

            var copyButtons = md.GetVisualDescendants()
                .OfType<Button>()
                .Where(IsTableCopyButton)
                .ToList();

            Assert.Equal(2, copyButtons.Count);

            window.Close();
        }, CancellationToken.None);
    }

    private static bool IsTableCopyButton(Button button)
        => string.Equals(button.Name, "PART_CopyTableButton", StringComparison.Ordinal)
           && button.Classes.Contains("strata-md-table-copy-button");

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n');
}
