using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Lumi.Services;

public sealed record UIAutomationStep
{
    [JsonPropertyName("action")]
    [Description("click, type (replace text or set a slider), keys, read, select, toggle, scroll, expand, collapse, or wait.")]
    public string Action { get; init; } = "";

    [JsonPropertyName("target")]
    [Description("Element number as a string, exact visible name, id:AutomationId, name:Exact name, or type:ControlType. Required except for keys.")]
    public string? Target { get; init; }

    [JsonPropertyName("value")]
    [Description("Text for type; shortcut for keys; option name for select (omit to select the target item itself); on/off for toggle; up/down/left/right (one page) or top/bottom for scroll; expected text/value for wait (omit to wait for existence). For click, omit for a background activation, or use double/right for a physical double-click or right-click (context menu).")]
    public string? Value { get; init; }

    [JsonPropertyName("timeoutMs")]
    [Description("Maximum time to wait for a target to exist and become enabled, or for an exact wait value, 0-10000 ms. Default 2000. Not a fixed sleep.")]
    public int TimeoutMs { get; init; } = 2000;

    internal string NormalizedAction => Action.Trim().ToLowerInvariant();

    internal string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Action))
            return "action is required.";
        if (NormalizedAction is not ("click" or "type" or "keys" or "read" or "select" or "toggle" or "scroll" or "expand" or "collapse" or "wait"))
            return $"Unknown action '{Action}'. Use click, type, keys, read, select, toggle, scroll, expand, collapse, or wait.";
        if (NormalizedAction != "keys" && string.IsNullOrWhiteSpace(Target))
            return $"target is required for {NormalizedAction}.";
        if (NormalizedAction == "click" && !string.IsNullOrWhiteSpace(Value)
            && Value.Trim().ToLowerInvariant() is not ("double" or "right"))
            return "click value must be omitted, double, or right.";
        if (NormalizedAction is "type" && Value is null)
            return "value is required for type; use an empty string to clear a field.";
        if (NormalizedAction == "keys" && string.IsNullOrWhiteSpace(Value))
            return "value is required for keys.";
        if (NormalizedAction == "toggle" && Value?.ToLowerInvariant() is not ("on" or "off"))
            return "toggle value must be 'on' or 'off'.";
        if (NormalizedAction == "scroll" && Value?.ToLowerInvariant() is not ("up" or "down" or "left" or "right" or "top" or "bottom"))
            return "scroll value must be up, down, left, right, top, or bottom.";
        if (TimeoutMs is < 0 or > 10000)
            return "timeoutMs must be between 0 and 10000.";
        return null;
    }
}
