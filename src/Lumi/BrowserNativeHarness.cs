#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
#if !WINDOWS
using Microsoft.Data.Sqlite;
#endif
using SkiaSharp;

namespace Lumi;

/// <summary>Headed, isolated browser capability and real Workspace docking regression check.</summary>
internal sealed class BrowserNativeHarness
{
    private readonly List<(string Name, bool Passed, string Detail)> _checks = [];
    private readonly List<(string Name, double Milliseconds)> _operationTimings = [];
    private readonly string _output;
    private readonly LocalServer _server = new();

    private BrowserNativeHarness(string? output)
    {
        _output = output ?? Path.Combine(DataStore.AppDirectory, "browser-results");
        Directory.CreateDirectory(_output);
    }

    internal static void Start(
        IClassicDesktopStyleApplicationLifetime desktop, MainViewModel vm, DataStore store,
        bool keepOpen, string? output)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var harness = new BrowserNativeHarness(output);
            var exitCode = 1;
            try
            {
                await harness.RunAsync(desktop, vm, store).WaitAsync(TimeSpan.FromMinutes(6));
                exitCode = harness._checks.All(check => check.Passed) ? 0 : 1;
            }
            catch (Exception ex)
            {
                harness.Check("harness-completed", false, ex.ToString());
            }
            finally
            {
                harness.WriteReport();
                Console.WriteLine($"[RESULT] LUMI_BROWSER_NATIVE_{(exitCode == 0 ? "OK" : "FAIL")}");
                Environment.ExitCode = exitCode;
                if (!keepOpen)
                {
                    harness._server.Dispose();
                    desktop.Shutdown(exitCode);
                }
                else
                {
                    Console.WriteLine($"[INFO] Browser fixture and server remain open. PID={Environment.ProcessId}");
                }
            }
        }, DispatcherPriority.Background);
    }

    private void Check(string name, bool passed, string detail = "")
    {
        _checks.Add((name, passed, detail));
        Console.WriteLine($"[CHK] {(passed ? "PASS" : "FAIL")} {name}: {detail}");
    }

    private async Task<string> MeasureAsync(string name, Func<Task<string>> operation)
    {
        var timer = Stopwatch.StartNew();
        try { return await operation(); }
        finally
        {
            var milliseconds = timer.Elapsed.TotalMilliseconds;
            _operationTimings.Add((name, milliseconds));
            Console.WriteLine($"[TIME] {name}: {milliseconds:F1} ms");
        }
    }

    private async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, MainViewModel vm, DataStore store)
    {
        _server.Start();
        var window = desktop.MainWindow ?? throw new InvalidOperationException("No main window.");
        store.Data.Settings.HasImportedBrowserCookies = true;
        var chat = new Chat
        {
            Title = "Native browser validation",
            Messages =
            [
                new ChatMessage { Role = "user", Content = "Open the local browser validation page." },
                new ChatMessage { Role = "assistant", Content = "The isolated browser fixture is ready." },
            ],
        };
        store.Data.Chats.Add(chat);
        vm.RefreshChatList();
        await vm.OpenChatByIdAsync(chat.Id);
        var chatVm = vm.ChatVM;
        await WaitAsync(() => Task.FromResult(chatVm.CurrentChat?.Id == chat.Id));
        var browser = chatVm.GetOrCreateBrowserService(chat.Id);
        chatVm.HasUsedBrowser = true;
        chatVm.RequestShowBrowser();
        var view = await WaitValueAsync(() => Task.FromResult(
            window.GetVisualDescendants().OfType<BrowserView>().FirstOrDefault()),
            value => value is { IsEffectivelyVisible: true } && value.Bounds.Width > 100);
        if (view is null)
            throw new InvalidOperationException("The real Workspace browser view did not appear.");
        await SettleBoundsAsync(view);
        Check("real-chat-and-workspace", chatVm.CurrentChat?.Id == chat.Id && view.IsEffectivelyVisible);

        var opened = await MeasureAsync("open", () => browser.OpenAndSnapshotAsync(_server.Url + "/fixture"));
        Check("navigation-and-snapshot", opened.Contains("Native Browser Fixture", StringComparison.Ordinal)
            && opened.Contains("--- Elements ---", StringComparison.Ordinal), Clip(opened));
        Check("native-controller-live", browser.HasController);
#if WINDOWS
        browser.WebView!.Profile.DefaultDownloadFolderPath = _output;
#endif
        await VerifyDockAsync(browser, view, window, "initial");
        await VerifyScreenshotAsync(browser, "initial");
#if !WINDOWS
        await VerifyNativeCookiesAsync(browser);
#endif

        var initialTab = browser.ActiveTabId;
        var found = await browser.FindElementsAsync("Alpha", 1);
        var number = Regex.Match(found, @"\[(\d+)\]").Groups[1].Value;
        Check("stable-ranked-target", number.Length > 0, Clip(found));
        Check("numbered-click", (await MeasureAsync("click", () => browser.DoAsync("click", number)))
            .Contains("Clicked", StringComparison.Ordinal));
        Check("click-actually-executed", await browser.EvaluateAsync("return String(window.alphaClicks)") == "1");
        await browser.EvaluateAsync("document.getElementById('alpha').outerHTML='<button id=\"alpha\">Alpha</button>'");
        Check("stale-reference-rejected", (await browser.DoAsync("click", number)).Contains("Error:", StringComparison.Ordinal));

        var filled = await MeasureAsync("fill", () => browser.DoAsync("fill", value:
            """{"#query":"native query","#notes":"line one\r\nline two","#choice":"Two","#checked":true,"#password":"fixture-secret"}"""));
        Check("form-fill", !filled.Contains("Error:", StringComparison.Ordinal), Clip(filled));
        var form = await browser.DoAsync("read_form");
        Check("form-read-and-secret-redaction", form.Contains("native query", StringComparison.Ordinal)
            && form.Contains("[redacted]", StringComparison.Ordinal)
            && !form.Contains("fixture-secret", StringComparison.Ordinal));
        var multiline = await browser.EvaluateAsync("return document.getElementById('notes').value");
        Check("multiline-retained", multiline == "line one\nline two", Clip(multiline));
        await browser.DoAsync("type", "#query", "enter query");
        await browser.DoAsync("press", "Enter");
        Check("keyboard-submit", await browser.EvaluateAsync("return String(window.submits)") == "1");

        var failedBatch = await browser.DoAsync("steps", value:
            """[{"action":"type","target":"#query","value":"retained"},{"action":"click","target":"#missing"},{"action":"click","target":"#submit"}]""");
        Check("batch-fail-stop", failedBatch.Contains("Completed 1 of 3", StringComparison.Ordinal)
            && await browser.EvaluateAsync("return String(window.submits)") == "1", Clip(failedBatch));
        var batch = await MeasureAsync("batch-three", () => browser.DoAsync("steps", value:
            """[{"action":"clear","target":"#query"},{"action":"type","target":"#query","value":"batch query"},{"action":"press","target":"Enter"}]"""));
        Check("batch-success", batch.Contains("Completed 3 of 3", StringComparison.Ordinal));
        for (var iteration = 1; iteration <= 3; iteration++)
        {
            var typed = await MeasureAsync("ready-type-" + iteration,
                () => browser.DoAsync("type", "#query", "batch query"));
            Check("ready-type-" + iteration, !typed.Contains("Error:", StringComparison.Ordinal), Clip(typed));
        }
        Check("wait", !(await browser.DoAsync("wait", "#query", "1000")).Contains("Error:", StringComparison.Ordinal));
        await browser.DoAsync("scroll", "down", "300");
        Check("scroll", double.TryParse(await browser.EvaluateAsync("return String(scrollY)"),
            System.Globalization.CultureInfo.InvariantCulture, out var scrollY) && scrollY > 0);
        await browser.DoAsync("scroll", "up", "300");
        await browser.DoAsync("select", "#choice", "One");
        Check("select", await browser.EvaluateAsync("return document.getElementById('choice').value") == "1");

        await VerifyUploadsAsync(browser);

        await browser.EvaluateAsync("document.cookie='nativeSession=fixture-session;path=/';return document.cookie");
        await browser.ManageTabsAsync("new", url: _server.Url + "/second");
        var secondTab = browser.ActiveTabId;
        Check("new-tab", secondTab != initialTab && browser.Tabs.Count == 2);
        var cookies = await browser.EvaluateAsync("return document.cookie");
        if (OperatingSystem.IsLinux())
            Check("linux-per-tab-session-policy", !cookies.Contains("nativeSession=fixture-session", StringComparison.Ordinal),
                "WebKitGTK session cookies stay in their original tab; authenticated workflows must use that tab.");
        else
            Check("same-profile-session-across-tabs", cookies.Contains("nativeSession=fixture-session", StringComparison.Ordinal), cookies);
        var wrongTab = await browser.DoAsync("click", number);
        Check("cross-tab-reference-rejected", wrongTab.Contains("Error:", StringComparison.Ordinal));
        await browser.ManageTabsAsync("switch", initialTab);
        Check("switch-preserves-page-state", await browser.EvaluateAsync("return document.getElementById('query').value") == "batch query");
        Check("original-tab-retains-session",
            (await browser.EvaluateAsync("return document.cookie")).Contains("nativeSession=fixture-session", StringComparison.Ordinal));
        await browser.ManageTabsAsync("close", secondTab);
        Check("close-tab", browser.Tabs.Count == 1 && browser.ActiveTabId == initialTab);
        await browser.DoAsync("click", "#popup");
        await WaitAsync(() => Task.FromResult(browser.Tabs.Count == 2));
        Check("popup-tab-created", browser.Tabs.Count == 2);
        await browser.ManageTabsAsync("switch", initialTab);
        await browser.ManageTabsAsync("close", browser.Tabs.First(tab => tab.Id != initialTab).Id);

        await browser.OpenAndSnapshotAsync(_server.Url + "/second");
        var back = await browser.GoBackAsync();
        Check("history-back", !back.Contains("Error:", StringComparison.Ordinal)
            && (await browser.LookAsync()).Contains("Native Browser Fixture", StringComparison.Ordinal));
        await browser.ClearCookiesAsync();
        Check("clear-cookies", !(await browser.EvaluateAsync("return document.cookie")).Contains("nativeSession=", StringComparison.Ordinal));

#if WINDOWS
        const string themeScript = "return String(matchMedia('(prefers-color-scheme: dark)').matches)";
        const string lightThemeResult = "false";
        const string darkThemeResult = "true";
#else
        const string themeScript = "return document.documentElement.style.colorScheme";
        const string lightThemeResult = "light";
        const string darkThemeResult = "dark";
#endif
        browser.SetTheme(false);
        await WaitAsync(async () => await browser.EvaluateAsync(themeScript) == lightThemeResult);
        Check("theme-light", await browser.EvaluateAsync(themeScript) == lightThemeResult);
        browser.SetTheme(true);
        await WaitAsync(async () => await browser.EvaluateAsync(themeScript) == darkThemeResult);
        Check("theme-dark", await browser.EvaluateAsync(themeScript) == darkThemeResult);

#if !WINDOWS
        {
            var unsupported = await browser.DoAsync("download", "*.bin");
            Check("native-download-explicit-limitation", unsupported.Contains("Error:", StringComparison.Ordinal)
                && unsupported.Contains(NativeWebViewPlatform.NativeDownloadUnsupportedReason, StringComparison.Ordinal)
                && unsupported.Contains("curl", StringComparison.Ordinal), Clip(unsupported));
        }
#else
        {
            var navigationReply = await browser.OpenAndSnapshotAsync(_server.Url + "/download");
            var download = "";
            // Downloads abort navigation; verify the download API, not native event ordering.
            await WaitAsync(async () =>
            {
                download = await browser.DoAsync("download", "lumi-native-fixture.bin");
                return download.Contains("Downloaded:", StringComparison.Ordinal);
            });
            Check("download-detected", download.Contains("Downloaded:", StringComparison.Ordinal),
                Clip(navigationReply) + "\nFinal download status: " + Clip(download));
            Check("download-saved-content",
                await File.ReadAllTextAsync(Path.Combine(_output, "lumi-native-fixture.bin")) == "LUMI_NATIVE_DOWNLOAD");
        }
#endif

        await browser.OpenAndSnapshotAsync(_server.Url + "/fixture");
        view.HideCurrentController();
        var hiddenFailed = false;
        try { await browser.CaptureScreenshotAsync(); }
        catch (InvalidOperationException) { hiddenFailed = true; }
        Check("hidden-capture-rejected", hiddenFailed);
        view.ShowCurrentController();
        view.RefreshBounds();
        await VerifyDockAsync(browser, view, window, "reshown");
        window.Width += 140;
        window.Height += 100;
        await SettleBoundsAsync(view);
        view.RefreshBounds();
        await VerifyDockAsync(browser, view, window, "resized");
        UiScaleService.Apply(125);
        await SettleBoundsAsync(view);
        view.RefreshBounds();
        await VerifyDockAsync(browser, view, window, "ui-scale-125");
        await VerifyScreenshotAsync(browser, "scaled");
        UiScaleService.Apply(100);
        await SettleBoundsAsync(view);

#if !WINDOWS
        var layer = window.GetVisualDescendants().OfType<Canvas>()
            .FirstOrDefault(control => control.Name == "NativeWebViewLayer");
        Check("native-host-present", layer is not null);
        Check("native-host-holds-tabs", layer?.Children.OfType<NativeWebView>().Count() == browser.Tabs.Count);
#else
        Check("no-native-host-on-windows", !window.GetVisualDescendants().Any(control => control.Name == "NativeWebViewLayer"));
#endif
        await VerifyWindowTransferAsync(desktop, vm, chat, browser, window);
#if !WINDOWS
        await VerifyNavigationWaitAsync(browser);
        await VerifyDisposalAsync(window, browser);
        await VerifySettingsCookieResetAsync(vm, store, browser);
#endif
        Check("harness-completed", true);
    }

    private async Task VerifyWindowTransferAsync(
        IClassicDesktopStyleApplicationLifetime desktop, MainViewModel vm,
        Chat chat, BrowserService browser, Window mainWindow)
    {
        await browser.EvaluateAsync("document.getElementById('query').value='transfer retained';return true");
        await vm.OpenChatInNewWindowCommand.ExecuteAsync(chat);
        var detached = await WaitValueAsync(() => Task.FromResult(desktop.Windows.OfType<ChatWindow>().FirstOrDefault()),
            value => value is { IsVisible: true });
        if (detached is null)
            throw new InvalidOperationException("The real detached chat window did not appear.");
        if (detached.DataContext is not ChatWindowViewModel windowVm)
            throw new InvalidOperationException("The detached chat has no chat ViewModel.");
        windowVm.ChatVM.HasUsedBrowser = true;
        windowVm.ChatVM.RequestShowBrowser();
        var detachedView = await WaitValueAsync(() => Task.FromResult(
            detached.GetVisualDescendants().OfType<BrowserView>().FirstOrDefault()),
            value => value is { IsEffectivelyVisible: true } && value.Bounds.Width > 100);
        if (detachedView is null)
            throw new InvalidOperationException("The detached Workspace browser did not appear.");
        await VerifyDockAsync(browser, detachedView, detached, "detached");
        Check("window-transfer-preserves-state", browser.HasController
            && await browser.EvaluateAsync("return document.getElementById('query').value") == "transfer retained");
        await VerifyScreenshotAsync(browser, "detached");
#if !WINDOWS
        var detachedLayer = detached.GetVisualDescendants().OfType<Canvas>()
            .FirstOrDefault(control => control.Name == "NativeWebViewLayer");
#else
        // WebView2 must have a live parent before the old parent HWND is destroyed.
        var mainHandle = mainWindow.TryGetPlatformHandle()
            ?? throw new InvalidOperationException("The main WebView2 host has no native window handle.");
        browser.SetParentHwnd(mainHandle.Handle);
#endif
        detached.Close();
        await WaitAsync(() => Task.FromResult(!desktop.Windows.Contains(detached)));
        await vm.OpenChatByIdAsync(chat.Id);
        var restoredBrowser = vm.ChatVM.GetOrCreateBrowserService(chat.Id);
        Check("window-return-preserves-service", ReferenceEquals(browser, restoredBrowser));
        vm.ChatVM.HasUsedBrowser = true;
        vm.ChatVM.RequestShowBrowser();
        var restoredView = await WaitValueAsync(() => Task.FromResult(
            mainWindow.GetVisualDescendants().OfType<BrowserView>().FirstOrDefault()),
            value => value is { IsEffectivelyVisible: true } && value.Bounds.Width > 100);
        if (restoredView is null)
            throw new InvalidOperationException("The main Workspace browser did not return.");
        await VerifyDockAsync(restoredBrowser, restoredView, mainWindow, "returned");
        Check("window-return-preserves-state", restoredBrowser.HasController
            && await restoredBrowser.EvaluateAsync("return document.getElementById('query').value") == "transfer retained");
#if !WINDOWS
        Check("closed-window-releases-native-host",
            detachedLayer is not null && !detachedLayer.Children.OfType<NativeWebView>().Any(),
            $"layer found={detachedLayer is not null}, native views={detachedLayer?.Children.OfType<NativeWebView>().Count()}");
#endif
        await VerifyScreenshotAsync(restoredBrowser, "returned");
    }

