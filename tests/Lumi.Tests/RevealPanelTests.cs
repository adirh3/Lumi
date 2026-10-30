using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Lumi.Views.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>The motion a job wake's details open and fold with, as a "worked for" row's do.</summary>
[Collection("Headless UI")]
public sealed class RevealPanelTests
{
    [Fact]
    public async Task OffScreenChangesApplyAtOnce()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var panel = new RevealPanel { Child = new Border { Width = 200, Height = 120 } };
            Assert.False(panel.IsVisible);
            Assert.Equal(0, panel.Reveal);

            panel.IsOpen = true;
            Assert.True(panel.IsVisible);
            Assert.Equal(1, panel.Reveal);
            Assert.Equal(1, panel.Opacity);
            panel.Measure(new Size(400, double.PositiveInfinity));
            Assert.Equal(120, panel.DesiredSize.Height);

            panel.IsOpen = false;
            Assert.False(panel.IsVisible);
            Assert.Equal(0, panel.Reveal);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task OnScreenChangesMoveUntilSettledUnlessMotionIsOff()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var content = new Border { Width = 200, Height = 120 };
            var panel = new RevealPanel { Child = content };
            var host = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Children = { new Border { Height = 20 }, panel } };
            var window = new Window { Width = 400, Height = 400, Content = host };
            window.Show();
            try
            {
                Pump(window);
                panel.IsOpen = true;
                Pump(window);
                // Opening starts from nothing: the content keeps its natural size, clipped by the panel.
                Assert.True(panel.IsVisible);
                Assert.True(panel.Reveal < 1);
                Assert.Equal(120, content.Bounds.Height);
                Assert.True(panel.Bounds.Height < 120);

                panel.Settle();
                Pump(window);
                Assert.Equal(1, panel.Reveal);
                Assert.Equal(120, panel.Bounds.Height);

                panel.IsOpen = false;
                Pump(window);
                Assert.True(panel.IsVisible);
                Assert.True(panel.Reveal > 0);
                panel.Settle();
                Assert.False(panel.IsVisible);

                panel.Classes.Add("motion-disabled");
                panel.IsOpen = true;
                Assert.Equal(1, panel.Reveal);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
