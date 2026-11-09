#if DEBUG || LUMI_MOBILE_SMOOTHNESS
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Smoothness;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using StrataTheme.Controls;
using Xunit;
using Xunit.Abstractions;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class MobileSmoothnessSurfaceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AQuietGapBetweenStrokesDoesNotFinishTheDriversInputBatch()
    {
        using var session = HeadlessMobileSession.Start();
        await session.Dispatch(async () =>
        {
            await using var shell = MobileSmoothnessFixture.CreateShell();
            var view = new MobileShellView { DataContext = shell };
            var window = new Window { Width = 390, Height = 844, Content = view };
            try
            {
                window.Show();
                await shell.StartAsync();
                using var benchmark = new MobileSmoothnessSession(view);
                var preparation = benchmark.CommandAsync("""{"operation":"prepare","scenario":"scroll-plain"}""");
                while (!preparation.IsCompleted)
                    await Pump();
                using var prepared = JsonDocument.Parse(await preparation);
                Assert.Equal(JsonValueKind.Null, prepared.RootElement.GetProperty("error").ValueKind);
                var target = prepared.RootElement.GetProperty("target");
                var x = target.GetProperty("x").GetDouble();
                var y = target.GetProperty("y").GetDouble();
                await benchmark.CommandAsync("""{"operation":"arm","scenario":"scroll-plain"}""");
                benchmark.ObserveHostTouch(x, y, pressed: true);
                benchmark.ObserveHostTouch(x, y, pressed: false);
                var gap = System.Diagnostics.Stopwatch.StartNew();
                while (gap.Elapsed < TimeSpan.FromMilliseconds(650))
                    await Pump();
                using var pending = JsonDocument.Parse(await benchmark.CommandAsync("""{"operation":"read"}"""));
                Assert.False(pending.RootElement.GetProperty("measurement").GetProperty("complete").GetBoolean());
                using var finish = JsonDocument.Parse(await benchmark.CommandAsync("""{"operation":"finish-input"}"""));
                Assert.Equal(JsonValueKind.Null, finish.RootElement.GetProperty("error").ValueKind);
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline)
                {
                    await Pump();
                    using var state = JsonDocument.Parse(await benchmark.CommandAsync("""{"operation":"read"}"""));
                    if (!state.RootElement.GetProperty("measurement").GetProperty("complete").GetBoolean())
                        continue;
                    // This deliberately motionless contract test must fail coverage, not report a fast scroll.
                    Assert.Contains("did not move enough real content",
                        state.RootElement.GetProperty("measurement").GetProperty("error").GetString());
                    return;
                }
                Assert.Fail("The completed input batch did not settle.");

                async Task Pump()
                {
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    await Task.Delay(16);
                }
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MixedTurnsAreBuiltColdAndRetainedWithoutLosingTheirCards()
    {
        using var session = HeadlessMobileSession.Start();
        await session.Dispatch(async () =>
        {
            await using var shell = MobileSmoothnessFixture.CreateShell();
            var view = new MobileShellView { DataContext = shell };
            var window = new Window { Width = 390, Height = 844, Content = view };
            var events = new List<(string Turn, string Kinds, string Phase, double Ms)>();
            void Presented(TranscriptTurnView turn, string phase, double duration)
            {
                if (turn.DataContext is TranscriptTurnViewModel model)
                    events.Add((model.Id, string.Join(",", model.DisplayItems.Select(item => item.Kind)), phase, duration));
            }
            TranscriptTurnView.PresentationMeasured += Presented;
            try
            {
                window.Show();
                await shell.StartAsync();
                using var benchmark = new MobileSmoothnessSession(view);
                var preparation = benchmark.CommandAsync("""{"operation":"prepare","scenario":"scroll-reveal-cold"}""");
                await PumpUntil(() => preparation.IsCompleted);
                using var result = JsonDocument.Parse(await preparation);
                Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("error").ValueKind);
                var transcript = view.FindControl<ChatDetailView>("ChatSurface")!.FindControl<ItemsControl>("Transcript")!;
                events.Clear();
                var retained = new Dictionary<string, Control>();
                for (var index = shell.Chat.Turns.Count - 1; index >= 0; index--)
                {
                    transcript.ScrollIntoView(index);
                    await Settle();
                    var host = transcript.GetVisualDescendants().OfType<TranscriptTurnView>()
                        .Single(turn => ReferenceEquals(turn.DataContext, shell.Chat.Turns[index]));
                    Assert.True(host.IsRealized);
                    retained.Add(shell.Chat.Turns[index].Id, Assert.IsAssignableFrom<Control>(host.Child));
                    var bubble = Assert.Single(host.GetVisualDescendants().OfType<StrataChatMessage>());
                    Assert.DoesNotContain(bubble.GetVisualDescendants(), child => child is Button or TextBox);
                }
                Assert.True(events.Count(e => e.Phase == "cold") >= 3);
                foreach (var e in events.Where(e => e.Phase == "measure").OrderByDescending(e => e.Ms).Take(12))
                    output.WriteLine($"{e.Turn} {e.Kinds}: {e.Phase} {e.Ms:F2}ms");
                events.Clear();
                for (var index = 0; index < shell.Chat.Turns.Count; index++)
                {
                    transcript.ScrollIntoView(index);
                    await Settle();
                    var host = transcript.GetVisualDescendants().OfType<TranscriptTurnView>()
                        .Single(turn => ReferenceEquals(turn.DataContext, shell.Chat.Turns[index]));
                    Assert.Same(retained[shell.Chat.Turns[index].Id], host.Child);
                    Assert.True(host.IsRealized);
                }
                Assert.DoesNotContain(events, e => e.Phase == "cold");
                Assert.True(events.Count(e => e.Phase == "retained") >= 3);

                async Task PumpUntil(Func<bool> done)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(15);
                    while (!done() && DateTime.UtcNow < deadline)
                        await Settle();
                    Assert.True(done(), "The real fixture surface did not settle.");
                }
                async Task Settle()
                {
                    for (var frame = 0; frame < 3; frame++)
                    {
                        window.UpdateLayout();
                        Dispatcher.UIThread.RunJobs();
                        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        await Task.Delay(16);
                    }
                }
            }
            finally
            {
                TranscriptTurnView.PresentationMeasured -= Presented;
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task KeyboardReadinessWaitsForTheReportedSlideToSettle()
    {
        using var session = HeadlessMobileSession.Start();
        await session.Dispatch(async () =>
        {
            await using var shell = MobileSmoothnessFixture.CreateShell();
            var view = new MobileShellView { DataContext = shell };
            var window = new Window { Width = 390, Height = 844, Content = view };
            try
            {
                window.Show();
                window.UpdateLayout();
                var reports = new List<(double Inset, double Duration)>();
                view.SmoothnessKeyboardReported += (inset, duration) => reports.Add((inset, duration));
                view.ApplyInputPaneGeometry(Avalonia.Controls.Platform.InputPaneState.Open,
                    new Avalonia.Rect(0, 544, 390, 300), TimeSpan.FromMilliseconds(180),
                    new Avalonia.Animation.Easings.LinearEasing());
                Assert.Contains(reports, report => report.Inset == 300 && report.Duration == 180);
                Assert.False(view.IsSmoothnessKeyboardSettled);
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (!view.IsSmoothnessKeyboardSettled && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(16);
                    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                }
                Assert.True(view.IsSmoothnessKeyboardSettled);
                Assert.InRange(view.SmoothnessKeyboardInset, 299, 300);
                view.ApplyInputPaneGeometry(Avalonia.Controls.Platform.InputPaneState.Closed, default);
                Assert.True(view.IsSmoothnessKeyboardSettled);
                Assert.Equal(0, view.SmoothnessKeyboardInset);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EveryScenarioPreparesARealTouchReachableSurface()
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            await using var shell = MobileSmoothnessFixture.CreateShell();
            var view = new MobileShellView { DataContext = shell };
            var window = new Window { Width = 390, Height = 844, Content = view };
            try
            {
                window.Show();
                await shell.StartAsync();
                using var benchmark = new MobileSmoothnessSession(view);
                foreach (var scenario in MobileSmoothnessSession.Scenarios)
                {
                    if (scenario == "switch-plain")
                        view.FindControl<MobileDrawerView>("DrawerContent")!.FindControl<ScrollViewer>("DrawerScroll")!
                            .Offset = new Avalonia.Vector(0, 2000);
                    var prepare = benchmark.CommandAsync(JsonSerializer.Serialize(
                        new SmoothnessRequest("prepare", scenario), SmoothnessJsonContext.Default.SmoothnessRequest));
                    while (!prepare.IsCompleted)
                    {
                        window.UpdateLayout();
                        Dispatcher.UIThread.RunJobs();
                        await Task.Delay(16);
                    }
                    using var result = JsonDocument.Parse(await prepare);
                    Assert.True(result.RootElement.GetProperty("error").ValueKind == JsonValueKind.Null,
                        $"{scenario}: {result.RootElement.GetProperty("error")}");
                    Assert.Equal(JsonValueKind.Object, result.RootElement.GetProperty("target").ValueKind);
                    Assert.True(result.RootElement.GetProperty("target").GetProperty("width").GetDouble() > 0);
                }
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }
}
#endif
