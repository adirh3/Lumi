using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Lumi.Services;

internal readonly record struct BrowserActionResult(
    bool Succeeded, string Message, bool Pending = false, long TargetWaitMs = 0, long ElapsedMs = 0)
{
    internal static BrowserActionResult Success(string message) => new(true, message);
    internal static BrowserActionResult Failure(string message) => new(false, message);
    internal string ToDisplayText(bool diagnostics = false)
    {
        var text = Succeeded ? Message : "Error: " + Message;
        return diagnostics
            ? text + $"\nTiming: target-ready={TargetWaitMs} ms; action={Math.Max(0, ElapsedMs - TargetWaitMs)} ms."
            : text;
    }

    internal static BrowserActionResult FromScript(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("ok", out var ok) ||
                ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.String)
                return Failure("The page did not return an automation result; the action was not retried.");

            return new(ok.GetBoolean(), message.GetString() ?? "",
                root.TryGetProperty("pending", out var pending) && pending.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return Failure("The page returned an invalid automation result; the action was not retried.");
        }
    }

    // Adapter only for the existing navigation/download/upload methods. DOM actions use typed results.
    internal static BrowserActionResult FromLegacy(string? text)
    {
        var message = text?.Trim() ?? "";
        if (message.Length == 0 || message is "null" or "{}" or "(undefined)")
            return Failure("The browser returned no result; the action was not retried.");
        foreach (var prefix in new[] { "Error:", "JS Error:", "Timeout", "Unknown action",
                     "Cannot go back", "No download detected", "Download interrupted",
                     "Download detected but doesn't match" })
        {
            if (!message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            return Failure(message.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                ? message[6..].TrimStart() : message);
        }
        return Success(message);
    }

    internal static BrowserActionResult FromException(Exception exception) =>
        Failure(exception is TimeoutException
            ? "The browser operation timed out; any executed action was not retried."
            : $"The browser operation failed ({exception.GetType().Name}); any executed action was not retried.");
}

internal sealed record BrowserAutomationStep(string Action, string? Target, string? Value);

internal static class BrowserAutomationBatch
{
    internal static bool Supports(string action) => action is
        "click" or "type" or "press" or "select" or "scroll" or "back" or "clear" or
        "fill" or "read_form" or "upload" or "wait";

    internal static async Task<string> ExecuteAsync(
        string? json,
        Func<BrowserAutomationStep, Task<BrowserActionResult>> execute,
        Func<Task<string>> observe,
        bool diagnostics = false)
    {
        var steps = new List<BrowserAutomationStep>();
        try
        {
            using var doc = JsonDocument.Parse(json ?? "");
            if (doc.RootElement.ValueKind != JsonValueKind.Array ||
                doc.RootElement.GetArrayLength() is < 1 or > 20)
                return "Error: steps must be a JSON array of 1–20 actions.";
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    steps.Add(new("", null, null));
                    continue;
                }
                string? Read(string key) => item.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                steps.Add(new((Read("action") ?? "").Trim().ToLowerInvariant(), Read("target"), Read("value")));
            }
        }
        catch (JsonException)
        {
            // JSON parser errors can quote the supplied password.
            return "Error: invalid JSON for steps.";
        }

        var completed = new List<string>();
        var timer = Stopwatch.StartNew();
        long targetWaitMs = 0;
        async Task<string> FinishAsync(string summary)
        {
            var actionMs = timer.ElapsedMilliseconds;
            var observationTimer = Stopwatch.StartNew();
            var snapshot = await ObserveSafelyAsync(observe);
            return summary + (diagnostics
                ? $"\nTiming: target-ready={targetWaitMs} ms; actions={Math.Max(0, actionMs - targetWaitMs)} ms; observation={observationTimer.ElapsedMilliseconds} ms."
                : "") + "\n\n" + snapshot;
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            BrowserActionResult result;
            try
            {
                result = Supports(step.Action)
                    ? await execute(step)
                    : BrowserActionResult.Failure("Unsupported or missing action in steps.");
            }
            catch (Exception ex)
            {
                result = BrowserActionResult.FromException(ex);
            }
            targetWaitMs += result.TargetWaitMs;
            if (!result.Succeeded)
            {
                var remaining = steps.Skip(i + 1).Select((s, j) =>
                    $"{i + j + 2} ({(Supports(s.Action) ? s.Action : "unsupported")})");
                return await FinishAsync($"Error: step {i + 1} failed: {result.Message}\n" +
                    $"Completed {completed.Count} of {steps.Count} steps:\n" +
                    (completed.Count == 0 ? "(none)" : string.Join("\n", completed)) +
                    "\nUnexecuted steps: " + (i + 1 == steps.Count ? "(none)" : string.Join(", ", remaining)));
            }
            completed.Add($"{i + 1}. {step.Action}: {result.Message}");
        }
        return await FinishAsync($"Completed {steps.Count} of {steps.Count} steps:\n" +
            string.Join("\n", completed));
    }

    private static async Task<string> ObserveSafelyAsync(Func<Task<string>> observe)
    {
        try { return await observe(); }
        catch (Exception ex) { return "Fresh observation unavailable: " + BrowserActionResult.FromException(ex).Message; }
    }
}
