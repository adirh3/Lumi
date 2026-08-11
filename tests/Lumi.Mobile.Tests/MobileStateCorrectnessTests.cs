using System.ComponentModel;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class MobileStateCorrectnessTests
{
    [Fact]
    public void HostResetClearsDraftsAttachmentsAndPendingConfiguration()
    {
        var chat = new MobileChatViewModel(new ImmediateSink());
        var chatId = Guid.NewGuid();
        chat.Reset(chatId, "PC A chat");
        chat.PromptText = "secret draft from PC A";
        chat.Attachments.Add(new PendingAttachment("secret.txt", @"C:\pc-a\secret.txt"));
        chat.Model = "gpt-5.6-sol";
        chat.Reset(Guid.NewGuid(), "Another chat");
        chat.Reset(chatId, "PC A chat");
        Assert.Equal("secret draft from PC A", chat.PromptText);

        chat.ResetHostState();
        chat.Reset(chatId, "Same id on PC B");

        Assert.Equal("", chat.PromptText);
        Assert.Empty(chat.Attachments);
        Assert.False(chat.HasPendingConfiguration);
    }

    [Fact]
    public async Task HostResetDiscardsLateFailedSendFromPreviousPc()
    {
        var sink = new ControllableSink();
        var chat = new MobileChatViewModel(sink);
        var collidingChatId = Guid.NewGuid();
        chat.Reset(collidingChatId, "PC A chat");
        chat.PromptText = "PC A secret draft";

        var send = chat.SendCommand.ExecuteAsync(null);
        await sink.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        chat.ResetHostState();
        chat.Reset(collidingChatId, "PC B chat");
        chat.PromptText = "PC B draft";

        sink.CommandResult.SetResult(new RemoteCommandResult
        {
            Ok = false,
            Error = "late PC A failure"
        });
        await send;

        Assert.Equal("PC B draft", chat.PromptText);
        Assert.Null(chat.ErrorText);
        Assert.Empty(chat.Attachments);
        Assert.False(chat.IsBusy);
    }

    [Fact]
    public async Task HostResetDiscardsLateUploadFromPreviousPc()
    {
        var sink = new ControllableSink();
        var chat = new MobileChatViewModel(sink);
        var collidingChatId = Guid.NewGuid();
        chat.Reset(collidingChatId, "PC A chat");

        var upload = chat.AttachFileAsync("pc-a-secret.txt", new byte[] { 1, 2, 3 });
        await sink.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        chat.ResetHostState();
        chat.Reset(collidingChatId, "PC B chat");
        chat.PromptText = "PC B draft";

        sink.UploadResult.SetResult(new RemoteUploadResponse
        {
            Ok = true,
            FileName = "pc-a-secret.txt",
            Path = @"C:\pc-a\pc-a-secret.txt"
        });
        await upload;

        Assert.Equal("PC B draft", chat.PromptText);
        Assert.Null(chat.ErrorText);
        Assert.Empty(chat.Attachments);
        Assert.False(chat.IsUploading);
    }

    [Fact]
    public void LibraryHostResetClosesEditorAndRowActions()
    {
        var library = new LibraryViewModel(new ImmediateSink());
        library.BeginCreateCommand.Execute(null);
        library.EditName = "PC A project";
        var entry = new LibraryEntryViewModel
        {
            Section = LibrarySection.Projects,
            Identifier = Guid.NewGuid().ToString(),
            Name = "PC A"
        };
        library.OpenRowActionsCommand.Execute(entry);

        library.ResetHostState();

        Assert.False(library.IsEditing);
        Assert.False(library.IsRowActionsOpen);
        Assert.Null(library.ActionEntry);
        Assert.Equal("", library.EditName);
        Assert.Empty(library.Entries);
    }

    [Fact]
    public async Task DuplicateComposerLabelsSendStableProjectAndLumiValues()
    {
        var sink = new RecordingSink();
        var chat = new MobileChatViewModel(sink);
        var projectId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        chat.Reset(chatId, "Chat");
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 1,
            TotalRawMessageCount = 1,
            Status = new RemoteChatStatus { ChatId = chatId }
        });
        chat.ApplyLibraryCatalogs(new RemoteLibrary
        {
            Projects =
            [
                new RemoteProject { Id = Guid.NewGuid(), Name = "Duplicate" },
                new RemoteProject { Id = projectId, Name = "Duplicate" }
            ],
            Lumis =
            [
                new RemoteLumi { Id = Guid.NewGuid(), Name = "Duplicate Lumi" },
                new RemoteLumi { Id = agentId, Name = "Duplicate Lumi" }
            ]
        });

        chat.ProjectValue = projectId.ToString();
        chat.ProjectName = "Duplicate";
        await WaitForCommandAsync(sink, "projectId");
        Assert.Equal(projectId.ToString(), sink.LastCommand!.Get("projectId"));
        Assert.Null(sink.LastCommand.Get("project"));

        chat.AgentValue = agentId.ToString();
        chat.AgentName = "Duplicate Lumi";
        await WaitForCommandAsync(sink, "agentId");
        Assert.Equal(agentId.ToString(), sink.LastCommand!.Get("agentId"));
        Assert.Null(sink.LastCommand.Get("agent"));
    }

    [Fact]
    public async Task SwitchingChatsWhileSendIsInFlight_DoesNotMutateTheNewChat()
    {
        var sink = new ControllableSink();
        var chat = new MobileChatViewModel(sink);
        var originChatId = Guid.NewGuid();
        var nextChatId = Guid.NewGuid();

        chat.Reset(originChatId, "Origin");
        chat.PromptText = "send from origin";
        var send = chat.SendCommand.ExecuteAsync(null);
        await sink.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(chat.IsBusy);
        Assert.NotEmpty(chat.Turns);

        chat.Reset(nextChatId, "Next");
        chat.PromptText = "draft for next";

        sink.CommandResult.SetResult(new RemoteCommandResult
        {
            Ok = false,
            Error = "origin failed"
        });
        await send;

        Assert.Equal(nextChatId, chat.ChatId);
        Assert.Equal("draft for next", chat.PromptText);
        Assert.Null(chat.ErrorText);
        Assert.Empty(chat.Turns);
        Assert.False(chat.IsBusy);

        chat.Reset(originChatId, "Origin");
        Assert.Equal("send from origin", chat.PromptText);
        Assert.Equal("origin failed", chat.ErrorText);
    }

    [Fact]
    public async Task NavigatingAwayAndBackToTheSameChat_MergesOldFailureWithoutOverwritingNewDraft()
    {
        var sink = new ControllableSink();
        var chat = new MobileChatViewModel(sink);
        var originChatId = Guid.NewGuid();

        chat.Reset(originChatId, "Origin");
        chat.PromptText = "old send";
        var send = chat.SendCommand.ExecuteAsync(null);
        await sink.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        chat.Reset(Guid.NewGuid(), "Other");
        chat.Reset(originChatId, "Origin");
        chat.PromptText = "new draft after returning";

        sink.CommandResult.SetResult(new RemoteCommandResult
        {
            Ok = false,
            Error = "old send failed"
        });
        await send;

        Assert.Equal(originChatId, chat.ChatId);
        Assert.Equal("new draft after returning", chat.PromptText);
        Assert.Equal("old send failed", chat.ErrorText);
        Assert.Empty(chat.Turns);
    }

    [Fact]
    public async Task StartingAnotherBlankChatWhileSendIsInFlight_IgnoresTheOldCreationResult()
    {
        var sink = new ControllableSink();
        var chat = new MobileChatViewModel(sink);
        var adopted = false;
        chat.ChatCreated += (chatId, generation) =>
            adopted = chat.TryAdoptCreatedChat(chatId, generation);

        chat.Reset(Guid.Empty, "First new chat");
        chat.PromptText = "create the first chat";
        var send = chat.SendCommand.ExecuteAsync(null);
        var command = await sink.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(command.GetBool("newChat"));

        chat.Reset(Guid.Empty, "Second new chat");
        chat.PromptText = "draft for the second chat";

        sink.CommandResult.SetResult(new RemoteCommandResult
        {
            Ok = true,
            ChatId = Guid.NewGuid()
        });
        await send;

        Assert.Equal(Guid.Empty, chat.ChatId);
        Assert.Equal("draft for the second chat", chat.PromptText);
        Assert.Empty(chat.Turns);
        Assert.False(adopted);
    }

    [Fact]
    public async Task DraftsAndAttachments_AreIsolatedPerChatAndFreshBlankSurfacesStartClear()
    {
        var sink = new ImmediateSink();
        var chat = new MobileChatViewModel(sink);
        var firstChatId = Guid.NewGuid();
        var secondChatId = Guid.NewGuid();

        chat.Reset(firstChatId, "First");
        chat.PromptText = "first draft";
        await chat.AttachFileAsync("first.txt", new byte[] { 1 });

        chat.Reset(secondChatId, "Second");
        Assert.Equal("", chat.PromptText);
        Assert.Empty(chat.Attachments);

        chat.PromptText = "second draft";
        await chat.AttachFileAsync("second.txt", new byte[] { 2 });

        chat.Reset(firstChatId, "First");
        Assert.Equal("first draft", chat.PromptText);
        Assert.Equal("first.txt", Assert.Single(chat.Attachments).FileName);

        chat.Reset(secondChatId, "Second");
        Assert.Equal("second draft", chat.PromptText);
        Assert.Equal("second.txt", Assert.Single(chat.Attachments).FileName);

        chat.Reset(Guid.Empty, "New chat");
        chat.PromptText = "abandoned blank draft";
        await chat.AttachFileAsync("blank.txt", new byte[] { 3 });

        chat.Reset(Guid.Empty, "Another new chat");
        Assert.Equal("", chat.PromptText);
        Assert.Empty(chat.Attachments);
    }

    [Fact]
    public async Task UploadCompletingAfterAChatSwitch_DoesNotAttachToTheNewSurface()
    {
        var sink = new ControllableSink();
        var chat = new MobileChatViewModel(sink);
        var originChatId = Guid.NewGuid();

        chat.Reset(originChatId, "Origin");
        var upload = chat.AttachFileAsync("origin.txt", new byte[] { 1, 2, 3 });
        await sink.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var nextChatId = Guid.NewGuid();
        chat.Reset(nextChatId, "Next");
        chat.PromptText = "next draft";

        sink.UploadResult.SetResult(new RemoteUploadResponse
        {
            Ok = true,
            FileName = "origin.txt",
            Path = @"C:\uploads\origin.txt"
        });
        await upload;

        Assert.Equal(nextChatId, chat.ChatId);
        Assert.Equal("next draft", chat.PromptText);
        Assert.Empty(chat.Attachments);
        Assert.Null(chat.ErrorText);
        Assert.False(chat.IsUploading);

        chat.Reset(originChatId, "Origin");
        Assert.Equal("origin.txt", Assert.Single(chat.Attachments).FileName);
    }

    [Fact]
    public async Task BlankChatAdoption_KeepsAnInFlightUploadOnTheSameSurface()
    {
        var sink = new ControllableSink();
        var chat = new MobileChatViewModel(sink);
        var createdChatId = Guid.NewGuid();
        chat.ChatCreated += (chatId, generation) =>
            Assert.True(chat.TryAdoptCreatedChat(chatId, generation));

        chat.Reset(Guid.Empty, "New chat");
        var upload = chat.AttachFileAsync("after-create.txt", new byte[] { 1, 2, 3 });
        await sink.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        chat.PromptText = "create this chat";
        var send = chat.SendCommand.ExecuteAsync(null);
        await sink.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        sink.CommandResult.SetResult(new RemoteCommandResult
        {
            Ok = true,
            ChatId = createdChatId
        });
        await send;

        Assert.Equal(createdChatId, chat.ChatId);
        Assert.True(chat.IsUploading);

        sink.UploadResult.SetResult(new RemoteUploadResponse
        {
            Ok = true,
            FileName = "after-create.txt",
            Path = @"C:\uploads\after-create.txt"
        });
        await upload;

        Assert.False(chat.IsUploading);
        Assert.Equal("after-create.txt", Assert.Single(chat.Attachments).FileName);
    }

    [Fact]
    public async Task FailedCreatingSendWithChatId_AdoptsCreatedChatAndRestoresItsDraft()
    {
        var sink = new ControllableSink();
        var chat = new MobileChatViewModel(sink);
        var createdChatId = Guid.NewGuid();
        chat.ChatCreated += (chatId, generation) =>
            Assert.True(chat.TryAdoptCreatedChat(chatId, generation));

        chat.Reset(Guid.Empty, "New chat");
        chat.Model = "gpt-5.6";
        chat.PromptText = "restore after creation";
        var send = chat.SendCommand.ExecuteAsync(null);
        await sink.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        sink.CommandResult.SetResult(new RemoteCommandResult
        {
            Ok = false,
            ChatId = createdChatId,
            Error = "turn failed after chat creation"
        });
        await send;

        Assert.Equal(createdChatId, chat.ChatId);
        Assert.Equal("restore after creation", chat.PromptText);
        Assert.Equal("turn failed after chat creation", chat.ErrorText);
        Assert.True(chat.HasPendingConfiguration);
    }

    [Fact]
    public async Task LateFailedCreatingSend_MapsItsDraftToTheReturnedChatId()
    {
        var sink = new ControllableSink();
        var chat = new MobileChatViewModel(sink);
        var createdChatId = Guid.NewGuid();
        var adopted = false;
        chat.ChatCreated += (chatId, generation) =>
            adopted = chat.TryAdoptCreatedChat(chatId, generation);

        chat.Reset(Guid.Empty, "First new chat");
        chat.PromptText = "first blank payload";
        var send = chat.SendCommand.ExecuteAsync(null);
        await sink.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        chat.Reset(Guid.Empty, "Second new chat");
        chat.PromptText = "second blank draft";
        sink.CommandResult.SetResult(new RemoteCommandResult
        {
            Ok = false,
            ChatId = createdChatId,
            Error = "created, but the first turn failed"
        });
        await send;

        Assert.False(adopted);
        Assert.Equal(Guid.Empty, chat.ChatId);
        Assert.Equal("second blank draft", chat.PromptText);
        Assert.Null(chat.ErrorText);

        chat.Reset(createdChatId, "Created chat");
        Assert.Equal("first blank payload", chat.PromptText);
        Assert.Equal("created, but the first turn failed", chat.ErrorText);
    }

    [Fact]
    public async Task CreatingSendCarriesAllStagedConfigurationAndAccumulatedLists()
    {
        var sink = new ControllableSink();
        var chat = new MobileChatViewModel(sink);
        chat.Reset(Guid.Empty, "New chat");
        chat.Model = "gpt-5.6";
        chat.Quality = "high";
        chat.ContextWindowTier = "Long context";
        chat.AgentName = "Researcher";
        chat.ProjectName = "Apollo";
        chat.SkillChips.Add(new StrataComposerChip("Documents", "✦"));
        chat.SkillChips.Add(new StrataComposerChip("Web", "✦"));
        chat.SkillChips.Add(new StrataComposerChip("Documents", "✦"));
        chat.McpChips.Add(new StrataComposerChip("Browser", "⚙"));
        chat.McpChips.Add(new StrataComposerChip("Files", "⚙"));
        chat.PromptText = "configured first turn";

        var send = chat.SendCommand.ExecuteAsync(null);
        var command = await sink.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("gpt-5.6", command.Get("model"));
        Assert.Equal("high", command.Get("quality"));
        Assert.Equal("high", command.Get("reasoningEffort"));
        Assert.Equal("Long context", command.Get("contextWindowTier"));
        Assert.Equal("Researcher", command.Get("agent"));
        Assert.Equal("Apollo", command.Get("project"));
        Assert.Equal("Apollo", command.Get("projectName"));
        Assert.Equal(["Documents", "Web"], Assert.IsType<string[]>(command.GetList("addSkills")));
        Assert.Equal(["Browser", "Files"], Assert.IsType<string[]>(command.GetList("addMcps")));

        sink.CommandResult.SetResult(new RemoteCommandResult { Ok = true });
        await send;
        Assert.False(chat.HasPendingConfiguration);
    }

    [Fact]
    public async Task UnchangedTimeoutRetry_ReusesTheRequestId()
    {
        var sink = new SequencedCommandSink(commandCount: 2);
        var chat = new MobileChatViewModel(sink);
        var originChatId = Guid.NewGuid();
        chat.Reset(originChatId, "Existing chat");
        await chat.AttachFileAsync("notes.txt", new byte[] { 1, 2, 3 });
        chat.PromptText = "retry exactly";

        var firstSend = chat.SendCommand.ExecuteAsync(null);
        var first = await sink.WaitForCommandAsync(0);
        chat.Reset(Guid.NewGuid(), "Other chat");
        sink.Complete(
            0,
            new RemoteCommandResult
            {
                Ok = false,
                Error = "The request timed out.",
                IsTimeout = true,
                RequestId = first.RequestId
            });
        await firstSend;

        Assert.False(string.IsNullOrWhiteSpace(first.RequestId));
        Assert.Equal("", chat.PromptText);
        Assert.Empty(chat.Attachments);

        chat.Reset(originChatId, "Existing chat");
        Assert.Equal("retry exactly", chat.PromptText);
        Assert.Equal("notes.txt", Assert.Single(chat.Attachments).FileName);

        // The server may publish busy status while the timed-out operation continues. Reusing its
        // idempotency key must still replay the exact original arguments rather than adding steer.
        chat.IsBusy = true;
        var retry = chat.SendCommand.ExecuteAsync(null);
        var second = await sink.WaitForCommandAsync(1);
        sink.Complete(1, new RemoteCommandResult { Ok = true });
        await retry;

        Assert.Equal(first.RequestId, second.RequestId);
        Assert.Null(first.GetBool("steer"));
        Assert.Null(second.GetBool("steer"));
    }

    [Fact]
    public async Task UnchangedAmbiguousTransportRetry_ReusesTheRequestId()
    {
        var sink = new SequencedCommandSink(commandCount: 2);
        var chat = new MobileChatViewModel(sink);
        chat.Reset(Guid.NewGuid(), "Existing chat");
        chat.PromptText = "retry exactly";

        var firstSend = chat.SendCommand.ExecuteAsync(null);
        var first = await sink.WaitForCommandAsync(0);
        sink.Complete(
            0,
            new RemoteCommandResult
            {
                Ok = false,
                Error = "connection reset",
                IsOutcomeUnknown = true,
                RequestId = first.RequestId
            });
        await firstSend;

        var retry = chat.SendCommand.ExecuteAsync(null);
        var second = await sink.WaitForCommandAsync(1);
        sink.Complete(1, new RemoteCommandResult { Ok = true });
        await retry;

        Assert.Equal(first.RequestId, second.RequestId);
    }

    [Fact]
    public async Task AuthoritativeTranscriptClearsAnAmbiguousRetryThatWasAccepted()
    {
        var sink = new ScriptedCommandSink(new RemoteCommandResult
        {
            Ok = false,
            Error = "connection reset",
            IsOutcomeUnknown = true
        });
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(sink);
        chat.Reset(chatId, "Existing chat");
        chat.PromptText = "accepted remotely";

        await chat.SendCommand.ExecuteAsync(null);
        Assert.Equal("accepted remotely", chat.PromptText);
        Assert.NotNull(chat.ErrorText);

        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            RevisionEpoch = "server-a",
            Revision = 2,
            TotalRawMessageCount = 1,
            Turns =
            [
                new RemoteTranscriptTurn
                {
                    Id = "turn-1",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = "user-1",
                            Kind = RemoteProtocol.ItemKinds.User,
                            Text = "accepted remotely",
                            RequestId = Assert.Single(sink.Commands).RequestId
                        }
                    ]
                }
            ],
            Status = new RemoteChatStatus { ChatId = chatId }
        });

        Assert.Equal("", chat.PromptText);
        Assert.Null(chat.ErrorText);
        Assert.Single(sink.Commands);
    }

    [Fact]
    public async Task IdenticalHistoricalPromptDoesNotClearAnAmbiguousRetry()
    {
        var sink = new ScriptedCommandSink(new RemoteCommandResult
        {
            Ok = false,
            Error = "connection reset",
            IsOutcomeUnknown = true
        });
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(sink);
        chat.Reset(chatId, "Existing chat");
        chat.PromptText = "continue";
        await chat.SendCommand.ExecuteAsync(null);

        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 2,
            TotalRawMessageCount = 1,
            Turns =
            [
                new RemoteTranscriptTurn
                {
                    Id = "old-turn",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = "old-user",
                            Kind = RemoteProtocol.ItemKinds.User,
                            Text = "continue",
                            RequestId = "different-request"
                        }
                    ]
                }
            ],
            Status = new RemoteChatStatus { ChatId = chatId }
        });

        Assert.Equal("continue", chat.PromptText);
        Assert.NotNull(chat.ErrorText);
    }

    [Fact]
    public async Task ServerEpochChangeBlocksAnUnsafeAmbiguousReplay()
    {
        var sink = new ScriptedCommandSink(new RemoteCommandResult
        {
            Ok = false,
            Error = "connection reset",
            IsOutcomeUnknown = true
        });
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(sink);
        chat.Reset(chatId, "Existing chat");
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            RevisionEpoch = "server-a",
            Revision = 1,
            Status = new RemoteChatStatus { ChatId = chatId }
        });
        chat.PromptText = "unknown outcome";
        await chat.SendCommand.ExecuteAsync(null);

        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            RevisionEpoch = "server-b",
            Revision = 1,
            Status = new RemoteChatStatus { ChatId = chatId }
        });
        await chat.SendCommand.ExecuteAsync(null);

        Assert.Single(sink.Commands);
        Assert.Contains("safely replay", chat.ErrorText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExpiredAmbiguousRetryIsNotReplayedWithAnEvictedRequestId()
    {
        var now = DateTimeOffset.UtcNow;
        var sink = new ScriptedCommandSink(new RemoteCommandResult
        {
            Ok = false,
            Error = "The request timed out.",
            IsTimeout = true
        });
        var chat = new MobileChatViewModel(sink, () => now);
        chat.Reset(Guid.NewGuid(), "Existing chat");
        chat.PromptText = "unknown outcome";
        await chat.SendCommand.ExecuteAsync(null);

        now += TimeSpan.FromMinutes(10);
        await chat.SendCommand.ExecuteAsync(null);

        Assert.Single(sink.Commands);
        Assert.Contains("safely replay", chat.ErrorText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IncompatibleDesktopHidesAtomicWorktreeSelection()
    {
        var chat = new MobileChatViewModel(new RecordingSink());
        var projectId = Guid.NewGuid();
        chat.ApplyProjectCatalog(
        [
            new RemoteProject { Id = projectId, Name = "Code", IsCodingProject = true }
        ]);
        chat.Reset(Guid.NewGuid(), "Empty");
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chat.ChatId,
            Revision = 1,
            Status = new RemoteChatStatus { ChatId = chat.ChatId }
        });
        chat.ProjectValue = projectId.ToString();
        chat.ProjectName = "Code";
        Assert.True(chat.CanChooseWorktree);

        chat.ApplyRemoteProtocolVersion(3);

        Assert.False(chat.CanChooseWorktree);
        Assert.False(chat.UseWorktree);
    }

    [Fact]
    public async Task EditedTimeoutDraft_UsesANewRequestId()
    {
        var sink = new ScriptedCommandSink(
            new RemoteCommandResult
            {
                Ok = false,
                Error = "The request timed out.",
                IsTimeout = true
            },
            new RemoteCommandResult { Ok = true });
        var chat = new MobileChatViewModel(sink);
        chat.Reset(Guid.NewGuid(), "Existing chat");
        chat.PromptText = "original";

        await chat.SendCommand.ExecuteAsync(null);
        var firstRequestId = Assert.Single(sink.Commands).RequestId;

        chat.PromptText = "edited";
        await chat.SendCommand.ExecuteAsync(null);

        Assert.Equal(2, sink.Commands.Count);
        Assert.NotEqual(firstRequestId, sink.Commands[1].RequestId);
    }

    [Fact]
    public async Task RemoveAfterTimeout_SurvivesOriginalReplayAndFlushesAfterAcknowledgement()
    {
        var sink = new SequencedCommandSink(commandCount: 3);
        var chat = new MobileChatViewModel(sink);
        var createdChatId = Guid.NewGuid();
        var skill = new StrataComposerChip("Documents", "✦");
        chat.ChatCreated += (chatId, generation) =>
            Assert.True(chat.TryAdoptCreatedChat(chatId, generation));

        chat.Reset(Guid.Empty, "New chat");
        chat.SkillChips.Add(skill);
        chat.PromptText = "create with a skill";

        var firstSend = chat.SendCommand.ExecuteAsync(null);
        var first = await sink.WaitForCommandAsync(0);
        Assert.Equal(["Documents"], Assert.IsType<string[]>(first.GetList("addSkills")));

        sink.Complete(
            0,
            new RemoteCommandResult
            {
                Ok = false,
                Error = "Lumi took too long to answer.",
                IsTimeout = true,
                RequestId = first.RequestId
            });
        await firstSend;

        await chat.RemoveSkillCommand.ExecuteAsync(skill);
        Assert.Empty(chat.SkillChips);

        var retry = chat.SendCommand.ExecuteAsync(null);
        var replay = await sink.WaitForCommandAsync(1);
        Assert.Equal(first.RequestId, replay.RequestId);
        Assert.Equal(["Documents"], Assert.IsType<string[]>(replay.GetList("addSkills")));
        Assert.Null(replay.GetList("removeSkills"));

        sink.Complete(1, new RemoteCommandResult { Ok = true, ChatId = createdChatId });
        await retry;
        Assert.Equal(createdChatId, chat.ChatId);
        Assert.True(chat.HasPendingConfiguration);

        var flush = chat.FlushPendingConfigurationAsync();
        var removal = await sink.WaitForCommandAsync(2);
        Assert.Null(removal.GetList("addSkills"));
        Assert.Equal(["Documents"], Assert.IsType<string[]>(removal.GetList("removeSkills")));

        sink.Complete(2, new RemoteCommandResult { Ok = true });
        await flush;
        Assert.False(chat.HasPendingConfiguration);
    }

    [Fact]
    public async Task PendingSkillAndMcpOperations_RespectAddRemoveReAddOrder()
    {
        var sink = new ControllableSink();
        var chat = new MobileChatViewModel(sink);
        var readdedSkill = new StrataComposerChip("Readded skill", "✦");
        var removedSkill = new StrataComposerChip("Removed skill", "✦");
        var readdedMcp = new StrataComposerChip("Readded MCP", "⚙");
        var removedMcp = new StrataComposerChip("Removed MCP", "⚙");

        chat.Reset(Guid.Empty, "New chat");
        chat.SkillChips.Add(readdedSkill);
        chat.SkillChips.Add(removedSkill);
        await chat.RemoveSkillCommand.ExecuteAsync(readdedSkill);
        await chat.RemoveSkillCommand.ExecuteAsync(removedSkill);
        chat.SkillChips.Add(new StrataComposerChip(readdedSkill.Name, readdedSkill.Glyph));

        chat.McpChips.Add(readdedMcp);
        chat.McpChips.Add(removedMcp);
        await chat.RemoveMcpCommand.ExecuteAsync(readdedMcp);
        await chat.RemoveMcpCommand.ExecuteAsync(removedMcp);
        chat.McpChips.Add(new StrataComposerChip(readdedMcp.Name, readdedMcp.Glyph));

        Assert.Equal(readdedSkill.Name, Assert.Single(chat.SkillChips).Name);
        Assert.Equal(readdedMcp.Name, Assert.Single(chat.McpChips).Name);

        chat.PromptText = "ordered configuration";
        var send = chat.SendCommand.ExecuteAsync(null);
        var command = await sink.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([readdedSkill.Name], Assert.IsType<string[]>(command.GetList("addSkills")));
        Assert.Equal([removedSkill.Name], Assert.IsType<string[]>(command.GetList("removeSkills")));
        Assert.Equal([readdedMcp.Name], Assert.IsType<string[]>(command.GetList("addMcps")));
        Assert.Equal([removedMcp.Name], Assert.IsType<string[]>(command.GetList("removeMcps")));

        sink.CommandResult.SetResult(new RemoteCommandResult { Ok = true });
        await send;
    }

    [Fact]
    public async Task BlankRemovalCommands_UpdateLocalSelectionsBeforeAServerChatExists()
    {
        var sink = new ControllableSink();
        var chat = new MobileChatViewModel(sink);
        var skill = new StrataComposerChip("Local skill", "✦");
        var mcp = new StrataComposerChip("Local MCP", "⚙");

        chat.Reset(Guid.Empty, "New chat");
        chat.AgentName = "Local agent";
        chat.ProjectName = "Local project";
        chat.SkillChips.Add(skill);
        chat.McpChips.Add(mcp);

        await chat.RemoveAgentCommand.ExecuteAsync(null);
        await chat.RemoveProjectCommand.ExecuteAsync(null);
        await chat.RemoveSkillCommand.ExecuteAsync(skill);
        await chat.RemoveMcpCommand.ExecuteAsync(mcp);

        Assert.Null(chat.AgentName);
        Assert.Null(chat.ProjectName);
        Assert.Empty(chat.SkillChips);
        Assert.Empty(chat.McpChips);
        Assert.False(sink.CommandStarted.Task.IsCompleted);

        chat.PromptText = "send the cleared configuration";
        var send = chat.SendCommand.ExecuteAsync(null);
        var command = await sink.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("", command.Get("agent"));
        Assert.Equal("", command.Get("project"));
        Assert.Equal(["Local skill"], Assert.IsType<string[]>(command.GetList("removeSkills")));
        Assert.Equal(["Local MCP"], Assert.IsType<string[]>(command.GetList("removeMcps")));
        Assert.Null(command.GetList("addSkills"));
        Assert.Null(command.GetList("addMcps"));

        sink.CommandResult.SetResult(new RemoteCommandResult { Ok = true });
        await send;
    }

    [Fact]
    public async Task MappedCreatedChatLateFailure_MergesIntoCurrentReactivation()
    {
        var sink = new SequencedCommandSink(commandCount: 1);
        var chat = new MobileChatViewModel(sink);
        var createdChatId = Guid.NewGuid();
        var newerAttachment = new PendingAttachment("newer.txt", @"C:\uploads\newer.txt");

        chat.Model = "old-model";
        chat.PromptText = "original text";
        await chat.AttachFileAsync("original.txt", new byte[] { 1, 2, 3 });

        var send = chat.SendCommand.ExecuteAsync(null);
        await sink.WaitForCommandAsync(0);

        // A snapshot can expose and open the just-created chat before the command response returns.
        // The blank origin is already mapped, but Reset gives that same logical chat a new activation.
        chat.Model = "new-model";
        Assert.True(chat.TryAdoptCreatedChat(createdChatId, blankGeneration: 0));
        chat.Reset(createdChatId, "Created chat");
        chat.PromptText = "newer text";
        chat.Attachments.Add(newerAttachment);
        chat.IsBusy = true;

        sink.Complete(
            0,
            new RemoteCommandResult
            {
                Ok = false,
                ChatId = createdChatId,
                Error = "the creating turn failed"
            });
        await send;

        Assert.Equal(createdChatId, chat.ChatId);
        Assert.Equal("newer text", chat.PromptText);
        Assert.Equal("new-model", chat.Model);
        Assert.Equal("the creating turn failed", chat.ErrorText);
        Assert.True(chat.IsBusy);
        Assert.Contains(newerAttachment, chat.Attachments);
        Assert.Contains(
            chat.Attachments,
            attachment => attachment.FileName == "original.txt");
    }

    [Fact]
    public async Task UnknownSizeRead_StopsAtTheLimitPlusOneWithoutGrowingPastIt()
    {
        const int limit = 1024;
        await using var source = new CountingReadStream(limit + 500);

        var read = await ChatDetailView.ReadBoundedAsync(source, limit);
        await using var buffer = read.Buffer;

        Assert.True(read.IsTooLarge);
        Assert.Equal(limit + 1, source.BytesRead);
        Assert.Equal(limit + 1, buffer.Length);
        Assert.Equal(0, buffer.Position);
        Assert.InRange(buffer.Capacity, 0, limit + 1);
    }

    [Fact]
    public async Task FailedSend_DoesNotOverwriteNewerTextOnTheSameSurface()
    {
        var sink = new ControllableSink();
        var chat = new MobileChatViewModel(sink);
        chat.Reset(Guid.NewGuid(), "Chat");
        chat.PromptText = "first message";

        var send = chat.SendCommand.ExecuteAsync(null);
        await sink.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        chat.PromptText = "newer draft";

        sink.CommandResult.SetResult(new RemoteCommandResult
        {
            Ok = false,
            Error = "send failed"
        });
        await send;

        Assert.Equal("newer draft", chat.PromptText);
        Assert.Equal("send failed", chat.ErrorText);
    }

    [Fact]
    public async Task SendTransportException_RestoresTheActiveDraftWithoutThrowing()
    {
        var chat = new MobileChatViewModel(new ThrowingCommandSink());
        chat.Reset(Guid.NewGuid(), "Chat");
        chat.PromptText = "retry me";

        var exception = await Record.ExceptionAsync(() => chat.SendCommand.ExecuteAsync(null));

        Assert.Null(exception);
        Assert.Equal("retry me", chat.PromptText);
        Assert.False(chat.IsBusy);
        Assert.NotNull(chat.ErrorText);
        Assert.Empty(chat.Turns);
    }

    [Fact]
    public async Task BlankCodingChatSendsTheSelectedWorktreeIntentAtomically()
    {
        var sink = new RecordingSink();
        var chat = new MobileChatViewModel(sink);
        var projectId = Guid.NewGuid();
        chat.ApplyProjectCatalog(
        [
            new RemoteProject
            {
                Id = projectId,
                Name = "Lumi",
                IsCodingProject = true,
                DefaultNewChatsUseWorktree = false
            }
        ]);
        chat.Reset(Guid.Empty, "New chat");
        chat.ProjectValue = projectId.ToString();
        chat.ProjectName = "Lumi";
        chat.SelectNewWorktreeCommand.Execute(null);
        chat.PromptText = "fix the build";

        await chat.SendCommand.ExecuteAsync(null);

        Assert.Equal(RemoteProtocol.Actions.SendMessage, sink.LastCommand?.Action);
        Assert.Equal("true", sink.LastCommand?.Get("newChat"));
        Assert.Equal(projectId.ToString(), sink.LastCommand?.Get("projectId"));
        Assert.Equal("true", sink.LastCommand?.Get("worktree"));
    }

    [Fact]
    public async Task StopDoesNotUnlockBlankProjectMutationWhileCreationIsUnresolved()
    {
        var sink = new ControllableSink();
        var chat = new MobileChatViewModel(sink);
        var projectId = Guid.NewGuid();
        chat.ApplyProjectCatalog(
        [
            new RemoteProject
            {
                Id = projectId,
                Name = "Lumi",
                IsCodingProject = true,
                DefaultNewChatsUseWorktree = true
            }
        ]);
        chat.Reset(Guid.Empty, "New chat");
        chat.ProjectValue = projectId.ToString();
        chat.ProjectName = "Lumi";
        chat.PromptText = "create it";

        var send = chat.SendCommand.ExecuteAsync(null);
        await sink.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await chat.StopCommand.ExecuteAsync(null);
        await chat.RemoveProjectCommand.ExecuteAsync(null);

        Assert.False(chat.CanChangeProjectSelection);
        Assert.Equal(projectId.ToString(), chat.ProjectValue);
        Assert.Equal("Lumi", chat.ProjectName);

        var createdChatId = Guid.NewGuid();
        sink.CommandResult.SetResult(new RemoteCommandResult
        {
            Ok = true,
            ChatId = createdChatId
        });
        await send;

        Assert.True(chat.TryAdoptCreatedChat(createdChatId, blankGeneration: 1));
        Assert.True(chat.CanChangeProjectSelection);
    }

    [Fact]
    public async Task AmbiguousStopReusesItsRequestId()
    {
        var sink = new SequencedCommandSink(commandCount: 2);
        var chat = new MobileChatViewModel(sink);
        var chatId = Guid.NewGuid();
        chat.Reset(chatId, "Running");
        chat.IsBusy = true;

        var firstStop = chat.StopCommand.ExecuteAsync(null);
        var first = await sink.WaitForCommandAsync(0);
        sink.Complete(
            0,
            new RemoteCommandResult
            {
                Ok = false,
                Error = "connection reset",
                IsOutcomeUnknown = true,
                RequestId = first.RequestId
            });
        await firstStop;

        Assert.True(chat.IsBusy);
        Assert.Equal("Stopping…", chat.StatusText);

        var retry = chat.StopCommand.ExecuteAsync(null);
        var second = await sink.WaitForCommandAsync(1);
        sink.Complete(1, new RemoteCommandResult { Ok = true });
        await retry;

        Assert.Equal(first.RequestId, second.RequestId);
        Assert.False(chat.IsBusy);
        Assert.False(chat.IsStreaming);
    }

    [Fact]
    public async Task NewSendInvalidatesAnAmbiguousStopRequestId()
    {
        var sink = new SequencedCommandSink(commandCount: 3);
        var chat = new MobileChatViewModel(sink);
        var chatId = Guid.NewGuid();
        chat.Reset(chatId, "Running");
        chat.IsBusy = true;

        var firstStop = chat.StopCommand.ExecuteAsync(null);
        var firstStopCommand = await sink.WaitForCommandAsync(0);
        sink.Complete(
            0,
            new RemoteCommandResult
            {
                Ok = false,
                Error = "connection reset",
                IsOutcomeUnknown = true,
                RequestId = firstStopCommand.RequestId
            });
        await firstStop;

        chat.PromptText = "start another turn";
        var send = chat.SendCommand.ExecuteAsync(null);
        await sink.WaitForCommandAsync(1);
        sink.Complete(1, new RemoteCommandResult { Ok = true, ChatId = chatId });
        await send;

        chat.IsBusy = true;
        var secondStop = chat.StopCommand.ExecuteAsync(null);
        var secondStopCommand = await sink.WaitForCommandAsync(2);
        sink.Complete(2, new RemoteCommandResult { Ok = true });
        await secondStop;

        Assert.NotEqual(firstStopCommand.RequestId, secondStopCommand.RequestId);
    }

    [Fact]
    public async Task LateSuccessfulStopDoesNotClearANewerTurn()
    {
        var sink = new SequencedCommandSink(commandCount: 2);
        var chat = new MobileChatViewModel(sink);
        var chatId = Guid.NewGuid();
        chat.Reset(chatId, "Running");
        chat.IsBusy = true;

        var stop = chat.StopCommand.ExecuteAsync(null);
        await sink.WaitForCommandAsync(0);

        chat.PromptText = "new turn";
        var send = chat.SendCommand.ExecuteAsync(null);
        await sink.WaitForCommandAsync(1);
        sink.Complete(1, new RemoteCommandResult { Ok = true, ChatId = chatId });
        await send;
        Assert.True(chat.IsBusy);

        sink.Complete(0, new RemoteCommandResult { Ok = true, ChatId = chatId });
        await stop;

        Assert.True(chat.IsBusy);
    }

    [Fact]
    public async Task IdleStatusCompletesAnAmbiguousStop()
    {
        var sink = new SequencedCommandSink(commandCount: 1);
        var chat = new MobileChatViewModel(sink);
        var chatId = Guid.NewGuid();
        chat.Reset(chatId, "Running");
        chat.IsBusy = true;

        var stop = chat.StopCommand.ExecuteAsync(null);
        var command = await sink.WaitForCommandAsync(0);
        sink.Complete(
            0,
            new RemoteCommandResult
            {
                Ok = false,
                Error = "connection reset",
                IsOutcomeUnknown = true,
                RequestId = command.RequestId
            });
        await stop;

        chat.ApplyStatus(new RemoteChatStatus { ChatId = chatId });

        Assert.False(chat.IsBusy);
        Assert.False(chat.IsStreaming);
        Assert.Null(chat.StatusText);
    }

    [Fact]
    public async Task IdleStatusBeforeAnAmbiguousStopResponseDoesNotRearmStopping()
    {
        var sink = new ControllableSink();
        var chat = new MobileChatViewModel(sink);
        var chatId = Guid.NewGuid();
        chat.Reset(chatId, "Running");
        chat.IsBusy = true;

        var stop = chat.StopCommand.ExecuteAsync(null);
        var command = await sink.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        chat.ApplyStatus(new RemoteChatStatus { ChatId = chatId });
        sink.CommandResult.SetResult(new RemoteCommandResult
        {
            Ok = false,
            Error = "connection reset",
            IsOutcomeUnknown = true,
            RequestId = command.RequestId
        });
        await stop;

        Assert.False(chat.IsBusy);
        Assert.False(chat.IsStreaming);
        Assert.Null(chat.StatusText);
    }

    [Fact]
    public async Task StartedWorktreeChatCannotRemoveItsProject()
    {
        var sink = new RecordingSink();
        var chat = new MobileChatViewModel(sink);
        var projectId = Guid.NewGuid();
        chat.ApplyProjectCatalog(
        [
            new RemoteProject
            {
                Id = projectId,
                Name = "Lumi",
                IsCodingProject = true
            }
        ]);
        var chatId = Guid.NewGuid();
        chat.Reset(chatId, "Started");
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 1,
            TotalRawMessageCount = 1,
            Status = new RemoteChatStatus
            {
                ChatId = chatId,
                ProjectId = projectId,
                ProjectName = "Lumi",
                UsesWorktree = true
            }
        });

        await chat.RemoveProjectCommand.ExecuteAsync(null);

        Assert.False(chat.CanChangeProjectSelection);
        Assert.Equal(projectId.ToString(), chat.ProjectValue);
        Assert.Equal("Lumi", chat.ProjectName);
        Assert.Null(sink.LastCommand);
    }

    [Fact]
    public void StartedWorktreeChatRejectsDirectProjectPropertyChanges()
    {
        var sink = new RecordingSink();
        var chat = new MobileChatViewModel(sink);
        var originalProjectId = Guid.NewGuid();
        var otherProjectId = Guid.NewGuid();
        chat.ApplyProjectCatalog(
        [
            new RemoteProject
            {
                Id = originalProjectId,
                Name = "Original",
                IsCodingProject = true
            },
            new RemoteProject
            {
                Id = otherProjectId,
                Name = "Other",
                IsCodingProject = true
            }
        ]);
        var chatId = Guid.NewGuid();
        chat.Reset(chatId, "Started");
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 1,
            TotalRawMessageCount = 1,
            Status = new RemoteChatStatus
            {
                ChatId = chatId,
                ProjectId = originalProjectId,
                ProjectName = "Original",
                UsesWorktree = true
            }
        });

        chat.ProjectValue = otherProjectId.ToString();
        chat.ProjectName = "Other";

        Assert.Equal(originalProjectId.ToString(), chat.ProjectValue);
        Assert.Equal("Original", chat.ProjectName);
        Assert.Null(sink.LastCommand);
    }

    [Fact]
    public void StartedWorktreeTranscriptDiscardsStagedProjectIntent()
    {
        var sink = new RecordingSink();
        var chat = new MobileChatViewModel(sink);
        var originalProjectId = Guid.NewGuid();
        var stagedProjectId = Guid.NewGuid();
        chat.ApplyProjectCatalog(
        [
            new RemoteProject
            {
                Id = originalProjectId,
                Name = "Original",
                IsCodingProject = true
            },
            new RemoteProject
            {
                Id = stagedProjectId,
                Name = "Staged",
                IsCodingProject = true
            }
        ]);
        var chatId = Guid.NewGuid();
        chat.Reset(chatId, "Empty");
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 1,
            TotalRawMessageCount = 0,
            Status = new RemoteChatStatus
            {
                ChatId = chatId,
                ProjectId = originalProjectId,
                ProjectName = "Original",
                UsesWorktree = true
            }
        });
        chat.ProjectValue = stagedProjectId.ToString();
        chat.ProjectName = "Staged";

        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 2,
            TotalRawMessageCount = 1,
            Status = new RemoteChatStatus
            {
                ChatId = chatId,
                ProjectId = originalProjectId,
                ProjectName = "Original",
                UsesWorktree = true
            }
        });

        Assert.Equal(originalProjectId.ToString(), chat.ProjectValue);
        Assert.Equal("Original", chat.ProjectName);
    }

    [Fact]
    public async Task SwitchingBlankProjectsRecomputesTheWorkspaceDefault()
    {
        var sink = new RecordingSink();
        var chat = new MobileChatViewModel(sink);
        var localProjectId = Guid.NewGuid();
        var worktreeProjectId = Guid.NewGuid();
        chat.ApplyProjectCatalog(
        [
            new RemoteProject
            {
                Id = localProjectId,
                Name = "Local",
                IsCodingProject = true,
                DefaultNewChatsUseWorktree = false
            },
            new RemoteProject
            {
                Id = worktreeProjectId,
                Name = "Worktree",
                IsCodingProject = true,
                DefaultNewChatsUseWorktree = true
            }
        ]);
        chat.Reset(Guid.Empty, "New chat");

        chat.ProjectValue = localProjectId.ToString();
        chat.ProjectName = "Local";
        Assert.False(chat.UseWorktree);

        chat.ProjectValue = worktreeProjectId.ToString();
        chat.ProjectName = "Worktree";
        Assert.True(chat.UseWorktree);

        chat.ProjectValue = null;
        chat.ProjectName = null;
        chat.PromptText = "hello";
        await chat.SendCommand.ExecuteAsync(null);

        Assert.False(chat.UseWorktree);
        Assert.Null(sink.LastCommand?.Get("projectId"));
        Assert.Null(sink.LastCommand?.Get("worktree"));
    }

    [Fact]
    public async Task RemovingTheSelectedProjectClearsPendingWorktreeIntent()
    {
        var sink = new RecordingSink();
        var chat = new MobileChatViewModel(sink);
        var projectId = Guid.NewGuid();
        chat.ApplyProjectCatalog(
        [
            new RemoteProject
            {
                Id = projectId,
                Name = "Lumi",
                IsCodingProject = true
            }
        ]);
        chat.Reset(Guid.Empty, "New chat");
        chat.ProjectValue = projectId.ToString();
        chat.ProjectName = "Lumi";
        chat.SelectNewWorktreeCommand.Execute(null);

        await chat.RemoveProjectCommand.ExecuteAsync(null);
        chat.PromptText = "hello";
        await chat.SendCommand.ExecuteAsync(null);

        Assert.False(chat.UseWorktree);
        Assert.Null(sink.LastCommand?.Get("projectId"));
        Assert.Null(sink.LastCommand?.Get("worktree"));
    }

    [Fact]
    public async Task FailedWorktreeCreationKeepsTheIntentForTheAdoptedChatRetry()
    {
        var createdChatId = Guid.NewGuid();
        var sink = new WorktreeRetrySink(createdChatId);
        var chat = new MobileChatViewModel(sink);
        var projectId = Guid.NewGuid();
        chat.ApplyProjectCatalog(
        [
            new RemoteProject
            {
                Id = projectId,
                Name = "Lumi",
                IsCodingProject = true
            }
        ]);
        chat.Reset(Guid.Empty, "New chat");
        chat.ProjectValue = projectId.ToString();
        chat.ProjectName = "Lumi";
        chat.SelectNewWorktreeCommand.Execute(null);
        chat.ChatCreated += (chatId, generation) =>
            Assert.True(chat.TryAdoptCreatedChat(chatId, generation));
        chat.PromptText = "fix the build";

        await chat.SendCommand.ExecuteAsync(null);
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = createdChatId,
            Revision = 1,
            TotalRawMessageCount = 0,
            Status = new RemoteChatStatus
            {
                ChatId = createdChatId,
                ProjectId = projectId,
                ProjectName = "Lumi",
                UsesWorktree = false
            }
        });
        await chat.FlushPendingConfigurationAsync();

        Assert.Equal(createdChatId, chat.ChatId);
        Assert.True(chat.UseWorktree);
        Assert.True(chat.CanChooseWorktree);
        Assert.Equal("fix the build", chat.PromptText);
        Assert.DoesNotContain(
            sink.Commands,
            command => command.Action == RemoteProtocol.Actions.ConfigureChat);

        await chat.SendCommand.ExecuteAsync(null);

        var sends = sink.Commands
            .Where(command => command.Action == RemoteProtocol.Actions.SendMessage)
            .ToArray();
        Assert.Equal(2, sends.Length);
        Assert.Equal(createdChatId.ToString(), sends[1].Get("chatId"));
        Assert.Equal("true", sends[1].Get("worktree"));
    }

    [Fact]
    public void EmptyExistingChatReflectsItsPersistedWorktreeState()
    {
        var chat = new MobileChatViewModel(new RecordingSink());
        var projectId = Guid.NewGuid();
        chat.ApplyProjectCatalog(
        [
            new RemoteProject
            {
                Id = projectId,
                Name = "Lumi",
                IsCodingProject = true
            }
        ]);
        var chatId = Guid.NewGuid();
        chat.Reset(chatId, "Empty");

        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 1,
            TotalRawMessageCount = 0,
            Status = new RemoteChatStatus
            {
                ChatId = chatId,
                ProjectId = projectId,
                ProjectName = "Lumi",
                UsesWorktree = true
            }
        });

        Assert.True(chat.CanChooseWorktree);
        Assert.True(chat.UseWorktree);
        Assert.Equal("New worktree", chat.WorkspaceSummary);
    }

    [Fact]
    public void CatalogRefreshDoesNotOverrideAnExistingChatsLocalWorkspace()
    {
        var projectId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        var project = new RemoteProject
        {
            Id = projectId,
            Name = "Lumi",
            IsCodingProject = true,
            DefaultNewChatsUseWorktree = true
        };
        var chat = new MobileChatViewModel(new RecordingSink());
        chat.ApplyProjectCatalog([project]);
        chat.Reset(chatId, "Empty");
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 1,
            TotalRawMessageCount = 0,
            Status = new RemoteChatStatus
            {
                ChatId = chatId,
                ProjectId = projectId,
                ProjectName = "Lumi",
                UsesWorktree = false
            }
        });

        chat.ApplyProjectCatalog([project]);

        Assert.False(chat.UseWorktree);
        Assert.True(chat.IsLocalWorkspaceSelected);
    }

    [Fact]
    public async Task LocalSelectionStagesDetachEvenWhenMissingWorktreeProjectsAsLocal()
    {
        var sink = new RecordingSink();
        var projectId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(sink);
        chat.ApplyProjectCatalog(
        [
            new RemoteProject { Id = projectId, Name = "Lumi", IsCodingProject = true }
        ]);
        chat.Reset(chatId, "Empty");
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 1,
            TotalRawMessageCount = 0,
            Status = new RemoteChatStatus
            {
                ChatId = chatId,
                ProjectId = projectId,
                ProjectName = "Lumi",
                UsesWorktree = false
            }
        });

        chat.SelectLocalWorkspaceCommand.Execute(null);
        chat.PromptText = "run locally";
        await chat.SendCommand.ExecuteAsync(null);

        var send = Assert.Single(
            sink.Commands,
            command => command.Action == RemoteProtocol.Actions.SendMessage);
        Assert.Equal("false", send.Get("worktree"));
    }

    [Fact]
    public async Task AuthoritativeFirstTurnClearsStaleDeferredWorktreeIntent()
    {
        var sink = new RecordingSink();
        var projectId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(sink);
        chat.ApplyProjectCatalog(
        [
            new RemoteProject { Id = projectId, Name = "Lumi", IsCodingProject = true }
        ]);
        chat.Reset(chatId, "Empty");
        chat.ProjectValue = projectId.ToString();
        chat.ProjectName = "Lumi";
        chat.SelectNewWorktreeCommand.Execute(null);

        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 1,
            TotalRawMessageCount = 1,
            Status = new RemoteChatStatus
            {
                ChatId = chatId,
                ProjectId = projectId,
                ProjectName = "Lumi",
                UsesWorktree = false
            }
        });
        chat.PromptText = "follow up";
        await chat.SendCommand.ExecuteAsync(null);

        var send = Assert.Single(
            sink.Commands,
            command => command.Action == RemoteProtocol.Actions.SendMessage);
        Assert.Null(send.Get("worktree"));
    }

    [Fact]
    public async Task TranscriptInvalidationRevokesEmptyHistoryAuthorityAndWorkspaceIntent()
    {
        var sink = new RecordingSink();
        var projectId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(sink);
        chat.ApplyProjectCatalog(
        [
            new RemoteProject { Id = projectId, Name = "Lumi", IsCodingProject = true }
        ]);
        chat.Reset(chatId, "Empty");
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 1,
            TotalRawMessageCount = 0,
            Status = new RemoteChatStatus
            {
                ChatId = chatId,
                ProjectId = projectId,
                ProjectName = "Lumi",
                UsesWorktree = false
            }
        });
        chat.SelectNewWorktreeCommand.Execute(null);
        Assert.True(chat.CanChooseWorktree);

        chat.InvalidateTranscriptAuthority();
        chat.PromptText = "send safely";
        await chat.SendCommand.ExecuteAsync(null);

        Assert.False(chat.CanChooseWorktree);
        var send = Assert.Single(
            sink.Commands,
            command => command.Action == RemoteProtocol.Actions.SendMessage);
        Assert.Null(send.Get("worktree"));
    }

    [Fact]
    public async Task EmptyChatStagesProjectAndWorkspaceTogetherUntilSend()
    {
        var sink = new RecordingSink();
        var oldProjectId = Guid.NewGuid();
        var newProjectId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(sink);
        chat.ApplyProjectCatalog(
        [
            new RemoteProject { Id = oldProjectId, Name = "Old", IsCodingProject = true },
            new RemoteProject { Id = newProjectId, Name = "New", IsCodingProject = true }
        ]);
        chat.Reset(chatId, "Empty");
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 1,
            TotalRawMessageCount = 0,
            Status = new RemoteChatStatus
            {
                ChatId = chatId,
                ProjectId = oldProjectId,
                ProjectName = "Old",
                UsesWorktree = true
            }
        });

        chat.ProjectValue = newProjectId.ToString();
        chat.ProjectName = "New";
        chat.SelectNewWorktreeCommand.Execute(null);
        await Task.Delay(50);

        Assert.DoesNotContain(
            sink.Commands,
            command => command.Action == RemoteProtocol.Actions.ConfigureChat
                       && command.Get("projectId") == newProjectId.ToString());

        chat.PromptText = "switch";
        await chat.SendCommand.ExecuteAsync(null);

        var send = Assert.Single(
            sink.Commands,
            command => command.Action == RemoteProtocol.Actions.SendMessage);
        Assert.Equal(newProjectId.ToString(), send.Get("projectId"));
        Assert.Equal("true", send.Get("worktree"));
    }

    [Fact]
    public async Task ReopeningStagedProjectSwitchDoesNotFlushProjectBeforeWorkspace()
    {
        var sink = new RecordingSink();
        var oldProjectId = Guid.NewGuid();
        var newProjectId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(sink);
        chat.ApplyProjectCatalog(
        [
            new RemoteProject { Id = oldProjectId, Name = "Old", IsCodingProject = true },
            new RemoteProject { Id = newProjectId, Name = "New", IsCodingProject = true }
        ]);
        chat.Reset(chatId, "Empty");
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 1,
            TotalRawMessageCount = 0,
            Status = new RemoteChatStatus
            {
                ChatId = chatId,
                ProjectId = oldProjectId,
                ProjectName = "Old",
                UsesWorktree = true
            }
        });
        chat.ProjectValue = newProjectId.ToString();
        chat.ProjectName = "New";
        chat.SelectNewWorktreeCommand.Execute(null);

        chat.Reset(Guid.NewGuid(), "Other");
        sink.Commands.Clear();
        chat.Reset(chatId, "Empty");
        await Task.Delay(50);

        Assert.DoesNotContain(
            sink.Commands,
            command => command.Action == RemoteProtocol.Actions.ConfigureChat
                       && command.Get("projectId") == newProjectId.ToString());

        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 2,
            TotalRawMessageCount = 0,
            Status = new RemoteChatStatus
            {
                ChatId = chatId,
                ProjectId = oldProjectId,
                ProjectName = "Old",
                UsesWorktree = true
            }
        });
        chat.PromptText = "switch";
        await chat.SendCommand.ExecuteAsync(null);

        var send = Assert.Single(
            sink.Commands,
            command => command.Action == RemoteProtocol.Actions.SendMessage);
        Assert.Equal(newProjectId.ToString(), send.Get("projectId"));
        Assert.Equal("true", send.Get("worktree"));
    }

    [Fact]
    public async Task AmbiguousRetryLocksWorkspaceChoiceUntilItIsResolved()
    {
        var projectId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(new TimeoutCommandSink());
        chat.ApplyProjectCatalog(
        [
            new RemoteProject { Id = projectId, Name = "Lumi", IsCodingProject = true }
        ]);
        chat.Reset(chatId, "Empty");
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 1,
            TotalRawMessageCount = 0,
            Status = new RemoteChatStatus
            {
                ChatId = chatId,
                ProjectId = projectId,
                ProjectName = "Lumi",
                UsesWorktree = true
            }
        });
        Assert.True(chat.UseWorktree);
        chat.PromptText = "retry";

        await chat.SendCommand.ExecuteAsync(null);
        Assert.True(chat.UseWorktree);
        chat.SelectLocalWorkspaceCommand.Execute(null);

        Assert.False(chat.CanChooseWorktree);
        Assert.True(chat.UseWorktree);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public void SendIsDisabledUntilLatestTranscriptAndUploadsAreReady(
        bool isLoading,
        bool isLatestWindow,
        bool isUploading)
    {
        var chat = new MobileChatViewModel(new RecordingSink())
        {
            PromptText = "send me",
            IsLoading = isLoading,
            IsLatestWindow = isLatestWindow,
            IsUploading = isUploading
        };

        Assert.False(chat.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task ProjectEditorPreservesInstructionsAndWorkingDirectorySeparately()
    {
        var sink = new RecordingSink();
        var library = new LibraryViewModel(sink);
        var projectId = Guid.NewGuid();
        library.Apply(new RemoteLibrary
        {
            Projects =
            [
                new RemoteProject
                {
                    Id = projectId,
                    Name = "Lumi",
                    Instructions = "Use .NET",
                    WorkingDirectory = @"C:\repo"
                }
            ]
        });
        sink.LibraryItem = new RemoteLibraryItem
        {
            Resource = RemoteProtocol.Resources.Projects,
            Identifier = projectId.ToString(),
            Name = "Lumi",
            Body = "Use .NET",
            WorkingDirectory = @"C:\repo"
        };

        var entry = Assert.Single(library.Entries);
        await library.BeginEditCommand.ExecuteAsync(entry);

        Assert.Equal("Use .NET", library.EditBody);
        Assert.Equal(@"C:\repo", library.EditWorkingDirectory);

        await library.SaveCommand.ExecuteAsync(null);

        Assert.Equal(RemoteProtocol.Actions.ConfigureFeature, sink.LastCommand?.Action);
        Assert.Equal("Use .NET", sink.LastCommand?.Get("instructions"));
        Assert.Equal(@"C:\repo", sink.LastCommand?.Get("workingDirectory"));
    }

    [Fact]
    public async Task LibraryEditorSavesFullDetailInsteadOfTruncatedProjection()
    {
        var suffix = new string('z', 4096);
        var fullContent = new string('x', RemoteProtocol.MobileToolInputLimit) + suffix;
        var skillId = Guid.NewGuid();
        var sink = new RecordingSink
        {
            LibraryItem = new RemoteLibraryItem
            {
                Resource = RemoteProtocol.Resources.Skills,
                Identifier = skillId.ToString(),
                Name = "Long skill",
                Description = "Description",
                Body = fullContent,
                Glyph = "✦"
            }
        };
        var library = new LibraryViewModel(sink);
        library.SectionIndex = (int)LibrarySection.Skills;
        library.Apply(new RemoteLibrary
        {
            Skills =
            [
                new RemoteSkill
                {
                    Id = skillId,
                    Name = "Long skill",
                    Content = RemoteProtocol.TruncateForMobile(
                        fullContent,
                        RemoteProtocol.MobileToolInputLimit)
                }
            ]
        });

        await library.BeginEditCommand.ExecuteAsync(Assert.Single(library.Entries));
        await library.SaveCommand.ExecuteAsync(null);

        Assert.Equal(fullContent, sink.LastCommand?.Get("content"));
        Assert.EndsWith(suffix, sink.LastCommand?.Get("content"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DelayedLibrarySaveDoesNotCloseANewerEditor()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var sink = new DeferredLibrarySink(
            new RemoteLibraryItem
            {
                Resource = RemoteProtocol.Resources.Skills,
                Identifier = firstId.ToString(),
                Name = "First",
                Body = "First body"
            },
            new RemoteLibraryItem
            {
                Resource = RemoteProtocol.Resources.Skills,
                Identifier = secondId.ToString(),
                Name = "Second",
                Body = "Second body"
            });
        var library = new LibraryViewModel(sink) { SectionIndex = (int)LibrarySection.Skills };
        library.Apply(new RemoteLibrary
        {
            Skills =
            [
                new RemoteSkill { Id = firstId, Name = "First" },
                new RemoteSkill { Id = secondId, Name = "Second" }
            ]
        });

        await library.BeginEditCommand.ExecuteAsync(library.Entries[0]);
        var saveFirst = library.SaveCommand.ExecuteAsync(null);
        await sink.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        library.CancelEditCommand.Execute(null);
        await library.BeginEditCommand.ExecuteAsync(library.Entries[1]);
        library.EditBody = "Unsaved second edit";

        sink.CommandResult.SetResult(new RemoteCommandResult { Ok = true });
        await saveFirst;

        Assert.True(library.IsEditing);
        Assert.Equal(secondId.ToString(), library.SelectedEntry?.Identifier);
        Assert.Equal("Unsaved second edit", library.EditBody);
    }

    [Fact]
    public async Task ExistingChatChipConfigurationCompletesBeforeSendStarts()
    {
        var sink = new SequencedCommandSink(commandCount: 2);
        var chat = new MobileChatViewModel(sink);
        chat.Reset(Guid.NewGuid(), "Existing chat");

        chat.SkillChips.Add(new StrataComposerChip("Documents", "✦"));
        var configure = await sink.WaitForCommandAsync(0);
        chat.PromptText = "use the skill";
        var send = chat.SendCommand.ExecuteAsync(null);

        await Task.Delay(30);
        Assert.Equal(RemoteProtocol.Actions.ConfigureChat, configure.Action);
        Assert.False(sink.HasStarted(1));

        sink.Complete(0, new RemoteCommandResult { Ok = true });
        var sendCommand = await sink.WaitForCommandAsync(1);
        Assert.Equal(RemoteProtocol.Actions.SendMessage, sendCommand.Action);
        sink.Complete(1, new RemoteCommandResult { Ok = true });
        await send;
    }

    [Fact]
    public void DelayedTranscriptStatusCannotRevertNewerLocalConfiguration()
    {
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(new ImmediateSink());
        chat.Reset(chatId, "Chat");
        chat.ApplyStatus(new RemoteChatStatus { ChatId = chatId, Model = "model-a" });
        var requestStatusVersion = chat.StatusVersion;
        chat.Model = "model-b";

        chat.ApplyTranscript(
            new RemoteTranscript
            {
                ChatId = chatId,
                Revision = 1,
                Status = new RemoteChatStatus { ChatId = chatId, Model = "model-a" }
            },
            requestStatusVersion);

        Assert.Equal("model-b", chat.Model);
    }

    [Fact]
    public void ProjectLibraryUpdateClearsStaleScopeAndPublishesComposerCatalogs()
    {
        var chat = new MobileChatViewModel(new ImmediateSink());
        chat.Reset(Guid.Empty, "New chat");
        chat.ProjectName = "Deleted project";

        chat.ApplyLibraryCatalogs(new RemoteLibrary
        {
            Projects = [new RemoteProject { Id = Guid.NewGuid(), Name = "Renamed project" }],
            Skills = [new RemoteSkill { Id = Guid.NewGuid(), Name = "Project skill", IconGlyph = "✦" }],
            Lumis = [new RemoteLumi { Id = Guid.NewGuid(), Name = "Project Lumi", IconGlyph = "◉" }],
            McpServers = [new RemoteMcpServer { Id = Guid.NewGuid(), Name = "Project MCP", IsEnabled = true }]
        });

        Assert.Null(chat.ProjectName);
        Assert.False(chat.HasPendingConfiguration);
        Assert.Contains(chat.AvailableProjects, chip => chip.Name == "Renamed project");
        Assert.Contains(chat.AvailableSkills, chip => chip.Name == "Project skill");
        Assert.Contains(chat.AvailableAgents, chip => chip.Name == "Project Lumi");
        Assert.Contains(chat.AvailableMcps, chip => chip.Name == "Project MCP");
    }

    [Fact]
    public async Task LibraryFrameClearsADeletedActiveProjectFilter()
    {
        await using var scope = new ShellScope();
        var shell = scope.Shell;
        shell.ActiveProjectId = Guid.NewGuid();
        var applyLibrary = typeof(MobileShellViewModel).GetMethod(
            "ApplyLibrary",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(applyLibrary);

        applyLibrary!.Invoke(shell,
        [
            new RemoteLibrary
            {
                Projects = [new RemoteProject { Id = Guid.NewGuid(), Name = "Remaining project" }]
            },
            true
        ]);

        Assert.Null(shell.ActiveProject);
        Assert.Null(shell.ChatList.ProjectFilterId);
        Assert.Equal("Remaining project", Assert.Single(shell.Projects).Name);
    }

    [Fact]
    public async Task DelayedStopCompletionDoesNotMutateAnotherChat()
    {
        var sink = new SequencedCommandSink(commandCount: 1);
        var chat = new MobileChatViewModel(sink);
        chat.Reset(Guid.NewGuid(), "A");
        chat.IsBusy = true;
        var stop = chat.StopCommand.ExecuteAsync(null);
        await sink.WaitForCommandAsync(0);

        var chatB = Guid.NewGuid();
        chat.Reset(chatB, "B");
        chat.IsBusy = true;
        chat.IsStreaming = true;
        chat.StatusText = "B is running";

        sink.Complete(0, new RemoteCommandResult { Ok = true });
        await stop;

        Assert.Equal(chatB, chat.ChatId);
        Assert.True(chat.IsBusy);
        Assert.True(chat.IsStreaming);
        Assert.Equal("B is running", chat.StatusText);
    }

    [Fact]
    public async Task RemovedSelectedChatResetsTheActiveSurface()
    {
        await using var scope = new ShellScope();
        var shell = scope.Shell;
        var chatId = Guid.NewGuid();
        shell.ChatList.Apply(
        [
            new RemoteChatGroup
            {
                Label = "Today",
                Chats = [new RemoteChat { Id = chatId, Title = "Deleted chat" }]
            }
        ]);
        shell.Chat.Reset(chatId, "Deleted chat");
        shell.ChatList.SelectedChatId = chatId;

        shell.ChatList.Apply([]);

        Assert.Equal(Guid.Empty, shell.Chat.ChatId);
        Assert.Equal(Guid.Empty, shell.ChatList.SelectedChatId);
        Assert.Empty(shell.Chat.Turns);
    }

    [Fact]
    public async Task BackDismissesTheDrawerThenEveryChatSheetBeforeExit()
    {
        await using var scope = new ShellScope();
        var shell = scope.Shell;
        shell.UpdateLayout(393, 852);
        shell.IsDrawerOpen = true;
        shell.Chat.IsRunSettingsSheetOpen = true;
        shell.Chat.IsModelSheetOpen = true;
        shell.Chat.IsContextSheetOpen = true;
        shell.Chat.IsEffortSheetOpen = true;
        shell.Chat.IsPlanOpen = true;

        shell.GoBackCommand.Execute(null);
        Assert.False(shell.IsDrawerOpen);
        Assert.True(shell.Chat.IsPlanOpen);

        shell.GoBackCommand.Execute(null);
        Assert.False(shell.Chat.IsPlanOpen);
        Assert.True(shell.Chat.IsEffortSheetOpen);

        shell.GoBackCommand.Execute(null);
        Assert.False(shell.Chat.IsEffortSheetOpen);
        Assert.True(shell.Chat.IsContextSheetOpen);

        shell.GoBackCommand.Execute(null);
        Assert.False(shell.Chat.IsContextSheetOpen);
        Assert.True(shell.Chat.IsModelSheetOpen);

        shell.GoBackCommand.Execute(null);
        Assert.False(shell.Chat.IsModelSheetOpen);
        Assert.True(shell.Chat.IsRunSettingsSheetOpen);

        shell.GoBackCommand.Execute(null);
        Assert.False(shell.Chat.IsRunSettingsSheetOpen);
        Assert.False(shell.CanGoBack);
    }

    [Fact]
    public void McpAndJobRowsDoNotAdvertiseUnsupportedDetailEditing()
    {
        Assert.False(new LibraryEntryViewModel
        {
            Section = LibrarySection.McpServers,
            Identifier = "mcp"
        }.CanEdit);
        Assert.False(new LibraryEntryViewModel
        {
            Section = LibrarySection.Jobs,
            Identifier = "job"
        }.CanEdit);
    }

    [Fact]
    public async Task BackDismissesLibraryActionsThenEditorThenPage()
    {
        await using var scope = new ShellScope();
        var shell = scope.Shell;
        shell.Page = MobilePage.Library;
        shell.Library.IsEditing = true;
        shell.Library.IsRowActionsOpen = true;

        shell.GoBackCommand.Execute(null);
        Assert.False(shell.Library.IsRowActionsOpen);
        Assert.True(shell.Library.IsEditing);
        Assert.Equal(MobilePage.Library, shell.Page);

        shell.GoBackCommand.Execute(null);
        Assert.False(shell.Library.IsEditing);
        Assert.Equal(MobilePage.Library, shell.Page);

        shell.GoBackCommand.Execute(null);
        Assert.Equal(MobilePage.Chat, shell.Page);
        Assert.False(shell.CanGoBack);
    }

    [Fact]
    public async Task BackDismissesChatActionsThenDrawerThenPageThenFallsThrough()
    {
        await using var scope = new ShellScope();
        var shell = scope.Shell;
        shell.UpdateLayout(393, 852);
        shell.Page = MobilePage.Settings;
        shell.IsDrawerOpen = true;
        shell.IsChatActionsOpen = true;

        shell.GoBackCommand.Execute(null);
        Assert.False(shell.IsChatActionsOpen);
        Assert.True(shell.IsDrawerOpen);
        Assert.Equal(MobilePage.Settings, shell.Page);

        shell.GoBackCommand.Execute(null);
        Assert.False(shell.IsDrawerOpen);
        Assert.Equal(MobilePage.Settings, shell.Page);

        shell.GoBackCommand.Execute(null);
        Assert.Equal(MobilePage.Chat, shell.Page);
        Assert.False(shell.CanGoBack);
    }

    [Fact]
    public async Task CanGoBackNotifiesWheneverSheetStateChanges()
    {
        await using var scope = new ShellScope();
        var shell = scope.Shell;

        AssertNotifies(shell, value => shell.Chat.IsRunSettingsSheetOpen = value);
        AssertNotifies(shell, value => shell.Chat.IsModelSheetOpen = value);
        AssertNotifies(shell, value => shell.Chat.IsContextSheetOpen = value);
        AssertNotifies(shell, value => shell.Chat.IsEffortSheetOpen = value);
        AssertNotifies(shell, value => shell.Chat.IsPlanOpen = value);
        AssertNotifies(shell, value => shell.IsChatActionsOpen = value);

        shell.Page = MobilePage.Library;
        AssertNotifies(shell, value => shell.Library.IsRowActionsOpen = value);
        AssertNotifies(shell, value => shell.Library.IsEditing = value);
    }

    private static void AssertNotifies(MobileShellViewModel shell, Action<bool> setState)
    {
        var notifications = 0;
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(MobileShellViewModel.CanGoBack))
                notifications++;
        };

        shell.PropertyChanged += handler;
        try
        {
            setState(true);
            Assert.True(notifications > 0);

            notifications = 0;
            setState(false);
            Assert.True(notifications > 0);
        }
        finally
        {
            shell.PropertyChanged -= handler;
        }
    }

    private sealed class ControllableSink : IRemoteCommandSink
    {
        public TaskCompletionSource<RemoteCommand> CommandStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<RemoteCommandResult> CommandResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> UploadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<RemoteUploadResponse> UploadResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command)
        {
            CommandStarted.TrySetResult(command);
            return await CommandResult.Task;
        }

        public async Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content)
        {
            UploadStarted.TrySetResult(fileName);
            return await UploadResult.Task;
        }
    }

    private sealed class ImmediateSink : IRemoteCommandSink
    {
        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            Task.FromResult(new RemoteCommandResult { Ok = true });

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse
            {
                Ok = true,
                FileName = fileName,
                Path = $@"C:\uploads\{fileName}"
            });
    }

    private sealed class ThrowingCommandSink : IRemoteCommandSink
    {
        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            throw new OperationCanceledException("connection closed");

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { Ok = true });
    }

    private sealed class RecordingSink : IRemoteCommandSink, IRemoteLibraryDetailSink
    {
        public RemoteCommand? LastCommand { get; private set; }
        public List<RemoteCommand> Commands { get; } = [];
        public RemoteLibraryItem? LibraryItem { get; set; }

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command)
        {
            LastCommand = command;
            Commands.Add(command);
            return Task.FromResult(new RemoteCommandResult { Ok = true });
        }

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { Ok = true });

        public Task<RemoteLibraryItem?> GetLibraryItemAsync(string resource, string identifier) =>
            Task.FromResult(LibraryItem);
    }

    private sealed class WorktreeRetrySink(Guid createdChatId) : IRemoteCommandSink
    {
        private int _sendCount;

        public List<RemoteCommand> Commands { get; } = [];

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command)
        {
            Commands.Add(command);
            if (command.Action == RemoteProtocol.Actions.SendMessage && ++_sendCount == 1)
            {
                return Task.FromResult(new RemoteCommandResult
                {
                    Error = "Lumi could not create the worktree.",
                    ChatId = createdChatId
                });
            }

            return Task.FromResult(new RemoteCommandResult
            {
                Ok = true,
                ChatId = createdChatId
            });
        }

        public Task<RemoteUploadResponse> UploadAsync(
            string fileName,
            ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { Ok = true });
    }

    private sealed class TimeoutCommandSink : IRemoteCommandSink
    {
        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            Task.FromResult(new RemoteCommandResult
            {
                Error = "Lumi took too long to answer.",
                RequestId = command.RequestId,
                IsTimeout = true
            });

        public Task<RemoteUploadResponse> UploadAsync(
            string fileName,
            ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { Ok = true });
    }

    private static async Task WaitForCommandAsync(RecordingSink sink, string argument)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (sink.LastCommand?.Arguments.ContainsKey(argument) == true)
                return;
            await Task.Delay(10);
        }

        throw new TimeoutException($"No command containing '{argument}' was sent.");
    }

    private sealed class CountingReadStream(int length) : Stream
    {
        private int _remaining = length;

        public int BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var count = Math.Min(buffer.Length, _remaining);
            if (count == 0)
                return ValueTask.FromResult(0);

            buffer.Span[..count].Fill(0x5A);
            _remaining -= count;
            BytesRead += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class ScriptedCommandSink(params RemoteCommandResult[] results) : IRemoteCommandSink
    {
        private readonly Queue<RemoteCommandResult> _results = new(results);

        public List<RemoteCommand> Commands { get; } = [];

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command)
        {
            Commands.Add(command);
            return Task.FromResult(_results.Dequeue());
        }

        public Task<RemoteUploadResponse> UploadAsync(
            string fileName,
            ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse
            {
                Ok = true,
                FileName = fileName,
                Path = $@"C:\uploads\{fileName}"
            });
    }

    private sealed class SequencedCommandSink(int commandCount) : IRemoteCommandSink
    {
        private readonly TaskCompletionSource<RemoteCommand>[] _started =
            CreateSources<RemoteCommand>(commandCount);
        private readonly TaskCompletionSource<RemoteCommandResult>[] _results =
            CreateSources<RemoteCommandResult>(commandCount);
        private int _nextCommand;

        public Task<RemoteCommand> WaitForCommandAsync(int index) =>
            _started[index].Task.WaitAsync(TimeSpan.FromSeconds(2));

        public void Complete(int index, RemoteCommandResult result) =>
            _results[index].SetResult(result);

        public bool HasStarted(int index) => _started[index].Task.IsCompleted;

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command)
        {
            var index = _nextCommand++;
            _started[index].SetResult(command);
            return _results[index].Task;
        }

        public Task<RemoteUploadResponse> UploadAsync(
            string fileName,
            ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse
            {
                Ok = true,
                FileName = fileName,
                Path = $@"C:\uploads\{fileName}"
            });

        private static TaskCompletionSource<T>[] CreateSources<T>(int count) =>
            Enumerable.Range(0, count)
                .Select(_ => new TaskCompletionSource<T>(
                    TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();
    }

    private sealed class DeferredLibrarySink(params RemoteLibraryItem[] items) :
        IRemoteCommandSink,
        IRemoteLibraryDetailSink
    {
        private readonly Dictionary<string, RemoteLibraryItem> _items =
                items.ToDictionary(item => item.Identifier, StringComparer.Ordinal);

        public TaskCompletionSource<RemoteCommand> CommandStarted { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<RemoteCommandResult> CommandResult { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command)
        {
                CommandStarted.TrySetResult(command);
                return CommandResult.Task;
        }

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
                Task.FromResult(new RemoteUploadResponse { Ok = true });

        public Task<RemoteLibraryItem?> GetLibraryItemAsync(string resource, string identifier) =>
                Task.FromResult(_items.GetValueOrDefault(identifier));
    }

    private sealed class ShellScope : IAsyncDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "lumi-mobile-tests", Guid.NewGuid().ToString("n"));

        public ShellScope()
        {
            Shell = new MobileShellViewModel(
                store: new MobileSettingsStore(_directory),
                post: action => action());
        }

        public MobileShellViewModel Shell { get; }

        public async ValueTask DisposeAsync()
        {
            await Shell.DisposeAsync();

            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
