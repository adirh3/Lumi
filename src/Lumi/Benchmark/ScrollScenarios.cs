using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Lumi.Benchmark;

/// <summary>
/// Defines a scrolling pattern that can be executed against a ScrollViewer.
/// </summary>
internal abstract class ScrollScenario
{
    public abstract string Name { get; }
    public abstract string Description { get; }

    /// <summary>
    /// Execute the scrolling pattern for the given duration.
    /// All scroll operations are dispatched to the UI thread.
    /// </summary>
    public abstract Task RunAsync(ScrollViewer scrollViewer, TimeSpan duration, CancellationToken ct);

    /// <summary>Simulate a mouse wheel event on the scroll viewer.</summary>
    protected static void SimulateWheel(ScrollViewer sv, double deltaY)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var currentOffset = sv.Offset;
            sv.Offset = currentOffset.WithY(currentOffset.Y - deltaY);
        });
    }

    /// <summary>Set scroll offset directly (for jump scenarios).</summary>
    protected static void SetOffset(ScrollViewer sv, double y)
    {
        Dispatcher.UIThread.Post(() =>
        {
            sv.Offset = sv.Offset.WithY(Math.Clamp(y, 0, sv.ScrollBarMaximum.Y));
        });
    }

    /// <summary>Get the current scroll extent on the UI thread.</summary>
    protected static Task<(double offset, double max)> GetScrollState(ScrollViewer sv)
    {
        var tcs = new TaskCompletionSource<(double, double)>();
        Dispatcher.UIThread.Post(() =>
            tcs.TrySetResult((sv.Offset.Y, sv.ScrollBarMaximum.Y)));
        return tcs.Task;
    }
}

/// <summary>Fast mouse wheel scrolling — large deltas at high frequency.</summary>
internal sealed class FastScrollScenario : ScrollScenario
{
    public override string Name => "fast-scroll";
    public override string Description => "Fast mouse wheel (large deltas, high frequency)";

    public override async Task RunAsync(ScrollViewer sv, TimeSpan duration, CancellationToken ct)
    {
        var end = DateTime.UtcNow + duration;
        var direction = 1.0; // positive = scroll down

        while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
        {
            var (offset, max) = await GetScrollState(sv);

            // Reverse direction at boundaries
            if (offset >= max - 10) direction = -1.0;
            else if (offset <= 10) direction = 1.0;

            SimulateWheel(sv, -direction * 300); // large delta
            await Task.Delay(16, ct); // ~60 events/sec
        }
    }
}

/// <summary>Slow, gentle mouse wheel scrolling — small deltas.</summary>
internal sealed class SlowScrollScenario : ScrollScenario
{
    public override string Name => "slow-scroll";
    public override string Description => "Slow mouse wheel (small deltas, relaxed pace)";

    public override async Task RunAsync(ScrollViewer sv, TimeSpan duration, CancellationToken ct)
    {
        var end = DateTime.UtcNow + duration;
        var direction = 1.0;

        while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
        {
            var (offset, max) = await GetScrollState(sv);
            if (offset >= max - 10) direction = -1.0;
            else if (offset <= 10) direction = 1.0;

            SimulateWheel(sv, -direction * 40); // small delta
            await Task.Delay(80, ct); // ~12 events/sec
        }
    }
}

/// <summary>Jump scrolling — random position jumps (scrollbar drag simulation).</summary>
internal sealed class JumpScrollScenario : ScrollScenario
{
    public override string Name => "jump";
    public override string Description => "Random position jumps (scrollbar drag)";

    public override async Task RunAsync(ScrollViewer sv, TimeSpan duration, CancellationToken ct)
    {
        var rng = new Random(42); // deterministic for reproducibility
        var end = DateTime.UtcNow + duration;

        while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
        {
            var (_, max) = await GetScrollState(sv);
            var target = rng.NextDouble() * max;
            SetOffset(sv, target);
            await Task.Delay(200, ct); // 5 jumps/sec
        }
    }
}

/// <summary>Touchpad scrolling — high-frequency small pixel deltas like a precision touchpad.</summary>
internal sealed class TouchpadScrollScenario : ScrollScenario
{
    public override string Name => "touchpad";
    public override string Description => "Touchpad (high-freq, tiny pixel deltas)";

