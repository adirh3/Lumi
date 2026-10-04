#if WINDOWS
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;

namespace Lumi.Services;

// Windows' UI Automation client blocks ~2 s on each mutating pattern call when the target cannot
// take focus (disconnected or locked sessions, and Win32 controls whose built-in proxies focus the
// window first, 4 s for a tab); when it can, those proxies may move the user's foreground window.
// The control messages and MSAA calls below perform the same operations, with the owner
// notifications a user action produces, in the background; callers verify and fall back to UIA.
public sealed partial class UIAutomationService
{
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_HSCROLL = 0x0114;
    private const uint WM_VSCROLL = 0x0115;
    private const uint LB_SETCURSEL = 0x0186;
    private const uint LB_FINDSTRINGEXACT = 0x01A2;
    private const uint TCM_GETCURSEL = 0x130B;
    private const uint TCM_SETCURFOCUS = 0x1330;
    private const int LBN_SELCHANGE = 1;
    private const int LBS_OWNERDRAWFIXED = 0x0010;
    private const int LBS_OWNERDRAWVARIABLE = 0x0020;
    private const int LBS_HASSTRINGS = 0x0040;
    private const int LBS_MULTIPLESEL = 0x0008;
    private const int LBS_EXTENDEDSEL = 0x0800;
    private const int WS_HSCROLL = 0x00100000;
    private const int WS_VSCROLL = 0x00200000;
    private const int SB_HORZ = 0;
    private const int SB_VERT = 1;
    private const int SB_PAGEUP = 2;
    private const int SB_PAGEDOWN = 3;
    private const int SB_TOP = 6;
    private const int SB_BOTTOM = 7;
    private const int SB_ENDSCROLL = 8;
    private const uint TBM_GETPOS = 0x0400;
    private const uint TBM_GETRANGEMIN = 0x0401;
    private const uint TBM_GETRANGEMAX = 0x0402;
    private const uint TBM_SETPOS = 0x0405;
    private const int TBS_VERT = 0x0002;
    private const int TB_THUMBPOSITION = 4;
    private const int TB_ENDTRACK = 8;
    private const long WS_POPUP = 0x80000000L;
    private const int SELFLAG_TAKESELECTION = 2;
    private const uint TVM_EXPAND = 0x1102;
    private const uint TVM_GETNEXTITEM = 0x110A;
    private const uint TVM_SELECTITEM = 0x110B;
    private const uint TVM_GETITEMSTATE = 0x1127;
    private const int TVE_COLLAPSE = 1;
    private const int TVE_EXPAND = 2;
    private const int TVGN_ROOT = 0;
    private const int TVGN_NEXT = 1;
    private const int TVGN_CHILD = 4;
    private const int TVGN_CARET = 9;
    private const int TVIS_SELECTED = 0x02;
    private const int TVIS_EXPANDED = 0x20;

