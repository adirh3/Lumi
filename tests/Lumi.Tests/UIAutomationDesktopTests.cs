using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lumi.Services;
using Xunit;
using Xunit.Abstractions;

namespace Lumi.Tests;

[CollectionDefinition("Desktop automation", DisableParallelization = true)]
public sealed class DesktopAutomationCollection;

[Collection("Desktop automation")]
public sealed class UIAutomationDesktopTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void NativeDocumentReplacement_ReallySavesInPreparedNotepad()
    {
        RequireInteractiveRun();
        var unavailable = UIAutomationService.DescribeUnavailableInput();
        Skip.If(unavailable is not null, unavailable);
        var handle = Environment.GetEnvironmentVariable("LUMI_UI_AUTOMATION_NOTEPAD_WINDOW");
        var file = Environment.GetEnvironmentVariable("LUMI_UI_AUTOMATION_NOTEPAD_FILE");
        Skip.If(string.IsNullOrWhiteSpace(handle) || string.IsNullOrWhiteSpace(file),
            "Requires an explicitly prepared test-only Notepad window and its disposable file.");
        Assert.NotNull(file);
        Assert.True(File.Exists(file));
        using var service = new UIAutomationService();
        var tree = service.InspectWindow("hwnd:" + handle, 8, 250);
        output.WriteLine(tree);
        var caption = Path.GetFileName(file) + " - Notepad";
        Assert.True(tree.StartsWith($"UI for \"{caption}\"", StringComparison.Ordinal)
            || tree.StartsWith($"UI for \"*{caption}\"", StringComparison.Ordinal), tree);
        var editor = Id(tree, "Document", "Text editor");
        var expected = $"Native editor check {Guid.NewGuid():N}\r\nUnicode: \u05e9\u05dc\u05d5\u05dd \u00e9 \U0001F642\r\nNo trailing newline.";
        using var result = JsonDocument.Parse(service.ExecuteSteps("hwnd:" + handle,
        [
            new() { Action = "type", Target = editor, Value = expected },
            new() { Action = "keys", Target = editor, Value = "Ctrl+S" }
        ], observe: false, allowForeground: true));
        output.WriteLine(result.RootElement.ToString());
        Assert.True(result.RootElement.GetProperty("success").GetBoolean(), result.RootElement.ToString());
        string? lastReadError = null;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            try { return File.ReadAllText(file).ReplaceLineEndings("\n") == expected.ReplaceLineEndings("\n"); }
            catch (IOException ex) { lastReadError = ex.Message; return false; }
        }, TimeSpan.FromSeconds(3)),
            $"The saved file must contain the requested text, not just an updated UIA value. Last transient read error: {lastReadError}");
    }

    [SkippableFact]
    public void Batch_FillsRealNativeControlsAndCommitsTheOrder()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(fixture.Title);
        var steps = new List<UIAutomationStep>();
        foreach (var (name, value) in new[]
        {
            ("First name", "Morgan"), ("Last name", "Reed"),
            ("Email", "morgan.reed@example.test"), ("Company", "Northwind Demo"),
            ("City", "Bristol"), ("Quantity", "4")
        })
            steps.Add(new() { Action = "type", Target = Id(tree, "Edit", name), Value = value });
        steps.Add(new() { Action = "select", Target = Id(tree, "ComboBox", "Product"), Value = "Travel mug" });
        steps.Add(new() { Action = "select", Target = Id(tree, "ComboBox", "Delivery"), Value = "Express" });
        steps.Add(new() { Action = "toggle", Target = Id(tree, "CheckBox", "Send receipt"), Value = "on" });
        steps.Add(new() { Action = "click", Target = Id(tree, "Button", "Submit order") });
        using var result = JsonDocument.Parse(service.ExecuteSteps(fixture.Title, steps.ToArray()));
        output.WriteLine(result.RootElement.ToString());
        Assert.True(result.RootElement.GetProperty("success").GetBoolean(), result.RootElement.ToString());
        Assert.Equal(10, result.RootElement.GetProperty("completedSteps").GetInt32());
        using var state = fixture.ReadState();
        var order = state.RootElement.GetProperty("order");
        Assert.True(order.GetProperty("submitted").GetBoolean());
        Assert.Equal("Morgan", order.GetProperty("firstName").GetString());
        Assert.Equal("Travel mug", order.GetProperty("product").GetString());
        Assert.Equal("Express", order.GetProperty("delivery").GetString());
        Assert.Equal(4, order.GetProperty("quantity").GetInt32());
        Assert.True(order.GetProperty("sendReceipt").GetBoolean());
    }

