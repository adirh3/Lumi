using System.Text.Json;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Microsoft.Extensions.AI;
using Xunit;

namespace Lumi.Tests;

public sealed class UIAutomationCaptureTests
{
    private static readonly DateTimeOffset CapturedAt = DateTimeOffset.Parse("2026-09-27T10:00:00Z");
    private static readonly UIAutomationWindowInfo Window = new(123, 456, "Fixture", "fixture", -1920, 100, 1600, 1000, 144);

    private static UIAutomationCapture Capture()
        => new("capture-1", Window, 800, 500, CapturedAt, new byte[] { 1, 2, 3 });

    [Fact]
    public void Coordinates_MapImagePixelsToTheCapturedWindowIncludingNegativeMonitors()
    {
        var capture = Capture();
        Assert.Equal((-1720, 200), capture.MapPoint(100, 50, Window, CapturedAt));
        Assert.Equal((-321, 1099), capture.MapPoint(799.9, 499.9, Window, CapturedAt));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(800, 1)]
    [InlineData(1, 500)]
    [InlineData(double.NaN, 1)]
    [InlineData(1, double.PositiveInfinity)]
    public void Coordinates_RejectPointsOutsideTheImage(double x, double y)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Capture().MapPoint(x, y, Window, CapturedAt));

    [Fact]
    public void Coordinates_RejectExpiredMovedResizedDpiChangedOrReplacedWindows()
    {
        var capture = Capture();
        Assert.Throws<InvalidOperationException>(() => capture.MapPoint(1, 1, Window, CapturedAt.AddMinutes(3)));
        foreach (var changed in new[]
        {
            Window with { Left = Window.Left + 1 }, Window with { Top = Window.Top + 1 },
            Window with { Width = 1700 }, Window with { Height = 1100 },
            Window with { Dpi = 192 }, Window with { ProcessId = 999 }, Window with { Handle = 999 }
        })
            Assert.Throws<InvalidOperationException>(() => capture.MapPoint(1, 1, changed, CapturedAt));
    }

    [Fact]
    public async Task Screenshot_IsActualImageContentThroughTheFunctionAdapter()
    {
        var capture = Capture();
        var tool = AIFunctionFactory.Create(
            ([System.ComponentModel.Description("Window title")] string title) =>
                ChatViewModel.CreateDesktopScreenshotContent(capture),
            "ui_screenshot_probe", serializerOptions: AppDataJsonContext.Default.Options);
        Assert.Contains("title", tool.JsonSchema.ToString());
        var output = await tool.InvokeAsync(new AIFunctionArguments { ["title"] = "Fixture" });
        var content = Assert.IsType<ToolResultAIContent>(output);
        Assert.Equal("success", content.Result.ResultType);
        var image = Assert.Single(content.Result.BinaryResultsForLlm!);
        Assert.Equal(ToolBinaryResultType.Image, image.Type);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(capture.PngBytes.ToArray(), Convert.FromBase64String(image.Data));
        using var metadata = JsonDocument.Parse(content.Result.TextResultForLlm);
        Assert.Equal(capture.CaptureId, metadata.RootElement.GetProperty("captureId").GetString());
        Assert.Equal(800, metadata.RootElement.GetProperty("width").GetInt32());
    }

    [Fact]
    public void BackgroundBatch_RejectsKeyboardBeforeEarlierMutatingSteps()
    {
        using var service = new UIAutomationService();
        using var result = JsonDocument.Parse(service.ExecuteSteps("Unused",
        [
            new() { Action = "type", Target = "Email", Value = "must not run" },
            new() { Action = "keys", Value = "Ctrl+S" }
        ]));
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.True(result.RootElement.GetProperty("requiresForeground").GetBoolean());
        Assert.Equal(0, result.RootElement.GetProperty("completedSteps").GetInt32());
        Assert.Equal(2, result.RootElement.GetProperty("failedStep").GetInt32());
    }
}
