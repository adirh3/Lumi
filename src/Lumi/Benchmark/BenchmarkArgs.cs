using System;
using System.Collections.Generic;
using System.Linq;

namespace Lumi.Benchmark;

/// <summary>
/// Parses and holds CLI arguments for the scroll benchmark.
/// Usage: Lumi --benchmark [options]
/// </summary>
internal sealed class BenchmarkArgs
{
    public bool IsBenchmark { get; set; }
    public bool ShowHelp { get; set; }
    public string Scenario { get; set; } = "all";
    public string? ChatTitle { get; set; }
    public int DurationSeconds { get; set; } = 10;
    public int SyntheticMessageCount { get; set; } = 100;
    public string? OutputPath { get; set; }
    public bool Verbose { get; set; }
    public bool WarmUp { get; set; } = true;
    public int Iterations { get; set; } = 1;

    public static readonly IReadOnlyList<string> ValidScenarios =
    [
        "all",
        "fast-scroll",
        "slow-scroll",
        "jump",
        "touchpad",
        "touchscreen",
        "flick",
        "mixed"
    ];

    public static BenchmarkArgs Parse(string[] args)
    {
        var result = new BenchmarkArgs();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg is "--benchmark" or "-benchmark")
            {
                result.IsBenchmark = true;
                continue;
            }

            if (!result.IsBenchmark) continue;

            string? NextArg() => i + 1 < args.Length ? args[++i] : null;

            switch (arg)
            {
                case "--scenario" or "-s":
                    var s = NextArg();
                    if (s is not null && ValidScenarios.Contains(s, StringComparer.OrdinalIgnoreCase))
                        result.Scenario = s.ToLowerInvariant();
                    break;

                case "--chat" or "-c":
                    result.ChatTitle = NextArg();
                    break;

                case "--duration" or "-d":
                    if (int.TryParse(NextArg(), out var dur) && dur > 0)
                        result.DurationSeconds = dur;
                    break;

                case "--messages" or "-m":
                    if (int.TryParse(NextArg(), out var msg) && msg > 0)
                        result.SyntheticMessageCount = msg;
                    break;

                case "--output" or "-o":
                    result.OutputPath = NextArg();
                    break;

                case "--verbose" or "-v":
                    result.Verbose = true;
                    break;

                case "--no-warmup":
                    result.WarmUp = false;
                    break;

                case "--iterations" or "-i":
                    if (int.TryParse(NextArg(), out var iter) && iter > 0)
                        result.Iterations = iter;
                    break;

                case "--help" or "-h":
                    result.ShowHelp = true;
                    break;
            }
        }

        return result;
    }

    public const string HelpText = """
        Lumi Scroll Benchmark
        =====================
        Usage: Lumi --benchmark [options]

        Options:
          --scenario, -s <name>    Scenario to run (default: all)
                                   Values: all, fast-scroll, slow-scroll, jump,
                                           touchpad, touchscreen, flick, mixed
          --chat, -c <title>       Use existing chat by title (default: synthetic)
          --duration, -d <sec>     Duration per scenario in seconds (default: 10)
          --messages, -m <count>   Synthetic message count (default: 100)
          --output, -o <path>      Write JSON results to file
          --verbose, -v            Print per-frame timing data
          --no-warmup              Skip warm-up phase
          --iterations, -i <n>     Run each scenario N times (default: 1)
          --help, -h               Show this help

        Results are displayed in a window after the benchmark completes.
        """;
}
