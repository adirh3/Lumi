using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Lumi.Services;

/// <summary>Presentation plan for an MCP elicitation request, derived from its JSON schema.</summary>
public sealed record ElicitationPromptPlan(string Question, IReadOnlyList<string> Options, bool AllowFreeText);

/// <summary>
/// Pure (UI-free) logic that maps an MCP elicitation request's JSON schema onto Lumi's
/// question-card UI and turns the user's answer back into a typed content payload.
/// Kept free of SDK and Avalonia types so it can be unit tested directly.
/// </summary>
public static class McpElicitationResolver
{
    private const string AcceptYes = "Yes";
    private const string DeclineNo = "No";

    /// <summary>Builds the prompt text, choice options, and free-text flag for an elicitation.</summary>
    public static ElicitationPromptPlan BuildPrompt(string? message, IDictionary<string, object?>? properties)
    {
        var text = string.IsNullOrWhiteSpace(message)
            ? "An MCP server is requesting input."
            : message!.Trim();

        var fields = properties?.Keys.ToList() ?? [];

        if (fields.Count == 0)
            return new ElicitationPromptPlan(text, [AcceptYes, DeclineNo], AllowFreeText: false);

        if (fields.Count == 1)
        {
            var name = fields[0];
            var fragment = properties![name];
            var choices = ReadEnum(fragment);
            if (choices.Count > 0)
                return new ElicitationPromptPlan($"{text}\n\n{DescribeField(name, fragment)}", choices, AllowFreeText: false);

            return new ElicitationPromptPlan($"{text}\n\n{DescribeField(name, fragment)}", [], AllowFreeText: true);
        }

        var builder = new StringBuilder(text);
        builder.Append("\n\nProvide values as `field: value`, one per line:");
        foreach (var name in fields)
            builder.Append("\n• ").Append(DescribeField(name, properties![name]));

        return new ElicitationPromptPlan(builder.ToString(), [], AllowFreeText: true);
    }

    /// <summary>
    /// Turns the user's answer into an (accept, content) pair. An empty answer, or the
    /// "No" choice on a confirmation, declines the elicitation.
    /// </summary>
    public static (bool Accept, Dictionary<string, object?> Content) Resolve(
        IDictionary<string, object?>? properties,
        string? answer)
    {
        var fields = properties?.Keys.ToList() ?? [];
        var content = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (fields.Count == 0)
        {
            var accepted = string.Equals(answer?.Trim(), AcceptYes, StringComparison.OrdinalIgnoreCase);
            return (accepted, content);
        }

        if (string.IsNullOrWhiteSpace(answer))
            return (false, content);

        if (fields.Count == 1)
        {
            var name = fields[0];
            content[name] = Coerce(answer.Trim(), ReadType(properties![name]));
            return (true, content);
        }

        foreach (var line in answer.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sep = line.IndexOf(':');
            if (sep <= 0) continue;
            var key = line[..sep].Trim();
            var value = line[(sep + 1)..].Trim();
            var matched = fields.FirstOrDefault(f => string.Equals(f, key, StringComparison.OrdinalIgnoreCase));
            if (matched is null) continue;
            content[matched] = Coerce(value, ReadType(properties![matched]));
        }

        return content.Count > 0 ? (true, content) : (false, content);
    }

    private static string DescribeField(string name, object? fragment)
    {
        var type = ReadType(fragment);
        return string.IsNullOrEmpty(type) ? name : $"{name} ({type})";
    }

    /// <summary>Coerces a raw string answer to the JSON-schema type the server expects.</summary>
    public static object? Coerce(string value, string? type)
    {
        switch (type)
        {
            case "boolean":
                if (bool.TryParse(value, out var b)) return b;
                return value.Trim().ToLowerInvariant() is "yes" or "y" or "true" or "1" or "on";
            case "integer":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                return value;
            case "number":
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                return value;
            default:
                return value;
        }
    }

    private static string? ReadType(object? fragment) => ReadStringProperty(fragment, "type");

    private static IReadOnlyList<string> ReadEnum(object? fragment)
    {
        switch (fragment)
        {
            case JsonElement json when json.ValueKind == JsonValueKind.Object
                                       && json.TryGetProperty("enum", out var en)
                                       && en.ValueKind == JsonValueKind.Array:
                return en.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Select(s => s!)
                    .ToList();
            case IDictionary<string, object?> dict when dict.TryGetValue("enum", out var raw) && raw is IEnumerable<object?> items:
                return items.Select(i => i?.ToString()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
            default:
                return [];
        }
    }

    private static string? ReadStringProperty(object? fragment, string property)
    {
        switch (fragment)
        {
            case JsonElement json when json.ValueKind == JsonValueKind.Object
                                       && json.TryGetProperty(property, out var v)
                                       && v.ValueKind == JsonValueKind.String:
                return v.GetString();
            case IDictionary<string, object?> dict when dict.TryGetValue(property, out var raw):
                return raw?.ToString();
            default:
                return null;
        }
    }
}
