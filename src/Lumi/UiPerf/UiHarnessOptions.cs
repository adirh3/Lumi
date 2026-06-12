using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Lumi.UiPerf;

/// <summary>
/// Parsed configuration for the UI responsiveness harness. Supports a "full" run (all
/// categories) or a "filtered" run (an explicit subset of UX categories).
/// </summary>
public sealed class UiHarnessOptions
{
    /// <summary>CLI flags that enable the harness.</summary>
    public static readonly IReadOnlyList<string> EnableFlags = new[]
    {
        "--ui-perf-harness",
        "--ui-responsiveness-harness",
        "--stress-ui",
        "--ui-perf",
    };

    public bool Enabled { get; private set; }
    public int Iterations { get; private set; } = 6;
    public int WarmupIterations { get; private set; } = 2;
    public int SampleIntervalMs { get; private set; } = 8;
    public int SettleQuietMs { get; private set; } = 120;
    public bool KeepOpen { get; private set; }
    public string? OutputPath { get; private set; }

    /// <summary>
    /// Number of concurrently "running" (streaming) chats to spin up as background load while
    /// measuring. 0 (default) measures an idle app; a higher value reproduces the real lag a user
    /// feels when several agents are working at once. Enables the <c>chat-switch-live</c> action.
    /// </summary>
    public int RunningChats { get; private set; }

    /// <summary>
    /// When set, the harness exits with a non-zero code if any action's impact is at or above this
    /// level — turning the harness into a CI/regression gate that guards Lumi's responsiveness.
    /// </summary>
    public ImpactLevel? FailOnLevel { get; private set; }

    private readonly List<string> _rawCategories = new();
    private readonly HashSet<string> _includedCategories = new(StringComparer.Ordinal);

    /// <summary>Categories requested by the user (display form), empty when running in full mode.</summary>
    public IReadOnlyList<string> RequestedCategories => _rawCategories;

    /// <summary>True when no category filter was supplied (run every category).</summary>
    public bool IsFull => _includedCategories.Count == 0;

    public string Mode => IsFull ? "full" : "filtered";

    /// <summary>Whether the given category should run under the current filter.</summary>
    public bool IncludesCategory(string category)
        => IsFull || _includedCategories.Contains(NormalizeCategory(category));

    /// <summary>Normalizes a category key so "Chat Open", "chat-open" and "chatopen" all match.</summary>
    public static string NormalizeCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return string.Empty;

        Span<char> buffer = stackalloc char[category.Length];
        var length = 0;
        foreach (var ch in category)
        {
            if (ch is '-' or '_' or ' ')
                continue;
            buffer[length++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..length]);
    }

    public static UiHarnessOptions Parse(IReadOnlyList<string>? args)
    {
        var options = new UiHarnessOptions();
        if (args is null || args.Count == 0)
            return options;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
                continue;

            var (key, inlineValue) = SplitFlag(arg);

            string? TakeValue()
            {
                if (inlineValue is not null)
                    return inlineValue;
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    return args[++i];
                return null;
            }

            switch (key.ToLowerInvariant())
            {
                case "--ui-perf-harness":
                case "--ui-responsiveness-harness":
                case "--stress-ui":
                case "--ui-perf":
                    options.Enabled = true;
                    break;
                case "--ui-perf-iterations":
                case "--ui-perf-iters":
                    if (TryInt(TakeValue(), out var iterations))
                        options.Iterations = Math.Clamp(iterations, 1, 200);
                    break;
                case "--ui-perf-warmup":
                    if (TryInt(TakeValue(), out var warmup))
                        options.WarmupIterations = Math.Clamp(warmup, 0, 50);
                    break;
                case "--ui-perf-sample-ms":
                    if (TryInt(TakeValue(), out var sampleMs))
                        options.SampleIntervalMs = Math.Clamp(sampleMs, 1, 200);
                    break;
                case "--ui-perf-settle-ms":
                    if (TryInt(TakeValue(), out var settleMs))
                        options.SettleQuietMs = Math.Clamp(settleMs, 0, 5000);
                    break;
                case "--ui-perf-running-chats":
                case "--ui-perf-running":
                case "--ui-perf-load":
                    if (TryInt(TakeValue(), out var runningChats))
                        options.RunningChats = Math.Clamp(runningChats, 0, 32);
                    break;
                case "--ui-perf-keep-open":
                case "--ui-perf-keepopen":
                    options.KeepOpen = true;
                    break;
                case "--ui-perf-output":
                case "--ui-perf-out":
                    options.OutputPath = TakeValue();
                    break;
                case "--ui-perf-fail-on":
                case "--ui-perf-failon":
                case "--ui-perf-gate":
                    options.FailOnLevel = ParseImpact(TakeValue());
                    break;
                case "--ui-perf-mode":
                    if (string.Equals(TakeValue(), "full", StringComparison.OrdinalIgnoreCase))
                    {
                        options._rawCategories.Clear();
                        options._includedCategories.Clear();
                    }
                    break;
                case "--ui-perf-filter":
                case "--ui-perf-categories":
                case "--ui-perf-only":
                    options.AddCategories(TakeValue());
                    break;
            }
        }

        return options;
    }

    private void AddCategories(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        foreach (var token in value.Split(new[] { ',', ';', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = NormalizeCategory(token);
            if (normalized.Length == 0)
                continue;
            if (_includedCategories.Add(normalized))
                _rawCategories.Add(token.Trim());
        }
    }

    private static (string Key, string? Value) SplitFlag(string arg)
    {
        var equalsIndex = arg.IndexOf('=');
        return equalsIndex < 0
            ? (arg, null)
            : (arg[..equalsIndex], arg[(equalsIndex + 1)..]);
    }

    private static bool TryInt(string? value, out int result)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    /// <summary>Parses an impact level name (good/moderate/high/critical) for the regression gate.</summary>
    public static ImpactLevel? ParseImpact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "good" => ImpactLevel.Good,
            "moderate" or "mod" => ImpactLevel.Moderate,
            "high" => ImpactLevel.High,
            "critical" or "crit" => ImpactLevel.Critical,
            _ => null,
        };
    }
}
