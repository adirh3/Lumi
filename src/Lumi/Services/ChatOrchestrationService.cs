using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Lumi.Models;
using Lumi.ViewModels;

namespace Lumi.Services;

/// <summary>
/// Backs the <c>manage_chats</c> tool, letting Lumi act as a "manager" that orchestrates other chats:
/// create new chats (optionally in a project / with a Lumi agent), send messages into existing chats,
/// track their progress, pin or unpin them, and list them — all without leaving the current conversation.
///
/// Messages are delivered through the same robust target-chat send path that background jobs use
/// (<see cref="ChatViewModel.SendExternalMessageAsync"/>). Executor resolution mirrors
/// <see cref="BackgroundJobService"/>: a live owner is reused, a visible owner is retained, otherwise a
/// fresh surface is acquired from the <see cref="ChatSessionStore"/>. Orchestrated runs execute under a
/// service-lifetime token so a worker chat keeps running after the manager's own turn ends — exactly like
/// a person kicking off work in several chats and checking back on them later.
/// </summary>
public sealed class ChatOrchestrationService : IDisposable
{
    private const int DefaultListLimit = 20;
    private const int MaxListLimit = 60;
    private const int DefaultStatusMessages = 8;
    private const int MaxStatusMessages = 40;
    private const int DefaultWaitSeconds = 240;
    private const int MaxWaitSeconds = 1800;
    private const int SnippetChars = 400;

    private readonly DataStore _dataStore;
    private readonly ChatSurfaceRegistry _registry;
    private readonly ChatSessionStore _sessionStore;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly ConcurrentDictionary<Guid, OrchestrationRun> _runs = new();
    // Chats whose run is being started but has not yet been recorded in _runs. Guards the await gap
    // between the busy-check and StartRun so two concurrent sends to the same chat can't both proceed.
    // Only ever touched on the UI thread (inside InvokeUiAsync bodies), so a plain set is safe.
    private readonly HashSet<Guid> _starting = [];
    private readonly Func<DateTimeOffset> _now;
    // The target-chat send primitive. Defaults to the real ChatViewModel path; overridable in tests so the
    // run/cleanup plumbing (the _runs / _starting busy-gate) can be exercised without a live Copilot session.
    // The two string? args are the optional per-send model / reasoning-effort override (null = keep the
    // chat's current selection).
    private readonly Func<ChatViewModel, Chat, string, string, string?, string?, CancellationToken, Task> _sendMessage;
    private bool _isDisposed;

    public ChatOrchestrationService(
        DataStore dataStore,
        ChatSurfaceRegistry registry,
        ChatSessionStore sessionStore,
        Func<DateTimeOffset>? nowProvider = null,
        Func<ChatViewModel, Chat, string, string, string?, string?, CancellationToken, Task>? sendOverride = null)
    {
        _dataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _now = nowProvider ?? (() => DateTimeOffset.Now);
        _sendMessage = sendOverride
            ?? ((executor, chat, message, author, model, effort, token) =>
                executor.SendExternalMessageAsync(chat, message, author, token, model, effort));
    }

    /// <summary>Raised (on the UI thread) when a managed chat is created, started, or finishes a run,
    /// so the shell can refresh the chat list / running indicators.</summary>
    public event Action? ChatsChanged;

    /// <summary>Single entry point for the <c>manage_chats</c> tool.</summary>
    public Task<string> ManageChatsAsync(
        string action,
        string? identifier = null,
        string? title = null,
        string? message = null,
        string? project = null,
        string? agent = null,
        string[]? skills = null,
        string? model = null,
        string? reasoningEffort = null,
        bool? worktree = null,
        bool? wait = null,
        int? timeoutSeconds = null,
        int? maxMessages = null,
        string? query = null,
        int? limit = null,
        Guid? sourceChatId = null,
        Action<Guid, string>? onChatLinked = null,
        CancellationToken cancellationToken = default)
    {
        return NormalizeAction(action) switch
        {
            "list" or "show" or "search" => ListChatsAsync(query ?? identifier, project, limit),
            "create" or "new" => CreateChatAsync(title, message, project, agent, skills, model, reasoningEffort, worktree, wait, timeoutSeconds, sourceChatId, onChatLinked, cancellationToken),
            "send" or "message" or "reply" => SendMessageAsync(identifier, message, model, reasoningEffort, wait, timeoutSeconds, sourceChatId, onChatLinked, cancellationToken),
            "status" or "progress" or "read" => GetStatusAsync(identifier, maxMessages, cancellationToken),
            "pin" => SetPinnedAsync(identifier, isPinned: true, cancellationToken),
            "unpin" => SetPinnedAsync(identifier, isPinned: false, cancellationToken),
            _ => Task.FromResult(
                $"Unknown manage_chats action \"{action}\". Use list, create, send, status, pin, or unpin.")
        };
    }

