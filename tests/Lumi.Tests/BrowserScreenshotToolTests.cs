using System.Reflection;
using System.Text.Json;
using GitHub.Copilot;
using Lumi.Services;
using Lumi.ViewModels;
using Microsoft.Extensions.AI;
using Xunit;

namespace Lumi.Tests;

public sealed class BrowserScreenshotToolTests
{
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");

    [Theory]
    [InlineData(null)]
    [InlineData("tab-alpha")]
    public async Task ScreenshotDelegate_ThroughSdkConversion_DeliversAnImageNotJsonText(string? requestedTabId)
    {
        string? capturedTabId = "not-called";
        var captureCount = 0;
        var capture = new TaskCompletionSource<BrowserScreenshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = ChatViewModel.BuildBrowserScreenshotTool(tabId =>
        {
            capturedTabId = tabId;
            captureCount++;
            return capture.Task;
        });
        var arguments = new AIFunctionArguments();
        if (requestedTabId is not null)
            arguments["tabId"] = JsonSerializer.SerializeToElement(requestedTabId);

        var invocation = tool.InvokeAsync(arguments).AsTask();
        Assert.False(invocation.IsCompleted);
        capture.SetResult(new BrowserScreenshot("tab-alpha", "https://example.test/canvas", 1, 1, PngBytes));
        var rawResult = await invocation;

        Assert.Equal(requestedTabId, capturedTabId);
        Assert.Equal(1, captureCount);
        var contents = Assert.IsType<AIContent[]>(rawResult);
        Assert.Equal(2, contents.Length);
        var text = Assert.IsType<TextContent>(contents[0]);
        var image = Assert.IsType<DataContent>(contents[1]);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(PngBytes, image.Data.ToArray());

        // This is the actual SDK conversion called by CopilotSession after AIFunction.InvokeAsync.
        // Testing only a constructed DataContent would miss accidental JSON marshalling by the factory.
        var convert = typeof(ToolResultObject).GetMethod(
            "ConvertFromInvocationResult",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(convert);
        var result = Assert.IsType<ToolResultObject>(
            convert.Invoke(null, [rawResult, tool.JsonSerializerOptions]));

        Assert.Equal("success", result.ResultType);
        Assert.Null(result.Error);
        Assert.Equal(text.Text, result.TextResultForLlm);
        Assert.Contains("tab-alpha", result.TextResultForLlm);
        Assert.Contains("https://example.test/canvas", result.TextResultForLlm);
        Assert.Contains("1 × 1 pixels", result.TextResultForLlm);
        Assert.DoesNotContain(Convert.ToBase64String(PngBytes), result.TextResultForLlm);
        var binary = Assert.Single(result.BinaryResultsForLlm!);
        Assert.Equal(ToolBinaryResultType.Image, binary.Type);
        Assert.Equal("image/png", binary.MimeType);
        Assert.Equal(PngBytes, Convert.FromBase64String(binary.Data));

        var wireResult = JsonSerializer.SerializeToElement(result);
        var wireImage = Assert.Single(wireResult.GetProperty("binaryResultsForLlm").EnumerateArray());
        Assert.Equal("image", wireImage.GetProperty("type").GetString());
        Assert.Equal("image/png", wireImage.GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String(PngBytes), wireImage.GetProperty("data").GetString());
    }

    [Fact]
    public async Task ScreenshotDelegate_PreservesUsefulCaptureFailureForSdkToolBoundary()
    {
        var expected = new InvalidOperationException("Browser tab 'closed-tab' was closed. List tabs and try again.");
        var tool = ChatViewModel.BuildBrowserScreenshotTool(_ => Task.FromException<BrowserScreenshot>(expected));

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tool.InvokeAsync(new AIFunctionArguments { ["tabId"] = "closed-tab" }).AsTask());

        Assert.Same(expected, actual);
    }

    [Fact]
    public void ScreenshotTool_HasAnOptionalStableTabIdAndVisualGuidance()
    {
        var tool = ChatViewModel.BuildBrowserScreenshotTool(
            _ => throw new InvalidOperationException("Schema inspection must not capture."));

        Assert.Equal(ToolDisplayHelper.BrowserScreenshotToolName, tool.Name);
        Assert.Equal(["tabId"], tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        if (tool.JsonSchema.TryGetProperty("required", out var required))
            Assert.Empty(required.EnumerateArray());
        Assert.Contains("active tab", tool.Description);
        Assert.Contains("visual layout", tool.Description);
        Assert.Contains("canvas", tool.Description);
        Assert.Contains("icons", tool.Description);
        Assert.Contains("visible and ready", tool.Description);
        Assert.Contains("does not switch tabs or show the browser", tool.Description);
    }
}
