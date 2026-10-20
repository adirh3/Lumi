using System.Text.Json;
using Lumi.Models;
using Lumi.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace Lumi.Tests;

public sealed class UIAutomationTests
{
    [Theory]
    [InlineData("click", "Save", null)]
    [InlineData("type", "17", "")]
    [InlineData("keys", null, "Ctrl+S")]
    [InlineData("read", "id:email", null)]
    [InlineData("select", "Country", "Canada")]
    [InlineData("select", "Settings tab", null)]
    [InlineData("toggle", "Newsletter", "on")]
    [InlineData("toggle", "Newsletter", "OFF")]
    [InlineData("scroll", "type:List", "down")]
    [InlineData("scroll", "type:List", "Bottom")]
    [InlineData("scroll", "type:List", "top")]
    [InlineData("expand", "name:Europe", null)]
    [InlineData("collapse", "name:Europe", null)]
    [InlineData("click", "Report", "double")]
    [InlineData("click", "Report", "Right")]
    [InlineData("wait", "Status", "Saved")]
    public void Step_ValidatesSupportedActions(string action, string? target, string? value)
    {
        Assert.Null(new UIAutomationStep { Action = action, Target = target, Value = value }.Validate());
    }

    [Theory]
    [InlineData("", "Save", null)]
    [InlineData("delete", "Save", null)]
    [InlineData("click", null, null)]
    [InlineData("type", "Email", null)]
    [InlineData("keys", null, "")]
    [InlineData("toggle", "Newsletter", "maybe")]
    [InlineData("scroll", "List", "diagonal")]
    [InlineData("expand", null, null)]
    [InlineData("click", "Report", "triple")]
    public void Step_RejectsInvalidActions(string action, string? target, string? value)
    {
        Assert.NotNull(new UIAutomationStep { Action = action, Target = target, Value = value }.Validate());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    public void Step_RejectsUnboundedWait(int timeout)
    {
        Assert.NotNull(new UIAutomationStep { Action = "wait", Target = "Ready", TimeoutMs = timeout }.Validate());
    }

    [Fact]
    public void Batch_ValidatesAllStepsBeforeTouchingTheDesktop()
    {
        using var service = new UIAutomationService();
        using var result = JsonDocument.Parse(service.ExecuteSteps("This window must never be accessed",
        [
            new() { Action = "click", Target = "Save" },
            new() { Action = "toggle", Target = "Newsletter", Value = "invalid" }
        ]));

        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, result.RootElement.GetProperty("completedSteps").GetInt32());
        Assert.Equal(2, result.RootElement.GetProperty("failedStep").GetInt32());
        Assert.Equal(0, result.RootElement.GetProperty("results").GetArrayLength());
        Assert.Contains("No actions were performed", result.RootElement.GetProperty("error").GetString());
        Assert.True(result.RootElement.GetProperty("elapsedMs").GetDouble() >= 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public void Batch_RejectsInvalidSizeWithoutNativeCalls(int count)
    {
        using var service = new UIAutomationService();
        var steps = Enumerable.Range(0, count)
            .Select(_ => new UIAutomationStep { Action = "read", Target = "Status" }).ToArray();
        using var result = JsonDocument.Parse(service.ExecuteSteps("Unused", steps));
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("1-32", result.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void ToolSchema_ExposesStructuredStepsAndDeserializesDefaults()
    {
        var function = AIFunctionFactory.Create((UIAutomationStep[] steps) => steps.Length, "ui_batch_schema",
            serializerOptions: AppDataJsonContext.Default.Options);
        var schema = function.JsonSchema.ToString();
        Assert.Contains("\"action\"", schema);
        Assert.Contains("\"target\"", schema);
        Assert.Contains("\"timeoutMs\"", schema);

        var steps = JsonSerializer.Deserialize("""[{"action":"type","target":"12","value":"hello"}]""",
            AppDataJsonContext.Default.UIAutomationStepArray);
        Assert.NotNull(steps);
        Assert.Equal("type", steps[0].Action);
        Assert.Equal(2000, steps[0].TimeoutMs);
        Assert.Null(steps[0].Validate());
    }

#if WINDOWS
    [Fact]
    public void DisposedService_RejectsQueuedEntryPointsBeforeNativeWork()
    {
        var service = new UIAutomationService();
        service.Dispose();
        service.Dispose();
        Assert.Contains("disposed", service.InspectWindow("Unused"), StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ObjectDisposedException>(() => service.GetWindowInfo("Unused"));
        Assert.Throws<ObjectDisposedException>(() => service.CaptureWindow("Unused"));
        using var batch = JsonDocument.Parse(service.ExecuteSteps("Unused",
            [new() { Action = "keys", Value = "Ctrl+S" }], allowForeground: true));
        Assert.False(batch.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, batch.RootElement.GetProperty("completedSteps").GetInt32());
        Assert.Contains("disposed", batch.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Ctrl+S")]
    [InlineData("Win+R")]
    [InlineData("Ctrl+Shift+Tab")]
    [InlineData("Enter")]
    public void Keys_AcceptsOneKeyWithOptionalModifiers(string keys)
        => UIAutomationService.ValidateKeyChord(keys);

    [Theory]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("NotAKey")]
    public void Keys_RejectsMalformedChordsBeforeAnyInput(string keys)
        => Assert.Throws<ArgumentException>(() => UIAutomationService.ValidateKeyChord(keys));

    [Fact]
    public void Batch_CancellationBeforeStartDoesNotFindOrFocusAWindow()
    {
        using var service = new UIAutomationService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var result = JsonDocument.Parse(service.ExecuteSteps("Unused",
            [new() { Action = "click", Target = "Save" }], cancellationToken: cancellation.Token));
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, result.RootElement.GetProperty("completedSteps").GetInt32());
        Assert.Contains("cancel", result.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }
#else
    [Fact]
    public void Batch_NonWindowsReportsUnsupportedRatherThanSuccess()
    {
        using var service = new UIAutomationService();
        using var result = JsonDocument.Parse(service.ExecuteSteps("Unused",
            [new() { Action = "read", Target = "Status" }]));
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("only available on Windows", result.RootElement.GetProperty("error").GetString());
    }
#endif
}