#if WINDOWS
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoordinateClick_WaitsForActivationButDoesNotClickThroughTopmostWindows(bool coveredByTopmost)
    {
        RequireInteractiveRun();
        Skip.If(GetForegroundWindow() == IntPtr.Zero, "Requires an unlocked desktop for physical input.");
        using var target = new NativeFixture();
        using var service = new UIAutomationService();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var checkbox = automation.FromHandle(new IntPtr(target.WindowHandle))
            .FindFirstDescendant(automation.ConditionFactory.ByAutomationId("Sendreceipt"));
        Assert.NotNull(checkbox);
        var capture = service.CaptureWindow(target.Title, 800);
        var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        System.Drawing.Rectangle bounds;
        try { bounds = checkbox.BoundingRectangle; }
        finally { SetThreadDpiAwarenessContext(previousDpi); }
        var x = (bounds.Left + 10 - capture.Window.Left) * capture.PixelWidth / capture.Window.Width;
        var y = (bounds.Top + bounds.Height / 2 - capture.Window.Top) * capture.PixelHeight / capture.Window.Height;
        using var cover = new NativeFixture();
        cover.ShowAsForegroundSentinel();
        if (!coveredByTopmost)
            Assert.True(SetWindowPos(new IntPtr(cover.WindowHandle), new IntPtr(-2), 0, 0, 0, 0, 0x0013));

        var result = service.ClickAt(capture.CaptureId, x, y, allowForeground: true);
        output.WriteLine($"Image point=({x},{y}); physical checkbox={bounds}; window={capture.Window}; result={result}");
        if (coveredByTopmost)
        {
            Assert.StartsWith("UI automation failed:", result);
            Assert.Equal(FlaUI.Core.Definitions.ToggleState.Off, checkbox.Patterns.Toggle.Pattern.ToggleState.Value);
        }
        else
        {
            Assert.StartsWith("Sent 1 left click(s) at image coordinate", result);
            var expectedPointer = capture.MapPoint(x, y, capture.Window, DateTimeOffset.UtcNow);
            // Compare in physical pixels; a DPI-unaware read is virtualized on scaled displays.
            System.Drawing.Point actualPointer;
            previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try { Assert.True(GetCursorPos(out actualPointer)); }
            finally { SetThreadDpiAwarenessContext(previousDpi); }
            output.WriteLine($"Expected pointer=({expectedPointer.X},{expectedPointer.Y}); actual={actualPointer}");
            Assert.InRange(Math.Abs(actualPointer.X - expectedPointer.X), 0, 1);
            Assert.InRange(Math.Abs(actualPointer.Y - expectedPointer.Y), 0, 1);
            Assert.True(SpinWait.SpinUntil(() => checkbox.Patterns.Toggle.Pattern.ToggleState.Value
                == FlaUI.Core.Definitions.ToggleState.On, TimeSpan.FromSeconds(1)));
            Assert.Contains("superseded", service.ClickAt(capture.CaptureId, x, y), StringComparison.OrdinalIgnoreCase);
        }
    }

    [SkippableFact]
    public void TextPatternVerification_ReadsBeyondThePreviewLimit()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture(textDocument: true);
        using var service = new UIAutomationService();
        var matches = service.FindElements(fixture.Title, "id:TestDocument");
        var id = Regex.Match(matches, @"(?m)^\[(\d+)\]").Groups[1].Value;
        Assert.True(int.TryParse(id, out var elementId), matches);
        var expected = new string('a', 5000) + "\r\nUnicode: \u05e9\u05dc\u05d5\u05dd \u00e9 \U0001F642\r\nLast line.";
        Assert.StartsWith("Text replaced", service.TypeText(elementId, expected));
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var editor = automation.FromHandle(new IntPtr(fixture.WindowHandle))
            .FindFirstDescendant(automation.ConditionFactory.ByAutomationId("TestDocument"));
        Assert.NotNull(editor);
        Assert.True(editor.Patterns.Text.IsSupported);
        // RichEdit exposes its final paragraph terminator through TextPattern.
        Assert.Equal(expected.ReplaceLineEndings("\n") + "\n",
            UIAutomationService.ReadCompleteDocumentText(editor).ReplaceLineEndings("\n"));
    }
