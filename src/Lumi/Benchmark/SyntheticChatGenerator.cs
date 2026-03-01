using System;
using System.Collections.Generic;
using Lumi.Models;

namespace Lumi.Benchmark;

/// <summary>
/// Generates synthetic chat content with diverse message types for benchmarking.
/// Creates a realistic mix of markdown, code, tables, tool calls, and plain text.
/// </summary>
internal static class SyntheticChatGenerator
{
    public static Chat Generate(int messageCount)
    {
        var chat = new Chat
        {
            Title = "[Benchmark] Synthetic Chat",
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };

        var rng = new Random(42); // deterministic for reproducibility

        for (int i = 0; i < messageCount; i++)
        {
            var kind = rng.Next(0, 12);
            var msg = kind switch
            {
                0 => MakeUserMessage(i),
                1 => MakeShortAssistantMessage(i),
                2 => MakeLongAssistantMessage(i),
                3 => MakeMarkdownWithHeaders(i),
                4 => MakeCodeBlock(i),
                5 => MakeTable(i),
                6 => MakeBulletList(i),
                7 => MakeToolCall(i),
                8 => MakeToolResult(i),
                9 => MakeReasoningMessage(i),
                10 => MakeMixedContent(i),
                _ => MakeUserMessage(i),
            };

            chat.Messages.Add(msg);
        }

        return chat;
    }

    private static ChatMessage MakeUserMessage(int i) => new()
    {
        Role = "user",
        Content = $"Message #{i}: Can you help me understand how the scroll performance works? I'm particularly interested in frame timing and jank detection. What metrics should I look at?",
    };

    private static ChatMessage MakeShortAssistantMessage(int i) => new()
    {
        Role = "assistant",
        Content = $"Sure! Here's a quick summary of key metrics for scroll performance (msg #{i}):\n\n- **FPS** (Frames Per Second)\n- **Frame time** variance\n- **P95/P99** latency\n- **Jank** percentage",
    };

    private static ChatMessage MakeLongAssistantMessage(int i) => new()
    {
        Role = "assistant",
        Content = $"""
            # Performance Analysis Report #{i}

            Scroll performance is critical for chat applications. When users scroll through long conversations, any frame drops or stutters are immediately perceptible and degrade the experience.

            ## Key Metrics

            The most important metrics to track are:

            1. **Average FPS** — Should stay above 55fps for smooth scrolling
            2. **P99 Frame Time** — The worst 1% of frames. If this exceeds 33ms, users will notice hitches
            3. **Jank Percentage** — Frames taking >2x the average. Keep this below 5%
            4. **Standard Deviation** — Lower is better. High variance means inconsistent frame pacing even if average is good

            ## Why Scrolling is Hard

            Chat UIs are particularly challenging because:
            - Messages vary wildly in height (short text vs. code blocks vs. tables)
            - Markdown rendering is expensive (inline parsing, syntax highlighting)
            - The visual tree can grow very large with hundreds of messages
            - Virtualization is tricky when items have unpredictable heights
            - Rich content (charts, images) adds layout complexity

            ## Recommendations

            For optimal scroll performance:
            - Use incremental loading (only render visible + buffer messages)
            - Cache rendered markdown blocks
            - Debounce scroll events to reduce layout passes
            - Consider composition-layer scrolling where possible
            - Profile with actual content, not synthetic data alone

            The bottom line: measure, don't guess. Frame-level profiling during real scrolling scenarios is the only reliable way to identify and fix performance issues.
            """,
    };

    private static ChatMessage MakeMarkdownWithHeaders(int i) => new()
    {
        Role = "assistant",
        Content = $"""
            # Section {i}: Architecture Overview

            ## Component Structure

            The application follows an **MVVM** pattern with these layers:

            ### View Layer
            Controls are built programmatically for maximum flexibility.

            ### ViewModel Layer
            Uses `CommunityToolkit.Mvvm` source generators for clean property declarations.

            ### Model Layer
            Simple POCOs with JSON serialization support.

            ---

            > **Note:** This is a synthetic message for benchmark testing. The content is designed to exercise various markdown rendering paths including headers, bold text, inline code, blockquotes, and horizontal rules.
            """,
    };

    private static ChatMessage MakeCodeBlock(int i) => new()
    {
        Role = "assistant",
        Content = $$"""
            Here's the implementation for message #{{i}}:

            ```csharp
            public class ScrollBenchmark
            {
                private readonly List<double> _frameTimes = new();
                private readonly Stopwatch _stopwatch = new();
                
                public void Start()
                {
                    _stopwatch.Restart();
                    _frameTimes.Clear();
                }
                
                public void RecordFrame()
                {
                    var elapsed = _stopwatch.Elapsed.TotalMilliseconds;
                    _frameTimes.Add(elapsed);
                }
                
                public double GetAverageFps()
                {
                    if (_frameTimes.Count < 2) return 0;
                    var totalTime = _frameTimes[^1] - _frameTimes[0];
                    return (_frameTimes.Count - 1) / (totalTime / 1000.0);
                }
                
                public double GetP99FrameTime()
                {
                    var deltas = new List<double>();
                    for (int i = 1; i < _frameTimes.Count; i++)
                        deltas.Add(_frameTimes[i] - _frameTimes[i - 1]);
                    deltas.Sort();
                    var idx = (int)(0.99 * deltas.Count);
                    return deltas[Math.Min(idx, deltas.Count - 1)];
                }
            }
            ```

            And the corresponding test:

            ```csharp
            [Test]
            public void ScrollBenchmark_RecordsFrames()
            {
                var bench = new ScrollBenchmark();
                bench.Start();
                for (int i = 0; i < 100; i++)
                {
                    bench.RecordFrame();
                    Thread.Sleep(16); // ~60fps
                }
                Assert.That(bench.GetAverageFps(), Is.GreaterThan(50));
            }
            ```
            """,
    };

