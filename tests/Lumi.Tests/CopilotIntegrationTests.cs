using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot.SDK;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Microsoft.Extensions.AI;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Integration tests for the Copilot SDK through <see cref="CopilotService"/>
/// and <see cref="SessionConfigBuilder"/>.  Every test that calls the SDK is
/// gated behind LUMI_INTEGRATION_TESTS=1. Pure-unit tests are always-on.
///
/// Scenario coverage (maps 1-to-1 with the user-visible actions in Lumi):
///
///  1. New chat — create session, send, receive streaming response
///  2. Resume — session resume preserves conversation memory
///  3. Edit message — re-send corrected content as a new turn
///  4. Regenerate — re-send identical content to get a fresh response
///  5. Stop / abort — cancel mid-stream, continue afterwards
///  6. Custom tools — tool invocation, start/complete events, hook call
///  7. Multiple tools — several tools on one session
///  8. Working directory — ConfigDir and WorkingDirectory propagation
///  9. Custom agents — sub-agent configs registered on session
/// 10. Skill directories — skill dir passed to session config
/// 11. InfiniteSessions — multi-turn without context window errors
/// 12. UserInputHandler — native question flow
/// 13. Session hooks — OnPreToolUse and OnErrorOccurred wired correctly
/// 14. Session list + delete — CRUD lifecycle
/// 15. Title generation — lightweight throwaway session
/// 16. Suggestion generation — parse JSON array from throwaway session
/// 17. Concurrent sessions — independent contexts
/// 18. Streaming event lifecycle — correct event ordering
/// 19. Reasoning effort — config propagation
/// 20. System prompt Append mode — merges with SDK default prompt
/// 21. Lightweight session — restricted tool set
/// 22. Session event replay — GetMessagesAsync returns log
/// 23. Session delete cleanup — server-side deletion
/// 24. Resume with fallback — resume failure falls back to fresh session
/// 25. Memory checkpoint extraction — explicit personal facts saved, technical context ignored
///
/// Unit tests (always run, no SDK connection):
/// U1. ExcludedTools set correctly
/// U2. ResumeConfig field parity
/// U3. Error hook wiring
/// U4. Nil / empty inputs produce safe defaults
/// U5. System prompt modes
/// U6. InfiniteSession config
/// U7. MCP server config building (McpLocalServerConfig / McpRemoteServerConfig)
/// </summary>
[Trait("Category", "Integration")]
public class CopilotIntegrationTests : IAsyncLifetime
{
    private CopilotService _service = null!;

    private static bool IsEnabled =>
        Environment.GetEnvironmentVariable("LUMI_INTEGRATION_TESTS") == "1";

    public async Task InitializeAsync()
    {
        if (!IsEnabled) return;
        _service = new CopilotService();
        await _service.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        if (_service is not null)
            await _service.DisposeAsync();
    }

    private void SkipIfDisabled() =>
        Skip.If(!IsEnabled, "Set LUMI_INTEGRATION_TESTS=1 to run SDK integration tests.");

