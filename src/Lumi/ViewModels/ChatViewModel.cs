using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot.SDK;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;
    private CancellationTokenSource? _cts;
    private string _accumulatedContent = "";
    private string _accumulatedReasoning = "";
    private ChatMessage? _streamingMessage;
    private ChatMessage? _reasoningMessage;
    private readonly HashSet<string> _shownFileChips = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while LoadChat is bulk-adding messages. The View skips CollectionChanged.Add during this.</summary>
    public bool IsLoadingChat { get; private set; }

    [ObservableProperty] private Chat? _currentChat;
    [ObservableProperty] private string? _promptText;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _selectedModel;
    [ObservableProperty] private LumiAgent? _activeAgent;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public ObservableCollection<string> AvailableModels { get; set; } = ["claude-sonnet-4", "gpt-4o", "o3"];
    public ObservableCollection<string> PendingAttachments { get; } = [];

    // Events for the view to react to
    public event Action? ScrollToEndRequested;
    public event Action? ChatUpdated;
    public event Action<string>? FileCreatedByTool;

    public ChatViewModel(DataStore dataStore, CopilotService copilotService)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        _selectedModel = dataStore.Data.Settings.PreferredModel;

        _copilotService.OnTurnStart += () => Dispatcher.UIThread.Post(() =>
        {
            IsBusy = true;
            IsStreaming = true;
            StatusText = "Thinking…";
        });

        _copilotService.OnMessageDelta += delta => Dispatcher.UIThread.Post(() =>
        {
            _accumulatedContent += delta;
            if (_streamingMessage is not null)
            {
                _streamingMessage.Content = _accumulatedContent;
                var vm = Messages.LastOrDefault(m => m.Message == _streamingMessage);
                vm?.NotifyContentChanged();
            }
            else
            {
                _streamingMessage = new ChatMessage
                {
                    Role = "assistant",
                    Author = ActiveAgent?.Name ?? "Lumi",
                    Content = _accumulatedContent,
                    IsStreaming = true
                };
                Messages.Add(new ChatMessageViewModel(_streamingMessage));
            }
            StatusText = "Generating…";
            ScrollToEndRequested?.Invoke();
        });

        _copilotService.OnMessageComplete += content => Dispatcher.UIThread.Post(() =>
        {
            if (_streamingMessage is not null)
            {
                _streamingMessage.Content = content;
                _streamingMessage.IsStreaming = false;
                var vm = Messages.LastOrDefault(m => m.Message == _streamingMessage);
                vm?.NotifyStreamingEnded();

                CurrentChat?.Messages.Add(_streamingMessage);
            }
            _streamingMessage = null;
            _accumulatedContent = "";
        });

        _copilotService.OnReasoningDelta += (id, delta) => Dispatcher.UIThread.Post(() =>
        {
            _accumulatedReasoning += delta;
            if (_reasoningMessage is null)
            {
                _reasoningMessage = new ChatMessage
                {
                    Role = "reasoning",
                    Author = "Thinking",
                    Content = _accumulatedReasoning,
                    IsStreaming = true
                };
                Messages.Add(new ChatMessageViewModel(_reasoningMessage));
            }
            else
            {
                _reasoningMessage.Content = _accumulatedReasoning;
                var vm = Messages.LastOrDefault(m => m.Message == _reasoningMessage);
                vm?.NotifyContentChanged();
            }
            StatusText = "Reasoning…";
            ScrollToEndRequested?.Invoke();
        });

        _copilotService.OnReasoningComplete += content => Dispatcher.UIThread.Post(() =>
        {
            if (_reasoningMessage is not null)
            {
                _reasoningMessage.Content = content;
                _reasoningMessage.IsStreaming = false;
                var vm = Messages.LastOrDefault(m => m.Message == _reasoningMessage);
                vm?.NotifyStreamingEnded();
            }
            _reasoningMessage = null;
            _accumulatedReasoning = "";
        });

        _copilotService.OnToolStart += (callId, name, args) => Dispatcher.UIThread.Post(() =>
        {
            var displayName = FormatToolDisplayName(name, args);
            var toolMsg = new ChatMessage
            {
                Role = "tool",
                ToolCallId = callId,
                ToolName = name,
                ToolStatus = "InProgress",
                Content = args ?? "",
                Author = displayName
            };
            CurrentChat?.Messages.Add(toolMsg);
            Messages.Add(new ChatMessageViewModel(toolMsg));
            StatusText = $"Running {displayName}…";
            ScrollToEndRequested?.Invoke();
        });

        _copilotService.OnToolComplete += (callId, success) => Dispatcher.UIThread.Post(() =>
        {
            var vm = Messages.LastOrDefault(m => m.Message.ToolCallId == callId);
            if (vm is not null)
            {
                vm.Message.ToolStatus = success ? "Completed" : "Failed";
                vm.NotifyToolStatusChanged();

                // Only show file chip for file-creation tools
                if (success)
                {
                    string? filePath = null;
                    if (IsFileCreationTool(vm.Message.ToolName))
                        filePath = ExtractFilePathFromArgs(vm.Message.Content);
                    else if (vm.Message.ToolName == "powershell")
                        filePath = ExtractFilePathFromPowershell(vm.Message.Content);

                    if (filePath is not null && File.Exists(filePath) && _shownFileChips.Add(filePath))
                        FileCreatedByTool?.Invoke(filePath);
                }
            }
        });

        _copilotService.OnTurnEnd += () => Dispatcher.UIThread.Post(() =>
        {
            IsBusy = false;
            IsStreaming = false;
            StatusText = "";
            SaveChat();
        });

        _copilotService.OnTitleChanged += title => Dispatcher.UIThread.Post(() =>
        {
            if (CurrentChat is not null)
            {
                CurrentChat.Title = title;
                SaveChat();
                ChatUpdated?.Invoke();
            }
        });

        _copilotService.OnError += error => Dispatcher.UIThread.Post(() =>
        {
            StatusText = $"Error: {error}";
            IsBusy = false;
            IsStreaming = false;
        });
    }

    public void LoadChat(Chat chat)
    {
        IsLoadingChat = true;
        Messages.Clear();
        foreach (var msg in chat.Messages.Where(m => m.Role != "reasoning"))
            Messages.Add(new ChatMessageViewModel(msg));
        CurrentChat = chat;
        IsLoadingChat = false;
    }

    public void ClearChat()
    {
        Messages.Clear();
        CurrentChat = null;
        _streamingMessage = null;
        _reasoningMessage = null;
        _accumulatedContent = "";
        _accumulatedReasoning = "";
        _shownFileChips.Clear();
        StatusText = "";
    }

    [RelayCommand]
    private async Task SendMessage()
    {
        if (string.IsNullOrWhiteSpace(PromptText))
            return;

        if (!_copilotService.IsConnected)
        {
            StatusText = "Not connected to GitHub Copilot. Retrying…";
            try { await _copilotService.ConnectAsync(); }
            catch { StatusText = "Connection failed. Check your GitHub Copilot access."; return; }
        }

        var prompt = PromptText!.Trim();
        PromptText = "";

        var attachments = TakePendingAttachments();

        // Create chat if needed
        if (CurrentChat is null)
        {
            var chat = new Chat
            {
                Title = prompt.Length > 40 ? prompt[..40].Trim() + "…" : prompt
            };
            _dataStore.Data.Chats.Add(chat);
            CurrentChat = chat;
            SaveChat();
            ChatUpdated?.Invoke();
        }

        // Add user message
        var userMsg = new ChatMessage
        {
            Role = "user",
            Content = prompt,
            Author = _dataStore.Data.Settings.UserName ?? "You",
            Attachments = attachments?.Select(a => a.Path).ToList() ?? []
        };
        CurrentChat.Messages.Add(userMsg);
        Messages.Add(new ChatMessageViewModel(userMsg));
        SaveChat();
        ScrollToEndRequested?.Invoke();

        // Build system prompt
        var skills = _dataStore.Data.Skills;
        var memories = _dataStore.Data.Memories;
        var project = CurrentChat.ProjectId.HasValue
            ? _dataStore.Data.Projects.FirstOrDefault(p => p.Id == CurrentChat.ProjectId)
            : null;
        var systemPrompt = SystemPromptBuilder.Build(
            _dataStore.Data.Settings, ActiveAgent, project, skills, memories);

        try
        {
            _cts = new CancellationTokenSource();

            // Create or resume session
            var workDir = GetWorkingDirectory();
            if (CurrentChat.CopilotSessionId is null)
            {
                var session = await _copilotService.CreateSessionAsync(
                    systemPrompt, SelectedModel, workDir, _cts.Token);
                CurrentChat.CopilotSessionId = session.SessionId;
            }
            else if (_copilotService.CurrentSessionId != CurrentChat.CopilotSessionId)
            {
                await _copilotService.ResumeSessionAsync(
                    CurrentChat.CopilotSessionId, systemPrompt, SelectedModel, workDir, _cts.Token);
            }

            await _copilotService.SendMessageAsync(prompt, attachments, _cts.Token);
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            IsBusy = false;
            IsStreaming = false;
        }
    }

    [RelayCommand]
    private async Task StopGeneration()
    {
        _cts?.Cancel();
        await _copilotService.AbortAsync();
        IsBusy = false;
        IsStreaming = false;
        StatusText = "Stopped";
    }

    private void SaveChat()
    {
        if (CurrentChat is not null)
        {
            CurrentChat.UpdatedAt = DateTimeOffset.Now;
            _dataStore.Save();
        }
    }

    public void SetActiveAgent(LumiAgent? agent)
    {
        ActiveAgent = agent;
    }

    public void AddAttachment(string filePath)
    {
        if (!PendingAttachments.Contains(filePath))
            PendingAttachments.Add(filePath);
    }

    public void RemoveAttachment(string filePath)
    {
        PendingAttachments.Remove(filePath);
    }

    private List<UserMessageDataAttachmentsItemFile>? TakePendingAttachments()
    {
        if (PendingAttachments.Count == 0) return null;
        var items = PendingAttachments.Select(fp => new UserMessageDataAttachmentsItemFile
        {
            Path = fp,
            DisplayName = Path.GetFileName(fp)
        }).ToList();
        PendingAttachments.Clear();
        return items;
    }

    public static bool IsFileCreationTool(string? toolName)
    {
        return toolName is "write_file" or "create_file" or "create" or "edit_file"
            or "str_replace_editor" or "str_replace" or "create_and_write_file"
            or "replace_string_in_file" or "multi_replace_string_in_file"
            or "insert" or "write" or "save_file";
    }

    public static string? ExtractFilePathFromArgs(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("filePath", out var fp)) return fp.GetString();
            if (root.TryGetProperty("path", out var p)) return p.GetString();
            if (root.TryGetProperty("file", out var f)) return f.GetString();
            if (root.TryGetProperty("filename", out var fn)) return fn.GetString();
            if (root.TryGetProperty("file_path", out var fp2)) return fp2.GetString();
            // For multi_replace, check first replacement
            if (root.TryGetProperty("replacements", out var repl) 
                && repl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in repl.EnumerateArray())
                {
                    if (item.TryGetProperty("filePath", out var rfp)) return rfp.GetString();
                    if (item.TryGetProperty("path", out var rp)) return rp.GetString();
                }
            }
            return null;
        }
        catch { return null; }
    }

    private static readonly System.Text.RegularExpressions.Regex FilePathRegex = new(
        @"(?:^|[\s`""'(\[])([A-Za-z]:\\[^\s`""'<>|*?\[\]]+\.\w{1,10})"
        + @"|(?:^|[\s`""'(\[])((?:/|~/)[^\s`""'<>|*?\[\]]+\.\w{1,10})",
        System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex PsFileOutputRegex = new(
        @"(?:SaveAs|Save)\s*\(\s*[""']([^""']+)[""']"
        + @"|(?:Out-File|Set-Content|Add-Content|Export-\w+)\s+['""]?([^'""\s;|]+)"
        + @"|>\s*['""]?([^'""\s;|]+)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Extracts a file output path from a powershell command (SaveAs, Out-File, Set-Content, redirect, etc.).
    /// Returns the raw filename if variables like $PWD prevent full resolution.
    /// </summary>
    public static string? ExtractFilePathFromPowershell(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            if (!doc.RootElement.TryGetProperty("command", out var cmdEl)) return null;
            var command = cmdEl.GetString();
            if (string.IsNullOrEmpty(command)) return null;

            var match = PsFileOutputRegex.Match(command);
            if (!match.Success) return null;

            var rawPath = match.Groups[1].Success ? match.Groups[1].Value
                        : match.Groups[2].Success ? match.Groups[2].Value
                        : match.Groups[3].Success ? match.Groups[3].Value
                        : null;
            if (rawPath is null) return null;

            // If the path contains PS variables, just return the filename portion
            if (rawPath.Contains('$'))
                return Path.GetFileName(rawPath);

            try { return Path.GetFullPath(rawPath); }
            catch { return rawPath; }
        }
        catch { return null; }
    }

    public static string[] ExtractFilePathsFromContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];
        var matches = FilePathRegex.Matches(content);
        return matches
            .Select(m => !string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Removes the user message and its response, then resends.
    /// The message content may have been edited before calling this.
    /// </summary>
    public async Task ResendFromMessageAsync(ChatMessage userMessage)
    {
        if (CurrentChat is null || IsBusy) return;

        var idx = CurrentChat.Messages.IndexOf(userMessage);
        if (idx < 0) return;

        var prompt = userMessage.Content;

        // Remove the user message and everything after it
        while (CurrentChat.Messages.Count > idx)
            CurrentChat.Messages.RemoveAt(CurrentChat.Messages.Count - 1);

        // Rebuild the UI without the removed messages
        Messages.Clear();
        foreach (var msg in CurrentChat.Messages.Where(m => m.Role != "reasoning"))
            Messages.Add(new ChatMessageViewModel(msg));

        _shownFileChips.Clear();

        // Re-add the user message as a fresh entry
        var newUserMsg = new ChatMessage
        {
            Role = "user",
            Content = prompt,
            Author = userMessage.Author
        };
        CurrentChat.Messages.Add(newUserMsg);
        Messages.Add(new ChatMessageViewModel(newUserMsg));
        SaveChat();
        ScrollToEndRequested?.Invoke();

        // Resend
        if (!_copilotService.IsConnected)
        {
            StatusText = "Not connected to GitHub Copilot. Retrying\u2026";
            try { await _copilotService.ConnectAsync(); }
            catch { StatusText = "Connection failed."; return; }
        }

        var skills = _dataStore.Data.Skills;
        var memories = _dataStore.Data.Memories;
        var project = CurrentChat.ProjectId.HasValue
            ? _dataStore.Data.Projects.FirstOrDefault(p => p.Id == CurrentChat.ProjectId)
            : null;
        var systemPrompt = SystemPromptBuilder.Build(
            _dataStore.Data.Settings, ActiveAgent, project, skills, memories);

        try
        {
            _cts = new CancellationTokenSource();
            var workDir = GetWorkingDirectory();

            // Try to resume existing session to preserve context
            if (CurrentChat.CopilotSessionId is not null)
            {
                if (_copilotService.CurrentSessionId == CurrentChat.CopilotSessionId)
                {
                    // Session is still active \u2014 just resend
                }
                else
                {
                    // Resume the saved session to restore server-side history
                    await _copilotService.ResumeSessionAsync(
                        CurrentChat.CopilotSessionId, systemPrompt, SelectedModel, workDir, _cts.Token);
                }
            }
            else
            {
                // No session at all \u2014 create new (first-time edge case)
                var session = await _copilotService.CreateSessionAsync(
                    systemPrompt, SelectedModel, workDir, _cts.Token);
                CurrentChat.CopilotSessionId = session.SessionId;
            }

            await _copilotService.SendMessageAsync(userMessage.Content, null, _cts.Token);
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            IsBusy = false;
            IsStreaming = false;
        }
    }

    private string GetWorkingDirectory()
    {
        // If a project is selected and has a path, use it
        if (CurrentChat?.ProjectId.HasValue == true)
        {
            var project = _dataStore.Data.Projects
                .FirstOrDefault(p => p.Id == CurrentChat.ProjectId);
            if (project is not null && !string.IsNullOrWhiteSpace(project.Instructions))
            {
                // Check if instructions contain a directory hint (first line may be a path)
            }
        }

        // Default to user's home/Documents folder
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var lumiDir = Path.Combine(docs, "Lumi");
        Directory.CreateDirectory(lumiDir);
        return lumiDir;
    }

    private static string FormatToolDisplayName(string toolName, string? argsJson = null)
    {
        var fileName = ExtractShortFileName(argsJson);
        return toolName switch
        {
            "web_fetch" or "fetch" => "Reading website",
            "web_search" or "search" => "Searching the web",
            "view" or "read_file" or "read" => fileName is not null ? $"Reading {fileName}" : "Reading file",
            "create" or "write_file" or "create_file" or "write" or "save_file" => fileName is not null ? $"Creating {fileName}" : "Creating file",
            "edit" or "edit_file" or "str_replace_editor" or "str_replace" or "replace_string_in_file" or "insert" => fileName is not null ? $"Editing {fileName}" : "Editing file",
            "multi_replace_string_in_file" => fileName is not null ? $"Editing {fileName}" : "Editing files",
            "list_dir" or "list_directory" or "ls" => "Listing directory",
            "bash" or "shell" or "powershell" or "run_command" or "execute_command" or "run_terminal" or "run_in_terminal" => "Running command",
            "read_powershell" => "Reading terminal output",
            "write_powershell" => "Writing to terminal",
            "stop_powershell" => "Stopping terminal",
            "report_intent" => "Planning",
            "grep" or "grep_search" or "search_files" or "glob" => "Searching files",
            "file_search" or "find" => "Finding files",
            "semantic_search" => "Searching codebase",
            "delete_file" or "delete" or "rm" => fileName is not null ? $"Deleting {fileName}" : "Deleting file",
            "move_file" or "rename_file" or "mv" or "rename" => fileName is not null ? $"Moving {fileName}" : "Moving file",
            "get_errors" or "diagnostics" => fileName is not null ? $"Checking {fileName}" : "Checking errors",
            "browser_navigate" or "navigate" => "Opening page",
            "browser_click" or "click" => "Clicking element",
            "browser_type" or "type" => "Typing text",
            "browser_snapshot" or "screenshot" => "Taking screenshot",
            _ => FormatSnakeCaseToTitle(toolName)
        };
    }

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
            // For multi_replace, check first replacement
            else if (root.TryGetProperty("replacements", out var repl)
                     && repl.ValueKind == JsonValueKind.Array)
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

    private static string FormatSnakeCaseToTitle(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return name;
        words[0] = char.ToUpper(words[0][0]) + words[0][1..];
        return string.Join(' ', words);
    }
}

public partial class ChatMessageViewModel : ObservableObject
{
    public ChatMessage Message { get; }

    [ObservableProperty] private string _content;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string? _toolStatus;

    public string Role => Message.Role;
    public string? Author => Message.Author;
    public string TimestampText => Message.Timestamp.ToString("HH:mm");
    public string? ToolName => Message.ToolName;

    public ChatMessageViewModel(ChatMessage message)
    {
        Message = message;
        _content = message.Content;
        _isStreaming = message.IsStreaming;
        _toolStatus = message.ToolStatus;
    }

    public void NotifyContentChanged()
    {
        Content = Message.Content;
    }

    public void NotifyStreamingEnded()
    {
        Content = Message.Content;
        IsStreaming = false;
    }

    public void NotifyToolStatusChanged()
    {
        ToolStatus = Message.ToolStatus;
    }
}