    public override async Task RunAsync(ScrollViewer sv, TimeSpan duration, CancellationToken ct)
    {
        var end = DateTime.UtcNow + duration;
        var direction = 1.0;
        var phase = 0.0;

        while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
        {
            var (offset, max) = await GetScrollState(sv);
            if (offset >= max - 10) direction = -1.0;
            else if (offset <= 10) direction = 1.0;

            // Vary speed sinusoidally to simulate finger acceleration/deceleration
            phase += 0.05;
            var speed = 5 + 15 * Math.Abs(Math.Sin(phase));
            SimulateWheel(sv, -direction * speed);
            await Task.Delay(8, ct); // ~125 events/sec (touchpad rate)
        }
    }
}

/// <summary>Touchscreen-like scrolling — flick with deceleration phases.</summary>
internal sealed class TouchscreenScrollScenario : ScrollScenario
{
    public override string Name => "touchscreen";
    public override string Description => "Touch flick with inertia deceleration";

    public override async Task RunAsync(ScrollViewer sv, TimeSpan duration, CancellationToken ct)
    {
        var end = DateTime.UtcNow + duration;
        var direction = 1.0;

        while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
        {
            var (offset, max) = await GetScrollState(sv);
            if (offset >= max - 100) direction = -1.0;
            else if (offset <= 100) direction = 1.0;

            // Simulate a flick: fast start, exponential deceleration
            var velocity = 800.0 * direction;
            var friction = 0.95;

            while (Math.Abs(velocity) > 2 && DateTime.UtcNow < end && !ct.IsCancellationRequested)
            {
                SimulateWheel(sv, -velocity * 0.016); // velocity * dt
                velocity *= friction;
                await Task.Delay(16, ct);
            }

            // Pause between flicks (finger lift + next touch)
            await Task.Delay(300, ct);
        }
    }
}

/// <summary>Quick flick to top/bottom — tests scroll-to-edge performance.</summary>
internal sealed class FlickScrollScenario : ScrollScenario
{
    public override string Name => "flick";
    public override string Description => "Flick to top/bottom edges alternating";

    public override async Task RunAsync(ScrollViewer sv, TimeSpan duration, CancellationToken ct)
    {
        var end = DateTime.UtcNow + duration;
        var goToBottom = true;

        while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
        {
            var (_, max) = await GetScrollState(sv);

            // Rapidly scroll in one direction
            var target = goToBottom ? max : 0;
            var steps = 30;
            var (startOffset, _) = await GetScrollState(sv);
            var step = (target - startOffset) / steps;

            for (int i = 0; i < steps && DateTime.UtcNow < end && !ct.IsCancellationRequested; i++)
            {
                SimulateWheel(sv, -step);
                await Task.Delay(16, ct);
            }

            goToBottom = !goToBottom;
            await Task.Delay(500, ct); // pause at edge
        }
    }
}

/// <summary>
/// Mixed scrolling — cycles through different patterns within a single run
/// to simulate realistic user behavior.
/// </summary>
internal sealed class MixedScrollScenario : ScrollScenario
{
    public override string Name => "mixed";
    public override string Description => "Mixed patterns (realistic user simulation)";

    public override async Task RunAsync(ScrollViewer sv, TimeSpan duration, CancellationToken ct)
    {
        var scenarios = new ScrollScenario[]
        {
            new SlowScrollScenario(),
            new FastScrollScenario(),
            new TouchpadScrollScenario(),
            new TouchscreenScrollScenario(),
            new JumpScrollScenario(),
            new FlickScrollScenario(),
        };

        var segmentDuration = TimeSpan.FromTicks(duration.Ticks / scenarios.Length);

        foreach (var scenario in scenarios)
        {
            if (ct.IsCancellationRequested) break;
            await scenario.RunAsync(sv, segmentDuration, ct);
        }
    }
}

/// <summary>Factory to resolve scenarios by name.</summary>
internal static class ScrollScenarioFactory
{
    public static ScrollScenario[] GetScenarios(string name) => name switch
    {
        "all" =>
        [
            new FastScrollScenario(),
            new SlowScrollScenario(),
            new JumpScrollScenario(),
            new TouchpadScrollScenario(),
            new TouchscreenScrollScenario(),
            new FlickScrollScenario(),
            new MixedScrollScenario(),
        ],
        "fast-scroll" => [new FastScrollScenario()],
        "slow-scroll" => [new SlowScrollScenario()],
        "jump" => [new JumpScrollScenario()],
        "touchpad" => [new TouchpadScrollScenario()],
        "touchscreen" => [new TouchscreenScrollScenario()],
        "flick" => [new FlickScrollScenario()],
        "mixed" => [new MixedScrollScenario()],
        _ => [new MixedScrollScenario()],
    };
}
