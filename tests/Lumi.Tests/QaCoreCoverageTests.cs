using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Xunit;

namespace Lumi.Tests;

[Trait("Category", "CoreQA")]
public sealed class QaCoreCoverageTests
{
    [Fact]
    public async Task ExactMethodMembershipPasses()
    {
        var result = await ValidateAsync(["First", "Second"], ("First", "Passed"), ("Second", "Passed"));
        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.True(json.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(2, json.RootElement.GetProperty("passedMethods").GetInt32());
    }

    [Fact]
    public async Task RemovingOneRequiredMethodFailsEvenWhenOtherCasesPass()
    {
        var result = await ValidateAsync(["First", "Second"], ("First", "Passed"));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Missing: [Example.Checks.Second]", result.Error);
    }

    [Fact]
    public async Task AnUnexecutedDefinitionCannotSatisfyCoverage()
    {
        var result = await ValidateAsync(["First", "Second"], ("First", "Passed"), ("Second", null));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Missing: [Example.Checks.Second]", result.Error);
    }

    [Fact]
    public async Task AnUnregisteredMethodRequiresAnExplicitCatalogUpdate()
    {
        var result = await ValidateAsync(["First"], ("First", "Passed"), ("Unexpected", "Passed"));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unregistered: [Example.Checks.Unexpected]", result.Error);
    }

    [Fact]
    public async Task AdditionalParameterizedCasesDoNotRequireCountChanges()
    {
        var result = await ValidateAsync(["First"], ("First", "Passed"), ("First", "Passed"));
        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(1, json.RootElement.GetProperty("passedMethods").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("passedCases").GetInt32());
    }

    [Theory]
    [InlineData("NotExecuted")]
    [InlineData("Failed")]
    public async Task NonPassingResultsNeverCount(string outcome)
    {
        var result = await ValidateAsync(["First"], ("First", outcome));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("did not pass", result.Error);
    }

    private static async Task<ValidationResult> ValidateAsync(
        string[] required, params (string Method, string? Outcome)[] actual)
    {
        var root = Path.Combine(TestProfile.Root, "qa-coverage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var catalogPath = Path.Combine(root, "catalog.json");
        var catalog = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["desktop"] = new JsonArray(required.Select(method => (JsonNode?)JsonValue.Create("Example.Checks." + method)).ToArray())
        };
        await File.WriteAllTextAsync(catalogPath, catalog.ToJsonString());
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var definitions = new XElement(ns + "TestDefinitions");
        var results = new XElement(ns + "Results");
        for (var index = 0; index < actual.Length; index++)
        {
            var (method, outcome) = actual[index];
            var id = "case-" + index;
            definitions.Add(new XElement(ns + "UnitTest", new XAttribute("id", id),
                new XElement(ns + "TestMethod", new XAttribute("className", "Example.Checks, Example"),
                    new XAttribute("name", method))));
            if (outcome is not null)
                results.Add(new XElement(ns + "UnitTestResult", new XAttribute("testId", id),
                    new XAttribute("testName", method), new XAttribute("outcome", outcome)));
        }
        var trxPath = Path.Combine(root, "results.trx");
        new XDocument(new XElement(ns + "TestRun", definitions, results)).Save(trxPath);
        var driverPath = Path.Combine(root, "check-coverage.ps1");
        await File.WriteAllTextAsync(driverPath, """
            param([string]$Validator, [string]$Trx, [string]$Catalog)
            $ErrorActionPreference = 'Stop'
            try { & $Validator -TrxPath $Trx -CatalogPath $Catalog -Project desktop }
            catch {
                [Console]::Error.WriteLine($_.Exception.Message)
                exit 1
            }
            """);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-File", driverPath,
            "-Validator", Path.Combine(AppContext.BaseDirectory, "QaAssets", "Assert-LumiQaCoverage.ps1"),
            "-Trx", trxPath, "-Catalog", catalogPath
        })
            process.StartInfo.ArgumentList.Add(argument);
        Assert.True(process.Start());
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        return new(process.ExitCode, await stdout, await stderr);
    }

    private sealed record ValidationResult(int ExitCode, string Output, string Error);
}