#endif

    [SkippableFact]
    public void ExplicitForegroundKeys_ActivateAnEditorCoveredByAnotherNormalWindow()
    {
        RequireInteractiveRun();
        Skip.If(GetForegroundWindow() == IntPtr.Zero, "Requires an unlocked desktop.");
        using var target = new NativeFixture();
        using var cover = new NativeFixture();
        cover.ShowAsForegroundSentinel();
        Assert.True(SetWindowPos(new IntPtr(cover.WindowHandle), new IntPtr(-2), 0, 0, 0, 0, 0x0013));
        Assert.Equal(new IntPtr(cover.WindowHandle), GetForegroundWindow());
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(target.Title);
        var result = service.SendKeys("End", int.Parse(Id(tree, "Edit", "First name")), allowForeground: true);
        Assert.StartsWith("Sent keys:", result);
        Assert.Equal(new IntPtr(target.WindowHandle), GetForegroundWindow());
    }

    [SkippableFact]
    public void EditableCombo_TypesNewTextAndClearsWithoutSelectingAPreset()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        using var opened = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
            [new() { Action = "click", Target = "id:Preferences" }]));
        Assert.True(opened.RootElement.GetProperty("success").GetBoolean(), opened.RootElement.ToString());
        var tree = opened.RootElement.GetProperty("observation").GetString()!;
        var combo = int.Parse(Id(tree, "ComboBox", "Reminder time"));
        using var foregroundApp = GetForegroundWindow() == IntPtr.Zero ? null : new NativeFixture();
        foregroundApp?.ShowAsForegroundSentinel();
        var foreground = GetForegroundWindow();
        var hasPointer = GetCursorPos(out var pointer);

        foreach (var value in new[] { "12:45", "", "12:45" })
        {
            var result = service.TypeText(combo, value);
            Assert.DoesNotContain("failed", result, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"Value: \"{value}\"", service.ReadElement(combo));
            Assert.Equal(foreground, GetForegroundWindow());
            if (hasPointer)
            {
                Assert.True(GetCursorPos(out var currentPointer));
                Assert.Equal(pointer, currentPointer);
            }
        }
        using var saved = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
            [new() { Action = "click", Target = "id:Savepreferences" }]));
        Assert.True(saved.RootElement.GetProperty("success").GetBoolean(), saved.RootElement.ToString());
        using var state = fixture.ReadState();
        Assert.Equal("12:45", state.RootElement.GetProperty("preferences").GetProperty("reminderTime").GetString());
    }

    [SkippableFact]
    public async Task QueuedCaptureAfterDisposal_DoesNotRestoreItsTarget()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        var window = new IntPtr(fixture.WindowHandle);
        ShowWindow(window, 7);
        Assert.True(IsIconic(window));
        var service = new UIAutomationService();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = Task.Run(async () =>
        {
            await release.Task;
            return service.CaptureWindow(fixture.Title);
        });
        service.Dispose();
        release.SetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await capture);
        Assert.True(IsIconic(window));
    }

    [SkippableFact]
    public void BackgroundActionsAndCapture_PreserveForegroundAndPointer()
    {
        RequireInteractiveRun();
        Skip.If(GetForegroundWindow() == IntPtr.Zero, "Unlock the desktop to verify foreground-window preservation.");
        using var target = new NativeFixture();
        using var other = new NativeFixture();
        using var service = new UIAutomationService();
        other.ShowAsForegroundSentinel();
        var foreground = GetForegroundWindow();
        Assert.Equal(new IntPtr(other.WindowHandle), foreground);
        Assert.True(GetCursorPos(out var cursor));
        var tree = service.InspectWindow(target.Title);
        UIAutomationStep[] steps =
        [
            new() { Action = "type", Target = Id(tree, "Edit", "First name"), Value = "Background" },
            new() { Action = "toggle", Target = Id(tree, "CheckBox", "Send receipt"), Value = "on" },
            new() { Action = "select", Target = Id(tree, "ComboBox", "Product"), Value = "Travel mug" },
            new() { Action = "read", Target = Id(tree, "Edit", "First name") },
            // Windows' own UIA proxies for these controls focus the target; the service must not.
            new() { Action = "click", Target = Id(tree, "TabItem", "Scroll") },
            new() { Action = "scroll", Target = "id:ReportEntries", Value = "bottom" },
            new() { Action = "click", Target = Id(tree, "TabItem", "Tree") },
            new() { Action = "expand", Target = "name:Europe" },
            new() { Action = "click", Target = "name:France" },
            new() { Action = "click", Target = Id(tree, "TabItem", "Files") },
            new() { Action = "click", Target = "id:ListViewItem-2" },
            new() { Action = "click", Target = Id(tree, "TabItem", "Grid") },
            new() { Action = "type", Target = "name:Status Row 1", Value = "Paid" }
        ];
        foreach (var step in steps)
        {
            using var result = JsonDocument.Parse(service.ExecuteSteps(target.Title, [step], observe: false));
            Assert.True(result.RootElement.GetProperty("success").GetBoolean(), result.RootElement.ToString());
            Assert.True(foreground == GetForegroundWindow(),
                $"Background {step.Action} changed focus from {foreground} to {GetForegroundWindow()}.");
        }
        var capture = service.CaptureWindow(target.Title, 800);
        Assert.Equal(target.Title, capture.Window.Title);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, capture.PngBytes.Span[..8].ToArray());
        Assert.Equal(foreground, GetForegroundWindow());
        Assert.True(GetCursorPos(out var after));
        Assert.Equal(cursor, after);

        var denied = service.ClickAt(capture.CaptureId, 10, 10);
        Assert.Contains("allowForeground=true", denied);
        Assert.Equal(foreground, GetForegroundWindow());
        Assert.True(GetCursorPos(out after));
        Assert.Equal(cursor, after);
    }

    [SkippableFact]
    public void BackgroundOperations_DoNotRequireAnInteractiveForeground()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var foreground = GetForegroundWindow();
        var tree = service.InspectWindow(fixture.Title);
        var firstName = Id(tree, "Edit", "First name");
        using var result = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "type", Target = firstName, Value = "Without foreground" },
            new() { Action = "toggle", Target = Id(tree, "CheckBox", "Send receipt"), Value = "on" },
            new() { Action = "select", Target = Id(tree, "ComboBox", "Product"), Value = "Travel mug" }
        ]));
        Assert.True(result.RootElement.GetProperty("success").GetBoolean(), result.RootElement.ToString());
        Assert.Contains("Without foreground", service.ReadElement(int.Parse(firstName)));
        var image = service.CaptureWindow(fixture.Title, 800);
        Assert.True(image.PngBytes.Length > 100);
        Assert.Equal(foreground, GetForegroundWindow());
    }

    [SkippableFact]
    public void PreviewCapture_DoesNotRetargetAgentInputContext()
    {
        RequireInteractiveRun();
        using var first = new NativeFixture();
        using var second = new NativeFixture();
        using var service = new UIAutomationService();
        service.InspectWindow(first.Title);
        service.CaptureWindow(second.Title, retainForClick: false);
        Assert.Equal(first.WindowHandle, service.GetWindowInfo()!.Handle);
    }

    [SkippableFact]
    public void CaptureCoordinates_RejectWindowMovementBeforeAnyInput()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var capture = service.CaptureWindow(fixture.Title, 800);
        var foreground = GetForegroundWindow();
        Assert.True(SetWindowPos(new IntPtr(fixture.WindowHandle), IntPtr.Zero,
            capture.Window.Left + 15, capture.Window.Top + 10, 0, 0, 0x0015));
        var result = service.ClickAt(capture.CaptureId, 20, 20, allowForeground: true);
        Assert.Contains("moved, resized", result);
        Assert.Equal(foreground, GetForegroundWindow());
    }

    [SkippableFact]
    public void Screenshot_RestoresMinimizedTargetWithoutForegroundHandoff()
    {
        RequireInteractiveRun();
        Skip.If(GetForegroundWindow() == IntPtr.Zero, "Requires an unlocked desktop to establish the foreground sentinel.");
        using var fixture = new NativeFixture();
        using var foregroundApp = new NativeFixture();
        foregroundApp.ShowAsForegroundSentinel();
        using var service = new UIAutomationService();
        var window = new IntPtr(fixture.WindowHandle);
        ShowWindow(window, 7);
        Assert.True(IsIconic(window));
        var foreground = GetForegroundWindow();
        Assert.Equal(new IntPtr(foregroundApp.WindowHandle), foreground);
        var capture = service.CaptureWindow(fixture.Title, 800);
        Assert.True(capture.PngBytes.Length > 100);
        Assert.False(IsIconic(window));
        Assert.Equal(foreground, GetForegroundWindow());
        Assert.Equal(fixture.Title, capture.Window.Title);
    }

#if WINDOWS
    [SkippableFact]
    public void Capture_UsesTheTargetDpiWithoutBlackPadding()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var capture = service.CaptureWindow(fixture.Title, 800);
        using var stream = new MemoryStream(capture.PngBytes.ToArray());
        using var bitmap = new System.Drawing.Bitmap(stream);
        Assert.Equal(capture.PixelWidth, bitmap.Width);
        Assert.Equal(capture.PixelHeight, bitmap.Height);
        var background = bitmap.GetPixel(bitmap.Width - 24, bitmap.Height - 45);
        Assert.True(background.R > 30 && background.G > 30 && background.B > 30,
            "The fixture's lower-right client area must render, not become black padding from a DPI mismatch.");
    }
