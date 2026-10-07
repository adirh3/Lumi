using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class BrowserAutomationResultTests
{
    [Theory]
    [InlineData("Error: missing input")]
    [InlineData("error: missing input")]
    [InlineData("Timeout waiting for input")]
    [InlineData("Unknown action: submit")]
    [InlineData("Cannot go back — no previous page.")]
    [InlineData("No download detected.")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("")]
    public void LegacyFailuresAreNormalized(string text)
    {
        var result = BrowserActionResult.FromLegacy(text);
        Assert.False(result.Succeeded);
        Assert.StartsWith("Error: ", result.ToDisplayText());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"clicked\"")]
    [InlineData("{\"ok\":\"true\",\"message\":\"clicked\"}")]
    [InlineData("{\"ok\":true,\"message\":null}")]
    [InlineData("{\"ok\":true}")]
    [InlineData("{bad secret-password json")]
    public void InvalidScriptResultsFailWithoutEchoingPayload(string json)
    {
        var result = BrowserActionResult.FromScript(json);
        Assert.False(result.Succeeded);
        Assert.Contains("not retried", result.Message);
        Assert.DoesNotContain("secret-password", result.Message);
    }

    [Fact]
    public void StructuredStatusDoesNotInferFailureFromPageText()
    {
        var result = BrowserActionResult.FromScript("""{"ok":true,"message":"Clicked button Error: help"}""");
        Assert.True(result.Succeeded);
        Assert.Equal("Clicked button Error: help", result.Message);
        Assert.True(BrowserActionResult.FromLegacy("Uploaded Error: help.txt").Succeeded);
    }

    [Fact]
    public void PendingIsNotSuccess()
    {
        var result = BrowserActionResult.FromScript("""{"ok":false,"message":"Waiting","pending":true}""");
        Assert.False(result.Succeeded);
        Assert.True(result.Pending);
    }

    [Fact]
    public void ExceptionDetailsCannotEchoTypedPassword()
    {
        var result = BrowserActionResult.FromException(new InvalidOperationException("secret-password"));
        Assert.False(result.Succeeded);
        Assert.Contains("InvalidOperationException", result.Message);
        Assert.DoesNotContain("secret-password", result.Message);
    }

    [Fact]
    public async Task PartialFillStopsBatchBeforeSubmitAndObservesOnce()
    {
        var executed = new List<string>();
        var observations = 0;
        var output = await BrowserAutomationBatch.ExecuteAsync(
            """[{"action":"click"},{"action":"fill","value":"secret-password"},{"action":"click","target":"Submit"}]""",
            step =>
            {
                executed.Add(step.Action);
                return Task.FromResult(step.Action == "fill"
                    ? BrowserActionResult.Failure("Fill stopped at field 2: missing. Completed fields: 1")
                    : BrowserActionResult.Success("Clicked once"));
            },
            () => { observations++; return Task.FromResult("fresh observation"); });

        Assert.Equal(new[] { "click", "fill" }, executed);
        Assert.Equal(1, observations);
        Assert.Contains("step 2 failed", output);
        Assert.Contains("Completed 1 of 3", output);
        Assert.Contains("Unexecuted steps: 3 (click)", output);
        Assert.Contains("fresh observation", output);
        Assert.DoesNotContain("secret-password", output);
    }

    [Theory]
    [InlineData("steps")]
    [InlineData("download")]
    [InlineData("unknown")]
    [InlineData("")]
    public async Task UnsupportedStepFailsInsteadOfSkipping(string action)
    {
        var calls = 0;
        var output = await BrowserAutomationBatch.ExecuteAsync(
            "[{\"action\":\"" + action + "\"},{\"action\":\"click\"}]",
            _ => { calls++; return Task.FromResult(BrowserActionResult.Success("Clicked")); },
            () => Task.FromResult("fresh observation"));
        Assert.Equal(0, calls);
        Assert.Contains("step 1 failed", output);
        Assert.Contains("Unexecuted steps: 2 (click)", output);
    }

    [Fact]
    public async Task ExceptionDoesNotRetryExecutedClickOrContinue()
    {
        var calls = 0;
        var output = await BrowserAutomationBatch.ExecuteAsync(
            """[{"action":"click"},{"action":"click"}]""",
            _ => { calls++; throw new TimeoutException("secret-password"); },
            () => Task.FromResult("fresh observation"));
        Assert.Equal(1, calls);
        Assert.Contains("timed out", output);
        Assert.Contains("not retried", output);
        Assert.DoesNotContain("secret-password", output);
    }

    [Fact]
    public async Task SuccessfulBatchObservesOnlyAtEnd()
    {
        var events = new List<string>();
        var output = await BrowserAutomationBatch.ExecuteAsync(
            """[{"action":"type","value":"secret-password"},{"action":"click"}]""",
            step => { events.Add(step.Action); return Task.FromResult(BrowserActionResult.Success("Done")); },
            () => { events.Add("observe"); return Task.FromResult("fresh observation"); });
        Assert.Equal(new[] { "type", "click", "observe" }, events);
        Assert.Contains("Completed 2 of 2", output);
        Assert.DoesNotContain("secret-password", output);
    }

    [Fact]
    public async Task MalformedBatchDoesNotEchoInput()
    {
        var output = await BrowserAutomationBatch.ExecuteAsync(
            "[secret-password",
            _ => throw new InvalidOperationException("must not execute"),
            () => throw new InvalidOperationException("must not observe"));
        Assert.Equal("Error: invalid JSON for steps.", output);
    }

    [Fact]
    public void DocumentScopesAreUniqueAndStayWithinJavascriptSafeIntegers()
    {
        var first = BrowserDomScript.Build("look");
        var second = BrowserDomScript.Build("look");
        Assert.NotEqual(first, second);
        Assert.True(BrowserDomScript.MaxScope * BrowserDomScript.NodesPerScope +
            BrowserDomScript.NodesPerScope - 1 <= 9_007_199_254_740_991);
    }

    [Fact]
    public void ScriptArgumentsAreQuotedWithoutReflectionSerialization()
    {
        const string value = "quotes: \"' slash: \\ newline:\n separator:\u2028 </script>";
        var script = BrowserDomScript.Build("type", "input", value);
        var start = script.IndexOf("const value = ", StringComparison.Ordinal) + "const value = ".Length;
        var end = script.IndexOf(";\n", start, StringComparison.Ordinal);
        using var literal = JsonDocument.Parse(script[start..end]);
        Assert.Equal(value, literal.RootElement.GetString());
        Assert.DoesNotContain("</script>", script);
    }
}
