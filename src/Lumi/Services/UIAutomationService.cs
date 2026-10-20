#if WINDOWS
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Identifiers;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace Lumi.Services;

/// <summary>
/// Provides UI Automation capabilities for interacting with any open window on Windows.
/// Uses numbered element IDs (like browser tools) so the LLM can reference elements across calls.
/// </summary>
public sealed partial class UIAutomationService : IDisposable
{
    private static readonly object DesktopLock = new();
    private const int MaxCachedElements = 2048;
    private UIA3Automation? _automation;
    private bool _disposed;
    private readonly Dictionary<int, IndexedElement> _elementCache = new();
    private readonly Dictionary<string, int> _runtimeIds = new();
    private int _nextElementId;
    /// <summary>The window handle from the most recent inspect/find call.</summary>
    private IntPtr _lastWindowHandle;

    private sealed record IndexedElement(AutomationElement Element, IntPtr WindowHandle, string RuntimeKey);

    private UIA3Automation GetAutomation()
    {
        if (_automation is not null) return _automation;
        _automation = new UIA3Automation
        {
            ConnectionTimeout = TimeSpan.FromSeconds(2),
            TransactionTimeout = TimeSpan.FromSeconds(5)
        };
        return _automation;
    }

    // UIA cache requests are thread-local; a whole operation stays synchronous under this lock.
    // The SDK wrappers run these operations on a worker, never on Avalonia's dispatcher.
    private string RunLocked(Func<string> operation)
    {
        lock (DesktopLock)
        {
            try
            {
                ThrowIfDisposed();
                return operation();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[UIAutomation] {ex}");
                return $"UI automation failed: {ex.Message}";
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public string ListWindows() => RunLocked(ListWindowsCore);
    public string InspectWindow(string titleQuery, int depth = 5, int maxElements = 160, bool compact = true)
        => RunLocked(() => InspectWindowCore(titleQuery, depth, maxElements, compact));
    public string FindElements(string titleQuery, string query)
        => RunLocked(() => FindElementsCore(titleQuery, query));
    public string ClickElement(int elementId, bool allowForeground = false)
        => RunLocked(() => WithElement(elementId, target => ClickElementCore(target, allowForeground)));
    public string TypeText(int elementId, string text, bool allowForeground = false)
        => RunLocked(() => WithElement(elementId, target => TypeTextCore(target, text, allowForeground)));
    public string SendKeys(string keys, int? elementId = null, bool allowForeground = false)
        => RunLocked(() => elementId.HasValue
            ? WithElement(elementId.Value, target => SendKeysCore(keys, target, allowForeground))
            : SendKeysCore(keys, allowForeground: allowForeground));
    public string ReadElement(int elementId)
        => RunLocked(() => WithElement(elementId, ReadElementCore));

    private string WithElement(int id, Func<IndexedElement, string> operation)
    {
        var target = GetIndexedElement(id);
        _lastWindowHandle = target.WindowHandle;
        return operation(target);
    }

    // ── Window Listing ──────────────────────────────────────────────────────

    private string ListWindowsCore()
    {
        var windows = new List<(IntPtr Hwnd, string Title, string Process, int Pid, bool Minimized)>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, 512);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                windows.Add((hwnd, title, proc.ProcessName, (int)pid, IsIconic(hwnd)));
            }
            catch (ArgumentException)
            {
                // Process may have exited
            }
            catch (Win32Exception ex)
            {
                Trace.TraceWarning($"[UIAutomation] Cannot identify process {pid}: {ex.Message}");
                windows.Add((hwnd, title, "unknown", (int)pid, IsIconic(hwnd)));
            }

            return true;
        }, IntPtr.Zero);

        if (windows.Count == 0) return "No visible windows found.";

