using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
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
    public void BuildSkillReferences_UsesTargetWorkDirForHiddenChatExternalSkills()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-skill-route-test-{Guid.NewGuid():N}");
        var targetWorkDir = Path.Combine(tempRoot, "target");
        var visibleWorkDir = Path.Combine(tempRoot, "visible");

        try
        {
            Directory.CreateDirectory(Path.Combine(targetWorkDir, ".github", "skills"));
            Directory.CreateDirectory(Path.Combine(visibleWorkDir, ".github", "skills"));
            File.WriteAllText(
                Path.Combine(targetWorkDir, ".github", "skills", "target-skill.md"),
                """
                ---
                name: Target Skill
                description: Skill from the target project
                ---

                Use the target project's context.
                """);
            File.WriteAllText(
                Path.Combine(visibleWorkDir, ".github", "skills", "visible-skill.md"),
                """
                ---
                name: Visible Skill
                description: Skill from the visible project
                ---

                Use the visible project's context.
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

            var references = InvokeBuildSkillReferences(
                harness.ViewModel,
                externalSkillNames: ["Target Skill"],
                workDir: targetWorkDir);

            var skill = Assert.Single(references);
            Assert.Equal("Target Skill", skill.Name);
            Assert.Equal("Skill from the target project", skill.Description);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void FindSkillReferenceByName_UsesExplicitWorkDirForExternalSkills()
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

            var reference = harness.ViewModel.FindSkillReferenceByName("Shared Skill", targetWorkDir);

            Assert.NotNull(reference);
            Assert.Equal("Shared Skill", reference!.Name);
            Assert.Equal("Target project version", reference.Description);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
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
            Messages = [new ChatMessage { Role = "user", Content = "hello" }]
        };
    }

    private static List<SkillReference> InvokeBuildSkillReferences(
        ChatViewModel viewModel,
        IReadOnlyCollection<string> externalSkillNames,
        string workDir)
    {
        var method = typeof(ChatViewModel).GetMethod(
            "BuildSkillReferences",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(IReadOnlyCollection<Guid>), typeof(IReadOnlyCollection<string>), typeof(string)],
            modifiers: null);

        Assert.NotNull(method);
        return Assert.IsType<List<SkillReference>>(method!.Invoke(
            viewModel,
            [Array.Empty<Guid>(), externalSkillNames, workDir]));
    }

    private sealed record TestHarness(ChatViewModel ViewModel) : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
