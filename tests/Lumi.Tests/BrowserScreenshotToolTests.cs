using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitHub.Copilot;
using Lumi.Models;
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
            arguments["tabId"] = JsonSerializer.SerializeToElement(requestedTabId, AppDataJsonContext.Default.String);

        var invocation = tool.InvokeAsync(arguments).AsTask();
        Assert.False(invocation.IsCompleted);
        capture.SetResult(new BrowserScreenshot("tab-alpha", "https://example.test/canvas", 1, 1, PngBytes));
        var rawResult = await invocation;

        Assert.Equal(requestedTabId, capturedTabId);
        Assert.Equal(1, captureCount);
        var content = Assert.IsType<ToolResultAIContent>(rawResult);
        var result = ConvertToolResult(rawResult, tool);

        Assert.Same(content.Result, result);
        Assert.Equal("success", result.ResultType);
        Assert.Null(result.Error);
        Assert.Contains("tab-alpha", result.TextResultForLlm);
        Assert.Contains("https://example.test/canvas", result.TextResultForLlm);
        Assert.Contains("1 × 1 pixels", result.TextResultForLlm);
        Assert.DoesNotContain(Convert.ToBase64String(PngBytes), result.TextResultForLlm);
        var binary = Assert.Single(result.BinaryResultsForLlm!);
        Assert.Equal(ToolBinaryResultType.Image, binary.Type);
        Assert.Equal("image/png", binary.MimeType);
        Assert.Equal(PngBytes, Convert.FromBase64String(binary.Data));

        var wireResult = JsonSerializer.SerializeToElement(result, BrowserScreenshotTestJsonContext.Default.ToolResultObject);
        var wireImage = Assert.Single(wireResult.GetProperty("binaryResultsForLlm").EnumerateArray());
        Assert.Equal("image", wireImage.GetProperty("type").GetString());
        Assert.Equal("image/png", wireImage.GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String(PngBytes), wireImage.GetProperty("data").GetString());
    }

    [Theory]
    [InlineData("Tab tab-alpha is hidden. Switch to this tab and show the browser panel before capturing.")]
    [InlineData("Browser tab 'tab-alpha' was closed. List tabs and try again.")]
    [InlineData("Screenshot exceeds the 3 MiB PNG budget at a readable size. Narrow the browser panel or use look/find to inspect text.")]
    public async Task ScreenshotDelegate_ThroughSdkConversion_PreservesActionableFailure(string reason)
    {
        var expected = new InvalidOperationException(reason);
        var tool = ChatViewModel.BuildBrowserScreenshotTool(_ => Task.FromException<BrowserScreenshot>(expected));
        var raw = await tool.InvokeAsync(new AIFunctionArguments { ["tabId"] = "tab-alpha" });
        var result = ConvertToolResult(raw, tool);

        Assert.Equal("failure", result.ResultType);
        Assert.Equal(reason, result.Error);
        Assert.StartsWith("Error: Browser screenshot failed.", result.TextResultForLlm);
        Assert.Contains(reason, result.TextResultForLlm);
        Assert.Null(result.BinaryResultsForLlm);
        var wire = JsonSerializer.SerializeToElement(result, BrowserScreenshotTestJsonContext.Default.ToolResultObject);
        Assert.Equal("failure", wire.GetProperty("resultType").GetString());
        Assert.Contains(reason, wire.GetProperty("textResultForLlm").GetString());
        Assert.Equal(reason, wire.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ScreenshotDelegate_DoesNotHideUnexpectedErrors()
    {
        var expected = new ApplicationException("Unexpected capture failure");
        var tool = ChatViewModel.BuildBrowserScreenshotTool(_ => Task.FromException<BrowserScreenshot>(expected));
        var actual = await Assert.ThrowsAsync<ApplicationException>(() => tool.InvokeAsync(new AIFunctionArguments()).AsTask());
        Assert.Same(expected, actual);
    }

    private static ToolResultObject ConvertToolResult(object? rawResult, AIFunction tool)
    {
        // CopilotSession uses this SDK conversion after invoking the actual AIFunction.
        var convert = typeof(ToolResultObject).GetMethod(
            "ConvertFromInvocationResult", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(convert);
        return Assert.IsType<ToolResultObject>(convert.Invoke(null, [rawResult, tool.JsonSerializerOptions]));
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
        Assert.Contains("2048-pixel", tool.Description);
        Assert.Contains("3 MiB PNG (4 MiB base64)", tool.Description);
        Assert.Contains("image-count limits", tool.Description);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("resume")]
    [InlineData("lightweight")]
    public void ScreenshotTool_CanBeRegisteredAndPreloadedWithoutReflectionMetadata(string sessionKind)
    {
        var screenshot = ChatViewModel.BuildBrowserScreenshotTool(
            _ => throw new InvalidOperationException("Session setup must not capture."));
        SessionConfigBase config = sessionKind switch
        {
            "create" => SessionConfigBuilder.Build(
                "prompt", null, null, null, [], [], [screenshot], null, null, null, null),
            "resume" => SessionConfigBuilder.BuildForResume(
                "prompt", null, null, null, [], [], [screenshot], null, null, null, null),
            "lightweight" => SessionConfigBuilder.BuildLightweight(new LightweightSessionOptions
            {
                SystemPrompt = "prompt",
                Tools = [screenshot]
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(sessionKind))
        };
        var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(config.Tools!));

        // The app disables reflection. Its binary SDK envelope needs no JSON return schema,
        // even though the input schema must remain available during every session setup.
        Assert.Throws<NotSupportedException>(() => tool.JsonSerializerOptions!.GetTypeInfo(typeof(ToolResultAIContent)));
        Assert.Null(tool.ReturnJsonSchema);
        Assert.Equal(ToolDisplayHelper.BrowserScreenshotToolName, tool.Name);
        Assert.Equal(["tabId"], tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(CopilotToolDefer.Never, Assert.IsType<CopilotToolDefer>(tool.AdditionalProperties["defer"]));
    }

#if WINDOWS
    [Theory]
    [InlineData(800, 600, 800, 600)]
    [InlineData(2048, 1024, 2048, 1024)]
    [InlineData(3840, 2160, 2048, 1152)]
    [InlineData(2160, 3840, 1152, 2048)]
    public async Task ScreenshotSizing_PreservesAspectRatioAndDeliversActualDimensions(
        int width, int height, int expectedWidth, int expectedHeight)
    {
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.Clear(System.Drawing.Color.Magenta);
        var original = ScreenshotFromBitmap(bitmap);
        var resized = BrowserService.PrepareScreenshotForModel(original);

        Assert.Equal(expectedWidth, resized.Width);
        Assert.Equal(expectedHeight, resized.Height);
        Assert.Equal(original.TabId, resized.TabId);
        Assert.Equal(original.Url, resized.Url);
        Assert.InRange(resized.PngBytes.Length, 1, BrowserService.MaxScreenshotPngBytes);
        if (width == expectedWidth && height == expectedHeight)
            Assert.Same(original, resized);
        using var stream = new MemoryStream(resized.PngBytes);
        using var decoded = new System.Drawing.Bitmap(stream);
        Assert.Equal(expectedWidth, decoded.Width);
        Assert.Equal(expectedHeight, decoded.Height);
        Assert.Equal(System.Drawing.Color.Magenta.ToArgb(), decoded.GetPixel(0, 0).ToArgb());

        var tool = ChatViewModel.BuildBrowserScreenshotTool(_ => Task.FromResult(resized));
        var result = ConvertToolResult(await tool.InvokeAsync(new AIFunctionArguments()), tool);
        Assert.Contains($"{expectedWidth} × {expectedHeight} pixels", result.TextResultForLlm);
        Assert.Equal(resized.PngBytes, Convert.FromBase64String(Assert.Single(result.BinaryResultsForLlm!).Data));
    }

    [Fact]
    public void ScreenshotSizing_ReducesHighDetailPngToFitEncodedBudget()
    {
        using var bitmap = NoiseBitmap(2048, 1536);
        var original = ScreenshotFromBitmap(bitmap);
        Assert.True(original.PngBytes.Length > BrowserService.MaxScreenshotPngBytes);

        var resized = BrowserService.PrepareScreenshotForModel(original);

        Assert.InRange(resized.PngBytes.Length, 1, BrowserService.MaxScreenshotPngBytes);
        Assert.InRange(Convert.ToBase64String(resized.PngBytes).Length, 1, 4 * 1024 * 1024);
        Assert.InRange(resized.Width, 1024, 2047);
        Assert.InRange(Math.Abs(resized.Height - resized.Width * 0.75), 0, 1);
        using var stream = new MemoryStream(resized.PngBytes);
        using var decoded = new System.Drawing.Bitmap(stream);
        Assert.Equal(resized.Width, decoded.Width);
        Assert.Equal(resized.Height, decoded.Height);
    }

    [Fact]
    public void ScreenshotSizing_RejectsOversizedImageRatherThanMakingItIllegible()
    {
        using var bitmap = NoiseBitmap(1024, 1024);
        var screenshot = ScreenshotFromBitmap(bitmap);
        Assert.True(screenshot.PngBytes.Length > BrowserService.MaxScreenshotPngBytes);

        var error = Assert.Throws<InvalidOperationException>(
            () => BrowserService.PrepareScreenshotForModel(screenshot));

        Assert.Contains("readable size", error.Message);
        Assert.Contains("look/find", error.Message);
    }

    private static BrowserScreenshot ScreenshotFromBitmap(System.Drawing.Bitmap bitmap)
    {
        using var output = new MemoryStream();
        bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        return new BrowserScreenshot("tab-sized", "https://example.test/viewport",
            bitmap.Width, bitmap.Height, output.ToArray());
    }

    private static System.Drawing.Bitmap NoiseBitmap(int width, int height)
    {
        var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try
        {
            var pixels = new byte[data.Stride * height];
            new Random(42).NextBytes(pixels);
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }
#endif
}

[JsonSerializable(typeof(ToolResultObject))]
internal partial class BrowserScreenshotTestJsonContext : JsonSerializerContext;
