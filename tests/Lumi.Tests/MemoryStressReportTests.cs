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
            managedBytes: [100_000_000, 112_000_000, 130_000_000]);
        var options = MemoryHarnessOptions.Parse(
        [
            "--memory-stress-harness",
            "--memory-max-growth-mb", "10",
            "--memory-max-slope-mb", "5",
        ]);

        var report = MemoryStressReport.Build(options, [samples]);

        Assert.True(report.Scenarios.Single().ManagedGrowthFailed);
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

    private static MemoryScenarioSamples Scenario(
        IReadOnlyList<int> retained,
        IReadOnlyList<long> managedBytes)
    {
        var samples = new MemoryScenarioSamples
        {
            ScenarioId = "chat-surfaces",
            DisplayName = "Chat surfaces",
            AllowedRetainedCount = 0,
        };

        for (var i = 0; i < managedBytes.Count; i++)
        {
            samples.Cycles.Add(new MemoryCycleSample
            {
                Cycle = i + 1,
                ManagedBytes = managedBytes[i],
                HeapSizeBytes = managedBytes[i],
                PrivateBytes = managedBytes[i] * 2,
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
