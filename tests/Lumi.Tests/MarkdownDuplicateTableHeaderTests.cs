using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Input;
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
    public async Task TableCopyButton_OffersPlainTextMarkdownAndRichTextChoices()
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
            try
            {
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
                Assert.Equal(["Plain text", "Markdown", "Rich text"],
                    menuItems.Select(static item => item.Header?.ToString()));

                var clipboard = TopLevel.GetTopLevel(copyButton)?.Clipboard;
                Assert.NotNull(clipboard);
                const string plainText = "Model\tSize\nBosch\t8 kg\nLG\t7 kg";
                const string markdown = "| Model | Size |\n| --- | --- |\n| Bosch | 8 kg |\n| LG | 7 kg |";

                menuItems[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await PumpAsync();
                Assert.Equal(plainText, NormalizeLineEndings(await clipboard.TryGetTextAsync() ?? string.Empty));

                menuItems[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await PumpAsync();
                using (var data = await clipboard.TryGetDataAsync())
                {
                    Assert.NotNull(data);
                    Assert.Equal(markdown, NormalizeLineEndings(await data.TryGetTextAsync() ?? string.Empty));
                    Assert.Equal(markdown, NormalizeLineEndings(
                        await data.TryGetValueAsync(DataFormat.CreateStringPlatformFormat("text/markdown")) ?? string.Empty));
                }

                menuItems[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await PumpAsync();
                using (var data = await clipboard.TryGetDataAsync())
                {
                    Assert.NotNull(data);
                    Assert.Equal(plainText, NormalizeLineEndings(await data.TryGetTextAsync() ?? string.Empty));
                    var html = await data.TryGetValueAsync(DataFormat.CreateStringPlatformFormat("text/html"));
                    Assert.NotNull(html);
                    var table = Assert.Single(XDocument.Parse(html).Descendants("table"));
                    Assert.Equal(["Model", "Size"], table.Descendants("th").Select(static cell => cell.Value));
                    Assert.Equal(plainText, string.Join("\n", table.Descendants("tr")
                        .Select(static row => string.Join("\t", row.Elements().Select(static cell => cell.Value)))));
                }
            }
            finally
            {
                window.Close();
            }
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
