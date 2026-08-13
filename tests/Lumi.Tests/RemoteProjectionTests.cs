using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
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

    private static RemoteTranscript BuildCompact(
        Chat chat,
        IReadOnlyList<ChatMessage> messages,
        string? workingDirectory = null)
        => RemoteProjector.BuildTranscript(
            chat,
            messages,
            new RemoteChatStatus { ChatId = chat.Id },
            showReasoning: true,
            showToolCalls: true,
            revision: 7,
            compact: true,
            workingDirectory: workingDirectory);

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
    public void LibrarySnapshotIdentifiesCodingProjectsWithoutLeakingTheirPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lumi-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, ".git"));
        try
        {
            var project = new Project
            {
                Name = "Code",
                WorkingDirectory = directory,
                DefaultNewChatsUseWorktree = true
            };
            var library = RemoteProjector.BuildLibrary(new DataStore(new AppData
            {
                Projects = [project]
            }));

            var remote = Assert.Single(library.Projects);
            Assert.True(remote.IsCodingProject);
            Assert.True(remote.DefaultNewChatsUseWorktree);
            Assert.Null(remote.WorkingDirectory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ChatStatusCarriesExistingWorktreeStateWithoutLeakingItsPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lumi-worktree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var chat = new Chat { WorktreePath = directory };
            var dataStore = new DataStore(new AppData { Chats = [chat] });
            using var viewModel = new ChatViewModel(dataStore, TestCopilot.Shared);

            var status = RemoteProjector.BuildStatus(dataStore, viewModel, chat);
            var json = JsonSerializer.Serialize(
                status,
                RemoteJsonContext.Default.RemoteChatStatus);

            Assert.True(status.UsesWorktree);
            Assert.DoesNotContain(directory, json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
    public void TranscriptCarriesTheAcceptedRemoteRequestIdentity()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Receipt" };
        var message = Message("user", "continue");
        message.RemoteRequestId = "request-123";

        var transcript = Build(chat, [message]);

        Assert.Equal("request-123", Assert.Single(transcript.Turns).Items[0].RequestId);
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
    public void CompactTranscript_CollapsesTechnicalRowsAndSurfacesFileChanges()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Compact" };
        var search = Message(
            "tool",
            """{"query":"Avalonia mobile patterns"}""",
            toolName: "web_search",
            toolStatus: "Completed");
        search.ToolDurationMs = 1_200;
        search.ToolOutput = new string('s', 4_000);
        var edit = Message(
            "tool",
            """{"filePath":"C:\\repo\\src\\Auth.cs","oldString":"old","newString":"new\nline"}""",
            toolName: "edit",
            toolStatus: "Completed");
        edit.ToolDurationMs = 2_400;
        edit.ToolOutput = new string('e', 4_000);
        var workspaceChange = Message(
            "tool",
            """{"filePath":"C:\\repo\\src\\NewFile.cs","operation":"Create"}""",
            toolName: ToolDisplayHelper.WorkspaceFileChangedToolName,
            toolStatus: "Completed");
        var messages = new List<ChatMessage>
        {
            Message("user", "Improve auth"),
            Message("reasoning", "private chain of thought"),
            search,
            edit,
            workspaceChange,
            Message("assistant", "Authentication is updated.")
        };

        var transcript = BuildCompact(chat, messages, @"C:\repo");
        var items = Assert.Single(transcript.Turns).Items;

        Assert.Collection(
            items,
            item => Assert.Equal(RemoteProtocol.ItemKinds.User, item.Kind),
            item =>
            {
                Assert.Equal(RemoteProtocol.ItemKinds.Activity, item.Kind);
                Assert.Equal(2, item.ActionCount);
                Assert.Equal("Completed", item.Status);
                Assert.Equal(3_600, item.DurationMs);
                Assert.Equal(2, item.FileChanges!.Count);
                Assert.Contains(item.FileChanges, change =>
                    change.Path == "src/Auth.cs"
                    && change.Operation == "Modified"
                    && change.LinesAdded == 2
                    && change.LinesRemoved == 1);
                Assert.Contains(item.FileChanges, change =>
                    change.Path == "src/NewFile.cs" && change.Operation == "Created");
                Assert.Null(item.Tools);
                Assert.False(string.IsNullOrWhiteSpace(item.ActivityId));
            },
            item => Assert.Equal(RemoteProtocol.ItemKinds.Assistant, item.Kind));

        var wire = JsonSerializer.Serialize(transcript, RemoteJsonContext.Default.RemoteTranscript);
        var detailedWire = JsonSerializer.Serialize(
            Build(chat, messages),
            RemoteJsonContext.Default.RemoteTranscript);
        Assert.DoesNotContain("private chain of thought", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("Avalonia mobile patterns", wire, StringComparison.Ordinal);
        Assert.True(
            wire.Length * 3 < detailedWire.Length,
            $"expected compact wire to be at least 3x smaller; compact={wire.Length}, detailed={detailedWire.Length}");
    }

    [Fact]
    public void CompactActivityDetails_LoadRawToolsOnlyOnDemand()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Details" };
        var search = Message(
            "tool",
            """{"query":"current docs"}""",
            toolName: "web_search",
            toolStatus: "Completed");
        search.ToolOutput = "three sources";
        var verify = Message(
            "tool",
            """{"command":"dotnet test"}""",
            toolName: "powershell",
            toolStatus: "Completed");
        verify.ToolOutput = "12 tests passed";
        var messages = new List<ChatMessage>
        {
            Message("user", "Check it"),
            search,
            verify,
            Message("assistant", "Done")
        };
        var transcript = BuildCompact(chat, messages);
        var activity = Assert.Single(
            Assert.Single(transcript.Turns).Items,
            item => item.Kind == RemoteProtocol.ItemKinds.Activity);
        var transcriptWire = JsonSerializer.Serialize(
            transcript,
            RemoteJsonContext.Default.RemoteTranscript);

        Assert.DoesNotContain("current docs", transcriptWire, StringComparison.Ordinal);
        Assert.DoesNotContain("three sources", transcriptWire, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet test", transcriptWire, StringComparison.Ordinal);
        Assert.DoesNotContain("12 tests passed", transcriptWire, StringComparison.Ordinal);

        var details = Assert.IsType<RemoteActivityDetails>(
            RemoteProjector.BuildActivityDetails(
                chat,
                messages,
                activity.ActivityId!));

        Assert.Equal(chat.Id, details.ChatId);
        Assert.Equal(activity.ActivityId, details.ActivityId);
        Assert.Collection(
            details.Tools,
            tool =>
            {
                Assert.Equal("research", tool.Category);
                Assert.Contains("current docs", tool.Input, StringComparison.Ordinal);
                Assert.Equal("three sources", tool.Output);
            },
            tool =>
            {
                Assert.Equal("verify", tool.Category);
                Assert.Contains("dotnet test", tool.Input, StringComparison.Ordinal);
                Assert.Equal("12 tests passed", tool.Output);
            });
    }

    [Fact]
    public void CompactTranscript_DoesNotDependOnDesktopToolVisibility()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Compact" };
        var transcript = RemoteProjector.BuildTranscript(
            chat,
            [
                Message("user", "Work"),
                Message("tool", "{}", toolName: "task", toolStatus: "Completed"),
                Message("assistant", "Done")
            ],
            new RemoteChatStatus { ChatId = chat.Id },
            showReasoning: false,
            showToolCalls: false,
            revision: 1,
            compact: true);

        Assert.Contains(
            Assert.Single(transcript.Turns).Items,
            item => item.Kind == RemoteProtocol.ItemKinds.Activity);
    }

    [Fact]
    public void CompactTranscript_UsesStableFullTurnActivityAcrossPagedWindows()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Long turn" };
        var reasoning = Message("reasoning", "thinking");
        var messages = new List<ChatMessage>
        {
            Message("user", "Do a lot"),
            reasoning
        };
        messages.AddRange(Enumerable.Range(0, 60).Select(index =>
            Message(
                "tool",
                $$"""{"query":"item {{index}}"}""",
                toolName: "web_search",
                toolStatus: "Completed")));
        messages.Add(Message("assistant", "Finished"));

        var latestWindow = RemoteProjector.SelectTranscriptWindow(
            messages,
            beforeMessageIndex: null,
            maxMessages: 40);
        var latest = RemoteProjector.BuildTranscript(
            chat,
            latestWindow,
            new RemoteChatStatus { ChatId = chat.Id },
            showReasoning: true,
            showToolCalls: true,
            revision: 1,
            compact: true,
            activitySourceMessages: messages);
        var latestActivity = Assert.Single(
            Assert.Single(latest.Turns).Items,
            item => item.Kind == RemoteProtocol.ItemKinds.Activity);

        var earlierWindow = RemoteProjector.SelectTranscriptWindow(
            messages,
            beforeMessageIndex: latestWindow.StartMessageIndex,
            maxMessages: 40);
        var earlier = RemoteProjector.BuildTranscript(
            chat,
            earlierWindow,
            new RemoteChatStatus { ChatId = chat.Id },
            showReasoning: true,
            showToolCalls: true,
            revision: 1,
            compact: true,
            activitySourceMessages: messages);
        var earlierActivity = Assert.Single(
            Assert.Single(earlier.Turns).Items,
            item => item.Kind == RemoteProtocol.ItemKinds.Activity);

        Assert.Equal(reasoning.Id.ToString("N"), latestActivity.ActivityId);
        Assert.Equal(latestActivity.ActivityId, earlierActivity.ActivityId);
        Assert.Equal(60, latestActivity.ActionCount);
        Assert.Equal(60, earlierActivity.ActionCount);

        var details = Assert.IsType<RemoteActivityDetails>(
            RemoteProjector.BuildActivityDetails(
                chat,
                messages,
                latestActivity.ActivityId!));
        Assert.Equal(RemoteProtocol.MobileActivityToolCountLimit, details.Tools.Count);
        Assert.Equal("omitted", details.Tools[^1].Name);
    }

    [Fact]
    public void CompactWindowIncludesTheUserPromptAndCollapsesTechnicalRows()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Dense turn" };
        var messages = new List<ChatMessage>
        {
            Message("user", "Keep the prompt visible"),
            Message("reasoning", "private")
        };
        messages.AddRange(Enumerable.Range(0, 300).Select(index =>
            Message(
                "tool",
                $$"""{"command":"step {{index}}"}""",
                toolName: "powershell",
                toolStatus: "Completed")));
        messages.Add(Message("assistant", "Done"));

        var window = RemoteProjector.SelectCompactTranscriptWindow(
            messages,
            beforeMessageIndex: null,
            maxVisibleItems: 40);
        var transcript = RemoteProjector.BuildTranscript(
            chat,
            window,
            new RemoteChatStatus { ChatId = chat.Id },
            showReasoning: true,
            showToolCalls: true,
            revision: 1,
            compact: true,
            activitySourceMessages: messages);

        Assert.Equal(0, window.StartMessageIndex);
        Assert.Equal(3, window.Messages.Count);
        Assert.Collection(
            Assert.Single(transcript.Turns).Items,
            item =>
            {
                Assert.Equal(RemoteProtocol.ItemKinds.User, item.Kind);
                Assert.Equal("Keep the prompt visible", item.Text);
            },
            item =>
            {
                Assert.Equal(RemoteProtocol.ItemKinds.Activity, item.Kind);
                Assert.Equal(300, item.ActionCount);
            },
            item => Assert.Equal(RemoteProtocol.ItemKinds.Assistant, item.Kind));
    }

    [Fact]
    public void CompactWindowCountsOnlyRowsThePhoneDisplays()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Tool-heavy history" };
        var messages = new List<ChatMessage>();
        ChatMessage? secondTurnUser = null;
        for (var turnIndex = 0; turnIndex < 3; turnIndex++)
        {
            var user = Message("user", $"Prompt {turnIndex}");
            if (turnIndex == 1)
                secondTurnUser = user;
            messages.Add(user);
            messages.Add(Message("reasoning", $"hidden reasoning {turnIndex}"));
            for (var toolIndex = 0; toolIndex < 100; toolIndex++)
            {
                var tool = Message(
                    "tool",
                    $$"""{"command":"secret-command-{{turnIndex}}-{{toolIndex}}"}""",
                    toolName: "powershell",
                    toolStatus: "Completed");
                tool.ToolOutput = $"hidden-output-{turnIndex}-{toolIndex}";
                messages.Add(tool);
            }
            messages.Add(Message("assistant", $"Answer {turnIndex}"));
        }

        var latest = RemoteProjector.SelectCompactTranscriptWindow(
            messages,
            beforeMessageIndex: null,
            maxVisibleItems: 6,
            textBudgetCharacters: 128);
        var transcript = RemoteProjector.BuildTranscript(
            chat,
            latest,
            new RemoteChatStatus { ChatId = chat.Id },
            showReasoning: true,
            showToolCalls: true,
            revision: 1,
            compact: true,
            activitySourceMessages: messages);

        Assert.Equal(messages.IndexOf(secondTurnUser!), latest.StartMessageIndex);
        Assert.Equal(messages.Count, latest.EndMessageIndex);
        Assert.Equal(6, latest.Messages.Count);
        Assert.Equal(2, transcript.Turns.Count);
        Assert.All(
            transcript.Turns,
            turn =>
            {
                Assert.Collection(
                    turn.Items,
                    item => Assert.Equal(RemoteProtocol.ItemKinds.User, item.Kind),
                    item =>
                    {
                        Assert.Equal(RemoteProtocol.ItemKinds.Activity, item.Kind);
                        Assert.Equal(100, item.ActionCount);
                        Assert.Null(item.Tools);
                    },
                    item => Assert.Equal(RemoteProtocol.ItemKinds.Assistant, item.Kind));
            });

        var wire = JsonSerializer.Serialize(
            transcript,
            RemoteJsonContext.Default.RemoteTranscript);
        Assert.DoesNotContain("secret-command", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden-output", wire, StringComparison.Ordinal);

        var earlier = RemoteProjector.SelectCompactTranscriptWindow(
            messages,
            beforeMessageIndex: latest.StartMessageIndex,
            maxVisibleItems: 6,
            textBudgetCharacters: 128);
        Assert.Equal(0, earlier.StartMessageIndex);
        Assert.Equal(latest.StartMessageIndex, earlier.EndMessageIndex);
        Assert.Equal(3, earlier.Messages.Count);
    }

    [Fact]
    public void CompactWindowNormalizesAStaleCursorToTheCurrentTurnBoundary()
    {
        var messages = new List<ChatMessage>
        {
            Message("user", "Earlier prompt"),
            Message("assistant", "Earlier answer"),
            Message("user", "Growing prompt"),
            Message(
                "tool",
                """{"command":"step 1"}""",
                toolName: "powershell",
                toolStatus: "Completed")
        };
        var staleCursor = messages.Count;
        messages.Add(Message(
            "tool",
            """{"command":"step 2"}""",
            toolName: "powershell",
            toolStatus: "Completed"));
        messages.Add(Message("assistant", "Growing answer"));
        messages.Add(Message("user", "Newer prompt"));
        messages.Add(Message("assistant", "Newer answer"));

        var window = RemoteProjector.SelectCompactTranscriptWindow(
            messages,
            beforeMessageIndex: staleCursor,
            maxVisibleItems: 3);

        Assert.Equal(2, window.StartMessageIndex);
        Assert.Equal(6, window.EndMessageIndex);
        Assert.True(window.HasLaterMessages);
        Assert.Collection(
            window.Messages,
            message => Assert.Equal("Growing prompt", message.Content),
            message => Assert.Equal("tool", message.Role),
            message => Assert.Equal("Growing answer", message.Content));
    }

    [Fact]
    public void CompactWindowTextBudgetIgnoresHiddenSpecialToolJson()
    {
        var announced = Message(
            "tool",
            $$"""{"filePath":"C:\\out\\report.png","hidden":"{{new string('x', 8_000)}}"}""",
            toolName: "announce_file",
            toolStatus: "Completed");
        var question = Message(
            "tool",
            $$"""{"hidden":"{{new string('y', 8_000)}}"}""",
            toolName: "ask_question",
            toolStatus: "Completed",
            questionId: "question-1");
        question.QuestionText = "Choose one";
        question.QuestionOptions = """["A","B"]""";
        question.ToolOutput = "A";
        var messages = new List<ChatMessage>
        {
            Message("user", "Earlier"),
            Message("assistant", "Visible"),
            Message("user", "Latest"),
            announced,
            question,
            Message("assistant", "Done")
        };

        var window = RemoteProjector.SelectCompactTranscriptWindow(
            messages,
            beforeMessageIndex: null,
            maxVisibleItems: 6,
            textBudgetCharacters: 100);

        Assert.Equal(0, window.StartMessageIndex);
        Assert.Equal(messages.Count, window.EndMessageIndex);
        Assert.Equal(messages.Count, window.Messages.Count);
    }

    [Fact]
    public void CompactTranscript_SummarizesReasoningWithoutSendingItsText()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Reasoning" };
        var reasoning = Message("reasoning", "private reasoning text");
        var transcript = BuildCompact(
            chat,
            [
                Message("user", "Explain"),
                reasoning,
                Message("assistant", "Answer")
            ]);

        var activity = Assert.Single(
            Assert.Single(transcript.Turns).Items,
            item => item.Kind == RemoteProtocol.ItemKinds.Activity);
        Assert.Equal(reasoning.Id.ToString("N"), activity.ActivityId);
        Assert.Equal(0, activity.ActionCount);
        Assert.Equal("Thought through the response", activity.Label);
        Assert.DoesNotContain(
            "private reasoning text",
            JsonSerializer.Serialize(transcript, RemoteJsonContext.Default.RemoteTranscript),
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompactFileChanges_ExcludeFailuresAndClassifyInsertAndDelete()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Changes" };
        var transcript = BuildCompact(
            chat,
            [
                Message("user", "Edit"),
                Message(
                    "tool",
                    """{"filePath":"failed.cs","oldString":"a","newString":"b"}""",
                    toolName: "edit",
                    toolStatus: "Failed"),
                Message(
                    "tool",
                    """{"filePath":"existing.cs","insert_text":"line"}""",
                    toolName: "insert",
                    toolStatus: "Completed"),
                Message(
                    "tool",
                    """{"filePath":"authoritative.cs","content":"replacement"}""",
                    toolName: "write_file",
                    toolStatus: "Completed"),
                Message(
                    "tool",
                    """{"filePath":"authoritative.cs","operation":"Modify"}""",
                    toolName: ToolDisplayHelper.WorkspaceFileChangedToolName,
                    toolStatus: "Completed"),
                Message(
                    "tool",
                    """{"filePath":"delete-after-event.cs","operation":"Modify"}""",
                    toolName: ToolDisplayHelper.WorkspaceFileChangedToolName,
                    toolStatus: "Completed"),
                Message(
                    "tool",
                    """{"filePath":"delete-after-event.cs"}""",
                    toolName: "delete_file",
                    toolStatus: "Completed"),
                Message(
                    "tool",
                    """
                    {"patch":"*** Begin Patch\n*** Delete File: removed.cs\n*** End Patch"}
                    """,
                    toolName: "apply_patch",
                    toolStatus: "Completed"),
                Message("assistant", "Done")
            ]);

        var changes = Assert.Single(
            Assert.Single(transcript.Turns).Items,
            item => item.Kind == RemoteProtocol.ItemKinds.Activity).FileChanges!;
        Assert.DoesNotContain(changes, change => change.Path == "failed.cs");
        Assert.Contains(changes, change =>
            change.Path == "existing.cs" && change.Operation == "Modified");
        Assert.Contains(changes, change =>
            change.Path == "authoritative.cs" && change.Operation == "Modified");
        Assert.Contains(changes, change =>
            change.Path == "delete-after-event.cs" && change.Operation == "Deleted");
        Assert.Contains(changes, change =>
            change.Path == "removed.cs" && change.Operation == "Deleted");
    }

    [Fact]
    public void CompactFileChanges_MergeRelativeAndAbsolutePathsAgainstTheExecutionDirectory()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Workspace" };
        var workingDirectory = Path.Combine(Path.GetTempPath(), "lumi-compact-workspace");
        var absolutePath = Path.Combine(workingDirectory, "src", "Auth.cs");
        var transcript = BuildCompact(
            chat,
            [
                Message("user", "Edit"),
                Message(
                    "tool",
                    """{"filePath":"src/Auth.cs","content":"replacement"}""",
                    toolName: "write_file",
                    toolStatus: "Completed"),
                Message(
                    "tool",
                    JsonSerializer.Serialize(new
                    {
                        filePath = absolutePath,
                        operation = "Modify"
                    }),
                    toolName: ToolDisplayHelper.WorkspaceFileChangedToolName,
                    toolStatus: "Completed"),
                Message("assistant", "Done")
            ],
            workingDirectory);

        var change = Assert.Single(Assert.Single(
            Assert.Single(transcript.Turns).Items,
            item => item.Kind == RemoteProtocol.ItemKinds.Activity).FileChanges!);
        Assert.Equal("src/Auth.cs", change.Path);
        Assert.Equal("Modified", change.Operation);
    }

    [Fact]
    public void CompactActivity_UsesAuthoritativeBackgroundShellState()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Background" };
        var shell = Message(
            "tool",
            """{"command":"long-running-task"}""",
            toolName: "powershell",
            toolStatus: "Completed");
        shell.ToolCallId = "shell-1";
        var messages = new List<ChatMessage>
        {
            Message("user", "Run it"),
            shell,
            Message("assistant", "Started")
        };
        var running = new HashSet<string>(["shell-1"], StringComparer.Ordinal);

        var transcript = RemoteProjector.BuildTranscript(
            chat,
            messages,
            new RemoteChatStatus { ChatId = chat.Id },
            showReasoning: true,
            showToolCalls: true,
            revision: 1,
            compact: true,
            activitySourceMessages: messages,
            runningBackgroundToolCallIds: running);
        var activity = Assert.Single(
            Assert.Single(transcript.Turns).Items,
            item => item.Kind == RemoteProtocol.ItemKinds.Activity);
        var details = Assert.IsType<RemoteActivityDetails>(
            RemoteProjector.BuildActivityDetails(
                chat,
                messages,
                activity.ActivityId!,
                runningBackgroundToolCallIds: running));

        Assert.Equal("InProgress", activity.Status);
        Assert.Equal("InProgress", Assert.Single(details.Tools).Status);
    }

    [Fact]
    public void ActivityDetailCapRetainsRunningAndFailedActions()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Capped details" };
        var messages = new List<ChatMessage> { Message("user", "Run many") };
        messages.AddRange(Enumerable.Range(0, 40).Select(index =>
            Message(
                "tool",
                $$"""{"query":"{{index}}"}""",
                toolName: "web_search",
                toolStatus: "Completed")));
        var failed = Message(
            "tool",
            """{"command":"failing-command"}""",
            toolName: "powershell",
            toolStatus: "Failed");
        failed.ToolCallId = "failed-tool";
        messages.Add(failed);
        var runningShell = Message(
            "tool",
            """{"command":"background-command"}""",
            toolName: "powershell",
            toolStatus: "Completed");
        runningShell.ToolCallId = "running-shell";
        messages.Add(runningShell);
        messages.Add(Message("assistant", "Done"));
        var running = new HashSet<string>(["running-shell"], StringComparer.Ordinal);
        var transcript = RemoteProjector.BuildTranscript(
            chat,
            messages,
            new RemoteChatStatus { ChatId = chat.Id },
            showReasoning: true,
            showToolCalls: true,
            revision: 1,
            compact: true,
            activitySourceMessages: messages,
            runningBackgroundToolCallIds: running);
        var activity = Assert.Single(
            Assert.Single(transcript.Turns).Items,
            item => item.Kind == RemoteProtocol.ItemKinds.Activity);

        var details = Assert.IsType<RemoteActivityDetails>(
            RemoteProjector.BuildActivityDetails(
                chat,
                messages,
                activity.ActivityId!,
                runningBackgroundToolCallIds: running));

        Assert.Equal(RemoteProtocol.MobileActivityToolCountLimit, details.Tools.Count);
        Assert.Contains(details.Tools, tool =>
            tool.Id == "failed-tool" && tool.Status == "Failed");
        Assert.Contains(details.Tools, tool =>
            tool.Id == "running-shell" && tool.Status == "InProgress");
        Assert.Contains(details.Tools, tool =>
            tool.Name == "omitted" && tool.Status == "Completed");
    }

    [Fact]
    public void ActivityDetails_StayWithinTheAdvertisedUtf8Limit()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Bounded" };
        var messages = new List<ChatMessage> { Message("user", "Work") };
        messages.AddRange(Enumerable.Range(0, RemoteProtocol.MobileActivityToolCountLimit).Select(index =>
        {
            var tool = Message(
                "tool",
                JsonSerializer.Serialize(new { command = new string('א', 8_000), index }),
                toolName: "powershell",
                toolStatus: "Completed");
            tool.ToolOutput = new string('界', 24_000);
            return tool;
        }));
        var totalFileChanges = RemoteProtocol.MobileFileChangeCountLimit + 5;
        messages.AddRange(Enumerable.Range(0, totalFileChanges).Select(index =>
            Message(
                "tool",
                JsonSerializer.Serialize(new
                {
                    filePath = $"{index}-{new string('界', RemoteProtocol.MobilePathLimit)}.cs",
                    operation = "Modify"
                }),
                toolName: ToolDisplayHelper.WorkspaceFileChangedToolName,
                toolStatus: "Completed")));
        messages.Add(Message("assistant", "Done"));
        var transcript = BuildCompact(chat, messages);
        var activity = Assert.Single(
            Assert.Single(transcript.Turns).Items,
            item => item.Kind == RemoteProtocol.ItemKinds.Activity);

        var details = Assert.IsType<RemoteActivityDetails>(
            RemoteProjector.BuildActivityDetails(chat, messages, activity.ActivityId!));
        var wire = JsonSerializer.Serialize(
            details,
            RemoteJsonContext.Default.RemoteActivityDetails);

        Assert.InRange(
            Encoding.UTF8.GetByteCount(wire),
            1,
            RemoteProtocol.MaxActivityJsonBytes);
        Assert.Equal(totalFileChanges, activity.FileChangeCount);
        Assert.Equal(totalFileChanges, details.TotalFileChangeCount);
        Assert.Equal(RemoteProtocol.MobileFileChangeCountLimit, details.FileChanges.Count);
        Assert.Equal("Omitted", details.FileChanges[^1].Operation);
    }

    [Fact]
    public void ActivityDetails_PreserveBoundedRawEditInput()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Raw details" };
        const string raw =
            """{"filePath":"src/Auth.cs","oldString":"before","newString":"after"}""";
        var messages = new List<ChatMessage>
        {
            Message("user", "Edit"),
            Message("tool", raw, toolName: "edit", toolStatus: "Completed"),
            Message("assistant", "Done")
        };
        var transcript = BuildCompact(chat, messages);
        var activity = Assert.Single(
            Assert.Single(transcript.Turns).Items,
            item => item.Kind == RemoteProtocol.ItemKinds.Activity);

        var details = Assert.IsType<RemoteActivityDetails>(
            RemoteProjector.BuildActivityDetails(chat, messages, activity.ActivityId!));

        Assert.Equal(raw, Assert.Single(details.Tools).Input);
    }

    [Fact]
    public void ActivityDetails_NeverTransmitEmbeddedSubagentReasoning()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Subagent privacy" };
        const string secret = "PRIVATE_SUBAGENT_REASONING";
        var messages = new List<ChatMessage>
        {
            Message("user", "Delegate"),
            Message(
                "tool",
                $$"""
                {"description":"Review the feature","agentDisplayName":"Reviewer","transcript":"Checked the code","reasoning":"{{secret}}"}
                """,
                toolName: "agent:reviewer",
                toolStatus: "Completed"),
            Message("assistant", "Done")
        };
        var transcript = BuildCompact(chat, messages);
        var activity = Assert.Single(
            Assert.Single(transcript.Turns).Items,
            item => item.Kind == RemoteProtocol.ItemKinds.Activity);

        var details = Assert.IsType<RemoteActivityDetails>(
            RemoteProjector.BuildActivityDetails(chat, messages, activity.ActivityId!));
        var wire = JsonSerializer.Serialize(
            details,
            RemoteJsonContext.Default.RemoteActivityDetails);

        Assert.DoesNotContain(secret, wire, StringComparison.Ordinal);
        Assert.DoesNotContain("\"reasoning\"", wire, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Review the feature", Assert.Single(details.Tools).Input, StringComparison.Ordinal);
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
