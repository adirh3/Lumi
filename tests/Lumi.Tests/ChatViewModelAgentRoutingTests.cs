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

        Assert.Contains(ToolDisplayHelper.BrowserOpenToolName, toolNames);
        Assert.Contains(ToolDisplayHelper.BrowserLookToolName, toolNames);
        Assert.Contains(ToolDisplayHelper.BrowserFindToolName, toolNames);
        Assert.Contains(ToolDisplayHelper.BrowserDoToolName, toolNames);
        Assert.Contains(ToolDisplayHelper.BrowserJsToolName, toolNames);
        Assert.DoesNotContain("browser", toolNames);
        Assert.DoesNotContain(toolNames, static name => name.StartsWith("browser_", StringComparison.Ordinal));
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

    private static List<AIFunction> InvokeBuildCustomTools(ChatViewModel viewModel)
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
            [Guid.NewGuid(), null, catalog]));
    }

    private sealed record TestHarness(ChatViewModel ViewModel) : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
