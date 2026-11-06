using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class MobileMotionTests
{
    [Fact]
    public void OnlyFreshLatestItemsRequestAnEntranceAndRefreshDoesNotReplayIt()
    {
        var chat = new MobileChatViewModel(new RecordingSink());
        var id = Guid.NewGuid();
        chat.Reset(id, "Motion", isLoading: true);
        var transcript = Transcript(id, 1, "first");
        chat.ApplyTranscript(transcript);
        chat.IsLoading = false;
        Assert.False(chat.Turns[0].Items[0].TryConsumeEntranceAnimation());

        transcript.Revision = 2;
        transcript.Turns[0].Items.Add(Answer("second"));
        chat.ApplyTranscript(transcript);
        Assert.False(chat.Turns[0].Items[0].TryConsumeEntranceAnimation());
        var arrival = chat.Turns[0].Items[1];
        Assert.True(arrival.TryConsumeEntranceAnimation());
        Assert.False(arrival.TryConsumeEntranceAnimation());

        transcript.Revision = 3;
        transcript.Turns[0].Items[1].Text += " Streaming update.";
        chat.ApplyTranscript(transcript);
        Assert.Same(arrival, chat.Turns[0].Items[1]);
        Assert.False(arrival.TryConsumeEntranceAnimation());
    }

    [Fact]
    public void PagingReturningToLatestAndServerRestartDoNotAnimateHistory()
    {
        var chat = new MobileChatViewModel(new RecordingSink());
        var id = Guid.NewGuid();
        chat.Reset(id, "Motion");
        chat.ApplyTranscript(Transcript(id, 1, "initial"));
        var older = Transcript(id, 2, "older");
        older.IsLatestWindow = false;
        chat.ApplyTranscript(older);
        Assert.False(chat.Turns[0].Items[0].TryConsumeEntranceAnimation());

        chat.ApplyTranscript(Transcript(id, 3, "latest"));
        Assert.False(chat.Turns[0].Items[0].TryConsumeEntranceAnimation());

        var restarted = Transcript(id, 1, "restarted");
        restarted.RevisionEpoch = "new-desktop-instance";
        chat.ApplyTranscript(restarted);
        Assert.False(chat.Turns[0].Items[0].TryConsumeEntranceAnimation());
    }

    [Fact]
    public async Task OptimisticSendArrivesBeforeIoAndServerEchoDoesNotArriveTwice()
    {
        var sink = new RecordingSink { HoldSend = true };
        var chat = new MobileChatViewModel(sink);
        var id = Guid.NewGuid();
        chat.Reset(id, "Motion");
        chat.ApplyTranscript(Transcript(id, 1, "history"));
        chat.PromptText = "A new thought";
        var send = chat.SendCommand.ExecuteAsync(null);
        try
        {
            Assert.False(send.IsCompleted);
            Assert.True(chat.ShowThinking);
            var echo = Assert.IsType<UserTurnItemViewModel>(chat.Turns[^1].Items[0]);
            var echoTurn = chat.Turns[^1];
            Assert.Equal("A new thought", echo.Text);
            Assert.True(echo.TryConsumeEntranceAnimation());
            Assert.False(echo.TryConsumeEntranceAnimation());

            var transcript = Transcript(id, 2, "history");
            transcript.Turns.Add(new RemoteTranscriptTurn
            {
                Id = "new-turn",
                Items =
                [
                    new RemoteTranscriptItem
                    {
                        Id = "server-user", Kind = RemoteProtocol.ItemKinds.User, Text = "A new thought",
                        RequestId = Assert.Single(sink.Commands).RequestId
                    },
                    Answer("reply")
                ]
            });
            chat.ApplyTranscript(transcript);
            Assert.Same(echoTurn, chat.Turns[^1]);
            Assert.Same(echo, chat.Turns[^1].Items[0]);
            Assert.Equal("new-turn", echoTurn.Id);
            Assert.Equal("server-user", echo.Id);
            Assert.False(chat.Turns[^1].Items[0].TryConsumeEntranceAnimation());
            Assert.True(chat.Turns[^1].Items[1].TryConsumeEntranceAnimation());
            Assert.False(chat.ShowThinking);
        }
        finally
        {
            sink.Response.TrySetResult(new RemoteCommandResult { Ok = true });
            await send;
        }
    }

    [Fact]
    public async Task SendPaintsBeforePendingConfigurationAndKeepsTheTappedChatWhenNavigating()
    {
        var sink = new RecordingSink { HoldConfiguration = true };
        var chat = new MobileChatViewModel(sink);
        var id = Guid.NewGuid();
        chat.Reset(id, "Motion");
        chat.ApplyTranscript(Transcript(id, 1, "history"));
        chat.Model = "selected-model";
        Assert.Equal(RemoteProtocol.Actions.ConfigureChat, Assert.Single(sink.Commands).Action);
        chat.PromptText = "Send without waiting for settings";

        var send = chat.SendCommand.ExecuteAsync(null);
        try
        {
            Assert.False(send.IsCompleted);
            Assert.Empty(chat.PromptText);
            Assert.True(chat.ShowThinking);
            Assert.Equal("Send without waiting for settings",
                Assert.IsType<UserTurnItemViewModel>(chat.Turns[^1].Items[0]).Text);
            Assert.Single(sink.Commands);

            chat.Reset(Guid.NewGuid(), "Another chat");
            chat.PromptText = "Keep this draft";
        }
        finally
        {
            sink.ConfigurationResponse.TrySetResult(new RemoteCommandResult { Ok = true });
            await send;
        }

        var command = Assert.Single(sink.Commands, item => item.Action == RemoteProtocol.Actions.SendMessage);
        Assert.Equal(id.ToString(), command.Get("chatId"));
        Assert.Equal("Send without waiting for settings", command.Get("message"));
        Assert.Equal("selected-model", command.Get("model"));
        Assert.Equal("Keep this draft", chat.PromptText);
        Assert.Empty(chat.Turns);
    }

    [Fact]
    public async Task StopCancelsSendWaitingForConfigurationAndReturnsItsDraft()
    {
        var sink = new RecordingSink { HoldConfiguration = true };
        var chat = new MobileChatViewModel(sink);
        var id = Guid.NewGuid();
        chat.Reset(id, "Stop before dispatch");
        chat.ApplyTranscript(Transcript(id, 1, "history"));
        chat.Model = "selected-model";
        chat.PromptText = "Keep this unsent message";
        var attachment = new PendingAttachment("notes.txt", @"C:\uploads\notes.txt");
        chat.Attachments.Add(attachment);
        var send = chat.SendCommand.ExecuteAsync(null);
        try
        {
            Assert.True(chat.IsBusy);
            Assert.DoesNotContain(sink.Commands, command => command.Action == RemoteProtocol.Actions.SendMessage);
            await chat.StopCommand.ExecuteAsync(null);
            await send.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(chat.IsBusy);
            Assert.False(chat.ShowThinking);
            Assert.Equal("Keep this unsent message", chat.PromptText);
            Assert.Same(attachment, Assert.Single(chat.Attachments));
            Assert.Null(chat.ErrorText);
            Assert.Single(chat.Turns);
        }
        finally
        {
            sink.ConfigurationResponse.TrySetResult(new RemoteCommandResult { Ok = true });
            await send;
        }
        Assert.DoesNotContain(sink.Commands, command => command.Action == RemoteProtocol.Actions.SendMessage);
        Assert.Contains(sink.Commands, command => command.Action == RemoteProtocol.Actions.StopGeneration);
    }

    [Fact]
    public async Task StoppingAnotherChatDoesNotCancelAStagedSend()
    {
        var sink = new RecordingSink { HoldConfiguration = true };
        var chat = new MobileChatViewModel(sink);
        var id = Guid.NewGuid();
        chat.Reset(id, "Sending chat");
        chat.ApplyTranscript(Transcript(id, 1, "history"));
        chat.Model = "selected-model";
        chat.PromptText = "Send to the original chat";
        var send = chat.SendCommand.ExecuteAsync(null);
        try
        {
            chat.Reset(Guid.NewGuid(), "Other chat");
            chat.PromptText = "Other draft";
            await chat.StopCommand.ExecuteAsync(null);
        }
        finally
        {
            sink.ConfigurationResponse.TrySetResult(new RemoteCommandResult { Ok = true });
            await send;
        }
        var command = Assert.Single(sink.Commands, command => command.Action == RemoteProtocol.Actions.SendMessage);
        Assert.Equal(id.ToString(), command.Get("chatId"));
        Assert.Equal("Other draft", chat.PromptText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmationPreservesBubbleWhenTheLatestWindowDropsItsOldestTurn(bool hasInterveningTurn)
    {
        var sink = new RecordingSink();
        var chat = new MobileChatViewModel(sink);
        var id = Guid.NewGuid();
        var history = Enumerable.Range(0, RemoteProtocol.InitialCompactTranscriptWindowVisibleItemLimit / 2)
            .Select(index => new RemoteTranscriptTurn
            {
                Id = $"turn-{index}",
                Items = [new()
                {
                    Id = $"user-{index}", Kind = RemoteProtocol.ItemKinds.User, Text = $"Question {index}"
                }, Answer($"answer-{index}")]
            }).ToList();
        chat.Reset(id, "Rolling tail");
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = id, Revision = 1, Turns = history,
            TotalRawMessageCount = history.Count * 2, Status = new() { ChatId = id }
        });
        var dropped = hasInterveningTurn ? 2 : 1;
        var retained = chat.Turns[dropped];
        chat.PromptText = "Keep the new bubble";
        await chat.SendCommand.ExecuteAsync(null);
        var echo = chat.Turns[^1];
        var user = echo.Items[0];
        Assert.True(user.TryConsumeEntranceAnimation());
        var accepted = new RemoteTranscriptTurn
        {
            Id = "accepted", Items = [new()
            {
                Id = "accepted-user", Kind = RemoteProtocol.ItemKinds.User,
                Text = "Keep the new bubble", RequestId = Assert.Single(sink.Commands).RequestId
            }, Answer("new-answer")]
        };
        var incoming = history.Skip(dropped).ToList();
        if (hasInterveningTurn)
            incoming.Add(new RemoteTranscriptTurn
            {
                Id = "intervening", Items = [new()
                {
                    Id = "intervening-user", Kind = RemoteProtocol.ItemKinds.User, Text = "A message from desktop"
                }, Answer("intervening-answer")]
            });
        incoming.Add(accepted);
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = id, Revision = 2, WindowStartMessageIndex = dropped * 2,
            TotalRawMessageCount = history.Count * 2 + dropped * 2, HasEarlierMessages = true,
            Turns = incoming, Status = new() { ChatId = id }
        });
        Assert.Same(retained, chat.Turns[0]);
        Assert.Same(echo, chat.Turns[^1]);
        Assert.Same(user, chat.Turns[^1].Items[0]);
        Assert.False(user.TryConsumeEntranceAnimation());
        Assert.True(chat.Turns[^1].Items[1].TryConsumeEntranceAnimation());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServerConfirmationPreservesTheFirstBubbleAndOnlyAnimatesNewActivity(bool newChat)
    {
        var id = Guid.NewGuid();
        var sink = new RecordingSink { CreatedChatId = newChat ? id : null };
        var chat = new MobileChatViewModel(sink);
        chat.ChatCreated += (created, generation) => Assert.True(chat.TryAdoptCreatedChat(created, generation));
        chat.Reset(newChat ? Guid.Empty : id, "Motion");
        if (!newChat)
            chat.ApplyTranscript(new RemoteTranscript
            {
                ChatId = id, RevisionEpoch = "desktop", Revision = 1,
                Status = new RemoteChatStatus { ChatId = id }
            });
        chat.PromptText = "First message";
        await chat.SendCommand.ExecuteAsync(null);
        var echoTurn = Assert.Single(chat.Turns);
        var echo = Assert.Single(echoTurn.Items);
        Assert.True(echo.TryConsumeEntranceAnimation());
        Assert.Equal(id, chat.ChatId);

        var transcript = new RemoteTranscript
        {
            ChatId = id, RevisionEpoch = "desktop", Revision = 2,
            Status = new RemoteChatStatus { ChatId = id, IsBusy = true },
            Turns = [new()
            {
                Id = "accepted",
                Items = [new()
                {
                    Id = "accepted-user", Kind = RemoteProtocol.ItemKinds.User,
                    RequestId = Assert.Single(sink.Commands).RequestId, Text = "First message"
                }, Answer("response")]
            }]
        };
        chat.ApplyTranscript(transcript);
        Assert.Same(echoTurn, Assert.Single(chat.Turns));
        Assert.Same(echo, chat.Turns[0].Items[0]);
        Assert.False(echo.TryConsumeEntranceAnimation());
        Assert.True(chat.Turns[0].Items[1].TryConsumeEntranceAnimation());
    }

    [Fact]
    public async Task InFlightOldTranscriptDoesNotDetachThePendingBubble()
    {
        var sink = new RecordingSink { HoldSend = true };
        var chat = new MobileChatViewModel(sink);
        var id = Guid.NewGuid();
        chat.Reset(id, "Motion");
        var transcript = Transcript(id, 1, "history");
        chat.ApplyTranscript(transcript);
        chat.PromptText = "Pending message";
        var send = chat.SendCommand.ExecuteAsync(null);
        var echo = chat.Turns[^1];
        var changes = 0;
        chat.Turns.CollectionChanged += (_, _) => changes++;
        try
        {
            chat.ApplyTranscript(transcript);
            Assert.Same(echo, chat.Turns[^1]);
            Assert.Equal(0, changes);
            Assert.True(chat.ShowThinking);
        }
        finally
        {
            sink.Response.TrySetResult(new RemoteCommandResult { Ok = true });
            await send;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleReceivedDuringTranscriptFetchSettlesWithTheVisibleReplyUnlessNewerWorkStarted(bool newerWork)
    {
        var sink = new RecordingSink();
        var chat = new MobileChatViewModel(sink);
        var id = Guid.NewGuid();
        chat.Reset(id, "Motion");
        chat.ApplyTranscript(Transcript(id, 1, "history"));
        chat.PromptText = "New message";
        await chat.SendCommand.ExecuteAsync(null);
        var requestedAt = chat.StatusVersion;
        chat.ApplyStatus(new RemoteChatStatus { ChatId = id });
        Assert.True(chat.ShowThinking);
        if (newerWork)
            chat.ApplyStatus(new RemoteChatStatus { ChatId = id, IsBusy = true });

        var transcript = Transcript(id, 2, "history");
        transcript.Turns.Add(new RemoteTranscriptTurn
        {
            Id = "accepted",
            Items = [new()
            {
                Id = "accepted-user", Kind = RemoteProtocol.ItemKinds.User,
                RequestId = Assert.Single(sink.Commands).RequestId, Text = "New message"
            }, Answer("reply")]
        });
        chat.ApplyTranscript(transcript, requestedAt);
        Assert.False(chat.ShowThinking);
        Assert.Equal(newerWork, chat.IsBusy);
    }

    [Fact]
    public async Task ConfirmedBubbleSurvivesALostSendAcknowledgement()
    {
        var sink = new RecordingSink { HoldSend = true };
        var chat = new MobileChatViewModel(sink);
        var id = Guid.NewGuid();
        chat.Reset(id, "Motion");
        chat.ApplyTranscript(Transcript(id, 1, "history"));
        chat.PromptText = "Accepted message";
        var send = chat.SendCommand.ExecuteAsync(null);
        var echo = chat.Turns[^1];
        var transcript = Transcript(id, 2, "history");
        transcript.Turns.Add(new RemoteTranscriptTurn
        {
            Id = "accepted",
            Items = [new()
            {
                Id = "accepted-user", Kind = RemoteProtocol.ItemKinds.User,
                RequestId = Assert.Single(sink.Commands).RequestId, Text = "Accepted message"
            }, Answer("reply")]
        });
        try
        {
            chat.ApplyTranscript(transcript);
        }
        finally
        {
            sink.Response.TrySetResult(new RemoteCommandResult
            {
                Error = "The acknowledgement was lost", IsOutcomeUnknown = true
            });
            await send;
        }
        Assert.Same(echo, chat.Turns[^1]);
        Assert.Equal(2, chat.Turns.Count);
        Assert.Equal("Accepted message", Assert.IsType<UserTurnItemViewModel>(echo.Items[0]).Text);
    }

    [Fact]
    public async Task OffscreenArrivalIntentExpiresInsteadOfReplayingLater()
    {
        var item = new AssistantItemViewModel(Answer("offscreen"));
        item.RequestEntranceAnimation();
        await Task.Delay(2100);
        Assert.False(item.TryConsumeEntranceAnimation());
        Assert.False(item.TryConsumeEntranceAnimation());
    }

    private static RemoteTranscript Transcript(Guid id, long revision, string itemId) => new()
    {
        ChatId = id, Revision = revision, IsLatestWindow = true,
        Status = new RemoteChatStatus { ChatId = id },
        Turns = [new RemoteTranscriptTurn { Id = "turn", Items = [Answer(itemId)] }]
    };

    private static RemoteTranscriptItem Answer(string id) => new()
    {
        Id = id, Kind = RemoteProtocol.ItemKinds.Assistant, Text = $"Answer {id}."
    };

    private sealed class RecordingSink : IRemoteCommandSink
    {
        public bool HoldSend { get; init; }
        public bool HoldConfiguration { get; init; }
        public Guid? CreatedChatId { get; init; }
        public List<RemoteCommand> Commands { get; } = [];
        public TaskCompletionSource<RemoteCommandResult> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<RemoteCommandResult> ConfigurationResponse { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command)
        {
            Commands.Add(command);
            return HoldSend && command.Action == RemoteProtocol.Actions.SendMessage
                ? Response.Task
                : HoldConfiguration && command.Action == RemoteProtocol.Actions.ConfigureChat
                    ? ConfigurationResponse.Task
                : Task.FromResult(new RemoteCommandResult { Ok = true, ChatId = CreatedChatId });
        }

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            throw new NotSupportedException();
    }
}
