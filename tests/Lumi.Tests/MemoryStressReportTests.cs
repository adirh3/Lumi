using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Lumi.MemoryDiagnostics;
using Xunit;

namespace Lumi.Tests;

public sealed class MemoryStressReportTests
{
    [Fact]
    public void Build_StableScenario_Passes()
    {
        var samples = Scenario(
            retained: [0, 0, 0],
            managedBytes: [100_000_000, 100_500_000, 100_250_000]);

        var report = MemoryStressReport.Build(
            MemoryHarnessOptions.Parse(["--memory-stress-harness"]),
            [samples]);

        Assert.False(report.GateFailed);
        Assert.False(report.Scenarios.Single().Failed);
        Assert.Contains("GATE: PASS", report.ToConsole());
    }

    [Fact]
    public void Build_RetainedObjects_FailsEvenWhenHeapIsFlat()
    {
        var samples = Scenario(
            retained: [2, 4, 6],
            managedBytes: [100_000_000, 100_000_000, 100_000_000]);

        var report = MemoryStressReport.Build(
            MemoryHarnessOptions.Parse(["--memory-stress-harness"]),
            [samples]);

        var result = report.Scenarios.Single();
        Assert.True(report.GateFailed);
        Assert.True(result.RetentionFailed);
        Assert.False(result.ManagedGrowthFailed);
        Assert.Equal(6, result.FinalRetainedCount);
    }

    [Fact]
    public void Build_SustainedManagedGrowth_FailsOnlyWhenGrowthAndSlopeBreach()
    {
        var samples = Scenario(
            retained: [0, 0, 0],
            managedBytes: [100_000_000, 112_000_000, 130_000_000],
            // Held flat so this isolates the managed gate from the native one.
            privateBytes: [400_000_000, 400_000_000, 400_000_000]);
        var options = MemoryHarnessOptions.Parse(
        [
            "--memory-stress-harness",
            "--memory-max-growth-mb", "10",
            "--memory-max-slope-mb", "5",
        ]);

        var report = MemoryStressReport.Build(options, [samples]);

        Assert.True(report.Scenarios.Single().ManagedGrowthFailed);
        Assert.False(report.Scenarios.Single().PrivateGrowthFailed);
        Assert.True(report.GateFailed);
    }

    [Fact]
    public void Build_ScenarioError_IsHarnessFailureNotMemoryGateFailure()
    {
        var samples = Scenario([0], [100_000_000]);
        samples.Errors.Add("scenario failed");

        var report = MemoryStressReport.Build(
            MemoryHarnessOptions.Parse(["--memory-stress-harness"]),
            [samples]);

        Assert.True(report.HasHarnessErrors);
        Assert.False(report.GateFailed);
        Assert.Equal(1, report.HarnessErrorScenarioCount);
        Assert.Contains("HARNESS: FAIL", report.ToConsole());
    }

    [Fact]
    public void BuildHarnessFailure_ProducesMachineReadableHarnessError()
    {
        var report = MemoryStressReport.BuildHarnessFailure(
            MemoryHarnessOptions.Parse(["--memory-stress-harness"]),
            new InvalidOperationException("setup failed"));

        Assert.True(report.HasHarnessErrors);
        Assert.False(report.GateFailed);
        Assert.Equal("harness", report.Scenarios.Single().ScenarioId);
        Assert.Contains("setup failed", report.ToJson());
    }

    [Fact]
    public void Build_RealTranscriptScrollLeakSignature_FailsAtDefaultThresholds()
    {
        // These are the private-byte samples actually recorded by the default 6-cycle run while the
        // transcript-scroll GPU leak was live (memory-report-20260816-104957), with the managed heap
        // flat because Skia's OpenGL offscreen render targets are unbudgeted native allocations.
        // The gate exists specifically to catch THIS, so it must fail on it without any tuning.
        var samples = Scenario(
            retained: [0, 0, 0, 0, 0, 0],
            managedBytes: [
                Mib(105.5), Mib(107.8), Mib(107.2), Mib(107.9), Mib(106.9), Mib(107.3)],
            privateBytes: [
                Mib(577.3), Mib(577.8), Mib(577.2), Mib(577.7), Mib(596.9), Mib(632.9)]);

        var report = MemoryStressReport.Build(
            MemoryHarnessOptions.Parse(["--memory-stress-harness"]),
            [samples]);

        var result = report.Scenarios.Single();
        Assert.True(result.PrivateGrowthFailed);
        Assert.False(result.ManagedGrowthFailed);
        Assert.False(result.RetentionFailed);
        Assert.True(report.GateFailed);
        Assert.Contains("native leak", report.ToConsole());
    }

