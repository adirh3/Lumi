using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GitHub.Copilot.SDK;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.Services;

/// <summary>
/// Terminal output processing, file detection, tool classification, and display name formatting utilities.
/// </summary>
public static partial class ToolDisplayHelper
{
    /// <summary>Extensions for intermediary/script files that shouldn't appear as attachment chips.</summary>
    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".ps1", ".bat", ".cmd", ".sh", ".bash", ".vbs", ".wsf", ".js", ".mjs", ".ts"
    };

    /// <summary>Formats a tool name for the status bar (e.g. "Running: Reading file.txt").</summary>
    public static string FormatToolStatusName(string toolName, string? argsJson = null)
    {
        var fileName = ExtractShortFileName(argsJson);
        return toolName switch
        {
            "web_fetch" or "fetch" or "lumi_fetch" => Loc.Tool_ReadingWebsite,
            "web_search" or "search" or "lumi_search" => Loc.Tool_SearchingWeb,
            "lumi_research" => "Researching",
            "view" or "read_file" or "read" => fileName is not null ? string.Format(Loc.Tool_ReadingNamed, fileName) : Loc.Tool_ReadingFile,
            "create" or "write_file" or "create_file" or "write" or "save_file" => fileName is not null ? string.Format(Loc.Tool_CreatingNamed, fileName) : Loc.Tool_CreatingFile,
            "edit" or "edit_file" or "str_replace_editor" or "str_replace" or "replace_string_in_file" or "insert" => fileName is not null ? string.Format(Loc.Tool_EditingNamed, fileName) : Loc.Tool_EditingFile,
            "multi_replace_string_in_file" => fileName is not null ? string.Format(Loc.Tool_EditingNamed, fileName) : Loc.Tool_EditingFiles,
            "list_dir" or "list_directory" or "ls" => Loc.Tool_ListingDirectory,
            "bash" or "shell" or "powershell" or "run_command" or "execute_command" or "run_terminal" or "run_in_terminal" => Loc.Tool_RunningCommand,
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
            "browser" => Loc.Tool_OpeningPage,
            "browser_look" => Loc.Tool_BrowserSnapshot,
            "browser_do" => Loc.Tool_Action,
            "browser_js" => Loc.Tool_BrowserEvaluate,
            "save_memory" => Loc.Tool_Remembering,
            "update_memory" => Loc.Tool_UpdatingMemory,
            "delete_memory" => Loc.Tool_Forgetting,
            "recall_memory" => Loc.Tool_Recalling,
            "announce_file" => Loc.Tool_SharingFile,
            "fetch_skill" => Loc.Tool_FetchingSkill,
            "ask_question" => Loc.Tool_AskingQuestion,
            "code_review" => Loc.Tool_ReviewingCode,
            "generate_tests" => Loc.Tool_GeneratingTests,
            "explain_code" => Loc.Tool_ExplainingCode,
            "analyze_project" => Loc.Tool_AnalyzingProject,
            "ui_list_windows" => Loc.Tool_ListingWindows,
            "ui_press_keys" => Loc.Tool_PressingKeys,
            "ui_inspect" => Loc.Tool_InspectingWindow,
            "ui_find" => Loc.Tool_FindingElement,
            "ui_click" => Loc.Tool_ClickingControl,
            "ui_type" => Loc.Tool_TypingInControl,
            "ui_read" => Loc.Tool_ReadingControl,
            _ => FormatSnakeCaseToTitle(toolName)
        };
    }

    public static string FormatSnakeCaseToTitle(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return name;
        words[0] = char.ToUpper(words[0][0]) + words[0][1..];
        return string.Join(' ', words);
    }

    /// <summary>Returns true if the tool creates files (for resource link detection).</summary>
    public static bool IsFileCreationTool(string? toolName)
    {
        return toolName is "write_file" or "create_file" or "create" or "edit_file"
            or "str_replace_editor" or "str_replace" or "create_and_write_file"
            or "replace_string_in_file" or "multi_replace_string_in_file"
            or "insert" or "write" or "save_file";
    }

    public static bool IsTerminalStreamingTool(string? toolName)
    {
        return toolName is "powershell" or "read_powershell" or "write_powershell" or "stop_powershell";
    }

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
        return Regex.Replace(output, @"\s*<exited with exit code \d+>\s*$", "", RegexOptions.IgnoreCase).TrimEnd();
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
    public static string? ExtractTerminalOutput(ToolExecutionCompleteDataResult? result)
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

    /// <summary>Extracts terminal/text output from tool execution result contents.</summary>
    public static string? ExtractTerminalOutputFromContents(ToolExecutionCompleteDataResultContentsItem[]? contents)
    {
        if (contents is not { Length: > 0 })
            return null;

        var chunks = new List<string>();
        foreach (var item in contents)
        {
            if (item is ToolExecutionCompleteDataResultContentsItemTerminal terminal)
            {
                if (!string.IsNullOrWhiteSpace(terminal.Text))
                    chunks.Add(terminal.Text);
                continue;
            }

            if (item is ToolExecutionCompleteDataResultContentsItemText text
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
