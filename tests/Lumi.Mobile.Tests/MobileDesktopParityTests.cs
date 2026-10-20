using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class MobileDesktopParityTests
{
    [Fact]
    public void LiveListMovesChatsBetweenBucketsAndKeepsLoadedRowsAndIdentity()
    {
        var now = DateTimeOffset.Now;
        var recent = new RemoteChat { Id = Guid.NewGuid(), Title = "Recent", UpdatedAt = now };
        var older = new RemoteChat { Id = Guid.NewGuid(), Title = "Older", UpdatedAt = now.AddDays(-1) };
        var tail = new RemoteChat { Id = Guid.NewGuid(), Title = "Loaded tail", UpdatedAt = now.AddDays(-9) };
        var list = new ChatListViewModel(new RecordingSink());
        list.Apply(new RemoteChatPage
        {
            TotalCount = 3,
            HasMore = true,
            Groups =
            [
                new RemoteChatGroup { Label = "Today", Chats = [recent] },
                new RemoteChatGroup { Label = "Yesterday", Chats = [older] }
            ]
        });
        list.Apply(new RemoteChatPage
        {
            Offset = 2,
            TotalCount = 3,
            Groups = [new RemoteChatGroup { Label = "Older", Chats = [tail] }]
        }, reset: false);
        var olderRow = list.Groups.SelectMany(group => group.Chats).Single(chat => chat.Id == older.Id);
        var tailRow = list.Groups.SelectMany(group => group.Chats).Single(chat => chat.Id == tail.Id);
        list.SelectedChatId = older.Id;

        list.ApplyLive(new RemoteChatPage
        {
            TotalCount = 3,
            HasMore = true,
            Groups = [new RemoteChatGroup
            {
                Label = "Today",
                Chats =
                [
                    new RemoteChat { Id = older.Id, Title = older.Title, UpdatedAt = now.AddMinutes(1), HasUnreadMessages = true },
                    recent
                ]
            }]
        });

        Assert.Equal(["Today", "Older"], list.Groups.Select(group => group.Label));
        Assert.Equal([older.Id, recent.Id, tail.Id], list.Groups.SelectMany(group => group.Chats).Select(chat => chat.Id));
        Assert.Same(olderRow, list.Groups[0].Chats[0]);
        Assert.Same(tailRow, list.Groups[1].Chats[0]);
        Assert.True(olderRow.IsSelected);
        Assert.True(olderRow.HasUnreadMessages);
        Assert.False(list.HasMoreChats);

        list.ApplyLive(new RemoteChatPage
        {
            TotalCount = 3,
            Groups =
            [
                new RemoteChatGroup { Label = "Pinned", Chats = [new RemoteChat
                {
                    Id = recent.Id, Title = recent.Title, UpdatedAt = now, IsPinned = true
                }] },
                new RemoteChatGroup { Label = "Today", Chats = [new RemoteChat
                {
                    Id = older.Id, Title = older.Title, UpdatedAt = now.AddMinutes(1)
                }] }
            ]
        });
        Assert.Equal(["Pinned", "Today", "Older"], list.Groups.Select(group => group.Label));
        Assert.False(olderRow.HasUnreadMessages);
        Assert.Equal(3, list.VisibleChatCount);
    }

    [Fact]
    public void NewLiveRowsKeepDesktopOrderInsteadOfBeingInsertedInReverse()
    {
        var now = DateTimeOffset.Now;
        var rows = Enumerable.Range(0, 3).Select(index => new RemoteChat
        {
            Id = Guid.NewGuid(), Title = $"Chat {index}", UpdatedAt = now.AddMinutes(-index)
        }).ToList();
        var list = new ChatListViewModel(new RecordingSink());
        list.ApplyLive(new RemoteChatPage
        {
            TotalCount = rows.Count,
            Groups = [new RemoteChatGroup { Label = "Today", Chats = rows }]
        });
        Assert.Equal(rows.Select(row => row.Id), list.Groups.SelectMany(group => group.Chats).Select(row => row.Id));
    }

    [Theory]
    [InlineData(@"C:\Users\test\opaque-upload.pdf")]
    [InlineData("/tmp/opaque-upload.pdf")]
    [InlineData(@"\\PC\files\opaque-upload.pdf")]
    public void NamedAttachmentsRoundTripWithoutShowingPcPaths(string path)
    {
        var prompt = RemoteUserMessageContent.BuildPrompt("Review this report.",
            [new RemoteAttachment { Path = path, FileName = "Quarterly report.pdf" }]);
        var row = Assert.IsType<UserTurnItemViewModel>(TranscriptItemFactory.Create(new RemoteTranscriptItem
        {
            Id = "user", Kind = RemoteProtocol.ItemKinds.User, Author = "Lumi Mobile", Text = prompt
        }));
        Assert.Equal("Review this report.", row.Text);
        Assert.Equal("Quarterly report.pdf", Assert.Single(row.Attachments).FileName);
        Assert.Equal(path, row.Attachments[0].Path);
        Assert.DoesNotContain(path, row.Text);
    }

    [Fact]
    public void OlderHostAttachmentMessagesBecomeChipsAndOrdinaryProseIsNotStripped()
    {
        const string content = "Review this.\nAttached files:\nC:\\Temp\\report.pdf";
        var parsed = RemoteUserMessageContent.Parse(content, "Lumi Mobile");
        Assert.Equal("Review this.", parsed.Text);
        Assert.Equal("report.pdf", Assert.Single(parsed.Attachments).FileName);
        Assert.Equal(content, RemoteUserMessageContent.Parse(content, "You").Text);
        const string prose = "Explain this format:\nAttached files:\nnot a path";
        Assert.Equal(prose, RemoteUserMessageContent.Parse(prose, "Lumi Mobile").Text);
    }

    [Fact]
    public void MoreThanTheWireAttachmentLimitStillUsesCleanMessageText()
    {
        var files = Enumerable.Range(1, RemoteProtocol.MobileAttachmentCountLimit + 1)
            .Select(index => new RemoteAttachment
            {
                Path = $@"C:\uploads\opaque-{index}.png",
                FileName = $"Photo {index}.png"
            }).ToArray();
        var prompt = RemoteUserMessageContent.BuildPrompt("Review these photos.", files);
        var row = new UserTurnItemViewModel(new RemoteTranscriptItem
        {
            Kind = RemoteProtocol.ItemKinds.User,
            Author = "Lumi Mobile",
            Text = prompt
        });
        Assert.Equal("Review these photos.", row.Text);
        Assert.Equal(files.Length, row.Attachments.Count);
        Assert.Equal("Photo 25.png", row.Attachments[^1].FileName);
    }

    [Fact]
    public async Task AttachmentEchoAndAcceptedTranscriptUseTheSameOriginalFilename()
    {
        var sink = new RecordingSink();
        var chat = new MobileChatViewModel(sink);
        await chat.AttachFileAsync("Quarterly report.pdf", new byte[] { 1, 2, 3 });
        chat.PromptText = "Review this.";
        var sending = chat.SendCommand.ExecuteAsync(null);
        var echo = Assert.Single(Assert.Single(chat.Turns).Items.OfType<UserTurnItemViewModel>());
        Assert.Equal("Review this.", echo.Text);
        Assert.Equal("Quarterly report.pdf", Assert.Single(echo.Attachments).FileName);
        Assert.True(chat.ShowThinking);
        var prompt = Assert.Single(sink.Commands).Get("message");
        var accepted = new UserTurnItemViewModel(new RemoteTranscriptItem
        {
            Kind = RemoteProtocol.ItemKinds.User, Author = "Lumi Mobile", Text = prompt
        });
        Assert.Equal(echo.Text, accepted.Text);
        Assert.Equal(echo.Attachments[0].FileName, accepted.Attachments[0].FileName);
        sink.Completion.SetResult(new RemoteCommandResult { Ok = true, ChatId = Guid.NewGuid() });
        await sending;
    }

    [Fact]
    public void LegacyJobWakeBecomesAnEventWithDetailsAndNotAUserBubble()
    {
        const string content = "Background job triggered: Build watcher\n\nJob instructions:\nReport the build.\n\nTrigger context:\nWake script exited with code 0.\nStarted: 18:00\nCompleted: 18:01\nBuild finished.\nFull script output:\n{\"status\":\"passed\"}\n\nRespond as Lumi";
        JobWakeItemViewModel? opened = null;
        var wake = Assert.IsType<JobWakeItemViewModel>(TranscriptItemFactory.Create(
            new RemoteTranscriptItem
            {
                Id = "wake", Kind = RemoteProtocol.ItemKinds.User,
                Author = "Lumi Job - Build watcher", Text = content
            }, openJobWake: item => opened = item));
        Assert.Equal("Build watcher", wake.Details.JobName);
        Assert.Equal("Build finished.", wake.Summary);
        Assert.Equal("0", wake.Details.ExitCode);
        Assert.Equal("{\"status\":\"passed\"}", wake.Details.OutputText);
        Assert.True(wake.HasInstructions);
        wake.OpenCommand.Execute(null);
        Assert.Same(wake, opened);
        Assert.IsType<UserTurnItemViewModel>(TranscriptItemFactory.Create(
            new RemoteTranscriptItem { Kind = RemoteProtocol.ItemKinds.User, Author = "You", Text = content }));
    }

    [Fact]
    public void JobDetailsCloseWhenTheSameChatSurfaceIsReset()
    {
        var chat = new MobileChatViewModel(new RecordingSink());
        var chatId = Guid.NewGuid();
        chat.Reset(chatId, "Job");
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 1,
            Turns = [new RemoteTranscriptTurn
            {
                Id = "turn",
                Items = [new RemoteTranscriptItem
                {
                    Id = "wake", Kind = RemoteProtocol.ItemKinds.User, Author = "Lumi Job - Watcher",
                    Text = "Background job triggered: Watcher\nJob instructions:\nReport progress."
                }]
            }]
        });
        Assert.IsType<JobWakeItemViewModel>(chat.Turns[0].Items[0]).OpenCommand.Execute(null);
        Assert.True(chat.IsJobWakeSheetOpen);
        Assert.True(chat.HasOpenSheet);
        chat.Reset(chatId, "Job");
        Assert.False(chat.IsJobWakeSheetOpen);
        Assert.Null(chat.SelectedJobWake);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DesktopSetupsAreNotInferredFromChatHistoryAndPreserveTheDraft(bool desktopSupportsSetups)
    {
        var projectId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        await using var host = new FakeLumiDesktop
        {
            Snapshot = new RemoteSnapshot
            {
                Settings = new RemoteSettings
                {
                    AvailableModels = ["model-a"], PreferredModel = "model-a",
                    ModelDisplayNames = ["model-a=Model A"],
                    ModelReasoningEfforts = ["model-a=Low,High"],
                    NewChat = desktopSupportsSetups ? new RemoteNewChatExperience
                    {
                        Greeting = "Good evening, Adir",
                        Brief = "1 new reply",
                        SetupsTitle = "Your usual setups",
                        Setups = [new RemoteChatSetup
                        {
                            Id = "desktop-project",
                            Title = "Project",
                            Description = "Agent · Worktree · Model A · High",
                            Glyph = "P",
                            ProjectId = projectId,
                            ProjectName = "Project",
                            AgentId = agentId,
                            AgentName = "Agent",
                            UseWorktree = true,
                            Model = "model-a",
                            Quality = "High"
                        }],
                        Starters = [new RemoteChatStarter
                        {
                            Glyph = "S", Label = "Recap my day", Prompt = "Review my conversations and recap today."
                        }]
                    } : null
                },
                Library = new RemoteLibrary
                {
                    Projects = [new RemoteProject { Id = projectId, Name = "Project", IsCodingProject = true }],
                    Lumis = [new RemoteLumi { Id = agentId, Name = "Agent" }]
                },
                Chats = new RemoteChatPage
                {
                    TotalCount = 2,
                    Groups = [new RemoteChatGroup
                    {
                        Label = "Today",
                        Chats =
                        [
                            new RemoteChat
                            {
                                Id = Guid.NewGuid(), Title = "Recent", ProjectId = projectId, AgentId = agentId,
                                LastModelUsed = "model-a", MessageCount = 2
                            },
                            new RemoteChat
                            {
                                Id = Guid.NewGuid(), Title = "General", LastModelUsed = "model-a", MessageCount = 2
                            }
                        ]
                    }]
                }
            }
        };
        host.Start();
        await using var shell = new MobileShellViewModel(
            new LumiRemoteClient("setup-test", "Test phone"),
            store: new MobileSettingsStore(Path.Combine(Path.GetTempPath(), "lumi-mobile-tests", Guid.NewGuid().ToString("N"))),
            post: action => action());
        shell.Connect.ManualAddress = host.BaseUrl;
        await shell.Connect.ConnectManuallyCommand.ExecuteAsync(null);
        shell.Connect.PairingCode = "123456";
        await shell.Connect.SubmitCodeCommand.ExecuteAsync(null);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (shell.BootstrapSnapshotCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(shell.BootstrapSnapshotCount > 0);
        if (!desktopSupportsSetups)
        {
            Assert.Empty(shell.StartSetups);
            Assert.False(shell.HasDesktopNewChat);
            return;
        }
        var setup = Assert.Single(shell.StartSetups, item => item.ProjectId == projectId);
        Assert.Equal("Project", setup.Title);
        Assert.Equal("Agent · Worktree · Model A · High", setup.Description);
        Assert.Single(shell.StartSetups);
        Assert.Equal("Good evening, Adir", shell.Greeting);
        Assert.Equal("1 new reply", shell.NewChatBrief);
        Assert.Equal("Review my conversations and recap today.", Assert.Single(shell.Chat.Starters).EffectivePrompt);
        shell.Chat.PromptText = "Keep my draft";
        await shell.Chat.AttachFileAsync("notes.txt", new byte[] { 42 });
        shell.UseStartSetupCommand.Execute(setup);
        Assert.Equal(Guid.Empty, shell.Chat.ChatId);
        Assert.Equal("Keep my draft", shell.Chat.PromptText);
        Assert.Equal("notes.txt", Assert.Single(shell.Chat.Attachments).FileName);
        Assert.Equal(projectId.ToString(), shell.Chat.ProjectValue);
        Assert.Equal(agentId.ToString(), shell.Chat.AgentValue);
        Assert.Equal("model-a", shell.Chat.Model);
        Assert.Equal("High", shell.Chat.Quality);
        Assert.True(shell.Chat.UseWorktree);
        Assert.True(setup.IsSelected);
        Assert.Equal(projectId, shell.ActiveProjectId);
        lock (host.ReceivedCommands)
            Assert.DoesNotContain(host.ReceivedCommands, command =>
                command.Action is RemoteProtocol.Actions.CreateChat or RemoteProtocol.Actions.SendMessage);
        await shell.Chat.SendCommand.ExecuteAsync(null);
        lock (host.ReceivedCommands)
        {
            var send = Assert.Single(host.ReceivedCommands, command => command.Action == RemoteProtocol.Actions.SendMessage);
            Assert.True(send.GetBool("useChatSetup"));
            Assert.Equal("High", send.Get("quality"));
            Assert.True(send.GetBool("worktree"));
        }
    }

    private sealed class RecordingSink : IRemoteCommandSink
    {
        public List<RemoteCommand> Commands { get; } = [];
        public TaskCompletionSource<RemoteCommandResult> Completion { get; } = new();
        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command)
        {
            Commands.Add(command);
            return Completion.Task;
        }
        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse
            {
                Ok = true, FileName = fileName, Path = @"C:\Temp\20261009-180001-a1b2c3d4e5f6.pdf"
            });
    }
}
