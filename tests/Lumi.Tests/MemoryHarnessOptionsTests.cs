using Lumi.MemoryDiagnostics;
using Xunit;

namespace Lumi.Tests;

public sealed class MemoryHarnessOptionsTests
{
    [Fact]
    public void Parse_NoArgs_IsDisabledAndFull()
    {
        var options = MemoryHarnessOptions.Parse([]);

        Assert.False(options.Enabled);
        Assert.True(options.IsFull);
        Assert.Equal("full", options.Mode);
    }

    [Theory]
    [InlineData("--memory-stress-harness")]
    [InlineData("--memory-leak-harness")]
    [InlineData("--stress-memory")]
    [InlineData("--memory-harness")]
    public void Parse_EnableFlags_EnableHarness(string flag)
    {
        Assert.True(MemoryHarnessOptions.Parse([flag]).Enabled);
    }

    [Fact]
    public void Parse_NumericAndOutputFlags()
    {
        var options = MemoryHarnessOptions.Parse(
        [
            "--memory-stress-harness",
            "--memory-cycles", "9",
            "--memory-warmup", "3",
            "--memory-actions", "20",
            "--memory-settle-ms", "250",
            "--memory-gc-passes", "5",
            "--memory-max-growth-mb", "40",
            "--memory-max-slope-mb", "4",
            "--memory-output", "C:\\reports\\memory.json",
            "--memory-keep-open",
        ]);

        Assert.Equal(9, options.Cycles);
        Assert.Equal(3, options.WarmupCycles);
        Assert.Equal(20, options.ActionsPerCycle);
        Assert.Equal(250, options.SettleMilliseconds);
        Assert.Equal(5, options.GcPasses);
        Assert.Equal(40L * 1024 * 1024, options.MaxManagedGrowthBytes);
        Assert.Equal(4L * 1024 * 1024, options.MaxManagedSlopeBytesPerCycle);
        Assert.Equal("C:\\reports\\memory.json", options.OutputPath);
        Assert.True(options.KeepOpen);
    }

    [Fact]
    public void Parse_Filter_NormalizesScenarioNames()
    {
        var options = MemoryHarnessOptions.Parse(
        [
            "--memory-stress-harness",
            "--memory-filter=chat-surfaces, transcript_rebuild",
        ]);

        Assert.False(options.IsFull);
        Assert.True(options.IncludesScenario("Chat Surfaces"));
        Assert.True(options.IncludesScenario("transcript-rebuild"));
        Assert.False(options.IncludesScenario("attachment-thumbnails"));
    }

    [Fact]
    public void Parse_ClampsNumericValues()
    {
        var options = MemoryHarnessOptions.Parse(
        [
            "--memory-stress-harness",
            "--memory-cycles", "0",
            "--memory-actions", "999",
            "--memory-gc-passes", "0",
        ]);

        Assert.Equal(1, options.Cycles);
        Assert.Equal(200, options.ActionsPerCycle);
        Assert.Equal(1, options.GcPasses);
    }
}