    /// <summary>
    /// Sends TVM_EXPAND or TVM_SELECTITEM to the Win32 tree node behind an element, without the UIA
    /// proxy's focus wait. The tree notifies its owner of the selection, and of a node's first
    /// expansion, as UIA's own pattern does. The node is located by its sibling path, which the UIA
    /// tree proxy exposes in native order, and cross-checked against UIA state.
    /// </summary>
    private bool TryNativeTreeItem(IndexedElement item, uint message, int code)
    {
        var automation = GetAutomation();
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        var rows = new PropertyCondition(automation.PropertyLibrary.PatternAvailability.IsSelectionItemPatternAvailable, true);
        var path = new List<int>();
        var current = item.Element;
        var tree = IntPtr.Zero;
        for (var depth = 0; depth < 32 && tree == IntPtr.Zero; depth++)
        {
            if (current.Properties.NativeWindowHandle.ValueOrDefault != IntPtr.Zero) return false;
            var parent = walker.GetParent(current);
            if (parent is null) return false;
            var index = Array.FindIndex(parent.FindAllChildren(rows), sibling => sibling.Equals(current));
            if (index < 0) return false;
            path.Insert(0, index);
            tree = NativeControlHandle(parent, item.WindowHandle, "SysTreeView32");
            current = parent;
        }
        if (tree == IntPtr.Zero) return false;
        var node = SendNative(tree, TVM_GETNEXTITEM, TVGN_ROOT, 0, "The tree did not report its items.");
        for (var level = 0; level < path.Count && node != 0; level++)
        {
            if (level > 0) node = SendNative(tree, TVM_GETNEXTITEM, TVGN_CHILD, node, "The tree did not report its items.");
            for (var sibling = 0; sibling < path[level] && node != 0; sibling++)
                node = SendNative(tree, TVM_GETNEXTITEM, TVGN_NEXT, node, "The tree did not report its items.");
        }
        if (node == 0) return false;
        // The located node must agree with what UIA reports for the element before anything changes.
        var state = SendNative(tree, TVM_GETITEMSTATE, node, TVIS_SELECTED | TVIS_EXPANDED, "The tree did not report its item state.");
        var hasChildren = SendNative(tree, TVM_GETNEXTITEM, TVGN_CHILD, node, "The tree did not report its items.") != 0;
        var expansion = item.Element.Patterns.ExpandCollapse.IsSupported
            ? item.Element.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value : ExpandCollapseState.LeafNode;
        if (((state & TVIS_SELECTED) != 0) != item.Element.Patterns.SelectionItem.Pattern.IsSelected.Value
            || ((state & TVIS_EXPANDED) != 0 && hasChildren) != (expansion == ExpandCollapseState.Expanded)
            || hasChildren != (expansion != ExpandCollapseState.LeafNode))
            return false;
        SendNative(tree, message, code, node, "The tree did not answer.");
        return message == TVM_SELECTITEM
            ? item.Element.Patterns.SelectionItem.Pattern.IsSelected.Value
            : item.Element.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value
              == (code == TVE_EXPAND ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
    }

    /// <summary>Visible context/popup menus opened by the target window's UI thread.</summary>

    /// <summary>Whether the element exposes its MSAA interface, which skips UIA's client-side focus wait.</summary>
    private static bool SupportsLegacy(AutomationElement element) => element.Patterns.LegacyIAccessible.IsSupported;

    /// <summary>Runs a legacy MSAA call and reports whether it verifiably produced the intended state.</summary>
    private static bool TryLegacy(AutomationElement element, Action<FlaUI.Core.Patterns.ILegacyIAccessiblePattern> call, Func<bool> succeeded)
    {
        if (!SupportsLegacy(element)) return false;
        try
        {
            call(element.Patterns.LegacyIAccessible.Pattern);
            // Some providers apply the change on their UI thread shortly after returning.
            var settle = Stopwatch.StartNew();
            while (!succeeded())
            {
                if (settle.ElapsedMilliseconds >= 250) return false;
                Thread.Sleep(10);
            }
            return true;
        }
        catch (Exception ex) when (ex is COMException or FlaUI.Core.Exceptions.FlaUIException)
        {
            Trace.TraceInformation($"[UIAutomation] Legacy MSAA call unavailable; using the UIA pattern: {ex.Message}");
            return false;
        }
    }

    private static void SelectItem(AutomationElement element)
    {
        var selection = element.Patterns.SelectionItem.Pattern;
        if (!TryLegacy(element, legacy => legacy.Select(SELFLAG_TAKESELECTION), () => selection.IsSelected.ValueOrDefault))
            selection.Select();
    }

    private static bool TrySetLegacyValue(AutomationElement element, string value)
        // MSAA exposes range controls as percentages, so only text-like values use this path.
        => element.ControlType is not (ControlType.Slider or ControlType.ProgressBar or ControlType.ScrollBar or ControlType.Spinner)
           && TryLegacy(element, legacy => legacy.SetValue(value),
               () => string.Equals(element.Patterns.Value.Pattern.Value.ValueOrDefault, value, StringComparison.Ordinal));

    private static bool TryLegacyToggle(AutomationElement element)
    {
        // Only check boxes and toggle buttons, whose default action is the toggle itself, and never
        // checkable rows (which also expose SelectionItem): their "Check"/"Double Click" default action
        // may be a simulated click. Only frameworks that apply it synchronously (Win32 toolbar buttons
        // may post it): a late toggle plus the UIA fallback would flip the control twice.
        var framework = element.Properties.FrameworkId.ValueOrDefault;
        if (element.Patterns.SelectionItem.IsSupported
            || !(element.ControlType == ControlType.CheckBox && framework is null or "" or "Win32" or "WinForm" or "WPF" or "XAML"
                 || element.ControlType == ControlType.Button && framework is "WinForm" or "WPF" or "XAML"))
            return false;
        var toggle = element.Patterns.Toggle.Pattern;
        var before = toggle.ToggleState.Value;
        return TryLegacy(element, legacy => legacy.DoDefaultAction(), () => toggle.ToggleState.Value != before);
    }

    /// <summary>
    /// Presses a button through its MSAA default action, which skips UIA's focus wait. Falls back
    /// only when the action is unsupported: after any other failure the press may already have run.
    /// </summary>
    private static bool TryLegacyPress(AutomationElement element)
    {
        if (element.ControlType != ControlType.Button || !SupportsLegacy(element)) return false;
        try
        {
            element.Patterns.LegacyIAccessible.Pattern.DoDefaultAction();
            return true;
        }
        catch (COMException ex) when ((uint)ex.HResult is 0x80020003 or 0x80004001) // DISP_E_MEMBERNOTFOUND, E_NOTIMPL
        {
            return false;
        }
    }

    private string SetExpanded(IndexedElement target, bool expand)
    {
        EnsureEnabled(target);
        var element = target.Element;
        if (!element.Patterns.ExpandCollapse.IsSupported)
            throw new InvalidOperationException("This control cannot be expanded or collapsed.");
        var pattern = element.Patterns.ExpandCollapse.Pattern;
        var desired = expand ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        var state = pattern.ExpandCollapseState.Value;
        if (state == desired || (!expand && state == ExpandCollapseState.LeafNode))
            return expand ? "Already expanded." : "Already collapsed.";
        if (state == ExpandCollapseState.LeafNode)
            throw new InvalidOperationException("This item has no children to expand.");
        if (TryNativeTreeItem(target, TVM_EXPAND, expand ? TVE_EXPAND : TVE_COLLAPSE))
            return expand ? "Expanded." : "Collapsed.";
        // Only a default action that names this operation is safe; others may select or open items.
        var named = SupportsLegacy(element) && string.Equals(element.Patterns.LegacyIAccessible.Pattern.DefaultAction.ValueOrDefault,
            expand ? "Expand" : "Collapse", StringComparison.OrdinalIgnoreCase);
        if (!named || !TryLegacy(element, legacy => legacy.DoDefaultAction(), () => pattern.ExpandCollapseState.Value == desired))
        {
            if (expand) pattern.Expand();
            else pattern.Collapse();
        }
        return expand ? "Expanded." : "Collapsed.";
    }

    /// <summary>
    /// Finds a list/tree/grid row by exact name even when virtualization has not created it yet
    /// (WPF, UWP and WinUI lists), and realizes it so it can be targeted.
    /// </summary>
    private AutomationElement? FindVirtualizedItem(AutomationElement scope, string name)
    {
        var automation = GetAutomation();
        var containers = scope.Patterns.ItemContainer.IsSupported
            ? [scope]
            : scope.FindAllDescendants(new PropertyCondition(
                automation.PropertyLibrary.PatternAvailability.IsItemContainerPatternAvailable, true));
        foreach (var container in containers)
        {
            var items = container.Patterns.ItemContainer.Pattern;
            var item = items.FindItemByProperty(null, automation.PropertyLibrary.Element.Name, name);
            if (item is null) continue;
            if (items.FindItemByProperty(item, automation.PropertyLibrary.Element.Name, name) is not null)
                throw new InvalidOperationException($"More than one item is named '{name}'. Inspect the list and use an element number.");
            if (item.Patterns.VirtualizedItem.IsSupported)
                item.Patterns.VirtualizedItem.Pattern.Realize();
            return item;
        }
        return null;
    }

    /// <summary>Moves a Win32 trackbar in its own units and notifies its owner like a completed drag.</summary>
    private static string? TrySetNativeSlider(IndexedElement target, string text)
    {
        var handle = NativeControlHandle(target.Element, target.WindowHandle, "msctls_trackbar32");
        if (handle == IntPtr.Zero) return null;
        var (position, minimum, maximum) = ReadNativeSlider(handle);
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested))
            throw new ArgumentException($"Type a whole-number slider position from {minimum} to {maximum}.");
        if (requested < minimum || requested > maximum)
            throw new ArgumentOutOfRangeException(nameof(text), $"The slider accepts {minimum} to {maximum}; nothing was changed.");
        if (requested == position)
            return $"The slider is already at {requested} (range {minimum}-{maximum}).";
        SendNative(handle, TBM_SETPOS, 1, requested, "The slider did not answer the position change.");
        // TBM_SETPOS alone is silent; a user's drag ends with these owner notifications.
        var message = (GetWindowLongPtr(handle, -16).ToInt64() & TBS_VERT) != 0 ? WM_VSCROLL : WM_HSCROLL;
        foreach (var code in new[] { TB_THUMBPOSITION | requested << 16, TB_ENDTRACK })
            if (SendMessageTimeout(GetParent(handle), message, new IntPtr(code), handle, 2, 5000, out _) == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The slider change notification did not finish.");
        var actual = ReadNativeSlider(handle).Position;
        if (actual != requested)
            throw new InvalidOperationException($"The slider moved to {actual} instead of {requested}.");
        return $"Set the slider to {requested} (range {minimum}-{maximum}).";
    }

