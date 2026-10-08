#if WINDOWS
using System.Diagnostics;
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
                var coldOpenTimer = Stopwatch.StartNew();
                var opened = await browser.OpenAndSnapshotAsync(url);
                output.WriteLine($"BENCH cold open/profile initialization: {coldOpenTimer.ElapsedMilliseconds} ms.");
                Assert.Contains("Navigation: completed.", opened);
                Assert.Contains("Readiness: DOM available", opened);
                browser.WebView!.Profile.DefaultDownloadFolderPath = root;

                await VerifyRoutineActionEfficiency(browser);
                output.WriteLine("PASS: ready/continuously updating four-step batches and quiet clicks stay within the regression budget.");
                await VerifyLegacyDropdownCompatibility(browser);
                output.WriteLine("PASS: legacy dropdown openers/options, natural/numeric targets, controlled option scoping, and no submit mis-targeting.");
                await VerifyCoordinatedFillValidation(browser);
                output.WriteLine("PASS: coordinated fields validate their final state while invalid fills and lost values block submit.");
                await VerifyPageLevelFormErrors(browser);
                output.WriteLine("PASS: visible page-level errors are reported once, hidden errors omitted, and password text redacted.");
                await VerifyTargetReadinessAndForms(browser);
                output.WriteLine("PASS: dynamic radio/custom-select/fill/submit batches wait for their targets, disabled/invalid fields stop, and submit runs once.");
                await VerifyBackgroundJavaScriptAndUploads(browser, root);
                output.WriteLine("PASS: bounded async JavaScript, hidden-page timers/DOM waits, explicit failures, and multi-file input/change delivery.");
                await VerifyNavigationAndQueuedValidation(browser, url, root);
                output.WriteLine("PASS: navigation/loading phases, target waits without another look, failed-navigation status, and queued framework edit rejection.");
                await VerifyBlurNotifications(browser);
                output.WriteLine("PASS: unfocused edits notify blur validation once, commit model state, and preserve page-managed focus.");
                await VerifyKeyboardAndClickTargets(browser);
                output.WriteLine("PASS: separate/batched type-Enter retains focus and submits; natural clicks prefer Search buttons; explicit field targets remain valid.");
                await VerifyMultilineEdits(browser);
                output.WriteLine("PASS: LF/CRLF textarea type/fill retain normalized values and continue batches; genuine reset/replacement edits still stop.");
                await VerifyActionSettling(browser);
                output.WriteLine("PASS: explicit expected-state waits capture delayed results, live updates do not hold actions, and clicks are never retried.");
                await browser.OpenAndSnapshotAsync(url);
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
            // The batch runner gives this STA fixture a fresh host: a prior headless dispatcher
            // cannot be reset here. The collection also excludes concurrent Avalonia tests.
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

    private async Task VerifyBlurNotifications(BrowserService browser)
    {
        Assert.Equal("false", await EvaluateString(browser, "String(document.hasFocus())"));
        var result = await browser.DoAsync("steps", value: """
            [{"action":"type","target":"#query","value":"validated query"},
             {"action":"click","target":"#commit-validation"}]
            """);
        output.WriteLine(result);
        Assert.Equal("1", await EvaluateString(browser, "String(window.queryBlurs)"));
        Assert.Equal("validated query", await EvaluateString(browser, "window.validatedQuery"));
        Assert.Contains("Completed 2 of 2 steps", result);
        Assert.Equal("1", await EvaluateString(browser, "String(window.queryConfirmations)"));
        Assert.Equal("false", await EvaluateString(browser, "String(document.hasFocus())"));

        var filled = await browser.DoAsync("steps", value: """
            [{"action":"fill","value":"{\"#query\":\"filled query\"}"},
             {"action":"click","target":"#commit-validation"}]
            """);
        Assert.Contains("Completed 2 of 2 steps", filled);
        Assert.Equal("2", await EvaluateString(browser, "String(window.queryBlurs)"));
        Assert.Equal("filled query", await EvaluateString(browser, "window.validatedQuery"));
        Assert.Equal("2", await EvaluateString(browser, "String(window.queryConfirmations)"));

        await browser.EvaluateAsync("""
            document.getElementById('query').addEventListener('blur', () => document.getElementById('alpha').focus(), {once:true})
            """);
        Assert.DoesNotContain("Error:", await browser.DoAsync("type", "#query", "redirected query"));
        Assert.Equal("alpha", await EvaluateString(browser, "document.activeElement.id"));
        Assert.Equal("3", await EvaluateString(browser, "String(window.queryBlurs)"));
        Assert.Equal("redirected query", await EvaluateString(browser, "window.validatedQuery"));

        // Exercise the already-notified branch without activating the host window.
        await browser.EvaluateAsync("""
            document.getElementById('query').blur = function() {
                HTMLElement.prototype.blur.call(this);
                this.dispatchEvent(new FocusEvent('blur'));
            }
            """);
        Assert.DoesNotContain("Error:", await browser.DoAsync("type", "#query", "already notified"));
        Assert.Equal("4", await EvaluateString(browser, "String(window.queryBlurs)"));
        Assert.Equal("already notified", await EvaluateString(browser, "window.validatedQuery"));
        await browser.EvaluateAsync("""
            delete document.getElementById('query').blur;
            document.getElementById('query').addEventListener('blur', function() { this.value='rejected'; }, {once:true})
            """);
        var rejected = await browser.DoAsync("steps", value: """
            [{"action":"type","target":"#query","value":"must not be accepted"},
             {"action":"click","target":"#commit-validation"}]
            """);
        AssertError(rejected);
        Assert.Equal("5", await EvaluateString(browser, "String(window.queryBlurs)"));
        Assert.Equal("2", await EvaluateString(browser, "String(window.queryConfirmations)"));
        Assert.Equal("false", await EvaluateString(browser, "String(document.hasFocus())"));
        await browser.EvaluateAsync("window.queryBlurs=0");
        output.WriteLine("Actual blur listeners committed type/fill values exactly once, enabled confirmation, redirected logical focus, and rejected edits without activating the document. Already-delivered event simulation did not duplicate notification.");
    }

    private async Task VerifyRoutineActionEfficiency(BrowserService browser)
    {
        const string steps = """
            [{"action":"click","target":"#tick"},{"action":"click","target":"#followup"},
             {"action":"click","target":"#tick"},{"action":"click","target":"#followup"}]
            """;
        var ready = new List<long>();
        var updating = new List<long>();
        var responseBytes = new List<int>();
        foreach (var timings in new[] { ready, updating })
        {
            if (ReferenceEquals(timings, updating))
                await browser.EvaluateAsync("""
                    document.body.insertAdjacentHTML('beforeend', '<span id="telemetry" role="progressbar">Background telemetry</span>');
                    window.efficiencyNoise = setInterval(() => document.getElementById('live-result').textContent = String(performance.now()), 50);
                    """);
            for (var i = 0; i < 3; i++)
            {
                var timer = Stopwatch.StartNew();
                var result = await browser.DoAsync("steps", value: steps);
                timings.Add(timer.ElapsedMilliseconds);
                responseBytes.Add(System.Text.Encoding.UTF8.GetByteCount(result));
                Assert.Contains("Completed 4 of 4 steps", result);
                Assert.DoesNotContain("Error:", result);
                Assert.Single(Regex.Matches(result, @"(?m)^Page: "));
            }
            var sorted = timings.Order().ToArray();
            output.WriteLine($"BENCH {(ReferenceEquals(timings, ready) ? "ready" : "updating")} four-step batch (n=3): " +
                $"p50={sorted[1]} ms; p95={sorted[2]} ms; samples={string.Join(",", timings)}.");
        }
        var quietTimer = Stopwatch.StartNew();
        var quiet = await browser.DoAsync("click", "#tick quiet");
        var quietMs = quietTimer.ElapsedMilliseconds;
        output.WriteLine($"BENCH updating quiet click: {quietMs} ms; bytes={System.Text.Encoding.UTF8.GetByteCount(quiet)}.");
        output.WriteLine($"BENCH four-step response bytes: {string.Join(",", responseBytes)}.");
        await browser.EvaluateAsync("""
            clearInterval(window.efficiencyNoise); document.getElementById('telemetry').remove();
            window.tickClicks=0; window.followups=0;
            """);

        Assert.DoesNotContain("Page: ", quiet);
        Assert.True(ready.Max() < 1500, $"Already-ready four-step batch took {ready.Max()} ms (budget <1500 ms).");
        Assert.True(updating.Max() < 1500, $"Unrelated live content delayed a four-step batch to {updating.Max()} ms (budget <1500 ms).");
        Assert.True(quietMs < 750, $"Quiet click waited {quietMs} ms (budget <750 ms).");
    }

    private async Task VerifyTargetReadinessAndForms(BrowserService browser)
    {
        await browser.EvaluateAsync("""
            document.body.insertAdjacentHTML('beforeend', `
                <form id="dynamic-form">
                  <label>Branch<input id="dynamic-branch" type="radio" name="branch"></label>
                  <div id="branch-fields" hidden>
                    <button id="dynamic-model" type="button" role="combobox" aria-controls="model-options">Choose model</button>
                    <div id="model-options" role="listbox" hidden><button type="button" role="option">Model B</button></div>
                    <label>Name<input id="dynamic-name" required></label>
                    <label>Email<input id="dynamic-email" type="email"></label>
                    <button id="dynamic-submit" type="submit">Submit dynamic form</button>
                  </div>
                </form>
                <output id="dynamic-ack"></output>
                <button id="enable-next" type="button">Start next control</button>
                <button id="delayed-next" type="button" disabled>Delayed next control</button>
                <button id="never-ready" type="button" disabled>Permanently disabled</button>
                <span id="dynamic-telemetry" role="progressbar">Unrelated telemetry</span>`);
            window.dynamicSubmits=0; window.modelOpens=0; window.nextClicks=0;
            document.getElementById('dynamic-branch').onchange = () =>
                setTimeout(() => document.getElementById('branch-fields').hidden=false, 200);
            document.getElementById('dynamic-model').onclick = () => {
                window.modelOpens++;
                setTimeout(() => document.getElementById('model-options').hidden=false, 200);
            };
            document.querySelector('#model-options button').onclick = () => {
                document.getElementById('dynamic-model').textContent='Model B';
                document.getElementById('model-options').hidden=true;
            };
            document.getElementById('dynamic-form').onsubmit = event => {
                event.preventDefault(); window.dynamicSubmits++;
                setTimeout(() => {
                    document.getElementById('dynamic-ack').textContent='Submission acknowledged';
                    document.getElementById('dynamic-ack').dataset.ready='true';
                }, 400);
            };
            document.getElementById('enable-next').onclick = () =>
                setTimeout(() => document.getElementById('delayed-next').disabled=false, 300);
            document.getElementById('delayed-next').onclick = () => window.nextClicks++;
            """);
        var timer = Stopwatch.StartNew();
        var result = await browser.DoAsync("steps", value: """
            [{"action":"click","target":"#dynamic-branch"},
             {"action":"select","target":"#dynamic-model","value":"Model B"},
             {"action":"fill","value":"{\"#dynamic-name\":\"Fixture user\",\"#dynamic-email\":\"fixture@example.test\"}"},
             {"action":"click","target":"#dynamic-submit"}]
            """, diagnostics: true);
        output.WriteLine($"Dynamic four-step form ({timer.ElapsedMilliseconds} ms): {result}");
        Assert.Contains("Completed 4 of 4 steps", result);
        Assert.Contains("Filled 2 fields.", result);
        Assert.Contains("target-ready=", result);
        Assert.Contains("document-ready", result);
        Assert.DoesNotContain("Error:", result);
        Assert.Single(Regex.Matches(result, @"(?m)^Tab: "));
        Assert.Single(Regex.Matches(result, @"(?m)^Page: "));
        Assert.Equal("1", await EvaluateString(browser, "String(window.modelOpens)"));
        Assert.Equal("Model B", await EvaluateString(browser, "document.getElementById('dynamic-model').textContent"));
        Assert.Equal("Fixture user", await EvaluateString(browser, "document.getElementById('dynamic-name').value"));
        Assert.DoesNotContain("Error:", await browser.DoAsync("wait", "#dynamic-ack[data-ready='true']", "3000"));
        Assert.Equal("1", await EvaluateString(browser, "String(window.dynamicSubmits)"));

        var enabled = await browser.DoAsync("steps", value: """
            [{"action":"click","target":"#enable-next"},{"action":"click","target":"#delayed-next"}]
            """);
        Assert.Contains("Completed 2 of 2 steps", enabled);
        Assert.Equal("1", await EvaluateString(browser, "String(window.nextClicks)"));

        var disabled = await browser.DoAsync("steps", value: """
            [{"action":"click","target":"#never-ready"},{"action":"click","target":"#dynamic-submit"}]
            """);
        Assert.Contains("no action was executed", disabled);
        Assert.Contains("Completed 0 of 2", disabled);
        Assert.Contains("Unexecuted steps: 2 (click)", disabled);
        var invalid = await browser.DoAsync("steps", value: """
            [{"action":"fill","value":"{\"#dynamic-name\":\"Edited name\",\"#dynamic-email\":\"not-an-email\"}"},
             {"action":"click","target":"#dynamic-submit"}]
            """);
        Assert.Contains("Fill validation failed at field 2", invalid);
        Assert.Contains("Completed fields: 2; unexecuted fields: 0", invalid);
        Assert.Equal("1", await EvaluateString(browser, "String(window.dynamicSubmits)"));
        await browser.EvaluateAsync("document.getElementById('dynamic-telemetry').remove()");
    }

    private async Task VerifyLegacyDropdownCompatibility(BrowserService browser)
    {
        await browser.EvaluateAsync("""
            document.body.insertAdjacentHTML('beforeend', `
                <section id="legacy-dropdown-fixture">
                  <button id="legacy-trigger" type="button">Legacy model</button>
                  <div id="legacy-options" hidden><div class="option">Model B</div></div>
                  <div id="decoy-option" class="option">Model B</div>
                  <button id="scoped-trigger" type="button" role="combobox" aria-controls="scoped-options">Scoped model</button>
                  <div id="scoped-options" hidden><div class="Option">Model B</div></div>
                  <button id="list-trigger" type="button">Legacy list</button>
                  <div id="list-option" role="listitem" hidden>Model C</div>
                </section>`);
            window.legacyOpens=0; window.scopedOpens=0; window.listOpens=0; window.decoyClicks=0; window.legacySelection='';
            document.getElementById('legacy-trigger').onclick=() => {
                window.legacyOpens++;
                setTimeout(() => document.getElementById('legacy-options').hidden=false, 150);
            };
            document.querySelector('#legacy-options .option').onclick=() => {
                window.legacySelection='legacy B'; document.getElementById('legacy-options').hidden=true;
            };
            document.getElementById('decoy-option').onclick=() => window.decoyClicks++;
            document.getElementById('scoped-trigger').onclick=() => {
                window.scopedOpens++;
                setTimeout(() => document.getElementById('scoped-options').hidden=false, 150);
            };
            document.querySelector('#scoped-options .Option').onclick=() => {
                window.legacySelection='scoped B'; document.getElementById('scoped-options').hidden=true;
            };
            document.getElementById('list-trigger').onclick=() => {
                window.listOpens++; document.getElementById('list-option').hidden=false;
            };
            document.getElementById('list-option').onclick=() => {
                window.legacySelection='list C'; document.getElementById('list-option').hidden=true;
            };
            """);
        // The generic opener has no ARIA metadata; its options are identified only by a CSS class.
        var generic = await browser.DoAsync("select", "#legacy-trigger", "Model B");
        output.WriteLine("Legacy dropdown: " + generic);
        Assert.DoesNotContain("Error:", generic);
        Assert.Equal("legacy B", await EvaluateString(browser, "window.legacySelection"));
        Assert.Equal("1", await EvaluateString(browser, "String(window.legacyOpens)"));
        await browser.EvaluateAsync("document.getElementById('decoy-option').hidden=false");
        var scopedRef = Reference(await browser.LookAsync(), "name=\"scoped-trigger\"");
        Assert.DoesNotContain("Error:", await browser.DoAsync("select", scopedRef, "Model B"));
        Assert.Equal("scoped B", await EvaluateString(browser, "window.legacySelection"));
        Assert.Equal("1", await EvaluateString(browser, "String(window.scopedOpens)"));
        Assert.Equal("0", await EvaluateString(browser, "String(window.decoyClicks)"));
        Assert.DoesNotContain("Error:", await browser.DoAsync("select", "Legacy list", "Model C"));
        Assert.Equal("list C", await EvaluateString(browser, "window.legacySelection"));
        Assert.Equal("1", await EvaluateString(browser, "String(window.listOpens)"));
        AssertError(await browser.DoAsync("select", "#submit", "Model B"));
        Assert.Equal("0", await EvaluateString(browser, "String(window.submits)"));
        await browser.EvaluateAsync("document.getElementById('legacy-dropdown-fixture').remove()");
    }

    private async Task VerifyCoordinatedFillValidation(BrowserService browser)
    {
        await browser.EvaluateAsync("""
            document.body.insertAdjacentHTML('beforeend', `
                <form id="range-form">
                  <label>Start<input id="range-start" type="date" value="2026-10-08"></label>
                  <label>End<input id="range-end" type="date" value="2026-10-12"></label>
                  <button id="range-submit" type="submit">Save range</button>
                </form>`);
            window.rangeSubmits=0;
            const start=document.getElementById('range-start'), end=document.getElementById('range-end');
            const validate=() => {
                const valid=start.value <= end.value;
                start.setCustomValidity(valid ? '' : 'Start date is after end date.');
                end.setCustomValidity(valid ? '' : 'End date is before start date.');
            };
            start.oninput=validate; start.onchange=validate; end.oninput=validate; end.onchange=validate;
            document.getElementById('range-form').onsubmit=event => { event.preventDefault(); window.rangeSubmits++; };
            """);
        var valid = await browser.DoAsync("steps", value: """
            [{"action":"fill","value":"{\"#range-start\":\"2026-11-01\",\"#range-end\":\"2026-11-05\"}"},
             {"action":"click","target":"#range-submit"}]
            """);
        output.WriteLine("Coordinated date fill: " + valid);
        Assert.Contains("Completed 2 of 2 steps", valid);
        Assert.DoesNotContain("Error:", valid);
        Assert.Equal("2026-11-01", await EvaluateString(browser, "document.getElementById('range-start').value"));
        Assert.Equal("2026-11-05", await EvaluateString(browser, "document.getElementById('range-end').value"));
        Assert.Equal("1", await EvaluateString(browser, "String(window.rangeSubmits)"));
        await browser.EvaluateAsync("""
            const start=document.getElementById('range-start'), end=document.getElementById('range-end');
            const validate=() => {
                const valid=start.value <= end.value;
                start.setCustomValidity(valid ? '' : 'Start date is after end date.');
                end.setCustomValidity(valid ? '' : 'End date is before start date.');
            };
            end.oninput=() => queueMicrotask(validate);
            end.onchange=() => queueMicrotask(validate);
            """);
        var queued = await browser.DoAsync("fill", value: """
            {"#range-start":"2026-12-01","#range-end":"2026-12-05"}
            """);
        output.WriteLine("Queued framework date validation: " + queued);
        Assert.DoesNotContain("Error:", queued);
        Assert.Equal("true", await EvaluateString(browser, "String(document.getElementById('range-start').validity.valid)"));
        Assert.Equal("true", await EvaluateString(browser, "String(document.getElementById('range-end').validity.valid)"));
        var invalid = await browser.DoAsync("steps", value: """
            [{"action":"fill","value":"{\"#range-start\":\"2026-12-08\",\"#range-end\":\"2026-12-04\"}"},
             {"action":"click","target":"#range-submit"}]
            """);
        AssertError(invalid);
        Assert.Contains("Fill validation failed", invalid);
        Assert.Contains("Completed fields: 2; unexecuted fields: 0", invalid);
        Assert.Contains("Unexecuted steps: 2 (click)", invalid);
        Assert.Equal("1", await EvaluateString(browser, "String(window.rangeSubmits)"));
        await browser.EvaluateAsync("""
            document.getElementById('range-start').oninput=function() { this.value='2026-01-01'; };
            """);
        var reset = await browser.DoAsync("steps", value: """
            [{"action":"fill","value":"{\"#range-start\":\"2026-11-01\",\"#range-end\":\"2026-11-05\"}"},
             {"action":"click","target":"#range-submit"}]
            """);
        Assert.Contains("did not retain the requested value", reset);
        Assert.Contains("unexecuted fields: 1", reset);
        Assert.Equal("1", await EvaluateString(browser, "String(window.rangeSubmits)"));
        await browser.EvaluateAsync("document.getElementById('range-form').remove()");
    }

    private async Task VerifyPageLevelFormErrors(BrowserService browser)
    {
        const string secret = "page-alert-password-39172";
        await browser.EvaluateAsync("""
            document.body.insertAdjacentHTML('beforeend', `
                <section id="page-error-fixture">
                  <input id="alert-password" type="password">
                  <div class="error"><div id="server-alert" role="alert"></div></div>
                  <div class="Error">Try again later.</div>
                  <div role="alert" hidden>Hidden server error.</div>
                </section>`);
            """);
        Assert.DoesNotContain("Error:", await browser.DoAsync("type", "#alert-password", secret));
        await browser.EvaluateAsync(
            "document.getElementById('server-alert').textContent='Server rejected '+document.getElementById('alert-password').value;");
        var form = await browser.DoAsync("read_form");
        output.WriteLine("Form page-level errors: " + form);
        Assert.Contains("Page errors:", form);
        Assert.Contains("Server rejected [redacted]", form);
        Assert.Contains("Try again later.", form);
        Assert.Single(Regex.Matches(form, "Server rejected"));
        Assert.DoesNotContain("Hidden server error.", form);
        Assert.DoesNotContain(secret, form);
        await browser.EvaluateAsync("document.getElementById('page-error-fixture').remove()");
    }

    private async Task VerifyBackgroundJavaScriptAndUploads(BrowserService browser, string root)
    {
        Assert.Equal("visible", await browser.EvaluateAsync(
            "return await new Promise(resolve => setTimeout(() => resolve(document.visibilityState), 20));"));
        Assert.Equal("promise result", await browser.EvaluateAsync("return Promise.resolve('promise result');"));
        var objectResult = await browser.EvaluateAsync("return {number:42, text:'quotes \" and \\\\ and newline\\n'};");
        using (var doc = JsonDocument.Parse(objectResult))
            Assert.Equal(42, doc.RootElement.GetProperty("number").GetInt32());
        Assert.StartsWith("JS Error:", await browser.EvaluateAsync("throw new Error('fixture synchronous error');"));
        Assert.StartsWith("JS Error:", await browser.EvaluateAsync("return Promise.reject(new Error('fixture async error'));"));
        Assert.StartsWith("JS Error:", await browser.EvaluateAsync("const = invalid;"));
        Assert.StartsWith("JS Error:", await browser.EvaluateAsync("return 'must not execute';", timeoutMs: 0));

        browser.SetControllerVisible(false);
        await UntilAsync(async () => await EvaluateString(browser, "document.visibilityState") == "hidden");
        Assert.Contains("visibility: hidden", await browser.LookAsync());
        Assert.Equal("hidden timer", await browser.EvaluateAsync(
            "return new Promise(resolve => setTimeout(() => resolve('hidden timer'), 200));", timeoutMs: 3000));
        var timeoutTimer = Stopwatch.StartNew();
        var timedOut = await browser.EvaluateAsync("return new Promise(() => {});", timeoutMs: 300);
        Assert.StartsWith("JS Error:", timedOut);
        Assert.Contains("not retried", timedOut);
        Assert.Contains("may still finish", timedOut);
        Assert.InRange(timeoutTimer.ElapsedMilliseconds, 0, 1500);
        output.WriteLine("Hidden unresolved Promise: " + timedOut);
        var paintTimer = Stopwatch.StartNew();
        var paint = await browser.EvaluateAsync(
            "return new Promise(resolve => requestAnimationFrame(() => resolve('paint available')));", timeoutMs: 300);
        Assert.True(paint == "paint available" || paint.StartsWith("JS Error:", StringComparison.Ordinal), paint);
        Assert.InRange(paintTimer.ElapsedMilliseconds, 0, 1500);
        output.WriteLine("Hidden animation-frame availability: " + paint);
        await browser.EvaluateAsync("""
            setTimeout(() => {
                const button=document.createElement('button'); button.id='hidden-ready'; button.textContent='Hidden DOM ready';
                document.body.append(button);
            }, 200);
            return 'scheduled';
            """);
        Assert.DoesNotContain("Error:", await browser.DoAsync("wait", "#hidden-ready", "3000"));
        Assert.Equal("false", await EvaluateString(browser, "String(document.hasFocus())"));
        browser.SetControllerVisible(true);

        var first = Path.Combine(root, "upload-a.txt");
        var second = Path.Combine(root, "upload-b,1.txt");
        await File.WriteAllTextAsync(first, "First fixture file");
        await File.WriteAllTextAsync(second, "Second fixture file");
        await browser.EvaluateAsync("""
            document.body.insertAdjacentHTML('beforeend',
                '<input id="fixture-files" type="file" multiple><span id="upload-telemetry" role="progressbar">Unrelated upload telemetry</span>');
            window.uploadInputs=0; window.uploadChanges=0;
            document.getElementById('fixture-files').oninput=() => window.uploadInputs++;
            document.getElementById('fixture-files').onchange=() => window.uploadChanges++;
            """);
        var files = "[" + JsonSerializer.Serialize(first, Lumi.Models.AppDataJsonContext.Default.String) + "," +
            JsonSerializer.Serialize(second, Lumi.Models.AppDataJsonContext.Default.String) + "]";
        var uploadTimer = Stopwatch.StartNew();
        var uploaded = await browser.DoAsync("upload", "#fixture-files", files);
        output.WriteLine($"Multi-file upload ({uploadTimer.ElapsedMilliseconds} ms): {uploaded}");
        Assert.Contains("Uploaded 2 file(s)", uploaded);
        Assert.DoesNotContain("Error:", uploaded);
        Assert.Single(Regex.Matches(uploaded, @"(?m)^Page: "));
        Assert.Equal("upload-a.txt|upload-b,1.txt", await EvaluateString(browser,
            "Array.from(document.getElementById('fixture-files').files).map(file => file.name).join('|')"));
        Assert.Equal("1", await EvaluateString(browser, "String(window.uploadInputs)"));
        Assert.Equal("1", await EvaluateString(browser, "String(window.uploadChanges)"));
        Assert.True(uploadTimer.ElapsedMilliseconds < 1500, "Unrelated telemetry delayed an otherwise-ready upload.");
        await browser.EvaluateAsync("document.getElementById('upload-telemetry').remove()");
    }

    private async Task VerifyNavigationAndQueuedValidation(BrowserService browser, string url, string root)
    {
        var loadingPage = Path.Combine(root, "loading.html");
        await File.WriteAllTextAsync(loadingPage, """
            <!doctype html><html><head><title>Delayed application fixture</title></head><body>Loading...
            <script>
            window.arrivalClicks=0;
            setTimeout(() => {
                document.body.insertAdjacentHTML('beforeend', '<button id="app-ready">Continue when app is ready</button>');
                document.getElementById('app-ready').onclick=() => window.arrivalClicks++;
            }, 400);
            </script></body></html>
            """);
        var opened = await browser.OpenAndSnapshotAsync(new Uri(loadingPage).AbsoluteUri, diagnostics: true);
        output.WriteLine("Delayed application navigation: " + opened);
        Assert.Contains("Navigation: completed.", opened);
        Assert.Contains("Readiness: DOM available", opened);
        Assert.Contains("navigation=", opened);
        if (!opened.Contains("Continue when app is ready", StringComparison.Ordinal))
            Assert.Contains("no interactive elements yet", opened);
        var clicked = await browser.DoAsync("click", "#app-ready quiet");
        Assert.DoesNotContain("Error:", clicked);
        Assert.Equal("1", await EvaluateString(browser, "String(window.arrivalClicks)"));
        var hashNavigation = await browser.NavigateAsync(new Uri(loadingPage).AbsoluteUri + "#same-document");
        Assert.StartsWith("Navigated to ", hashNavigation);
        Assert.DoesNotContain("Error:", await browser.DoAsync("click", "#app-ready quiet"));
        Assert.Equal("2", await EvaluateString(browser, "String(window.arrivalClicks)"));
        var failed = await browser.OpenAndSnapshotAsync(new Uri(Path.Combine(root, "missing.html")).AbsoluteUri);
        AssertError(failed);
        Assert.DoesNotContain("Navigation: completed.", failed);

        await browser.OpenAndSnapshotAsync(url);
        await browser.EvaluateAsync("""
            document.getElementById('notes').oninput = function() {
                queueMicrotask(() => this.value='rejected asynchronously');
            };
            """);
        var rejected = await browser.DoAsync("steps", value: """
            [{"action":"type","target":"#notes","value":"must not be accepted"},
             {"action":"click","target":"#confirm-notes"}]
            """);
        AssertError(rejected);
        Assert.Contains("did not retain the requested value", rejected);
        Assert.Contains("Unexecuted steps: 2 (click)", rejected);
        Assert.Equal("0", await EvaluateString(browser, "String(window.confirmations)"));
        await browser.EvaluateAsync("document.getElementById('notes').oninput=null");
    }

    private async Task VerifyKeyboardAndClickTargets(BrowserService browser)
    {
        Assert.DoesNotContain("Error:", await browser.DoAsync("type", "#query", "local query"));
        Assert.Equal("query", await EvaluateString(browser, "document.activeElement.id"));
        Assert.DoesNotContain("Error:", await browser.DoAsync("press", "Enter"));
        Assert.Equal("1", await EvaluateString(browser, "String(window.searches)"));

        var batch = await browser.DoAsync("steps", value: """
            [{"action":"type","target":"#query","value":"batched query"},
             {"action":"press","target":"Enter"}]
            """);
        Assert.Contains("Completed 2 of 2 steps", batch);
        Assert.DoesNotContain("Error:", batch);
        Assert.Equal("2", await EvaluateString(browser, "String(window.searches)"));
        Assert.Equal("2", await EvaluateString(browser, "String(window.queryBlurs)"));

        var natural = await browser.DoAsync("click", "Search");
        Assert.Contains("button", natural);
        Assert.Equal("3", await EvaluateString(browser, "String(window.searches)"));

        await browser.EvaluateAsync("""
            const query = document.getElementById('query');
            query.insertAdjacentHTML('beforebegin', '<label for="query">Search</label>');
            query.setAttribute('onclick', 'this.select();window.fieldClicks++');
            window.fieldClicks=0;
            """);
        var labeled = await browser.DoAsync("click", "Search");
        output.WriteLine("Labeled Search input with inline click handler: " + labeled);
        output.WriteLine("Submissions: " + await EvaluateString(browser, "String(window.searches)") +
            "; input callbacks: " + await EvaluateString(browser, "String(window.fieldClicks)"));
        Assert.Matches(@"(?m)^Clicked \[\d+\] button\b", labeled);
        Assert.Equal("4", await EvaluateString(browser, "String(window.searches)"));
        Assert.Equal("0", await EvaluateString(browser, "String(window.fieldClicks)"));

        Assert.DoesNotContain("Error:", await browser.DoAsync("click", "#query"));
        Assert.Equal("query", await EvaluateString(browser, "document.activeElement.id"));
        var queryRef = Reference(await browser.LookAsync(), "name=\"search\"");
        Assert.DoesNotContain("Error:", await browser.DoAsync("click", queryRef));
        Assert.Equal("query", await EvaluateString(browser, "document.activeElement.id"));
        Assert.DoesNotContain("Error:", await browser.DoAsync("click", "query"));
        Assert.Equal("query", await EvaluateString(browser, "document.activeElement.id"));
        Assert.Equal("4", await EvaluateString(browser, "String(window.searches)"));
        Assert.Equal("3", await EvaluateString(browser, "String(window.fieldClicks)"));
        await browser.EvaluateAsync("""
            document.querySelector('main').insertAdjacentHTML('beforeend',
                '<div onclick="window.genericClicks++">Custom action</div>');
            window.genericClicks=0;
            """);
        Assert.DoesNotContain("Error:", await browser.DoAsync("click", "Custom action"));
        Assert.Equal("1", await EvaluateString(browser, "String(window.genericClicks)"));
        output.WriteLine("Search submitted exactly four times, including the labeled/onclick case with zero field callbacks; CSS/numeric/field-name clicks ran the input handler exactly once each.");
    }

    private async Task VerifyMultilineEdits(BrowserService browser)
    {
        var confirmations = 0;
        foreach (var newline in new[] { "\n", "\r\n" })
        {
            var text = "First line" + newline + "Second line";
            foreach (var action in new[] { "type", "fill" })
            {
                var value = action == "type" ? text : new System.Text.Json.Nodes.JsonObject { ["#notes"] = text }.ToJsonString();
                var steps = new System.Text.Json.Nodes.JsonArray
                {
                    new System.Text.Json.Nodes.JsonObject { ["action"] = action, ["target"] = "#notes", ["value"] = value },
                    new System.Text.Json.Nodes.JsonObject { ["action"] = "click", ["target"] = "#confirm-notes" }
                };
                var result = await browser.DoAsync("steps", value: steps.ToJsonString());
                Assert.Contains("Completed 2 of 2 steps", result);
                Assert.DoesNotContain("Error:", result);
                Assert.Equal("First line\nSecond line", await EvaluateString(browser, "document.getElementById('notes').value"));
                Assert.Equal((++confirmations).ToString(), await EvaluateString(browser, "String(window.confirmations)"));
            }
        }

        foreach (var behavior in new[] { "reset", "replace" })
        {
            await browser.EvaluateAsync(behavior == "reset"
                ? "document.getElementById('notes').oninput = function() { this.value = 'rejected'; }"
                : "document.getElementById('notes').oninput = function() { this.replaceWith(this.cloneNode(true)); }");
            var failed = await browser.DoAsync("steps", value: """
                [{"action":"type","target":"#notes","value":"must not be accepted"},
                 {"action":"click","target":"#confirm-notes"}]
                """);
            AssertError(failed);
            Assert.Equal("4", await EvaluateString(browser, "String(window.confirmations)"));
        }
        output.WriteLine("All four LF/CRLF type/fill batches confirmed once; reset/replacement failures prevented later confirmation.");
    }

    private async Task VerifyActionSettling(BrowserService browser)
    {
        var timer = Stopwatch.StartNew();
        var delayed = await browser.DoAsync("steps", value: """
            [{"action":"click","target":"#delayed"},{"action":"wait","target":"#delayed-result[data-ready='true']","value":"3000"}]
            """);
        var delayedMs = timer.ElapsedMilliseconds;
        var delayedClicks = await EvaluateString(browser, "String(window.delayedClicks)");

        await browser.EvaluateAsync("""
            window.noise = setInterval(() => document.getElementById('marker').dataset.tick = String(performance.now()), 50)
            """);
        timer.Restart();
        var noisy = await browser.DoAsync("steps", value: """
            [{"action":"click","target":"#tick quiet"},{"action":"click","target":"#followup quiet"}]
            """);
        var noisyMs = timer.ElapsedMilliseconds;
        var tickClicks = await EvaluateString(browser, "String(window.tickClicks)");
        var followups = await EvaluateString(browser, "String(window.followups)");
        await browser.EvaluateAsync("clearInterval(window.noise)");

        await browser.EvaluateAsync("""
            window.animation = setInterval(() => document.getElementById('live-result').textContent = String(performance.now()), 50)
            """);
        timer.Restart();
        var changing = await browser.DoAsync("click", "#tick");
        var changingMs = timer.ElapsedMilliseconds;
        var totalTickClicks = await EvaluateString(browser, "String(window.tickClicks)");
        await browser.EvaluateAsync("clearInterval(window.animation)");

        output.WriteLine($"Delayed click ({delayedMs} ms): {delayed}");
        output.WriteLine($"Attribute-noise batch ({noisyMs} ms): {noisy}");
        output.WriteLine($"Continuously changing page ({changingMs} ms): {changing}");
        Assert.Contains("Result arrived", delayed);
        Assert.DoesNotContain("Error:", delayed);
        Assert.Equal("1", delayedClicks);
        Assert.Contains("Completed 2 of 2 steps", noisy);
        Assert.DoesNotContain("Error:", noisy);
        Assert.Equal("1", tickClicks);
        Assert.Equal("1", followups);
        Assert.DoesNotContain("Error:", changing);
        Assert.DoesNotContain("time limit", changing);
        Assert.Contains("Readiness: DOM available", changing);
        Assert.Equal("2", totalTickClicks);
        Assert.InRange(changingMs, 0, 1500);
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

    private async Task VerifyCaptureAndDownloads(BrowserService browser, string url, string root)
    {
        var first = browser.ActiveTabId;
        output.WriteLine("Screenshot fixture scroll before setup: " + await EvaluateString(browser, "String(window.scrollY)"));
        Assert.DoesNotContain("Error:", await browser.DoAsync("scroll", "up", "10000"));
        Assert.Equal("0", await EvaluateString(browser, "String(window.scrollY)"));
        var image = await browser.CaptureScreenshotAsync();
        Assert.Equal("0", await EvaluateString(browser, "String(window.scrollY)"));
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

        var formBeforeCapture = await browser.DoAsync("read_form");
        browser.SetBounds(0, 0, 3840, 2160);
        var scaledImage = await browser.CaptureScreenshotAsync();
        Assert.Equal(2048, scaledImage.Width);
        Assert.Equal(1152, scaledImage.Height);
        Assert.InRange(scaledImage.PngBytes.Length, 1, BrowserService.MaxScreenshotPngBytes);
        Assert.Equal(image.TabId, scaledImage.TabId);
        Assert.Equal(image.Url, scaledImage.Url);
        using (var stream = new MemoryStream(scaledImage.PngBytes))
        using (var bitmap = new System.Drawing.Bitmap(stream))
        {
            Assert.Equal(scaledImage.Width, bitmap.Width);
            Assert.Equal(scaledImage.Height, bitmap.Height);
        }
        Assert.Equal(formBeforeCapture, await browser.DoAsync("read_form"));
        Assert.Equal(new System.Drawing.Rectangle(0, 0, 3840, 2160), browser.Controller!.Bounds);
        browser.SetBounds(0, 0, 940, 700);

        await browser.ManageTabsAsync("new", url: url);
        var second = browser.ActiveTabId;
        var hidden = await Assert.ThrowsAsync<InvalidOperationException>(() => browser.CaptureScreenshotAsync(first));
        Assert.Contains("hidden", hidden.Message, StringComparison.OrdinalIgnoreCase);
        var captureTool = Lumi.ViewModels.ChatViewModel.BuildBrowserScreenshotTool(browser.CaptureScreenshotAsync);
        var failure = Assert.IsType<GitHub.Copilot.ToolResultAIContent>(await captureTool.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { ["tabId"] = first }));
        Assert.Equal("failure", failure.Result.ResultType);
        Assert.Contains("Switch to this tab and show the browser panel", failure.Result.TextResultForLlm);
        Assert.Null(failure.Result.BinaryResultsForLlm);
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
        JsonSerializer.Deserialize(await browser.WebView!.ExecuteScriptAsync(expression),
            Lumi.Models.AppDataJsonContext.Default.String)!;

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
        <form onsubmit="event.preventDefault();window.searches++">
        <input id="query" type="search" name="search" placeholder="Search">
        <button id="search-submit" type="submit">Search</button></form>
        <button id="commit-validation" type="button" disabled onclick="window.queryConfirmations++">Accept validation</button>
        <textarea id="notes"></textarea>
        <button id="confirm-notes" type="button" onclick="window.confirmations++">Confirm notes</button>
        <button id="delayed" type="button" onclick="window.delayedClicks++;setTimeout(()=>{const result=document.getElementById('delayed-result');result.textContent='Result arrived';result.dataset.ready='true'},500)">Delayed result</button>
        <output id="delayed-result">Result pending</output>
        <button id="tick" type="button" onclick="window.tickClicks++">Count click</button>
        <button id="followup" type="button" onclick="window.followups++">Follow-up</button>
        <output id="live-result"></output>
        <button id="popup" type="button" onclick="window.open('about:blank','related-popup')">Open related popup</button>
        <button id="download" type="button" onclick="downloadFixture()">Download isolated file</button>
        <dialog id="dialog"><button id="dialog-action" type="button">Dialog action</button></dialog>
        </main><script>
        window.submits=0;window.popupReply='';window.delayedClicks=0;window.tickClicks=0;window.followups=0;
        window.searches=0;window.queryBlurs=0;window.confirmations=0;
        window.validatedQuery='';window.queryConfirmations=0;
        document.getElementById('query').addEventListener('blur', function() {
          window.queryBlurs++;window.validatedQuery=this.value;
          document.getElementById('commit-validation').disabled=false;
        });
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
