using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.Services;

/// <summary>
/// Static helpers for formatting tool call display names, arguments, and metadata.
/// Extracted from ChatView code-behind for reuse in ViewModels.
/// </summary>
public static partial class ToolDisplayHelper
{
    public static string GetToolGlyph(string toolName) => toolName switch
    {
        "powershell" or "run_in_terminal" or "bash" or "shell" => "⌨",
        "create" or "write_file" or "create_file" or "edit" or "edit_file" or "str_replace" or "insert"
            or "replace_string_in_file" or "multi_replace_string_in_file" or "str_replace_editor" => "📝",
        "view" or "read_file" or "read" => "📄",
        "browser" or "browser_navigate" or "browser_do" or "browser_look" or "browser_js" => "🌐",
        "lumi_search" or "web_search" or "search" => "🔎",
        "lumi_research" => "🔬",
        "web_fetch" or "lumi_fetch" => "📚",
        "ui_inspect" or "ui_find" or "ui_click" or "ui_type" or "ui_read" => "🖥",
        "save_memory" or "update_memory" or "recall_memory" or "delete_memory" => "🧠",
        "code_review" => "🔍",
        "generate_tests" => "🧪",
        "explain_code" => "📖",
        "analyze_project" => "🏗",
        "update_todo" or "manage_todo_list" => "✅",
        "sql" => "🗃",
        "task" => "🤖",
        _ when toolName.StartsWith("agent:", StringComparison.Ordinal) => "🤖",
        _ => "⚙"
    };

    public static bool IsFileEditTool(string toolName)
        => toolName is "edit" or "edit_file" or "str_replace" or "str_replace_editor"
            or "replace_string_in_file" or "insert" or "create" or "write_file"
            or "create_file" or "create_and_write_file" or "write" or "save_file"
            or "multi_replace_string_in_file";

    public static bool IsFileCreateTool(string toolName)
        => toolName is "create" or "write_file" or "create_file" or "write"
            or "save_file" or "create_and_write_file";

    /// <summary>Tools that produce no meaningful expandable detail and can be rendered as compact pills.</summary>
    public static bool IsCompactEligible(string toolName)
        => toolName is "view" or "read_file" or "read"
            or "grep" or "glob"
            or "recall_memory"
            or "report_intent"
            or "announce_file" or "fetch_skill"
            or "ui_list_windows" or "ui_read"
            || toolName.StartsWith("DotSight-", StringComparison.Ordinal)
            || toolName.StartsWith("Avalonia-MCP-", StringComparison.Ordinal)
            || toolName.StartsWith("avalonia-mcp-", StringComparison.Ordinal);

