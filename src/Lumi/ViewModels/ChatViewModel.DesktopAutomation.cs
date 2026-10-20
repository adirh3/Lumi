using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Threading;
using GitHub.Copilot;
using Lumi.Services;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    private async Task<string> RunDesktopOperationAsync(Guid chatId, string action, Func<string> operation,
        string? title = null, int? elementId = null, CancellationToken cancellationToken = default)
    {
        var result = await Task.Run(operation, cancellationToken).ConfigureAwait(false);
        var (status, failed) = DesktopOperationStatus(result);
        await PublishDesktopOperationAsync(chatId, action, status, title, elementId, !failed).ConfigureAwait(false);
        return result;
    }

    private async Task PublishDesktopOperationAsync(Guid chatId, string action, string status,
        string? title, int? elementId, bool refreshImage)
    {
        UIAutomationWindowInfo? window;
        try
        {
            window = await Task.Run(() => _uiAutomation.GetWindowInfo(title, elementId)).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDesktopCaptureFailure(ex))
        {
            Trace.TraceInformation($"[DesktopPreview] Target metadata unavailable: {ex.Message}");
            return;
        }
        if (window is null) return;
        PublishDesktopPreview(new DesktopPreviewUpdate(chatId, window.WindowKey, window.Title,
            window.ProcessName, action, status, DateTimeOffset.UtcNow));
        var shouldCapture = refreshImage && await Dispatcher.UIThread.InvokeAsync(
            () => CurrentChat?.Id == chatId && HasVisibleDesktopPreviewHost);
        if (shouldCapture)
            await RefreshDesktopFrameAsync(chatId, window.WindowKey).ConfigureAwait(false);
    }

    private async Task RefreshDesktopFrameAsync(Guid chatId, string? title = null)
    {
        DesktopPreviewUpdate? origin = null;
        try
        {
            origin = await Dispatcher.UIThread.InvokeAsync(
                () => CurrentChat?.Id == chatId ? DesktopPreview : null);
            if (origin is null) return;
            if (title is not null && title != origin.WindowKey) return;
            var window = await Task.Run(() => _uiAutomation.GetWindowInfo(title ?? origin.WindowKey)).ConfigureAwait(false);
            if (window is null) return;
            var capture = await Task.Run(() => _uiAutomation.CaptureWindow(window.WindowKey, retainForClick: false)).ConfigureAwait(false);
            PublishDesktopRefresh(origin, origin with
            {
                WindowKey = capture.Window.WindowKey,
                WindowTitle = capture.Window.Title,
                ProcessName = capture.Window.ProcessName,
                NewFrame = new DesktopPreviewFrame(capture.PngBytes, capture.PixelWidth, capture.PixelHeight, capture.CapturedAt)
            });
        }
        catch (Exception ex) when (IsDesktopCaptureFailure(ex))
        {
            Trace.TraceWarning($"[DesktopPreview] Capture unavailable: {ex.Message}");
            if (origin is not null)
                PublishDesktopRefresh(origin, origin with
                {
                    StatusText = $"Preview unavailable: {ex.Message}",
                    NewFrame = null
                });
        }
    }

    internal void PublishDesktopRefresh(DesktopPreviewUpdate origin, DesktopPreviewUpdate update)
    {
        if (_isDisposed) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => PublishDesktopRefresh(origin, update));
            return;
        }
        if (ReferenceEquals(DesktopPreview, origin))
            PublishDesktopPreview(update);
    }

    private async Task<ToolResultAIContent> CaptureDesktopToolAsync(Guid chatId, string title, int maxWidth)
    {
        try
        {
            var capture = await Task.Run(() => _uiAutomation.CaptureWindow(title, maxWidth)).ConfigureAwait(false);
            PublishCapture(chatId, "Window screenshot", "Screenshot captured without focusing the window", capture);
            return CreateDesktopScreenshotContent(capture);
        }
        catch (Exception ex) when (IsDesktopCaptureFailure(ex))
        {
            Trace.TraceWarning($"[UIAutomation] Screenshot failed: {ex.Message}");
            await PublishDesktopOperationAsync(chatId, "Window screenshot", ex.Message, title, null, false).ConfigureAwait(false);
            return new ToolResultAIContent(new ToolResultObject
            {
                ResultType = "failure",
                Error = ex.Message,
                TextResultForLlm = $"Window screenshot failed: {ex.Message}"
            });
        }
    }

    private void PublishCapture(Guid chatId, string action, string status, UIAutomationCapture capture)
        => PublishDesktopPreview(new DesktopPreviewUpdate(chatId, capture.Window.WindowKey,
            capture.Window.Title, capture.Window.ProcessName, action, status, capture.CapturedAt,
            new DesktopPreviewFrame(capture.PngBytes, capture.PixelWidth, capture.PixelHeight, capture.CapturedAt)));

    internal static ToolResultAIContent CreateDesktopScreenshotContent(UIAutomationCapture capture)
        => new(new ToolResultObject
        {
            ResultType = "success",
            TextResultForLlm = capture.Describe(),
            SessionLog = $"Window screenshot: {capture.Window.Title} ({capture.PixelWidth}x{capture.PixelHeight})",
            BinaryResultsForLlm =
            [
                new ToolBinaryResult
                {
                    Type = ToolBinaryResultType.Image,
                    MimeType = "image/png",
                    Data = Convert.ToBase64String(capture.PngBytes.Span),
                    Description = $"Window '{capture.Window.Title}', captureId {capture.CaptureId}; coordinates are pixels in this image."
                }
            ]
        });

    private static bool IsDesktopCaptureFailure(Exception exception)
        => exception is InvalidOperationException or Win32Exception or ExternalException
            or ArgumentException or PlatformNotSupportedException;

    private static (string Status, bool Failed) DesktopOperationStatus(string result)
    {
        if (result.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(result);
                if (document.RootElement.TryGetProperty("success", out var success))
                {
                    if (!success.GetBoolean())
                        return (document.RootElement.TryGetProperty("error", out var error)
                            ? error.GetString() ?? "Automation did not complete" : "Automation did not complete", true);
                    if (document.RootElement.TryGetProperty("completedSteps", out var completed))
                        return ($"Completed {completed.GetInt32()} actions", false);
                }
            }
            catch (JsonException ex)
            {
                Trace.TraceWarning($"[DesktopPreview] Invalid operation result: {ex.Message}");
                return ("Could not read the automation result", true);
            }
        }
        var firstLine = result.Split('\n', 2)[0].Trim();
        if (firstLine.Length > 240) firstLine = firstLine[..240] + "...";
        return (firstLine, result.StartsWith("UI automation failed:", StringComparison.Ordinal));
    }
}
