using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Threading;
using Lumi.Mobile.Behaviors;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>
/// A long file's sections come one a frame until they fill the screen, so no frame builds more than one;
/// then the rest come at once, and only those on screen are built.
/// </summary>
[Collection("Headless mobile UI")]
public sealed class ProgressiveItemsTests
{
    [Fact]
    public Task ItemsComeOneAFrameUntilTheyFillTheScreenThenTheRestAtOnce() =>
        RunAsync(list =>
        {
            var sections = Enumerable.Range(0, 30).Select(index => $"Section {index}").ToArray();
            ProgressiveItems.SetSource(list, sections);
            Dispatcher.UIThread.RunJobs();
            var counts = new List<int> { list.ItemCount };
            for (var frame = 0; frame < 12 && list.ItemCount < sections.Length; frame++)
            {
                NextFrame();
                counts.Add(list.ItemCount);
            }

            // At most one item comes before each frame. Six 200-tall items reach a screen below the 600-tall
            // one, so the rest then come at once, and only what the screen shows is built.
            Assert.True(counts[0] <= 1, string.Join(", ", counts));
            Assert.Equal(6, counts[^2]);
            Assert.All(counts.Zip(counts.Skip(1)).SkipLast(1), step => Assert.Equal(step.First + 1, step.Second));
            Assert.Equal(sections, list.Items.Cast<string>());
            Assert.InRange(list.GetRealizedContainers().Count(), 3, 4);

            // Another source starts again from its first item, and none clears the list.
            ProgressiveItems.SetSource(list, new[] { "Only" });
            Dispatcher.UIThread.RunJobs();
            Assert.True(list.ItemCount <= 1);
            NextFrame();
            Assert.Equal(["Only"], list.Items.Cast<string>());

            ProgressiveItems.SetSource(list, null);
            Assert.Null(list.ItemsSource);
        });

    [Fact]
    public Task AListThatIsNotOnScreenTakesEveryItemAtOnce() =>
        RunAsync(_ =>
        {
            var offscreen = new ItemsControl();
            ProgressiveItems.SetSource(offscreen, new[] { "One", "Two", "Three" });
            Assert.Equal(["One", "Two", "Three"], offscreen.Items.Cast<string>());
        });

    /// <summary>A frame: the render clock ticks once and everything it lets run, runs.</summary>
    private static void NextFrame()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task RunAsync(Action<ItemsControl> test)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(FrameByFrameApp), AvaloniaTestIsolationLevel.PerTest);
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(() =>
        {
            var list = new ItemsControl
            {
                ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
                ItemTemplate = new FuncDataTemplate<string>((text, _) => new TextBlock { Text = text, Height = 200 })
            };
            var window = new Window { Width = 400, Height = 600, Content = new ScrollViewer { Content = new StackPanel { Children = { list } } } };
            try
            {
                window.Show();
                for (var frame = 0; frame < 3; frame++)
                    NextFrame();
                test(list);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
        failure?.Throw();
    }

    /// <summary>The mobile app whose frames come only when a test asks for one, never from a timer between.</summary>
    public sealed class FrameByFrameApp : App
    {
        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<FrameByFrameApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { Fps = 1 });

        public override void OnFrameworkInitializationCompleted()
        {
        }
    }
}
