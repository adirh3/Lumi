using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class StrataQuestionCardMotionTests
{
    [Fact]
    public async Task AnsweredCard_OpensAndFoldsSmoothlyWithoutSqueezingItsOptions()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var row = Show();
            try
            {
                await AdvanceAsync(row.Window);
                var chipHeight = row.Card.Bounds.Height;
                Click(row, "PART_ExpandButton");
                Assert.True(row.Item.IsExpanded);
                await AdvanceAsync(row.Window);
                await AdvanceAsync(row.Window, 40);
                Assert.InRange(row.Layout.Reveal, 0.01, 0.99);
                Assert.InRange(row.Body.Opacity, 0.01, 0.99);
                var naturalHeight = row.Body.Bounds.Height;
                var midHeight = row.Card.Bounds.Height;
                var opening = await SampleAsync(row);
                Assert.InRange(midHeight, chipHeight + 1, row.Card.Bounds.Height - 1);
                Assert.Equal(naturalHeight, row.Body.Bounds.Height);
                Assert.True(row.Body.Bounds.Width + row.Body.Margin.Left + row.Body.Margin.Right
                    >= row.Body.DesiredSize.Width - 1);
                Assert.Equal(1, row.Body.Opacity);
                Assert.False(Part<Button>(row.Card, "PART_ExpandButton").IsVisible);
                Assert.All(opening.Zip(opening.Skip(1)),
                    step => Assert.True(step.Second >= step.First - 0.5));

                Click(row, "PART_CollapseButton");
                Assert.False(row.Item.IsExpanded);
                await AdvanceAsync(row.Window);
                await AdvanceAsync(row.Window, 30);
                Assert.InRange(row.Layout.Reveal, 0.01, 0.99);
                Assert.Equal(naturalHeight, row.Body.Bounds.Height);
                var closing = await SampleAsync(row);
                Assert.False(row.Body.IsVisible);
                Assert.Equal(chipHeight, row.Card.Bounds.Height, 1);
                Assert.All(closing.Zip(closing.Skip(1)),
                    step => Assert.True(step.Second <= step.First + 0.5));
            }
            finally
            {
                row.Window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AllocatedWidth_FillsTheUnansweredBodyAndBothAnsweredStates()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var row = Show(answered: false, minWidth: 420);
            try
            {
                row.Item.AllowFreeText = false;
                row.Item.Question = "Which color?";
                row.Item.OptionsList = ["Blue", "Green"];
                await AdvanceAsync(row.Window);
                Assert.Equal(420, row.Card.Bounds.Width, 1);
                AssertBodyFillsWidth(row);

                row.Card.Classes.Add("motion-disabled");
                row.Item.ApplyAnswer("Blue");
                await AdvanceAsync(row.Window);
                var chip = Part<Button>(row.Card, "PART_ExpandButton");
                Assert.Equal(row.Layout.Bounds.Width, chip.Bounds.Width, 1);

                Click(row, "PART_ExpandButton");
                await AdvanceAsync(row.Window);
                AssertBodyFillsWidth(row);
                Click(row, "PART_CollapseButton");
                await AdvanceAsync(row.Window);
                Assert.Equal(row.Layout.Bounds.Width, chip.Bounds.Width, 1);
            }
            finally
            {
                row.Window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReversingAndDetachingMidMotion_ContinuesThenSettlesWithoutStaleFrames()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var row = Show();
            try
            {
                await AdvanceAsync(row.Window);
                Click(row, "PART_ExpandButton");
                await AdvanceAsync(row.Window);
                await AdvanceAsync(row.Window, 40);
                var beforeReverse = row.Layout.Reveal;
                Part<Button>(row.Card, "PART_CollapseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(beforeReverse, row.Layout.Reveal);
                await AdvanceAsync(row.Window);
                await AdvanceAsync(row.Window, 20);
                Assert.InRange(row.Layout.Reveal, 0.001, beforeReverse - 0.001);
                Part<Button>(row.Card, "PART_ExpandButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var parent = (StackPanel)row.Window.Content!;
                parent.Children.Remove(row.Card);
                parent.Children.Add(row.Card);
                await AdvanceAsync(row.Window);
                Assert.Equal(1, row.Layout.Reveal);
                Assert.True(row.Body.IsVisible);
                await AdvanceAsync(row.Window, 300);
                Assert.Equal(1, row.Layout.Reveal);
                Click(row, "PART_CollapseButton");
                await SampleAsync(row);
                Assert.Equal(0, row.Layout.Reveal);
            }
            finally
            {
                row.Window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ProgrammaticAndMotionDisabledChanges_SettleImmediately()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var row = Show();
            try
            {
                await AdvanceAsync(row.Window);
                row.Item.IsExpanded = true;
                Assert.Equal(1, row.Layout.Reveal);
                row.Item.IsExpanded = false;
                Assert.Equal(0, row.Layout.Reveal);
                row.Card.Classes.Add("motion-disabled");
                Click(row, "PART_ExpandButton");
                Assert.Equal(1, row.Layout.Reveal);
                await AdvanceAsync(row.Window);
                Click(row, "PART_CollapseButton");
                Assert.Equal(0, row.Layout.Reveal);
            }
            finally
            {
                row.Window.Close();
            }
        }, CancellationToken.None);
    }

    private sealed record Row(Window Window, QuestionItem Item, StrataQuestionCard Card,
        QuestionCardPanel Layout, StackPanel Body);

    private static Row Show(bool answered = true, double minWidth = 0)
    {
        var item = new QuestionItem("motion", "Which action should happen next?",
            ["Run the focused tests", "Inspect the transcript", "Review the resulting changes"], true);
        if (answered)
            item.ApplyAnswer("Run the focused tests");
        var template = new ChatView().DataTemplates.Single(template => template.Match(item));
        var card = (StrataQuestionCard)template.Build(item)!;
        card.DataContext = item;
        card.MinWidth = minWidth;
        var window = new Window
        {
            Width = 600, Height = 600,
            Content = new StackPanel { Children = { card } }
        };
        window.Show();
        window.UpdateLayout();
        return new Row(window, item, card, Part<QuestionCardPanel>(card, "PART_Layout"),
            Part<StackPanel>(card, "PART_Body"));
    }

    private static T Part<T>(Control card, string name) where T : Control =>
        card.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void AssertBodyFillsWidth(Row row)
    {
        Assert.Equal(row.Layout.Bounds.Width - row.Body.Margin.Left - row.Body.Margin.Right,
            row.Body.Bounds.Width, 1);
        Assert.Equal(row.Body.Bounds.Width, Part<StackPanel>(row.Card, "PART_OptionsPanel").Bounds.Width, 1);
    }

    private static void Click(Row row, string name)
    {
        row.Window.UpdateLayout();
        var button = Part<Button>(row.Card, name);
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), row.Window)!.Value;
        row.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        row.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
    }

    private static async Task<List<double>> SampleAsync(Row row)
    {
        var heights = new List<double>();
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < 350)
        {
            await AdvanceAsync(row.Window, 16);
            heights.Add(row.Card.Bounds.Height);
        }
        return heights;
    }

    private static async Task AdvanceAsync(Window window, int milliseconds = 1)
    {
        await Task.Delay(milliseconds);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
    }
}