    /// <summary>
    /// Maps a tool call to a user-friendly display name and summary line.
    /// </summary>
    public static (string Name, string? Info) GetFriendlyToolDisplay(string toolName, string? author, string? argsJson)
    {
        switch (toolName)
        {
            case "web_fetch":
            case "lumi_fetch":
            {
                var url = ExtractJsonField(argsJson, "url");
                string? domain = null;
                if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    domain = uri.Host;
                return (Loc.Tool_ReadingWebsite, domain ?? url);
            }
            case "lumi_search":
            {
                var query = ExtractJsonField(argsJson, "query");
                return (Loc.Tool_SearchingWeb, query);
            }
            case "lumi_research":
            {
                var query = ExtractJsonField(argsJson, "query");
                return ("Researching", query);
            }
            case "view":
            {
                var path = ExtractJsonField(argsJson, "path");
                if (path is null) return (Loc.Tool_ReadingFile, null);
                var ext = Path.GetExtension(path);
                var fileName = Path.GetFileName(path);
                return string.IsNullOrEmpty(ext)
                    ? (Loc.Tool_ListingDirectory, fileName)
                    : (Loc.Tool_ReadingFile, fileName);
            }
            case "create" or "write_file" or "create_file" or "write" or "save_file" or "create_and_write_file":
                return (Loc.Tool_CreatingFile, ExtractShortFileName(argsJson));
            case "edit" or "edit_file" or "str_replace" or "str_replace_editor"
                or "replace_string_in_file" or "insert":
                return (Loc.Tool_EditingFile, ExtractShortFileName(argsJson));
            case "multi_replace_string_in_file":
                return (Loc.Tool_EditingFiles, ExtractShortFileName(argsJson));
            case "powershell" or "run_in_terminal" or "bash" or "shell":
            {
                var cmd = ExtractJsonField(argsJson, "command");
                var summary = SummarizeCommand(cmd);
                return (Loc.Tool_RunningCommand, summary ?? TruncateCommand(cmd));
            }
            case "grep" or "grep_search" or "search_files":
                return (Loc.Tool_SearchingFiles, ExtractJsonField(argsJson, "query"));
            case "file_search" or "find":
                return (Loc.Tool_FindingFiles, ExtractJsonField(argsJson, "query"));
            case "semantic_search":
                return (Loc.Tool_SearchingCodebase, ExtractJsonField(argsJson, "query"));
            case "delete_file" or "delete" or "rm":
                return (Loc.Tool_DeletingFile, ExtractShortFileName(argsJson));
            case "browser":
                return (Loc.Tool_OpeningPage, ExtractJsonField(argsJson, "url"));
            case "browser_look":
                return (Loc.Tool_BrowserSnapshot, null);
            case "browser_do":
                return (Loc.Tool_Action, ExtractJsonField(argsJson, "action"));
            case "browser_js":
                return (Loc.Tool_BrowserEvaluate, null);
            case "save_memory":
                return (Loc.Tool_Remembering, ExtractJsonField(argsJson, "key"));
            case "update_memory":
                return (Loc.Tool_UpdatingMemory, ExtractJsonField(argsJson, "key"));
            case "delete_memory":
                return (Loc.Tool_Forgetting, ExtractJsonField(argsJson, "key"));
            case "recall_memory":
                return (Loc.Tool_Recalling, ExtractJsonField(argsJson, "key"));
            case "announce_file":
                return (Loc.Tool_SharingFile, ExtractShortFileName(argsJson));
            case "fetch_skill":
                return (Loc.Tool_FetchingSkill, ExtractJsonField(argsJson, "name"));
            case "ask_question":
                return (Loc.Tool_AskingQuestion, null);
            case "ui_inspect":
                return (Loc.Tool_InspectingWindow, ExtractJsonField(argsJson, "title"));
            case "ui_find":
                return (Loc.Tool_FindingElement, ExtractJsonField(argsJson, "query"));
            case "ui_click":
                return (Loc.Tool_ClickingControl, ExtractJsonField(argsJson, "elementId"));
            case "ui_type":
                return (Loc.Tool_TypingInControl, ExtractJsonField(argsJson, "elementId"));
            case "ui_press_keys":
                return (Loc.Tool_PressingKeys, ExtractJsonField(argsJson, "keys"));
            case "ui_read":
                return (Loc.Tool_ReadingControl, ExtractJsonField(argsJson, "elementId"));
            case "code_review":
                return (Loc.Tool_ReviewingCode, ExtractJsonField(argsJson, "language"));
            case "generate_tests":
            {
                var lang = ExtractJsonField(argsJson, "language");
                var fw = ExtractJsonField(argsJson, "framework");
                return (Loc.Tool_GeneratingTests, fw is not null ? $"{lang} ({fw})" : lang);
            }
            case "explain_code":
                return (Loc.Tool_ExplainingCode, ExtractJsonField(argsJson, "language"));
            case "analyze_project":
                return (Loc.Tool_AnalyzingProject, null);
            case "sql":
                return (Loc.Tool_RunningQuery, ExtractJsonField(argsJson, "description"));
            case "task":
            {
                var agentType = ExtractJsonField(argsJson, "agent_type") ?? "agent";
                var desc = ExtractJsonField(argsJson, "description");
                return (string.Format(Loc.Tool_RunningAgent, agentType), desc);
            }
            default:
            {
                // agent:explore, agent:task, agent:Coding Lumi, etc.
                if (toolName.StartsWith("agent:", StringComparison.Ordinal))
                {
                    var agentName = toolName["agent:".Length..];
                    var desc = ExtractJsonField(argsJson, "description");
                    return (string.Format(Loc.Tool_RunningAgent, agentName), desc);
                }
                var displayName = author ?? FormatToolNameFriendly(toolName);
                var info = ExtractToolSummary(argsJson);
                return (displayName, info);
            }
        }
    }

