using Lumi.UiPerf;
using Xunit;

namespace Lumi.Tests;

public class UiHarnessOptionsTests
{
    [Fact]
    public void Parse_NoArgs_DisabledAndFull()
    {
        var options = UiHarnessOptions.Parse(System.Array.Empty<string>());

        Assert.False(options.Enabled);
        Assert.True(options.IsFull);
        Assert.Equal("full", options.Mode);
    }

    [Theory]
    [InlineData("--ui-perf-harness")]
    [InlineData("--ui-responsiveness-harness")]
    [InlineData("--stress-ui")]
    [InlineData("--ui-perf")]
    public void Parse_EnableFlags_Enable(string flag)
    {
        Assert.True(UiHarnessOptions.Parse(new[] { flag }).Enabled);
    }

    [Fact]
    public void Parse_FullMode_RunsEveryCategory()
    {
        var options = UiHarnessOptions.Parse(new[] { "--ui-perf-harness" });

        Assert.True(options.IsFull);
        Assert.True(options.IncludesCategory("Navigation"));
        Assert.True(options.IncludesCategory("Anything"));
    }

    [Fact]
    public void Parse_Filter_RestrictsToRequestedCategories()
    {
        var options = UiHarnessOptions.Parse(new[] { "--ui-perf-harness", "--ui-perf-filter", "navigation, chat-open" });

        Assert.False(options.IsFull);
        Assert.Equal("filtered", options.Mode);
        Assert.True(options.IncludesCategory("Navigation"));
        Assert.True(options.IncludesCategory("Chat Open"));   // normalized match
        Assert.False(options.IncludesCategory("Search"));
        Assert.Equal(2, options.RequestedCategories.Count);
    }

    [Theory]
    [InlineData("Chat Open")]
    [InlineData("chat-open")]
    [InlineData("chat_open")]
    [InlineData("CHATOPEN")]
    public void NormalizeCategory_IsCaseAndSeparatorInsensitive(string input)
    {
        Assert.Equal("chatopen", UiHarnessOptions.NormalizeCategory(input));
    }

    [Fact]
    public void Parse_NumericFlags_AreParsedAndClamped()
    {
        var options = UiHarnessOptions.Parse(new[]
        {
            "--ui-perf-harness",
            "--ui-perf-iterations", "10",
            "--ui-perf-warmup", "3",
            "--ui-perf-sample-ms", "5",
            "--ui-perf-settle-ms", "200",
        });

        Assert.Equal(10, options.Iterations);
        Assert.Equal(3, options.WarmupIterations);
        Assert.Equal(5, options.SampleIntervalMs);
        Assert.Equal(200, options.SettleQuietMs);
    }

    [Fact]
    public void Parse_Iterations_ClampedToValidRange()
    {
        Assert.Equal(200, UiHarnessOptions.Parse(new[] { "--ui-perf-harness", "--ui-perf-iterations", "9999" }).Iterations);
        Assert.Equal(1, UiHarnessOptions.Parse(new[] { "--ui-perf-harness", "--ui-perf-iterations", "0" }).Iterations);
    }

    [Fact]
    public void Parse_SupportsInlineEqualsValues()
    {
        var options = UiHarnessOptions.Parse(new[] { "--ui-perf-harness", "--ui-perf-iterations=4", "--ui-perf-filter=composer" });

        Assert.Equal(4, options.Iterations);
        Assert.True(options.IncludesCategory("Composer"));
        Assert.False(options.IncludesCategory("Navigation"));
    }

    [Fact]
    public void Parse_KeepOpenAndOutputPath()
    {
        var options = UiHarnessOptions.Parse(new[]
        {
            "--ui-perf-harness",
            "--ui-perf-keep-open",
            "--ui-perf-output", "C:\\reports\\ui.json",
        });

        Assert.True(options.KeepOpen);
        Assert.Equal("C:\\reports\\ui.json", options.OutputPath);
    }

    [Fact]
    public void Parse_FailOn_DefaultsToNoGate()
    {
        Assert.Null(UiHarnessOptions.Parse(new[] { "--ui-perf-harness" }).FailOnLevel);
    }

    [Theory]
    [InlineData("--ui-perf-fail-on")]
    [InlineData("--ui-perf-failon")]
    [InlineData("--ui-perf-gate")]
    public void Parse_FailOn_AliasesSetGateLevel(string flag)
    {
        var options = UiHarnessOptions.Parse(new[] { "--ui-perf-harness", flag, "critical" });

        Assert.Equal(ImpactLevel.Critical, options.FailOnLevel);
    }

    [Theory]
    [InlineData("good", ImpactLevel.Good)]
    [InlineData("moderate", ImpactLevel.Moderate)]
    [InlineData("mod", ImpactLevel.Moderate)]
    [InlineData("HIGH", ImpactLevel.High)]
    [InlineData("crit", ImpactLevel.Critical)]
    public void ParseImpact_AcceptsKnownLevels(string input, ImpactLevel expected)
    {
        Assert.Equal(expected, UiHarnessOptions.ParseImpact(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bogus")]
    public void ParseImpact_ReturnsNullForUnknown(string input)
    {
        Assert.Null(UiHarnessOptions.ParseImpact(input));
    }

    [Fact]
    public void Parse_FailOn_SupportsInlineEquals()
    {
        Assert.Equal(ImpactLevel.High, UiHarnessOptions.Parse(new[] { "--ui-perf-harness", "--ui-perf-fail-on=high" }).FailOnLevel);
    }
}
