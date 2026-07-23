using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumi.MemoryDiagnostics;

public sealed class MemoryCycleSample
{
    public int Cycle { get; init; }
    public long ManagedBytes { get; init; }
    public long HeapSizeBytes { get; init; }
    public long CommittedBytes { get; init; }
    public long FragmentedBytes { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateBytes { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    public int TrackedCount { get; init; }
    public int RetainedCount { get; init; }
    public IReadOnlyDictionary<string, int> RetainedByKind { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

public sealed class MemoryScenarioSamples
{
    public required string ScenarioId { get; init; }
    public required string DisplayName { get; init; }
    public string? Note { get; init; }
    public int AllowedRetainedCount { get; init; }
    public List<MemoryCycleSample> Cycles { get; } = [];
    public List<string> Errors { get; } = [];
}

public sealed class MemoryScenarioResult
{
    public required string ScenarioId { get; init; }
    public required string DisplayName { get; init; }
    public string? Note { get; init; }
    public int AllowedRetainedCount { get; init; }
    public IReadOnlyList<MemoryCycleSample> Cycles { get; init; } = Array.Empty<MemoryCycleSample>();
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public int FinalRetainedCount { get; init; }
    public IReadOnlyDictionary<string, int> FinalRetainedByKind { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
    public long ManagedGrowthBytes { get; init; }
    public double ManagedSlopeBytesPerCycle { get; init; }
    public long HeapGrowthBytes { get; init; }
    public double HeapSlopeBytesPerCycle { get; init; }
    public long PrivateGrowthBytes { get; init; }
    public bool RetentionFailed { get; init; }
    public bool ManagedGrowthFailed { get; init; }
    public bool MemoryGateFailed => RetentionFailed || ManagedGrowthFailed;
    public bool Failed => Errors.Count > 0 || RetentionFailed || ManagedGrowthFailed;
}

public sealed class MemoryStressReport
{
    public DateTimeOffset GeneratedAt { get; init; }
    public string Mode { get; init; } = "full";
    public int Cycles { get; init; }
    public int WarmupCycles { get; init; }
    public int ActionsPerCycle { get; init; }
    public int GcPasses { get; init; }
    public long MaxManagedGrowthBytes { get; init; }
    public long MaxManagedSlopeBytesPerCycle { get; init; }
    public IReadOnlyList<MemoryScenarioResult> Scenarios { get; init; } = Array.Empty<MemoryScenarioResult>();
    public int GateFailedScenarioCount { get; init; }
    public int HarnessErrorScenarioCount { get; init; }
    public int FailedScenarioCount { get; init; }
    public bool GateFailed => GateFailedScenarioCount > 0;
    public bool HasHarnessErrors => HarnessErrorScenarioCount > 0;

    public static MemoryStressReport Build(
        MemoryHarnessOptions options,
        IEnumerable<MemoryScenarioSamples> samples)
    {
        ArgumentNullException.ThrowIfNull(options);
        var results = new List<MemoryScenarioResult>();

        foreach (var sample in samples ?? Array.Empty<MemoryScenarioSamples>())
        {
            var cycles = sample.Cycles.ToList();
            var first = cycles.FirstOrDefault();
            var last = cycles.LastOrDefault();
            var managedGrowth = first is null || last is null ? 0 : last.ManagedBytes - first.ManagedBytes;
            var heapGrowth = first is null || last is null ? 0 : last.HeapSizeBytes - first.HeapSizeBytes;
            var privateGrowth = first is null || last is null ? 0 : last.PrivateBytes - first.PrivateBytes;
            var managedSlope = LinearSlope(cycles.Select(static cycle => (double)cycle.ManagedBytes));
            var heapSlope = LinearSlope(cycles.Select(static cycle => (double)cycle.HeapSizeBytes));
            var finalRetained = last?.RetainedCount ?? 0;

            results.Add(new MemoryScenarioResult
            {
                ScenarioId = sample.ScenarioId,
                DisplayName = sample.DisplayName,
                Note = sample.Note,
                AllowedRetainedCount = sample.AllowedRetainedCount,
                Cycles = cycles,
                Errors = sample.Errors.ToList(),
                FinalRetainedCount = finalRetained,
                FinalRetainedByKind = last?.RetainedByKind
                    ?? new Dictionary<string, int>(StringComparer.Ordinal),
                ManagedGrowthBytes = managedGrowth,
                ManagedSlopeBytesPerCycle = managedSlope,
                HeapGrowthBytes = heapGrowth,
                HeapSlopeBytesPerCycle = heapSlope,
                PrivateGrowthBytes = privateGrowth,
                RetentionFailed = finalRetained > sample.AllowedRetainedCount,
                ManagedGrowthFailed =
                    managedGrowth > options.MaxManagedGrowthBytes
                    && managedSlope > options.MaxManagedSlopeBytesPerCycle,
            });
        }

        results.Sort((left, right) =>
        {
            var byFailure = right.Failed.CompareTo(left.Failed);
            if (byFailure != 0)
                return byFailure;
            var byRetained = right.FinalRetainedCount.CompareTo(left.FinalRetainedCount);
            return byRetained != 0
                ? byRetained
                : right.ManagedSlopeBytesPerCycle.CompareTo(left.ManagedSlopeBytesPerCycle);
        });

        return new MemoryStressReport
        {
            GeneratedAt = DateTimeOffset.Now,
            Mode = options.Mode,
            Cycles = options.Cycles,
            WarmupCycles = options.WarmupCycles,
            ActionsPerCycle = options.ActionsPerCycle,
            GcPasses = options.GcPasses,
            MaxManagedGrowthBytes = options.MaxManagedGrowthBytes,
            MaxManagedSlopeBytesPerCycle = options.MaxManagedSlopeBytesPerCycle,
            Scenarios = results,
            GateFailedScenarioCount = results.Count(static result => result.MemoryGateFailed),
            HarnessErrorScenarioCount = results.Count(static result => result.Errors.Count > 0),
            FailedScenarioCount = results.Count(static result => result.Failed),
        };
    }

    public static MemoryStressReport BuildHarnessFailure(
        MemoryHarnessOptions options,
        Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var samples = new MemoryScenarioSamples
        {
            ScenarioId = "harness",
            DisplayName = "Harness setup or orchestration",
            Note = "The stress run could not complete, so no memory-gate conclusion is valid.",
            AllowedRetainedCount = 0,
        };
        samples.Errors.Add(error.ToString());
        return Build(options, [samples]);
    }

    public string ToConsole()
    {
        var sb = new StringBuilder();
        const string rule = "================================================================================";
        sb.AppendLine(rule);
        sb.AppendLine(" Lumi Memory Stress Report");
        sb.AppendLine(
            $" Generated: {GeneratedAt:yyyy-MM-dd HH:mm:ss}  Mode: {Mode}  " +
            $"Cycles: {Cycles} (+{WarmupCycles} warmup)  Actions/cycle: {ActionsPerCycle}");
        sb.AppendLine(
            $" Gate: retained objects above scenario allowance, or managed growth > {MiB(MaxManagedGrowthBytes):n1} MiB " +
            $"with slope > {MiB(MaxManagedSlopeBytesPerCycle):n1} MiB/cycle");
        sb.AppendLine(rule);
        sb.AppendLine();
        sb.AppendLine($"   {"RESULT",-7} {"retained",10} {"managed",11} {"slope/cycle",13} {"private",11}  scenario");
        sb.AppendLine($"   {new string('-', 74)}");

        foreach (var scenario in Scenarios)
        {
            sb.AppendLine(
                $"   {(scenario.Failed ? "FAIL" : "PASS"),-7} " +
                $"{scenario.FinalRetainedCount,5}/{scenario.AllowedRetainedCount,-4} " +
                $"{SignedMiB(scenario.ManagedGrowthBytes),11} " +
                $"{SignedMiB(scenario.ManagedSlopeBytesPerCycle),13} " +
                $"{SignedMiB(scenario.PrivateGrowthBytes),11}  {scenario.DisplayName}");

            if (scenario.FinalRetainedByKind.Count > 0)
            {
                sb.AppendLine("           retained: " + string.Join(
                    ", ",
                    scenario.FinalRetainedByKind
                        .OrderByDescending(static pair => pair.Value)
                        .Select(static pair => $"{pair.Key}={pair.Value}")));
            }

            foreach (var error in scenario.Errors)
                sb.AppendLine($"           error: {error}");
        }

        sb.AppendLine();
        if (HasHarnessErrors)
        {
            sb.AppendLine(
                $" HARNESS: FAIL - {HarnessErrorScenarioCount} scenario(s) could not complete; " +
                "the memory gate result is invalid.");
        }
        else
        {
            sb.AppendLine(GateFailed
                ? $" GATE: FAIL - {GateFailedScenarioCount} scenario(s) retained objects or kept growing after forced GC."
                : " GATE: PASS - no tested scenario retained unexpected objects or showed sustained managed growth.");
        }
        sb.AppendLine(rule);
        return sb.ToString();
    }

    public string ToJson()
        => JsonSerializer.Serialize(this, MemoryStressJsonContext.Default.MemoryStressReport);

    internal static double LinearSlope(IEnumerable<double> values)
    {
        var points = values?.ToArray() ?? Array.Empty<double>();
        if (points.Length < 2)
            return 0d;

        var xMean = (points.Length - 1) / 2d;
        var yMean = points.Average();
        var numerator = 0d;
        var denominator = 0d;

        for (var i = 0; i < points.Length; i++)
        {
            var xDelta = i - xMean;
            numerator += xDelta * (points[i] - yMean);
            denominator += xDelta * xDelta;
        }

        return denominator == 0d ? 0d : numerator / denominator;
    }

    private static double MiB(double bytes) => bytes / (1024d * 1024d);

    private static string SignedMiB(double bytes)
        => MiB(bytes).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " MiB";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(MemoryStressReport))]
internal partial class MemoryStressJsonContext : JsonSerializerContext;
