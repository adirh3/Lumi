using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Lumi.Views.Controls;
using StrataTheme.Animation;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class FitRowPanelTests
{
    [Theory]
    [InlineData(new double[] { 100, 120, 140 }, 500, 8, new double[] { 100, 120, 140 })]
    [InlineData(new double[] { 700, 190, 230 }, 884, 8, new double[] { 448, 190, 230 })]
    [InlineData(new double[] { 360, 340, 380 }, 884, 8, new double[] { 289, 289, 290 })]
    [InlineData(new double[] { 400, 210, 420 }, 861, 8, new double[] { 317, 210, 318 })]
    [InlineData(new double[] { 300, 500 }, double.PositiveInfinity, 8, new double[] { 300, 500 })]
    [InlineData(new double[] { 50, 50 }, 4, 8, new double[] { 0, 0 })]
    [InlineData(new double[0], 500, 8, new double[0])]
    public void ComputeWidths_KeepsNaturalWidthsThatFit_AndSharesTheRestFromTheNarrowestUp(
        double[] natural, double available, double spacing, double[] expected)
    {
        var widths = FitRowPanel.ComputeWidths(natural, available, spacing);

        Assert.Equal(expected.Length, widths.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], widths[i], precision: 6);
    }

    [Fact]
    public async Task Row_NeverWraps_AndFlagsOnlyTheChildrenItShrinks()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            var children = new[] { new Shrinkable(150), new Shrinkable(400), new Shrinkable(300) };
            var row = new FitRowPanel { Spacing = 8 };
            row.Children.AddRange(children);
            var host = new Border
            {
                Width = 600,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = row,
            };
            var window = new Window { Width = 1200, Height = 200, Content = host };

            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.Equal(new Rect(0, 0, 150, 20), children[0].Bounds);
                Assert.Equal(new Rect(158, 0, 217, 20), children[1].Bounds);
                Assert.Equal(new Rect(383, 0, 217, 20), children[2].Bounds);
                Assert.Equal(new[] { false, true, true }, children.Select(FitRowPanel.GetIsShrunk));

                host.Width = 1000;
                window.UpdateLayout();
                Assert.Equal(new Rect(158, 0, 400, 20), children[1].Bounds);
                Assert.All(children, child => Assert.False(FitRowPanel.GetIsShrunk(child)));

                host.Width = 600;
                children[1].IsVisible = false;
                window.UpdateLayout();
                Assert.Equal(new Rect(158, 0, 300, 20), children[2].Bounds);
                Assert.False(FitRowPanel.GetIsShrunk(children[2]));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChatFollowUps_ShowAllThreeOnOneRow_AtANormalWindowsChatWidth()
    {
        using var session = HeadlessTestSession.Start();
        Exception? failure = null;

        await session.Dispatch(async () =>
        {
            try
            {
                var chat = new Chat { Title = "Follow-ups" };
                chat.Messages.Add(new ChatMessage { Role = "user", Content = "Build me an expense dashboard." });
                chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "Here is your dashboard." });
                var data = new AppData
                {
                    Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false }
                };
                data.Chats.Add(chat);
                var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
                var view = new ChatView { DataContext = viewModel };
                // The chat column of a default 1320px window beside its 280px sidebar.
                var window = new Window { Width = 1040, Height = 820, Content = view };

                window.Show();
                try
                {
                    await viewModel.LoadChatAsync(chat);
                    await WaitUntilAsync(() => !viewModel.IsChatSurfaceLoading);
                    var row = view.FindControl<FitRowPanel>("SuggestionChipsRow");
                    Assert.NotNull(row);
                    var chips = row.Children.OfType<Button>().ToArray();
                    Assert.Equal(3, chips.Length);

                    // Suggestions arrive one property at a time, as opening a chat restores them; every
                    // chip still rises in, not just the one that made the row appear.
                    viewModel.SuggestionA = "Add a monthly budget line to the recurring charges chart";
                    viewModel.SuggestionB = "Show the diff";
                    viewModel.SuggestionC = "Export the dashboard as a PDF I can share with the whole team";
                    await PumpAsync();
                    Assert.All(chips, chip => Assert.True(SlideFadeEntrance.IsCompositedRunning(chip)));
                    window.UpdateLayout();

                    var layout = $"row {row.Bounds.Width}px; chips "
                                 + string.Join(", ", chips.Select(chip => $"{chip.Bounds.X}+{chip.Bounds.Width}"));
                    Assert.True(row.IsEffectivelyVisible);
                    Assert.All(chips, chip => Assert.True(chip.IsVisible));
                    Assert.All(chips, chip => Assert.True(chip.Bounds.Y == 0, layout));
                    Assert.Equal(chips[0].Bounds.Height, row.Bounds.Height);
                    Assert.True(chips[2].Bounds.Right <= row.Bounds.Width + 0.5, layout);

                    // The short suggestion keeps its whole text; the two long ones give way to fit, and
                    // only they offer their full text in a tip.
                    Assert.True(new[] { true, false, true }.SequenceEqual(chips.Select(FitRowPanel.GetIsShrunk)), layout);
                    Assert.All(chips, chip => Assert.Equal(
                        FitRowPanel.GetIsShrunk(chip) ? chip.CommandParameter : null, ToolTip.GetTip(chip)));

                    // With animations off, the next suggestions simply appear.
                    Array.ForEach(chips, SlideFadeEntrance.StopComposited);
                    viewModel.SuggestionA = viewModel.SuggestionB = viewModel.SuggestionC = "";
                    await PumpAsync();
                    Assert.False(row.IsVisible);
                    data.Settings.ShowAnimations = false;
                    viewModel.SuggestionA = "Show the diff";
                    viewModel.SuggestionB = "Summarize it";
                    viewModel.SuggestionC = "Open the dashboard";
                    await PumpAsync();
                    Assert.True(row.IsVisible);
                    Assert.All(chips, chip => Assert.False(SlideFadeEntrance.IsCompositedRunning(chip)));
                }
                finally
                {
                    window.Close();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }, CancellationToken.None);

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await PumpAsync();
            await Task.Delay(20);
        }

        Assert.True(condition(), "Timed out waiting for the chat view to finish loading.");
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    /// <summary>Wants its natural width, and takes less when that is all it is given (as a trimming label does).</summary>
    private sealed class Shrinkable(double natural) : Control
    {
        protected override Size MeasureOverride(Size availableSize)
            => new(Math.Min(natural, availableSize.Width), 20);
    }
}