    private static ChatMessage MakeTable(int i) => new()
    {
        Role = "assistant",
        Content = $"""
            ## Benchmark Results #{i}

            | Scenario | Avg FPS | P95 (ms) | P99 (ms) | Jank % |
            |----------|---------|----------|----------|--------|
            | Fast Scroll | 58.2 | 19.3 | 28.1 | 3.2% |
            | Slow Scroll | 60.1 | 17.1 | 22.4 | 1.1% |
            | Jump | 55.8 | 21.7 | 35.2 | 5.8% |
            | Touchpad | 59.4 | 18.2 | 24.8 | 2.3% |
            | Touchscreen | 57.1 | 20.1 | 31.5 | 4.1% |
            | Flick | 54.3 | 23.8 | 42.1 | 7.2% |
            | Mixed | 57.5 | 19.8 | 29.3 | 3.8% |

            ### Performance by Content Type

            | Content | Render Time | Layout Time | Total |
            |---------|-------------|-------------|-------|
            | Plain text | 0.3ms | 0.2ms | 0.5ms |
            | Markdown | 1.2ms | 0.8ms | 2.0ms |
            | Code block | 2.1ms | 0.5ms | 2.6ms |
            | Table | 3.4ms | 1.2ms | 4.6ms |
            | Mixed | 1.8ms | 0.7ms | 2.5ms |
            """,
    };

    private static ChatMessage MakeBulletList(int i) => new()
    {
        Role = "assistant",
        Content = $"""
            ## Optimization Checklist #{i}

            Here are the key areas to focus on:

            - **Layout Performance**
              - Minimize visual tree depth
              - Use fixed-size containers where possible
              - Avoid unnecessary measure/arrange passes
              - Cache expensive layout calculations

            - **Rendering Pipeline**
              - Reduce overdraw (overlapping opaque elements)
              - Use composition animations instead of per-frame updates
              - Batch property changes to avoid multiple render passes
              - Consider hardware acceleration settings

            - **Memory Management**
              - Pool UI elements for recycling
              - Dispose off-screen controls
              - Monitor GC pressure during scrolling
              - Use weak references for cached content

            - **Input Handling**
              - Debounce scroll events appropriately
              - Use `DispatcherPriority.Input` for scroll handlers
              - Avoid synchronous work in pointer event handlers
              - Consider predictive scrolling for touch input

            1. First numbered item
            2. Second numbered item with **bold** and `code`
            3. Third item with a [link](https://example.com)
            4. Fourth item
            5. Fifth item wrapping to multiple lines because it contains a longer description that exercises text wrapping in the bullet list rendering path
            """,
    };

    private static ChatMessage MakeToolCall(int i) => new()
    {
        Role = "tool",
        ToolName = i % 3 == 0 ? "web_search" : i % 3 == 1 ? "read_file" : "run_terminal",
        ToolCallId = $"call_{i:D4}",
        ToolStatus = "Completed",
        Content = "",
        ToolOutput = i % 3 == 0
            ? "Found 5 results for 'avalonia scroll performance'"
            : i % 3 == 1
                ? "File contents: 42 lines of C# code..."
                : "Command completed with exit code 0",
    };

    private static ChatMessage MakeToolResult(int i) => new()
    {
        Role = "assistant",
        Content = $"Based on the tool results (step #{i}), I can see that the implementation is working correctly. The scroll performance metrics show acceptable frame times across all scenarios.",
    };

    private static ChatMessage MakeReasoningMessage(int i) => new()
    {
        Role = "reasoning",
        Content = $"Let me think through this step by step for item #{i}. The user is asking about scroll performance, so I need to consider the rendering pipeline, layout costs, and input handling. The key insight is that frame time consistency matters more than raw FPS — a steady 55fps feels smoother than an average of 60fps with frequent drops to 30fps.",
    };

    private static ChatMessage MakeMixedContent(int i) => new()
    {
        Role = "assistant",
        Content = $"""
            ## Mixed Content Block #{i}

            Here's a combination of different content types:

            First, some **bold** and *italic* text with `inline code` references.

            ```python
            def measure_scroll_fps(duration_seconds=10):
                frames = []
                start = time.time()
                while time.time() - start < duration_seconds:
                    frame_start = time.perf_counter()
                    render_frame()
                    frame_end = time.perf_counter()
                    frames.append(frame_end - frame_start)
                return calculate_stats(frames)
            ```

            Key findings from the analysis:

            | Metric | Value | Status |
            |--------|-------|--------|
            | Avg FPS | 58.3 | ✅ Good |
            | P99 | 28ms | ⚠️ Marginal |
            | Jank | 4.2% | ✅ Acceptable |

            - Point one: Layout is the bottleneck
            - Point two: Markdown parsing is well-cached
            - Point three: Code blocks need optimization

            > **Conclusion:** The scroll performance is generally acceptable but could benefit from targeted optimizations in the code block rendering path.
            """,
    };
}