    private static (int Position, int Minimum, int Maximum) ReadNativeSlider(IntPtr handle)
        => ((int)SendNative(handle, TBM_GETPOS, 0, 0, "The slider did not report its position."),
            (int)SendNative(handle, TBM_GETRANGEMIN, 0, 0, "The slider did not report its range."),
            (int)SendNative(handle, TBM_GETRANGEMAX, 0, 0, "The slider did not report its range."));

    /// <summary>Describes a Win32 trackbar in its own units; its UIA Value is a percentage.</summary>
    private static string? DescribeNativeSlider(AutomationElement element, IntPtr window)
    {
        if (element.ControlType != ControlType.Slider) return null;
        var handle = NativeControlHandle(element, window, "msctls_trackbar32");
        if (handle == IntPtr.Zero) return null;
        try
        {
            var (position, minimum, maximum) = ReadNativeSlider(handle);
            return $"position={position} range={minimum}-{maximum}";
        }
        catch (Win32Exception ex)
        {
            // A busy app must not fail the whole inspection; the UIA value is still shown.
            Trace.TraceInformation($"[UIAutomation] Trackbar range unavailable: {ex.Message}");
            return null;
        }
    }

    /// <summary>Visible context/popup menus opened by the target window's UI thread.</summary>
    private IntPtr[] OpenMenus(IntPtr window)
    {
        var thread = GetWindowThreadProcessId(window, out _);
        var candidates = new List<IntPtr>();
        EnumThreadWindows(thread, (hwnd, _) =>
        {
            if (hwnd != window && IsWindowVisible(hwnd) && (GetWindowLongPtr(hwnd, -16).ToInt64() & WS_POPUP) != 0)
                candidates.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return candidates.Where(hwnd => HasNativeClass(hwnd, "#32768")
            || GetAutomation().FromHandle(hwnd).ControlType == ControlType.Menu).ToArray();
    }

    /// <summary>A menu item's popup must stay open: act while its app is foreground instead of activating the popup.</summary>
    private static void EnsureMenuOwnerFocused(IntPtr menuWindow)
    {
        RequirePhysicalInput();
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero
            && GetWindowThreadProcessId(foreground, out _) == GetWindowThreadProcessId(menuWindow, out _))
            return;
        EnsureWindowFocused(menuWindow);
    }

    /// <summary>Returns the element's own HWND when it is a Win32 control of the given base class inside the target window.</summary>
    private static IntPtr NativeControlHandle(AutomationElement? element, IntPtr window, string baseClass)
    {
        var handle = element?.Properties.NativeWindowHandle.ValueOrDefault ?? IntPtr.Zero;
        return handle != IntPtr.Zero && (handle == window || IsChild(window, handle)) && HasNativeClass(handle, baseClass)
            ? handle : IntPtr.Zero;
    }

    /// <summary>Matches a Win32 class or its WinForms superclass, for example ListBox and WindowsForms10.LISTBOX.app.*.</summary>
    private static bool HasNativeClass(IntPtr handle, string baseClass)
    {
        var name = new StringBuilder(256);
        if (GetClassName(handle, name, name.Capacity) == 0) return false;
        var actual = name.ToString();
        return actual.Equals(baseClass, StringComparison.OrdinalIgnoreCase)
               || actual.StartsWith($"WindowsForms10.{baseClass}.", StringComparison.OrdinalIgnoreCase);
    }

    private bool TrySelectNativeTab(IndexedElement target)
    {
        var tabs = GetAutomation().TreeWalkerFactory.GetControlViewWalker().GetParent(target.Element);
        var handle = NativeControlHandle(tabs, target.WindowHandle, "SysTabControl32");
        if (handle == IntPtr.Zero) return false;
        var items = tabs!.FindAllChildren(GetAutomation().ConditionFactory.ByControlType(ControlType.TabItem));
        var index = Array.FindIndex(items, item => item.Equals(target.Element));
        if (index < 0) return false;
        // Without TCS_BUTTONS, moving tab focus also selects the tab and sends TCN_SELCHANGING/
        // TCN_SELCHANGE to its owner, exactly like a click. Button-style tabs keep the UIA fallback.
        SendNative(handle, TCM_SETCURFOCUS, index, 0, "The tab control did not answer the tab change.");
        return SendNative(handle, TCM_GETCURSEL, 0, 0, "The tab control did not report its selected tab.") == index;
    }

    private bool TrySelectNativeListItem(IndexedElement item)
    {
        var list = GetAutomation().TreeWalkerFactory.GetControlViewWalker().GetParent(item.Element);
        var handle = NativeControlHandle(list, item.WindowHandle, "ListBox");
        return handle != IntPtr.Zero && TrySelectNativeListBoxOption(handle, item.Element.Properties.Name.ValueOrDefault);
    }

    private static bool TrySelectNativeListBoxOption(IntPtr list, string? name)
    {
        var style = GetWindowLongPtr(list, -16).ToInt64();
        if (string.IsNullOrEmpty(name) || (style & (LBS_MULTIPLESEL | LBS_EXTENDEDSEL)) != 0
            || ((style & (LBS_OWNERDRAWFIXED | LBS_OWNERDRAWVARIABLE)) != 0 && (style & LBS_HASSTRINGS) == 0))
            return false;
        var index = SendNativeText(list, LB_FINDSTRINGEXACT, -1, name, "The list did not answer its item lookup.");
        // The lookup is case-insensitive and returns the first match: leave duplicates to UIA,
        // which selects the specific element the caller identified.
        if (index < 0 || SendNativeText(list, LB_FINDSTRINGEXACT, index, name, "The list did not answer its item lookup.") != index)
            return false;
        if (SendNative(list, LB_SETCURSEL, index, 0, "The list did not answer the selection.") != index)
            throw new InvalidOperationException($"The list could not select '{name}'.");
        NotifyOwner(list, LBN_SELCHANGE, "The list selection notification did not finish.");
        return true;
    }

    /// <summary>Sends the WM_COMMAND notification a real selection change produces, without the provider's focus call.</summary>
    private static void NotifyOwner(IntPtr control, int notification, string failure)
    {
        var controlId = GetDlgCtrlID(control) & 0xffff;
        if (SendMessageTimeout(GetParent(control), WM_COMMAND, new IntPtr(controlId | notification << 16),
                control, 2, 5000, out _) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), failure);
    }

