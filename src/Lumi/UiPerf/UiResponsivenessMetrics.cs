using System;
using System.Collections.Generic;
using System.Linq;

namespace Lumi.UiPerf;

/// <summary>
/// A single UI-thread responsiveness sample. <see cref="LatencyMs"/> is how long a low-priority
/// dispatcher callback waited before running, i.e. how saturated the UI thread was at that moment.
/// </summary>
public readonly record struct LatencySample(double TimestampMs, double LatencyMs);

/// <summary>Severity of a responsiveness problem, from the user's perspective.</summary>
public enum ImpactLevel
{
    Good = 0,
    Moderate = 1,
    High = 2,
    Critical = 3,
}

/// <summary>Thresholds (milliseconds) used to classify UI responsiveness.</summary>
public static class UiResponsivenessThresholds
{
    // Sustained input latency (p99) observed during an action.
    public const double ModerateP99Ms = 50d;
    public const double HighP99Ms = 100d;
    public const double CriticalP99Ms = 200d;

    // Worst single stall (max) during an action — the biggest hitch the user feels.
    public const double ModerateStallMs = 75d;
    public const double HighStallMs = 150d;
    public const double CriticalStallMs = 300d;

    // Jank bucket boundaries.
    public const double JankMs = 50d;
    public const double BadJankMs = 100d;
    public const double FreezeMs = 250d;
}

/// <summary>Aggregated UI-thread latency statistics over a set of samples.</summary>
public sealed class LatencyStats
{
    public int Count { get; init; }
    public double MeanMs { get; init; }
    public double P50Ms { get; init; }
    public double P95Ms { get; init; }
    public double P99Ms { get; init; }
    public double MaxMs { get; init; }

    /// <summary>Samples slower than <see cref="UiResponsivenessThresholds.JankMs"/> (noticeable).</summary>
    public int JankCount { get; init; }

    /// <summary>Samples slower than <see cref="UiResponsivenessThresholds.BadJankMs"/> (clearly laggy).</summary>
    public int BadJankCount { get; init; }

    /// <summary>Samples slower than <see cref="UiResponsivenessThresholds.FreezeMs"/> (feels frozen).</summary>
    public int FreezeCount { get; init; }

    public static LatencyStats Empty { get; } = new();

    public static LatencyStats FromLatencies(IReadOnlyCollection<double> latenciesMs)
    {
        if (latenciesMs is null || latenciesMs.Count == 0)
            return Empty;

        var sorted = latenciesMs.ToArray();
        Array.Sort(sorted);

        var sum = 0d;
        var jank = 0;
        var badJank = 0;
        var freeze = 0;
        foreach (var value in sorted)
        {
            sum += value;
            if (value > UiResponsivenessThresholds.JankMs) jank++;
            if (value > UiResponsivenessThresholds.BadJankMs) badJank++;
            if (value > UiResponsivenessThresholds.FreezeMs) freeze++;
        }

        return new LatencyStats
        {
            Count = sorted.Length,
            MeanMs = sum / sorted.Length,
            P50Ms = Percentile(sorted, 50d),
            P95Ms = Percentile(sorted, 95d),
            P99Ms = Percentile(sorted, 99d),
            MaxMs = sorted[^1],
            JankCount = jank,
            BadJankCount = badJank,
            FreezeCount = freeze,
        };
    }

    /// <summary>Nearest-rank percentile over an ascending-sorted array.</summary>
    public static double Percentile(double[] sortedAscending, double percentile)
    {
        if (sortedAscending is null || sortedAscending.Length == 0)
            return 0d;
        if (sortedAscending.Length == 1)
            return sortedAscending[0];

        var clamped = Math.Clamp(percentile, 0d, 100d);
        var rank = (int)Math.Ceiling(clamped / 100d * sortedAscending.Length);
        var index = Math.Clamp(rank - 1, 0, sortedAscending.Length - 1);
        return sortedAscending[index];
    }
}

/// <summary>Classifies and scores UI responsiveness from latency statistics.</summary>
public static class UiImpactClassifier
{
    public static ImpactLevel Classify(double p99Ms, double maxStallMs)
    {
        if (p99Ms >= UiResponsivenessThresholds.CriticalP99Ms || maxStallMs >= UiResponsivenessThresholds.CriticalStallMs)
            return ImpactLevel.Critical;
        if (p99Ms >= UiResponsivenessThresholds.HighP99Ms || maxStallMs >= UiResponsivenessThresholds.HighStallMs)
            return ImpactLevel.High;
        if (p99Ms >= UiResponsivenessThresholds.ModerateP99Ms || maxStallMs >= UiResponsivenessThresholds.ModerateStallMs)
            return ImpactLevel.Moderate;
        return ImpactLevel.Good;
    }

    public static ImpactLevel Classify(LatencyStats stats)
        => Classify(stats.P99Ms, stats.MaxMs);

    /// <summary>Continuous score (ms-scaled) for ranking actions worst-first.</summary>
    public static double Score(double p99Ms, double maxStallMs)
        => (p99Ms * 0.6d) + (maxStallMs * 0.4d);

    public static double Score(LatencyStats stats)
        => Score(stats.P99Ms, stats.MaxMs);

    public static string Label(ImpactLevel level) => level switch
    {
        ImpactLevel.Critical => "CRITICAL",
        ImpactLevel.High => "HIGH",
        ImpactLevel.Moderate => "MODERATE",
        _ => "GOOD",
    };
}