    /// <summary>
    /// Formats tool arguments into a human-readable summary.
    /// </summary>
    public static string? FormatToolArgsFriendly(string toolName, string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return null;

        // Agent tools have a plain-text description as content, not JSON args
        if (toolName.StartsWith("agent:", StringComparison.Ordinal))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;

            switch (toolName)
            {
                case "web_fetch":
                {
                    var url = GetString(root, "url");
                    return url is not null ? $"**URL:** {url}" : null;
                }
                case "view":
                {
                    var path = GetString(root, "path");
                    if (path is null) break;
                    var fileName = Path.GetFileName(path);
                    var dir = Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(Path.GetExtension(path)))
                        return $"**Path:** `{path}`";
                    var sb = new StringBuilder();
                    sb.AppendLine($"**File:** `{fileName}`");
                    if (!string.IsNullOrEmpty(dir))
                        sb.AppendLine($"**Location:** `{dir}`");
                    return sb.ToString().TrimEnd();
                }
                case "powershell":
                {
                    var cmd = GetString(root, "command");
                    return !string.IsNullOrEmpty(cmd) ? $"```powershell\n{cmd.Trim()}\n```" : null;
                }
                case "sql":
                {
                    var query = GetString(root, "query");
                    return !string.IsNullOrEmpty(query) ? $"```sql\n{query.Trim()}\n```" : null;
                }
                case "task":
                {
                    var prompt = GetString(root, "prompt");
                    if (prompt is null) break;
                    var truncated = prompt.Length > 200 ? prompt[..197] + "..." : prompt;
                    return $"**Prompt:** {truncated}";
                }
                case "report_intent":
                    return GetString(root, "intent") is { } intent ? $"Intent: {intent}" : null;
                case "read_powershell":
                case "stop_powershell":
                    return null;
                case "create":
                case "write_file":
                case "create_file":
                case "edit":
                case "edit_file":
                case "str_replace":
                case "str_replace_editor":
                case "replace_string_in_file":
                case "insert":
                {
                    var path = GetString(root, "filePath") ?? GetString(root, "path");
                    if (path is null) break;
                    var fn = Path.GetFileName(path);
                    var dir = Path.GetDirectoryName(path);
                    var sb = new StringBuilder();
                    sb.AppendLine($"**File:** `{fn}`");
                    if (!string.IsNullOrEmpty(dir))
                        sb.AppendLine($"**Location:** `{dir}`");
                    return sb.ToString().TrimEnd();
                }
                case "multi_replace_string_in_file":
                {
                    if (root.TryGetProperty("replacements", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        var count = arr.GetArrayLength();
                        string? firstFile = null;
                        foreach (var item in arr.EnumerateArray())
                        {
                            if (item.TryGetProperty("filePath", out var fpVal))
                            { firstFile = Path.GetFileName(fpVal.GetString()); break; }
                        }
                        if (firstFile is not null)
                            return count > 1
                                ? $"**File:** `{firstFile}` (+{count - 1} more)"
                                : $"**File:** `{firstFile}`";
                    }
                    break;
                }
                case "save_memory":
                {
                    var key = GetString(root, "key");
                    var content = GetString(root, "content");
                    if (key is null) break;
                    var result = $"**Key:** {key}";
                    if (content is not null)
                        result += $"\n**Content:** {(content.Length > 120 ? content[..120] + "\u2026" : content)}";
                    return result;
                }
                case "update_memory":
                {
                    var key = GetString(root, "key");
                    var content = GetString(root, "content");
                    var newKey = GetString(root, "newKey");
                    if (key is null) break;
                    var result = $"**Key:** {key}";
                    if (newKey is not null) result += $"\n**New key:** {newKey}";
                    if (content is not null)
                        result += $"\n**Content:** {(content.Length > 120 ? content[..120] + "\u2026" : content)}";
                    return result;
                }
                case "delete_memory":
                case "recall_memory":
                    return GetString(root, "key") is { } k ? $"**Key:** {k}" : null;
            }

            return FormatGenericArgs(root);
        }
        catch
        {
            return argsJson;
        }
    }

    public static string? ExtractJsonField(string? json, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(fieldName, out var val) ? val.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>Extracts all file diffs from tool call args JSON.</summary>
    public static List<(string FilePath, string? OldText, string? NewText)> ExtractAllDiffs(string toolName, string? argsJson)
    {
        var results = new List<(string FilePath, string? OldText, string? NewText)>();
        if (string.IsNullOrWhiteSpace(argsJson)) return results;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;

            if (toolName is "multi_replace_string_in_file")
            {
                if (root.TryGetProperty("replacements", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var fp = item.TryGetProperty("filePath", out var fpVal) ? fpVal.GetString()
                            : item.TryGetProperty("path", out var pVal) ? pVal.GetString() : null;
                        if (fp is null) continue;
                        var old = item.TryGetProperty("oldString", out var osVal) ? osVal.GetString() : null;
                        var nw = item.TryGetProperty("newString", out var nsVal) ? nsVal.GetString() : null;
                        results.Add((fp, old, nw));
                    }
                }
                return results;
            }

            var filePath = root.TryGetProperty("filePath", out var f) ? f.GetString()
                : root.TryGetProperty("path", out var p) ? p.GetString()
                : root.TryGetProperty("file", out var fi) ? fi.GetString() : null;
            if (filePath is null) return results;

            var oldText = root.TryGetProperty("oldString", out var o) ? o.GetString()
                : root.TryGetProperty("old_str", out var os) ? os.GetString() : null;
            var newText = root.TryGetProperty("newString", out var n) ? n.GetString()
                : root.TryGetProperty("new_str", out var ns) ? ns.GetString()
                : root.TryGetProperty("content", out var c) ? c.GetString()
                : root.TryGetProperty("file_text", out var ft) ? ft.GetString()
                : root.TryGetProperty("insert_text", out var it) ? it.GetString() : null;

            results.Add((filePath, oldText, newText));
        }
        catch { }
        return results;
    }

    public sealed class TodoStepSnapshot
    {
        public int Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Status { get; init; } = "not-started";
    }

    public static List<TodoStepSnapshot> ParseTodoSteps(string? argsJson)
    {
        var result = new List<TodoStepSnapshot>();
        if (string.IsNullOrWhiteSpace(argsJson)) return result;

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("todos", out var todosText)
                && todosText.ValueKind == JsonValueKind.String)
            {
                var parsed = ParseTodoChecklist(todosText.GetString());
                if (parsed.Count > 0) return parsed;
            }

            JsonElement list = default;
            var hasList = (root.ValueKind == JsonValueKind.Object
                           && (root.TryGetProperty("todoList", out list)
                               || root.TryGetProperty("todo", out list)
                               || root.TryGetProperty("items", out list)
                               || root.TryGetProperty("tasks", out list)
                               || root.TryGetProperty("todos", out list)))
                          || root.ValueKind == JsonValueKind.Array;

            if (!hasList) return result;
            if (root.ValueKind == JsonValueKind.Array) list = root;
            if (list.ValueKind != JsonValueKind.Array) return result;

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var title = GetString(item, "title")
                            ?? GetString(item, "step")
                            ?? GetString(item, "name")
                            ?? GetString(item, "label")
                            ?? string.Empty;
                if (string.IsNullOrWhiteSpace(title)) continue;

                var status = GetString(item, "status") ?? GetString(item, "state") ?? "not-started";
                var id = 0;
                if (item.TryGetProperty("id", out var idEl))
                {
                    if (idEl.ValueKind == JsonValueKind.Number) id = idEl.GetInt32();
                    else if (idEl.ValueKind == JsonValueKind.String) _ = int.TryParse(idEl.GetString(), out id);
                }

                result.Add(new TodoStepSnapshot { Id = id, Title = title, Status = status });
            }
        }
        catch { }
        return result;
    }

    public static string BuildTodoDetailsMarkdown(IReadOnlyList<TodoStepSnapshot> steps)
    {
        if (steps.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var step in steps)
        {
            var isDone = string.Equals(step.Status, "completed", StringComparison.OrdinalIgnoreCase);
            sb.Append("- ").Append(isDone ? "[x] " : "[ ] ").AppendLine(step.Title);
        }
        return sb.ToString().TrimEnd();
    }

    public static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => string.Format(Loc.FileSize_B, bytes),
        < 1024 * 1024 => string.Format(Loc.FileSize_KB, $"{bytes / 1024.0:F1}"),
        < 1024 * 1024 * 1024 => string.Format(Loc.FileSize_MB, $"{bytes / (1024.0 * 1024):F1}"),
        _ => string.Format(Loc.FileSize_GB, $"{bytes / (1024.0 * 1024 * 1024):F2}")
    };

    // ── Private helpers ──────────────────────────────────

    private static string? GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? FormatGenericArgs(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return root.ToString();
        var sb = new StringBuilder();
        foreach (var prop in root.EnumerateObject())
        {
            var label = FriendlyFieldName(prop.Name);
            var value = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? "",
                JsonValueKind.True => Loc.Bool_Yes,
                JsonValueKind.False => Loc.Bool_No,
                JsonValueKind.Number => prop.Value.ToString(),
                _ => prop.Value.ToString()
            };
            if (value.Length > 200) value = value[..197] + "...";
            sb.AppendLine($"**{label}:** {value}");
        }
        return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
    }

    private static string FriendlyFieldName(string fieldName) => fieldName switch
    {
        "url" => Loc.FieldLabel_URL,
        "filePath" or "file_path" => Loc.FieldLabel_File,
        "path" => Loc.FieldLabel_Path,
        "query" => Loc.FieldLabel_SearchQuery,
        "command" => Loc.FieldLabel_Command,
        "description" => Loc.FieldLabel_Description,
        "initial_wait" => Loc.FieldLabel_Timeout,
        "intent" => Loc.FieldLabel_Intent,
        "content" => Loc.FieldLabel_Content,
        "text" => Loc.FieldLabel_Text,
        "language" => Loc.FieldLabel_Language,
        "timeout" => Loc.FieldLabel_Timeout,
        "args" or "arguments" => Loc.FieldLabel_Arguments,
        "input" => Loc.FieldLabel_Input,
        "output" => Loc.FieldLabel_Output,
        "name" => Loc.FieldLabel_Name,
        "type" => Loc.FieldLabel_Type,
        "format" => Loc.FieldLabel_Format,
        "limit" => Loc.FieldLabel_Limit,
        "offset" => Loc.FieldLabel_StartAt,
        "count" => Loc.FieldLabel_Count,
        _ => CapitalizeFirst(fieldName.Replace('_', ' '))
    };

    private static string CapitalizeFirst(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

    private static string? ExtractShortFileName(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            string? fullPath = null;
            if (root.TryGetProperty("filePath", out var fp)) fullPath = fp.GetString();
            else if (root.TryGetProperty("path", out var p)) fullPath = p.GetString();
            else if (root.TryGetProperty("file", out var f)) fullPath = f.GetString();
            else if (root.TryGetProperty("filename", out var fn)) fullPath = fn.GetString();
            else if (root.TryGetProperty("file_path", out var fp2)) fullPath = fp2.GetString();
            else if (root.TryGetProperty("replacements", out var repl) && repl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in repl.EnumerateArray())
                {
                    if (item.TryGetProperty("filePath", out var rfp)) { fullPath = rfp.GetString(); break; }
                    if (item.TryGetProperty("path", out var rp)) { fullPath = rp.GetString(); break; }
                }
            }
            return fullPath is not null ? Path.GetFileName(fullPath) : null;
        }
        catch { return null; }
    }

    private static string? SummarizeCommand(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        var firstLine = cmd.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => !l.TrimStart().StartsWith('#'))?.Trim();
        if (firstLine is null) return null;

        if (firstLine.StartsWith("Get-Content", StringComparison.OrdinalIgnoreCase)) return Loc.Cmd_ReadingFileContents;
        if (firstLine.StartsWith("Get-ChildItem", StringComparison.OrdinalIgnoreCase)
            || firstLine.StartsWith("dir ", StringComparison.OrdinalIgnoreCase)
            || firstLine.StartsWith("ls ", StringComparison.OrdinalIgnoreCase)) return Loc.Cmd_ListingFiles;
        if (firstLine.StartsWith("Copy-Item", StringComparison.OrdinalIgnoreCase)) return Loc.Cmd_CopyingFiles;
        if (firstLine.StartsWith("Remove-Item", StringComparison.OrdinalIgnoreCase)) return Loc.Cmd_CleaningUp;
        if (firstLine.StartsWith("Expand-Archive", StringComparison.OrdinalIgnoreCase)) return Loc.Cmd_ExtractingArchive;
        if (firstLine.StartsWith("Get-Command", StringComparison.OrdinalIgnoreCase)) return Loc.Cmd_CheckingTools;
        if (firstLine.StartsWith("Install-", StringComparison.OrdinalIgnoreCase)) return Loc.Cmd_InstallingPackage;
        if (firstLine.StartsWith("pip install", StringComparison.OrdinalIgnoreCase)) return Loc.Cmd_InstallingPython;
        if (firstLine.StartsWith("npm install", StringComparison.OrdinalIgnoreCase)) return Loc.Cmd_InstallingNpm;
        if (firstLine.StartsWith("cd ", StringComparison.OrdinalIgnoreCase)) return Loc.Cmd_NavigatingDirs;
        if (firstLine.Contains("New-Object -ComObject Word", StringComparison.OrdinalIgnoreCase)) return Loc.Cmd_OpeningWord;
        if (firstLine.Contains("New-Object -ComObject PowerPoint", StringComparison.OrdinalIgnoreCase)) return Loc.Cmd_OpeningPowerPoint;
        if (firstLine.Contains("New-Object -ComObject Excel", StringComparison.OrdinalIgnoreCase)) return Loc.Cmd_OpeningExcel;
        return null;
    }

    private static string? TruncateCommand(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        var firstLine = cmd.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (firstLine is null) return null;
        return firstLine.Length > 60 ? firstLine[..57] + "..." : firstLine;
    }

    private static string FormatToolNameFriendly(string toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return Loc.Tool_Action;
        var cleaned = toolName.Replace('_', ' ').Replace('.', ' ').Trim();
        return cleaned.Length == 0 ? Loc.Tool_Action : char.ToUpper(cleaned[0]) + cleaned[1..];
    }

    private static string? ExtractToolSummary(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("url", out var url)) return url.GetString();
            if (root.TryGetProperty("path", out var path)) return path.GetString();
            if (root.TryGetProperty("filePath", out var filePath)) return filePath.GetString();
            if (root.TryGetProperty("query", out var query)) return query.GetString();
            if (root.TryGetProperty("command", out var cmd)) return cmd.GetString();
            return null;
        }
        catch { return null; }
    }

    private static List<TodoStepSnapshot> ParseTodoChecklist(string? checklist)
    {
        var result = new List<TodoStepSnapshot>();
        if (string.IsNullOrWhiteSpace(checklist)) return result;

        var lines = checklist.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var index = 1;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            bool isDone;
            string title;
            if (line.StartsWith("- [x]", StringComparison.OrdinalIgnoreCase))
            { isDone = true; title = line[5..].Trim(); }
            else if (line.StartsWith("- [ ]", StringComparison.OrdinalIgnoreCase))
            { isDone = false; title = line[5..].Trim(); }
            else
            { isDone = false; title = line.TrimStart('-', '*', ' ').Trim(); }

            if (string.IsNullOrWhiteSpace(title)) continue;
            result.Add(new TodoStepSnapshot { Id = index++, Title = title, Status = isDone ? "completed" : "in-progress" });
        }
        return result;
    }
}
