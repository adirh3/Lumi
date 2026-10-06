using System.Diagnostics;
using System.Text.Json.Nodes;
#if WINDOWS
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
#endif

namespace Lumi.Services;

public sealed partial class UIAutomationService
{
    private sealed class ForegroundRequiredException(string reason) : InvalidOperationException(DescribeForegroundRequirement(reason));

    private static string DescribeForegroundRequirement(string reason)
    {
#if WINDOWS
        if (DescribeUnavailableInput() is { } unavailable)
            return $"{reason} No input was sent, and foreground input is unavailable right now: {unavailable}";
#endif
        return $"{reason} Background mode did not send physical input. Retry with allowForeground=true only if interrupting the desktop is acceptable.";
    }

    private static void RequireForeground(bool allowed, string reason)
    {
        if (!allowed) throw new ForegroundRequiredException(reason);
    }

    private sealed record StepResult(int Step, string Action, bool Success, double ElapsedMs, string? Output, string? Error);

    public string ExecuteSteps(string title, UIAutomationStep[] steps, bool observe = true,
        CancellationToken cancellationToken = default, bool allowForeground = false)
    {
        var clock = Stopwatch.StartNew();
        var results = new List<StepResult>();
        string Serialize(bool success, int? failedStep = null, string? error = null, string? observation = null,
            bool requiresForeground = false)
            => new JsonObject
            {
                ["success"] = success,
                ["completedSteps"] = results.Count(result => result.Success),
                ["failedStep"] = failedStep,
                ["elapsedMs"] = Math.Round(clock.Elapsed.TotalMilliseconds, 2),
                ["error"] = error,
                ["requiresForeground"] = requiresForeground,
                ["results"] = new JsonArray(results.Select(result => (JsonNode)new JsonObject
                {
                    ["step"] = result.Step,
                    ["action"] = result.Action,
                    ["success"] = result.Success,
                    ["elapsedMs"] = Math.Round(result.ElapsedMs, 2),
                    ["output"] = result.Output,
                    ["error"] = result.Error
                }).ToArray()),
                ["observation"] = observation
            }.ToJsonString();

        if (string.IsNullOrWhiteSpace(title))
            return Serialize(false, error: "A window title or hwnd: handle is required.");
        if (steps is null || steps.Length is < 1 or > 32)
            return Serialize(false, error: "Provide 1-32 steps. No actions were performed.");
        for (var i = 0; i < steps.Length; i++)
        {
            var error = steps[i] is null ? "A step cannot be null." : steps[i].Validate();
            if (error is not null)
                return Serialize(false, i + 1, $"{error} No actions were performed.");
            if (!allowForeground && (steps[i].NormalizedAction == "keys"
                    || (steps[i].NormalizedAction == "click" && !string.IsNullOrWhiteSpace(steps[i].Value))))
                return Serialize(false, i + 1,
                    "Keyboard steps and double/right clicks require allowForeground=true. No actions were performed.",
                    requiresForeground: true);
#if WINDOWS
            if (steps[i].NormalizedAction == "keys")
            {
                try { ValidateKeyChord(steps[i].Value!); }
                catch (ArgumentException ex) { return Serialize(false, i + 1, $"{ex.Message} No actions were performed."); }
            }
#endif
        }
#if WINDOWS
        lock (DesktopLock)
        {
            IntPtr window;
            try
            {
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();
                window = PrepareWindow(title, followPopup: false);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[UIAutomation] Batch could not start: {ex.Message}");
                return Serialize(false, error: ex.Message);
            }
            for (var i = 0; i < steps.Length; i++)
            {
                var stepClock = Stopwatch.StartNew();
                var step = steps[i];
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (clock.Elapsed > TimeSpan.FromSeconds(30))
                        throw new TimeoutException("Batch exceeded 30 seconds. Remaining steps were not performed.");
                    var output = ExecuteStep(window, step, cancellationToken, allowForeground);
                    results.Add(new(i + 1, step.NormalizedAction, true, stepClock.Elapsed.TotalMilliseconds, output, null));
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"[UIAutomation] Batch stopped at step {i + 1}: {ex.Message}");
                    results.Add(new(i + 1, step.NormalizedAction, false, stepClock.Elapsed.TotalMilliseconds, null, ex.Message));
                    return Serialize(false, i + 1, ex.Message, requiresForeground: ex is ForegroundRequiredException);
                }
            }
            string? observation = null;
            if (observe)
            {
                try
                {
                    observation = ObserveWindow(window, cancellationToken);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"[UIAutomation] Post-action observation failed: {ex.Message}");
                    return Serialize(false, error: $"Actions completed, but observation failed: {ex.Message}");
                }
            }
            return Serialize(true, observation: observation);
        }