    /// <summary>Pages or jumps a Win32 scroll bar. Returns null when the control needs the UIA fallback.</summary>
    private static string? TryScrollNative(IndexedElement target, string direction)
    {
        var handle = target.Element.Properties.NativeWindowHandle.ValueOrDefault;
        var vertical = direction is "up" or "down" or "top" or "bottom";
        if (handle == IntPtr.Zero || (handle != target.WindowHandle && !IsChild(target.WindowHandle, handle))
            || (GetWindowLongPtr(handle, -16).ToInt64() & (vertical ? WS_VSCROLL : WS_HSCROLL)) == 0)
            return null;
        var bar = vertical ? SB_VERT : SB_HORZ;
        var forward = direction is "down" or "right" or "bottom";
        if (!TryGetScrollInfo(handle, bar, out var before)) return null;
        var last = Math.Max(before.Min, before.Max - Math.Max((int)before.Page - 1, 0));
        var edge = forward ? "end" : "start";
        if (forward ? before.Position >= last : before.Position <= before.Min)
            return $"Already at the {edge}; nothing moved.";
        var message = vertical ? WM_VSCROLL : WM_HSCROLL;
        var request = direction switch { "top" => SB_TOP, "bottom" => SB_BOTTOM, _ when forward => SB_PAGEDOWN, _ => SB_PAGEUP };
        SendNative(handle, message, request, 0, "The control did not answer the scroll request.");
        SendNative(handle, message, SB_ENDSCROLL, 0, "The control did not finish scrolling.");
        if (!TryGetScrollInfo(handle, bar, out var after) || after.Position == before.Position)
            return null;
        var reached = forward ? after.Position >= last : after.Position <= after.Min;
        return reached ? $"Scrolled {direction} to the {edge}." : $"Scrolled {direction}; not yet at the {edge}.";
    }

