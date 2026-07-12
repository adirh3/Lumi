#if DEBUG
#pragma warning disable IL2026 // Debug-only bridge uses flexible JSON payloads and is excluded from Release builds.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Lumi.Models;
using Lumi.ViewModels;

namespace Lumi.Services;

internal sealed class LumiDebugBridge : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private readonly DataStore _dataStore;
    private readonly MainViewModel _mainViewModel;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private int _port;

    public LumiDebugBridge(DataStore dataStore, MainViewModel mainViewModel)
    {
        _dataStore = dataStore;
        _mainViewModel = mainViewModel;
    }

    public static string StatusFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lumi",
        "debug-bridge.json");

    public static string StatusDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lumi",
        "debug-bridges");

    private string BaseUrl => $"http://127.0.0.1:{_port}";
    private string InstanceStatusFilePath => Path.Combine(StatusDirectory, $"{_instanceId}.json");

    public void Start()
    {
        if (_listener is { IsListening: true })
            return;

        Exception? lastError = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var port = GetFreeLoopbackPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _port = port;
                _listener = listener;
                _cts = new CancellationTokenSource();
                _listenerTask = Task.Run(() => ListenAsync(listener, _cts.Token));
                WriteStatusFile();
                return;
            }
            catch (Exception ex) when (ex is HttpListenerException or SocketException)
            {
                lastError = ex;
                listener.Close();
            }
        }

        throw new InvalidOperationException("Failed to start Lumi debug bridge.", lastError);
    }

    public async ValueTask DisposeAsync()
    {
        var listener = _listener;
        var cts = _cts;
        var listenerTask = _listenerTask;
        _listener = null;
        _cts = null;
        _listenerTask = null;

        cts?.Cancel();
        if (listener is not null)
        {
            try { listener.Stop(); }
            catch (ObjectDisposedException) { }
            listener.Close();
        }

        if (listenerTask is not null)
        {
            try { await listenerTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (HttpListenerException) { }
        }

        cts?.Dispose();
        DeleteOwnStatusFile();
    }

    private async Task ListenAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch when (cancellationToken.IsCancellationRequested || !listener.IsListening)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestWithErrorBoundaryAsync(context, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleRequestWithErrorBoundaryAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            await HandleRequestAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.InternalServerError, new
                {
                    ok = false,
                    error = ex.Message,
                    type = ex.GetType().Name
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception writeException) when (writeException is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Client disconnected or bridge is shutting down while reporting the original error.
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if (!IsAuthorized(context.Request))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.Unauthorized, new
            {
                ok = false,
                error = "Invalid or missing Lumi debug bridge token."
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
        if (string.Equals(path, "/status", StringComparison.OrdinalIgnoreCase)
            && string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            var status = await InvokeUiAsync(BuildStatus).ConfigureAwait(false);
            await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ok = true, result = status }, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!string.Equals(path, "/invoke", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new
            {
                ok = false,
                error = "Use GET /status or POST /invoke."
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new
            {
                ok = false,
                error = "Request body is required."
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var request = JsonSerializer.Deserialize<BridgeRequest>(body, JsonOptions)
            ?? throw new InvalidOperationException("Invalid debug bridge request.");
        if (string.IsNullOrWhiteSpace(request.Action))
            throw new InvalidOperationException("Debug bridge action is required.");

        var result = await ExecuteAsync(request.Action, request.Arguments, cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ok = true, result }, cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<object> ExecuteAsync(string action, JsonElement? arguments, CancellationToken cancellationToken)
    {
        var normalized = action.Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        return normalized switch
        {
            "status" => InvokeUiAsync(BuildStatus),
            "navigate" => InvokeUiAsync(() => Navigate(arguments)),
            "list_chats" => InvokeUiAsync(() => ListChats(arguments)),
            "create_chat" => InvokeUiAsync(() => CreateChatAsync(arguments)),
            "open_chat" => InvokeUiAsync(() => OpenChatAsync(arguments)),
            "move_chat" => InvokeUiAsync(() => MoveChat(arguments)),
            "send_message" => SendMessageAsync(arguments, cancellationToken),
            "wait_for_idle" => WaitForIdleAsync(arguments, cancellationToken),
            "read_transcript" => ReadTranscriptAsync(arguments),
            "read_activity" => ReadActivityAsync(arguments),
            "load_fixture" => InvokeUiAsync(LoadFixture),
            "load_background_shell" => InvokeUiAsync(LoadBackgroundShellFixture),
            "list_features" => InvokeUiAsync(() => ListFeatures(arguments)),
            "configure_feature" => InvokeUiAsync(() => ConfigureFeatureAsync(arguments)),
            _ => throw new InvalidOperationException($"Unknown Lumi debug bridge action '{action}'.")
        };
    }

    private object BuildStatus()
    {
        var chat = _mainViewModel.ChatVM.CurrentChat;
        return new
        {
            bridge = new
            {
                instanceId = _instanceId,
                processId = Environment.ProcessId,
                url = BaseUrl,
                statusFilePath = StatusFilePath,
                instanceStatusFilePath = InstanceStatusFilePath,
                appDataDir = Path.GetDirectoryName(DataStore.ChatsDir),
                appDataRoot = Directory.GetParent(Path.GetDirectoryName(DataStore.ChatsDir)!)?.FullName
            },
            app = new
            {
                selectedNavIndex = _mainViewModel.SelectedNavIndex,
                page = GetPageName(_mainViewModel.SelectedNavIndex),
                isConnected = _mainViewModel.IsConnected,
                isConnecting = _mainViewModel.IsConnecting,
                connectionStatus = _mainViewModel.ConnectionStatus,
                isOnboarded = _mainViewModel.IsOnboarded,
                isBusy = _mainViewModel.ChatVM.IsBusy,
                isStreaming = _mainViewModel.ChatVM.IsStreaming,
                statusText = _mainViewModel.ChatVM.StatusText
            },
            context = new
            {
                selectedModel = _mainViewModel.ChatVM.SelectedModel,
                selectedContextWindowTier = _mainViewModel.ChatVM.SelectedContextWindowTier,
                activeSessionModel = _mainViewModel.ChatVM.ActiveSessionModelId,
                activeSessionContextWindowTier = _mainViewModel.ChatVM.ActiveSessionContextWindowTier,
                currentTokens = _mainViewModel.ChatVM.ContextCurrentTokens,
                tokenLimit = _mainViewModel.ChatVM.ContextTokenLimit,
                tokenLimitSource = _mainViewModel.ChatVM.ContextTokenLimitSourceDisplay,
                usageDisplay = _mainViewModel.ChatVM.ContextUsageDisplay,
                usagePercent = _mainViewModel.ChatVM.ContextUsagePercent
            },
            activeChat = chat is null ? null : ChatSummary(chat),
            counts = new
            {
                chats = _dataStore.Data.Chats.Count,
                projects = _dataStore.Data.Projects.Count,
                skills = _dataStore.Data.Skills.Count,
                lumis = _dataStore.Data.Agents.Count,
                mcpServers = _dataStore.Data.McpServers.Count,
                memories = _dataStore.Data.Memories.Count,
                jobs = _dataStore.Data.BackgroundJobs.Count
            }
        };
    }

    private object Navigate(JsonElement? arguments)
    {
        var args = arguments ?? default;
        var index = GetInt(args, "index");
        if (index is null)
        {
            var page = RequireString(args, "page");
            index = ResolvePageIndex(page);
        }

        if (index is < 0 or > 7)
            throw new InvalidOperationException("Navigation index must be between 0 and 7.");

        _mainViewModel.SelectedNavIndex = index.Value;

        var settingsPage = GetString(args, "settingsPage");
        if (_mainViewModel.SelectedNavIndex == 7 && !string.IsNullOrWhiteSpace(settingsPage))
            _mainViewModel.SettingsVM.SelectedPageIndex = ResolveSettingsPageIndex(settingsPage);

        return BuildStatus();
    }

    private object ListChats(JsonElement? arguments)
    {
        var args = arguments ?? default;
        var query = GetString(args, "query");
        var title = GetString(args, "title");
        var exactTitle = GetBool(args, "exactTitle") ?? false;
        var limit = Math.Clamp(GetInt(args, "limit") ?? 25, 1, 500);
        var offset = Math.Max(0, GetInt(args, "offset") ?? 0);
        var chats = _dataStore.Data.Chats.AsEnumerable();
        var project = ResolveProject(args, required: false);
        if (project is not null)
            chats = chats.Where(chat => chat.ProjectId == project.Id);

        var agent = ResolveAgent(args, required: false);
        if (agent is not null)
            chats = chats.Where(chat => chat.AgentId == agent.Id);

        if (!string.IsNullOrWhiteSpace(title))
        {
            chats = exactTitle
                ? chats.Where(chat => string.Equals(chat.Title, title, StringComparison.OrdinalIgnoreCase))
                : chats.Where(chat => chat.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            chats = chats.Where(chat =>
                chat.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || chat.Id.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)
                || (GetProjectName(chat.ProjectId)?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (GetAgentName(chat.AgentId)?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var includeEmpty = GetBool(args, "includeEmpty") ?? true;
        if (!includeEmpty)
            chats = chats.Where(chat => chat.Messages.Count > 0 || chat.CopilotSessionId is not null);

        var isRunning = GetBool(args, "isRunning");
        if (isRunning.HasValue)
            chats = chats.Where(chat => chat.IsRunning == isRunning.Value);

        var hasUnread = GetBool(args, "hasUnread");
        if (hasUnread.HasValue)
            chats = chats.Where(chat => chat.HasUnreadMessages == hasUnread.Value);

        var createdAfter = GetDateTimeOffset(args, "createdAfter");
        if (createdAfter.HasValue)
            chats = chats.Where(chat => chat.CreatedAt > createdAfter.Value);
        var createdBefore = GetDateTimeOffset(args, "createdBefore");
        if (createdBefore.HasValue)
            chats = chats.Where(chat => chat.CreatedAt < createdBefore.Value);
        var updatedAfter = GetDateTimeOffset(args, "updatedAfter");
        if (updatedAfter.HasValue)
            chats = chats.Where(chat => chat.UpdatedAt > updatedAfter.Value);
        var updatedBefore = GetDateTimeOffset(args, "updatedBefore");
        if (updatedBefore.HasValue)
            chats = chats.Where(chat => chat.UpdatedAt < updatedBefore.Value);

        var filtered = SortChats(chats, args).ToList();
        return new
        {
            totalMatched = filtered.Count,
            offset,
            limit,
            chats = filtered
                .Skip(offset)
                .Take(limit)
                .Select(ChatSummary)
                .ToList(),
            activeChatId = _mainViewModel.ChatVM.CurrentChat?.Id
        };
    }

    private async Task<object> CreateChatAsync(JsonElement? arguments)
    {
        var args = arguments ?? default;
        var now = DateTimeOffset.Now;
        var project = ResolveProject(args, required: false);
        var agent = ResolveAgent(args, required: false);
        var skillIds = ResolveSkills(args).Select(skill => skill.Id).ToList();
        var mcpServerNames = GetStringArray(args, "mcpServerNames");
        var title = GetString(args, "title")?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = "Debug chat";

        var chat = new Chat
        {
            Title = title,
            CreatedAt = now,
            UpdatedAt = now,
            ProjectId = project?.Id,
            AgentId = agent?.Id,
            ActiveSkillIds = skillIds,
            ActiveMcpServerNames = mcpServerNames,
            HasExplicitMcpServerSelection = mcpServerNames.Count > 0,
            LastModelUsed = GetString(args, "model") ?? _dataStore.Data.Settings.PreferredModel,
            LastReasoningEffortUsed = GetString(args, "reasoningEffort") ?? _dataStore.Data.Settings.ReasoningEffort,
            LastContextWindowTierUsed = GetString(args, "contextWindowTier") ?? _dataStore.Data.Settings.ContextWindowTier
        };

        _dataStore.Data.Chats.Add(chat);
        _dataStore.MarkChatChanged(chat);
        await _dataStore.SaveChatAsync(chat).ConfigureAwait(true);
        await _dataStore.SaveAsync().ConfigureAwait(true);
        _mainViewModel.RefreshChatList();

        var open = GetBool(args, "open") ?? true;
        if (open)
            await _mainViewModel.OpenChatByIdAsync(chat.Id).ConfigureAwait(true);

        return new
        {
            chat = ChatDetails(chat),
            opened = open
        };
    }

    private async Task<object> OpenChatAsync(JsonElement? arguments)
    {
        var chat = ResolveChat(arguments, required: true)!;
        var opened = await _mainViewModel.OpenChatByIdAsync(chat.Id).ConfigureAwait(true);
        return new
        {
            opened,
            chat = ChatDetails(chat),
            activeChatId = _mainViewModel.ChatVM.CurrentChat?.Id
        };
    }

    private async Task<object> SendMessageAsync(JsonElement? arguments, CancellationToken cancellationToken)
    {
        var args = arguments ?? default;
        var message = GetString(args, "message") ?? GetString(args, "prompt");
        if (string.IsNullOrWhiteSpace(message))
            throw new InvalidOperationException("message is required.");

        var newChat = GetBool(args, "newChat") ?? false;
        if (newChat)
        {
            await InvokeUiAsync(() =>
            {
                _mainViewModel.NewChatCommand.Execute(null);
                return true;
            }).ConfigureAwait(false);
        }

        var targetChat = ResolveChat(args, required: false, allowCurrent: false);
        if (targetChat is not null)
            await InvokeUiAsync(() => _mainViewModel.OpenChatByIdAsync(targetChat.Id)).ConfigureAwait(false);

        var beforeSend = await InvokeUiAsync(CaptureSendObservation).ConfigureAwait(false);
        await InvokeUiAsync(async () =>
        {
            _mainViewModel.SelectedNavIndex = 0;
            _mainViewModel.ChatVM.PromptText = message;
            await _mainViewModel.ChatVM.SendMessageCommand.ExecuteAsync(null).ConfigureAwait(true);
            return true;
        }).ConfigureAwait(false);

        var activeChatId = await InvokeUiAsync(() => _mainViewModel.ChatVM.CurrentChat?.Id).ConfigureAwait(false);
        if ((GetBool(args, "waitForIdle") ?? true) && activeChatId.HasValue)
        {
            await WaitForIdleCoreAsync(
                activeChatId.Value,
                GetInt(args, "timeoutMs") ?? 180000,
                Math.Clamp(GetInt(args, "pollIntervalMs") ?? 250, 50, 5000),
                cancellationToken).ConfigureAwait(false);
        }

        var afterSend = await InvokeUiAsync(CaptureSendObservation).ConfigureAwait(false);
        var assistantMessageAdded = afterSend.AssistantMessageCount > beforeSend.AssistantMessageCount;
        var errorMessageAdded = afterSend.ErrorMessageCount > beforeSend.ErrorMessageCount;
        var result = await InvokeUiAsync(() => new Dictionary<string, object?>
        {
            ["sent"] = true,
            ["chat"] = _mainViewModel.ChatVM.CurrentChat is null ? null : ChatDetails(_mainViewModel.ChatVM.CurrentChat),
            ["isBusy"] = _mainViewModel.ChatVM.IsBusy,
            ["statusText"] = _mainViewModel.ChatVM.StatusText,
            ["messageCountBefore"] = beforeSend.MessageCount,
            ["messageCountAfter"] = afterSend.MessageCount,
            ["assistantMessageAdded"] = assistantMessageAdded,
            ["errorMessageAdded"] = errorMessageAdded,
            ["lastAssistantMessage"] = Preview(afterSend.LastAssistantContent, Math.Clamp(GetInt(args, "maxContentChars") ?? 4000, 0, 100000)),
            ["lastErrorMessage"] = Preview(afterSend.LastErrorContent, Math.Clamp(GetInt(args, "maxContentChars") ?? 4000, 0, 100000)),
            ["warning"] = (GetBool(args, "waitForIdle") ?? true) && !assistantMessageAdded && !errorMessageAdded
                ? "Send completed and the chat became idle, but no new assistant or error message was observed."
                : null
        }).ConfigureAwait(false);
        if (GetBool(args, "returnActivity") == true)
            result["activity"] = await ReadActivityAsync(args).ConfigureAwait(false);
        if (GetBool(args, "returnTranscript") == true)
            result["transcript"] = await ReadTranscriptAsync(args).ConfigureAwait(false);

        return result;
    }

    private SendObservation CaptureSendObservation()
    {
        var messages = _mainViewModel.ChatVM.CurrentChat?.Messages
                       ?? _mainViewModel.ChatVM.Messages.Select(message => message.Message).ToList();
        return new SendObservation(
            MessageCount: messages.Count,
            AssistantMessageCount: messages.Count(message => message.Role == "assistant"),
            ErrorMessageCount: messages.Count(message => message.Role == "error"),
            LastAssistantContent: messages.LastOrDefault(message => message.Role == "assistant")?.Content,
            LastErrorContent: messages.LastOrDefault(message => message.Role == "error")?.Content);
    }

    private async Task<object> WaitForIdleAsync(JsonElement? arguments, CancellationToken cancellationToken)
    {
        var chat = await InvokeUiAsync(() => ResolveChat(arguments, required: false)).ConfigureAwait(false);
        var chatId = chat?.Id ?? await InvokeUiAsync(() => _mainViewModel.ChatVM.CurrentChat?.Id).ConfigureAwait(false);
        if (!chatId.HasValue)
            return new { idle = true, chat = (object?)null };

        var timeoutMs = GetInt(arguments ?? default, "timeoutMs") ?? 180000;
        return await WaitForIdleCoreAsync(
            chatId.Value,
            timeoutMs,
            Math.Clamp(GetInt(arguments ?? default, "pollIntervalMs") ?? 250, 50, 5000),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> WaitForIdleCoreAsync(
        Guid chatId,
        int timeoutMs,
        int pollIntervalMs = 250,
        CancellationToken cancellationToken = default)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds <= timeoutMs)
        {
            var state = await InvokeUiAsync(() =>
            {
                var chat = _dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == chatId)
                    ?? (_mainViewModel.ChatVM.CurrentChat?.Id == chatId ? _mainViewModel.ChatVM.CurrentChat : null);
                var isDisplayed = _mainViewModel.ChatVM.CurrentChat?.Id == chatId;
                var busy = isDisplayed
                    ? _mainViewModel.ChatVM.IsBusy || _mainViewModel.ChatVM.IsStreaming
                    : chat?.IsRunning == true;

                return new
                {
                    busy,
                    chat,
                    statusText = isDisplayed ? _mainViewModel.ChatVM.StatusText : ""
                };
            }).ConfigureAwait(false);

            if (!state.busy)
            {
                return new
                {
                    idle = true,
                    elapsedMs = deadline.ElapsedMilliseconds,
                    chat = state.chat is null ? null : ChatDetails(state.chat),
                    state.statusText
                };
            }

            await Task.Delay(pollIntervalMs, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Chat {chatId} did not become idle within {timeoutMs} ms.");
    }

    private async Task<object> ReadTranscriptAsync(JsonElement? arguments)
    {
        var args = arguments ?? default;
        var chat = await InvokeUiAsync(() => ResolveChat(args, required: false) ?? _mainViewModel.ChatVM.CurrentChat).ConfigureAwait(false);
        if (chat is null)
            return new { chat = (object?)null, messages = Array.Empty<object>(), totalMatched = 0 };

        var messages = await LoadMessagesForChatAsync(chat).ConfigureAwait(false);
        var filteredMessages = ApplyMessageFilters(messages, args).ToList();
        var page = PageMessages(filteredMessages, args);
        var shape = MessageShape.From(args);

        return new
        {
            chat = ChatDetails(chat),
            totalMessages = messages.Count,
            totalMatched = filteredMessages.Count,
            returned = page.Count,
            filters = DescribeMessageFilters(args),
            messages = page
                .Select(message => MessageDetails(message, shape))
                .ToList()
        };
    }

    private async Task<object> ReadActivityAsync(JsonElement? arguments)
    {
        var args = arguments ?? default;
        var shape = MessageShape.From(args);
        var sections = GetSections(args);
        var chat = await InvokeUiAsync(() => ResolveChat(args, required: false) ?? _mainViewModel.ChatVM.CurrentChat).ConfigureAwait(false);
        if (chat is null)
        {
            var empty = new Dictionary<string, object?>
            {
                ["summary"] = new { messageCount = 0, returnedMessageCount = 0, toolCallCount = 0 }
            };
            if (sections.Contains("status"))
                empty["status"] = await InvokeUiAsync(BuildStatus).ConfigureAwait(false);
            if (sections.Contains("chat"))
                empty["chat"] = null;
            return empty;
        }

        var messages = await LoadMessagesForChatAsync(chat).ConfigureAwait(false);
        var filteredMessages = ApplyMessageFilters(messages, args).ToList();
        var selectedMessages = PageMessages(filteredMessages, args);
        var toolCalls = selectedMessages
            .Where(message => message.Role == "tool")
            .Select(message => ToolActivity(message, shape))
            .ToList();
        var sources = selectedMessages
            .Where(message => message.Sources.Count > 0)
            .SelectMany(message => message.Sources.Select(source => new
            {
                messageId = message.Id,
                message.Role,
                source.Title,
                source.Url,
                source.Snippet
            }))
            .ToList();
        var files = selectedMessages
            .SelectMany(message => message.Attachments.Select(path => new
            {
                messageId = message.Id,
                kind = "attachment",
                path
            }))
            .Cast<object>()
            .Concat(selectedMessages
                .Where(message => string.Equals(message.ToolName, "announce_file", StringComparison.OrdinalIgnoreCase))
                .Select(message => new
                {
                    messageId = message.Id,
                    kind = "announced_file",
                    path = ExtractJsonString(message.Content, "filePath") ?? message.ToolOutput ?? ""
                }))
            .ToList();
        var questions = selectedMessages
            .Where(message => !string.IsNullOrWhiteSpace(message.QuestionId)
                              || string.Equals(message.ToolName, "ask_question", StringComparison.OrdinalIgnoreCase))
            .Select(message => new
            {
                messageId = message.Id,
                message.QuestionId,
                question = message.QuestionText ?? ExtractJsonString(message.Content, "question") ?? "",
                options = message.QuestionOptions,
                message.ToolStatus,
                answer = message.ToolOutput
            })
            .ToList();
        var errors = selectedMessages
            .Where(message => message.Role == "error"
                              || string.Equals(message.ToolStatus, "Failed", StringComparison.OrdinalIgnoreCase))
            .Select(message => new
            {
                messageId = message.Id,
                message.Role,
                message.ToolName,
                message.ToolStatus,
                content = shape.IncludeContent ? Preview(message.Content, shape.MaxContentChars) : null,
                output = shape.IncludeToolOutput ? Preview(message.ToolOutput, shape.MaxToolOutputChars) : null
            })
            .ToList();

        var result = new Dictionary<string, object?>();
        if (sections.Contains("status"))
            result["status"] = await InvokeUiAsync(BuildStatus).ConfigureAwait(false);
        if (sections.Contains("chat"))
            result["chat"] = ChatDetails(chat);
        if (sections.Contains("summary"))
            result["summary"] = new
            {
                messageCount = messages.Count,
                matchedMessageCount = filteredMessages.Count,
                returnedMessageCount = selectedMessages.Count,
                userMessageCount = filteredMessages.Count(message => message.Role == "user"),
                assistantMessageCount = filteredMessages.Count(message => message.Role == "assistant"),
                reasoningMessageCount = filteredMessages.Count(message => message.Role == "reasoning"),
                toolCallCount = filteredMessages.Count(message => message.Role == "tool"),
                failedToolCallCount = filteredMessages.Count(message => message.Role == "tool"
                    && string.Equals(message.ToolStatus, "Failed", StringComparison.OrdinalIgnoreCase)),
                sourceCount = filteredMessages.Sum(message => message.Sources.Count),
                fileCount = files.Count,
                questionCount = questions.Count,
                errorCount = errors.Count,
                earliestMessageAt = filteredMessages.FirstOrDefault()?.Timestamp,
                latestMessageAt = filteredMessages.LastOrDefault()?.Timestamp,
                filters = DescribeMessageFilters(args)
            };
        if (sections.Contains("messages"))
            result["messages"] = selectedMessages.Select(message => MessageDetails(message, shape)).ToList();
        if (sections.Contains("toolcalls"))
            result["toolCalls"] = toolCalls;
        if (sections.Contains("sources"))
            result["sources"] = sources;
        if (sections.Contains("files"))
            result["files"] = files;
        if (sections.Contains("questions"))
            result["questions"] = questions;
        if (sections.Contains("errors"))
            result["errors"] = errors;

        return result;
    }

    private static IEnumerable<ChatMessage> ApplyMessageFilters(IReadOnlyList<ChatMessage> messages, JsonElement args)
    {
        var filtered = messages.AsEnumerable();
        var afterMessageId = GetGuid(args, "afterMessageId") ?? GetGuid(args, "sinceMessageId");
        if (afterMessageId is { } afterId)
        {
            var index = messages.ToList().FindIndex(message => message.Id == afterId);
            if (index >= 0)
                filtered = filtered.Skip(index + 1);
        }

        var beforeMessageId = GetGuid(args, "beforeMessageId");
        if (beforeMessageId is { } beforeId)
        {
            var idSet = filtered.TakeWhile(message => message.Id != beforeId).Select(message => message.Id).ToHashSet();
            filtered = filtered.Where(message => idSet.Contains(message.Id));
        }

        var roles = GetStringSet(args, "roles");
        if (roles.Count > 0)
            filtered = filtered.Where(message => roles.Contains(message.Role));

        var toolNames = GetStringSet(args, "toolNames");
        if (toolNames.Count > 0)
            filtered = filtered.Where(message => message.ToolName is not null && toolNames.Contains(message.ToolName));

        var toolStatuses = GetStringSet(args, "toolStatuses");
        if (toolStatuses.Count > 0)
            filtered = filtered.Where(message => message.ToolStatus is not null && toolStatuses.Contains(message.ToolStatus));

        var afterTimestamp = GetDateTimeOffset(args, "afterTimestamp") ?? GetDateTimeOffset(args, "sinceTimestamp");
        if (afterTimestamp.HasValue)
            filtered = filtered.Where(message => message.Timestamp > afterTimestamp.Value);

        var beforeTimestamp = GetDateTimeOffset(args, "beforeTimestamp") ?? GetDateTimeOffset(args, "untilTimestamp");
        if (beforeTimestamp.HasValue)
            filtered = filtered.Where(message => message.Timestamp < beforeTimestamp.Value);

        var includeStreaming = GetBool(args, "includeStreaming") ?? true;
        if (!includeStreaming)
            filtered = filtered.Where(message => !message.IsStreaming);

        var onlyWithSources = GetBool(args, "onlyWithSources") ?? false;
        if (onlyWithSources)
            filtered = filtered.Where(message => message.Sources.Count > 0);

        var onlyWithFiles = GetBool(args, "onlyWithFiles") ?? false;
        if (onlyWithFiles)
            filtered = filtered.Where(message => message.Attachments.Count > 0
                                                || string.Equals(message.ToolName, "announce_file", StringComparison.OrdinalIgnoreCase));

        var query = GetString(args, "query") ?? GetString(args, "text");
        if (!string.IsNullOrWhiteSpace(query))
            filtered = filtered.Where(message => MessageContains(message, query));

        return filtered;
    }

    private static List<ChatMessage> PageMessages(IEnumerable<ChatMessage> messages, JsonElement args)
    {
        var sortDescending = !string.Equals(GetString(args, "sortDirection"), "asc", StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(GetString(args, "order"), "asc", StringComparison.OrdinalIgnoreCase);
        var ordered = sortDescending
            ? messages.OrderByDescending(message => message.Timestamp)
            : messages.OrderBy(message => message.Timestamp);
        var offset = Math.Max(0, GetInt(args, "offset") ?? 0);
        var maxMessages = Math.Clamp(GetInt(args, "maxMessages") ?? GetInt(args, "limit") ?? 200, 1, 1000);
        var page = ordered.Skip(offset).Take(maxMessages).ToList();

        return sortDescending && (GetBool(args, "returnChronological") ?? true)
            ? page.OrderBy(message => message.Timestamp).ToList()
            : page;
    }

    private static HashSet<string> GetSections(JsonElement args)
    {
        var sections = GetStringSet(args, "sections");
        if (sections.Count == 0 || sections.Contains("all"))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "status", "chat", "summary", "messages", "toolcalls", "sources", "files", "questions", "errors"
            };
        }

        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in sections)
            normalized.Add(section.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal));
        return normalized;
    }

    private static object DescribeMessageFilters(JsonElement args)
        => new
        {
            roles = GetStringSet(args, "roles"),
            toolNames = GetStringSet(args, "toolNames"),
            toolStatuses = GetStringSet(args, "toolStatuses"),
            query = GetString(args, "query") ?? GetString(args, "text"),
            afterTimestamp = GetString(args, "afterTimestamp") ?? GetString(args, "sinceTimestamp"),
            beforeTimestamp = GetString(args, "beforeTimestamp") ?? GetString(args, "untilTimestamp"),
            afterMessageId = GetString(args, "afterMessageId") ?? GetString(args, "sinceMessageId"),
            beforeMessageId = GetString(args, "beforeMessageId"),
            offset = GetInt(args, "offset") ?? 0,
            limit = GetInt(args, "maxMessages") ?? GetInt(args, "limit") ?? 200,
            sortDirection = GetString(args, "sortDirection") ?? GetString(args, "order") ?? "desc",
            returnChronological = GetBool(args, "returnChronological") ?? true
        };

    private static bool MessageContains(ChatMessage message, string query)
    {
        return Contains(message.Role, query)
               || Contains(message.Content, query)
               || Contains(message.Author, query)
               || Contains(message.ToolName, query)
               || Contains(message.ToolStatus, query)
               || Contains(message.ToolOutput, query)
               || message.Attachments.Any(path => Contains(path, query))
               || message.Sources.Any(source =>
                   Contains(source.Title, query)
                   || Contains(source.Url, query)
                   || Contains(source.Snippet, query));
    }

    private Task<List<ChatMessage>> LoadMessagesForChatAsync(Chat chat)
    {
        return InvokeUiAsync(async () =>
        {
            if (_mainViewModel.ChatVM.CurrentChat?.Id == chat.Id)
                return _mainViewModel.ChatVM.Messages.Select(vm => vm.Message).ToList();

            await _dataStore.LoadChatMessagesAsync(chat).ConfigureAwait(true);
            return chat.Messages.ToList();
        });
    }

    private object LoadFixture()
    {
        _mainViewModel.ChatVM.LoadDebugTranscriptFixture();
        _mainViewModel.SelectedNavIndex = 0;
        return new
        {
            loaded = true,
            chat = _mainViewModel.ChatVM.CurrentChat is null ? null : ChatDetails(_mainViewModel.ChatVM.CurrentChat),
            messageCount = _mainViewModel.ChatVM.Messages.Count,
            transcriptTurnCount = _mainViewModel.ChatVM.TranscriptTurns.Count,
            mountedTranscriptTurnCount = _mainViewModel.ChatVM.MountedTranscriptTurns.Count,
            transcriptItemCount = _mainViewModel.ChatVM.TranscriptTurns.Count
        };
    }

    private object LoadBackgroundShellFixture()
    {
        _mainViewModel.ChatVM.LoadDebugBackgroundShellFixture();
        _mainViewModel.SelectedNavIndex = 0;
        return new
        {
            loaded = true,
            variant = "background-shell",
            chat = _mainViewModel.ChatVM.CurrentChat is null ? null : ChatDetails(_mainViewModel.ChatVM.CurrentChat),
            messageCount = _mainViewModel.ChatVM.Messages.Count,
            statusText = _mainViewModel.ChatVM.StatusText,
            isBusy = _mainViewModel.ChatVM.IsBusy,
        };
    }

    private object ListFeatures(JsonElement? arguments)
    {
        var args = arguments ?? default;
        var resource = GetString(args, "resource")?.Trim().ToLowerInvariant();
        var query = GetString(args, "query");
        var limit = Math.Clamp(GetInt(args, "limit") ?? 100, 1, 1000);
        var offset = Math.Max(0, GetInt(args, "offset") ?? 0);
        return resource switch
        {
            "projects" or "project" => new { projects = Page(_dataStore.Data.Projects.Where(project => FeatureMatches(project.Name, query) || FeatureMatches(project.Instructions, query) || FeatureMatches(project.WorkingDirectory, query)), offset, limit).Select(ProjectSummary).ToList() },
            "skills" or "skill" => new { skills = Page(_dataStore.Data.Skills.Where(skill => FeatureMatches(skill.Name, query) || FeatureMatches(skill.Description, query) || FeatureMatches(skill.Content, query)), offset, limit).Select(SkillSummary).ToList() },
            "lumis" or "agents" or "agent" or "lumi" => new { lumis = Page(_dataStore.Data.Agents.Where(agent => FeatureMatches(agent.Name, query) || FeatureMatches(agent.Description, query) || FeatureMatches(agent.SystemPrompt, query)), offset, limit).Select(AgentSummary).ToList() },
            "mcps" or "mcp" or "mcp_servers" or "mcpservers" => new { mcpServers = Page(_dataStore.Data.McpServers.Where(server => FeatureMatches(server.Name, query) || FeatureMatches(server.Description, query) || FeatureMatches(server.Command, query) || FeatureMatches(server.Url, query)), offset, limit).Select(McpSummary).ToList() },
            "jobs" or "background_jobs" => new { jobs = Page(_dataStore.SnapshotBackgroundJobs().Where(job => FeatureMatches(job.Name, query) || FeatureMatches(job.Description, query) || FeatureMatches(job.Prompt, query)), offset, limit).Select(JobSummary).ToList() },
            "memories" or "memory" => new { memories = Page(_dataStore.Data.Memories.Where(memory => FeatureMatches(memory.Key, query) || FeatureMatches(memory.Category, query) || FeatureMatches(memory.Content, query)), offset, limit).Select(MemorySummary).ToList() },
            "settings" or "setting" => new { settings = SettingsSummary() },
            null or "" => new
            {
                projects = Page(_dataStore.Data.Projects.Where(project => FeatureMatches(project.Name, query) || FeatureMatches(project.Instructions, query) || FeatureMatches(project.WorkingDirectory, query)), offset, limit).Select(ProjectSummary).ToList(),
                skills = Page(_dataStore.Data.Skills.Where(skill => FeatureMatches(skill.Name, query) || FeatureMatches(skill.Description, query) || FeatureMatches(skill.Content, query)), offset, limit).Select(SkillSummary).ToList(),
                lumis = Page(_dataStore.Data.Agents.Where(agent => FeatureMatches(agent.Name, query) || FeatureMatches(agent.Description, query) || FeatureMatches(agent.SystemPrompt, query)), offset, limit).Select(AgentSummary).ToList(),
                mcpServers = Page(_dataStore.Data.McpServers.Where(server => FeatureMatches(server.Name, query) || FeatureMatches(server.Description, query) || FeatureMatches(server.Command, query) || FeatureMatches(server.Url, query)), offset, limit).Select(McpSummary).ToList(),
                jobs = Page(_dataStore.SnapshotBackgroundJobs().Where(job => FeatureMatches(job.Name, query) || FeatureMatches(job.Description, query) || FeatureMatches(job.Prompt, query)), offset, limit).Select(JobSummary).ToList(),
                memories = Page(_dataStore.Data.Memories.Where(memory => FeatureMatches(memory.Key, query) || FeatureMatches(memory.Category, query) || FeatureMatches(memory.Content, query)), offset, limit).Select(MemorySummary).ToList(),
                settings = SettingsSummary()
            },
            _ => throw new InvalidOperationException($"Unknown feature resource '{resource}'.")
        };
    }

    private async Task<object> ConfigureFeatureAsync(JsonElement? arguments)
    {
        var args = arguments ?? default;
        var resource = RequireString(args, "resource").Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        var action = GetString(args, "action") ?? "list";
        var dryRun = GetBool(args, "dryRun") ?? false;
        if (dryRun && !string.Equals(action, "list", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                dryRun = true,
                dataChanged = false,
                message = "Dry run only. No Lumi data was changed.",
                resource,
                action,
                providedArguments = TryParseJson(args.GetRawText())
            };
        }

        if (resource is "settings" or "setting")
            return await UpdateSettingsAsync(args).ConfigureAwait(true);

        var manager = new LumiFeatureManager(_dataStore);
        var result = resource switch
        {
            "projects" or "project" => manager.ManageProjects(
                action,
                GetString(args, "identifier"),
                GetString(args, "name"),
                GetString(args, "instructions"),
                GetString(args, "workingDirectory"),
                GetBool(args, "clearWorkingDirectory"),
                GetStringArrayOrNull(args, "additionalContextDirectories"),
                GetBool(args, "clearAdditionalContextDirectories"),
                GetString(args, "query")),
            "skills" or "skill" => manager.ManageSkills(
                action,
                GetString(args, "identifier"),
                GetString(args, "name"),
                GetString(args, "description"),
                GetString(args, "content"),
                GetString(args, "iconGlyph"),
                GetString(args, "query"),
                GetString(args, "updateMode"),
                GetString(args, "editOldString"),
                GetString(args, "editNewString")),
            "lumis" or "agents" or "agent" or "lumi" => manager.ManageLumis(
                action,
                GetString(args, "identifier"),
                GetString(args, "name"),
                GetString(args, "description"),
                GetString(args, "systemPrompt"),
                GetString(args, "iconGlyph"),
                GetStringArrayOrNull(args, "skillIdentifiers"),
                GetStringArrayOrNull(args, "toolNames"),
                GetStringArrayOrNull(args, "mcpServerIdentifiers"),
                GetString(args, "query")),
            "mcps" or "mcp" or "mcp_servers" or "mcpservers" => manager.ManageMcps(
                action,
                GetString(args, "identifier"),
                GetString(args, "name"),
                GetString(args, "description"),
                GetString(args, "serverType"),
                GetString(args, "command"),
                GetStringArrayOrNull(args, "args"),
                GetString(args, "url"),
                GetStringArrayOrNull(args, "envEntries"),
                GetStringArrayOrNull(args, "headerEntries"),
                GetStringArrayOrNull(args, "toolNames"),
                GetInt(args, "timeout"),
                GetBool(args, "clearTimeout"),
                GetBool(args, "isEnabled"),
                GetString(args, "query")),
            "memories" or "memory" => manager.ManageMemories(
                action,
                GetString(args, "identifier"),
                GetString(args, "key"),
                GetString(args, "content"),
                GetString(args, "category"),
                GetString(args, "query")),
            "jobs" or "background_jobs" => manager.ManageJobs(
                action,
                GetString(args, "identifier"),
                GetString(args, "name"),
                GetString(args, "description"),
                GetString(args, "prompt"),
                GetString(args, "chatIdentifier"),
                GetString(args, "triggerType"),
                GetString(args, "scheduleType"),
                GetInt(args, "intervalMinutes"),
                GetString(args, "dailyTime"),
                GetString(args, "daysOfWeek"),
                GetInt(args, "monthlyDay"),
                GetString(args, "cronExpression"),
                GetString(args, "runAt"),
                GetString(args, "scriptContent"),
                GetString(args, "scriptLanguage"),
                GetBool(args, "isTemporary"),
                GetBool(args, "isEnabled"),
                GetBool(args, "runNow"),
                GetString(args, "query"),
                _mainViewModel.ChatVM.CurrentChat?.Id),
            _ => throw new InvalidOperationException($"Unknown feature resource '{resource}'.")
        };

        if (result.DataChanged)
            await ApplyFeatureChangeAsync(result).ConfigureAwait(true);

        return new
        {
            result.Message,
            result.DataChanged,
            result.SyncSkillFiles,
            result.RenamedMcpOldName,
            result.RenamedMcpNewName,
            result.DeletedMcpName,
            result.SkillContentBytes,
            result.SkillContentHash
        };
    }

    private async Task<object> UpdateSettingsAsync(JsonElement args)
    {
        var changed = new List<string>();
        var settings = _dataStore.Data.Settings;
        var values = GetObject(args, "values") ?? args;

        SetString("userName", value =>
        {
            settings.UserName = value;
            _mainViewModel.UserName = value;
            _mainViewModel.SettingsVM.UserName = value;
        });
        SetString("language", value =>
        {
            settings.Language = value;
            _mainViewModel.SettingsVM.SelectedLanguage = _mainViewModel.SettingsVM.LanguageOptions
                .FirstOrDefault(option => option.EndsWith($"({value})", StringComparison.OrdinalIgnoreCase))
                ?? _mainViewModel.SettingsVM.SelectedLanguage;
        });
        SetBool("isDarkTheme", value =>
        {
            settings.IsDarkTheme = value;
            _mainViewModel.IsDarkTheme = value;
            _mainViewModel.SettingsVM.IsDarkTheme = value;
        });
        SetBool("isCompactDensity", value =>
        {
            settings.IsCompactDensity = value;
            _mainViewModel.IsCompactDensity = value;
            _mainViewModel.SettingsVM.IsCompactDensity = value;
        });
        SetBool("sendWithEnter", value =>
        {
            settings.SendWithEnter = value;
            _mainViewModel.ChatVM.SendWithEnter = value;
            _mainViewModel.SettingsVM.SendWithEnter = value;
        });
        SetBool("showTimestamps", value => settings.ShowTimestamps = _mainViewModel.SettingsVM.ShowTimestamps = value);
        SetBool("showToolCalls", value => settings.ShowToolCalls = _mainViewModel.SettingsVM.ShowToolCalls = value);
        SetBool("showReasoning", value => settings.ShowReasoning = _mainViewModel.SettingsVM.ShowReasoning = value);
        SetBool("expandReasoningWhileStreaming", value => settings.ExpandReasoningWhileStreaming = _mainViewModel.SettingsVM.ExpandReasoningWhileStreaming = value);
        SetBool("autoGenerateTitles", value => settings.AutoGenerateTitles = _mainViewModel.SettingsVM.AutoGenerateTitles = value);
        SetString("preferredModel", value =>
        {
            settings.PreferredModel = value;
            _mainViewModel.SettingsVM.PreferredModel = value;
            _mainViewModel.ChatVM.SelectedModel = value;
        });
        SetString("reasoningEffort", value =>
        {
            settings.ReasoningEffort = value;
            _mainViewModel.SettingsVM.ReasoningEffort = value;
        });
        SetBool("useMcpProxy", value => settings.UseMcpProxy = _mainViewModel.SettingsVM.UseMcpProxy = value);
        SetString("contextWindowTier", value =>
        {
            settings.ContextWindowTier = value;
            _mainViewModel.SettingsVM.ContextWindowTier = value;
        });
        SetBool("enableMemoryAutoSave", value => settings.EnableMemoryAutoSave = _mainViewModel.SettingsVM.EnableMemoryAutoSave = value);
        SetBool("enableMemoryAutoMaintenance", value => settings.EnableMemoryAutoMaintenance = _mainViewModel.SettingsVM.EnableMemoryAutoMaintenance = value);
        SetBool("autoSaveChats", value => settings.AutoSaveChats = _mainViewModel.SettingsVM.AutoSaveChats = value);

        if (changed.Count > 0)
        {
            await _dataStore.SaveAsync().ConfigureAwait(true);
            _mainViewModel.ChatVM.RebuildTranscript();
        }

        return new
        {
            dataChanged = changed.Count > 0,
            changed,
            settings = SettingsSummary()
        };

        void SetString(string key, Action<string> apply)
        {
            var value = GetString(values, key);
            if (value is null)
                return;

            apply(value);
            changed.Add(key);
        }

        void SetBool(string key, Action<bool> apply)
        {
            var value = GetBool(values, key);
            if (!value.HasValue)
                return;

            apply(value.Value);
            changed.Add(key);
        }
    }

    private async Task ApplyFeatureChangeAsync(FeatureChangeResult result)
    {
        if (result.SyncSkillFiles)
            _dataStore.SyncSkillFiles();

        if (result.RenamedMcpOldName is { } oldName && result.RenamedMcpNewName is { } newName)
        {
            foreach (var chat in _dataStore.Data.Chats.Where(chat => chat.ActiveMcpServerNames.Contains(oldName)).ToList())
            {
                for (var i = 0; i < chat.ActiveMcpServerNames.Count; i++)
                {
                    if (string.Equals(chat.ActiveMcpServerNames[i], oldName, StringComparison.Ordinal))
                        chat.ActiveMcpServerNames[i] = newName;
                }
                _dataStore.MarkChatChanged(chat);
            }

            if (_mainViewModel.ChatVM.ActiveMcpServerNames.Contains(oldName))
            {
                _mainViewModel.ChatVM.RemoveMcpByName(oldName);
                _mainViewModel.ChatVM.RegisterMcpByName(newName);
            }
        }

        if (result.DeletedMcpName is { } deletedName)
        {
            foreach (var chat in _dataStore.Data.Chats.Where(chat => chat.ActiveMcpServerNames.Contains(deletedName)).ToList())
            {
                chat.ActiveMcpServerNames.RemoveAll(name => string.Equals(name, deletedName, StringComparison.Ordinal));
                _dataStore.MarkChatChanged(chat);
            }

            _mainViewModel.ChatVM.RemoveMcpByName(deletedName);
        }

        await _dataStore.SaveAsync().ConfigureAwait(true);
        _mainViewModel.RefreshProjects();
        _mainViewModel.RefreshChatList();
        _mainViewModel.ProjectsVM.RefreshFromStore();
        _mainViewModel.SkillsVM.RefreshFromStore();
        _mainViewModel.AgentsVM.RefreshFromStore();
        _mainViewModel.McpServersVM.RefreshFromStore();
        _mainViewModel.MemoriesVM.RefreshFromStore();
        _mainViewModel.JobsVM.RefreshFromStore();
        _mainViewModel.ChatVM.RefreshComposerCatalogs(syncProjectContextMcpSelections: false);
        _mainViewModel.ChatVM.RaiseFeatureManagementStateChangedForTest();
    }

    private object MoveChat(JsonElement? arguments)
    {
        var chat = ResolveChat(arguments, required: true)!;
        var args = arguments ?? default;
        var oldProjectId = chat.ProjectId;

        // A target project resolves to an assign; no project (or removeFromProject:true) resolves to
        // "All projects" (remove). Mirrors exactly what the sidebar context menu invokes.
        var removeRequested = GetBool(args, "removeFromProject") ?? false;
        var project = removeRequested ? null : ResolveProject(args, required: false);

        if (project is null)
            _mainViewModel.RemoveChatFromProjectCommand.Execute(chat);
        else
            _mainViewModel.AssignChatToProjectCommand.Execute(new object[] { chat, project });

        return new
        {
            moved = true,
            chatId = chat.Id,
            oldProjectId,
            newProjectId = chat.ProjectId,
            newProjectName = GetProjectName(chat.ProjectId),
            // Null after an idle move means the session was invalidated and the next send rebuilds
            // it with the new project's working directory / system prompt.
            copilotSessionId = chat.CopilotSessionId,
            isCurrentChat = _mainViewModel.ChatVM.CurrentChat?.Id == chat.Id
        };
    }

    private Chat? ResolveChat(JsonElement? arguments, bool required, bool allowCurrent = true)
    {
        var args = arguments ?? default;
        var id = GetString(args, "chatId") ?? GetString(args, "id");
        if (Guid.TryParse(id, out var chatId))
        {
            var byId = _dataStore.Data.Chats.FirstOrDefault(chat => chat.Id == chatId);
            if (byId is not null)
                return byId;

            if (_mainViewModel.ChatVM.CurrentChat?.Id == chatId)
                return _mainViewModel.ChatVM.CurrentChat;
        }

        var title = GetString(args, "title") ?? GetString(args, "chatTitle");
        if (!string.IsNullOrWhiteSpace(title))
        {
            var exactTitle = GetBool(args, "exactTitle") ?? false;
            var exact = _dataStore.Data.Chats
                .OrderByDescending(chat => chat.UpdatedAt)
                .FirstOrDefault(chat => string.Equals(chat.Title, title, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact;

            if (exactTitle)
                return required
                    ? throw new InvalidOperationException($"Chat with exact title '{title}' was not found.")
                    : null;

            var contains = _dataStore.Data.Chats
                .OrderByDescending(chat => chat.UpdatedAt)
                .FirstOrDefault(chat => chat.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
            if (contains is not null)
                return contains;
        }

        if (allowCurrent && _mainViewModel.ChatVM.CurrentChat is not null)
            return _mainViewModel.ChatVM.CurrentChat;

        if (required)
            throw new InvalidOperationException("Chat was not found. Provide chatId or title.");

        return null;
    }

    private Project? ResolveProject(JsonElement args, bool required)
    {
        var id = GetString(args, "projectId");
        if (Guid.TryParse(id, out var projectId))
        {
            var byId = _dataStore.Data.Projects.FirstOrDefault(project => project.Id == projectId);
            if (byId is not null)
                return byId;
        }

        var name = GetString(args, "projectName") ?? GetString(args, "project");
        if (!string.IsNullOrWhiteSpace(name))
        {
            var byName = _dataStore.Data.Projects.FirstOrDefault(project =>
                string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
                return byName;
        }

        if (required)
            throw new InvalidOperationException("Project was not found.");

        return null;
    }

    private LumiAgent? ResolveAgent(JsonElement args, bool required)
    {
        var id = GetString(args, "agentId") ?? GetString(args, "lumiId");
        if (Guid.TryParse(id, out var agentId))
        {
            var byId = _dataStore.Data.Agents.FirstOrDefault(agent => agent.Id == agentId);
            if (byId is not null)
                return byId;
        }

        var name = GetString(args, "agentName") ?? GetString(args, "lumiName") ?? GetString(args, "agent");
        if (!string.IsNullOrWhiteSpace(name))
        {
            var byName = _dataStore.Data.Agents.FirstOrDefault(agent =>
                string.Equals(agent.Name, name, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
                return byName;
        }

        if (required)
            throw new InvalidOperationException("Lumi agent was not found.");

        return null;
    }

    private List<Skill> ResolveSkills(JsonElement args)
    {
        var identifiers = GetStringArray(args, "skillIdentifiers");
        identifiers.AddRange(GetStringArray(args, "skillNames"));
        var result = new List<Skill>();
        foreach (var identifier in identifiers.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Skill? skill = null;
            if (Guid.TryParse(identifier, out var skillId))
                skill = _dataStore.Data.Skills.FirstOrDefault(candidate => candidate.Id == skillId);

            skill ??= _dataStore.Data.Skills.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, identifier, StringComparison.OrdinalIgnoreCase));
            if (skill is not null)
                result.Add(skill);
        }

        return result;
    }

    private static IEnumerable<Chat> SortChats(IEnumerable<Chat> chats, JsonElement args)
    {
        var sortBy = (GetString(args, "sortBy") ?? "updatedAt").Trim().ToLowerInvariant();
        var descending = !string.Equals(GetString(args, "sortDirection"), "asc", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(GetString(args, "order"), "asc", StringComparison.OrdinalIgnoreCase);
        return (sortBy switch
        {
            "title" or "name" => descending
                ? chats.OrderByDescending(chat => chat.Title, StringComparer.OrdinalIgnoreCase)
                : chats.OrderBy(chat => chat.Title, StringComparer.OrdinalIgnoreCase),
            "created" or "createdat" => descending
                ? chats.OrderByDescending(chat => chat.CreatedAt)
                : chats.OrderBy(chat => chat.CreatedAt),
            "messages" or "messagecount" => descending
                ? chats.OrderByDescending(chat => chat.Messages.Count)
                : chats.OrderBy(chat => chat.Messages.Count),
            _ => descending
                ? chats.OrderByDescending(chat => chat.UpdatedAt)
                : chats.OrderBy(chat => chat.UpdatedAt)
        }).ThenByDescending(chat => chat.UpdatedAt);
    }

    private string? GetProjectName(Guid? projectId)
        => projectId.HasValue
            ? _dataStore.Data.Projects.FirstOrDefault(project => project.Id == projectId.Value)?.Name
            : null;

    private string? GetAgentName(Guid? agentId)
        => agentId.HasValue
            ? _dataStore.Data.Agents.FirstOrDefault(agent => agent.Id == agentId.Value)?.Name
            : null;

    private static IEnumerable<T> Page<T>(IEnumerable<T> items, int offset, int limit)
        => items.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 1000));

    private static bool FeatureMatches(string? value, string? query)
        => string.IsNullOrWhiteSpace(query) || Contains(value, query);

    private void WriteStatusFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatusFilePath)!);
        Directory.CreateDirectory(StatusDirectory);
        var appDataDir = Path.GetDirectoryName(DataStore.ChatsDir)!;
        var status = new
        {
            instanceId = _instanceId,
            processId = Environment.ProcessId,
            url = BaseUrl,
            token = _token,
            startedAt = DateTimeOffset.Now,
            appDataDir,
            appDataRoot = Directory.GetParent(appDataDir)?.FullName,
            protocolVersion = 1
        };
        var json = JsonSerializer.Serialize(status, JsonOptions);
        WriteAllTextAtomic(InstanceStatusFilePath, json);
        WriteAllTextAtomic(StatusFilePath, json);
    }

    private static void WriteAllTextAtomic(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, contents, Encoding.UTF8);
        File.Move(tempPath, path, overwrite: true);
    }

    private void DeleteOwnStatusFile()
    {
        if (!File.Exists(StatusFilePath))
            return;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(StatusFilePath));
            if (!document.RootElement.TryGetProperty("instanceId", out var id)
                || !string.Equals(id.GetString(), _instanceId, StringComparison.Ordinal))
                return;

            File.Delete(StatusFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning("Failed to remove Lumi debug bridge status file: {0}", ex.Message);
        }

        try
        {
            if (File.Exists(InstanceStatusFilePath))
                File.Delete(InstanceStatusFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Failed to remove Lumi debug bridge instance status file: {0}", ex.Message);
        }
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var header = request.Headers["X-Lumi-Debug-Token"];
        var query = request.QueryString["token"];
        return FixedTimeEquals(header, _token) || FixedTimeEquals(query, _token);
    }

    private static bool FixedTimeEquals(string? left, string right)
    {
        if (string.IsNullOrEmpty(left))
            return false;

        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static async Task WriteJsonAsync(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        object payload,
        CancellationToken cancellationToken)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(response.OutputStream, payload, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        response.OutputStream.Close();
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<T> InvokeUiAsync<T>(Func<T> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return action();

        return await Dispatcher.UIThread.InvokeAsync(action);
    }

    private static async Task<T> InvokeUiAsync<T>(Func<Task<T>> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return await action().ConfigureAwait(true);

        return await Dispatcher.UIThread.InvokeAsync(action);
    }

    private static int ResolvePageIndex(string page)
    {
        return page.Trim().Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "chat" or "chats" => 0,
            "jobs" or "backgroundjobs" => 1,
            "projects" or "project" => 2,
            "skills" or "skill" => 3,
            "agents" or "lumis" or "agent" or "lumi" => 4,
            "memories" or "memory" => 5,
            "mcp" or "mcps" or "mcpservers" => 6,
            "settings" or "setting" => 7,
            _ => throw new InvalidOperationException($"Unknown page '{page}'.")
        };
    }

    private static string GetPageName(int index)
    {
        return index switch
        {
            0 => "Chat",
            1 => "Jobs",
            2 => "Projects",
            3 => "Skills",
            4 => "Lumis",
            5 => "Memories",
            6 => "MCP Servers",
            7 => "Settings",
            _ => "Unknown"
        };
    }

    private static int ResolveSettingsPageIndex(string page)
    {
        return page.Trim().Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "profile" => 0,
            "general" => 1,
            "appearance" => 2,
            "chat" => 3,
            "aimodels" or "models" or "ai" => 4,
            "privacy" or "privacydata" or "data" => 5,
            "about" or "updates" or "update" => 6,
            _ => throw new InvalidOperationException($"Unknown settings page '{page}'.")
        };
    }

    private static string RequireString(JsonElement args, string propertyName)
    {
        return GetString(args, propertyName) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{propertyName} is required.");
    }

    private static string? GetString(JsonElement args, string propertyName)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool Contains(string? value, string query)
        => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static Guid? GetGuid(JsonElement args, string propertyName)
    {
        var value = GetString(args, propertyName);
        return Guid.TryParse(value, out var guid) ? guid : null;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement args, string propertyName)
    {
        var value = GetString(args, propertyName);
        return DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : null;
    }

    private static bool? GetBool(JsonElement args, string propertyName)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static int? GetInt(JsonElement args, string propertyName)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
            return parsed;

        return null;
    }

    private static JsonElement? GetObject(JsonElement args, string propertyName)
    {
        return args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Object
            ? value
            : null;
    }

    private static string[]? GetStringArrayOrNull(JsonElement args, string propertyName)
    {
        var values = GetStringArray(args, propertyName);
        return values.Count == 0 && (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(propertyName, out _))
            ? null
            : values.ToArray();
    }

    private static List<string> GetStringArray(JsonElement args, string propertyName)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(propertyName, out var value))
            return [];

        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToList();
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()?
                .Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList() ?? [];
        }

        return [];
    }

    private static HashSet<string> GetStringSet(JsonElement args, string propertyName)
        => GetStringArray(args, propertyName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static object ChatSummary(Chat chat)
        => new
        {
            id = chat.Id,
            chat.Title,
            chat.ProjectId,
            chat.AgentId,
            messageCount = chat.Messages.Count,
            chat.CreatedAt,
            chat.UpdatedAt,
            chat.IsRunning,
            chat.HasUnreadMessages
        };

    private object ChatDetails(Chat chat)
        => new
        {
            id = chat.Id,
            chat.Title,
            projectId = chat.ProjectId,
            projectName = _dataStore.Data.Projects.FirstOrDefault(project => project.Id == chat.ProjectId)?.Name,
            agentId = chat.AgentId,
            agentName = _dataStore.Data.Agents.FirstOrDefault(agent => agent.Id == chat.AgentId)?.Name,
            messageCount = chat.Messages.Count,
            chat.CreatedAt,
            chat.UpdatedAt,
            chat.IsRunning,
            chat.HasUnreadMessages,
            activeSkillIds = chat.ActiveSkillIds,
            activeExternalSkillNames = chat.ActiveExternalSkillNames,
            activeMcpServerNames = chat.ActiveMcpServerNames,
            chat.HasExplicitMcpServerSelection,
            chat.LastModelUsed,
            chat.LastReasoningEffortUsed,
            chat.LastContextWindowTierUsed,
            chat.TotalInputTokens,
            chat.TotalOutputTokens
        };

    private static object MessageDetails(ChatMessage message, MessageShape shape)
        => new
        {
            id = message.Id,
            message.Role,
            author = shape.IncludeMetadata ? message.Author : null,
            content = shape.IncludeContent ? Preview(message.Content, shape.MaxContentChars) : null,
            message.Timestamp,
            toolName = shape.IncludeMetadata ? message.ToolName : null,
            toolCallId = shape.IncludeMetadata ? message.ToolCallId : null,
            parentToolCallId = shape.IncludeMetadata ? message.ParentToolCallId : null,
            toolStatus = shape.IncludeMetadata ? message.ToolStatus : null,
            toolOutput = shape.IncludeToolOutput ? Preview(message.ToolOutput, shape.MaxToolOutputChars) : null,
            isStreaming = shape.IncludeMetadata ? (bool?)message.IsStreaming : null,
            model = shape.IncludeMetadata ? message.Model : null,
            attachments = shape.IncludeFiles ? message.Attachments : null,
            sources = shape.IncludeSources ? message.Sources : null,
            activeSkills = shape.IncludeMetadata ? message.ActiveSkills : null
        };

    private static object ToolActivity(ChatMessage message, MessageShape shape)
        => new
        {
            messageId = message.Id,
            message.ToolName,
            message.ToolStatus,
            message.ToolCallId,
            message.ParentToolCallId,
            arguments = TryParseJson(message.Content),
            output = shape.IncludeToolOutput ? Preview(message.ToolOutput, shape.MaxToolOutputChars) : null,
            message.Timestamp
        };

    private sealed record MessageShape(
        bool IncludeContent,
        bool IncludeMetadata,
        bool IncludeSources,
        bool IncludeFiles,
        bool IncludeToolOutput,
        int MaxContentChars,
        int MaxToolOutputChars)
    {
        public static MessageShape From(JsonElement args)
        {
            var compact = GetBool(args, "compact") ?? false;
            return new MessageShape(
                IncludeContent: GetBool(args, "includeContent") ?? true,
                IncludeMetadata: GetBool(args, "includeMetadata") ?? !compact,
                IncludeSources: GetBool(args, "includeSources") ?? true,
                IncludeFiles: GetBool(args, "includeFiles") ?? true,
                IncludeToolOutput: GetBool(args, "includeToolOutput") ?? false,
                MaxContentChars: Math.Clamp(GetInt(args, "maxContentChars") ?? (compact ? 600 : 20000), 0, 100000),
                MaxToolOutputChars: Math.Clamp(GetInt(args, "maxToolOutputChars") ?? (compact ? 600 : 2000), 0, 50000));
        }
    }

    private static object? TryParseJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static string? ExtractJsonString(string value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(propertyName, out var property)
                   && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Preview(string? value, int maxLength)
    {
        if (value is null || maxLength <= 0)
            return null;

        var normalized = value.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..Math.Max(0, maxLength - 1)].TrimEnd() + "...";
    }

    private static object ProjectSummary(Project project)
        => new
        {
            id = project.Id,
            project.Name,
            project.WorkingDirectory,
            additionalContextDirectories = project.AdditionalContextDirectories,
            instructionsLength = project.Instructions.Length,
            project.CreatedAt,
            project.IsRunning
        };

    private static object SkillSummary(Skill skill)
        => new
        {
            id = skill.Id,
            skill.Name,
            skill.Description,
            skill.IconGlyph,
            contentLength = skill.Content.Length,
            skill.IsBuiltIn,
            skill.CreatedAt
        };

    private static object AgentSummary(LumiAgent agent)
        => new
        {
            id = agent.Id,
            agent.Name,
            agent.Description,
            agent.IconGlyph,
            systemPromptLength = agent.SystemPrompt.Length,
            skillIds = agent.SkillIds,
            toolNames = agent.ToolNames,
            mcpServerIds = agent.McpServerIds,
            agent.IsBuiltIn,
            agent.IsLearningAgent,
            agent.CreatedAt
        };

    private static object McpSummary(McpServer server)
        => new
        {
            id = server.Id,
            server.Name,
            server.Description,
            server.ServerType,
            server.Command,
            args = server.Args,
            server.Url,
            tools = server.Tools,
            server.Timeout,
            server.IsEnabled,
            server.CreatedAt
        };

    private static object JobSummary(BackgroundJob job)
        => new
        {
            id = job.Id,
            job.Name,
            job.Description,
            job.ChatId,
            job.TriggerType,
            job.ScheduleType,
            job.IsEnabled,
            job.IsTemporary,
            job.NextRunAt,
            job.LastRunStatus,
            job.LastRunAt,
            job.UpdatedAt
        };

    private static object MemorySummary(Memory memory)
        => new
        {
            id = memory.Id,
            memory.Key,
            memory.Category,
            memory.Scope,
            memory.ProjectId,
            contentLength = memory.Content.Length,
            memory.Status,
            memory.UpdatedAt,
            memory.Confidence
        };

    private object SettingsSummary()
        => new
        {
            _dataStore.Data.Settings.UserName,
            _dataStore.Data.Settings.UserSex,
            _dataStore.Data.Settings.IsOnboarded,
            _dataStore.Data.Settings.Language,
            _dataStore.Data.Settings.IsDarkTheme,
            _dataStore.Data.Settings.IsCompactDensity,
            _dataStore.Data.Settings.SendWithEnter,
            _dataStore.Data.Settings.ShowTimestamps,
            _dataStore.Data.Settings.ShowToolCalls,
            _dataStore.Data.Settings.ShowReasoning,
            _dataStore.Data.Settings.ExpandReasoningWhileStreaming,
            _dataStore.Data.Settings.AutoGenerateTitles,
            _dataStore.Data.Settings.PreferredModel,
            _dataStore.Data.Settings.ReasoningEffort,
            _dataStore.Data.Settings.ContextWindowTier,
            _dataStore.Data.Settings.EnableMemoryAutoSave,
            _dataStore.Data.Settings.EnableMemoryAutoMaintenance,
            _dataStore.Data.Settings.AutoSaveChats
        };

    private sealed class BridgeRequest
    {
        public string? Action { get; set; }
        public JsonElement? Arguments { get; set; }
    }

    private sealed record SendObservation(
        int MessageCount,
        int AssistantMessageCount,
        int ErrorMessageCount,
        string? LastAssistantContent,
        string? LastErrorContent);
}
#pragma warning restore IL2026
#endif
