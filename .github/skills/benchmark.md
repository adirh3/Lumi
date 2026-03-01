---
description: "Running, modifying, or analyzing the Lumi scroll performance benchmark. USE FOR: running benchmarks, interpreting results, adding scenarios, modifying metrics, debugging frame drops."
---

# Lumi Scroll Benchmark

Built-in scroll performance benchmark that measures rendering performance under various scrolling patterns. Runs inside the real app with real UI. Results are displayed in an Avalonia window after completion.

## Usage

```bash
# Run all scenarios with synthetic chat content
Lumi.exe --benchmark

# Specific scenario
Lumi.exe --benchmark --scenario fast-scroll

# Against an existing chat
Lumi.exe --benchmark --chat "My Chat Title"

# Full options
Lumi.exe --benchmark --scenario touchpad --duration 10 --messages 200 --iterations 3 --output results.json --verbose
```

## What it measures

### Render FPS (actual displayed frames)
Measured via `TopLevel.RequestAnimationFrame` — fires exactly once per compositor frame, which is vsync-bound to the display refresh rate. Each callback records the compositor-reported timestamp, and render FPS is computed from the frame-to-frame deltas. Avg FPS = `1000 / avgDelta`. Min/Max FPS use **rolling 500ms windows** (not single-frame deltas) to eliminate timestamp jitter — so they reflect sustained performance, not noise.

### UPS (Updates Per Second)
`DispatcherTimer` at Render priority with 1ms interval measures how often the UI thread processes render-priority work. Min/Max UPS also use **rolling 500ms windows** for consistency.

### Dropped frames
Render frames where the compositor delta exceeds 1.5× the **median** frame time. Uses median (not mean) so the threshold adapts to the actual display refresh rate without being skewed by outliers.

### Jank
Render frames where the compositor delta exceeds 2× the **median** frame time or 33.3ms (whichever is larger). Measured on render deltas, not update deltas, so it reflects what the user actually sees.

### Scroll velocity (px/s)
Time-normalized: each scroll displacement is divided by its actual time delta. During a 300ms stall, the scroll delta is divided by 0.3s — giving the true velocity rather than an inflated per-tick value. Max uses P99 to resist single-sample spikes.

### Edge trimming
The first and last 250ms of all sample streams are discarded before computing statistics, eliminating startup/teardown artifacts.

### Other metrics
- **Render frame time percentiles** — P50/P90/P99 of actual compositor frame intervals.
- **Update time percentiles** — P50/P90/P95/P99 of dispatcher update intervals.

## Scenarios

All scenarios set `ScrollViewer.Offset` directly (bypassing the input pipeline). They test **rendering performance** under different scroll patterns, not input handling overhead.

| Name | Pattern | Details |
|------|---------|---------|
| `fast-scroll` | Rapid continuous | 300px jumps, 60/sec. Worst case for layout thrashing. |
| `slow-scroll` | Gentle continuous | 40px jumps, 12/sec. Baseline rendering load. |
| `jump` | Random teleport | Random position every 200ms. Cold layout of new viewports. |
| `touchpad` | High-freq tiny | 5–20px sinusoidal, 125/sec. Many small relayouts. |
| `touchscreen` | Flick + decelerate | 800→2 px/s exponential decay, 60/sec. Varying load per frame. |
| `flick` | Edge-to-edge sweep | 30 steps to opposite edge, then 500ms pause. Max virtualization churn. |
| `mixed` | All combined | Cycles through slow→fast→touchpad→jump→flick sequentially. |
| `all` | Every scenario | Runs each scenario independently for separate metrics (default). |

## CLI Options

| Flag | Description | Default |
|------|-------------|---------|
| `--scenario, -s` | Scenario name | `all` |
| `--chat, -c` | Existing chat title to use | synthetic |
| `--duration, -d` | Seconds per scenario | `10` |
| `--messages, -m` | Synthetic message count | `100` |
| `--output, -o` | JSON output file path | none |
| `--iterations, -i` | Repeat each scenario N times | `1` |
| `--verbose, -v` | Include raw update deltas | off |
| `--no-warmup` | Skip 3s warm-up phase | warm up on |
| `--help, -h` | Show help | — |

## Interpreting Results

### What's good vs bad

