#if WINDOWS
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Lumi.Services;

public sealed partial class BrowserService
{
    private readonly BrowserOperationGate _operationGate = new();

    private Task<string> RunOperationAsync(Func<Task<string>> operation) =>
        _operationGate.RunAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return operation();
        });

    private async Task<BrowserActionResult> RunDomActionAsync(
        string operation, string? target = null, string? value = null, int limit = 50, bool preferDialog = true)
    {
        long targetWaitMs = 0;
        try
        {
            await EnsureInitializedAsync();
            await WaitForActionLockAsync();
            try
            {
                if (operation is "click" or "type" or "clear" or "select")
                {
                    var targetTimer = Stopwatch.StartNew();
                    var targetDeadline = Environment.TickCount64 + 2500;
                    while (true)
                    {
                        var targetState = await ExecuteDomScriptAsync("probe", target, operation);
                        if (targetState.Succeeded) break;
                        if (!targetState.Pending)
                            return targetState with { TargetWaitMs = targetTimer.ElapsedMilliseconds };
                        if (Environment.TickCount64 >= targetDeadline)
                            return BrowserActionResult.Failure("Timeout waiting for a visible, enabled target; no action was executed.")
                                with { TargetWaitMs = targetTimer.ElapsedMilliseconds };
                        await Task.Delay(100);
                    }
                    targetWaitMs = targetTimer.ElapsedMilliseconds;
                }
                var result = await ExecuteDomScriptAsync(operation, target, value, limit, preferDialog);
                if (operation != "select" || !result.Pending)
                    return result with { TargetWaitMs = targetWaitMs };

                // Opening the dropdown is a side effect: only poll for the option, never re-click the opener.
                var optionTimer = Stopwatch.StartNew();
                var deadline = Environment.TickCount64 + 2500;
                do
                {
                    result = await ExecuteDomScriptAsync("select_option", target, value);
                    if (!result.Pending)
                        return result with { TargetWaitMs = targetWaitMs + optionTimer.ElapsedMilliseconds };
                    await Task.Delay(100);
                } while (Environment.TickCount64 < deadline);
                return BrowserActionResult.Failure("The dropdown opened, but the requested option did not appear before timeout.")
                    with { TargetWaitMs = targetWaitMs + optionTimer.ElapsedMilliseconds };
            }
            finally { _actionLock.Release(); }
        }
        catch (Exception ex) { return BrowserActionResult.FromException(ex); }
    }

    private async Task<BrowserActionResult> ExecuteDomScriptAsync(
        string operation, string? target = null, string? value = null, int limit = 50, bool preferDialog = true)
    {
        var json = await InvokeOnUiThreadAsync(() =>
        {
            if (_isNavigating && operation is "probe" or "ready" or "wait")
                return Task.FromResult("""{"ok":false,"message":"The document is navigating.","pending":true}""");
            var script = BrowserDomScript.Build(operation, target, value, limit, preferDialog);
            return _webView!.ExecuteScriptAsync(script);
        });
        return BrowserActionResult.FromScript(json);
    }

    private async Task<BrowserActionResult> WaitForDomElementAsync(string target, int timeoutMs)
    {
        var started = Environment.TickCount64;
        var deadline = started + Math.Clamp(timeoutMs, 0, 30000);
        do
        {
            var result = await RunDomActionAsync("wait", target);
            if (!result.Pending)
                return result with { TargetWaitMs = Environment.TickCount64 - started };
            if (Environment.TickCount64 >= deadline) break;
            await Task.Delay(100);
        } while (true);
        return BrowserActionResult.Failure("Timeout waiting for a visible, enabled target.")
            with { TargetWaitMs = Environment.TickCount64 - started };
    }

    private async Task<BrowserActionResult> WaitForDocumentReadyAsync(int maxWaitMs = 2500, int pollMs = 100)
    {
        var started = Environment.TickCount64;
        var deadline = started + maxWaitMs;
        do
        {
            var result = await RunDomActionAsync("ready");
            if (!result.Pending)
                return result with { ElapsedMs = Environment.TickCount64 - started };
            if (Environment.TickCount64 >= deadline) break;
            await Task.Delay(pollMs);
        } while (true);
        return new(false,
            $"Observation: document readiness reached its {Environment.TickCount64 - started} ms limit; the snapshot may still change. " +
            "Completed actions were not retried. The next action waits for its own target.",
            Pending: true, ElapsedMs: Environment.TickCount64 - started);
    }

    private async Task<BrowserActionResult> ExecuteActionAsync(BrowserAutomationStep step)
    {
        var timer = Stopwatch.StartNew();
        var result = await ExecuteActionCoreAsync(step);
        var action = BrowserAutomationBatch.Supports(step.Action) || step.Action == "download" ? step.Action : "unsupported";
        Debug.WriteLine($"Browser action={action}; target_ms={result.TargetWaitMs}; elapsed_ms={timer.ElapsedMilliseconds}; success={result.Succeeded}");
        return result with { ElapsedMs = timer.ElapsedMilliseconds };
    }

    private async Task<BrowserActionResult> ExecuteActionCoreAsync(BrowserAutomationStep step)
    {
        try
        {
            var (action, target, value) = step;
            if (action is "click" or "press" or "scroll" or "clear" or "back")
            {
                if (target?.EndsWith(" quiet", StringComparison.OrdinalIgnoreCase) == true)
                    target = target[..^6].TrimEnd();
                if (string.Equals(value, "quiet", StringComparison.OrdinalIgnoreCase))
                    value = null;
            }
            if (action is "click" or "type" or "select" or "clear" && string.IsNullOrWhiteSpace(target))
                return BrowserActionResult.Failure("This action needs a target.");
            if (action is "type" or "select" or "fill" && value is null)
                return BrowserActionResult.Failure("This action needs a value.");
            if (action == "scroll" && target is not (null or "up" or "down"))
                return BrowserActionResult.Failure("Scroll direction must be up or down.");

            BrowserActionResult result;
            switch (action)
            {
                case "click": case "type": case "select": case "clear": case "fill": case "read_form":
                    result = await RunDomActionAsync(action, target, value);
                    break;
                case "press":
                    result = await RunDomActionAsync("press", target ?? "Enter");
                    break;
                case "wait":
                    result = await WaitForDomElementAsync(target ?? "body", int.TryParse(value, out var ms) ? ms : 10000);
                    break;
                case "scroll":
                    result = BrowserActionResult.FromLegacy(await ScrollCoreAsync(target ?? "down", int.TryParse(value, out var px) ? px : 500));
                    break;
                case "back":
                    result = BrowserActionResult.FromLegacy(await GoBackCoreAsync());
                    break;
                case "download":
                    result = BrowserActionResult.FromLegacy(await WaitForDownloadAsync(target));
                    break;
                case "upload":
                    result = BrowserActionResult.FromLegacy(await UploadFileCoreAsync(target, value));
                    break;
                default:
                    return BrowserActionResult.Failure("Unknown or unsupported browser action.");
            }

            if (!result.Succeeded || action is "read_form" or "wait" or "download")
                return result;
            if (action is "type" or "select" or "fill" or "clear")
            {
                var validation = await RunDomActionAsync("validate_edits", value: action);
                if (!validation.Succeeded)
                    return BrowserActionResult.Failure(action == "fill" ? validation.Message : result.Message + "\n" + validation.Message)
                        with { TargetWaitMs = result.TargetWaitMs };
            }
            return result;
        }
        catch (Exception ex) { return BrowserActionResult.FromException(ex); }
    }
}
#endif
