using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Lumi.Tests;

/// <summary>Strata's line-focus TextBox: focus draws only the Strata line, as the composer does.</summary>
[Collection("Headless UI")]
public sealed class StrataTextBoxLineFocusTests
{
    [Fact]
    public async Task LineFocus_DrawsOnlyTheStrataLineAlongTheBottomEdge()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var field = new TextBox { Classes = { "line-focus" }, Width = 240 };
            var outside = new Button { Content = "Outside" };
            var window = new Window { Width = 400, Height = 200, Content = new StackPanel { Children = { field, outside } } };
            try
            {
                window.Show();
                await SettleAsync();
                var line = Part(field, "FocusAccentBar");
                var frame = Part(field, "PART_Border");

                // A collapsed 2px bar along the bottom edge, not an outline around the field.
                Assert.Equal(2, line.Height);
                Assert.Equal(VerticalAlignment.Bottom, line.VerticalAlignment);
                Assert.Equal(0, line.BorderThickness.Top);
                Assert.Equal(0, line.Opacity, 3);

                Assert.True(field.Focus());
                await SettleAsync(500);

                Assert.Equal(1, line.Opacity, 3);
                Assert.Equal(1, line.RenderTransform!.Value.M11, 3);
                Assert.True(frame.BoxShadow.Count == 0 || frame.BoxShadow[0].Color.A == 0, "no focus halo");

                Assert.True(outside.Focus());
                await SettleAsync(500);
                Assert.Equal(0, line.Opacity, 3);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static Border Part(TextBox field, string name)
        => field.GetVisualDescendants().OfType<Border>().Single(border => border.Name == name);

    private static async Task SettleAsync(int milliseconds = 0)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        if (milliseconds > 0)
        {
            await Task.Delay(milliseconds);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }
}
