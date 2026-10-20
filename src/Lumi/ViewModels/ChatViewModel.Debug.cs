#if DEBUG
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using GitHub.Copilot.Rpc;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    private Task? _debugPermissionProbeTask;

    /// <summary>
    /// Raises real native approval requests (a command, a new file and a Lumi tool) in a fresh chat of
    /// an isolated Debug profile, without a model message, so the approval cards can be inspected.
    /// </summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void StartDebugPermissionProbe()
    {
        var profile = Environment.GetEnvironmentVariable("LUMI_APPDATA_DIR");
        if (string.IsNullOrWhiteSpace(profile))
            throw new InvalidOperationException("Permission probes require an isolated LUMI_APPDATA_DIR.");
        if (CurrentChat is not { } chat || chat.Messages.Count != 0 || chat.CopilotSessionId is not null
            || _debugPermissionProbeTask is { IsCompleted: false })
            throw new InvalidOperationException("Create a fresh, empty isolated Debug chat for this probe.");
        var root = System.IO.Path.GetFullPath(profile);
        // A project made through the debug bridge shows in Customize; the probe only writes inside the profile.
        var project = chat.ProjectId is { } projectId
            ? _dataStore.Data.Projects.FirstOrDefault(candidate => candidate.Id == projectId)
            : null;
        if (project?.WorkingDirectory is not { Length: > 0 } projectFolder
            || !System.IO.Path.GetFullPath(projectFolder).StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            var probeFolder = System.IO.Path.Combine(root, "approval-probe");
            project = _dataStore.Data.Projects.FirstOrDefault(candidate => candidate.Name == "Approval probe");
            if (project is null)
            {
                project = new Project { Name = "Approval probe", WorkingDirectory = probeFolder };
                _dataStore.Data.Projects.Add(project);
            }
            projectFolder = project.WorkingDirectory ?? probeFolder;
        }
        var folder = System.IO.Path.GetFullPath(projectFolder);
        System.IO.Directory.CreateDirectory(folder);
        chat.ProjectId = project.Id;
        chat.ApprovalMode = ApprovalMode.AskFirst;
        chat.HasExplicitMcpServerSelection = true;
        chat.ActiveMcpServerNames.Clear();
        NotifyPermissionPropertiesChanged();
        _debugPermissionProbeTask = RunDebugPermissionProbeAsync(chat, folder);
    }

    private async Task RunDebugPermissionProbeAsync(Chat chat, string folder)
    {
        var cancellation = new CancellationTokenSource();
        var ct = cancellation.Token;
        _ctsSources[chat.Id] = cancellation;
        ClearManualStopRequested(chat.Id);
        var runtime = GetOrCreateRuntimeState(chat.Id);
        MarkRuntimeActive(runtime, "Preparing native approval probe");
        if (CurrentChat?.Id == chat.Id)
            ApplyDisplayedRuntimeState(runtime);
        string? errorMessage = null;
        try
        {
            if (!_copilotService.IsConnected)
                await _copilotService.ConnectAsync();
            ct.ThrowIfCancellationRequested();
            if (!await EnsureSessionAsync(chat, ct)
                || !_sessionCache.TryGetValue(chat.Id, out var session))
                throw new InvalidOperationException("The isolated native probe session wasn't created.");
            // No message is sent, so build the session's tool set now, as a turn would.
            await session.Rpc.Tools.InitializeAndValidateAsync(ct);
            System.Text.Json.JsonElement Arguments(string json)
            {
                using var document = System.Text.Json.JsonDocument.Parse(json);
                return document.RootElement.Clone();
            }
            string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value, AppDataJsonContext.Default.String);
            var shell = OperatingSystem.IsWindows() ? "powershell" : "bash";
            var notes = System.IO.Path.Combine(folder, "notes.md");
            await Task.WhenAll(
                session.Rpc.Tools.ExecuteAsync(shell, Arguments(
                    "{\"command\":\"echo 'Hello from the approval probe'\",\"description\":\"Say hello from the approval probe\",\"mode\":\"sync\",\"initial_wait\":10}"),
                    "debug-approval-command", ct),
                session.Rpc.Tools.ExecuteAsync("create", Arguments(
                    "{\"path\":" + Json(notes) + ",\"file_text\":" + Json("# Approval probe\n\nWritten only after you allowed it.\n") + "}"),
                    "debug-approval-edit", ct),
                session.Rpc.Tools.ExecuteAsync("manage_current_chat", Arguments("{\"action\":\"get\"}"),
                    "debug-approval-lumi-tool", ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            errorMessage = "Native approval probe stopped.";
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("[Approvals] Isolated Debug probe failed: {0}", error);
            errorMessage = "Native approval probe failed: " + error.Message;
        }
        finally
        {
            var ownsRequest = _ctsSources.TryGetValue(chat.Id, out var current) && ReferenceEquals(current, cancellation);
            if (ownsRequest)
                ReleaseChatCancellation(chat.Id, cancel: false);
            if (ownsRequest || !_ctsSources.ContainsKey(chat.Id))
            {
                MarkRuntimeTerminal(runtime);
                if (CurrentChat?.Id == chat.Id)
                {
                    ApplyDisplayedRuntimeState(runtime);
                    StatusText = errorMessage ?? "Native approval probe finished. No model message was sent.";
                }
            }
            _debugPermissionProbeTask = null;
        }
    }

    /// <summary>Opens a local browser fixture without a model turn, only in an isolated Debug app.</summary>
    internal async Task<string> LoadDebugBrowserFixtureAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LUMI_APPDATA_DIR")))
            throw new InvalidOperationException("Browser fixtures require an isolated LUMI_APPDATA_DIR.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile
            || !System.IO.File.Exists(uri.LocalPath))
            throw new InvalidOperationException("Browser fixtures require an existing local file URL.");
        if (CurrentChat is null)
            LoadDebugTranscriptFixture();
        var browser = GetOrCreateBrowserService(CurrentChat!.Id);
        HasUsedBrowser = true;
        RequestShowBrowser();
        return await browser.OpenAndSnapshotAsync(url);
    }

    public void LoadDebugTranscriptFixture(Chat? preparedFixture = null)
    {
        ClearChat();

        var fixture = preparedFixture ?? DebugAgentHarness.CreateTranscriptFixtureChat(_dataStore);

        BrowserHideRequested?.Invoke();
        DiffHideRequested?.Invoke();
        PlanHideRequested?.Invoke();
        ClearSuggestions();

        _pendingSkillInjections.Clear();
        _activeExternalSkillNames.Clear();

        IsBusy = false;
        IsStreaming = false;
        HasUsedBrowser = false;
        IsBrowserOpen = false;
        StatusText = preparedFixture is null ? "Debug transcript fixture" : "Interactive UI lab";
        TotalInputTokens = fixture.TotalInputTokens;
        TotalOutputTokens = fixture.TotalOutputTokens;
        fixture.ContextCurrentTokens = fixture.TotalInputTokens + fixture.TotalOutputTokens;
        fixture.HasExactContextUsage = true;
        fixture.ContextTokenLimit = 128000;
        ContextCurrentTokens = fixture.ContextCurrentTokens;
        ContextTokenLimit = fixture.ContextTokenLimit;
        HasPlan = !string.IsNullOrWhiteSpace(fixture.PlanContent);
        PlanContent = fixture.PlanContent;
        ActiveAgent = null;
        SelectedSdkAgentName = null;
        SelectedAgentName = null;
        SelectedAgentGlyph = "D";

        ActiveSkillIds.Clear();
        ActiveSkillChips.Clear();
        ActiveMcpServerNames.Clear();
        ActiveMcpChips.Clear();
        PendingAttachments.Clear();
        PendingAttachmentItems.Clear();

        _isBulkLoadingMessages = true;
        try
        {
            Messages.Clear();
            foreach (var msg in fixture.Messages)
                Messages.Add(new ChatMessageViewModel(msg));

            CurrentChat = fixture;
            LoadDebugContextWindowDetails();
            PromptText = "";
            RebuildTranscript();
            if (preparedFixture is null)
                _transcriptBuilder.AppendPlanCardToLastTurn("Debug plan", () => PlanShowRequested?.Invoke());
        }
        finally
        {
            _isBulkLoadingMessages = false;
        }

        if (fixture.Messages.SelectMany(m => m.ActiveSkills).FirstOrDefault() is { } skill)
        {
            ActiveSkillChips.Add(new StrataTheme.Controls.StrataComposerChip(skill.Name, skill.Glyph));
        }

        RefreshProjectBadge();
        RefreshAgentBadge();
        RefreshComposerCatalogs();

        // Seed representative suggestion chips so the post-turn suggestion row is inspectable in the
        // fixture. SuggestionA is intentionally too long for the row, which trims it to keep all three on
        // one line and exercises the hover tooltip that reveals the full suggestion text.
        if (preparedFixture is null)
        {
            SuggestionA = "Walk me through exactly how the streaming token accumulator throttles UI updates during a very long assistant response";
            SuggestionB = "Summarize the root cause";
            SuggestionC = "Show the diff that fixed the freeze";
        }

        // Feed this tail through the live collection path, not Rebuild, so the fixture exercises
        // progressive folding before a final answer (without sending anything to Copilot).
        foreach (var message in preparedFixture is null
                     ? DebugAgentHarness.CreateActivityTrailFixtureMessages() : [])
        {
            fixture.Messages.Add(message);
            Messages.Add(new ChatMessageViewModel(message));
        }

        foreach (var message in DebugAgentHarness.CreatePromptSharingFixtureMessages(_dataStore))
        {
            fixture.Messages.Add(message);
            Messages.Add(new ChatMessageViewModel(message));
        }

        foreach (var message in preparedFixture is null
                     ? DebugAgentHarness.CreateTextSelectionFixtureMessages() : [])
        {
            fixture.Messages.Add(message);
            Messages.Add(new ChatMessageViewModel(message));
        }

        if (preparedFixture is null)
        {
            var connector = new ChatMessage
            {
                Role = "tool",
                ToolCallId = "debug-connector-progress",
                ToolName = "workiq-search",
                ToolStatus = "InProgress",
                ToolStartedAt = DateTimeOffset.UtcNow,
                Content = "{\"query\":\"Find the project update\"}",
            };
            fixture.Messages.Add(connector);
            Messages.Add(new ChatMessageViewModel(connector));
            _transcriptBuilder.UpdateToolProgress(connector.ToolCallId, """
                ## Searching **Microsoft 365**

                This is a synthetic connector progress update; no account is being queried.

                - Checked **mail** and `calendar`
                - Searching shared documents
                - Preparing a summary with [source links](https://example.com)
                """);
            if (_dataStore.Data.Settings.ShowToolCalls)
            {
                var group = TranscriptTurns.SelectMany(turn => turn.Items).OfType<ToolGroupItem>().Last();
                group.IsExpanded = true;
                group.ToolCalls.OfType<ToolCallItem>().Last().IsExpanded = true;
            }
        }

        ScrollToEndRequested?.Invoke();
    }

    // ---- Simulated concurrent streaming (UI responsiveness harness) ------------------------------
    // Reproduces the real cost of "multiple running chats" by driving Lumi's actual streaming
    // primitives — the production StreamingTextAccumulator (50ms throttle), the Messages -> transcript
    // pipeline, NotifyContentChanged, ScrollToEndRequested, and runtime-active marking (which pins the
    // surface as live and lights the sidebar busy indicator). No real Copilot is involved, so the load
    // is deterministic and repeatable. Only the displayed surface mutates the visible Messages
    // collection; background surfaces still run their throttled flush so the UI-thread load matches a
    // real session with several agents streaming at once.

    private CancellationTokenSource? _debugStreamCts;
    private StreamingTextAccumulator? _debugStreamAccumulator;
    private ChatMessage? _debugStreamMsg;
    private ChatMessageViewModel? _debugStreamVm;
    private Func<bool>? _debugStreamIsDisplayed;
    private Guid _debugStreamChatId;

    private static readonly string[] DebugStreamWords =
    {
        "lumi", "agent", "streaming", "responsive", "transcript", "render", "dispatcher", "throttle",
        "token", "markdown", "viewport", "switch", "latency", "frame", "thread", "workload", "measure",
        "optimize", "session", "concurrent", "background", "surface", "paging", "mounted", "turn",
    };

    /// <summary>
    /// Starts a faithful, deterministic streaming simulation on this surface's current chat. Used by
    /// the UI responsiveness harness to replicate concurrent "running chats" load. Must be called on
    /// the UI thread. <paramref name="isDisplayed"/> reports whether this surface is the one currently
    /// shown (so background surfaces skip UI work, exactly like the real streaming path).
    /// </summary>
    public void DebugStartSimulatedStreaming(Func<bool> isDisplayed, int deltaIntervalMs = 25, int maxTurnChars = 1800)
    {
        if (CurrentChat is not { } chat)
            return;

        DebugStopSimulatedStreaming();

        _debugStreamChatId = chat.Id;
        _debugStreamIsDisplayed = isDisplayed;

        var runtime = GetOrCreateRuntimeState(chat.Id);
        MarkRuntimeActive(runtime, Loc.Status_Generating);
        if (isDisplayed())
            ApplyDisplayedRuntimeState(runtime);

        var agentName = chat.AgentId.HasValue
            ? _dataStore.Data.Agents.FirstOrDefault(agent => agent.Id == chat.AgentId.Value)?.Name ?? Loc.Author_Lumi
            : Loc.Author_Lumi;
        var modelId = _dataStore.Data.Settings.PreferredModel;

        StreamingTextAccumulator? accumulator = null;
        accumulator = new StreamingTextAccumulator(
            4096,
            TimeSpan.FromMilliseconds(StreamingUiUpdateThrottleMs),
            () => DebugFlushSimulatedStreaming(chat, accumulator!, agentName, modelId));
        _debugStreamAccumulator = accumulator;

        var cts = new CancellationTokenSource();
        _debugStreamCts = cts;
        var token = cts.Token;
        var driver = accumulator;

        // Deltas arrive off the UI thread (like the SDK); only the throttled flush touches the UI.
        _ = Task.Run(async () =>
        {
            var random = new Random(chat.Id.GetHashCode());
            var turnChars = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var delta = DebugNextStreamDelta(random);
                    driver.Append(delta);
                    turnChars += delta.Length;

                    // Bound each simulated "turn" so a background surface (whose flush only updates the
                    // off-screen in-progress message) doesn't grow its buffer without limit. Real turns
                    // end and clear the buffer; this keeps the load deterministic and the per-flush
                    // content comparable across surfaces.
                    if (turnChars >= maxTurnChars)
                    {
                        driver.Reset();
                        turnChars = 0;
                    }

                    await Task.Delay(deltaIntervalMs, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    /// <summary>Stops the simulated stream and releases this surface's live/runtime state. UI thread.</summary>
    public void DebugStopSimulatedStreaming()
    {
        _debugStreamCts?.Cancel();
        _debugStreamCts?.Dispose();
        _debugStreamCts = null;

        _debugStreamAccumulator?.Dispose();
        _debugStreamAccumulator = null;

        var displayed = _debugStreamIsDisplayed?.Invoke() == true;

        if (_debugStreamChatId != Guid.Empty)
        {
            _inProgressMessages.Remove(_debugStreamChatId);
            if (_runtimeStates.TryGetValue(_debugStreamChatId, out var runtime))
            {
                MarkRuntimeTerminal(runtime);
                if (displayed)
                    ApplyDisplayedRuntimeState(runtime);
            }
        }

        if (_debugStreamMsg is not null)
            _debugStreamMsg.IsStreaming = false;
        if (displayed)
            _debugStreamVm?.NotifyStreamingEnded();

        _debugStreamMsg = null;
        _debugStreamVm = null;
        _debugStreamIsDisplayed = null;
        _debugStreamChatId = Guid.Empty;
    }

    private void DebugFlushSimulatedStreaming(
        Chat chat,
        StreamingTextAccumulator accumulator,
        string agentName,
        string? modelId)
    {
        // Stale flush from a previous run that lost the throttle race.
        if (!ReferenceEquals(_debugStreamAccumulator, accumulator))
            return;

        var content = accumulator.SnapshotOrNull();
        if (content is null)
            return;

        var displayed = _debugStreamIsDisplayed?.Invoke() == true;

        if (_debugStreamMsg is null)
        {
            _debugStreamMsg = new ChatMessage
            {
                Role = "assistant",
                Author = agentName,
                Content = content,
                IsStreaming = true,
                Model = modelId,
            };
            _inProgressMessages[chat.Id] = _debugStreamMsg;
            if (displayed)
            {
                _debugStreamVm = new ChatMessageViewModel(_debugStreamMsg);
                Messages.Add(_debugStreamVm);
                ScrollToEndRequested?.Invoke();
            }
        }
        else if (!string.Equals(_debugStreamMsg.Content, content, StringComparison.Ordinal))
        {
            _debugStreamMsg.Content = content;
            if (displayed)
            {
                _debugStreamVm?.NotifyContentChanged();
                ScrollToEndRequested?.Invoke();
            }
        }
    }

    private static string DebugNextStreamDelta(Random random)
    {
        var wordCount = 2 + random.Next(4);
        var span = new System.Text.StringBuilder(48);
        for (var i = 0; i < wordCount; i++)
            span.Append(DebugStreamWords[random.Next(DebugStreamWords.Length)]).Append(' ');

        // Sprinkle in light markdown structure so the renderer does representative parse work.
        return random.Next(7) switch
        {
            0 => "\n\n- " + span,
            1 => "**" + span.ToString().Trim() + "** ",
            2 => "\n\n",
            3 => "`" + span.ToString().Trim() + "` ",
            _ => span.ToString(),
        };
    }

    // ---- Honest background-shell repro (Adir's "looks stuck" scenario) ---------------------------
    // Deterministically reproduces the exact state that used to look stuck: the assistant's turn has
    // ended, the powershell tool call reported "Completed" within a fraction of a second, yet the OS
    // process it launched (mode: async) keeps running for a long time. With the fix, the terminal card
    // stays honestly "Running in background" with a live ticking clock + streaming output tail, its
    // tool group stays expanded, and the bottom line reads "Running in background · <elapsed>" instead
    // of a generic "Generating…" spinner. No real Copilot/shell is involved, so it is instant and
    // repeatable — perfect for inspecting the UX on demand. The state is driven live by a 1s timer.
    //
    // The session also runs a background agent (with its run in the transcript) and a client-owned task,
    // so the Background activity list shows every kind of row with live progress: the command's newest
    // output, the agent's latest step (its row opens the run on the Agents page) and the task's progress.

    private const string DebugBackgroundShellToolCallId = "debug-bgshell-root";
    private const string DebugBackgroundShellTaskId = "debug-bgshell-task";
    private const string DebugBackgroundAgentToolCallId = "debug-bgagent-root";
    private const string DebugBackgroundAgentTaskId = "debug-bgagent-task";
    private const string DebugBackgroundClientTaskId = "debug-bgclient-task";

    private static readonly (string Tool, string ArgsJson)[] DebugBackgroundAgentSteps =
    [
        ("view", "{\"path\":\"src\\\\Lumi\\\\ViewModels\\\\ChatViewModel.SessionLifecycle.cs\"}"),
        ("grep", "{\"pattern\":\"ReleaseSessionResources\",\"glob\":\"*.cs\"}"),
        ("view", "{\"path\":\"src\\\\Lumi\\\\ViewModels\\\\ChatViewModel.ChatStateCleanup.cs\"}"),
        ("powershell", "{\"command\":\"dotnet test tests\\\\Lumi.Tests --filter SessionCleanup\",\"description\":\"Run cleanup tests\"}"),
    ];

    private DispatcherTimer? _debugBgShellTimer;
    private DateTime _debugBgShellStartUtc;
    private Guid _debugBgShellChatId;
    private string? _debugBgShellToolCallId;
    private readonly StringBuilder _debugBgShellOutput = new();
    private int _debugBgShellTick;

    public void LoadDebugBackgroundShellFixture()
    {
        DebugStopBackgroundShellFixture();
        ClearChat();

        BrowserHideRequested?.Invoke();
        DiffHideRequested?.Invoke();
        PlanHideRequested?.Invoke();
        ClearSuggestions();

        _pendingSkillInjections.Clear();
        _activeExternalSkillNames.Clear();
        ActiveSkillIds.Clear();
        ActiveSkillChips.Clear();
        ActiveMcpServerNames.Clear();
        ActiveMcpChips.Clear();
        PendingAttachments.Clear();
        PendingAttachmentItems.Clear();

        HasUsedBrowser = false;
        IsBrowserOpen = false;
        HasPlan = false;
        ActiveAgent = null;
        SelectedSdkAgentName = null;
        SelectedAgentName = null;

        var chat = new Chat { Title = "Background shell (debug)" };
        chat.Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = "I'm doing a UI test for a long-running operation. Run a powershell script that "
                + "waits 10 minutes and end your turn without stopping it.",
        });
        chat.Messages.Add(new ChatMessage
        {
            Role = "assistant",
            Author = Loc.Author_Lumi,
            Content = "Started a background process that runs for a while. It keeps going after my turn "
                + "ends — you can watch its progress below, and I'll continue automatically once it finishes.",
        });
        chat.Messages.Add(new ChatMessage
        {
            Role = "tool",
            ToolName = "powershell",
            ToolStatus = "Completed",
            ToolCallId = DebugBackgroundShellToolCallId,
            Content = "{\"command\":\"Start-Sleep -Seconds 600\",\"mode\":\"async\","
                + "\"description\":\"Wait 10 minutes without blocking the turn\"}",
            ToolOutput = "Started background job (async). Process is running…",
        });

        var fixtureStart = DateTimeOffset.UtcNow;
        chat.Messages.Add(new ChatMessage
        {
            Role = "tool",
            ToolName = "task",
            ToolStatus = "InProgress",
            ToolCallId = DebugBackgroundAgentToolCallId,
            ToolStartedAt = fixtureStart,
            Content = "{\"description\":\"Review session cleanup paths\",\"agent_type\":\"code-review\","
                + "\"agentName\":\"code-review\",\"agentDisplayName\":\"Code review agent\","
                + "\"mode\":\"background\",\"model\":\"claude-sonnet-4.5\","
                + "\"prompt\":\"Review how Lumi releases a chat's session resources. Report leaks or races.\"}",
        });
        chat.Messages.Add(DebugBackgroundAgentStep(0, fixtureStart));

        var runtime = GetOrCreateRuntimeState(chat.Id);
        MarkSessionBackgroundActive(runtime);
        MarkAssistantIdle(runtime);

        _isBulkLoadingMessages = true;
        try
        {
            Messages.Clear();
            foreach (var msg in chat.Messages)
                Messages.Add(new ChatMessageViewModel(msg));

            CurrentChat = chat;
            PromptText = "";
            ApplyDisplayedRuntimeState(runtime);
            RebuildTranscript();
        }
        finally
        {
            _isBulkLoadingMessages = false;
        }

        RefreshProjectBadge();
        RefreshAgentBadge();
        RefreshComposerCatalogs();

        // The fix under test: keep the terminal card honestly "running in background" even though its
        // tool call already resolved to Completed. This drives the amber running state + live clock and
        // keeps the enclosing tool group expanded so the work never looks finished prematurely.
        _transcriptBuilder.SetTerminalRunningInBackground(DebugBackgroundShellToolCallId, true);

        _debugBgShellChatId = chat.Id;
        _debugBgShellToolCallId = DebugBackgroundShellToolCallId;
        _debugBgShellStartUtc = DateTime.UtcNow;
        _debugBgShellTick = 0;
        _debugBgShellOutput.Clear();
        _debugBgShellOutput.AppendLine("Started background job (async). Process is running…");
        _transcriptBuilder.UpdateTerminalOutput(
            DebugBackgroundShellToolCallId, _debugBgShellOutput.ToString().TrimEnd(), true);

        // What the session's Tasks API would report for this chat.
        ApplyBackgroundActivitySnapshot(
        [
            new TaskInfoShell
            {
                Id = DebugBackgroundShellTaskId,
                Description = "Wait 10 minutes without blocking the turn",
                Command = "Start-Sleep -Seconds 600",
                Status = GitHub.Copilot.Rpc.TaskStatus.Running,
                ExecutionMode = TaskExecutionMode.Background,
                AttachmentMode = TaskShellInfoAttachmentMode.Attached,
                StartedAt = fixtureStart,
            },
            new TaskInfoAgent
            {
                Id = DebugBackgroundAgentTaskId,
                AgentType = "code-review",
                DisplayName = "Code review agent",
                Description = "Review session cleanup paths",
                Prompt = "Review how Lumi releases a chat's session resources.",
                ToolCallId = DebugBackgroundAgentToolCallId,
                Status = GitHub.Copilot.Rpc.TaskStatus.Running,
                ExecutionMode = TaskExecutionMode.Background,
                StartedAt = fixtureStart,
            },
            new TaskInfoClient
            {
                Id = DebugBackgroundClientTaskId,
                ClientTaskId = DebugBackgroundClientTaskId,
                DisplayName = "Index workspace files",
                Description = "Building the search index for this project",
                ActiveTimeMs = 0,
                CanCancel = true,
                ExecutionMode = new TaskClientExecutionMode("background"),
                Owner = new TaskClientOwner(),
                Sequence = 1,
                StartedAt = fixtureStart,
                UpdatedAt = fixtureStart,
                Status = TaskClientStatus.Running,
            },
        ], fixtureStart);
        ApplyDebugBackgroundProgress();
        UpdateBackgroundStatusLine(chat.Id);

        _debugBgShellTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _debugBgShellTimer.Tick += (_, _) => DebugBackgroundShellTick();
        _debugBgShellTimer.Start();

        ScrollToEndRequested?.Invoke();
    }

    private static ChatMessage DebugBackgroundAgentStep(int step, DateTimeOffset startedAt)
    {
        var (tool, args) = DebugBackgroundAgentSteps[step % DebugBackgroundAgentSteps.Length];
        return new ChatMessage
        {
            Role = "tool",
            ToolName = tool,
            ToolStatus = "Completed",
            ToolCallId = $"debug-bgagent-step-{step}",
            ParentToolCallId = DebugBackgroundAgentToolCallId,
            ToolStartedAt = startedAt,
            ToolDurationMs = 420,
            Content = args,
        };
    }

    /// <summary>
    /// Feeds the command and task rows what the Tasks API's progress reads would return. The agent's
    /// row is its run, which the steps added on each tick keep live.
    /// </summary>
    private void ApplyDebugBackgroundProgress()
    {
        FindSessionActivity(DebugBackgroundShellTaskId)?.ApplyProgress(
            new TaskProgressShell { RecentOutput = _debugBgShellOutput.ToString() });

        var percentage = Math.Min(100, 8 + _debugBgShellTick * 3);
        FindSessionActivity(DebugBackgroundClientTaskId)?.ApplyProgress(new TaskProgressClient
        {
            LastMessage = $"Indexed {percentage * 12:N0} of 1,200 files",
            Percentage = percentage,
            Phase = "Indexing",
            RecentActivity = [],
            Sequence = _debugBgShellTick,
            Status = TaskClientStatus.Running,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    private void DebugBackgroundShellTick()
    {
        if (_debugBgShellToolCallId is null || CurrentChat?.Id != _debugBgShellChatId)
        {
            DebugStopBackgroundShellFixture();
            return;
        }

        _debugBgShellTick++;
        var elapsed = DateTime.UtcNow - _debugBgShellStartUtc;

        StatusText = string.Format(Loc.Status_BackgroundRunning, FormatCompactElapsed(elapsed));

        // Stream a fresh output line every couple of seconds so the card visibly "breathes".
        if (_debugBgShellTick % 2 == 0)
        {
            _debugBgShellOutput.AppendLine(
                $"[{FormatCompactElapsed(elapsed)}] still working… heartbeat {_debugBgShellTick / 2}");
            _transcriptBuilder.UpdateTerminalOutput(
                _debugBgShellToolCallId, _debugBgShellOutput.ToString().TrimEnd(), true);
        }

        // The agent takes a new step every few seconds, through the same live path as real events.
        if (_debugBgShellTick % 4 == 0 && CurrentChat is { } chat)
        {
            var step = DebugBackgroundAgentStep(_debugBgShellTick / 4, DateTimeOffset.UtcNow);
            chat.Messages.Add(step);
            Messages.Add(new ChatMessageViewModel(step));
        }

        ApplyDebugBackgroundProgress();
        UpdateBackgroundStatusLine(_debugBgShellChatId);
    }

    public void DebugStopBackgroundShellFixture()
    {
        _debugBgShellTimer?.Stop();
        _debugBgShellTimer = null;
        _debugBgShellChatId = Guid.Empty;
        _debugBgShellToolCallId = null;
        _debugBgShellTick = 0;
        _debugBgShellOutput.Clear();
    }
}
#endif