    private static bool TryGetScrollInfo(IntPtr handle, int bar, out ScrollInfo info)
    {
        info = new ScrollInfo { Size = (uint)Marshal.SizeOf<ScrollInfo>(), Mask = 0x17 }; // SIF_ALL
        return GetScrollInfo(handle, bar, ref info);
    }

    private static long SendNative(IntPtr handle, uint message, long wParam, long lParam, string failure)
    {
        if (SendMessageTimeout(handle, message, new IntPtr(wParam), new IntPtr(lParam), 2, 5000, out var result) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), failure);
        return result.ToInt64();
    }

    private static long SendNativeText(IntPtr handle, uint message, long wParam, string text, string failure)
    {
        if (SendTextMessageTimeout(handle, message, new IntPtr(wParam), text, 2, 5000, out var result) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), failure);
        return result.ToInt64();
    }

    /// <summary>
    /// Explains why physical keyboard/mouse input cannot reach any app right now, or returns null.
    /// Disconnected remote sessions and locked/secure desktops accept UIA and screenshots but not input.
    /// </summary>
    internal static string? DescribeUnavailableInput()
    {
        if (WTSQuerySessionInformation(IntPtr.Zero, uint.MaxValue, 8, out var buffer, out _))
        {
            try
            {
                if (Marshal.ReadInt32(buffer) != 0)
                    return "The Windows session is disconnected, so keyboard and mouse input cannot reach any app.";
            }
            finally { WTSFreeMemory(buffer); }
        }
        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == IntPtr.Zero)
            return "The PC is locked or showing a secure prompt, so keyboard and mouse input cannot reach the app.";
        try
        {
            var name = new StringBuilder(64);
            if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _)
                || !name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase))
                return "The PC is locked or showing a secure prompt, so keyboard and mouse input cannot reach the app.";
        }
        finally { CloseDesktop(desktop); }
        return GetForegroundWindow() == IntPtr.Zero
            ? "No interactive desktop is available, so keyboard and mouse input cannot reach the app."
            : null;
    }

    private static void RequirePhysicalInput()
    {
        if (DescribeUnavailableInput() is { } reason)
            throw new InvalidOperationException($"{reason} No keyboard or mouse input was sent. Background UI Automation actions and screenshots still work.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInfo
    {
        public uint Size;
        public uint Mask;
        public int Min;
        public int Max;
        public uint Page;
        public int Position;
        public int TrackPosition;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetScrollInfo(IntPtr window, int bar, ref ScrollInfo info);

    private delegate bool EnumThreadWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, EnumThreadWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass,
        out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder text, int size, out uint needed);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
}
#endif
