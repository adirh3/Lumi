using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Lumi.Views.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// The list behind an opened "Worked for" row shows its source from the top a few items at a time,
/// and its shown items stay the leading items of the source however the source changes meanwhile.
/// </summary>
[Collection("Headless UI")]
public sealed class ProgressiveItemsControlTests
{
    [Fact]
    public async Task IndependentGroupsFillNearTheirOwnEndRatherThanTheEndOfTheWholePage()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var firstSource = new ObservableCollection<string>(["first 0", "first 1"]);
            var secondSource = new ObservableCollection<string>(Enumerable.Range(0, 24).Select(index => $"second {index}"));
            var lastSource = new ObservableCollection<string>();
            var template = new FuncDataTemplate<string>((item, _) => new TextBlock { Text = item, Height = 40 });
            var first = new ProgressiveItemsControl { Source = firstSource, InitialItemCount = 3, ItemTemplate = template };
            var second = new ProgressiveItemsControl { Source = secondSource, InitialItemCount = 3, ItemTemplate = template };
            var last = new ProgressiveItemsControl { Source = lastSource, InitialItemCount = 3, ItemTemplate = template };
            var scroll = new ScrollViewer
            {
                Content = new StackPanel { Children = { first, second, last } }
            };
            var window = new Window { Width = 400, Height = 300, Content = scroll };
            window.Show();
            try
            {
                window.UpdateLayout();
                await second.ShowThroughAsync(secondSource[^1]);
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height * 3);

                for (var index = 2; index < 6; index++)
                    firstSource.Add($"first {index}");
                window.UpdateLayout();
                Assert.Equal(3, first.ShownCount);
                Assert.True(await FillsAsync(first, firstSource), "Later groups must not prevent an earlier group from filling.");
                Assert.Equal(0, scroll.Offset.Y);

                for (var index = 0; index < 6; index++)
                    lastSource.Add($"last {index}");
                window.UpdateLayout();
                await Task.Delay(50);
                Assert.Equal(3, last.ShownCount);

                scroll.Offset = new Vector(0, scroll.Extent.Height);
                Assert.True(await FillsAsync(last, lastSource), "A group below the viewport should fill when scrolled into view.");
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ACustomFirstPageStaysBoundedWhenTheSourceIsRepopulated()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var source = new ObservableCollection<string>();
            var list = new ProgressiveItemsControl { Source = source, InitialItemCount = 3 };
            var window = new Window { Width = 400, Height = 300, Content = list };
            window.Show();
            try
            {
                window.UpdateLayout();
                for (var index = 0; index < 40; index++)
                    source.Add($"item {index}");
                window.UpdateLayout();
                Assert.Equal(3, list.ShownCount);
                Assert.True(IsLeading(list, source));

                source.Clear();
                foreach (var item in Enumerable.Range(0, 40))
                    source.Add($"replacement {item}");
                Assert.Equal(3, list.ShownCount);
                Assert.True(IsLeading(list, source));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ShownItems_StayTheLeadingItemsOfTheSource()
    {
        using var session = HeadlessTestSession.Start();
        int withTheList = -1, whenDetached = -1, whenReattached = -1;
        bool leadingMidFill = false, completeAfterMidFillChanges = false, completeAfterLaterChanges = false, completeAfterMove = false;

        await session.Dispatch(async () =>
        {
            var source = new ObservableCollection<string>(Enumerable.Range(0, 40).Select(index => $"step {index}"));
            var list = new ProgressiveItemsControl { Source = source };
            var host = new ContentControl { Content = list };
            var window = new Window { Width = 400, Height = 300, Content = host };
            window.Show();
            try
            {
                window.UpdateLayout();
                withTheList = list.ShownCount;

                // Changes while only the first page is shown: inside it, at its end, and beyond it.
                source.Insert(0, "inserted first");
                source.RemoveAt(5);
                source[3] = "replaced";
                source.Insert(list.ShownCount, "at the end of the page");
                source.Add("appended");
                leadingMidFill = IsLeading(list, source) && list.ShownCount < source.Count;

                completeAfterMidFillChanges = await FillsAsync(list, source);

                source.Add("appended later");
                source.RemoveAt(0);
                source.Insert(10, "inserted later");
                completeAfterLaterChanges = list.ShownCount == source.Count && IsLeading(list, source);

                source.Move(2, 30);
                completeAfterMove = await FillsAsync(list, source);

                host.Content = null;
                whenDetached = list.ShownCount;
                host.Content = list;
                window.UpdateLayout();
                whenReattached = list.ShownCount;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

        Assert.Equal(ProgressiveItemsControl.FirstPageSize, withTheList);
        Assert.True(leadingMidFill, "Changes during the fill should keep the shown items leading the source.");
        Assert.True(completeAfterMidFillChanges, "The fill should finish with every source item shown in order.");
        Assert.True(completeAfterLaterChanges, "Once complete, the list should follow the source at once.");
        Assert.True(completeAfterMove, "A move should leave every source item shown in order.");
        Assert.Equal(0, whenDetached);
        Assert.Equal(ProgressiveItemsControl.FirstPageSize, whenReattached);
    }

    private static bool IsLeading(ProgressiveItemsControl list, ObservableCollection<string> source)
        => list.Items.Cast<object>().SequenceEqual(source.Take(list.ShownCount));

    private static async Task<bool> FillsAsync(ProgressiveItemsControl list, ObservableCollection<string> source)
    {
        for (var i = 0; i < 200 && list.ShownCount < source.Count; i++)
        {
            await Task.Delay(5);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        return list.ShownCount == source.Count && IsLeading(list, source);
    }
}