#endif

    [SkippableFact]
    public void Batch_OpensAndCompletesAnOwnedModalDialog()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(fixture.Title);
        using var open = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
            [new() { Action = "click", Target = Id(tree, "Button", "Preferences") }]));
        output.WriteLine(open.RootElement.ToString());
        Assert.True(open.RootElement.GetProperty("success").GetBoolean(), open.RootElement.ToString());
        var dialog = open.RootElement.GetProperty("observation").GetString()!;
        using var save = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "type", Target = Id(dialog, "Edit", "Display name"), Value = "Morgan R." },
            new() { Action = "select", Target = Id(dialog, "ComboBox", "Language"), Value = "French" },
            new() { Action = "select", Target = Id(dialog, "ComboBox", "Theme"), Value = "Dark" },
            new() { Action = "select", Target = Id(dialog, "ComboBox", "Reminder time"), Value = "09:30" },
            new() { Action = "toggle", Target = Id(dialog, "CheckBox", "Compact layout"), Value = "on" },
            new() { Action = "toggle", Target = Id(dialog, "CheckBox", "Desktop notifications"), Value = "off" },
            new() { Action = "click", Target = Id(dialog, "Button", "Save preferences") }
        ]));
        output.WriteLine(save.RootElement.ToString());
        Assert.True(save.RootElement.GetProperty("success").GetBoolean(), save.RootElement.ToString());
        using var state = fixture.ReadState();
        var preferences = state.RootElement.GetProperty("preferences");
        Assert.True(preferences.GetProperty("saved").GetBoolean());
        Assert.Equal("French", preferences.GetProperty("language").GetString());
        Assert.Equal("Dark", preferences.GetProperty("theme").GetString());
        Assert.Equal("09:30", preferences.GetProperty("reminderTime").GetString());
        Assert.True(preferences.GetProperty("compactLayout").GetBoolean());
        Assert.False(preferences.GetProperty("desktopNotifications").GetBoolean());
    }

    [SkippableFact]
    public void Inspection_PreservesIdsAndMissingTargetsStopBeforeLaterActions()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var before = service.InspectWindow(fixture.Title);
        var firstName = Id(before, "Edit", "First name");
        service.FindElements(fixture.Title, "Submit order");
        Assert.Equal(firstName, Id(service.InspectWindow(fixture.Title), "Edit", "First name"));
        using var otherWindow = new NativeFixture();

        using var result = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "type", Target = firstName, Value = "Before failure" },
            new() { Action = "click", Target = "This target does not exist", TimeoutMs = 0 },
            new() { Action = "type", Target = firstName, Value = "Must not run" }
        ], observe: false));
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(1, result.RootElement.GetProperty("completedSteps").GetInt32());
        Assert.Equal(2, result.RootElement.GetProperty("failedStep").GetInt32());
        Assert.Contains("Before failure", service.ReadElement(int.Parse(firstName)));
        Assert.DoesNotContain("Must not run", service.ReadElement(int.Parse(firstName)));
    }

    [SkippableFact]
    public void Batch_ExposesVirtualTabsAndSelectsAFilteredCatalogItem()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(fixture.Title);
        using var result = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "click", Target = Id(tree, "TabItem", "Catalog") },
            new() { Action = "type", Target = "id:SearchCatalog", Value = "SKU-187" },
            new() { Action = "click", Target = "SKU-187 - Cedar travel case" },
            new() { Action = "click", Target = "Use selected item" }
        ]));
        output.WriteLine(result.RootElement.ToString());
        Assert.True(result.RootElement.GetProperty("success").GetBoolean(), result.RootElement.ToString());
        using var state = fixture.ReadState();
        Assert.Equal("SKU-187", state.RootElement.GetProperty("catalog").GetProperty("committedId").GetString());
    }

    [SkippableFact]
    public void Type_RejectsAWindowInsteadOfSendingUnscopedText()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(fixture.Title);
        var window = int.Parse(Id(tree, "Window", fixture.Title));
        var result = service.TypeText(window, "Must not type into an arbitrary focused control");
        Assert.Contains("not an editable control", result);
        Assert.DoesNotContain("Must not type", service.ReadElement(int.Parse(Id(tree, "Edit", "First name"))));
    }

    [SkippableFact]
    public void Batch_DoesNotResolveAnElementNumberFromAnotherWindow()
    {
        RequireInteractiveRun();
        using var first = new NativeFixture();
        using var second = new NativeFixture();
        using var service = new UIAutomationService();
        var firstId = Id(service.InspectWindow(first.Title), "Edit", "First name");
        using var result = JsonDocument.Parse(service.ExecuteSteps(second.Title,
            [new() { Action = "type", Target = firstId, Value = "Wrong window" }], observe: false));
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("different window", result.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("Wrong window", service.ReadElement(int.Parse(firstId)));
    }

    [SkippableFact]
    public void DuplicateWindowTitles_RequireAnExplicitHandleBeforeEditing()
    {
        RequireInteractiveRun();
        var title = "Lumi duplicate title " + Guid.NewGuid().ToString("N");
        using var first = new NativeFixture(title: title);
        using var second = new NativeFixture(title: title);
        using var service = new UIAutomationService();
        var ambiguous = service.InspectWindow(title);
        Assert.Contains("ambiguous", ambiguous, StringComparison.OrdinalIgnoreCase);

        var firstTree = service.InspectWindow($"hwnd:{first.WindowHandle}");
        var secondTree = service.InspectWindow($"hwnd:{second.WindowHandle}");
        var firstId = int.Parse(Id(firstTree, "Edit", "First name"));
        var secondId = int.Parse(Id(secondTree, "Edit", "First name"));
        Assert.NotEqual(firstId, secondId);
        Assert.DoesNotContain("failed", service.TypeText(firstId, "First only"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("First only", service.ReadElement(firstId));
        Assert.DoesNotContain("First only", service.ReadElement(secondId));
    }

    [SkippableFact]
    public void DisabledControl_StopsTheBatchBeforeLaterMutations()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(fixture.Title, compact: false);
        var firstName = Id(tree, "Edit", "First name");
        using var result = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "click", Target = "id:Maximize-Restore", TimeoutMs = 0 },
            new() { Action = "type", Target = firstName, Value = "Must not run" }
        ], observe: false));
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, result.RootElement.GetProperty("completedSteps").GetInt32());
        Assert.Equal(1, result.RootElement.GetProperty("failedStep").GetInt32());
        Assert.Contains("disabled", result.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Must not run", service.ReadElement(int.Parse(firstName)));
        using var read = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
            [new() { Action = "read", Target = "id:Maximize-Restore", TimeoutMs = 0 }], observe: false));
        Assert.True(read.RootElement.GetProperty("success").GetBoolean(), read.RootElement.ToString());
        Assert.Contains("Enabled: False", read.RootElement.GetProperty("results")[0].GetProperty("output").GetString());
    }

    [SkippableFact]
    public void CaptureIds_AreSupersededAndInvalidatedByAnActualMutation()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(fixture.Title);
        var first = service.CaptureWindow(fixture.Title, 800);
        var second = service.CaptureWindow(fixture.Title, 800);
        Assert.Contains("superseded", service.ClickAt(first.CaptureId, 10, 10), StringComparison.OrdinalIgnoreCase);
        var field = int.Parse(Id(tree, "Edit", "First name"));
        Assert.DoesNotContain("failed", service.TypeText(field, "Changed after capture"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("superseded", service.ClickAt(second.CaptureId, 10, 10), StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void ClosedDialogControls_CannotModifyTheReopenedDialog()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        using var firstOpen = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
            [new() { Action = "click", Target = "id:Preferences" }]));
        Assert.True(firstOpen.RootElement.GetProperty("success").GetBoolean(), firstOpen.RootElement.ToString());
        var previousField = Id(firstOpen.RootElement.GetProperty("observation").GetString()!, "Edit", "Display name");
        using var close = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
            [new() { Action = "click", Target = "id:Savepreferences" }]));
        Assert.True(close.RootElement.GetProperty("success").GetBoolean(), close.RootElement.ToString());
        using var reopen = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
            [new() { Action = "click", Target = "id:Preferences" }]));
        Assert.True(reopen.RootElement.GetProperty("success").GetBoolean(), reopen.RootElement.ToString());
        var currentField = Id(reopen.RootElement.GetProperty("observation").GetString()!, "Edit", "Display name");
        Assert.NotEqual(previousField, currentField);

        using var rejected = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
            [new() { Action = "type", Target = previousField, Value = "Stale target must not run" }], observe: false));
        Assert.False(rejected.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, rejected.RootElement.GetProperty("completedSteps").GetInt32());
        Assert.DoesNotContain("Stale target must not run", service.ReadElement(int.Parse(currentField)));
    }

    [SkippableFact]
    public void MissingWaitTarget_TimesOutWithoutPerformingFollowingActions()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(fixture.Title);
        var field = Id(tree, "Edit", "First name");
        using var result = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "wait", Target = "A control that will never appear", TimeoutMs = 75 },
            new() { Action = "type", Target = field, Value = "Must not follow timeout" }
        ], observe: false));
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, result.RootElement.GetProperty("completedSteps").GetInt32());
        Assert.Equal(1, result.RootElement.GetProperty("failedStep").GetInt32());
        Assert.Contains("75 ms", result.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("Must not follow timeout", service.ReadElement(int.Parse(field)));
    }

    [SkippableFact]
    public void DelayedWorkflow_WaitsForActualReadinessBeforeApplyingOnce()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(fixture.Title);
        using var result = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "click", Target = Id(tree, "TabItem", "Workflow") },
            new() { Action = "click", Target = "id:Loadreport" },
            new() { Action = "click", Target = "id:Applyreport", TimeoutMs = 5000 },
            new() { Action = "wait", Target = "id:ReportResult", Value = "Report ready: 12 orders; total 480.", TimeoutMs = 0 }
        ]));
        Assert.True(result.RootElement.GetProperty("success").GetBoolean(), result.RootElement.ToString());
        using var state = fixture.ReadState();
        var workflow = state.RootElement.GetProperty("delayed");
        Assert.True(workflow.GetProperty("loadCompleted").GetBoolean());
        Assert.True(workflow.GetProperty("applied").GetBoolean());
        Assert.Equal(1, workflow.GetProperty("applyCount").GetInt32());
        Assert.True(DateTimeOffset.Parse(workflow.GetProperty("appliedAt").GetString()!)
            >= DateTimeOffset.Parse(workflow.GetProperty("loadCompletedAt").GetString()!));
    }

    [SkippableFact]
    public void NativeMenuSaveDialog_RemainsAccessibleAndSavesExactUnicodeText()
    {
        RequireInteractiveRun();
        Skip.If(GetForegroundWindow() == IntPtr.Zero, "Requires an unlocked desktop for native menu activation.");
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(fixture.Title);
        const string expected = "Caf\u00e9 review\r\nOrder REF-7328 \u2014 confirmed.\r\nTotal: \u20ac42";
        using var prepared = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "click", Target = Id(tree, "TabItem", "Workbench") },
            new() { Action = "type", Target = "id:DocumentText", Value = expected },
            new() { Action = "click", Target = "name:File" }
        ], allowForeground: true));
        Assert.True(prepared.RootElement.GetProperty("success").GetBoolean(), prepared.RootElement.ToString());
        using var opened = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "click", Target = "name:Save as" },
            new() { Action = "wait", Target = "id:FileNameControlHost", TimeoutMs = 5000 }
        ], allowForeground: true));
        Assert.True(opened.RootElement.GetProperty("success").GetBoolean(), opened.RootElement.ToString());
        var dialog = opened.RootElement.GetProperty("observation").GetString()!;
        var filename = Id(dialog, "Edit", "File name:");
        using var saved = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "type", Target = filename, Value = "automation-note.txt" },
            new() { Action = "click", Target = "name:Save" }
        ]));
        Assert.True(saved.RootElement.GetProperty("success").GetBoolean(), saved.RootElement.ToString());
        using var state = fixture.ReadState();
        var document = state.RootElement.GetProperty("document");
        Assert.True(document.GetProperty("saved").GetBoolean());
        Assert.Equal(1, document.GetProperty("saveCount").GetInt32());
        var path = document.GetProperty("filePath").GetString()!;
        Assert.Equal(fixture.ArtifactDirectory, Path.GetDirectoryName(path));
        Assert.Equal("automation-note.txt", Path.GetFileName(path));
        try { Assert.Equal(expected.ReplaceLineEndings("\n"), File.ReadAllText(path).ReplaceLineEndings("\n")); }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public void ScrollAction_ReachesTheRealEndBeforeAcknowledging()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(fixture.Title);
        var steps = new List<UIAutomationStep>
        {
            new() { Action = "click", Target = Id(tree, "TabItem", "Scroll") }
        };
        steps.AddRange(Enumerable.Range(0, 20).Select(_ => new UIAutomationStep
            { Action = "scroll", Target = "id:ReportEntries", Value = "down" }));
        steps.Add(new() { Action = "wait", Target = "name:End of report visible. Choose Confirm end.", TimeoutMs = 3000 });
        steps.Add(new() { Action = "click", Target = "id:Confirmend" });
        using var result = JsonDocument.Parse(service.ExecuteSteps(fixture.Title, steps.ToArray()));
        output.WriteLine(result.RootElement.ToString());
        Assert.True(result.RootElement.GetProperty("success").GetBoolean(), result.RootElement.ToString());
        using var state = fixture.ReadState();
        var scroll = state.RootElement.GetProperty("scroll");
        Assert.True(scroll.GetProperty("scrollObserved").GetBoolean());
        Assert.True(scroll.GetProperty("confirmedLastRowVisible").GetBoolean());
        Assert.True(scroll.GetProperty("confirmedTopIndex").GetInt32() > 0);
        Assert.Equal(1, scroll.GetProperty("confirmCount").GetInt32());
    }

    [SkippableFact]
    public void OwnedWindowTransfer_ReadsSourceAndCommitsOnlyToItsDestination()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(fixture.Title);
        using var openTab = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
            [new() { Action = "click", Target = Id(tree, "TabItem", "Transfer") }]));
        Assert.True(openTab.RootElement.GetProperty("success").GetBoolean(), openTab.RootElement.ToString());
        var source = Regex.Match(openTab.RootElement.GetProperty("observation").GetString()!, @"Source reference: (REF-\d+)");
        Assert.True(source.Success, openTab.RootElement.ToString());
        using var transferred = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "click", Target = "id:Opentransfer" },
            new() { Action = "type", Target = "id:Destinationreference", Value = source.Groups[1].Value },
            new() { Action = "click", Target = "id:Commit" }
        ]));
        Assert.True(transferred.RootElement.GetProperty("success").GetBoolean(), transferred.RootElement.ToString());
        using var state = fixture.ReadState();
        var transfer = state.RootElement.GetProperty("transfer");
        Assert.True(transfer.GetProperty("committed").GetBoolean());
        Assert.True(transfer.GetProperty("windowOpen").GetBoolean());
        Assert.Equal(source.Groups[1].Value, transfer.GetProperty("destinationReference").GetString());
        Assert.Equal(1, transfer.GetProperty("commitCount").GetInt32());
    }

    [SkippableFact]
    public void Batch_ResolvesLabelsByActionAndRejectsRealAmbiguity()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        // The label and the field share "First name": a read cannot tell them apart.
        using var ambiguous = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
            [new() { Action = "read", Target = "First name" }], observe: false));
        Assert.False(ambiguous.RootElement.GetProperty("success").GetBoolean());
        var error = ambiguous.RootElement.GetProperty("error").GetString()!;
        Assert.True(error.Contains("ambiguous"), error);
        // Only the field can be typed into, so the action identifies it.
        using var typed = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
            [new() { Action = "type", Target = "First name", Value = "By label" }], observe: false));
        Assert.True(typed.RootElement.GetProperty("success").GetBoolean(), typed.RootElement.ToString());

        var tree = service.InspectWindow(fixture.Title);
        Assert.Contains("By label", service.ReadElement(int.Parse(Id(tree, "Edit", "First name"))));
        var toggle = Id(tree, "CheckBox", "Send receipt");
        using var result = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "toggle", Target = toggle, Value = "on" },
            new() { Action = "toggle", Target = toggle, Value = "on" },
            new() { Action = "read", Target = toggle }
        ], observe: false));
        Assert.True(result.RootElement.GetProperty("success").GetBoolean(), result.RootElement.ToString());
        Assert.Contains("ToggleState: On",
            result.RootElement.GetProperty("results")[2].GetProperty("output").GetString());
    }

    [SkippableFact]
    public void TreeBatch_ExpandsNestedNodesAndAssignsACityQuickly()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(fixture.Title);
        using var result = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            // select without a value selects the target item itself.
            new() { Action = "select", Target = Id(tree, "TabItem", "Tree") },
            new() { Action = "expand", Target = "name:Europe" },
            new() { Action = "expand", Target = "name:Germany" },
            // Clicking or selecting a checkable row selects it; it never checks it.
            new() { Action = "click", Target = "name:Berlin" },
            new() { Action = "select", Target = "name:Hamburg" },
            new() { Action = "click", Target = "id:Assignlocation" }
        ]));
        output.WriteLine(result.RootElement.ToString());
        AssertSucceededQuickly(result);
        using var state = fixture.ReadState();
        var locations = state.RootElement.GetProperty("tree");
        Assert.Equal("Europe/Germany/Hamburg", locations.GetProperty("assignedPath").GetString());
        Assert.Equal(1, locations.GetProperty("assignCount").GetInt32());
        Assert.Equal(0, locations.GetProperty("checkCount").GetInt32());
        // The app's own expand events fired, so lazily populated trees would load their children.
        Assert.True(locations.GetProperty("expandCount").GetInt32() >= 2, state.RootElement.ToString());
    }

    [SkippableFact]
    public void GridBatch_FindsAnOffscreenRowByCellValueAndEditsItQuickly()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(fixture.Title);
        using var opened = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
            [new() { Action = "click", Target = Id(tree, "TabItem", "Grid") }], observe: false));
        AssertSucceededQuickly(opened);
        var found = service.FindElements(fixture.Title, "INV-1042");
        output.WriteLine(found);
        var row = Regex.Match(found, @"""Invoice Row (\d+)""");
        Assert.True(row.Success, found);
        using var edited = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "type", Target = $"name:Status Row {row.Groups[1].Value}", Value = "Paid" },
            new() { Action = "click", Target = "id:Savechanges" }
        ], observe: false));
        output.WriteLine(edited.RootElement.ToString());
        AssertSucceededQuickly(edited);
        using var state = fixture.ReadState();
        var grid = state.RootElement.GetProperty("grid");
        Assert.Equal("INV-1042", grid.GetProperty("changedList").GetString());
        Assert.Equal("Paid", grid.GetProperty("targetStatus").GetString());
        Assert.Equal(1, grid.GetProperty("saveCount").GetInt32());
    }

    [SkippableFact]
    public void WizardBatch_SetsRadioSpinnerAndSliderThenFinishesQuickly()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(fixture.Title);
        using var opened = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "click", Target = Id(tree, "TabItem", "Setup") },
            new() { Action = "click", Target = "id:Startsetup" }
        ]));
        AssertSucceededQuickly(opened);
        Assert.Contains("Choose a plan", opened.RootElement.GetProperty("observation").GetString());
        using var capacity = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "click", Target = "name:Pro" },
            new() { Action = "click", Target = "id:Next" }
        ]));
        AssertSucceededQuickly(capacity);
        output.WriteLine(capacity.RootElement.GetProperty("observation").GetString());
        // The trackbar is described in its own units, not UIA's percentage value.
        Assert.Contains("position=100 range=0-1000", capacity.RootElement.GetProperty("observation").GetString());
        using var finished = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "type", Target = "id:Seats", Value = "12" },
            new() { Action = "type", Target = "id:Storage", Value = "250" },
            // The slider's owner saw the change: its ValueChanged handler updated this label.
            new() { Action = "wait", Target = "id:StorageValue", Value = "Storage: 250 GB", TimeoutMs = 1000 },
            new() { Action = "toggle", Target = "id:Iaccepttheterms", Value = "on" },
            new() { Action = "click", Target = "id:Next" },
            new() { Action = "click", Target = "id:Finish" }
        ]));
        output.WriteLine(finished.RootElement.ToString());
        AssertSucceededQuickly(finished);
        using var state = fixture.ReadState();
        var wizard = state.RootElement.GetProperty("wizard");
        Assert.True(wizard.GetProperty("completed").GetBoolean());
        Assert.Equal("Pro", wizard.GetProperty("plan").GetString());
        Assert.Equal(12, wizard.GetProperty("seats").GetInt32());
        Assert.Equal(250, wizard.GetProperty("storageGb").GetInt32());
        Assert.True(wizard.GetProperty("acceptedTerms").GetBoolean());
        Assert.Equal(1, wizard.GetProperty("finishCount").GetInt32());
    }

    [SkippableFact]
    public void MessageBoxRecovery_ShowsTheSuggestionThenRetriesWithIt()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(fixture.Title);
        using var rejected = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "click", Target = Id(tree, "TabItem", "Account") },
            new() { Action = "type", Target = "id:Username", Value = "morgan_r" },
            new() { Action = "type", Target = "id:Email", Value = "morgan.reed@example.test" },
            new() { Action = "type", Target = "id:Age", Value = "34" },
            new() { Action = "click", Target = "id:Createaccount" }
        ]));
        AssertSucceededQuickly(rejected);
        Assert.Contains("Suggested alternative: morgan_r7", rejected.RootElement.GetProperty("observation").GetString());
        using var created = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "click", Target = "name:OK" },
            new() { Action = "type", Target = "id:Username", Value = "morgan_r7" },
            new() { Action = "click", Target = "id:Createaccount" }
        ]));
        output.WriteLine(created.RootElement.ToString());
        AssertSucceededQuickly(created);
        using var state = fixture.ReadState();
        var account = state.RootElement.GetProperty("account");
        Assert.True(account.GetProperty("created").GetBoolean());
        Assert.Equal("morgan_r7", account.GetProperty("username").GetString());
        Assert.Equal(1, account.GetProperty("rejectedCount").GetInt32());
        Assert.Equal(1, account.GetProperty("createdCount").GetInt32());
    }

    [SkippableFact]
    public void ContextMenu_RightClickExposesItsItemsAndDoubleClickOpensAnItem()
    {
        RequireInteractiveRun();
        var unavailable = UIAutomationService.DescribeUnavailableInput();
        Skip.If(unavailable is not null, unavailable);
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(fixture.Title);
        using var menu = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "click", Target = Id(tree, "TabItem", "Files") },
            new() { Action = "click", Target = "id:ListViewItem-6", Value = "right" }
        ], allowForeground: true));
        output.WriteLine(menu.RootElement.ToString());
        Assert.True(menu.RootElement.GetProperty("success").GetBoolean(), menu.RootElement.ToString());
        var observation = menu.RootElement.GetProperty("observation").GetString()!;
        Assert.Contains("Open menu", observation);
        Assert.Contains("\"Archive\"", observation);
        using var archived = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "click", Target = "name:Archive" },
            new() { Action = "click", Target = "name:Yes", TimeoutMs = 5000 },
            new() { Action = "click", Target = "id:ListViewItem-4", Value = "double" }
        ], allowForeground: true));
        output.WriteLine(archived.RootElement.ToString());
        Assert.True(archived.RootElement.GetProperty("success").GetBoolean(), archived.RootElement.ToString());
        using var state = fixture.ReadState();
        var files = state.RootElement.GetProperty("files");
        Assert.Equal("report-q3.xlsx", files.GetProperty("archivedList").GetString());
        Assert.Equal(0, files.GetProperty("deleteCount").GetInt32());
        Assert.Equal("report-q1.xlsx", files.GetProperty("openedList").GetString());
    }

    [SkippableFact]
    public void PointerOnlyControls_ExplainTheForegroundRequirementForThisSession()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(fixture.Title);
        var result = service.ClickElement(int.Parse(Id(tree, "Text", "Preferences status")));
        Assert.StartsWith("UI automation failed:", result);
        if (UIAutomationService.DescribeUnavailableInput() is { } unavailable)
        {
            // Disconnected/locked sessions accept UIA but no input; say so instead of suggesting a retry.
            Assert.Contains(unavailable, result);
            var keys = service.SendKeys("End", int.Parse(Id(tree, "Edit", "First name")), allowForeground: true);
            Assert.Contains(unavailable, keys);
        }
        else
            Assert.Contains("Retry with allowForeground=true", result);
    }

    [SkippableFact]
    public void Batch_RejectsPhysicalClickGesturesBeforeAnyActionWithoutForegroundPermission()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture();
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(fixture.Title);
        var firstName = Id(tree, "Edit", "First name");
        using var result = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "type", Target = firstName, Value = "Must not run" },
            new() { Action = "click", Target = Id(tree, "Button", "Submit order"), Value = "double" }
        ], observe: false));
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.True(result.RootElement.GetProperty("requiresForeground").GetBoolean());
        Assert.Equal(0, result.RootElement.GetProperty("completedSteps").GetInt32());
        Assert.DoesNotContain("Must not run", service.ReadElement(int.Parse(firstName)));
    }

    [SkippableFact]
    public void WpfBatch_RealizesAVirtualizedRowAndDrivesNativeWpfControls()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture(script: "Start-UIAutomationWpfFixture.ps1");
        using var service = new UIAutomationService();
        var tree = service.InspectWindow(fixture.Title);
        output.WriteLine(tree);
        Assert.DoesNotContain("Order 1873", tree);
        Assert.Contains("value=0 range=0-50", tree);
        using var selected = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            // Offscreen rows of a virtualized list do not exist in the UIA tree until realized.
            new() { Action = "select", Target = "id:Orders", Value = "Order 1873 - Harbor Books" },
            new() { Action = "expand", Target = "id:Advanced" },
            new() { Action = "toggle", Target = "id:Priority", Value = "on" },
            new() { Action = "select", Target = "id:Warehouse", Value = "York" }
        ], observe: false));
        output.WriteLine(selected.RootElement.ToString());
        AssertSucceededQuickly(selected);
        using var applied = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "type", Target = "id:Discount", Value = "15" },
            new() { Action = "click", Target = "id:Apply" },
            new() { Action = "wait", Target = "id:Status", Value = "Dispatch applied to Order 1873 - Harbor Books.", TimeoutMs = 3000 }
        ], observe: false));
        output.WriteLine(applied.RootElement.ToString());
        Assert.True(applied.RootElement.GetProperty("success").GetBoolean(), applied.RootElement.ToString());
        using var state = fixture.ReadState();
        var wpf = state.RootElement.GetProperty("wpf");
        Assert.Equal("Order 1873 - Harbor Books", wpf.GetProperty("selectedOrder").GetString());
        Assert.Equal(15, wpf.GetProperty("discount").GetInt32());
        Assert.True(wpf.GetProperty("priority").GetBoolean());
        Assert.Equal("York", wpf.GetProperty("warehouse").GetString());
        Assert.True(wpf.GetProperty("advancedExpanded").GetBoolean());
        Assert.Equal(1, wpf.GetProperty("applyCount").GetInt32());
    }

    [SkippableFact]
    public void WpfClickByName_RealizesAVirtualizedRow()
    {
        RequireInteractiveRun();
        using var fixture = new NativeFixture(script: "Start-UIAutomationWpfFixture.ps1");
        using var service = new UIAutomationService();
        using var result = JsonDocument.Parse(service.ExecuteSteps(fixture.Title,
        [
            new() { Action = "click", Target = "name:Order 1873 - Harbor Books" },
            new() { Action = "read", Target = "name:Order 1873 - Harbor Books" }
        ], observe: false));
        output.WriteLine(result.RootElement.ToString());
        AssertSucceededQuickly(result);
        Assert.Contains("Selected: True", result.RootElement.GetProperty("results")[1].GetProperty("output").GetString());
    }

    private static void AssertSucceededQuickly(JsonDocument result)
    {
        Assert.True(result.RootElement.GetProperty("success").GetBoolean(), result.RootElement.ToString());
        foreach (var step in result.RootElement.GetProperty("results").EnumerateArray())
            // Windows' Win32 UIA proxies block ~2 s per call when a background-safe path is missed.
            Assert.True(step.GetProperty("elapsedMs").GetDouble() < 1500,
                $"Step {step.GetProperty("step")} ({step.GetProperty("action")}) was slow:\n{result.RootElement}");
    }

    private static string Id(string tree, string type, string name)
    {
        var match = Regex.Match(tree, $@"(?m)^\s*\[(\d+)\] {Regex.Escape(type)} \| ""{Regex.Escape(name)}""(?: \||$)");
        Assert.True(match.Success, $"Missing {type} '{name}' in actual UIA tree:\n{tree}");
        return match.Groups[1].Value;
    }

    private static void RequireInteractiveRun()
        => Skip.IfNot(OperatingSystem.IsWindows()
                      && Environment.GetEnvironmentVariable("LUMI_UI_AUTOMATION_DESKTOP_TESTS") == "1",
            "Opt-in real-desktop tests; run with LUMI_UI_AUTOMATION_DESKTOP_TESTS=1 on an idle Windows desktop.");

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out System.Drawing.Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    internal sealed class NativeFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Lumi-uia-test-" + Guid.NewGuid().ToString("N"));
        private readonly Process _process;
        private readonly string _statePath;
        internal string ArtifactDirectory => _directory;
        public string Title { get; } = "Lumi UIA test " + Guid.NewGuid().ToString("N");
        public long WindowHandle { get { _process.Refresh(); return _process.MainWindowHandle.ToInt64(); } }

        public void ShowAsForegroundSentinel()
        {
            var window = new IntPtr(WindowHandle);
            Assert.True(SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x0043));
            SetForegroundWindow(window);
            if (GetForegroundWindow() != window)
            {
                using var service = new UIAutomationService();
                var tree = service.InspectWindow(Title);
                Assert.StartsWith("Sent keys:", service.SendKeys("End",
                    int.Parse(Id(tree, "Edit", "First name")), allowForeground: true));
            }
            Assert.True(SpinWait.SpinUntil(() => GetForegroundWindow() == window, TimeSpan.FromSeconds(1)),
                "The owned foreground sentinel must be active before measuring background behavior.");
        }

        public NativeFixture(bool textDocument = false, string? title = null, string script = "Start-UIAutomationFixture.ps1")
        {
            if (title is not null) Title = title;
            var root = AppContext.BaseDirectory;
            string? scriptPath = null;
            for (var i = 0; i < 12 && !string.IsNullOrEmpty(root); i++, root = Path.GetDirectoryName(root))
            {
                var candidate = Path.Combine(root, "tools", "automation", script);
                if (File.Exists(candidate)) { scriptPath = candidate; break; }
            }
            if (scriptPath is null)
                throw new FileNotFoundException("Could not locate the checked-in Windows UI automation fixture.", script);
            Directory.CreateDirectory(_directory);
            _statePath = Path.Combine(_directory, "state.json");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _directory
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-STA", "-ExecutionPolicy", "Bypass",
                         "-File", scriptPath, "-Title", Title, "-StatePath", _statePath })
                start.ArgumentList.Add(argument);
            if (textDocument) start.ArgumentList.Add("-TextDocument");
            _process = Process.Start(start) ?? throw new InvalidOperationException("Native fixture did not start.");
            try
            {
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(15) && !_process.HasExited)
                {
                    if (File.Exists(_statePath))
                    {
                        using var state = ReadState();
                        if (state.RootElement.GetProperty("ready").GetBoolean()) return;
                    }
                    Thread.Sleep(25);
                }
                throw new TimeoutException("Native fixture did not become ready.");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public JsonDocument ReadState() => JsonDocument.Parse(File.ReadAllText(_statePath));

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(5000);
            }
            _process.Dispose();
            if (File.Exists(_statePath)) File.Delete(_statePath);
            if (Directory.Exists(_directory) && !Directory.EnumerateFileSystemEntries(_directory).Any())
                Directory.Delete(_directory);
        }
    }
}
