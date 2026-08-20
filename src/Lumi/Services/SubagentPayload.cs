using System;
using System.Text.Json;

namespace Lumi.Services;

/// <summary>
/// Every field Lumi reads from a sub-agent tool message's content, in a single parse.
/// <para>
/// Covers both shapes that content takes: the raw <c>task</c>-tool arguments captured at tool start
/// (<c>description</c>, <c>agent_type</c>, <c>prompt</c>) and the richer payload written once
/// <c>subagent.started</c> arrives.
/// </para>
/// <para>
/// A streaming sub-agent rewrites and re-reads this content on every flush (~20×/s), and it grows
/// with the run. Reading fields one at a time through
/// <see cref="ToolDisplayHelper.ExtractJsonField"/> costs a full <see cref="JsonDocument"/> parse
/// each, so a flush used to re-parse the whole accumulating document about a dozen times. Parsing
/// once and reading off the result keeps that cost flat.
/// </para>
/// <para>
/// Values are the raw strings as stored, so each field reads exactly as
/// <see cref="ToolDisplayHelper.ExtractJsonField"/> would have returned it — null when absent.
/// </para>
/// </summary>
public readonly record struct SubagentPayload(
    string? Description,
    string? AgentType,
    string? AgentName,
    string? AgentDisplayName,
    string? AgentDescription,
    string? Mode,
    string? Model,
    string? Prompt,
    string? Transcript,
    string? Reasoning,
    string? EntriesJson)
{
    public static SubagentPayload Empty { get; } = default;

    /// <summary>Reads the whole payload in one pass. Malformed input yields <see cref="Empty"/>.</summary>
    public static SubagentPayload Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Empty;

            return new SubagentPayload(
                Description: ReadString(root, "description"),
                AgentType: ReadString(root, "agent_type"),
                AgentName: ReadString(root, "agentName"),
                AgentDisplayName: ReadString(root, "agentDisplayName"),
                AgentDescription: ReadString(root, "agentDescription"),
                Mode: ReadString(root, "mode"),
                Model: ReadString(root, "model"),
                Prompt: ReadString(root, "prompt"),
                Transcript: ReadString(root, "transcript"),
                Reasoning: ReadString(root, "reasoning"),
                EntriesJson: root.TryGetProperty("entries", out var entries)
                    && entries.ValueKind == JsonValueKind.Array
                        ? entries.GetRawText()
                        : null);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
