using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
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
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);

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
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);

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
}
