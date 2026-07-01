using System.Reflection;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatViewModelEditTests
{
    [Fact]
    public void BeginComposerEdit_LoadsMessageAttachmentsAndTurnSelectionsIntoComposer()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-edit-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var messageAttachment = Path.Combine(tempRoot, "message-attachment.txt");
        File.WriteAllText(messageAttachment, "message attachment");

        try
        {
            var skill = new Skill
            {
                Id = Guid.NewGuid(),
                Name = "Review skill",
                IconGlyph = "R"
            };
            var agent = new LumiAgent
            {
                Id = Guid.NewGuid(),
                Name = "Review Agent",
                IconGlyph = "A"
            };
            var activeMcp = new McpServer { Name = "docs", IsEnabled = true };
            var inactiveForMessageMcp = new McpServer { Name = "calendar", IsEnabled = true };
            var message = new ChatMessage
            {
                Role = "user",
                Content = "Original message",
                Model = "claude-sonnet-4.6",
                ReasoningEffort = "high",
                AgentId = agent.Id,
                HasAgentSelection = true,
                ActiveMcpServerNames = [activeMcp.Name],
                HasMcpSelection = true,
                Attachments = [messageAttachment],
                ActiveSkills =
                [
                    new SkillReference
                    {
                        Name = skill.Name,
                        Glyph = skill.IconGlyph,
                        Description = skill.Description
                    }
                ]
            };
            var chat = new Chat
            {
                Id = Guid.NewGuid(),
                Title = "Edit test",
                Messages = [message],
                ActiveMcpServerNames = [inactiveForMessageMcp.Name],
                LastModelUsed = "gpt-5-mini"
            };
            var appData = new AppData
            {
                Settings = new UserSettings { PreferredModel = "gpt-5-mini" },
                Chats = [chat],
                Skills = [skill],
                Agents = [agent],
                McpServers = [activeMcp, inactiveForMessageMcp]
            };
            var viewModel = new ChatViewModel(new DataStore(appData), new CopilotService())
            {
                CurrentChat = chat,
                PromptText = "Draft before editing"
            };

            InvokeBeginComposerEdit(viewModel, message);

            Assert.True(viewModel.IsEditingMessage);
            Assert.Equal("Original message", viewModel.PromptText);
            Assert.Equal(messageAttachment, Assert.Single(viewModel.PendingAttachments));
            Assert.Same(agent, viewModel.ActiveAgent);
            Assert.Equal(agent.Name, viewModel.SelectedAgentName);
            Assert.Equal("claude-sonnet-4.6", viewModel.SelectedModel);
            Assert.Equal(skill.Id, Assert.Single(viewModel.ActiveSkillIds));
            Assert.Equal(activeMcp.Name, Assert.Single(viewModel.ActiveMcpServerNames));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void BeginComposerEdit_WithExplicitEmptyAgentAndMcpSelectionsClearsComposerChips()
    {
        var agent = new LumiAgent
        {
            Id = Guid.NewGuid(),
            Name = "Draft Agent"
        };
        var mcp = new McpServer { Name = "docs", IsEnabled = true };
        var message = new ChatMessage
        {
            Role = "user",
            Content = "Original message",
            HasAgentSelection = true,
            HasMcpSelection = true
        };
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Edit test",
            AgentId = agent.Id,
            ActiveMcpServerNames = [mcp.Name],
            Messages = [message]
        };
        var appData = new AppData
        {
            Chats = [chat],
            Agents = [agent],
            McpServers = [mcp]
        };
        var viewModel = new ChatViewModel(new DataStore(appData), new CopilotService())
        {
            CurrentChat = chat
        };
        viewModel.SetActiveAgent(agent);
        viewModel.AddMcpServer(mcp.Name);

        InvokeBeginComposerEdit(viewModel, message);

        Assert.Null(viewModel.ActiveAgent);
        Assert.Null(viewModel.SelectedSdkAgentName);
        Assert.Null(viewModel.SelectedAgentName);
        Assert.Empty(viewModel.ActiveMcpServerNames);
        Assert.Empty(viewModel.ActiveMcpChips);
    }

    [Fact]
    public void SwitchingEditedMessageRestoresOriginalComposerSnapshotOnCancel()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-edit-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var draftAttachment = Path.Combine(tempRoot, "draft.txt");
        var firstMessageAttachment = Path.Combine(tempRoot, "first-message.txt");
        File.WriteAllText(draftAttachment, "draft attachment");
        File.WriteAllText(firstMessageAttachment, "first message attachment");

        try
        {
            var firstMessage = new ChatMessage
            {
                Role = "user",
                Content = "First message",
                Attachments = [firstMessageAttachment]
            };
            var secondMessage = new ChatMessage
            {
                Role = "user",
                Content = "Second message"
            };
            var chat = new Chat
            {
                Id = Guid.NewGuid(),
                Title = "Edit test",
                Messages = [firstMessage, secondMessage]
            };
            var viewModel = new ChatViewModel(new DataStore(new AppData { Chats = [chat] }), new CopilotService())
            {
                CurrentChat = chat,
                PromptText = "Draft before editing"
            };
            viewModel.AddAttachment(draftAttachment);

            InvokeBeginComposerEdit(viewModel, firstMessage);
            viewModel.PromptText = "Unsaved first edit";

            InvokeBeginComposerEdit(viewModel, secondMessage);
            viewModel.CancelComposerEditCommand.Execute(null);

            Assert.Equal("Draft before editing", viewModel.PromptText);
            var attachment = Assert.Single(viewModel.PendingAttachments);
            Assert.Equal(draftAttachment, attachment);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static void InvokeBeginComposerEdit(ChatViewModel viewModel, ChatMessage message)
    {
        var method = typeof(ChatViewModel).GetMethod(
            "BeginComposerEdit",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        method!.Invoke(viewModel, [message]);
    }
}
