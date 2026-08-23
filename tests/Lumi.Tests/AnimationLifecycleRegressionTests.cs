using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.Presence;
using Lumi.Services;
using Lumi.ViewModels;
using StrataTheme;
using StrataTheme.Animation;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class AnimationLifecycleRegressionTests
{
    private static readonly PulseExpectation[] ExpectedLumiPulses =
    [
        new("Views/McpServersView.axaml", "TextBlock", 0.3, 1, 0.8, "Alternate", "SineEaseInOut"),
        new("Views/OnboardingView.axaml", "Border[IsVisible=True]", 0.4, 1, 0.8, "Alternate"),
        new("Views/Controls/GitHubLoginView.axaml", "Border.login-pulse", 0.3, 1, 1.2, "Alternate", "SineEaseInOut"),
        new("Views/ChatView.axaml", "Border.steer-pill.steering Ellipse.steer-dot", 1, 0.25, 1.1, Easing: "SineEaseInOut"),
        new("Views/ChatView.axaml", "Border.suggestion-pill.pill-a", 0.3, 0.6, 0.95, PeakAt: 0.38),
        new("Views/ChatView.axaml", "Border.suggestion-pill.pill-b", 0.3, 0.6, 0.95, HoldUntil: 0.18, PeakAt: 0.56),
        new("Views/ChatView.axaml", "Border.suggestion-pill.pill-c", 0.3, 0.6, 0.95, HoldUntil: 0.36, PeakAt: 0.74),
        new("Views/ChatView.axaml", "Border#GitRefreshDot", 1, 0.25, 0.9, "Alternate"),
        new("Views/MainWindow.axaml", "Border#PillBusyDot", 1, 0.3, 1.2, "Alternate"),
        new("Views/MainWindow.axaml", "Border#BusyIndicator", 1, 0.3, 1.2, "Alternate"),
        new("Views/MainWindow.axaml", "Border#ProjectBusyIndicator", 1, 0.3, 1.2, "Alternate"),
    ];

    [Fact]
    public void RepositoryXaml_HasNoInfiniteStyleAnimations()
    {
        XNamespace avalonia = "https://github.com/avaloniaui";
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoots = new[]
        {
            Path.Combine(repositoryRoot, "src", "Lumi"),
            Path.Combine(repositoryRoot, "Strata", "src", "StrataTheme"),
        };

        var violations = sourceRoots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
            .SelectMany(path => XDocument.Load(path)
                .Descendants(avalonia + "Animation")
                .Where(animation => string.Equals(
                    (string?)animation.Attribute("IterationCount"),
                    "Infinite",
                    StringComparison.OrdinalIgnoreCase))
                .Select(_ => Path.GetRelativePath(repositoryRoot, path)))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void LumiLifecyclePulseStyles_PreserveOriginalVisualIdentity()
    {
        XNamespace avalonia = "https://github.com/avaloniaui";
        var lumiSourceRoot = Path.Combine(FindRepositoryRoot(), "src", "Lumi");

        foreach (var expected in ExpectedLumiPulses)
        {
            var style = XDocument
                .Load(Path.Combine(lumiSourceRoot, expected.RelativePath.Replace('/', Path.DirectorySeparatorChar)))
                .Descendants(avalonia + "Style")
                .Single(element =>
                    string.Equals((string?)element.Attribute("Selector"), expected.Selector, StringComparison.Ordinal) &&
                    FindSetter(element, avalonia, "LifecycleOpacityPulse.IsActive") is not null);

            Assert.Equal(expected.FromOpacity, ParseDouble(style, avalonia, "LifecycleOpacityPulse.FromOpacity"));
            Assert.Equal(expected.ToOpacity, ParseDouble(style, avalonia, "LifecycleOpacityPulse.ToOpacity"));
            Assert.Equal(
                TimeSpan.FromSeconds(expected.DurationSeconds),
                TimeSpan.Parse(
                    RequiredSetterValue(style, avalonia, "LifecycleOpacityPulse.Duration"),
                    CultureInfo.InvariantCulture));
            Assert.Equal(
                expected.PlaybackDirection,
                FindSetter(style, avalonia, "LifecycleOpacityPulse.PlaybackDirection")?.Attribute("Value")?.Value
                    ?? "Normal");
            Assert.Equal(
                expected.Easing,
                FindSetter(style, avalonia, "LifecycleOpacityPulse.Easing")?.Attribute("Value")?.Value
                    ?? "Linear");
            Assert.Equal(
                expected.HoldUntil,
                ParseOptionalDouble(style, avalonia, "LifecycleOpacityPulse.HoldUntil", 0));
            Assert.Equal(
                expected.PeakAt,
                ParseOptionalDouble(style, avalonia, "LifecycleOpacityPulse.PeakAt", 0.5));
            Assert.Equal("True", RequiredSetterValue(style, avalonia, "LifecycleOpacityPulse.IsActive"));
        }
    }

    [Fact]
    public void NavPillScaleAnimation_DoesNotUseWallClockStopFinalization()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Lumi",
            "Views",
            "MainWindow.axaml.cs"));
        var methodStart = source.IndexOf(
            "private static void AnimateNavPillScale",
            StringComparison.Ordinal);
        var nextMethod = source.IndexOf(
            "private void NewChatButton_Click",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(nextMethod > methodStart);

        var method = source[methodStart..nextMethod];
        Assert.Contains("StartAnimation(\"Scale\"", method, StringComparison.Ordinal);
        Assert.DoesNotContain("StopAnimation", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", method, StringComparison.Ordinal);
    }

    [Fact]
    public void StableCompositionEasing_MatchesAvaloniaDefaultAndRecoversFromNonFiniteProgress()
    {
        var stable = StableCompositionAnimations.EasingForTests;
        var expected = new SplineEasing(new KeySpline(0.25, 0.1, 0.25, 1.0));

        var maximumDifference = 0d;
        for (var step = 0; step <= 1_000; step++)
        {
            var progress = step / 1_000d;
            maximumDifference = Math.Max(
                maximumDifference,
                Math.Abs(stable.Ease(progress) - expected.Ease(progress)));
        }
        Assert.InRange(maximumDifference, 0, 0.002);

        var midpoint = stable.Ease(0.5);
        Assert.Equal(0, stable.Ease(double.NaN));
        Assert.Equal(0, stable.Ease(double.NegativeInfinity));
        Assert.Equal(1, stable.Ease(double.PositiveInfinity));
        Assert.Equal(midpoint, stable.Ease(0.5));
    }

    [Fact]
    public void StableCompositionEasing_RepeatedPoisonInputsCannotAffectUnrelatedProgress()
    {
        var easing = StableCompositionAnimations.EasingForTests;
        var expectedMidpoint = easing.Ease(0.5);

        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            Assert.Equal(0, easing.Ease(double.NaN));
            Assert.Equal(expectedMidpoint, easing.Ease(0.5));
        }
    }

    [Fact]
    public async Task StableCompositionFactory_ReplacesAvaloniaSharedDefaultEasing()
    {
        var session = HeadlessTestSession.Start();
        try
        {
            await session.Dispatch(() =>
            {
                var target = new Border();
                var window = new Window { Content = target };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();

                    var visual = ElementComposition.GetElementVisual(target);
                    Assert.NotNull(visual);

                    _ = visual.Compositor.CreateStableScalarKeyFrameAnimation();

                    Assert.True(StableCompositionAnimations.IsInstalledForTests(visual.Compositor));
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task PresenceStates_DoNotRunContinuousAnimation()
    {
        var session = HeadlessTestSession.Start(typeof(SkiaHeadlessTestApp));
        try
        {
            await session.Dispatch(() =>
            {
                var presence = new StrataPresence
                {
                    State = PresenceState.Dormant,
                    Halo = true,
                };
                var window = new Window { Width = 900, Height = 700, Content = presence };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                using var dormantFrame = window.CaptureRenderedFrame();
                Assert.NotNull(dormantFrame);

                Assert.False(presence.HasContinuousAnimationForTest);

                presence.State = PresenceState.Streaming;
                presence.Halo = false;
                Dispatcher.UIThread.RunJobs();
                using var streamingFrame = window.CaptureRenderedFrame();
                Assert.NotNull(streamingFrame);

                Assert.False(presence.HasContinuousAnimationForTest);

                presence.State = PresenceState.Thinking;
                presence.AnimateWhileWorking = true;
                Dispatcher.UIThread.RunJobs();
                Assert.True(presence.HasContinuousAnimationForTest);

                presence.AnimateWhileWorking = false;
                Dispatcher.UIThread.RunJobs();
                Assert.False(presence.HasContinuousAnimationForTest);

                presence.AnimateWhileWorking = true;
                presence.State = PresenceState.Attention;
                Dispatcher.UIThread.RunJobs();
                using var attentionFrame = window.CaptureRenderedFrame();
                Assert.NotNull(attentionFrame);

                Assert.False(presence.HasContinuousAnimationForTest);

                presence.State = PresenceState.Idle;
                Dispatcher.UIThread.RunJobs();
                using var idleFrame = window.CaptureRenderedFrame();
                Assert.NotNull(idleFrame);

                Assert.False(presence.HasContinuousAnimationForTest);

                Assert.True(presence.SplitToIsland(new Point(0.82, 0.5)));
                Dispatcher.UIThread.RunJobs();
                using var companionFrame = window.CaptureRenderedFrame();
                Assert.NotNull(companionFrame);

                Assert.False(presence.HasContinuousAnimationForTest);
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task OptInWorkingAnimationBeforeAttach_StartsBeaconScaleLoop()
    {
        var session = HeadlessTestSession.Start(typeof(SkiaHeadlessTestApp));
        try
        {
            await session.Dispatch(() =>
            {
                var presence = new StrataPresence
                {
                    State = PresenceState.Streaming,
                    AnimateWhileWorking = true,
                };
                var window = new Window { Width = 900, Height = 700, Content = presence };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    Assert.True(presence.IsBeaconScaleLoopActiveForTest);
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task PresenceController_DisableDetachesVisual_AndEnableReattachesIt()
    {
        var session = HeadlessTestSession.Start();
        try
        {
            await session.Dispatch(() =>
            {
                var host = new Grid();
                var window = new Window { Width = 900, Height = 700, Content = host };
                using var controller = new PresenceController(host, isEnabled: false);
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();

                    Assert.DoesNotContain(controller.Visual, host.Children);
                    Assert.False(controller.Visual.IsAttachedToVisualTree());

                    var beginDeferredArm = typeof(PresenceController).GetMethod(
                        "BeginDeferredArm",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new MissingMethodException(nameof(PresenceController), "BeginDeferredArm");
                    beginDeferredArm.Invoke(controller, null);
                    var armTimer = (DispatcherTimer?)GetRequiredField(controller, "_armTimer").GetValue(controller);
                    Assert.False(armTimer?.IsEnabled == true);

                    controller.SetEnabled(true);
                    Dispatcher.UIThread.RunJobs();

                    Assert.Same(controller.Visual, host.Children[0]);
                    Assert.True(controller.Visual.IsAttachedToVisualTree());

                    controller.SetEnabled(false);
                    Dispatcher.UIThread.RunJobs();

                    Assert.DoesNotContain(controller.Visual, host.Children);
                    Assert.False(controller.Visual.IsAttachedToVisualTree());
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task PresenceController_SameStateChatSwitchesUseFiniteCoalescedHandoff()
    {
        var session = HeadlessTestSession.Start(typeof(SkiaHeadlessTestApp));
        try
        {
            await session.Dispatch(() =>
            {
                var idleA = ExistingChat("Idle A");
                var idleB = ExistingChat("Idle B");
                var thinkingA = ExistingChat("Thinking A");
                var thinkingB = ExistingChat("Thinking B");
                var thinkingC = ExistingChat("Thinking C");
                var dataStore = new DataStore(new AppData
                {
                    Chats = [idleA, idleB, thinkingA, thinkingB, thinkingC],
                });

                using var idleSurfaceA = Surface(dataStore, idleA);
                using var idleSurfaceB = Surface(dataStore, idleB);
                using var thinkingSurfaceA = Surface(dataStore, thinkingA, isBusy: true);
                using var thinkingSurfaceB = Surface(dataStore, thinkingB, isBusy: true);
                using var thinkingSurfaceC = Surface(dataStore, thinkingC, isBusy: true);

                var host = new Grid();
                var window = new Window { Width = 900, Height = 700, Content = host };
                using var controller = new PresenceController(host);
                try
                {
                    controller.Attach(idleSurfaceA);
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);

                    controller.Visual.FocusPoint = new Point(0.31, 0.37);
                    var heldFocus = controller.Visual.FocusPoint;
                    controller.Repoint(idleSurfaceB);
                    Assert.Equal(1, controller.Visual.HandoffCountForTest);
                    Assert.True(controller.Visual.IsHandoffAnimationActiveForTest);
                    Assert.False(controller.Visual.HasContinuousAnimationForTest);
                    Assert.Equal(heldFocus, controller.Visual.FocusPoint);
                    Assert.NotNull(GetRequiredField(controller, "_sameStateFocusSnapRegistration").GetValue(controller));
                    var focusTimer = (DispatcherTimer?)GetRequiredField(controller, "_focusTimer").GetValue(controller);
                    Assert.False(focusTimer?.IsEnabled == true);

                    controller.Repoint(thinkingSurfaceA);
                    Assert.Equal(PresenceState.Thinking, controller.Visual.State);
                    Assert.Equal(1, controller.Visual.HandoffCountForTest);
                    Assert.False(controller.Visual.IsHandoffAnimationActiveForTest);
                    Assert.Null(GetRequiredField(controller, "_sameStateFocusSnapRegistration").GetValue(controller));

                    Thread.Sleep(1600);
                    Assert.False(controller.Visual.IsHandoffAnimationActiveForTest);
                    controller.Repoint(thinkingSurfaceB);
                    Assert.Equal(2, controller.Visual.HandoffCountForTest);
                    Assert.True(controller.Visual.IsHandoffAnimationActiveForTest);
                    Assert.False(controller.Visual.HasContinuousAnimationForTest);
                    var firstHoldUntil = (DateTime)GetRequiredField(
                        controller,
                        "_sameStateFocusHoldUntil").GetValue(controller)!;

                    controller.Repoint(thinkingSurfaceC);
                    Assert.Equal(2, controller.Visual.HandoffCountForTest);
                    var coalescedHoldUntil = (DateTime)GetRequiredField(
                        controller,
                        "_sameStateFocusHoldUntil").GetValue(controller)!;
                    Assert.InRange(
                        Math.Abs((coalescedHoldUntil - firstHoldUntil).TotalMilliseconds),
                        0,
                        60);

                    controller.SetEnabled(false);
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(controller.Visual.IsHandoffAnimationActiveForTest);
                    Assert.Null(GetRequiredField(controller, "_sameStateFocusSnapRegistration").GetValue(controller));
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task VulnerableDefaultEasing_NaturalCycleBoundaryFreezesOffsetAndScale()
    {
        var result = await RunCompositionCycleBoundaryScenarioAsync(useStableEasing: false);

        Assert.True(result.BaselineOffsetMoved, result.ToString());
        Assert.True(result.BaselineScaleMoved, result.ToString());
        Assert.True(result.NonFiniteInputCount > 0, result.ToString());
        Assert.True(result.VulnerableSplinePoisoned, result.ToString());
        Assert.False(result.PostTriggerOffsetMoved, result.ToString());
        Assert.False(result.PostTriggerScaleMoved, result.ToString());
    }

    [Fact]
    public async Task StableDefaultEasing_SameNaturalCycleBoundaryKeepsOffsetAndScaleMoving()
    {
        var result = await RunCompositionCycleBoundaryScenarioAsync(useStableEasing: true);

        Assert.True(result.BaselineOffsetMoved, result.ToString());
        Assert.True(result.BaselineScaleMoved, result.ToString());
        Assert.True(result.NonFiniteInputCount > 0, result.ToString());
        Assert.False(result.VulnerableSplinePoisoned, result.ToString());
        Assert.True(result.PostTriggerOffsetMoved, result.ToString());
        Assert.True(result.PostTriggerScaleMoved, result.ToString());
    }

    [Fact]
    public async Task ProductionOneSecondPulseCycleBoundary_ReproducesAndFixesGlobalFreeze()
    {
        var vulnerable = await RunCompositionCycleBoundaryScenarioAsync(
            useStableEasing: false,
            triggerCount: 1,
            maxExtraTicks: 0,
            triggerDuration: TimeSpan.FromSeconds(1),
            forceExactCycleBoundary: true);
        Assert.True(vulnerable.NonFiniteInputCount > 0, vulnerable.ToString());
        Assert.True(vulnerable.VulnerableSplinePoisoned, vulnerable.ToString());
        Assert.False(vulnerable.PostTriggerOffsetMoved, vulnerable.ToString());
        Assert.False(vulnerable.PostTriggerScaleMoved, vulnerable.ToString());

        var stable = await RunCompositionCycleBoundaryScenarioAsync(
            useStableEasing: true,
            triggerCount: 1,
            maxExtraTicks: 0,
            triggerDuration: TimeSpan.FromSeconds(1),
            forceExactCycleBoundary: true);
        Assert.True(stable.NonFiniteInputCount > 0, stable.ToString());
        Assert.False(stable.VulnerableSplinePoisoned, stable.ToString());
        Assert.True(stable.PostTriggerOffsetMoved, stable.ToString());
        Assert.True(stable.PostTriggerScaleMoved, stable.ToString());
    }

    [Fact]
    public void RepositoryCompositionKeyframes_UseStableFactories()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoots = new[]
        {
            Path.Combine(repositoryRoot, "src", "Lumi"),
            Path.Combine(repositoryRoot, "Strata", "src", "StrataTheme"),
        };
        var unsafeFactory = new Regex(
            @"\.Create(?!Stable)[A-Za-z0-9]+KeyFrameAnimation\(",
            RegexOptions.CultureInvariant);

        var violations = sourceRoots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !string.Equals(
                Path.GetFileName(path),
                "StableCompositionAnimations.cs",
                StringComparison.Ordinal))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (Line: line, Number: index + 1))
                .Where(item => unsafeFactory.IsMatch(item.Line))
                .Select(item => $"{Path.GetRelativePath(repositoryRoot, path)}:{item.Number}"))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"Composition keyframes must use stable factories:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [Fact]
    public Task ToolCompletionRace_DetachesEveryPulseAndCollectsEveryCard() =>
        Task.Run(RunToolCompletionRaceAsync);

    private static async Task RunToolCompletionRaceAsync()
    {
        const int cycleCount = 40;
        var weakCards = new List<WeakReference>(cycleCount);
        var activePulseCount = 0;
        var stoppedPulseCount = 0;
        var retainedCards = -1;
        Window? window = null;

        var session = HeadlessTestSession.Start();
        try
        {
            await session.Dispatch(() =>
            {
                var panel = new StackPanel();
                var proofWindow = new Window { Width = 720, Height = 640, Content = panel };
                window = proofWindow;
                proofWindow.Show();

                for (var i = 0; i < cycleCount; i++)
                {
                    TemplatedControl card = i % 2 == 0
                        ? new StrataAiToolCall
                        {
                            ToolName = "memory-proof",
                            Status = StrataAiToolCallStatus.InProgress,
                            InputParameters = "{\"cycle\":true}",
                        }
                        : new StrataTerminalPreview
                        {
                            ToolName = "PowerShell",
                            Command = "Write-Output memory-proof",
                            Status = StrataAiToolCallStatus.InProgress,
                        };

                    panel.Children.Add(card);
                    card.ApplyTemplate();
                    Dispatcher.UIThread.RunJobs();

                    var pulseTargets = card
                        .GetVisualDescendants()
                        .Where(LifecycleOpacityPulse.GetIsActive)
                        .ToArray();
                    activePulseCount += pulseTargets.Count(LifecycleOpacityPulse.IsRunning);

                    if (card is StrataAiToolCall toolCard)
                        toolCard.Status = StrataAiToolCallStatus.Completed;
                    else
                        ((StrataTerminalPreview)card).Status = StrataAiToolCallStatus.Completed;

                    panel.Children.Remove(card);
                    stoppedPulseCount += pulseTargets.Count(target => !LifecycleOpacityPulse.IsRunning(target));
                    weakCards.Add(new WeakReference(card));
                    pulseTargets = null!;
                    card = null!;
                }

                Dispatcher.UIThread.RunJobs();
                proofWindow.Content = new Border();
                Dispatcher.UIThread.RunJobs();
                for (var i = 0; i < 3; i++)
                {
                    using var frame = proofWindow.CaptureRenderedFrame();
                    Dispatcher.UIThread.RunJobs();
                }
            }, CancellationToken.None);

            ForceFullGc();
            retainedCards = weakCards.Count(reference => reference.IsAlive);
        }
        finally
        {
            if (window is not null)
            {
                var proofWindow = window;
                await session.Dispatch(() =>
                {
                    proofWindow.Close();
                    Dispatcher.UIThread.RunJobs();
                }, CancellationToken.None);
                window = null;
            }

            await Task.Run(session.Dispose).ConfigureAwait(false);
        }

        Assert.Equal(cycleCount * 2, activePulseCount);
        Assert.Equal(cycleCount * 2, stoppedPulseCount);
        Assert.Equal(0, retainedCards);
    }

    private static async Task<CompositionCycleBoundaryResult> RunCompositionCycleBoundaryScenarioAsync(
        bool useStableEasing,
        int triggerCount = 1,
        int maxExtraTicks = 10,
        TimeSpan? triggerDuration = null,
        bool forceExactCycleBoundary = false)
    {
        CompositionCycleBoundaryResult? result = null;
        var session = HeadlessTestSession.Start(typeof(SkiaHeadlessTestApp));
        try
        {
            await session.Dispatch(
                () => result = RunCompositionCycleBoundaryScenario(
                    useStableEasing,
                    triggerCount,
                    maxExtraTicks,
                    triggerDuration ?? TimeSpan.FromTicks(1),
                    forceExactCycleBoundary),
                CancellationToken.None);
        }
        finally
        {
            await Task.Run(session.Dispose).ConfigureAwait(false);
        }

        return result ?? throw new InvalidOperationException("Composition cycle-boundary scenario did not run.");
    }

    private static CompositionCycleBoundaryResult RunCompositionCycleBoundaryScenario(
        bool useStableEasing,
        int triggerCount,
        int maxExtraTicks,
        TimeSpan triggerDuration,
        bool forceExactCycleBoundary)
    {
        var offsetTarget = new Border
        {
            Width = 30,
            Height = 30,
            Background = Brushes.Red,
        };
        Canvas.SetLeft(offsetTarget, 20);
        Canvas.SetTop(offsetTarget, 40);

        var scaleTarget = new Border
        {
            Width = 40,
            Height = 40,
            Background = Brushes.Blue,
        };
        Canvas.SetLeft(scaleTarget, 220);
        Canvas.SetTop(scaleTarget, 35);

        var triggerTargets = Enumerable.Range(0, triggerCount)
            .Select(_ => new Border { Width = 1, Height = 1 })
            .ToArray();
        var canvas = new Canvas
        {
            Children =
            {
                offsetTarget,
                scaleTarget,
            },
        };
        foreach (var triggerTarget in triggerTargets)
        {
            Canvas.SetLeft(triggerTarget, -100);
            Canvas.SetTop(triggerTarget, -100);
            canvas.Children.Add(triggerTarget);
        }

        var window = new Window
        {
            Width = 320,
            Height = 120,
            Background = Brushes.Black,
            Content = canvas,
        };

        try
        {
            window.Show();
            TickComposition();
            TickComposition();

            var offsetVisual = ElementComposition.GetElementVisual(offsetTarget)
                ?? throw new InvalidOperationException("Offset probe has no composition visual.");
            var scaleVisual = ElementComposition.GetElementVisual(scaleTarget)
                ?? throw new InvalidOperationException("Scale probe has no composition visual.");
            var compositor = offsetVisual.Compositor;

            var vulnerableSpline = useStableEasing
                ? null
                : new SplineEasing(new KeySpline(0.25, 0.1, 0.25, 1.0));
            var observer = new ObservingEasing(
                useStableEasing
                    ? StableCompositionAnimations.EasingForTests
                    : vulnerableSpline!);
            GetRequiredField(compositor, "<DefaultEasing>k__BackingField").SetValue(compositor, observer);

            var offsetBase = offsetVisual.Offset;
            scaleVisual.CenterPoint = new Avalonia.Vector3D(20, 20, 0);
            var initial = CaptureProbeFrame(window);

            StartProbeAnimations(compositor, offsetVisual, scaleVisual, offsetBase);
            TickComposition();
            TickComposition(80);
            var baselineAnimated = CaptureProbeFrame(window);
            var baselineOffsetMoved = OffsetMoved(initial, baselineAnimated);
            var baselineScaleMoved = ScaleMoved(initial, baselineAnimated);

            canvas.Children.Remove(offsetTarget);
            canvas.Children.Remove(scaleTarget);

            var postOffsetTarget = new Border
            {
                Width = 30,
                Height = 30,
                Background = Brushes.Red,
            };
            Canvas.SetLeft(postOffsetTarget, 20);
            Canvas.SetTop(postOffsetTarget, 40);
            canvas.Children.Add(postOffsetTarget);

            var postScaleTarget = new Border
            {
                Width = 40,
                Height = 40,
                Background = Brushes.Blue,
            };
            Canvas.SetLeft(postScaleTarget, 220);
            Canvas.SetTop(postScaleTarget, 35);
            canvas.Children.Add(postScaleTarget);

            TickComposition();
            TickComposition();
            var postOffsetVisual = ElementComposition.GetElementVisual(postOffsetTarget)
                ?? throw new InvalidOperationException("Post-trigger Offset probe has no composition visual.");
            var postScaleVisual = ElementComposition.GetElementVisual(postScaleTarget)
                ?? throw new InvalidOperationException("Post-trigger Scale probe has no composition visual.");
            var postOffsetBase = postOffsetVisual.Offset;
            postScaleVisual.CenterPoint = new Avalonia.Vector3D(20, 20, 0);
            var postInitial = CaptureProbeFrame(window);

            var poisonIteration = StartCycleBoundaryStress(
                compositor,
                triggerTargets,
                observer,
                maxExtraTicks,
                triggerDuration,
                forceExactCycleBoundary);
            var vulnerableSplinePoisoned = vulnerableSpline is not null &&
                IsSplineParameterNaN(vulnerableSpline);

            StartProbeAnimations(compositor, postOffsetVisual, postScaleVisual, postOffsetBase);
            TickComposition();
            TickComposition(80);
            var postTrigger = CaptureProbeFrame(window);

            return new CompositionCycleBoundaryResult(
                BaselineOffsetMoved: baselineOffsetMoved,
                BaselineScaleMoved: baselineScaleMoved,
                NonFiniteInputCount: observer.NonFiniteInputCount,
                VulnerableSplinePoisoned: vulnerableSplinePoisoned,
                PostTriggerOffsetMoved: OffsetMoved(postInitial, postTrigger),
                PostTriggerScaleMoved: ScaleMoved(postInitial, postTrigger),
                PoisonIteration: poisonIteration,
                Initial: initial,
                BaselineAnimated: baselineAnimated,
                PostInitial: postInitial,
                PostTrigger: postTrigger);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static int StartCycleBoundaryStress(
        Compositor compositor,
        IReadOnlyList<Border> triggerTargets,
        ObservingEasing observer,
        int maxExtraTicks,
        TimeSpan triggerDuration,
        bool forceExactCycleBoundary)
    {
        for (var index = 0; index < triggerTargets.Count; index++)
        {
            var visual = ElementComposition.GetElementVisual(triggerTargets[index])
                ?? throw new InvalidOperationException($"Trigger {index} has no composition visual.");
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0f, 0f);
            animation.InsertKeyFrame(0.5f, 1f);
            animation.InsertKeyFrame(1f, 0f);
            animation.Duration = triggerDuration;
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Opacity", animation);

            TickComposition();
            if (observer.NonFiniteInputCount > 0)
                return index;

            if (forceExactCycleBoundary)
            {
                ForceExactCycleBoundary(compositor, observer, triggerDuration);
                return index;
            }
        }

        for (var tick = 0; tick < maxExtraTicks; tick++)
        {
            TickComposition();
            if (observer.NonFiniteInputCount > 0)
                return triggerTargets.Count + tick;
        }

        return -1;
    }

    private static void ForceExactCycleBoundary(
        Compositor compositor,
        ObservingEasing observer,
        TimeSpan triggerDuration)
    {
        var server = GetRequiredField(compositor, "_server").GetValue(compositor)
            ?? throw new InvalidOperationException("Compositor has no server counterpart.");
        var animations = GetRequiredProperty(server, "Animations").GetValue(server)
            ?? throw new InvalidOperationException("Server compositor has no animation clock.");
        var clockItems = GetRequiredField(animations, "_clockItems").GetValue(animations)
            as System.Collections.IEnumerable
            ?? throw new InvalidOperationException("Server animation clock items are unavailable.");

        object? trigger = null;
        foreach (var clockItem in clockItems)
        {
            if (clockItem is null ||
                !clockItem.GetType().Name.StartsWith(
                    "KeyFrameAnimationInstance",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var keyFrames = GetRequiredField(clockItem, "_keyFrames").GetValue(clockItem) as Array;
            if (keyFrames is null || keyFrames.Length == 0)
                continue;

            var firstKeyFrame = keyFrames.GetValue(0);
            if (firstKeyFrame is null)
                continue;

            var easing = GetRequiredField(firstKeyFrame, "EasingFunction").GetValue(firstKeyFrame);
            var duration = (TimeSpan)GetRequiredField(clockItem, "_duration").GetValue(clockItem)!;
            if (ReferenceEquals(easing, observer) && duration == triggerDuration)
            {
                trigger = clockItem;
                break;
            }
        }

        if (trigger is null)
            throw new InvalidOperationException("Could not locate the production-duration trigger animation.");

        var startedAt = (TimeSpan)GetRequiredField(trigger, "_startedAt").GetValue(trigger)!;
        var serverNowField = GetRequiredField(server, "<ServerNow>k__BackingField");
        var originalServerNow = serverNowField.GetValue(server);
        try
        {
            serverNowField.SetValue(server, startedAt + triggerDuration);
            GetRequiredMethod(animations, "Process").Invoke(animations, null);
        }
        finally
        {
            serverNowField.SetValue(server, originalServerNow);
        }

        if (observer.NonFiniteInputCount == 0)
            throw new InvalidOperationException("Exact cycle boundary did not reach the configured easing.");
    }

    private static void StartProbeAnimations(
        Compositor compositor,
        CompositionVisual offsetVisual,
        CompositionVisual scaleVisual,
        Avalonia.Vector3D offsetBase)
    {
        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.Target = "Offset";
        offset.InsertKeyFrame(
            1f,
            new System.Numerics.Vector3(
                (float)offsetBase.X + 80f,
                (float)offsetBase.Y,
                (float)offsetBase.Z));
        offset.Duration = TimeSpan.FromMilliseconds(200);
        offsetVisual.StartAnimation("Offset", offset);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Target = "Scale";
        scale.InsertKeyFrame(1f, new System.Numerics.Vector3(0.3f, 0.3f, 1f));
        scale.Duration = TimeSpan.FromMilliseconds(200);
        scaleVisual.StartAnimation("Scale", scale);
    }

    private static void TickComposition(int realMilliseconds = 0)
    {
        if (realMilliseconds > 0)
            Thread.Sleep(realMilliseconds);

        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static ProbeFrame CaptureProbeFrame(Window window)
    {
        using var bitmap = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("Headless compositor did not return a rendered frame.");
        return MeasureProbeFrame(bitmap);
    }

    private static unsafe ProbeFrame MeasureProbeFrame(WriteableBitmap bitmap)
    {
        using var framebuffer = bitmap.Lock();
        var redCount = 0;
        var blueCount = 0;
        double redXSum = 0;
        var address = (byte*)framebuffer.Address;

        for (var y = 0; y < framebuffer.Size.Height; y++)
        {
            var row = address + (y * framebuffer.RowBytes);
            for (var x = 0; x < framebuffer.Size.Width; x++)
            {
                var pixel = row + (x * 4);
                var red = pixel[0];
                var green = pixel[1];
                var blue = pixel[2];

                if (red > 180 && green < 90 && blue < 90)
                {
                    redCount++;
                    redXSum += x;
                }
                else if (blue > 180 && green < 90 && red < 90)
                {
                    blueCount++;
                }
            }
        }

        return new ProbeFrame(
            RedCentroidX: redCount == 0 ? double.NaN : redXSum / redCount,
            RedPixelCount: redCount,
            BluePixelCount: blueCount);
    }

    private static bool OffsetMoved(ProbeFrame before, ProbeFrame after) =>
        after.RedCentroidX - before.RedCentroidX >= 5;

    private static bool ScaleMoved(ProbeFrame before, ProbeFrame after) =>
        after.BluePixelCount >= before.BluePixelCount * 0.05 &&
        after.BluePixelCount <= before.BluePixelCount * 0.85;

    private static bool IsSplineParameterNaN(SplineEasing spline)
    {
        var keySpline = GetRequiredField(spline, "_internalKeySpline").GetValue(spline)!;
        return double.IsNaN((double)GetRequiredField(keySpline, "_parameter").GetValue(keySpline)!);
    }

    private static double ParseDouble(XElement style, XNamespace avalonia, string propertyName) =>
        double.Parse(RequiredSetterValue(style, avalonia, propertyName), CultureInfo.InvariantCulture);

    private static double ParseOptionalDouble(
        XElement style,
        XNamespace avalonia,
        string propertyName,
        double defaultValue)
    {
        var value = FindSetter(style, avalonia, propertyName)?.Attribute("Value")?.Value;
        return value is null
            ? defaultValue
            : double.Parse(value, CultureInfo.InvariantCulture);
    }

    private static string RequiredSetterValue(XElement style, XNamespace avalonia, string propertyName) =>
        FindSetter(style, avalonia, propertyName)?.Attribute("Value")?.Value
        ?? throw new InvalidDataException($"Missing setter '{propertyName}'.");

    private static XElement? FindSetter(XElement style, XNamespace avalonia, string propertyName) =>
        style
            .Elements(avalonia + "Setter")
            .SingleOrDefault(setter =>
                setter.Attribute("Property")?.Value.EndsWith(propertyName, StringComparison.Ordinal) == true);

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var startingDirectories = new[]
        {
            new FileInfo(sourceFilePath).Directory!,
            new DirectoryInfo(Directory.GetCurrentDirectory()),
            new DirectoryInfo(AppContext.BaseDirectory),
        };

        foreach (var startingDirectory in startingDirectories)
        {
            for (var directory = startingDirectory;
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "Lumi", "Lumi.csproj")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "Strata")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Lumi repository root.");
    }

    private static FieldInfo GetRequiredField(object instance, string fieldName) =>
        instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(instance.GetType().FullName, fieldName);

    private static Chat ExistingChat(string title)
    {
        var chat = new Chat { Title = title };
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "Hello" });
        return chat;
    }

    private static ChatViewModel Surface(DataStore dataStore, Chat chat, bool isBusy = false) =>
        new(dataStore, TestCopilot.Shared)
        {
            CurrentChat = chat,
            IsBusy = isBusy,
        };

    private static PropertyInfo GetRequiredProperty(object instance, string propertyName) =>
        instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMemberException(instance.GetType().FullName, propertyName);

    private static MethodInfo GetRequiredMethod(object instance, string methodName) =>
        instance.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(instance.GetType().FullName, methodName);

    private static void ForceFullGc()
    {
        for (var i = 0; i < 5; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    private sealed record PulseExpectation(
        string RelativePath,
        string Selector,
        double FromOpacity,
        double ToOpacity,
        double DurationSeconds,
        string PlaybackDirection = "Normal",
        string Easing = "Linear",
        double HoldUntil = 0,
        double PeakAt = 0.5);

    private sealed record CompositionCycleBoundaryResult(
        bool BaselineOffsetMoved,
        bool BaselineScaleMoved,
        long NonFiniteInputCount,
        bool VulnerableSplinePoisoned,
        bool PostTriggerOffsetMoved,
        bool PostTriggerScaleMoved,
        int PoisonIteration,
        ProbeFrame Initial,
        ProbeFrame BaselineAnimated,
        ProbeFrame PostInitial,
        ProbeFrame PostTrigger);

    private readonly record struct ProbeFrame(
        double RedCentroidX,
        int RedPixelCount,
        int BluePixelCount);

    private sealed class ObservingEasing(Easing inner) : Easing
    {
        private long _nonFiniteInputCount;

        public long NonFiniteInputCount => Interlocked.Read(ref _nonFiniteInputCount);

        public override double Ease(double progress)
        {
            var result = inner.Ease(progress);

            if (!double.IsFinite(progress))
                Interlocked.Increment(ref _nonFiniteInputCount);

            return result;
        }
    }
}
