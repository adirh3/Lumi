#if !WINDOWS
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Lumi.Services;

internal sealed partial class NativeBrowserTab
{
    private Task<string> RunOperationAsync(Func<Task<string>> operation) =>
        _operationGate.RunAsync(async () =>
        {
            ThrowIfDisposed();
            try { return await operation(); }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native browser action failed ({ex.GetType().Name}).");
                return $"Tab: {Id}\n" + BrowserActionResult.FromException(ex).ToDisplayText();
            }
        });

    internal Task<string> NavigateAsync(string url) =>
        RunOperationAsync(async () => (await NavigateCoreAsync(url)).ToDisplayText());

    private async Task<BrowserActionResult> NavigateCoreAsync(string url)
    {
        var uri = NativeBrowserLogic.ParseUrl(url);
        await EnsureInitializedAsync();
        await _actionLock.WaitAsync(_lifetime.Token);
        var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await BrowserService.OnUiThreadAsync(() =>
            {
                ThrowIfDisposed();
                _navigation = navigation;
                _view!.Navigate(uri);
            });
            var success = false;
            var deadline = Environment.TickCount64 + 18000;
            while (Environment.TickCount64 < deadline)
            {
                if (navigation.Task.IsCompleted)
                {
                    success = await navigation.Task;
                    break;
                }
                if (!string.IsNullOrEmpty(uri.Fragment))
                {
                    await BrowserService.OnUiThreadAsync(RefreshMetadataAsync);
                    if (Url == uri.AbsoluteUri)
                    {
                        success = true;
                        break;
                    }
                }
                await Task.Delay(100, _lifetime.Token);
            }
            if (!success)
                return BrowserActionResult.Failure("Navigation failed or timed out.");
            await BrowserService.OnUiThreadAsync(RefreshMetadataAsync);
            return BrowserActionResult.Success($"Navigated to {Url}. Page title: {Title}.");
        }
        finally
        {
            if (ReferenceEquals(_navigation, navigation))
                _navigation = null;
            _actionLock.Release();
        }
    }

    internal Task<string> OpenAndSnapshotAsync(string url) => RunOperationAsync(async () =>
    {
        var result = await NavigateCoreAsync(url);
        if (!result.Succeeded)
            return $"Tab: {Id}\n" + result.ToDisplayText();
        var ready = await WaitForContentSettleAsync();
        var snapshot = await LookCoreAsync();
        return ready.Succeeded ? snapshot :
            (ready.Pending ? ready.Message : ready.ToDisplayText()) + "\n\n" + snapshot;
    });

    internal Task<string> LookAsync(string? filter) => RunOperationAsync(() => LookCoreAsync(filter));

    private async Task<string> LookCoreAsync(string? filter = null) =>
        $"Tab: {Id}\n" + (await RunDomActionAsync("look", filter)).ToDisplayText();

    internal Task<string> FindElementsAsync(string query, int limit, bool preferDialog) =>
        RunOperationAsync(async () => $"Tab: {Id}\n" +
            (await RunDomActionAsync("find", query, limit: limit, preferDialog: preferDialog)).ToDisplayText());

    internal Task<string> PressKeyAsync(string key, string? selector) =>
        RunOperationAsync(async () => (await RunDomActionAsync("press", key, selector)).ToDisplayText());

    internal Task<string> WaitForAsync(string selector, int timeoutMs) =>
        RunOperationAsync(async () => (await WaitForDomElementAsync(selector, timeoutMs)).ToDisplayText());

    internal Task<string> ScrollAsync(string direction, int pixels) =>
        RunOperationAsync(() => ScrollCoreAsync(direction, pixels));

    private async Task<string> ScrollCoreAsync(string direction, int pixels)
    {
        await EnsureInitializedAsync();
        var delta = direction.Equals("up", StringComparison.OrdinalIgnoreCase) ? -pixels : pixels;
        await BrowserService.OnUiThreadAsync(() =>
            ExecuteScriptAsync($"window.scrollBy(0,{delta.ToString(System.Globalization.CultureInfo.InvariantCulture)})"));
        return $"Scrolled {direction} by {pixels}px";
    }

    internal Task<string> GoBackAsync() => RunOperationAsync(GoBackCoreAsync);

    private async Task<string> GoBackCoreAsync()
    {
        await EnsureInitializedAsync();
        await _actionLock.WaitAsync(_lifetime.Token);
        try
        {
            var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = false;
            await BrowserService.OnUiThreadAsync(() =>
            {
                if (!_view!.CanGoBack)
                    return;
                _navigation = navigation;
                started = _view.GoBack();
            });
            if (!started)
                return "Cannot go back - no previous page.";
            if (!await navigation.Task.WaitAsync(TimeSpan.FromSeconds(10), _lifetime.Token))
                return "Error: navigation back failed.";
            await BrowserService.OnUiThreadAsync(RefreshMetadataAsync);
            return $"Navigated back to: {Url}";
        }
        finally
        {
            _navigation = null;
            _actionLock.Release();
        }
    }

    internal Task<string> EvaluateAsync(string javascript) => RunOperationAsync(async () =>
    {
        await EnsureInitializedAsync();
        await _actionLock.WaitAsync(_lifetime.Token);
        try
        {
            var script = "(function(){try{var r=(function(){" + javascript + "})();" +
                "if(r===undefined)return '(undefined)';if(r===null)return '(null)';" +
                "if(typeof r==='object'){if(typeof r.then==='function')" +
                "return '(Promise returned - use .then() or callback pattern instead of await)';" +
                "try{return JSON.stringify(r,null,2)}catch(e){return String(r);}}" +
                "return String(r);}catch(e){return 'JS Error: '+e.message;}})()";
            var result = await BrowserService.OnUiThreadAsync(() => ExecuteScriptAsync(script));
            await BrowserService.OnUiThreadAsync(RefreshMetadataAsync);
            return NativeBrowserLogic.ReadDisplayResult(result);
        }
        finally { _actionLock.Release(); }
    });

    internal Task<string> DoAsync(string action, string? target, string? value) =>
        RunOperationAsync(async () =>
        {
            var requested = action.Trim().ToLowerInvariant();
            if (requested == "download")
                return $"Tab: {Id}\n" +
                    BrowserActionResult.Failure(NativeWebViewPlatform.NativeDownloadUnsupportedReason).ToDisplayText();
            if (requested == "steps")
                return $"Tab: {Id}\n" + await BrowserAutomationBatch.ExecuteAsync(
                    value, ExecuteActionAsync, () => LookCoreAsync());

            var quiet = false;
            if (requested is "click" or "press" or "scroll" or "clear" or "back")
            {
                quiet = string.Equals(value, "quiet", StringComparison.OrdinalIgnoreCase);
                if (target?.EndsWith(" quiet", StringComparison.OrdinalIgnoreCase) == true)
                {
                    quiet = true;
                    target = target[..^6].TrimEnd();
                }
                if (string.Equals(value, "quiet", StringComparison.OrdinalIgnoreCase))
                    value = null;
            }
            _lastNewWindowUrl = null;
            var result = await ExecuteActionAsync(new(requested, target, value));
            var output = $"Tab: {Id}\n" + result.ToDisplayText();
            if (!result.Succeeded)
                return output + (quiet ? "" : "\n\n" + await LookCoreAsync());
            if (quiet)
                return output + " (quiet - no snapshot)";
            if (requested is not ("click" or "type" or "press" or "select" or "back" or "clear" or "upload"))
                return output;
            if (_lastNewWindowUrl is { } popup)
                output += $"\n\nNew tab requested: {popup}. Observation below remains on the original tab.";
            return output + "\n\n" + await LookCoreAsync();
        });

    private async Task<BrowserActionResult> ExecuteActionAsync(BrowserAutomationStep step)
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
                result = BrowserActionResult.FromLegacy(await ScrollCoreAsync(target ?? "down",
                    int.TryParse(value, out var pixels) ? pixels : 500));
                break;
            case "back":
                result = BrowserActionResult.FromLegacy(await GoBackCoreAsync());
                break;
            case "download":
                return BrowserActionResult.Failure(NativeWebViewPlatform.NativeDownloadUnsupportedReason);
            case "upload":
                result = BrowserActionResult.FromLegacy(await UploadFileCoreAsync(target, value));
                break;
            default:
                return BrowserActionResult.Failure("Unknown or unsupported browser action.");
        }
        if (!result.Succeeded || action is "read_form" or "wait" or "download")
            return result;
        var ready = await WaitForContentSettleAsync(2500);
        if (!ready.Succeeded && !ready.Pending)
            return BrowserActionResult.Failure(result.Message + "\nAction executed; " +
                ready.Message + " It was not retried.");
        if (action is "type" or "select" or "fill" or "clear")
        {
            var validation = await RunDomActionAsync("validate_edits");
            if (!validation.Succeeded)
                return BrowserActionResult.Failure(result.Message + "\n" + validation.Message);
        }
        return ready.Pending
            ? BrowserActionResult.Success(result.Message + "\nThe action completed and was not retried.\n" + ready.Message)
            : result;
    }

    private async Task<BrowserActionResult> RunDomActionAsync(
        string operation, string? target = null, string? value = null, int limit = 50, bool preferDialog = true)
    {
        await EnsureInitializedAsync();
        await _actionLock.WaitAsync(_lifetime.Token);
        try
        {
            if (operation == "ready" && _isNavigating)
                return new(false, "The page is navigating.", Pending: true);
            if (operation is "click" or "type" or "clear" or "select")
            {
                var deadline = Environment.TickCount64 + 2500;
                while (true)
                {
                    var ready = await ExecuteDomScriptAsync("probe", target, operation);
                    if (ready.Succeeded)
                        break;
                    if (!ready.Pending)
                        return ready;
                    if (Environment.TickCount64 >= deadline)
                        return BrowserActionResult.Failure("Timeout waiting for a matching visible target; no action was executed.");
                    await Task.Delay(100, _lifetime.Token);
                }
            }
            var result = await ExecuteDomScriptAsync(operation, target, value, limit, preferDialog);
            if (operation == "select" && result.Pending)
            {
                var deadline = Environment.TickCount64 + 2500;
                do
                {
                    result = await ExecuteDomScriptAsync("select_option", target, value);
                    if (!result.Pending)
                        break;
                    await Task.Delay(100, _lifetime.Token);
                } while (Environment.TickCount64 < deadline);
                if (result.Pending)
                    return BrowserActionResult.Failure("The dropdown opened, but the requested option did not appear before timeout.");
            }
            if (!_isNavigating)
                await BrowserService.OnUiThreadAsync(RefreshMetadataAsync);
            return result;
        }
        finally { _actionLock.Release(); }
    }

    private Task<BrowserActionResult> ExecuteDomScriptAsync(
        string operation, string? target = null, string? value = null, int limit = 50, bool preferDialog = true) =>
        BrowserService.OnUiThreadAsync(async () => BrowserActionResult.FromScript(
            await ExecuteScriptAsync(BrowserDomScript.Build(operation, target, value, limit, preferDialog))));

    private async Task<BrowserActionResult> WaitForDomElementAsync(string target, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + Math.Clamp(timeoutMs, 0, 30000);
        do
        {
            var result = await RunDomActionAsync("wait", target);
            if (!result.Pending)
                return result;
            if (Environment.TickCount64 >= deadline)
                break;
            await Task.Delay(100, _lifetime.Token);
        } while (true);
        return BrowserActionResult.Failure("Timeout waiting for a matching visible element.");
    }

    private async Task<BrowserActionResult> WaitForContentSettleAsync(int maxWaitMs = 4000)
    {
        var start = Environment.TickCount64;
        var deadline = start + maxWaitMs;
        do
        {
            var ready = await RunDomActionAsync("ready");
            if (!ready.Succeeded && !ready.Pending)
                return ready;
            if (ready.Succeeded && Environment.TickCount64 - start >= 750)
                return ready;
            if (Environment.TickCount64 >= deadline)
                break;
            await Task.Delay(100, _lifetime.Token);
        } while (true);
        return new(false,
            "Observation note: page settling reached its time limit; the latest observation may still be changing. " +
            "Use wait for the next expected element before continuing.", Pending: true);
    }

}
#endif
