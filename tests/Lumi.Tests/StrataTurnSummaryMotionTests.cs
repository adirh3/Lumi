using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// A reader opening the "Worked for" row reveals its content from the top while it fades in, and
/// closing folds it away the same way, so the transcript below glides instead of jumping. The
/// content keeps its natural layout throughout, and changes made for the reader, such as opening
/// the row to show a search hit, apply at once.
/// </summary>
[Collection("Headless UI")]
public sealed class StrataTurnSummaryMotionTests
{
    private const double ScrollAreaHeight = 150;

    [Fact]
    public async Task ReaderToggle_RevealsAndFoldsTheContentWithoutSqueezingIt()
    {
        using var session = HeadlessTestSession.Start();
        double openMidReveal = -1, openMid = -1, scrollAreaMid = -1, openSettled = -1, grown = -1, closeMid = -1;
        bool settledOpaque = false, closedHidden = false, closedReleasedContent = false;
        List<double> opening = [], closing = [];

        await session.Dispatch(async () =>
        {
            var row = Show();
            try
            {
                await SettleAsync(row.Window);
                var host = ContentHost(row.Summary);

                ClickHeader(row.Window, row.Summary);
                await AdvanceAsync(row.Window, 40);
                openMidReveal = host.Reveal;
                openMid = host.Bounds.Height;
                scrollAreaMid = row.ScrollArea.Bounds.Height;
                opening = await SampleHeightsAsync(row.Window, host, 350);
                openSettled = host.Bounds.Height;
                settledOpaque = host.Reveal >= 0.999 && host.Opacity >= 0.999;

                row.Extra.Height = 80;
                await SettleAsync(row.Window);
                grown = host.Bounds.Height;

                ClickHeader(row.Window, row.Summary);
                await AdvanceAsync(row.Window, 30);
                closeMid = host.Bounds.Height;
                closing = await SampleHeightsAsync(row.Window, host, 300);
                closedHidden = !host.IsVisible;
                closedReleasedContent = row.Summary.DisplayedContent is null;
            }
            finally
            {
                row.Window.Close();
            }
        }, CancellationToken.None);

        Assert.InRange(openMidReveal, 0.01, 0.99);
        Assert.InRange(openMid, 1, openSettled - 1);
        Assert.Equal(ScrollAreaHeight, scrollAreaMid, 1);
        Assert.True(openSettled > ScrollAreaHeight, $"Opened height {openSettled} should fit the content.");
        Assert.True(settledOpaque);
        Assert.Equal(openSettled + 80, grown, 1);
        Assert.InRange(closeMid, 1, grown - 1);
        Assert.True(closedHidden);
        Assert.True(closedReleasedContent);

        // No frame steps back, so the row never flashes shut as it finishes opening, or open as it finishes closing.
        Assert.All(opening.Zip(opening.Skip(1)), step => Assert.True(step.Second >= step.First - 0.5, $"Opening stepped back: {string.Join(", ", opening)}"));
        Assert.All(closing.Zip(closing.Skip(1)), step => Assert.True(step.Second <= step.First + 0.5, $"Closing stepped back: {string.Join(", ", closing)}"));
    }

    [Fact]
    public async Task InterruptedToggle_ContinuesFromWhereTheContentIs()
    {
        using var session = HeadlessTestSession.Start();
        double beforeReverse = -1, afterReverse = -1, reopened = -1;
        bool closedHidden = false;

        await session.Dispatch(async () =>
        {
            var row = Show();
            try
            {
                await SettleAsync(row.Window);
                var host = ContentHost(row.Summary);

                ClickHeader(row.Window, row.Summary);
                await AdvanceAsync(row.Window, 40);
                beforeReverse = host.Reveal;
                ClickHeader(row.Window, row.Summary);
                await AdvanceAsync(row.Window, 20);
                afterReverse = host.Reveal;
                await AdvanceAsync(row.Window, 300);
                closedHidden = !host.IsVisible;

                ClickHeader(row.Window, row.Summary);
                await AdvanceAsync(row.Window, 30);
                ClickHeader(row.Window, row.Summary);
                await AdvanceAsync(row.Window, 15);
                ClickHeader(row.Window, row.Summary);
                await AdvanceAsync(row.Window, 400);
                reopened = host.IsVisible ? host.Reveal : -1;
            }
            finally
            {
                row.Window.Close();
            }
        }, CancellationToken.None);

        Assert.InRange(beforeReverse, 0.01, 0.99);
        Assert.True(afterReverse < beforeReverse, $"Closing from {beforeReverse} should continue down, not restart ({afterReverse}).");
        Assert.True(closedHidden);
        Assert.Equal(1, reopened, 3);
    }

