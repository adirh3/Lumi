using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.Services.Remote;
using Lumi.ViewModels;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// The desktop half of the phone protocol. These cover the projection the phone actually renders and
/// the network gate that decides who is allowed to talk to it at all.
/// </summary>
public sealed class RemoteProjectionTests
{
    private static ChatMessage Message(string role, string content, string? toolName = null,
        string? toolStatus = null, string? questionId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Role = role,
            Content = content,
            ToolName = toolName,
            ToolStatus = toolStatus,
            QuestionId = questionId,
            Timestamp = DateTime.UtcNow
        };

    private static RemoteTranscript Build(Chat chat, IReadOnlyList<ChatMessage> messages,
        bool showReasoning = true, bool showToolCalls = true)
        => RemoteProjector.BuildTranscript(chat, messages, new RemoteChatStatus { ChatId = chat.Id },
            showReasoning, showToolCalls, revision: 7);

    [Fact]
    public void LibrarySnapshotCarriesMetadataInsteadOfEditableBodies()
    {
        var data = new AppData
        {
            Projects =
            [
                new Project
                {
                    Id = Guid.NewGuid(),
                    Name = "Project",
                    Instructions = new string('p', 8_000),
                    WorkingDirectory = @"C:\private\workspace"
                }
            ],
            Skills =
            [
                .. Enumerable.Range(0, 600).Select(index => new Skill
                {
                    Id = Guid.NewGuid(),
                    Name = $"Skill {index}",
                    Description = new string('d', 2_000),
                    Content = new string('s', 8_000)
                })
            ],
            Agents =
            [
                new LumiAgent
                {
                    Id = Guid.NewGuid(),
                    Name = "Lumi",
                    Description = new string('d', 2_000),
                    SystemPrompt = new string('a', 8_000)
                }
            ],
            Memories =
            [
                new Lumi.Models.Memory { Id = Guid.NewGuid(), Key = "memory", Content = new string('m', 8_000) }
            ],
            McpServers =
            [
                new McpServer
                {
                    Id = Guid.NewGuid(),
                    Name = "MCP",
                    Command = new string('c', 8_000),
                    Url = "https://example.test/private"
                }
            ]
        };

        var library = RemoteProjector.BuildLibrary(new DataStore(data));
        var json = JsonSerializer.SerializeToUtf8Bytes(library, RemoteJsonContext.Default.RemoteLibrary);

        Assert.Null(library.Projects[0].WorkingDirectory);
        Assert.Null(library.Skills[0].Content);
        Assert.Null(library.Lumis[0].SystemPrompt);
        Assert.Null(library.McpServers[0].Command);
        Assert.Null(library.McpServers[0].Url);
        Assert.True(library.Projects[0].Instructions!.Length <= RemoteProtocol.MobileLibraryPreviewLimit);
        Assert.True(library.Memories[0].Content.Length <= RemoteProtocol.MobileLibraryPreviewLimit);
        Assert.True(json.Length < RemoteProtocol.MaxLibraryJsonBytes);
    }