    [Fact]
    public void Build_RealTranscriptScrollAfterFix_PassesAtDefaultThresholds()
    {
        // Same scenario, same machine, after the BitmapCache removal
        // (memory-report-20260816-114425). The gate must not fire on the fixed build.
        var samples = Scenario(
            retained: [0, 0, 0, 0, 0, 0],
            managedBytes: [
                Mib(111.8), Mib(111.8), Mib(111.8), Mib(111.8), Mib(111.8), Mib(111.7)],
            privateBytes: [
                Mib(531.5), Mib(505.7), Mib(505.8), Mib(506.1), Mib(504.6), Mib(506.1)]);

        var report = MemoryStressReport.Build(
            MemoryHarnessOptions.Parse(["--memory-stress-harness"]),
            [samples]);

        Assert.False(report.Scenarios.Single().PrivateGrowthFailed);
        Assert.False(report.GateFailed);
    }

    [Fact]
    public void Build_PrivateOscillationWithoutSustainedTrend_PassesAtDefaultThresholds()
    {
        // Growth alone breaches the budget (+40 MiB end-to-end) but the series just swings around a
        // stable plateau rather than trending upward, which is what the slope half of the gate is for.
        // Uses default options deliberately: a threshold override here would prove nothing.
        var samples = Scenario(
            retained: [0, 0, 0, 0, 0, 0],
            managedBytes: [
                Mib(100), Mib(100), Mib(100), Mib(100), Mib(100), Mib(100)],
            privateBytes: [
                Mib(500), Mib(620), Mib(480), Mib(610), Mib(490), Mib(540)]);

        var report = MemoryStressReport.Build(
            MemoryHarnessOptions.Parse(["--memory-stress-harness"]),
            [samples]);

        var result = report.Scenarios.Single();
        Assert.True(result.PrivateGrowthBytes > 32L * 1024 * 1024);
        Assert.False(result.PrivateGrowthFailed);
        Assert.False(report.GateFailed);
    }

    [Fact]
    public void Build_ScenarioPrivateBudgetOverridesGlobalGate()
    {
        // GPU-bitmap-heavy scenarios opt into a wider native budget rather than forcing the global
        // gate up to their noise floor. Same series, two budgets, opposite verdicts.
        long[] managed = [Mib(100), Mib(100), Mib(100), Mib(100), Mib(100), Mib(100)];
        long[] priv = [Mib(500), Mib(520), Mib(545), Mib(566), Mib(589), Mib(610)];
        var options = MemoryHarnessOptions.Parse(["--memory-stress-harness"]);

        var strict = MemoryStressReport.Build(
            options,
            [Scenario([0, 0, 0, 0, 0, 0], managed, priv)]);
        Assert.True(strict.Scenarios.Single().PrivateGrowthFailed);

        var relaxed = MemoryStressReport.Build(
            options,
            [Scenario([0, 0, 0, 0, 0, 0], managed, priv, allowedPrivateGrowthBytes: Mib(192))]);
        Assert.False(relaxed.Scenarios.Single().PrivateGrowthFailed);
        Assert.False(relaxed.GateFailed);
    }

    [Fact]
    public void ToJson_IncludesCyclesAndGate()
    {
        var report = MemoryStressReport.Build(
            MemoryHarnessOptions.Parse(["--memory-stress-harness"]),
            [Scenario([0, 0], [100_000_000, 100_100_000])]);

        using var document = JsonDocument.Parse(report.ToJson());
        var root = document.RootElement;

        Assert.False(root.GetProperty("gateFailed").GetBoolean());
        Assert.Equal(2, root.GetProperty("scenarios")[0].GetProperty("cycles").GetArrayLength());
        Assert.Equal(
            "chat-surfaces",
            root.GetProperty("scenarios")[0].GetProperty("scenarioId").GetString());
    }

    [Fact]
    public void LinearSlope_UsesAllCycles()
    {
        Assert.Equal(10d, MemoryStressReport.LinearSlope([10d, 20d, 30d]), 6);
    }

    private static long Mib(double value) => (long)(value * 1024 * 1024);

    private static MemoryScenarioSamples Scenario(
        IReadOnlyList<int> retained,
        IReadOnlyList<long> managedBytes,
        IReadOnlyList<long>? privateBytes = null,
        long? allowedPrivateGrowthBytes = null)
    {
        var samples = new MemoryScenarioSamples
        {
            ScenarioId = "chat-surfaces",
            DisplayName = "Chat surfaces",
            AllowedRetainedCount = 0,
            AllowedPrivateGrowthBytes = allowedPrivateGrowthBytes,
        };

        for (var i = 0; i < managedBytes.Count; i++)
        {
            samples.Cycles.Add(new MemoryCycleSample
            {
                Cycle = i + 1,
                ManagedBytes = managedBytes[i],
                HeapSizeBytes = managedBytes[i],
                PrivateBytes = privateBytes?[i] ?? managedBytes[i] * 2,
                RetainedCount = retained[i],
                TrackedCount = retained[i],
                RetainedByKind = retained[i] == 0
                    ? new Dictionary<string, int>()
                    : new Dictionary<string, int> { ["sentinel"] = retained[i] },
            });
        }

        return samples;
    }
}
