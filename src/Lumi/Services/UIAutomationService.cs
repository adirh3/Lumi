using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace Lumi.Services;

/// <summary>
/// Provides UI Automation capabilities for interacting with any open window on Windows.
/// Uses numbered element IDs (like browser tools) so the LLM can reference elements across calls.
/// </summary>
public sealed class UIAutomationService : IDisposable
{
    private readonly UIA3Automation _automation = new();
    private readonly Dictionary<int, AutomationElement> _elementCache = new();
    private int _nextElementId;
    /// <summary>The window handle from the most recent inspect/find call.</summary>
    private IntPtr _lastWindowHandle;

    // ── Window Listing ──────────────────────────────────────────────────────

    public string ListWindows()
    {
        var windows = new List<(IntPtr Hwnd, string Title, string Process, int Pid)>();

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
                windows.Add((hwnd, title, proc.ProcessName, (int)pid));
            }
            catch
            {
                // Process may have exited
            }

            return true;
        }, IntPtr.Zero);

        if (windows.Count == 0) return "No visible windows found.";

        var result = new StringBuilder();
        result.AppendLine($"Found {windows.Count} open windows:");
        result.AppendLine();
        foreach (var (_, title, process, pid) in windows)
        {
            result.AppendLine($"- [{process}] \"{title}\" (PID {pid})");
        }
        return result.ToString();
    }

    // ── Element Inspection ──────────────────────────────────────────────────

    public string InspectWindow(string titleQuery, int depth = 3)
    {
        var hwnd = FindWindowByTitle(titleQuery);
        if (hwnd == IntPtr.Zero)
            return $"No window found matching \"{titleQuery}\". Use ui_list_windows to see available windows.";

        // Auto-focus the window so subsequent keyboard actions target it
        BringToFront(hwnd);

        _elementCache.Clear();
        _nextElementId = 1;
        _lastWindowHandle = hwnd;

        var element = _automation.FromHandle(hwnd);
        if (element is null)
            return "Could not access the window's UI tree.";

        var result = new StringBuilder();
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, 512);
        result.AppendLine($"UI tree for \"{sb}\" (depth={depth}):");
        result.AppendLine("Interactive elements are tagged: [clickable] [editable] [toggleable] [selectable]");
        result.AppendLine();

        WalkTree(element, result, 0, depth);

        result.AppendLine();
        result.AppendLine($"Total elements indexed: {_elementCache.Count}. Use element numbers with ui_click, ui_type, ui_read, ui_press_keys.");
        return result.ToString();
    }

    // ── Find Elements ───────────────────────────────────────────────────────

    public string FindElements(string titleQuery, string query)
    {
        var hwnd = FindWindowByTitle(titleQuery);
        if (hwnd == IntPtr.Zero)
            return $"No window found matching \"{titleQuery}\". Use ui_list_windows to see available windows.";

        // Auto-focus and reset cache (new window context)
        BringToFront(hwnd);
        _elementCache.Clear();
        _nextElementId = 1;
        _lastWindowHandle = hwnd;

        var root = _automation.FromHandle(hwnd);
        if (root is null)
            return "Could not access the window's UI tree.";

        var allElements = root.FindAllDescendants();
        var matches = new List<(int Id, AutomationElement Element)>();
        var queryLower = query.ToLowerInvariant();

        foreach (var el in allElements)
        {
            try
            {
                var name = el.Name ?? "";
                var automationId = el.AutomationId ?? "";
                var className = el.ClassName ?? "";
                var controlType = el.ControlType.ToString();
                var helpText = "";
                try { helpText = el.HelpText ?? ""; } catch { }

                if (name.Contains(queryLower, StringComparison.OrdinalIgnoreCase) ||
                    automationId.Contains(queryLower, StringComparison.OrdinalIgnoreCase) ||
                    className.Contains(queryLower, StringComparison.OrdinalIgnoreCase) ||
                    controlType.Contains(queryLower, StringComparison.OrdinalIgnoreCase) ||
                    helpText.Contains(queryLower, StringComparison.OrdinalIgnoreCase))
                {
                    var id = _nextElementId++;
                    _elementCache[id] = el;
                    matches.Add((id, el));

                    if (matches.Count >= 30) break; // Cap results for efficiency
                }
            }
            catch
            {
                // Element may be stale
            }
        }

        if (matches.Count == 0)
            return $"No elements found matching \"{query}\". Try a different search term or use ui_inspect to browse the tree.";

        var result = new StringBuilder();
        result.AppendLine($"Found {matches.Count} elements matching \"{query}\":");
        result.AppendLine();

        foreach (var (id, el) in matches)
        {
            result.AppendLine(FormatElementLine(id, el));
        }

        return result.ToString();
    }

    // ── Click Element ───────────────────────────────────────────────────────

    public string ClickElement(int elementId)
    {
        if (!_elementCache.TryGetValue(elementId, out var element))
            return $"Element #{elementId} not found. Run ui_inspect or ui_find first to index elements.";

        // Ensure the window is in the foreground
        EnsureWindowFocused();

        try
        {
            // Try Invoke pattern first (most reliable for buttons/links)
            if (element.Patterns.Invoke.IsSupported)
            {
                element.Patterns.Invoke.Pattern.Invoke();
                return $"Clicked element #{elementId} (via Invoke). Note: the window's UI may have changed — re-run ui_inspect if you need to interact with new elements.";
            }

            // Try Toggle pattern for checkboxes
            if (element.Patterns.Toggle.IsSupported)
            {
                element.Patterns.Toggle.Pattern.Toggle();
                var state = element.Patterns.Toggle.Pattern.ToggleState.Value;
                return $"Toggled element #{elementId}. New state: {state}.";
            }

            // Try SelectionItem pattern for list items/tabs
            if (element.Patterns.SelectionItem.IsSupported)
            {
                element.Patterns.SelectionItem.Pattern.Select();
                return $"Selected element #{elementId}.";
            }

            // Try ExpandCollapse pattern for menus/combo boxes
            if (element.Patterns.ExpandCollapse.IsSupported)
            {
                var state = element.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value;
                if (state == ExpandCollapseState.Collapsed)
                    element.Patterns.ExpandCollapse.Pattern.Expand();
                else
                    element.Patterns.ExpandCollapse.Pattern.Collapse();
                return $"Expanded/collapsed element #{elementId}.";
            }

            // Fall back to mouse click
            element.Click();
            return $"Clicked element #{elementId} (mouse click). Note: the window's UI may have changed — re-run ui_inspect if you need to interact with new elements.";
        }
        catch (Exception ex)
        {
            return $"Failed to click element #{elementId}: {ex.Message}";
        }
    }

    // ── Type Text ───────────────────────────────────────────────────────────

    public string TypeText(int elementId, string text)
    {
        if (!_elementCache.TryGetValue(elementId, out var element))
            return $"Element #{elementId} not found. Run ui_inspect or ui_find first to index elements.";

        EnsureWindowFocused();

        try
        {
            // Try Value pattern first
            if (element.Patterns.Value.IsSupported)
            {
                element.Patterns.Value.Pattern.SetValue(text);
                return $"Set text on element #{elementId}.";
            }

            // Fall back to focus + keyboard input
            element.Focus();
            System.Threading.Thread.Sleep(100);
            Keyboard.Type(text);
            return $"Typed text into element #{elementId}.";
        }
        catch (Exception ex)
        {
            return $"Failed to type into element #{elementId}: {ex.Message}";
        }
    }

    // ── Send Keys ───────────────────────────────────────────────────────────

    public string SendKeys(string keys, int? elementId = null)
    {
        // Focus specific element if provided
        if (elementId.HasValue)
        {
            if (!_elementCache.TryGetValue(elementId.Value, out var element))
                return $"Element #{elementId} not found. Run ui_inspect or ui_find first to index elements.";

            EnsureWindowFocused();
            try { element.Focus(); } catch { }
            System.Threading.Thread.Sleep(50);
        }
        else
        {
            EnsureWindowFocused();
        }

        // Parse key combinations like "Ctrl+N", "Alt+F4", "Enter", "Tab"
        var keyParts = keys.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = new List<VirtualKeyShort>();
        VirtualKeyShort? mainKey = null;

        foreach (var part in keyParts)
        {
            var vk = ParseKey(part);
            if (vk is null)
                return $"Unknown key: \"{part}\". Use key names like Ctrl, Alt, Shift, Enter, Tab, Escape, F1-F12, Delete, Home, End, PageUp, PageDown, Up, Down, Left, Right, or single characters like A-Z, 0-9.";

            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Control", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Alt", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers.Add(vk.Value);
            }
            else
            {
                mainKey = vk;
            }
        }

        if (mainKey is null && modifiers.Count == 0)
            return "No valid keys specified.";

        try
        {
            // Press modifiers
            foreach (var mod in modifiers)
                Keyboard.Press(mod);

            // Press and release main key
            if (mainKey.HasValue)
            {
                Keyboard.Press(mainKey.Value);
                System.Threading.Thread.Sleep(30);
                Keyboard.Release(mainKey.Value);
            }

            // Release modifiers in reverse order
            for (int i = modifiers.Count - 1; i >= 0; i--)
                Keyboard.Release(modifiers[i]);

            System.Threading.Thread.Sleep(100);
            return $"Sent keys: {keys}. Note: the window's UI may have changed — re-run ui_inspect if you need to interact with new elements.";
        }
        catch (Exception ex)
        {
            // Ensure modifiers are released even on error
            foreach (var mod in modifiers)
                try { Keyboard.Release(mod); } catch { }
            return $"Failed to send keys: {ex.Message}";
        }
    }

    // ── Read Element ────────────────────────────────────────────────────────

    public string ReadElement(int elementId)
    {
        if (!_elementCache.TryGetValue(elementId, out var element))
            return $"Element #{elementId} not found. Run ui_inspect or ui_find first to index elements.";

        try
        {
            var info = new StringBuilder();
            info.AppendLine($"Element #{elementId}:");
            info.AppendLine($"  Type: {element.ControlType}");
            info.AppendLine($"  Name: {element.Name ?? "(none)"}");
            info.AppendLine($"  AutomationId: {element.AutomationId ?? "(none)"}");
            info.AppendLine($"  ClassName: {element.ClassName ?? "(none)"}");
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

            return info.ToString();
        }
        catch (Exception ex)
        {
            return $"Failed to read element #{elementId}: {ex.Message}";
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void BringToFront(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    private void EnsureWindowFocused()
    {
        if (_lastWindowHandle != IntPtr.Zero)
            SetForegroundWindow(_lastWindowHandle);
    }

    private void WalkTree(AutomationElement element, StringBuilder result, int indent, int maxDepth)
    {
        if (indent > maxDepth) return;

        var id = _nextElementId++;
        _elementCache[id] = element;

        var prefix = new string(' ', indent * 2);
        result.AppendLine($"{prefix}{FormatElementLine(id, element)}");

        if (indent >= maxDepth) return;

        try
        {
            var children = element.FindAllChildren();
            foreach (var child in children)
            {
                try
                {
                    WalkTree(child, result, indent + 1, maxDepth);
                }
                catch
                {
                    // Skip inaccessible children
                }
            }
        }
        catch
        {
            // Skip if children can't be enumerated
        }
    }

    private static string FormatElementLine(int id, AutomationElement el)
    {
        var parts = new List<string>();

        try
        {
            var controlType = el.ControlType.ToString();
            parts.Add(controlType);
        }
        catch
        {
            parts.Add("Unknown");
        }

        try
        {
            var name = el.Name;
            if (!string.IsNullOrWhiteSpace(name))
                parts.Add($"\"{Truncate(name, 80)}\"");
        }
        catch { }

        try
        {
            var automationId = el.AutomationId;
            if (!string.IsNullOrWhiteSpace(automationId))
                parts.Add($"[{automationId}]");
        }
        catch { }

        // Show enabled/disabled state
        try
        {
            if (!el.IsEnabled)
                parts.Add("(disabled)");
        }
        catch { }

        // Tag interactive capabilities
        try
        {
            var tags = new List<string>();
            if (el.Patterns.Invoke.IsSupported) tags.Add("clickable");
            if (el.Patterns.Value.IsSupported) tags.Add("editable");
            if (el.Patterns.Toggle.IsSupported) tags.Add("toggleable");
            if (el.Patterns.SelectionItem.IsSupported) tags.Add("selectable");
            if (el.Patterns.ExpandCollapse.IsSupported) tags.Add("expandable");
            if (tags.Count > 0) parts.Add($"[{string.Join(", ", tags)}]");
        }
        catch { }

        // Show value for editable elements
        try
        {
            if (el.Patterns.Value.IsSupported)
            {
                var val = el.Patterns.Value.Pattern.Value.Value;
                if (!string.IsNullOrEmpty(val))
                    parts.Add($"value=\"{Truncate(val, 50)}\"");
            }
        }
        catch { }

        // Show toggle state
        try
        {
            if (el.Patterns.Toggle.IsSupported)
            {
                var state = el.Patterns.Toggle.Pattern.ToggleState.Value;
                parts.Add($"[{state}]");
            }
        }
        catch { }

        return $"[{id}] {string.Join(" | ", parts)}";
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 1)] + "…";
    }

    private IntPtr FindWindowByTitle(string query)
    {
        var queryLower = query.ToLowerInvariant();
        IntPtr bestMatch = IntPtr.Zero;
        int bestScore = int.MaxValue;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, 512);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            var titleLower = title.ToLowerInvariant();

            // Exact match
            if (titleLower == queryLower)
            {
                bestMatch = hwnd;
                bestScore = 0;
                return false; // Stop enumeration
            }

            // Contains match - prefer shorter titles (more specific)
            if (titleLower.Contains(queryLower) && title.Length < bestScore)
            {
                bestMatch = hwnd;
                bestScore = title.Length;
            }

            return true;
        }, IntPtr.Zero);

        return bestMatch;
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
        _elementCache.Clear();
        _automation.Dispose();
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
}