#if !WINDOWS
    private async Task VerifyNavigationWaitAsync(BrowserService browser)
    {
        var click = browser.DoAsync("click", "#slow-nav");
        try
        {
            await _server.NavigationRequested.WaitAsync(TimeSpan.FromSeconds(5));
            var action = await click;
            Check("delayed-navigation-dispatched", action.Contains("Clicked", StringComparison.Ordinal), Clip(action));
            var wait = await browser.DoAsync("wait", "#query", "200");
            Check("wait-rejects-outgoing-document", wait.Contains("Error:", StringComparison.Ordinal), Clip(wait));
        }
        finally
        {
            _server.ReleaseNavigation();
        }
        await WaitAsync(async () => await browser.EvaluateAsync("return document.title") == "Navigation Fixture");
        Check("delayed-navigation-completed", (await browser.LookAsync()).Contains("Navigation Fixture", StringComparison.Ordinal));
        await browser.OpenAndSnapshotAsync(_server.Url + "/fixture");
    }

    private async Task VerifyNativeCookiesAsync(BrowserService browser)
    {
        var view = browser.GetHostLayer().Children.OfType<NativeWebView>().Single();
        var expires = DateTime.UtcNow.AddMinutes(30);
        await NativeWebViewCookies.SetAsync(view,
        [
            new Cookie("importedSession", "fixture-value", "/", "127.0.0.1") { Discard = true },
            new Cookie("importedEmpty", "", "/", "127.0.0.1") { HttpOnly = true, Expires = expires },
            new Cookie("importedSecure", "fixture-secure", "/secure", "127.0.0.1")
                { Secure = true, HttpOnly = true, Expires = expires },
        ]);
        var cookies = await NativeWebViewCookies.GetAllAsync(view);
        var session = cookies.SingleOrDefault(cookie => cookie.Name == "importedSession");
        var empty = cookies.SingleOrDefault(cookie => cookie.Name == "importedEmpty");
        var secure = cookies.SingleOrDefault(cookie => cookie.Name == "importedSecure");
        Check("native-cookie-session-roundtrip",
            session is { Value: "fixture-value", Path: "/" } && session.Expires == DateTime.MinValue,
            $"found={session is not null}, expiry={session?.Expires:O}, discard={session?.Discard}");
        Check("native-cookie-empty-and-expiry-roundtrip",
            empty is { Value: "", HttpOnly: true, Path: "/" }
            && Math.Abs((empty.Expires.ToUniversalTime() - expires).TotalSeconds) <= 2);
        Check("native-cookie-secure-path-roundtrip",
            secure is { Value: "fixture-secure", Secure: true, HttpOnly: true, Path: "/secure" });
        var visible = await browser.EvaluateAsync("return document.cookie");
        Check("native-cookie-visible-and-http-only",
            visible.Contains("importedSession=fixture-value", StringComparison.Ordinal)
            && !visible.Contains("importedEmpty=", StringComparison.Ordinal));
        await browser.ClearCookiesAsync();
        Check("native-cookie-clear-roundtrip", !(await NativeWebViewCookies.GetAllAsync(view))
            .Any(cookie => cookie.Name.StartsWith("imported", StringComparison.Ordinal)));
    }

    private async Task VerifyDisposalAsync(Window window, BrowserService retainedBrowser)
    {
        await retainedBrowser.EvaluateAsync("document.cookie='profileBoundary=main-only;path=/;max-age=3600';return true");
        await using var disposable = new BrowserService(Path.Combine(_output, "dispose-profile"));
        var opened = await disposable.OpenAndSnapshotAsync(_server.Url + "/second");
        Check("dispose-probe-controller-live", disposable.HasController && !opened.Contains("Error:", StringComparison.Ordinal));
        Check("native-profile-isolation", !(await disposable.EvaluateAsync("return document.cookie"))
            .Contains("profileBoundary=", StringComparison.Ordinal));
        await disposable.DisposeAsync();
        await disposable.DisposeAsync();
        Check("native-dispose-idempotent", !disposable.HasController && disposable.Tabs.Count == 0);
        var rejected = false;
        try { await disposable.CaptureScreenshotAsync(); }
        catch (ObjectDisposedException) { rejected = true; }
        Check("disposed-browser-capture-rejected", rejected);
        var layer = window.GetVisualDescendants().OfType<Canvas>()
            .FirstOrDefault(control => control.Name == "NativeWebViewLayer");
        Check("disposed-browser-releases-view", layer?.Children.OfType<NativeWebView>().Count() == retainedBrowser.Tabs.Count,
            $"layer found={layer is not null}, native views={layer?.Children.OfType<NativeWebView>().Count()}");
    }

    private async Task VerifySettingsCookieResetAsync(
        MainViewModel vm, DataStore store, BrowserService browser)
    {
        var layer = browser.GetHostLayer();
        var chatView = layer.Children.OfType<NativeWebView>().Single();
        var existingViews = layer.Children.OfType<NativeWebView>().ToHashSet();
        await using var sibling = new BrowserService();
        await sibling.OpenAndSnapshotAsync(_server.Url + "/second");
        var siblingView = layer.Children.OfType<NativeWebView>().Single(view => !existingViews.Contains(view));
        existingViews.Add(siblingView);
        await using var unrelated = new BrowserService(Path.Combine(_output, "settings-unrelated-profile"));
        await unrelated.OpenAndSnapshotAsync(_server.Url + "/second");
        var unrelatedView = layer.Children.OfType<NativeWebView>().Single(view => !existingViews.Contains(view));
        await using var dormant = new BrowserService();
        var session = new Cookie("settingsResetSession", "fixture-value", "/", "127.0.0.1")
            { Discard = true, HttpOnly = true };
        foreach (var view in new[] { chatView, siblingView, unrelatedView })
            await NativeWebViewCookies.SetAsync(view, [session]);
        Check("settings-reset-session-seeded", (await NativeWebViewCookies.GetAllAsync(chatView))
            .Any(cookie => cookie.Name == session.Name && cookie.Value == session.Value));

        await vm.SettingsVM.ResetBrowserCookiesCommand.ExecuteAsync(null);
        Check("settings-reset-reports-completion",
            !store.Data.Settings.HasImportedBrowserCookies && !string.IsNullOrWhiteSpace(vm.SettingsVM.BrowserCookieStatus),
            vm.SettingsVM.BrowserCookieStatus);
        Check("settings-reset-clears-active-chat-session", !(await NativeWebViewCookies.GetAllAsync(chatView))
            .Any(cookie => cookie.Name == session.Name));
        Check("settings-reset-clears-other-chat-session", !(await NativeWebViewCookies.GetAllAsync(siblingView))
            .Any(cookie => cookie.Name == session.Name));
        Check("settings-reset-preserves-other-profile", (await NativeWebViewCookies.GetAllAsync(unrelatedView))
            .Any(cookie => cookie.Name == session.Name && cookie.Value == session.Value));
        Check("settings-reset-keeps-unopened-peer-uninitialized", !dormant.IsInitialized && !dormant.HasController);

        var syntheticRoot = Path.Combine(_output, "synthetic-chromium-" + Guid.NewGuid().ToString("N"));
        var databaseFolder = Path.Combine(syntheticRoot, "Default", "Network");
        Directory.CreateDirectory(databaseFolder);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(databaseFolder, "Cookies"),
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO meta VALUES ('version', '24');
                CREATE TABLE cookies (
                    host_key TEXT NOT NULL, name TEXT NOT NULL, value TEXT NOT NULL,
                    encrypted_value BLOB NOT NULL, path TEXT NOT NULL,
                    expires_utc INTEGER NOT NULL, is_secure INTEGER NOT NULL,
                    is_httponly INTEGER NOT NULL, has_expires INTEGER NOT NULL,
                    is_persistent INTEGER NOT NULL
                );
                INSERT INTO cookies VALUES (
                    $host, 'lumi_settings_session', 'fixture-session-only',
                    X'', '/', 0, 0, 1, 0, 0
                );
                """;
            command.Parameters.AddWithValue("$host", new Uri(_server.Url).Host);
            await command.ExecuteNonQueryAsync();
        }
        var source = new BrowserCookieService.BrowserInfo("Fixture Chromium", syntheticRoot, "");
        var profile = new BrowserCookieService.BrowserProfile("Fixture", "Default", source);
        var imported = await vm.SettingsVM.BrowserService.ImportCookiesAsync(profile);
        Check("settings-import-reports-source-cookie-count", imported == 1, $"count={imported}");
        foreach (var (name, view) in new[] { ("active-chat", chatView), ("other-chat", siblingView) })
        {
            Check("settings-import-reaches-" + name, (await NativeWebViewCookies.GetAllAsync(view))
                .Any(cookie => cookie is
                {
                    Name: "lumi_settings_session",
                    Value: "fixture-session-only",
                    HttpOnly: true,
                    Path: "/",
                } && cookie.Expires == DateTime.MinValue));
        }
        var otherCookies = await NativeWebViewCookies.GetAllAsync(unrelatedView);
        Check("settings-import-preserves-other-profile",
            otherCookies.Any(cookie => cookie.Name == session.Name && cookie.Value == session.Value)
            && !otherCookies.Any(cookie => cookie.Name == "lumi_settings_session"));
        Check("settings-import-keeps-unopened-peer-uninitialized", !dormant.IsInitialized && !dormant.HasController);
        store.Data.Settings.HasImportedBrowserCookies = true;
    }
#endif

    private async Task VerifyUploadsAsync(BrowserService browser)
    {
        var path = Path.Combine(_output, "upload,fixture.txt");
        var empty = Path.Combine(_output, "empty.txt");
        var content = new string('x', NativeBrowserUpload.ChunkBytes + 37) + "\nUTF-8: \u00e9";
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
        await File.WriteAllBytesAsync(empty, []);
        var paths = "[\"" + JavaScriptEncoderQuote(path) + "\",\"" + JavaScriptEncoderQuote(empty) + "\"]";
        var upload = await browser.DoAsync("upload", "#files", paths);
        Check("upload-multiple-and-comma-name",
            !upload.Contains("Error:", StringComparison.Ordinal)
            && upload.Contains("Uploaded", StringComparison.Ordinal)
            && upload.Contains("upload,fixture.txt", StringComparison.Ordinal)
            && upload.Contains("empty.txt", StringComparison.Ordinal), Clip(upload));
        await WaitAsync(async () => await browser.EvaluateAsync("return String(window.uploadSizes.length)") == "2");
        var expectedLength = new FileInfo(path).Length;
        Check("upload-content-and-empty-file", await browser.EvaluateAsync("return JSON.stringify(window.uploadSizes)")
            == $"[{expectedLength},0]");
        Check("upload-readback-content", await browser.EvaluateAsync("return window.uploadText") == content);
        var single = await browser.DoAsync("upload", "#single-file", paths);
        Check("upload-single-input-rejects-multiple", single.Contains("Error:", StringComparison.Ordinal));
        var missing = await browser.DoAsync("upload", "#files", Path.Combine(_output, "not-there"));
        Check("upload-missing-file-rejected", missing.Contains("Error:", StringComparison.Ordinal));
    }

    private static string JavaScriptEncoderQuote(string value) =>
        System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(value);

    private async Task VerifyDockAsync(BrowserService browser, BrowserView view, Window window, string phase)
    {
        await SettleBoundsAsync(view);
        view.RefreshBounds();
        var layout = view.CalculateNativeWebViewLayout(window);
        if (layout is null)
        {
            Check("dock-" + phase, false, "No layout.");
            return;
        }
        var raw = await browser.EvaluateAsync("return JSON.stringify({width:innerWidth,height:innerHeight})");
        using var data = JsonDocument.Parse(raw);
        var width = data.RootElement.GetProperty("width").GetInt32();
        var height = data.RootElement.GetProperty("height").GetInt32();
        var expectedWidth = layout.Value.Width / layout.Value.RasterizationScale;
        var expectedHeight = layout.Value.Height / layout.Value.RasterizationScale;
        Check("viewport-" + phase, Math.Abs(width - expectedWidth) <= 3 && Math.Abs(height - expectedHeight) <= 3,
            $"page={width}x{height} expected={expectedWidth:F1}x{expectedHeight:F1}");
        var urlBar = view.FindControl<Border>("UrlBar");
        var bottom = urlBar?.TranslatePoint(new Point(0, urlBar.Bounds.Height), window);
        Check("below-chrome-" + phase, bottom is not null &&
            Math.Abs(layout.Value.Y / window.RenderScaling - bottom.Value.Y) <= 2);
    }

    private async Task VerifyScreenshotAsync(BrowserService browser, string phase)
    {
        var screenshot = await browser.CaptureScreenshotAsync();
        await File.WriteAllBytesAsync(Path.Combine(_output, "page-" + phase + ".png"), screenshot.PngBytes);
        Check("real-png-" + phase, screenshot.Width > 100 && screenshot.Height > 100
            && screenshot.PngBytes.Length > 2000 && screenshot.TabId == browser.ActiveTabId,
            $"{screenshot.Width}x{screenshot.Height}, {screenshot.PngBytes.Length} bytes");
        using var image = new Bitmap(new MemoryStream(screenshot.PngBytes));
        Check("png-decodes-" + phase, image.PixelSize.Width == screenshot.Width && image.PixelSize.Height == screenshot.Height);
        var raw = await browser.EvaluateAsync(
            "const r=document.getElementById('color-block').getBoundingClientRect();" +
            "return JSON.stringify({x:r.x+r.width/2,y:r.y+r.height/2,width:innerWidth,height:innerHeight})");
        using var position = JsonDocument.Parse(raw);
        var point = position.RootElement;
        using var pixels = SKBitmap.Decode(screenshot.PngBytes);
        if (pixels is null)
        {
            Check("png-page-pixels-" + phase, false, "PNG did not decode for pixel verification.");
            return;
        }
        var x = (int)(point.GetProperty("x").GetDouble() * screenshot.Width / point.GetProperty("width").GetInt32());
        var y = (int)(point.GetProperty("y").GetDouble() * screenshot.Height / point.GetProperty("height").GetInt32());
        if (x < 0 || y < 0 || x >= pixels.Width || y >= pixels.Height)
        {
            Check("png-page-pixels-" + phase, false, "Fixture marker is outside the captured viewport.");
            return;
        }
        var color = pixels.GetPixel(x, y);
        Check("png-page-pixels-" + phase,
            Math.Abs(color.Red - 237) <= 2 && Math.Abs(color.Green - 106) <= 2 && Math.Abs(color.Blue - 53) <= 2,
            $"page marker at ({x},{y}) = #{color.Red:x2}{color.Green:x2}{color.Blue:x2}");
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, int timeoutMs = 12000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(100);
        }
        throw new TimeoutException("The headed browser fixture did not reach the expected state.");
    }

    private static async Task<T?> WaitValueAsync<T>(Func<Task<T?>> get, Func<T?, bool> matches) where T : class
    {
        T? value = null;
        await WaitAsync(async () => matches(value = await get()));
        return value;
    }

    private static async Task SettleBoundsAsync(Control control)
    {
        var last = control.Bounds;
        var stable = 0;
        await WaitAsync(() =>
        {
            var current = control.Bounds;
            stable = current == last ? stable + 1 : 0;
            last = current;
            return Task.FromResult(stable >= 3 && current.Width > 100 && current.Height > 100);
        });
    }

    private void WriteReport()
    {
        using var stream = File.Create(Path.Combine(_output, "results.json"));
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("platform", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        json.WriteNumber("pid", Environment.ProcessId);
        json.WriteNumber("passed", _checks.Count(check => check.Passed));
        json.WriteNumber("failed", _checks.Count(check => !check.Passed));
        json.WriteStartArray("checks");
        foreach (var check in _checks)
        {
            json.WriteStartObject();
            json.WriteString("name", check.Name);
            json.WriteBoolean("passed", check.Passed);
            json.WriteString("detail", check.Detail);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("operationTimings");
        foreach (var timing in _operationTimings)
        {
            json.WriteStartObject();
            json.WriteString("name", timing.Name);
            json.WriteNumber("milliseconds", timing.Milliseconds);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
        Console.WriteLine($"[SUMMARY] passed={_checks.Count(check => check.Passed)} failed={_checks.Count(check => !check.Passed)} output={_output}");
    }

    private static string Clip(string text) => text.Length > 300 ? text[..300] : text;

    private sealed class LocalServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource<bool> _navigationRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseNavigation = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task NavigationRequested => _navigationRequested.Task;
        internal void ReleaseNavigation() => _releaseNavigation.TrySetResult(true);
        internal string Url { get; private set; } = "";

        internal void Start()
        {
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = RespondAsync(client);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private async Task RespondAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var request = await reader.ReadLineAsync(_stop.Token) ?? "";
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                    var path = request.Split(' ').ElementAtOrDefault(1) ?? "/";
                    var delayed = path.StartsWith("/delayed", StringComparison.Ordinal);
                    if (delayed)
                    {
                        _navigationRequested.TrySetResult(true);
                        await _releaseNavigation.Task.WaitAsync(_stop.Token);
                    }
                    var download = path.StartsWith("/download", StringComparison.Ordinal);
                    var body = download ? Encoding.UTF8.GetBytes("LUMI_NATIVE_DOWNLOAD") :
                        Encoding.UTF8.GetBytes(delayed
                            ? "<!doctype html><title>Navigation Fixture</title><input id=\"query\" value=\"destination\">"
                            : path.StartsWith("/second", StringComparison.Ordinal)
                            ? "<!doctype html><title>Second Fixture</title><h1>Second tab</h1><button>Other</button>"
                            : FixtureHtml);
                    var headers = "HTTP/1.1 200 OK\r\nContent-Type: " +
                        (download ? "application/octet-stream\r\nContent-Disposition: attachment; filename=lumi-native-fixture.bin\r\n"
                            : "text/html; charset=utf-8\r\n") +
                        "Content-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _stop.Token);
                    await stream.WriteAsync(body, _stop.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    if (!_stop.IsCancellationRequested)
                        Console.WriteLine($"[SERVER] {ex.GetType().Name}");
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    private const string FixtureHtml = """
        <!doctype html><html><head><meta charset="utf-8"><title>Native Browser Fixture</title>
        <style>body{margin:0;background:#f6fafb;color:#15213a;font:16px system-ui}
        header{background:#143b68;color:white;padding:20px}main{padding:20px}
        button,input,select,textarea{margin:5px;padding:8px}#color-block{width:180px;height:100px;background:#ed6a35}
        .spacer{height:1000px}</style></head><body><header>Native Browser Fixture</header><main>
        <div id="color-block"></div><button id="alpha" onclick="window.alphaClicks++">Alpha</button>
        <form onsubmit="event.preventDefault();window.submits++">
        <input id="query" placeholder="Search"><button id="submit">Search</button>
        <textarea id="notes"></textarea><select id="choice"><option value="1">One</option><option value="2">Two</option></select>
        <input id="checked" type="checkbox"><input id="password" type="password"></form>
        <label for="files">Upload files</label><input id="files" type="file" multiple>
        <input id="single-file" type="file">
        <a id="popup" href="/second" target="_blank">Open popup</a><a href="/download">Download</a>
        <a id="slow-nav" href="/delayed">Slow navigation</a>
        <div class="spacer"></div><button>Bottom</button></main><script>
        window.alphaClicks=0;window.submits=0;window.uploadSizes=[];window.uploadText='';
        document.getElementById('files').addEventListener('change',async function(){
        window.uploadSizes=Array.from(this.files).map(f=>f.size);
        window.uploadText=this.files.length?await this.files[0].text():'';});
        </script></body></html>
        """;
}
#endif