    [Fact]
    public void InactiveChatStatus_UsesPersistedModelInsteadOfNull()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Inactive",
            LastModelUsed = "claude-opus-5",
            LastReasoningEffortUsed = "high",
            PlanContent = "# Plan"
        };
        var dataStore = new DataStore(new AppData { Chats = [chat] });
        using var viewModel = new ChatViewModel(dataStore, TestCopilot.Shared);

        var status = RemoteProjector.BuildStatus(dataStore, viewModel, chat);

        Assert.Equal("claude-opus-5", status.Model);
        Assert.Equal("high", status.Quality);
        Assert.Equal("# Plan", status.PlanContent);
    }

    [Fact]
    public void ActiveChatStatus_CarriesOwnerSpecificComposerCatalogs()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Project chat" };
        var dataStore = new DataStore(new AppData { Chats = [chat] });
        using var viewModel = new ChatViewModel(dataStore, TestCopilot.Shared)
        {
            CurrentChat = chat
        };
        viewModel.AvailableAgentChips.Add(new StrataComposerChip("Workspace agent", "◉"));
        viewModel.AvailableSkillChips.Add(new StrataComposerChip("Workspace skill", "✦"));
        viewModel.AvailableMcpChips.Add(new StrataComposerChip("Workspace MCP", "⚙"));

        var status = RemoteProjector.BuildStatus(dataStore, viewModel, chat);

        Assert.True(status.HasComposerCatalogs);
        Assert.Contains(status.AvailableAgents, chip => chip.Name == "Workspace agent");
        Assert.Contains(status.AvailableSkills, chip => chip.Name == "Workspace skill");
        Assert.Contains(status.AvailableMcps, chip => chip.Name == "Workspace MCP");
    }

    [Fact]
    public void Transcript_StartsANewTurnForEveryUserMessage()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Trip" };
        var transcript = Build(chat,
        [
            Message("user", "Plan my trip"),
            Message("assistant", "Sure"),
            Message("user", "Add a hotel"),
            Message("assistant", "Booked")
        ]);

        Assert.Equal(2, transcript.Turns.Count);
        Assert.Equal(7, transcript.Revision);
        Assert.Equal(chat.Id, transcript.ChatId);
        Assert.All(transcript.Turns, turn => Assert.Equal(2, turn.Items.Count));
        Assert.Equal(RemoteProtocol.ItemKinds.User, transcript.Turns[0].Items[0].Kind);
        Assert.Equal(RemoteProtocol.ItemKinds.Assistant, transcript.Turns[0].Items[1].Kind);
    }

    [Fact]
    public void Transcript_CarriesTheServerRevisionEpoch()
    {
        const string epoch = "server-generation-a";
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Epoch" };

        var transcript = RemoteProjector.BuildTranscript(
            chat,
            [Message("assistant", "new server")],
            new RemoteChatStatus { ChatId = chat.Id },
            showReasoning: true,
            showToolCalls: true,
            revision: 1,
            revisionEpoch: epoch);

        Assert.Equal(epoch, transcript.RevisionEpoch);
        Assert.Equal(1, transcript.Revision);
    }

    [Fact]
    public void Transcript_CarriesSteerDeliveryStateForMobileFeedback()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Steer" };
        var user = Message("user", "change direction");
        user.SteerDelivery = MessageSteerState.Steered;

        var transcript = Build(chat, [user]);

        Assert.Equal("Steered", transcript.Turns[0].Items[0].SteerState);
    }

    [Fact]
    public void Transcript_GroupsConsecutiveToolCallsIntoOneRow()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Tools" };
        var transcript = Build(chat,
        [
            Message("user", "Check my disk"),
            Message("tool", "listing", toolName: "glob", toolStatus: "Completed"),
            Message("tool", "reading", toolName: "view", toolStatus: "Completed"),
            Message("assistant", "All good")
        ]);

        var items = transcript.Turns[0].Items;
        var group = Assert.Single(items, i => i.Kind == RemoteProtocol.ItemKinds.ToolGroup);
        Assert.Equal(2, group.Tools!.Count);
        Assert.Equal(["glob", "view"], group.Tools.Select(t => t.Name));
    }

    [Fact]
    public void Transcript_HonoursTheDesktopsReasoningAndToolPreferences()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Quiet" };
        List<ChatMessage> messages =
        [
            Message("user", "Hi"),
            Message("reasoning", "thinking hard"),
            Message("tool", "ran", toolName: "bash", toolStatus: "Completed"),
            Message("assistant", "Hello")
        ];

        var full = Build(chat, messages);
        var quiet = Build(chat, messages, showReasoning: false, showToolCalls: false);

        Assert.Contains(full.Turns[0].Items, i => i.Kind == RemoteProtocol.ItemKinds.Reasoning);
        Assert.DoesNotContain(quiet.Turns[0].Items, i => i.Kind == RemoteProtocol.ItemKinds.Reasoning);
        Assert.DoesNotContain(quiet.Turns[0].Items, i => i.Kind == RemoteProtocol.ItemKinds.Terminal);
        Assert.Equal(2, quiet.Turns[0].Items.Count);
    }

    [Fact]
    public void Transcript_KeepsQuestionsEvenWhenToolCallsAreHidden()
    {
        // A pending question is the one "tool" the user must be able to answer from their phone.
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Ask" };
        var transcript = Build(chat,
        [
            Message("user", "Pick one"),
            Message("tool", "Which theme?", toolName: "ask_question", questionId: "q1")
        ], showToolCalls: false);

        Assert.Contains(transcript.Turns[0].Items, i => i.Kind == RemoteProtocol.ItemKinds.Question);
    }

    [Fact]
    public void Transcript_RendersShellToolsAsATerminalPanel()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Shell" };
        var transcript = Build(chat,
        [
            Message("user", "List files"),
            Message("tool", "output", toolName: "powershell", toolStatus: "Completed")
        ]);

        Assert.Contains(transcript.Turns[0].Items, i => i.Kind == RemoteProtocol.ItemKinds.Terminal);
    }

    [Fact]
    public void Transcript_LeavesThePlanOutOfTheConversation()
    {
        // The plan used to be appended to the last turn on every rebuild, which pinned a full-size
        // card to the bottom of the conversation and re-rendered it on every streamed token. It is
        // chat-level state and travels on RemoteChatStatus.PlanContent instead.
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Plan", PlanContent = "- step one" };
        var transcript = Build(chat, [Message("user", "Go"), Message("assistant", "Working")]);

        Assert.DoesNotContain(
            transcript.Turns.SelectMany(t => t.Items),
            i => string.Equals(i.Kind, "plan", StringComparison.Ordinal));
    }

    [Fact]
    public void Transcript_KeepsSourcesModelAndLinkedChatOnTheAssistantRow()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Embedded metadata" };
        var linkedChatId = Guid.NewGuid();
        var assistant = Message("assistant", "Result");
        assistant.Model = "gpt-5";
        assistant.LinkedChatId = linkedChatId;
        assistant.LinkedChatTitle = "Research follow-up";
        assistant.Sources.Add(new SearchSource
        {
            Title = "Lumi docs",
            Snippet = "Verified source",
            Url = "https://example.test"
        });

        var transcript = Build(chat, [Message("user", "Research"), assistant]);

        var item = Assert.Single(
            transcript.Turns[0].Items,
            candidate => candidate.Kind == RemoteProtocol.ItemKinds.Assistant);
        var source = Assert.Single(item.Sources!);
        Assert.Equal("Lumi docs", source.Title);
        Assert.Equal("gpt-5", item.Model);
        Assert.Equal(linkedChatId, item.LinkedChatId);
        Assert.Equal("Research follow-up", item.Label);
        Assert.DoesNotContain(
            transcript.Turns[0].Items,
            candidate => string.Equals(candidate.Kind, "sources", StringComparison.Ordinal));
    }

    [Fact]
    public void Transcript_ToleratesAnAssistantReplyWithNoUserMessage()
    {
        // Background jobs and agent hand-offs can open a chat with an assistant message.
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Job result" };
        var transcript = Build(chat, [Message("assistant", "Your build finished")]);

        Assert.Single(transcript.Turns);
        Assert.Equal("Your build finished", transcript.Turns[0].Items[0].Text);
    }

    [Fact]
    public void Transcript_ItemIdsAreStableAcrossRebuilds()
    {
        // The phone reconciles by id; unstable ids would rebuild every row on each refresh.
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Stable" };
        List<ChatMessage> messages = [Message("user", "Hi"), Message("assistant", "Hello")];

        var first = Build(chat, messages);
        var second = Build(chat, messages);

        Assert.Equal(
            first.Turns.SelectMany(t => t.Items).Select(i => i.Id),
            second.Turns.SelectMany(t => t.Items).Select(i => i.Id));
    }

    [Fact]
    public void ChatPreviewUsesTheMostRecentMeaningfulConversationText()
    {
        var preview = RemoteProjector.BuildChatPreview(
        [
            Message("user", "First question"),
            Message("assistant", "  The latest\nuseful\tanswer.  "),
            Message("reasoning", "internal scratchpad"),
            Message("tool", "raw tool output", toolName: "powershell")
        ]);

        Assert.Equal("The latest useful answer.", preview);
    }

    [Fact]
    public void Library_ProjectsEveryResourceTheLibraryTabsShow()
    {
        var projectId = Guid.NewGuid();
        var data = new AppData();
        data.Projects.Add(new Project { Id = projectId, Name = "Lumi", Instructions = "Be great" });
        data.Chats.Add(new Chat { Id = Guid.NewGuid(), Title = "A", ProjectId = projectId });
        data.Chats.Add(new Chat { Id = Guid.NewGuid(), Title = "B", ProjectId = projectId });
        data.Skills.Add(new Skill { Id = Guid.NewGuid(), Name = "Doc", Content = "body", IsBuiltIn = true });
        data.Agents.Add(new LumiAgent { Id = Guid.NewGuid(), Name = "Daily", SkillIds = [Guid.NewGuid()] });
        data.Memories.Add(new Memory { Id = Guid.NewGuid(), Key = "Name", Content = "Adir", Category = "Personal" });
        data.McpServers.Add(new McpServer { Id = Guid.NewGuid(), Name = "github", IsEnabled = true });

        var library = RemoteProjector.BuildLibrary(new DataStore(data));

        Assert.Equal(2, library.Projects[0].ChatCount);
        Assert.True(library.Skills[0].IsBuiltIn);
        Assert.Equal(1, library.Lumis[0].SkillCount);
        Assert.Equal("Personal", library.Memories[0].Category);
        Assert.True(library.McpServers[0].IsEnabled);
    }

    [Fact]
    public void Settings_CarryTheDesktopPreferencesAndModelList()
    {
        var data = new AppData();
        data.Settings.UserName = "Adir";
        data.Settings.ShowReasoning = false;
        data.Settings.PreferredModel = "claude-opus-5";

        var settings = RemoteProjector.BuildSettings(new DataStore(data), ["claude-opus-5", "gpt-5.6-sol"]);

        Assert.Equal("Adir", settings.UserName);
        Assert.False(settings.ShowReasoning);
        Assert.Equal("claude-opus-5", settings.PreferredModel);
        Assert.Equal(2, settings.AvailableModels.Count);
        Assert.Contains("claude-opus-5=Claude Opus 5", settings.ModelDisplayNames);
        Assert.Contains("gpt-5.6-sol=GPT 5.6 Sol", settings.ModelDisplayNames);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.42", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.4.9", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    // Tailscale: CGNAT 100.64.0.0/10 is only reachable through the WireGuard tunnel, from a device
    // already authenticated into the user's tailnet, so it is as private as the RFC1918 ranges.
    [InlineData("100.64.0.1", true)]
    [InlineData("100.96.82.26", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("fd7a:115c:a1e0::1", true)]
    // ...but the rest of 100.0.0.0/8 is ordinary routable space and stays refused.
    [InlineData("100.63.255.255", false)]
    [InlineData("100.128.0.1", false)]
    // Anything routable is refused outright: this server is private-network-only by construction.
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("203.0.113.7", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void OnlyPrivateCallersReachTheServer(string address, bool allowed)
    {
        var endPoint = new IPEndPoint(IPAddress.Parse(address), 51234);

        Assert.Equal(allowed, LumiRemoteServer.IsPrivateCaller(endPoint));
    }

    [Fact]
    public void AnUnknownCallerIsRefused()
    {
        Assert.False(LumiRemoteServer.IsPrivateCaller(null));
    }

    [Fact]
    public void IPv4MappedLoopbackIsTreatedAsLoopback()
    {
        // Dual-mode sockets report IPv4 peers as ::ffff:127.0.0.1.
        var endPoint = new IPEndPoint(IPAddress.Parse("::ffff:127.0.0.1"), 51234);

        Assert.True(LumiRemoteServer.IsPrivateCaller(endPoint));
    }
}