| Metric | Good | Acceptable | Bad |
|--------|------|------------|-----|
| Avg Render FPS | ≥ 95% of display Hz | ≥ 85% of display Hz | < 85% |
| Min Render FPS (windowed) | ≥ 80% of display Hz | ≥ 60% | < 60% |
| P99 Render Frame Time | < 2× vsync interval | < 3× | > 3× |
| Dropped Frames | < 2% of total frames | < 5% | > 5% |
| Jank Frames | 0 | < 0.5% | > 0.5% |
| Update Std Dev | < 1ms | < 3ms | > 5ms |

Since the benchmark adapts to any display, interpret relative to the display's refresh rate. For example, on a 60Hz display: avg FPS of 57+ is good. On 175Hz: avg FPS of 166+ is good.

### Diagnosing problems

| Symptom | Likely Cause | Where to Look |
|---------|-------------|---------------|
| Low avg FPS across all scenarios | General rendering overhead | ChatView.axaml.cs message building, StrataMarkdown rendering |
| Low avg FPS only in fast-scroll/flick | Layout thrashing during rapid scrolling | ScrollViewer virtualization, large visual tree |
| High P99 but good avg | Occasional GC pauses or layout spikes | Check for object allocations in hot paths, large markdown blocks |
| Many dropped frames, few jank | Frequent small misses (just over 1.5× threshold) | Minor layout cost per frame, slightly too expensive controls |
| Jank frames present | Major stalls (>33ms frames) | Heavy synchronous work on UI thread (file I/O, parsing) |
| Low min FPS in one scenario | Sustained perf dip during a specific scroll pattern | Look for pattern-specific issues (e.g., content virtualization on direction reversal) |
| High std dev | Inconsistent frame pacing | Mixed content sizes, GC pressure, background thread contention |

### Comparing before/after

Run with `--output results.json --iterations 3` before and after changes. Compare:
1. **Avg Render FPS** — did it improve?
2. **P99 frame time** — did tail latency improve?
3. **Dropped frame count** — fewer drops?
4. **Jank count** — fewer visible stutters?
5. **Min FPS (windowed)** — did worst sustained period improve?

Use the same `--messages` count and `--duration` for fair comparison. The synthetic chat is deterministic (seeded RNG), so content is identical across runs.

## Architecture

Files in `src/Lumi/Benchmark/`:
- **BenchmarkArgs.cs** — CLI parsing
- **BenchmarkOutput.cs** — output abstraction (StringBuilder-backed, ready for logging framework)
- **FrameMonitor.cs** — UPS timer + `RequestAnimationFrame` render counter + `FrameStatistics` + `BenchmarkReport`
- **ScrollScenarios.cs** — scrolling pattern implementations
- **SyntheticChatGenerator.cs** — deterministic diverse chat content
- **BenchmarkRunner.cs** — orchestrator, results displayed in an Avalonia window

## How render FPS is measured

```
Start() → _stopwatch.Restart()
        → TopLevel.RequestAnimationFrame(OnAnimationFrame)
        → DispatcherTimer.Start()

OnAnimationFrame(TimeSpan elapsed):
  - Computes delta from compositor timestamp vs previous callback
  - Stores delta in _renderFrameDeltas list
  - Increments _renderFrameCount
  - Re-registers: RequestAnimationFrame(OnAnimationFrame)

GetStatistics():
  - Trims first/last 250ms of all sample streams
  - Sorts render frame deltas
  - AvgRenderFps = 1000 / avgDelta
  - MinRenderFps = min of rolling 500ms window FPS values
  - MaxRenderFps = max of rolling 500ms window FPS values
  - DroppedFrames = count of deltas > 1.5 × median delta
  - JankFrames = count of deltas > max(2 × median delta, 33.3ms)
  - ScrollVelocity = scrollDelta / timeDelta (px/s), max uses P99
```

### Why these techniques matter

- **Rolling windows** — A single timestamp can jitter by ~0.5ms due to OS scheduling. One jittery sample would make single-frame min/max useless (e.g., showing FPS above the display's actual refresh rate). A 500ms window contains enough frames at any refresh rate to smooth noise while still capturing real sustained dips.
- **Median-based thresholds** — The median is robust against outliers. Using it for jank/dropped thresholds means the threshold auto-adapts to any display refresh rate without hardcoding.
- **Time-normalized scroll velocity** — `px/update` is misleading when update frequency varies. During a stall, one update covers many scroll events, inflating the per-update value. `px/s` gives the true physical velocity.
- **Edge trimming** — The first frame after `Start()` and the last frame before `Stop()` have irregular deltas from setup/teardown overhead. Trimming 250ms from each end removes these without losing meaningful data.