    [Fact]
    public async Task ProgrammaticAndMotionDisabledChanges_ApplyAtOnce()
    {
        using var session = HeadlessTestSession.Start();
        double programmaticReveal = -1, disabledReveal = -1;
        bool programmaticClosed = false, disabledExpanded = false, disabledClosed = false;

        await session.Dispatch(async () =>
        {
            var row = Show();
            try
            {
                await SettleAsync(row.Window);
                var host = ContentHost(row.Summary);

                row.Summary.IsExpanded = true;
                await SettleAsync(row.Window);
                programmaticReveal = host.Reveal;
                row.Summary.IsExpanded = false;
                await SettleAsync(row.Window);
                programmaticClosed = !host.IsVisible && row.Summary.DisplayedContent is null;

                row.Summary.Classes.Add("motion-disabled");
                ClickHeader(row.Window, row.Summary);
                await SettleAsync(row.Window);
                disabledExpanded = row.Summary.IsExpanded;
                disabledReveal = host.Reveal;
                ClickHeader(row.Window, row.Summary);
                await SettleAsync(row.Window);
                disabledClosed = !host.IsVisible;
            }
            finally
            {
                row.Window.Close();
            }
        }, CancellationToken.None);

        Assert.Equal(1, programmaticReveal, 3);
        Assert.True(programmaticClosed);
        Assert.True(disabledExpanded);
        Assert.Equal(1, disabledReveal, 3);
        Assert.True(disabledClosed);
    }

    [Fact]
    public async Task DetachingMidMotion_SettlesTheRow()
    {
        using var session = HeadlessTestSession.Start();
        bool settledOpen = false, stayedOpen = false;

        await session.Dispatch(async () =>
        {
            var row = Show();
            try
            {
                await SettleAsync(row.Window);
                var host = ContentHost(row.Summary);
                var panel = (StackPanel)row.Window.Content!;

                ClickHeader(row.Window, row.Summary);
                await AdvanceAsync(row.Window, 40);
                panel.Children.Remove(row.Summary);
                panel.Children.Add(row.Summary);
                await SettleAsync(row.Window);
                settledOpen = host.IsVisible && host.Reveal >= 0.999 && host.Opacity >= 0.999;

                await AdvanceAsync(row.Window, 100);
                stayedOpen = host.IsVisible && host.Reveal >= 0.999;
            }
            finally
            {
                row.Window.Close();
            }
        }, CancellationToken.None);

        Assert.True(settledOpen);
        Assert.True(stayedOpen);
    }

    private sealed record Row(Window Window, StrataTurnSummary Summary, ScrollViewer ScrollArea, Border Extra);

    private static Row Show()
    {
        var scrollArea = new ScrollViewer { MaxHeight = ScrollAreaHeight, Content = new Border { Height = 400 } };
        var extra = new Border { Height = 0 };
        var summary = new StrataTurnSummary
        {
            Label = "Worked for 8s",
            Content = new StackPanel { Children = { scrollArea, extra } },
        };
        var window = new Window { Width = 480, Height = 640, Content = new StackPanel { Children = { summary } } };
        window.Show();
        return new Row(window, summary, scrollArea, extra);
    }

    private static TurnSummaryRevealHost ContentHost(StrataTurnSummary summary)
        => summary.GetVisualDescendants().OfType<TurnSummaryRevealHost>().Single();

    private static void ClickHeader(Window window, StrataTurnSummary summary)
    {
        var header = summary.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_Header");
        var topLeft = header.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException("PART_Header is not attached to the test window.");
        var point = topLeft + new Point(header.Bounds.Width / 2, header.Bounds.Height / 2);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
    }

    private static async Task<List<double>> SampleHeightsAsync(Window window, TurnSummaryRevealHost host, int milliseconds)
    {
        var heights = new List<double>();
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < milliseconds)
        {
            await AdvanceAsync(window, 16);
            if (host.IsVisible)
                heights.Add(host.Bounds.Height);
        }

        return heights;
    }

    private static async Task AdvanceAsync(Window window, int milliseconds)
    {
        await Task.Delay(milliseconds);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await SettleAsync(window);
    }

    private static async Task SettleAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
    }
}
