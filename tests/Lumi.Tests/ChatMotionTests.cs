using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using StrataTheme.Animation;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ChatMotionTests
{
    [Theory]
    [InlineData("user")]
    [InlineData("assistant")]
    public void LiveMessages_RequestOneEntrance_ButHistoryDoesNot(string role)
    {
        var builder = CreateBuilder();
        var turns = builder.Rebuild([]);
        var message = CreateMessage(role, "A fresh message");

        builder.ProcessMessageToTranscript(message);
        var item = Assert.Single(Assert.Single(turns).Items);
        Assert.True(item.TryConsumeEntranceAnimation());
        Assert.False(item.TryConsumeEntranceAnimation());

        builder.ProcessMessageToTranscript(message);
        Assert.False(item.HasPendingEntranceAnimation);

        var history = builder.Rebuild([message]);
        Assert.False(Assert.Single(Assert.Single(history).Items).TryConsumeEntranceAnimation());
    }

    [Theory]
    [InlineData("user")]
    [InlineData("assistant")]
    public void DisabledAnimations_DoNotRequestMessageEntrances(string role)
    {
        var builder = CreateBuilder(showAnimations: false);
        var turns = builder.Rebuild([]);
        builder.ProcessMessageToTranscript(CreateMessage(role, "No motion"));

        Assert.False(Assert.Single(Assert.Single(turns).Items).HasPendingEntranceAnimation);
    }

    [Fact]
    public async Task OffscreenMessages_ExpireInsteadOfAnimatingWhenHistoryIsOpened()
    {
        var item = new UserMessageItem(CreateMessage("user", "Older arrival"), false);
        item.RequestEntranceAnimation();

        await Task.Delay(TimeSpan.FromMilliseconds(2100));

        Assert.False(item.TryConsumeEntranceAnimation());
        Assert.False(item.HasPendingEntranceAnimation);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComposerFocusLine_UsesVisibleMotionWithRealThemeBrushes(bool dark)
    {
        // Avalonia's drawing-free and Skia headless backends cannot share cached rendering services.
        Skip.IfNot(Environment.GetEnvironmentVariable("LUMI_COMPOSER_MOTION_RENDER") == "1",
            "Run this method alone with LUMI_COMPOSER_MOTION_RENDER=1 for real-pixel verification.");
        using var session = HeadlessTestSession.Start(typeof(SkiaHeadlessTestApp));
        await session.Dispatch(() =>
        {
            var composer = new StrataChatComposer { Width = 400, Margin = new Thickness(20) };
            var outside = new Button { Content = "Outside" };
            var window = new Window
            {
                Width = 500, Height = 260,
                RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
                Content = new StackPanel { Children = { composer, outside } },
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                var input = composer.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "PART_Input");
                var line = composer.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PART_FocusLine");
                var sweep = composer.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PART_FocusSweep");
                Assert.True(outside.Focus());
                _ = CaptureLinePixels(window, line);
                Thread.Sleep(450);
                var unfocused = CaptureLinePixels(window, line);
                var trackBounds = line.Bounds;

                Assert.True(input.Focus());
                window.UpdateLayout();
                _ = CaptureLinePixels(window, line);

                var revealedEdges = new List<int>();
                var revealSamples = new List<string>();
                for (var i = 0; i < 10; i++)
                {
                    Thread.Sleep(25);
                    var pixels = CaptureLinePixels(window, line);
                    var changed = Enumerable.Range(2, pixels.Length - 4)
                        .Where(x => ColorDistance(pixels[x], unfocused[x]) > 45)
                        .ToArray();
                    revealSamples.Add($"scale={line.RenderTransform?.Value.M11:0.00}, opacity={line.Opacity:0.00}, " +
                        $"pixels={(changed.Length == 0 ? "none" : $"{changed[0]}..{changed[^1]}")}, " +
                        $"right={pixels[^5]:X8}/{unfocused[^5]:X8}");
                    if (changed.Length > 0 && changed[0] <= 4 && changed[^1] < pixels.Length * 0.9)
                        revealedEdges.Add(changed[^1]);
                    Assert.Equal(trackBounds, line.Bounds);
                }

                Assert.True(revealedEdges.Count >= 2,
                    "Focus should draw a visible left-anchored line, not fade in its entire width: " +
                    string.Join("; ", revealSamples));
                Assert.True(revealedEdges[^1] > revealedEdges[0] + trackBounds.Width * 0.15);

                Thread.Sleep(450);
                sweep.IsVisible = false;
                var resting = CaptureLinePixels(window, line);
                sweep.ClearValue(Visual.IsVisibleProperty);
                window.UpdateLayout();
                _ = CaptureLinePixels(window, line);
                var positions = new List<double>();
                var peakContrast = 0;
                for (var i = 0; i < 10; i++)
                {
                    Thread.Sleep(100);
                    var pixels = CaptureLinePixels(window, line);
                    var weight = 0d;
                    var weightedX = 0d;
                    for (var x = 2; x < pixels.Length - 2; x++)
                    {
                        var gain = SumRgb(pixels[x]) - SumRgb(resting[x]);
                        peakContrast = Math.Max(peakContrast, gain);
                        if (gain <= 20)
                            continue;
                        weight += gain;
                        weightedX += gain * x;
                    }

                    if (weight > 0)
                        positions.Add(weightedX / weight);
                    Assert.Equal(trackBounds, line.Bounds);
                }

                Assert.True(peakContrast >= 75,
                    $"The real theme's moving highlight must be visibly distinct; RGB gain was {peakContrast}.");
                Assert.True(positions.Count >= 3, "The sweep should enter the clipped underline and remain visible.");
                Assert.True(positions[^1] > positions[0] + 40,
                    $"Expected visible left-to-right travel: {string.Join(", ", positions)}");
                for (var i = 1; i < positions.Count; i++)
                    Assert.True(positions[i] >= positions[i - 1] - 1);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ComposerFocusLine_DisabledMotionShowsTheStaticLineImmediately()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var composer = new StrataChatComposer { Width = 400, Classes = { "motion-disabled" } };
            var outside = new Button { Content = "Outside" };
            var window = new Window
            {
                Width = 500, Height = 260,
                Content = new StackPanel { Children = { composer, outside } },
            };
            try
            {
                window.Show();
                await FlushLayoutAsync(window);
                var input = composer.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "PART_Input");
                var line = composer.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PART_FocusLine");
                var sweep = composer.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PART_FocusSweep");
                Assert.True(outside.Focus());
                Assert.True(input.Focus());
                Assert.Null(line.Transitions);
                Assert.Equal(1, line.Opacity);
                Assert.Equal(Matrix.Identity, line.RenderTransform?.Value ?? Matrix.Identity);
                Assert.False(sweep.IsVisible);
                Assert.False(LifecycleOffsetSweep.IsRunning(sweep));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ComposerFocusLine_FollowsFocusVisibilityAndAttachment()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var composer = new StrataChatComposer { Width = 400 };
            var outside = new Button { Content = "Outside" };
            var panel = new StackPanel { Children = { composer, outside } };
            var window = new Window { Width = 500, Height = 300, Content = panel };
            try
            {
                window.Show();
                await FlushLayoutAsync(window);
                var input = composer.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "PART_Input");
                var line = composer.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PART_FocusLine");
                var sweep = composer.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PART_FocusSweep");
                Assert.DoesNotContain(composer.GetVisualDescendants().OfType<Control>(),
                    c => c.Name is "PART_FocusAccent" or "PART_FocusGlow");

                outside.Focus();
                Dispatcher.UIThread.RunJobs();
                Assert.False(LifecycleOffsetSweep.IsRunning(sweep));
                Assert.False(sweep.IsVisible);

                Assert.True(input.Focus());
                Dispatcher.UIThread.RunJobs();
                Assert.True(LifecycleOffsetSweep.GetIsActive(sweep));
                Assert.True(LifecycleOffsetSweep.IsRunning(sweep));
                Assert.Equal(2, line.Bounds.Height);
                Assert.Equal(new Thickness(14, 0), line.Margin);
                Assert.Equal(new CornerRadius(1), line.CornerRadius);
                Assert.Equal(input.Bounds.Width - 28, line.Bounds.Width, 3);
                Assert.Equal(input.Bounds.Bottom, line.Bounds.Bottom, 3);
                Assert.False(line.IsHitTestVisible);
                var inputAccent = input.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "FocusAccentBar");
                Assert.False(inputAccent.IsVisible);
                Assert.False(LifecycleOpacityPulse.IsRunning(inputAccent));

                panel.IsVisible = false;
                Assert.False(LifecycleOffsetSweep.IsRunning(sweep));
                panel.IsVisible = true;
                Assert.True(input.Focus());
                Dispatcher.UIThread.RunJobs();
                Assert.True(LifecycleOffsetSweep.IsRunning(sweep));

                outside.Focus();
                Dispatcher.UIThread.RunJobs();
                Assert.False(LifecycleOffsetSweep.GetIsActive(sweep));
                Assert.False(LifecycleOffsetSweep.IsRunning(sweep));
                Assert.False(sweep.IsVisible);

                input.Focus();
                Dispatcher.UIThread.RunJobs();
                window.Content = null;
                Assert.False(LifecycleOffsetSweep.IsRunning(sweep));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Entrance_InterpolatesWithoutReflow_AndSettlesAfterInterruption()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var target = new Border { Width = 180, Height = 60, Background = Brushes.Blue };
            var panel = new StackPanel { Children = { target } };
            var window = new Window { Width = 400, Height = 240, Content = panel };
            try
            {
                window.Show();
                await FlushLayoutAsync(window);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                var bounds = target.Bounds;

                SlideFadeEntrance.Play(target, duration: TimeSpan.FromSeconds(1));
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                await Task.Delay(100);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Assert.InRange(target.Opacity, 0.001, 0.999);
                Assert.InRange(target.RenderTransform!.Value.M32, 0.001, 11.999);
                Assert.Equal(bounds, target.Bounds);

                SlideFadeEntrance.Play(target, offsetY: 6, duration: TimeSpan.FromMilliseconds(120));
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                await Task.Delay(200);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Assert.Equal(1, target.Opacity, 3);
                Assert.Equal(Matrix.Identity, target.RenderTransform?.Value ?? Matrix.Identity);
                Assert.Equal(bounds, target.Bounds);

                SlideFadeEntrance.Play(target);
                panel.Children.Remove(target);
                panel.Children.Add(target);
                await FlushLayoutAsync(window);
                Assert.Equal(1, target.Opacity, 3);
                Assert.Equal(Matrix.Identity, target.RenderTransform?.Value ?? Matrix.Identity);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MessageEntrance_IsConsumedOnRealization_AndDoesNotReplayOnRemount()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var item = new UserMessageItem(CreateMessage("user", "Slide in once"), false);
            item.RequestEntranceAnimation();
            var turn = new TranscriptTurn("motion-turn") { Items = { item } };
            var control = CreateTurnControl(turn);
            var window = new Window { Width = 500, Height = 300, Content = control };
            try
            {
                window.Show();
                await FlushLayoutAsync(window);
                var host = Assert.Single(turn.RealizedItemsHost!.Children);
                Assert.False(item.HasPendingEntranceAnimation);
                Assert.Contains(host.Transitions!, t => t is TransformOperationsTransition);

                window.Content = null;
                turn.ReleaseRealizedHost();
                window.Content = CreateTurnControl(turn);
                await FlushLayoutAsync(window);
                var remounted = Assert.Single(turn.RealizedItemsHost!.Children);
                Assert.NotSame(host, remounted);
                Assert.Null(remounted.Transitions);
                Assert.Equal(1, remounted.Opacity);
            }
            finally
            {
                window.Close();
                turn.ReleaseRealizedHost();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EmptyStreamingMessage_WaitsForVisibleContent_AndOnlyAnimatesOnce()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var source = CreateMessage("assistant", "", isStreaming: true);
            var item = new AssistantMessageItem(source, false);
            item.RequestEntranceAnimation();
            var turn = new TranscriptTurn("streaming-motion") { Items = { item } };
            var window = new Window { Width = 500, Height = 300, Content = CreateTurnControl(turn) };
            try
            {
                window.Show();
                await FlushLayoutAsync(window);
                var host = Assert.Single(turn.RealizedItemsHost!.Children);
                Assert.False(host.IsVisible);
                Assert.True(item.HasPendingEntranceAnimation);
                Assert.Null(host.Transitions);

                source.Message.Content = "First visible text";
                source.NotifyContentChanged();
                await FlushLayoutAsync(window);
                Assert.True(host.IsVisible);
                Assert.False(item.HasPendingEntranceAnimation);
                Assert.Contains(host.Transitions!, t => t is TransformOperationsTransition);
                var transitions = host.Transitions;

                source.Message.Content += " and the next token";
                source.NotifyContentChanged();
                Assert.Same(transitions, host.Transitions);
                Assert.False(item.HasPendingEntranceAnimation);
            }
            finally
            {
                source.Message.IsStreaming = false;
                source.NotifyStreamingEnded();
                window.Close();
                turn.ReleaseRealizedHost();
            }
        }, CancellationToken.None);
    }

    private static TranscriptBuilder CreateBuilder(bool showAnimations = true)
        => new(new DataStore(new AppData { Settings = new UserSettings { ShowAnimations = showAnimations } }),
            _ => { }, (_, _) => { }, _ => { }, (_, _) => Task.CompletedTask, () => null);

    private static ChatMessageViewModel CreateMessage(string role, string content, bool isStreaming = false)
        => new(new ChatMessage { Role = role, Content = content, IsStreaming = isStreaming });

    private static TranscriptTurnControl CreateTurnControl(TranscriptTurn turn)
    {
        var control = new TranscriptTurnControl { Turn = turn };
        control.DataTemplates.Add(new FuncDataTemplate<TranscriptItem>((item, _) =>
            new Border { Width = 180, Height = 60, Child = new TextBlock { Text = item!.StableId } }));
        return control;
    }

    private static async Task FlushLayoutAsync(Window window)
    {
        window.UpdateLayout();
        foreach (var turn in window.GetVisualDescendants().OfType<TranscriptTurnControl>().ToArray())
            turn.RealizePendingHost();
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
    }

    private static int[] CaptureLinePixels(Window window, Border line)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        using var bitmap = window.CaptureRenderedFrame();
        Assert.NotNull(bitmap);
        var origin = line.TranslatePoint(new Point(0, 1), window)!.Value;
        using var framebuffer = bitmap.Lock();
        var pixels = new int[(int)line.Bounds.Width];
        for (var x = 0; x < pixels.Length; x++)
        {
            var address = framebuffer.Address + (int)origin.Y * framebuffer.RowBytes + ((int)origin.X + x) * 4;
            pixels[x] = System.Runtime.InteropServices.Marshal.ReadInt32(address);
        }
        return pixels;
    }

    private static int SumRgb(int pixel) => (pixel & 255) + ((pixel >> 8) & 255) + ((pixel >> 16) & 255);

    private static int ColorDistance(int a, int b)
        => Math.Abs((a & 255) - (b & 255))
            + Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255))
            + Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255));
}
