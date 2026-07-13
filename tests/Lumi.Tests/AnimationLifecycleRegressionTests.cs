using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
}
