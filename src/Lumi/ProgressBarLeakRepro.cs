#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Lumi;

/// <summary>
/// DEBUG-only reproduction harness that isolates the ProgressBar infinite-animation
/// composition retention leak — and proves whether the <c>:indeterminate</c> gate fixes it.
///
/// <para>
/// Mechanism under test (proven from the real Release dump via <c>gcroot</c>): a ProgressBar's
/// <c>PART_Indicator</c> runs an <c>IterationCount="Infinite"</c> opacity pulse. The resulting
/// <c>DisposeAnimationInstanceSubject&lt;Double&gt;</c> is held by the applied
/// <c>StyleInstance</c> and directly references the animated Border, whose composition subtree is
/// registered in the window's app-lifetime <c>ServerCompositionTarget._attachedVisuals</c>. When the
/// bar detaches, the never-completing animation is never torn down, so the whole composition subtree
/// (and, in the app, the enclosing StrataChatMessage) stays pinned forever.
/// </para>
///
/// <para>
/// The harness realizes bars inside a headed window (real Compositor + render thread), keeps that
/// window OPEN (so its composition target behaves like the app-lifetime MainWindow — closing it would
/// tear every visual down and mask the leak), detaches each bar, forces GC, and counts survivors:
/// <list type="bullet">
///   <item><description><b>determinate</b> bars — pre-fix: pinned; with the gate: collected.</description></item>
///   <item><description><b>indeterminate</b> bars — a control group that animates in BOTH builds, so it
///   stays pinned either way. This isolates the animation as the cause: if determinate retention
///   collapses while indeterminate stays high, the infinite animation is provably the retainer.</description></item>
/// </list>
/// </para>
/// </summary>
internal static class ProgressBarLeakRepro
{
    private const int Iterations = 30;

    public static bool IsFlag(string arg) =>
        arg.Equals("--progressbar-leak-repro", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--pb-leak-repro", StringComparison.OrdinalIgnoreCase);

    public static void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _ = Task.Run(async () =>
        {
            int det = -1, indet = -1;
            try
            {
                // Let the first real frame + lazy view realization settle before measuring.
                await Task.Delay(1200);
                (det, indet) = await Dispatcher.UIThread.InvokeAsync(RunCoreAsync);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[pb-leak-repro] FAILED: " + ex);
            }

            // Single machine-readable line the outer script greps for.
            Console.WriteLine(
                $"PROGRESSBAR_LEAK_REPRO determinate_alive={det}/{Iterations} indeterminate_alive={indet}/{Iterations}");

            Dispatcher.UIThread.Post(() =>
            {
                try { Environment.ExitCode = 0; desktop.Shutdown(0); }
                catch { /* already shutting down */ }
            });
        });
    }

    private static async Task<(int determinateAlive, int indeterminateAlive)> RunCoreAsync()
    {
        // A real headed window with its own live Compositor/ServerCompositionTarget. Kept OFF-SCREEN
        // and open for the whole run so detached bars are measured against a persistent render target.
        var window = new Window
        {
            Width = 260,
            Height = 180,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(60, 60),
            Title = "pb-leak-repro",
        };
        var host = new StackPanel();
        window.Content = host;
        window.Show();
        await Settle(300);

        var determinate = await MeasureAsync(host, indeterminate: false);
        var indeterminate = await MeasureAsync(host, indeterminate: true);

        window.Close();
        await Settle(80);
        return (determinate, indeterminate);
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

    private static Task Settle(int milliseconds) => Task.Delay(milliseconds);
}
#endif