    // ── list ────────────────────────────────────────────────────────────────

    private async Task<string> ListChatsAsync(string? query, string? project, int? limit)
    {
        var max = Math.Clamp(limit ?? DefaultListLimit, 1, MaxListLimit);
        var trimmedQuery = (query ?? "").Trim();

        return await InvokeUiAsync(() =>
        {
            var projectFilter = ResolveProject(project);
            if (project is { Length: > 0 } && !string.IsNullOrWhiteSpace(project) && projectFilter is null)
                return $"No project matches \"{project}\".";

            IEnumerable<Chat> chats = _dataStore.Data.Chats;
            if (projectFilter is not null)
                chats = chats.Where(c => c.ProjectId == projectFilter.Id);

            if (trimmedQuery.Length > 0)
                chats = chats.Where(c => c.Title.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase));

            var ordered = chats
                .OrderByDescending(c => c.IsPinned)
                .ThenByDescending(c => c.UpdatedAt)
                .Take(max)
                .ToList();
            if (ordered.Count == 0)
                return trimmedQuery.Length > 0 || projectFilter is not null
                    ? "No chats matched that filter."
                    : "There are no chats yet. Use manage_chats action=create to start one.";

            var builder = new StringBuilder();
            builder.Append(projectFilter is not null
                ? $"Chats in project \"{projectFilter.Name}\""
                : "Chats");
            if (trimmedQuery.Length > 0)
                builder.Append($" matching \"{trimmedQuery}\"");
            builder.Append($" ({ordered.Count}, pinned first, then most recent):\n");

            var index = 1;
            foreach (var chat in ordered)
            {
                builder.Append('\n').Append(index++).Append(". ").Append(Quote(chat.Title)).Append('\n');
                builder.Append("   id: ").Append(chat.Id).Append('\n');
                builder.Append("   ").Append(DescribeMeta(chat)).Append('\n');
                builder.Append("   status: ").Append(DescribeLiveState(chat)).Append('\n');
            }

            builder.Append("\nUse manage_chats action=status with an id to see progress, action=send to message it, or action=pin/unpin to change its priority.");
            return builder.ToString();
        }).ConfigureAwait(false);
    }

    // ── pin / unpin ─────────────────────────────────────────────────────────

    private async Task<string> SetPinnedAsync(
        string? identifier,
        bool isPinned,
        CancellationToken cancellationToken)
    {
        var result = await InvokeUiAsync(() =>
        {
            var (chat, error) = ResolveChat(identifier);
            if (chat is null)
                return (Changed: false, Message: error!);

            if (chat.IsPinned == isPinned)
            {
                var state = isPinned ? "pinned" : "not pinned";
                return (Changed: false, Message: $"Chat {Quote(chat.Title)} is already {state}.");
            }

            chat.IsPinned = isPinned;
            _dataStore.MarkChatChanged(chat);
            var verb = isPinned ? "Pinned" : "Unpinned";
            var note = isPinned ? " It will appear at the top of its project." : "";
            return (Changed: true, Message: $"{verb} chat {Quote(chat.Title)} (id: {chat.Id}).{note}");
        }).ConfigureAwait(false);

        if (!result.Changed)
            return result.Message;

        await _dataStore.SaveAsync(cancellationToken).ConfigureAwait(false);
        RaiseChatsChanged();
        return result.Message;
    }

    // ── create ──────────────────────────────────────────────────────────────

    private async Task<string> CreateChatAsync(
        string? title,
        string? message,
        string? project,
        string? agent,
        string[]? skills,
        string? model,
        string? reasoningEffort,
        bool? worktree,
        bool? wait,
        int? timeoutSeconds,
        Guid? sourceChatId,
        Action<Guid, string>? onChatLinked,
        CancellationToken cancellationToken)
    {
        var trimmedMessage = string.IsNullOrWhiteSpace(message) ? null : message.Trim();

        var setup = await InvokeUiAsync(() =>
        {
            var proj = ResolveProject(project);
            if (!string.IsNullOrWhiteSpace(project) && proj is null)
                return (Chat: (Chat?)null, Error: $"No project matches \"{project}\".", ProjectName: "", AgentName: "", ProjectDir: (string?)null);

            var lumi = ResolveAgent(agent);
            if (!string.IsNullOrWhiteSpace(agent) && lumi is null)
                return (Chat: (Chat?)null, Error: $"No Lumi/agent matches \"{agent}\".", ProjectName: "", AgentName: "", ProjectDir: (string?)null);

            var skillIds = new List<Guid>();
            foreach (var s in skills ?? [])
            {
                var skill = ResolveSkill(s);
                if (skill is null)
                    return (Chat: (Chat?)null, Error: $"No skill matches \"{s}\".", ProjectName: "", AgentName: "", ProjectDir: (string?)null);
                if (!skillIds.Contains(skill.Id))
                    skillIds.Add(skill.Id);
            }

            var now = _now();
            var resolvedTitle = !string.IsNullOrWhiteSpace(title)
                ? title.Trim()
                : trimmedMessage is not null
                    ? Truncate(Collapse(trimmedMessage), 60)
                    : "Managed chat";

            var chat = new Chat
            {
                Title = resolvedTitle,
                CreatedAt = now,
                UpdatedAt = now,
                ProjectId = proj?.Id,
                AgentId = lumi?.Id,
                ActiveSkillIds = skillIds,
                LastModelUsed = string.IsNullOrWhiteSpace(model) ? _dataStore.Data.Settings.PreferredModel : model.Trim(),
                LastReasoningEffortUsed = string.IsNullOrWhiteSpace(reasoningEffort) ? _dataStore.Data.Settings.ReasoningEffort : reasoningEffort.Trim(),
                LastContextWindowTierUsed = _dataStore.Data.Settings.ContextWindowTier
            };

            _dataStore.Data.Chats.Add(chat);
            _dataStore.MarkChatChanged(chat);
            // Capture the display names + project working dir here (on the UI thread) so the header can be
            // built and the worktree created off-thread below without touching _dataStore.Data.Projects /
            // .Agents from a background SDK thread.
            return (Chat: (Chat?)chat, Error: (string?)null, ProjectName: proj?.Name ?? "", AgentName: lumi?.Name ?? "", ProjectDir: proj?.WorkingDirectory);
        }).ConfigureAwait(false);

        if (setup.Chat is null)
            return setup.Error!;

        var created = setup.Chat;

        // Optional git worktree — only for a coding project (git repo). Created before the first message
        // so the worker's session works inside the isolated worktree from its very first turn. Done off the
        // UI-thread mutation above (git is I/O); Chat.WorktreePath is a plain field with no observers yet.
        var worktreeNote = await MaybeCreateWorktreeAsync(created, worktree == true, setup.ProjectDir).ConfigureAwait(false);

        onChatLinked?.Invoke(created.Id, created.Title);
        await _dataStore.SaveChatAsync(created, cancellationToken).ConfigureAwait(false);
        await _dataStore.SaveAsync(cancellationToken).ConfigureAwait(false);
        RaiseChatsChanged();

        var header = new StringBuilder();
        header.Append($"Created chat {Quote(created.Title)} (id: {created.Id})");
        if (created.ProjectId is not null)
            header.Append($" in project \"{setup.ProjectName}\"");
        if (created.AgentId is not null)
            header.Append($" with Lumi \"{setup.AgentName}\"");
        header.Append('.');
        if (worktreeNote is not null)
            header.Append(' ').Append(worktreeNote);

        if (trimmedMessage is null)
        {
            header.Append(" No message was sent yet — use manage_chats action=send with this id to start it.");
            return header.ToString();
        }

        var send = await StartOrRunAsync(created, trimmedMessage, model, reasoningEffort, wait, timeoutSeconds, sourceChatId, cancellationToken)
            .ConfigureAwait(false);
        return header.Append('\n').Append(send).ToString();
    }

    /// <summary>Creates a git worktree for a freshly-created managed chat when requested and the project is
    /// a git repository. Returns a short human note for the create result, or null when no worktree was
    /// requested. Mirrors the interactive worktree flow (branch <c>lumi/{8hex}</c>) and is best-effort:
    /// any failure falls back to the project folder with an explanatory note. Internal for direct testing —
    /// exercising it through the full headless UI dispatch is unreliable because the real git subprocess
    /// await hops off the dispatcher thread.</summary>
    internal static async Task<string?> MaybeCreateWorktreeAsync(Chat chat, bool requested, string? projectDir)
    {
        if (!requested)
            return null;

        if (string.IsNullOrWhiteSpace(projectDir) || !GitService.IsGitRepo(projectDir))
            return "A git worktree was requested but the project is not a git repository, so the chat uses the project folder directly.";

        try
        {
            var branchName = $"lumi/{Guid.NewGuid().ToString("N")[..8]}";
            var path = await GitService.CreateWorktreeAsync(projectDir, branchName).ConfigureAwait(false);
            if (path is null)
                return "A git worktree was requested but could not be created, so the chat uses the project folder directly.";

            chat.WorktreePath = path;
            return $"It runs in an isolated git worktree on branch \"{branchName}\".";
        }
        catch (Exception ex)
        {
            return $"A git worktree was requested but failed to create ({ex.Message}); the chat uses the project folder directly.";
        }
    }

    // ── send ────────────────────────────────────────────────────────────────

    private async Task<string> SendMessageAsync(
        string? identifier,
        string? message,
        string? model,
        string? reasoningEffort,
        bool? wait,
        int? timeoutSeconds,
        Guid? sourceChatId,
        Action<Guid, string>? onChatLinked,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "A message is required to send to a chat.";

        var resolution = await InvokeUiAsync(() => ResolveChat(identifier)).ConfigureAwait(false);
        if (resolution.Chat is null)
            return resolution.Error!;

        if (resolution.Chat.Id != sourceChatId)
            onChatLinked?.Invoke(resolution.Chat.Id, resolution.Chat.Title);

        return await StartOrRunAsync(resolution.Chat, message.Trim(), model, reasoningEffort, wait, timeoutSeconds, sourceChatId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> StartOrRunAsync(
        Chat chat,
        string message,
        string? model,
        string? reasoningEffort,
        bool? wait,
        int? timeoutSeconds,
        Guid? sourceChatId,
        CancellationToken cancellationToken)
    {
        if (sourceChatId is { } source && source == chat.Id)
            return "That id is the current chat — a manager can't send a message to its own chat. Target a different chat.";

        var modelOverride = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        var effortOverride = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort.Trim();

        Task? runTask = null;
        string? error = null;

        await InvokeUiAsync(async () =>
        {
            if (IsBusy(chat.Id))
            {
                error = $"Chat {Quote(chat.Title)} is already running — check it with manage_chats action=status before sending again.";
                return true;
            }

            // Build the author label here, on the UI thread — it reads _dataStore.Data.Chats.
            var author = BuildAuthor(sourceChatId);

            // Reserve synchronously (before the await below) so a concurrent send to this same chat
            // sees it as busy. Released once StartRun has recorded the run in _runs (or on failure).
            _starting.Add(chat.Id);
            try
            {
                var (executor, release) = await ResolveExecutorAsync(chat).ConfigureAwait(true);
                runTask = StartRun(executor, release, chat, message, author, modelOverride, effortOverride);
            }
            catch (Exception ex)
            {
                error = $"Failed to start chat {Quote(chat.Title)}: {ex.Message}";
            }
            finally
            {
                _starting.Remove(chat.Id);
            }

            return true;
        }).ConfigureAwait(false);

        if (error is not null)
            return error;
        if (runTask is null)
            return $"Could not start chat {Quote(chat.Title)}.";

        RaiseChatsChanged();

        var shouldWait = wait == true;
        if (!shouldWait)
        {
            return $"Sent to chat {Quote(chat.Title)} (id: {chat.Id}); it is now working in the background. "
                 + "Use manage_chats action=status with this id to track its progress.";
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds ?? DefaultWaitSeconds, 5, MaxWaitSeconds));
        try
        {
            await runTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return $"Chat {Quote(chat.Title)} is still working after {timeout.TotalSeconds:0}s. "
                 + "It keeps running in the background — use manage_chats action=status with its id to check back.";
        }
        catch (OperationCanceledException)
        {
            return $"Stopped waiting on chat {Quote(chat.Title)}; it continues running in the background.";
        }
        catch (Exception ex)
        {
            return $"Chat {Quote(chat.Title)} finished with an error: {ex.Message}";
        }

        return await InvokeUiAsync(() => DescribeReply(chat)).ConfigureAwait(false);
    }

    // ── status ──────────────────────────────────────────────────────────────

    private async Task<string> GetStatusAsync(string? identifier, int? maxMessages, CancellationToken cancellationToken)
    {
        var window = Math.Clamp(maxMessages ?? DefaultStatusMessages, 1, MaxStatusMessages);

        var snap = await InvokeUiAsync(() =>
        {
            var resolution = ResolveChat(identifier);
            if (resolution.Chat is null)
                return (Snapshot: (StatusSnapshot?)null, Error: resolution.Error);

            var chat = resolution.Chat;
            var loaded = chat.Messages.Count > 0 ? chat.Messages.ToList() : null;
            return (Snapshot: new StatusSnapshot(
                    chat,
                    DescribeLiveState(chat),
                    DescribeMeta(chat),
                    loaded),
                Error: (string?)null);
        }).ConfigureAwait(false);

        if (snap.Snapshot is null)
            return snap.Error!;

        var status = snap.Snapshot;
        var messages = status.LoadedMessages
            ?? (await _dataStore.ReadChatMessagesAsync(status.Chat, cancellationToken).ConfigureAwait(false)).ToList();

        return FormatStatus(status, messages, window);
    }

    private static string FormatStatus(StatusSnapshot status, List<ChatMessage> messages, int window)
    {
        var chat = status.Chat;
        var builder = new StringBuilder();
        builder.Append("Chat ").Append(Quote(chat.Title)).Append(" (id: ").Append(chat.Id).Append(")\n");
        builder.Append(status.Meta).Append('\n');
        builder.Append("State: ").Append(status.LiveState).Append('\n');

        var userCount = messages.Count(m => m.Role == "user");
        var assistantCount = messages.Count(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content));
        var errorCount = messages.Count(m => m.Role == "error");
        builder.Append($"Messages: {messages.Count} ({userCount} from senders, {assistantCount} from Lumi");
        if (errorCount > 0)
            builder.Append($", {errorCount} error(s)");
        builder.Append(")\n");

        var lastError = messages.LastOrDefault(m => m.Role == "error");
        if (lastError is not null)
            builder.Append("Last error: ").Append(Truncate(Collapse(lastError.Content), SnippetChars)).Append('\n');

        var lastAssistant = messages.LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content));
        if (lastAssistant is not null)
        {
            builder.Append("\nLatest Lumi reply:\n");
            builder.Append(Truncate(lastAssistant.Content.Trim(), SnippetChars * 2));
            builder.Append('\n');
        }
        else
        {
            builder.Append("\nLumi has not produced a text reply in this chat yet.\n");
        }

        var recentTools = messages
            .Where(m => m.Role == "tool" && !string.IsNullOrWhiteSpace(m.ToolName))
            .TakeLast(5)
            .Select(m => $"{ToolDisplayHelper.GetToolGlyph(m.ToolName!)} {m.ToolName}{(string.IsNullOrWhiteSpace(m.ToolStatus) ? "" : $" ({m.ToolStatus})")}")
            .ToList();
        if (recentTools.Count > 0)
        {
            builder.Append("\nRecent tool activity:\n");
            foreach (var tool in recentTools)
                builder.Append("  • ").Append(tool).Append('\n');
        }

        builder.Append("\nUse read_chat with this id for the full transcript, or manage_chats action=send to reply.");
        return builder.ToString();
    }

    // ── run plumbing ──────────────────────────────────────────────────────────

    private Task StartRun(ChatViewModel executor, bool release, Chat chat, string message, string author,
        string? modelOverride, string? effortOverride)
    {
        // RunCoreAsync yields (await Task.Yield()) before running any of its body, guaranteeing this _runs
        // insertion happens-before the run's finally can remove it — even when the send fails synchronously.
        // Without that ordering a synchronously-completing run would remove-then-insert, leaking a permanent
        // _runs entry that wedges the chat into a false "busy" state until the app restarts.
        var task = RunCoreAsync(executor, release, chat, message, author, modelOverride, effortOverride, _lifetimeCts.Token);
        _runs[chat.Id] = new OrchestrationRun(_now(), Truncate(Collapse(message), 120), task);
        return task;
    }

    private async Task RunCoreAsync(
        ChatViewModel executor,
        bool release,
        Chat chat,
        string message,
        string author,
        string? modelOverride,
        string? effortOverride,
        CancellationToken token)
    {
        // Yield before touching anything so StartRun records this run in _runs before the finally below can
        // run. This keeps _runs insertion strictly happen-before removal even when the send completes
        // synchronously (e.g. SendExternalMessageAsync throwing at its own busy-guard returns an already-
        // faulted task), which would otherwise remove-then-insert and leak a permanent _runs entry. The
        // continuation resumes on the captured UI SynchronizationContext.
        await Task.Yield();
        try
        {
            await _sendMessage(executor, chat, message, author, modelOverride, effortOverride, token).ConfigureAwait(true);
        }
        catch
        {
            // SendExternalMessageAsync records its own error card in the transcript for failures it reaches,
            // which manage_chats action=status / DescribeReply surface. Swallow here so an unobserved
            // background task never crashes the app. Note this means runTask never faults, so a wait=true
            // caller observes the outcome via DescribeReply rather than a faulted WaitAsync.
        }
        finally
        {
            if (release)
                _sessionStore.Release(executor);
            _runs.TryRemove(chat.Id, out _);
            RaiseChatsChanged();
        }
    }

    private async Task<(ChatViewModel Executor, bool Release)> ResolveExecutorAsync(Chat chat)
    {
        if (_registry.TryGetLiveOwner(chat.Id, out var live))
            return (live, false);

        if (_registry.TryGetOwner(chat.Id, out var visible))
        {
            _sessionStore.Retain(visible);
            return (visible, true);
        }

        var acquired = await _sessionStore.AcquireChatAsync(chat).ConfigureAwait(true);
        return (acquired, true);
    }

    private bool IsBusy(Guid chatId)
        => _starting.Contains(chatId) || _runs.ContainsKey(chatId) || _registry.TryGetLiveOwner(chatId, out _);

    /// <summary>Completes when no orchestrated run is in flight. Intended for graceful shutdown and for
    /// tests that need to drain background runs deterministically; await it on the UI thread.</summary>
    internal Task WaitForRunsAsync()
        => Task.WhenAll(_runs.Values.Select(r => r.Task).ToArray());

    private string BuildAuthor(Guid? sourceChatId)
    {
        if (sourceChatId is { } id)
        {
            var source = _dataStore.Data.Chats.FirstOrDefault(c => c.Id == id);
            if (source is not null)
                return $"Lumi Manager · {source.Title}";
        }

        return "Lumi Manager";
    }

    private string DescribeReply(Chat chat)
    {
        var reply = chat.Messages.LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content));
        var error = chat.Messages.LastOrDefault(m => m.Role == "error");
        var builder = new StringBuilder();
        builder.Append("Chat ").Append(Quote(chat.Title)).Append(" (id: ").Append(chat.Id).Append(") finished this turn.\n");
        if (error is not null && (reply is null || chat.Messages.IndexOf(error) > chat.Messages.IndexOf(reply)))
        {
            builder.Append("It ended with an error:\n").Append(Truncate(Collapse(error.Content), SnippetChars));
            return builder.ToString();
        }

        if (reply is not null)
            builder.Append("Lumi replied:\n").Append(Truncate(reply.Content.Trim(), SnippetChars * 3));
        else
            builder.Append("It produced no text reply — use manage_chats action=status for details.");
        return builder.ToString();
    }

    // ── resolution helpers (call on UI thread) ────────────────────────────────

    private (Chat? Chat, string? Error) ResolveChat(string? identifier)
    {
        var normalized = (identifier ?? "").Trim();
        if (normalized.Length == 0)
            return (null, "A chat id or exact title is required.");

        if (Guid.TryParse(normalized, out var id))
        {
            var byId = _dataStore.Data.Chats.FirstOrDefault(c => c.Id == id);
            return byId is null ? (null, $"No chat found with id {normalized}.") : (byId, null);
        }

        var matches = _dataStore.Data.Chats
            .Where(c => string.Equals(c.Title, normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count switch
        {
            0 => (null, $"No chat matches \"{normalized}\". Use manage_chats action=list to see chat ids."),
            1 => (matches[0], null),
            _ => (null, $"Multiple chats are titled \"{normalized}\". Use a chat id instead (manage_chats action=list).")
        };
    }

    private Project? ResolveProject(string? identifier)
        => ResolveByIdOrName(_dataStore.Data.Projects, identifier, p => p.Id, p => p.Name);

    private LumiAgent? ResolveAgent(string? identifier)
        => ResolveByIdOrName(_dataStore.Data.Agents, identifier, a => a.Id, a => a.Name);

    private Skill? ResolveSkill(string? identifier)
        => ResolveByIdOrName(_dataStore.Data.Skills, identifier, s => s.Id, s => s.Name);

    private static T? ResolveByIdOrName<T>(
        IEnumerable<T> items,
        string? identifier,
        Func<T, Guid> getId,
        Func<T, string> getName) where T : class
    {
        var normalized = (identifier ?? "").Trim();
        if (normalized.Length == 0)
            return null;

        if (Guid.TryParse(normalized, out var id))
            return items.FirstOrDefault(item => getId(item) == id);

        return items.FirstOrDefault(item => string.Equals(getName(item), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private string GetProjectName(Guid? projectId)
        => projectId is { } id
            ? _dataStore.Data.Projects.FirstOrDefault(p => p.Id == id)?.Name ?? "?"
            : "";

    private string GetAgentName(Guid? agentId)
        => agentId is { } id
            ? _dataStore.Data.Agents.FirstOrDefault(a => a.Id == id)?.Name ?? "?"
            : "";

    // ── formatting helpers (call on UI thread for chat access) ────────────────

    private string DescribeMeta(Chat chat)
    {
        var parts = new List<string>();
        if (chat.IsPinned)
            parts.Add("pinned");
        if (chat.ProjectId is not null)
            parts.Add($"project: {GetProjectName(chat.ProjectId)}");
        if (chat.AgentId is not null)
            parts.Add($"Lumi: {GetAgentName(chat.AgentId)}");
        parts.Add($"updated {DescribeRelative(chat.UpdatedAt)}");
        return string.Join(" · ", parts);
    }

    private string DescribeLiveState(Chat chat)
    {
        var running = chat.IsRunning || _registry.TryGetLiveOwner(chat.Id, out _);
        if (running)
        {
            if (_runs.TryGetValue(chat.Id, out var run))
                return $"running (started by you {DescribeRelative(run.StartedAt)})";
            return "running";
        }

        return chat.HasUnreadMessages ? "idle · unread reply waiting" : "idle";
    }

    private string DescribeRelative(DateTimeOffset when)
    {
        var delta = _now() - when;
        if (delta < TimeSpan.Zero)
            delta = TimeSpan.Zero;
        if (delta.TotalSeconds < 45)
            return "just now";
        if (delta.TotalMinutes < 60)
            return $"{(int)Math.Round(delta.TotalMinutes)}m ago";
        if (delta.TotalHours < 24)
            return $"{(int)Math.Round(delta.TotalHours)}h ago";
        if (delta.TotalDays < 7)
            return $"{(int)Math.Round(delta.TotalDays)}d ago";
        return when.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture);
    }

    private void RaiseChatsChanged()
    {
        var handler = ChatsChanged;
        if (handler is null)
            return;

        if (Dispatcher.UIThread.CheckAccess())
            handler();
        else
            Dispatcher.UIThread.Post(handler);
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

    private static string NormalizeAction(string? action)
        => (action ?? "").Trim().ToLowerInvariant();

    private static string Quote(string text) => $"\"{text}\"";

    private static string Collapse(string text)
        => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Truncate(string text, int max)
    {
        text ??= "";
        if (text.Length <= max)
            return text;
        return text[..Math.Max(0, max - 1)].TrimEnd() + "…";
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;
        _isDisposed = true;

        try { _lifetimeCts.Cancel(); }
        catch (ObjectDisposedException) { }
        _lifetimeCts.Dispose();
        _runs.Clear();
    }

    private sealed record OrchestrationRun(DateTimeOffset StartedAt, string Message, Task Task);

    private sealed record StatusSnapshot(
        Chat Chat,
        string LiveState,
        string Meta,
        List<ChatMessage>? LoadedMessages);
}
