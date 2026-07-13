#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using StrataTheme.Controls;

namespace Lumi;

/// <summary>
/// DEBUG-only headed proof that lifecycle-managed perpetual animations release detached visuals.
/// Exercises determinate and indeterminate progress bars plus the exact tool/terminal
/// in-progress-to-completed detach race against a live compositor target.
/// </summary>
internal static class AnimationLifecycleLeakRepro
{
    private const int Iterations = 30;

    public static bool IsFlag(string arg) =>
        arg.Equals("--animation-lifecycle-leak-repro", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--animation-leak-repro", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--progressbar-leak-repro", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--pb-leak-repro", StringComparison.OrdinalIgnoreCase);

    public static void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _ = Task.Run(async () =>
        {
            var result = ReproResult.Failed;
            Window? proofWindow = null;
            var exitCode = 1;
            try
            {
                await Task.Delay(1200);
                (result, proofWindow) = await Dispatcher.UIThread.InvokeAsync(RunCoreAsync);
                exitCode = result.AllCollected ? 0 : 3;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[animation-leak-repro] FAILED: " + ex);
            }

            Console.WriteLine(
                $"ANIMATION_LIFECYCLE_REPRO pid={Environment.ProcessId} " +
                $"determinate_alive={result.DeterminateAlive}/{Iterations} " +
                $"indeterminate_alive={result.IndeterminateAlive}/{Iterations} " +
                $"tool_alive={result.ToolAlive}/{Iterations} " +
                $"terminal_alive={result.TerminalAlive}/{Iterations} " +
                $"result={(result.AllCollected ? "PASS" : "FAIL")}");

            if (GetHoldMilliseconds() is var holdMilliseconds and > 0)
            {
                Console.WriteLine($"ANIMATION_LIFECYCLE_REPRO_HOLD milliseconds={holdMilliseconds}");
                await Task.Delay(holdMilliseconds);
            }

            Dispatcher.UIThread.Post(() =>
            {
                proofWindow?.Close();
                Environment.ExitCode = exitCode;
                desktop.Shutdown(exitCode);
            });
        });
    }

    private static async Task<(ReproResult result, Window proofWindow)> RunCoreAsync()
    {
        var window = new Window
        {
            Width = 260,
            Height = 180,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(60, 60),
            Title = "animation-lifecycle-leak-repro",
        };
        var host = new StackPanel();
        window.Content = host;
        window.Show();
        await Settle(300);

        var determinate = await MeasureAsync(host, indeterminate: false);
        var indeterminate = await MeasureAsync(host, indeterminate: true);
        var tools = await MeasureToolCardsAsync(host, terminal: false);
        var terminals = await MeasureToolCardsAsync(host, terminal: true);

        return (new ReproResult(determinate, indeterminate, tools, terminals), window);
    }

    /// <summary>
    /// Realizes then detaches <see cref="Iterations"/> bars one at a time, keeping a
    /// <see cref="WeakReference"/> to each, then forces GC and returns how many survived.
    /// </summary>
    private static async Task<int> MeasureAsync(Panel host, bool indeterminate)
    {
        var refs = new List<WeakReference>(Iterations);

        for (var i = 0; i < Iterations; i++)
        {
            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 42,
                IsIndeterminate = indeterminate,
                Width = 200,
                Height = 6,
            };

            host.Children.Add(bar);
            // Realize the template (OnApplyTemplate) and let the compositor tick a few frames so any
            // infinite animation actually registers on the server render target.
            await Settle(120);

            refs.Add(new WeakReference(bar));
            host.Children.Remove(bar);
            bar = null;
            await Settle(50);
        }

        await FlushAnimationFramesAsync(host);

        // Drain to collection: flush the dispatcher + force compacting GCs with real time between them
        // so any pending detach/dispose work on the render side completes.
        for (var g = 0; g < 8; g++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            await Settle(60);
        }

        return refs.Count(r => r.IsAlive);
    }

    private static async Task<int> MeasureToolCardsAsync(Panel host, bool terminal)
    {
        var refs = new List<WeakReference>(Iterations);

        for (var i = 0; i < Iterations; i++)
        {
            TemplatedControl card = terminal
                ? new StrataTerminalPreview
                {
                    ToolName = "PowerShell",
                    Command = "Write-Output animation-proof",
                    Status = StrataAiToolCallStatus.InProgress,
                }
                : new StrataAiToolCall
                {
                    ToolName = "animation-proof",
                    InputParameters = "{\"cycle\":true}",
                    Status = StrataAiToolCallStatus.InProgress,
                };

            host.Children.Add(card);
            await Settle(120);

            // A pattern local here would be hoisted by the async state machine and root the final
            // tool card during this method's own GC measurement.
            if (terminal)
                ((StrataTerminalPreview)card).Status = StrataAiToolCallStatus.Completed;
            else
                ((StrataAiToolCall)card).Status = StrataAiToolCallStatus.Completed;

            refs.Add(new WeakReference(card));
            host.Children.Remove(card);
            card = null!;
            await Settle(50);
        }

        await FlushAnimationFramesAsync(host);

        for (var g = 0; g < 8; g++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            await Settle(60);
        }

        return refs.Count(reference => reference.IsAlive);
    }

    private static async Task FlushAnimationFramesAsync(Visual visual)
    {
        var topLevel = TopLevel.GetTopLevel(visual)
            ?? throw new InvalidOperationException("The proof host is not attached to a top-level.");

        for (var frame = 0; frame < 3; frame++)
        {
            var tick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            topLevel.RequestAnimationFrame(_ => tick.TrySetResult());
            await tick.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static int GetHoldMilliseconds()
    {
        var value = Environment.GetEnvironmentVariable("LUMI_ANIMATION_REPRO_HOLD_MS");
        return int.TryParse(value, out var milliseconds)
            ? Math.Clamp(milliseconds, 0, 10 * 60 * 1000)
            : 0;
    }

    private static Task Settle(int milliseconds) => Task.Delay(milliseconds);

    private readonly record struct ReproResult(
        int DeterminateAlive,
        int IndeterminateAlive,
        int ToolAlive,
        int TerminalAlive)
    {
        public static ReproResult Failed => new(-1, -1, -1, -1);

        public bool AllCollected =>
            DeterminateAlive == 0 &&
            IndeterminateAlive == 0 &&
            ToolAlive == 0 &&
            TerminalAlive == 0;
    }
}
#endif