#else
        return Serialize(false, error: NotSupported);
#endif
    }

#if WINDOWS
    private string ObserveWindow(IntPtr window, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWindow(window)) return "The target window has closed.";
            if (!IsWindowEnabled(window) && GetActiveWindow(window) == window
                && clock.ElapsedMilliseconds < 1500)
            {
                cancellationToken.WaitHandle.WaitOne(10);
                continue;
            }
            try
            {
                return InspectWindowCore($"hwnd:0x{window:X}", 5, 120);
            }
            catch (ArgumentException) when (!IsWindow(window))
            {
                return "The target window has closed.";
            }
            catch (Exception ex) when (clock.ElapsedMilliseconds < 500
                && ex is COMException or FlaUI.Core.Exceptions.FlaUIException)
            {
                Trace.TraceInformation($"[UIAutomation] Refreshing a transitioning window: {ex.Message}");
                cancellationToken.WaitHandle.WaitOne(10);
            }
        }
    }

    private string ExecuteStep(IntPtr window, UIAutomationStep step, CancellationToken cancellationToken, bool allowForeground)
    {
        _lastWindowHandle = GetActiveWindow(window);
        if (step.NormalizedAction == "keys" && string.IsNullOrWhiteSpace(step.Target))
            return SendKeysCore(step.Value!, allowForeground: allowForeground);

        var wait = Stopwatch.StartNew();
        var nextRealizationMs = 0L;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Realizing virtualized rows scans item containers and scrolls a found row into view, so do
            // it only periodically, and never for a passive wait.
            var realize = step.NormalizedAction != "wait" && wait.ElapsedMilliseconds >= nextRealizationMs;
            if (realize) nextRealizationMs = wait.ElapsedMilliseconds + 500;
            var target = ResolveTarget(GetActiveWindow(window), step.Target!, realize, step.NormalizedAction);
            if (target is not null)
            {
                if (step.NormalizedAction == "wait")
                {
                    if (step.Value is null)
                        return "Target exists.";
                    var value = ReadValue(target.Element);
                    if (string.Equals(value, step.Value, StringComparison.Ordinal))
                        return $"Verified value: {Truncate(value, 200)}";
                }
                else if (step.NormalizedAction != "read"
                    && (!IsWindowEnabled(target.WindowHandle) || !target.Element.IsEnabled))
                {
                    if (wait.ElapsedMilliseconds >= step.TimeoutMs)
                        throw new InvalidOperationException($"Target '{step.Target}' remained disabled for {step.TimeoutMs} ms. Remaining actions were not performed.");
                }
                else
                {
                    if (step.NormalizedAction is "select" or "toggle" or "scroll" or "expand" or "collapse")
                        InvalidateWindowCaptures(target.WindowHandle);
                    return step.NormalizedAction switch
                    {
                        "click" => string.IsNullOrWhiteSpace(step.Value)
                            ? ClickElementCore(target, allowForeground)
                            : PointerClick(target, step.Value, allowForeground),
                        "type" => TypeTextCore(target, step.Value!, allowForeground),
                        "keys" => SendKeysCore(step.Value!, target, allowForeground),
                        "read" => ReadElementCore(target),
                        "select" => string.IsNullOrEmpty(step.Value)
                            ? SelectTarget(target, allowForeground)
                            : SelectValue(target, step.Value, allowForeground),
                        "toggle" => SetToggle(target, step.Value!),
                        "scroll" => ScrollElement(target, step.Value!),
                        "expand" => SetExpanded(target, expand: true),
                        "collapse" => SetExpanded(target, expand: false),
                        _ => throw new ArgumentException($"Unknown action '{step.Action}'.")
                    };
                }
            }
            if (wait.ElapsedMilliseconds >= step.TimeoutMs) break;
            if (cancellationToken.WaitHandle.WaitOne(Math.Min(25, Math.Max(1, step.TimeoutMs - (int)wait.ElapsedMilliseconds))))
                cancellationToken.ThrowIfCancellationRequested();
        } while (true);

        throw new TimeoutException(step.NormalizedAction == "wait" && step.Value is not null
            ? $"Target '{step.Target}' did not have the expected value within {step.TimeoutMs} ms."
            : $"Target '{step.Target}' was not found within {step.TimeoutMs} ms. Inspect the current UI.");
    }

    private IndexedElement? ResolveTarget(IntPtr window, string selector, bool realizeVirtualized = false, string? action = null)
    {
        if (int.TryParse(selector.TrimStart('#'), out var id))
        {
            var indexed = GetIndexedElement(id);
            if (indexed.WindowHandle != window && !OpenMenus(window).Contains(indexed.WindowHandle))
                throw new InvalidOperationException($"Element #{id} belongs to a different window or an inactive dialog. Inspect the current window.");
            return indexed;
        }

        // An open context menu is what a user's next click targets; a same-named control in the
        // window (a toolbar "Delete") must not run instead. Menus are popups on the app's UI thread.
        var match = OpenMenus(window).Select(menu => FindUniqueTarget(menu, selector, action)).FirstOrDefault(found => found is not null)
                    ?? FindUniqueTarget(window, selector, action);
        if (match is not null || !realizeVirtualized) return match;
        var name = selector.StartsWith("name:", StringComparison.OrdinalIgnoreCase) ? selector[5..]
            : selector.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
              || selector.StartsWith("type:", StringComparison.OrdinalIgnoreCase) ? null : selector;
        if (string.IsNullOrWhiteSpace(name)) return null;
        var item = FindVirtualizedItem(GetAutomation().FromHandle(window), name);
        return item is null ? null : GetIndexedElement(IndexElement(item, window));
    }

    private IndexedElement? FindUniqueTarget(IntPtr window, string selector, string? action = null)
    {
        AutomationElement[] matches;
        using (CreateCacheRequest().Activate())
            matches = GetAutomation().FromHandle(window).FindAllDescendants(CreateCondition(selector, substring: false));
        if (matches.Length == 0) return null;
        if (matches.Length == 1) return GetIndexedElement(IndexElement(matches[0], window));
        // WPF/UWP controls repeat their name on an inner text block; that text is not a separate target.
        var controls = matches.Where(element => element.ControlType != ControlType.Text).ToArray();
        if (controls.Length == 1 && matches.All(element => element.ControlType != ControlType.Text
                                                           || IsWithin(element, controls[0])))
            return GetIndexedElement(IndexElement(controls[0], window));
        // A label often shares its field's name; only one of them can perform a mutating action.
        var capable = action is null ? [] : matches.Where(element => CanPerform(element, action)).ToArray();
        if (capable.Length == 1)
            return GetIndexedElement(IndexElement(capable[0], window));
        var candidates = string.Join("\n", matches.Take(8).Select(element =>
            FormatElementLine(IndexElement(element, window), element)));
        throw new InvalidOperationException($"Target '{selector}' is ambiguous. Use an element number or a unique id: selector:\n{candidates}");
    }

    /// <summary>Whether an element can perform a mutating batch action; reads and waits never disambiguate.</summary>
    private static bool CanPerform(AutomationElement element, string action)
    {
        var patterns = element.Patterns;
        return action switch
        {
            "type" => patterns.Value.IsSupported || patterns.RangeValue.IsSupported
                      || element.ControlType is ControlType.Edit or ControlType.Document,
            "select" => patterns.SelectionItem.IsSupported || patterns.Selection.IsSupported
                        || patterns.ItemContainer.IsSupported || patterns.ExpandCollapse.IsSupported,
            "toggle" => patterns.Toggle.IsSupported,
            "expand" or "collapse" => patterns.ExpandCollapse.IsSupported,
            "scroll" => patterns.Scroll.IsSupported,
            "click" => patterns.Invoke.IsSupported || patterns.Toggle.IsSupported
                       || patterns.SelectionItem.IsSupported || patterns.ExpandCollapse.IsSupported,
            _ => false
        };
    }

    private bool IsWithin(AutomationElement element, AutomationElement ancestor)
    {
        var walker = GetAutomation().TreeWalkerFactory.GetControlViewWalker();
        var current = walker.GetParent(element);
        for (var depth = 0; current is not null && depth < 4; depth++, current = walker.GetParent(current))
            if (current.Equals(ancestor)) return true;
        return false;
    }

    private string PointerClick(IndexedElement target, string gesture, bool allowForeground)
    {
        EnsureEnabled(target);
        var right = gesture.Trim().Equals("right", StringComparison.OrdinalIgnoreCase);
        RequireForeground(allowForeground, right
            ? "A right-click (context menu) uses the physical mouse."
            : "A double-click uses the physical mouse.");
        InvalidateWindowCaptures(target.WindowHandle);
        EnsureWindowFocused(target.WindowHandle);
        ClickVerified(target, right ? MouseButton.Right : MouseButton.Left, doubleClick: !right);
        Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(80));
        return right ? "Right-clicked. An opened context menu's items can be targeted in this window." : "Double-clicked.";
    }

    private static string ReadValue(AutomationElement element)
    {
        if (element.Properties.IsPassword.ValueOrDefault)
            throw new InvalidOperationException("Protected password contents cannot be read or compared.");
        if (element.Patterns.Value.IsSupported)
            return element.Patterns.Value.Pattern.Value.Value;
        if (element.Patterns.Text.IsSupported)
            return ReadCompleteDocumentText(element);
        return element.Name;
    }

    internal static string ReadCompleteDocumentText(AutomationElement element)
        => element.Patterns.Text.Pattern.DocumentRange.GetText(-1);

    private string SelectTarget(IndexedElement target, bool allowForeground)
    {
        EnsureEnabled(target);
        var element = target.Element;
        if (!element.Patterns.SelectionItem.IsSupported)
            throw new InvalidOperationException("Without a value, select needs a selectable item such as a tab, list row or radio button. To choose an option of a combo box or list, set value to the option's name.");
        // Tabs need real activation (web tabs); rows are selected, never toggled or opened.
        if (element.ControlType == ControlType.TabItem)
            return ClickElementCore(target, allowForeground);
        if (element.ControlType == ControlType.RadioButton && NativeButtonHandle(target) != IntPtr.Zero)
        {
            InvokeElement(target);
            return "Selected the option.";
        }
        if (!TrySelectNativeListItem(target) && !TryNativeTreeItem(target, TVM_SELECTITEM, TVGN_CARET))
            SelectItem(element);
        return "Selected.";
    }

    private string SelectValue(IndexedElement target, string value, bool allowForeground)
    {
        EnsureEnabled(target);
        var element = target.Element;
        if (element.ControlType == ControlType.ComboBox)
        {
            if (TrySelectNativeCombo(target, value))
                return $"Selected '{value}' without activating the window.";
            if (element.Patterns.Value.IsSupported && !element.Patterns.Value.Pattern.IsReadOnly.Value)
            {
                element.Patterns.Value.Pattern.SetValue(value);
                if (!string.Equals(element.Patterns.Value.Pattern.Value.Value, value, StringComparison.Ordinal))
                    throw new InvalidOperationException($"The combo box did not accept '{value}'. Inspect its current options.");
                return $"Selected '{value}' through its value pattern.";
            }
        }
        if (element.ControlType == ControlType.List
            && NativeControlHandle(element, target.WindowHandle, "ListBox") is var list && list != IntPtr.Zero
            && TrySelectNativeListBoxOption(list, value))
            return $"Selected '{value}' without activating the window.";
        // Item containers resolve an option without opening its drop-down, including rows that
        // virtualization has not created yet.
        if (element.Patterns.ItemContainer.IsSupported
            && FindVirtualizedItem(element, value) is { } item && item.Patterns.SelectionItem.IsSupported)
        {
            SelectItem(item);
            return $"Selected '{value}'.";
        }
        if (element.ControlType == ControlType.ComboBox)
        {
            if (!allowForeground && !element.Patterns.ExpandCollapse.IsSupported)
                throw new ForegroundRequiredException("This combo box has no background selection pattern.");
            if (allowForeground)
            {
                FocusElement(target);
                var combo = element.AsComboBox();
                combo.Select(value);
                if (!string.Equals(combo.Value, value, StringComparison.Ordinal))
                    throw new InvalidOperationException($"The combo box did not select '{value}'. Its current value is '{combo.Value}'.");
                return $"Selected '{value}'.";
            }
        }
        if (element.Patterns.ExpandCollapse.IsSupported)
            element.Patterns.ExpandCollapse.Pattern.Expand();
        var automation = GetAutomation();
        var condition = new AndCondition(
            automation.ConditionFactory.ByName(value, PropertyConditionFlags.IgnoreCase),
            new PropertyCondition(automation.PropertyLibrary.PatternAvailability.IsSelectionItemPatternAvailable, true));
        var matches = element.FindAllDescendants(condition);
        if (matches.Length != 1)
            throw new InvalidOperationException($"Expected one selectable option named '{value}', found {matches.Length}. Inspect the expanded list.");
        SelectItem(matches[0]);
        if (element.Patterns.ExpandCollapse.IsSupported)
            element.Patterns.ExpandCollapse.Pattern.Collapse();
        return $"Selected '{value}'.";
    }

    private static string SetToggle(IndexedElement target, string value)
    {
        EnsureEnabled(target);
        if (!target.Element.Patterns.Toggle.IsSupported)
            throw new InvalidOperationException("This control does not support the Toggle pattern.");
        var pattern = target.Element.Patterns.Toggle.Pattern;
        var expected = value.Equals("on", StringComparison.OrdinalIgnoreCase) ? ToggleState.On : ToggleState.Off;
        for (var attempt = 0; attempt < 2 && pattern.ToggleState.Value != expected; attempt++)
            ToggleOnce(target);
        if (pattern.ToggleState.Value != expected)
            throw new InvalidOperationException($"The control did not reach toggle state {expected}.");
        return $"Toggle state: {expected}.";
    }

    private static string ScrollElement(IndexedElement target, string value)
    {
        EnsureEnabled(target);
        var direction = value.ToLowerInvariant();
        if (TryScrollNative(target, direction) is { } native)
            return native;
        if (!target.Element.Patterns.Scroll.IsSupported)
            throw new InvalidOperationException("This control does not support scrolling. Target its scrollable list or pane.");
        var pattern = target.Element.Patterns.Scroll.Pattern;
        var vertical = direction is "up" or "down" or "top" or "bottom";
        var forward = direction is "down" or "right" or "bottom";
        var edge = forward ? "end" : "start";
        double Position() => vertical ? pattern.VerticalScrollPercent.ValueOrDefault : pattern.HorizontalScrollPercent.ValueOrDefault;
        bool AtEdge(double percent) => percent >= 0 && (forward ? percent >= 99.95 : percent <= 0.05);
        if (AtEdge(Position()))
            return $"Already at the {edge}; nothing moved.";
        if (direction is "top" or "bottom")
            pattern.SetScrollPercent(-1, forward ? 100 : 0);
        else
            pattern.Scroll(
                direction switch { "left" => ScrollAmount.LargeDecrement, "right" => ScrollAmount.LargeIncrement, _ => ScrollAmount.NoAmount },
                direction switch { "up" => ScrollAmount.LargeDecrement, "down" => ScrollAmount.LargeIncrement, _ => ScrollAmount.NoAmount });
        var after = Position();
        return after < 0 ? $"Scrolled {direction}."
            : AtEdge(after) ? $"Scrolled {direction} to the {edge}." : $"Scrolled {direction}; not yet at the {edge}.";
    }

    internal static void ValidateKeyChord(string keys) => ParseKeyChord(keys);

    private static VirtualKeyShort[] ParseKeyChord(string keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keys);
        var parts = keys.Split('+', StringSplitOptions.TrimEntries);
        var parsed = new List<VirtualKeyShort>();
        for (var i = 0; i < parts.Length; i++)
        {
            var key = ParseKey(parts[i]) ?? throw new ArgumentException($"Unknown key '{parts[i]}'.");
            var modifier = key is VirtualKeyShort.CONTROL or VirtualKeyShort.ALT or VirtualKeyShort.SHIFT or VirtualKeyShort.LWIN;
            if (i < parts.Length - 1 && !modifier)
                throw new ArgumentException("Use modifiers followed by one key, for example Ctrl+Shift+S.");
            if (i == parts.Length - 1 && modifier)
                throw new ArgumentException("A shortcut must end with a non-modifier key.");
            if (parsed.Contains(key))
                throw new ArgumentException($"Duplicate key '{parts[i]}'.");
            parsed.Add(key);
        }
        return parsed.ToArray();
    }

    private static void SendKeyChord(string keys)
    {
        var chord = ParseKeyChord(keys);
        try
        {
            Keyboard.TypeSimultaneously(chord);
        }
        catch
        {
            foreach (var key in chord.Reverse())
            {
                try { Keyboard.Release(key); }
                catch (Exception ex) { Trace.TraceWarning($"[UIAutomation] Could not release {key}: {ex.Message}"); }
            }
            throw;
        }
    }
#endif
}
