#if WINDOWS
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Lumi.Services;
using Microsoft.Web.WebView2.Core;
using Xunit;
using Xunit.Abstractions;

namespace Lumi.Tests;

// Use a REAL Win32 platform and dedicated STA dispatcher (HeadlessUnitTestSession is MTA).
// Headless HWNDs cannot host WebView2. No production App, Copilot, profile or network is used.
public sealed class BrowserNativeTestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<BrowserNativeTestApp>().UsePlatformDetect();
}

[Collection("Headless UI")]
public sealed class BrowserServiceIntegrationTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task RealWebView2PreservesReferencesSafetyTabsAndNativeLifecycle()
    {
        Skip.IfNot(Environment.UserInteractive, "Real WebView2 requires an interactive Windows desktop.");
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Skip.If(true, "WebView2 runtime is not installed.");
        }

        var root = Path.Combine(Path.GetTempPath(), "Lumi-browser-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var page = Path.Combine(root, "fixture.html");
        await File.WriteAllTextAsync(page, FixtureHtml);
        var url = new Uri(page).AbsoluteUri;
        Exception? failure = null;
        var completed = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        await RunOnNativeStaAsync(async () =>
        {
            Window? window = null;
            BrowserService? browser = null;
            try
            {
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                window = new Window { Title = "Lumi isolated WebView2 regression tests", Width = 960, Height = 760 };
                window.Show();
                browser = new BrowserService(Path.Combine(root, "profile"));
                browser.SetParentHwnd(window.TryGetPlatformHandle()!.Handle);
                browser.SetBounds(0, 0, 940, 700);
                browser.SetControllerVisible(true);
                await browser.OpenAndSnapshotAsync(url);
                browser.WebView!.Profile.DefaultDownloadFolderPath = root;

                await VerifyReferences(browser, url);
                output.WriteLine("PASS: filtered/ranked/limited identities, dialog order, replacement/navigation stale references.");
                await browser.OpenAndSnapshotAsync(url);
                await VerifyFailStopAndRedaction(browser);
                output.WriteLine("PASS: failed steps/partial fill never submit, ordinary values survive, passwords are redacted.");
                await browser.OpenAndSnapshotAsync(url);
                await VerifyTabsAndPopup(browser, url);
                output.WriteLine("PASS: new/list/switch/close preserve forms, references reject wrong tab, popup retains opener.");
                await VerifyCaptureAndDownloads(browser, url, root);
                output.WriteLine("PASS: real PNG dimensions/pixels, hidden-tab failure, tab-specific downloads.");
                await browser.DisposeAsync();
                await browser.DisposeAsync();
                browser.SetControllerVisible(true);
                browser.SetBounds(0, 0, 940, 700);
                Assert.False(browser.HasController);
                Assert.Empty(browser.Tabs);
                await Assert.ThrowsAsync<ObjectDisposedException>(() => browser.CaptureScreenshotAsync());
                output.WriteLine("PASS: native controller disposal is idempotent and capture fails after dispose.");
                completed = true;
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (browser is not null) await browser.DisposeAsync();
                window?.Close();
            }
        }, timeout.Token);

        // WebView2 may retain profile file locks briefly after controller.Close().
        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(completed, "Native test body did not complete.");
    }

    private static Task RunOnNativeStaAsync(Func<Task> body, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? error = null;
            var bodyCompleted = false;
            // Same scoped-locator/reset isolation as Avalonia's unit-test session, but unlike its
            // worker thread this one is STA. The collection excludes concurrent Avalonia tests.
            var reset = typeof(Dispatcher).GetMethod("ResetForUnitTests",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            var enterScope = typeof(AvaloniaLocator).GetMethod("EnterScope",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!;
            using ((IDisposable)enterScope.Invoke(null, null)!)
            {
                try
                {
                    reset.Invoke(null, null);
                    BrowserNativeTestApp.BuildAvaloniaApp().SetupWithoutStarting();
                    var dispatcher = Dispatcher.UIThread;
                    dispatcher.Post(async () =>
                    {
                        try { await body(); }
                        catch (Exception ex) { error = ex; }
                        finally
                        {
                            bodyCompleted = true;
                            dispatcher.InvokeShutdown();
                        }
                    });
                    dispatcher.MainLoop(cancellationToken);
                }
                catch (Exception ex) { error = ex; }
                finally
                {
                    try { reset.Invoke(null, null); }
                    catch (Exception ex) { error ??= ex; }
                }
            }
            // Complete only AFTER the native loop and scoped state are torn down.
            if (error is not null) completion.TrySetException(error);
            else if (!bodyCompleted) completion.TrySetCanceled(cancellationToken);
            else completion.TrySetResult();
        }) { IsBackground = true, Name = "Lumi browser integration STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static async Task VerifyReferences(BrowserService browser, string url)
    {
        var all = await browser.LookAsync();
        var original = Reference(all, "name=\"alpha\"");
        var filtered = await browser.LookAsync("alpha");
        Assert.Equal(original, Reference(filtered, "name=\"alpha\""));
        var ranked = await browser.FindElementsAsync("alpha", 1);
        Assert.Equal(original, Reference(ranked, "name=\"alpha\""));
        Assert.Single(Regex.Matches(ranked, @"(?m)^\[\d+\]"));
        await browser.DoAsync("type", original, "identity retained");
        Assert.Equal("identity retained", await EvaluateString(browser, "document.getElementById('alpha').value"));

        // Opening a dialog changes ordering, not any existing node's identity.
        await browser.EvaluateAsync("document.getElementById('dialog').show()");
        var dialog = await browser.LookAsync();
        Assert.Equal(original, Reference(dialog, "name=\"alpha\""));
        Assert.True(dialog.IndexOf("name=\"dialog-action\"", StringComparison.Ordinal) <
                    dialog.IndexOf("name=\"alpha\"", StringComparison.Ordinal), dialog);
        await browser.EvaluateAsync("document.getElementById('dialog').close()");

        // Replacing with identical markup must not silently retarget an old numeric identity.
        await browser.EvaluateAsync("const old = document.getElementById('alpha'); old.replaceWith(old.cloneNode(true))");
        AssertError(await browser.DoAsync("type", original, "wrong replacement"));
        Assert.Equal("identity retained", await EvaluateString(browser, "document.getElementById('alpha').value"));
        var replacement = Reference(await browser.LookAsync(), "name=\"alpha\"");
        Assert.NotEqual(original, replacement);
        await browser.OpenAndSnapshotAsync(url + "?navigated=1");
        AssertError(await browser.DoAsync("type", replacement, "wrong document"));
        Assert.Equal("ordinary seed", await EvaluateString(browser, "document.getElementById('alpha').value"));
    }

    private static async Task VerifyFailStopAndRedaction(BrowserService browser)
    {
        var failedBatch = await browser.DoAsync("steps", value: """
            [{"action":"type","target":"#does-not-exist","value":"missing"},
             {"action":"click","target":"#submit"}]
            """);
        AssertError(failedBatch);
        Assert.Equal("0", await EvaluateString(browser, "String(window.submits)"));
        var partialFill = await browser.DoAsync("steps", value: """
            [{"action":"fill","value":"{\"#alpha\":\"ordinary edited\",\"#locked\":\"cannot edit\"}"},
             {"action":"click","target":"#submit"}]
            """);
        AssertError(partialFill);
        Assert.Equal("0", await EvaluateString(browser, "String(window.submits)"));
        Assert.Equal("ordinary edited", await EvaluateString(browser, "document.getElementById('alpha').value"));

        const string secret = "private-password-78123";
        var typed = await browser.DoAsync("type", "#password", secret);
        var form = await browser.DoAsync("read_form");
        var look = await browser.LookAsync();
        var found = await browser.FindElementsAsync("password", 5);
        foreach (var text in new[] { typed, form, look, found })
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.Contains("ordinary edited", form);
        Assert.Contains("redacted", form, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(secret, await EvaluateString(browser, "document.getElementById('password').value"));
    }

    private static async Task VerifyTabsAndPopup(BrowserService browser, string url)
    {
        var first = browser.ActiveTabId;
        var firstRef = Reference(await browser.LookAsync(), "name=\"alpha\"");
        await browser.DoAsync("type", firstRef, "tab one retained");
        await browser.ManageTabsAsync("new");
        Assert.StartsWith("Navigated to ", await browser.NavigateAsync(url));
        Assert.Equal(url, browser.CurrentUrl);
        var second = browser.ActiveTabId;
        Assert.NotEqual(first, second);
        Assert.Equal(2, browser.Tabs.Count);
        Assert.Contains(first, await browser.ManageTabsAsync("list"));
        AssertError(await browser.DoAsync("type", firstRef, "wrong tab"));
        await browser.DoAsync("type", "#alpha", "tab two retained");
        await browser.ManageTabsAsync("switch", first);
        Assert.Equal("tab one retained", await EvaluateString(browser, "document.getElementById('alpha').value"));
        await browser.ManageTabsAsync("switch", second);
        Assert.Equal("tab two retained", await EvaluateString(browser, "document.getElementById('alpha').value"));
        var secondWebView = browser.WebView!;
        var pinnedBatch = browser.DoAsync("steps", value: """
            [{"action":"wait","target":"#arriving","value":"5000"},
             {"action":"type","target":"#alpha","value":"pinned to tab two"}]
            """);
        await browser.ManageTabsAsync("switch", first);
        await secondWebView.ExecuteScriptAsync(
            "const arriving = document.createElement('button'); arriving.id='arriving'; arriving.textContent='Ready'; document.body.append(arriving)");
        Assert.DoesNotContain("Error:", await pinnedBatch);
        Assert.Equal("tab one retained", await EvaluateString(browser, "document.getElementById('alpha').value"));
        await browser.ManageTabsAsync("switch", second);
        Assert.Equal("pinned to tab two", await EvaluateString(browser, "document.getElementById('alpha').value"));
        var interruptedBatch = browser.DoAsync("steps", value: """
            [{"action":"wait","target":"#never-arrives","value":"5000"},
             {"action":"click","target":"#submit"}]
            """);
        await browser.ManageTabsAsync("close", second);
        AssertError(await interruptedBatch);
        Assert.Equal(first, browser.ActiveTabId);
        Assert.Equal("tab one retained", await EvaluateString(browser, "document.getElementById('alpha').value"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => browser.ManageTabsAsync("switch", second));

        // A related WebView is required: navigating a normal independent tab is insufficient.
        await browser.DoAsync("click", "#popup quiet");
        await UntilAsync(() => browser.Tabs.Count == 2 && browser.ActiveTabId != first);
        Assert.Equal("true", await EvaluateString(browser, "String(window.opener !== null)"));
        await browser.EvaluateAsync("window.opener.postMessage('popup-related', '*')");
        var popup = browser.ActiveTabId;
        await browser.ManageTabsAsync("switch", first);
        await UntilAsync(async () => await EvaluateString(browser, "window.popupReply || ''") == "popup-related");
        await browser.ManageTabsAsync("switch", popup);
        await browser.EvaluateAsync("window.close()");
        await UntilAsync(() => browser.Tabs.Count == 1 && browser.ActiveTabId == first);
    }

    private static async Task VerifyCaptureAndDownloads(BrowserService browser, string url, string root)
    {
        var first = browser.ActiveTabId;
        var image = await browser.CaptureScreenshotAsync();
        Assert.Equal(first, image.TabId);
        Assert.True(image.Width > 100 && image.Height > 100);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, image.PngBytes[..8]);
        using (var stream = new MemoryStream(image.PngBytes))
        using (var bitmap = new System.Drawing.Bitmap(stream))
        {
            Assert.Equal(image.Width, bitmap.Width);
            Assert.Equal(image.Height, bitmap.Height);
            // The fixture paints a solid magenta marker. This detects a blank/fabricated PNG.
            var pixel = bitmap.GetPixel(10, 10);
            Assert.True(pixel.R > 180 && pixel.B > 130 && pixel.G < 100, $"Unexpected screenshot pixel: {pixel}");
        }

        await browser.ManageTabsAsync("new", url: url);
        var second = browser.ActiveTabId;
        var hidden = await Assert.ThrowsAsync<InvalidOperationException>(() => browser.CaptureScreenshotAsync(first));
        Assert.Contains("hidden", hidden.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(second, browser.ActiveTabId);
        await browser.ManageTabsAsync("switch", first);
        browser.WebView!.DownloadStarting += (_, args) => args.Handled = true;
        await browser.DoAsync("click", "#download quiet");
        var download = await browser.DoAsync("download", "*.txt");
        Assert.Contains("lumi-isolated-download.txt", download);
        await UntilAsync(() => File.Exists(Path.Combine(root, "lumi-isolated-download.txt")));
        Assert.Equal("isolated download contents", await File.ReadAllTextAsync(Path.Combine(root, "lumi-isolated-download.txt")));
        await browser.ManageTabsAsync("switch", second);
        var otherTab = await browser.DoAsync("download", "*.txt");
        Assert.DoesNotContain(Path.Combine(root, "lumi-isolated-download.txt"), otherTab);
        await browser.ManageTabsAsync("close", second);
        await browser.ManageTabsAsync("close", first);
        var blank = Assert.Single(browser.Tabs);
        Assert.NotEqual(first, blank.Id);
        Assert.Equal("about:blank", blank.Url);
        Assert.True(blank.IsActive);
        Assert.True(browser.HasController);
    }

    private static string Reference(string result, string marker)
    {
        var line = result.Split('\n').FirstOrDefault(line => line.Contains(marker, StringComparison.Ordinal));
        Assert.True(line is not null, $"No element containing '{marker}' in:\n{result}");
        var match = Regex.Match(line!, @"\[(\d+)\]");
        Assert.True(match.Success, line);
        return match.Groups[1].Value;
    }

    private static void AssertError(string result) =>
        Assert.Contains("Error", result, StringComparison.OrdinalIgnoreCase);

    private static async Task<string> EvaluateString(BrowserService browser, string expression) =>
        JsonSerializer.Deserialize<string>(await browser.WebView!.ExecuteScriptAsync(expression))!;

    private static Task UntilAsync(Func<bool> condition) => UntilAsync(() => Task.FromResult(condition()));

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = Environment.TickCount64 + 10000;
        while (!await condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "Timed out waiting for native browser state.");
            await Task.Delay(50);
        }
    }

    // In inactive #if blocks, lines starting with '#' are still parsed as C# directives.
    private const string FixtureHtml = """
        <!doctype html><html><head><meta charset="utf-8"><title>Lumi isolated browser fixture</title>
        <style>body{margin:0;background:#fff;color:#111;font:16px sans-serif}
        div#marker{width:100%;height:35px;background:#e000d0}main{padding:16px}
        input,button{margin:8px;padding:8px}dialog{position:fixed;top:350px}</style></head>
        <body><div id="marker"></div><main><h1>Browser regression fixture</h1>
        <form onsubmit="event.preventDefault();window.submits++">
        <label>Alpha<input id="alpha" name="alpha" value="ordinary seed"></label>
        <label>Password<input id="password" type="password"></label>
        <input id="locked" value="read only" readonly>
        <button id="submit" type="submit">Submit fixture</button></form>
        <button id="alpha-help" type="button">Alpha help</button>
        <button id="popup" type="button" onclick="window.open('about:blank','related-popup')">Open related popup</button>
        <button id="download" type="button" onclick="downloadFixture()">Download isolated file</button>
        <dialog id="dialog"><button id="dialog-action" type="button">Dialog action</button></dialog>
        </main><script>
        window.submits=0;window.popupReply='';
        window.addEventListener('message', e => window.popupReply=e.data);
        function downloadFixture(){
          const a=document.createElement('a');
          a.href=URL.createObjectURL(new Blob(['isolated download contents'],{type:'text/plain'}));
          a.download='lumi-isolated-download.txt';a.click();
        }
        </script></body></html>
        """;
}
#endif
