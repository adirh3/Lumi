using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Lumi.Services;

/// <summary>What a single ordered entry in a sub-agent's run log represents.</summary>
public enum SubagentRunEntryKind
{
    Assistant,
    Reasoning,
}

/// <summary>One finalized message produced by a sub-agent during its run.</summary>
public readonly record struct SubagentRunEntry(SubagentRunEntryKind Kind, string Text, DateTimeOffset Timestamp);

/// <summary>
/// Ordered log of the assistant/reasoning messages a sub-agent produced, persisted inside the
/// sub-agent tool message's payload JSON as a compact <c>entries</c> array.
/// <para>
/// Live streaming keeps only the in-flight text in the payload's <c>transcript</c>/<c>reasoning</c>
/// fields (they are cleared once a message finalizes). This log is what turns a sub-agent run into a
/// readable conversation instead of "whatever it said last", so the read-only run transcript can be
/// rebuilt after a chat switch or an app restart.
/// </para>
/// </summary>
public static class SubagentRunLog
{
    private const string AssistantCode = "a";
    private const string ReasoningCode = "r";

    /// <summary>Parses a persisted <c>entries</c> array. Malformed input yields an empty log.</summary>
    public static IReadOnlyList<SubagentRunEntry> Parse(string? entriesJson)
    {
        if (string.IsNullOrWhiteSpace(entriesJson))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(entriesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var entries = new List<SubagentRunEntry>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var text = element.TryGetProperty("c", out var content) ? content.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var kind = element.TryGetProperty("k", out var kindValue)
                    && string.Equals(kindValue.GetString(), ReasoningCode, StringComparison.Ordinal)
                        ? SubagentRunEntryKind.Reasoning
                        : SubagentRunEntryKind.Assistant;

                var timestamp = element.TryGetProperty("t", out var stamp)
                    && DateTimeOffset.TryParse(
                        stamp.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var parsed)
                        ? parsed
                        : default;

                entries.Add(new SubagentRunEntry(kind, text, timestamp));
            }

            return entries;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Serializes a log back to the compact array stored in the sub-agent payload.</summary>
    public static string Serialize(IReadOnlyList<SubagentRunEntry> entries)
    {
        if (entries.Count == 0)
            return "[]";

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("k", entry.Kind == SubagentRunEntryKind.Reasoning ? ReasoningCode : AssistantCode);
                writer.WriteString("t", entry.Timestamp.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("c", entry.Text);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Appends a finalized sub-agent message. Blank text is ignored, and an entry identical to the
    /// previous one of the same kind is dropped — the SDK reports the same reasoning text through
    /// both the reasoning event and the following assistant message, and replaying it would double
    /// it in the run transcript.
    /// </summary>
    public static string Append(
        string? entriesJson,
        SubagentRunEntryKind kind,
        string? text,
        DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.IsNullOrWhiteSpace(entriesJson) ? "[]" : entriesJson;

        var entries = new List<SubagentRunEntry>(Parse(entriesJson));
        if (entries.Count > 0)
        {
            var last = entries[^1];
            if (last.Kind == kind && string.Equals(last.Text, text, StringComparison.Ordinal))
                return Serialize(entries);
        }

        entries.Add(new SubagentRunEntry(kind, text, timestamp));
        return Serialize(entries);
    }
}