    // ═══════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Awaits a task with a timeout, throwing <see cref="TimeoutException"/> on expiry.</summary>
    private static async Task<T> Timeout<T>(Task<T> task, int seconds = 45)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var delay = Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token);
        var winner = await Task.WhenAny(task, delay);
        if (winner == delay)
            throw new TimeoutException($"Timed out after {seconds}s");
        cts.Cancel();
        return await task;
    }

    /// <summary>
    /// Sends a prompt and waits for the final <see cref="AssistantMessageEvent"/>.
    /// Returns the assistant response text and the event subscription (caller must dispose).
    /// </summary>
    private static async Task<(string Response, IDisposable Sub)> SendAndWait(
        CopilotSession session, string prompt, int timeoutSeconds = 45)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent msg:
                    tcs.TrySetResult(msg.Data.Content ?? "");
                    break;
                case SessionErrorEvent err:
                    tcs.TrySetException(new Exception($"Session error: {err.Data.Message}"));
                    break;
            }
        });

        await session.SendAsync(new MessageOptions { Prompt = prompt });
        var response = await Timeout(tcs.Task, timeoutSeconds);
        return (response, sub);
    }

    /// <summary>Creates a bare session config with the given system prompt.</summary>
    private static SessionConfig SimpleConfig(string? systemPrompt = null) =>
        SessionConfigBuilder.Build(
            systemPrompt: systemPrompt,
            model: null, workingDirectory: null, skillDirectories: null,
            customAgents: null, tools: null, mcpServers: null,
            reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: null);

    private static HashSet<string> SnapshotSessionStateDirectories()
    {
        var root = GetSessionStateRoot();
        if (!Directory.Exists(root))
            return [];

        return Directory
            .GetDirectories(root)
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<List<string>> WaitForNewSessionDirsWithFirstUserAsync(
        HashSet<string> baseline,
        Func<string, bool> predicate,
        int attempts = 20,
        int delayMs = 500)
    {
        List<string> matches = [];
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await Task.Delay(delayMs);
            matches = GetNewSessionDirsWithFirstUser(baseline, predicate);
            if (matches.Count > 0)
                break;
        }

        return matches;
    }

    private static List<string> GetNewSessionDirsWithFirstUser(
        HashSet<string> baseline,
        Func<string, bool> predicate)
    {
        var root = GetSessionStateRoot();
        if (!Directory.Exists(root))
            return [];

        var matches = new List<string>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            var sessionId = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(sessionId) || baseline.Contains(sessionId))
                continue;

            var firstUserMessage = TryReadFirstUserMessage(dir);
            if (firstUserMessage is not null && predicate(firstUserMessage))
                matches.Add(sessionId);
        }

        return matches;
    }

    private static string? TryReadFirstUserMessage(string sessionDirectory)
    {
        var eventsPath = Path.Combine(sessionDirectory, "events.jsonl");
        if (!File.Exists(eventsPath))
            return null;

        foreach (var line in File.ReadLines(eventsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                if (!document.RootElement.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String
                    || !string.Equals(type.GetString(), "user.message", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!document.RootElement.TryGetProperty("data", out var data)
                    || !data.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                return content.GetString();
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return null;
    }

    private static string GetSessionStateRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot",
            "session-state");

    private static string BuildMemorySystemPrompt()
    {
        var method = typeof(MemoryAgentService).GetMethod(
            "BuildSystemPrompt",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(null, null));
    }

    private static string BuildMemoryCheckpointPrompt(MemoryAgentCheckpoint checkpoint)
    {
        var method = typeof(MemoryAgentService).GetMethod(
            "BuildCheckpointPrompt",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(null, [checkpoint]));
    }

    private static List<AIFunction> BuildMemoryTestTools(
        List<MemoryAgentSnapshot> existingMemories,
        List<(string Key, string Content, string? Category)> saves,
        List<(string Key, string? Content, string? NewKey, string? Category)> updates,
        List<string> deletes)
    {
        return
        [
            AIFunctionFactory.Create(
                ([Description("Brief label for the memory")] string key,
                 [Description("Full memory text with details")] string content,
                 [Description("Category")] string? category) =>
                {
                    saves.Add((key, content, category));
                    return Task.FromResult($"Memory saved: {key}");
                },
                "save_memory",
                "Save or update a persistent memory about the user"),

            AIFunctionFactory.Create(
                ([Description("Key of the memory to update")] string key,
                 [Description("New content text (optional)")] string? content,
                 [Description("New key if renaming (optional)")] string? newKey,
                 [Description("New category (optional)")] string? category) =>
                {
                    updates.Add((key, content, newKey, category));
                    return Task.FromResult($"Memory updated: {newKey ?? key}");
                },
                "update_memory",
                "Update an existing memory's content, key, or category"),

            AIFunctionFactory.Create(
                ([Description("Key of the memory to remove")] string key) =>
                {
                    deletes.Add(key);
                    return Task.FromResult($"Memory deleted: {key}");
                },
                "delete_memory",
                "Remove a memory that is no longer relevant"),

            AIFunctionFactory.Create(
                ([Description("Key of the memory to retrieve full content for")] string key) =>
                {
                    var match = existingMemories.FirstOrDefault(m =>
                        string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
                    return match?.Content ?? $"Memory not found: {key}";
                },
                "recall_memory",
                "Fetch the full content of a memory by its key")
        ];
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  1. New chat — create session + send + receive streaming
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task NewChat_SendMessage_ReceivesStreamingResponse()
    {
        SkipIfDisabled();

        var config = SimpleConfig("You are a helpful assistant. Always respond concisely.");
        var session = await _service.CreateSessionAsync(config);
        Assert.False(string.IsNullOrEmpty(session.SessionId));

        var receivedDelta = false;
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent:
                    receivedDelta = true;
                    break;
                case AssistantMessageEvent msg:
                    tcs.TrySetResult(msg.Data.Content ?? "");
                    break;
                case SessionErrorEvent err:
                    tcs.TrySetException(new Exception(err.Data.Message));
                    break;
            }
        });

        await session.SendAsync(new MessageOptions { Prompt = "Say hello in exactly 3 words." });
        var response = await Timeout(tcs.Task);

        Assert.True(receivedDelta, "Should have received streaming deltas");
        Assert.False(string.IsNullOrWhiteSpace(response));
        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  2. Resume session — reconnect preserves conversation memory
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task ResumeSession_MaintainsConversationContext()
    {
        SkipIfDisabled();

        var config = SimpleConfig("You are a helpful assistant. Remember everything.");
        var session1 = await _service.CreateSessionAsync(config);
        var sessionId = session1.SessionId;

        var uniqueWord = $"Zephyr{Random.Shared.Next(10000, 99999)}";
        var (_, sub1) = await SendAndWait(session1,
            $"Remember this word: {uniqueWord}. Just say OK.");
        sub1.Dispose();

        // Resume into a new session object
        var resumeConfig = SessionConfigBuilder.BuildForResume(
            systemPrompt: "You are a helpful assistant. Remember everything.",
            model: null, workingDirectory: null, skillDirectories: null,
            customAgents: null, tools: null, mcpServers: null,
            reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: null);
        var session2 = await _service.ResumeSessionAsync(sessionId, resumeConfig);
        Assert.Equal(sessionId, session2.SessionId);

        var (reply, sub2) = await SendAndWait(session2,
            "What word did I ask you to remember? Reply with just that word.");
        sub2.Dispose();

        Assert.Contains(uniqueWord, reply, StringComparison.OrdinalIgnoreCase);
        await session2.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  3. Edit message — corrected content sent as new turn
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task EditMessage_SendsNewTurnWithCorrection()
    {
        SkipIfDisabled();

        var config = SimpleConfig("You are a helpful assistant. Answer concisely.");
        var session = await _service.CreateSessionAsync(config);

        var (first, sub1) = await SendAndWait(session, "What is 2+2?");
        sub1.Dispose();
        Assert.NotEmpty(first);

        // "Edit" — send corrected question as a new turn
        var (edited, sub2) = await SendAndWait(session,
            "Actually, what is 2+3? Just the number.");
        sub2.Dispose();
        Assert.Contains("5", edited);

        await session.DisposeAsync();
    }

    [SkippableFact]
    public async Task EditMessage_RebuiltSession_DoesNotLeakOriginalPrompt()
    {
        SkipIfDisabled();

        // Original turn before edit
        var firstToken = $"APPLE_{Guid.NewGuid():N}";
        var secondToken = $"BANANA_{Guid.NewGuid():N}";

        var config = SimpleConfig("You are a helpful assistant. Follow memory questions exactly.");
        var originalSession = await _service.CreateSessionAsync(config);

        var (_, firstSub) = await SendAndWait(originalSession,
            $"Remember this secret token exactly: {firstToken}. Reply only OK.", 60);
        firstSub.Dispose();

        await originalSession.DisposeAsync();

        // Simulate edited resend behavior used by ChatViewModel:
        // create a fresh backend session + replay corrected context only.
        var replayBuilder = typeof(ChatViewModel).GetMethod(
            "BuildEditedReplayPrompt",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(replayBuilder);

        var retainedContext = new List<Lumi.Models.ChatMessage>();
        var correctedPrompt = $"Remember this secret token exactly: {secondToken}. Reply only OK.";
        var replayPrompt = (string?)replayBuilder!.Invoke(null, [retainedContext, correctedPrompt]);
        Assert.False(string.IsNullOrWhiteSpace(replayPrompt));

        var editedSession = await _service.CreateSessionAsync(config);
        var (_, editSub) = await SendAndWait(editedSession, replayPrompt!, 60);
        editSub.Dispose();

        var (recall, recallSub) = await SendAndWait(
            editedSession,
            "What is the secret token I asked you to remember? Reply with only the token.",
            60);
        recallSub.Dispose();

        Assert.Contains(secondToken, recall, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(firstToken, recall, StringComparison.OrdinalIgnoreCase);

        await editedSession.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  4. Regenerate — same content, fresh response
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task Regenerate_SamePrompt_ProducesFreshResponse()
    {
        SkipIfDisabled();

        var config = SimpleConfig("You are a helpful assistant. Always respond concisely.");
        var session = await _service.CreateSessionAsync(config);

        var (first, sub1) = await SendAndWait(session,
            "Give me one random fun fact about space.");
        sub1.Dispose();

        // Re-send exact same prompt (simulates regenerate)
        var (second, sub2) = await SendAndWait(session,
            "Give me one random fun fact about space.");
        sub2.Dispose();

        // Both should be non-empty responses
        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.False(string.IsNullOrWhiteSpace(second));
        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  5. Stop / abort mid-stream and continue
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task StopGeneration_ThenContinue()
    {
        SkipIfDisabled();

        var config = SimpleConfig("You are a helpful assistant.");
        var session = await _service.CreateSessionAsync(config);

        var gotDelta = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var sub1 = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent:
                    gotDelta.TrySetResult(true);
                    break;
                case AbortEvent:
                    abortHandled.TrySetResult(true);
                    break;
            }
        });

        await session.SendAsync(new MessageOptions
        {
            Prompt = "List the numbers from 1 to 200, one per line."
        });

        await Timeout(gotDelta.Task, 30);
        await session.AbortAsync();
        await Timeout(abortHandled.Task, 15);

        // Follow-up after abort — session must still be usable
        var (followUp, sub2) = await SendAndWait(session,
            "I stopped you. Just say OK to confirm you're still working.");
        sub2.Dispose();

        Assert.False(string.IsNullOrWhiteSpace(followUp));
        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  6. Custom tools — invocation + events + hook
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task CustomTool_IsInvoked_WithEventsAndHook()
    {
        SkipIfDisabled();

        var toolCalled = false;
        var preToolHookCalled = false;

        var tool = AIFunctionFactory.Create(
            ([Description("Name of the city")] string city) =>
            {
                toolCalled = true;
                return $"Sunny, 22°C in {city}.";
            },
            "get_weather",
            "Get the current weather for a city.");

        var hooks = new SessionHooks
        {
            OnPreToolUse = async (input, _) =>
            {
                preToolHookCalled = true;
                return new PreToolUseHookOutput { PermissionDecision = "allow" };
            }
        };

        var config = SessionConfigBuilder.Build(
            systemPrompt: "You are a weather assistant. You MUST use the get_weather tool whenever a user asks about weather. Do not answer weather questions without calling the tool first.",
            model: null, workingDirectory: null, skillDirectories: null,
            customAgents: null, tools: [tool], mcpServers: null,
            reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: hooks);

        var session = await _service.CreateSessionAsync(config);

        var toolStarted = false;
        var toolCompleted = false;
        var response = "";
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var sub = session.On(evt =>
        {
            switch (evt)
            {
                case ToolExecutionStartEvent:
                    toolStarted = true;
                    break;
                case ToolExecutionCompleteEvent:
                    toolCompleted = true;
                    break;
                case AssistantMessageEvent msg:
                    response = msg.Data.Content ?? "";
                    break;
                case SessionIdleEvent:
                    tcs.TrySetResult(true);
                    break;
                case SessionErrorEvent err:
                    tcs.TrySetException(new Exception(err.Data.Message));
                    break;
            }
        });

        await session.SendAsync(new MessageOptions { Prompt = "What's the weather in Paris? Use the get_weather tool." });
        await Timeout(tcs.Task, 60);

        Assert.True(toolCalled, "Custom tool function should have been called");
        Assert.True(preToolHookCalled, "OnPreToolUse hook should have fired");
        Assert.True(toolStarted, "ToolExecutionStartEvent should have fired");
        Assert.True(toolCompleted, "ToolExecutionCompleteEvent should have fired");
        Assert.Contains("22", response); // our tool returns 22°C

        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  7. Multiple tools on one session
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task MultipleTools_BothCanBeInvoked()
    {
        SkipIfDisabled();

        var weatherCalled = false;
        var timeCalled = false;

        var weatherTool = AIFunctionFactory.Create(
            ([Description("City name")] string city) =>
            {
                weatherCalled = true;
                return "Rainy, 15°C";
            },
            "get_weather", "Get current weather for a city.");

        var timeTool = AIFunctionFactory.Create(
            ([Description("City name")] string city) =>
            {
                timeCalled = true;
                return "2:30 PM";
            },
            "get_time", "Get current time in a city.");

        var config = SessionConfigBuilder.Build(
            systemPrompt: "You have get_weather and get_time tools. You MUST use BOTH tools whenever asked. Always call get_weather AND get_time. Never skip a tool.",
            model: null, workingDirectory: null, skillDirectories: null,
            customAgents: null, tools: [weatherTool, timeTool], mcpServers: null,
            reasoningEffort: null, userInputHandler: null,
            onPermission: null,
            hooks: new SessionHooks
            {
                OnPreToolUse = async (_, _) =>
                    new PreToolUseHookOutput { PermissionDecision = "allow" }
            });

        var session = await _service.CreateSessionAsync(config);

        var doneTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = session.On(evt =>
        {
            if (evt is SessionIdleEvent) doneTcs.TrySetResult(true);
            else if (evt is SessionErrorEvent err)
                doneTcs.TrySetException(new Exception(err.Data.Message));
        });

        await session.SendAsync(new MessageOptions
        {
            Prompt = "What's the weather AND the time in London? You MUST call both get_weather and get_time."
        });
        await Timeout(doneTcs.Task, 60);

        Assert.True(weatherCalled, "Weather tool should be called");
        Assert.True(timeCalled, "Time tool should be called");

        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  8. Working directory / ConfigDir
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task WorkingDirectory_PropagatedToSession()
    {
        SkipIfDisabled();

        var workDir = Environment.CurrentDirectory;
        var config = SessionConfigBuilder.Build(
            systemPrompt: "You are a coding assistant.",
            model: null, workingDirectory: workDir, skillDirectories: null,
            customAgents: null, tools: null, mcpServers: null,
            reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: null);

        Assert.Equal(workDir, config.WorkingDirectory);
        Assert.Equal(workDir, config.ConfigDir);

        var session = await _service.CreateSessionAsync(config);
        Assert.NotEmpty(session.SessionId);
        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  9. Custom agents — sub-agents registered on config
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task CustomAgents_RegisteredOnSession()
    {
        SkipIfDisabled();

        var agents = new List<CustomAgentConfig>
        {
            new() { Name = "weatherbot", DisplayName = "WeatherBot",
                     Description = "A bot that provides weather info" }
        };

        var config = SessionConfigBuilder.Build(
            systemPrompt: "You have access to sub-agents.",
            model: null, workingDirectory: null, skillDirectories: null,
            customAgents: agents, tools: null, mcpServers: null,
            reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: null);

        Assert.Equal(agents, config.CustomAgents);

        var session = await _service.CreateSessionAsync(config);
        Assert.NotEmpty(session.SessionId);
        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 10. Skill directories
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task SkillDirectories_PassedToSession()
    {
        SkipIfDisabled();

        var skillDir = System.IO.Path.GetTempPath();
        var config = SessionConfigBuilder.Build(
            systemPrompt: "You are a helpful assistant with skills.",
            model: null, workingDirectory: null,
            skillDirectories: [skillDir],
            customAgents: null, tools: null, mcpServers: null,
            reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: null);

        Assert.Contains(skillDir, config.SkillDirectories!);

        var session = await _service.CreateSessionAsync(config);
        Assert.NotEmpty(session.SessionId);
        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 11. InfiniteSessions — multiple turns without context errors
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task InfiniteSessions_MultiTurnWorks()
    {
        SkipIfDisabled();

        var config = SimpleConfig("You are a helpful assistant.");
        Assert.True(config.InfiniteSessions!.Enabled);

        var session = await _service.CreateSessionAsync(config);

        for (var i = 0; i < 5; i++)
        {
            var (resp, sub) = await SendAndWait(session,
                $"Say 'turn {i}' — nothing else.");
            sub.Dispose();
            Assert.False(string.IsNullOrWhiteSpace(resp), $"Turn {i} response should not be empty");
        }

        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 12. UserInputHandler — native question flow
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task UserInputHandler_WiredToConfig()
    {
        SkipIfDisabled();

        UserInputHandler handler = async (request, _) =>
        {
            return new UserInputResponse { Answer = "Blue", WasFreeform = true };
        };

        var config = SessionConfigBuilder.Build(
            systemPrompt: "When asked about preferences, ask the user.",
            model: null, workingDirectory: null, skillDirectories: null,
            customAgents: null, tools: null, mcpServers: null,
            reasoningEffort: null, userInputHandler: handler,
            onPermission: null, hooks: null);

        Assert.NotNull(config.OnUserInputRequest);

        var session = await _service.CreateSessionAsync(config);
        Assert.NotEmpty(session.SessionId);
        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 13. Session hooks — pre-tool-use + error hooks wired
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task SessionHooks_PreToolUse_FiredOnToolCall()
    {
        SkipIfDisabled();

        var preToolUseCalled = false;

        var tool = AIFunctionFactory.Create(
            ([Description("Expression to evaluate")] string expr) => "42",
            "calculate", "Perform a calculation.");

        var config = SessionConfigBuilder.Build(
            systemPrompt: "You are a calculator. You MUST use the calculate tool for every math question. Never compute anything yourself — always call the tool.",
            model: null, workingDirectory: null, skillDirectories: null,
            customAgents: null, tools: [tool], mcpServers: null,
            reasoningEffort: null, userInputHandler: null,
            onPermission: null,
            hooks: new SessionHooks
            {
                OnPreToolUse = async (input, _) =>
                {
                    preToolUseCalled = true;
                    return new PreToolUseHookOutput { PermissionDecision = "allow" };
                }
            });

        var session = await _service.CreateSessionAsync(config);

        var doneTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = session.On(evt =>
        {
            if (evt is SessionIdleEvent) doneTcs.TrySetResult(true);
            else if (evt is SessionErrorEvent err)
                doneTcs.TrySetException(new Exception(err.Data.Message));
        });

        await session.SendAsync(new MessageOptions
        {
            Prompt = "What is 6 * 7? Use the calculate tool."
        });
        await Timeout(doneTcs.Task, 60);

        Assert.True(preToolUseCalled, "OnPreToolUse should fire when a tool is used");
        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 14. Session list + delete
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task ListAndDeleteSession_Lifecycle()
    {
        SkipIfDisabled();

        var config = SimpleConfig("You are a test assistant.");
        var session = await _service.CreateSessionAsync(config);
        var sid = session.SessionId;

        // Send a message so the session is fully registered
        var (_, msgSub) = await SendAndWait(session, "Say OK.");
        msgSub.Dispose();

        // Session indexing may take a moment — retry a few times
        bool found = false;
        for (int attempt = 0; attempt < 5 && !found; attempt++)
        {
            var sessions = await _service.ListSessionsAsync();
            found = sessions.Any(s => s.SessionId == sid);
            if (!found) await Task.Delay(1000);
        }
        Assert.True(found, $"Session {sid} should appear in session list");

        await _service.DeleteSessionAsync(sid);

        var afterDelete = await _service.ListSessionsAsync();
        Assert.DoesNotContain(afterDelete, s => s.SessionId == sid);

        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 15. Title generation — lightweight session
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task TitleGeneration_ProducesTitle()
    {
        SkipIfDisabled();

        var title = await _service.GenerateTitleAsync("How do I make sourdough starter?");
        Assert.False(string.IsNullOrWhiteSpace(title));
        Assert.True(title!.Length <= 80, $"Title too long: {title}");
    }

    [SkippableFact]
    public async Task TitleGeneration_DoesNotLeaveTitleHelperSessionState()
    {
        SkipIfDisabled();

        var baseline = SnapshotSessionStateDirectories();

        var title = await _service.GenerateTitleAsync("How do I make sourdough starter?");

        Assert.False(string.IsNullOrWhiteSpace(title));

        var leakedTitleSessions = await WaitForNewSessionDirsWithFirstUserAsync(
            baseline,
            firstUser => string.Equals(firstUser.Trim(), "title:", StringComparison.Ordinal));

        Assert.Empty(leakedTitleSessions);
    }

    [SkippableFact]
    public async Task MemoryCheckpoint_SavesExplicitPreference_WithoutLeakingHelperPrompt()
    {
        SkipIfDisabled();

        var marker = $"MEMCHK_{Guid.NewGuid():N}";
        var userMessage =
            $"My favorite tea is jasmine green tea and it has been my favorite for years. Ignore test marker {marker}.";
        var assistantMessage =
            $"Got it — jasmine green tea is your longtime favorite. I will ignore the test marker {marker}.";

        var checkpoint = new MemoryAgentCheckpoint
        {
            ChatId = Guid.NewGuid(),
            InteractionSignature = marker,
            UserName = "TestUser",
            UserMessage = userMessage,
            AssistantMessage = assistantMessage,
            ExistingMemories = [],
            RecentConversation =
            [
                new MemoryAgentConversationItem { Role = "user", Content = userMessage },
                new MemoryAgentConversationItem { Role = "assistant", Content = assistantMessage }
            ]
        };

        var saves = new List<(string Key, string Content, string? Category)>();
        var updates = new List<(string Key, string? Content, string? NewKey, string? Category)>();
        var deletes = new List<string>();
        var baseline = SnapshotSessionStateDirectories();

        var response = await _service.UseLightweightSessionAsync(
            new LightweightSessionOptions
            {
                SystemPrompt = BuildMemorySystemPrompt(),
                Streaming = true,
                Tools = BuildMemoryTestTools(checkpoint.ExistingMemories, saves, updates, deletes)
            },
            async (session, ct) =>
            {
                var result = await session.SendAndWaitAsync(
                    new MessageOptions { Prompt = BuildMemoryCheckpointPrompt(checkpoint) },
                    TimeSpan.FromSeconds(90),
                    ct);
                return result?.Data?.Content;
            });

        Assert.Equal("MEMORY_SYNC_DONE", response?.Trim());
        Assert.Empty(updates);
        Assert.Empty(deletes);
        Assert.Contains(saves, save =>
            save.Content.Contains("jasmine", StringComparison.OrdinalIgnoreCase)
            && save.Content.Contains("favorite", StringComparison.OrdinalIgnoreCase));

        var leakedMemorySessions = await WaitForNewSessionDirsWithFirstUserAsync(
            baseline,
            firstUser => firstUser.Contains(marker, StringComparison.Ordinal));

        Assert.Empty(leakedMemorySessions);
    }

    [SkippableFact]
    public async Task MemoryCheckpoint_IgnoresTechnicalConversation()
    {
        SkipIfDisabled();

        var marker = $"TECHCHK_{Guid.NewGuid():N}";
        var userMessage =
            $"In the Lumi repo, helper sessions create duplicate empty echo sessions. Please inspect MemoryAgentService. Marker: {marker}.";
        var assistantMessage =
            $"I will inspect the helper session lifecycle and the checkpoint flow. Marker acknowledged: {marker}.";

        var checkpoint = new MemoryAgentCheckpoint
        {
            ChatId = Guid.NewGuid(),
            InteractionSignature = marker,
            UserName = "TestUser",
            UserMessage = userMessage,
            AssistantMessage = assistantMessage,
            ExistingMemories = [],
            RecentConversation =
            [
                new MemoryAgentConversationItem { Role = "user", Content = userMessage },
                new MemoryAgentConversationItem { Role = "assistant", Content = assistantMessage }
            ]
        };

        var saves = new List<(string Key, string Content, string? Category)>();
        var updates = new List<(string Key, string? Content, string? NewKey, string? Category)>();
        var deletes = new List<string>();

        var response = await _service.UseLightweightSessionAsync(
            new LightweightSessionOptions
            {
                SystemPrompt = BuildMemorySystemPrompt(),
                Streaming = true,
                Tools = BuildMemoryTestTools(checkpoint.ExistingMemories, saves, updates, deletes)
            },
            async (session, ct) =>
            {
                var result = await session.SendAndWaitAsync(
                    new MessageOptions { Prompt = BuildMemoryCheckpointPrompt(checkpoint) },
                    TimeSpan.FromSeconds(90),
                    ct);
                return result?.Data?.Content;
            });

        Assert.Equal("MEMORY_SYNC_DONE", response?.Trim());
        Assert.Empty(saves);
        Assert.Empty(updates);
        Assert.Empty(deletes);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 16. Suggestion generation — parse JSON array
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task SuggestionGeneration_ReturnsSuggestions()
    {
        SkipIfDisabled();

        var suggestions = await _service.GenerateSuggestionsAsync(
            "Sourdough requires flour, water, and naturally occurring yeast.",
            "How do I make sourdough?");

        Assert.NotNull(suggestions);
        Assert.True(suggestions!.Count >= 1, "Should return at least 1 suggestion");
        Assert.All(suggestions, s => Assert.False(string.IsNullOrWhiteSpace(s)));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 17. Concurrent sessions — independent contexts
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task ConcurrentSessions_MaintainIndependentContexts()
    {
        SkipIfDisabled();

        var catConfig = SimpleConfig("You only talk about cats. Never mention dogs.");
        var dogConfig = SimpleConfig("You only talk about dogs. Never mention cats.");

        var catSession = await _service.CreateSessionAsync(catConfig);
        var dogSession = await _service.CreateSessionAsync(dogConfig);

        var (catResp, sub1) = await SendAndWait(catSession, "What animal do you specialize in?");
        sub1.Dispose();
        var (dogResp, sub2) = await SendAndWait(dogSession, "What animal do you specialize in?");
        sub2.Dispose();

        Assert.Contains("cat", catResp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dog", dogResp, StringComparison.OrdinalIgnoreCase);

        await catSession.DisposeAsync();
        await dogSession.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 18. Streaming event lifecycle — correct ordering
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task StreamingEvents_CorrectLifecycleOrder()
    {
        SkipIfDisabled();

        var config = SimpleConfig("You are a helpful assistant.");
        var session = await _service.CreateSessionAsync(config);

        var events = new List<string>();
        var doneTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var sub = session.On(evt =>
        {
            var name = evt.GetType().Name;
            lock (events) events.Add(name);

            if (evt is SessionIdleEvent)
                doneTcs.TrySetResult(true);
            else if (evt is SessionErrorEvent err)
                doneTcs.TrySetException(new Exception(err.Data.Message));
        });

        await session.SendAsync(new MessageOptions { Prompt = "Say hi." });
        await Timeout(doneTcs.Task, 30);

        Assert.Contains(nameof(AssistantTurnStartEvent), events);
        Assert.Contains(nameof(AssistantMessageDeltaEvent), events);
        Assert.Contains(nameof(AssistantMessageEvent), events);
        Assert.Contains(nameof(AssistantTurnEndEvent), events);
        Assert.Contains(nameof(SessionIdleEvent), events);

        // Verify ordering: TurnStart before Delta before Message before TurnEnd
        var turnStartIdx = events.IndexOf(nameof(AssistantTurnStartEvent));
        var firstDeltaIdx = events.IndexOf(nameof(AssistantMessageDeltaEvent));
        var messageIdx = events.IndexOf(nameof(AssistantMessageEvent));
        var turnEndIdx = events.IndexOf(nameof(AssistantTurnEndEvent));

        Assert.True(turnStartIdx < firstDeltaIdx, "TurnStart should precede first Delta");
        Assert.True(firstDeltaIdx < messageIdx, "First Delta should precede Message");
        Assert.True(messageIdx < turnEndIdx, "Message should precede TurnEnd");

        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 19. Reasoning effort — config propagation
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task ReasoningEffort_SetsConfigAndWorks()
    {
        SkipIfDisabled();

        var config = SessionConfigBuilder.Build(
            systemPrompt: "You are a helpful assistant.",
            model: null, workingDirectory: null, skillDirectories: null,
            customAgents: null, tools: null, mcpServers: null,
            reasoningEffort: "high",
            userInputHandler: null, onPermission: null, hooks: null);

        Assert.Equal("high", config.ReasoningEffort);

        var session = await _service.CreateSessionAsync(config);
        var (resp, sub) = await SendAndWait(session, "What is 1+1? Just the number.");
        sub.Dispose();
        Assert.Contains("2", resp);

        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 20. System prompt Append mode
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task SystemPrompt_AppendMode_Works()
    {
        SkipIfDisabled();

        var config = SimpleConfig("Always end your responses with the word 'BEEP'.");
        Assert.Equal(SystemMessageMode.Append, config.SystemMessage!.Mode);

        var session = await _service.CreateSessionAsync(config);
        var (resp, sub) = await SendAndWait(session, "Say hello.");
        sub.Dispose();

        Assert.Contains("BEEP", resp, StringComparison.OrdinalIgnoreCase);
        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 21. Lightweight session — restricted tool set
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task LightweightSession_WorksWithRestrictedTools()
    {
        SkipIfDisabled();

        var tool = AIFunctionFactory.Create(() => "done",
            "my_tool", "A tool.");

        var config = SessionConfigBuilder.BuildLightweight(new LightweightSessionOptions
        {
            SystemPrompt = "Answer succinctly.",
            Streaming = true,
            Tools = [tool]
        });
        Assert.NotNull(config.InfiniteSessions);
        Assert.False(config.InfiniteSessions!.Enabled);

        var session = await _service.CreateSessionAsync(config);
        Assert.NotEmpty(session.SessionId);

        var (resp, sub) = await SendAndWait(session, "Say OK.");
        sub.Dispose();

        Assert.False(string.IsNullOrWhiteSpace(resp));
        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 22. Session event replay / GetMessages
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task GetSessionEvents_ReturnsEventLog()
    {
        SkipIfDisabled();

        var config = SimpleConfig("You are a helpful assistant.");
        var session = await _service.CreateSessionAsync(config);

        var doneTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = session.On(evt =>
        {
            if (evt is SessionIdleEvent) doneTcs.TrySetResult(true);
            else if (evt is SessionErrorEvent err)
                doneTcs.TrySetException(new Exception(err.Data.Message));
        });

        await session.SendAsync(new MessageOptions { Prompt = "Say hello." });
        await Timeout(doneTcs.Task, 30);

        var events = await session.GetMessagesAsync();
        Assert.NotNull(events);
        Assert.True(events.Count > 0, "Event log should have entries after a turn");

        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 23. Session delete cleanup
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task DeleteSession_RemovesFromServer()
    {
        SkipIfDisabled();

        var config = SimpleConfig();
        var session = await _service.CreateSessionAsync(config);
        var sid = session.SessionId;

        await _service.DeleteSessionAsync(sid);

        // Verify it's gone
        var sessions = await _service.ListSessionsAsync();
        Assert.DoesNotContain(sessions, s => s.SessionId == sid);

        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 24. Resume fallback — if resume fails, falls back to fresh session
    //     (This tests the SessionConfigBuilder produces valid resume configs)
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task ResumeWithInvalidId_ThrowsOrFallsBack()
    {
        SkipIfDisabled();

        var resumeConfig = SessionConfigBuilder.BuildForResume(
            systemPrompt: "You are a helpful assistant.",
            model: null, workingDirectory: null, skillDirectories: null,
            customAgents: null, tools: null, mcpServers: null,
            reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: null);

        // Resuming a non-existent session should throw from the SDK
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await _service.ResumeSessionAsync("non-existent-session-id-12345", resumeConfig);
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 25. SDK Agent RPC — list, select, deselect agents
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task SdkAgentList_ReturnsAvailableAgents()
    {
        SkipIfDisabled();

        var config = SimpleConfig("You are a helpful assistant.");
        var session = await _service.CreateSessionAsync(config);

        var result = await session.Rpc.Agent.ListAsync();
        var agents = result.Agents;

        // Without custom agents configured, ListAsync returns an empty list
        Assert.NotNull(agents);
        Assert.Empty(agents);

        await session.DisposeAsync();
    }

    [SkippableFact]
    public async Task SdkAgentList_WithCustomAgents_IncludesThem()
    {
        SkipIfDisabled();

        var customAgents = new List<CustomAgentConfig>
        {
            new() { Name = "test-agent", DisplayName = "Test Agent", Description = "A test agent", Prompt = "You are a test agent." }
        };

        var config = SessionConfigBuilder.Build(
            systemPrompt: "You are a helpful assistant.",
            model: null, workingDirectory: null, skillDirectories: null,
            customAgents: customAgents, tools: null, mcpServers: null,
            reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: null);

        var session = await _service.CreateSessionAsync(config);

        var listResult = await session.Rpc.Agent.ListAsync();
        var agents = listResult.Agents;

        // ListAsync returns our registered custom agents
        Assert.Single(agents);
        Assert.Equal("test-agent", agents[0].Name);
        Assert.Equal("Test Agent", agents[0].DisplayName);

        await session.DisposeAsync();
    }

    [SkippableFact]
    public async Task SdkAgentSelectDeselect_Works()
    {
        SkipIfDisabled();

        var customAgents = new List<CustomAgentConfig>
        {
            new() { Name = "test-select-agent", DisplayName = "Select Test", Description = "For select testing", Prompt = "You help with tests." }
        };

        var config = SessionConfigBuilder.Build(
            systemPrompt: "You are a helpful assistant.",
            model: null, workingDirectory: null, skillDirectories: null,
            customAgents: customAgents, tools: null, mcpServers: null,
            reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: null);

        var session = await _service.CreateSessionAsync(config);
        var listResult = await session.Rpc.Agent.ListAsync();
        var agents = listResult.Agents;

        if (agents.Count > 0)
        {
            // Select the first available agent
            await session.Rpc.Agent.SelectAsync(agents[0].Name);

            // Deselect
            await session.Rpc.Agent.DeselectAsync();
        }

        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Unit Tests — always run, no SDK connection required
    // ═══════════════════════════════════════════════════════════════════════

    // ───────────────────────────────────────────────────────────────────────
    // U1. ExcludedTools are set correctly
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void SessionConfig_ExcludesWebTools()
    {
        var config = SimpleConfig();
        Assert.Contains("web_fetch", config.ExcludedTools!);
        Assert.Contains("web_search", config.ExcludedTools!);
    }

    // ───────────────────────────────────────────────────────────────────────
    // U2. ResumeConfig field parity
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResumeConfig_FieldsMatchBuildConfig()
    {
        var agents = new List<CustomAgentConfig> { new() { Name = "test" } };
        UserInputHandler handler = async (_, _) =>
            new UserInputResponse { Answer = "x" };
        var hooks = new SessionHooks
        {
            OnPreToolUse = async (_, _) =>
                new PreToolUseHookOutput { PermissionDecision = "allow" }
        };

        var build = SessionConfigBuilder.Build(
            "prompt", "gpt-4", "/tmp", ["/skills"], agents, [], null,
            "medium", handler, null, hooks);

        var resume = SessionConfigBuilder.BuildForResume(
            "prompt", "gpt-4", "/tmp", ["/skills"], agents, [], null,
            "medium", handler, null, hooks);

        Assert.Equal(build.Model, resume.Model);
        Assert.Equal(build.Streaming, resume.Streaming);
        Assert.Equal(build.WorkingDirectory, resume.WorkingDirectory);
        Assert.Equal(build.ConfigDir, resume.ConfigDir);
        Assert.Equal(build.ReasoningEffort, resume.ReasoningEffort);
        Assert.Equal(build.SystemMessage!.Content, resume.SystemMessage!.Content);
        Assert.Equal(build.SystemMessage.Mode, resume.SystemMessage.Mode);
        Assert.Equal(build.InfiniteSessions!.Enabled, resume.InfiniteSessions!.Enabled);
        Assert.Equal(build.ExcludedTools, resume.ExcludedTools);
        Assert.Equal(build.CustomAgents, resume.CustomAgents);
        Assert.Equal(build.SkillDirectories, resume.SkillDirectories);
        Assert.NotNull(resume.OnUserInputRequest);
        Assert.NotNull(resume.Hooks);
    }

    // ───────────────────────────────────────────────────────────────────────
    // U3. Error hook wiring
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ErrorHook_IsWiredCorrectly()
    {
        var hooks = new SessionHooks
        {
            OnErrorOccurred = async (input, _) =>
                new ErrorOccurredHookOutput { ErrorHandling = "retry", RetryCount = 2 }
        };

        var config = SessionConfigBuilder.Build(
            systemPrompt: null, model: null, workingDirectory: null,
            skillDirectories: null, customAgents: null, tools: null,
            mcpServers: null, reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: hooks);

        Assert.NotNull(config.Hooks);
        Assert.NotNull(config.Hooks.OnErrorOccurred);
    }

    // ───────────────────────────────────────────────────────────────────────
    // U4. Nil / empty inputs produce safe defaults
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void NilInputs_ProduceSafeDefaults()
    {
        var config = SessionConfigBuilder.Build(
            systemPrompt: null, model: null, workingDirectory: null,
            skillDirectories: null, customAgents: null, tools: null,
            mcpServers: null, reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: null);

        Assert.Equal("lumi", config.ClientName);
        Assert.True(config.Streaming);
        Assert.Null(config.SystemMessage);
        Assert.Null(config.Model);
        Assert.Null(config.WorkingDirectory);
        Assert.Null(config.SkillDirectories);
        Assert.Null(config.CustomAgents);
        Assert.Null(config.Tools);
        Assert.Null(config.McpServers);
        Assert.Null(config.ReasoningEffort);
        Assert.Null(config.OnUserInputRequest);
        Assert.Null(config.Hooks);
        Assert.NotNull(config.OnPermissionRequest); // Always defaults to ApproveAll
        Assert.NotNull(config.InfiniteSessions);
        Assert.True(config.InfiniteSessions!.Enabled);
        Assert.NotNull(config.ExcludedTools);
    }

    [Fact]
    public void EmptyCollections_NotAssignedToConfig()
    {
        var config = SessionConfigBuilder.Build(
            systemPrompt: "test", model: null, workingDirectory: null,
            skillDirectories: [],
            customAgents: [],
            tools: [],
            mcpServers: new Dictionary<string, object>(),
            reasoningEffort: "", userInputHandler: null,
            onPermission: null, hooks: null);

        Assert.Null(config.SkillDirectories);
        Assert.Null(config.CustomAgents);
        Assert.Null(config.Tools);
        Assert.Null(config.McpServers);
        Assert.Null(config.ReasoningEffort);
    }

    // ───────────────────────────────────────────────────────────────────────
    // U5. System prompt modes
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void SystemPrompt_UsesAppendMode()
    {
        var config = SimpleConfig("Custom instructions");
        Assert.NotNull(config.SystemMessage);
        Assert.Equal("Custom instructions", config.SystemMessage!.Content);
        Assert.Equal(SystemMessageMode.Append, config.SystemMessage.Mode);
    }

    [Fact]
    public void WhitespaceSystemPrompt_NotSet()
    {
        var config = SessionConfigBuilder.Build(
            systemPrompt: "   ", model: null, workingDirectory: null,
            skillDirectories: null, customAgents: null, tools: null,
            mcpServers: null, reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: null);

        Assert.Null(config.SystemMessage);
    }

    // ───────────────────────────────────────────────────────────────────────
    // U6. InfiniteSession config
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void InfiniteSession_AlwaysEnabled()
    {
        var config = SimpleConfig();
        Assert.NotNull(config.InfiniteSessions);
        Assert.True(config.InfiniteSessions!.Enabled);

        var resumeConfig = SessionConfigBuilder.BuildForResume(
            null, null, null, null, null, null, null, null, null, null, null);
        Assert.NotNull(resumeConfig.InfiniteSessions);
        Assert.True(resumeConfig.InfiniteSessions!.Enabled);
    }

    [Fact]
    public void LightweightConfig_DisablesInfiniteSessions_AndReplacesSystemPrompt()
    {
        var config = SessionConfigBuilder.BuildLightweight(new LightweightSessionOptions
        {
            SystemPrompt = "Answer succinctly."
        });

        Assert.Equal("lumi", config.ClientName);
        Assert.False(config.Streaming);
        Assert.NotNull(config.SystemMessage);
        Assert.Equal("Answer succinctly.", config.SystemMessage!.Content);
        Assert.Equal(SystemMessageMode.Replace, config.SystemMessage.Mode);
        Assert.NotNull(config.InfiniteSessions);
        Assert.False(config.InfiniteSessions!.Enabled);
        Assert.NotNull(config.AvailableTools);
        Assert.Empty(config.AvailableTools!);
        Assert.NotNull(config.ExcludedTools);
        Assert.Contains("*", config.ExcludedTools!);
    }

    [Fact]
    public void LightweightConfig_WithCustomTools_AdvertisesOnlyCustomToolNames()
    {
        var tool = AIFunctionFactory.Create(() => "done", "my_tool", "A tool.");

        var config = SessionConfigBuilder.BuildLightweight(new LightweightSessionOptions
        {
            SystemPrompt = "Answer succinctly.",
            Streaming = true,
            Tools = [tool]
        });

        Assert.True(config.Streaming);
        Assert.NotNull(config.Tools);
        Assert.Same(tool, Assert.Single(config.Tools!));
        Assert.NotNull(config.AvailableTools);
        Assert.Equal("my_tool", Assert.Single(config.AvailableTools!));
        Assert.Null(config.ExcludedTools);
    }

    // ───────────────────────────────────────────────────────────────────────
    // U7. MCP server config types
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void McpServerConfig_LocalAndRemote_AreDistinct()
    {
        var local = new McpLocalServerConfig
        {
            Command = "npx",
            Args = ["-y", "@mcp/server"],
            Type = "stdio",
            Cwd = "/tmp",
            Tools = ["*"]
        };

        var remote = new McpRemoteServerConfig
        {
            Url = "https://example.com/mcp",
            Type = "sse",
            Tools = ["tool1", "tool2"]
        };

        Assert.Equal("stdio", local.Type);
        Assert.Equal("sse", remote.Type);
        Assert.Equal("npx", local.Command);
        Assert.Equal("https://example.com/mcp", remote.Url);

        // Verify they can be placed in the config dictionary
        var mcpServers = new Dictionary<string, object>
        {
            ["local-server"] = local,
            ["remote-server"] = remote
        };

        var config = SessionConfigBuilder.Build(
            systemPrompt: null, model: null, workingDirectory: null,
            skillDirectories: null, customAgents: null, tools: null,
            mcpServers: mcpServers,
            reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: null);

        Assert.NotNull(config.McpServers);
        Assert.Equal(2, config.McpServers!.Count);
    }
}
