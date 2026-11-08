using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Behaviors;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using StrataTheme.Animation;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class MobileMotionRenderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TappingSendAnimatesTheRealBubbleAndConfirmationKeepsItsControl(bool windowRollsForward)
    {
        using var session = HeadlessUnitTestSession.StartNew(
            typeof(MobileMotionRenderApp), AvaloniaTestIsolationLevel.PerTest);
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch<bool>(async () =>
        {
            var handler = new HeldSendHandler();
            var storeDirectory = Path.Combine(Path.GetTempPath(), "Lumi-mobile-send", Guid.NewGuid().ToString("N"));
            var store = new MobileSettingsStore(storeDirectory);
            await using var model = new MobileShellViewModel(
                client: new LumiRemoteClient("motion-test", "Motion test", handler),
                store: store, post: action => action());
            model.Client.Configure("http://127.0.0.1:18764", "fixture");
            model.Client.MarkProtocolCompatibleForTests();
            model.IsPaired = model.IsConnected = model.IsHostReady = true;
            model.Glow = MobileShellViewModel.GlowPreference.Off;
            var view = new ChatDetailView { DataContext = model };
            var window = new Window { Width = 412, Height = 892, Content = view };
            Task? send = null;
            try
            {
                window.Show();
                var id = Guid.NewGuid();
                var initialTurn = new RemoteTranscriptTurn
                {
                    Id = "history",
                    Items = [new()
                    {
                        Id = "history-user", Kind = RemoteProtocol.ItemKinds.User, Text = "A previous message"
                    }]
                };
                model.Chat.Reset(id, "Send motion", isLoading: true);
                model.Chat.ApplyTranscript(new RemoteTranscript
                {
                    ChatId = id, Revision = 1, RevisionEpoch = "test",
                    Status = new RemoteChatStatus { ChatId = id },
                    Turns = [initialTurn]
                });
                model.Chat.IsLoading = false;
                await PumpAsync(window, TimeSpan.FromMilliseconds(450));
                // Frost has separate pixel tests; software blur would dominate synchronous captures.
                foreach (var frost in view.GetVisualDescendants().OfType<StrataBackdropBlur>())
                    frost.BlurRadius = 0;

                var hiddenBeforePaint = false;
                ContentPresenter? preparedHost = null;
                var sinceHidden = new Stopwatch();
                using var observation = Visual.OpacityProperty.Changed.AddClassHandler<ContentPresenter>((control, _) =>
                {
                    if (control.DataContext is UserTurnItemViewModel { Text: "Smooth send" } && control.Opacity == 0)
                    {
                        hiddenBeforePaint = true;
                        preparedHost = control;
                        sinceHidden.Restart();
                    }
                });
                model.Chat.PromptText = "Smooth send";
                await PumpAsync(window, TimeSpan.FromMilliseconds(30));
                using (var warmup = window.CaptureRenderedFrame()!)
                    Measure(warmup);
                var button = view.GetVisualDescendants().OfType<Button>()
                    .Single(control => control.Name == "PART_SendButton");
                var tap = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                // The headless pointer extensions drain/render after each input too. Deliver the same
                // raw pointer events without those extra frames so observation starts before playback.
                var platform = window.PlatformImpl!;
                var headlessWindow = platform.GetType().GetInterfaces()
                    .Single(type => type.FullName == "Avalonia.Headless.IHeadlessWindow");
                var mouseDown = headlessWindow.GetMethod("MouseDown")!
                    .CreateDelegate<Action<Point, MouseButton, RawInputModifiers>>(platform);
                var mouseUp = headlessWindow.GetMethod("MouseUp")!
                    .CreateDelegate<Action<Point, MouseButton, RawInputModifiers>>(platform);
                mouseDown(tap, MouseButton.Left, RawInputModifiers.None);
                mouseUp(tap, MouseButton.Left, RawInputModifiers.None);
                send = model.Chat.SendCommand.ExecutionTask;
                Assert.NotNull(send);
                Assert.True(model.Chat.ShowThinking);
                Assert.Empty(model.Chat.PromptText);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                var turn = model.Chat.Turns[^1];
                var user = Assert.Single(turn.Items);
                var host = Assert.IsType<ContentPresenter>(preparedHost);
                Assert.Same(user, host.DataContext);
                Assert.Contains(host.GetVisualDescendants(), control => control is StrataChatMessage);
                Assert.True(hiddenBeforePaint, "The sent bubble must be hidden before its first paint.");
                var bubble = host.GetVisualDescendants().OfType<Border>()
                    .Single(control => control.Name == "PART_Bubble");
                bubble.Background = Brushes.White;
                Dispatcher.UIThread.RunJobs();
                var origin = host.TranslatePoint(default, window)!.Value;
                var region = new Rect(origin, host.Bounds.Size);
                var bounds = host.Bounds;
                var compositionVisual = Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(host);
                Assert.NotNull(compositionVisual);
                // A queued animation is not yet a rendered one. Await this host's pending batch before
                // occupying the UI thread, otherwise a fast suite can leave playback uncommitted.
                await compositionVisual.Compositor.RequestCommitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var transitionCount = host.Transitions?.Count ?? 0;
                var samples = new List<(double Brightness, double Y)>();
                var elapsed = Stopwatch.StartNew();
                var captureStartedAt = sinceHidden.ElapsedMilliseconds;
                var dispatcherWorkRan = false;
                Dispatcher.UIThread.Post(() => dispatcherWorkRan = true, DispatcherPriority.Background);
                while (elapsed.ElapsedMilliseconds < 380)
                {
                    // CaptureRenderedFrame drains UI jobs and renders extra frames before returning.
                    // Take this tick's pixels directly so measurement cannot skip the short entrance.
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using var frame = window.GetLastRenderedFrame()!;
                    samples.Add(Measure(frame, region));
                    Assert.Equal(bounds, host.Bounds);
                    Thread.Sleep(16);
                }
                Assert.False(dispatcherWorkRan, "Frame sampling must not service queued UI work.");
                Assert.True(samples.Any(sample =>
                    sample.Brightness > samples[^1].Brightness * 0.08
                    && sample.Brightness < samples[^1].Brightness * 0.92),
                    "The actual sent bubble must paint intermediate entrance frames while the UI thread is occupied. " +
                    $"Region {region}; composition visual {compositionVisual is not null}; transitions {transitionCount}; " +
                    $"capture started {captureStartedAt}ms after preparation; visible fractions: {string.Join(", ", samples.Select(sample =>
                        (sample.Brightness / samples[^1].Brightness).ToString("F2")))}.");

                var detached = false;
                host.DetachedFromVisualTree += (_, _) => detached = true;
                model.Chat.ApplyTranscript(new RemoteTranscript
                {
                    ChatId = id, Revision = 2, RevisionEpoch = "test",
                    Status = new RemoteChatStatus { ChatId = id, IsBusy = true },
                    WindowStartMessageIndex = windowRollsForward ? 1 : 0,
                    HasEarlierMessages = windowRollsForward,
                    Turns = [.. (windowRollsForward ? Array.Empty<RemoteTranscriptTurn>() : new[] { initialTurn }), new()
                    {
                        Id = "server-turn",
                        Items = [new()
                        {
                            Id = "server-user", Kind = RemoteProtocol.ItemKinds.User,
                            Text = "Smooth send", RequestId = handler.RequestId
                        }]
                    }]
                });
                await PumpAsync(window, TimeSpan.FromMilliseconds(60));
                Assert.False(detached);
                Assert.Same(turn, model.Chat.Turns[^1]);
                Assert.Same(user, Assert.Single(turn.Items));
                Assert.True(model.Chat.ShowThinking);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                handler.Response.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"ok":true}""")
                });
                if (send is not null)
                    await send;
                window.Close();
                Directory.Delete(storeDirectory, recursive: true);
            }
            return true;
        }, CancellationToken.None);
        failure?.Throw();
    }

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

    private static (double Brightness, double Y) Measure(Bitmap bitmap, Rect? region = null)
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
        var area = region ?? new Rect(20, 20, Math.Min(size.Width, 130) - 20, size.Height - 30);
        for (var y = Math.Max(0, (int)area.Top); y < Math.Min(size.Height, area.Bottom); y++)
        for (var x = Math.Max(0, (int)area.Left); x < Math.Min(size.Width, area.Right); x++)
        {
            var index = y * stride + x * 4;
            var value = bytes[index] + bytes[index + 1] + bytes[index + 2];
            brightness += value;
            weightedY += value * y;
        }
        return (brightness, brightness > 0 ? weightedY / brightness : 0);
    }

    private sealed class HeldSendHandler : HttpMessageHandler
    {
        public string? RequestId { get; private set; }
        public TaskCompletionSource<HttpResponseMessage> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            RequestId = document.RootElement.GetProperty("requestId").GetString();
            return await Response.Task.WaitAsync(cancellationToken);
        }
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
