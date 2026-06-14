using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using StrataTheme.Controls;

using ChatMessage = Lumi.Models.ChatMessage;

namespace Lumi.ViewModels;

public partial class ChatViewModel : ObservableObject, IDisposable
{
    private const int SuggestionHistoryScanLimit = 1000;
    private const int SuggestionHistorySummaryMaxItems = 24;
    private const int SuggestionHistoryDisplayMaxLength = 160;
    private static readonly HttpClient McpDiagnosticsHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };
    private static readonly Regex SensitiveHttpDiagnosticPattern = new(
        @"(?i)(authorization|token|api[_-]?key|secret|password)(\s*[=:]\s*)([^\s,;]+)",
        RegexOptions.Compiled);

    private static readonly bool TranscriptDiagnosticsEnabled = Debugger.IsAttached
        || string.Equals(Environment.GetEnvironmentVariable("LUMI_TRANSCRIPT_DEBUG"), "1", StringComparison.Ordinal);

    /// <summary>
    /// Tracks <c>sessionId|serverName</c> pairs we've already started an interactive OAuth login for,
    /// so the startup status poll and repeated live status events don't relaunch the browser for the
    /// same server. Removed when the server connects, or when an attempt is cancelled by navigate-away,
    /// so a later status change can retry; kept on a genuine sign-in failure to avoid hammering a
    /// failing endpoint. Purged on session teardown so dead sessions don't accumulate entries.
    /// </summary>
    private readonly HashSet<string> _mcpOAuthLoginAttempts = new(StringComparer.Ordinal);
    private readonly object _mcpOAuthLoginLock = new();

    /// <summary>
    /// The resolved OAuth chip message per <c>sessionId|serverName</c> once a login attempt has produced
    /// an outcome (browser opened, or sign-in couldn't start). Repeated <c>NeedsAuth</c> status events
    /// re-assert this instead of downgrading the chip back to the generic "signing you in" text.
    /// Guarded by <see cref="_mcpOAuthLoginLock"/>; cleared when the server connects.
    /// </summary>
    private readonly Dictionary<string, string> _mcpOAuthResolvedMessages = new(StringComparer.Ordinal);

    /// <summary>
    /// Configured MCP servers for the active session of each chat, captured so live
    /// <c>mcp_server_status_changed</c> events can build a meaningful error message and tell each
    /// server's transport apart (stdio servers never use OAuth).
    /// </summary>
    private readonly ConcurrentDictionary<Guid, IReadOnlyDictionary<string, McpServerConfig>> _activeMcpConfigs = new();

    /// <summary>
    /// Checks MCP server status after session creation and reacts to any server that failed or needs
    /// authentication. Runs in the background so it doesn't block message sending.
    /// </summary>
    private async Task CheckMcpServerStatusAsync(
        CopilotSession session,
        Guid chatId,
        IReadOnlyDictionary<string, McpServerConfig> configuredServers,
        CancellationToken ct)
    {
        _activeMcpConfigs[chatId] = configuredServers;
        try
        {
            // Give the MCP servers a moment to connect
            await Task.Delay(2000, ct);
            var mcpList = await session.Rpc.Mcp.ListAsync(ct);
            if (mcpList?.Servers is not { Count: > 0 }) return;

            foreach (var server in mcpList.Servers)
            {
                if (server.Status == McpServerStatus.Failed || server.Status == McpServerStatus.NeedsAuth)
                {
                    await HandleMcpServerStatusAsync(
                        session, chatId, server.Name, server.Status, server.Error, ct).ConfigureAwait(false);
                }
            }
        }
        catch { /* best effort — don't let MCP status checks break the chat flow */ }
    }

    /// <summary>
    /// Reacts to a single MCP server's status: refreshes its composer chip and, when a remote server
    /// needs OAuth, starts the interactive login once per session+server. Safe to call from both the
    /// startup status poll and live <c>mcp_server_status_changed</c> events.
    /// </summary>
    private async Task HandleMcpServerStatusAsync(
        CopilotSession session,
        Guid chatId,
        string serverName,
        McpServerStatus status,
        string? error,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serverName))
            return;

        McpServerConfig? config = null;
        if (_activeMcpConfigs.TryGetValue(chatId, out var configured))
            configured.TryGetValue(serverName, out config);

        if (status == McpServerStatus.Connected)
        {
            // A server that recovered — including after the user completed OAuth — drops its error chip
            // and forgets the prior login attempt so a later token expiry can re-drive sign-in.
            var connectedKey = McpOAuthKey(session, serverName);
            lock (_mcpOAuthLoginLock)
            {
                _mcpOAuthLoginAttempts.Remove(connectedKey);
                _mcpOAuthResolvedMessages.Remove(connectedKey);
            }
            Dispatcher.UIThread.Post(() => ClearMcpChipError(chatId, serverName));
            return;
        }

        if (status != McpServerStatus.NeedsAuth && status != McpServerStatus.Failed)
            return;

        var errorMessage = await BuildMcpStatusErrorMessageAsync(
            serverName, status, error ?? "", config, ct).ConfigureAwait(false);

        if (status == McpServerStatus.NeedsAuth)
        {
            // Don't let a repeated NeedsAuth event downgrade a richer, already-resolved message
            // (e.g. revert "Lumi opened your browser…" back to the generic "signing you in…").
            lock (_mcpOAuthLoginLock)
            {
                if (_mcpOAuthResolvedMessages.TryGetValue(McpOAuthKey(session, serverName), out var resolved))
                    errorMessage = resolved;
            }
        }

        Dispatcher.UIThread.Post(() => SetMcpChipError(chatId, serverName, errorMessage));

        if (status == McpServerStatus.NeedsAuth)
            await TryInitiateMcpOAuthLoginAsync(session, chatId, serverName, config, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the interactive MCP OAuth login for a remote server at most once per session+server.
    /// Opening the browser is delegated to <see cref="CopilotService.StartMcpOAuthLoginAsync"/>; when a
    /// cached token already authenticates the server the runtime reconnects silently and reports
    /// completion via a later status event that clears the chip.
    /// </summary>
    private async Task TryInitiateMcpOAuthLoginAsync(
        CopilotSession session,
        Guid chatId,
        string serverName,
        McpServerConfig? config,
        CancellationToken ct)
    {
        // stdio servers don't authenticate over OAuth; only remote (and unknown) servers can.
        if (config is McpStdioServerConfig)
            return;

        // Don't pop a browser for a chat the user has navigated away from.
        if (CurrentChat?.Id != chatId || _activeSession != session)
            return;

        var key = McpOAuthKey(session, serverName);
        lock (_mcpOAuthLoginLock)
        {
            if (!_mcpOAuthLoginAttempts.Add(key))
                return;
        }

        try
        {
            var authorizationUrl = await _copilotService
                .StartMcpOAuthLoginAsync(session, serverName, forceReauth: false, ct)
                .ConfigureAwait(false);

            // A non-empty URL means the runtime opened the browser for interactive consent.
            // An empty URL means a cached token is being reused; a later Connected event clears the chip.
            if (!string.IsNullOrWhiteSpace(authorizationUrl))
            {
                var openedMessage =
                    $"Lumi opened your browser to sign in to MCP server '{serverName}'. " +
                    "Finish signing in and it reconnects automatically.";
                ResolveMcpOAuthChip(chatId, serverName, key, openedMessage);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The chat was navigated away from (or its load was superseded) while sign-in was starting.
            // That isn't a real failure: forget the attempt so a later status event can retry cleanly,
            // and don't poison the chip with a bogus "couldn't start sign-in (The operation was canceled)".
            // Note the guard: an *internal* timeout (TaskCanceledException with ct NOT cancelled) is a real
            // sign-in failure and must fall through to the honest-failure branch below, not be retried.
            lock (_mcpOAuthLoginLock)
                _mcpOAuthLoginAttempts.Remove(key);
        }
        catch (Exception ex)
        {
            // Sign-in couldn't be started — e.g. the server's identity provider doesn't support
            // dynamic client registration. Report it honestly and keep the attempt recorded so we
            // don't hammer a failing endpoint on every status event; a later session retries cleanly.
            var failedMessage =
                $"Lumi couldn't start sign-in for MCP server '{serverName}' automatically " +
                $"({DescribeMcpOAuthLoginFailure(ex)}). Open it from the MCP servers page to sign in.";
            ResolveMcpOAuthChip(chatId, serverName, key, failedMessage);
        }
    }

    /// <summary>
    /// Records the final OAuth chip message for a session+server and shows it, so later repeated
    /// <c>NeedsAuth</c> status events re-assert it rather than reverting to the generic pending text.
    /// </summary>
    private void ResolveMcpOAuthChip(Guid chatId, string serverName, string key, string message)
    {
        lock (_mcpOAuthLoginLock)
            _mcpOAuthResolvedMessages[key] = message;
        Dispatcher.UIThread.Post(() => SetMcpChipError(chatId, serverName, message));
    }

    private static string McpOAuthKey(CopilotSession session, string serverName)
        => $"{session.SessionId}|{serverName}";

    /// <summary>
    /// Releases the per-session OAuth chip/login bookkeeping for a chat's current session when that
    /// session is torn down, so servers that never reached <c>Connected</c> (dismissed browser, failed
    /// sign-in, navigate-away) don't leak <c>sessionId|serverName</c> entries for the app's lifetime.
    /// </summary>
    private void ForgetMcpOAuthState(Guid chatId)
    {
        if (!_sessionCache.TryGetValue(chatId, out var session))
            return;

        var prefix = $"{session.SessionId}|";
        lock (_mcpOAuthLoginLock)
        {
            _mcpOAuthLoginAttempts.RemoveWhere(k => k.StartsWith(prefix, StringComparison.Ordinal));
            if (_mcpOAuthResolvedMessages.Count > 0)
            {
                var stale = _mcpOAuthResolvedMessages.Keys
                    .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                    .ToList();
                foreach (var staleKey in stale)
                    _mcpOAuthResolvedMessages.Remove(staleKey);
            }
        }
    }

    /// <summary>Extracts a short, user-facing reason from an MCP OAuth login failure.</summary>
    private static string DescribeMcpOAuthLoginFailure(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        const string marker = "message: ";
        var index = message.LastIndexOf(marker, StringComparison.Ordinal);
        if (index >= 0)
            message = message[(index + marker.Length)..];
        message = message.Trim();
        return string.IsNullOrEmpty(message) ? ex.GetType().Name : message;
    }

    /// <summary>Puts the named MCP composer chip into an error state (tooltip = <paramref name="errorMessage"/>).</summary>
    private void SetMcpChipError(Guid chatId, string serverName, string errorMessage)
    {
        if (CurrentChat?.Id != chatId)
            return;

        var existingChip = ActiveMcpChips.OfType<StrataComposerChip>().FirstOrDefault(c => c.Name == serverName);
        if (existingChip is null)
            return;

        ActiveMcpChips[ActiveMcpChips.IndexOf(existingChip)] = existingChip with { ErrorMessage = errorMessage };
    }

    /// <summary>Clears the error state from the named MCP composer chip, if present.</summary>
    private void ClearMcpChipError(Guid chatId, string serverName)
    {
        if (CurrentChat?.Id != chatId)
            return;

        var existingChip = ActiveMcpChips.OfType<StrataComposerChip>().FirstOrDefault(c => c.Name == serverName);
        if (existingChip is null || !existingChip.HasError)
            return;

        ActiveMcpChips[ActiveMcpChips.IndexOf(existingChip)] = existingChip with { ErrorMessage = null };
    }

    internal static async Task<string> BuildMcpStatusErrorMessageAsync(
        string serverName,
        McpServerStatus status,
        string rawError,
        McpServerConfig? config,
        CancellationToken ct)
    {
        if (status == McpServerStatus.NeedsAuth)
        {
            return config is McpHttpServerConfig authRemote
                ? $"Sign-in required for MCP server '{serverName}' at {SanitizeMcpDiagnosticUrl(authRemote.Url)}. Lumi is signing you in…"
                : $"Sign-in required for MCP server '{serverName}'. If it supports OAuth, Lumi will sign you in and reconnect automatically.";
        }

        if (config is McpHttpServerConfig remote)
            return await BuildHttpMcpStatusErrorMessageAsync(serverName, rawError, remote, ct).ConfigureAwait(false);

        if (config is McpStdioServerConfig local)
            return BuildStdioMcpStatusErrorMessage(serverName, rawError, local);

        return BuildGenericMcpStatusErrorMessage(rawError);
    }

    private static async Task<string> BuildHttpMcpStatusErrorMessageAsync(
        string serverName,
        string rawError,
        McpHttpServerConfig remote,
        CancellationToken ct)
    {
        if (ShouldProbeHttpMcpEndpoint(remote.Url))
        {
            var diagnostic = await ProbeHttpMcpEndpointAsync(remote, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(diagnostic))
                return $"HTTP MCP server '{serverName}' failed. {diagnostic}";
        }

        var safeUrl = SanitizeMcpDiagnosticUrl(remote.Url);
        return string.IsNullOrWhiteSpace(rawError)
            ? $"HTTP MCP server '{serverName}' failed to connect to {safeUrl}."
            : $"HTTP MCP server '{serverName}' failed for {safeUrl}: {rawError}";
    }

    private static string BuildStdioMcpStatusErrorMessage(
        string serverName,
        string rawError,
        McpStdioServerConfig local)
    {
        if (rawError.Contains("system cannot find the file specified", StringComparison.OrdinalIgnoreCase)
            || rawError.Contains("command not found", StringComparison.OrdinalIgnoreCase)
            || rawError.Contains("ENOENT", StringComparison.OrdinalIgnoreCase))
        {
            var cwd = string.IsNullOrWhiteSpace(local.WorkingDirectory) ? Environment.CurrentDirectory : local.WorkingDirectory;
            return $"Command not found for MCP server '{serverName}': '{local.Command}'. Working directory: '{cwd}'. Install '{local.Command}' or add it to the PATH used by Lumi.";
        }

        return BuildGenericMcpStatusErrorMessage(rawError);
    }

    private static string BuildGenericMcpStatusErrorMessage(string rawError)
    {
        return rawError switch
        {
            _ when rawError.Contains("Connection closed", StringComparison.OrdinalIgnoreCase)
                => "Server process exited immediately. Verify the command is installed and runnable.",
            _ when string.IsNullOrWhiteSpace(rawError)
                => "Failed to connect to MCP server.",
            _ => rawError
        };
    }

    private static async Task<string?> ProbeHttpMcpEndpointAsync(McpHttpServerConfig remote, CancellationToken ct)
    {
        if (!Uri.TryCreate(remote.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(
                    """
                    {"jsonrpc":"2.0","id":"lumi-diagnostic","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"lumi-diagnostic","version":"1"}}}
                    """,
                    Encoding.UTF8,
                    "application/json")
            };

            // Diagnostic probes intentionally omit configured auth headers. The SDK
            // already made the real MCP connection attempt; this probe is only for
            // safe loopback endpoint/status discovery.

            using var response = await McpDiagnosticsHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var body = await ReadHttpDiagnosticBodyAsync(response, ct).ConfigureAwait(false);
            var status = $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();
            var hint = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => " Authentication is required; configure headers/token for this MCP server.",
                HttpStatusCode.Forbidden => " Authentication or permission is required for this MCP server.",
                HttpStatusCode.NotFound => " Endpoint was not found; check the MCP URL and protocol for this server.",
                HttpStatusCode.MethodNotAllowed => " Endpoint rejected POST; check whether this server uses a different MCP transport or URL.",
                _ => ""
            };
            var summary = string.IsNullOrWhiteSpace(body) ? "" : $" Response: {body}";
            return $"POST {SanitizeMcpDiagnosticUrl(remote.Url)} returned {status}.{hint}{summary}";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return $"POST {SanitizeMcpDiagnosticUrl(remote.Url)} failed: {ex.Message}";
        }
    }

    private static string SanitizeMcpDiagnosticUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        try
        {
            var builder = new UriBuilder(uri)
            {
                UserName = "",
                Password = "",
                Query = "",
                Fragment = ""
            };

            return builder.Uri.GetLeftPart(UriPartial.Path);
        }
        catch (UriFormatException)
        {
            return uri.GetLeftPart(UriPartial.Path);
        }
    }

    private static bool ShouldProbeHttpMcpEndpoint(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.IsLoopback;
    }

    private static async Task<string> ReadHttpDiagnosticBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrWhiteSpace(mediaType)
                && !mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                && !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            body = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
            body = SensitiveHttpDiagnosticPattern.Replace(body, "$1$2[redacted]");
            return body.Length <= 300 ? body : body[..300] + "...";
        }
        catch
        {
            return "";
        }
    }

    private void SetSessionSetupStatus(Chat chat, string statusText)
    {
        var runtime = GetOrCreateRuntimeState(chat.Id);
        runtime.StatusText = statusText;
        if (CurrentChat?.Id == chat.Id)
            StatusText = statusText;
    }

    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;
    private readonly MemoryAgentService _memoryAgentService;
    private readonly CodingToolService _codingToolService;
    private readonly UIAutomationService _uiAutomation = new();
    private readonly object _chatLoadSync = new();
    private CancellationTokenSource? _chatLoadCts;
    private long _chatLoadRequestId;
    private bool _isBulkLoadingMessages;
    /// <summary>Maps chat ID → CancellationTokenSource for per-chat cancellation.</summary>
    private readonly Dictionary<Guid, CancellationTokenSource> _ctsSources = new();
    private readonly TranscriptBuilder _transcriptBuilder;
    private readonly TranscriptWindowController _transcriptWindow = new(new TranscriptPagingOptions
    {
        EnableDiagnostics = TranscriptDiagnosticsEnabled,
    });

    /// <summary>The CopilotSession for the currently displayed chat. Events for this session update the UI.</summary>
    private CopilotSession? _activeSession;
    /// <summary>Maps chat ID → locally attached CopilotSession objects for active or running chats.</summary>
    private readonly Dictionary<Guid, CopilotSession> _sessionCache = new();
    /// <summary>Tracks SDK session disposal in progress so resume waits until the prior handle is released.</summary>
    private readonly Dictionary<Guid, Task> _sessionReleaseTasks = new();
    /// <summary>Maps chat ID → live event subscriptions for locally attached sessions.</summary>
    private readonly Dictionary<Guid, IDisposable> _sessionSubs = new();
    /// <summary>Maps chat ID → in-progress streaming message not yet committed to Chat.Messages.</summary>
    private readonly Dictionary<Guid, ChatMessage> _inProgressMessages = new();
    /// <summary>Per-chat runtime state sourced from live session events.</summary>
    private readonly Dictionary<Guid, ChatRuntimeState> _runtimeStates = new();
    /// <summary>Maps chat ID → per-chat BrowserService instance. Created lazily on first browser tool use.</summary>
    private readonly ConcurrentDictionary<Guid, BrowserService> _chatBrowserServices = new();
    /// <summary>Skills activated mid-chat (after session exists). Consumed on next SendMessage to inject into prompt.</summary>
    private readonly List<Guid> _pendingSkillInjections = new();
    /// <summary>Per-chat guard so suggestion generation is queued at most once concurrently.</summary>
    private readonly HashSet<Guid> _suggestionGenerationInFlightChats = new();
    /// <summary>Chat ID that the visible suggestion row is allowed to represent, including pending chat loads.</summary>
    private Guid? _suggestionDisplayChatId;
    /// <summary>Maps chat ID → unsent composer draft text. Guid.Empty is used for the "new chat" state.</summary>
    private readonly Dictionary<Guid, string> _chatDrafts = new();
    /// <summary>Maps chat ID → prompt submitted while the chat was busy. Drained after the active turn stops.</summary>
    private readonly Dictionary<Guid, string> _queuedBusySendPrompts = new();
    /// <summary>Tracks the last assistant message ID that already produced suggestions per chat.</summary>
    private readonly Dictionary<Guid, Guid> _lastSuggestedAssistantMessageByChat = new();

    /// <summary>Gets or lazily creates a per-chat BrowserService instance. Browser tool callbacks run
    /// off the UI thread while chat-switch/cleanup code touches this map on the UI thread, so the
    /// backing store is a ConcurrentDictionary and creation goes through an atomic GetOrAdd.</summary>
    private BrowserService GetOrCreateBrowserService(Guid chatId)
        => _chatBrowserServices.GetOrAdd(chatId, static _ => new BrowserService());

    /// <summary>Gets the BrowserService for a chat if one exists, without creating.</summary>
    public BrowserService? GetBrowserServiceForChat(Guid chatId)
    {
        _chatBrowserServices.TryGetValue(chatId, out var service);
        return service;
    }

    /// <summary>Gets all per-chat BrowserService instances (for theme propagation etc.).</summary>
    public IReadOnlyDictionary<Guid, BrowserService> ChatBrowserServices => _chatBrowserServices;

    /// <summary>True while a chat is being loaded and the loading overlay is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatSurfaceLoading))]
    private bool _isLoadingChat;

    /// <summary>
    /// True while a freshly opened transcript is still realizing its mounted turns (the deferred,
    /// frame-budgeted layout pass that runs after the placeholders mount). The view drives this so
    /// the loading overlay stays up until the transcript is actually measured and pinned, rather
    /// than flashing blank for a frame.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatSurfaceLoading))]
    private bool _isTranscriptRealizing;

    /// <summary>
    /// Drives the chat loading overlay: visible while either the chat history is loading or the
    /// transcript is still realizing its mounted turns after an open/switch.
    /// </summary>
    public bool IsChatSurfaceLoading => IsLoadingChat || IsTranscriptRealizing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentChatTitle))]
    private Chat? _currentChat;

    /// <summary>Exposes CurrentChat.Title so the header binding updates without toggling CurrentChat.</summary>
    public string? CurrentChatTitle => CurrentChat?.Title;

    [ObservableProperty] private string? _promptText;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _selectedModel;

    partial void OnIsBusyChanging(bool value)
    {
        if (!value)
            FinalizeTranscriptActivityBeforeIdle();
    }

    private void FinalizeTranscriptActivityBeforeIdle()
    {
        _transcriptBuilder.CloseCurrentToolGroup();
        _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
    }

    [ObservableProperty] private LumiAgent? _activeAgent;
    [ObservableProperty] private long _totalInputTokens;
    [ObservableProperty] private long _totalOutputTokens;
    [ObservableProperty] private long _contextCurrentTokens;
    [ObservableProperty] private long _contextTokenLimit;

    public bool HasTokenUsage => TotalInputTokens > 0 || TotalOutputTokens > 0;
    public bool ShowInfoStrip => IsCodingProject || HasTokenUsage;
    public string TokenUsageSummary => HasContextUsage
        ? $"{ContextUsagePercent}%"
        : FormatTokenCount(TotalInputTokens + TotalOutputTokens);
    public string TokenUsageSuffixText => HasContextUsage ? "context" : "tokens";
    public string TokenInputDisplay => $"{TotalInputTokens:N0}";
    public string TokenOutputDisplay => $"{TotalOutputTokens:N0}";
    public string TokenTotalDisplay => $"{TotalInputTokens + TotalOutputTokens:N0}";
    public bool HasContextUsage => ContextCurrentTokens > 0 && ContextTokenLimit > 0;
    public string? ActiveSessionModelId => CurrentChat is not null && _runtimeStates.TryGetValue(CurrentChat.Id, out var runtime)
        ? runtime.ActiveModelId
        : null;
    public string? ActiveSessionContextWindowTier => CurrentChat is not null && _runtimeStates.TryGetValue(CurrentChat.Id, out var runtime)
        ? runtime.ActiveContextWindowTier
        : null;
    public string ContextTokenLimitSourceDisplay => CurrentChat is not null && _runtimeStates.TryGetValue(CurrentChat.Id, out var runtime)
        ? runtime.ContextTokenLimitSource.ToString()
        : ContextTokenLimitSource.Unknown.ToString();
    public int ContextUsagePercent => ContextTokenLimit > 0
        ? (int)Math.Round(100.0 * ContextCurrentTokens / ContextTokenLimit)
        : 0;
    public string ContextUsageDisplay => HasContextUsage
        ? $"{FormatTokenCount(ContextCurrentTokens)} / {FormatTokenCount(ContextTokenLimit)}"
        : "";

    partial void OnTotalInputTokensChanged(long value) { NotifyTokenPropertiesChanged(); }
    partial void OnTotalOutputTokensChanged(long value) { NotifyTokenPropertiesChanged(); }
    partial void OnContextCurrentTokensChanged(long value) { NotifyTokenPropertiesChanged(); }
    partial void OnContextTokenLimitChanged(long value) { NotifyTokenPropertiesChanged(); }

    private void NotifyTokenPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasTokenUsage));
        OnPropertyChanged(nameof(ShowInfoStrip));
        OnPropertyChanged(nameof(TokenUsageSummary));
        OnPropertyChanged(nameof(TokenUsageSuffixText));
        OnPropertyChanged(nameof(TokenInputDisplay));
        OnPropertyChanged(nameof(TokenOutputDisplay));
        OnPropertyChanged(nameof(TokenTotalDisplay));
        OnPropertyChanged(nameof(HasContextUsage));
        OnPropertyChanged(nameof(ContextTokenLimitSourceDisplay));
        OnPropertyChanged(nameof(ContextUsagePercent));
        OnPropertyChanged(nameof(ContextUsageDisplay));
    }

    private static string FormatTokenCount(long tokens) => tokens switch
    {
        < 1_000 => $"{tokens}",
        < 1_000_000 => $"{tokens / 1_000.0:0.#}K",
        _ => $"{tokens / 1_000_000.0:0.##}M"
    };

    private static long NormalizeTokenCount(double tokens)
    {
        if (double.IsNaN(tokens) || double.IsInfinity(tokens) || tokens <= 0)
            return 0;

        return (long)Math.Round(tokens);
    }

    internal static (long TokenLimit, ContextTokenLimitSource Source) ResolveContextTokenLimitFromSessionUsage(
        long sessionTokenLimit,
        long catalogTokenLimit)
    {
        if (sessionTokenLimit > 0)
            return (sessionTokenLimit, ContextTokenLimitSource.Session);

        return catalogTokenLimit > 0
            ? (catalogTokenLimit, ContextTokenLimitSource.Catalog)
            : (0, ContextTokenLimitSource.Unknown);
    }

    private long ResolveKnownContextTokenLimit(string? modelId, string? contextTier = null)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return 0;

        if (string.Equals(contextTier, ModelContextWindowTiers.LongContext, StringComparison.OrdinalIgnoreCase)
            && _modelLongContextTokenLimits.TryGetValue(modelId, out var longTokenLimit))
        {
            return longTokenLimit;
        }

        return _modelContextTokenLimits.TryGetValue(modelId, out var tokenLimit)
            ? tokenLimit
            : 0;
    }

    internal (string? ModelId, string? ContextTier) ResolveCatalogFallbackContextWindowSelection(
        Chat chat,
        ChatRuntimeState runtime,
        string? requestedModelId)
    {
        if (!string.IsNullOrWhiteSpace(runtime.ActiveModelId))
        {
            var activeTier = !string.IsNullOrWhiteSpace(runtime.ActiveContextWindowTier)
                ? runtime.ActiveContextWindowTier
                : _modelsWithLongContext.Contains(runtime.ActiveModelId)
                    ? ModelContextWindowTiers.Default
                    : null;
            return (runtime.ActiveModelId, activeTier);
        }

        return (requestedModelId, ResolveSelectedContextWindowTierForChat(chat, requestedModelId));
    }

    private string? ResolveSessionContextWindowTier(string? modelId, object? sessionContextTier)
    {
        var tierValue = GetSessionContextTierValue(sessionContextTier);
        if (!string.IsNullOrWhiteSpace(tierValue))
            return ModelSelectionHelper.NormalizeContextWindowTier(tierValue, modelId, _modelsWithLongContext);

        return !string.IsNullOrWhiteSpace(modelId) && _modelsWithLongContext.Contains(modelId)
            ? ModelContextWindowTiers.Default
            : null;
    }

    private static string? GetSessionContextTierValue(object? sessionContextTier)
    {
        if (sessionContextTier is null)
            return null;

        if (sessionContextTier is ContextTier contextTier)
            return contextTier.Value;

        return sessionContextTier.ToString();
    }

    private void ApplySessionModelState(
        Chat chat,
        ChatRuntimeState runtime,
        string? modelId,
        string? reasoningEffort,
        object? sessionContextTier,
        bool updateDisplayed)
    {
        var effectiveModel = string.IsNullOrWhiteSpace(modelId)
            ? ResolveSelectedModelForChat(chat)
            : modelId;
        var effectiveContextTier = ResolveSessionContextWindowTier(effectiveModel, sessionContextTier);
        var modelStateChanged = !string.Equals(runtime.ActiveModelId, effectiveModel, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(runtime.ActiveContextWindowTier, effectiveContextTier, StringComparison.OrdinalIgnoreCase);

        if (modelStateChanged && runtime.ContextTokenLimitSource == ContextTokenLimitSource.Session)
        {
            runtime.ContextTokenLimit = 0;
            runtime.ContextTokenLimitSource = ContextTokenLimitSource.Unknown;
            chat.ContextTokenLimit = 0;
            if (updateDisplayed)
                ContextTokenLimit = 0;
        }

        runtime.ActiveModelId = effectiveModel;
        runtime.ActiveContextWindowTier = effectiveContextTier;
        OnPropertyChanged(nameof(ActiveSessionModelId));
        OnPropertyChanged(nameof(ActiveSessionContextWindowTier));
        OnPropertyChanged(nameof(ContextTokenLimitSourceDisplay));

        if (!string.IsNullOrWhiteSpace(effectiveModel))
            chat.LastModelUsed = effectiveModel;

        chat.LastReasoningEffortUsed = ModelSelectionHelper.NormalizeEffort(
            reasoningEffort,
            effectiveModel,
            _modelReasoningEfforts,
            _modelDefaultEfforts);
        chat.LastContextWindowTierUsed = effectiveContextTier;

        if (updateDisplayed && !string.IsNullOrWhiteSpace(effectiveModel))
            ApplyModelSelection(effectiveModel, chat.LastReasoningEffortUsed, effectiveContextTier);

        ApplyKnownContextTokenLimit(chat, runtime, effectiveModel, updateDisplayed);
    }

    private void ApplyKnownContextTokenLimit(
        Chat chat,
        ChatRuntimeState runtime,
        string? modelId,
        bool updateDisplayed)
    {
        if (runtime.ContextTokenLimitSource == ContextTokenLimitSource.Session)
            return;

        var (fallbackModelId, contextTier) = ResolveCatalogFallbackContextWindowSelection(chat, runtime, modelId);
        var tokenLimit = ResolveKnownContextTokenLimit(fallbackModelId, contextTier);
        if (tokenLimit <= 0)
            return;

        var currentTokens = runtime.ContextCurrentTokens <= 0 && chat.ContextCurrentTokens > 0
            ? chat.ContextCurrentTokens
            : (long?)null;
        ApplyContextUsage(chat, runtime, currentTokens, tokenLimit, ContextTokenLimitSource.Catalog, updateDisplayed);
    }

    private void ApplyContextUsage(
        Chat chat,
        ChatRuntimeState runtime,
        long? currentTokens,
        long? tokenLimit,
        ContextTokenLimitSource tokenLimitSource,
        bool updateDisplayed)
    {
        if (currentTokens is > 0 and var currentTokenValue)
        {
            runtime.ContextCurrentTokens = currentTokenValue;
            chat.ContextCurrentTokens = currentTokenValue;
        }

        if (tokenLimit is > 0 and var tokenLimitValue)
        {
            var canApplyTokenLimit = tokenLimitSource != ContextTokenLimitSource.Catalog
                || runtime.ContextTokenLimitSource != ContextTokenLimitSource.Session;
            if (canApplyTokenLimit)
            {
                runtime.ContextTokenLimit = tokenLimitValue;
                runtime.ContextTokenLimitSource = tokenLimitSource;
                chat.ContextTokenLimit = tokenLimitValue;
                OnPropertyChanged(nameof(ContextTokenLimitSourceDisplay));
            }
        }

        if (updateDisplayed)
        {
            ContextCurrentTokens = runtime.ContextCurrentTokens;
            ContextTokenLimit = runtime.ContextTokenLimit;
        }
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    /// <summary>Full transcript turn store retained in memory for the active chat.</summary>
    [ObservableProperty] private ObservableCollection<TranscriptTurn> _transcriptTurns = [];
    public ObservableCollection<TranscriptTurn> MountedTranscriptTurns => _transcriptWindow.MountedTurns;
    public string TranscriptDiagnosticsText => ShowTranscriptDiagnostics ? _transcriptWindow.DiagnosticsText : string.Empty;
    public bool IsTranscriptPinnedToBottom => _transcriptWindow.IsPinnedToBottom;
    public bool ShowTranscriptDiagnostics { get; } = TranscriptDiagnosticsEnabled;

    public ObservableCollection<string> AvailableModels { get; } = [];
    public ObservableCollection<string> PendingAttachments { get; } = [];

    /// <summary>Skills currently active for this chat session — shown as chips in the composer.</summary>
    public ObservableCollection<object> ActiveSkillChips { get; } = [];

    /// <summary>Skill IDs active for the current chat.</summary>
    public List<Guid> ActiveSkillIds { get; } = [];

    /// <summary>MCP servers currently active for this chat session — shown as chips in the composer.</summary>
    public ObservableCollection<object> ActiveMcpChips { get; } = [];

    /// <summary>MCP server names active for the current chat (empty = use all enabled).</summary>
    public List<string> ActiveMcpServerNames { get; } = [];

    [ObservableProperty] private string _suggestionA = string.Empty;
    [ObservableProperty] private string _suggestionB = string.Empty;
    [ObservableProperty] private string _suggestionC = string.Empty;
    [ObservableProperty] private bool _isSuggestionsGenerating;

    /// <summary>True when any generated suggestion chip is available (not generating, at least one non-empty).</summary>
    public bool HasSuggestions =>
        !IsSuggestionsGenerating &&
        (!string.IsNullOrWhiteSpace(SuggestionA) ||
         !string.IsNullOrWhiteSpace(SuggestionB) ||
         !string.IsNullOrWhiteSpace(SuggestionC));

    partial void OnSuggestionAChanged(string value) => OnPropertyChanged(nameof(HasSuggestions));
    partial void OnSuggestionBChanged(string value) => OnPropertyChanged(nameof(HasSuggestions));
    partial void OnSuggestionCChanged(string value) => OnPropertyChanged(nameof(HasSuggestions));
    partial void OnIsSuggestionsGeneratingChanged(bool value) => OnPropertyChanged(nameof(HasSuggestions));

     // Events for the view to react to
     public event Action? ScrollToEndRequested;
     public event Action? UserMessageSent;
     public event Action? ChatUpdated;
    public event Action? FeatureManagementStateChanged;

     /// <summary>Test-only helper to raise ChatUpdated without sending a real message.</summary>
     internal void RaiseChatUpdatedForTest() => ChatUpdated?.Invoke();
     /// <summary>Test-only helper to raise feature-management UI refresh notifications.</summary>
     internal void RaiseFeatureManagementStateChangedForTest() => FeatureManagementStateChanged?.Invoke();
     public event Action<Guid, string>? ChatTitleChanged;
     public event Action? BrowserHideRequested;
    /// <summary>Raised when a file-edit tool wants to show a diff in the preview island.</summary>
    public event Action<FileChangeItem>? DiffShowRequested;
    /// <summary>Raised to hide the diff preview island.</summary>
    public event Action? DiffHideRequested;
    /// <summary>Raised when the user clicks the plan card to open it in the right panel.</summary>
    public event Action? PlanShowRequested;

    /// <summary>Raised when a model/effort change in a new chat updates the global default selection.</summary>
    public event Action<string, string?, string?>? DefaultModelSelectionChanged;
    /// <summary>Raised to hide the plan preview island.</summary>
    public event Action? PlanHideRequested;

    /// <summary>Raised when the LLM calls ask_question. Args: questionId, question, options (JSON array string), allowFreeText.</summary>
    public event Action<string, string, string, bool>? QuestionAsked;

    /// <summary>Pending question completions keyed by question ID.</summary>
    private readonly Dictionary<string, TaskCompletionSource<string>> _pendingQuestions = new();

    /// <summary>Raised when the view should rebuild DataTemplates (e.g. settings changed).</summary>
    public event Action? TranscriptRebuilt;

    /// <summary>Raised when a Workspace activity item asks to scroll the transcript to a turn (by StableId).</summary>
    public event Action<string>? WorkspaceJumpToTurnRequested;

    /// <summary>Raised when the Workspace panel open/closed preference changes so the view re-evaluates visibility.</summary>
    public event Action? WorkspacePanelPreferenceChanged;

    public ChatViewModel(DataStore dataStore, CopilotService copilotService)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        _memoryAgentService = new MemoryAgentService(dataStore, copilotService);
        _codingToolService = new CodingToolService(copilotService, GetCurrentCancellationToken);
        _selectedModel = dataStore.Data.Settings.PreferredModel;

        _transcriptBuilder = new TranscriptBuilder(
            dataStore,
            showDiffAction: item => DiffShowRequested?.Invoke(item),
            submitQuestionAnswerAction: SubmitQuestionAnswer,
            resendFromMessageAction: ResendFromMessageAsync,
            getSelectedModel: () => SelectedModel);
        _transcriptBuilder.SetLiveTarget(_transcriptTurns);
        _transcriptWindow.BindTranscript(_transcriptTurns, "ctor");
        _transcriptWindow.PropertyChanged += OnTranscriptWindowPropertyChanged;

        // Seed with preferred modelso the ComboBox has an initial selection
        if (!string.IsNullOrWhiteSpace(_selectedModel))
            AvailableModels.Add(_selectedModel);

        // Default all enabled MCPs to active so the MCP picker shows them checked
        PopulateDefaultMcps();

        // Wire messages → transcript items
        Messages.CollectionChanged += (_, args) =>
        {
            if (_isBulkLoadingMessages || _transcriptBuilder.IsRebuildingTranscript) return;

            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && args.NewItems is not null)
            {
                foreach (ChatMessageViewModel msgVm in args.NewItems)
                    _transcriptBuilder.ProcessMessageToTranscript(msgVm);
            }
            else if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                TranscriptTurns.Clear();
                _transcriptBuilder.ResetState();
            }
        };

        // When the CopilotService reconnects (new CLI process), all cached sessions
        // are invalid — they reference the old, dead client.
        _copilotService.Reconnected += OnCopilotReconnected;

        // When a session is deleted remotely, detach it so the next send creates a fresh one.
        _copilotService.SessionDeletedRemotely += OnSessionDeletedRemotely;

        InitializeMvvmUiState();
    }

    private void OnTranscriptWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ShowTranscriptDiagnostics && e.PropertyName == nameof(TranscriptWindowController.DiagnosticsText))
            OnPropertyChanged(nameof(TranscriptDiagnosticsText));

        if (e.PropertyName == nameof(TranscriptWindowController.IsPinnedToBottom))
            OnPropertyChanged(nameof(IsTranscriptPinnedToBottom));
    }

    private void SetSelectedModelValue(string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(modelId) && !AvailableModels.Contains(modelId))
            AvailableModels.Add(modelId);

        SelectedModel = modelId;
    }

    public void RestoreDefaultModelSelection()
    {
        ApplyModelSelection(
            _dataStore.Data.Settings.PreferredModel,
            _dataStore.Data.Settings.ReasoningEffort,
            _dataStore.Data.Settings.ContextWindowTier);
    }

    partial void OnIsBusyChanged(bool value)
    {
        if (value)
            _transcriptBuilder.ShowTypingIndicator(StatusText);
        else
        {
            _transcriptBuilder.HideTypingIndicator();
            // Refresh git status after turn completes
            if (IsCodingProject)
                QueueRefreshCodingProjectState();
            // Newly produced files / sources may have arrived this turn.
            RebuildWorkspacePanel();
        }
    }

    partial void OnStatusTextChanged(string value)
    {
        if (IsBusy)
            _transcriptBuilder.UpdateTypingIndicatorLabel(value);
    }

    internal void RebuildTranscript()
    {
        TranscriptTurns = _transcriptBuilder.Rebuild(Messages);
        _transcriptWindow.BindTranscript(TranscriptTurns, "rebuild");
        _transcriptWindow.ResetToLatest(TranscriptWindowController.DefaultInitialViewportHeight, "rebuild");

        // Rebuild() calls ResetState() which clears the typing indicator.
        // Re-show it if this chat is still busy (e.g. switching to a streaming chat).
        if (IsBusy)
            _transcriptBuilder.ShowTypingIndicator(StatusText);

        RebuildWorkspacePanel();

        TranscriptRebuilt?.Invoke();
    }

    private IReadOnlyList<ChatMessage> GetDisplayMessagesForChat(Chat chat)
    {
        var displayMessages = chat.Messages
            .Where(static msg => msg.Role != "assistant" || !string.IsNullOrWhiteSpace(msg.Content))
            .ToList();

        if (_inProgressMessages.TryGetValue(chat.Id, out var inProgress)
            && displayMessages.All(message => message.Id != inProgress.Id))
        {
            displayMessages.Add(inProgress);
        }

        return displayMessages;
    }

    private bool AreDisplayedMessagesInSync(IReadOnlyList<ChatMessage> displayMessages)
    {
        if (Messages.Count != displayMessages.Count)
            return false;

        for (var i = 0; i < displayMessages.Count; i++)
        {
            var message = displayMessages[i];
            var viewModel = Messages[i];
            if (viewModel.Message.Id != message.Id
                || viewModel.Role != message.Role
                || !string.Equals(viewModel.Content, message.Content, StringComparison.Ordinal)
                || viewModel.IsStreaming != message.IsStreaming
                || !string.Equals(viewModel.ToolStatus, message.ToolStatus, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void SynchronizeDisplayedMessagesFromChat(Chat chat, bool forceRebuild = false)
    {
        var displayMessages = GetDisplayMessagesForChat(chat);
        if (!forceRebuild && AreDisplayedMessagesInSync(displayMessages))
            return;

        _isBulkLoadingMessages = true;
        try
        {
            Messages.Clear();
            foreach (var msg in displayMessages)
                Messages.Add(new ChatMessageViewModel(msg));

            RebuildTranscript();
        }
        finally
        {
            _isBulkLoadingMessages = false;
        }
    }

    private static string BuildSubagentPayloadJson(
        string? description,
        string? agentName,
        string? agentDisplayName,
        string? agentDescription,
        string? mode,
        string? model = null,
        string? transcript = null,
        string? reasoning = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("description", description ?? string.Empty);
            writer.WriteString("agentName", agentName ?? string.Empty);
            writer.WriteString("agentDisplayName", agentDisplayName ?? string.Empty);
            writer.WriteString("agentDescription", agentDescription ?? string.Empty);
            writer.WriteString("mode", mode ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(model))
                writer.WriteString("model", model);
            writer.WriteString("transcript", transcript ?? string.Empty);
            writer.WriteString("reasoning", reasoning ?? string.Empty);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    internal TranscriptWindowMutation InitializeMountedTranscript(double viewportHeight)
    {
        var mutation = _transcriptWindow.ResetToLatest(viewportHeight, "initial-open");
        if (ShowTranscriptDiagnostics)
            OnPropertyChanged(nameof(TranscriptDiagnosticsText));
        return mutation;
    }

    internal TranscriptWindowMutation EnsureMountedTranscriptCoverage(double viewportHeight, double? actualExtentHeight = null)
    {
        var mutation = _transcriptWindow.EnsureViewportCoverage(viewportHeight, "viewport-fill", actualExtentHeight);
        if (ShowTranscriptDiagnostics)
            OnPropertyChanged(nameof(TranscriptDiagnosticsText));
        return mutation;
    }

    internal TranscriptWindowMutation UpdateTranscriptViewport(
        double offsetY,
        double viewportHeight,
        double extentHeight,
        bool isPinnedToBottom,
        double distanceFromBottom)
    {
        var mutation = _transcriptWindow.UpdateViewport(
            new TranscriptViewportState(
                offsetY,
                viewportHeight,
                extentHeight,
                isPinnedToBottom,
                distanceFromBottom),
            "scroll");
        if (ShowTranscriptDiagnostics)
            OnPropertyChanged(nameof(TranscriptDiagnosticsText));
        return mutation;
    }

    internal void UpdateTranscriptPinnedState(bool isPinnedToBottom, double distanceFromBottom)
    {
        _transcriptWindow.UpdatePinnedState(isPinnedToBottom, distanceFromBottom, "scroll-state");
    }

    internal bool EnsureLatestTranscriptMounted()
    {
        var changed = _transcriptWindow.EnsureLatestMounted("user-sent");
        if (changed && ShowTranscriptDiagnostics)
            OnPropertyChanged(nameof(TranscriptDiagnosticsText));
        return changed;
    }

    internal TranscriptWindowMutation EnsureLatestTranscriptMountedIfAdjacentTailGap()
    {
        var mutation = _transcriptWindow.EnsureLatestMountedIfAdjacentTailGap("assistant-completed");
        if (mutation.HasChanges && ShowTranscriptDiagnostics)
            OnPropertyChanged(nameof(TranscriptDiagnosticsText));
        return mutation;
    }

    internal bool MountTranscriptPageContainingTurn(TranscriptTurn turn)
    {
        var changed = _transcriptWindow.MountPageContainingTurn(turn, "search-navigate");
        if (changed && ShowTranscriptDiagnostics)
            OnPropertyChanged(nameof(TranscriptDiagnosticsText));
        return changed;
    }

    internal void RecordTranscriptScrollCompensation(string reason, double beforeOffset, double afterOffset)
    {
        _transcriptWindow.RecordScrollCompensation(reason, beforeOffset, afterOffset);
        if (ShowTranscriptDiagnostics)
            OnPropertyChanged(nameof(TranscriptDiagnosticsText));
    }

    internal TranscriptWindowDiagnosticsSnapshot CaptureTranscriptDiagnostics() => _transcriptWindow.CaptureSnapshot();

    private List<Skill> ResolveSkillsByIds(IReadOnlyCollection<Guid> skillIds)
    {
        if (skillIds.Count == 0)
            return [];

        var skillsById = _dataStore.Data.Skills.ToDictionary(s => s.Id);
        var resolvedSkills = new List<Skill>(skillIds.Count);
        foreach (var skillId in skillIds)
        {
            if (skillsById.TryGetValue(skillId, out var skill))
                resolvedSkills.Add(skill);
        }

        return resolvedSkills;
    }

    private List<SkillReference> BuildSkillReferences(IReadOnlyCollection<Guid> skillIds)
    {
        return ResolveSkillsByIds(skillIds)
            .Select(static s => new SkillReference
            {
                Name = s.Name,
                Glyph = s.IconGlyph,
                Description = s.Description
            })
            .ToList();
    }

    private async Task<string?> SyncActiveSkillDirectoryAsync(CancellationToken ct)
        => await SyncSkillDirectoryAsync(ActiveSkillIds, ct);

    private async Task<string?> SyncSkillDirectoryAsync(IReadOnlyCollection<Guid> skillIds, CancellationToken ct)
    {
        if (skillIds.Count == 0)
            return null;

        var activeSkillIds = skillIds.ToList();
        return await _dataStore.SyncSkillFilesForIdsAsync(activeSkillIds, ct);
    }

    private (long RequestId, CancellationTokenSource Source) BeginChatLoad(CancellationToken outerCancellationToken)
    {
        CancellationTokenSource? previous;
        CancellationTokenSource current;
        long requestId;

        lock (_chatLoadSync)
        {
            previous = _chatLoadCts;
            current = CancellationTokenSource.CreateLinkedTokenSource(outerCancellationToken);
            _chatLoadCts = current;
            requestId = ++_chatLoadRequestId;
        }

        try { previous?.Cancel(); }
        catch (ObjectDisposedException) { }
        return (requestId, current);
    }

    private bool IsCurrentChatLoad(long requestId, CancellationTokenSource source)
    {
        lock (_chatLoadSync)
            return requestId == _chatLoadRequestId && ReferenceEquals(_chatLoadCts, source);
    }

    /// <summary>Creates or resumes a Copilot session for the given chat, building
    /// system prompt, tools, agents, skill dirs, and MCP servers as needed.</summary>
    private async Task<bool> EnsureSessionAsync(
        Chat chat,
        CancellationToken ct,
        bool allowCreateFallback = true)
    {
        var allSkills = _dataStore.Data.Skills;
        var activeSkills = ResolveSkillsByIds(chat.ActiveSkillIds);
        var memories = _dataStore.Data.Memories;
        var project = chat.ProjectId.HasValue
            ? _dataStore.Data.Projects.FirstOrDefault(p => p.Id == chat.ProjectId)
            : null;
        var workDir = GetEffectiveWorkingDirectory(chat);
        var projectContextCatalog = GetProjectContextCatalog(chat, workDir);
        var externalSkills = projectContextCatalog.Skills;
        var activeAgent = chat.AgentId.HasValue
            ? _dataStore.Data.Agents.FirstOrDefault(agent => agent.Id == chat.AgentId.Value)
            : null;
        var systemPrompt = SystemPromptBuilder.Build(
            _dataStore.Data.Settings, activeAgent, project, allSkills, activeSkills, memories, _dataStore.SnapshotBackgroundJobs());
        systemPrompt = AppendAvailableExternalSkillsToPrompt(systemPrompt, externalSkills, chat.ActiveExternalSkillNames);

        var sdkAgentName = GetSessionSdkAgentName(chat, CurrentChat, SelectedSdkAgentName);
        var externalAgent = activeAgent is null
            ? FindExternalAgentByName(projectContextCatalog, sdkAgentName)
            : null;
        var skillDirTask = SyncSkillDirectoryAsync(chat.ActiveSkillIds, ct);
        var mcpServers = BuildMcpServers(workDir, projectContextCatalog, chat, activeAgent);

        var customAgents = BuildCustomAgents();
        var customTools = BuildCustomTools(chat.Id, activeAgent, projectContextCatalog);
        if (!string.IsNullOrWhiteSpace(externalAgent?.Content))
            systemPrompt = (systemPrompt ?? "") + "\n\n--- Active Agent: " + externalAgent.Name + " ---\n" + externalAgent.Content;

        var skillDirs = new List<string>();
        var dir = await skillDirTask;
        if (!string.IsNullOrWhiteSpace(dir))
            skillDirs.Add(dir);

        var selectedModel = ResolveSelectedModelForChat(chat);
        var persistedEffort = ResolvePersistedReasoningEffortForChat(chat, selectedModel);
        if (chat.LastReasoningEffortUsed != persistedEffort)
            chat.LastReasoningEffortUsed = persistedEffort;

        var effort = persistedEffort;
        var contextTier = ResolveSelectedContextWindowTierForChat(chat, selectedModel);
        var agentName = ResolveSessionAgentName(
            activeAgent,
            externalAgent,
            sdkAgentName,
            allowSdkAgentRouting: CanRouteSdkAgentByName(chat, externalAgent, sdkAgentName));

        // Native user input handler — wired to the existing question card UI.
        // Capture chat.Id in the closure so questions always target the owning chat,
        // even if the user switches to a different chat while this session is active.
        var inputHandlerChatId = chat.Id;
        Func<UserInputRequest, UserInputInvocation, Task<UserInputResponse>> userInputHandler = async (request, invocation) =>
        {
            var questionId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingQuestions[questionId] = tcs;

            var optionsList = request.Choices is { Count: > 0 } ? (IList<string>)request.Choices : Array.Empty<string>();
            var optionsJson = System.Text.Json.JsonSerializer.Serialize(optionsList.ToList(), Lumi.Models.AppDataJsonContext.Default.ListString);
            var freeText = request.AllowFreeform ?? true;

            Dispatcher.UIThread.Post(() =>
            {
                if (CurrentChat?.Id != inputHandlerChatId) return;
                _transcriptBuilder.AddQuestionToTranscript(questionId, request.Question, optionsList, freeText);
                QuestionAsked?.Invoke(questionId, request.Question, optionsJson, freeText);
                ScrollToEndRequested?.Invoke();
            });

            // Persist question data on the tool message so rebuild can recreate the question card.
            // If no matching tool message exists (SDK native user-input path), create one.
            Dispatcher.UIThread.Post(() =>
            {
                var owningChat = _dataStore.Data.Chats.Find(c => c.Id == inputHandlerChatId);
                if (owningChat is not null)
                {
                    var toolMsg = owningChat.Messages.LastOrDefault(m =>
                        m.ToolName == "ask_question" && m.ToolStatus == "InProgress" && m.QuestionId is null);
                    if (toolMsg is null)
                    {
                        toolMsg = new ChatMessage
                        {
                            Role = "tool",
                            ToolName = "ask_question",
                            ToolStatus = "InProgress",
                            Content = "",
                        };
                        owningChat.Messages.Add(toolMsg);
                    }
                    toolMsg.QuestionId = questionId;
                    toolMsg.QuestionText = request.Question;
                    toolMsg.QuestionOptions = optionsJson;
                    toolMsg.QuestionAllowFreeText = freeText;
                    toolMsg.QuestionAllowMultiSelect = false;
                }
            });

            try
            {
                var answer = await tcs.Task;
                return new GitHub.Copilot.UserInputResponse { Answer = answer, WasFreeform = true };
            }
            finally
            {
                _pendingQuestions.Remove(questionId);
            }
        };

        // Session hooks for lifecycle events
        var hooks = new GitHub.Copilot.SessionHooks
        {
            OnPreToolUse = async (input, invocation) =>
            {
                // Auto-allow all tools (permission UI can be added later)
                return new GitHub.Copilot.PreToolUseHookOutput { PermissionDecision = "allow" };
            },
            OnErrorOccurred = async (input, invocation) =>
            {
                // Retry transient errors, abort on persistent ones. Besides the SDK's own
                // Recoverable flag, GitHub's backend occasionally wraps an internal RPC failure
                // (twirp/usersd "failed to do request") in a 401 on long sessions; the CLI marks it
                // non-recoverable but a plain resend recovers, so retry those too. Bare/ambiguous
                // 401/403s are deliberately NOT matched — they may be a genuine logout and must
                // surface (abort) so the user can re-authenticate.
                if (input.Recoverable || CopilotService.IsTransientServerAuthError(input.Error))
                    return new GitHub.Copilot.ErrorOccurredHookOutput { ErrorHandling = "retry", RetryCount = 3 };
                return new GitHub.Copilot.ErrorOccurredHookOutput { ErrorHandling = "abort" };
            }
        };

        if (mcpServers is { Count: > 0 })
            SetSessionSetupStatus(chat, Loc.Status_ConnectingMcp);

        // When MCP servers are configured, apply a timeout so a broken server
        // doesn't block the UI indefinitely.
        using var sessionCts = mcpServers is { Count: > 0 }
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        sessionCts?.CancelAfter(TimeSpan.FromSeconds(30));
        var sessionCt = sessionCts?.Token ?? ct;

        if (chat.CopilotSessionId is not null)
            await AwaitPendingSessionReleaseAsync(chat.Id, sessionCt);

        if (chat.CopilotSessionId is null)
        {
            if (!allowCreateFallback)
                return false;

            try
            {
                var createConfig = SessionConfigBuilder.Build(
                    systemPrompt, selectedModel, workDir, skillDirs, customAgents, customTools,
                    mcpServers, effort, userInputHandler, onPermission: null, hooks, agentName, contextTier);
                var createdSession = await _copilotService.CreateSessionAsync(createConfig, sessionCt);
                chat.CopilotSessionId = createdSession.SessionId;
                _dataStore.MarkChatChanged(chat);
                _activeSession = createdSession;
                SubscribeToSession(createdSession, chat, workDir);

                // Check MCP server status after session creation and surface errors
                if (mcpServers is { Count: > 0 })
                {
                    _ = CheckMcpServerStatusAsync(createdSession, chat.Id, mcpServers, ct);
                }

                return true;
            }
            catch (OperationCanceledException) when (sessionCts is not null && sessionCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException("MCP server connection timed out. Check that your MCP servers are installed and responding.");
            }
        }

        // Try to resume with retry for transient errors
        const int maxRetries = 2;
        Exception? lastError = null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                SetSessionSetupStatus(chat, attempt > 0 ? Loc.Status_Reconnecting : Loc.Status_Resuming);
                var resumeConfig = SessionConfigBuilder.BuildForResume(
                    systemPrompt, selectedModel, workDir, skillDirs, customAgents, customTools,
                    mcpServers, effort, userInputHandler, onPermission: null, hooks, agentName, contextTier);
                var session = await _copilotService.ResumeSessionAsync(
                    chat.CopilotSessionId, resumeConfig, sessionCt);
                _activeSession = session;
                SubscribeToSession(session, chat, workDir);
                if (mcpServers is { Count: > 0 })
                    _ = CheckMcpServerStatusAsync(session, chat.Id, mcpServers, ct);

                // The SDK does not automatically change the session model on resume —
                // ResumeSessionConfig.Model only sets a preference for the CLI process,
                // but the session's internal model stays at whatever it was created with.
                // Explicitly call SetModelAsync so context-window limits match the
                // user's current selection (e.g. switching from gpt-5.4 to opus-4.6-1m).
                if (!string.IsNullOrWhiteSpace(selectedModel))
                {
                    try
                    {
                        await session.SetModelAsync(
                            selectedModel,
                            new SetModelOptions
                            {
                                ReasoningEffort = effort,
                                ReasoningSummary = SessionConfigBuilder.DefaultReasoningSummary,
                                ContextTier = SessionConfigBuilder.CreateContextTier(contextTier)
                            },
                            sessionCt);
                    }
                    catch { /* best-effort — session works with original model if this fails */ }
                }

                return true; // Resume succeeded
            }
            catch (OperationCanceledException) when (sessionCts is not null && sessionCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException("MCP server connection timed out. Check that your MCP servers are installed and responding.");
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (!await _copilotService.IsHealthyAsync(TimeSpan.FromSeconds(2)))
                    await TryReconnectCopilotAsync(ct);

                if (attempt < maxRetries)
                {
                    await Task.Delay(500 * (attempt + 1), ct);
                    continue;
                }
            }
        }

        // All retries failed.
        SetSessionSetupStatus(chat, Loc.Status_SessionExpired);
        if (!allowCreateFallback)
            return false;

        try
        {
            var createConfig = SessionConfigBuilder.Build(
                systemPrompt, selectedModel, workDir, skillDirs, customAgents, customTools,
                mcpServers, effort, userInputHandler, onPermission: null, hooks, agentName, contextTier);
            var createdSession = await _copilotService.CreateSessionAsync(createConfig, sessionCt);
            chat.CopilotSessionId = createdSession.SessionId;
            _activeSession = createdSession;
            SubscribeToSession(createdSession, chat, workDir);
            if (mcpServers is { Count: > 0 })
                _ = CheckMcpServerStatusAsync(createdSession, chat.Id, mcpServers, ct);
            _dataStore.MarkChatChanged(chat);
            await SaveChatAsync(chat, saveIndex: true);
            return true;
        }
        catch (OperationCanceledException) when (sessionCts is not null && sessionCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException("MCP server connection timed out. Check that your MCP servers are installed and responding.");
        }
    }

    public async Task LoadChatAsync(Chat chat, CancellationToken cancellationToken = default)
    {
        var (requestId, loadCts) = BeginChatLoad(cancellationToken);
        var loadToken = loadCts.Token;
        var previousChat = CurrentChat?.Id != chat.Id ? CurrentChat : null;

        if (CurrentChat?.Id == chat.Id && chat.Messages.Count > 0)
        {
            try
            {
                await _dataStore.LoadChatMessagesAsync(chat, loadToken);

                if (loadToken.IsCancellationRequested || !IsCurrentChatLoad(requestId, loadCts))
                    return;

                _suggestionDisplayChatId = chat.Id;
                chat.HasUnreadMessages = false;
                SynchronizeDisplayedMessagesFromChat(chat, forceRebuild: true);
                RestoreSuggestionsForChat(chat);
                SweepInactiveChatStates();
            }
            finally
            {
                lock (_chatLoadSync)
                {
                    if (ReferenceEquals(_chatLoadCts, loadCts))
                    {
                        _chatLoadCts = null;
                        IsLoadingChat = false;
                    }
                }

                loadCts.Dispose();
            }

            return;
        }

        if (CurrentChat?.Id != chat.Id)
        {
            _suggestionDisplayChatId = chat.Id;

            // Save unsent composer draft for the chat we're leaving
            var leavingId = CurrentChat?.Id ?? Guid.Empty;
            if (!string.IsNullOrEmpty(PromptText))
                _chatDrafts[leavingId] = PromptText!;
            else
                _chatDrafts.Remove(leavingId);

            BrowserHideRequested?.Invoke();
            DiffHideRequested?.Invoke();
            ClearSuggestions();
        }

        IsLoadingChat = true;
        try
        {
            // Load messages from per-chat file if not already in memory
            await _dataStore.LoadChatMessagesAsync(chat, loadToken);

            if (loadToken.IsCancellationRequested || !IsCurrentChatLoad(requestId, loadCts))
                return;

            // Yield so the UI thread can render the loading overlay before heavy synchronous work
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

            // Reuse the cached session only while the current CLI connection can still talk to it.
            // Inactive chats are evicted separately, and AutoRestart can still leave stale session handles.
            _activeSession = await TryGetReusableCachedSessionAsync(chat, loadToken);

            // Clear pending state from any previous chat
            _pendingSkillInjections.Clear();
            _activeExternalSkillNames.Clear();
            _transcriptBuilder.PendingFetchedSkillRefs.Clear();

            // Restore real runtime state for this session/chat
            var runtime = GetOrCreateRuntimeState(chat.Id);
            ApplyKnownContextTokenLimit(chat, runtime, ResolveSelectedModelForChat(chat), updateDisplayed: false);
            IsBusy = runtime.IsBusy;
            IsStreaming = runtime.IsStreaming;
            StatusText = runtime.StatusText;
            TotalInputTokens = runtime.TotalInputTokens;
            TotalOutputTokens = runtime.TotalOutputTokens;
            ContextCurrentTokens = runtime.ContextCurrentTokens;
            ContextTokenLimit = runtime.ContextTokenLimit;
            // The browser toggle/panel follow the live BrowserService, which persists across chat
            // switches, so derive visibility from the service rather than the transient runtime flag.
            HasUsedBrowser = _chatBrowserServices.ContainsKey(chat.Id);

            _isBulkLoadingMessages = true;
            try
            {
                Messages.Clear();
                foreach (var msg in GetDisplayMessagesForChat(chat))
                    Messages.Add(new ChatMessageViewModel(msg));

                CurrentChat = chat;
                chat.HasUnreadMessages = false; // Clear unread when switching to this chat

                // Restore unsent composer draft for this chat
                PromptText = _chatDrafts.TryGetValue(chat.Id, out var draft) ? draft : "";
                RestoreSuggestionsForChat(chat);

                if (previousChat is not null)
                {
                    var previousRuntime = GetOrCreateRuntimeState(previousChat.Id);
                    if (!previousRuntime.IsBusy && !previousRuntime.IsStreaming)
                        QueueSaveChat(previousChat, saveIndex: false, releaseIfInactive: true);
                }

                // Release all non-active, non-busy runtime states that may have
                // accumulated (e.g. from chats the user left while they were streaming).
                SweepInactiveChatStates();

                // If this chat has an active browser, show its panel (after CurrentChat is set
                // so ActiveChatId is already updated when the MainWindow handler runs)
                if (_chatBrowserServices.ContainsKey(chat.Id))
                    BrowserShowRequested?.Invoke(chat.Id);

                // Rebuild transcript items from the fully loaded message list before
                // re-enabling live incremental transcript processing.
                RebuildTranscript();
            }
            finally
            {
                _isBulkLoadingMessages = false;
            }

            // Restore active skills from chat
            ActiveSkillIds.Clear();
            _activeExternalSkillNames.Clear();
            ActiveSkillChips.Clear();
            foreach (var skillId in chat.ActiveSkillIds)
                ActiveSkillIds.Add(skillId);
            foreach (var skillName in chat.ActiveExternalSkillNames)
                _activeExternalSkillNames.Add(skillName);
            RefreshActiveSkillChipsFromState();

            // Restore active MCP servers from chat (default to all enabled for older chats with no saved selection)
            ActiveMcpServerNames.Clear();
            ActiveMcpChips.Clear();
            var enabledServersByName = new Dictionary<string, McpServer>(StringComparer.Ordinal);
            foreach (var server in _dataStore.Data.McpServers)
            {
                if (server.IsEnabled && !enabledServersByName.ContainsKey(server.Name))
                    enabledServersByName[server.Name] = server;
            }

            // Restore project-scoped MCPs from the same context catalog used for sessions.
            var projectContextMcpNames = GetProjectContextCatalog(chat).McpServers
                .Select(server => server.Name)
                .ToList();

            if (chat.HasExplicitMcpServerSelection || chat.ActiveMcpServerNames.Count > 0)
            {
                foreach (var name in chat.ActiveMcpServerNames)
                {
                    if (enabledServersByName.ContainsKey(name))
                    {
                        ActiveMcpServerNames.Add(name);
                        ActiveMcpChips.Add(new StrataTheme.Controls.StrataComposerChip(name));
                    }
                    else if (projectContextMcpNames.Contains(name))
                    {
                        ActiveMcpServerNames.Add(name);
                        ActiveMcpChips.Add(new StrataTheme.Controls.StrataComposerChip(name, "🔌"));
                    }
                }
            }
            else
            {
                // Older chats did not store whether an empty list was intentional, so default them to all enabled.
                foreach (var server in enabledServersByName.Values)
                {
                    ActiveMcpServerNames.Add(server.Name);
                    ActiveMcpChips.Add(new StrataTheme.Controls.StrataComposerChip(server.Name));
                }
                // Also include project-context MCPs by default.
                foreach (var name in projectContextMcpNames)
                {
                    if (!ActiveMcpServerNames.Contains(name))
                    {
                        ActiveMcpServerNames.Add(name);
                        ActiveMcpChips.Add(new StrataTheme.Controls.StrataComposerChip(name, "🔌"));
                    }
                }
            }

            // Restore active agent from chat
            ActiveAgent = chat.AgentId.HasValue
                ? _dataStore.Data.Agents.FirstOrDefault(a => a.Id == chat.AgentId.Value)
                : null;

            // Restore SDK agent selection
            SelectedSdkAgentName = chat.SdkAgentName;

            // Restore per-chat model selection (falls back to global preferred model)
            ApplyModelSelection(
                chat.LastModelUsed ?? _dataStore.Data.Settings.PreferredModel,
                chat.LastReasoningEffortUsed ?? _dataStore.Data.Settings.ReasoningEffort,
                chat.LastContextWindowTierUsed ?? _dataStore.Data.Settings.ContextWindowTier);

            // Git status can be slow in large repos/worktrees. Do not keep the chat
            // loading overlay up after the transcript is already interactive.
            QueueRefreshCodingProjectState();

            // Refresh SDK agents if we have a session
            if (_activeSession is not null)
            {
                _ = PopulateFromSessionAsync();
                _ = RefreshPlanAsync(chat);
            }
            else if (!string.IsNullOrWhiteSpace(chat.PlanContent))
            {
                // Restore plan from persisted data (no active session, e.g. after restart)
                HasPlan = true;
                PlanContent = chat.PlanContent;
                _transcriptBuilder.AppendPlanCardToLastTurn("Plan", () => PlanShowRequested?.Invoke());
            }
            else
            {
                HasPlan = false;
                PlanContent = null;
            }

            // Refresh composer catalogs for the new chat's project context so workspace
            // MCPs, agents, and skills from .vscode/mcp.json and .github/ are available.
            RefreshComposerCatalogs();
        }
        catch (OperationCanceledException) when (loadToken.IsCancellationRequested)
        {
            // A newer chat selection or external cancellation superseded this load.
            if (IsCurrentChatLoad(requestId, loadCts))
                _suggestionDisplayChatId = CurrentChat?.Id;
        }
        finally
        {
            lock (_chatLoadSync)
            {
                if (ReferenceEquals(_chatLoadCts, loadCts))
                {
                    _chatLoadCts = null;
                    IsLoadingChat = false;
                }
            }
            loadCts.Dispose();
        }
    }

    /// <summary>Refreshes plan state for a chat when a session is available.</summary>
    private async Task RefreshPlanAsync(Chat chat)
    {
        if (_activeSession is null) return;
        try
        {
            var (exists, content) = await _copilotService.ReadSessionPlanAsync(_activeSession);
            HasPlan = exists;
            PlanContent = content;
            if (exists)
                _transcriptBuilder.AppendPlanCardToLastTurn("Plan", () => PlanShowRequested?.Invoke());
        }
        catch { /* best effort */ }
    }

    /// <summary>Stages a plan card for insertion at end of the current turn via TranscriptBuilder.</summary>
    private void StagePlanCard(string statusText)
    {
        _transcriptBuilder.SetPendingPlanCard(statusText, () => PlanShowRequested?.Invoke());
    }

    public void ClearChat()
    {
        lock (_chatLoadSync)
        {
            _chatLoadRequestId++;
            try { _chatLoadCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        // Save unsent composer draft for the chat we're leaving
        var leavingId = CurrentChat?.Id ?? Guid.Empty;
        if (!string.IsNullOrEmpty(PromptText))
            _chatDrafts[leavingId] = PromptText!;
        else
            _chatDrafts.Remove(leavingId);

        BrowserHideRequested?.Invoke();
        DiffHideRequested?.Invoke();
        PlanHideRequested?.Invoke();
        HasUsedBrowser = false;

        // Detach from the visible chat; inactive chat state is released later when it is safe.
        _activeSession = null;
        _suggestionDisplayChatId = null;
        ClearSuggestions();

        Messages.Clear();
        TranscriptTurns.Clear();
        _transcriptBuilder.ResetState();
        CurrentChat = null;
        QueueRefreshCodingProjectState();
        IsBusy = false;
        IsStreaming = false;
        TotalInputTokens = 0;
        TotalOutputTokens = 0;
        ContextCurrentTokens = 0;
        ContextTokenLimit = 0;
        ActiveSkillIds.Clear();
        ActiveSkillChips.Clear();
        ActiveMcpServerNames.Clear();
        ActiveMcpChips.Clear();
        PendingAttachments.Clear();
        PendingAttachmentItems.Clear();
        AvailableFileSuggestions = null;
        _fileSearchCts?.Cancel();
        _fileSearchCts?.Dispose();
        _fileSearchCts = null;
        PopulateDefaultMcps();
        _pendingProjectId = null;
        _pendingSkillInjections.Clear();
        _activeExternalSkillNames.Clear();
        StatusText = "";
        ActiveAgent = null;
        RestoreDefaultModelSelection();

        // Reset plan/SDK agent state
        HasPlan = false;
        PlanContent = null;
        IsPlanOpen = false;
        SelectedSdkAgentName = null;
        SdkAgentChips.Clear();

        // Restore unsent composer draft for the "new chat" state
        PromptText = _chatDrafts.TryGetValue(Guid.Empty, out var draft) ? draft : "";

        SyncComposerProjectSelectionFromState();
        RefreshProjectBadge();
    }

    /// <summary>
    /// Called when MCP server config changes so the next Copilot session create/resume uses the updated MCP catalog.
    /// </summary>
    public void InvalidateMcpSession()
    {
        if (CurrentChat is not null)
        {
            _pendingSkillInjections.Clear();
            _activeExternalSkillNames.Clear();
        }
    }

    /// <summary>
    /// Called when project settings change so the next message recreates the session
    /// with updated project instructions, context folders, file-based skills/agents, and MCPs.
    /// </summary>
    public void InvalidateProjectSession()
    {
        if (CurrentChat is not null)
        {
            InvalidateCurrentSession();
            _pendingSkillInjections.Clear();
        }
    }

    /// <summary>Discards the current chat's session so a fresh one is created on the next message.</summary>
    private void InvalidateCurrentSession()
    {
        if (CurrentChat is null) return;
        var chatId = CurrentChat.Id;

        CancelPendingQuestions(CurrentChat);
        ReleaseSessionResources(chatId, cancelActiveRequest: true, deleteServerSession: true);
        RemoveSuggestionTracking(chatId);
        CurrentChat.CopilotSessionId = null;
        _dataStore.MarkChatChanged(CurrentChat);
        _activeSession = null;
    }

    private bool ConsumePendingSessionInvalidation(Chat chat)
    {
        if (!_pendingSessionInvalidations.Remove(chat.Id))
            return false;

        if (CurrentChat?.Id == chat.Id)
        {
            InvalidateCurrentSession();
        }
        else if (!string.IsNullOrWhiteSpace(chat.CopilotSessionId))
        {
            CancelPendingQuestions(chat);
            ReleaseSessionResources(chat.Id, cancelActiveRequest: true, deleteServerSession: true);
            RemoveSuggestionTracking(chat.Id);
            chat.CopilotSessionId = null;
            _dataStore.MarkChatChanged(chat);
        }

        return true;
    }

    [RelayCommand]
    private async Task SelectSuggestion(string suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion)) return;
        PromptText = suggestion;
        await SendMessage();
    }

    public bool IsChatBusy(Guid chatId)
    {
        return OwnsLiveChat(chatId);
    }

    public async Task SendBackgroundJobMessageAsync(
        BackgroundJob job,
        string triggerContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var targetChat = _dataStore.Data.Chats.FirstOrDefault(chat => chat.Id == job.ChatId)
            ?? throw new InvalidOperationException($"Background job chat not found: {job.ChatId}");

        if (IsChatBusy(targetChat.Id))
            throw new InvalidOperationException($"Chat \"{targetChat.Title}\" is already running.");

        await _dataStore.LoadChatMessagesAsync(targetChat, cancellationToken);

        if (!_copilotService.IsConnected)
            await _copilotService.ConnectAsync(cancellationToken);

        var targetWorkDir = GetEffectiveWorkingDirectory(targetChat);
        var targetContextCatalog = GetProjectContextCatalog(targetChat, targetWorkDir);
        if (string.IsNullOrWhiteSpace(targetChat.LastModelUsed))
        {
            var targetModel = ResolveSelectedModelForChat(targetChat);
            if (!string.IsNullOrWhiteSpace(targetModel))
                targetChat.LastModelUsed = targetModel;
        }

        if (string.IsNullOrWhiteSpace(targetChat.LastReasoningEffortUsed))
        {
            var targetEffort = ResolvePersistedReasoningEffortForChat(targetChat, targetChat.LastModelUsed);
            if (!string.IsNullOrWhiteSpace(targetEffort))
                targetChat.LastReasoningEffortUsed = targetEffort;
        }

        if (string.IsNullOrWhiteSpace(targetChat.LastContextWindowTierUsed))
        {
            var targetTier = ResolveSelectedContextWindowTierForChat(targetChat, targetChat.LastModelUsed);
            if (!string.IsNullOrWhiteSpace(targetTier))
                targetChat.LastContextWindowTierUsed = targetTier;
        }

        var prompt = BuildBackgroundJobPrompt(job, triggerContext);
        var userMsg = new ChatMessage
        {
            Role = "user",
            Content = prompt,
            Author = $"Lumi Job - {job.Name}",
            ActiveSkills = BuildSkillReferences(targetChat.ActiveSkillIds, targetChat.ActiveExternalSkillNames, targetContextCatalog)
        };

        targetChat.Messages.Add(userMsg);
        if (CurrentChat?.Id == targetChat.Id)
        {
            Messages.Add(new ChatMessageViewModel(userMsg));
            ScrollToEndRequested?.Invoke();
        }
        else
        {
            targetChat.HasUnreadMessages = true;
        }

        QueueSaveChat(targetChat, saveIndex: true, touchIndex: true);
        ChatUpdated?.Invoke();

        CancellationTokenSource? cts = null;
        MessageOptions? sendOptions = null;
        CopilotSession? sendSession = null;
        var retainedContext = targetChat.Messages.Take(Math.Max(targetChat.Messages.Count - 1, 0)).ToList();
        var promptAdditions = BuildSendPromptAdditions(
            externalSkillNames: targetChat.ActiveExternalSkillNames,
            consumePendingSkillInjections: false,
            projectContextCatalog: targetContextCatalog);
        var localUserMessageCount = 0;
        var localAssistantMessageCount = 0;

        try
        {
            var chatId = targetChat.Id;
            if (ReleasePreviousTurnCancellation(chatId)
                && _sessionCache.TryGetValue(chatId, out var abortSession))
            {
                try { await abortSession.AbortAsync(); }
                catch { /* best-effort */ }
            }

            var runtime = GetOrCreateRuntimeState(chatId);
            MarkRuntimeActive(runtime, Loc.Status_Thinking);
            if (CurrentChat?.Id == chatId)
                ApplyDisplayedRuntimeState(runtime);

            var needsSessionSetup = targetChat.CopilotSessionId is null
                                    || !_sessionCache.TryGetValue(chatId, out var cachedSession)
                                    || cachedSession.SessionId != targetChat.CopilotSessionId;
            if (ConsumePendingSessionInvalidation(targetChat))
                needsSessionSetup = true;

            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ctsSources[chatId] = cts;

            var needsReplayPrompt = false;
            if (needsSessionSetup)
            {
                var previousSessionId = targetChat.CopilotSessionId;
                var ok = await EnsureSessionAsync(targetChat, cts.Token, allowCreateFallback: true);
                if (!ok)
                    throw new InvalidOperationException(Loc.Status_OriginalSessionUnavailable);

                needsReplayPrompt = ShouldReplayTranscriptAfterSessionReset(
                    chatWasCreatedThisTurn: false,
                    previousSessionId,
                    targetChat.CopilotSessionId,
                    retainedContext.Count);

                if (CurrentChat?.Id == chatId)
                    _ = PopulateFromSessionAsync();
                _ = RefreshQuotaAsync();
            }

            sendSession = _sessionCache.TryGetValue(chatId, out var sessionForChat)
                ? sessionForChat
                : _activeSession!;
            RestoreActiveSessionIfSwitched(targetChat);

            var basePrompt = needsReplayPrompt
                ? BuildSessionRecoveryReplayPrompt(retainedContext, prompt)
                : prompt;
            sendOptions = new MessageOptions { Prompt = basePrompt + promptAdditions };
            localUserMessageCount = targetChat.Messages.Count(static m => m.Role == "user");
            localAssistantMessageCount = CountCompletedAssistantMessages(targetChat);

            var expectedSessionUserMessageCount = await CaptureExpectedSessionUserMessageCountAsync(
                sendSession,
                localUserMessageCount,
                cts.Token);
            PreparePendingTurnTracking(targetChat, expectedSessionUserMessageCount, localAssistantMessageCount);
            await sendSession.SendAsync(sendOptions, cts.Token);
        }
        catch (Exception ex) when (IsSessionNotFoundError(ex) && cts is not null && sendOptions is not null)
        {
            try
            {
                DetachPersistedSession(targetChat);
                var ok = await EnsureSessionAsync(targetChat, cts.Token, allowCreateFallback: true);
                if (!ok)
                    throw new InvalidOperationException(Loc.Status_OriginalSessionUnavailable);

                sendSession = _sessionCache.TryGetValue(targetChat.Id, out var sessionForChat)
                    ? sessionForChat
                    : _activeSession!;
                RestoreActiveSessionIfSwitched(targetChat);
                sendOptions.Prompt = BuildSessionRecoveryReplayPrompt(retainedContext, prompt) + promptAdditions;
                var expectedSessionUserMessageCount = await CaptureExpectedSessionUserMessageCountAsync(
                    sendSession,
                    localUserMessageCount,
                    cts.Token);
                PreparePendingTurnTracking(targetChat, expectedSessionUserMessageCount, localAssistantMessageCount);
                await sendSession.SendAsync(sendOptions, cts.Token);
            }
            catch (Exception retryEx)
            {
                ClearPendingTurnTracking(targetChat.Id);
                HandleSendError(retryEx, cts.IsCancellationRequested, chat: targetChat);
                throw;
            }
        }
        catch (Exception ex) when (sendOptions is not null && IsCopilotTransportError(ex))
        {
            var recovery = await TryRecoverTransportSendAsync(targetChat, sendOptions);
            RestoreActiveSessionIfSwitched(targetChat);
            if (recovery.Recovered)
                return;

            ClearPendingTurnTracking(targetChat.Id);
            HandleSendError(ex, cts?.IsCancellationRequested == true, recovery.FailureMessage, chat: targetChat);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ClearPendingTurnTracking(targetChat.Id);
            var runtime = GetOrCreateRuntimeState(targetChat.Id);
            MarkRuntimeTerminal(runtime);
            if (CurrentChat?.Id == targetChat.Id)
            {
                StatusText = runtime.StatusText;
                IsBusy = false;
                IsStreaming = false;
                _transcriptBuilder.HideTypingIndicator();
                _transcriptBuilder.CloseCurrentToolGroup();
                _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
            }

            throw;
        }
        catch (OperationCanceledException) when (cts is not null && !cts.IsCancellationRequested)
        {
            ClearPendingTurnTracking(targetChat.Id);
            var errorText = string.Format(Loc.Status_Error, "Background job session cancelled unexpectedly.");
            var runtime = GetOrCreateRuntimeState(targetChat.Id);
            MarkRuntimeTerminal(runtime, errorText);
            if (CurrentChat?.Id == targetChat.Id)
            {
                StatusText = errorText;
                IsBusy = false;
                IsStreaming = false;
                _transcriptBuilder.HideTypingIndicator();
                _transcriptBuilder.CloseCurrentToolGroup();
                _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
            }
            throw;
        }
        catch (Exception ex) when (cts is not null)
        {
            ClearPendingTurnTracking(targetChat.Id);
            HandleSendError(ex, cts.IsCancellationRequested, chat: targetChat);
            throw;
        }
    }

    private static string BuildBackgroundJobPrompt(BackgroundJob job, string triggerContext)
    {
        var builder = new StringBuilder();
        builder.Append("Background job triggered: ")
            .Append(job.Name)
            .Append("\n\nJob instructions:\n")
            .Append(string.IsNullOrWhiteSpace(job.Prompt) ? job.Description : job.Prompt);

        if (!string.IsNullOrWhiteSpace(triggerContext))
        {
            builder.Append("\n\nTrigger context:\n")
                .Append(triggerContext.Trim());
        }

        builder.Append("\n\nRespond as Lumi in this chat. Be concise, explain what changed or what you found, and mention what you will keep watching if the job remains enabled.");
        return builder.ToString();
    }

    [RelayCommand]
    private async Task SendMessage()
    {
        await SendMessageCore(PromptText, consumeComposerPrompt: true);
    }

    private async Task SendMessageCore(string? promptText, bool consumeComposerPrompt)
    {
        if (string.IsNullOrWhiteSpace(promptText))
            return;

        var prompt = promptText.Trim();
        if (CurrentChat is { } activeChat && IsChatRuntimeActive(activeChat.Id))
        {
            QueueBusySendPrompt(activeChat.Id, prompt);
            if (consumeComposerPrompt)
            {
                PromptText = "";
                _chatDrafts.Remove(activeChat.Id);
            }

            return;
        }

        if (!_copilotService.IsConnected)
        {
            StatusText = Loc.Status_NotConnected;
            try { await _copilotService.ConnectAsync(); }
            catch
            {
                StatusText = Loc.Status_CheckAccess;
                if (!consumeComposerPrompt && CurrentChat is not null)
                {
                    _chatDrafts[CurrentChat.Id] = prompt;
                    if (string.IsNullOrWhiteSpace(PromptText))
                        PromptText = prompt;
                }

                return;
            }
        }

        if (consumeComposerPrompt)
        {
            PromptText = "";
            _chatDrafts.Remove(CurrentChat?.Id ?? Guid.Empty);
        }
        ClearSuggestions();
        var selectedReasoningEffort = GetPersistedReasoningEffortPreference();
        var selectedContextTier = GetSelectedContextWindowTier();

        // Expire any pending question cards — the user chose to type instead
        if (CurrentChat is not null)
            CancelPendingQuestions(CurrentChat);

        var attachments = TakePendingAttachments();
        var createdChat = false;

        // Create chat if needed
        var needsWorktreeCreation = false;
        if (CurrentChat is null)
        {
            var chat = new Chat
            {
                Title = prompt.Length > 40 ? prompt[..40].Trim() + "…" : prompt,
                AgentId = ActiveAgent?.Id,
                ProjectId = _pendingProjectId ?? ActiveProjectFilterId,
                ActiveSkillIds = new List<Guid>(ActiveSkillIds),
                ActiveExternalSkillNames = new List<string>(_activeExternalSkillNames),
                ActiveMcpServerNames = new List<string>(ActiveMcpServerNames),
                HasExplicitMcpServerSelection = true,
                SdkAgentName = SelectedSdkAgentName,
                WorktreePath = IsWorktreeMode ? WorktreePath : null,
                LastModelUsed = SelectedModel,
                LastReasoningEffortUsed = selectedReasoningEffort,
                LastContextWindowTierUsed = selectedContextTier
            };
            _pendingProjectId = null;
            _dataStore.Data.Chats.Add(chat);
            CurrentChat = chat;
            createdChat = true;
            needsWorktreeCreation = IsWorktreeMode && WorktreePath is null;
        }

        // Capture before any async operations — CurrentChat may change if the user switches chats
        var targetChat = CurrentChat!;
        ClearPersistedSuggestions(targetChat);
        targetChat.LastModelUsed = SelectedModel;
        targetChat.LastReasoningEffortUsed = selectedReasoningEffort;
        targetChat.LastContextWindowTierUsed = selectedContextTier;

        // Add user message immediately so it appears before async worktree creation
        var isSilentRetry = _silentRetryPrompt is not null && prompt == _silentRetryPrompt;
        _silentRetryPrompt = null;

        ChatMessage? userMsg = null;
        if (!isSilentRetry)
        {
            userMsg = new ChatMessage
            {
                Role = "user",
                Content = prompt,
                Author = _dataStore.Data.Settings.UserName ?? Loc.Author_You,
                Attachments = attachments?.OfType<AttachmentFile>().Select(a => a.Path).ToList() ?? [],
                ActiveSkills = BuildSkillReferences(ActiveSkillIds, _activeExternalSkillNames)
            };
            targetChat.Messages.Add(userMsg);
            Messages.Add(new ChatMessageViewModel(userMsg));
            QueueSaveChat(targetChat, saveIndex: true, touchIndex: true);
            ChatUpdated?.Invoke();
            UserMessageSent?.Invoke();
        }

        // Lazily create the worktree after the user message is visible.
        // The typing indicator shows "Creating worktree…" as a typewriter,
        // then transitions naturally to "Thinking…" when IsBusy is set.
        if (needsWorktreeCreation)
        {
            var projectDir = GetProjectWorkingDirectory();
            if (GitService.IsGitRepo(projectDir))
            {
                var runtime = GetOrCreateRuntimeState(targetChat.Id);
                MarkRuntimeActive(runtime, Loc.Status_CreatingWorktree);
                if (CurrentChat?.Id == targetChat.Id)
                    ApplyDisplayedRuntimeState(runtime);
                try
                {
                    var chatId = Guid.NewGuid().ToString("N")[..8];
                    var branchName = $"lumi/{chatId}";
                    var path = await GitService.CreateWorktreeAsync(projectDir, branchName);

                    if (path is not null)
                    {
                        WorktreePath = path;
                        targetChat.WorktreePath = path;

                        // Rebase attachment paths before persisting so the saved
                        // chat has the corrected worktree paths from the start.
                        if (attachments is { Count: > 0 } && userMsg is not null)
                            RebaseAttachmentPaths(attachments, userMsg, projectDir, path);

                        QueueSaveChat(targetChat, saveIndex: false);
                    }
                    else
                    {
                        IsWorktreeMode = false;
                    }
                }
                catch
                {
                    IsWorktreeMode = false;
                }
            }
            else
            {
                IsWorktreeMode = false;
            }
        }

        // Rebase attachment paths for existing worktrees (e.g. files dragged from the
        // project directory while an existing worktree is already selected).
        // New worktrees are handled inside the creation block above.
        if (!needsWorktreeCreation && WorktreePath is { Length: > 0 } wtPath && attachments is { Count: > 0 } && userMsg is not null)
        {
            var projDir = GetProjectWorkingDirectory();
            RebaseAttachmentPaths(attachments, userMsg, projDir, wtPath);
        }

        if (createdChat)
        {
            QueueRefreshCodingProjectState();
            QueueGeneratedChatTitle(targetChat, prompt);
        }

        CancellationTokenSource? cts = null;
        MessageOptions? sendOptions = null;
        CopilotSession? sendSession = null;
        var retainedContext = userMsg is null
            ? targetChat.Messages.ToList()
            : targetChat.Messages.Take(Math.Max(targetChat.Messages.Count - 1, 0)).ToList();
        var promptAdditions = BuildSendPromptAdditions();
        var localUserMessageCount = 0;
        var localAssistantMessageCount = 0;
        try
        {
            // Cancel any previous in-flight request for this chat
            var chatId = targetChat.Id;
            if (ReleasePreviousTurnCancellation(chatId))
            {
                // Abort the session so the SDK fully stops the old turn before
                // we send a new one. Without this, two concurrent SendAsync calls
                // end up on the same session, corrupting SDK state.
                if (_sessionCache.TryGetValue(chatId, out var cachedSession))
                {
                    try { await cachedSession.AbortAsync(); }
                    catch { /* best-effort */ }
                }
            }
            var runtime = GetOrCreateRuntimeState(targetChat.Id);
            MarkRuntimeActive(runtime, Loc.Status_Thinking);
            if (CurrentChat?.Id == targetChat.Id)
                ApplyDisplayedRuntimeState(runtime);

            var needsSessionSetup = _activeSession?.SessionId != targetChat.CopilotSessionId
                                    || targetChat.CopilotSessionId is null;
            if (ConsumePendingSessionInvalidation(targetChat))
                needsSessionSetup = true;

            // Deferred invalidation releases the current chat's session resources, including
            // any CTS tracked in _ctsSources. Create the new turn CTS only after that work.
            cts = new CancellationTokenSource();
            _ctsSources[chatId] = cts;

            var needsReplayPrompt = false;
            if (needsSessionSetup)
            {
                var previousSessionId = targetChat.CopilotSessionId;
                var ok = await EnsureSessionAsync(
                    targetChat,
                    cts.Token,
                    allowCreateFallback: true);
                if (!ok)
                {
                    HandleSendError(
                        new InvalidOperationException(Loc.Status_OriginalSessionUnavailable),
                        wasCancelledByUser: false,
                        overrideMessage: Loc.Status_OriginalSessionUnavailable,
                        chat: targetChat);
                    return;
                }

                needsReplayPrompt = ShouldReplayTranscriptAfterSessionReset(
                    createdChat,
                    previousSessionId,
                    targetChat.CopilotSessionId,
                    retainedContext.Count);

                // Agent is pre-selected via SessionConfig.Agent in EnsureSessionAsync.
                // File-based Copilot agents are handled via system prompt injection.

                // Discover SDK agents in background (non-blocking)
                _ = PopulateFromSessionAsync();
                // Refresh quota in background
                _ = RefreshQuotaAsync();
            }

            // Capture the session that was set up for targetChat.
            // EnsureSessionAsync sets _activeSession, but if the user switched away
            // during worktree creation, we must restore _activeSession to the displayed
            // chat's session so streaming events for THIS send don't pollute the UI.
            sendSession = _activeSession!;
            RestoreActiveSessionIfSwitched(targetChat);

            var basePrompt = needsReplayPrompt
                ? BuildSessionRecoveryReplayPrompt(retainedContext, prompt)
                : prompt;
            sendOptions = new MessageOptions { Prompt = basePrompt + promptAdditions };
            localUserMessageCount = targetChat.Messages.Count(m => m.Role == "user");
            localAssistantMessageCount = CountCompletedAssistantMessages(targetChat);

            if (attachments is { Count: > 0 })
                sendOptions.Attachments = attachments;

            var expectedSessionUserMessageCount = await CaptureExpectedSessionUserMessageCountAsync(
                sendSession,
                localUserMessageCount,
                cts.Token);
            PreparePendingTurnTracking(
                targetChat,
                expectedSessionUserMessageCount,
                localAssistantMessageCount);
            await sendSession.SendAsync(sendOptions, cts.Token);
        }
        catch (Exception ex) when (IsSessionNotFoundError(ex) && cts is not null && sendOptions is not null)
        {
            // Stale session cache — evict and resume
            try
            {
                StatusText = Loc.Status_Reconnecting;
                DetachPersistedSession(targetChat);
                var ok = await EnsureSessionAsync(targetChat, cts.Token, allowCreateFallback: true);
                if (!ok)
                {
                    ClearPendingTurnTracking(targetChat.Id);
                    HandleSendError(
                        new InvalidOperationException(Loc.Status_OriginalSessionUnavailable),
                        wasCancelledByUser: false,
                        overrideMessage: Loc.Status_OriginalSessionUnavailable,
                        chat: targetChat);
                    return;
                }
                sendSession = _activeSession!;
                RestoreActiveSessionIfSwitched(targetChat);
                sendOptions.Prompt = BuildSessionRecoveryReplayPrompt(retainedContext, prompt) + promptAdditions;
                var expectedSessionUserMessageCount = await CaptureExpectedSessionUserMessageCountAsync(
                    sendSession,
                    localUserMessageCount,
                    cts.Token);
                PreparePendingTurnTracking(
                    targetChat,
                    expectedSessionUserMessageCount,
                    localAssistantMessageCount);
                await sendSession.SendAsync(sendOptions, cts.Token);
            }
            catch (Exception retryEx)
            {
                if (IsCopilotTransportError(retryEx))
                {
                    var recovery = await TryRecoverTransportSendAsync(targetChat, sendOptions);
                    RestoreActiveSessionIfSwitched(targetChat);
                    if (recovery.Recovered)
                        return;

                    ClearPendingTurnTracking(targetChat.Id);
                    HandleSendError(retryEx, cts.IsCancellationRequested, recovery.FailureMessage, chat: targetChat);
                    return;
                }

                ClearPendingTurnTracking(targetChat.Id);
                HandleSendError(
                    retryEx,
                    cts.IsCancellationRequested,
                    IsCopilotTransportError(retryEx) ? Loc.Status_ConnectionRecoveryFailed : null,
                    chat: targetChat);
            }
        }
        catch (Exception ex) when (sendOptions is not null && IsCopilotTransportError(ex))
        {
            var recovery = await TryRecoverTransportSendAsync(targetChat, sendOptions);
            RestoreActiveSessionIfSwitched(targetChat);
            if (recovery.Recovered)
                    return;

            ClearPendingTurnTracking(targetChat.Id);
            HandleSendError(ex, cts?.IsCancellationRequested == true, recovery.FailureMessage, chat: targetChat);
        }
        catch (OperationCanceledException) when (cts is not null && !cts.IsCancellationRequested)
        {
            // SDK cancelled internally (e.g. MCP server failure) — surface as error
            var errorText = string.Format(Loc.Status_Error, "Session cancelled unexpectedly. MCP servers may have failed to connect.");
            var runtime = GetOrCreateRuntimeState(targetChat.Id);
            MarkRuntimeTerminal(runtime, errorText);
            ClearPendingTurnTracking(targetChat.Id);

            if (CurrentChat?.Id == targetChat.Id)
            {
                StatusText = errorText;
                IsBusy = false;
                IsStreaming = false;
                _transcriptBuilder.HideTypingIndicator();
                _transcriptBuilder.CloseCurrentToolGroup();
                _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by StopGeneration — expected, no error to surface
            ClearPendingTurnTracking(targetChat.Id);
        }
        catch (Exception ex) when (cts is not null)
        {
            ClearPendingTurnTracking(targetChat.Id);
            HandleSendError(ex, cts.IsCancellationRequested, chat: targetChat);
        }
    }

    private async Task<bool> TryReconnectCopilotAsync(CancellationToken ct)
    {
        try
        {
            await _copilotService.ForceReconnectAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Retries after a connection loss by sending "Try again" silently
    /// (no visible user message bubble) so the conversation continues seamlessly.</summary>
    private async Task RetryAfterConnectionLossAsync()
    {
        if (CurrentChat is null) return;

        if (!_copilotService.IsConnected)
        {
            try { await _copilotService.ConnectAsync(); }
            catch { StatusText = Loc.Status_CheckAccess; return; }
        }

        _silentRetryPrompt = "Try again";
        PromptText = _silentRetryPrompt;
        try
        {
            await SendMessage();
        }
        catch
        {
            _silentRetryPrompt = null;
        }
    }

    /// <summary>When set, SendMessage skips adding the user message bubble.</summary>
    private string? _silentRetryPrompt;

    private async Task<(CancellationTokenSource? TurnCts, string? FailureMessage)> TryRecoverTransportConnectionAsync(Chat chat)
    {
        try
        {
            using var reconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var runtime = GetOrCreateRuntimeState(chat.Id);
            MarkRuntimeActive(runtime, Loc.Status_Reconnecting);
            if (CurrentChat?.Id == chat.Id)
                ApplyDisplayedRuntimeState(runtime);

            if (!await TryReconnectCopilotAsync(reconnectCts.Token))
                return (null, Loc.Status_ConnectionRecoveryFailed);

            if (!await EnsureSessionAsync(chat, reconnectCts.Token, allowCreateFallback: false))
                return (null, Loc.Status_OriginalSessionUnavailable);

            var recoveredTurnCts = new CancellationTokenSource();
            _ctsSources[chat.Id] = recoveredTurnCts;
            return (recoveredTurnCts, null);
        }
        catch
        {
            return (null, Loc.Status_ConnectionRecoveryFailed);
        }
    }

    private async Task<(bool Recovered, string? FailureMessage)> TryRecoverTransportSendAsync(
        Chat chat,
        MessageOptions sendOptions)
    {
        var pendingRuntime = GetOrCreateRuntimeState(chat.Id);
        int pendingSessionUserMessageCount;
        int pendingAssistantCount;
        lock (pendingRuntime)
        {
            pendingSessionUserMessageCount = pendingRuntime.PendingSessionUserMessageCount;
            pendingAssistantCount = pendingRuntime.PendingAssistantMessageCount;
        }

        var (recoveredTurnCts, failureMessage) = await TryRecoverTransportConnectionAsync(chat);
        // Use the session from cache — _activeSession may have been restored to the displayed chat
        if (recoveredTurnCts is null || !_sessionCache.TryGetValue(chat.Id, out var recoveredSession))
            return (false, failureMessage ?? Loc.Status_ConnectionRecoveryFailed);
        RestoreActiveSessionIfSwitched(chat);

        var recoveredAnalysis = await AnalyzePendingTurnRecoveryAsync(
            recoveredSession,
            pendingSessionUserMessageCount,
            recoveredTurnCts.Token);
        if (!recoveredAnalysis.UserMessageObserved)
        {
            MarkRuntimeActive(pendingRuntime, Loc.Status_ConnectionRecoveredRetry);
            if (CurrentChat?.Id == chat.Id)
                ApplyDisplayedRuntimeState(pendingRuntime);

            var expectedSessionUserMessageCount = await CaptureExpectedSessionUserMessageCountAsync(
                recoveredSession,
                pendingSessionUserMessageCount,
                recoveredTurnCts.Token);
            SetPendingSessionUserMessageCount(chat.Id, expectedSessionUserMessageCount);
            await recoveredSession.SendAsync(sendOptions.Clone(), recoveredTurnCts.Token);
            return (true, null);
        }

        if (await ApplyRecoveredTurnStateAsync(chat, recoveredAnalysis))
        {
            return (true, null);
        }

        if (CountCompletedAssistantMessages(chat) > pendingAssistantCount)
            return (true, null);

        var recoveredByWaiting = await WaitForRecoveredTurnAsync(
            recoveredSession,
            chat,
            pendingSessionUserMessageCount,
            pendingAssistantCount,
            recoveredTurnCts.Token);
        return (recoveredByWaiting, recoveredByWaiting ? null : Loc.Status_ConnectionRecoveryFailed);
    }

    /// <summary>If the user switched away from <paramref name="sendChat"/>, restores
    /// <see cref="_activeSession"/> to the displayed chat's cached session so streaming
    /// events for the background send don't pollute the visible UI.</summary>
    private void RestoreActiveSessionIfSwitched(Chat sendChat)
    {
        if (CurrentChat?.Id == sendChat.Id)
            return;
        if (CurrentChat is not null && _sessionCache.TryGetValue(CurrentChat.Id, out var displayedSession))
            _activeSession = displayedSession;
        else
            _activeSession = null;
    }

    private static int CountCompletedAssistantMessages(Chat chat)
        => chat.Messages.Count(static m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content));

    private async Task<IReadOnlyList<SessionEvent>?> TryGetSessionEventsAsync(CopilotSession session, CancellationToken ct)
    {
        try
        {
            return await session.GetEventsAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    private async Task<PendingTurnRecoveryAnalysis> AnalyzePendingTurnRecoveryAsync(
        CopilotSession session,
        int expectedSessionUserMessageCount,
        CancellationToken ct)
    {
        PendingTurnRecoveryAnalysis? liveAnalysis = null;
        var liveEvents = await TryGetSessionEventsAsync(session, ct);
        if (liveEvents is not null)
            liveAnalysis = PendingTurnRecoveryAnalyzer.Analyze(liveEvents, expectedSessionUserMessageCount);

        var persistedAnalysis = await PendingTurnRecoveryAnalyzer.TryAnalyzeSessionLogAsync(
            session.SessionId,
            expectedSessionUserMessageCount,
            ct);

        return PendingTurnRecoveryAnalyzer.Merge(liveAnalysis, persistedAnalysis);
    }

    private async Task<int> CaptureExpectedSessionUserMessageCountAsync(
        CopilotSession session,
        int fallbackExpectedSessionUserMessageCount,
        CancellationToken ct)
    {
        var observedSessionUserMessageCount = 0;
        var foundObservedCount = false;

        var persistedUserMessageCount = await PendingTurnRecoveryAnalyzer.TryCountSessionUserMessagesAsync(
            session.SessionId,
            ct);
        if (persistedUserMessageCount.HasValue)
        {
            observedSessionUserMessageCount = persistedUserMessageCount.Value;
            foundObservedCount = true;
        }

        var liveEvents = await TryGetSessionEventsAsync(session, ct);
        if (liveEvents is not null)
        {
            var liveUserMessageCount = PendingTurnRecoveryAnalyzer.CountUserMessages(liveEvents);
            if (!foundObservedCount || liveUserMessageCount > observedSessionUserMessageCount)
            {
                observedSessionUserMessageCount = liveUserMessageCount;
                foundObservedCount = true;
            }
        }

        return foundObservedCount
            ? observedSessionUserMessageCount + 1
            : Math.Max(1, fallbackExpectedSessionUserMessageCount);
    }

    private bool SyncRecoveredAssistantMessages(Chat chat, IReadOnlyList<RecoveredAssistantMessage> recoveredAssistantMessages)
    {
        if (recoveredAssistantMessages.Count == 0)
            return false;

        var author = chat.AgentId.HasValue
            ? _dataStore.Data.Agents.FirstOrDefault(agent => agent.Id == chat.AgentId.Value)?.Name ?? Loc.Author_Lumi
            : Loc.Author_Lumi;
        foreach (var assistantMessage in recoveredAssistantMessages)
        {
            var recoveredMessage = new ChatMessage
            {
                Role = "assistant",
                Author = author,
                Content = assistantMessage.Content,
                IsStreaming = false,
                Model = ResolveSelectedModelForChat(chat)
            };
            chat.Messages.Add(recoveredMessage);

            if (CurrentChat?.Id == chat.Id)
                Messages.Add(new ChatMessageViewModel(recoveredMessage));
        }

        var runtime = GetOrCreateRuntimeState(chat.Id);
        MarkRuntimeTerminal(runtime);
        if (CurrentChat?.Id == chat.Id)
        {
            ApplyDisplayedRuntimeState(runtime);
            ScrollToEndRequested?.Invoke();
        }

        QueueSaveChat(chat, saveIndex: true, touchIndex: true);
        return true;
    }

    private async Task<bool> WaitForRecoveredTurnAsync(
        CopilotSession session,
        Chat chat,
        int expectedSessionUserMessageCount,
        int assistantCountBeforeRecovery,
        CancellationToken ct)
    {
        var sawRecoveredTurnActivity = false;
        var turnActivity = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = session.On<SessionEvent>(evt =>
        {
            switch (evt)
            {
                case AssistantTurnStartEvent:
                case AssistantReasoningEvent:
                case AssistantReasoningDeltaEvent:
                case AssistantMessageDeltaEvent:
                case AssistantMessageEvent:
                case ToolExecutionStartEvent:
                case ToolExecutionPartialResultEvent:
                case ToolExecutionProgressEvent:
                case ToolExecutionCompleteEvent:
                case AssistantTurnEndEvent:
                    sawRecoveredTurnActivity = true;
                    turnActivity.TrySetResult(true);
                    break;
                case SessionIdleEvent:
                    turnActivity.TrySetResult(true);
                    break;
                case SessionErrorEvent err:
                    turnActivity.TrySetException(new InvalidOperationException(err.Data.Message));
                    break;
            }
        });

        try
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(TimeSpan.FromSeconds(8));
            await turnActivity.Task.WaitAsync(waitCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }

        var recoveredAnalysis = await AnalyzePendingTurnRecoveryAsync(
            session,
            expectedSessionUserMessageCount,
            ct);
        if (await ApplyRecoveredTurnStateAsync(chat, recoveredAnalysis))
            return true;

        return sawRecoveredTurnActivity || CountCompletedAssistantMessages(chat) > assistantCountBeforeRecovery;
    }

    private bool WasCancelledByUser(Guid? chatId)
        => chatId.HasValue && _ctsSources.GetValueOrDefault(chatId.Value)?.IsCancellationRequested == true;

    /// <summary>Returns a cached session only when it is still usable on the current CLI connection.</summary>
    private async Task<CopilotSession?> TryGetReusableCachedSessionAsync(Chat chat, CancellationToken ct)
    {
        if (!_sessionCache.TryGetValue(chat.Id, out var cachedSession))
            return null;

        if (!await _copilotService.IsHealthyAsync(TimeSpan.FromSeconds(2)))
        {
            InvalidateLocalSessionCache(chat);
            return null;
        }

        try
        {
            await _copilotService.ReadSessionPlanAsync(cachedSession, ct);
            return cachedSession;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsSessionNotFoundError(ex))
        {
            InvalidateLocalSessionCache(chat);
            return null;
        }
        catch (Exception ex) when (IsCopilotTransportError(ex))
        {
            InvalidateLocalSessionCache(chat);
            return null;
        }
        catch
        {
            // Non-session-specific plan RPC failures should not discard a healthy cached session.
            return cachedSession;
        }
    }

    /// <summary>Detects a stale cached session (the session ID is unknown to the current CLI process).</summary>
    private static bool IsSessionNotFoundError(Exception ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message;
        return msg.Contains("Session not found", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCopilotTransportError(Exception ex)
    {
        var message = FlattenExceptionMessages(ex);
        return message.Contains("JSON-RPC", StringComparison.OrdinalIgnoreCase)
               || message.Contains("remote party was lost", StringComparison.OrdinalIgnoreCase)
               || message.Contains("connection lost", StringComparison.OrdinalIgnoreCase)
               || message.Contains("broken pipe", StringComparison.OrdinalIgnoreCase)
               || message.Contains("pipe is being closed", StringComparison.OrdinalIgnoreCase)
               || message.Contains("stream closed", StringComparison.OrdinalIgnoreCase)
               || message.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
               || message.Contains("connection aborted", StringComparison.OrdinalIgnoreCase)
               || message.Contains("transport connection", StringComparison.OrdinalIgnoreCase);
    }

    private static string FlattenExceptionMessages(Exception ex)
    {
        var builder = new StringBuilder();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (builder.Length > 0)
                builder.Append(" → ");

            builder.Append(current.Message);
        }

        return builder.ToString();
    }

    /// <summary>Evicts a stale session from the local cache so EnsureSessionAsync will
    /// re-establish it via ResumeSessionAsync, preserving server-side context.</summary>
    private void InvalidateLocalSessionCache(Chat chat)
    {
        _sessionCache.Remove(chat.Id);
        if (_sessionSubs.TryGetValue(chat.Id, out var sub))
        {
            sub.Dispose();
            _sessionSubs.Remove(chat.Id);
        }

        if (CurrentChat?.Id == chat.Id
            || string.Equals(_activeSession?.SessionId, chat.CopilotSessionId, StringComparison.Ordinal))
        {
            _activeSession = null;
        }
    }

    private void DetachPersistedSession(Chat chat, string? sessionId = null)
    {
        var detachedSessionId = sessionId ?? chat.CopilotSessionId;
        DisposeSessionSubscription(chat.Id);
        _sessionCache.Remove(chat.Id);
        if (!string.IsNullOrWhiteSpace(detachedSessionId)
            && string.Equals(_activeSession?.SessionId, detachedSessionId, StringComparison.Ordinal))
        {
            _activeSession = null;
        }

        chat.CopilotSessionId = null;
        _dataStore.MarkChatChanged(chat);
    }

    private string BuildSendPromptAdditions(
        IReadOnlyCollection<string>? externalSkillNames = null,
        bool consumePendingSkillInjections = true,
        ProjectContextCatalogSnapshot? projectContextCatalog = null)
    {
        var builder = new StringBuilder();
        var hasActivatedSkillsSection = false;

        void AppendActivatedSkillsHeader()
        {
            if (hasActivatedSkillsSection)
                return;

            builder.Append("\n\n--- Activated Skills (apply these to help with the request) ---\n");
            hasActivatedSkillsSection = true;
        }

        if (consumePendingSkillInjections && _pendingSkillInjections.Count > 0)
        {
            var injectedSkills = ResolveSkillsByIds(_pendingSkillInjections);
            _pendingSkillInjections.Clear();

            if (injectedSkills.Count > 0)
            {
                AppendActivatedSkillsHeader();
                foreach (var skill in injectedSkills)
                {
                    builder.Append("\n### ")
                        .Append(skill.Name)
                        .Append('\n')
                        .Append(skill.Content)
                        .Append('\n');
                }
            }
        }

        var externalNames = externalSkillNames ?? _activeExternalSkillNames;
        if (externalNames.Count > 0)
        {
            var externalSkills = ResolveExternalSkills(
                projectContextCatalog ?? GetProjectContextCatalog(),
                externalNames);

            if (externalSkills.Count > 0)
            {
                AppendActivatedSkillsHeader();
                foreach (var skill in externalSkills)
                {
                    builder.Append("\n### ")
                        .Append(skill.Name)
                        .Append('\n')
                        .Append(skill.Content)
                        .Append('\n');
                }
            }
        }

        return builder.ToString();
    }

    private static bool ShouldReplayTranscriptAfterSessionReset(
        bool chatWasCreatedThisTurn,
        string? previousSessionId,
        string? currentSessionId,
        int retainedContextCount)
    {
        if (chatWasCreatedThisTurn || retainedContextCount == 0)
            return false;

        return string.IsNullOrWhiteSpace(previousSessionId)
               || !string.Equals(previousSessionId, currentSessionId, StringComparison.Ordinal);
    }

    /// <summary>Handles a send error by surfacing it as a status + error message in the transcript.</summary>
    private void HandleSendError(Exception ex, bool wasCancelledByUser, string? overrideMessage = null, Chat? chat = null)
    {
        if (ex is OperationCanceledException && wasCancelledByUser)
            return; // Cancelled by StopGeneration — expected

        chat ??= CurrentChat;

        if (chat is not null)
            ClearPendingTurnTracking(chat.Id);

        // A PROVABLY transient backend-internal failure (twirp/usersd "failed to do request", or a
        // 5xx "unavailable") is not a logout: the credential is still valid and a resend recovers.
        // Surface it as a one-click-retryable failure (same affordance as a connection loss) instead
        // of a terminal error. A bare/ambiguous 401/403 is NOT matched here, so a genuine logout
        // falls through to the normal error path below and the user can re-authenticate.
        if (overrideMessage is null && chat is not null
            && CopilotService.IsTransientServerAuthError(FlattenExceptionMessages(ex)))
        {
            ApplyUnexpectedAbortState(chat, Loc.Status_TransientAuthRetry);
            return;
        }

        var message = overrideMessage ?? FlattenExceptionMessages(ex);
        var errorText = string.Format(Loc.Status_Error, message);

        if (chat is not null)
        {
            var runtime = GetOrCreateRuntimeState(chat.Id);
            MarkRuntimeTerminal(runtime, errorText);

            var errorMsg = new ChatMessage
            {
                Role = "error",
                Author = Loc.Author_Lumi,
                Content = errorText
            };
            chat.Messages.Add(errorMsg);

            // Only update view-level state if this chat is still displayed
            if (CurrentChat?.Id == chat.Id)
            {
                StatusText = errorText;
                IsBusy = false;
                IsStreaming = false;
                _transcriptBuilder.HideTypingIndicator();
                _transcriptBuilder.CloseCurrentToolGroup();
                _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
                var msgVm = new ChatMessageViewModel(errorMsg);
                Messages.Add(msgVm);
                ScrollToEndRequested?.Invoke();
            }
        }
        else
        {
            StatusText = errorText;
            IsBusy = false;
            IsStreaming = false;
            _transcriptBuilder.HideTypingIndicator();
            _transcriptBuilder.CloseCurrentToolGroup();
            _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
        }
    }

    [RelayCommand]
    private async Task StopGeneration()
    {
        if (CurrentChat is null) return;

        var chat = CurrentChat;
        var chatId = chat.Id;
        SetManualStopRequested(chatId, true);
        ReleaseChatCancellation(chatId, cancel: true);

        // Get the session for this specific chat (not _activeSession which may differ)
        if (_sessionCache.TryGetValue(chatId, out var session))
        {
            try { await session.AbortAsync(); }
            catch { /* Best-effort abort */ }
        }

        var runtime = GetOrCreateRuntimeState(chatId);
        var stoppedTools = MarkInProgressToolsStopped(chat);
        MarkRuntimeTerminal(runtime, Loc.Status_Stopped);
        ClearPendingTurnTracking(chatId);

        // Only update UI properties if this is still the displayed chat
        if (CurrentChat?.Id == chatId)
        {
            IsBusy = false;
            IsStreaming = false;
            StatusText = Loc.Status_Stopped;
            _transcriptBuilder.HideTypingIndicator();
            _transcriptBuilder.CloseCurrentToolGroup();
            _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
        }

        if (stoppedTools)
            QueueSaveChat(chat, saveIndex: false);

        await DrainQueuedBusySendAsync(chatId);
    }

    private async Task SaveCurrentChatAsync(bool saveIndex = true, bool touchIndex = false)
    {
        if (CurrentChat is null) return;
        if (touchIndex)
            CurrentChat.UpdatedAt = DateTimeOffset.Now;
        _dataStore.MarkChatChanged(CurrentChat);
        await SaveChatAsync(CurrentChat, saveIndex);
    }

    private void QueueSaveChat(Chat chat, bool saveIndex, bool releaseIfInactive = false, bool touchIndex = false)
    {
        if (touchIndex)
            chat.UpdatedAt = DateTimeOffset.Now;
        _dataStore.MarkChatChanged(chat);
        _ = SaveChatAsync(chat, saveIndex, releaseIfInactive);
    }

    private void QueueSaveChatIndex(Chat chat)
    {
        if (!_dataStore.Data.Settings.AutoSaveChats)
            return;

        _dataStore.MarkChatChanged(chat);
        _ = SaveIndexAsync();
    }

    private void QueueGeneratedChatTitle(Chat chat, string firstUserMessage)
    {
        if (!_dataStore.Data.Settings.AutoGenerateTitles || string.IsNullOrWhiteSpace(firstUserMessage))
            return;

        var provisionalTitle = chat.Title;
        _ = GenerateTitleForChatAsync(chat, firstUserMessage, provisionalTitle);
    }

    private async Task GenerateTitleForChatAsync(Chat chat, string firstUserMessage, string? expectedCurrentTitle)
    {
        try
        {
            var generatedTitle = await _copilotService.GenerateTitleAsync(firstUserMessage).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(generatedTitle))
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (!_dataStore.Data.Settings.AutoGenerateTitles)
                    return;

                if (!_dataStore.Data.Chats.Any(c => c.Id == chat.Id))
                    return;

                ApplyChatTitle(chat, generatedTitle, expectedCurrentTitle);
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Lumi] Title generation failed: {ex.Message}");
        }
    }

    private void ApplyChatTitle(Chat chat, string? title, string? expectedCurrentTitle = null)
    {
        var normalizedTitle = NormalizeChatTitle(title);
        if (normalizedTitle is null)
            return;

        if (expectedCurrentTitle is not null
            && !string.Equals(chat.Title, expectedCurrentTitle, StringComparison.Ordinal))
            return;

        if (string.Equals(chat.Title, normalizedTitle, StringComparison.Ordinal))
            return;

        chat.Title = normalizedTitle;
        _dataStore.MarkChatChanged(chat);
        if (HasPersistedChatFile(chat) && _dataStore.Data.Settings.AutoSaveChats)
            _ = SaveIndexAsync();
        ChatTitleChanged?.Invoke(chat.Id, chat.Title);
    }

    private static string? NormalizeChatTitle(string? title)
    {
        var normalized = title?.Trim().Trim('"', '\'', '.', '!');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private void QueueAutonomousMemoryCheckpoint(Chat chat)
    {
        if (!_dataStore.Data.Settings.EnableMemoryAutoSave)
            return;

        var checkpoint = CreateMemoryCheckpoint(chat);
        if (checkpoint is null)
            return;

        _ = _memoryAgentService.ProcessCheckpointAsync(checkpoint);
    }

    internal void QueueChatCompletionFollowUps(Chat chat)
    {
        QueueAutonomousMemoryCheckpoint(chat);
        QueueSuggestionGenerationForLatestAssistant(chat);
    }

    private MemoryAgentCheckpoint? CreateMemoryCheckpoint(Chat chat)
    {
        var assistantIndex = -1;
        for (var i = chat.Messages.Count - 1; i >= 0; i--)
        {
            var message = chat.Messages[i];
            if (message.Role == "assistant" && !string.IsNullOrWhiteSpace(message.Content))
            {
                assistantIndex = i;
                break;
            }
        }

        if (assistantIndex <= 0)
            return null;

        var userIndex = -1;
        for (var i = assistantIndex - 1; i >= 0; i--)
        {
            var message = chat.Messages[i];
            if (message.Role == "user" && !string.IsNullOrWhiteSpace(message.Content))
            {
                userIndex = i;
                break;
            }
        }

        if (userIndex < 0)
            return null;

        var userMessage = chat.Messages[userIndex];
        var assistantMessage = chat.Messages[assistantIndex];
        var recentConversation = chat.Messages
            .Where(m => (m.Role == "user" || m.Role == "assistant") && !string.IsNullOrWhiteSpace(m.Content))
            .TakeLast(8)
            .Select(m => new MemoryAgentConversationItem
            {
                Role = m.Role,
                Content = m.Content
            })
            .ToList();

        var project = chat.ProjectId.HasValue
            ? _dataStore.Data.Projects.FirstOrDefault(p => p.Id == chat.ProjectId.Value)
            : null;

        var memories = _dataStore.Data.Memories
            .Where(m => string.Equals(m.Status, MemoryStatuses.Active, StringComparison.OrdinalIgnoreCase))
            .Where(m =>
            {
                var scope = MemoryAgentService.NormalizeScope(m.Scope, m.ProjectId);
                return scope == MemoryScopes.Global || (chat.ProjectId.HasValue && m.ProjectId == chat.ProjectId.Value);
            })
            .Where(m => MemoryAgentService.EvaluateMemoryCandidate(
                m.Key,
                m.Content,
                m.Category,
                m.Scope).ShouldSave)
            .Select(m => new MemoryAgentSnapshot
            {
                Key = m.Key,
                Content = m.Content,
                Category = m.Category,
                Scope = m.Scope,
                ProjectId = m.ProjectId
            })
            .ToList();

        return new MemoryAgentCheckpoint
        {
            ChatId = chat.Id,
            InteractionSignature = $"{userMessage.Id:N}:{assistantMessage.Id:N}",
            UserMessage = userMessage.Content,
            AssistantMessage = assistantMessage.Content,
            UserName = _dataStore.Data.Settings.UserName,
            ProjectId = project?.Id,
            ProjectName = project?.Name,
            ExistingMemories = memories,
            RecentConversation = recentConversation
        };
    }

    private void QueueSuggestionGenerationForLatestAssistant(Chat chat)
    {
        if (_suggestionGenerationInFlightChats.Contains(chat.Id))
            return;

        var lastAssistant = GetLatestSuggestionEligibleAssistantMessage(chat);
        if (lastAssistant is null)
            return;

        var lastSuggestedId = chat.FollowUpSuggestionAssistantMessageId;
        if ((!lastSuggestedId.HasValue
                && _lastSuggestedAssistantMessageByChat.TryGetValue(chat.Id, out var trackedSuggestedId)
                && trackedSuggestedId == lastAssistant.Id)
            || lastSuggestedId == lastAssistant.Id)
        {
            return;
        }

        var context = CreateSuggestionGenerationContext(chat, lastAssistant.Id);
        if (context is null)
            return;

        _suggestionGenerationInFlightChats.Add(chat.Id);
        _ = GenerateSuggestionsAsync(chat, lastAssistant.Id, context);
    }

    private async Task GenerateSuggestionsAsync(
        Chat chat,
        Guid assistantMessageId,
        SuggestionGenerationContext context)
    {
        var suggestionsApplied = false;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (CanUpdateDisplayedSuggestions(chat))
                    IsSuggestionsGenerating = true;
            });

            var userHistorySummary = await BuildSuggestionHistorySummaryAsync(
                context.LatestUserMessageId,
                context.LoadedMessageSnapshots);

            var suggestions = await _copilotService.GenerateSuggestionsAsync(
                context.AssistantMessage,
                context.LatestUserMessage,
                userHistorySummary);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // If another assistant message arrived, don't overwrite with stale suggestions.
                var latestAssistantId = GetLatestSuggestionEligibleAssistantMessageId(chat);
                if (chat.Messages.Count > 0 && latestAssistantId != assistantMessageId)
                    return;

                var normalizedSuggestions = NormalizeFollowUpSuggestions(suggestions);
                StoreGeneratedSuggestions(chat, assistantMessageId, normalizedSuggestions);
                TryApplyDisplayedSuggestions(chat, normalizedSuggestions);

                suggestionsApplied = true;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Lumi] Suggestion generation failed: {ex.Message}");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (CanUpdateDisplayedSuggestions(chat))
                    IsSuggestionsGenerating = false;

                _suggestionGenerationInFlightChats.Remove(chat.Id);
                if (suggestionsApplied)
                    _lastSuggestedAssistantMessageByChat[chat.Id] = assistantMessageId;
            });
        }
    }

    private SuggestionGenerationContext? CreateSuggestionGenerationContext(
        Chat chat,
        Guid assistantMessageId)
    {
        // Resolve the specific assistant message that completed on idle.
        var assistantIndex = chat.Messages.FindIndex(m => m.Id == assistantMessageId);
        if (assistantIndex < 0)
            return null;

        var assistantMessage = chat.Messages[assistantIndex];
        if (assistantMessage.Role != "assistant" || string.IsNullOrWhiteSpace(assistantMessage.Content))
            return null;

        // Use the user message that led to this assistant reply for tighter context.
        var lastUser = chat.Messages
            .Take(assistantIndex)
            .LastOrDefault(m => m.Role == "user" && !string.IsNullOrWhiteSpace(m.Content));

        var loadedMessageSnapshots = _dataStore.Data.Chats
            .Where(static item => item.Messages.Count > 0)
            .ToDictionary(
                static item => item.Id,
                static item => (IReadOnlyList<ChatMessage>)item.Messages.ToList());

        return new SuggestionGenerationContext(
            assistantMessage.Content,
            lastUser?.Content,
            lastUser?.Id,
            loadedMessageSnapshots);
    }

    private async Task<string?> BuildSuggestionHistorySummaryAsync(
        Guid? latestUserMessageId,
        IReadOnlyDictionary<Guid, IReadOnlyList<ChatMessage>> loadedMessageSnapshots)
    {
        var userPromptHistory = await _dataStore.GetUserPromptHistoryAsync(
            SuggestionHistoryScanLimit,
            loadedMessageSnapshots);

        var historyItems = userPromptHistory
            .Where(item => item.MessageId != latestUserMessageId);

        return FormatSuggestionHistorySummary(historyItems, SuggestionHistorySummaryMaxItems);
    }

    private static string? FormatSuggestionHistorySummary(
        IEnumerable<UserPromptHistoryItem> historyItems,
        int maxItems)
    {
        ArgumentNullException.ThrowIfNull(historyItems);
        if (maxItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxItems), "History item limit must be greater than zero.");

        var groups = historyItems
            .Select(static item => new
            {
                Key = NormalizeSuggestionHistoryContent(item.Content),
                Content = TrimSuggestionHistoryContent(item.Content),
                item.Timestamp
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.Key)
                                  && !string.IsNullOrWhiteSpace(item.Content))
            .GroupBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static group =>
            {
                var latest = group.OrderByDescending(static item => item.Timestamp).First();
                return new SuggestionHistoryGroup(
                    group.Key,
                    latest.Content,
                    group.Count(),
                    group.Max(static item => item.Timestamp));
            })
            .OrderByDescending(static group => group.LastUsedAt)
            .ToList();

        if (groups.Count == 0)
            return null;

        var frequentGroups = groups
            .Where(static group => group.Count > 1)
            .OrderByDescending(static group => group.Count)
            .ThenByDescending(static group => group.LastUsedAt)
            .Take(Math.Min(8, maxItems))
            .ToList();

        var frequentKeys = frequentGroups
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        var recentGroups = groups
            .Where(group => !frequentKeys.Contains(group.Key))
            .OrderByDescending(static group => group.LastUsedAt)
            .Take(Math.Max(0, maxItems - frequentGroups.Count))
            .ToList();

        var summary = new StringBuilder();
        if (frequentGroups.Count > 0)
        {
            summary.AppendLine("Frequent user requests:");
            foreach (var group in frequentGroups)
                summary.AppendLine($"- {group.Content} (used {group.Count}x)");
        }

        if (recentGroups.Count > 0)
        {
            if (summary.Length > 0)
                summary.AppendLine();

            summary.AppendLine("Recent user requests:");
            foreach (var group in recentGroups)
                summary.AppendLine($"- {group.Content}");
        }

        return summary.Length > 0 ? summary.ToString().TrimEnd() : null;
    }

    private static string CollapseSuggestionHistoryWhitespace(string content)
        => Regex.Replace(content.Trim(), @"\s+", " ");

    private static string NormalizeSuggestionHistoryContent(string content)
        => CollapseSuggestionHistoryWhitespace(content)
            .Trim(' ', '.', '!', '?', ':', ';', '"', '\'')
            .ToLowerInvariant();

    private static string TrimSuggestionHistoryContent(string content)
    {
        var singleLine = CollapseSuggestionHistoryWhitespace(content);
        return singleLine.Length <= SuggestionHistoryDisplayMaxLength
            ? singleLine
            : singleLine[..(SuggestionHistoryDisplayMaxLength - 3)].TrimEnd() + "...";
    }

    private sealed record SuggestionHistoryGroup(
        string Key,
        string Content,
        int Count,
        DateTimeOffset LastUsedAt);

    private sealed record SuggestionGenerationContext(
        string AssistantMessage,
        string? LatestUserMessage,
        Guid? LatestUserMessageId,
        IReadOnlyDictionary<Guid, IReadOnlyList<ChatMessage>> LoadedMessageSnapshots);

    private static ChatMessage? GetLatestSuggestionEligibleAssistantMessage(Chat chat)
    {
        var assistantIndex = chat.Messages.FindLastIndex(static m =>
            m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content));
        if (assistantIndex < 0)
            return null;

        var userIndex = chat.Messages.FindLastIndex(static m =>
            m.Role == "user" && !string.IsNullOrWhiteSpace(m.Content));
        return userIndex > assistantIndex ? null : chat.Messages[assistantIndex];
    }

    private static Guid? GetLatestSuggestionEligibleAssistantMessageId(Chat chat)
        => GetLatestSuggestionEligibleAssistantMessage(chat)?.Id;

    private static List<string> NormalizeFollowUpSuggestions(IEnumerable<string>? suggestions)
        => suggestions?
            .Where(static suggestion => !string.IsNullOrWhiteSpace(suggestion))
            .Select(static suggestion => suggestion.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList() ?? [];

    private void ApplyDisplayedSuggestions(IReadOnlyList<string> suggestions)
    {
        SuggestionA = suggestions.ElementAtOrDefault(0) ?? "";
        SuggestionB = suggestions.ElementAtOrDefault(1) ?? "";
        SuggestionC = suggestions.ElementAtOrDefault(2) ?? "";
    }

    private bool CanUpdateDisplayedSuggestions(Chat chat)
        => CurrentChat?.Id == chat.Id && _suggestionDisplayChatId == chat.Id;

    private void TryApplyDisplayedSuggestions(Chat chat, IReadOnlyList<string> suggestions)
    {
        if (CanUpdateDisplayedSuggestions(chat))
            ApplyDisplayedSuggestions(suggestions);
    }

    private void RestoreSuggestionsForChat(Chat chat)
    {
        _suggestionDisplayChatId = chat.Id;
        ApplyDisplayedSuggestions([]);

        if (_suggestionGenerationInFlightChats.Contains(chat.Id))
        {
            IsSuggestionsGenerating = true;
            return;
        }

        IsSuggestionsGenerating = false;
        if (chat.FollowUpSuggestionAssistantMessageId is null
            || chat.FollowUpSuggestionAssistantMessageId != GetLatestSuggestionEligibleAssistantMessageId(chat))
        {
            return;
        }

        ApplyDisplayedSuggestions(chat.FollowUpSuggestions);
    }

    private void StoreGeneratedSuggestions(Chat chat, Guid assistantMessageId, IReadOnlyList<string> suggestions)
    {
        chat.FollowUpSuggestions = [..suggestions];
        chat.FollowUpSuggestionAssistantMessageId = assistantMessageId;
        _lastSuggestedAssistantMessageByChat[chat.Id] = assistantMessageId;
        QueueSaveChatIndex(chat);
    }

    private void ClearPersistedSuggestions(Chat chat)
    {
        if (chat.FollowUpSuggestions.Count == 0 && chat.FollowUpSuggestionAssistantMessageId is null)
            return;

        chat.FollowUpSuggestions = [];
        chat.FollowUpSuggestionAssistantMessageId = null;
        _lastSuggestedAssistantMessageByChat.Remove(chat.Id);
    }

    private void ClearSuggestions()
    {
        ApplyDisplayedSuggestions([]);
        IsSuggestionsGenerating = false;
    }

    private async Task SaveChatAsync(Chat chat, bool saveIndex, bool releaseIfInactive = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_dataStore.Data.Settings.AutoSaveChats)
            {
                await _dataStore.SaveChatAsync(chat, cancellationToken);
                if (saveIndex)
                    await _dataStore.SaveAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Avoid surfacing persistence races/IO failures as hard UI errors.
        }

        if (releaseIfInactive)
        {
            if (Dispatcher.UIThread.CheckAccess())
                ReleaseInactiveChatState(chat);
            else
                Dispatcher.UIThread.Post(() => ReleaseInactiveChatState(chat));
        }
    }

    private async Task SaveIndexAsync()
    {
        try
        {
            await _dataStore.SaveAsync();
        }
        catch
        {
            // Best-effort persistence for UX responsiveness.
        }
    }

    private static bool HasPersistedChatFile(Chat chat) =>
        File.Exists(Path.Combine(DataStore.ChatsDir, $"{chat.Id}.json"));

    /// <summary>
    /// Picks the best model from a list of model IDs using name/version heuristics.
    /// </summary>
    public static string? PickBestModel(IReadOnlyList<string> models)
    {
        if (models.Count == 0) return null;

        return models
            .OrderByDescending(ScoreModel)
            .ThenByDescending(m => m) // alphabetical tiebreaker (higher version strings win)
            .First();
    }

    private static int ScoreModel(string id)
    {
        var m = id.ToLowerInvariant();
        int score = 0;

        // ── Tier scoring (primary) ──
        if (m.Contains("opus"))        score += 5000;
        else if (m.Contains("sonnet")) score += 4000;
        else if (m.Contains("pro"))    score += 3000;
        else if (m.Contains("haiku"))  score += 1000;
        else                           score += 2000; // gpt-N, etc.

        // ── Version extraction: find the first N.N or N pattern ──
        var versionMatch = VersionRegex().Match(m);
        if (versionMatch.Success)
        {
            var major = int.Parse(versionMatch.Groups[1].Value);
            var minor = versionMatch.Groups[2].Success ? int.Parse(versionMatch.Groups[2].Value) : 0;
            score += major * 100 + minor * 10;
        }

        // ── Penalties for specialized/diminished variants ──
        if (m.Contains("mini"))    score -= 800;
        if (m.Contains("fast"))    score -= 400;
        if (m.Contains("codex"))   score -= 300;
        if (m.Contains("preview")) score -= 200;

        return score;
    }

    [GeneratedRegex(@"(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionRegex();

    /// <summary>
    /// Formats a model ID into a display name by splitting on hyphens and applying
    /// known token mappings (e.g. "claude-opus-4.6-1m" → "Claude Opus 4.6 1M").
    /// </summary>
    internal static string? FormatModelDisplay(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;

        var segments = modelId.Split('-');
        var parts = new List<string>(segments.Length);

        foreach (var seg in segments)
        {
            var lower = seg.ToLowerInvariant();

            // Context-window indicators like "1m", "2m" → "1M", "2M"
            if (ContextWindowRegex().IsMatch(lower))
            {
                parts.Add(seg.ToUpperInvariant());
                continue;
            }

            // Version numbers like "4.6", "5", "5.1" — keep as-is
            if (VersionSegmentRegex().IsMatch(lower))
            {
                parts.Add(seg);
                continue;
            }

            // Known tokens → proper casing
            if (KnownModelTokens.TryGetValue(lower, out var display))
            {
                parts.Add(display);
                continue;
            }

            // Unknown segment — title-case
            if (seg.Length > 0)
                parts.Add(char.ToUpperInvariant(seg[0]) + seg[1..]);
        }

        return string.Join(" ", parts);
    }

    private static readonly Dictionary<string, string> KnownModelTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = "Claude", ["opus"] = "Opus", ["sonnet"] = "Sonnet", ["haiku"] = "Haiku",
        ["gpt"] = "GPT", ["gemini"] = "Gemini", ["o1"] = "o1", ["o3"] = "o3", ["o4"] = "o4",
        ["codex"] = "Codex", ["mini"] = "Mini", ["max"] = "Max",
        ["pro"] = "Pro", ["preview"] = "Preview", ["turbo"] = "Turbo",
    };

    [GeneratedRegex(@"^\d+m$", RegexOptions.IgnoreCase)]
    private static partial Regex ContextWindowRegex();

    [GeneratedRegex(@"^\d+(\.\d+)?$")]
    private static partial Regex VersionSegmentRegex();

    /// <summary>
    /// Removes the user message and its response, then resends.
    /// The message content may have been edited before calling this.
    /// </summary>
    public async Task ResendFromMessageAsync(ChatMessage userMessage, bool wasEdited)
    {
        if (CurrentChat is null) return;

        // Stop any active generation first
        if (IsBusy)
            await StopGeneration();

        var idx = CurrentChat.Messages.IndexOf(userMessage);
        if (idx < 0) return;

        var prompt = userMessage.Content;

        // Remove the user message and everything after it
        while (CurrentChat.Messages.Count > idx)
            CurrentChat.Messages.RemoveAt(CurrentChat.Messages.Count - 1);

        // Preserve the retained transcript (before the edited user turn) so we can
        // rebuild context safely if we need to recreate the backend session.
        var retainedContext = CurrentChat.Messages.ToList();

        // Rebuild the UI without the removed messages
        _transcriptBuilder.IsRebuildingTranscript = true;
        Messages.Clear();
        foreach (var msg in CurrentChat.Messages.Where(m =>
            m.Role != "reasoning"
            && !(m.Role == "assistant" && string.IsNullOrWhiteSpace(m.Content))))
            Messages.Add(new ChatMessageViewModel(msg));
        _transcriptBuilder.IsRebuildingTranscript = false;

        RebuildTranscript();

        _transcriptBuilder.ShownFileChips.Clear();
        _transcriptBuilder.PendingFetchedSkillRefs.Clear();

        // For edits: there is currently no public SDK API to rewind/remove prior
        // turns from the server-side history. To avoid leaking the pre-edit prompt,
        // we recreate the backend session and pass only the retained transcript.
        // For regenerates (same content): reuse the existing session as-is.

        // Re-add the user message as a fresh entry
        var newUserMsg = new ChatMessage
        {
            Role = "user",
            Content = prompt,
            Author = userMessage.Author
        };
        CurrentChat.Messages.Add(newUserMsg);
        Messages.Add(new ChatMessageViewModel(newUserMsg));
        QueueSaveChat(CurrentChat, saveIndex: true, touchIndex: true);
        ChatUpdated?.Invoke();
        ScrollToEndRequested?.Invoke();

        // Resend
        if (!_copilotService.IsConnected)
        {
            StatusText = Loc.Status_NotConnected;
            try { await _copilotService.ConnectAsync(); }
            catch
            {
                StatusText = Loc.Status_ConnectionFailedShort;
                var connErrorMsg = new ChatMessage
                {
                    Role = "error",
                    Author = Loc.Author_Lumi,
                    Content = Loc.Status_ConnectionFailedShort
                };
                CurrentChat.Messages.Add(connErrorMsg);
                var connVm = new ChatMessageViewModel(connErrorMsg);
                Messages.Add(connVm);
                ScrollToEndRequested?.Invoke();
                return;
            }
        }

        MessageOptions? resendOptions = null;
        var promptAdditions = BuildSendPromptAdditions();
        var localUserMessageCount = 0;
        var localAssistantMessageCount = 0;
        try
        {
            // Cancel any previous in-flight request for this chat
            var chatId = CurrentChat.Id;
            if (ReleasePreviousTurnCancellation(chatId))
            {
                if (_sessionCache.TryGetValue(chatId, out var cachedSession))
                {
                    try { await cachedSession.AbortAsync(); }
                    catch { /* best-effort */ }
                }
            }

            var needsSessionSetup = _activeSession?.SessionId != CurrentChat.CopilotSessionId
                                    || CurrentChat.CopilotSessionId is null;
            if (ConsumePendingSessionInvalidation(CurrentChat))
                needsSessionSetup = true;

            // Editing must not keep old server-side context. Recreate session first.
            // Must happen BEFORE creating the new CTS, because InvalidateCurrentSession
            // calls ReleaseSessionResources which disposes any CTS still in _ctsSources.
            var shouldReplayPrompt = wasEdited;
            var previousSessionId = CurrentChat.CopilotSessionId;
            if (wasEdited)
            {
                InvalidateCurrentSession();
                needsSessionSetup = true;
            }

            var cts = new CancellationTokenSource();
            _ctsSources[chatId] = cts;

            if (needsSessionSetup)
            {
                var ok = await EnsureSessionAsync(CurrentChat, cts.Token, allowCreateFallback: true);

                if (!ok)
                {
                    StatusText = "Session expired. Please start a new chat.";
                    var errorMsg = new ChatMessage
                    {
                        Role = "error",
                        Author = Loc.Author_Lumi,
                        Content = "Session expired. Please start a new chat to continue."
                    };
                    CurrentChat.Messages.Add(errorMsg);
                    var msgVm = new ChatMessageViewModel(errorMsg);
                    Messages.Add(msgVm);
                    ScrollToEndRequested?.Invoke();
                    return;
                }

                if (!wasEdited)
                {
                    shouldReplayPrompt = ShouldReplayTranscriptAfterSessionReset(
                        chatWasCreatedThisTurn: false,
                        previousSessionId,
                        CurrentChat.CopilotSessionId,
                        retainedContext.Count);
                }
            }

            var runtime = GetOrCreateRuntimeState(CurrentChat.Id);
            MarkRuntimeActive(runtime, Loc.Status_Thinking);
            ApplyDisplayedRuntimeState(runtime);

            var resendPrompt = BuildResendPrompt(
                retainedContext,
                prompt,
                wasEdited,
                shouldReplayPrompt,
                promptAdditions);

            resendOptions = new MessageOptions { Prompt = resendPrompt };
            localUserMessageCount = CurrentChat.Messages.Count(m => m.Role == "user");
            localAssistantMessageCount = CountCompletedAssistantMessages(CurrentChat);
            var expectedSessionUserMessageCount = await CaptureExpectedSessionUserMessageCountAsync(
                _activeSession!,
                localUserMessageCount,
                cts.Token);
            PreparePendingTurnTracking(
                CurrentChat,
                expectedSessionUserMessageCount,
                localAssistantMessageCount);
            await _activeSession!.SendAsync(resendOptions, cts.Token);
        }
        catch (Exception ex) when (IsSessionNotFoundError(ex) && CurrentChat is not null)
        {
            // Stale session cache — evict and resume
            try
            {
                var cts = _ctsSources.GetValueOrDefault(CurrentChat.Id);
                if (cts is null) return;
                StatusText = Loc.Status_Reconnecting;
                DetachPersistedSession(CurrentChat);
                var ok = await EnsureSessionAsync(CurrentChat, cts.Token, allowCreateFallback: true);
                if (!ok)
                {
                    ClearPendingTurnTracking(CurrentChat.Id);
                    HandleSendError(
                        new InvalidOperationException(Loc.Status_OriginalSessionUnavailable),
                        wasCancelledByUser: false,
                        overrideMessage: Loc.Status_OriginalSessionUnavailable);
                    return;
                }
                var resendPrompt2 = BuildResendPrompt(
                    retainedContext,
                    prompt,
                    wasEdited,
                    shouldReplayPrompt: !wasEdited,
                    promptAdditions);
                resendOptions = new MessageOptions { Prompt = resendPrompt2 };
                var expectedSessionUserMessageCount = await CaptureExpectedSessionUserMessageCountAsync(
                    _activeSession!,
                    localUserMessageCount,
                    cts.Token);
                PreparePendingTurnTracking(
                    CurrentChat,
                    expectedSessionUserMessageCount,
                    localAssistantMessageCount);
                await _activeSession!.SendAsync(resendOptions, cts.Token);
            }
            catch (Exception retryEx)
            {
                if (CurrentChat is not null && resendOptions is not null && IsCopilotTransportError(retryEx))
                {
                    var recovery = await TryRecoverTransportSendAsync(CurrentChat, resendOptions);
                    if (recovery.Recovered)
                        return;

                    ClearPendingTurnTracking(CurrentChat.Id);
                    HandleSendError(retryEx, WasCancelledByUser(CurrentChat?.Id), recovery.FailureMessage);
                    return;
                }

                ClearPendingTurnTracking(CurrentChat!.Id);
                HandleSendError(retryEx, WasCancelledByUser(CurrentChat?.Id));
            }
        }
        catch (Exception ex) when (CurrentChat is not null && resendOptions is not null && IsCopilotTransportError(ex))
        {
            var recovery = await TryRecoverTransportSendAsync(CurrentChat, resendOptions);
            if (recovery.Recovered)
                return;

            ClearPendingTurnTracking(CurrentChat.Id);
            HandleSendError(ex, WasCancelledByUser(CurrentChat?.Id), recovery.FailureMessage);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by StopGeneration — expected
            if (CurrentChat is not null)
                ClearPendingTurnTracking(CurrentChat.Id);
        }
        catch (Exception ex)
        {
            if (CurrentChat is not null)
                ClearPendingTurnTracking(CurrentChat.Id);
            HandleSendError(ex, WasCancelledByUser(CurrentChat?.Id));
        }
    }

    private static string BuildSessionRecoveryReplayPrompt(List<ChatMessage> retainedContext, string latestPrompt)
        => BuildReplayPrompt(
            retainedContext,
            latestPrompt,
            "The previous backend chat session is unavailable. Continue using ONLY the conversation context below.",
            "Treat the transcript as the complete conversation history so far.",
            "Latest user message:");

    private static string BuildResendPrompt(
        List<ChatMessage> retainedContext,
        string prompt,
        bool wasEdited,
        bool shouldReplayPrompt,
        string promptAdditions)
    {
        var resendPrompt = wasEdited
            ? BuildEditedReplayPrompt(retainedContext, prompt)
            : shouldReplayPrompt
                ? BuildSessionRecoveryReplayPrompt(retainedContext, prompt)
                : prompt;

        return resendPrompt + promptAdditions;
    }

    private static string BuildEditedReplayPrompt(List<ChatMessage> retainedContext, string editedPrompt)
        => BuildReplayPrompt(
            retainedContext,
            editedPrompt,
            "The user edited an earlier message. Use ONLY the corrected conversation context below.",
            "Ignore any previous conversation state not included here.",
            "Latest user message (edited):");

    private static string BuildReplayPrompt(
        List<ChatMessage> retainedContext,
        string latestPrompt,
        string instruction,
        string followUpInstruction,
        string latestLabel)
    {
        if (retainedContext.Count == 0)
            return latestPrompt;

        var lines = new List<string>
        {
            instruction,
            followUpInstruction,
            "",
            "Conversation so far:"
        };

        foreach (var msg in retainedContext)
        {
            if (string.IsNullOrWhiteSpace(msg.Content))
                continue;

            string role = msg.Role switch
            {
                "assistant" => "Assistant",
                "system" => "System",
                _ => "User"
            };

            if (msg.Role is "user" or "assistant" or "system")
                lines.Add($"{role}: {msg.Content.Trim()}");
        }

        lines.Add("");
        lines.Add(latestLabel);
        lines.Add(latestPrompt);

        return string.Join("\n", lines);
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
    public string? ModelName => ChatViewModel.FormatModelDisplay(Message.Model);
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
