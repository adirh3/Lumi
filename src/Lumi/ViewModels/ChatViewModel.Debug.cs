#if DEBUG
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    public void LoadDebugTranscriptFixture()
    {
        ClearChat();

        var fixture = DebugAgentHarness.CreateTranscriptFixtureChat(_dataStore);

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
        StatusText = "Debug transcript fixture";
        TotalInputTokens = fixture.TotalInputTokens;
        TotalOutputTokens = fixture.TotalOutputTokens;
        fixture.ContextCurrentTokens = fixture.TotalInputTokens + fixture.TotalOutputTokens;
        fixture.ContextTokenLimit = 128000;
        ContextCurrentTokens = fixture.ContextCurrentTokens;
        ContextTokenLimit = fixture.ContextTokenLimit;
        HasPlan = true;
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
            PromptText = "";
            RebuildTranscript();
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
        // fixture. SuggestionA is intentionally long enough to truncate at the chip MaxWidth, which
        // exercises the hover tooltip that reveals the full suggestion text.
        SuggestionA = "Walk me through exactly how the streaming token accumulator throttles UI updates during a very long assistant response";
        SuggestionB = "Summarize the root cause";
        SuggestionC = "Show the diff that fixed the freeze";

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

    private const string DebugBackgroundShellToolCallId = "debug-bgshell-root";

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

        var runtime = GetOrCreateRuntimeState(chat.Id);
        MarkRuntimeActive(
            runtime,
            string.Format(Loc.Status_BackgroundRunning, FormatCompactElapsed(TimeSpan.Zero)),
            isStreaming: false,
            hasPendingBackgroundWork: true);

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

        _debugBgShellTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _debugBgShellTimer.Tick += (_, _) => DebugBackgroundShellTick();
        _debugBgShellTimer.Start();

        ScrollToEndRequested?.Invoke();
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
