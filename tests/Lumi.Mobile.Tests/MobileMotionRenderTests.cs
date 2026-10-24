using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Behaviors;
using StrataTheme.Animation;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class MobileMotionRenderTests
{
    [Fact]
    public async Task EntrancePaintsIntermediatePixelsWithoutMovingTheLogicalViewport()
    {
        using var session = HeadlessUnitTestSession.StartNew(
            typeof(MobileMotionRenderApp), AvaloniaTestIsolationLevel.PerTest);
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(() =>
        {
            var target = new Border
            {
                Width = 80, Height = 40, Background = Brushes.White,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = new Thickness(30, 50, 0, 0)
            };
            var window = new Window { Width = 160, Height = 160, Background = Brushes.Black, Content = target };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                using var restFrame = window.CaptureRenderedFrame()!;
                var rest = Measure(restFrame);
                var bounds = target.Bounds;
                using var entrance = new MobileEntrance(target, offsetY: 16,
                    duration: TimeSpan.FromMilliseconds(600));
                entrance.Play();
                Dispatcher.UIThread.RunJobs();
                var samples = new List<(double Brightness, double Y)>();
                var elapsed = Stopwatch.StartNew();
                while (elapsed.ElapsedMilliseconds < 680)
                {
                    // Loading or a streamed markdown update can occupy the UI between commits.
                    Thread.Sleep(samples.Count == 3 ? 100 : 16);
                    using var frame = window.CaptureRenderedFrame()!;
                    samples.Add(Measure(frame));
                    Assert.Equal(bounds, target.Bounds);
                    Assert.Equal(1, target.Opacity);
                    Assert.Equal(0, target.RenderTransform!.Value.M32);
                }
                Assert.True(samples.Count(sample =>
                    sample.Brightness > rest.Brightness * 0.08
                    && sample.Brightness < rest.Brightness * 0.92) >= 3,
                    "The rendered entrance must contain multiple partially visible frames, not a hidden-to-visible snap.");
                Assert.True(samples.Any(sample => sample.Brightness > rest.Brightness * 0.08
                    && sample.Y >= rest.Y + 5),
                    "The rise must visibly move pixels while leaving layout and scroll anchors at rest.");
                Assert.InRange(samples[^1].Brightness / rest.Brightness, 0.98, 1.02);
                Assert.InRange(Math.Abs(samples[^1].Y - rest.Y), 0, 0.5);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BottomSheetMotionKeepsPaintingWhileTheUiThreadIsOccupied(bool closing)
    {
        using var session = HeadlessUnitTestSession.StartNew(
            typeof(MobileMotionRenderApp), AvaloniaTestIsolationLevel.PerTest);
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch<bool>(async () =>
        {
            var sheet = new StrataBottomSheet
            {
                Title = "Projects", Classes = { "safe-area" }, SheetMargin = new Thickness(12),
                Content = new Border { Height = 160, Background = Brushes.White }
            };
            var window = new Window
            {
                Width = 240, Height = 420, Background = Brushes.Black,
                Content = sheet
            };
            try
            {
                window.Show();
                sheet.ApplyTemplate();
                // Frost has its own pixel tests; isolate motion from software blur's capture cost.
                sheet.GetVisualDescendants().OfType<StrataBackdropBlur>().Single().BlurRadius = 0;
                sheet.IsOpen = true;
                await PumpAsync(window, StrataBottomSheet.OpenDuration * 2);
                using var restFrame = window.CaptureRenderedFrame()!;
                var rest = Measure(restFrame);
                if (!closing)
                {
                    sheet.Close();
                    await PumpAsync(window, StrataBottomSheet.CloseDuration + TimeSpan.FromMilliseconds(60));
                }

                var motion = sheet.GetVisualDescendants().OfType<Border>()
                    .Single(control => control.Name == "PART_SheetMotion");
                var bounds = motion.Bounds;
                sheet.IsOpen = !closing;
                Dispatcher.UIThread.RunJobs();
                var samples = new List<double>();
                var elapsed = Stopwatch.StartNew();
                var duration = closing ? StrataBottomSheet.CloseDuration : StrataBottomSheet.OpenDuration;
                while (elapsed.Elapsed < duration + TimeSpan.FromMilliseconds(100))
                {
                    // Do not pump the UI: loading and streamed updates must not stall sheet motion.
                    Thread.Sleep(16);
                    using var frame = window.CaptureRenderedFrame()!;
                    samples.Add(Measure(frame).Brightness / rest.Brightness);
                    Assert.Equal(bounds, motion.Bounds);
                    Assert.Null(motion.Transitions);
                    Assert.Equal(1, motion.Opacity);
                    Assert.Equal(0, motion.RenderTransform?.Value.M32 ?? 0);
                }

                // Capture is synchronous; verify intermediate pixels, not this machine's frame budget.
                Assert.True(samples.Any(value => value is > 0.02 and < 0.98),
                    $"The rendered {(closing ? "exit" : "entrance")} must keep moving without UI-thread ticks. " +
                    $"Visible fractions: {string.Join(", ", samples.Select(value => value.ToString("F2")))}.");
                Assert.InRange(samples[^1], closing ? 0 : 0.98, closing ? 0.02 : 1.02);
                await PumpAsync(window, StrataBottomSheet.CloseDuration + TimeSpan.FromMilliseconds(60));
                Assert.Equal(!closing, sheet.IsPresented);
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

    private static async Task PumpAsync(Window window, TimeSpan duration)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < duration)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
            await Task.Delay(10);
        }
    }

    private static (double Brightness, double Y) Measure(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var bytes = new byte[stride * size.Height];
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), buffer, bytes.Length, stride);
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        double brightness = 0, weightedY = 0;
        for (var y = 20; y < size.Height - 10; y++)
        for (var x = 20; x < Math.Min(size.Width, 130); x++)
        {
            var index = y * stride + x * 4;
            var value = bytes[index] + bytes[index + 1] + bytes[index + 2];
            brightness += value;
            weightedY += value * y;
        }
        return (brightness, brightness > 0 ? weightedY / brightness : 0);
    }
}

public sealed class MobileMotionRenderApp : App
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<MobileMotionRenderApp>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();

    public override void OnFrameworkInitializationCompleted()
    {
    }
}
