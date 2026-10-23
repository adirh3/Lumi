using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Lumi.Services.Permissions;
using Lumi.Services.Sharing;
using Lumi.ViewModels;
using Microsoft.Extensions.AI;
using Xunit;
using Xunit.Abstractions;

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
///  3. Edit message — rewind history via History.Truncate, resend edit as a new turn
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
/// 16. Suggestion generation — stateless queries on a warm helper session
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
/// U7. MCP server config building (McpStdioServerConfig / McpHttpServerConfig)
/// </summary>
[Trait("Category", "Integration")]
public class CopilotIntegrationTests : IAsyncLifetime
{
    private CopilotService _service = null!;
    private readonly ITestOutputHelper _output;

    public CopilotIntegrationTests(ITestOutputHelper output) => _output = output;

    private static bool IsEnabled =>
        Environment.GetEnvironmentVariable("LUMI_INTEGRATION_TESTS") == "1";

    [SkippableFact]
    public async Task PromptSharing_RealModelPreservesDashboardRefinementsAndUsesRecipientFiles()
    {
        SkipIfDisabled();
        var result = new Lumi.Models.ChatMessage
        {
            Role = "assistant",
            Content = @"Created C:\Users\Sender\Reports\dashboard.xlsx with monthly category charts, recurring-charge highlights and a dark blue theme. The original CSV is untouched."
        };
        var chat = new Chat
        {
            Messages =
            [
                new() { Content = "Recommend an anime to watch." },
                new() { Role = "assistant", Content = "Try a short adventure anime." },
                new() { Content = @"Create an Excel expense dashboard from C:\Users\Sender\bank.csv.", Attachments = [@"C:\Users\Sender\bank.csv"] },
                new() { Role = "assistant", Content = "I can use Excel COM on this Windows computer." },
                new() { Content = "Use monthly category charts, highlight recurring charges and use a dark blue theme. Keep the original CSV unchanged. Never email or upload the bank data. API_KEY=private-fixture-key" },
                result
            ]
        };
        var source = PromptDraftBuilder.Create(chat, result.Id, "Windows");
        var prompt = await _service.GenerateSharedPromptAsync(source);
        _output.WriteLine(PromptSharing.Write(prompt));

        Assert.Contains(prompt.Inputs, input => input.Kind == PromptInputKind.File);
        Assert.Contains("monthly", prompt.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recurring", prompt.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blue", prompt.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("(?i)(unchanged|untouched|read.only|do not (?:modify|alter|change)|without (?:modifying|altering|changing))", prompt.Content);
        Assert.Matches("(?i)(never|do not|no|must not).*(?:upload|email)", prompt.Content);
        Assert.DoesNotContain("anime", prompt.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-fixture-key", PromptSharing.Write(prompt));
        Assert.DoesNotContain("Sender", PromptSharing.Write(prompt), StringComparison.OrdinalIgnoreCase);
        Assert.False(PromptDraftBuilder.HasPersonalLocation(prompt));
        Assert.Contains("operating system", PromptSharing.ComposeText(prompt));
        Assert.Equal(6, chat.Messages.Count);
    }

    [SkippableFact]
    public async Task PromptSharing_RealModelMakesPlatformSpecificBackupReusableWithoutAssumingSourceSetup()
    {
        var result = new Lumi.Models.ChatMessage
        {
            Role = "assistant",
            Content = @"Set up SenderBackup on Windows with a scheduled PowerShell/robocopy job. It copies C:\Users\Sender\Code to D:\Backups at 09:00 Monday-Friday, retaining the last seven dated backups. It never uploads to the cloud or deletes the source."
        };
        SkipIfDisabled();
        var chat = new Chat
        {
            Messages =
            [
                new() { Content = @"Set up a local backup of C:\Users\Sender\Code into D:\Backups." },
                new() { Content = "Run at 09:00 on weekdays, retain seven dated backups. Don't upload to any cloud service or delete the source. Verify I can restore a file." },
                result
            ]
        };
        var source = PromptDraftBuilder.Create(chat, result.Id, "Windows");
        var prompt = await _service.GenerateSharedPromptAsync(source);
        _output.WriteLine(PromptSharing.Write(prompt));
        Assert.True(prompt.Inputs.Count(input => input.Kind == PromptInputKind.Folder) >= 2);
        Assert.Matches("(?i)(09:00|9:00|9\\s*(?:am|a\\.m\\.))", prompt.Content);
        Assert.Matches("(?i)(weekday|Monday)", prompt.Content);
        Assert.Matches("(?i)(seven|\\b7\\b)", prompt.Content);
        Assert.Contains("restore", prompt.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("(?i)(windows|operating system|platform|scheduler)", prompt.Content + string.Join(' ', prompt.Requirements));
        Assert.False(PromptDraftBuilder.HasPersonalLocation(prompt));
        Assert.DoesNotContain("SenderBackup", prompt.Content);
        Assert.Contains("creating recurring jobs", PromptSharing.ComposeText(prompt));
    }

    [SkippableFact]
    public async Task PromptSharing_RealRecipientAdaptsAWindowsTaskToLinuxAndAsksForMissingData()
    {
        SkipIfDisabled();
        var result = new Lumi.Models.ChatMessage
        {
            Role = "assistant", Content = "Created the Excel dashboard using Excel COM on Windows. The original CSV is unchanged."
        };
        var chat = new Chat
        {
            Messages =
            [
                new() { Content = "Create an Excel workbook with monthly expense charts from my bank CSV. Keep the CSV unchanged.", Attachments = [@"C:\Users\Sender\bank.csv"] },
                result
            ]
        };
        var model = await _service.GetBackgroundModelIdAsync();
        var prompt = await _service.GenerateSharedPromptAsync(PromptDraftBuilder.Create(chat, result.Id, "Windows"));
        var raw = await _service.UseLightweightSessionAsync(new LightweightSessionOptions
        {
            Model = model,
            SystemPrompt = """
                Evaluate a shared task for a declared recipient machine. This is a planning-only
                test: do not execute anything. The recipient is on Linux, has Python and openpyxl,
                does not have Microsoft Excel, COM, PowerShell or Windows desktop automation, and
                has not supplied the bank CSV. Treat these declared capabilities as authoritative,
                not the test process's actual host environment.
                Return ONLY JSON: {"approach":"specific viable tool/approach","missingInputs":["what the user must supply"],"canProceed":false,"needsWindows":false}.
                Follow the shared prompt's setup checks. Do not claim you have accessed files or
                completed the task. Distinguish missing data from missing Windows-only tooling.
                """
        }, async (session, ct) =>
        {
            var response = await session.SendAndWaitAsync(new MessageOptions
            {
                Prompt = PromptSharing.ComposeText(prompt)
            }, TimeSpan.FromSeconds(90), ct);
            return response?.Data?.Content;
        });
        Assert.False(string.IsNullOrWhiteSpace(raw));
        _output.WriteLine("Generated share:\n" + PromptSharing.Write(prompt) + "\nLinux recipient plan:\n" + raw);
        var json = raw!.Trim();
        if (PackText.TryUnwrapSingleFence(json, out var inner))
            json = inner;
        using var plan = JsonDocument.Parse(json);
        Assert.False(plan.RootElement.GetProperty("canProceed").GetBoolean());
        Assert.False(plan.RootElement.GetProperty("needsWindows").GetBoolean());
        Assert.Contains("openpyxl", plan.RootElement.GetProperty("approach").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(plan.RootElement.GetProperty("missingInputs").EnumerateArray(),
            input => input.GetString()?.Contains("csv", StringComparison.OrdinalIgnoreCase) == true);
    }

    [SkippableFact]
    public async Task PromptSharing_RealModelRetainsFormatsFromRedactedFileLocations()
    {
        SkipIfDisabled();
        var result = new Lumi.Models.ChatMessage
        {
            Role = "assistant",
            Content = @"Created D:\PrivateExports\private-slides.pptx. One slide per section, three short bullets per slide."
        };
        var chat = new Chat
        {
            Messages =
            [
                new() { Content = """Convert "C:\Users\Sender\private-source.md" to "D:\PrivateExports\private-slides.pptx". Use one slide per section and at most three short bullets per slide.""" },
                result
            ]
        };
        var source = PromptDraftBuilder.Create(chat, result.Id, "Windows");
        var prompt = await _service.GenerateSharedPromptAsync(source);
        _output.WriteLine(PromptSharing.Write(prompt));
        Assert.Contains(prompt.Inputs, input => input.Kind == PromptInputKind.File);
        Assert.Matches(@"(?i)(markdown|\.md)", prompt.Content + string.Join(' ', prompt.Requirements));
        Assert.Contains(".pptx", prompt.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("(?i)(slide[^\\n]*section|section[^\\n]*slide)", prompt.Content);
        Assert.False(PromptDraftBuilder.HasPersonalLocation(prompt));
        Assert.DoesNotContain("private-source", prompt.Content);
        Assert.DoesNotContain("private-slides", prompt.Content);
    }

    public async Task InitializeAsync()
    {
        if (!IsEnabled) return;
        _service = new CopilotService();
        await _service.ConnectAsync();
    }

#pragma warning disable GHCP001 // Deterministic native permission pipeline; no model inference.
    /// <summary>
    /// Lumi's approval choices, applied by its own policy code, are what the native runtime enforces:
    /// a Never rule even in Full access, an Always-ask layer even in Full access, Ask first, and an
    /// Allow rule, on a created session and again after resume (the runtime keeps no rules across it).
    /// </summary>
    [SkippableFact]
    public async Task Permissions_LumisPolicy_IsEnforcedNatively_OnCreateAndResume()
    {
        SkipIfDisabled();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var ct = timeout.Token;
        var executions = 0;
        var tool = AIFunctionFactory.Create((string receipt) =>
        {
            Interlocked.Increment(ref executions);
            return receipt;
        }, "permission_probe", "Return the isolated permission-test receipt.");
        ApprovalRule ProbeRule(ApprovalRuleAction action)
            => new() { Action = action, Kind = ApprovalRuleKind.LumiTool, Target = "permission_probe" };

        var data = new AppData();
        var chat = new Chat();
        data.Chats.Add(chat);
        data.Settings.ApprovalRules.Add(ProbeRule(ApprovalRuleAction.Never));
        using var vm = new ChatViewModel(new DataStore(data), _service);
        var handler = SessionConfigBuilder.BuildPermissionHandler(() => ApprovalPolicy.ResolveMode(chat, null, data.Settings).Mode);

        var created = await _service.CreateSessionAsync(SessionConfigBuilder.Build(
            "Permission integration fixture. No inference is needed.", null, null, null,
            [], [], [tool], null, null, handler, ChatViewModel.BuildSessionHooks(),
            enableCapabilityDiscovery: false), ct);
        var sessionId = created.SessionId;
        try
        {
            // Full access, the default, still refuses what the user said Never to, without asking.
            await vm.PrepareSessionPermissionsAsync(created, chat, null, ct);
            Assert.Equal(PermissionMode.AllowAll, (await created.Rpc.Permissions.GetModeAsync(ct)).Mode);
            Assert.Null(await RunAsync(created, chat, "never", answer: null));
            Assert.Equal(0, executions);

            // Auto-review is the runtime's native assisted mode, which Lumi's CLI environment turns on.
            chat.ApprovalMode = ApprovalMode.AutoReview;
            await vm.PrepareSessionPermissionsAsync(created, chat, null, ct);
            Assert.Equal(PermissionMode.Assisted, (await created.Rpc.Permissions.GetModeAsync(ct)).Mode);

            // Ask first: the request waits for a person, and runs once allowed.
            data.Settings.ApprovalRules.Clear();
            chat.ApprovalMode = ApprovalMode.AskFirst;
            await vm.PrepareSessionPermissionsAsync(created, chat, null, ct);
            Assert.Equal(PermissionMode.Manual, (await created.Rpc.Permissions.GetModeAsync(ct)).Mode);
            Assert.NotNull(await RunAsync(created, chat, "ask-allow", ApproveOnce()));
            Assert.Equal(1, executions);
            Assert.NotNull(await RunAsync(created, chat, "ask-deny", Deny()));
            Assert.Equal(1, executions);

            // "Allow for this chat" is a native session approval, kept apart from the rules: replacing
            // the rules alone leaves it, and Lumi's policy apply clears it, so a forgotten one asks again.
            Assert.NotNull(await RunAsync(created, chat, "session-allow", ApproveForSession()));
            Assert.Equal(2, executions);
            Assert.True((await created.Rpc.Permissions.ConfigureAsync(rules: ApprovalPolicy.BuildSessionRules([]), cancellationToken: ct)).Success);
            Assert.Null(await RunAsync(created, chat, "session-remembered", answer: null));
            Assert.Equal(3, executions);
            await vm.PrepareSessionPermissionsAsync(created, chat, null, ct);
            Assert.NotNull(await RunAsync(created, chat, "session-forgotten", Deny()));
            Assert.Equal(3, executions);

            // A remembered Allow rule runs it without asking.
            chat.ApprovalRules.Add(ProbeRule(ApprovalRuleAction.Allow));
            await vm.PrepareSessionPermissionsAsync(created, chat, null, ct);
            Assert.Null(await RunAsync(created, chat, "allowed", answer: null));
            Assert.Equal(4, executions);
        }
        finally
        {
            await created.DisposeAsync();
        }

        // After a resume Lumi installs the chat's mode and rules again.
        await using var resumed = await _service.ResumeSessionAsync(sessionId, SessionConfigBuilder.BuildForResume(
            "Permission integration fixture. No inference is needed.", null, null, null,
            [], [], [tool], null, null, handler, ChatViewModel.BuildSessionHooks(),
            enableCapabilityDiscovery: false), ct);
        await vm.PrepareSessionPermissionsAsync(resumed, chat, null, ct);
        Assert.Equal(PermissionMode.Manual, (await resumed.Rpc.Permissions.GetModeAsync(ct)).Mode);
        Assert.Null(await RunAsync(resumed, chat, "resume-allowed", answer: null));
        Assert.Equal(5, executions);
        chat.ApprovalRules.Clear();
        await vm.PrepareSessionPermissionsAsync(resumed, chat, null, ct);
        Assert.NotNull(await RunAsync(resumed, chat, "resume-deny", Deny()));
        Assert.Equal(5, executions);

        // Stop: native Abort retires a waiting request without running it.
        var stoppingRequest = new TaskCompletionSource<PermissionRequestedData>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (resumed.On<PermissionRequestedEvent>(evt => stoppingRequest.TrySetResult(evt.Data)))
        {
            var stoppingArguments = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["receipt"] = "must-not-execute" });
            var stoppingExecution = resumed.Rpc.Tools.ExecuteAsync("permission_probe", stoppingArguments, "stop-pending", ct);
            var stoppingPermission = await stoppingRequest.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            await resumed.AbortAsync(ct);
            var stopError = await Record.ExceptionAsync(() => stoppingExecution.WaitAsync(TimeSpan.FromSeconds(10), ct));
            Assert.False(stopError is TimeoutException, "Native Abort must release the blocked tool call.");
            Assert.DoesNotContain((await resumed.Rpc.Permissions.PendingRequestsAsync(ct)).Items,
                item => item.RequestId == stoppingPermission.RequestId);
            Assert.Equal(5, executions);
        }

        // An Always-ask rule is a startup layer that requires a person even in Full access.
        var alwaysAsk = new Chat();
        data.Chats.Add(alwaysAsk);
        alwaysAsk.ApprovalRules.Add(ProbeRule(ApprovalRuleAction.Ask));
        var askLayer = ApprovalPolicy.BuildAskLayer(alwaysAsk.ApprovalRules);
        var askConfig = SessionConfigBuilder.Build(
            "Permission integration fixture. No inference is needed.", null, null, null,
            [], [], [tool], null, null,
            SessionConfigBuilder.BuildPermissionHandler(() => ApprovalPolicy.ResolveMode(alwaysAsk, null, data.Settings).Mode),
            ChatViewModel.BuildSessionHooks(), enableCapabilityDiscovery: false);
        askConfig.ManagedSettings = askLayer;
        await using var asking = await _service.CreateSessionAsync(askConfig, ct);
        await vm.PrepareSessionPermissionsAsync(asking, alwaysAsk, askLayer, ct);
        Assert.Equal(PermissionMode.AllowAll, (await asking.Rpc.Permissions.GetModeAsync(ct)).Mode);
        var managed = await RunAsync(asking, alwaysAsk, "always-ask", ApproveOnce());
        Assert.True(managed?.PermissionRequest?.ManagedApprovalRequired);
        Assert.Equal(6, executions);

        static GitHub.Copilot.Rpc.PermissionDecision ApproveOnce()
            => new GitHub.Copilot.Rpc.PermissionDecisionApproveOnce { ApprovedInteractively = true };

        static GitHub.Copilot.Rpc.PermissionDecision ApproveForSession()
            => new GitHub.Copilot.Rpc.PermissionDecisionApproveForSession
            {
                Approval = new GitHub.Copilot.Rpc.PermissionDecisionApproveForSessionApprovalCustomTool { ToolName = "permission_probe" }
            };

        static GitHub.Copilot.Rpc.PermissionDecision Deny()
            => GitHub.Copilot.Rpc.PermissionDecision.Reject("Denied by the isolated test user.");

        // Runs the probe; answers the request it raises, or returns null when none needed a person.
        async Task<PermissionRequestedData?> RunAsync(
            CopilotSession session, Chat owner, string toolCallId, GitHub.Copilot.Rpc.PermissionDecision? answer)
        {
            var requested = new TaskCompletionSource<PermissionRequestedData>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = session.On<PermissionRequestedEvent>(evt =>
            {
                if (evt.Data.ResolvedByHook != true
                    && evt.Data.PermissionRequest is PermissionRequestCustomTool { ToolName: "permission_probe" } request
                    && SessionConfigBuilder.NeedsPerson(ApprovalPolicy.ResolveMode(owner, null, data.Settings).Mode, request))
                    requested.TrySetResult(evt.Data);
            });
            var arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["receipt"] = toolCallId });
            var execution = session.Rpc.Tools.ExecuteAsync("permission_probe", arguments, toolCallId, ct);
            if (await Task.WhenAny(execution, requested.Task).WaitAsync(TimeSpan.FromSeconds(30), ct) == execution)
            {
                await execution;
                Assert.Null(answer);
                _output.WriteLine($"{toolCallId}: settled without asking");
                return null;
            }

            var permission = await requested.Task;
            Assert.NotNull(answer);
            Assert.False(execution.IsCompleted);
            Assert.True((await session.Rpc.Permissions.HandlePendingPermissionRequestAsync(
                permission.RequestId, answer!, cancellationToken: ct)).Success);
            await execution.WaitAsync(TimeSpan.FromSeconds(20), ct);
            Assert.False((await session.Rpc.Permissions.HandlePendingPermissionRequestAsync(
                permission.RequestId, answer!, cancellationToken: ct)).Success);
            _output.WriteLine($"{toolCallId}: native request {permission.RequestId} answered {answer!.Kind}");
            return permission;
        }
    }
#pragma warning restore GHCP001

    public async Task DisposeAsync()
    {
        if (_service is not null)
            await _service.DisposeAsync();
    }

    private void SkipIfDisabled() =>
        Skip.If(!IsEnabled, "Set LUMI_INTEGRATION_TESTS=1 to run SDK integration tests.");

    [SkippableFact]
    public async Task Authentication_RefreshUpdatesSharedLoginPanel()
    {
        SkipIfDisabled();
        var status = await _service.GetAuthStatusAsync();
        Skip.If(status.IsAuthenticated != true, "Requires an already authenticated Copilot account.");

        await using var browser = new BrowserService();
        using var settings = new SettingsViewModel(
            new DataStore(new AppData()), _service, browser, new UpdateService());
        var login = new GitHubLoginViewModel(_service);
        settings.LoginVM = login;

        await settings.RefreshAuthStatusAsync();

        Assert.True(settings.IsAuthenticated);
        Assert.True(login.IsAuthenticated);
        Assert.Equal(settings.GitHubLogin, login.GitHubLogin);
        Assert.NotEmpty(login.GitHubLogin);
    }

    [SkippableFact]
    public async Task Authentication_DisconnectInvalidatesOwnedSessions()
    {
        SkipIfDisabled();
        var before = await _service.GetAuthStatusAsync();
        var notifications = 0;
        _service.Reconnected += OnReconnected;
        try
        {
            await _service.DisconnectAsync();
            Assert.Equal(1, notifications);
            var refreshed = await _service.GetAuthStatusAsync();
            Assert.True(_service.IsConnected);
            Assert.Equal(before.IsAuthenticated, refreshed.IsAuthenticated);
            Assert.Equal(1, notifications);
        }
        finally
        {
            _service.Reconnected -= OnReconnected;
        }

        void OnReconnected() => notifications++;
    }

#pragma warning disable GHCP001 // Vault-disabled SDK fixture never targets a real user.
    [SkippableFact]
    public async Task Authentication_SdkLogoutWorksWithoutAccessToTheOsVault()
    {
        SkipIfDisabled();
        var profile = Directory.CreateTempSubdirectory("Lumi-logout-fixture-");
        try
        {
            var environment = Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .Where(entry => entry.Key is string key
                    && !key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
                    && !key.StartsWith("COPILOT_", StringComparison.OrdinalIgnoreCase)
                    && !key.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!,
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            environment["PATH"] = "";
            var binary = OperatingSystem.IsWindows() ? "copilot.exe" : "copilot";
            var cli = Path.Combine(AppContext.BaseDirectory, "runtimes",
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "native", binary);
            await using var client = new CopilotClient(new CopilotClientOptions
            {
                Mode = CopilotClientMode.Empty,
                Connection = RuntimeConnection.ForStdio(cli),
                BaseDirectory = profile.FullName,
                Environment = environment,
                UseLoggedInUser = false
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await client.StartAsync(timeout.Token);
            Assert.False((await client.GetAuthStatusAsync(timeout.Token)).IsAuthenticated);
            Assert.Empty((await client.Rpc.Account.GetAllUsersAsync(timeout.Token))!);

            var result = await client.Rpc.Account.LogoutAsync(
                authInfo: new GitHub.Copilot.Rpc.AuthInfoUser
                {
                    Host = "https://github.com",
                    Login = $"lumi-fixture-{Guid.NewGuid():N}"
                },
                cancellationToken: timeout.Token);

            Assert.NotNull(result);
            Assert.False(result.HasMoreUsers);
            Assert.False((await client.GetAuthStatusAsync(timeout.Token)).IsAuthenticated);
        }
        finally
        {
            profile.Delete(recursive: true);
        }
    }
#pragma warning restore GHCP001

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
        var sub = session.On<SessionEvent>(evt =>
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
            model: null, workingDirectory: null, mcpPlan: null,
            skillDirectories: null,
            customAgents: null, tools: null,
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
            DataStore.CopilotConfigDir,
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
        using var sub = session.On<SessionEvent>(evt =>
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
        await session1.DisposeAsync();

        // Resume into a new session object
        var resumeConfig = SessionConfigBuilder.BuildForResume(
            systemPrompt: "You are a helpful assistant. Remember everything.",
            model: null, workingDirectory: null, mcpPlan: null,
            skillDirectories: null,
            customAgents: null, tools: null,
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

    [SkippableTheory]
    [InlineData("foreground", true, false)]
    [InlineData("background", true, false)]
    [InlineData("resend", true, false)]
    [InlineData("edit", true, false)]
    [InlineData("model", true, false)]
    [InlineData("foreground", false, false)]
    [InlineData("foreground", true, true)]
    [InlineData("background", true, true)]
    [InlineData("resend", true, true)]
    [InlineData("edit", true, true)]
    [InlineData("model", true, true)]
    public async Task ExpiredCachedChat_ResumesSavedHistoryOrRebuildsWhenMissing(
        string sendKind, bool savedHistoryExists, bool lumiOwnsRelease)
    {
        SkipIfDisabled();
        var model = await _service.GetBackgroundModelIdAsync();
        using var ui = HeadlessTestSession.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await ui.Dispatch(async () =>
        {
            var settings = new UserSettings
            {
                PreferredModel = model,
                AutoSaveChats = false,
                AutoGenerateTitles = false,
                EnableMemoryAutoSave = false,
                EnableMemoryAutoMaintenance = false,
                SuggestFollowUps = false
            };
            var store = new DataStore(new AppData { Settings = settings });
            using var viewModel = new ChatViewModel(store, _service);
            viewModel.SelectedModel = model;
            var word = $"Zephyr{Random.Shared.Next(10000, 99999)}";
            viewModel.PromptText = $"For this conversation, the test word is {word}. Reply only OK. Do not use tools.";
            await viewModel.SendMessageCommand.ExecuteAsync(null);
            await WaitForReplyAsync(1);
            if (sendKind == "edit")
            {
                viewModel.PromptText = "Reply only SECOND. Do not use tools.";
                await viewModel.SendMessageCommand.ExecuteAsync(null);
                await WaitForReplyAsync(2);
            }

            var chat = Assert.IsType<Chat>(viewModel.CurrentChat);
            var sessionId = chat.CopilotSessionId;
            Assert.False(string.IsNullOrWhiteSpace(sessionId));

            if (lumiOwnsRelease)
            {
                viewModel.ReleaseIdleSessionRuntimes(
                    DateTimeOffset.UtcNow + CopilotService.IdleSessionTimeout + TimeSpan.FromSeconds(1));
                Assert.False(viewModel.HasLiveCopilotRuntimeForCurrentChat());
                Assert.Equal(sessionId, chat.CopilotSessionId);
            }
            else
            {
                // An unexpected native close must remain resumable too.
                await _service.Client!.Rpc.Sessions.CloseAsync(sessionId!, deadline.Token);
                Assert.True(viewModel.HasLiveCopilotRuntimeForCurrentChat());
            }
            if (!savedHistoryExists)
            {
                Assert.True(DataStore.UsesIsolatedProfile);
                Assert.True(Guid.TryParse(sessionId, out _));
                File.Delete(Path.Combine(DataStore.CopilotConfigDir, "session-state", sessionId!, "events.jsonl"));
            }
            const string question = "What is the test word? Reply with just the word. Do not use tools.";
            if (sendKind == "background")
                await viewModel.SendExternalMessageAsync(chat, question, "Test job", deadline.Token);
            else if (sendKind == "resend")
                await viewModel.ResendFromMessageAsync(chat.Messages.First(message => message.Role == "user"), wasEdited: false);
            else if (sendKind == "edit")
            {
                var editedMessage = chat.Messages.Last(message => message.Role == "user");
                editedMessage.Content = question;
                await viewModel.ResendFromMessageAsync(editedMessage, wasEdited: true);
            }
            else if (sendKind == "model")
            {
                var alternate = (await _service.Client!.ListModelsAsync(deadline.Token))
                    .First(candidate => candidate.Id != model);
                viewModel.SelectedModel = alternate.Id;
                typeof(ChatViewModel).GetMethod("CancelPendingMidSessionModelSync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, null);
                if (!lumiOwnsRelease)
                {
                    var switched = (Task<bool>)typeof(ChatViewModel).GetMethod("SwitchModelMidSessionAsync",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, [alternate.Id, null, null, deadline.Token])!;
                    Assert.False(await switched);
                    var detachedCache = (Dictionary<Guid, CopilotSession>)typeof(ChatViewModel)
                        .GetField("_sessionCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewModel)!;
                    Assert.False(detachedCache.ContainsKey(chat.Id));
                    Assert.Null(typeof(ChatViewModel).GetField(
                        "_activeSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewModel));
                    Assert.Equal(sessionId, chat.CopilotSessionId);
                }
                viewModel.PromptText = question;
                await viewModel.SendMessageCommand.ExecuteAsync(null);
            }
            else
            {
                viewModel.PromptText = question;
                await viewModel.SendMessageCommand.ExecuteAsync(null);
            }
            await WaitForReplyAsync(sendKind == "resend" ? 1 : 2);

            Assert.Same(chat, viewModel.CurrentChat);
            if (savedHistoryExists)
            {
                Assert.Equal(sessionId, chat.CopilotSessionId);
                if (sendKind != "resend")
                {
                    var cache = (Dictionary<Guid, CopilotSession>)typeof(ChatViewModel)
                        .GetField("_sessionCache", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(viewModel)!;
                    var nativeUsers = (await cache[chat.Id].GetEventsAsync(deadline.Token))
                        .OfType<UserMessageEvent>().ToList();
                    Assert.Equal(2, nativeUsers.Count);
                    Assert.DoesNotContain(word, nativeUsers[^1].Data.Content, StringComparison.OrdinalIgnoreCase);
                }
            }
            else
                Assert.NotEqual(sessionId, chat.CopilotSessionId);
            if (sendKind != "resend")
                Assert.Contains(word, chat.Messages.Last(message => message.Role == "assistant").Content,
                    StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(chat.Messages, message => message.Role == "error");

            async Task WaitForReplyAsync(int count)
            {
                while (viewModel.IsBusy || viewModel.IsSessionActive
                    || (viewModel.CurrentChat?.Messages.Count(message =>
                        message.Role == "assistant" && !string.IsNullOrWhiteSpace(message.Content)) ?? 0) < count)
                {
                    Assert.DoesNotContain(viewModel.CurrentChat?.Messages ?? [],
                        message => message.Role == "error");
                    await Task.Delay(25, deadline.Token);
                }
            }
        }, deadline.Token);
    }

    [SkippableFact]
    public async Task ModelSelection_AfterClosedSession_DefersResumeAndKeepsLatestChoice()
    {
        SkipIfDisabled();
        var model = await _service.GetBackgroundModelIdAsync();
        var alternate = (await _service.Client!.ListModelsAsync()).First(candidate => candidate.Id != model).Id;
        using var ui = HeadlessTestSession.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await ui.Dispatch(async () =>
        {
            var store = new DataStore(new AppData
            {
                Settings = new UserSettings
                {
                    PreferredModel = model,
                    AutoSaveChats = false,
                    AutoGenerateTitles = false,
                    EnableMemoryAutoSave = false,
                    EnableMemoryAutoMaintenance = false,
                    SuggestFollowUps = false
                }
            });
            var provider = new PausedCapabilityProvider();
            using var catalog = new CapabilityCatalog(new LumiCapabilityProvider(store), provider);
            using var vm = new ChatViewModel(store, _service, capabilityCatalog: catalog)
            {
                SelectedModel = model,
                PromptText = "Reply only READY. Do not use tools."
            };
            await vm.SendMessageCommand.ExecuteAsync(null);
            while (vm.IsBusy || vm.IsSessionActive)
                await Task.Delay(25, deadline.Token);
            var chat = Assert.IsType<Chat>(vm.CurrentChat);
            var originalId = chat.CopilotSessionId;
            Assert.False(string.IsNullOrWhiteSpace(originalId));
            await _service.Client.Rpc.Sessions.CloseAsync(originalId!, deadline.Token);
            provider.Pause();
            try
            {
                vm.SelectedModel = alternate;
                var activeSession = typeof(ChatViewModel).GetField(
                    "_activeSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
                while (activeSession.GetValue(vm) is not null && !provider.Entered.Task.IsCompleted)
                    await Task.Delay(25, deadline.Token);
                Assert.Null(activeSession.GetValue(vm));
                Assert.False(provider.Entered.Task.IsCompleted, "A picker change must not load a closed session.");
                vm.SelectedModel = model;
            }
            finally
            {
                provider.Resume();
            }

            vm.PromptText = "Reply only LATEST. Do not use tools.";
            await vm.SendMessageCommand.ExecuteAsync(null);
            while (vm.IsBusy || vm.IsSessionActive)
                await Task.Delay(25, deadline.Token);
            var cache = (Dictionary<Guid, CopilotSession>)typeof(ChatViewModel).GetField(
                "_sessionCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            var actualModel = (await cache[chat.Id].Rpc.Model.GetCurrentAsync(deadline.Token)).ModelId;
            Assert.Equal(model, vm.SelectedModel);
            Assert.Equal(model, actualModel);
            Assert.Equal(originalId, chat.CopilotSessionId);
            Assert.DoesNotContain(chat.Messages, message => message.Role == "error");
        }, deadline.Token);
    }

    private sealed class PausedCapabilityProvider : ICapabilityProvider
    {
        private TaskCompletionSource? _resume;
        public string Id => "model-resume-fixture";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Pause() => _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Resume()
        {
            var resume = _resume;
            _resume = null;
            resume?.TrySetResult();
        }
        public async Task<CapabilityProviderResult> LoadAsync(CapabilityQuery query, CancellationToken cancellationToken)
        {
            if (_resume is { } resume)
            {
                Entered.TrySetResult();
                await resume.Task.WaitAsync(cancellationToken);
            }
            return CapabilityProviderResult.Empty;
        }
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExpiredCachedChat_DeliversPendingExternalSkillAfterResume(bool resend, bool lumiOwnsRelease)
    {
        SkipIfDisabled();
        const string skillName = "idle-skill-fixture";
        var root = Path.Combine(DataStore.CopilotConfigDir, $"skill-recovery-{Guid.NewGuid():N}");
        var skillDirectory = Path.Combine(root, ".github", "skills", skillName);
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"),
            $"---\nname: {skillName}\ndescription: A synthetic idle recovery check.\n---\n\nAnswer the user's request briefly. Do not use any shell or network tools.");
        var model = await _service.GetBackgroundModelIdAsync();
        using var ui = HeadlessTestSession.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await ui.Dispatch(async () =>
        {
            var project = new Project { Name = "Skill recovery fixture", WorkingDirectory = root };
            var chat = new Chat { Title = "Skill recovery fixture", ProjectId = project.Id };
            var store = new DataStore(new AppData
            {
                Settings = new UserSettings
                {
                    PreferredModel = model,
                    AutoSaveChats = false,
                    AutoGenerateTitles = false,
                    EnableMemoryAutoSave = false,
                    EnableMemoryAutoMaintenance = false,
                    SuggestFollowUps = false
                },
                Projects = [project],
                Chats = [chat]
            });
            using var viewModel = new ChatViewModel(store, _service)
            {
                CurrentChat = chat,
                SelectedModel = model,
                PromptText = "Reply only READY. Do not use tools."
            };
            await viewModel.SendMessageCommand.ExecuteAsync(null);
            await WaitForReplyAsync();
            var sessionId = chat.CopilotSessionId;
            Assert.False(string.IsNullOrWhiteSpace(sessionId));
            if (lumiOwnsRelease)
            {
                viewModel.ReleaseIdleSessionRuntimes(
                    DateTimeOffset.UtcNow + CopilotService.IdleSessionTimeout + TimeSpan.FromSeconds(1));
                Assert.False(viewModel.HasLiveCopilotRuntimeForCurrentChat());
            }
            else
            {
                await _service.Client!.Rpc.Sessions.CloseAsync(sessionId!, deadline.Token);
                Assert.True(viewModel.HasLiveCopilotRuntimeForCurrentChat());
            }
            viewModel.RegisterSkillIdByName(skillName);
            Assert.Contains(skillName, chat.ActiveExternalSkillNames);

            if (resend)
                await viewModel.ResendFromMessageAsync(chat.Messages.First(message => message.Role == "user"), wasEdited: false);
            else
            {
                viewModel.PromptText = "Reply only DONE.";
                await viewModel.SendMessageCommand.ExecuteAsync(null);
            }
            await WaitForReplyAsync();

            Assert.Equal(sessionId, chat.CopilotSessionId);
            var cache = (Dictionary<Guid, CopilotSession>)typeof(ChatViewModel)
                .GetField("_sessionCache", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(viewModel)!;
            var nativeUsers = (await cache[chat.Id].GetEventsAsync(deadline.Token))
                .OfType<UserMessageEvent>().ToList();
            Assert.Contains(skillName, nativeUsers[^1].Data.Content);
            Assert.DoesNotContain(chat.Messages, message => message.Role == "error");

            async Task WaitForReplyAsync()
            {
                while (viewModel.IsBusy || viewModel.IsSessionActive)
                {
                    Assert.DoesNotContain(chat.Messages, message => message.Role == "error");
                    await Task.Delay(25, deadline.Token);
                }
                Assert.Contains(chat.Messages, message => message.Role == "assistant"
                    && !string.IsNullOrWhiteSpace(message.Content));
            }
        }, deadline.Token);
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

    [SkippableFact]
    public async Task EditMessage_TruncateHistory_RewindsToBeforeEditedTurn()
    {
        SkipIfDisabled();

        // Mirrors ChatViewModel.TryRewindEditedHistoryAsync: rewind the live session's
        // server-side history to just before an edited turn via History.Truncate, then
        // resend the edit as a NORMAL turn — no transcript-as-text replay.
        var keptToken = $"APPLE_{Guid.NewGuid():N}";
        var staleToken = $"BANANA_{Guid.NewGuid():N}";
        var editedToken = $"CHERRY_{Guid.NewGuid():N}";

        var config = SimpleConfig("You are a helpful assistant. Follow instructions exactly.");
        var session = await _service.CreateSessionAsync(config);

        // Turn 1 (kept) then turn 2 (the one that will be edited away).
        var (_, sub1) = await SendAndWait(session,
            $"Remember this secret token exactly: {keptToken}. Reply only OK.", 60);
        sub1.Dispose();
        var (_, sub2) = await SendAndWait(session,
            $"Also remember this secret token exactly: {staleToken}. Reply only OK.", 60);
        sub2.Dispose();

        // Pick the truncation target through the SAME production helper the app uses
        // (PendingTurnRecoveryAnalyzer.SelectEditTruncationTarget): the retained history has
        // exactly one genuine user turn, so the target is the 2nd genuine user turn. Using the
        // helper (instead of a hardcoded index) exercises the real phantom-skipping code path
        // and stays correct whether or not the CLI injects a system-sourced user.message.
        var eventsBefore = await session.GetEventsAsync();
        var target = PendingTurnRecoveryAnalyzer.SelectEditTruncationTarget(eventsBefore, retainedUserCount: 1);
        Assert.NotNull(target);

        var truncateResult = await session.Rpc.History.TruncateAsync(target!.Id.ToString());
        Assert.True(truncateResult.EventsRemoved > 0);

        // After truncation the 1st genuine user turn survives and there is no 2nd genuine turn.
        var eventsAfter = await session.GetEventsAsync();
        Assert.NotNull(PendingTurnRecoveryAnalyzer.SelectEditTruncationTarget(eventsAfter, retainedUserCount: 0));
        Assert.Null(PendingTurnRecoveryAnalyzer.SelectEditTruncationTarget(eventsAfter, retainedUserCount: 1));

        // Resend the edited 2nd turn as a normal fresh turn.
        var (_, editSub) = await SendAndWait(session,
            $"Also remember this secret token exactly: {editedToken}. Reply only OK.", 60);
        editSub.Dispose();

        var (recall, recallSub) = await SendAndWait(session,
            "List every secret token you were asked to remember, comma-separated. Tokens only.", 60);
        recallSub.Dispose();

        // The kept turn and the edited token survive; the edited-away token is gone.
        Assert.Contains(keptToken, recall, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(editedToken, recall, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(staleToken, recall, StringComparison.OrdinalIgnoreCase);

        await session.DisposeAsync();
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

        using var sub1 = session.On<SessionEvent>(evt =>
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
    public async Task ChatPause_EmptyBatchContinuesAfterAbort_WithoutAnotherUserMessage()
    {
        SkipIfDisabled();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var tool = AIFunctionFactory.Create(async (CancellationToken cancellationToken) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                try { await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken); }
                catch (OperationCanceledException)
                {
                    canceled.TrySetResult();
                    throw;
                }
            }
            return "NATIVE_RESUME_COMPLETE";
        }, "resume_probe", "Get the completion marker. Retry this tool if it was interrupted.");
        var config = SessionConfigBuilder.BuildLightweight(new LightweightSessionOptions
        {
            Model = "gpt-5-mini",
            SystemPrompt = "Call resume_probe and reply with its returned marker. Retry if interrupted.",
            Tools = [tool],
            Streaming = true
        });
        var session = await _service.CreateSessionAsync(config);
        var userMessages = 0;
        var assistantMessages = new List<string>();
        using var subscription = session.On<SessionEvent>(evt =>
        {
            if (evt is UserMessageEvent)
                Interlocked.Increment(ref userMessages);
            else if (evt is AssistantMessageEvent message)
                assistantMessages.Add(message.Data.Content ?? "");
        });
        try
        {
            await session.SendAsync(new MessageOptions { Prompt = "Run the tool and report its marker." });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(60));
#pragma warning disable GHCP001
            await session.Rpc.Queue.SetDrainPausedAsync(true);
            var abort = await session.Rpc.AbortAsync();
            Assert.True(abort.Success, abort.Error);
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _output.WriteLine("Abort accepted.");
            Assert.Equal(1, calls);
            await session.Rpc.Queue.SetDrainPausedAsync(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await session.Rpc.SendMessagesAsync([], wait: true, cancellationToken: timeout.Token);
#pragma warning restore GHCP001
            _output.WriteLine($"Calls: {calls}; user messages: {userMessages}; assistant responses: " +
                              string.Join(" | ", assistantMessages));
            Assert.True(calls >= 2);
            Assert.Equal(1, userMessages);
            Assert.Contains(assistantMessages, message => message.Contains("NATIVE_RESUME_COMPLETE"));
        }
        finally
        {
            await session.DisposeAsync();
            await _service.DeleteSessionAsync(session.SessionId);
        }
    }

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
            model: null, workingDirectory: null, mcpPlan: null,
            skillDirectories: null,
            customAgents: null, tools: [tool],
            reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: hooks);

        var session = await _service.CreateSessionAsync(config);

        var toolStarted = false;
        var toolCompleted = false;
        var response = "";
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var sub = session.On<SessionEvent>(evt =>
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
            model: null, workingDirectory: null, mcpPlan: null,
            skillDirectories: null,
            customAgents: null, tools: [weatherTool, timeTool],
            reasoningEffort: null, userInputHandler: null,
            onPermission: null,
            hooks: new SessionHooks
            {
                OnPreToolUse = async (_, _) =>
                    new PreToolUseHookOutput { PermissionDecision = "allow" }
            });

        var session = await _service.CreateSessionAsync(config);

        var doneTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = session.On<SessionEvent>(evt =>
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
            model: null, workingDirectory: workDir, mcpPlan: null,
            skillDirectories: null,
            customAgents: null, tools: null,
            reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: null);

        Assert.Equal(workDir, config.WorkingDirectory);
        Assert.Equal(DataStore.CopilotConfigDir, config.ConfigDirectory);
        Assert.NotEqual(workDir, config.ConfigDirectory);

        var session = await _service.CreateSessionAsync(config);
        Assert.NotEmpty(session.SessionId);
        await session.DisposeAsync();
    }

    [SkippableFact]
    public async Task NativeMcpSessionLoss_RealCliRestartKeepsSessionAndDoesNotReplay()
    {
        SkipIfDisabled();
        using var failedServer = new LazyMcpRuntimeTests.FakeMcp("call-session-stuck");
        using var unaffectedServer = new LazyMcpRuntimeTests.FakeMcp();
        const string serverName = "native-session-loss";
        var configDirectory = Directory.CreateDirectory(Path.Combine(failedServer.Root, "cli-config")).FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var session = await _service.CreateSessionAsync(new SessionConfig
        {
            WorkingDirectory = failedServer.Root,
            ConfigDirectory = configDirectory,
            EnableConfigDiscovery = false,
            EnableSessionStore = false,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            McpServers = new Dictionary<string, McpServerConfig>
            {
                [serverName] = failedServer.Definition(lazy: false).Config,
                ["unaffected-server"] = unaffectedServer.Definition(lazy: false).Config
            }
        }, timeout.Token);
        var sessionId = session.SessionId;
        var nativeCalls = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var restartTimes = new Dictionary<string, long>(StringComparer.Ordinal);
        var restartCount = 0;
        try
        {
            await session.Rpc.Mcp.ListAsync(timeout.Token);
            await session.Rpc.Tools.InitializeAndValidateAsync(timeout.Token);
            var metadata = await session.Rpc.Tools.GetCurrentMetadataAsync(timeout.Token);
            Assert.NotNull(metadata.Tools);
            var tool = Assert.Single(metadata.Tools,
                item => item.McpServerName == serverName && item.McpToolName == "echo");
            var marker = Guid.NewGuid().ToString("N");
            var arguments = JsonSerializer.SerializeToElement(new { value = marker });
            Assert.Contains("MCP error -32001: Session not found",
                (await session.Rpc.Tools.ExecuteAsync(
                    tool.Name, arguments, "baseline-first", timeout.Token)).GetRawText(), StringComparison.Ordinal);
            Assert.Contains("MCP error -32001: Session not found",
                (await session.Rpc.Tools.ExecuteAsync(
                    tool.Name, arguments, "baseline-second", timeout.Token)).GetRawText(), StringComparison.Ordinal);
            Assert.Single(failedServer.Starts);
            Assert.Equal(2, failedServer.Messages("tools/call").Length);

            var failure = await session.Rpc.Tools.ExecuteAsync(
                tool.Name, arguments, "expired-call", timeout.Token);
            Assert.NotEqual("success", failure.GetProperty("resultType").GetString());
            var completion = new ToolExecutionCompleteData
            {
                ToolCallId = "expired-call",
                Success = false,
                Error = new ToolExecutionCompleteError { Message = failure.GetProperty("error").GetString() }
            };
            Assert.True(McpStdioServerConnection.IsRecoverableSessionLossMessage(completion.Error.Message));
            nativeCalls[completion.ToolCallId] = serverName;
            // Direct tool RPCs do not emit assistant-turn events. Feed their actual failure
            // through the subscription's helper; unit tests cover event provenance separately.
            await ChatViewModel.RestartNativeMcpServerAfterSessionLossAsync(
                completion, nativeCalls, restartTimes, async name =>
                {
                    Assert.Equal(serverName, name);
                    Interlocked.Increment(ref restartCount);
                    await session.Rpc.Mcp.RestartServerAsync(name, null, timeout.Token);
                });

            Assert.Equal(1, restartCount);
            Assert.Equal(2, failedServer.Starts.Length);
            Assert.Single(unaffectedServer.Starts);
            Assert.Equal(3, failedServer.Messages("tools/call").Length);
            Assert.Equal(sessionId, session.SessionId);
            Assert.Contains((await session.Rpc.Mcp.ListToolsAsync(serverName, timeout.Token)).Tools,
                item => item.Name == "echo");

            // Direct RPC calls must refresh the offered set before their next invocation.
            await session.Rpc.Tools.InitializeAndValidateAsync(timeout.Token);
            var result = await session.Rpc.Tools.ExecuteAsync(tool.Name, arguments, "explicit-next-call", timeout.Token);
            _output.WriteLine("Post-restart synthetic tool result: " + result.GetRawText());
            Assert.Contains(marker, result.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(4, failedServer.Messages("tools/call").Length);
            Assert.Equal(1, restartCount);
            Assert.Equal(sessionId, session.SessionId);
        }
        finally
        {
            await session.DisposeAsync();
            await _service.DeleteSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task LazyMcp_RealCliInitializeCapture_AndDiscoveryAcrossRuntimeLifetimes()
    {
        SkipIfDisabled();
        using var fake = new LazyMcpRuntimeTests.FakeMcp("dynamic");
        const string serverName = "lumi-lazy-capture";
        var configDirectory = Directory.CreateDirectory(Path.Combine(fake.Root, "cli-config")).FullName;
        var sessionIds = new HashSet<string>(StringComparer.Ordinal);
        JsonElement? coldTools = null;
        var coldMessageCount = 0;

        for (var lifetime = 0; lifetime < 2; lifetime++)
        {
            await using var runtime = new McpProxyRuntime(fake.CacheDirectory);
            using var registration = runtime.AcquireSessionRegistration(fake.Definition());
            var remote = registration.ServerConfig;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            // No config discovery, existing chats, prompts, or model inference. The test class
            // owns/disposes its CopilotService; each iteration owns a new synthetic session.
            var session = await _service.CreateSessionAsync(new SessionConfig
            {
                ClientName = "lumi",
                WorkingDirectory = fake.Root,
                ConfigDirectory = configDirectory,
                EnableConfigDiscovery = false,
                EnableSessionStore = false,
                OnPermissionRequest = PermissionHandler.ApproveAll,
                McpServers = new Dictionary<string, McpServerConfig> { [serverName] = remote }
            }, timeout.Token);
            try
            {
                Assert.True(sessionIds.Add(session.SessionId), "Capture must use distinct, newly created sessions.");
                var connected = false;
                while (!connected)
                {
                    var servers = await session.Rpc.Mcp.ListAsync(timeout.Token);
                    var server = servers.Servers.SingleOrDefault(s => s.Name == serverName);
                    Assert.True(string.IsNullOrWhiteSpace(server?.Error), server?.Error);
                    if (server?.Status.Value == "connected")
                        connected = true;
                    else
                        await Task.Delay(100, timeout.Token);
                }

                var tools = await session.Rpc.Mcp.ListToolsAsync(serverName, timeout.Token);
                Assert.Contains(tools.Tools, tool => tool.Name == "echo");
                if (lifetime == 0)
                {
                    Assert.Single(fake.Starts);
                    var initialize = Assert.Single(fake.Messages("initialize")).GetProperty("params");
                    // Only protocol-defined discovery identity is printed; never CLI environment,
                    // headers, credentials, other MCP definitions, or session configuration.
                    _output.WriteLine("Real Copilot MCP initialize (sanitized): " + JsonSerializer.Serialize(new
                    {
                        protocolVersion = initialize.GetProperty("protocolVersion"),
                        capabilities = initialize.GetProperty("capabilities"),
                        clientInfo = initialize.GetProperty("clientInfo")
                    }));
                    coldTools = JsonSerializer.SerializeToElement(tools.Tools);
                    coldMessageCount = fake.Messages().Length;
                    Assert.NotEmpty(fake.Messages("tools/list"));
                    _output.WriteLine("Synthetic backend discovery requests: " +
                        string.Join("\n", fake.Messages().Select(message => message.GetRawText())));
                    _output.WriteLine("Discovery cache files: " +
                        (Directory.Exists(fake.CacheDirectory) ? Directory.GetFiles(fake.CacheDirectory, "*.json").Length : 0));
                    var cachePath = Assert.Single(Directory.GetFiles(fake.CacheDirectory, "*.json"));
                    var snapshot = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(cachePath))!;
                    Assert.True(snapshot["initializeResult"]!["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>());
                    snapshot["createdAtUtc"] = DateTimeOffset.UtcNow.AddDays(-42);
                    await File.WriteAllTextAsync(cachePath, snapshot.ToJsonString());
                }
                else
                {
                    Assert.Single(fake.Starts);
                    Assert.Equal(coldMessageCount, fake.Messages().Length);
                    Assert.True(JsonElement.DeepEquals(coldTools!.Value, JsonSerializer.SerializeToElement(tools.Tools)),
                        "The second CLI session must discover the same cached tool metadata without starting the backend.");
                    _output.WriteLine($"Second runtime discovery backend starts: {fake.Starts.Length - 1}");
                }
                Assert.Empty(fake.Messages("tools/call"));
            }
            finally
            {
                await session.DisposeAsync();
                await _service.DeleteSessionAsync(session.SessionId);
            }
        }
    }

    [SkippableFact]
    public async Task GitHubMcpWebSearch_CanBeInvokedByNormalSession()
    {
        SkipIfDisabled();

        var mcpServers = new Dictionary<string, McpServerConfig>();
        var token = CopilotService.TryGetGitHubTokenForMcp();
        GitHubMcpWebSearchBootstrap.Ensure(mcpServers, token);
        var serverConfig = Assert.IsType<McpHttpServerConfig>(mcpServers[GitHubMcpWebSearchBootstrap.ServerName]);
        var hasAuthHeader = serverConfig.Headers?.ContainsKey("Authorization") == true;

        var config = SessionConfigBuilder.Build(
            systemPrompt: "You MUST use web_search whenever the user asks you to search the web. Do not answer web-search requests without using the tool.",
            model: null, workingDirectory: null, mcpPlan: new McpSessionPlan(mcpServers, []),
            skillDirectories: null,
            customAgents: null, tools: null,
            reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: null);

        var session = await _service.CreateSessionAsync(config);
        var mcpList = await Timeout(session.Rpc.Mcp.ListAsync(), 30);
        var mcpSummary = string.Join(", ", mcpList.Servers.Select(server => $"{server.Name}:{server.Status}:{server.Error}"));
        Assert.Contains(mcpList.Servers, server => server.Name == GitHubMcpWebSearchBootstrap.ServerName);

        var toolNames = new List<string>();
        var assistantResponse = "";
        var doneTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var sub = session.On<SessionEvent>(evt =>
        {
            switch (evt)
            {
                case ToolExecutionStartEvent tool:
                    toolNames.Add(tool.Data.ToolName);
                    if (!string.IsNullOrWhiteSpace(tool.Data.McpToolName))
                        toolNames.Add(tool.Data.McpToolName);
                    break;
                case AssistantMessageEvent msg:
                    assistantResponse = msg.Data.Content ?? "";
                    break;
                case SessionIdleEvent:
                    doneTcs.TrySetResult(true);
                    break;
                case SessionErrorEvent err:
                    doneTcs.TrySetException(new Exception(err.Data.Message));
                    break;
            }
        });

        try
        {
            await session.SendAsync(new MessageOptions
            {
                Prompt = "Search the web for the exact query 'GitHub Copilot' using web_search, then summarize one result in one sentence."
            });
            await Timeout(doneTcs.Task, 90);

            Assert.True(
                toolNames.Any(toolName =>
                    string.Equals(toolName, "web_search", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(toolName, "github-mcp-server-web_search", StringComparison.OrdinalIgnoreCase)),
                $"Token found: {!string.IsNullOrWhiteSpace(token)}; auth header: {hasAuthHeader}; MCP servers: {mcpSummary}; assistant response: {assistantResponse}");
        }
        finally
        {
            await session.DisposeAsync();
        }
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
            model: null, workingDirectory: null, mcpPlan: null,
            skillDirectories: null,
            customAgents: agents, tools: null,
            reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: null);

        Assert.Equal(agents, config.CustomAgents);

        var session = await _service.CreateSessionAsync(config);
        Assert.NotEmpty(session.SessionId);
        await session.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 10. Capability discovery is owned by the SDK
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task SkillDiscovery_IsDelegatedToTheRuntime()
    {
        SkipIfDisabled();

        var config = SessionConfigBuilder.Build(
            systemPrompt: "You are a helpful assistant with skills.",
            model: null, workingDirectory: null,
            mcpPlan: null, skillDirectories: null, customAgents: null, tools: null,
            reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: null);

        // Lumi hands the runtime no skill roots: config discovery finds project, personal, plugin
        // and built-in skills on its own.
        Assert.Null(config.SkillDirectories);
        Assert.True(config.EnableSkills);
        Assert.True(config.EnableConfigDiscovery);

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

        Func<UserInputRequest, UserInputInvocation, Task<UserInputResponse>> handler = async (request, _) =>
        {
            return new UserInputResponse { Answer = "Blue", WasFreeform = true };
        };

        var config = SessionConfigBuilder.Build(
            systemPrompt: "When asked about preferences, ask the user.",
            model: null, workingDirectory: null, mcpPlan: null,
            skillDirectories: null,
            customAgents: null, tools: null,
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
            model: null, workingDirectory: null, mcpPlan: null,
            skillDirectories: null,
            customAgents: null, tools: [tool],
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
        using var sub = session.On<SessionEvent>(evt =>
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
        await session.DisposeAsync();

        await _service.DeleteSessionAsync(sid);

        var afterDelete = await _service.ListSessionsAsync();
        Assert.DoesNotContain(afterDelete, s => s.SessionId == sid);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 15. Title generation — lightweight session
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BackgroundModel_HelperConfigPreservesExplicitOverridesAndIsolation()
    {
        await using var service = new CopilotService();
        var config = await service.BuildLightweightSessionConfigAsync(new LightweightSessionOptions
        {
            SystemPrompt = "Isolated fixture helper.",
            Model = "explicit-fixture-model",
            Streaming = false
        });

        Assert.Equal("explicit-fixture-model", config.Model);
        Assert.False(config.EnableConfigDiscovery);
        Assert.False(config.EnableSessionStore);
        Assert.Empty(config.AvailableTools!);
        Assert.Equal("Isolated fixture helper.", config.SystemMessage?.Content);
    }

    [Fact]
    public async Task BackgroundModel_ByokOnlyTogglePreservesCredentialStoreRouting()
    {
        await using var service = new CopilotService();
        var keyStore = new FakeSecureKeyStore();
        var endpoint = new ByokEndpoint
        {
            Id = "background-credential-endpoint",
            Name = "Credential fixture",
            ProviderType = "openai",
            BaseUrl = "http://127.0.0.1:1/v1",
            WireApi = "completions",
            ApiKeyMode = ByokApiKeyMode.CredentialStore
        };
        var model = new ByokModel
        {
            Id = "background-credential-model",
            EndpointId = endpoint.Id,
            ModelId = "private-wire-model",
            DisplayName = "Private background model"
        };
        var data = new AppData
        {
            Settings = new UserSettings
            {
                BackgroundModel = ByokConfigHelper.BuildModelToken(model),
                ByokEndpoints = [endpoint],
                ByokModels = [model]
            }
        };
        await keyStore.SetAsync(
            Lumi.Services.Byok.SecureKeyStoreFactory.EndpointKey(endpoint.Id), "fixture-credential");
        service.SetSettingsProvider(() => data.Settings, keyStore);
        await using var browser = new BrowserService();
        using var settingsVm = new SettingsViewModel(
            new DataStore(data), service, browser, new UpdateService(), keyStore);
        var options = new LightweightSessionOptions { SystemPrompt = "Credential fixture helper." };
        Assert.Equal("fixture-credential",
            (await service.BuildLightweightSessionConfigAsync(options)).Provider?.ApiKey);

        settingsVm.UseBYOKOnly = true;
        Assert.Equal("fixture-credential",
            (await service.BuildLightweightSessionConfigAsync(options)).Provider?.ApiKey);
        settingsVm.UseBYOKOnly = false;
        Assert.Equal("fixture-credential",
            (await service.BuildLightweightSessionConfigAsync(options)).Provider?.ApiKey);
    }

    [SkippableFact]
    public async Task BackgroundModel_UsesLivePreferenceForHelpersAndRefreshesCachedSuggestions()
    {
        SkipIfDisabled();
        var settings = new UserSettings { PreferredModel = "not-the-background-model" };
        _service.SetSettingsProvider(() => settings);
        var catalog = await _service.GetModelsAsync();
        var automatic = await _service.GetBackgroundModelIdAsync();
        Assert.Equal(CopilotService.SelectCheapestModelId(catalog), automatic);
        _output.WriteLine($"BACKGROUND_MODEL_AUTO={automatic}");
        foreach (var model in catalog.Where(CopilotService.IsModelEnabled))
        {
            _output.WriteLine(
                $"{model.Id}: tokenSampleCredits={CopilotService.GetModelTokenCost(model)?.ToString("R", CultureInfo.InvariantCulture) ?? "unknown"}, " +
                $"multiplier={model.Billing?.Multiplier?.ToString("R", CultureInfo.InvariantCulture) ?? "unknown"}");
        }

        var usedModel = await _service.UseLightweightSessionAsync(
            new LightweightSessionOptions { SystemPrompt = "Reply exactly BACKGROUND_MODEL_OK." },
            async (session, ct) =>
            {
                var usage = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var subscription = session.On<AssistantUsageEvent>(evt =>
                {
                    if (evt.Data?.Model is { } model)
                        usage.TrySetResult(model);
                });
                var reply = await session.SendAndWaitAsync(
                    new MessageOptions { Prompt = "Reply exactly BACKGROUND_MODEL_OK." },
                    TimeSpan.FromSeconds(60), ct);
                Assert.Equal("BACKGROUND_MODEL_OK", reply?.Data?.Content?.Trim());
                return await usage.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            });
        Assert.Equal(automatic, usedModel);

        settings.BackgroundModel = automatic;
        Assert.False(string.IsNullOrWhiteSpace(
            await _service.GenerateTitleAsync("Find cozy anime for a rainy evening.")));
        var suggestions = await _service.GenerateSuggestionsAsync(
            "Try a cozy slice-of-life anime with short episodes.", "Find a cozy anime.");
        Assert.NotEmpty(suggestions!);
        var sessionField = typeof(CopilotService).GetField("_suggestionSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = Assert.IsType<CopilotSession>(sessionField.GetValue(_service));

        var secondModel = CopilotService.SelectCheapestModelId(catalog.Where(model => model.Id != automatic));
        Assert.NotNull(secondModel);
        settings.BackgroundModel = secondModel!;
        suggestions = await _service.GenerateSuggestionsAsync(
            "You can watch one short episode this evening.", "What can I watch tonight?");
        Assert.NotEmpty(suggestions!);
        var second = Assert.IsType<CopilotSession>(sessionField.GetValue(_service));
        Assert.NotEqual(first.SessionId, second.SessionId);
        var queryCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable GHCP001 // Suggestions use the SDK's native transient-query lifecycle.
        using var secondSubscription = second.On<UiEphemeralQueryEvent>(evt =>
        {
            if (evt.Data.Phase == UIEphemeralQueryPhase.Completed)
                queryCompleted.TrySetResult();
        });
#pragma warning restore GHCP001
        await _service.GenerateSuggestionsAsync("It is available with subtitles.", "Does it have subtitles?");
        Assert.Same(second, sessionField.GetValue(_service));
        await queryCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(secondModel, (await second.Rpc.Model.GetCurrentAsync()).ModelId);
        _output.WriteLine($"BACKGROUND_MODEL_OVERRIDE={secondModel}");

        settings.UseBYOKOnly = true;
        Assert.Null(await _service.GenerateTitleAsync("This must not reach Copilot."));
        Assert.Null(await _service.GenerateSuggestionsAsync("Private exchange.", "Private message."));
        await Assert.ThrowsAsync<ByokOnlyRequestBlockedException>(() =>
            _service.BuildLightweightSessionConfigAsync(
                new LightweightSessionOptions { SystemPrompt = "Blocked helper." }));

        var endpoint = new ByokEndpoint
        {
            Id = "background-fixture-endpoint",
            Name = "Background fixture",
            ProviderType = "openai",
            BaseUrl = "http://127.0.0.1:1/v1",
            WireApi = "completions",
            ApiKeyMode = ByokApiKeyMode.None
        };
        var byokModel = new ByokModel
        {
            Id = "background-fixture-model",
            EndpointId = endpoint.Id,
            ModelId = "private-wire-model",
            DisplayName = "Private background model",
            DefaultContextWindowTokens = 128_000,
            MaxRequestsPerMinute = 10
        };
        settings.ByokEndpoints.Add(endpoint);
        settings.ByokModels.Add(byokModel);
        Assert.Null(ByokConfigHelper.ValidateEndpoint(endpoint));
        Assert.Null(ByokConfigHelper.ValidateModel(byokModel, endpoint));
        settings.BackgroundModel = ByokConfigHelper.BuildModelToken(byokModel);
        var config = await _service.BuildLightweightSessionConfigAsync(
            new LightweightSessionOptions { SystemPrompt = "BYOK helper." });
        Assert.Equal(byokModel.ModelId, config.Model);
        Assert.Equal(endpoint.BaseUrl, config.Provider?.BaseUrl);
        Assert.Equal(128_000, config.ModelCapabilities?.Limits?.MaxContextWindowTokens);
        Assert.True(_service.ByokRateLimiter.IsRateLimited(byokModel.Id));
        settings.BackgroundModel = "byok:missing";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.BuildLightweightSessionConfigAsync(
                new LightweightSessionOptions { SystemPrompt = "Invalid BYOK helper." }));
    }

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
            UserName = null,
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

    [SkippableFact]
    public async Task SuggestionGeneration_ReusesStatelessHelperAndRecreatesAnExpiredSession()
    {
        SkipIfDisabled();
        var sessionField = typeof(CopilotService).GetField(
            "_suggestionSession", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.NotEmpty((await _service.GenerateSuggestionsAsync(
            "The OLED television has better contrast.", "Compare these televisions."))!);
        var first = Assert.IsType<CopilotSession>(sessionField.GetValue(_service));
        Assert.NotEmpty((await _service.GenerateSuggestionsAsync(
            "The soup needs another ten minutes.", "When is dinner ready?"))!);
        Assert.Same(first, sessionField.GetValue(_service));
        Assert.DoesNotContain(await first.GetEventsAsync(),
            evt => evt is UserMessageEvent or AssistantMessageEvent);

        // Closing the server-side session reproduces the stale handle left by the idle sweeper.
        await _service.Client!.Rpc.Sessions.CloseAsync(first.SessionId);
        Assert.NotEmpty((await _service.GenerateSuggestionsAsync(
            "The trip takes two hours.", "When should we leave?"))!);
        var replacement = Assert.IsType<CopilotSession>(sessionField.GetValue(_service));
        Assert.NotEqual(first.SessionId, replacement.SessionId);
        Assert.DoesNotContain(await replacement.GetEventsAsync(),
            evt => evt is UserMessageEvent or AssistantMessageEvent);

        var lastUse = (DateTimeOffset)typeof(CopilotService).GetField(
            "_suggestionSessionLastUsedUtc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_service)!;
        await _service.ReleaseIdleSuggestionSessionAsync(lastUse + CopilotService.IdleSessionTimeout - TimeSpan.FromTicks(1));
        Assert.Same(replacement, sessionField.GetValue(_service));
        var gate = (SemaphoreSlim)typeof(CopilotService).GetField(
            "_suggestionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_service)!;
        await gate.WaitAsync();
        try
        {
            await _service.ReleaseIdleSuggestionSessionAsync(lastUse + CopilotService.IdleSessionTimeout);
            Assert.Same(replacement, sessionField.GetValue(_service));
        }
        finally
        {
            gate.Release();
        }
        await _service.ReleaseIdleSuggestionSessionAsync(lastUse + CopilotService.IdleSessionTimeout);
        Assert.Null(sessionField.GetValue(_service));
        Assert.NotEmpty((await _service.GenerateSuggestionsAsync(
            "The garden needs watering.", "How should I care for these plants?"))!);
        var afterIdleRelease = Assert.IsType<CopilotSession>(sessionField.GetValue(_service));
        Assert.NotEqual(replacement.SessionId, afterIdleRelease.SessionId);
        Assert.DoesNotContain(await afterIdleRelease.GetEventsAsync(),
            evt => evt is UserMessageEvent or AssistantMessageEvent);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 16b. Context-aware suggestions — a frequently-typed prompt must NOT leak
    //      into unrelated conversations, but SHOULD remain available when the
    //      current conversation is actually about that topic. Drives the real
    //      ranker + real model end-to-end across several distinct topics.
    // ═══════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task SuggestionGeneration_RespectsConversationContextAcrossTopics()
    {
        SkipIfDisabled();

        // The user types coding prompts constantly, but also has other recurring requests. The ranker
        // hands the model ONE global, conversation-agnostic list; the model must decide what fits.
        var history = new List<UserPromptHistoryItem>();
        var origin = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        void Add(string content, int times, TimeSpan offset)
        {
            for (var i = 0; i < times; i++)
                history.Add(new UserPromptHistoryItem(Guid.NewGuid(), Guid.NewGuid(), content, origin.Add(offset).AddMinutes(i)));
        }
        Add("commit and push to main", 14, TimeSpan.Zero);
        Add("run code review", 9, TimeSpan.FromHours(1));
        Add("plan my day", 5, TimeSpan.FromHours(2));
        Add("write me a story", 4, TimeSpan.FromHours(3));

        // The SAME global block is sent for every conversation — exactly as production does it.
        var block = SuggestionHistoryRanker.BuildFrequentRequestsBlock(history, 8);
        Assert.NotNull(block);
        Assert.Contains("commit and push to main", block!, StringComparison.OrdinalIgnoreCase);
        _output.WriteLine($"[Global block]\n{block}\n");

        // Words that would only appear if the coding history leaked into an unrelated chat.
        string[] codingLeakTerms = ["commit", "push", "git", "code review", "pull request", "repository"];

        var unrelated = new[]
        {
            (Topic: "TV shopping",
             User: "Which 65-inch OLED TV should I buy for a bright living room?",
             Assistant: "For a bright room the LG G4 and Samsung S95D QD-OLED have the highest peak brightness, while the Sony Bravia 8 trades brightness for excellent processing. Expect to pay between $1,800 and $2,600."),
            (Topic: "Cooking",
             User: "How do I make a good sourdough starter from scratch?",
             Assistant: "Mix equal parts flour and water, leave it loosely covered at room temperature, and feed it daily. After about a week it should be bubbly and double in size, ready to leaven bread."),
            (Topic: "Travel",
             User: "What are the best neighborhoods to stay in when visiting Lisbon?",
             Assistant: "Alfama is historic and atmospheric, Baixa is central and walkable, and Príncipe Real is trendy with great restaurants. Each offers a different vibe for exploring the city."),
            (Topic: "TV shopping (Hebrew)",
             User: "איזו טלוויזיית OLED בגודל 65 אינץ' הכי משתלמת לסלון מואר?",
             Assistant: "לסלון מואר ה-LG G4 וה-Samsung S95D בולטים בבהירות שיא גבוהה, בעוד שה-Sony Bravia 8 מציעה עיבוד תמונה מצוין במחיר נמוך יותר. טווח המחירים הוא בערך 6,000 עד 9,000 שקלים."),
        };

        foreach (var (topic, user, assistant) in unrelated)
        {
            // Same global block every time — the model must ignore it as irrelevant here.
            var suggestions = await _service.GenerateSuggestionsAsync(assistant, user, block);
            Assert.NotNull(suggestions);
            Assert.NotEmpty(suggestions!);

            _output.WriteLine($"[{topic}] suggestions: {string.Join(" | ", suggestions!)}");

            var joined = string.Join("\n", suggestions!).ToLowerInvariant();
            foreach (var term in codingLeakTerms)
            {
                Assert.False(
                    joined.Contains(term, StringComparison.Ordinal),
                    $"[{topic}] coding term '{term}' leaked into suggestions: {string.Join(" | ", suggestions!)}");
            }
        }

        // ── Coding conversation: now the recurring coding prompt IS a natural next step ──
        var codeUser = "I finished the feature on my branch. What's the git workflow to ship it?";
        var codeAssistant = "Your local commits look good. Next, commit any remaining changes, push the branch to the remote, open a pull request against main, and merge once review passes.";

        var codeSuggestions = await _service.GenerateSuggestionsAsync(codeAssistant, codeUser, block);
        Assert.NotNull(codeSuggestions);
        Assert.NotEmpty(codeSuggestions!);
        _output.WriteLine($"[Coding] suggestions: {string.Join(" | ", codeSuggestions!)}");

        // For a git-workflow conversation the model should produce a shipping-related suggestion.
        string[] shipTerms = ["commit", "push", "pr", "pull request", "merge", "review", "main", "branch", "remote", "ship"];
        var codeJoined = string.Join("\n", codeSuggestions!).ToLowerInvariant();
        Assert.True(
            shipTerms.Any(term => codeJoined.Contains(term, StringComparison.Ordinal)),
            $"[Coding] expected a git/ship-related suggestion, got: {string.Join(" | ", codeSuggestions!)}");
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

        using var sub = session.On<SessionEvent>(evt =>
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
            model: null, workingDirectory: null, mcpPlan: null,
            skillDirectories: null,
            customAgents: null, tools: null,
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
        using var sub = session.On<SessionEvent>(evt =>
        {
            if (evt is SessionIdleEvent) doneTcs.TrySetResult(true);
            else if (evt is SessionErrorEvent err)
                doneTcs.TrySetException(new Exception(err.Data.Message));
        });

        await session.SendAsync(new MessageOptions { Prompt = "Say hello." });
        await Timeout(doneTcs.Task, 30);

        var events = await session.GetEventsAsync();
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
            model: null, workingDirectory: null, mcpPlan: null,
            skillDirectories: null,
            customAgents: null, tools: null,
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
            model: null, workingDirectory: null, mcpPlan: null,
            skillDirectories: null,
            customAgents: customAgents, tools: null,
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
            model: null, workingDirectory: null, mcpPlan: null,
            skillDirectories: null,
            customAgents: customAgents, tools: null,
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
    public void SessionConfig_ExcludesDuplicatedSdkBuiltInsButKeepsSdkWebSearch()
    {
        var config = SimpleConfig();
        Assert.Contains("builtin:web_fetch", config.ExcludedTools!);
        Assert.Contains("builtin:browser", config.ExcludedTools!);
        Assert.Contains("builtin:ask_user", config.ExcludedTools!);
        Assert.DoesNotContain("builtin:web_search", config.ExcludedTools!);
    }

    // ───────────────────────────────────────────────────────────────────────
    // U2. ResumeConfig field parity
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResumeConfig_FieldsMatchBuildConfig()
    {
        var agents = new List<CustomAgentConfig> { new() { Name = "test" } };
        Func<UserInputRequest, UserInputInvocation, Task<UserInputResponse>> handler = async (_, _) =>
            new UserInputResponse { Answer = "x" };
        var hooks = new SessionHooks
        {
            OnPreToolUse = async (_, _) =>
                new PreToolUseHookOutput { PermissionDecision = "allow" }
        };

        var build = SessionConfigBuilder.Build(
            "prompt", "gpt-4", "/tmp", null, null, agents, [],
            "medium", handler, null, hooks);

        var resume = SessionConfigBuilder.BuildForResume(
            "prompt", "gpt-4", "/tmp", null, null, agents, [],
            "medium", handler, null, hooks);

        Assert.Equal(build.Model, resume.Model);
        Assert.Equal(build.Streaming, resume.Streaming);
        Assert.Equal(build.WorkingDirectory, resume.WorkingDirectory);
        Assert.Equal(build.ConfigDirectory, resume.ConfigDirectory);
        Assert.Equal(build.ReasoningEffort, resume.ReasoningEffort);
        Assert.Equal(build.SystemMessage!.Content, resume.SystemMessage!.Content);
        Assert.Equal(build.SystemMessage.Mode, resume.SystemMessage.Mode);
        Assert.Equal(build.InfiniteSessions!.Enabled, resume.InfiniteSessions!.Enabled);
        Assert.Equal(build.ExcludedTools, resume.ExcludedTools);
        Assert.Equal(build.CustomAgents, resume.CustomAgents);
        Assert.Equal(build.SkillDirectories, resume.SkillDirectories);
        Assert.Equal(build.EnableSkills, resume.EnableSkills);
        Assert.Equal(build.EnableConfigDiscovery, resume.EnableConfigDiscovery);
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
            mcpPlan: null, skillDirectories: null, customAgents: null, tools: null,
             reasoningEffort: null, userInputHandler: null,
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
            mcpPlan: null, skillDirectories: null, customAgents: null, tools: null,
             reasoningEffort: null, userInputHandler: null,
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
    public void EmptyCollections_NotAssignedToConfigExceptExplicitMcpSelection()
    {
        var config = SessionConfigBuilder.Build(
            systemPrompt: "test", model: null, workingDirectory: null,
            mcpPlan: new McpSessionPlan([], []),
           skillDirectories: null,
            customAgents: [],
            tools: [],
            reasoningEffort: "", userInputHandler: null,
            onPermission: null, hooks: null);

        Assert.Null(config.SkillDirectories);
        Assert.Null(config.CustomAgents);
        Assert.Null(config.Tools);
        Assert.NotNull(config.McpServers);
        Assert.Empty(config.McpServers!);
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
            mcpPlan: null, skillDirectories: null, customAgents: null, tools: null,
             reasoningEffort: null, userInputHandler: null,
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
        Assert.Contains("builtin:*", config.ExcludedTools!);
        Assert.Contains("mcp:*", config.ExcludedTools!);
        Assert.Contains("custom:*", config.ExcludedTools!);
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
        var exposedTool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(config.Tools!));
        Assert.Same(tool.UnderlyingMethod, exposedTool.UnderlyingMethod);
        Assert.Equal(tool.JsonSchema.GetRawText(), exposedTool.JsonSchema.GetRawText());
        Assert.Equal(CopilotToolDefer.Never,
            Assert.IsType<CopilotToolDefer>(exposedTool.AdditionalProperties["defer"]));
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
        var local = new McpStdioServerConfig
        {
            Command = "npx",
            Args = ["-y", "@mcp/server"],
            WorkingDirectory = "/tmp",
            Tools = ["*"]
        };

        var remote = new McpHttpServerConfig
        {
            Url = "https://example.com/mcp",
            Tools = ["tool1", "tool2"]
        };

        Assert.Equal("stdio", local.Type);
        Assert.Equal("http", remote.Type);
        Assert.Equal("npx", local.Command);
        Assert.Equal("https://example.com/mcp", remote.Url);

        // Verify they can be placed in the config dictionary
        var mcpServers = new Dictionary<string, McpServerConfig>
        {
            ["local-server"] = local,
            ["remote-server"] = remote
        };

        var config = SessionConfigBuilder.Build(
            systemPrompt: null, model: null, workingDirectory: null,
            mcpPlan: new McpSessionPlan(mcpServers, []), skillDirectories: null, customAgents: null, tools: null,
            reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: null);

        Assert.NotNull(config.McpServers);
        Assert.Equal(2, config.McpServers!.Count);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 30. Session fork — sessions.fork gives a branch the model's real memory
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lumi's "fork chat" relies on the server-level <c>sessions.fork</c> RPC to duplicate a live
    /// session, so the branch continues with the model's actual conversation state instead of
    /// having the transcript replayed to it as text. Verifies the copy inherits memory, that the
    /// branches are isolated afterwards, and that the fork resumes under Lumi's normal resume
    /// config (which requires CopilotService to relocate it into Lumi's config directory).
    /// </summary>
    [SkippableFact]
    public async Task ForkSession_InheritsMemory_AndBranchesStayIsolated()
    {
        SkipIfDisabled();

        var original = await _service.CreateSessionAsync(SimpleConfig("You are a terse test assistant."));
        var magic = $"Zephyr{Random.Shared.Next(10000, 99999)}";

        var (_, learnSub) = await SendAndWait(original, $"Remember this magic word: {magic}. Reply only: OK");
        learnSub.Dispose();

        var forkedId = await _service.ForkSessionAsync(original.SessionId, name: "integration fork");
        Assert.False(string.IsNullOrWhiteSpace(forkedId));
        Assert.NotEqual(original.SessionId, forkedId);

        // Resume with Lumi's real resume config: this only succeeds because ForkSessionAsync moved
        // the forked session out of the CLI's default base dir into Lumi's ConfigDirectory.
        var fork = await _service.ResumeSessionAsync(forkedId!, ResumeConfigFor("You are a terse test assistant."));

        var (recall, recallSub) = await SendAndWait(fork, "What is the magic word I told you? Reply with just the word.");
        recallSub.Dispose();
        Assert.Contains(magic, recall, StringComparison.OrdinalIgnoreCase);

        // Teaching the branch something new must never leak back into the original.
        var branchOnly = $"Quasar{Random.Shared.Next(10000, 99999)}";
        var (_, teachSub) = await SendAndWait(fork, $"Remember a second word: {branchOnly}. Reply only: OK");
        teachSub.Dispose();

        var (isolation, isolationSub) = await SendAndWait(original,
            $"Do you know the word {branchOnly}? Answer YES or NO only.");
        isolationSub.Dispose();
        Assert.DoesNotContain("YES", isolation, StringComparison.OrdinalIgnoreCase);

        await fork.DisposeAsync();
        await original.DisposeAsync();
    }

    /// <summary>
    /// "Fork from here" passes a cut event id, and the forked session must not carry any turn after
    /// that point — otherwise the model would remember more than the forked transcript shows.
    /// </summary>
    [SkippableFact]
    public async Task ForkSession_WithCutEvent_DropsLaterTurns()
    {
        SkipIfDisabled();

        var original = await _service.CreateSessionAsync(SimpleConfig("You are a terse test assistant."));

        var first = $"Alpha{Random.Shared.Next(10000, 99999)}";
        var second = $"Beta{Random.Shared.Next(10000, 99999)}";

        var (_, s1) = await SendAndWait(original, $"Remember word ONE: {first}. Reply only: OK");
        s1.Dispose();

        // Everything up to here is what the fork should keep.
        var cutEventId = (await original.GetEventsAsync())[^1].Id.ToString();

        var (_, s2) = await SendAndWait(original, $"Remember word TWO: {second}. Reply only: OK");
        s2.Dispose();

        var forkedId = await _service.ForkSessionAsync(original.SessionId, cutEventId, "integration cut fork");
        Assert.False(string.IsNullOrWhiteSpace(forkedId));

        var fork = await _service.ResumeSessionAsync(forkedId!, ResumeConfigFor("You are a terse test assistant."));
        var (known, sub) = await SendAndWait(fork,
            "List every code word you know, comma separated. If none, say NONE.");
        sub.Dispose();

        Assert.Contains(first, known, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(second, known, StringComparison.OrdinalIgnoreCase);

        await fork.DisposeAsync();
        await original.DisposeAsync();
    }

    /// <summary>
    /// End-to-end check of the seam Lumi actually ships: the cut event is chosen by
    /// <see cref="PendingTurnRecoveryAnalyzer.SelectForkCutEvent"/> from a real server event log
    /// (not a hand-picked index), then handed to <c>ForkSessionAsync</c>. This is what catches an
    /// off-by-one between local turns and server events — the previous test only proves the SDK
    /// honours a cut id, not that Lumi picks the right one.
    /// </summary>
    [SkippableFact]
    public async Task ForkSession_CutEventChosenByAnalyzer_KeepsExactlyTheRetainedTurns()
    {
        SkipIfDisabled();

        var original = await _service.CreateSessionAsync(SimpleConfig("You are a terse test assistant."));

        var first = $"Alpha{Random.Shared.Next(10000, 99999)}";
        var second = $"Beta{Random.Shared.Next(10000, 99999)}";

        var (_, s1) = await SendAndWait(original, $"Remember word ONE: {first}. Reply only: OK");
        s1.Dispose();
        var (_, s2) = await SendAndWait(original, $"Remember word TWO: {second}. Reply only: OK");
        s2.Dispose();

        // Exactly what ChatViewModel does for "fork from here" on the first assistant reply:
        // one local user turn is retained, so turn 2 is the first excluded one.
        var events = await original.GetEventsAsync();
        var selection = PendingTurnRecoveryAnalyzer.SelectForkCutEvent(events, 1);
        Assert.True(selection.Resolved);
        Assert.NotNull(selection.Event);

        var forkedId = await _service.ForkSessionAsync(
            original.SessionId, selection.Event!.Id.ToString(), "integration analyzer fork");
        Assert.False(string.IsNullOrWhiteSpace(forkedId));

        var fork = await _service.ResumeSessionAsync(forkedId!, ResumeConfigFor("You are a terse test assistant."));
        var (known, sub) = await SendAndWait(fork,
            "List every code word you know, comma separated. If none, say NONE.");
        sub.Dispose();

        Assert.Contains(first, known, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(second, known, StringComparison.OrdinalIgnoreCase);

        await fork.DisposeAsync();
        await original.DisposeAsync();
    }

    [SkippableFact]
    public async Task ForkSession_WholeConversation_SkipsTurnResolution()
    {
        SkipIfDisabled();

        var original = await _service.CreateSessionAsync(SimpleConfig("You are a terse test assistant."));
        var first = $"Alpha{Random.Shared.Next(10000, 99999)}";
        var second = $"Beta{Random.Shared.Next(10000, 99999)}";

        var (_, s1) = await SendAndWait(original, $"Remember word ONE: {first}. Reply only: OK");
        s1.Dispose();
        var (_, s2) = await SendAndWait(original, $"Remember word TWO: {second}. Reply only: OK");
        s2.Dispose();

        var forkedId = await ChatViewModel.ForkSessionAtTurnAsync(
            _service,
            original.SessionId,
            original,
            sessionForkCutUserTurns: null,
            name: "integration whole fork");

        Assert.False(string.IsNullOrWhiteSpace(forkedId));

        var fork = await _service.ResumeSessionAsync(
            forkedId!,
            ResumeConfigFor("You are a terse test assistant."));
        var (known, sub) = await SendAndWait(
            fork,
            "List every code word you know, comma separated. If none, say NONE.");
        sub.Dispose();

        Assert.Contains(first, known, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(second, known, StringComparison.OrdinalIgnoreCase);

        await fork.DisposeAsync();
        await original.DisposeAsync();
    }

    /// <summary>A released session has no in-memory events, so it cannot be forked — Lumi must fall
    /// back to transcript replay rather than silently producing an empty branch.</summary>
    [SkippableFact]
    public async Task ForkSession_ReleasedSession_ReturnsNullSoCallerCanFallBack()
    {
        SkipIfDisabled();

        var original = await _service.CreateSessionAsync(SimpleConfig("You are a terse test assistant."));
        var sessionId = original.SessionId;

        var (_, sub) = await SendAndWait(original, "Remember the word Orbit. Reply only: OK");
        sub.Dispose();

        await _service.ReleaseSessionAsync(original);

        Assert.Null(await _service.ForkSessionAsync(sessionId));
    }

    [SkippableFact]
    public async Task ForkSession_UnknownSessionId_ReturnsNull()
    {
        SkipIfDisabled();

        Assert.Null(await _service.ForkSessionAsync("not-a-real-session-id-98765"));
        Assert.Null(await _service.ForkSessionAsync(null));
        Assert.Null(await _service.ForkSessionAsync("   "));
    }

    private static ResumeSessionConfig ResumeConfigFor(string systemPrompt) =>
        SessionConfigBuilder.BuildForResume(
            systemPrompt: systemPrompt,
            model: null, workingDirectory: null, mcpPlan: null,
            skillDirectories: null,
            customAgents: null, tools: null,
            reasoningEffort: null, userInputHandler: null,
            onPermission: null, hooks: null);
}
