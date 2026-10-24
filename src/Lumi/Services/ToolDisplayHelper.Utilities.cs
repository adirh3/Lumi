using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.Services;

/// <summary>
/// Terminal output processing, file detection, tool classification, and display name formatting utilities.
/// </summary>
public static partial class ToolDisplayHelper
{
    private const int MaxFailedToolOutputChars = 8000;
    private const int MaxRawFailedToolOutputChars = MaxFailedToolOutputChars * 4;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static string FailedToolOutputTruncationMarker =>
        $"{Environment.NewLine}{Environment.NewLine}{Loc.Tool_OutputTruncated}";

    /// <summary>Extensions for intermediary/script files that shouldn't appear as attachment chips.</summary>
    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".ps1", ".bat", ".cmd", ".sh", ".bash", ".vbs", ".wsf", ".js", ".mjs", ".ts"
    };

    /// <summary>
    /// Formats a tool name into a standalone live-status phrase (e.g. "Reading file" or "Running command").
    /// A shell command reads as the purpose the agent gave it, and a tool Lumi has no wording for uses
    /// <paramref name="toolTitle"/>, the display title its server provides (see <see cref="GetToolTitle"/>).
    /// </summary>
    public static string FormatToolStatusName(
        string toolName, string? argsJson = null, string? displaySkillName = null, string? toolTitle = null)
    {
        if (toolName.StartsWith("agent:", StringComparison.Ordinal))
            return string.Format(Loc.Tool_RunningAgent, toolName["agent:".Length..]);

        var fileName = ExtractShortFileName(argsJson);
        return toolName switch
        {
            "web_fetch" or "fetch" or "lumi_fetch" => Loc.Tool_ReadingWebsite,
            "web_search" or "search" => Loc.Tool_SearchingWeb,
            "view" or "read_file" or "read" => fileName is not null ? string.Format(Loc.Tool_ReadingNamed, fileName) : Loc.Tool_ReadingFile,
            "create" or "write_file" or "create_file" or "write" or "save_file" => fileName is not null ? string.Format(Loc.Tool_CreatingNamed, fileName) : Loc.Tool_CreatingFile,
            "edit" or "edit_file" or "str_replace_editor" or "str_replace" or "replace_string_in_file" or "insert" => fileName is not null ? string.Format(Loc.Tool_EditingNamed, fileName) : Loc.Tool_EditingFile,
            "apply_patch" => Loc.Tool_EditingFiles,
            "multi_replace_string_in_file" => fileName is not null ? string.Format(Loc.Tool_EditingNamed, fileName) : Loc.Tool_EditingFiles,
            "list_dir" or "list_directory" or "ls" => Loc.Tool_ListingDirectory,
            "bash" or "shell" or "powershell" or "run_command" or "execute_command" or "run_terminal" or "run_in_terminal" => GetCommandTitle(argsJson) ?? Loc.Tool_RunningCommand,
            "read_powershell" => Loc.Tool_ReadingTerminal,
            "write_powershell" => Loc.Tool_WritingTerminal,
            "stop_powershell" => Loc.Tool_StoppingTerminal,
            "report_intent" => Loc.Tool_Planning,
            "grep" or "grep_search" or "search_files" or "glob" => Loc.Tool_SearchingFiles,
            "file_search" or "find" => Loc.Tool_FindingFiles,
            "semantic_search" => Loc.Tool_SearchingCodebase,
            "delete_file" or "delete" or "rm" => fileName is not null ? string.Format(Loc.Tool_DeletingNamed, fileName) : Loc.Tool_DeletingFile,
            "move_file" or "rename_file" or "mv" or "rename" => fileName is not null ? string.Format(Loc.Tool_MovingNamed, fileName) : Loc.Tool_MovingFile,
            "get_errors" or "diagnostics" => fileName is not null ? string.Format(Loc.Tool_CheckingNamed, fileName) : Loc.Tool_CheckingErrors,
            "browser" or BrowserOpenToolName => Loc.Tool_OpeningPage,
            "browser_look" or BrowserLookToolName => Loc.Tool_BrowserSnapshot,
            "browser_find" or BrowserFindToolName => Loc.Tool_FindingElement,
            "browser_do" or BrowserDoToolName => Loc.Tool_Action,
            "browser_js" or BrowserJsToolName => Loc.Tool_BrowserEvaluate,
            BrowserTabsToolName => Loc.Tool_ManagingBrowserTabs,
            BrowserScreenshotToolName => Loc.Tool_BrowserScreenshot,
            "save_memory" => Loc.Tool_Remembering,
            "update_memory" => Loc.Tool_UpdatingMemory,
            "delete_memory" => Loc.Tool_Forgetting,
            "recall_memory" => Loc.Tool_Recalling,
            "announce_file" => Loc.Tool_SharingFile,
            "fetch_skill" => ExtractJsonField(argsJson, "name") is { Length: > 0 } skillName
                ? string.Format(Loc.Tool_UsingNamedSkill, skillName)
                : Loc.Tool_FetchingSkill,
            "skill" => (displaySkillName ?? ExtractJsonField(argsJson, "skill")) is { Length: > 0 } nativeSkillName
                ? string.Format(Loc.Tool_UsingNamedSkill, nativeSkillName)
                : Loc.Tool_FetchingSkill,
            "manage_projects" => Loc.Tool_ManagingProjects,
            "manage_skills" => Loc.Tool_ManagingSkills,
            "manage_lumis" => Loc.Tool_ManagingAgents,
            "manage_mcps" => Loc.Tool_ManagingConnectors,
            "manage_memories" => Loc.Tool_ManagingMemories,
            "manage_current_chat" => string.Equals(ExtractJsonField(argsJson, "action"), "get", StringComparison.OrdinalIgnoreCase)
                ? Loc.Tool_InspectingCurrentChat : Loc.Tool_UpdatingCurrentChat,
            "manage_chats" => Loc.Tool_OrchestratingChats,
            "search_chats" => Loc.Tool_SearchingChats,
            "read_chat" => Loc.Tool_ReadingChat,
            "ask_question" => Loc.Tool_AskingQuestion,
            "ask_input" => Loc.Tool_AskingQuestion,
            "code_review" => Loc.Tool_ReviewingCode,
            "generate_tests" => Loc.Tool_GeneratingTests,
            "explain_code" => Loc.Tool_ExplainingCode,
            "analyze_project" => Loc.Tool_AnalyzingProject,
            "ui_list_windows" => Loc.Tool_ListingWindows,
            "ui_press_keys" => Loc.Tool_PressingKeys,
            "ui_inspect" => Loc.Tool_InspectingWindow,
            "ui_do" => Loc.Tool_AutomatingWindow,
            "ui_screenshot" => Loc.Tool_TakingScreenshot,
            "ui_click_at" => Loc.Tool_ClickingControl,
            "ui_find" => Loc.Tool_FindingElement,
            "ui_click" => Loc.Tool_ClickingControl,
            "ui_type" => Loc.Tool_TypingInControl,
            "ui_read" => Loc.Tool_ReadingControl,
            "sql" => GetCommandTitle(argsJson) ?? Loc.Tool_RunningQuery,
            "task" => string.Format(Loc.Tool_RunningAgent, ExtractJsonField(argsJson, "agent_type") ?? Loc.Tool_DefaultAgent),
            _ => !string.IsNullOrWhiteSpace(toolTitle) ? toolTitle.Trim() : FormatSnakeCaseToTitle(toolName)
        };
    }

    /// <summary>
    /// The purpose the agent gave a shell command or a query (its <c>description</c> argument, e.g. "Run
    /// the focused tests"), so it reads as what it is for rather than as "Running command". Null without one.
    /// </summary>
    public static string? GetCommandTitle(string? argsJson)
    {
        var description = ExtractJsonField(argsJson, "description");
        if (string.IsNullOrWhiteSpace(description))
            return null;

        var line = description.Trim().Split('\n', 2)[0].Trim();
        if (line.Length > 80)
            line = line[..79].TrimEnd() + "\u2026";
        return line.Length == 0 ? null : char.ToUpper(line[0], CultureInfo.CurrentCulture) + line[1..];
    }

    /// <summary>
    /// A display title for a tool Lumi has no wording for: the title its descriptor provides (MCP tools
    /// may carry one), else the server's tool name without the connector prefix ("search_issues" reads
    /// "Search issues"). Null when neither is known.
    /// </summary>
    public static string? GetToolTitle(string? descriptorTitle, string? mcpToolName)
    {
        if (!string.IsNullOrWhiteSpace(descriptorTitle))
            return descriptorTitle.Trim();

        return string.IsNullOrWhiteSpace(mcpToolName)
            ? null
            : FormatSnakeCaseToTitle(mcpToolName.Trim().Replace('-', '_'));
    }

    /// <summary>Appends an ellipsis to a live status label unless it already has one.</summary>
    public static string FormatProgressLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return label;

        var trimmed = label.TrimEnd();
        return trimmed.EndsWith("…", StringComparison.Ordinal) || trimmed.EndsWith("...", StringComparison.Ordinal)
            ? trimmed
            : $"{trimmed}…";
    }

    public static string FormatSnakeCaseToTitle(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return name;
        words[0] = char.ToUpper(words[0][0]) + words[0][1..];
        return string.Join(' ', words);
    }

    /// <summary>
    /// Extracts the file name from a path for DISPLAY, tolerant of both '/' and '\' separators.
    /// On Windows this is exactly <see cref="Path.GetFileName(string)"/> (which already splits on
    /// both), so Windows output is byte-for-byte unchanged. On macOS/Linux, <see cref="Path.GetFileName(string)"/>
    /// only splits on '/', so a foreign Windows-style path (e.g. from a Windows-authored tool call or
    /// a chat synced from Windows) would otherwise render in full; honoring '\' too keeps the label to
    /// just the file name. Display-only — never use for semantic path manipulation.
    /// </summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(path))]
    public static string? GetDisplayFileName(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        if (OperatingSystem.IsWindows())
            return Path.GetFileName(path);
        var idx = path.AsSpan().LastIndexOfAny('/', '\\');
        return idx >= 0 ? path[(idx + 1)..] : path;
    }

    public static string TruncateInlineLabel(string text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(text);

        var compact = new StringBuilder(Math.Min(text.Length, Math.Max(1, maxLength)));
        var pendingSpace = false;
        // Tool labels are formatted on the UI thread; stop once truncation is certain.
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = compact.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                compact.Append(' ');
                pendingSpace = false;
            }

            compact.Append(character);
            if (compact.Length > maxLength)
                break;
        }

        var label = compact.ToString();
        if (label.Length <= maxLength)
            return label;

        return label[..Math.Max(1, maxLength - 1)].TrimEnd() + "…";
    }

    /// <summary>Returns true if the tool creates files (for resource link detection).</summary>
    public static bool IsFileCreationTool(string? toolName)
    {
        return toolName is "write_file" or "create_file" or "create" or "edit_file"
            or "str_replace_editor" or "str_replace" or "create_and_write_file"
            or "replace_string_in_file" or "multi_replace_string_in_file"
            or "insert" or "write" or "save_file";
    }

    /// <summary>
    /// The primary shell-command invocation tool: <c>powershell</c> on Windows, <c>bash</c>/<c>shell</c>
    /// on Linux/macOS. These start a terminal session (get a live preview) and frequently mutate the
    /// repo (git, builds, file moves), so they also drive live git/file-chip refresh. On Windows the
    /// CLI only ever emits <c>powershell</c>, so this matches the previous <c>== "powershell"</c> checks
    /// exactly there; the extra names only ever appear on Linux/macOS.
    /// </summary>
    public static bool IsShellCommandTool(string? toolName)
    {
        return toolName is "powershell" or "bash" or "shell";
    }

    public static bool IsTerminalStreamingTool(string? toolName)
    {
        return IsShellCommandTool(toolName)
            || toolName is "read_powershell" or "write_powershell" or "stop_powershell";
    }

    public static bool IsSearchTool(string? toolName)
    {
        return toolName is "web_search" or "search";
    }

    /// <summary>Extracts web search citations from resource-link tool results.</summary>
    public static List<SearchSource> ExtractSearchSources(ToolExecutionCompleteResult? result)
    {
        if (result?.Contents is not { Length: > 0 } contents)
            return [];

        var sources = new List<SearchSource>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in contents)
        {
            if (item is not ToolExecutionCompleteContentResourceLink resource
                || string.IsNullOrWhiteSpace(resource.Uri)
                || !Uri.TryCreate(resource.Uri, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !seenUrls.Add(uri.AbsoluteUri))
            {
                continue;
            }

            var title = string.IsNullOrWhiteSpace(resource.Title) ? resource.Name : resource.Title;
            if (string.IsNullOrWhiteSpace(title))
                title = uri.Host;

            sources.Add(new SearchSource
            {
                Title = title,
                Snippet = resource.Description?.Trim() ?? string.Empty,
                Url = uri.AbsoluteUri
            });
        }

        return sources;
    }

    public static bool IsWebFetchTool(string? toolName)
    {
        return toolName is "web_fetch" or "fetch" or "lumi_fetch";
    }

    public static SearchSource? ExtractFetchSource(string? argsJson)
    {
        var url = ExtractJsonField(argsJson, "url");
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !IsWebUri(uri))
            return null;

        return new SearchSource
        {
            Title = uri.Host,
            Snippet = string.Empty,
            Url = uri.AbsoluteUri
        };
    }

    private static bool IsWebUri(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>Returns true if the file looks like a user-facing deliverable, not a temp script.</summary>
    public static bool IsUserFacingFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return !ScriptExtensions.Contains(ext);
    }

    /// <summary>Converts a file:// URI or plain path to a local filesystem path.</summary>
    public static string? UriToLocalPath(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
            return parsed.LocalPath;
        if (Path.IsPathRooted(uri))
            return uri;
        return null;
    }

    // ── Terminal output helpers ───────────────────────────

    /// <summary>Strips SDK metadata lines (e.g. exit code markers) from terminal output.</summary>
    public static string? CleanTerminalOutput(string? output)
    {
        if (string.IsNullOrEmpty(output)) return output;
        return Regex.Replace(
            output,
            @"\s*<exited with exit code \d+>\s*$",
            "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout).TrimEnd();
    }

    public static string MergeTerminalOutput(string? existingOutput, string incomingOutput, bool replaceExistingOutput)
    {
        if (string.IsNullOrWhiteSpace(incomingOutput))
            return existingOutput ?? string.Empty;

        if (replaceExistingOutput || string.IsNullOrWhiteSpace(existingOutput))
            return incomingOutput;

        if (incomingOutput.StartsWith(existingOutput, StringComparison.Ordinal))
            return incomingOutput;

        if (existingOutput.EndsWith(incomingOutput, StringComparison.Ordinal))
            return existingOutput;

        return existingOutput + Environment.NewLine + incomingOutput;
    }

    /// <summary>Extracts terminal/text output from SDK tool result fields.</summary>
    public static string? ExtractTerminalOutput(ToolExecutionCompleteResult? result)
    {
        if (result is null)
            return null;

        var detailed = CleanTerminalOutput(result.DetailedContent);
        if (!string.IsNullOrWhiteSpace(detailed))
            return detailed;

        var contentsText = ExtractTerminalOutputFromContents(result.Contents);
        if (!string.IsNullOrWhiteSpace(contentsText))
            return CleanTerminalOutput(contentsText);

        return CleanTerminalOutput(result.Content);
    }

    private static string? ExtractToolCompletionOutput(ToolExecutionCompleteResult? result)
    {
        if (result is null)
            return null;

        var parts = new List<string>();
        var contentsText = CleanTerminalOutput(ExtractTerminalOutputFromContents(result.Contents));
        AddDistinctOutputPart(parts, contentsText);
        AddDistinctOutputPart(parts, CleanTerminalOutput(result.DetailedContent));
        AddDistinctOutputPart(parts, CleanTerminalOutput(result.Content));

        return parts.Count == 0
            ? null
            : string.Join($"{Environment.NewLine}{Environment.NewLine}", parts);
    }

    public static string? ExtractFailedToolOutput(ToolExecutionCompleteError? error, ToolExecutionCompleteResult? result)
    {
        var errorOutput = LimitFailedToolOutput(FormatToolErrorOutput(error));
        var completionOutput = LimitFailedToolOutput(ExtractToolCompletionOutput(result));

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(errorOutput))
            parts.Add(errorOutput);

        if (!string.IsNullOrWhiteSpace(completionOutput)
            && !parts.Any(part => string.Equals(part, completionOutput, StringComparison.Ordinal)))
        {
            parts.Add(completionOutput);
        }

        return LimitFailedToolOutput(parts.Count == 0
            ? null
            : string.Join($"{Environment.NewLine}{Environment.NewLine}", parts));
    }

    private static string? FormatToolErrorOutput(ToolExecutionCompleteError? error)
    {
        if (error is null || string.IsNullOrWhiteSpace(error.Message))
            return null;

        var message = NormalizeDisplayText(error.Message)?.Trim();
        var code = NormalizeDisplayText(error.Code)?.Trim();
        if (string.IsNullOrWhiteSpace(message))
            return string.IsNullOrWhiteSpace(code) ? null : string.Format(Loc.Tool_ErrorCode, code);

        return string.IsNullOrWhiteSpace(code)
            ? message
            : string.Format(Loc.Tool_ErrorWithCode, message, code);
    }

    private static string? LimitFailedToolOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var raw = SafeSubstring(output, MaxRawFailedToolOutputChars);
        var trimmed = RedactCommonSecrets(NormalizeDisplayText(raw)?.Trim());
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        return trimmed.Length <= MaxFailedToolOutputChars
            ? trimmed
            : TrimToFailedOutputLimit(trimmed);
    }

    private static string TrimToFailedOutputLimit(string output)
    {
        var maxBodyLength = Math.Max(0, MaxFailedToolOutputChars - FailedToolOutputTruncationMarker.Length);
        var body = SafeSubstring(output, maxBodyLength);
        return body + FailedToolOutputTruncationMarker;
    }

    private static string SafeSubstring(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;

        var result = value[..maxLength];
        if (result.Length > 0 && char.IsHighSurrogate(result[^1]))
            result = result[..^1];

        return result;
    }

    internal static string? NormalizeDisplayText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var withoutAnsi = Regex.Replace(
            value,
            @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\)|P[^\x1B]*(?:\x1B\\)|[_^][^\x1B]*(?:\x1B\\))",
            string.Empty,
            RegexOptions.CultureInvariant,
            RegexTimeout);
        return new string(withoutAnsi
            .Where(IsAllowedDisplayChar)
            .ToArray());
    }

    private static bool IsAllowedDisplayChar(char c)
    {
        if (c is '\r' or '\n' or '\t')
            return true;

        if (char.IsControl(c))
            return false;

        return c is not ('\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E'
            or '\u2066' or '\u2067' or '\u2068' or '\u2069');
    }

    internal static string? RedactCommonSecrets(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var redacted = Regex.Replace(
            value,
            @"(?ix)
              (?<prefix>[""']?)
              \b
              (?<key>
                  [A-Z0-9_./-]*
                  (api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|token|password|passwd|pwd|secret|client[_-]?secret|private[_-]?key)
                  [A-Z0-9_./-]*
              )
              (?<suffix>[""']?)
              (?<sep>\s*[:=]\s*)
              (?:
                  (?<quote>"")(?:\\.|[^""\\])*""
                  | (?<quote>')(?:\\.|[^'\\])*'
                  | (?:bearer|basic)\s+\S+
                  | [^\s""',}]+
              )",
            match =>
            {
                var quote = match.Groups["quote"].Value;
                return $"{match.Groups["prefix"].Value}{match.Groups["key"].Value}{match.Groups["suffix"].Value}{match.Groups["sep"].Value}{quote}[REDACTED]{quote}";
            },
            RegexOptions.CultureInvariant,
            RegexTimeout);

        return Regex.Replace(
            redacted,
            @"(?ix)
              (?<name>[""']?\bauthorization\b[""']?\s*:\s*)
              (?<quote>[""']?)
              (?<scheme>bearer|basic)
              \s+\S+
              [""']?",
            match =>
            {
                var quote = match.Groups["quote"].Value;
                return $"{match.Groups["name"].Value}{quote}{match.Groups["scheme"].Value} [REDACTED]{quote}";
            },
            RegexOptions.CultureInvariant,
            RegexTimeout);
    }

    private static void AddDistinctOutputPart(List<string> parts, string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return;

        if (!parts.Any(part => string.Equals(part, output, StringComparison.Ordinal)))
            parts.Add(output);
    }

    /// <summary>Extracts terminal/text output from tool execution result contents.</summary>
    public static string? ExtractTerminalOutputFromContents(ToolExecutionCompleteContent[]? contents)
    {
        if (contents is not { Length: > 0 })
            return null;

        var chunks = new List<string>();
        foreach (var item in contents)
        {
            if (item is ToolExecutionCompleteContentTerminal terminal)
            {
                if (!string.IsNullOrWhiteSpace(terminal.Text))
                    chunks.Add(terminal.Text);
                continue;
            }

            if (item is ToolExecutionCompleteContentText text
                && !string.IsNullOrWhiteSpace(text.Text))
            {
                chunks.Add(text.Text);
            }
        }

        return chunks.Count > 0 ? string.Join(Environment.NewLine, chunks) : null;
    }

    public static void ApplyTerminalOutput(Chat chat, string rootToolCallId, string output, bool replaceExistingOutput)
    {
        if (string.IsNullOrWhiteSpace(rootToolCallId) || string.IsNullOrWhiteSpace(output))
            return;

        var rootToolMessage = chat.Messages.LastOrDefault(m => m.ToolCallId == rootToolCallId);
        if (rootToolMessage is null)
            return;

        rootToolMessage.ToolOutput = MergeTerminalOutput(rootToolMessage.ToolOutput, output, replaceExistingOutput);
    }

    public static string ResolveRootTerminalToolCallId(
        string toolCallId,
        Dictionary<string, string?> toolParentById,
        Dictionary<string, string> terminalRootByToolCallId)
    {
        if (terminalRootByToolCallId.TryGetValue(toolCallId, out var knownRoot))
            return knownRoot;

        var current = toolCallId;
        for (var depth = 0; depth < 24; depth++)
        {
            if (terminalRootByToolCallId.TryGetValue(current, out var mappedRoot))
            {
                terminalRootByToolCallId[toolCallId] = mappedRoot;
                return mappedRoot;
            }

            if (!toolParentById.TryGetValue(current, out var parent) || string.IsNullOrWhiteSpace(parent))
                break;

            current = parent;
        }

        terminalRootByToolCallId[toolCallId] = current;
        return current;
    }
}
