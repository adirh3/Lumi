using Lumi.Qa;
using Xunit;

namespace Lumi.Tests;

[Trait("Category", "CoreQA")]
public sealed class QaSmokeContractTests
{
    [Fact]
    public void OrdinaryLaunchDoesNotEnableQa()
    {
        var options = QaSmokeOptions.Parse([]);
        Assert.False(options.Enabled);
        Assert.False(options.VerifyRestart);
        Assert.Null(options.OutputDirectory);
    }

    [Fact]
    public void QaOptionsSelectThePhaseAndNormalizeTheArtifactDirectory()
    {
        var options = QaSmokeOptions.Parse(["--QA-SMOKE", "--qa-verify-restart", "--qa-output", "QaResults"]);
        Assert.True(options.Enabled);
        Assert.True(options.VerifyRestart);
        Assert.Equal(Path.GetFullPath("QaResults"), options.OutputDirectory);
    }

    [Theory]
    [InlineData("--qa-output")]
    [InlineData("--qa-output", "--qa-verify-restart")]
    [InlineData("--qa-output", "")]
    public void IncompleteOutputArgumentsAreRejected(params string[] arguments)
        => Assert.Throws<ArgumentException>(() => QaSmokeOptions.Parse(["--qa-smoke", .. arguments]));

    [Theory]
    [InlineData("--qa-verify-restart")]
    [InlineData("--qa-output", "QaResults")]
    public void QaOptionsCannotBeUsedOnAnOrdinaryLaunch(params string[] arguments)
        => Assert.Throws<ArgumentException>(() => QaSmokeOptions.Parse(arguments));

    [Fact]
    public void OnlyTheExactCompletedScenarioSetPasses()
    {
        var report = new QaSmokeReport { ExpectedScenarios = ["first", "second"] };
        Assert.False(report.Complete);
        report.Scenarios.Add(new("second", true, 1));
        Assert.False(report.Complete);
        report.Scenarios.Add(new("first", true, 1));
        Assert.True(report.Complete);
        report.Scenarios.Add(new("extra", true, 1));
        Assert.False(report.Complete);
    }

    [Theory]
    [InlineData("first", true)]
    [InlineData("second", false)]
    [InlineData("unexpected", true)]
    public void DuplicateFailedAndUnexpectedScenariosCannotProduceAGreenReport(string id, bool passed)
    {
        var report = new QaSmokeReport { ExpectedScenarios = ["first", "second"] };
        report.Scenarios.Add(new("first", true, 1));
        report.Scenarios.Add(new(id, passed, 1));
        Assert.False(report.Complete);
    }

    [Fact]
    public void EmptyOrDuplicateCatalogsNeverPass()
    {
        Assert.False(new QaSmokeReport { ExpectedScenarios = [] }.Complete);
        var report = new QaSmokeReport { ExpectedScenarios = ["first", "first"] };
        report.Scenarios.AddRange([new("first", true, 1), new("first", true, 1)]);
        Assert.False(report.Complete);
        Assert.NotEmpty(QaSmokeReport.ScenarioIds);
        Assert.Equal(QaSmokeReport.ScenarioIds.Count, QaSmokeReport.ScenarioIds.Distinct().Count());
        Assert.DoesNotContain(QaSmokeReport.RestartScenarioIds[0], QaSmokeReport.ScenarioIds);
    }
}
