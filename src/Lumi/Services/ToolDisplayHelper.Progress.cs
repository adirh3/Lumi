using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lumi.Localization;

namespace Lumi.Services;

/// <summary>One row of the agent's SQL <c>todos</c> table, as the session's plan API reads it back.</summary>
public readonly record struct SqlTodoRow(string? Id, string? Title, string? Status, string? CreatedAt);

public static partial class ToolDisplayHelper
{
    private static readonly Regex StreamingPathField = new(
        @"""(?:path|filePath|file_path)""\s*:\s*""((?:[^""\\]|\\.)*)""",
        RegexOptions.CultureInvariant);

    private static readonly Regex StreamingPatchHeader = new(
        @"\*\*\* (?:Add|Update|Delete) File: ([^\r\n]+)\r?\n",
        RegexOptions.CultureInvariant);

    // Inside JSON arguments the patch is an escaped string: its lines end in an escaped newline, and the
    // escape pairs are read whole, so the escaped backslash in a path such as C:\\notes.md ends nothing.
    private static readonly Regex StreamingJsonPatchHeader = new(
        @"\*\*\* (?:Add|Update|Delete) File: ((?:[^""\\]|\\[^n])+?)\\n",
        RegexOptions.CultureInvariant);

    /// <summary>A query's first line, for its row's glance detail.</summary>
    internal static string? SummarizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        foreach (var line in query.Split('\n'))
        {
            var text = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (text.Length == 0 || text.StartsWith("--", StringComparison.Ordinal))
                continue;

            return text.Length > 80 ? text[..79].TrimEnd() + "\u2026" : text;
        }

        return null;
    }

    /// <summary>
    /// The plan a turn shows from the agent's SQL todos, as <c>todoList</c> JSON for
    /// <see cref="ParseTodoSteps"/>: every step still open, every step created this turn, and every step
    /// the turn has already shown, so a step that finishes stays in view. The table outlives turns, so
    /// earlier finished work is left out. Statuses are put in Lumi's plan words. Null when no step
    /// qualifies.
    /// </summary>
    /// <param name="shownIds">Steps this turn has shown; the ones shown now are added.</param>
    public static string? BuildSqlPlanSnapshot(
        IEnumerable<SqlTodoRow> rows, DateTimeOffset? turnStartedAt, ISet<string> shownIds)
    {
        // The table stores datetime('now'): UTC, to the second.
        var turnStart = turnStartedAt is { } started
            ? started.UtcDateTime.AddTicks(-(started.UtcDateTime.Ticks % TimeSpan.TicksPerSecond))
            : (DateTime?)null;
        var steps = new List<(string Title, string Status)>();
        foreach (var row in rows)
        {
            var title = string.IsNullOrWhiteSpace(row.Title) ? row.Id?.Trim() : row.Title.Trim();
            if (string.IsNullOrEmpty(title))
                continue;

            var key = string.IsNullOrWhiteSpace(row.Id) ? title : row.Id.Trim();
            var status = NormalizeSqlTodoStatus(row.Status);
            var createdThisTurn = turnStart is null
                || !DateTime.TryParseExact(row.CreatedAt, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created)
                || created >= turnStart;
            if (status == "completed" && !createdThisTurn && !shownIds.Contains(key))
                continue;

            shownIds.Add(key);
            steps.Add((title, status));
        }

        if (steps.Count == 0)
            return null;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("todoList");
            foreach (var (title, status) in steps)
            {
                writer.WriteStartObject();
                writer.WriteString("title", title);
                writer.WriteString("status", status);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>A SQL todo status in the words Lumi's plans use (those of <c>update_todo</c>).</summary>
    internal static string NormalizeSqlTodoStatus(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "done" or "completed" or "complete" => "completed",
            "in_progress" or "in-progress" or "active" or "running" => "in-progress",
            "blocked" => "blocked",
            "failed" or "error" => "failed",
            _ => "not-started",
        };

    /// <summary>
    /// The file a tool call is about to write, read from its arguments while they still stream in (a
    /// <c>path</c> field, or a patch's file header). Null until the whole path has arrived.
    /// </summary>
    public static string? ExtractStreamingFilePath(string? partialInput)
    {
        if (string.IsNullOrEmpty(partialInput))
            return null;

        var field = StreamingPathField.Match(partialInput);
        if (field.Success)
            return UnescapeJsonString(field.Groups[1].Value);

        var isJson = partialInput.TrimStart().StartsWith('{');
        var header = (isJson ? StreamingJsonPatchHeader : StreamingPatchHeader).Match(partialInput);
        if (!header.Success)
            return null;

        var path = header.Groups[1].Value;
        return (isJson ? UnescapeJsonString(path) : path)?.Trim();
    }

    /// <summary>What a file tool is doing while its arguments are still being written ("Writing notes.md").</summary>
    public static string FormatStreamingToolName(string? filePath)
        => GetDisplayFileName(filePath) is { Length: > 0 } fileName
            ? string.Format(Loc.Tool_WritingNamed, fileName)
            : Loc.Tool_WritingFile;

    private static string? UnescapeJsonString(string escaped)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes("\"" + escaped + "\""));
            return reader.Read() ? reader.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
