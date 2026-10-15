using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
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
