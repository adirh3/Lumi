using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Microsoft.Extensions.AI;
using System.Reflection;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatViewModelAgentRoutingTests
{
    [Fact]
    public void GetSessionSdkAgentName_DoesNotUseSelectedAgentFromAnotherChat()
    {
        var targetChat = new Chat { Id = Guid.NewGuid(), Title = "Job chat" };
        var visibleChat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Visible chat",
            SdkAgentName = "Project B Agent"
        };

        var agentName = ChatViewModel.GetSessionSdkAgentName(
            targetChat,
            visibleChat,
            selectedSdkAgentName: "Project B Agent");

        Assert.Null(agentName);
    }

    [Fact]
    public void GetSessionSdkAgentName_UsesTargetChatPersistedAgent()
    {
        var targetChat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Job chat",
            SdkAgentName = "Project A Agent"
        };
        var visibleChat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Visible chat",
            SdkAgentName = "Project B Agent"
        };

        var agentName = ChatViewModel.GetSessionSdkAgentName(
            targetChat,
            visibleChat,
            selectedSdkAgentName: "Project B Agent");

        Assert.Equal("Project A Agent", agentName);
    }

    [Fact]
    public void ResolveSessionAgentName_DoesNotRouteFileBasedExternalAgentThroughSessionConfig()
    {
        var externalAgent = new CopilotAgentDefinition(
            "Workspace Agent",
            "Workspace-specific agent",
            "Use workspace context.",
            "AGENT.md");

        var agentName = ChatViewModel.ResolveSessionAgentName(
            activeAgent: null,
            externalAgent,
            sdkAgentName: "Workspace Agent",
            allowSdkAgentRouting: true);

        Assert.Null(agentName);
    }

    [Fact]
    public void ResolveSessionAgentName_DoesNotRouteUnavailableSdkAgent()
    {
        var agentName = ChatViewModel.ResolveSessionAgentName(
            activeAgent: null,
            externalAgent: null,
            sdkAgentName: "Project B Agent",
            allowSdkAgentRouting: false);

        Assert.Null(agentName);
    }

    [Fact]
    public void ResolveSessionAgentName_RoutesLumiAgent()
    {
        var lumiAgent = new LumiAgent { Name = "Coding Lumi" };

        var agentName = ChatViewModel.ResolveSessionAgentName(
            lumiAgent,
            externalAgent: null,
            sdkAgentName: "Project B Agent",
            allowSdkAgentRouting: false);

        Assert.Equal("Coding Lumi", agentName);
    }

    [Fact]
    public void SubagentOutputIsActive_FalseWhenNoNestedSubagentExecuting()
    {
        // Regression: selecting a Lumi agent makes the CLI emit subagent.selected for the
        // top-level configured agent (no nested execution). Output suppression must be driven
        // ONLY by genuine nested sub-agent execution, so with ActiveSubagentExecutionDepth == 0
        // the main turn must NOT be suppressed — otherwise the whole reply is dropped.
        var runtime = new ChatRuntimeState
        {
            Chat = new Chat { Title = "top-level agent" },
            ActiveSubagentExecutionDepth = 0
        };

        Assert.False(ChatViewModel.SubagentOutputIsActive(runtime));
    }

    [Fact]
    public void SubagentOutputIsActive_TrueWhileNestedSubagentExecuting()
    {
        // Genuine nested sub-agents are bracketed by subagent.started/completed which drive
        // ActiveSubagentExecutionDepth; their output must still be routed away from the main
        // transcript.
        var runtime = new ChatRuntimeState
        {
            Chat = new Chat { Title = "nested subagent" },
            ActiveSubagentExecutionDepth = 1
        };

        Assert.True(ChatViewModel.SubagentOutputIsActive(runtime));
    }

    [Fact]
    public void ResolveSelectedModelForChat_DoesNotUseVisibleChatSelectionForHiddenChat()
    {
        var targetChat = new Chat { Id = Guid.NewGuid(), Title = "Job chat" };
        var visibleChat = CreateChatWithMessage("Visible chat");
        using var harness = CreateHarness(new AppData
        {
            Settings = new UserSettings { PreferredModel = "global-model" },
            Chats = [targetChat, visibleChat]
        });
        harness.ViewModel.CurrentChat = visibleChat;
        harness.ViewModel.SelectedModel = "visible-chat-model";

        var model = harness.ViewModel.ResolveSelectedModelForChat(targetChat);

        Assert.Equal("global-model", model);
    }

    [Fact]
    public void ResolveSelectedModelForChat_UsesVisibleSelectionForCurrentChat()
    {
        var currentChat = new Chat { Id = Guid.NewGuid(), Title = "Current chat" };
        using var harness = CreateHarness(new AppData { Chats = [currentChat] });
        harness.ViewModel.CurrentChat = currentChat;
        harness.ViewModel.SelectedModel = "current-chat-model";

        var model = harness.ViewModel.ResolveSelectedModelForChat(currentChat);

        Assert.Equal("current-chat-model", model);
    }

    [Fact]
    public void ResolvePersistedReasoningEffortForChat_DoesNotUseVisibleChatSelectionForHiddenChat()
    {
        var targetChat = new Chat { Id = Guid.NewGuid(), Title = "Job chat" };
        var visibleChat = CreateChatWithMessage("Visible chat");
        using var harness = CreateHarness(new AppData
        {
            Settings = new UserSettings { ReasoningEffort = "medium" },
            Chats = [targetChat, visibleChat]
        });
        harness.ViewModel.CurrentChat = visibleChat;
        harness.ViewModel.SelectedQuality = "high";

        var effort = harness.ViewModel.ResolvePersistedReasoningEffortForChat(targetChat, modelId: "gpt-5.4");

        Assert.Equal("medium", effort);
    }

    [Fact]
    public void ResolvePersistedReasoningEffortForChat_DropsEffort_ForEffortLessModel_WhenCatalogLoaded()
    {
        // Regression guard for the manage_chats send/create override on effort-less models (e.g.
        // claude-sonnet-4.5). With a loaded catalog, a stored/global effort must NOT be forwarded to a model
        // that has no reasoning-effort support — doing so errors the turn on session setup (and is swallowed on a
        // mid-session switch, silently keeping the previous model and defeating the model override).
        var targetChat = new Chat { Id = Guid.NewGuid(), Title = "Job chat" };
        var visibleChat = CreateChatWithMessage("Visible chat");
        using var harness = CreateHarness(new AppData
        {
            Settings = new UserSettings { ReasoningEffort = "high" },
            Chats = [targetChat, visibleChat]
        });
        harness.ViewModel.UpdateModelCapabilities([CreateModel("effort-capable", "low", "medium", "high")]);
        harness.ViewModel.CurrentChat = visibleChat;

        var effort = harness.ViewModel.ResolvePersistedReasoningEffortForChat(targetChat, modelId: "effort-less");

        Assert.Null(effort);
    }

    [Fact]
    public void ResolvePersistedReasoningEffortForChat_KeepsEffort_ForEffortCapableModel_WhenCatalogLoaded()
    {
        var targetChat = new Chat { Id = Guid.NewGuid(), Title = "Job chat" };
        var visibleChat = CreateChatWithMessage("Visible chat");
        using var harness = CreateHarness(new AppData
        {
            Settings = new UserSettings { ReasoningEffort = "low" },
            Chats = [targetChat, visibleChat]
        });
        harness.ViewModel.UpdateModelCapabilities([CreateModel("effort-capable", "low", "medium", "high")]);
        harness.ViewModel.CurrentChat = visibleChat;

        var effort = harness.ViewModel.ResolvePersistedReasoningEffortForChat(targetChat, modelId: "effort-capable");

        Assert.Equal("low", effort);
    }

    [Fact]
    public void ResolvePersistedReasoningEffortForChat_DropsEffort_ForEffortLessModel_WhenChatIsCurrentSurface()
    {
        // The manage_chats orchestration executor loads the target chat as its OWN CurrentChat, so
        // ResolvePersistedReasoningEffortForChat takes the CurrentChat branch (live UI preference) rather
        // than the hidden-chat branch. That branch must STILL validate the effort against the resolved
        // model: a stored "low" on an effort-less model such as claude-sonnet-4.5 must be dropped, not
        // forwarded to the SDK (which errors the session on setup). Direct regression guard for the live
        // bug where manage_chats create/send model=claude-sonnet-4.5 reasoningEffort=low errored the worker
        // turn even though the model catalog was fully loaded — the older code returned the raw preference
        // here and skipped model validation entirely.
        var currentChat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Worker chat",
            LastReasoningEffortUsed = "low",
            Messages = [new Lumi.Models.ChatMessage { Role = "user", Content = "hello" }]
        };
        using var harness = CreateHarness(new AppData
        {
            Settings = new UserSettings { ReasoningEffort = "high" },
            Chats = [currentChat]
        });
        harness.ViewModel.UpdateModelCapabilities([CreateModel("effort-capable", "low", "medium", "high")]);
        harness.ViewModel.CurrentChat = currentChat;

        var effort = harness.ViewModel.ResolvePersistedReasoningEffortForChat(currentChat, modelId: "effort-less");

        Assert.Null(effort);
    }

    [Fact]
    public void ResolvePersistedReasoningEffortForChat_KeepsEffort_ForEffortCapableModel_WhenChatIsCurrentSurface()
    {
        // Companion to the effort-less current-surface guard: the CurrentChat branch must still return a
        // supported effort for a model that DOES support it, so an orchestrated create/send with a valid
        // model+effort override isn't silently downgraded.
        var currentChat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Worker chat",
            LastReasoningEffortUsed = "low",
            Messages = [new Lumi.Models.ChatMessage { Role = "user", Content = "hello" }]
        };
        using var harness = CreateHarness(new AppData
        {
            Settings = new UserSettings { ReasoningEffort = "high" },
            Chats = [currentChat]
        });
        harness.ViewModel.UpdateModelCapabilities([CreateModel("effort-capable", "low", "medium", "high")]);
        harness.ViewModel.CurrentChat = currentChat;

        var effort = harness.ViewModel.ResolvePersistedReasoningEffortForChat(currentChat, modelId: "effort-capable");

        Assert.Equal("low", effort);
    }

    [Fact]
    public void ResolveReasoningEffortForModel_PreservesStoredEffort_WhenCatalogNotLoaded()
    {
        // Before the model catalog loads NormalizeEffort returns null for every model; the stored effort must be
        // preserved so a pre-load selection/override isn't lost (distinct from a known effort-less model).
        using var harness = CreateHarness(new AppData());

        var effort = harness.ViewModel.ResolveReasoningEffortForModel("high", "any-model");

        Assert.Equal("high", effort);
    }

    [Fact]
    public void ResolveReasoningEffortForModel_DropsEffort_ForEffortLessModel_WhenCatalogLoaded()
    {
        using var harness = CreateHarness(new AppData());
        harness.ViewModel.UpdateModelCapabilities([CreateModel("effort-capable", "low", "medium", "high")]);

        var effort = harness.ViewModel.ResolveReasoningEffortForModel("high", "effort-less");

        Assert.Null(effort);
    }

    [Fact]
    public void FindSkillReferenceByName_DoesNotResolveExternalSkills()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-fetch-skill-route-test-{Guid.NewGuid():N}");
        var targetWorkDir = Path.Combine(tempRoot, "target");
        var visibleWorkDir = Path.Combine(tempRoot, "visible");

        try
        {
            Directory.CreateDirectory(Path.Combine(targetWorkDir, ".github", "skills"));
            Directory.CreateDirectory(Path.Combine(visibleWorkDir, ".github", "skills"));
            File.WriteAllText(
                Path.Combine(targetWorkDir, ".github", "skills", "shared-skill.md"),
                """
                ---
                name: Shared Skill
                description: Target project version
                ---

                Use the target project version.
                """);
            File.WriteAllText(
                Path.Combine(visibleWorkDir, ".github", "skills", "shared-skill.md"),
                """
                ---
                name: Shared Skill
                description: Visible project version
                ---

                Use the visible project version.
                """);

            var visibleProject = new Project { Id = Guid.NewGuid(), Name = "Visible", WorkingDirectory = visibleWorkDir };
            var visibleChat = CreateChatWithMessage("Visible chat");
            visibleChat.ProjectId = visibleProject.Id;
            using var harness = CreateHarness(new AppData
            {
                Projects = [visibleProject],
                Chats = [visibleChat]
            });
            harness.ViewModel.CurrentChat = visibleChat;

            Assert.Null(harness.ViewModel.FindSkillReferenceByName("Shared Skill", targetWorkDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void BuildCustomTools_UsesLumiBrowserNamespace()
    {
        using var harness = CreateHarness(new AppData());

        var toolNames = InvokeBuildCustomTools(harness.ViewModel)
            .Select(tool => tool.Name)
            .ToArray();

        // The embedded browser (WebView2) is Windows-only, so the lumi_browser_* tools are only
        // registered on Windows. Elsewhere they must be absent so the agent isn't told about them.
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(ToolDisplayHelper.BrowserOpenToolName, toolNames);
            Assert.Contains(ToolDisplayHelper.BrowserLookToolName, toolNames);
            Assert.Contains(ToolDisplayHelper.BrowserFindToolName, toolNames);
            Assert.Contains(ToolDisplayHelper.BrowserDoToolName, toolNames);
            Assert.Contains(ToolDisplayHelper.BrowserJsToolName, toolNames);
        }
        else
        {
            Assert.DoesNotContain(ToolDisplayHelper.BrowserOpenToolName, toolNames);
            Assert.DoesNotContain(ToolDisplayHelper.BrowserJsToolName, toolNames);
        }

        // Regardless of platform, no tool should use the bare "browser" namespace.
        Assert.DoesNotContain("browser", toolNames);
        Assert.DoesNotContain(toolNames, static name => name.StartsWith("browser_", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildCustomTools_NoAgentInjectsAllLumiToolCategories()
    {
        using var harness = CreateHarness(new AppData());

        var toolNames = InvokeBuildCustomTools(harness.ViewModel)
            .Select(tool => tool.Name)
            .ToArray();

        Assert.Contains("lumi_fetch", toolNames);
        Assert.Contains("ask_question", toolNames);
        Assert.Contains("manage_current_chat", toolNames);
        Assert.Contains("manage_lumis", toolNames);
        Assert.Contains("code_review", toolNames);
        Assert.Contains(ToolDisplayHelper.BrowserOpenToolName, toolNames);
        if (OperatingSystem.IsWindows())
            Assert.Contains("ui_list_windows", toolNames);
    }

    [Fact]
    public void BuildCustomAgents_DoesNotSetSdkToolAllowlist()
    {
        var agent = new LumiAgent
        {
            Name = "Restricted Lumi",
            ToolNames = ["lumi_fetch"]
        };
        using var harness = CreateHarness(new AppData { Agents = [agent] });

        var config = Assert.Single(InvokeBuildCustomAgents(harness.ViewModel));

        Assert.Null(config.Tools);
    }

    [Fact]
    public void BuildCustomTools_RestrictedAgentFiltersOnlyLumiInjectedTools()
    {
        var agent = new LumiAgent
        {
            Name = "Research Lumi",
            ToolNames = ["web_search", "lumi_fetch"]
        };
        using var harness = CreateHarness(new AppData());

        var toolNames = InvokeBuildCustomTools(harness.ViewModel, agent)
            .Select(tool => tool.Name)
            .ToArray();

        Assert.Equal(["lumi_fetch"], toolNames);
    }

    [Fact]
    public void BuildCustomTools_ExplicitEmptySelectionInjectsNoLumiTools()
    {
        var agent = new LumiAgent
        {
            Name = "Prompt-only Lumi",
            HasExplicitToolSelection = true
        };
        using var harness = CreateHarness(new AppData());

        Assert.Empty(InvokeBuildCustomTools(harness.ViewModel, agent));
    }

    [Fact]
    public void SetActiveAgent_BusySessionDefersToolReconfiguration()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Busy chat",
            CopilotSessionId = "session-1"
        };
        var agent = new LumiAgent { Id = Guid.NewGuid(), Name = "Restricted Lumi" };
        using var harness = CreateHarness(new AppData { Chats = [chat], Agents = [agent] });
        harness.ViewModel.CurrentChat = chat;

        var cancellationSources = GetPrivateField<Dictionary<Guid, CancellationTokenSource>>(
            harness.ViewModel,
            "_ctsSources");
        using var turnCts = new CancellationTokenSource();
        cancellationSources[chat.Id] = turnCts;

        harness.ViewModel.SetActiveAgent(agent);

        var pendingReconfigurations = GetPrivateField<HashSet<Guid>>(
            harness.ViewModel,
            "_pendingSessionReconfigurations");
        Assert.Contains(chat.Id, pendingReconfigurations);
        Assert.Equal(agent.Id, chat.AgentId);
    }

    [Fact]
    public void SetActiveAgent_DeselectingBusySessionBeforeSessionCreationDefersToolReconfiguration()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "First-turn chat"
        };
        var agent = new LumiAgent { Id = Guid.NewGuid(), Name = "Restricted Lumi" };
        using var harness = CreateHarness(new AppData { Chats = [chat], Agents = [agent] });
        harness.ViewModel.CurrentChat = chat;
        harness.ViewModel.SetActiveAgent(agent);

        var cancellationSources = GetPrivateField<Dictionary<Guid, CancellationTokenSource>>(
            harness.ViewModel,
            "_ctsSources");
        using var turnCts = new CancellationTokenSource();
        cancellationSources[chat.Id] = turnCts;

        harness.ViewModel.SetActiveAgent(null);

        var pendingReconfigurations = GetPrivateField<HashSet<Guid>>(
            harness.ViewModel,
            "_pendingSessionReconfigurations");
        Assert.Contains(chat.Id, pendingReconfigurations);
        Assert.Null(chat.AgentId);
        Assert.Null(harness.ViewModel.ActiveAgent);
        Assert.Null(chat.CopilotSessionId);
    }

    private static TestHarness CreateHarness(AppData data)
    {
        var store = new DataStore(data);
        return new TestHarness(new ChatViewModel(store, new CopilotService()));
    }

    private static Chat CreateChatWithMessage(string title)
    {
        return new Chat
        {
            Id = Guid.NewGuid(),
            Title = title,
            Messages = [new Lumi.Models.ChatMessage { Role = "user", Content = "hello" }]
        };
    }

    private static GitHub.Copilot.ModelInfo CreateModel(string id, params string[] efforts)
        => new()
        {
            Id = id,
            Name = id,
            SupportedReasoningEfforts = efforts.ToList(),
            DefaultReasoningEffort = efforts.Length > 0 ? "high" : null
        };

    private static List<CustomAgentConfig> InvokeBuildCustomAgents(ChatViewModel viewModel)
    {
        var method = typeof(ChatViewModel).GetMethod(
            "BuildCustomAgents",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return Assert.IsType<List<CustomAgentConfig>>(method!.Invoke(viewModel, [null]));
    }

    private static List<AIFunction> InvokeBuildCustomTools(ChatViewModel viewModel, LumiAgent? agent = null)
    {
        var method = typeof(ChatViewModel).GetMethod(
            "BuildCustomTools",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(Guid),
                typeof(LumiAgent),
                typeof(ProjectContextCatalogSnapshot)
            ],
            modifiers: null);

        Assert.NotNull(method);
        var catalog = new ProjectContextCatalogSnapshot([], [], []);
        return Assert.IsType<List<AIFunction>>(method!.Invoke(
            viewModel,
            [Guid.NewGuid(), agent, catalog]));
    }

    private static T GetPrivateField<T>(ChatViewModel viewModel, string fieldName)
    {
        var field = typeof(ChatViewModel).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        return Assert.IsType<T>(field!.GetValue(viewModel));
    }

    private sealed record TestHarness(ChatViewModel ViewModel) : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
