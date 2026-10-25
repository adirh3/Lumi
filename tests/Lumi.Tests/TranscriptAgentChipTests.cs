using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// A sub-agent in the chat is a glass chip inside a row button that opens its run. The chip draws all of
/// the chrome; the button must add nothing of its own, or hovering shows a rectangle behind the chip. Nor
/// may the row move or draw outside the chip: a turn clips to its bounds, and the row is often the first
/// thing in its turn, so anything above the chip is cut off.
/// </summary>
[Collection("Headless UI")]
public sealed class TranscriptAgentChipTests
{
    [Fact]
    public async Task AgentRow_DrawsOnlyTheChip_AndStaysInPlace_WhenHovered()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var chip = new StrataAgentRun
            {
                Classes = { "inline" },
                AgentName = "Explore agent",
                Title = "Inspect the transcript",
                Initial = "E",
                Status = StrataAiToolCallStatus.Completed,
            };
            var row = new Button { Classes = { "agent-row" }, Content = chip };
            var window = new Window { Width = 640, Height = 240, Content = new StackPanel { Name = "Transcript", Margin = new Thickness(24), Children = { row } } };
            // The chat view's own styles (a showing chat view needs its view model).
            var chatView = new ChatView();
            var styles = chatView.Styles.ToList();
            chatView.Styles.Clear();
            window.Styles.AddRange(styles);
            window.Show();
            try
            {
                await PumpAsync();
                window.UpdateLayout();

                // Strata's button paints a surface, shadow and aurora line on hover; the row has none.
                var ownParts = row.GetVisualDescendants().OfType<Control>()
                    .Where(control => ReferenceEquals(control.TemplatedParent, row))
                    .ToList();
                Assert.DoesNotContain(ownParts, part => part.Name is "PART_Surface" or "PART_Aurora");
                var focusRing = Assert.IsType<Border>(ownParts.Single(part => part.Name == "PART_FocusRing"));
                Assert.Equal(new CornerRadius(16), focusRing.CornerRadius);
                // Focus is an accent rim over the chip's own, inside the row.
                Assert.Equal(new Rect(row.Bounds.Size), focusRing.Bounds);
                var chipRoot = chip.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_Root");
                var restShadow = chipRoot.BoxShadow;
                var restFill = chipRoot.Background;

                var center = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
                window.MouseMove(center, RawInputModifiers.None);
                await PumpAsync();
                // Let any transition finish.
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                await Task.Delay(400);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                await PumpAsync();

                Assert.Contains(":pointerover", row.Classes);
                var presenter = ownParts.OfType<ContentPresenter>().Single();
                Assert.True(IsClear(presenter.Background), $"The row paints {presenter.Background} on hover.");
                Assert.True(IsClear(focusRing.BorderBrush));
                Assert.Equal(Matrix.Identity, row.RenderTransform?.Value ?? Matrix.Identity);
                Assert.Equal(restShadow, chipRoot.BoxShadow);
                // Hovering brightens the chip's glass instead.
                Assert.NotEqual(restFill, chipRoot.Background);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static bool IsClear(IBrush? brush)
        => brush is null || brush is ISolidColorBrush { Color.A: 0 };

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}