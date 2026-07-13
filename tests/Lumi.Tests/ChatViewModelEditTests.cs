using System.Reflection;
using CommunityToolkit.Mvvm.Input;
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
                ContextWindowTier = ModelContextWindowTiers.LongContext,
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
                LastModelUsed = "gpt-5-mini",
                LastContextWindowTierUsed = "default"
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
            viewModel.UpdateModelCapabilities(
                [],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude-sonnet-4.6" });

            InvokeBeginComposerEdit(viewModel, message);

            Assert.True(viewModel.IsEditingMessage);
            Assert.Equal("Original message", viewModel.PromptText);
            Assert.Equal(messageAttachment, Assert.Single(viewModel.PendingAttachments));
            Assert.Same(agent, viewModel.ActiveAgent);
            Assert.Equal(agent.Name, viewModel.SelectedAgentName);
            Assert.Equal("claude-sonnet-4.6", viewModel.SelectedModel);
            Assert.Equal(skill.Id, Assert.Single(viewModel.ActiveSkillIds));
            Assert.Equal(activeMcp.Name, Assert.Single(viewModel.ActiveMcpServerNames));
            Assert.Equal(ModelContextWindowTiers.LongContext, viewModel.GetSelectedContextWindowTier());

            // Hydrating a historical turn is composer-only: merely opening Edit must not mutate the
            // persisted chat or route the live session before the user chooses Send.
            Assert.Null(chat.AgentId);
            Assert.Empty(chat.ActiveSkillIds);
            Assert.Equal(inactiveForMessageMcp.Name, Assert.Single(chat.ActiveMcpServerNames));
            Assert.Equal("gpt-5-mini", chat.LastModelUsed);
            Assert.Equal("default", chat.LastContextWindowTierUsed);
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

    [Fact]
    public void CancelComposerEdit_RestoresPersistedModelAndContextTierChangedDuringEdit()
    {
        var message = new ChatMessage { Role = "user", Content = "Original message" };
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Edit test",
            Messages = [message],
            LastModelUsed = "model-A",
            LastReasoningEffortUsed = "high",
            LastContextWindowTierUsed = "default"
        };
        var viewModel = new ChatViewModel(new DataStore(new AppData { Chats = [chat] }), new CopilotService())
        {
            CurrentChat = chat
        };
        viewModel.UpdateModelCapabilities(
            [],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "model-A", "model-B" });
        viewModel.ApplyModelSelection("model-A", "high", "default");

        InvokeBeginComposerEdit(viewModel, message);

        // Exercise the real composer setters: these mutate per-chat persisted fields and, with a
        // normal composer, queue a mid-session SetModelAsync. Edit mode keeps them transactional.
        viewModel.SelectedModel = "model-B";
        viewModel.SelectedContextWindowTier = "Long";
        Assert.Equal("model-B", viewModel.SelectedModel);
        Assert.Equal(ModelContextWindowTiers.LongContext, viewModel.GetSelectedContextWindowTier());
        Assert.Equal("model-A", chat.LastModelUsed);
        Assert.Equal("default", chat.LastContextWindowTierUsed);

        viewModel.CancelComposerEditCommand.Execute(null);

        // Cancel must roll the visible composer and persisted selection back to the pre-edit values,
        // not leave the chat/session on the discarded in-edit selection.
        Assert.Equal("model-A", viewModel.SelectedModel);
        Assert.Equal("default", viewModel.GetSelectedContextWindowTier());
        Assert.Equal("model-A", chat.LastModelUsed);
        Assert.Equal("high", chat.LastReasoningEffortUsed);
        Assert.Equal("default", chat.LastContextWindowTierUsed);
    }

    [Fact]
    public void ComposerSelectionDivergesFromSnapshot_DetectsAgentAndMcpChanges()
    {
        var agent = new LumiAgent { Id = Guid.NewGuid(), Name = "Agent" };
        var mcp = new McpServer { Name = "docs", IsEnabled = true };
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Messages = [new ChatMessage { Role = "user", Content = "x" }]
        };
        var appData = new AppData { Chats = [chat], Agents = [agent], McpServers = [mcp] };
        var viewModel = new ChatViewModel(new DataStore(appData), new CopilotService()) { CurrentChat = chat };

        // Snapshot the baseline (no agent, no MCP, no skills).
        var snapshot = CaptureSnapshot(viewModel);

        // No composer change since the snapshot → the rewound session is still valid.
        Assert.False(InvokeDiverges(viewModel, snapshot));

        // Activating an MCP server diverges from the snapshot → must recreate the session.
        viewModel.ActiveMcpServerNames.Add("different-mcp");
        Assert.True(InvokeDiverges(viewModel, snapshot));
    }

    [Fact]
    public void ComposerSelectionDivergesFromSnapshot_WhenActiveSkillStillNeedsInjection()
    {
        var skill = new Skill { Id = Guid.NewGuid(), Name = "Skill" };
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            CopilotSessionId = "existing-session",
            Messages = [new ChatMessage { Role = "user", Content = "x" }]
        };
        var appData = new AppData { Chats = [chat], Skills = [skill] };
        var viewModel = new ChatViewModel(new DataStore(appData), new CopilotService()) { CurrentChat = chat };

        // AddSkill on an existing session marks the skill for next-turn injection. Even if the edit
        // leaves the visible skill selection unchanged, the reused session is stale and must rebuild.
        viewModel.AddSkill(skill);
        var snapshot = CaptureSnapshot(viewModel);

        Assert.True(InvokeDiverges(viewModel, snapshot));
    }

    [Fact]
    public void ComposerSelectionDivergesFromSnapshot_DetectsAgentChange()
    {
        var agent = new LumiAgent { Id = Guid.NewGuid(), Name = "Agent" };
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Messages = [new ChatMessage { Role = "user", Content = "x" }]
        };
        var appData = new AppData { Chats = [chat], Agents = [agent] };
        var viewModel = new ChatViewModel(new DataStore(appData), new CopilotService()) { CurrentChat = chat };

        var snapshot = CaptureSnapshot(viewModel);
        Assert.False(InvokeDiverges(viewModel, snapshot));

        viewModel.SetActiveAgent(agent);
        Assert.True(InvokeDiverges(viewModel, snapshot));
    }

    [Fact]
    public async Task CancelComposerEdit_SdkAgentToDraftLumiAgent_RestoresSdkAgent()
    {
        var draftAgent = new LumiAgent { Id = Guid.NewGuid(), Name = "Draft Lumi Agent" };
        var message = new ChatMessage { Role = "user", Content = "Original message" };
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            SdkAgentName = "sdk-reviewer",
            Messages = [message]
        };
        var appData = new AppData { Chats = [chat], Agents = [draftAgent] };
        var viewModel = new ChatViewModel(new DataStore(appData), new CopilotService())
        {
            CurrentChat = chat,
            SelectedSdkAgentName = chat.SdkAgentName
        };

        InvokeBeginComposerEdit(viewModel, message);
        viewModel.SelectedAgentName = draftAgent.Name;
        Assert.Same(draftAgent, viewModel.ActiveAgent);
        Assert.Null(viewModel.SelectedSdkAgentName);

        var command = Assert.IsAssignableFrom<IAsyncRelayCommand>(viewModel.CancelComposerEditCommand);
        await command.ExecuteAsync(null);

        Assert.Null(viewModel.ActiveAgent);
        Assert.Equal("sdk-reviewer", viewModel.SelectedSdkAgentName);
        Assert.Equal("sdk-reviewer", chat.SdkAgentName);
        Assert.Null(chat.AgentId);
    }

    private static object CaptureSnapshot(ChatViewModel viewModel)
    {
        var method = typeof(ChatViewModel).GetMethod(
            "CaptureComposerEditSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(viewModel, null)!;
    }

    private static bool InvokeDiverges(ChatViewModel viewModel, object snapshot)
    {
        var method = typeof(ChatViewModel).GetMethod(
            "ComposerSelectionDivergesFromSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (bool)method!.Invoke(viewModel, [snapshot])!;
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