        var result = new StringBuilder();
        result.AppendLine($"Found {windows.Count} open windows:");
        result.AppendLine();
        foreach (var (hwnd, title, process, pid, minimized) in windows)
        {
            result.AppendLine($"- [{process}] \"{title}\" (PID {pid}, hwnd:0x{hwnd:X}{(minimized ? ", minimized" : "")})");
        }
        result.AppendLine("Use a known app title directly with ui_inspect. Minimized targets are restored without activation when inspected; no manual restore is needed.");
        return result.ToString();
    }

    // ── Element Inspection ──────────────────────────────────────────────────

    private string InspectWindowCore(string titleQuery, int depth, int maxElements, bool compact = true)
    {
        var hwnd = PrepareWindow(titleQuery);
        _lastWindowHandle = hwnd;
        depth = Math.Clamp(depth, 1, 8);
        maxElements = Math.Clamp(maxElements, 1, 500);
        var automation = GetAutomation();
        using var cache = CreateCacheRequest().Activate();
        var element = automation.FromHandle(hwnd);
        if (compact)
            return InspectCompact(element, hwnd, maxElements) + DescribeOpenMenus(hwnd);
        var result = new StringBuilder();
        result.AppendLine($"UI for \"{GetWindowTitle(hwnd)}\" (hwnd:0x{hwnd:X}, depth={depth}):");
        var count = 0;
        var inaccessible = 0;
        WalkTree(element, hwnd, result, 0, depth, maxElements, ref count, ref inaccessible);
        result.AppendLine($"{count} elements shown. IDs stay valid across inspections while the controls exist. Use ui_do for a sequence of actions.");
        if (count >= maxElements)
            result.AppendLine("Element limit reached; use ui_find with a specific name, id:AutomationId or type:ControlType.");
        if (inaccessible > 0)
            result.AppendLine($"{inaccessible} inaccessible branches were omitted.");
        return result.ToString();
    }

    private string InspectCompact(AutomationElement root, IntPtr hwnd, int maxElements)
    {
        var result = new StringBuilder();
        result.AppendLine($"UI for \"{GetWindowTitle(hwnd)}\" (hwnd:0x{hwnd:X}, visible controls at all depths):");
        result.AppendLine(FormatElementLine(IndexElement(root, hwnd), root));
        var candidates = new List<AutomationElement>();
        var unavailable = 0;
        var visible = new NotCondition(new PropertyCondition(
            GetAutomation().PropertyLibrary.Element.IsOffscreen, true));
        foreach (var element in root.FindAllDescendants(visible))
        {
            try
            {
                if (IsCompactElement(element))
                    candidates.Add(element);
            }
            catch (Exception ex) when (ex is COMException or FlaUI.Core.Exceptions.FlaUIException)
            {
                unavailable++;
                Trace.TraceInformation($"[UIAutomation] Compact inspection skipped an unavailable element: {ex.Message}");
            }
        }

        var shown = 1;
        // Keep navigation and actions ahead of long article/store text, without changing their IDs.
        foreach (var element in candidates.OrderBy(element => element.ControlType == ControlType.Text))
        {
            if (shown >= maxElements) break;
            try
            {
                var line = FormatElementLine(IndexElement(element, hwnd), element);
                result.AppendLine(line);
                shown++;
            }
            catch (Exception ex) when (ex is COMException or FlaUI.Core.Exceptions.FlaUIException)
            {
                unavailable++;
                Trace.TraceInformation($"[UIAutomation] Compact inspection skipped a changing element: {ex.Message}");
            }
        }
        result.AppendLine($"{shown} of {candidates.Count + 1} relevant controls shown. Use ui_do for known actions; verify the resulting content, not just a selected tab.");
        if (shown < candidates.Count + 1)
            result.AppendLine("Use ui_find for a specific missing target, or increase maxElements.");
        if (unavailable > 0)
            result.AppendLine($"{unavailable} changing/inaccessible elements omitted.");
        return result.ToString();
    }

    private string DescribeOpenMenus(IntPtr window)
    {
        var result = new StringBuilder();
        foreach (var menu in OpenMenus(window))
        {
            result.AppendLine("Open menu (its items can be targeted in this window's ui_do batch):");
            result.Append(InspectCompact(GetAutomation().FromHandle(menu), menu, 40));
        }
        return result.ToString();
    }

    private bool IsCompactElement(AutomationElement element)
    {
        if (element.Properties.IsOffscreen.ValueOrDefault)
            return false;
        var type = element.ControlType;
        if (type is ControlType.Pane or ControlType.Group or ControlType.TitleBar
            or ControlType.ToolBar or ControlType.MenuBar or ControlType.ScrollBar
            or ControlType.Thumb or ControlType.Separator)
            return false;
        if (type == ControlType.Custom)
        {
            var patterns = GetAutomation().PropertyLibrary.PatternAvailability;
            if (!ReadCached<bool>(element, patterns.IsInvokePatternAvailable)
                && !ReadCached<bool>(element, patterns.IsTogglePatternAvailable)
                && !ReadCached<bool>(element, patterns.IsSelectionItemPatternAvailable)
                && !ReadCached<bool>(element, patterns.IsValuePatternAvailable))
                return false;
        }
        return !string.IsNullOrWhiteSpace(element.Properties.Name.ValueOrDefault)
               || !string.IsNullOrWhiteSpace(element.Properties.AutomationId.ValueOrDefault)
               || type is ControlType.Edit or ControlType.Document or ControlType.ComboBox;
    }

    // ── Find Elements ───────────────────────────────────────────────────────

    private string FindElementsCore(string titleQuery, string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var hwnd = PrepareWindow(titleQuery);
        _lastWindowHandle = hwnd;
        var automation = GetAutomation();
        using var cache = CreateCacheRequest().Activate();
        var matches = automation.FromHandle(hwnd).FindAllDescendants(CreateCondition(query, substring: true));
        if (matches.Length == 0)
            return $"No elements found matching \"{query}\". Lists can virtualize offscreen rows: target a row by its exact name in ui_do (it is realized automatically), or scroll the list. Otherwise use ui_inspect to browse the tree.";
        var result = new StringBuilder();
        result.AppendLine($"Found {matches.Length} elements matching \"{query}\" in hwnd:0x{hwnd:X} (showing up to 30):");
        foreach (var element in matches.Take(30))
            result.AppendLine(FormatElementLine(IndexElement(element, hwnd), element));
        return result.ToString();
    }

    // ── Click Element ───────────────────────────────────────────────────────

    private string ClickElementCore(IndexedElement indexed, bool allowForeground = false)
    {
        var element = indexed.Element;
        EnsureEnabled(indexed);
        InvalidateWindowCaptures(indexed.WindowHandle);
        if (element.ControlType == ControlType.MenuItem && IsWinFormsMenuItem(element))
        {
            // ToolStrip Invoke can hold its accessibility provider inside a modal callback,
            // making the resulting file dialog inaccessible until it closes.
            RequireForeground(allowForeground, "This native menu requires pointer activation so its dialog remains accessible.");
            EnsureMenuOwnerFocused(indexed.WindowHandle);
            ClickVerified(indexed);
            Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(80));
            return "Activated the native menu item.";
        }
        // A row's label click selects it; its checkbox changes only through the explicit toggle action.
        // Checkable rows (a checkable Win32 tree reports each node as a CheckBox) expose both patterns.
        var selectableRow = element.Patterns.SelectionItem.IsSupported && element.Patterns.Toggle.IsSupported;
        // Try Toggle pattern for checkboxes
        if (element.Patterns.Toggle.IsSupported && !selectableRow)
        {
            ToggleOnce(indexed);
            var state = element.Patterns.Toggle.Pattern.ToggleState.Value;
            return $"Toggle state: {state}.";
        }

        // SelectionItem on web-backed tabs may only update accessibility state, not dispatch
        // the app's navigation handler. A click must actually activate the tab.
        if (element.ControlType == ControlType.TabItem)
        {
            if (TrySelectNativeTab(indexed))
                return "Selected the native tab without activating the window.";
            if (element.Patterns.SelectionItem.IsSupported && IsNativeTab(element))
            {
                element.Patterns.SelectionItem.Pattern.Select();
                return "Selected the native tab.";
            }
            if (element.Patterns.Invoke.IsSupported)
                return InvokeElement(indexed);
            if (element.Patterns.LegacyIAccessible.IsSupported
                && !string.IsNullOrWhiteSpace(element.Patterns.LegacyIAccessible.Pattern.DefaultAction.ValueOrDefault))
            {
                element.Patterns.LegacyIAccessible.Pattern.DoDefaultAction();
                return "Activated the tab through its native default action.";
            }
            RequireForeground(allowForeground, "This tab exposes no background activation action.");
            EnsureWindowFocused(indexed.WindowHandle);
            if (element.Patterns.ScrollItem.IsSupported)
                element.Patterns.ScrollItem.Pattern.ScrollIntoView();
            ClickVerified(indexed);
            return "Clicked and activated the tab.";
        }

        if (element.ControlType == ControlType.RadioButton && NativeButtonHandle(indexed) != IntPtr.Zero)
        {
            InvokeElement(indexed);
            return "Selected the option.";
        }

        if (element.Patterns.SelectionItem.IsSupported)
        {
            if (!TrySelectNativeListItem(indexed) && !TryNativeTreeItem(indexed, TVM_SELECTITEM, TVGN_CARET))
                SelectItem(element);
            return "Selected.";
        }

        if (element.Patterns.Invoke.IsSupported)
            return InvokeElement(indexed);

        // Try ExpandCollapse pattern for menus/combo boxes
        if (element.Patterns.ExpandCollapse.IsSupported)
        {
            var state = element.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value;
            if (state == ExpandCollapseState.Collapsed)
                element.Patterns.ExpandCollapse.Pattern.Expand();
            else
                element.Patterns.ExpandCollapse.Pattern.Collapse();
            return "Expanded/collapsed.";
        }

        // Fall back to mouse click
        RequireForeground(allowForeground, "This control requires a pointer click.");
        EnsureWindowFocused(indexed.WindowHandle);
        ClickVerified(indexed);
        return "Clicked (mouse).";
    }

    private bool IsNativeTab(AutomationElement element)
    {
        var parent = GetAutomation().TreeWalkerFactory.GetControlViewWalker().GetParent(element);
        var className = parent?.Properties.ClassName.ValueOrDefault;
        return className is not null
            && (className.Equals("SysTabControl32", StringComparison.OrdinalIgnoreCase)
                || className.StartsWith("WindowsForms10.SysTabControl32.", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsWinFormsMenuItem(AutomationElement element)
    {
        var walker = GetAutomation().TreeWalkerFactory.GetControlViewWalker();
        for (var depth = 0; depth < 5 && element is not null; depth++)
        {
            if (string.Equals(element.Properties.FrameworkId.ValueOrDefault, "WinForm", StringComparison.OrdinalIgnoreCase))
                return true;
            element = walker.GetParent(element);
        }
        return false;
    }

    // ── Type Text ───────────────────────────────────────────────────────────

    private string TypeTextCore(IndexedElement indexed, string text, bool allowForeground = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        var element = indexed.Element;
        EnsureEnabled(indexed);
        InvalidateWindowCaptures(indexed.WindowHandle);
        var supportsValue = element.Patterns.Value.IsSupported;
        if (supportsValue && element.Patterns.Value.Pattern.IsReadOnly.Value)
            throw new InvalidOperationException("The target is read-only.");
        if (element.ControlType == ControlType.ComboBox)
        {
            var editor = element.FindFirstChild(GetAutomation().ConditionFactory.ByControlType(ControlType.Edit));
            if (editor is not null)
                return TypeTextCore(GetIndexedElement(IndexElement(editor, indexed.WindowHandle)), text, allowForeground);
            if (TrySelectNativeCombo(indexed, text))
                return $"Selected '{text}' without activating the window.";
        }

        var nativeHandle = element.Properties.NativeWindowHandle.ValueOrDefault;
        var nativeClass = new StringBuilder(256);
        if (nativeHandle != IntPtr.Zero)
            GetClassName(nativeHandle, nativeClass, nativeClass.Capacity);
        // Native Edit's UIA Value provider can focus the target. Edit messages avoid that
        // side effect and retain change notifications and undo for native documents.
        if (element.ControlType is ControlType.Edit or ControlType.Document
            && (nativeHandle == indexed.WindowHandle || IsChild(indexed.WindowHandle, nativeHandle))
            && (nativeClass.ToString().StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)
                || nativeClass.ToString().StartsWith("WindowsForms10.RichEdit", StringComparison.OrdinalIgnoreCase)
                || nativeClass.ToString().Equals("Edit", StringComparison.OrdinalIgnoreCase)
                || nativeClass.ToString().StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase)))
        {
            if (SendMessageTimeout(nativeHandle, 0x00B1, IntPtr.Zero, new IntPtr(-1), 2, 5000, out _) == IntPtr.Zero
                || SendTextMessageTimeout(nativeHandle, 0x00C2, new IntPtr(1), text.ReplaceLineEndings("\r\n"), 2, 5000, out _) == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Native text replacement did not finish. Read the field before retrying.");
            // RichEdit's host may dispatch dirty/save notifications after the native edit returns.
            Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(100));
            if (!element.Properties.IsPassword.ValueOrDefault)
                VerifyNativeText(nativeHandle, text);
            return "Text replaced through the native editor, preserving undo and save notifications.";
        }

        if (element.ControlType == ControlType.Slider)
        {
            if (TrySetNativeSlider(indexed, text) is { } slider)
                return slider;
            if (!supportsValue && element.Patterns.RangeValue.IsSupported)
            {
                if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    throw new ArgumentException("Type a numeric slider value.");
                element.Patterns.RangeValue.Pattern.SetValue(number);
                return $"Set the slider to {element.Patterns.RangeValue.Pattern.Value.Value.ToString(CultureInfo.InvariantCulture)}.";
            }
        }

        if (supportsValue && element.ControlType != ControlType.Document)
        {
            if (!TrySetLegacyValue(element, text))
                element.Patterns.Value.Pattern.SetValue(text);
            VerifyText(element, text);
            return "Text set.";
        }
        if (element.ControlType is not (ControlType.Edit or ControlType.Document))
            throw new InvalidOperationException("Target is not an editable control. Use ui_find with type:Edit or type:Document, not the window or its title bar.");

        RequireForeground(allowForeground, "This editor requires keyboard input.");
        FocusElement(indexed);
        SendKeyChord("Ctrl+A");
        if (text.Length == 0)
            SendKeyChord("Backspace");
        else
            Keyboard.Type(text);
        VerifyText(element, text);
        return "Text replaced using keyboard input.";
    }

    private static void VerifyText(AutomationElement element, string expected)
    {
        if (element.Properties.IsPassword.ValueOrDefault) return;
        if ((element.Patterns.Value.IsSupported || element.Patterns.Text.IsSupported)
            && !string.Equals(ReadValue(element).ReplaceLineEndings("\n"),
                expected.ReplaceLineEndings("\n"), StringComparison.Ordinal))
            throw new InvalidOperationException("The editor did not retain the requested text. Read its current value before retrying.");
    }

    private static void VerifyNativeText(IntPtr window, string expected)
    {
        // TextPattern may include a synthetic final paragraph marker. Verify a native edit
        // through its text API instead, with room for an extra character to detect truncation.
        var capacity = checked(expected.ReplaceLineEndings("\r\n").Length + 2);
        var actual = new StringBuilder(capacity);
        if (ReadTextMessageTimeout(window, 0x000D, new IntPtr(capacity), actual, 2, 5000, out _) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The editor did not return its updated text.");
        if (!string.Equals(actual.ToString().ReplaceLineEndings("\n"),
                expected.ReplaceLineEndings("\n"), StringComparison.Ordinal))
            throw new InvalidOperationException("The editor did not retain the requested text. Read its current value before retrying.");
    }

    // ── Send Keys ───────────────────────────────────────────────────────────

    private string SendKeysCore(string keys, IndexedElement? indexed = null, bool allowForeground = false)
    {
        ValidateKeyChord(keys);
        RequireForeground(allowForeground, "Keyboard shortcuts use the user's foreground keyboard.");
        if (indexed is not null)
            FocusElement(indexed);
        else
            EnsureWindowFocused(GetActiveWindow(_lastWindowHandle));
        InvalidateWindowCaptures(indexed?.WindowHandle ?? _lastWindowHandle);
        SendKeyChord(keys);
        return $"Sent keys: {keys}.";
    }

    // ── Read Element ────────────────────────────────────────────────────────

    private string ReadElementCore(IndexedElement indexed)
    {
        var element = indexed.Element;
        if (element.Properties.IsPassword.ValueOrDefault)
            return "Protected password field; its contents are not exposed.";
        var info = new StringBuilder();
        info.AppendLine("Element:");
        info.AppendLine($"  Type: {element.ControlType}");
        info.AppendLine($"  Name: {element.Properties.Name.ValueOrDefault ?? "(unavailable)"}");
        info.AppendLine($"  AutomationId: {element.Properties.AutomationId.ValueOrDefault ?? "(unavailable)"}");
        info.AppendLine($"  ClassName: {element.Properties.ClassName.ValueOrDefault ?? "(unavailable)"}");
        info.AppendLine($"  Enabled: {element.IsEnabled}");

        try
        {
            var bounds = element.BoundingRectangle;
            info.AppendLine($"  Bounds: {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}");
        }
        catch { }

        // Supported interactions
        var interactions = new List<string>();
        if (element.Patterns.Invoke.IsSupported) interactions.Add("clickable");
        if (element.Patterns.Value.IsSupported) interactions.Add("editable");
        if (element.Patterns.Toggle.IsSupported) interactions.Add("toggleable");
        if (element.Patterns.SelectionItem.IsSupported) interactions.Add("selectable");
        if (element.Patterns.ExpandCollapse.IsSupported) interactions.Add("expandable");
        if (element.Patterns.Scroll.IsSupported) interactions.Add("scrollable");
        if (interactions.Count > 0)
            info.AppendLine($"  Interactions: [{string.Join(", ", interactions)}]");

        // Read value
        if (element.Patterns.Value.IsSupported)
        {
            var val = element.Patterns.Value.Pattern.Value.Value;
            info.AppendLine($"  Value: \"{val}\"");
        }

        // Read text
        if (element.Patterns.Text.IsSupported)
        {
            var text = element.Patterns.Text.Pattern.DocumentRange.GetText(2000);
            info.AppendLine($"  Text: \"{text}\"");
        }

        // Toggle state
        if (element.Patterns.Toggle.IsSupported)
        {
            var state = element.Patterns.Toggle.Pattern.ToggleState.Value;
            info.AppendLine($"  ToggleState: {state}");
        }

        // Selection state
        if (element.Patterns.SelectionItem.IsSupported)
        {
            var isSelected = element.Patterns.SelectionItem.Pattern.IsSelected.Value;
            info.AppendLine($"  Selected: {isSelected}");
        }

        // Range value
        if (element.Patterns.RangeValue.IsSupported)
        {
            var rv = element.Patterns.RangeValue.Pattern;
            info.AppendLine($"  RangeValue: {rv.Value.Value} (min={rv.Minimum.Value}, max={rv.Maximum.Value})");
        }
        if (DescribeNativeSlider(element, indexed.WindowHandle) is { } slider)
            info.AppendLine($"  Slider: {slider} (type a position in this range)");

        return info.ToString();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private IntPtr PrepareWindow(string titleQuery, bool followPopup = true)
    {
        var window = FindWindowByTitle(titleQuery);
        if (IsIconic(window))
        {
            // Unlike SW_RESTORE, SW_SHOWNOACTIVATE restores the target without moving
            // keyboard focus away from the app the user is currently using.
            ShowWindow(window, 4);
            var wait = Stopwatch.StartNew();
            while (IsIconic(window) && wait.ElapsedMilliseconds < 1000)
                Thread.Sleep(10);
            if (IsIconic(window))
                throw new InvalidOperationException("The target did not restore in the background. It may be busy or blocked by an elevated dialog.");
            Trace.TraceInformation($"[UIAutomation] Restored minimized hwnd:0x{window:X} without activation.");
        }
        return followPopup ? GetActiveWindow(window) : window;
    }

    private static void EnsureWindowFocused(IntPtr hwnd)
    {
        RequirePhysicalInput();
        if (!TryFocusWindow(hwnd))
            throw new InvalidOperationException($"Windows did not allow target hwnd:0x{hwnd:X} to take focus (foreground hwnd:0x{GetForegroundWindow():X}). No keyboard input was sent.");
    }

    private static bool TryFocusWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            throw new InvalidOperationException("The target window is no longer available. Inspect it again.");
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);
        if (GetForegroundWindow() == hwnd)
            return true;
        SetForegroundWindow(hwnd);
        if (GetForegroundWindow() != hwnd)
        {
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var targetThread = GetWindowThreadProcessId(hwnd, out _);
            var currentThread = GetCurrentThreadId();
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
            var previousFocus = GetGUIThreadInfo(targetThread, ref info)
                && (info.Focus == hwnd || IsChild(hwnd, info.Focus)) ? info.Focus : hwnd;
            var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread
                && AttachThreadInput(currentThread, foregroundThread, true);
            var attachedTarget = targetThread != currentThread && targetThread != foregroundThread
                && AttachThreadInput(currentThread, targetThread, true);
            try
            {
                BringWindowToTop(hwnd);
                ShowWindow(hwnd, 5);
                SetForegroundWindow(hwnd);
                SetActiveWindow(hwnd);
                SetFocus(hwnd);
                SetFocus(previousFocus);
            }
            finally
            {
                if (attachedTarget && !AttachThreadInput(currentThread, targetThread, false))
                    Trace.TraceWarning("[UIAutomation] Failed to detach the target input queue.");
                if (attachedForeground && !AttachThreadInput(currentThread, foregroundThread, false))
                    Trace.TraceWarning("[UIAutomation] Failed to detach the foreground input queue.");
            }
        }
        var activation = Stopwatch.StartNew();
        while (GetForegroundWindow() != hwnd && activation.ElapsedMilliseconds < 250)
            Thread.Sleep(5);
        return GetForegroundWindow() == hwnd;
    }

    private static void EnsureEnabled(IndexedElement indexed)
    {
        if (!IsWindow(indexed.WindowHandle))
            throw new InvalidOperationException("The target window was closed. Inspect the new window.");
        if (!IsWindowEnabled(indexed.WindowHandle) || !indexed.Element.IsEnabled)
            throw new InvalidOperationException("The target is disabled or blocked by a dialog. Inspect the active dialog.");
    }

    private static string InvokeElement(IndexedElement indexed)
    {
        var nativeHandle = NativeButtonHandle(indexed);
        var isNativeButton = nativeHandle != IntPtr.Zero;

        // Legacy WinForms Invoke can hold its accessibility provider inside ShowDialog,
        // blocking subsequent UIA reads. BM_CLICK runs the same handler on the real UI
        // thread without holding that provider. Observe modal entry instead of waiting for exit.
        var invocation = Task.Run(() =>
        {
            if (isNativeButton)
            {
                if (SendMessageTimeout(nativeHandle, 0x00F5, IntPtr.Zero, IntPtr.Zero, 2, 5000, out _) == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Native button invocation did not finish. Inspect its state before retrying.");
            }
            // A button's MSAA default action is its press, without UIA's client-side focus wait.
            else if (!TryLegacyPress(indexed.Element))
                indexed.Element.Patterns.Invoke.Pattern.Invoke();
        });
        while (!invocation.IsCompleted)
        {
            if (!IsWindow(indexed.WindowHandle)
                || (!IsWindowEnabled(indexed.WindowHandle)
                    && GetActiveWindow(indexed.WindowHandle) != indexed.WindowHandle))
            {
                _ = invocation.ContinueWith(task =>
                {
                    if (task.IsFaulted)
                        Trace.TraceWarning($"[UIAutomation] Provider completed after the observed window transition: {task.Exception?.GetBaseException().Message}");
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return "Invoked; the target window closed or opened an owned modal dialog.";
            }
            Thread.Sleep(5);
        }
        invocation.GetAwaiter().GetResult();
        return "Clicked (Invoke).";
    }

    private static IntPtr NativeButtonHandle(IndexedElement target)
    {
        if (target.Element.ControlType is not (ControlType.Button or ControlType.CheckBox or ControlType.RadioButton))
            return IntPtr.Zero;
        var handle = target.Element.Properties.NativeWindowHandle.ValueOrDefault;
        if (handle == IntPtr.Zero || !IsChild(target.WindowHandle, handle))
            return IntPtr.Zero;
        var name = new StringBuilder(256);
        GetClassName(handle, name, name.Capacity);
        return name.ToString().Equals("Button", StringComparison.OrdinalIgnoreCase)
               || name.ToString().StartsWith("WindowsForms10.BUTTON.", StringComparison.OrdinalIgnoreCase)
            ? handle : IntPtr.Zero;
    }

    private static void ToggleOnce(IndexedElement target)
    {
        if (NativeButtonHandle(target) != IntPtr.Zero)
            InvokeElement(target);
        else if (!TryLegacyToggle(target.Element))
            target.Element.Patterns.Toggle.Pattern.Toggle();
    }

    private static bool TrySelectNativeCombo(IndexedElement target, string value)
    {
        var handle = target.Element.Properties.NativeWindowHandle.ValueOrDefault;
        if (handle == IntPtr.Zero || !IsChild(target.WindowHandle, handle))
            return false;
        var name = new StringBuilder(256);
        GetClassName(handle, name, name.Capacity);
        if (!name.ToString().Equals("ComboBox", StringComparison.OrdinalIgnoreCase)
            && !name.ToString().StartsWith("WindowsForms10.COMBOBOX.", StringComparison.OrdinalIgnoreCase))
            return false;

        if (SendTextMessageTimeout(handle, 0x0158, new IntPtr(-1), value, 2, 5000, out var index) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The combo box did not answer its item lookup.");
        if (index.ToInt64() < 0)
            throw new InvalidOperationException($"The combo box has no option named '{value}'.");
        if (SendMessageTimeout(handle, 0x014E, index, IntPtr.Zero, 2, 5000, out var selected) == IntPtr.Zero
            || selected.ToInt64() < 0)
            throw new InvalidOperationException($"The combo box could not select '{value}'.");

        // Notify the parent exactly as a user selection would, without the provider's Focus call.
        var parent = GetParent(handle);
        var controlId = GetDlgCtrlID(handle) & 0xffff;
        foreach (var notification in new[] { 1, 9 })
            if (SendMessageTimeout(parent, 0x0111, new IntPtr(controlId | notification << 16),
                    handle, 2, 5000, out _) == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The combo box selection notification did not finish.");
        return true;
    }

    private static void FocusElement(IndexedElement indexed)
    {
        EnsureEnabled(indexed);
        RequirePhysicalInput();
        TryFocusWindow(indexed.WindowHandle);
        if (GetForegroundWindow() != indexed.WindowHandle
            && indexed.Element.ControlType is ControlType.Edit or ControlType.Document)
            ClickVerified(indexed);
        indexed.Element.Focus();
        EnsureWindowFocused(indexed.WindowHandle);
        if (indexed.Element.ControlType is ControlType.Edit or ControlType.Document
            && !indexed.Element.Properties.HasKeyboardFocus.ValueOrDefault)
            throw new InvalidOperationException("The text control did not receive keyboard focus. No text was sent.");
    }

    private static void ClickVerified(IndexedElement indexed, MouseButton button = MouseButton.Left, bool doubleClick = false)
    {
        using var dpi = new CaptureDpiScope();
        EnsurePointerAvailable(indexed.WindowHandle);
        if (indexed.Element.Properties.IsOffscreen.ValueOrDefault && indexed.Element.Patterns.ScrollItem.IsSupported)
            indexed.Element.Patterns.ScrollItem.Pattern.ScrollIntoView();
        var point = indexed.Element.GetClickablePoint();
        var hit = WindowFromPoint(point);
        var native = indexed.Element.Properties.NativeWindowHandle.ValueOrDefault;
        var owner = native != IntPtr.Zero ? native : indexed.WindowHandle;
        if (hit != owner && !IsChild(owner, hit)
            && !(indexed.Element.ControlType == ControlType.MenuItem && IsOwnedPopup(indexed.WindowHandle, hit)))
            throw new InvalidOperationException($"The target at ({point.X}, {point.Y}) is covered by another window or control (hwnd:0x{hit:X}). No mouse input was sent.");
        if (doubleClick)
            Mouse.DoubleClick(point, button);
        else
            Mouse.Click(point, button);
    }

    private static void EnsurePointerAvailable(IntPtr window)
    {
        RequirePhysicalInput();
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(0, ref info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not verify mouse input ownership. No mouse input was sent.");
        if (info.Capture != IntPtr.Zero && info.Capture != window
            && !IsChild(window, info.Capture) && !IsOwnedPopup(window, info.Capture))
            throw new InvalidOperationException("Another window has captured the mouse. No mouse input was sent.");
    }

    private static bool IsOwnedPopup(IntPtr window, IntPtr hit)
    {
        var popup = GetAncestor(hit, 2);
        for (var depth = 0; popup != IntPtr.Zero && depth < 8; depth++)
        {
            popup = GetWindow(popup, 4);
            if (popup == window) return true;
        }
        return false;
    }

    private IndexedElement GetIndexedElement(int id)
        => _elementCache.TryGetValue(id, out var indexed)
            ? indexed
            : throw new InvalidOperationException($"Element #{id} is unknown or expired. Run ui_inspect or ui_find; IDs are never reassigned to a different control.");

    private int IndexElement(AutomationElement element, IntPtr hwnd)
    {
        var runtimeId = element.Properties.RuntimeId.ValueOrDefault;
        var runtimeKey = runtimeId is { Length: > 0 }
            ? $"{hwnd}:{string.Join(",", runtimeId)}"
            : $"{hwnd}:unidentified:{_nextElementId + 1}";
        if (_runtimeIds.TryGetValue(runtimeKey, out var existing))
        {
            _elementCache[existing] = new IndexedElement(element, hwnd, runtimeKey);
            return existing;
        }
        if (_elementCache.Count >= MaxCachedElements)
        {
            var oldest = _elementCache.Keys.Min();
            _runtimeIds.Remove(_elementCache[oldest].RuntimeKey);
            _elementCache.Remove(oldest);
        }
        var id = checked(++_nextElementId);
        _runtimeIds[runtimeKey] = id;
        _elementCache[id] = new IndexedElement(element, hwnd, runtimeKey);
        return id;
    }

    private CacheRequest CreateCacheRequest()
    {
        var library = GetAutomation().PropertyLibrary;
        var properties = library.Element;
        var patterns = library.PatternAvailability;
        // Full references are required for subsequent traversal and actions; FlaUI defaults to None.
        var request = new CacheRequest
        {
            TreeScope = TreeScope.Element,
            AutomationElementMode = AutomationElementMode.Full
        };
        foreach (var property in new[]
        {
            properties.RuntimeId, properties.Name, properties.AutomationId, properties.ControlType,
            properties.ClassName, properties.IsEnabled, properties.IsOffscreen, properties.IsPassword,
            properties.NativeWindowHandle,
            patterns.IsInvokePatternAvailable, patterns.IsValuePatternAvailable,
            patterns.IsTogglePatternAvailable, patterns.IsSelectionItemPatternAvailable,
            patterns.IsExpandCollapsePatternAvailable, patterns.IsTextPatternAvailable,
            patterns.IsRangeValuePatternAvailable,
            library.Value.Value, library.Toggle.ToggleState, library.SelectionItem.IsSelected,
            library.RangeValue.Value, library.RangeValue.Minimum, library.RangeValue.Maximum
        })
            request.Add(property);
        return request;
    }

    private void WalkTree(AutomationElement element, IntPtr hwnd, StringBuilder result, int indent,
        int maxDepth, int maxElements, ref int count, ref int inaccessible)
    {
        if (count >= maxElements) return;
        var id = IndexElement(element, hwnd);
        var line = FormatElementLine(id, element);
        result.Append(' ', indent * 2).AppendLine(line);
        count++;
        if (indent >= maxDepth) return;
        try
        {
            foreach (var child in element.FindAllChildren())
            {
                if (count >= maxElements) break;
                try
                {
                    WalkTree(child, hwnd, result, indent + 1, maxDepth, maxElements, ref count, ref inaccessible);
                }
                catch (Exception ex) when (ex is COMException or FlaUI.Core.Exceptions.FlaUIException)
                {
                    inaccessible++;
                    Trace.TraceInformation($"[UIAutomation] Skipping an unavailable element: {ex.Message}");
                    result.AppendLine($"  [unavailable: {Truncate(ex.Message, 160)}]");
                }
            }
        }
        catch (Exception ex) when (ex is COMException or FlaUI.Core.Exceptions.FlaUIException)
        {
            if (indent == 0) throw;
            inaccessible++;
            Trace.TraceWarning($"[UIAutomation] Inaccessible branch: {ex.Message}");
        }
    }

    private string FormatElementLine(int id, AutomationElement element)
    {
        var library = GetAutomation().PropertyLibrary;
        var availability = library.PatternAvailability;
        var parts = new List<string> { element.ControlType.ToString() };
        var name = element.Properties.Name.ValueOrDefault;
        var automationId = element.Properties.AutomationId.ValueOrDefault;
        if (!string.IsNullOrWhiteSpace(name))
            parts.Add($"\"{Truncate(name, 100)}\"");
        if (!string.IsNullOrWhiteSpace(automationId))
            parts.Add($"id:{Truncate(automationId, 100)}");
        if (element.FrameworkAutomationElement.TryGetPropertyValue<bool>(library.Element.IsEnabled, out var enabled) && !enabled)
            parts.Add("disabled");
        if (element.Properties.IsOffscreen.ValueOrDefault) parts.Add("offscreen");
        var tags = new List<string>();
        if (ReadCached<bool>(element, availability.IsInvokePatternAvailable)) tags.Add("clickable");
        if (ReadCached<bool>(element, availability.IsValuePatternAvailable)) tags.Add("editable");
        if (ReadCached<bool>(element, availability.IsTextPatternAvailable)) tags.Add("text");
        if (ReadCached<bool>(element, availability.IsTogglePatternAvailable)) tags.Add("toggleable");
        if (ReadCached<bool>(element, availability.IsSelectionItemPatternAvailable)) tags.Add("selectable");
        if (ReadCached<bool>(element, availability.IsExpandCollapsePatternAvailable)) tags.Add("expandable");
        if (tags.Count > 0) parts.Add($"[{string.Join(", ", tags)}]");
        var slider = DescribeNativeSlider(element, GetIndexedElement(id).WindowHandle);
        if (element.Properties.IsPassword.ValueOrDefault)
            parts.Add("protected");
        else if (slider is not null)
            parts.Add(slider);
        else if (ReadCached<bool>(element, availability.IsRangeValuePatternAvailable))
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"value={ReadCached<double>(element, library.RangeValue.Value)} range={ReadCached<double>(element, library.RangeValue.Minimum)}-{ReadCached<double>(element, library.RangeValue.Maximum)}"));
        else if (ReadCached<bool>(element, availability.IsValuePatternAvailable)
                 && ReadCached<string>(element, library.Value.Value) is { Length: > 0 } value)
            parts.Add($"value=\"{Truncate(value, 100)}\"");
        if (ReadCached<bool>(element, availability.IsTogglePatternAvailable))
            parts.Add(ReadCached<ToggleState>(element, library.Toggle.ToggleState).ToString());
        if (ReadCached<bool>(element, availability.IsSelectionItemPatternAvailable)
            && ReadCached<bool>(element, library.SelectionItem.IsSelected))
            parts.Add("selected");
        return $"[{id}] {string.Join(" | ", parts)}";
    }

    private static T? ReadCached<T>(AutomationElement element, PropertyId property)
        => element.FrameworkAutomationElement.TryGetPropertyValue<T>(property, out var value) ? value : default;

    private ConditionBase CreateCondition(string selector, bool substring)
    {
        var automation = GetAutomation();
        var factory = automation.ConditionFactory;
        if (selector.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
            return factory.ByAutomationId(selector[3..], PropertyConditionFlags.IgnoreCase);
        if (selector.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            return factory.ByName(selector[5..], PropertyConditionFlags.IgnoreCase);
        if (selector.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<ControlType>(selector[5..], true, out var type) || !Enum.IsDefined(type))
                throw new ArgumentException($"Unknown control type '{selector[5..]}'.");
            return factory.ByControlType(type);
        }
        var flags = PropertyConditionFlags.IgnoreCase
                    | (substring ? PropertyConditionFlags.MatchSubstring : PropertyConditionFlags.None);
        var conditions = new List<ConditionBase>
        {
            factory.ByName(selector, flags), factory.ByAutomationId(selector, flags)
        };
        if (substring)
        {
            conditions.Add(factory.ByClassName(selector, flags));
            conditions.Add(new PropertyCondition(automation.PropertyLibrary.Element.HelpText, selector, flags));
            // Grid cells and list rows often have generic names ("Status Row 41"); their data is the value.
            conditions.Add(new PropertyCondition(automation.PropertyLibrary.Value.Value, selector, flags));
            if (Enum.TryParse<ControlType>(selector, true, out var type) && Enum.IsDefined(type))
                conditions.Add(factory.ByControlType(type));
        }
        return new OrCondition(conditions.ToArray());
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 1)] + "…";
    }

    private IntPtr FindWindowByTitle(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.StartsWith("hwnd:", StringComparison.OrdinalIgnoreCase))
        {
            var value = query[5..];
            var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (!long.TryParse(hex ? value[2..] : value, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var handle)
                || !IsWindowVisible(new IntPtr(handle)))
                throw new ArgumentException($"Invalid or unavailable window handle '{query}'.");
            return new IntPtr(handle);
        }
        var matches = new List<(IntPtr Hwnd, string Title)>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;
            if (title.Contains(query, StringComparison.OrdinalIgnoreCase))
                matches.Add((hwnd, title));
            return true;
        }, IntPtr.Zero);
        var exact = matches.Where(m => m.Title.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) matches = exact;
        return matches.Count switch
        {
            1 => matches[0].Hwnd,
            0 => throw new InvalidOperationException($"No window matches '{query}'. Use ui_list_windows."),
            _ => throw new InvalidOperationException($"Window '{query}' is ambiguous. Use a hwnd: selector from ui_list_windows.")
        };
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var text = new StringBuilder(512);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    private static IntPtr GetActiveWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            throw new InvalidOperationException("No live target window. Use ui_inspect or provide its title to ui_do.");
        var popup = GetLastActivePopup(hwnd);
        return popup != IntPtr.Zero && IsWindowVisible(popup) ? popup : hwnd;
    }

    private static VirtualKeyShort? ParseKey(string key)
    {
        return key.ToLowerInvariant() switch
        {
            "ctrl" or "control" => VirtualKeyShort.CONTROL,
            "alt" => VirtualKeyShort.ALT,
            "shift" => VirtualKeyShort.SHIFT,
            "enter" or "return" => VirtualKeyShort.RETURN,
            "tab" => VirtualKeyShort.TAB,
            "escape" or "esc" => VirtualKeyShort.ESCAPE,
            "space" => VirtualKeyShort.SPACE,
            "backspace" or "back" => VirtualKeyShort.BACK,
            "delete" or "del" => VirtualKeyShort.DELETE,
            "home" => VirtualKeyShort.HOME,
            "end" => VirtualKeyShort.END,
            "pageup" or "pgup" => VirtualKeyShort.PRIOR,
            "pagedown" or "pgdown" or "pgdn" => VirtualKeyShort.NEXT,
            "up" => VirtualKeyShort.UP,
            "down" => VirtualKeyShort.DOWN,
            "left" => VirtualKeyShort.LEFT,
            "right" => VirtualKeyShort.RIGHT,
            "f1" => VirtualKeyShort.F1,
            "f2" => VirtualKeyShort.F2,
            "f3" => VirtualKeyShort.F3,
            "f4" => VirtualKeyShort.F4,
            "f5" => VirtualKeyShort.F5,
            "f6" => VirtualKeyShort.F6,
            "f7" => VirtualKeyShort.F7,
            "f8" => VirtualKeyShort.F8,
            "f9" => VirtualKeyShort.F9,
            "f10" => VirtualKeyShort.F10,
            "f11" => VirtualKeyShort.F11,
            "f12" => VirtualKeyShort.F12,
            "insert" or "ins" => VirtualKeyShort.INSERT,
            "printscreen" or "prtsc" => VirtualKeyShort.SNAPSHOT,
            "win" or "windows" => VirtualKeyShort.LWIN,
            // Single characters A-Z
            _ when key.Length == 1 && char.IsAsciiLetterUpper(key[0]) => (VirtualKeyShort)key[0],
            _ when key.Length == 1 && char.IsAsciiLetterLower(key[0]) => (VirtualKeyShort)char.ToUpper(key[0]),
            // Single digits 0-9
            _ when key.Length == 1 && char.IsAsciiDigit(key[0]) => (VirtualKeyShort)key[0],
            _ => null
        };
    }

    public void Dispose()
    {
        lock (DesktopLock)
        {
            if (_disposed) return;
            _disposed = true;
            _elementCache.Clear();
            _runtimeIds.Clear();
            ClearCaptures();
            _automation?.Dispose();
            _automation = null;
        }
    }

    // ── P/Invoke ────────────────────────────────────────────────────────────

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint fromThread, uint toThread, bool attach);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern int GetDlgCtrlID(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(System.Drawing.Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetLastActivePopup(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint message, IntPtr wParam,
        IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendTextMessageTimeout(IntPtr hWnd, uint message, IntPtr wParam,
        string text, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr ReadTextMessageTimeout(IntPtr hWnd, uint message, IntPtr wParam,
        StringBuilder text, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hWnd);
}

#else
namespace Lumi.Services;

/// <summary>
/// Non-Windows stub. Desktop UI Automation relies on the Windows-only FlaUI/UIA3
/// stack, so the ui_* tools are never registered on Linux/macOS and these members
/// are inert. The type still exists so cross-platform callers compile unchanged.
/// </summary>
public sealed partial class UIAutomationService : IDisposable
{
    private const string NotSupported = "Desktop UI automation is only available on Windows.";

    public string ListWindows() => NotSupported;
    public string InspectWindow(string titleQuery, int depth = 5, int maxElements = 160, bool compact = true) => NotSupported;
    public string FindElements(string titleQuery, string query) => NotSupported;
    public string ClickElement(int elementId, bool allowForeground = false) => NotSupported;
    public string TypeText(int elementId, string text, bool allowForeground = false) => NotSupported;
    public string SendKeys(string keys, int? elementId = null, bool allowForeground = false) => NotSupported;
    public string ReadElement(int elementId) => NotSupported;
    internal static string? DescribeUnavailableInput() => NotSupported;
    public void Dispose() { }
}
#endif
